using MySqlConnector;

namespace CompeteServer.Database
{
    /// <summary>
    /// 成就静态定义（achievement_config + condition + reward 的合并视图）
    /// 与任务系统解耦：成就只负责长期收集/里程碑，不接取、不交付物品。
    /// </summary>
    public sealed class AchievementConfig
    {
        /// <summary>成就ID（全服唯一；旧任务成就沿用 4001 等原ID）</summary>
        public int AchievementId { get; init; }

        /// <summary>成就名</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>描述/达成提示</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>系列（战斗/成长/社交/活动/隐藏）</summary>
        public string Series { get; init; } = string.Empty;

        /// <summary>稀有度：1铜 2银 3金 4白金</summary>
        public int Rarity { get; init; }

        /// <summary>成就点（仅展示）</summary>
        public int Points { get; init; }

        /// <summary>是否隐藏：未解锁前列表显示 ???</summary>
        public bool Hidden { get; init; }

        /// <summary>阶段链上一阶段ID（0=无前置；上一阶段领取后才激活本阶段）</summary>
        public int PrevAchievementId { get; init; }

        /// <summary>同系列内排序权重（越小越靠前）</summary>
        public int Sort { get; init; }

        /// <summary>是否启用</summary>
        public bool Enabled { get; init; } = true;

        /// <summary>条件列表（默认 AND）</summary>
        public List<AchievementCondition> Conditions { get; init; } = new();

        /// <summary>奖励列表（物品ID / 数量）</summary>
        public List<(int ItemId, int Count)> Rewards { get; init; } = new();
    }

    /// <summary>成就条件</summary>
    public sealed class AchievementCondition
    {
        /// <summary>条件序号（从1开始）</summary>
        public int Seq { get; init; }

        /// <summary>条件类型（见 AchievementRepository.Condition*）</summary>
        public int ConditionType { get; init; }

        /// <summary>参数1：统计ID / 物品ID / yyyyMMdd</summary>
        public int Param1 { get; init; }

        /// <summary>参数2：预留</summary>
        public int Param2 { get; init; }

        /// <summary>目标值</summary>
        public int Target { get; init; }

        /// <summary>展示文案</summary>
        public string Text { get; init; } = string.Empty;
    }

    /// <summary>玩家成就行（player_achievements）</summary>
    public sealed class PlayerAchievementRow
    {
        /// <summary>成就ID</summary>
        public int AchievementId { get; set; }

        /// <summary>状态：1进行中 2已解锁待领取 3已领取</summary>
        public int State { get; set; }

        /// <summary>主条件当前值（列表显示）</summary>
        public int Progress { get; set; }

        /// <summary>解锁时间</summary>
        public DateTime? UnlockedAt { get; set; }

        /// <summary>领取时间</summary>
        public DateTime? ClaimedAt { get; set; }
    }

    /// <summary>条件视图（带玩家当前值，供协议映射）</summary>
    public sealed record AchievementConditionView(
        int Seq, int ConditionType, int Param1, int Param2, int Target, int Current, string Text);

    /// <summary>一条解锁结果（供推送/日志）</summary>
    public sealed record AchievementUnlock(int AchievementId, string Name, int Points, int Rarity);

    /// <summary>玩家成就快照 + 汇总</summary>
    public sealed record AchievementSnapshot(
        List<PlayerAchievementRow> Rows,
        int Total,
        int Unlocked,
        int Claimed,
        int Claimable,
        int Points,
        int TotalPoints);

    /// <summary>领取结果</summary>
    public sealed record ClaimOutcome(
        bool Success,
        string Message,
        List<int> ClaimedIds,
        List<(int ItemId, int Count)> Granted,
        List<(int ItemId, int Count)> Overflow,
        int GrantedUnits);

    /// <summary>条件计算上下文（一次请求只查一次统计/背包/好友/账号）</summary>
    public sealed class AchievementConditionContext
    {
        /// <summary>生涯统计：statId → 累计值</summary>
        public Dictionary<int, int> Stats { get; set; } = new();

        /// <summary>背包持有量：itemId → 总数（仅按需加载）</summary>
        public Dictionary<int, int>? ItemTotals { get; set; }

        /// <summary>好友数量（仅按需加载）</summary>
        public int? FriendCount { get; set; }

        /// <summary>账号注册时间（仅按需加载）</summary>
        public DateTime? CreatedAt { get; set; }
    }

    /// <summary>
    /// 成就仓库（服务端权威）
    /// 数据分层：
    ///   achievement_config/condition/reward = 静态定义；
    ///   player_achievements = 玩家进度 + 解锁 + 领取（三态）；
    ///   career_scores（既有表） = 通用玩家统计（累计值/最大值）。
    /// 与任务的边界：成就不接取、不交付物品；旧 category=3 任务成就在启动时迁移到本表。
    /// </summary>
    public class AchievementRepository
    {
        // ──────────────────────────────
        // 常量
        // ──────────────────────────────

        /// <summary>条件类型：统计值 ≥ 目标</summary>
        public const int ConditionStat = 1;

        /// <summary>条件类型：持有物品数 ≥ 目标</summary>
        public const int ConditionItemOwn = 2;

        /// <summary>条件类型：好友数 ≥ 目标</summary>
        public const int ConditionFriendCount = 3;

        /// <summary>条件类型：账号注册天数 ≥ 目标</summary>
        public const int ConditionAccountAgeDays = 4;

        /// <summary>条件类型：在指定日期前注册（param1=yyyyMMdd）</summary>
        public const int ConditionRegisteredBefore = 5;

        /// <summary>状态：进行中</summary>
        public const int StateProcessing = 1;

        /// <summary>状态：已解锁待领取</summary>
        public const int StateUnlocked = 2;

        /// <summary>状态：已领取</summary>
        public const int StateClaimed = 3;

        /// <summary>旧任务成就ID（迁移用）</summary>
        public static readonly int[] LegacyAchievementIds = { 4001, 4002, 4003, 4101, 4201, 4301 };

        // ──────────────────────────────
        // 种子定义
        // ──────────────────────────────

        private sealed record SeedCondition(int Type, int Param1, int Param2, int Target, string Text);

        private sealed record SeedAchievement(
            int Id, string Name, string Description, string Series, int Rarity, int Points,
            bool Hidden, int PrevId, int Sort, SeedCondition[] Conditions, (int ItemId, int Count)[] Rewards);

