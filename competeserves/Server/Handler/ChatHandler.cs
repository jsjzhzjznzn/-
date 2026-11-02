using System.Collections.Concurrent;
using System.Diagnostics;

using Common.Enum;
using Common.Network;

using Google.Protobuf;

using CompeteServer.Database;
using CompeteServer.Entity;
using CompeteServer.Manager;
using CompeteServer.Network;

namespace CompeteServer.Handler
{
    /// <summary>
    /// 聊天消息处理类（自 MessageHandler 拆出）
    /// 负责聊天发送 / 聊天历史的消息处理与消息实体转换
    /// 隐私三层：会话身份推导 + 好友准入校验 + 会话键由仓库内部生成
    /// </summary>
    public static class ChatHandler
    {
        /// <summary>
        /// 发送聊天消息处理方法
        /// 发送者身份只认会话绑定（客户端不传 senderId），校验后落库并按需推送
        /// 隐私三层：会话身份推导 + 好友准入校验 + 会话键由仓库内部生成
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的发送请求消息体</param>
        public static void HandleChatSend(ClientSession session, ReadOnlySpan<byte> body)
        {
            ChatSendRequest request = ChatSendRequest.Parser.ParseFrom(body);

            // 第一层：未登录的会话不能发消息
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.ChatSendResponse,
                    new ChatSendResponse { Success = false, Message = "请先登录" });
                return;
            }

            int senderId = session.PlayerId;   // 身份来自登录时绑定的会话，客户端无法伪造
            int targetId = request.TargetId;
            string content = request.Content.Trim();
            int contentType = request.ContentType;

            // 内容类型只认文本(0)与表情(1)：其它值一律当文本处理，避免客户端塞脏值污染数据
            if (contentType != ChatRepository.ContentTypeSticker) contentType = ChatRepository.ContentTypeText;
            bool isSticker = contentType == ChatRepository.ContentTypeSticker;

            if (content.Length == 0)
            {
                _ = session.Send(MessageID.ChatSendResponse,
                    new ChatSendResponse { Success = false, Message = isSticker ? "表情不能为空" : "消息内容不能为空" });
                return;
            }
            // 表情只传"键"（资源名），长度上限远比正文小：拦住把表情当长文本通道用的客户端
            if (isSticker && content.Length > ChatRepository.MaxStickerKeyLength)
            {
                _ = session.Send(MessageID.ChatSendResponse,
                    new ChatSendResponse { Success = false, Message = $"表情标识不能超过{ChatRepository.MaxStickerKeyLength}字符" });
                return;
            }
            if (!isSticker && content.Length > ChatRepository.MaxContentLength)
            {
                _ = session.Send(MessageID.ChatSendResponse,
                    new ChatSendResponse { Success = false, Message = $"消息内容不能超过{ChatRepository.MaxContentLength}字" });
                return;
            }
            if (targetId == senderId)
            {
                _ = session.Send(MessageID.ChatSendResponse,
                    new ChatSendResponse { Success = false, Message = "不能给自己发消息" });
                return;
            }

            // 第二层：只能给好友发消息，防止任意玩家被骚扰
            if (!new FriendRepository().AreFriends(senderId, targetId))
            {
                Console.WriteLine($"聊天被拒（非好友）:{senderId} → {targetId}");
                _ = session.Send(MessageID.ChatSendResponse,
                    new ChatSendResponse { Success = false, Message = "对方不是你的好友" });
                return;
            }

            // 第三层（结构性）：会话键由仓库内部用 senderId 推导，只会落到本人的会话上
            // 内容审核（服务端权威，两级）：只对文本正文，表情只传资源键不参与
            //   一级 = 违禁词 AC 自动机精确打码（打码不改长度，前面的长度校验仍然成立）
            //   二级 = 朴素贝叶斯，一级零命中时才判"整段像不像违规"（词库外的新词 / 变体 / 广告话术）
            if (!isSticker)
            {
                string masked = BannedWordFilter.Mask(content);
                if (!ReferenceEquals(masked, content))
                {
                    // 一级命中：已知词精确打码后放行（Mask 无命中时返回同一实例，引用比较即可判"零命中"）
                    content = masked;
                }
                else
                {
                    NaiveBayesFilter.Decision decision = NaiveBayesFilter.Screen(content, out string screened);
                    if (decision == NaiveBayesFilter.Decision.Reject)
                    {
                        Console.WriteLine($"聊天被拒（二级审核疑似违规）:{senderId} → {targetId}");
                        _ = session.Send(MessageID.ChatSendResponse,
                            new ChatSendResponse { Success = false, Message = "内容涉嫌违规，请修改后再发送" });
                        return;
                    }
                    if (decision == NaiveBayesFilter.Decision.Mask) content = screened;
                }
            }

