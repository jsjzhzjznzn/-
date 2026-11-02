using MySqlConnector;

namespace CompeteServer.Database
{
    /// <summary>
    /// 任务静态配置（quest_config + quest_reward 两张表的合并视图）
    /// 设计取舍：每个任务只含一个目标（Goal），用"组合任务"代替"组合目标"——
    /// 这样目标天然复用任务具备的发奖/扣道具能力，配置也只需一张任务表
    /// </summary>
    public sealed class QuestConfig
    {
        /// <summary>任务ID</summary>
        public int QuestId { get; init; }

        /// <summary>任务名</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>任务描述</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>分类（1 主线 / 2 支线）</summary>
        public int Category { get; init; }

        /// <summary>目标类型（1 挑战成绩 / 2 收集物品 / 3 加好友 / 4 发消息）</summary>
        public int GoalType { get; init; }

        /// <summary>目标参数1（挑战成绩=挑战ID，0=任意；收集物品=物品ID）</summary>
        public int GoalParam1 { get; init; }

        /// <summary>目标参数2（挑战成绩=分数阈值，0=不限）</summary>
        public int GoalParam2 { get; init; }

        /// <summary>目标进度（达成所需次数/数量）</summary>
        public int TargetProgress { get; init; }

        /// <summary>前置任务ID（0=无前置）</summary>
        public int PrevQuestId { get; init; }

        /// <summary>父任务ID（0=顶层任务；非 0 = 本任务是该章节任务的步骤）</summary>
        public int ParentQuestId { get; init; }

        /// <summary>奖励列表（物品ID / 数量）</summary>
        public List<(int ItemId, int Count)> Rewards { get; init; } = new();
    }

    /// <summary>
    /// 玩家任务存档行（player_quests）
    /// </summary>
    public sealed class PlayerQuestRow
    {
        /// <summary>任务ID</summary>
        public int QuestId { get; set; }

        /// <summary>状态（1 进行中 / 2 已完成可提交 / 3 已提交）</summary>
        public int State { get; set; }

        /// <summary>累计进度（收集类进度由实时统计得出，此值仅存档）</summary>
        public int Progress { get; set; }
    }

    /// <summary>
    /// 任务仓库类（服务端权威）
    /// 数据分层：quest_config/quest_reward = 任务静态配置；player_quests = 玩家任务进度（未接取不落库）
    /// 进度来源分治（本项目的取舍）：
    ///   累计类（挑战成绩/加好友/发消息）进度存 player_quests.progress，由玩法事件推进，以缓存值为准；
    ///   收集类（收集物品）进度 = 服务端实时聚合 player_items 的持有量，天然准确且防作弊。
    /// 收集类任务为"交付"语义：提交时重新校验持有量，足够才扣除物品并发放奖励（事务内完成）。
    /// 状态机：未接取（无行）→ 进行中 → 已完成（可提交）→ 已提交；放弃 = 删行回到未接取。
    /// 表结构见 Migrations\005_AddQuests.sql（EnsureTable 提供幂等建表兜底）
    /// </summary>
    public class QuestRepository
    {
        // ──────────────────────────────
        // 常量
        // ──────────────────────────────

        /// <summary>目标类型：挑战成绩（param1=挑战ID，param2=分数阈值）</summary>
        public const int GoalTypeChallenge = 1;

        /// <summary>目标类型：收集物品（param1=物品ID，交付语义，提交时扣除）</summary>
        public const int GoalTypeCollectItem = 2;

        /// <summary>目标类型：加好友（同意好友申请时 +1）</summary>
        public const int GoalTypeAddFriend = 3;

        /// <summary>目标类型：发消息（发送聊天消息时 +1）</summary>
        public const int GoalTypeSendMessage = 4;

        /// <summary>目标类型：章节步骤容器（本任务是"章节"，子任务=步骤按序执行；目标数以实际子任务数为准）</summary>
        public const int GoalTypeChapter = 5;

        /// <summary>目标类型：生涯分数（成就用；param1 = 生涯指标ID，进度实时读生涯值）</summary>
        public const int GoalTypeCareer = 6;

        /// <summary>任务分类：成就（自动激活、多阶段用 prev 链接，不进"可接取"池）</summary>
        public const int CategoryAchievement = 3;

        /// <summary>状态：进行中</summary>
        public const int StateProcessing = 1;

        /// <summary>状态：已完成（可提交）</summary>
        public const int StateFinished = 2;

        /// <summary>状态：已提交（终态）</summary>
        public const int StateSubmitted = 3;

        // ──────────────────────────────
        // 初始化（服务端启动时调用）
        // ──────────────────────────────

        /// <summary>幂等建表：与 Migrations\005_AddQuests.sql 等价，表已存在则无副作用。</summary>
        public void EnsureTable()
        {
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();

            using (MySqlCommand command = new(
                """
                CREATE TABLE IF NOT EXISTS `quest_config` (
                  `quest_id`        INT          NOT NULL,
                  `name`            VARCHAR(64)  NOT NULL,
                  `description`     VARCHAR(255) NOT NULL DEFAULT '',
                  `category`        INT          NOT NULL DEFAULT 1,
                  `goal_type`       INT          NOT NULL,
                  `goal_param1`     INT          NOT NULL DEFAULT 0,
                  `goal_param2`     INT          NOT NULL DEFAULT 0,
                  `target_progress` INT          NOT NULL DEFAULT 1,
                  `prev_quest_id`   INT          NOT NULL DEFAULT 0,
                  `parent_quest_id` INT          NOT NULL DEFAULT 0,
                  PRIMARY KEY (`quest_id`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
                """,
                connection))
            {
                command.ExecuteNonQuery();
            }

            // 已有表的升级路径：补 parent_quest_id 列（MySQL 不支持 ADD COLUMN IF NOT EXISTS，改查 information_schema）
            using (MySqlCommand check = new(
                "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'quest_config' AND column_name = 'parent_quest_id'",
                connection))
            {
                if (Convert.ToInt64(check.ExecuteScalar() ?? 0L) == 0)
                {
                    using MySqlCommand alter = new(
                        "ALTER TABLE quest_config ADD COLUMN `parent_quest_id` INT NOT NULL DEFAULT 0 AFTER `prev_quest_id`",
                        connection);
                    alter.ExecuteNonQuery();
                }
            }

            using (MySqlCommand command = new(
                """
                CREATE TABLE IF NOT EXISTS `quest_reward` (
                  `quest_id` INT NOT NULL,
                  `item_id`  INT NOT NULL,
                  `count`    INT NOT NULL,
                  PRIMARY KEY (`quest_id`, `item_id`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
                """,
                connection))
            {
                command.ExecuteNonQuery();
            }

            using (MySqlCommand command = new(
                """
                CREATE TABLE IF NOT EXISTS `player_quests` (
                  `player_id`    INT      NOT NULL,
                  `quest_id`     INT      NOT NULL,
                  `state`        INT      NOT NULL DEFAULT 1,
                  `progress`     INT      NOT NULL DEFAULT 0,
                  `accepted_at`  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                  `finished_at`  DATETIME NULL,
                  `submitted_at` DATETIME NULL,
                  PRIMARY KEY (`player_id`, `quest_id`),
                  KEY `idx_player_state` (`player_id`, `state`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
                """,
                connection))
            {
                command.ExecuteNonQuery();
            }

            InvalidateConfigCache();   // 建表/补列后让缓存重建（启动时无害，也兼容运行中调用）
        }

