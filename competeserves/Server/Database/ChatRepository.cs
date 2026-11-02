using MongoDB.Driver;

using CompeteServer.Entity;

namespace CompeteServer.Database
{
    /// <summary>
    /// 聊天仓库类
    /// 负责 MongoDB messages 集合的读写（一条消息一个文档）
    /// 隐私保证：会话键 conversationId 一律由本类内部的 BuildConversationId 生成，
    ///           方法签名不暴露 conversationId 参数，调用方无法越权访问他人会话
    /// 注意：集合无需迁移脚本，首次写入自动创建；索引由 EnsureIndexes 在启动时建立
    /// </summary>
    public class ChatRepository
    {
        /// <summary>messages 集合名</summary>
        private const string CollectionName = "messages";

        /// <summary>
        /// 世界频道的固定会话键
        /// 公共频道没有"两个人"，用固定键与私聊共用一个集合：
        /// 直接复用既有的 { conversationId:1, sentAt:-1 } 复合索引，查询走与私聊完全相同的路径
        /// </summary>
        public const string WorldConversationId = "world";

        /// <summary>世界频道消息的接收者ID（公共频道没有具体接收人，固定 0，不参与任何未读统计）</summary>
        public const int WorldMessageReceiverId = 0;

        /// <summary>单条消息内容的最大长度（字符）</summary>
        public const int MaxContentLength = 500;

        /// <summary>表情键的最大长度（字符）：表情只传"键"，键就是资源名，不需要正文那么长</summary>
        public const int MaxStickerKeyLength = 64;

        /// <summary>消息内容类型：文本（默认值，也是历史文档反序列化后的值）</summary>
        public const int ContentTypeText = 0;

        /// <summary>消息内容类型：表情包（Content 存的是表情键）</summary>
        public const int ContentTypeSticker = 1;

        /// <summary>不传 limit 时的默认拉取条数</summary>
        private const int DefaultLimit = 50;

        /// <summary>单次拉取条数的上限，防止客户端传超大值拖垮服务</summary>
        private const int MaxLimit = 200;

        /// <summary>获取 messages 集合句柄；Mongo 熔断/不可用时返回 null（由各方法走降级分支）。</summary>
        private static IMongoCollection<ChatMessage>? CollectionIfAvailable
            => MongoManager.Instance.GetCollectionIfAvailable<ChatMessage>(CollectionName);

        // ──────────────────────────────
        // 会话键
        // ──────────────────────────────

        /// <summary>
        /// 生成会话键的方法
        /// 把两个玩家ID归一化（小的在前），保证 A→B 与 B→A 落到同一个键上
        /// 这是聊天隐私的结构性保证：调用方只能传入自己的ID，因此永远算不出别人的会话键
        /// </summary>
        /// <param name="playerA">参数名：playerA，类型：int，用途：一方玩家ID</param>
        /// <param name="playerB">参数名：playerB，类型：int，用途：另一方玩家ID</param>
        /// <returns>返回值类型：string，用途：归一化会话键，如 "3_7"</returns>
        private static string BuildConversationId(int playerA, int playerB)
        {
            int smaller = Math.Min(playerA, playerB);
            int larger = Math.Max(playerA, playerB);
            return $"{smaller}_{larger}";
        }

        // ──────────────────────────────
        // 写入消息
        // ──────────────────────────────