            // Mongo 不可用时 SaveMessage 返回 null：回执失败让客户端回滚乐观消息，绝不静默丢回执
            ChatMessage? saved = new ChatRepository().SaveMessage(senderId, targetId, content, contentType);
            if (saved == null)
            {
                _ = session.Send(MessageID.ChatSendResponse,
                    new ChatSendResponse { Success = false, Message = "聊天服务暂不可用，请稍后再试" });
                return;
            }
            Console.WriteLine(isSticker
                ? $"聊天已落库(表情):{senderId} → {targetId} | {content}"
                : $"聊天已落库:{senderId} → {targetId} | {content}");

            // 回执发送方：带上服务端生成的ID与时间戳，供客户端校准本地乐观UI
            _ = session.Send(MessageID.ChatSendResponse, new ChatSendResponse
            {
                Success = true,
                Message = "发送成功",
                Echo = ToChatItem(saved),
            });

            // 接收方在线才推送；不在线则只落库，等其上线拉历史补齐
            ClientSession? targetSession = SessionManager.Instance.GetSessionByPlayerId(targetId);
            if (targetSession != null)
            {
                _ = targetSession.Send(MessageID.ChatReceiveNotify, new ChatReceiveNotify
                {
                    Message = ToChatItem(saved),
                });
            }