        /// <summary>
        /// 首期成就种子（28 条，覆盖战斗/成长/社交/活动/隐藏/多条件）。
        /// INSERT IGNORE：已存在的行不覆盖，方便手工改库；改种子后需清掉旧行或手工改库。
        /// </summary>
        private static readonly SeedAchievement[] SeedAchievements =
        {
            // ── 战斗 ──
            new(5001, "首战告捷", "完成首次挑战。", "战斗", 1, 5, false, 0, 10,
                new[] { new SeedCondition(1, CareerRepository.ScoreChallengeCount, 0, 1, "累计完成 1 次挑战") },
                new[] { (1001, 2) }),
            new(4001, "挑战新秀", "生涯累计完成 5 次挑战。", "战斗", 1, 10, false, 0, 20,
                new[] { new SeedCondition(1, CareerRepository.ScoreChallengeCount, 0, 5, "累计完成 5 次挑战") },
                new[] { (1001, 2) }),
            new(4002, "挑战狂人", "生涯累计完成 20 次挑战。", "战斗", 2, 20, false, 4001, 30,
                new[] { new SeedCondition(1, CareerRepository.ScoreChallengeCount, 0, 20, "累计完成 20 次挑战") },
                new[] { (1007, 3) }),
            new(4003, "挑战大师", "生涯累计完成 50 次挑战。", "战斗", 3, 30, false, 4002, 40,
                new[] { new SeedCondition(1, CareerRepository.ScoreChallengeCount, 0, 50, "累计完成 50 次挑战") },
                new[] { (1017, 1) }),
            new(5002, "百步穿杨", "单次挑战成绩达到 80 分。", "战斗", 2, 15, false, 0, 50,
                new[] { new SeedCondition(1, CareerRepository.ScoreBestChallenge, 0, 80, "历史最高挑战分达到 80") },
                new[] { (1007, 2) }),
            new(5003, "神射手", "单次挑战成绩达到 95 分。", "战斗", 3, 25, false, 0, 60,
                new[] { new SeedCondition(1, CareerRepository.ScoreBestChallenge, 0, 95, "历史最高挑战分达到 95") },
                new[] { (1017, 1) }),
            new(5004, "初入副本", "在副本中击杀首个敌人。", "战斗", 1, 5, false, 0, 70,
                new[] { new SeedCondition(1, CareerRepository.ScoreDungeonKill, 0, 1, "副本击杀 1 只敌人") },
                new[] { (1001, 2) }),
            new(5005, "百人斩", "在副本中累计击杀 100 只敌人。", "战斗", 2, 20, false, 0, 80,
                new[] { new SeedCondition(1, CareerRepository.ScoreDungeonKill, 0, 100, "副本击杀 100 只敌人") },
                new[] { (1006, 2) }),
            new(5006, "拾金者", "在副本中累计拾取 50 次掉落。", "战斗", 2, 15, false, 0, 90,
                new[] { new SeedCondition(1, CareerRepository.ScoreDungeonPickup, 0, 50, "副本拾取 50 次") },
                new[] { (1005, 3) }),

            // ── 成长 ──
            new(4201, "小有家资", "生涯累计获得 30 个物品。", "成长", 1, 10, false, 0, 10,
                new[] { new SeedCondition(1, CareerRepository.ScoreItemGain, 0, 30, "累计获得 30 个物品") },
                new[] { (1006, 2) }),
            new(5101, "富甲一方", "生涯累计获得 300 个物品。", "成长", 3, 25, false, 0, 20,
                new[] { new SeedCondition(1, CareerRepository.ScoreItemGain, 0, 300, "累计获得 300 个物品") },
                new[] { (1006, 5) }),
            new(4301, "任务大师", "生涯累计提交 3 个任务。", "成长", 1, 10, false, 0, 30,
                new[] { new SeedCondition(1, CareerRepository.ScoreQuestSubmit, 0, 3, "累计提交 3 个任务") },
                new[] { (1008, 2) }),
            new(5102, "任务狂人", "生涯累计提交 20 个任务。", "成长", 2, 25, false, 0, 40,
                new[] { new SeedCondition(1, CareerRepository.ScoreQuestSubmit, 0, 20, "累计提交 20 个任务") },
                new[] { (1008, 5) }),
            new(5103, "初露锋芒", "账号注册满 7 天。", "成长", 1, 10, false, 0, 50,
                new[] { new SeedCondition(4, 0, 0, 7, "账号注册满 7 天") },
                new[] { (1001, 3) }),
            new(5104, "老玩家", "账号注册满 30 天。", "成长", 2, 20, false, 0, 60,
                new[] { new SeedCondition(4, 0, 0, 30, "账号注册满 30 天") },
                new[] { (1007, 2) }),

            // ── 社交 ──
            new(5201, "交个朋友", "拥有 1 位好友。", "社交", 1, 5, false, 0, 10,
                new[] { new SeedCondition(3, 0, 0, 1, "拥有 1 位好友") },
                new[] { (1005, 2) }),
            new(5202, "好友成群", "拥有 10 位好友。", "社交", 2, 20, false, 0, 20,
                new[] { new SeedCondition(3, 0, 0, 10, "拥有 10 位好友") },
                new[] { (1005, 5) }),
            new(5203, "畅聊几句", "累计发送 5 条消息。", "社交", 1, 5, false, 0, 30,
                new[] { new SeedCondition(1, CareerRepository.ScoreMessageCount, 0, 5, "累计发送 5 条消息") },
                new[] { (1001, 2) }),
            new(4101, "社交达人", "生涯累计发送 10 条消息。", "社交", 1, 10, false, 0, 40,
                new[] { new SeedCondition(1, CareerRepository.ScoreMessageCount, 0, 10, "累计发送 10 条消息") },
                new[] { (1005, 2) }),
            new(5204, "话痨", "累计发送 500 条消息。", "社交", 2, 25, false, 0, 50,
                new[] { new SeedCondition(1, CareerRepository.ScoreMessageCount, 0, 500, "累计发送 500 条消息") },
                new[] { (1008, 2) }),

            // ── 活动 ──
            new(5301, "初次唤取", "完成首次唤取。", "活动", 1, 5, false, 0, 10,
                new[] { new SeedCondition(1, CareerRepository.ScoreGachaDraw, 0, 1, "累计唤取 1 次") },
                new[] { (1001, 2) }),
            new(5302, "十连达人", "累计唤取 10 次。", "活动", 2, 15, false, 0, 20,
                new[] { new SeedCondition(1, CareerRepository.ScoreGachaDraw, 0, 10, "累计唤取 10 次") },
                new[] { (1007, 2) }),
            new(5303, "欧皇初现", "首次获得 5★ 物品。", "活动", 2, 10, false, 0, 30,
                new[] { new SeedCondition(1, CareerRepository.ScoreGachaFiveStar, 0, 1, "获得 1 次 5★") },
                new[] { (1006, 2) }),
            new(5304, "天选之人", "累计获得 5 次 5★ 物品。", "活动", 4, 30, false, 0, 40,
                new[] { new SeedCondition(1, CareerRepository.ScoreGachaFiveStar, 0, 5, "获得 5 次 5★") },
                new[] { (1017, 1) }),
            new(5305, "签到七日", "累计签到 7 天。", "活动", 1, 10, false, 0, 50,
                new[] { new SeedCondition(1, CareerRepository.ScoreSignIn, 0, 7, "累计签到 7 天") },
                new[] { (1001, 5) }),
            new(5306, "全勤达人", "累计签到 30 天。", "活动", 3, 25, false, 0, 60,
                new[] { new SeedCondition(1, CareerRepository.ScoreSignIn, 0, 30, "累计签到 30 天") },
                new[] { (1007, 3) }),

            // ── 隐藏 ──
            new(5401, "？？？", "单次挑战获得 0 分。", "隐藏", 3, 5, true, 0, 10,
                new[] { new SeedCondition(1, CareerRepository.ScoreZeroChallenge, 0, 1, "单次挑战获得 0 分") },
                new[] { (1001, 1) }),

            // ── 多条件 ──
            new(5501, "全能冒险家", "挑战、聊天、社交样样不落。", "成长", 4, 40, false, 0, 70,
                new[]
                {
                    new SeedCondition(1, CareerRepository.ScoreChallengeCount, 0, 10, "累计完成 10 次挑战"),
                    new SeedCondition(1, CareerRepository.ScoreMessageCount, 0, 20, "累计发送 20 条消息"),
                    new SeedCondition(3, 0, 0, 3, "拥有 3 位好友"),
                },
                new[] { (1017, 1) }),
        };