        /// <summary>
        /// 保存一条聊天消息的方法
        /// 始终以服务端 UTC 时间落库，忽略客户端时钟
        /// 容错：Mongo 不可用（熔断中/操作失败）时返回 null——绝不让异常炸穿消息处理循环
        /// （会话消息串行分发，一次选服超时会堵住它后面排队的所有请求）
        /// </summary>
        /// <param name="senderId">参数名：senderId，类型：int，用途：发送者ID（由服务端会话推导）</param>
        /// <param name="receiverId">参数名：receiverId，类型：int，用途：接收者ID</param>
        /// <param name="content">参数名：content，类型：string，用途：消息正文（内容类型为表情时是表情键）</param>
        /// <param name="contentType">参数名：contentType，类型：int，用途：0=文本（默认）1=表情包</param>
        /// <returns>返回值类型：ChatMessage?，用途：落库后的消息（Id 已由驱动回填）；null = 落库失败（Mongo 不可用）</returns>
        public ChatMessage? SaveMessage(int senderId, int receiverId, string content, int contentType = 0)
        {
            ChatMessage message = new()
            {
                ConversationId = BuildConversationId(senderId, receiverId),
                SenderId = senderId,
                ReceiverId = receiverId,
                Content = content,
                ContentType = contentType,
                SentAtUtc = DateTime.UtcNow,
                Read = false,
            };

            IMongoCollection<ChatMessage>? collection = CollectionIfAvailable;
            if (collection == null) return null;
            try
            {
                // InsertOne 会自动生成 _id 并回填到传入的文档上
                collection.InsertOne(message);
                return message;
            }
            catch (Exception exception)
            {
                MongoManager.Instance.MarkDown("InsertOne", exception);
                return null;
            }
        }

        // ──────────────────────────────
        // 查询历史
        // ──────────────────────────────

        /// <summary>
        /// 查询两人之间历史消息的方法
        /// 会话键由己方ID与对方ID在内部生成，查询天然被限制在本人参与的会话内
        /// </summary>
        /// <param name="myId">参数名：myId，类型：int，用途：己方玩家ID（服务端会话身份）</param>
        /// <param name="targetId">参数名：targetId，类型：int，用途：对方玩家ID</param>
        /// <param name="beforeMs">参数名：beforeMs，类型：long，用途：翻页游标（UTC毫秒），只取该时间之前的消息；0 表示不限</param>
        /// <param name="limit">参数名：limit，类型：int，用途：拉取条数，0 用默认值，超过上限会被截断</param>
        /// <returns>返回值类型：List&lt;ChatMessage&gt;，用途：按时间倒序（新→旧）排列的消息列表（Mongo 不可用时为空表）</returns>
        public List<ChatMessage> GetHistory(int myId, int targetId, long beforeMs, int limit)
        {
            string conversationId = BuildConversationId(myId, targetId);

            FilterDefinition<ChatMessage> filter =
                Builders<ChatMessage>.Filter.Eq(message => message.ConversationId, conversationId);

            // 翻页：只要比游标更早的消息
            if (beforeMs > 0)
            {
                DateTime beforeUtc = DateTimeOffset.FromUnixTimeMilliseconds(beforeMs).UtcDateTime;
                filter &= Builders<ChatMessage>.Filter.Lt(message => message.SentAtUtc, beforeUtc);
            }

            int effectiveLimit = limit <= 0 ? DefaultLimit : Math.Min(limit, MaxLimit);

            IMongoCollection<ChatMessage>? collection = CollectionIfAvailable;
            if (collection == null) return new List<ChatMessage>();
            try
            {
                return collection.Find(filter)
                    .SortByDescending(message => message.SentAtUtc)   // 先取最新的
                    .ThenByDescending(message => message.Id)          // 同毫秒按 ObjectId 次序稳定，防止两条消息时序翻转
                    .Limit(effectiveLimit)
                    .ToList();
            }
            catch (Exception exception)
            {
                MongoManager.Instance.MarkDown("Find", exception);
                return new List<ChatMessage>();
            }
        }

        // ──────────────────────────────
        // 已读标记
        // ──────────────────────────────