            // 发消息 → 任务进度推进 + 生涯数据累计（累计发消息数）+ 成就重算
            QuestHandler.RaiseMessageSent(senderId);
        }

        /// <summary>
        /// 聊天历史请求处理方法
        /// 己方身份取自会话，与目标ID一起交给仓库内部推导会话键，无法越权查询他人会话
        /// 不做好友校验：历史记录属于双方，解除好友不应抹掉既有记录（发送才要求是好友）
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的历史请求消息体</param>
        public static void HandleChatHistory(ClientSession session, ReadOnlySpan<byte> body)
        {
            // 未登录的会话不提供历史查询
            if (session.PlayerId <= 0) return;

            ChatHistoryRequest request = ChatHistoryRequest.Parser.ParseFrom(body);

            ChatRepository repository = new ChatRepository();
            List<ChatMessage> history = repository
                .GetHistory(session.PlayerId, request.TargetId, request.BeforeMs, request.Limit);

            // 拉历史即已读：把对方发我的未读消息置 Read=true（下期红点/未读数统计的地基）
            // peek=true 是登录后的消息预热：只读不写，绝不提前清掉未读角标；
            // 用户真正打开会话时发的是 peek=false，才走置已读
            if (!request.Peek)
            {
                repository.MarkConversationRead(session.PlayerId, request.TargetId);
            }

            // 仓库按时间倒序返回（便于 limit 取最新），这里反转为正序（旧→新）再下发
            history.Reverse();

            ChatHistoryResponse response = new ChatHistoryResponse { TargetId = request.TargetId };
            foreach (ChatMessage message in history)
            {
                response.Messages.Add(ToChatItem(message));
            }
            response.Count = response.Messages.Count;

            Console.WriteLine($"聊天历史返回:{session.PlayerId} ↔ {request.TargetId} | {response.Count}条");
            _ = session.Send(MessageID.ChatHistoryResponse, response);
        }

        // ──────────────────────────────
        // 世界频道（公共频道：无好友准入 + 服务端广播）
        // ──────────────────────────────

        /// <summary>世界频道每人发送冷却（毫秒）：公共频道面向所有人，防刷屏的第一道闸</summary>
        private const int WorldSendCooldownMs = 3000;

        /// <summary>
        /// 每玩家上次世界频道发送时刻（Stopwatch 单调时钟，不受系统改时间影响）
        /// 同会话处理是串行的，跨会话并发由 ConcurrentDictionary 兜底；条目随玩家数增长，规模可忽略
        /// </summary>
        private static readonly ConcurrentDictionary<int, long> worldLastSendTicks = new();

        /// <summary>
        /// 世界频道发送处理方法
        /// 链路：登录校验 → 内容校验 → 内容审核（一级打码 / 二级贝叶斯）→ 冷却限速 → 落库 → 回执发送方 → 全服广播
        /// 与私聊的三层隐私模型不同：公共频道无 target、无好友准入，但发送者身份仍只认会话绑定
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的世界频道发送请求</param>
        public static void HandleWorldChatSend(ClientSession session, ReadOnlySpan<byte> body)
        {
            WorldChatSendRequest request = WorldChatSendRequest.Parser.ParseFrom(body);

            // 未登录的会话不能在世界频道发言
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.WorldChatSendResponse,
                    new WorldChatSendResponse { Success = false, Message = "请先登录" });
                return;
            }

            int senderId = session.PlayerId;   // 身份来自登录时绑定的会话，客户端无法伪造
            string content = request.Content.Trim();
            int contentType = request.ContentType;

            // 与私聊同口径：内容类型只认文本(0)与表情(1)，其它值一律当文本
            if (contentType != ChatRepository.ContentTypeSticker) contentType = ChatRepository.ContentTypeText;
            bool isSticker = contentType == ChatRepository.ContentTypeSticker;

            if (content.Length == 0)
            {
                _ = session.Send(MessageID.WorldChatSendResponse,
                    new WorldChatSendResponse { Success = false, Message = isSticker ? "表情不能为空" : "消息内容不能为空" });
                return;
            }
            if (isSticker && content.Length > ChatRepository.MaxStickerKeyLength)
            {
                _ = session.Send(MessageID.WorldChatSendResponse,
                    new WorldChatSendResponse { Success = false, Message = $"表情标识不能超过{ChatRepository.MaxStickerKeyLength}字符" });
                return;
            }
            if (!isSticker && content.Length > ChatRepository.MaxContentLength)
            {
                _ = session.Send(MessageID.WorldChatSendResponse,
                    new WorldChatSendResponse { Success = false, Message = $"消息内容不能超过{ChatRepository.MaxContentLength}字" });
                return;
            }

            // 内容审核（服务端权威，两级）放在冷却之前：被拦下的消息不该白白吃掉一次发言冷却
            //   一级 = 违禁词 AC 自动机精确打码；二级 = 朴素贝叶斯（一级零命中才判）
            if (!isSticker)
            {
                string masked = BannedWordFilter.Mask(content);
                if (!ReferenceEquals(masked, content))
                {
                    content = masked;
                }
                else
                {
                    NaiveBayesFilter.Decision decision = NaiveBayesFilter.Screen(content, out string screened);
                    if (decision == NaiveBayesFilter.Decision.Reject)
                    {
                        Console.WriteLine($"世界频道被拒（二级审核疑似违规）:{senderId}");
                        _ = session.Send(MessageID.WorldChatSendResponse,
                            new WorldChatSendResponse { Success = false, Message = "内容涉嫌违规，请修改后再发送" });
                        return;
                    }
                    if (decision == NaiveBayesFilter.Decision.Mask) content = screened;
                }
            }

            // 冷却：纯内存判定，不产生数据库访问；超速直接拒绝并告知还需等多久
            if (!TryPassWorldCooldown(senderId, out int waitSeconds))
            {
                _ = session.Send(MessageID.WorldChatSendResponse,
                    new WorldChatSendResponse { Success = false, Message = $"发言太快，请 {waitSeconds} 秒后再试" });
                return;
            }

            // Mongo 不可用时返回 null：回执失败让客户端回滚乐观消息，绝不静默丢回执
            ChatMessage? saved = new ChatRepository().SaveWorldMessage(senderId, content, contentType);
            if (saved == null)
            {
                _ = session.Send(MessageID.WorldChatSendResponse,
                    new WorldChatSendResponse { Success = false, Message = "世界频道暂不可用，请稍后再试" });
                return;
            }

            string senderName = ResolveSenderName(senderId);
            Console.WriteLine(isSticker
                ? $"世界频道落库(表情):{senderId}|{senderName} | {content}"
                : $"世界频道落库:{senderId}|{senderName} | {content}");

            // 回执发送方：带服务端ID/时间戳/昵称，供客户端校准本地乐观UI
            ChatMessageItem item = ToChatItem(saved, senderName);
            _ = session.Send(MessageID.WorldChatSendResponse,
                new WorldChatSendResponse { Success = true, Message = "发送成功", Echo = item });

            // 广播给全部在线会话（含发送者自己）：发送者客户端按 MessageId 去重，其本地那条已被回执校准
            WorldChatNotify notify = new() { Message = item };
            byte[] notifyBody = notify.ToByteArray();
            byte[] payload = new byte[4 + notifyBody.Length];
            BitConverter.GetBytes((int)MessageID.WorldChatNotify).CopyTo(payload, 0);
            Buffer.BlockCopy(notifyBody, 0, payload, 4, notifyBody.Length);
            _ = SessionManager.Instance.Broadcast(payload);

            // 发消息 → 任务进度推进 + 生涯累计（与私聊同一口径：世界频道发言同样算"发消息"）
            QuestHandler.RaiseMessageSent(senderId);
        }

        /// <summary>
        /// 世界频道历史请求处理方法
        /// 无好友校验（公共频道），登录即可拉取；返回按时间正序（旧→新）排列的最近消息
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的世界频道历史请求</param>
        public static void HandleWorldChatHistory(ClientSession session, ReadOnlySpan<byte> body)
        {
            // 未登录的会话不提供历史查询
            if (session.PlayerId <= 0) return;

            WorldChatHistoryRequest request = WorldChatHistoryRequest.Parser.ParseFrom(body);

            List<ChatMessage> history = new ChatRepository()
                .GetWorldHistory(request.BeforeMs, request.Limit);

            // 仓库按时间倒序返回（便于 limit 取最新），这里反转为正序（旧→新）再下发
            history.Reverse();

            // 昵称批量补齐（一次 IN 查询）：避免逐行查账号表的 N+1
            Dictionary<int, string> names = new AccountRepository()
                .GetNamesByIds(history.Select(message => message.SenderId).Distinct());

            WorldChatHistoryResponse response = new();
            foreach (ChatMessage message in history)
            {
                names.TryGetValue(message.SenderId, out string? name);
                response.Messages.Add(ToChatItem(message, name ?? $"玩家{message.SenderId}"));
            }
            response.Count = response.Messages.Count;

            Console.WriteLine($"世界频道历史返回:{session.PlayerId} | {response.Count}条");
            _ = session.Send(MessageID.WorldChatHistoryResponse, response);
        }

        /// <summary>
        /// 世界频道冷却判定：距上次成功发言不足 WorldSendCooldownMs 即拒绝
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：说话者ID</param>
        /// <param name="waitSeconds">输出参数：还需等待的秒数（向上取整，至少 1），判定通过时为 0</param>
        /// <returns>返回值类型：bool，用途：true=放行并记录本次时刻，false=仍在冷却中</returns>
        private static bool TryPassWorldCooldown(int playerId, out int waitSeconds)
        {
            long now = Stopwatch.GetTimestamp();
            waitSeconds = 0;

            if (worldLastSendTicks.TryGetValue(playerId, out long last))
            {
                double elapsedMs = (now - last) * 1000.0 / Stopwatch.Frequency;
                if (elapsedMs < WorldSendCooldownMs)
                {
                    waitSeconds = Math.Max(1, (int)Math.Ceiling((WorldSendCooldownMs - elapsedMs) / 1000.0));
                    return false;
                }
            }

            worldLastSendTicks[playerId] = now;
            return true;
        }

        /// <summary>
        /// 取玩家昵称（账号名）的方法
        /// 查不到（账号表异常/账号已删）时退化为占位名，保证世界频道每行都有"谁说的"
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <returns>返回值类型：string，用途：账号名或占位名</returns>
        private static string ResolveSenderName(int playerId)
        {
            (bool found, string name) = new AccountRepository().QueryPlayer(playerId);
            return found && !string.IsNullOrEmpty(name) ? name : $"玩家{playerId}";
        }

        /// <summary>
        /// 把 MongoDB 消息实体转换为协议消息条目的方法
        /// </summary>
        /// <param name="message">参数名：message，类型：ChatMessage，用途：仓库取出的消息实体</param>
        /// <param name="senderName">参数名：senderName，类型：string，用途：发送者昵称（世界频道必填；私聊不传=不下发）</param>
        /// <returns>返回值类型：ChatMessageItem，用途：可下发给客户端的协议条目</returns>
        private static ChatMessageItem ToChatItem(ChatMessage message, string? senderName = null)
        {
            return new ChatMessageItem
            {
                MessageId = message.Id.ToString(),   // ObjectId 的 24 位十六进制字符串
                SenderId = message.SenderId,
                ReceiverId = message.ReceiverId,
                Content = message.Content,
                ContentType = message.ContentType,   // 0=文本 1=表情；历史文档没有该字段，反序列化为 0
                SenderName = senderName ?? string.Empty,
                // SpecifyKind 兜底：确保按 UTC 换算，避免驱动读回 Kind=Unspecified 时被当成本地时间
                SentAtMs = new DateTimeOffset(DateTime.SpecifyKind(message.SentAtUtc, DateTimeKind.Utc))
                    .ToUnixTimeMilliseconds(),
                Read = message.Read,
            };
        }
    }
}
