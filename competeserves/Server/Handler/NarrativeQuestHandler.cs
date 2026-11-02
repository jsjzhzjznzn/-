using Common.Enum;
using Common.Network;

using CompeteServer.Database;
using CompeteServer.Manager;
using CompeteServer.Network;

namespace CompeteServer.Handler
{
    /// <summary>
    /// 叙事任务消息处理类（自 MessageHandler 路由转发）
    /// 服务端权威模式（鸣潮式）：客户端只报玩法事件（2705）与接取/提交请求（2706/2708），
    /// 全部规则由 NarrativeQuestRepository 引擎裁决（前置链/进度推进/达标/限时惰性判定），
    /// 任何变更通过 2710 全量快照推送同步给在线客户端（客户端篡改内存会被推送覆盖）；
    /// 身份一律取自会话绑定（session.PlayerId），与 2501 服务端权威任务体系并行互不影响
    /// </summary>
    public static class NarrativeQuestHandler
    {
        // ──────────────────────────────
        // 消息处理
        // ──────────────────────────────

        /// <summary>
        /// 叙事任务拉取处理方法
        /// 登录成功后客户端请求拉取，返回该玩家的全量状态快照（引擎内存缓存优先，含惰性限时判定）
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        public static void HandleSync(ClientSession session)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.NarrativeQuestSyncResponse,
                    new NarrativeQuestSyncResponse { Success = false, Message = "请先登录" });
                return;
            }

            NarrativeQuestRepository repository = new();
            NarrativeQuestSyncResponse response = new()
            {
                Success = true,
                Message = "OK",
                Quests = { BuildEntries(repository.GetSnapshot(session.PlayerId)) },
            };

            Console.WriteLine($"叙事任务拉取:{session.PlayerId} 返回 {response.Quests.Count} 条");
            _ = session.Send(MessageID.NarrativeQuestSyncResponse, response);
        }

        /// <summary>
        /// 叙事任务接取处理方法
        /// 服务端校验（配置存在/前置链全部提交/未在执行中）通过后置执行中并落库，
        /// 响应附带操作后的全量快照供客户端整表镜像
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的请求消息体</param>
        public static void HandleAccept(ClientSession session, ReadOnlySpan<byte> body)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.NarrativeQuestAcceptResponse,
                    new NarrativeQuestAcceptResponse { Success = false, Message = "请先登录" });
                return;
            }

            NarrativeQuestAcceptRequest request = NarrativeQuestAcceptRequest.Parser.ParseFrom(body);
            NarrativeQuestRepository repository = new();
            (bool success, string message, List<MmQuestRow> snapshot) =
                repository.Accept(session.PlayerId, request.QuestId);

            Console.WriteLine($"叙事任务接取:{session.PlayerId} 任务{request.QuestId} → {(success ? "OK" : message)}");
            _ = session.Send(MessageID.NarrativeQuestAcceptResponse, new NarrativeQuestAcceptResponse
            {
                Success = success,
                Message = message,
                Quests = { BuildEntries(snapshot) },
            });
        }

        /// <summary>
        /// 叙事任务提交处理方法
        /// 服务端校验（执行中/全目标达标/未过期）通过后置提交并落库，
        /// 响应附带操作后的全量快照供客户端整表镜像
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的请求消息体</param>
        public static void HandleSubmit(ClientSession session, ReadOnlySpan<byte> body)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.NarrativeQuestSubmitResponse,
                    new NarrativeQuestSubmitResponse { Success = false, Message = "请先登录" });
                return;
            }

            NarrativeQuestSubmitRequest request = NarrativeQuestSubmitRequest.Parser.ParseFrom(body);
            NarrativeQuestRepository repository = new();
            (bool success, string message, List<MmQuestRow> snapshot, int grantedUnits) =
                repository.Submit(session.PlayerId, request.QuestId);

            Console.WriteLine($"叙事任务提交:{session.PlayerId} 任务{request.QuestId} → {(success ? "OK" : message)}（发放 {grantedUnits} 件）");
            _ = session.Send(MessageID.NarrativeQuestSubmitResponse, new NarrativeQuestSubmitResponse
            {
                Success = success,
                Message = message,
                Quests = { BuildEntries(snapshot) },
            });

            if (!success) return;

            // 提交成功：先提示（含实际发放件数，客户端用于弹奖励条），再通知背包重拉
            string title = repository.GetConfig(request.QuestId)?.Title ?? $"任务{request.QuestId}";
            _ = session.Send(MessageID.NarrativeQuestToastNotify, new NarrativeQuestToastNotify
            {
                Title = "任务完成",
                Message = grantedUnits > 0 ? $"【{title}】奖励已放入背包（{grantedUnits} 件）" : $"【{title}】已完成",
                GrantedUnits = grantedUnits,
            });
        }

        /// <summary>
        /// 叙事任务放弃处理方法
        /// 服务端校验（执行中）通过后置未激活、进度清零并落库，
        /// 响应附带操作后的全量快照供客户端整表镜像
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的请求消息体</param>
        public static void HandleAbandon(ClientSession session, ReadOnlySpan<byte> body)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.NarrativeQuestAbandonResponse,
                    new NarrativeQuestAbandonResponse { Success = false, Message = "请先登录" });
                return;
            }

            NarrativeQuestAbandonRequest request = NarrativeQuestAbandonRequest.Parser.ParseFrom(body);
            (bool success, string message, List<MmQuestRow> snapshot) =
                new NarrativeQuestRepository().Abandon(session.PlayerId, request.QuestId);

            Console.WriteLine($"叙事任务放弃:{session.PlayerId} 任务{request.QuestId} → {(success ? "OK" : message)}");
            _ = session.Send(MessageID.NarrativeQuestAbandonResponse, new NarrativeQuestAbandonResponse
            {
                Success = success,
                Message = message,
                Quests = { BuildEntries(snapshot) },
            });
        }

        /// <summary>
        /// 叙事任务玩法事件上报处理方法
        /// 客户端只报"发生了什么"（击杀/收集/到达/对话），本方法不回独立回执；
        /// 引擎匹配目标推进进度，有任何变更（或惰性到期）即向在线客户端推 2710 全量快照
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的请求消息体</param>
        public static void HandleEventReport(ClientSession session, ReadOnlySpan<byte> body)
        {
            if (session.PlayerId <= 0) return;   // 未登录的上报直接丢弃（无回执协议，不值得回包）

            NarrativeQuestEventReport report = NarrativeQuestEventReport.Parser.ParseFrom(body);

            // 护栏：数量封顶（到达固定按 1，由引擎处理），事件Key长度限制
            int count = Math.Clamp(report.Count, 0, NarrativeQuestRepository.MaxEventCount);
            string targetKey = report.TargetKey.Length > 128 ? report.TargetKey[..128] : report.TargetKey;
            string graphName = report.GraphName.Length > 128 ? report.GraphName[..128] : report.GraphName;
            string eventKey = report.EventKey.Length > 128 ? report.EventKey[..128] : report.EventKey;

            NarrativeQuestRepository repository = new();
            bool advanced = repository.OnGameEvent(session.PlayerId, (int)report.EventType, targetKey, count, graphName, eventKey);
            if (advanced)
            {
                PushNarrativeState(session.PlayerId);
            }
        }

        // ──────────────────────────────
        // 推送（任何变更后向在线玩家推全量快照）
        // ──────────────────────────────

        /// <summary>
        /// 推送叙事任务全量状态（2710）：任何事件推进/限时到期导致的变更后调用；
        /// 不在线静默跳过（下次登录拉取时自然同步），照 2501 PushQuestNotify 模式
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：目标玩家ID</param>
        public static void PushNarrativeState(int playerId)
        {
            ClientSession? session = SessionManager.Instance.GetSessionByPlayerId(playerId);
            if (session == null) return;

            NarrativeQuestStateNotify notify = new()
            {
                Quests = { BuildEntries(new NarrativeQuestRepository().GetSnapshot(playerId)) },
            };
            _ = session.Send(MessageID.NarrativeQuestStateNotify, notify);
        }

        // ──────────────────────────────
        // 工具
        // ──────────────────────────────

        /// <summary>
        /// 引擎行 → 协议条目（remain_seconds 由权威 expire_time_ms 实时折算，客户端据此重启限时计时）
        /// </summary>
        private static List<NarrativeQuestStateEntry> BuildEntries(List<MmQuestRow> rows)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            List<NarrativeQuestStateEntry> entries = new();
            foreach (MmQuestRow row in rows)
            {
                float remainSeconds = row.ExpireTimeMs > 0
                    ? Math.Max(0f, (row.ExpireTimeMs - now) / 1000f)
                    : 0f;
                entries.Add(new NarrativeQuestStateEntry
                {
                    QuestId = row.QuestId,
                    State = row.State,
                    AcceptedAt = row.AcceptedAt,
                    RemainSeconds = remainSeconds,
                    ProgressList = { row.Progress },
                });
            }
            return entries;
        }
    }
}
