using Common.Enum;
using Common.Network;

using CompeteServer.Database;
using CompeteServer.Entity;
using CompeteServer.Manager;
using CompeteServer.Network;

namespace CompeteServer.Handler
{
    /// <summary>
    /// 消息处理中心类
    /// 负责根据消息ID分发和处理客户端发送的protobuf网络请求；
    /// 自身保留账号（登录/注册）与背包两组处理，好友/聊天/排行榜/任务/邮件已拆到
    /// FriendHandler / ChatHandler / LeaderboardHandler / QuestHandler / MailHandler，此处只做路由转发
    /// </summary>
    public class MessageHandler
    {
        /// <summary>
        /// 消息分发处理方法
        /// 根据消息ID将请求路由到对应的处理方法（本类或各业务处理类）
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="messageId">参数名：messageId，类型：int，用途：消息类型标识（对应MessageID枚举值）</param>
        /// <param name="packet">参数名：packet，类型：byte[]，用途：完整帧体（前 4 字节消息ID + protobuf 消息体）</param>
        /// <returns>返回值类型：Task，用途：Agent 对话这类长耗时分支返回的未完成任务，调用方必须 await；
        /// 其余同步分支返回已完成的 Task（本方法不标 async，避免 ReadOnlySpan 跨 await 的生命周期问题）</returns>
        public static Task Handle(ClientSession session, int messageId, byte[] packet)
        {
            ReadOnlySpan<byte> body = packet.AsSpan(4);
            switch ((MessageID)messageId)
            {
                case MessageID.LoginRequest:
                    HandleLogin(session, body);
                    break;

                case MessageID.TokenLoginRequest:
                    HandleTokenLogin(session, body);
                    break;

                case MessageID.RegisterRequest:
                    HandleRegister(session, body);
                    break;

                case MessageID.FriendListRequest:
                    FriendHandler.HandleFriendList(session);
                    break;

                case MessageID.QueryPlayerRequest:
                    FriendHandler.HandleQueryPlayer(session, body);
                    break;

                case MessageID.FriendAddRequest:
                    FriendHandler.HandleFriendAdd(session, body);
                    break;

                case MessageID.FriendRespondRequest:
                    FriendHandler.HandleFriendRespond(session, body);
                    break;

                case MessageID.FriendRequestsRequest:
                    FriendHandler.HandleFriendRequests(session);
                    break;

                case MessageID.ChatSendRequest:
                    ChatHandler.HandleChatSend(session, body);
                    break;

                case MessageID.ChatHistoryRequest:
                    ChatHandler.HandleChatHistory(session, body);
                    break;

                case MessageID.WorldChatSendRequest:
                    ChatHandler.HandleWorldChatSend(session, body);
                    break;

                case MessageID.WorldChatHistoryRequest:
                    ChatHandler.HandleWorldChatHistory(session, body);
                    break;

                case MessageID.ScoreSubmitRequest:
                    LeaderboardHandler.HandleScoreSubmit(session, body);
                    break;

                case MessageID.LeaderboardRequest:
                    LeaderboardHandler.HandleLeaderboard(session, body);
                    break;

                case MessageID.BagListRequest:
                    HandleBagList(session);
                    break;

                case MessageID.BagUseRequest:
                    HandleBagUse(session, body);
                    break;

                case MessageID.BagDiscardRequest:
                    HandleBagDiscard(session, body);
                    break;

                case MessageID.BagTidyRequest:
                    HandleBagTidy(session);
                    break;

                case MessageID.BagSwapRequest:
                    HandleBagSwap(session, body);
                    break;

                case MessageID.BagGrantRequest:
                    HandleBagGrant(session, body);
                    break;

                case MessageID.MailListRequest:
                    MailHandler.HandleMailList(session);
                    break;

                case MessageID.MailReadRequest:
                    MailHandler.HandleMailRead(session, body);
                    break;

                case MessageID.MailClaimRequest:
                    MailHandler.HandleMailClaim(session, body);
                    break;

                case MessageID.MailDeleteRequest:
                    MailHandler.HandleMailDelete(session, body);
                    break;

                case MessageID.MailGrantRequest:
                    MailHandler.HandleMailGrant(session, body);
                    break;

                case MessageID.MailPublicGrantRequest:
                    MailHandler.HandleMailPublicGrant(session, body);
                    break;

                case MessageID.MailPublicRevokeRequest:
                    MailHandler.HandleMailPublicRevoke(session, body);
                    break;

                case MessageID.NarrativeQuestSyncRequest:
                    NarrativeQuestHandler.HandleSync(session);
                    break;

                case MessageID.NarrativeQuestAcceptRequest:
                    NarrativeQuestHandler.HandleAccept(session, body);
                    break;

                case MessageID.NarrativeQuestSubmitRequest:
                    NarrativeQuestHandler.HandleSubmit(session, body);
                    break;

                case MessageID.NarrativeQuestAbandonRequest:
                    NarrativeQuestHandler.HandleAbandon(session, body);
                    break;

                case MessageID.NarrativeQuestEventReport:
                    NarrativeQuestHandler.HandleEventReport(session, body);
                    break;

                case MessageID.DungeonKillReportRequest:
                    DungeonHandler.HandleKillReport(session, body);
                    break;

                case MessageID.DungeonPickupRequest:
                    DungeonHandler.HandlePickup(session, body);
                    break;

                case MessageID.ActivityListRequest:
                    ActivityHandler.HandleActivityList(session);
                    break;

                case MessageID.GachaDrawRequest:
                    ActivityHandler.HandleGachaDraw(session, body);
                    break;

                case MessageID.GachaHistoryRequest:
                    ActivityHandler.HandleGachaHistory(session, body);
                    break;

                case MessageID.SignInListRequest:
                    ActivityHandler.HandleSignInList(session, body);
                    break;

                case MessageID.SignInClaimRequest:
                    ActivityHandler.HandleSignInClaim(session, body);
                    break;

                case MessageID.ActivityGmRotateRequest:
                    ActivityHandler.HandleActivityGmRotate(session, body);
                    break;

                case MessageID.PingRequest:
                    HandlePing(session, body);
                    break;

                // ── 对战好友邀请（信令层：对战流量仍走房主进程的 NGO 7777）──
                case MessageID.BattleInviteRequest:
                    BattleHandler.HandleBattleInvite(session, body);
                    break;

                case MessageID.BattleInviteRespondRequest:
                    BattleHandler.HandleBattleInviteRespond(session, body);
                    break;

                case MessageID.BattleInviteCancelRequest:
                    BattleHandler.HandleBattleInviteCancel(session, body);
                    break;

                // ── 场景存档（一玩家一场景一行，进入场景拉取、事件点覆盖式上报）──
                case MessageID.SaveStatePullRequest:
                    SceneSaveHandler.HandlePull(session, body);
                    break;

                case MessageID.SaveStatePushRequest:
                    SceneSaveHandler.HandlePush(session, body);
                    break;

                // ── 成就（独立于任务的长期收集/里程碑；领取服务端权威且幂等）──
                case MessageID.AchievementListRequest:
                    AchievementHandler.HandleAchievementList(session);
                    break;

                case MessageID.AchievementClaimRequest:
                    AchievementHandler.HandleAchievementClaim(session, body);
                    break;

                case MessageID.AchievementGmRequest:
                    AchievementHandler.HandleAchievementGm(session, body);
                    break;

                // ── 装备（背包管"有哪些"，player_equipment 管"穿在哪"，equipment_instance 管"这一件是什么"）──
                case MessageID.EquipmentListRequest:
                    EquipmentHandler.HandleEquipmentList(session);
                    break;

                case MessageID.EquipRequest:
                    EquipmentHandler.HandleEquip(session, body);
                    break;

                // ── 新手引导进度（一玩家一引导一行，进主页拉取、每步覆盖式上报）──
                case MessageID.GuidePullRequest:
                    GuideHandler.HandlePull(session, body);
                    break;

                case MessageID.GuidePushRequest:
                    GuideHandler.HandlePush(session, body);
                    break;

                // Agent 两个分支会等网络/LLM，必须把 Task 交回调用方 await：
                // 会话内串行由 ClientSession.ProcessLoop 保证，这里只负责不占住线程
                case MessageID.AgentChatRequest:
                    return AgentHandler.HandleAgentChatAsync(session, packet);

                case MessageID.AgentHistoryRequest:
                    return AgentHandler.HandleAgentHistoryAsync(session, packet);

                default:
                    Console.WriteLine($"未知消息:{messageId}");
                    break;
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// 心跳请求处理方法
        /// 原样回带客户端时间戳并附服务端时间；回执本身即刷新会话空闲计时（收包路径统一重置）
        /// </summary>
        private static void HandlePing(ClientSession session, ReadOnlySpan<byte> body)
        {
            PingRequest request;
            try
            {
                request = PingRequest.Parser.ParseFrom(body);
            }
            catch (Exception e)
            {
                Console.WriteLine($"心跳包解析失败 Session={session.SessionID}: {e.Message}");
                return;
            }
            _ = session.Send(MessageID.PongResponse, new PongResponse
            {
                ClientMs = request.ClientMs,
                ServerMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
        }

        // ──────────────────────────────
        // 账号
        // ──────────────────────────────

        /// <summary>
        /// 
        /// 注册请求处理方法
        /// 查重后写入数据库，返回注册结果（不做自动登录，客户端注册后走正常登录流程）
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的注册请求消息体</param>
        private static void HandleRegister(ClientSession session, ReadOnlySpan<byte> body)
        {
            Console.WriteLine("收到注册请求");

            RegisterRequest request = RegisterRequest.Parser.ParseFrom(body);

            AccountRepository repository = new AccountRepository();
            (bool success, string message) = repository.Register(request.Account, request.Password);

            RegisterResponse response = new RegisterResponse
            {
                Success = success,
                Message = message,
            };
            Console.WriteLine($"注册结果:{request.Account} → {message}");

            _ = session.Send(MessageID.RegisterResponse, response);
        }

        /// <summary>
        /// 登录请求处理方法
        /// 验证账号密码，登录成功后将账号ID绑定到当前会话并返回结果；
        /// 勾选 remember_me 时额外签发自动登录令牌随响应下发（明文只下发这一次，库里只存哈希）
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的登录请求消息体</param>
        private static void HandleLogin(ClientSession session, ReadOnlySpan<byte> body)
        {
            Console.WriteLine("收到登录请求");

            LoginRequest request = LoginRequest.Parser.ParseFrom(body);

            AccountRepository repository = new AccountRepository();
            int? playerId = repository.Login(request.Account, request.Password);

            LoginResponse response = new LoginResponse();

            // 失败：账号不存在或密码错误
            if (playerId == null)
            {
                response.Success = false;
                response.Message = "账号或密码错误";
            }
            // 成功（含会话绑定与在线注册；账号已在线时由共享路径写失败文案）
            else if (CompleteLogin(session, playerId.Value, request.Account, response))
            {
                // 勾选"下次自动登录" → 签发令牌；重新签发会覆盖旧令牌（旧的立即作废）
                if (request.RememberMe)
                {
                    (string token, long expireMs) = new AutoLoginRepository().IssueToken(playerId.Value);
                    response.AutoLoginToken = token;
                    response.AutoLoginTokenExpireMs = expireMs;
                }
            }

            _ = session.Send(MessageID.LoginResponse, response);
        }

        /// <summary>
        /// 自动登录请求处理方法
        /// 用上次 remember_me 登录时保存的令牌免密码换会话；成功走与密码登录相同的会话绑定与在线注册，
        /// 并把滑动续期后的新过期时间随响应下发；失败统一文案（不区分账号不存在/令牌不匹配/已过期，避免账号探测）
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的自动登录请求消息体</param>
        private static void HandleTokenLogin(ClientSession session, ReadOnlySpan<byte> body)
        {
            Console.WriteLine("收到自动登录请求");

            TokenLoginRequest request = TokenLoginRequest.Parser.ParseFrom(body);

            LoginResponse response = new LoginResponse
            {
                Success = false,
                Message = "自动登录已失效，请重新登录",
            };

            int? playerId = new AccountRepository().GetPlayerIdByAccount(request.Account);
            if (playerId == null)
            {
                _ = session.Send(MessageID.TokenLoginResponse, response);
                return;
            }

            (bool valid, long expireMs) = new AutoLoginRepository().ValidateToken(playerId.Value, request.Token);
            if (!valid)
            {
                Console.WriteLine($"自动登录失败:玩家{playerId} 令牌无效或已过期");
                _ = session.Send(MessageID.TokenLoginResponse, response);
                return;
            }

            // 与密码登录相同的成功路径（账号已在线等失败文案由共享路径覆盖）
            if (!CompleteLogin(session, playerId.Value, request.Account, response))
            {
                _ = session.Send(MessageID.TokenLoginResponse, response);
                return;
            }

            // 滑动续期：新过期时间随响应下发，客户端更新本地保存（令牌本体不变）
            response.AutoLoginToken = request.Token;
            response.AutoLoginTokenExpireMs = expireMs;
            response.Message = "自动登录成功";

            _ = session.Send(MessageID.TokenLoginResponse, response);
        }

        /// <summary>
        /// 登录成功路径方法（密码登录与自动登录共用）
        /// 会话绑定 + 在线玩家注册 + 写响应的成功字段；同账号已有会话时顶号（踢下旧会话），绑定本身失败才写失败文案
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="account">参数名：account，类型：string，用途：账号名</param>
        /// <param name="response">参数名：response，类型：LoginResponse，用途：待回填的登录响应</param>
        /// <returns>返回值类型：bool，用途：成功返回true，绑定失败返回false</returns>
        private static bool CompleteLogin(ClientSession session, int playerId, string account, LoginResponse response)
        {
            // 同一账号只允许一个在线会话；已有会话则顶号（锁内摘册+标记，踢下动作在锁外做）
            if (!SessionManager.Instance.TryBindPlayer(session, playerId, out ClientSession? evicted))
            {
                response.Success = false;
                response.Message = "该连接已登录，请断开后重连";
                return false;
            }

            Console.WriteLine($"玩家登录成功:{account} Id={playerId}");
            response.Success = true;
            response.PlayerId = playerId;
            response.Message = "登录成功";

            // 构建在线玩家实体并加入管理器（聊天/好友等系统从这里查询在线状态）
            // 顶号场景下 AddPlayer 是字典索引器覆盖，天然替换旧实体，故这里不需要对 PlayerManager 做任何清理
            PlayerManager.Instance.AddPlayer(new PlayerEntity
            {
                PlayerId = playerId,
                Account = account,
                LoginTimeUtc = DateTime.UtcNow,
            });

            // 被顶号的旧会话：通知写完再关流（KickOut 内部处理顺序与宽限期），
            // 不先通知就关 → 客户端只看到断线，会自动重连回来与新会话无限互踢
            if (evicted != null)
            {
                Console.WriteLine($"[顶号] 踢下旧会话 SessionID={evicted.SessionID} 玩家={playerId}");
                evicted.KickOut("账号在其它设备登录");
            }

            // 登录主动简报（Phase 5 ②）：阿福上线问好+待办汇总；后台执行不阻塞登录回执
            AgentHandler.PushLoginBrief(session, playerId);

            // 阿福主动提醒（悬浮球 3107）：巡检封顶按"每次登录会话"计，登录时重置计数
            AgentReminderService.OnPlayerLogin(playerId);

            // 成就登录初始化：补齐应激活行，并按历史统计静默重算（不推送，避免登录瞬间刷旧成就）
            AchievementService.OnPlayerLogin(playerId);

            return true;
        }

        // ──────────────────────────────
        // 背包（服务端权威：规则与校验都在 BagRepository，客户端只渲染返回的快照）
        // ──────────────────────────────

        /// <summary>
        /// 背包列表处理方法
        /// 返回容量 + 非空槽位快照 + 物品静态配置表（客户端据此建运行时表并补齐空槽占位）
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        private static void HandleBagList(ClientSession session)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.BagListResponse,
                    new BagListResponse { Success = false, Message = "请先登录" });
                return;
            }

            BagRepository bag = new();
            BagListResponse response = new()
            {
                Success = true,
                Message = "OK",
                Capacity = BagRepository.WeaponCapacity,
            };
            response.Slots.AddRange(BuildBagSlots(bag, session.PlayerId));
            foreach ((int itemId, string name, int itemType, int rarity, string iconKey, string description, int maxStack) in bag.GetAllConfigs())
            {
                response.Configs.Add(new ItemConfigInfo
                {
                    ItemId = itemId,
                    Name = name,
                    ItemType = itemType,
                    Rarity = rarity,
                    IconKey = iconKey,
                    Desc = description,
                    MaxStack = maxStack,
                });
            }

            Console.WriteLine($"背包列表返回:{session.PlayerId} {response.Slots.Count}个非空槽 / {response.Configs.Count}条配置");
            _ = session.Send(MessageID.BagListResponse, response);
        }

        /// <summary>背包使用处理方法（服务端校验归属与数量后扣减，响应带最新槽位快照）。</summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的请求消息体</param>
        private static void HandleBagUse(ClientSession session, ReadOnlySpan<byte> body)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.BagUseResponse,
                    new BagUseResponse { Success = false, Message = "请先登录" });
                return;
            }

