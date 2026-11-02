using System.Collections.Concurrent;

using Common.Enum;
using Common.Network;

using CompeteServer.Database;
using CompeteServer.Manager;
using CompeteServer.Network;

namespace CompeteServer.Handler
{
    /// <summary>
    /// 副本掉落处理器类
    /// 掉落的信任边界：客户端只能上报"击杀了哪类敌人"，掉什么/掉多少由服务端按规则表裁决——
    /// 这是与"客户端要什么就给什么"（BagGrant 测试发放）相对的正确做法。
    /// 形态A（鸣潮式）：击杀回执时直接 Grant 入包，回执条目即"已经到手"的物品；
    /// 形态B（世界掉落）：击杀回执只发 60 秒限时令牌，玩家走近掉落物凭令牌拾取才入包。
    /// 入包统一走 BagRepository.Grant（背包满自动转邮件）+ QuestHandler.RaiseItemGained（收集任务/生涯联动）。
    /// </summary>
    public static class DungeonHandler
    {
        /// <summary>击杀上报限频：同一玩家两次上报最小间隔（毫秒），压制刷击杀脚本（正常攻击节奏远大于此值）</summary>
        private const long MinKillIntervalMs = 200;

        /// <summary>每玩家最近一次击杀上报时间（PlayerId → TickCount64）</summary>
        private static readonly ConcurrentDictionary<int, long> lastKillTick = new();