        // ──────────────────────────────
        // 初始化
        // ──────────────────────────────

        /// <summary>幂等建表：与 Migrations\017_AddAchievements.sql 等价。</summary>
        public void EnsureTable()
        {
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();

            using (MySqlCommand command = new(
                """
                CREATE TABLE IF NOT EXISTS `achievement_config` (
                  `achievement_id`      INT          NOT NULL,
                  `name`                VARCHAR(64)  NOT NULL,
                  `description`         VARCHAR(255) NOT NULL DEFAULT '',
                  `series`              VARCHAR(32)  NOT NULL DEFAULT '成长',
                  `rarity`              INT          NOT NULL DEFAULT 1,
                  `points`              INT          NOT NULL DEFAULT 0,
                  `hidden`              TINYINT      NOT NULL DEFAULT 0,
                  `prev_achievement_id` INT          NOT NULL DEFAULT 0,
                  `sort`                INT          NOT NULL DEFAULT 0,
                  `enabled`             TINYINT      NOT NULL DEFAULT 1,
                  PRIMARY KEY (`achievement_id`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
                """,
                connection))
            {
                command.ExecuteNonQuery();
            }

            using (MySqlCommand command = new(
                """
                CREATE TABLE IF NOT EXISTS `achievement_condition` (
                  `achievement_id` INT          NOT NULL,
                  `seq`            INT          NOT NULL,
                  `condition_type` INT          NOT NULL,
                  `param1`         INT          NOT NULL DEFAULT 0,
                  `param2`         INT          NOT NULL DEFAULT 0,
                  `target`         INT          NOT NULL DEFAULT 1,
                  `text`           VARCHAR(128) NOT NULL DEFAULT '',
                  PRIMARY KEY (`achievement_id`, `seq`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
                """,
                connection))
            {
                command.ExecuteNonQuery();
            }

            using (MySqlCommand command = new(
                """
                CREATE TABLE IF NOT EXISTS `achievement_reward` (
                  `achievement_id` INT NOT NULL,
                  `item_id`        INT NOT NULL,
                  `count`          INT NOT NULL,
                  PRIMARY KEY (`achievement_id`, `item_id`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
                """,
                connection))
            {
                command.ExecuteNonQuery();
            }

            using (MySqlCommand command = new(
                """
                CREATE TABLE IF NOT EXISTS `player_achievements` (
                  `player_id`      INT      NOT NULL,
                  `achievement_id` INT      NOT NULL,
                  `state`          TINYINT  NOT NULL DEFAULT 1,
                  `progress`       INT      NOT NULL DEFAULT 0,
                  `unlocked_at`    DATETIME NULL,
                  `claimed_at`     DATETIME NULL,
                  PRIMARY KEY (`player_id`, `achievement_id`),
                  KEY `idx_player_state` (`player_id`, `state`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
                """,
                connection))
            {
                command.ExecuteNonQuery();
            }

            InvalidateConfigCache();
        }

        /// <summary>幂等种子：写入成就定义、条件与奖励（INSERT IGNORE，已存在不覆盖）。</summary>
        public void SeedConfigs()
        {
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlTransaction transaction = connection.BeginTransaction();

            foreach (SeedAchievement achievement in SeedAchievements)
            {
                using (MySqlCommand command = new(
                    """
                    INSERT IGNORE INTO `achievement_config`
                      (`achievement_id`, `name`, `description`, `series`, `rarity`, `points`,
                       `hidden`, `prev_achievement_id`, `sort`, `enabled`)
                    VALUES (@Id, @Name, @Description, @Series, @Rarity, @Points,
                            @Hidden, @PrevId, @Sort, 1)
                    """,
                    connection, transaction))
                {
                    command.Parameters.AddWithValue("@Id", achievement.Id);
                    command.Parameters.AddWithValue("@Name", achievement.Name);
                    command.Parameters.AddWithValue("@Description", achievement.Description);
                    command.Parameters.AddWithValue("@Series", achievement.Series);
                    command.Parameters.AddWithValue("@Rarity", achievement.Rarity);
                    command.Parameters.AddWithValue("@Points", achievement.Points);
                    command.Parameters.AddWithValue("@Hidden", achievement.Hidden ? 1 : 0);
                    command.Parameters.AddWithValue("@PrevId", achievement.PrevId);
                    command.Parameters.AddWithValue("@Sort", achievement.Sort);
                    command.ExecuteNonQuery();
                }

                for (int index = 0; index < achievement.Conditions.Length; index++)
                {
                    SeedCondition condition = achievement.Conditions[index];
                    using MySqlCommand command = new(
                        """
                        INSERT IGNORE INTO `achievement_condition`
                          (`achievement_id`, `seq`, `condition_type`, `param1`, `param2`, `target`, `text`)
                        VALUES (@Id, @Seq, @Type, @Param1, @Param2, @Target, @Text)
                        """,
                        connection, transaction);
                    command.Parameters.AddWithValue("@Id", achievement.Id);
                    command.Parameters.AddWithValue("@Seq", index + 1);
                    command.Parameters.AddWithValue("@Type", condition.Type);
                    command.Parameters.AddWithValue("@Param1", condition.Param1);
                    command.Parameters.AddWithValue("@Param2", condition.Param2);
                    command.Parameters.AddWithValue("@Target", Math.Max(1, condition.Target));
                    command.Parameters.AddWithValue("@Text", condition.Text);
                    command.ExecuteNonQuery();
                }

                foreach ((int itemId, int count) in achievement.Rewards)
                {
                    using MySqlCommand command = new(
                        """
                        INSERT IGNORE INTO `achievement_reward` (`achievement_id`, `item_id`, `count`)
                        VALUES (@Id, @ItemId, @Count)
                        """,
                        connection, transaction);
                    command.Parameters.AddWithValue("@Id", achievement.Id);
                    command.Parameters.AddWithValue("@ItemId", itemId);
                    command.Parameters.AddWithValue("@Count", count);
                    command.ExecuteNonQuery();
                }
            }

            transaction.Commit();
            InvalidateConfigCache();
        }