        /// <summary>
        /// 幂等种子：写入任务配置与奖励（INSERT IGNORE，已存在的任务不覆盖，方便手工改库后不被重置）。
        /// 8 条种子覆盖三类目标（挑战成绩/收集物品/加好友/发消息）与两条前置链（主线 2001→2005、支线 2101→2102）。
        /// </summary>
        public void SeedQuestConfigs()
        {
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();

            using (MySqlCommand command = new(
                """
                INSERT IGNORE INTO `quest_config`
                  (`quest_id`, `name`, `description`, `category`, `goal_type`, `goal_param1`, `goal_param2`, `target_progress`, `prev_quest_id`)
                VALUES
                  (2001, '初次挑战', '完成 1 次挑战，熟悉一下玩法吧。',           1, 1, 1,    0, 1,    0),
                  (2002, '挑战好手', '累计 2 次挑战成绩达到 60 分。',             1, 1, 1,   60, 2, 2001),
                  (2003, '交付铁矿', '收集 10 个铁矿并交付（提交时扣除）。',       1, 2, 1003, 0, 10, 2002),
                  (2004, '交个朋友', '添加 1 位好友。',                          1, 3, 0,    0, 1,  2003),
                  (2005, '畅聊几句', '给好友发送 5 条消息。',                     1, 4, 0,    0, 5,  2004),
                  (2101, '采集铜矿', '收集 5 个铜矿并交付（提交时扣除）。',        2, 2, 1004, 0, 5,    0),
                  (2102, '采集银矿', '收集 3 个银矿并交付（提交时扣除）。',        2, 2, 1005, 0, 3, 2101),
                  (2103, '挑战达人', '单次挑战成绩达到 80 分。',                  2, 1, 1,   80, 1,    0)
                """,
                connection))
            {
                command.ExecuteNonQuery();
            }

            using (MySqlCommand command = new(
                """
                INSERT IGNORE INTO `quest_reward` (`quest_id`, `item_id`, `count`)
                VALUES
                  (2001, 1003, 5),    -- 初次挑战  → 铁矿 x5
                  (2002, 1007, 3),    -- 挑战好手  → 强化石 x3
                  (2003, 1001, 5),    -- 交付铁矿  → 治疗药剂 x5
                  (2004, 1005, 3),    -- 交个朋友  → 银矿 x3
                  (2005, 1012, 1),    -- 畅聊几句  → 长刃·初 x1
                  (2101, 1001, 3),    -- 采集铜矿  → 治疗药剂 x3
                  (2102, 1007, 2),    -- 采集银矿  → 强化石 x2
                  (2103, 1017, 1)     -- 挑战达人  → 长刃·终 x1
                """,
                connection))
            {
                command.ExecuteNonQuery();
            }

            // ──────────────────────────────
            // 完整版种子：章节任务（子任务表步骤）+ 成就任务（生涯分数）
            // 章节 goal_type=5 只当步骤容器，进度 = 已提交步骤数；
            // 成就 category=3、goal_type=6，进度实时读生涯数据，多阶段用 prev 链接（免数据继承）
            // ──────────────────────────────

            using (MySqlCommand command = new(
                """
                INSERT IGNORE INTO `quest_config`
                  (`quest_id`, `name`, `description`, `category`, `goal_type`, `goal_param1`, `goal_param2`, `target_progress`, `prev_quest_id`, `parent_quest_id`)
                VALUES
                  (3001, '新手试炼',       '完成三个步骤：挑战一次、交付铜矿、和朋友聊两句。',   1, 5, 0,    0,  3,    0,    0),
                  (3002, '初试身手',       '完成 1 次挑战。',                             1, 1, 1,    0,  1,    0, 3001),
                  (3003, '交付铜矿',       '收集 5 个铜矿并交付（提交时扣除）。',           1, 2, 1004, 0,  5,    0, 3001),
                  (3004, '报个平安',       '给好友发送 2 条消息。',                        1, 4, 0,    0,  2,    0, 3001),
                  (3101, '采集试炼',       '完成两个步骤：交付铁矿、打出好成绩。',           2, 5, 0,    0,  2,    0,    0),
                  (3102, '交付铁矿',       '收集 5 个铁矿并交付（提交时扣除）。',           2, 2, 1003, 0,  5,    0, 3101),
                  (3103, '小试锋芒',       '单次挑战成绩达到 40 分。',                     2, 1, 1,   40,  1,    0, 3101)
                """,
                connection))
            {
                command.ExecuteNonQuery();
            }

            using (MySqlCommand command = new(
                """
                INSERT IGNORE INTO `quest_reward` (`quest_id`, `item_id`, `count`)
                VALUES
                  (3001, 1007, 5),    -- 新手试炼(章节) → 强化石 x5
                  (3002, 1001, 2),    -- 步骤：初试身手 → 治疗药剂 x2
                  (3003, 1003, 3),    -- 步骤：交付铜矿 → 铁矿 x3
                  (3004, 1005, 2),    -- 步骤：报个平安 → 银矿 x2
                  (3101, 1001, 5),    -- 采集试炼(章节) → 治疗药剂 x5
                  (3102, 1004, 2),    -- 步骤：交付铁矿 → 铜矿 x2
                  (3103, 1007, 1)     -- 步骤：小试锋芒 → 强化石 x1
                """,
                connection))
            {
                command.ExecuteNonQuery();
            }

            InvalidateConfigCache();   // 种子写完后让缓存重建（否则要等 TTL 过期才能看到新任务）
        }

        // ──────────────────────────────
        // 查询
        // ──────────────────────────────

        // ──────────────────────────────
        // 静态配置缓存（quest_config / quest_reward 是纯静态数据，只有种子和手工改库会变）
        // 一次任务列表请求原本要反复读它 3-4 次（每次 2 条 SELECT），一次挑战事件链 8-10 次——
        // 缓存后整个请求链共享同一份快照。TTL 只为兼容"手工改库"的习惯：改动最迟 10s 生效。
        // 线程安全：会话消息在线程池上并发处理，这里用"原子引用发布 + 不可变快照"——
        // 发布后的 List/Dictionary 不再修改，读者拿旧引用也只是旧快照；失败读到的空结果不缓存。
        // EnsureTable/SeedQuestConfigs 结束时主动失效。
        // ──────────────────────────────

        /// <summary>配置列表缓存（发布后不可变）</summary>
        private static List<QuestConfig>? configCache;

        /// <summary>配置索引缓存（发布后不可变；替代每次请求重建 Dictionary）</summary>
        private static Dictionary<int, QuestConfig>? configMapCache;

        /// <summary>缓存发布时刻（Environment.TickCount64；0 = 无缓存）</summary>
        private static long configCacheTick;

        /// <summary>缓存有效期：手工改库最迟 10 秒后可见</summary>
        private const long ConfigCacheTtlMs = 10_000;

