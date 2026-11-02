using Common.Enum;
using Common.Network;

using CompeteServer.Database;
using CompeteServer.Entity;
using CompeteServer.Manager;
using CompeteServer.Network;

namespace CompeteServer.Handler
{
    /// <summary>
    /// 好友消息处理类（自 MessageHandler 拆出）
    /// 负责好友列表 / 玩家查询 / 好友申请 / 申请回应 / 待处理申请列表的消息处理
    /// 身份一律取自会话绑定（session.PlayerId），客户端无法伪造
    /// </summary>
    public static class FriendHandler
    {
        /// <summary>
        /// 好友列表请求处理方法
        /// 查询当前会话玩家的好友列表（含实时在线状态）并返回
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        public static void HandleFriendList(ClientSession session)
        {
            // 未登录的会话不提供好友查询
            if (session.PlayerId <= 0)
            {
                Console.WriteLine("收到好友列表请求，但该会话未登录，已忽略");
                return;
            }

            Console.WriteLine($"收到好友列表请求:PlayerId={session.PlayerId}");

            System.Collections.Generic.List<(int FriendId, string Name)> friendList =
                new FriendRepository().GetFriends(session.PlayerId);

            FriendListResponse response = new FriendListResponse();
            ChatRepository chatRepository = new ChatRepository();
            // 未读数一次聚合取回：N 个好友 = 1 次 Mongo 查询（替代逐好友 CountUnread 的 N+1 往返）
            Dictionary<int, int> unreadMap = chatRepository.CountUnreadBatch(
                session.PlayerId, friendList.Select(friend => friend.FriendId));
            // 每个会话的最后消息时间：再一次聚合（客户端据此只预热有历史的会话，并按最近活跃排序）
            Dictionary<int, long> lastMessageMap = chatRepository.GetLastMessageTimes(
                session.PlayerId, friendList.Select(friend => friend.FriendId));
            foreach ((int friendId, string name) in friendList)
            {
                // 在线状态从内存 PlayerManager 实时判断（不查库）
                response.Friends.Add(new FriendInfo
                {
                    PlayerId = friendId,
                    Name = name,
                    Online = PlayerManager.Instance.GetPlayer(friendId) != null,
                    // 未读聊天数：Mongo 聚合（对方发我且未读），客户端红点角标数据源
                    UnreadCount = unreadMap.TryGetValue(friendId, out int unread) ? unread : 0,
                    // 最后消息时间：0 = 从未聊过，客户端不预热这类会话
                    LastMessageAtMs = lastMessageMap.TryGetValue(friendId, out long lastMs) ? lastMs : 0,
                });
            }

            response.Count = response.Friends.Count;
            Console.WriteLine($"好友列表返回:{response.Count}人");

            _ = session.Send(MessageID.FriendListResponse, response);
        }

        /// <summary>
        /// 查询玩家请求处理方法
        /// 按玩家ID查询账号是否存在（添加朋友前先确认对方存在）
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的查询请求消息体</param>
        public static void HandleQueryPlayer(ClientSession session, ReadOnlySpan<byte> body)
        {
            QueryPlayerRequest request = QueryPlayerRequest.Parser.ParseFrom(body);

            // 未登录的会话不允许查询玩家（防止未认证连接按 ID 枚举全部账号名）
            if (session.PlayerId <= 0)
            {
                Console.WriteLine($"收到查询玩家请求，但该会话未登录，已拒绝:目标Id={request.PlayerId}");
                _ = session.Send(MessageID.QueryPlayerResponse, new QueryPlayerResponse
                {
                    Found = false,
                    PlayerId = request.PlayerId,
                });
                return;
            }

            QueryPlayerResponse response = new QueryPlayerResponse();
            (bool found, string name) = new AccountRepository().QueryPlayer(request.PlayerId);
            response.Found = found;
            response.PlayerId = request.PlayerId;
            response.Name = name;

            Console.WriteLine($"查询玩家:{request.PlayerId} → {(found ? $"找到:{name}" : "不存在")}");
            _ = session.Send(MessageID.QueryPlayerResponse, response);
        }