        /// <summary>
        /// 旧任务成就迁移（category=3）：状态复制到 player_achievements，然后删除旧任务行。
        /// 幂等：迁移后 quest_config 里不再有这些 ID，重复执行不会重复复制。
        /// </summary>
        public void MigrateLegacyAchievements()
        {
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();

                int copied;
                using (MySqlCommand command = new(
                    """
                    INSERT INTO `player_achievements`
                      (`player_id`, `achievement_id`, `state`, `progress`, `unlocked_at`, `claimed_at`)
                    SELECT pq.`player_id`, pq.`quest_id`,
                           CASE pq.`state` WHEN 3 THEN 3 WHEN 2 THEN 2 ELSE 1 END,
                           pq.`progress`, pq.`finished_at`, pq.`submitted_at`
                    FROM `player_quests` pq
                    JOIN `quest_config` qc ON qc.`quest_id` = pq.`quest_id` AND qc.`category` = 3
                    ON DUPLICATE KEY UPDATE
                      `state`       = GREATEST(`player_achievements`.`state`, VALUES(`state`)),
                      `progress`    = GREATEST(`player_achievements`.`progress`, VALUES(`progress`)),
                      `unlocked_at` = COALESCE(`player_achievements`.`unlocked_at`, VALUES(`unlocked_at`)),
                      `claimed_at`  = COALESCE(`player_achievements`.`claimed_at`, VALUES(`claimed_at`))
                    """,
                    connection))
                {
                    copied = command.ExecuteNonQuery();
                }

                int rewardsDeleted;
                using (MySqlCommand command = new(
                    "DELETE FROM `quest_reward` WHERE `quest_id` IN (4001, 4002, 4003, 4101, 4201, 4301)",
                    connection))
                {
                    rewardsDeleted = command.ExecuteNonQuery();
                }

                int rowsDeleted;
                using (MySqlCommand command = new(
                    "DELETE FROM `player_quests` WHERE `quest_id` IN (4001, 4002, 4003, 4101, 4201, 4301)",
                    connection))
                {
                    rowsDeleted = command.ExecuteNonQuery();
                }

                int configsDeleted;
                using (MySqlCommand command = new(
                    "DELETE FROM `quest_config` WHERE `quest_id` IN (4001, 4002, 4003, 4101, 4201, 4301)",
                    connection))
                {
                    configsDeleted = command.ExecuteNonQuery();
                }

                if (copied > 0 || rewardsDeleted > 0 || rowsDeleted > 0 || configsDeleted > 0)
                {
                    Console.WriteLine(
                        $"成就迁移:复制 {copied} 条状态 / 删除旧奖励 {rewardsDeleted} 条 / 旧进度 {rowsDeleted} 条 / 旧配置 {configsDeleted} 条");
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"成就迁移失败 [{exception.GetType().Name}]: {exception.Message}");
            }
        }

        // ──────────────────────────────
        // 静态配置缓存
        // ──────────────────────────────

        private static List<AchievementConfig>? configCache;
        private static Dictionary<int, AchievementConfig>? configMapCache;
        private static IndexData? indexCache;
        private static long configCacheTick;
        private const long ConfigCacheTtlMs = 10_000;

        private sealed class IndexData
        {
            public Dictionary<int, List<int>> ByStat { get; } = new();
            public Dictionary<int, List<int>> ByItem { get; } = new();
            public List<int> Friend { get; } = new();
            public List<int> Account { get; } = new();
        }

        private static readonly List<int> EmptyIds = new();

        /// <summary>读取全部成就配置（10 秒内共享缓存；失败读到的空结果不缓存）。</summary>
        public List<AchievementConfig> GetConfigs()
        {
            List<AchievementConfig>? cache = configCache;
            long tick = Interlocked.Read(ref configCacheTick);
            if (cache != null && tick > 0 && Environment.TickCount64 - tick < ConfigCacheTtlMs)
            {
                return cache;
            }

            List<AchievementConfig> fresh = LoadConfigsFromDb();
            if (fresh.Count > 0)
            {
                configMapCache = fresh.ToDictionary(config => config.AchievementId);
                indexCache = BuildIndex(fresh);
                configCache = fresh;
                Interlocked.Exchange(ref configCacheTick, Environment.TickCount64);
            }
            return fresh;
        }

        /// <summary>配置索引（achievementId → 配置；与 GetConfigs 共享缓存）。</summary>
        public Dictionary<int, AchievementConfig> GetConfigMap()
        {
            GetConfigs();
            return configMapCache ?? new Dictionary<int, AchievementConfig>();
        }

        /// <summary>主动失效配置缓存（建表/种子/手工改库后调用）。</summary>
        public static void InvalidateConfigCache()
        {
            configCache = null;
            configMapCache = null;
            indexCache = null;
            Interlocked.Exchange(ref configCacheTick, 0);
        }

        private List<AchievementConfig> LoadConfigsFromDb()
        {
            var configs = new List<AchievementConfig>();
            var map = new Dictionary<int, AchievementConfig>();

            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();

                using (MySqlCommand command = new(
                    """
                    SELECT `achievement_id`, `name`, `description`, `series`, `rarity`, `points`,
                           `hidden`, `prev_achievement_id`, `sort`, `enabled`
                    FROM `achievement_config`
                    ORDER BY `achievement_id`
                    """,
                    connection))
                using (MySqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var config = new AchievementConfig
                        {
                            AchievementId = reader.GetInt32(0),
                            Name = reader.GetString(1),
                            Description = reader.GetString(2),
                            Series = reader.GetString(3),
                            Rarity = reader.GetInt32(4),
                            Points = reader.GetInt32(5),
                            Hidden = reader.GetInt32(6) != 0,
                            PrevAchievementId = reader.GetInt32(7),
                            Sort = reader.GetInt32(8),
                            Enabled = reader.GetInt32(9) != 0,
                        };
                        configs.Add(config);
                        map[config.AchievementId] = config;
                    }
                }