        /// <summary>
        /// 标记会话中对方发给我的消息为已读的方法
        /// 拉取历史即视为已读：Read 字段置活，未读数统计（红点）以此为基础
        /// 过滤条件带 ReceiverId=我，只改对方发给我的消息，不影响我发出的
        /// </summary>
        /// <param name="myId">参数名：myId，类型：int，用途：己方玩家ID（服务端会话身份）</param>
        /// <param name="targetId">参数名：targetId，类型：int，用途：对方玩家ID</param>
        public void MarkConversationRead(int myId, int targetId)
        {
            FilterDefinition<ChatMessage> filter =
                Builders<ChatMessage>.Filter.Eq(message => message.ConversationId, BuildConversationId(myId, targetId)) &
                Builders<ChatMessage>.Filter.Eq(message => message.ReceiverId, myId) &
                Builders<ChatMessage>.Filter.Eq(message => message.Read, false);

            UpdateDefinition<ChatMessage> update =
                Builders<ChatMessage>.Update.Set(message => message.Read, true);

            IMongoCollection<ChatMessage>? collection = CollectionIfAvailable;
            if (collection == null) return;   // Mongo 不可用：已读标记缺失，等恢复后拉历史会补齐
            try
            {
                collection.UpdateMany(filter, update);
            }
            catch (Exception exception)
            {
                MongoManager.Instance.MarkDown("UpdateMany", exception);
            }
        }

        /// <summary>
        /// 统计某好友发给我的未读消息数的方法
        /// 红点数据源：好友列表下发未读数，客户端据此点亮角标（走 { conversationId:1, sentAt:-1 } 复合索引）
        /// </summary>
        /// <param name="myId">参数名：myId，类型：int，用途：己方玩家ID（服务端会话身份）</param>
        /// <param name="friendId">参数名：friendId，类型：int，用途：对方玩家ID</param>
        /// <returns>返回值类型：int，用途：对方发我且 read=false 的消息条数（Mongo 不可用时为 0）</returns>
        public int CountUnread(int myId, int friendId)
        {
            FilterDefinition<ChatMessage> filter =
                Builders<ChatMessage>.Filter.Eq(message => message.ConversationId, BuildConversationId(myId, friendId)) &
                Builders<ChatMessage>.Filter.Eq(message => message.ReceiverId, myId) &
                Builders<ChatMessage>.Filter.Eq(message => message.Read, false);

            IMongoCollection<ChatMessage>? collection = CollectionIfAvailable;
            if (collection == null) return 0;
            try
            {
                return (int)collection.CountDocuments(filter);
            }
            catch (Exception exception)
            {
                MongoManager.Instance.MarkDown("CountDocuments", exception);
                return 0;
            }
        }

        /// <summary>
        /// 批量统计多个好友发给我的未读消息数的方法
        /// 好友列表场景用：N 个好友一次聚合查询取回（替代逐好友 CountUnread 的 N+1 次往返）
        /// </summary>
        /// <param name="myId">参数名：myId，类型：int，用途：己方玩家ID（服务端会话身份）</param>
        /// <param name="friendIds">参数名：friendIds，类型：IEnumerable&lt;int&gt;，用途：要统计的好友ID集合</param>
        /// <returns>返回值类型：Dictionary&lt;int, int&gt;，用途：好友ID→未读数（无未读的好友不在表中，调用方按 0 处理；Mongo 不可用时为空表）</returns>
        public Dictionary<int, int> CountUnreadBatch(int myId, IEnumerable<int> friendIds)
        {
            var result = new Dictionary<int, int>();
            List<int> friends = friendIds.Distinct().ToList();
            if (friends.Count == 0) return result;

            // 会话键→好友ID 反查表：聚合按 conversationId 分组，结果要换回好友ID
            var keyToFriend = new Dictionary<string, int>();
            foreach (int friendId in friends)
            {
                keyToFriend[BuildConversationId(myId, friendId)] = friendId;
            }

            IMongoCollection<ChatMessage>? collection = CollectionIfAvailable;
            if (collection == null) return result;
            try
            {
                var rows = collection.Aggregate()
                    .Match(Builders<ChatMessage>.Filter.In(message => message.ConversationId, keyToFriend.Keys) &
                           Builders<ChatMessage>.Filter.Eq(message => message.ReceiverId, myId) &
                           Builders<ChatMessage>.Filter.Eq(message => message.Read, false))
                    .Group(message => message.ConversationId,
                           grouping => new { ConversationId = grouping.Key, Count = grouping.Count() })
                    .ToList();

                foreach (var row in rows)
                {
                    if (keyToFriend.TryGetValue(row.ConversationId, out int friendId))
                    {
                        result[friendId] = row.Count;
                    }
                }
            }
            catch (Exception exception)
            {
                MongoManager.Instance.MarkDown("Aggregate", exception);
            }
            return result;
        }