        /// <summary>
        /// 读取全部任务配置（按任务ID升序，奖励一并挂好；10 秒内共享缓存）。
        /// </summary>
        /// <returns>返回值类型：List，用途：任务配置列表（含奖励）</returns>
        public List<QuestConfig> GetConfigs()
        {
            List<QuestConfig>? cache = configCache;
            long tick = Interlocked.Read(ref configCacheTick);
            if (cache != null && tick > 0 && Environment.TickCount64 - tick < ConfigCacheTtlMs)
            {
                return cache;
            }

            List<QuestConfig> fresh = LoadConfigsFromDb();
            if (fresh.Count > 0)
            {
                configMapCache = Index(fresh);
                configCache = fresh;
                Interlocked.Exchange(ref configCacheTick, Environment.TickCount64);
            }
            return fresh;
        }

        /// <summary>
        /// 读取任务配置索引（questId → 配置；与 GetConfigs 共享同一份缓存快照）
        /// </summary>
        /// <returns>返回值类型：Dictionary，用途：任务ID → 配置</returns>
        public Dictionary<int, QuestConfig> GetConfigMap()
        {
            GetConfigs();   // 确保缓存就绪（失败时也会尝试加载一次）
            return configMapCache ?? new Dictionary<int, QuestConfig>();
        }

        /// <summary>主动失效配置缓存（建表/种子后调用；手工改库后也可调用以立即生效）。</summary>
        public static void InvalidateConfigCache()
        {
            configCache = null;
            configMapCache = null;
            Interlocked.Exchange(ref configCacheTick, 0);
        }

        /// <summary>从数据库加载全部配置与奖励（发布后不再修改）。</summary>
        private List<QuestConfig> LoadConfigsFromDb()
        {
            var configs = new List<QuestConfig>();
            var rewards = new Dictionary<int, List<(int ItemId, int Count)>>();

            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();

                using (MySqlCommand command = new(
                    "SELECT `quest_id`, `name`, `description`, `category`, `goal_type`, `goal_param1`, `goal_param2`, `target_progress`, `prev_quest_id`, `parent_quest_id` FROM quest_config ORDER BY `quest_id`",
                    connection))
                using (MySqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        configs.Add(new QuestConfig
                        {
                            QuestId = reader.GetInt32(0),
                            Name = reader.GetString(1),
                            Description = reader.GetString(2),
                            Category = reader.GetInt32(3),
                            GoalType = reader.GetInt32(4),
                            GoalParam1 = reader.GetInt32(5),
                            GoalParam2 = reader.GetInt32(6),
                            TargetProgress = Math.Max(1, reader.GetInt32(7)),
                            PrevQuestId = reader.GetInt32(8),
                            ParentQuestId = reader.GetInt32(9),
                        });
                    }
                }