                using (MySqlCommand command = new(
                    """
                    SELECT `achievement_id`, `seq`, `condition_type`, `param1`, `param2`, `target`, `text`
                    FROM `achievement_condition`
                    ORDER BY `achievement_id`, `seq`
                    """,
                    connection))
                using (MySqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (!map.TryGetValue(reader.GetInt32(0), out AchievementConfig? config)) continue;
                        config.Conditions.Add(new AchievementCondition
                        {
                            Seq = reader.GetInt32(1),
                            ConditionType = reader.GetInt32(2),
                            Param1 = reader.GetInt32(3),
                            Param2 = reader.GetInt32(4),
                            Target = Math.Max(1, reader.GetInt32(5)),
                            Text = reader.GetString(6),
                        });
                    }
                }

                using (MySqlCommand command = new(
                    """
                    SELECT `achievement_id`, `item_id`, `count`
                    FROM `achievement_reward`
                    ORDER BY `achievement_id`, `item_id`
                    """,
                    connection))
                using (MySqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (!map.TryGetValue(reader.GetInt32(0), out AchievementConfig? config)) continue;
                        config.Rewards.Add((reader.GetInt32(1), reader.GetInt32(2)));
                    }
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"成就配置读取失败 [{exception.GetType().Name}]: {exception.Message}");
            }

            return configs;
        }

        private static IndexData BuildIndex(List<AchievementConfig> configs)
        {
            var index = new IndexData();
            var statSets = new Dictionary<int, HashSet<int>>();
            var itemSets = new Dictionary<int, HashSet<int>>();
            var friendSet = new HashSet<int>();
            var accountSet = new HashSet<int>();

            foreach (AchievementConfig config in configs)
            {
                if (!config.Enabled) continue;
                foreach (AchievementCondition condition in config.Conditions)
                {
                    switch (condition.ConditionType)
                    {
                        case ConditionStat:
                            if (!statSets.TryGetValue(condition.Param1, out HashSet<int>? statIds))
                            {
                                statIds = new HashSet<int>();
                                statSets[condition.Param1] = statIds;
                            }
                            statIds.Add(config.AchievementId);
                            break;
                        case ConditionItemOwn:
                            if (!itemSets.TryGetValue(condition.Param1, out HashSet<int>? itemIds))
                            {
                                itemIds = new HashSet<int>();
                                itemSets[condition.Param1] = itemIds;
                            }
                            itemIds.Add(config.AchievementId);
                            break;
                        case ConditionFriendCount:
                            friendSet.Add(config.AchievementId);
                            break;
                        case ConditionAccountAgeDays:
                        case ConditionRegisteredBefore:
                            accountSet.Add(config.AchievementId);
                            break;
                    }
                }
            }

            foreach ((int statId, HashSet<int> ids) in statSets) index.ByStat[statId] = ids.ToList();
            foreach ((int itemId, HashSet<int> ids) in itemSets) index.ByItem[itemId] = ids.ToList();
            index.Friend.AddRange(friendSet);
            index.Account.AddRange(accountSet);
            return index;
        }

        /// <summary>某统计变化时受影响的成就ID。</summary>
        public List<int> GetStatCandidates(int statId)
        {
            GetConfigs();
            return indexCache != null && indexCache.ByStat.TryGetValue(statId, out List<int>? ids) ? ids : EmptyIds;
        }

        /// <summary>某物品持有量变化时受影响的成就ID。</summary>
        public List<int> GetItemCandidates(int itemId)
        {
            GetConfigs();
            return indexCache != null && indexCache.ByItem.TryGetValue(itemId, out List<int>? ids) ? ids : EmptyIds;
        }

        /// <summary>好友数量变化时受影响的成就ID。</summary>
        public List<int> GetFriendCandidates()
        {
            GetConfigs();
            return indexCache?.Friend ?? EmptyIds;
        }

        /// <summary>账号年龄/注册日期变化时受影响的成就ID。</summary>
        public List<int> GetAccountCandidates()
        {
            GetConfigs();
            return indexCache?.Account ?? EmptyIds;
        }

        // ──────────────────────────────
        // 玩家进度
        // ──────────────────────────────

        /// <summary>读取玩家全部成就行（无行 = 未激活）。</summary>
        public Dictionary<int, PlayerAchievementRow> GetPlayerRows(int playerId)
        {
            var rows = new Dictionary<int, PlayerAchievementRow>();
            if (playerId <= 0) return rows;

            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlCommand command = new(
                    """
                    SELECT `achievement_id`, `state`, `progress`, `unlocked_at`, `claimed_at`
                    FROM `player_achievements`
                    WHERE `player_id` = @PlayerId
                    """,
                    connection);
                command.Parameters.AddWithValue("@PlayerId", playerId);
                using MySqlDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows[reader.GetInt32(0)] = new PlayerAchievementRow
                    {
                        AchievementId = reader.GetInt32(0),
                        State = reader.GetInt32(1),
                        Progress = reader.GetInt32(2),
                        UnlockedAt = reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                        ClaimedAt = reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                    };
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"玩家成就读取失败 [{exception.GetType().Name}]: {exception.Message}");
            }
            return rows;
        }

        /// <summary>
        /// 补齐玩家成就行：为"应激活而未激活"的成就插入进行中行（登录/拉列表/领取后调用，幂等）。
        /// 阶段链：只有上一阶段已领取（state=3）才激活下一阶段。
        /// </summary>
        public void EnsurePlayerRows(int playerId)
        {
            if (playerId <= 0) return;

            try
            {
                Dictionary<int, AchievementConfig> configs = GetConfigMap();
                Dictionary<int, PlayerAchievementRow> rows = GetPlayerRows(playerId);

                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlTransaction transaction = connection.BeginTransaction();

                foreach (AchievementConfig config in configs.Values)
                {
                    if (!config.Enabled) continue;
                    if (rows.ContainsKey(config.AchievementId)) continue;
                    if (config.PrevAchievementId > 0 &&
                        !(rows.TryGetValue(config.PrevAchievementId, out PlayerAchievementRow? prev) &&
                          prev.State == StateClaimed))
                    {
                        continue;
                    }

                    using MySqlCommand command = new(
                        """
                        INSERT IGNORE INTO `player_achievements`
                          (`player_id`, `achievement_id`, `state`, `progress`)
                        VALUES (@PlayerId, @AchievementId, 1, 0)
                        """,
                        connection, transaction);
                    command.Parameters.AddWithValue("@PlayerId", playerId);
                    command.Parameters.AddWithValue("@AchievementId", config.AchievementId);
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (Exception exception)
            {
                Console.WriteLine($"成就激活失败 [{exception.GetType().Name}]: {exception.Message}");
            }
        }

        /// <summary>重算指定成就（服务端事件/领取奖励后调用），返回本次解锁的成就。</summary>
        public List<AchievementUnlock> Evaluate(int playerId, IEnumerable<int> achievementIds)
        {
            var unlocks = new List<AchievementUnlock>();
            if (playerId <= 0 || achievementIds == null) return unlocks;

            List<int> ids = achievementIds.Distinct().ToList();
            if (ids.Count == 0) return unlocks;

            Dictionary<int, AchievementConfig> map = GetConfigMap();
            Dictionary<int, PlayerAchievementRow> rows = GetPlayerRows(playerId);
            var candidates = new List<AchievementConfig>();
            foreach (int id in ids)
            {
                if (rows.TryGetValue(id, out PlayerAchievementRow? row) &&
                    row.State == StateProcessing &&
                    map.TryGetValue(id, out AchievementConfig? config))
                {
                    candidates.Add(config);
                }
            }
            if (candidates.Count == 0) return unlocks;

            AchievementConditionContext context = CreateContext(playerId, candidates);
            DateTime now = DateTime.UtcNow;

            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                foreach (AchievementConfig config in candidates)
                {
                    PlayerAchievementRow row = rows[config.AchievementId];
                    bool allMet = config.Conditions.Count > 0;
                    int progress = 0;
                    int target = PrimaryTarget(config);
                    bool firstUnmetSet = false;

                    foreach (AchievementCondition condition in config.Conditions.OrderBy(item => item.Seq))
                    {
                        int current = Current(condition, context);
                        if (current < condition.Target)
                        {
                            allMet = false;
                            if (!firstUnmetSet)
                            {
                                progress = Math.Min(current, condition.Target);
                                target = Math.Max(1, condition.Target);
                                firstUnmetSet = true;
                            }
                        }
                    }

                    if (allMet)
                    {
                        progress = target;
                        using MySqlCommand command = new(
                            """
                            UPDATE `player_achievements`
                            SET `state` = 2,
                                `progress` = @Progress,
                                `unlocked_at` = COALESCE(`unlocked_at`, @Now)
                            WHERE `player_id` = @PlayerId AND `achievement_id` = @AchievementId AND `state` = 1
                            """,
                            connection);
                        command.Parameters.AddWithValue("@Progress", progress);
                        command.Parameters.AddWithValue("@Now", now);
                        command.Parameters.AddWithValue("@PlayerId", playerId);
                        command.Parameters.AddWithValue("@AchievementId", config.AchievementId);
                        if (command.ExecuteNonQuery() > 0)
                        {
                            row.State = StateUnlocked;
                            row.Progress = progress;
                            row.UnlockedAt ??= now;
                            unlocks.Add(new AchievementUnlock(
                                config.AchievementId, config.Name, config.Points, config.Rarity));
                        }
                    }
                    else if (progress != row.Progress)
                    {
                        using MySqlCommand command = new(
                            """
                            UPDATE `player_achievements`
                            SET `progress` = @Progress
                            WHERE `player_id` = @PlayerId AND `achievement_id` = @AchievementId AND `state` = 1
                            """,
                            connection);
                        command.Parameters.AddWithValue("@Progress", progress);
                        command.Parameters.AddWithValue("@PlayerId", playerId);
                        command.Parameters.AddWithValue("@AchievementId", config.AchievementId);
                        command.ExecuteNonQuery();
                        row.Progress = progress;
                    }
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"成就重算失败 [{exception.GetType().Name}]: {exception.Message}");
            }

            return unlocks;
        }

        /// <summary>重算全部进行中的成就（登录/拉列表时调用，静默不推送）。</summary>
        public List<AchievementUnlock> EvaluateAll(int playerId)
        {
            Dictionary<int, PlayerAchievementRow> rows = GetPlayerRows(playerId);
            List<int> ids = rows.Values
                .Where(row => row.State == StateProcessing)
                .Select(row => row.AchievementId)
                .ToList();
            return ids.Count == 0 ? new List<AchievementUnlock>() : Evaluate(playerId, ids);
        }

        // ──────────────────────────────
        // 条件计算
        // ──────────────────────────────

        /// <summary>构建条件上下文：统计必查；背包/好友/账号按条件需要加载。</summary>
        public AchievementConditionContext CreateContext(int playerId, IEnumerable<AchievementConfig> configs)
        {
            var context = new AchievementConditionContext
            {
                Stats = new CareerRepository().GetValues(playerId),
            };

            bool needItems = false;
            bool needFriends = false;
            bool needAccount = false;
            foreach (AchievementConfig config in configs)
            {
                foreach (AchievementCondition condition in config.Conditions)
                {
                    switch (condition.ConditionType)
                    {
                        case ConditionItemOwn: needItems = true; break;
                        case ConditionFriendCount: needFriends = true; break;
                        case ConditionAccountAgeDays:
                        case ConditionRegisteredBefore: needAccount = true; break;
                    }
                }
            }

            if (needItems) context.ItemTotals = LoadItemTotals(playerId);
            if (needFriends) context.FriendCount = LoadFriendCount(playerId);
            if (needAccount) context.CreatedAt = LoadCreatedAt(playerId);
            return context;
        }

        /// <summary>条件当前值。</summary>
        public int Current(AchievementCondition condition, AchievementConditionContext context)
        {
            switch (condition.ConditionType)
            {
                case ConditionStat:
                    return context.Stats.TryGetValue(condition.Param1, out int stat) ? stat : 0;
                case ConditionItemOwn:
                    return context.ItemTotals != null &&
                           context.ItemTotals.TryGetValue(condition.Param1, out int itemCount)
                        ? itemCount
                        : 0;
                case ConditionFriendCount:
                    return context.FriendCount ?? 0;
                case ConditionAccountAgeDays:
                    if (context.CreatedAt == null) return 0;
                    return Math.Max(0, (int)(DateTime.Now - context.CreatedAt.Value).TotalDays);
                case ConditionRegisteredBefore:
                    if (context.CreatedAt == null) return 0;
                    if (!TryParseDate(condition.Param1, out DateTime before)) return 0;
                    return context.CreatedAt.Value.Date <= before.Date ? 1 : 0;
                default:
                    return 0;
            }
        }

        /// <summary>构建某成就的条件视图（带玩家当前值）。</summary>
        public List<AchievementConditionView> BuildConditionViews(
            AchievementConfig config, AchievementConditionContext context)
        {
            var views = new List<AchievementConditionView>(config.Conditions.Count);
            foreach (AchievementCondition condition in config.Conditions.OrderBy(item => item.Seq))
            {
                views.Add(new AchievementConditionView(
                    condition.Seq,
                    condition.ConditionType,
                    condition.Param1,
                    condition.Param2,
                    condition.Target,
                    Current(condition, context),
                    condition.Text));
            }
            return views;
        }

        private static int PrimaryTarget(AchievementConfig config)
        {
            if (config.Conditions.Count == 0) return 1;
            AchievementCondition first = config.Conditions.OrderBy(item => item.Seq).First();
            return Math.Max(1, first.Target);
        }

        private static bool TryParseDate(int yyyyMMdd, out DateTime date)
        {
            date = default;
            if (yyyyMMdd <= 0) return false;
            string text = yyyyMMdd.ToString("D8");
            return DateTime.TryParseExact(text, "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out date);
        }

        private Dictionary<int, int> LoadItemTotals(int playerId)
        {
            var totals = new Dictionary<int, int>();
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlCommand command = new(
                    """
                    SELECT `item_id`, SUM(`count`) FROM `player_items`
                    WHERE `player_id` = @PlayerId GROUP BY `item_id`
                    """,
                    connection);
                command.Parameters.AddWithValue("@PlayerId", playerId);
                using MySqlDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    totals[reader.GetInt32(0)] = reader.GetInt32(1);
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"成就物品统计失败 [{exception.GetType().Name}]: {exception.Message}");
            }
            return totals;
        }

        private int LoadFriendCount(int playerId)
        {
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlCommand command = new(
                    "SELECT COUNT(*) FROM `friendships` WHERE `OwnerId` = @PlayerId AND `Status` = 1",
                    connection);
                command.Parameters.AddWithValue("@PlayerId", playerId);
                return Convert.ToInt32(command.ExecuteScalar() ?? 0);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"成就好友统计失败 [{exception.GetType().Name}]: {exception.Message}");
                return 0;
            }
        }

        private DateTime? LoadCreatedAt(int playerId)
        {
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlCommand command = new(
                    "SELECT `CreatedAt` FROM `accounts` WHERE `Id` = @PlayerId",
                    connection);
                command.Parameters.AddWithValue("@PlayerId", playerId);
                object? value = command.ExecuteScalar();
                return value == null || value == DBNull.Value ? null : Convert.ToDateTime(value);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"成就账号年龄读取失败 [{exception.GetType().Name}]: {exception.Message}");
                return null;
            }
        }

        // ──────────────────────────────
        // 快照 / 汇总
        // ──────────────────────────────

        /// <summary>构建玩家成就快照与汇总（会先补齐应激活行）。</summary>
        public AchievementSnapshot BuildSnapshot(int playerId)
        {
            EnsurePlayerRows(playerId);
            Dictionary<int, AchievementConfig> configs = GetConfigMap();
            Dictionary<int, PlayerAchievementRow> rows = GetPlayerRows(playerId);

            var list = new List<PlayerAchievementRow>();
            int total = 0;
            int unlocked = 0;
            int claimed = 0;
            int claimable = 0;
            int points = 0;
            int totalPoints = 0;

            foreach (AchievementConfig config in configs.Values.OrderBy(item => item.AchievementId))
            {
                if (!config.Enabled) continue;
                total++;
                totalPoints += config.Points;
                if (!rows.TryGetValue(config.AchievementId, out PlayerAchievementRow? row)) continue;
                list.Add(row);
                if (row.State >= StateUnlocked)
                {
                    unlocked++;
                    points += config.Points;
                }
                if (row.State == StateClaimed) claimed++;
                if (row.State == StateUnlocked) claimable++;
            }

            return new AchievementSnapshot(list, total, unlocked, claimed, claimable, points, totalPoints);
        }

        /// <summary>只取汇总（红点/推送用）。</summary>
        public AchievementSnapshot BuildSummary(int playerId)
        {
            return BuildSnapshot(playerId);
        }

        // ──────────────────────────────
        // 领取（服务端权威 + 事务幂等）
        // ──────────────────────────────

        /// <summary>
        /// 领取成就奖励。事务内行锁 + state=2 校验 + 受影响行数守卫，重复领取不会重复发奖；
        /// 背包放不下的奖励转存邮箱（与任务奖励同一套规则）。
        /// </summary>
        public ClaimOutcome Claim(int playerId, IEnumerable<int> achievementIds)
        {
            var emptyGranted = new List<(int ItemId, int Count)>();
            var emptyOverflow = new List<(int ItemId, int Count)>();
            if (playerId <= 0)
            {
                return new ClaimOutcome(false, "请先登录", new List<int>(), emptyGranted, emptyOverflow, 0);
            }
            if (achievementIds == null)
            {
                return new ClaimOutcome(false, "未指定成就", new List<int>(), emptyGranted, emptyOverflow, 0);
            }

            Dictionary<int, AchievementConfig> configs = GetConfigMap();
            List<int> wanted = achievementIds.Where(id => id > 0).Distinct().ToList();
            if (wanted.Count == 0)
            {
                return new ClaimOutcome(false, "未指定成就", new List<int>(), emptyGranted, emptyOverflow, 0);
            }
            var claimedIds = new List<int>();
            var granted = new List<(int ItemId, int Count)>();
            var overflow = new List<(int ItemId, int Count)>();
            int grantedUnits = 0;
            string failMessage = string.Empty;

            using IDisposable writeLock = PlayerWriteLock.Acquire(playerId);

            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlTransaction transaction = connection.BeginTransaction();
                BagRepository.LockPlayerRow(connection, transaction, playerId);
                BagSlot[] slots =
                    BagRepository.LoadSlotsForUpdate(connection, transaction, playerId);
                Dictionary<int, int> maxStacks = LoadMaxStacks();

                foreach (int achievementId in wanted)
                {
                    if (!configs.TryGetValue(achievementId, out AchievementConfig? config))
                    {
                        failMessage = "成就不存在";
                        continue;
                    }

                    int state;
                    using (MySqlCommand select = new(
                        """
                        SELECT `state` FROM `player_achievements`
                        WHERE `player_id` = @PlayerId AND `achievement_id` = @AchievementId
                        FOR UPDATE
                        """,
                        connection, transaction))
                    {
                        select.Parameters.AddWithValue("@PlayerId", playerId);
                        select.Parameters.AddWithValue("@AchievementId", achievementId);
                        using MySqlDataReader reader = select.ExecuteReader();
                        if (!reader.Read())
                        {
                            failMessage = "成就未激活";
                            continue;
                        }
                        state = reader.GetInt32(0);
                    }

                    if (state == StateClaimed)
                    {
                        failMessage = "该成就奖励已领取";
                        continue;
                    }
                    if (state != StateUnlocked)
                    {
                        failMessage = "该成就尚未解锁";
                        continue;
                    }

                    foreach ((int itemId, int count) in config.Rewards)
                    {
                        int maxStack = maxStacks.TryGetValue(itemId, out int stack) ? Math.Max(1, stack) : 1;
                        int remaining = BagRepository.GrantIntoSlots(
                            connection, transaction, playerId, ref slots, itemId, count, maxStack);
                        int delivered = count - remaining;
                        if (delivered > 0)
                        {
                            granted.Add((itemId, delivered));
                            grantedUnits += delivered;
                        }
                        if (remaining > 0) overflow.Add((itemId, remaining));
                    }

                    using (MySqlCommand update = new(
                        """
                        UPDATE `player_achievements`
                        SET `state` = 3, `claimed_at` = @Now, `progress` = @Progress
                        WHERE `player_id` = @PlayerId AND `achievement_id` = @AchievementId AND `state` = 2
                        """,
                        connection, transaction))
                    {
                        update.Parameters.AddWithValue("@Now", DateTime.UtcNow);
                        update.Parameters.AddWithValue("@Progress", PrimaryTarget(config));
                        update.Parameters.AddWithValue("@PlayerId", playerId);
                        update.Parameters.AddWithValue("@AchievementId", achievementId);
                        if (update.ExecuteNonQuery() == 0)
                        {
                            transaction.Rollback();
                            return new ClaimOutcome(false, "领取失败，请重试",
                                new List<int>(), emptyGranted, emptyOverflow, 0);
                        }
                    }

                    EnsureNextChainRow(connection, transaction, playerId, achievementId, configs);
                    claimedIds.Add(achievementId);
                }

                if (claimedIds.Count == 0)
                {
                    transaction.Rollback();
                    return new ClaimOutcome(false,
                        string.IsNullOrEmpty(failMessage) ? "领取失败" : failMessage,
                        new List<int>(), emptyGranted, emptyOverflow, 0);
                }

                BagRepository.SaveSlots(connection, transaction, playerId, slots);

                if (overflow.Count > 0)
                {
                    MailRepository.InsertOverflowMail(connection, transaction, playerId,
                        "成就奖励转入邮箱",
                        "背包空间不足，部分成就奖励已转入邮箱，请清理背包后领取。",
                        overflow);
                }

                transaction.Commit();
            }
            catch (Exception exception)
            {
                Console.WriteLine($"领取成就失败 [{exception.GetType().Name}]: {exception.Message}");
                return new ClaimOutcome(false, "领取失败，请稍后再试",
                    new List<int>(), emptyGranted, emptyOverflow, 0);
            }

            if (overflow.Count > 0)
            {
                CompeteServer.Manager.AgentReminderService.OnMailArrived(
                    playerId, "成就奖励转入邮箱", overflow.Sum(item => item.Count));
            }

            string message = overflow.Count > 0
                ? $"领取成功（{overflow.Sum(item => item.Count)} 个奖励已转入邮箱）"
                : "领取成功";
            if (claimedIds.Count < wanted.Count && !string.IsNullOrEmpty(failMessage))
            {
                message += $"；{failMessage}";
            }

            return new ClaimOutcome(true, message, claimedIds, granted, overflow, grantedUnits);
        }

        /// <summary>GM/联调：改进度、强制解锁、重置。</summary>
        public (bool Success, string Message) GmSet(
            int playerId, int achievementId, int progress, bool unlock, bool reset)
        {
            if (playerId <= 0 || achievementId <= 0) return (false, "参数无效");
            Dictionary<int, AchievementConfig> configs = GetConfigMap();
            if (!configs.TryGetValue(achievementId, out AchievementConfig? config))
            {
                return (false, "成就不存在");
            }

            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();

                if (reset)
                {
                    using MySqlCommand delete = new(
                        "DELETE FROM `player_achievements` WHERE `player_id` = @PlayerId AND `achievement_id` = @AchievementId",
                        connection);
                    delete.Parameters.AddWithValue("@PlayerId", playerId);
                    delete.Parameters.AddWithValue("@AchievementId", achievementId);
                    delete.ExecuteNonQuery();
                    return (true, "已重置");
                }

                using (MySqlCommand ensure = new(
                    """
                    INSERT IGNORE INTO `player_achievements`
                      (`player_id`, `achievement_id`, `state`, `progress`)
                    VALUES (@PlayerId, @AchievementId, 1, 0)
                    """,
                    connection))
                {
                    ensure.Parameters.AddWithValue("@PlayerId", playerId);
                    ensure.Parameters.AddWithValue("@AchievementId", achievementId);
                    ensure.ExecuteNonQuery();
                }

                if (unlock)
                {
                    using MySqlCommand update = new(
                        """
                        UPDATE `player_achievements`
                        SET `state` = 2, `progress` = @Progress, `unlocked_at` = COALESCE(`unlocked_at`, @Now)
                        WHERE `player_id` = @PlayerId AND `achievement_id` = @AchievementId AND `state` <> 3
                        """,
                        connection);
                    update.Parameters.AddWithValue("@Progress", PrimaryTarget(config));
                    update.Parameters.AddWithValue("@Now", DateTime.UtcNow);
                    update.Parameters.AddWithValue("@PlayerId", playerId);
                    update.Parameters.AddWithValue("@AchievementId", achievementId);
                    update.ExecuteNonQuery();
                    return (true, "已解锁");
                }

                using (MySqlCommand update = new(
                    """
                    UPDATE `player_achievements`
                    SET `progress` = @Progress
                    WHERE `player_id` = @PlayerId AND `achievement_id` = @AchievementId AND `state` = 1
                    """,
                    connection))
                {
                    update.Parameters.AddWithValue("@Progress", Math.Max(0, progress));
                    update.Parameters.AddWithValue("@PlayerId", playerId);
                    update.Parameters.AddWithValue("@AchievementId", achievementId);
                    update.ExecuteNonQuery();
                }
                Evaluate(playerId, new[] { achievementId });
                return (true, "已改进度");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"成就GM失败 [{exception.GetType().Name}]: {exception.Message}");
                return (false, "操作失败");
            }
        }

        // ──────────────────────────────
        // 内部工具
        // ──────────────────────────────

        private static void EnsureNextChainRow(
            MySqlConnection connection, MySqlTransaction transaction,
            int playerId, int claimedId, Dictionary<int, AchievementConfig> configs)
        {
            foreach (AchievementConfig next in configs.Values)
            {
                if (!next.Enabled || next.PrevAchievementId != claimedId) continue;
                using MySqlCommand command = new(
                    """
                    INSERT IGNORE INTO `player_achievements`
                      (`player_id`, `achievement_id`, `state`, `progress`)
                    VALUES (@PlayerId, @AchievementId, 1, 0)
                    """,
                    connection, transaction);
                command.Parameters.AddWithValue("@PlayerId", playerId);
                command.Parameters.AddWithValue("@AchievementId", next.AchievementId);
                command.ExecuteNonQuery();
            }
        }

        private static Dictionary<int, int> LoadMaxStacks()
        {
            var stacks = new Dictionary<int, int>();
            foreach ((int itemId, BagRepository.ItemConfig config) in BagRepository.GetConfigMap())
            {
                stacks[itemId] = config.MaxStack;
            }
            return stacks;
        }
    }
}
