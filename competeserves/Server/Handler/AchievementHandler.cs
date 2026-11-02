using Common.Enum;
using Common.Network;

using CompeteServer.Database;
using CompeteServer.Manager;
using CompeteServer.Network;

namespace CompeteServer.Handler
{
    /// <summary>
    /// 成就消息处理（34xx）
    /// 列表/领取/GM 走这里；解锁推送由 AchievementService 统一发出。
    /// </summary>
    public static class AchievementHandler
    {
        /// <summary>
        /// 成就列表：先补齐应激活行并按现有统计静默重算，再返回配置 + 玩家状态 + 汇总。
        /// </summary>
        /// <param name="session">客户端会话</param>
        public static void HandleAchievementList(ClientSession session)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.AchievementListResponse,
                    new AchievementListResponse { Success = false, Message = "请先登录" });
                return;
            }

            AchievementRepository repository = new();
            repository.EnsurePlayerRows(session.PlayerId);
            List<AchievementUnlock> unlocks = repository.EvaluateAll(session.PlayerId);
            if (unlocks.Count > 0)
            {
                AchievementService.PushUnlocks(session.PlayerId, unlocks);
            }

            AchievementSnapshot snapshot = repository.BuildSnapshot(session.PlayerId);
            Dictionary<int, AchievementConfig> configs = repository.GetConfigMap();
            AchievementConditionContext context = repository.CreateContext(session.PlayerId, configs.Values);
            Dictionary<int, BagRepository.ItemConfig> itemConfigs = BagRepository.GetConfigMap();

            AchievementListResponse response = new() { Success = true, Message = "OK" };

            foreach (AchievementConfig config in configs.Values
                         .Where(item => item.Enabled)
                         .OrderBy(item => item.Series)
                         .ThenBy(item => item.Sort)
                         .ThenBy(item => item.AchievementId))
            {
                AchievementCfgInfo info = new()
                {
                    AchievementId = config.AchievementId,
                    Name = config.Name,
                    Description = config.Description,
                    Series = config.Series,
                    Rarity = config.Rarity,
                    Points = config.Points,
                    Hidden = config.Hidden,
                    PrevAchievementId = config.PrevAchievementId,
                    Sort = config.Sort,
                };

                foreach (AchievementConditionView condition in repository.BuildConditionViews(config, context))
                {
                    info.Conditions.Add(new AchievementConditionInfo
                    {
                        Seq = condition.Seq,
                        ConditionType = condition.ConditionType,
                        Param1 = condition.Param1,
                        Param2 = condition.Param2,
                        Target = condition.Target,
                        Current = condition.Current,
                        Text = condition.Text,
                    });
                }

                foreach ((int itemId, int count) in config.Rewards)
                {
                    info.Rewards.Add(new AchievementRewardInfo
                    {
                        ItemId = itemId,
                        Count = count,
                        Name = itemConfigs.TryGetValue(itemId, out BagRepository.ItemConfig? item)
                            ? item.Name
                            : $"#{itemId}",
                    });
                }

                response.Configs.Add(info);
            }

            foreach (PlayerAchievementRow row in snapshot.Rows)
            {
                configs.TryGetValue(row.AchievementId, out AchievementConfig? config);
                response.Achievements.Add(new PlayerAchievementInfo
                {
                    AchievementId = row.AchievementId,
                    State = row.State,
                    Progress = row.Progress,
                    Target = PrimaryTarget(config),
                    UnlockedAtMs = ToUnixMs(row.UnlockedAt),
                    ClaimedAtMs = ToUnixMs(row.ClaimedAt),
                });
            }

            response.Summary = BuildSummary(snapshot);
            Console.WriteLine(
                $"成就列表返回:{session.PlayerId} {response.Configs.Count}条配置 / {response.Achievements.Count}条状态 / 可领取{snapshot.Claimable}");
            _ = session.Send(MessageID.AchievementListResponse, response);
        }

        /// <summary>
        /// 领取成就奖励（服务端事务 + 幂等；奖励入包后联动任务/生涯/成就统计）。
        /// </summary>
        /// <param name="session">客户端会话</param>
        /// <param name="body">请求体</param>
        public static void HandleAchievementClaim(ClientSession session, ReadOnlySpan<byte> body)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.AchievementClaimResponse,
                    new AchievementClaimResponse { Success = false, Message = "请先登录" });
                return;
            }

            AchievementClaimRequest request = AchievementClaimRequest.Parser.ParseFrom(body);
            AchievementRepository repository = new();
            ClaimOutcome outcome = repository.Claim(session.PlayerId, request.AchievementIds);

            // 奖励实际入包后，走既有的物品漏斗：收集任务重算 + 生涯获得物品 + 成就（含持有类）
            if (outcome.Success && outcome.Granted.Count > 0)
            {
                foreach ((int itemId, int count) in outcome.Granted)
                {
                    QuestHandler.RaiseItemGained(session.PlayerId, itemId, count);
                }
            }

            AchievementSnapshot snapshot = repository.BuildSnapshot(session.PlayerId);
            Dictionary<int, AchievementConfig> configs = repository.GetConfigMap();
            Dictionary<int, BagRepository.ItemConfig> itemConfigs = BagRepository.GetConfigMap();

            AchievementClaimResponse response = new()
            {
                Success = outcome.Success,
                Message = outcome.Message,
                Summary = BuildSummary(snapshot),
            };

            foreach (int achievementId in outcome.ClaimedIds)
            {
                response.ClaimedIds.Add(achievementId);
            }

            foreach ((int itemId, int count) in outcome.Granted)
            {
                response.Granted.Add(new AchievementRewardInfo
                {
                    ItemId = itemId,
                    Count = count,
                    Name = itemConfigs.TryGetValue(itemId, out BagRepository.ItemConfig? item)
                        ? item.Name
                        : $"#{itemId}",
                });
            }

            foreach (PlayerAchievementRow row in snapshot.Rows)
            {
                configs.TryGetValue(row.AchievementId, out AchievementConfig? config);
                response.Achievements.Add(new PlayerAchievementInfo
                {
                    AchievementId = row.AchievementId,
                    State = row.State,
                    Progress = row.Progress,
                    Target = PrimaryTarget(config),
                    UnlockedAtMs = ToUnixMs(row.UnlockedAt),
                    ClaimedAtMs = ToUnixMs(row.ClaimedAt),
                });
            }

            Console.WriteLine(
                $"领取成就:{session.PlayerId} 请求[{string.Join(',', request.AchievementIds)}] → {outcome.Message} " +
                $"(入包{outcome.GrantedUnits}件/转邮件{outcome.Overflow.Sum(x => x.Count)}件)");
            _ = session.Send(MessageID.AchievementClaimResponse, response);
        }

        /// <summary>
        /// GM/联调：改进度、强制解锁、重置。
        /// </summary>
        /// <param name="session">客户端会话</param>
        /// <param name="body">请求体</param>
        public static void HandleAchievementGm(ClientSession session, ReadOnlySpan<byte> body)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.AchievementGmResponse,
                    new AchievementGmResponse { Success = false, Message = "请先登录" });
                return;
            }

            AchievementGmRequest request = AchievementGmRequest.Parser.ParseFrom(body);
            AchievementRepository repository = new();
            (bool success, string message) = repository.GmSet(
                session.PlayerId, request.AchievementId, request.Progress, request.Unlock, request.Reset);

            AchievementSnapshot snapshot = repository.BuildSnapshot(session.PlayerId);
            Dictionary<int, AchievementConfig> configs = repository.GetConfigMap();

            AchievementGmResponse response = new()
            {
                Success = success,
                Message = message,
                Summary = BuildSummary(snapshot),
            };

            foreach (PlayerAchievementRow row in snapshot.Rows)
            {
                configs.TryGetValue(row.AchievementId, out AchievementConfig? config);
                response.Achievements.Add(new PlayerAchievementInfo
                {
                    AchievementId = row.AchievementId,
                    State = row.State,
                    Progress = row.Progress,
                    Target = PrimaryTarget(config),
                    UnlockedAtMs = ToUnixMs(row.UnlockedAt),
                    ClaimedAtMs = ToUnixMs(row.ClaimedAt),
                });
            }

            Console.WriteLine($"成就GM:{session.PlayerId} 成就{request.AchievementId} → {message}");
            _ = session.Send(MessageID.AchievementGmResponse, response);
        }

        // ──────────────────────────────
        // 映射工具
        // ──────────────────────────────

        private static AchievementSummary BuildSummary(AchievementSnapshot snapshot) => new()
        {
            Total = snapshot.Total,
            Unlocked = snapshot.Unlocked,
            Claimed = snapshot.Claimed,
            Claimable = snapshot.Claimable,
            Points = snapshot.Points,
            TotalPoints = snapshot.TotalPoints,
        };

        private static int PrimaryTarget(AchievementConfig? config)
        {
            if (config == null || config.Conditions.Count == 0) return 1;
            return Math.Max(1, config.Conditions.OrderBy(item => item.Seq).First().Target);
        }

        private static long ToUnixMs(DateTime? value)
        {
            if (value == null) return 0;
            DateTime utc = DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
            return new DateTimeOffset(utc).ToUnixTimeMilliseconds();
        }
    }
}