        /// <summary>
        /// 好友申请请求处理方法
        /// 校验目标后写入 friendships 表（Status=0 待同意）
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的申请请求消息体</param>
        public static void HandleFriendAdd(ClientSession session, ReadOnlySpan<byte> body)
        {
            FriendAddRequest request = FriendAddRequest.Parser.ParseFrom(body);

            // 未登录的会话不能发申请（身份来自会话绑定）
            if (session.PlayerId <= 0)
            {
                FriendAddResponse denied = new FriendAddResponse { Success = false, Message = "请先登录" };
                _ = session.Send(MessageID.FriendAddResponse, denied);
                return;
            }

            (bool success, string message) = new FriendRepository().AddFriendRequest(session.PlayerId, request.TargetId);
            Console.WriteLine($"好友申请:{session.PlayerId} → {request.TargetId} → {message}");

            FriendAddResponse response = new FriendAddResponse { Success = success, Message = message };
            _ = session.Send(MessageID.FriendAddResponse, response);
        }

        /// <summary>
        /// 好友申请回应处理方法
        /// 同意：双向成为好友；拒绝：删除申请
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的回应请求消息体</param>
        public static void HandleFriendRespond(ClientSession session, ReadOnlySpan<byte> body)
        {
            FriendRespondRequest request = FriendRespondRequest.Parser.ParseFrom(body);

            if (session.PlayerId <= 0)
            {
                FriendRespondResponse denied = new FriendRespondResponse { Success = false, Message = "请先登录" };
                _ = session.Send(MessageID.FriendRespondResponse, denied);
                return;
            }

            (bool success, string message) = new FriendRepository().RespondToRequest(session.PlayerId, request.FromPlayerId, request.Accept);
            Console.WriteLine($"好友回应:{session.PlayerId} ← {request.FromPlayerId} → {message}");

            FriendRespondResponse response = new FriendRespondResponse { Success = success, Message = message };
            _ = session.Send(MessageID.FriendRespondResponse, response);

            // 成为好友 → 推进双方"加好友"任务进度（同意方与申请方都算一次，离线也照样落库）
            if (success && request.Accept)
            {
                QuestHandler.RaiseGameEvent(session.PlayerId, QuestRepository.GoalTypeAddFriend, 0, 1);
                QuestHandler.RaiseGameEvent(request.FromPlayerId, QuestRepository.GoalTypeAddFriend, 0, 1);

                // 成就：累计加好友 + 当前好友数量（双方各算一次）
                AchievementService.ReportStat(session.PlayerId, CareerRepository.ScoreFriendAdd, 1);
                AchievementService.ReportStat(request.FromPlayerId, CareerRepository.ScoreFriendAdd, 1);
                AchievementService.OnFriendChanged(session.PlayerId);
                AchievementService.OnFriendChanged(request.FromPlayerId);
            }
        }

        /// <summary>
        /// 待处理申请列表请求处理方法
        /// 返回所有申请了我且等待回应的申请者列表
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        public static void HandleFriendRequests(ClientSession session)
        {
            if (session.PlayerId <= 0) return;

            Console.WriteLine($"收到待处理申请列表请求:PlayerId={session.PlayerId}");

            FriendRequestsResponse response = new FriendRequestsResponse();
            foreach ((int fromId, string name) in new FriendRepository().GetPendingRequests(session.PlayerId))
            {
                response.Requests.Add(new FriendInfo
                {
                    PlayerId = fromId,
                    Name = name,
                    Online = PlayerManager.Instance.GetPlayer(fromId) != null,
                });
            }
            response.Count = response.Requests.Count;

            _ = session.Send(MessageID.FriendRequestsResponse, response);
        }
    }
}
