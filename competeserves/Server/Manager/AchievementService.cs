using Common.Enum;
using Common.Network;

using CompeteServer.Database;
using CompeteServer.Network;

namespace CompeteServer.Manager
{
    /// <summary>
    /// 成就服务（事件入口）
    /// 定位：玩法模块只上报"发生了什么"（统计/物品/好友/账号变化），
    /// 由本服务统一写统计、重算受影响的成就、解锁并推送。
    /// 不在这里做网络请求处理；3401/3403/3407 由 AchievementHandler 负责。
    /// </summary>
    public static class AchievementService
    {
        /// <summary>
        /// 登录时初始化：补齐应激活的成就行，并按现有历史统计静默重算一遍。
        /// 不推送解锁（避免登录瞬间刷一屏旧成就）；客户端拉列表即可看到解锁状态。
        /// </summary>
        /// <param name="playerId">玩家ID</param>
        public static void OnPlayerLogin(int playerId)
        {
            if (playerId <= 0) return;
            try
            {
                var repository = new AchievementRepository();
                repository.EnsurePlayerRows(playerId);
                repository.EvaluateAll(playerId);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"成就登录初始化失败 [{exception.GetType().Name}]: {exception.Message}");
            }
        }

        /// <summary>
        /// 统计值变化：写生涯表 + 重算该统计相关的成就 + 推送解锁。
        /// </summary>
        /// <param name="playerId">玩家ID</param>
        /// <param name="statId">统计ID（CareerRepository.Score*）</param>
        /// <param name="delta">增量；max=true 时表示本次观测值</param>
        /// <param name="max">true=只保留历史最大（如最高分）</param>
        public static void ReportStat(int playerId, int statId, int delta, bool max = false)
        {
            if (playerId <= 0 || statId <= 0) return;
            try
            {
                CareerRepository career = new();
                if (max) career.SetMax(playerId, statId, delta);
                else career.Add(playerId, statId, delta);

                OnCareerStatChanged(playerId, statId);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"成就统计上报失败 [{exception.GetType().Name}]: {exception.Message}");
            }
        }

        /// <summary>
        /// 生涯统计已由别处写入后调用（QuestHandler 的累计入口 / QuestRepository.Submit）：
        /// 只重算受影响的成就，不重复写统计。
        /// </summary>
        /// <param name="playerId">玩家ID</param>
        /// <param name="statId">发生变化的统计ID</param>
        public static void OnCareerStatChanged(int playerId, int statId)
        {
            if (playerId <= 0 || statId <= 0) return;
            try
            {
                var repository = new AchievementRepository();
                List<int> candidates = repository.GetStatCandidates(statId);
                if (candidates.Count == 0) return;
                List<AchievementUnlock> unlocks = repository.Evaluate(playerId, candidates);
                PushUnlocks(playerId, unlocks);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"成就统计重算失败 [{exception.GetType().Name}]: {exception.Message}");
            }
        }

        /// <summary>
        /// 某物品持有量变化（获得/使用/丢弃）：重算"持有物品"类成就。
        /// 累计获得数由 RaiseCareerDelta(ScoreItemGain) 走统计路径。
        /// </summary>
        /// <param name="playerId">玩家ID</param>
        /// <param name="itemId">物品ID</param>
        public static void OnItemChanged(int playerId, int itemId)
        {
            if (playerId <= 0 || itemId <= 0) return;
            try
            {
                var repository = new AchievementRepository();
                List<int> candidates = repository.GetItemCandidates(itemId);
                if (candidates.Count == 0) return;
                List<AchievementUnlock> unlocks = repository.Evaluate(playerId, candidates);
                PushUnlocks(playerId, unlocks);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"成就物品重算失败 [{exception.GetType().Name}]: {exception.Message}");
            }
        }

        /// <summary>好友关系变化：重算"好友数量"类成就。</summary>
        /// <param name="playerId">玩家ID</param>
        public static void OnFriendChanged(int playerId)
        {
            if (playerId <= 0) return;
            try
            {
                var repository = new AchievementRepository();
                List<int> candidates = repository.GetFriendCandidates();
                if (candidates.Count == 0) return;
                List<AchievementUnlock> unlocks = repository.Evaluate(playerId, candidates);
                PushUnlocks(playerId, unlocks);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"成就好友重算失败 [{exception.GetType().Name}]: {exception.Message}");
            }
        }

        /// <summary>账号年龄/注册日期相关：重算账号类成就（登录/拉列表时调用）。</summary>
        /// <param name="playerId">玩家ID</param>
        public static void OnAccountMaybeChanged(int playerId)
        {
            if (playerId <= 0) return;
            try
            {
                var repository = new AchievementRepository();
                List<int> candidates = repository.GetAccountCandidates();
                if (candidates.Count == 0) return;
                List<AchievementUnlock> unlocks = repository.Evaluate(playerId, candidates);
                PushUnlocks(playerId, unlocks);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"成就账号重算失败 [{exception.GetType().Name}]: {exception.Message}");
            }
        }

        /// <summary>唤取结果：累计抽数 + 五星数（一次唤取可能同时推进两个统计）。</summary>
        /// <param name="playerId">玩家ID</param>
        /// <param name="drawCount">本次抽数（单抽 1 / 十连 10）</param>
        /// <param name="fiveStarCount">本次五星数量</param>
        public static void ReportGacha(int playerId, int drawCount, int fiveStarCount)
        {
            if (playerId <= 0) return;
            if (drawCount > 0) ReportStat(playerId, CareerRepository.ScoreGachaDraw, drawCount);
            if (fiveStarCount > 0) ReportStat(playerId, CareerRepository.ScoreGachaFiveStar, fiveStarCount);
        }

        /// <summary>把本次解锁的成就推给在线玩家（离线只落库，下次拉列表可见）。</summary>
        /// <param name="playerId">玩家ID</param>
        /// <param name="unlocks">本次解锁列表</param>
        public static void PushUnlocks(int playerId, List<AchievementUnlock> unlocks)
        {
            if (playerId <= 0 || unlocks == null || unlocks.Count == 0) return;

            ClientSession? session = SessionManager.Instance.GetSessionByPlayerId(playerId);
            if (session == null) return;

            var notify = new AchievementUnlockedNotify();
            foreach (AchievementUnlock unlock in unlocks)
            {
                notify.Items.Add(new AchievementUnlockedInfo
                {
                    AchievementId = unlock.AchievementId,
                    Name = unlock.Name,
                    Points = unlock.Points,
                    Rarity = unlock.Rarity,
                });
            }

            AchievementSnapshot summary = new AchievementRepository().BuildSummary(playerId);
            notify.Summary = new AchievementSummary
            {
                Total = summary.Total,
                Unlocked = summary.Unlocked,
                Claimed = summary.Claimed,
                Claimable = summary.Claimable,
                Points = summary.Points,
                TotalPoints = summary.TotalPoints,
            };

            _ = session.Send(MessageID.AchievementUnlockedNotify, notify);
        }
    }
}