        /// <summary>
        /// 击杀上报处理方法
        /// 按敌人类型 roll 掉落：A 型直接入包，B 型发限时拾取令牌
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的击杀上报请求消息体</param>
        public static void HandleKillReport(ClientSession session, ReadOnlySpan<byte> body)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.DungeonKillReportResponse,
                    new DungeonKillReportResponse { Success = false, Message = "请先登录" });
                return;
            }

            DungeonKillReportRequest request = DungeonKillReportRequest.Parser.ParseFrom(body);

            // 限频：客户端击杀有明确节奏（攻击间隔/移动耗时），过快的一律视为异常上报
            long now = Environment.TickCount64;
            if (now - lastKillTick.GetValueOrDefault(session.PlayerId) < MinKillIntervalMs)
            {
                _ = session.Send(MessageID.DungeonKillReportResponse,
                    new DungeonKillReportResponse { Success = false, Message = "操作太频繁，请稍候再试" });
                return;
            }
            lastKillTick[session.PlayerId] = now;

            if (request.EnemyType != DungeonRepository.EnemyTypeDirect &&
                request.EnemyType != DungeonRepository.EnemyTypeWorld)
            {
                _ = session.Send(MessageID.DungeonKillReportResponse,
                    new DungeonKillReportResponse { Success = false, Message = $"未知的敌人类型:{request.EnemyType}" });
                return;
            }

            // 成就：击杀事实已成立（即使本次无掉落），累计击杀 + 按敌人类型计数
            AchievementService.ReportStat(session.PlayerId, CareerRepository.ScoreDungeonKill, 1);
            AchievementService.ReportStat(
                session.PlayerId, CareerRepository.ScoreDungeonKillByType(request.EnemyType), 1);

            DungeonRepository dungeon = new();
            List<(int ItemId, int Count)> rolled = dungeon.RollDrops(request.EnemyType);
            if (rolled.Count == 0)
            {
                _ = session.Send(MessageID.DungeonKillReportResponse,
                    new DungeonKillReportResponse { Success = true, Message = "本次没有掉落" });
                Console.WriteLine($"副本击杀:{session.PlayerId} 类型{request.EnemyType} → 本次没有掉落");
                return;
            }

            DungeonKillReportResponse response = new() { Success = true };
            var notes = new List<string>();

            if (request.EnemyType == DungeonRepository.EnemyTypeDirect)
            {
                // 形态A（鸣潮式直接入包）：roll 完立即入账——回执里的条目就是"已经到手"的物品
                foreach ((int itemId, int count) in rolled)
                {
                    (bool ok, string message, int granted) = new BagRepository().Grant(session.PlayerId, itemId, count);
                    if (!ok)
                    {
                        notes.Add($"{dungeon.GetItemName(itemId)}×{count} 未放入：{message}");
                        continue;
                    }
                    if (granted < count)
                    {
                        notes.Add($"{dungeon.GetItemName(itemId)}×{count - granted} 背包放不下已转入邮箱");
                    }
                    response.Drops.Add(new DungeonDropInfo
                    {
                        ItemId = itemId,
                        Count = granted,
                        ItemName = dungeon.GetItemName(itemId),
                    });
                    if (granted > 0) QuestHandler.RaiseItemGained(session.PlayerId, itemId, granted);
                }
                response.Message = notes.Count > 0 ? string.Join("；", notes) : "OK";
                Console.WriteLine($"副本击杀(直接入包):{session.PlayerId} 类型{request.EnemyType} → {response.Drops.Count}种入包 | {response.Message}");
            }
            else
            {
                // 形态B（世界掉落）：roll 完只发限时令牌，物品在玩家拾取前不入包
                foreach ((int itemId, int count) in rolled)
                {
                    (string? token, long expireMs) = dungeon.CreatePendingDrop(session.PlayerId, itemId, count);
                    if (token == null)
                    {
                        // 上限拦截：本次击杀作废并要求玩家先拾取已有的（半个令牌都不发，避免回执半截）
                        response.Success = false;
                        response.Message = "待拾取的掉落物太多，先拾取已有的掉落物";
                        response.Drops.Clear();
                        break;
                    }
                    response.Drops.Add(new DungeonDropInfo
                    {
                        ItemId = itemId,
                        Count = count,
                        ItemName = dungeon.GetItemName(itemId),
                        DropToken = token,
                        ExpireMs = expireMs,
                    });
                }
                if (response.Success) response.Message = "OK";
                Console.WriteLine($"副本击杀(世界掉落):{session.PlayerId} 类型{request.EnemyType} → {response.Drops.Count}个待拾取令牌");
            }

            _ = session.Send(MessageID.DungeonKillReportResponse, response);
        }

        /// <summary>
        /// 拾取处理方法
        /// 校验令牌归属与有效期后 Grant 入包（原子摘除令牌，同一令牌只能领一次）
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的拾取请求消息体</param>
        public static void HandlePickup(ClientSession session, ReadOnlySpan<byte> body)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.DungeonPickupResponse,
                    new DungeonPickupResponse { Success = false, Message = "请先登录" });
                return;
            }

            DungeonPickupRequest request = DungeonPickupRequest.Parser.ParseFrom(body);
            DungeonRepository dungeon = new();

            (bool claimed, string claimMessage, int itemId, int count) =
                dungeon.TryPickup(session.PlayerId, request.DropToken);
            if (!claimed)
            {
                _ = session.Send(MessageID.DungeonPickupResponse,
                    new DungeonPickupResponse { Success = false, Message = claimMessage });
                Console.WriteLine($"副本拾取:{session.PlayerId} → {claimMessage}");
                return;
            }

            // 成就：拾取事实已成立（令牌原子摘除成功），累计拾取次数
            AchievementService.ReportStat(session.PlayerId, CareerRepository.ScoreDungeonPickup, 1);

            (bool ok, string message, int granted) = new BagRepository().Grant(session.PlayerId, itemId, count);
            DungeonPickupResponse response = new()
            {
                Success = ok,
                Message = message,
                Item = new DungeonDropInfo
                {
                    ItemId = itemId,
                    Count = granted,
                    ItemName = dungeon.GetItemName(itemId),
                },
            };
            if (ok && granted > 0) QuestHandler.RaiseItemGained(session.PlayerId, itemId, granted);

            Console.WriteLine($"副本拾取:{session.PlayerId} 物品{itemId} 数量{count} 实入{granted} → {message}");
            _ = session.Send(MessageID.DungeonPickupResponse, response);
        }
    }
}