        /// <summary>
        /// 批量取多个好友会话的「最后一条消息时间」的方法
        /// 好友列表场景用：与 CountUnreadBatch 同为一次聚合查询，供客户端只预热有历史的会话。
        /// 无消息的好友不在返回表中（调用方按 0 处理）；Mongo 不可用时返回空表。
        /// </summary>
        /// <param name="myId">参数名：myId，类型：int，用途：己方玩家ID（服务端会话身份）</param>
        /// <param name="friendIds">参数名：friendIds，类型：IEnumerable&lt;int&gt;，用途：好友ID集合</param>
        /// <returns>返回值类型：Dictionary&lt;int, long&gt;，用途：好友ID→最后一条消息 UTC 毫秒时间戳（0=无记录）</returns>
        public Dictionary<int, long> GetLastMessageTimes(int myId, IEnumerable<int> friendIds)
        {
            var result = new Dictionary<int, long>();
            List<int> friends = friendIds.Distinct().ToList();
            if (friends.Count == 0) return result;

            // 会话键→好友ID 反查表：聚合按 conversationId 分组，结果要换回好友ID
            var keyToFriend = new Dictionary<string, int>();
            foreach (int friendId in friends)
            {
                keyToFriend[BuildConversationId(myId, friendId)] = friendId;
            }

            IMongoCollection<ChatMessage>? collection = CollectionIfAvailable;
            if (collection == null) return result;
            try
            {
                var rows = collection.Aggregate()
                    .Match(Builders<ChatMessage>.Filter.In(message => message.ConversationId, keyToFriend.Keys))
                    .Group(message => message.ConversationId,
                           grouping => new { ConversationId = grouping.Key, LastSentAt = grouping.Max(message => message.SentAtUtc) })
                    .ToList();

                foreach (var row in rows)
                {
                    if (!keyToFriend.TryGetValue(row.ConversationId, out int friendId)) continue;
                    // SpecifyKind 兜底：驱动读回的 DateTime 可能是 Unspecified，必须按 UTC 换算
                    result[friendId] = new DateTimeOffset(DateTime.SpecifyKind(row.LastSentAt, DateTimeKind.Utc))
                        .ToUnixTimeMilliseconds();
                }
            }
            catch (Exception exception)
            {
                MongoManager.Instance.MarkDown("Aggregate", exception);
            }
            return result;
        }

        // ──────────────────────────────
        // 世界频道（公共频道：固定会话键 + 无接收者）
        // ──────────────────────────────