            BagUseRequest request = BagUseRequest.Parser.ParseFrom(body);
            BagRepository bag = new();
            int itemId = bag.GetSlotItemId(session.PlayerId, request.SlotIndex);   // 任务系统需要知道"变了哪个物品"
            (bool success, string message) = bag.UseItem(session.PlayerId, request.SlotIndex, request.Count);

            BagUseResponse response = new() { Success = success, Message = message };
            response.Slots.AddRange(BuildBagSlots(bag, session.PlayerId));
            Console.WriteLine($"背包使用:{session.PlayerId} 槽{request.SlotIndex} 数量{request.Count} → {message}");
            _ = session.Send(MessageID.BagUseResponse, response);

            // 物品减少 → 收集类任务进度按实时持有量重算（可能从"可提交"回退为"进行中"）
            if (success) QuestHandler.RaiseItemChanged(session.PlayerId, itemId);
        }

        /// <summary>背包丢弃处理方法（数量扣减，丢光则清空该槽）。</summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的请求消息体</param>
        private static void HandleBagDiscard(ClientSession session, ReadOnlySpan<byte> body)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.BagDiscardResponse,
                    new BagDiscardResponse { Success = false, Message = "请先登录" });
                return;
            }

            BagDiscardRequest request = BagDiscardRequest.Parser.ParseFrom(body);
            BagRepository bag = new();
            int itemId = bag.GetSlotItemId(session.PlayerId, request.SlotIndex);   // 任务系统需要知道"变了哪个物品"
            (bool success, string message) = bag.DiscardItem(session.PlayerId, request.SlotIndex, request.Count);

            BagDiscardResponse response = new() { Success = success, Message = message };
            response.Slots.AddRange(BuildBagSlots(bag, session.PlayerId));
            Console.WriteLine($"背包丢弃:{session.PlayerId} 槽{request.SlotIndex} 数量{request.Count} → {message}");
            _ = session.Send(MessageID.BagDiscardResponse, response);

            // 物品减少 → 收集类任务进度按实时持有量重算（可能从"可提交"回退为"进行中"）
            if (success) QuestHandler.RaiseItemChanged(session.PlayerId, itemId);
        }

        /// <summary>背包整理处理方法（同类合并 + 排序重排，规则在 BagRepository）。</summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        private static void HandleBagTidy(ClientSession session)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.BagTidyResponse,
                    new BagTidyResponse { Success = false, Message = "请先登录" });
                return;
            }

            BagRepository bag = new();
            (bool success, string message) = bag.Tidy(session.PlayerId);

            BagTidyResponse response = new() { Success = success, Message = message };
            response.Slots.AddRange(BuildBagSlots(bag, session.PlayerId));
            Console.WriteLine($"背包整理:{session.PlayerId} → {message}");
            _ = session.Send(MessageID.BagTidyResponse, response);
        }

        /// <summary>背包换位处理方法（拖拽换位的服务端落地，含与空槽交换）。</summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的请求消息体</param>
        private static void HandleBagSwap(ClientSession session, ReadOnlySpan<byte> body)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.BagSwapResponse,
                    new BagSwapResponse { Success = false, Message = "请先登录" });
                return;
            }

            BagSwapRequest request = BagSwapRequest.Parser.ParseFrom(body);
            BagRepository bag = new();
            (bool success, string message) = bag.SwapSlots(session.PlayerId, request.FromSlot, request.ToSlot);

            BagSwapResponse response = new() { Success = success, Message = message };
            response.Slots.AddRange(BuildBagSlots(bag, session.PlayerId));
            Console.WriteLine($"背包换位:{session.PlayerId} {request.FromSlot}↔{request.ToSlot} → {message}");
            _ = session.Send(MessageID.BagSwapResponse, response);
        }

        /// <summary>测试发放处理方法（本期物品唯一来源，调试用）。</summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的请求消息体</param>
        private static void HandleBagGrant(ClientSession session, ReadOnlySpan<byte> body)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.BagGrantResponse,
                    new BagGrantResponse { Success = false, Message = "请先登录" });
                return;
            }

            BagGrantRequest request = BagGrantRequest.Parser.ParseFrom(body);
            BagRepository bag = new();
            (bool success, string message, int granted) = bag.Grant(session.PlayerId, request.ItemId, request.Count);

            BagGrantResponse response = new() { Success = success, Message = message };
            response.Slots.AddRange(BuildBagSlots(bag, session.PlayerId));
            Console.WriteLine($"背包发放:{session.PlayerId} 物品{request.ItemId} 数量{request.Count} → {message}");
            _ = session.Send(MessageID.BagGrantResponse, response);

            // 物品增加 → 收集类任务重算 + 生涯"累计获得物品"按实际放入的单位数累计
            if (success) QuestHandler.RaiseItemGained(session.PlayerId, request.ItemId, granted);
        }

        /// <summary>构造槽位快照列表（背包各操作响应统一回带最新快照，客户端整表刷新）。</summary>
        /// <param name="bag">参数名：bag，类型：BagRepository，用途：背包仓库实例</param>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <returns>返回值类型：List，用途：protobuf 槽位快照列表</returns>
        private static List<BagSlotInfo> BuildBagSlots(BagRepository bag, int playerId)
        {
            List<(int SlotIndex, int ItemId, int Count, long InstanceId)> rows = bag.GetSlots(playerId);
            var slots = new List<BagSlotInfo>(rows.Count);
            foreach ((int slotIndex, int itemId, int count, long instanceId) in rows)
            {
                slots.Add(new BagSlotInfo
                {
                    SlotIndex = slotIndex,
                    ItemId = itemId,
                    Count = count,
                    InstanceId = instanceId,
                });
            }
            return slots;
        }
    }
}
