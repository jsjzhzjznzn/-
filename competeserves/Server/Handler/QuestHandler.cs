using Common.Enum;
using Common.Network;

using CompeteServer.Database;
using CompeteServer.Manager;
using CompeteServer.Network;

namespace CompeteServer.Handler
{
    /// <summary>
    /// 任务消息处理类（自 MessageHandler 拆出）
    /// 负责任务列表 / 接取 / 提交 / 放弃 / 调试改进度的消息处理，并提供玩法事件接入任务的静态入口
    /// 身份一律取自会话绑定（session.PlayerId），客户端无法伪造
    /// </summary>
    public static class QuestHandler
    {
        // ──────────────────────────────
        // 消息处理
        // ──────────────────────────────

        /// <summary>
        /// 任务列表处理方法
        /// 返回任务静态配置表（含奖励物品名、父子关系）+ 玩家已接取任务的状态快照 + 生涯数据。
        /// 拉列表前先做成就自动激活（幂等）：成就不进"可接取"池，激活后由生涯数据持续驱动进度
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        public static void HandleQuestList(ClientSession session)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.QuestListResponse,
                    new QuestListResponse { Success = false, Message = "请先登录" });
                return;
            }

            QuestRepository repository = new();
            repository.AutoActivateAchievements(session.PlayerId);
            Dictionary<int, string> itemNames = LoadItemNames();

            QuestListResponse response = new() { Success = true, Message = "OK" };
            foreach (QuestConfig config in repository.GetConfigs())
            {
                QuestCfgInfo info = new()
                {
                    QuestId = config.QuestId,
                    Name = config.Name,
                    Description = config.Description,
                    Category = config.Category,
                    GoalType = config.GoalType,
                    GoalParam1 = config.GoalParam1,
                    GoalParam2 = config.GoalParam2,
                    TargetProgress = config.TargetProgress,
                    PrevQuestId = config.PrevQuestId,
                    ParentQuestId = config.ParentQuestId,
                };
                foreach ((int itemId, int count) in config.Rewards)
                {
                    info.Rewards.Add(new QuestRewardInfo
                    {
                        ItemId = itemId,
                        Count = count,
                        Name = itemNames.TryGetValue(itemId, out string? name) ? name : $"#{itemId}",
                    });
                }
                response.Configs.Add(info);
            }
            response.Quests.AddRange(BuildQuestInfos(repository.BuildSnapshot(session.PlayerId)));

            foreach (KeyValuePair<int, int> pair in new CareerRepository().GetValues(session.PlayerId))
            {
                response.CareerScores.Add(new CareerScoreInfo { ScoreId = pair.Key, Value = pair.Value });
            }

            Console.WriteLine($"任务列表返回:{session.PlayerId} {response.Configs.Count}条配置 / {response.Quests.Count}条进度 / {response.CareerScores.Count}项生涯");
            _ = session.Send(MessageID.QuestListResponse, response);
        }

        /// <summary>
        /// 接取任务处理方法（服务端校验前置任务与重复接取）
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的请求消息体</param>
        public static void HandleQuestAccept(ClientSession session, ReadOnlySpan<byte> body)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.QuestAcceptResponse,
                    new QuestAcceptResponse { Success = false, Message = "请先登录" });
                return;
            }

            QuestAcceptRequest request = QuestAcceptRequest.Parser.ParseFrom(body);
            QuestRepository repository = new();
            (bool success, string message) = repository.Accept(session.PlayerId, request.QuestId);

            Console.WriteLine($"接取任务:{session.PlayerId} 任务{request.QuestId} → {message}");
            _ = session.Send(MessageID.QuestAcceptResponse, new QuestAcceptResponse
            {
                Success = success,
                Message = message,
                Quests = { BuildQuestInfos(repository.BuildSnapshot(session.PlayerId)) },
            });
        }

        /// <summary>
        /// 提交任务处理方法（收集类为交付语义，服务端事务内校验持有量、扣物品、发奖励）
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的请求消息体</param>
        public static void HandleQuestSubmit(ClientSession session, ReadOnlySpan<byte> body)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.QuestSubmitResponse,
                    new QuestSubmitResponse { Success = false, Message = "请先登录" });
                return;
            }

            QuestSubmitRequest request = QuestSubmitRequest.Parser.ParseFrom(body);
            QuestRepository repository = new();
            (bool success, string message, int grantedUnits) = repository.Submit(session.PlayerId, request.QuestId);

            if (success)
            {
                // 生涯写入（提交任务数 / 奖励物品数）已在仓库层完成（手动/自动提交口径一致）；
                // 这里只做成就按实时值重算并推送 + 多阶段成就下一阶段自动激活
                PushQuestNotify(session.PlayerId,
                    repository.OnCareerChanged(session.PlayerId, CareerRepository.ScoreQuestSubmit));
                if (grantedUnits > 0)
                {
                    PushQuestNotify(session.PlayerId,
                        repository.OnCareerChanged(session.PlayerId, CareerRepository.ScoreItemGain));
                }
                repository.AutoActivateAchievements(session.PlayerId);
            }

            Console.WriteLine($"提交任务:{session.PlayerId} 任务{request.QuestId} → {message}");
            _ = session.Send(MessageID.QuestSubmitResponse, new QuestSubmitResponse
            {
                Success = success,
                Message = message,
                Quests = { BuildQuestInfos(repository.BuildSnapshot(session.PlayerId)) },
            });
        }

        /// <summary>
        /// 放弃任务处理方法（仅进行中的任务可放弃，放弃后回到未接取）
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的请求消息体</param>
        public static void HandleQuestAbandon(ClientSession session, ReadOnlySpan<byte> body)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.QuestAbandonResponse,
                    new QuestAbandonResponse { Success = false, Message = "请先登录" });
                return;
            }

            QuestAbandonRequest request = QuestAbandonRequest.Parser.ParseFrom(body);
            QuestRepository repository = new();
            (bool success, string message) = repository.Abandon(session.PlayerId, request.QuestId);

            Console.WriteLine($"放弃任务:{session.PlayerId} 任务{request.QuestId} → {message}");
            _ = session.Send(MessageID.QuestAbandonResponse, new QuestAbandonResponse
            {
                Success = success,
                Message = message,
                Quests = { BuildQuestInfos(repository.BuildSnapshot(session.PlayerId)) },
            });
        }

        /// <summary>
        /// 调试：设置任务进度处理方法（GM 指令，进度以存档缓存为准，便于联调）
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的请求消息体</param>
        public static void HandleQuestGmSetProgress(ClientSession session, ReadOnlySpan<byte> body)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.QuestGmSetProgressResponse,
                    new QuestGmSetProgressResponse { Success = false, Message = "请先登录" });
                return;
            }

            QuestGmSetProgressRequest request = QuestGmSetProgressRequest.Parser.ParseFrom(body);
            QuestRepository repository = new();
            (bool success, string message) = repository.GmSetProgress(session.PlayerId, request.QuestId, request.Progress);

            Console.WriteLine($"任务GM改进度:{session.PlayerId} 任务{request.QuestId} → {message}");
            _ = session.Send(MessageID.QuestGmSetProgressResponse, new QuestGmSetProgressResponse
            {
                Success = success,
                Message = message,
                Quests = { BuildQuestInfos(repository.BuildSnapshot(session.PlayerId)) },
            });
        }

        // ──────────────────────────────
        // 玩法事件接入（供其他模块调用：复杂的是其它模块要配合任务的部分）
        // ──────────────────────────────

        /// <summary>
        /// 累计类目标事件入口（挑战成绩 / 加好友 / 发消息）
        /// 推进进度并把变化推给在线玩家；不在线则只落库，上线拉列表即补齐
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="goalType">参数名：goalType，类型：int，用途：目标类型（见 QuestRepository.GoalType*）</param>
        /// <param name="param1">参数名：param1，类型：int，用途：事件参数（挑战ID；其余传 0）</param>
        /// <param name="value">参数名：value，类型：int，用途：事件值（挑战为分数；其余为增量，通常 1）</param>
        public static void RaiseGameEvent(int playerId, int goalType, int param1, int value)
        {
            if (playerId <= 0) return;
            List<(int QuestId, int State, int Progress)> changed =
                new QuestRepository().OnGameEvent(playerId, goalType, param1, value);
            PushQuestNotify(playerId, changed);
        }

        /// <summary>
        /// 收集类目标事件入口（物品获得 / 使用 / 丢弃后调用，进度按实时持有量重算）
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="itemId">参数名：itemId，类型：int，用途：发生变化的物品ID</param>
        public static void RaiseItemChanged(int playerId, int itemId)
        {
            if (playerId <= 0 || itemId <= 0) return;
            List<(int QuestId, int State, int Progress)> changed =
                new QuestRepository().OnItemChanged(playerId, itemId);
            PushQuestNotify(playerId, changed);
        }

        // ──────────────────────────────
        // 生涯数据 + 成就（其他模块统一从这些入口上报，避免各处散写）
        // ──────────────────────────────

        /// <summary>
        /// 挑战成绩事件：任务进度推进 + 生涯（累计挑战次数 +1、历史最高分取大）+ 成就重算推送
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="challengeId">参数名：challengeId，类型：int，用途：挑战ID</param>
        /// <param name="score">参数名：score，类型：int，用途：本次成绩</param>
        public static void RaiseChallengeScore(int playerId, int challengeId, int score)
        {
            RaiseGameEvent(playerId, QuestRepository.GoalTypeChallenge, challengeId, score);
            RaiseCareerDelta(playerId, CareerRepository.ScoreChallengeCount, 1);
            RaiseCareerMax(playerId, CareerRepository.ScoreBestChallenge, score);

            // 隐藏成就：单次 0 分（独立统计，避免"最高分≥0"这种恒真条件）
            if (score == 0)
            {
                AchievementService.ReportStat(playerId, CareerRepository.ScoreZeroChallenge, 1);
            }
        }

        /// <summary>
        /// 发送消息事件：任务进度推进 + 生涯（累计发消息数 +1）+ 成就重算推送
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：发送方玩家ID</param>
        public static void RaiseMessageSent(int playerId)
        {
            RaiseGameEvent(playerId, QuestRepository.GoalTypeSendMessage, 0, 1);
            RaiseCareerDelta(playerId, CareerRepository.ScoreMessageCount, 1);
        }

        /// <summary>
        /// 获得物品事件：收集类任务重算 + 生涯（累计获得物品数按单位计）+ 成就重算推送
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="itemId">参数名：itemId，类型：int，用途：物品ID</param>
        /// <param name="units">参数名：units，类型：int，用途：实际获得的单位数（背包满时小于请求数）</param>
        public static void RaiseItemGained(int playerId, int itemId, int units)
        {
            RaiseItemChanged(playerId, itemId);
            if (units > 0) RaiseCareerDelta(playerId, CareerRepository.ScoreItemGain, units);

            // 成就：持有数量类条件（按物品ID索引，只重算相关成就）
            AchievementService.OnItemChanged(playerId, itemId);
        }

        /// <summary>生涯指标累加 + 成就按实时值重算 + 推送在线玩家。</summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="scoreId">参数名：scoreId，类型：int，用途：生涯指标ID（见 CareerRepository.Score*）</param>
        /// <param name="delta">参数名：delta，类型：int，用途：增量</param>
        public static void RaiseCareerDelta(int playerId, int scoreId, int delta)
        {
            if (playerId <= 0 || delta == 0) return;
            new CareerRepository().Add(playerId, scoreId, delta);
            PushQuestNotify(playerId, new QuestRepository().OnCareerChanged(playerId, scoreId));
            AchievementService.OnCareerStatChanged(playerId, scoreId);
        }

        /// <summary>生涯"越大越优"指标（历史最高分）记录 + 成就重算推送。</summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="scoreId">参数名：scoreId，类型：int，用途：生涯指标ID</param>
        /// <param name="value">参数名：value，类型：int，用途：本次观测值（只保留历史最大）</param>
        public static void RaiseCareerMax(int playerId, int scoreId, int value)
        {
            if (playerId <= 0) return;
            new CareerRepository().SetMax(playerId, scoreId, value);
            PushQuestNotify(playerId, new QuestRepository().OnCareerChanged(playerId, scoreId));
            AchievementService.OnCareerStatChanged(playerId, scoreId);
        }

        // ──────────────────────────────
        // 内部工具
        // ──────────────────────────────

        /// <summary>把仓库快照转成协议条目。</summary>
        private static List<PlayerQuestInfo> BuildQuestInfos(List<(int QuestId, int State, int Progress)> snapshot)
        {
            var infos = new List<PlayerQuestInfo>(snapshot.Count);
            foreach ((int questId, int state, int progress) in snapshot)
            {
                infos.Add(new PlayerQuestInfo { QuestId = questId, State = state, Progress = progress });
            }
            return infos;
        }

        /// <summary>
        /// 任务进度推送：只推给在线玩家（不在线不必推，进度已落库）
        /// 文案优先提示"可提交"，其余变化只刷数据不打扰
        /// </summary>
        private static void PushQuestNotify(int playerId, List<(int QuestId, int State, int Progress)> changed)
        {
            if (changed.Count == 0) return;

            ClientSession? session = SessionManager.Instance.GetSessionByPlayerId(playerId);
            if (session == null) return;

            QuestProgressNotify notify = new();
            var finishedNames = new List<string>();
            Dictionary<int, QuestConfig>? configMap = null;

            foreach ((int questId, int state, int progress) in changed)
            {
                notify.Quests.Add(new PlayerQuestInfo { QuestId = questId, State = state, Progress = progress });

                if (state == QuestRepository.StateFinished)
                {
                    // 只有出现"可提交"时才查配置补文案（绝大多数事件不触发；走静态缓存）
                    configMap ??= new QuestRepository().GetConfigMap();
                    if (configMap.TryGetValue(questId, out QuestConfig? config))
                    {
                        finishedNames.Add(config.Name);
                    }
                }
            }

            if (finishedNames.Count > 0)
            {
                notify.Message = $"任务【{string.Join("、", finishedNames)}】已完成，可提交！";
            }

            _ = session.Send(MessageID.QuestProgressNotify, notify);
        }

        /// <summary>读取物品名表（任务配置里的奖励只存物品ID，下发给客户端前补齐名字）。</summary>
        private static Dictionary<int, string> LoadItemNames()
        {
            var names = new Dictionary<int, string>();
            foreach ((int itemId, string name, int _, int _, string _, string _, int _) in
                     new BagRepository().GetAllConfigs())
            {
                names[itemId] = name;
            }
            return names;
        }
    }
}