        /// <summary>
        /// 保存一条世界频道消息的方法
        /// 与私聊共用 messages 集合与索引：会话键固定为 WorldConversationId，接收者留 0（无未读语义）
        /// 容错：Mongo 不可用（熔断中/操作失败）时返回 null——由处理器回执失败，绝不静默丢消息
        /// </summary>
        /// <param name="senderId">参数名：senderId，类型：int，用途：发送者ID（由服务端会话推导）</param>
        /// <param name="content">参数名：content，类型：string，用途：消息正文（内容类型为表情时是表情键）</param>
        /// <param name="contentType">参数名：contentType，类型：int，用途：0=文本（默认）1=表情包</param>
        /// <returns>返回值类型：ChatMessage?，用途：落库后的消息（Id 已由驱动回填）；null = 落库失败</returns>
        public ChatMessage? SaveWorldMessage(int senderId, string content, int contentType = 0)
        {
            ChatMessage message = new()
            {
                ConversationId = WorldConversationId,
                SenderId = senderId,
                ReceiverId = WorldMessageReceiverId,
                Content = content,
                ContentType = contentType,
                SentAtUtc = DateTime.UtcNow,
                // 公共频道没有"已读"语义：字段保留恒 false，且永不参与未读统计（统计条件带 ReceiverId=我）
                Read = false,
            };

            IMongoCollection<ChatMessage>? collection = CollectionIfAvailable;
            if (collection == null) return null;
            try
            {
                collection.InsertOne(message);
                return message;
            }
            catch (Exception exception)
            {
                MongoManager.Instance.MarkDown("InsertOne", exception);
                return null;
            }
        }

        /// <summary>
        /// 查询世界频道历史消息的方法
        /// 与私聊历史同一查询形状（会话键等值 + 时间倒序 + limit），走同一复合索引
        /// </summary>
        /// <param name="beforeMs">参数名：beforeMs，类型：long，用途：翻页游标（UTC毫秒），只取该时间之前的消息；0 表示不限</param>
        /// <param name="limit">参数名：limit，类型：int，用途：拉取条数，0 用默认值，超过上限会被截断</param>
        /// <returns>返回值类型：List&lt;ChatMessage&gt;，用途：按时间倒序（新→旧）排列的消息列表（Mongo 不可用时为空表）</returns>
        public List<ChatMessage> GetWorldHistory(long beforeMs, int limit)
        {
            FilterDefinition<ChatMessage> filter =
                Builders<ChatMessage>.Filter.Eq(message => message.ConversationId, WorldConversationId);

            if (beforeMs > 0)
            {
                DateTime beforeUtc = DateTimeOffset.FromUnixTimeMilliseconds(beforeMs).UtcDateTime;
                filter &= Builders<ChatMessage>.Filter.Lt(message => message.SentAtUtc, beforeUtc);
            }

            int effectiveLimit = limit <= 0 ? DefaultLimit : Math.Min(limit, MaxLimit);

            IMongoCollection<ChatMessage>? collection = CollectionIfAvailable;
            if (collection == null) return new List<ChatMessage>();
            try
            {
                return collection.Find(filter)
                    .SortByDescending(message => message.SentAtUtc)   // 先取最新的
                    .ThenByDescending(message => message.Id)          // 同毫秒按 ObjectId 次序稳定
                    .Limit(effectiveLimit)
                    .ToList();
            }
            catch (Exception exception)
            {
                MongoManager.Instance.MarkDown("Find", exception);
                return new List<ChatMessage>();
            }
        }

        // ──────────────────────────────
        // 索引
        // ──────────────────────────────

        /// <summary>
        /// 建立查询索引的方法
        /// 复合索引 { conversationId: 1, sentAt: -1 }：前缀等值定位会话，后缀与排序方向一致，
        /// 使「按会话查 + 按时间倒序 + limit」走索引直接完成，避免全表扫描与内存排序
        /// 已存在同名索引时为幂等操作，启动时重复调用无副作用
        /// </summary>
        public void EnsureIndexes()
        {
            IndexKeysDefinition<ChatMessage> keys = Builders<ChatMessage>.IndexKeys
                .Ascending(message => message.ConversationId)
                .Descending(message => message.SentAtUtc);

            IMongoCollection<ChatMessage>? collection = CollectionIfAvailable;
            if (collection == null) return;   // Mongo 不可用：跳过建索引，恢复后下次启动会补建（幂等）
            try
            {
                collection.Indexes.CreateOne(new CreateIndexModel<ChatMessage>(keys));
            }
            catch (Exception exception)
            {
                MongoManager.Instance.MarkDown("CreateIndex", exception);
            }
        }
    }
}