                using (MySqlCommand command = new(
                    "SELECT `quest_id`, `item_id`, `count` FROM quest_reward ORDER BY `quest_id`, `item_id`",
                    connection))
                using (MySqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        int questId = reader.GetInt32(0);
                        if (!rewards.TryGetValue(questId, out List<(int, int)>? list))
                        {
                            list = new List<(int, int)>();
                            rewards[questId] = list;
                        }
                        list.Add((reader.GetInt32(1), Math.Max(1, reader.GetInt32(2))));
                    }
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"任务配置读取失败 [{exception.GetType().Name}]: {exception.Message}");
            }

            foreach (QuestConfig config in configs)
            {
                if (rewards.TryGetValue(config.QuestId, out List<(int ItemId, int Count)>? list))
                {
                    config.Rewards.AddRange(list);
                }
            }
            return configs;
        }

        /// <summary>把任务配置列表索引成字典（内部按ID查配置用）。</summary>
        private static Dictionary<int, QuestConfig> Index(List<QuestConfig> configs)
        {
            var map = new Dictionary<int, QuestConfig>(configs.Count);
            foreach (QuestConfig config in configs) map[config.QuestId] = config;
            return map;
        }

        /// <summary>
        /// 读取玩家任务存档（只含已接取过的任务，未接取不入库）。
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID（服务端会话绑定，不可伪造）</param>
        /// <returns>返回值类型：Dictionary，用途：任务ID → 存档行</returns>
        public Dictionary<int, PlayerQuestRow> GetPlayerQuests(int playerId)
        {
            var rows = new Dictionary<int, PlayerQuestRow>();
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlCommand command = new(
                    "SELECT `quest_id`, `state`, `progress` FROM player_quests WHERE `player_id` = @PlayerId",
                    connection);
                command.Parameters.AddWithValue("@PlayerId", playerId);
                using MySqlDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var row = new PlayerQuestRow
                    {
                        QuestId = reader.GetInt32(0),
                        State = reader.GetInt32(1),
                        Progress = reader.GetInt32(2),
                    };
                    rows[row.QuestId] = row;
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"玩家任务读取失败 [{exception.GetType().Name}]: {exception.Message}");
            }
            return rows;
        }

        /// <summary>
        /// 构建玩家任务状态快照（下发给客户端）
        /// "实时进度类"任务（收集物品=实时持有量；生涯分数=生涯累计值）的进度与状态按实时值推导
        /// （提交时仍会重新校验，故此处只用于展示）；已提交的任务保持终态，不再按实时量回退
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <returns>返回值类型：List，用途：(任务ID, 状态, 进度) 列表</returns>
        public List<(int QuestId, int State, int Progress)> BuildSnapshot(int playerId)
        {
            Dictionary<int, QuestConfig> map = GetConfigMap();
            Dictionary<int, PlayerQuestRow> rows = GetPlayerQuests(playerId);

            // 两类实时进度源各查一次（多数玩家没有相关任务时返回空表，代价可忽略）
            Dictionary<int, int> itemTotals = LoadItemTotals(playerId);
            Dictionary<int, int> careerValues = new CareerRepository().GetValues(playerId);

            var snapshot = new List<(int QuestId, int State, int Progress)>(rows.Count);
            foreach (PlayerQuestRow row in rows.Values)
            {
                if (!map.TryGetValue(row.QuestId, out QuestConfig? config))
                {
                    continue;   // 配置被删掉的脏数据：忽略（不让客户端看到无配置的任务）
                }

                if (row.State != StateSubmitted && config.GoalType == GoalTypeCollectItem)
                {
                    int live = itemTotals.TryGetValue(config.GoalParam1, out int count) ? count : 0;
                    snapshot.Add((row.QuestId, live >= config.TargetProgress ? StateFinished : StateProcessing, live));
                    continue;
                }
                if (row.State != StateSubmitted && config.GoalType == GoalTypeCareer)
                {
                    int live = careerValues.TryGetValue(config.GoalParam1, out int value) ? value : 0;
                    snapshot.Add((row.QuestId, live >= config.TargetProgress ? StateFinished : StateProcessing, live));
                    continue;
                }

                snapshot.Add((row.QuestId, row.State, row.Progress));
            }

            snapshot.Sort((left, right) => left.QuestId.CompareTo(right.QuestId));
            return snapshot;
        }

        /// <summary>
        /// 成就自动激活：为"应激活而未激活"的成就补"进行中"行（拉列表/提交任务等时机调用，幂等）
        /// 成就不进"可接取"池——自动追踪、达成后可领取，避免入口红点被成就常年占着；
        /// 多阶段成就靠 prev 链：前一阶段提交后，下一阶段在下次调用时自动激活
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        public void AutoActivateAchievements(int playerId)
        {
            try
            {
                Dictionary<int, QuestConfig> configs = GetConfigMap();
                Dictionary<int, PlayerQuestRow> rows = GetPlayerQuests(playerId);

                foreach (QuestConfig config in configs.Values)
                {
                    if (config.Category != CategoryAchievement) continue;
                    if (config.ParentQuestId != 0) continue;          // 成就不做别人的步骤
                    if (rows.ContainsKey(config.QuestId)) continue;   // 已激活
                    if (config.PrevQuestId > 0 &&
                        !(rows.TryGetValue(config.PrevQuestId, out PlayerQuestRow? prev) && prev.State == StateSubmitted))
                    {
                        continue;   // 上一阶段未提交，暂不激活
                    }
                    InsertQuestRow(playerId, config);
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"成就自动激活失败 [{exception.GetType().Name}]: {exception.Message}");
            }
        }

        // ──────────────────────────────
        // 操作（全部服务端权威）
        // ──────────────────────────────

        /// <summary>
        /// 接取任务的方法
        /// 校验：任务存在、未重复接取、前置任务已提交；实时进度类（收集/生涯）接取时立即按实时值结算。
        /// 步骤任务不独立接取（由所属章节自动开启）；章节任务接取时自动开启第一个步骤
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="questId">参数名：questId，类型：int，用途：任务ID</param>
        /// <returns>返回值类型：(bool Success, string Message)，用途：操作结果与提示</returns>
        public (bool Success, string Message) Accept(int playerId, int questId)
        {
            Dictionary<int, QuestConfig> configs = GetConfigMap();
            if (!configs.TryGetValue(questId, out QuestConfig? config))
            {
                return (false, "任务不存在");
            }

            // 步骤任务不独立接取：章节任务只提供上下文，逻辑都集中在当前步骤（FSM 语义）
            if (config.ParentQuestId > 0)
            {
                string parentName = configs.TryGetValue(config.ParentQuestId, out QuestConfig? parent)
                    ? parent.Name
                    : $"#{config.ParentQuestId}";
                return (false, $"该任务是【{parentName}】的步骤，接取章节任务即可");
            }

            try
            {
                Dictionary<int, PlayerQuestRow> rows = GetPlayerQuests(playerId);
                if (rows.TryGetValue(questId, out PlayerQuestRow? existing))
                {
                    return existing.State == StateSubmitted
                        ? (false, "该任务已完成")
                        : (false, "任务已接取");
                }

                // 前置任务必须已提交（绝大多数任务的接取条件都是前置任务完成，先判可省掉大量扫描）
                if (config.PrevQuestId > 0)
                {
                    bool prevDone = rows.TryGetValue(config.PrevQuestId, out PlayerQuestRow? prev)
                        && prev.State == StateSubmitted;
                    if (!prevDone)
                    {
                        string prevName = configs.TryGetValue(config.PrevQuestId, out QuestConfig? prevConfig)
                            ? prevConfig.Name
                            : $"#{config.PrevQuestId}";
                        return (false, $"需要先完成任务【{prevName}】");
                    }
                }

                // 章节任务：没有步骤的章节是配置错误
                if (config.GoalType == GoalTypeChapter && ChildrenOf(configs, questId).Count == 0)
                {
                    return (false, "任务配置错误：该章节没有步骤");
                }

                InsertQuestRow(playerId, config);

                // 章节任务：自动开启第一个步骤（玩家看到"第 1/X 步"）
                if (config.GoalType == GoalTypeChapter)
                {
                    List<QuestConfig> children = ChildrenOf(configs, questId);
                    InsertQuestRow(playerId, children[0]);
                    return (true, $"已接取任务【{config.Name}】，共 {children.Count} 步");
                }

                return (true, $"已接取任务【{config.Name}】");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"接取任务失败 [{exception.GetType().Name}]: {exception.Message}");
                return (false, "接取失败，请稍后再试");
            }
        }

        /// <summary>
        /// 提交任务的方法（收集类为交付语义）
        /// 单事务 + 行锁内完成：锁任务行校验状态 → 实时校验进度 → 扣除交付物品 → 发放奖励 → 置为已提交（守卫更新）。
        /// 实时进度类（收集/生涯）会在此处重新校验实时值（玩家可能已把道具用掉/丢弃），不足则回退为进行中并拒绝提交。
        /// 状态校验与发奖在同一锁窗口内落地，跨线程并发提交（本人会话 vs 事件线程自动提交）不会双重发奖。
        /// 步骤任务提交后自动推进所属章节：重算已完成步数，全部完成则章节进入可提交，并自动开启下一步骤
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="questId">参数名：questId，类型：int，用途：任务ID</param>
        /// <returns>返回值类型：(bool Success, string Message, int GrantedUnits)，用途：操作结果、奖励说明、实际发放的物品总单位数（生涯"获得物品"指标用）</returns>
        public (bool Success, string Message, int GrantedUnits) Submit(int playerId, int questId)
        {
            int grantedUnits = 0;
            var granted = new List<string>();
            var overflow = new List<(int ItemId, int Count)>();   // 背包放不下的奖励（事务内转存邮箱）
            Dictionary<int, QuestConfig> configs = GetConfigMap();
            if (!configs.TryGetValue(questId, out QuestConfig? config))
            {
                return (false, "任务不存在", 0);
            }

            using IDisposable writeLock = PlayerWriteLock.Acquire(playerId);

            try
            {
                // 读-校验-改-写全部收进同一事务：旧实现状态校验在事务外，两条路径并发提交同一任务会双重发奖
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlTransaction transaction = connection.BeginTransaction();
                BagRepository.LockPlayerRow(connection, transaction, playerId);

                // 0) 锁任务行（FOR UPDATE）：后续校验与提交都在同一锁窗口内生效
                int state;
                int progress;
                using (MySqlCommand select = new(
                    """
                    SELECT `state`, `progress` FROM player_quests
                    WHERE `player_id` = @PlayerId AND `quest_id` = @QuestId
                    FOR UPDATE
                    """,
                    connection, transaction))
                {
                    select.Parameters.AddWithValue("@PlayerId", playerId);
                    select.Parameters.AddWithValue("@QuestId", questId);
                    using MySqlDataReader reader = select.ExecuteReader();
                    if (!reader.Read())
                    {
                        return (false, "任务尚未接取", 0);   // 事务随 return 释放即回滚（未作任何写）
                    }
                    state = reader.GetInt32(0);
                    progress = reader.GetInt32(1);
                }
                if (state == StateSubmitted)
                {
                    return (false, "任务已提交", 0);
                }

                // 锁内读背包：实时校验、扣除与发放都以这份快照为准
                BagSlot[] slots = BagRepository.LoadSlotsForUpdate(connection, transaction, playerId);

                // 1) 实时进度类：提交时以实时值为准重新校验（进度值可能因使用/丢弃而减少，故此处必须重新校验）
                if (config.GoalType == GoalTypeCollectItem)
                {
                    int live = BagRepository.CountInSlots(slots, config.GoalParam1);
                    if (live < config.TargetProgress)
                    {
                        // 先显式回滚放下行锁，再回退进度：MarkProcessing 走独立连接，否则会被本事务锁住同一段
                        transaction.Rollback();
                        MarkProcessing(playerId, questId, live);
                        return (false, $"还需 {config.TargetProgress - live} 个【{ItemName(config.GoalParam1)}】", 0);
                    }
                }
                else if (config.GoalType == GoalTypeCareer)
                {
                    int live = new CareerRepository().GetValue(playerId, config.GoalParam1);
                    if (live < config.TargetProgress)
                    {
                        return (false, $"任务尚未完成（当前 {live}/{config.TargetProgress}）", 0);
                    }
                }
                else if (progress < config.TargetProgress)
                {
                    return (false, "任务尚未完成", 0);
                }

                // 2) 收集类：扣除交付物品（按槽位顺序扣，扣光的槽清空）
                if (config.GoalType == GoalTypeCollectItem)
                {
                    int remaining = config.TargetProgress;
                    for (int i = 0; i < slots.Length && remaining > 0; i++)
                    {
                        if (slots[i].Count <= 0 || slots[i].ItemId != config.GoalParam1) continue;
                        int take = Math.Min(slots[i].Count, remaining);
                        remaining -= take;
                        int left = slots[i].Count - take;
                        long instanceId = slots[i].InstanceId;
                        slots[i] = left > 0 ? new BagSlot(slots[i].ItemId, left, instanceId) : default;
                        // 交付掉的装备实例一并删除，否则装备列表里会留下背包中已不存在的"幽灵武器"
                        if (left <= 0 && instanceId > 0)
                        {
                            BagRepository.DeleteEquipmentInstance(connection, transaction, playerId, instanceId);
                        }
                    }
                }

                // 3) 发放奖励（先补同类未满堆叠，再占空槽；与背包发放同规则；放不下的记入 overflow）
                if (config.Rewards.Count > 0)
                {
                    Dictionary<int, int> maxStacks = LoadMaxStacks();
                    foreach ((int itemId, int count) in config.Rewards)
                    {
                        int maxStack = maxStacks.TryGetValue(itemId, out int stack) ? Math.Max(1, stack) : 1;
                        int remaining = BagRepository.GrantIntoSlots(
                            connection, transaction, playerId, ref slots, itemId, count, maxStack);

                        int delivered = count - remaining;
                        if (delivered > 0)
                        {
                            grantedUnits += delivered;
                            granted.Add($"{ItemName(itemId)} x{delivered}");
                        }
                        if (remaining > 0) overflow.Add((itemId, remaining));
                    }
                }

                // 4) 落库：槽位差异回写 + 任务置为已提交
                BagRepository.SaveSlots(connection, transaction, playerId, slots);

                // 守卫更新：带 state<>已提交 条件并核对受影响行数（行锁后的兜底——万一状态被无锁路径改写，整单回滚不发第二份奖）
                using (MySqlCommand update = new(
                    """
                    UPDATE player_quests SET `state` = @State, `submitted_at` = @SubmittedAt
                    WHERE `player_id` = @PlayerId AND `quest_id` = @QuestId AND `state` <> @SubmittedState
                    """,
                    connection, transaction))
                {
                    update.Parameters.AddWithValue("@State", StateSubmitted);
                    update.Parameters.AddWithValue("@SubmittedAt", DateTime.UtcNow);
                    update.Parameters.AddWithValue("@PlayerId", playerId);
                    update.Parameters.AddWithValue("@QuestId", questId);
                    update.Parameters.AddWithValue("@SubmittedState", StateSubmitted);
                    if (update.ExecuteNonQuery() == 0)
                    {
                        transaction.Rollback();
                        return (false, "任务已提交", 0);
                    }
                }

                // 4.5) 背包放不下的奖励转存邮箱（同一事务——DNF 云幂罐子的教训：宁可玩家晚拿，不能奖励凭空消失）
                if (overflow.Count > 0)
                {
                    MailRepository.InsertOverflowMail(connection, transaction, playerId,
                        "任务奖励转入邮箱",
                        $"背包空间不足，【{config.Name}】的部分奖励已转入邮箱，请清理背包后领取。",
                        overflow);
                }

                transaction.Commit();
            }
            catch (Exception exception)
            {
                Console.WriteLine($"提交任务失败 [{exception.GetType().Name}]: {exception.Message}");
                return (false, "提交失败，请稍后再试", 0);
            }

            // 步骤任务：提交后推进所属章节（重算已完成步数 → 全部完成则章节可提交 → 自动开启下一步骤）
            if (config.ParentQuestId > 0)
            {
                AdvanceChapter(playerId, config.ParentQuestId, configs);
                AcceptNextStep(playerId, config.ParentQuestId, configs);
            }

            // 生涯：提交任务数 +1；任务奖励发放的物品计入"累计获得物品"。
            // 统计放在仓库层而不是消息处理器层——这样"手动提交"与"步骤自动提交"两条路径的口径一致
            //（踩过坑：放在处理器层会漏掉 OnGameEvent 里自动提交的步骤）；
            // 成就的重算与推送由调用方（处理器/事件入口）负责，仓库不碰网络
            CareerRepository career = new();
            career.Add(playerId, CareerRepository.ScoreQuestSubmit, 1);
            if (grantedUnits > 0)
            {
                career.Add(playerId, CareerRepository.ScoreItemGain, grantedUnits);
            }

            // 成就按实时统计重算（手动提交与章节步骤自动提交两条路径都经过这里，口径一致）
            CompeteServer.Manager.AchievementService.OnCareerStatChanged(
                playerId, CareerRepository.ScoreQuestSubmit);
            if (grantedUnits > 0)
            {
                CompeteServer.Manager.AchievementService.OnCareerStatChanged(
                    playerId, CareerRepository.ScoreItemGain);
            }

            // 阿福主动提醒（悬浮球 3107）：满包转存的奖励进了邮箱——玩家最容易"不知道奖励去哪了"的场景。
            // 事务已提交，这里是 fire-and-forget（在线判定/到达冷却/并发闸都在服务内），不阻塞提交返回
            if (overflow.Count > 0)
            {
                CompeteServer.Manager.AgentReminderService.OnMailArrived(
                    playerId, "任务奖励转入邮箱", overflow.Sum(item => item.Count));
            }

            string rewardText;
            if (overflow.Count > 0 && granted.Count == 0)
            {
                // 全部奖励都没塞进背包：不显示"无奖励"（奖励在邮箱里，不在包里也不算丢）
                rewardText = $"获得：{overflow.Sum(item => item.Count)} 个奖励已转入邮箱（背包空间不足）";
            }
            else
            {
                rewardText = granted.Count > 0 ? $"获得：{string.Join("、", granted)}" : "无奖励";
                if (overflow.Count > 0)
                {
                    rewardText += $"（背包空间不足，{overflow.Sum(item => item.Count)} 个奖励已转入邮箱）";
                }
            }
            return (true, $"任务【{config.Name}】完成！{rewardText}", grantedUnits);
        }

        /// <summary>
        /// 放弃任务的方法（仅进行中的任务可放弃，删行回到未接取，之后可重新接取）
        /// 章节任务放弃时级联删除全部步骤（重新接取后步骤从头开始）；步骤任务不能单独放弃
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="questId">参数名：questId，类型：int，用途：任务ID</param>
        /// <returns>返回值类型：(bool Success, string Message)，用途：操作结果与提示</returns>
        public (bool Success, string Message) Abandon(int playerId, int questId)
        {
            Dictionary<int, QuestConfig> configs = GetConfigMap();
            if (!configs.TryGetValue(questId, out QuestConfig? config))
            {
                return (false, "任务不存在");
            }

            // 步骤任务不能单独放弃：步骤的生死由所属章节管理
            if (config.ParentQuestId > 0)
            {
                string parentName = configs.TryGetValue(config.ParentQuestId, out QuestConfig? parent)
                    ? parent.Name
                    : $"#{config.ParentQuestId}";
                return (false, $"该任务是【{parentName}】的步骤，请放弃整个任务");
            }

            Dictionary<int, PlayerQuestRow> rows = GetPlayerQuests(playerId);
            if (!rows.TryGetValue(questId, out PlayerQuestRow? row))
            {
                return (false, "任务尚未接取");
            }
            if (row.State != StateProcessing)
            {
                return (false, "任务已完成，无法放弃");
            }

            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();

                var toDelete = new List<int> { questId };
                if (config.GoalType == GoalTypeChapter)
                {
                    toDelete.AddRange(ChildrenOf(configs, questId).Select(child => child.QuestId));
                }
                foreach (int id in toDelete)
                {
                    using MySqlCommand command = new(
                        "DELETE FROM player_quests WHERE `player_id` = @PlayerId AND `quest_id` = @QuestId",
                        connection);
                    command.Parameters.AddWithValue("@PlayerId", playerId);
                    command.Parameters.AddWithValue("@QuestId", id);
                    command.ExecuteNonQuery();
                }

                return (true, config.GoalType == GoalTypeChapter
                    ? $"已放弃任务【{config.Name}】，全部步骤一并重置"
                    : $"已放弃任务【{config.Name}】");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"放弃任务失败 [{exception.GetType().Name}]: {exception.Message}");
                return (false, "放弃失败，请稍后再试");
            }
        }

        /// <summary>
        /// 调试：直接改写任务进度（GM 指令）
        /// 累计类可直接设置；收集类/成就类进度由实时数据决定，不接受改写；
        /// 章节任务视为"补完全部步骤"（直接置步骤为已提交，GM 通道不扣道具、不发步骤奖励）；
        /// 步骤任务补到目标值后走与事件路径相同的自动提交（发步骤奖励并推进章节）
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="questId">参数名：questId，类型：int，用途：任务ID</param>
        /// <param name="progress">参数名：progress，类型：int，用途：要写入的进度值</param>
        /// <returns>返回值类型：(bool Success, string Message)，用途：操作结果与提示</returns>
        public (bool Success, string Message) GmSetProgress(int playerId, int questId, int progress)
        {
            Dictionary<int, QuestConfig> configs = GetConfigMap();
            if (!configs.TryGetValue(questId, out QuestConfig? config))
            {
                return (false, "任务不存在");
            }
            if (config.GoalType == GoalTypeCollectItem)
            {
                return (false, "收集类任务进度由实际持有量决定，请直接发放物品");
            }
            if (config.GoalType == GoalTypeCareer)
            {
                return (false, "成就进度由生涯数据决定，请通过玩法事件累计");
            }

            Dictionary<int, PlayerQuestRow> rows = GetPlayerQuests(playerId);
            if (!rows.TryGetValue(questId, out PlayerQuestRow? row))
            {
                return (false, "任务尚未接取");
            }
            if (row.State == StateSubmitted)
            {
                return (false, "任务已提交");
            }

            // 章节任务：GM = 补完全部步骤（步骤行直接置为已提交，不扣道具、不发步骤奖励），章节进入可提交
            if (config.GoalType == GoalTypeChapter)
            {
                List<QuestConfig> children = ChildrenOf(configs, questId);
                foreach (QuestConfig child in children)
                {
                    UpsertSubmittedRow(playerId, child.QuestId, child.TargetProgress);
                }
                SaveProgress(playerId, questId, children.Count, StateFinished);
                return (true, $"任务【{config.Name}】已补完全部 {children.Count} 步（步骤奖励未发放），可提交领奖");
            }

            int value = Math.Clamp(progress, 0, config.TargetProgress);
            int state = value >= config.TargetProgress ? StateFinished : StateProcessing;

            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlCommand command = new(
                    """
                    UPDATE player_quests SET `progress` = @Progress, `state` = @State, `finished_at` = @FinishedAt
                    WHERE `player_id` = @PlayerId AND `quest_id` = @QuestId
                    """,
                    connection);
                command.Parameters.AddWithValue("@Progress", value);
                command.Parameters.AddWithValue("@State", state);
                command.Parameters.AddWithValue("@FinishedAt", state == StateFinished ? DateTime.UtcNow : DBNull.Value);
                command.Parameters.AddWithValue("@PlayerId", playerId);
                command.Parameters.AddWithValue("@QuestId", questId);
                command.ExecuteNonQuery();
            }
            catch (Exception exception)
            {
                Console.WriteLine($"设置任务进度失败 [{exception.GetType().Name}]: {exception.Message}");
                return (false, "设置失败，请稍后再试");
            }

            // 步骤任务补到目标后：与事件驱动路径一致，自动提交并推进章节
            if (config.ParentQuestId > 0 && state == StateFinished)
            {
                _ = Submit(playerId, questId);
            }

            return (true, $"任务【{config.Name}】进度已设为 {value}/{config.TargetProgress}");
        }

        // ──────────────────────────────
        // 玩法事件接入（其他模块配合任务的部分）
        // ──────────────────────────────

        /// <summary>
        /// 累计类目标的事件推进入口
        /// 挑战成绩：仅统计"分数达到阈值"的次数（param2=0 表示不限制分数，即统计次数）；
        /// 加好友/发消息：每次事件 +1。只处理进行中的任务（已完成即终止逻辑，避免复杂事件链）
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="goalType">参数名：goalType，类型：int，用途：目标类型（1 挑战 / 3 加好友 / 4 发消息）</param>
        /// <param name="param1">参数名：param1，类型：int，用途：事件参数（挑战ID）</param>
        /// <param name="value">参数名：value，类型：int，用途：事件值（挑战为分数；其余为增量，通常 1）</param>
        /// <returns>返回值类型：List，用途：发生变化的任务状态 (任务ID, 状态, 进度)</returns>
        public List<(int QuestId, int State, int Progress)> OnGameEvent(int playerId, int goalType, int param1, int value)
        {
            var changed = new List<(int QuestId, int State, int Progress)>();
            if (playerId <= 0) return changed;

            try
            {
                Dictionary<int, QuestConfig> configs = GetConfigMap();
                Dictionary<int, PlayerQuestRow> rows = GetPlayerQuests(playerId);

                foreach (PlayerQuestRow row in rows.Values)
                {
                    if (row.State != StateProcessing) continue;
                    if (!configs.TryGetValue(row.QuestId, out QuestConfig? config)) continue;
                    if (config.GoalType != goalType) continue;

                    int delta;
                    if (goalType == GoalTypeChallenge)
                    {
                        // 挑战ID 不匹配（0 = 任意挑战）或分数未达阈值 → 不推进
                        if (config.GoalParam1 != 0 && config.GoalParam1 != param1) continue;
                        if (value < config.GoalParam2) continue;
                        delta = 1;
                    }
                    else
                    {
                        delta = value;
                    }

                    int progress = Math.Min(row.Progress + delta, config.TargetProgress);
                    int state = progress >= config.TargetProgress ? StateFinished : StateProcessing;
                    if (progress == row.Progress && state == row.State) continue;

                    SaveProgress(playerId, row.QuestId, progress, state);
                    row.Progress = progress;
                    row.State = state;
                    changed.Add((row.QuestId, state, progress));

                    // 步骤任务达标：非收集步骤自动提交（发步骤奖励）并推进章节；收集步骤保持"待交付"由玩家交付。
                    // 推送里要带上章节与后续步骤的最新状态，客户端一次推送即可看到"第 X/Y 步"切换
                    if (config.ParentQuestId > 0 && state == StateFinished && config.GoalType != GoalTypeCollectItem)
                    {
                        _ = Submit(playerId, row.QuestId);   // 生涯统计在 Submit 内，自动提交同样计入
                        AppendChapterTree(playerId, config.ParentQuestId, configs, changed);
                        // 提交产生了生涯变化 → 成就按实时值重算，结果并入本次推送
                        changed.AddRange(OnCareerChanged(playerId, CareerRepository.ScoreQuestSubmit));
                        changed.AddRange(OnCareerChanged(playerId, CareerRepository.ScoreItemGain));
                    }
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"任务事件推进失败 [{exception.GetType().Name}]: {exception.Message}");
            }
            return changed;
        }

        /// <summary>
        /// 收集类目标的事件重算入口（物品获得/使用/丢弃后调用）
        /// 进度 = 实时持有量，因此进度可能增加（获得）也可能减少（使用/丢弃）——
        /// 减少时从"已完成"回退为"进行中"（提交时还会再校验一次，保证不会白拿奖励）
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="itemId">参数名：itemId，类型：int，用途：发生变化的物品ID</param>
        /// <returns>返回值类型：List，用途：发生变化的任务状态 (任务ID, 状态, 进度)</returns>
        public List<(int QuestId, int State, int Progress)> OnItemChanged(int playerId, int itemId)
        {
            var changed = new List<(int QuestId, int State, int Progress)>();
            if (playerId <= 0 || itemId <= 0) return changed;

            try
            {
                Dictionary<int, QuestConfig> configs = GetConfigMap();
                Dictionary<int, PlayerQuestRow> rows = GetPlayerQuests(playerId);

                // 只关心该物品相关的收集类任务；没有相关任务时不必查背包
                var related = new List<(PlayerQuestRow Row, QuestConfig Config)>();
                foreach (PlayerQuestRow row in rows.Values)
                {
                    if (row.State == StateSubmitted) continue;
                    if (!configs.TryGetValue(row.QuestId, out QuestConfig? config)) continue;
                    if (config.GoalType != GoalTypeCollectItem || config.GoalParam1 != itemId) continue;
                    related.Add((row, config));
                }
                if (related.Count == 0) return changed;

                Dictionary<int, int> totals = LoadItemTotals(playerId);
                int live = totals.TryGetValue(itemId, out int count) ? count : 0;

                foreach ((PlayerQuestRow row, QuestConfig config) in related)
                {
                    int state = live >= config.TargetProgress ? StateFinished : StateProcessing;
                    if (live == row.Progress && state == row.State) continue;

                    SaveProgress(playerId, row.QuestId, live, state);
                    row.Progress = live;
                    row.State = state;
                    changed.Add((row.QuestId, state, live));
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"任务进度重算失败 [{exception.GetType().Name}]: {exception.Message}");
            }
            return changed;
        }

        /// <summary>
        /// 成就（生涯分数类目标）的重算入口：生涯指标变化后调用
        /// 进度 = 生涯累计值（只增不减），因此状态只会"进行中 → 可提交"，不存在回退；
        /// 多阶段成就靠 prev 链由拉列表时的自动激活补位，无需在此处理
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="scoreId">参数名：scoreId，类型：int，用途：发生变化的生涯指标ID</param>
        /// <returns>返回值类型：List，用途：发生变化的任务状态 (任务ID, 状态, 进度)</returns>
        public List<(int QuestId, int State, int Progress)> OnCareerChanged(int playerId, int scoreId)
        {
            var changed = new List<(int QuestId, int State, int Progress)>();
            if (playerId <= 0 || scoreId <= 0) return changed;

            try
            {
                Dictionary<int, QuestConfig> configs = GetConfigMap();
                Dictionary<int, PlayerQuestRow> rows = GetPlayerQuests(playerId);

                var related = new List<(PlayerQuestRow Row, QuestConfig Config)>();
                foreach (PlayerQuestRow row in rows.Values)
                {
                    if (row.State == StateSubmitted) continue;
                    if (!configs.TryGetValue(row.QuestId, out QuestConfig? config)) continue;
                    if (config.GoalType != GoalTypeCareer || config.GoalParam1 != scoreId) continue;
                    related.Add((row, config));
                }
                if (related.Count == 0) return changed;

                int live = new CareerRepository().GetValue(playerId, scoreId);
                foreach ((PlayerQuestRow row, QuestConfig config) in related)
                {
                    int state = live >= config.TargetProgress ? StateFinished : StateProcessing;
                    if (live == row.Progress && state == row.State) continue;

                    SaveProgress(playerId, row.QuestId, live, state);
                    row.Progress = live;
                    row.State = state;
                    changed.Add((row.QuestId, state, live));
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"成就进度重算失败 [{exception.GetType().Name}]: {exception.Message}");
            }
            return changed;
        }

        // ──────────────────────────────
        // 章节任务（子任务表步骤）
        // ──────────────────────────────

        /// <summary>取章节任务的步骤列表（按 quest_id 升序 = 执行顺序）。</summary>
        private static List<QuestConfig> ChildrenOf(Dictionary<int, QuestConfig> configs, int chapterId)
        {
            var children = new List<QuestConfig>();
            foreach (QuestConfig config in configs.Values)
            {
                if (config.ParentQuestId == chapterId) children.Add(config);
            }
            children.Sort((left, right) => left.QuestId.CompareTo(right.QuestId));
            return children;
        }

        /// <summary>
        /// 插入"进行中"任务行（接取 / 成就自动激活 / 开启步骤共用）。
        /// 实时进度类（收集/生涯）接取时立即按实时值结算，已达标直接进入可提交
        /// </summary>
        private void InsertQuestRow(int playerId, QuestConfig config)
        {
            int state = StateProcessing;
            int progress = 0;

            if (config.GoalType == GoalTypeCollectItem)
            {
                Dictionary<int, int> totals = LoadItemTotals(playerId);
                progress = totals.TryGetValue(config.GoalParam1, out int count) ? count : 0;
                if (progress >= config.TargetProgress) state = StateFinished;
            }
            else if (config.GoalType == GoalTypeCareer)
            {
                Dictionary<int, int> career = new CareerRepository().GetValues(playerId);
                progress = career.TryGetValue(config.GoalParam1, out int value) ? value : 0;
                if (progress >= config.TargetProgress) state = StateFinished;
            }

            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlCommand command = new(
                """
                INSERT INTO player_quests (`player_id`, `quest_id`, `state`, `progress`, `finished_at`)
                VALUES (@PlayerId, @QuestId, @State, @Progress, @FinishedAt)
                """,
                connection);
            command.Parameters.AddWithValue("@PlayerId", playerId);
            command.Parameters.AddWithValue("@QuestId", config.QuestId);
            command.Parameters.AddWithValue("@State", state);
            command.Parameters.AddWithValue("@Progress", progress);
            command.Parameters.AddWithValue("@FinishedAt",
                state == StateFinished ? DateTime.UtcNow : DBNull.Value);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// 推进章节任务：重算"已提交步骤数"写回章节行，全部完成则章节进入可提交。
        /// 上层章节不自带目标逻辑，总由子任务（步骤）的提交来更新它——这就是"子任务表步骤"的 FSM 语义
        /// </summary>
        private void AdvanceChapter(int playerId, int chapterId, Dictionary<int, QuestConfig> configs)
        {
            try
            {
                List<QuestConfig> children = ChildrenOf(configs, chapterId);
                if (children.Count == 0) return;
                Dictionary<int, PlayerQuestRow> rows = GetPlayerQuests(playerId);
                if (!rows.TryGetValue(chapterId, out PlayerQuestRow? chapter)) return;
                if (chapter.State == StateSubmitted) return;

                int submitted = 0;
                foreach (QuestConfig child in children)
                {
                    if (rows.TryGetValue(child.QuestId, out PlayerQuestRow? row) && row.State == StateSubmitted)
                    {
                        submitted++;
                    }
                }

                int state = submitted >= children.Count ? StateFinished : StateProcessing;
                if (chapter.Progress == submitted && chapter.State == state) return;
                SaveProgress(playerId, chapterId, submitted, state);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"章节推进失败 [{exception.GetType().Name}]: {exception.Message}");
            }
        }

        /// <summary>
        /// 开启章节的下一个步骤：按序找第一个"未提交"的步骤，没有行则接取；
        /// 已有行且未提交说明当前步骤仍在进行，不动
        /// </summary>
        private void AcceptNextStep(int playerId, int chapterId, Dictionary<int, QuestConfig> configs)
        {
            try
            {
                Dictionary<int, PlayerQuestRow> rows = GetPlayerQuests(playerId);
                foreach (QuestConfig child in ChildrenOf(configs, chapterId))
                {
                    if (rows.TryGetValue(child.QuestId, out PlayerQuestRow? row))
                    {
                        if (row.State != StateSubmitted) return;   // 当前步骤未完成，不开启下一个
                        continue;
                    }
                    InsertQuestRow(playerId, child);
                    return;
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"开启下一步骤失败 [{exception.GetType().Name}]: {exception.Message}");
            }
        }

        /// <summary>把章节及其全部步骤的最新状态追加进推送列表（自动提交步骤后调用，一次推送让客户端看到"第 X/Y 步"切换）。</summary>
        private void AppendChapterTree(
            int playerId, int chapterId, Dictionary<int, QuestConfig> configs,
            List<(int QuestId, int State, int Progress)> changed)
        {
            try
            {
                Dictionary<int, PlayerQuestRow> rows = GetPlayerQuests(playerId);
                if (rows.TryGetValue(chapterId, out PlayerQuestRow? chapter))
                {
                    changed.Add((chapterId, chapter.State, chapter.Progress));
                }
                foreach (QuestConfig child in ChildrenOf(configs, chapterId))
                {
                    if (rows.TryGetValue(child.QuestId, out PlayerQuestRow? row))
                    {
                        changed.Add((row.QuestId, row.State, row.Progress));
                    }
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"章节状态收集失败 [{exception.GetType().Name}]: {exception.Message}");
            }
        }

        /// <summary>插入或更新任务行为"已提交"（GM 补完章节步骤用，绕过正常校验、不扣道具、不发步骤奖励）。</summary>
        private void UpsertSubmittedRow(int playerId, int questId, int progress)
        {
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlCommand command = new(
                """
                INSERT INTO player_quests (`player_id`, `quest_id`, `state`, `progress`, `submitted_at`)
                VALUES (@PlayerId, @QuestId, @State, @Progress, @Now)
                ON DUPLICATE KEY UPDATE `state` = @State, `progress` = @Progress, `submitted_at` = @Now
                """,
                connection);
            command.Parameters.AddWithValue("@PlayerId", playerId);
            command.Parameters.AddWithValue("@QuestId", questId);
            command.Parameters.AddWithValue("@State", StateSubmitted);
            command.Parameters.AddWithValue("@Progress", progress);
            command.Parameters.AddWithValue("@Now", DateTime.UtcNow);
            command.ExecuteNonQuery();
        }

        // ──────────────────────────────
        // 内部工具
        // ──────────────────────────────

        /// <summary>把任务写回"进行中"并记录当前进度（收集类交付时物品不足的回退路径）。</summary>
        private void MarkProcessing(int playerId, int questId, int progress)
        {
            try
            {
                SaveProgress(playerId, questId, progress, StateProcessing);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"任务状态回退失败 [{exception.GetType().Name}]: {exception.Message}");
            }
        }

        /// <summary>更新单个任务的进度与状态（状态切到已完成时补 finished_at）。</summary>
        private void SaveProgress(int playerId, int questId, int progress, int state)
        {
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlCommand command = new(
                """
                UPDATE player_quests
                SET `progress` = @Progress,
                    `state` = @State,
                    `finished_at` = CASE WHEN @State = 2 THEN COALESCE(`finished_at`, @Now) ELSE NULL END
                WHERE `player_id` = @PlayerId AND `quest_id` = @QuestId
                """,
                connection);
            command.Parameters.AddWithValue("@Progress", progress);
            command.Parameters.AddWithValue("@State", state);
            command.Parameters.AddWithValue("@Now", DateTime.UtcNow);
            command.Parameters.AddWithValue("@PlayerId", playerId);
            command.Parameters.AddWithValue("@QuestId", questId);
            command.ExecuteNonQuery();
        }

        /// <summary>聚合玩家各物品的持有总量（item_id → 总数；收集类任务进度的唯一数据源）。</summary>
        private Dictionary<int, int> LoadItemTotals(int playerId)
        {
            var totals = new Dictionary<int, int>();
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlCommand command = new(
                    "SELECT `item_id`, SUM(`count`) FROM player_items WHERE `player_id` = @PlayerId GROUP BY `item_id`",
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
                Console.WriteLine($"物品持有量统计失败 [{exception.GetType().Name}]: {exception.Message}");
            }
            return totals;
        }

        /// <summary>读取物品名（任务提示与奖励文案用；走 BagRepository 统一配置缓存，查不到回退成 #ID）。</summary>
        private static string ItemName(int itemId)
        {
            return BagRepository.GetConfigMap().TryGetValue(itemId, out BagRepository.ItemConfig? config)
                ? config.Name
                : $"#{itemId}";
        }

        /// <summary>读取全部物品的堆叠上限（发奖时按上限拆堆用；走 BagRepository 统一配置缓存）。</summary>
        private static Dictionary<int, int> LoadMaxStacks()
        {
            var stacks = new Dictionary<int, int>();
            foreach ((int itemId, BagRepository.ItemConfig config) in BagRepository.GetConfigMap())
            {
                stacks[itemId] = config.MaxStack;   // 缓存装载时已 Math.Max(1, …) 兜底
            }
            return stacks;
        }
    }
}
