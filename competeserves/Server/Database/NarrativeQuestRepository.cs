using System.Collections.Concurrent;
using System.Text.Json;
using MySqlConnector;

namespace CompeteServer.Database
{
    /// <summary>
    /// 叙事任务静态配置（服务端权威引擎的规则来源）
    /// 由客户端 QuestData SO 经导出工具生成 JSON、启动时种子入库（改配置 = 改 SO → 导出 → 重启服务端）
    /// </summary>
    public sealed class NarrativeQuestConfig
    {
        /// <summary>任务ID</summary>
        public int QuestId { get; init; }

        /// <summary>分类（0主线/1支线，展示用）</summary>
        public int Category { get; init; }

        /// <summary>任务标题</summary>
        public string Title { get; init; } = string.Empty;

        /// <summary>任务描述</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>接取模式（0系统派发/1手动接取）</summary>
        public int AcceptMode { get; init; }

        /// <summary>限时秒数（0=无限时）</summary>
        public float TimeLimitSec { get; init; }

        /// <summary>前置任务ID列表（全部"提交"才可接取）</summary>
        public List<int> PrereqIds { get; init; } = new();

        /// <summary>目标配置列表</summary>
        public List<NarrativeGoalConfig> Goals { get; init; } = new();

        /// <summary>提交奖励列表（提交时同一事务发放，背包满转邮件）</summary>
        public List<NarrativeQuestReward> Rewards { get; init; } = new();

        /// <summary>目标是否全部达标（进度与目标一一对应）</summary>
        public bool AllDone(List<int> progress) => AllDone(Goals, progress);

        /// <summary>静态：按目标列表判定进度是否全部达标</summary>
        public static bool AllDone(List<NarrativeGoalConfig> goals, List<int>? progress)
        {
            if (progress == null || progress.Count < goals.Count) return false;
            for (int i = 0; i < goals.Count; i++)
            {
                if (progress[i] < goals[i].Need) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 叙事任务目标配置
    /// type 对应 EQuestGoalType：对话0 / 击杀1 / 收集2 / 到达3；
    /// 对话目标用 graphName（对话图资产名）+ eventKey 匹配，其余用 targetKey
    /// </summary>
    public sealed class NarrativeGoalConfig
    {
        /// <summary>目标类型（0对话/1击杀/2收集/3到达）</summary>
        public int Type { get; init; }

        /// <summary>匹配Key（击杀=怪物Key/收集=物品Key/到达=区域Key）</summary>
        public string TargetKey { get; init; } = string.Empty;

        /// <summary>目标数量（≤0 视为 1）</summary>
        public int Need { get; init; } = 1;

        /// <summary>对话图资产名（仅对话目标）</summary>
        public string GraphName { get; init; } = string.Empty;

        /// <summary>对话事件Key（仅对话目标；选项 eventKey 或整图结束信号 GraphFinished）</summary>
        public string EventKey { get; init; } = string.Empty;

        /// <summary>目标描述（展示用）</summary>
        public string Description { get; init; } = string.Empty;
    }

    /// <summary>
    /// 叙事任务奖励配置（提交时发放）
    /// 物品静态属性（名称/图标/堆叠上限）一律查 item_config，本结构只存规则
    /// </summary>
    public sealed class NarrativeQuestReward
    {
        /// <summary>物品ID（对应 item_config.item_id；装备类逐件建实例）</summary>
        public int ItemId { get; init; }

        /// <summary>数量（≤0 视为不发）</summary>
        public int Count { get; init; }
    }

    /// <summary>
    /// 玩家叙事任务状态行（mm_player_quests）
    /// 对应 Mm-DialogSystem 框架的 QuestSaveData：任务ID/状态/各目标进度/接取时间/限时
    /// </summary>
    public sealed class MmQuestRow
    {
        /// <summary>任务ID（客户端 QuestData 配置侧定义）</summary>
        public int QuestId { get; set; }

        /// <summary>任务状态（EQuestState：未激活0/可用1/执行中2/提交3/失败4）</summary>
        public int State { get; set; }

        /// <summary>各目标进度列表（索引与配置 goals 对应）</summary>
        public List<int> Progress { get; set; } = new();

        /// <summary>接取时间（UTC 毫秒，展示用）</summary>
        public float AcceptedAt { get; set; }

        /// <summary>限时绝对截止时间（UTC 毫秒，0=无限时；权威值，由服务端接取时计算）</summary>
        public long ExpireTimeMs { get; set; }
    }

    /// <summary>
    /// 叙事任务仓库 + 服务端权威引擎（Mm-DialogSystem 框架任务系统的服务器侧）
    /// 架构（鸣潮式）：客户端只报事件（2705）/发接取提交请求，全部规则由本引擎裁决；
    /// 数据分层：mm_quest_config = 静态规则（导出工具种子）；
    ///           玩家态 = 进程内缓存（读全走内存、变更写穿 MySQL 全量快照，空闲逐出）；
    /// 与 2501 服务端权威任务体系（quest_config/player_quests）并行、互不影响
    /// </summary>
    public sealed class NarrativeQuestRepository
    {
        // ──────────────────────────────
        // 常量与护栏（客户端权威信任边界已升级为服务端记账：事件仍来自客户端，但进度/状态/限时全由服务端裁决）
        // ──────────────────────────────

        /// <summary>单玩家最多存档的任务条数（超过整单拒绝，防异常客户端写垃圾行）</summary>
        public const int MaxQuestRows = 256;

        /// <summary>单任务最多目标数（progress 列表长度上限）</summary>
        public const int MaxGoalCount = 16;

        /// <summary>单次事件上报的最大数量（击杀/收集可批量，超出封顶）</summary>
        public const int MaxEventCount = 1000;

        /// <summary>任务状态（与客户端 EQuestState 对齐：未激活0/可用1/执行中2/提交3/失败4）</summary>
        public const int StateInactive = 0, StateAvailable = 1, StateActive = 2, StateSubmitted = 3, StateFailed = 4;

        /// <summary>玩家态缓存的空闲逐出时间（未活跃则丢弃，下次访问重新从库装载）</summary>
        private static readonly TimeSpan CacheIdleEviction = TimeSpan.FromMinutes(5);

        /// <summary>静态配置表只读快照（启动种子后原子发布；改配置需导出+重启，不走 TTL）</summary>
        private static Dictionary<int, NarrativeQuestConfig>? configMap;

        /// <summary>玩家态进程内缓存：键=玩家ID，值=条目（含锁与行列表）</summary>
        private static readonly ConcurrentDictionary<int, PlayerCacheEntry> PlayerCache = new();

        /// <summary>当前 UTC 毫秒（uint 环绕安全，服务器进程生命周期内单调）</summary>
        private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>配置 JSON 反序列化选项（导出工具 Json.NET 写 PascalCase 字段名，这里大小写不敏感兜底）</summary>
        private static readonly JsonSerializerOptions JsonCaseInsensitive = new() { PropertyNameCaseInsensitive = true };

        /// <summary>
        /// 玩家缓存条目：所有对行列表的读写都在 Gate 锁内（会话消息在线程池上并发处理）
        /// </summary>
        private sealed class PlayerCacheEntry
        {
            /// <summary>条目互斥锁（串行化该玩家的任务操作）</summary>
            public readonly object Gate = new();

            /// <summary>该玩家的全部任务行（含未激活任务的基线行；无行的任务不入列表）</summary>
            public List<MmQuestRow> Rows = new();

            /// <summary>最近一次访问时间（用于空闲逐出）</summary>
            public DateTime LastAccess = DateTime.UtcNow;
        }

        // ──────────────────────────────
        // 建表 / 配置种子
        // ──────────────────────────────

        /// <summary>
        /// 幂等建表（与 Migrations\009/010 等价，已有表无副作用；含 mm_player_quests 补列）
        /// </summary>
        public void EnsureTable()
        {
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using (MySqlCommand command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS `mm_player_quests` (
                      `player_id`      INT          NOT NULL COMMENT '玩家ID = accounts.Id',
                      `quest_id`       INT          NOT NULL COMMENT '任务ID（客户端 QuestData 配置侧定义）',
                      `state`          TINYINT      NOT NULL DEFAULT 0 COMMENT '任务状态（EQuestState：未激活0/可用1/执行中2/提交3/失败4）',
                      `progress_json`  TEXT         NOT NULL COMMENT '各目标进度JSON数组 [1,0,3]（与配置 goals 索引对应）',
                      `accepted_at`    FLOAT        NOT NULL DEFAULT 0 COMMENT '接取时间（UTC毫秒，展示用）',
                      `expire_time_ms` BIGINT       NOT NULL DEFAULT 0 COMMENT '限时绝对截止时间（UTC毫秒，0=无限时；服务端权威）',
                      `updated_at`     TIMESTAMP    NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP COMMENT '最近一次快照时间',
                      PRIMARY KEY (`player_id`, `quest_id`)
                    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='玩家叙事任务状态（服务端权威引擎持有，客户端只读镜像）';
                    """;
                command.ExecuteNonQuery();
            }
            using (MySqlCommand command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS `mm_quest_config` (
                      `quest_id`       INT          NOT NULL COMMENT '任务ID（客户端 QuestData 配置侧定义）',
                      `category`       TINYINT      NOT NULL DEFAULT 1 COMMENT '分类（0主线/1支线，展示用）',
                      `title`          VARCHAR(128) NOT NULL DEFAULT '' COMMENT '任务标题（导出自 QuestData.title）',
                      `description`    VARCHAR(512) NOT NULL DEFAULT '' COMMENT '任务描述',
                      `accept_mode`    TINYINT      NOT NULL DEFAULT 1 COMMENT '接取模式（0系统派发/1手动接取）',
                      `time_limit_sec` FLOAT        NOT NULL DEFAULT 0 COMMENT '限时秒数（0=无限时）',
                      `prereq_csv`     VARCHAR(128) NOT NULL DEFAULT '' COMMENT '前置任务ID列表（逗号分隔，空=无前置）',
                      `goals_json`     TEXT         NOT NULL COMMENT '目标配置JSON数组',
                      `rewards_json`   TEXT         NOT NULL COMMENT '提交奖励JSON数组（[{ItemId,Count}]）',
                      `updated_at`     TIMESTAMP    NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP COMMENT '最近导入时间',
                      PRIMARY KEY (`quest_id`)
                    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='叙事任务静态配置（服务端权威引擎的规则来源，由导出工具种子）';
                    """;
                command.ExecuteNonQuery();
            }

            // 已有表的升级路径：补 rewards_json 列（MySQL 无 ADD COLUMN IF NOT EXISTS，查 information_schema）
            using (MySqlCommand check = connection.CreateCommand())
            {
                check.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'mm_quest_config' AND column_name = 'rewards_json'";
                if (Convert.ToInt64(check.ExecuteScalar() ?? 0L) == 0)
                {
                    using MySqlCommand alter = connection.CreateCommand();
                    alter.CommandText = "ALTER TABLE `mm_quest_config` ADD COLUMN `rewards_json` TEXT NOT NULL COMMENT '提交奖励JSON数组（[{ItemId,Count}]）' AFTER `goals_json`";
                    alter.ExecuteNonQuery();
                }
            }

            // 已有表的升级路径：补 expire_time_ms 列（MySQL 无 ADD COLUMN IF NOT EXISTS，查 information_schema）
            using (MySqlCommand check = connection.CreateCommand())
            {
                check.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'mm_player_quests' AND column_name = 'expire_time_ms'";
                if (Convert.ToInt64(check.ExecuteScalar() ?? 0L) == 0)
                {
                    using MySqlCommand alter = connection.CreateCommand();
                    alter.CommandText = "ALTER TABLE `mm_player_quests` ADD COLUMN `expire_time_ms` BIGINT NOT NULL DEFAULT 0 COMMENT '限时绝对截止时间（UTC毫秒，0=无限时；服务端权威）' AFTER `accepted_at`";
                    alter.ExecuteNonQuery();
                }
            }
        }

        /// <summary>
        /// 从导出工具生成的 JSON 种子配置（幂等 upsert），并把全表载入静态只读缓存。
        /// 文件缺失只告警不阻断（服务器照常启动，直到有人导出配置前该系统无可用任务）。
        /// </summary>
        /// <param name="jsonPath">参数名：jsonPath，类型：string，用途：配置 JSON 文件路径（Config/mm_quest_config.json）</param>
        public void LoadAndSeedConfigs(string jsonPath)
        {
            if (!File.Exists(jsonPath))
            {
                Console.WriteLine($"叙事任务配置文件不存在（{jsonPath}），跳过种子。请在 Unity 执行 Tools/Mm叙事任务/导出配置到服务器 后重启。");
                configMap = new Dictionary<int, NarrativeQuestConfig>();
                return;
            }

            ExportFile export = JsonSerializer.Deserialize<ExportFile>(
                File.ReadAllText(jsonPath),
                JsonCaseInsensitive) ?? new ExportFile();
            if (export.Quests.Count > 4096)
                throw new InvalidOperationException($"叙事任务配置条数异常（{export.Quests.Count}），拒绝种子");

            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            foreach (ExportQuest quest in export.Quests)
            {
                if (quest.Goals.Count > MaxGoalCount)
                    throw new InvalidOperationException($"任务{quest.QuestId}目标数超限（{quest.Goals.Count} > {MaxGoalCount}）");

                using MySqlCommand upsert = connection.CreateCommand();
                upsert.CommandText = """
                    INSERT INTO mm_quest_config (quest_id, category, title, description, accept_mode, time_limit_sec, prereq_csv, goals_json, rewards_json)
                    VALUES (@questId, @category, @title, @description, @acceptMode, @timeLimit, @prereq, @goals, @rewards)
                    ON DUPLICATE KEY UPDATE
                      category = VALUES(category), title = VALUES(title), description = VALUES(description),
                      accept_mode = VALUES(accept_mode), time_limit_sec = VALUES(time_limit_sec),
                      prereq_csv = VALUES(prereq_csv), goals_json = VALUES(goals_json), rewards_json = VALUES(rewards_json)
                    """;
                upsert.Parameters.AddWithValue("@questId", quest.QuestId);
                upsert.Parameters.AddWithValue("@category", quest.Category);
                upsert.Parameters.AddWithValue("@title", quest.Title ?? string.Empty);
                upsert.Parameters.AddWithValue("@description", quest.Description ?? string.Empty);
                upsert.Parameters.AddWithValue("@acceptMode", quest.AcceptMode);
                upsert.Parameters.AddWithValue("@timeLimit", quest.TimeLimitSec);
                upsert.Parameters.AddWithValue("@prereq", string.Join(",", quest.PrereqIds ?? new List<int>()));
                upsert.Parameters.AddWithValue("@goals", JsonSerializer.Serialize(quest.Goals));
                upsert.Parameters.AddWithValue("@rewards", JsonSerializer.Serialize(quest.Rewards ?? new List<ExportReward>()));
                upsert.ExecuteNonQuery();
            }

            // 静态只读快照原子发布（含 JSON 未给出的历史行清理语义：本种子只 upsert 不删除，历史任务保留）
            Dictionary<int, NarrativeQuestConfig> map = export.Quests
                .Select(q => new NarrativeQuestConfig
                {
                    QuestId = q.QuestId,
                    Category = q.Category,
                    Title = q.Title ?? string.Empty,
                    Description = q.Description ?? string.Empty,
                    AcceptMode = q.AcceptMode,
                    TimeLimitSec = q.TimeLimitSec,
                    PrereqIds = q.PrereqIds ?? new List<int>(),
                    Goals = (q.Goals ?? new List<ExportGoal>()).Select(g => new NarrativeGoalConfig
                    {
                        Type = g.Type,
                        TargetKey = g.TargetKey ?? string.Empty,
                        Need = g.Count > 0 ? g.Count : 1,
                        GraphName = g.GraphName ?? string.Empty,
                        EventKey = g.EventKey ?? string.Empty,
                        Description = g.Description ?? string.Empty,
                    }).ToList(),
                    Rewards = (q.Rewards ?? new List<ExportReward>())
                        .Where(r => r.ItemId > 0 && r.Count > 0)
                        .Select(r => new NarrativeQuestReward { ItemId = r.ItemId, Count = r.Count })
                        .ToList(),
                })
                .ToDictionary(c => c.QuestId);

            configMap = map;
            Console.WriteLine($"叙事任务配置就绪：{map.Count} 条（来源 {Path.GetFileName(jsonPath)}，改配置需重新导出+重启）");
        }

        /// <summary>导出文件的 JSON 形状（与 Unity 导出工具的序列化结构保持一致）</summary>
        private sealed class ExportFile
        {
            public List<ExportQuest> Quests { get; set; } = new();
        }

        /// <summary>单个导出任务</summary>
        private sealed class ExportQuest
        {
            public int QuestId { get; set; }
            public int Category { get; set; }
            public string Title { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public int AcceptMode { get; set; }
            public float TimeLimitSec { get; set; }
            public List<int> PrereqIds { get; set; } = new();
            public List<ExportGoal> Goals { get; set; } = new();
            public List<ExportReward> Rewards { get; set; } = new();
        }

        /// <summary>单个导出奖励</summary>
        private sealed class ExportReward
        {
            public int ItemId { get; set; }
            public int Count { get; set; }
        }

        /// <summary>单个导出目标</summary>
        private sealed class ExportGoal
        {
            public int Type { get; set; }
            public string TargetKey { get; set; } = string.Empty;
            public int Count { get; set; } = 1;
            public string GraphName { get; set; } = string.Empty;
            public string EventKey { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
        }

        // ──────────────────────────────
        // 玩家态缓存（读全走内存，变更写穿 MySQL）
        // ──────────────────────────────

        /// <summary>
        /// 取玩家缓存条目（首次访问从库装载；顺带清理空闲条目）
        /// </summary>
        private PlayerCacheEntry GetEntry(int playerId)
        {
            return PlayerCache.GetOrAdd(playerId, _ => LoadEntryFromDatabase(playerId));
        }

        /// <summary>从数据库装载一个玩家的全部任务行（新玩家=空列表）</summary>
        private PlayerCacheEntry LoadEntryFromDatabase(int playerId)
        {
            PlayerCacheEntry entry = new();
            entry.Rows = LoadRows(playerId);

            // 轻量清理：空闲超时的条目直接丢弃（下次访问重新装载）
            foreach (KeyValuePair<int, PlayerCacheEntry> pair in PlayerCache)
            {
                if (pair.Key == playerId) continue;
                lock (pair.Value.Gate)
                {
                    if (DateTime.UtcNow - pair.Value.LastAccess > CacheIdleEviction)
                        PlayerCache.TryRemove(pair.Key, out _);
                }
            }
            return entry;
        }

        /// <summary>直查数据库：该玩家全部任务行</summary>
        private List<MmQuestRow> LoadRows(int playerId)
        {
            List<MmQuestRow> rows = new();
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlCommand command = connection.CreateCommand();
            command.CommandText = "SELECT quest_id, state, progress_json, accepted_at, expire_time_ms FROM mm_player_quests WHERE player_id = @playerId";
            command.Parameters.AddWithValue("@playerId", playerId);

            using MySqlDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                List<int> progress = new();
                string json = reader.IsDBNull(2) ? "[]" : reader.GetString(2);
                foreach (int value in JsonSerializer.Deserialize<List<int>>(json) ?? new List<int>())
                    progress.Add(value);

                rows.Add(new MmQuestRow
                {
                    QuestId = reader.GetInt32(0),
                    State = reader.GetInt32(1),
                    Progress = progress,
                    AcceptedAt = reader.GetFloat(3),
                    ExpireTimeMs = reader.GetInt64(4),
                });
            }
            return rows;
        }

        /// <summary>变更写穿：把该玩家全部行全量落库（单事务 DELETE+INSERT，快照替换幂等）</summary>
        private void Flush(int playerId, List<MmQuestRow> rows)
        {
            if (rows.Count > MaxQuestRows) rows = rows.GetRange(0, MaxQuestRows);   // 护栏：条目封顶

            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlTransaction transaction = connection.BeginTransaction();
            try
            {
                using (MySqlCommand delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM mm_player_quests WHERE player_id = @playerId";
                    delete.Parameters.AddWithValue("@playerId", playerId);
                    delete.ExecuteNonQuery();
                }

                foreach (MmQuestRow row in rows)
                {
                    using MySqlCommand insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO mm_player_quests (player_id, quest_id, state, progress_json, accepted_at, expire_time_ms) VALUES (@playerId, @questId, @state, @progressJson, @acceptedAt, @expireMs)";
                    insert.Parameters.AddWithValue("@playerId", playerId);
                    insert.Parameters.AddWithValue("@questId", row.QuestId);
                    insert.Parameters.AddWithValue("@state", row.State);
                    insert.Parameters.AddWithValue("@progressJson", JsonSerializer.Serialize(row.Progress));
                    insert.Parameters.AddWithValue("@acceptedAt", row.AcceptedAt);
                    insert.Parameters.AddWithValue("@expireMs", row.ExpireTimeMs);
                    insert.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        /// <summary>惰性限时判定：执行中且已过期 → 失败。返回是否有变更（需要落库）</summary>
        private static bool SweepExpired(List<MmQuestRow> rows, long now)
        {
            bool changed = false;
            foreach (MmQuestRow row in rows)
            {
                if (row.State != StateActive || row.ExpireTimeMs <= 0 || now <= row.ExpireTimeMs) continue;
                row.State = StateFailed;
                changed = true;
            }
            return changed;
        }

        // ──────────────────────────────
        // 引擎操作（全部在条目锁内裁决，返回快照供响应/推送）
        // ──────────────────────────────

        /// <summary>
        /// 拉取玩家全量状态快照（登录/接取/提交响应/推送共用；先做惰性限时判定）
        /// </summary>
        public List<MmQuestRow> GetSnapshot(int playerId)
        {
            PlayerCacheEntry entry = GetEntry(playerId);
            lock (entry.Gate)
            {
                entry.LastAccess = DateTime.UtcNow;
                if (SweepExpired(entry.Rows, NowMs))
                    Flush(playerId, entry.Rows);
                return entry.Rows;
            }
        }

        /// <summary>
        /// 接取任务：服务端校验 配置存在 + 前置全部提交 + 未过期，通过后置执行中并落库。
        /// 框架语义保留：提交/失败的任务可重接（进度清零重新开始）
        /// </summary>
        public (bool Success, string Message, List<MmQuestRow> Snapshot) Accept(int playerId, int questId)
        {
            NarrativeQuestConfig? config = GetConfig(questId);
            if (config == null)
                return (false, "任务不存在（请先在 Unity 导出配置并重启服务端）", GetSnapshot(playerId));

            PlayerCacheEntry entry = GetEntry(playerId);
            lock (entry.Gate)
            {
                entry.LastAccess = DateTime.UtcNow;
                SweepExpired(entry.Rows, NowMs);

                MmQuestRow? row = entry.Rows.Find(r => r.QuestId == questId);
                if (row != null && row.State == StateActive)
                    return (false, "任务已在进行中", entry.Rows);

                // 前置任务链校验（服务端权威：客户端无法绕过）
                Dictionary<int, NarrativeQuestConfig> map = configMap ?? new Dictionary<int, NarrativeQuestConfig>();
                foreach (int prereqId in config.PrereqIds)
                {
                    MmQuestRow? prereq = entry.Rows.Find(r => r.QuestId == prereqId);
                    if (prereq == null || prereq.State != StateSubmitted)
                    {
                        string prereqName = map.TryGetValue(prereqId, out NarrativeQuestConfig? c) ? c.Title : prereqId.ToString();
                        return (false, $"需要先完成任务【{prereqName}】", entry.Rows);
                    }
                }

                long now = NowMs;
                if (row == null)
                {
                    row = new MmQuestRow { QuestId = questId };
                    entry.Rows.Add(row);
                }
                row.State = StateActive;
                row.Progress = new List<int>(new int[config.Goals.Count]);   // 进度清零
                row.AcceptedAt = now;
                row.ExpireTimeMs = config.TimeLimitSec > 0 ? now + (long)(config.TimeLimitSec * 1000f) : 0;

                Flush(playerId, entry.Rows);
                return (true, "OK", entry.Rows);
            }
        }

        /// <summary>
        /// 提交任务：服务端校验 执行中 + 全目标达标 + 未过期，通过后置提交、发放奖励并落库。
        /// 奖励发放与状态落库在同一事务内（含背包主行锁 / 任务行锁），背包放不下的奖励转存邮箱。
        /// </summary>
        /// <returns>返回值类型：元组，用途：成功标志 / 提示文案 / 操作后的全量快照 / 实际发放的物品件数</returns>
        public (bool Success, string Message, List<MmQuestRow> Snapshot, int GrantedUnits) Submit(int playerId, int questId)
        {
            NarrativeQuestConfig? config = GetConfig(questId);
            if (config == null)
                return (false, "任务不存在", GetSnapshot(playerId), 0);

            PlayerCacheEntry entry = GetEntry(playerId);
            lock (entry.Gate)
            {
                entry.LastAccess = DateTime.UtcNow;
                SweepExpired(entry.Rows, NowMs);

                MmQuestRow? row = entry.Rows.Find(r => r.QuestId == questId);
                if (row == null || row.State != StateActive)
                    return (false, "任务未在进行中", entry.Rows, 0);
                if (!NarrativeQuestConfig.AllDone(config.Goals, row.Progress))
                    return (false, "目标尚未完成", entry.Rows, 0);

                (bool granted, string error, int units) = GrantRewardsAndSubmit(playerId, questId, config);
                if (!granted)
                    return (false, error, entry.Rows, 0);

                row.State = StateSubmitted;
                return (true, "OK", entry.Rows, units);
            }
        }

        /// <summary>
        /// 放弃任务：执行中 → 未激活、进度清零（客户端刷新可接取后即可重接），落库后返回快照
        /// </summary>
        public (bool Success, string Message, List<MmQuestRow> Snapshot) Abandon(int playerId, int questId)
        {
            PlayerCacheEntry entry = GetEntry(playerId);
            lock (entry.Gate)
            {
                entry.LastAccess = DateTime.UtcNow;
                SweepExpired(entry.Rows, NowMs);

                MmQuestRow? row = entry.Rows.Find(r => r.QuestId == questId);
                if (row == null || row.State == StateInactive)
                    return (false, "任务未接取", entry.Rows);
                if (row.State != StateActive)
                    return (false, "任务已提交，不能放弃", entry.Rows);

                row.State = StateInactive;
                row.Progress = new List<int>();
                row.ExpireTimeMs = 0;
                Flush(playerId, entry.Rows);
                return (true, "OK", entry.Rows);
            }
        }

        /// <summary>
        /// 发奖 + 置提交（单事务）：锁背包主行 → 锁任务行校验状态 → 逐项入包 → 写回槽位 → 更新任务状态 → 满包转邮件。
        /// 任一步失败整单回滚（不会出现"发了奖但任务没提交"或反之）。
        /// </summary>
        private (bool Success, string Error, int GrantedUnits) GrantRewardsAndSubmit(
            int playerId, int questId, NarrativeQuestConfig config)
        {
            using IDisposable writeLock = PlayerWriteLock.Acquire(playerId);
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlTransaction transaction = connection.BeginTransaction();

                BagRepository.LockPlayerRow(connection, transaction, playerId);

                // 任务行锁 + 状态复核：内存锁只在本进程内有效，跨进程/重启后由这里兜底（防重复发奖）
                using (MySqlCommand select = new(
                    "SELECT `state` FROM mm_player_quests WHERE `player_id` = @PlayerId AND `quest_id` = @QuestId FOR UPDATE",
                    connection, transaction))
                {
                    select.Parameters.AddWithValue("@PlayerId", playerId);
                    select.Parameters.AddWithValue("@QuestId", questId);
                    object? state = select.ExecuteScalar();
                    if (state == null)
                    {
                        transaction.Rollback();
                        return (false, "任务未在进行中", 0);
                    }
                    if (Convert.ToInt32(state) != StateActive)
                    {
                        transaction.Rollback();
                        return (false, "任务已提交", 0);
                    }
                }

                int grantedUnits = 0;
                if (config.Rewards.Count > 0)
                {
                    BagSlot[] slots = BagRepository.LoadSlotsForUpdate(connection, transaction, playerId);
                    Dictionary<int, int> maxStacks = LoadMaxStacks();
                    var overflow = new List<(int ItemId, int Count)>();

                    foreach (NarrativeQuestReward reward in config.Rewards)
                    {
                        if (reward.ItemId <= 0 || reward.Count <= 0) continue;

                        int maxStack = maxStacks.TryGetValue(reward.ItemId, out int stack) ? Math.Max(1, stack) : 1;
                        int remaining = BagRepository.GrantIntoSlots(
                            connection, transaction, playerId, ref slots, reward.ItemId, reward.Count, maxStack);

                        grantedUnits += reward.Count - remaining;
                        if (remaining > 0) overflow.Add((reward.ItemId, remaining));
                    }

                    BagRepository.SaveSlots(connection, transaction, playerId, slots);

                    // 背包放不下的奖励同事务转存邮箱（宁可晚拿，不能凭空消失）
                    if (overflow.Count > 0)
                    {
                        MailRepository.InsertOverflowMail(connection, transaction, playerId,
                            "任务奖励转入邮箱",
                            $"背包空间不足，【{config.Title}】的部分奖励已转入邮箱，请清理背包后领取。",
                            overflow);
                    }
                }

                using (MySqlCommand update = new(
                    "UPDATE mm_player_quests SET `state` = @Submitted WHERE `player_id` = @PlayerId AND `quest_id` = @QuestId AND `state` = @Active",
                    connection, transaction))
                {
                    update.Parameters.AddWithValue("@Submitted", StateSubmitted);
                    update.Parameters.AddWithValue("@Active", StateActive);
                    update.Parameters.AddWithValue("@PlayerId", playerId);
                    update.Parameters.AddWithValue("@QuestId", questId);
                    if (update.ExecuteNonQuery() == 0)
                    {
                        transaction.Rollback();
                        return (false, "任务已提交", 0);
                    }
                }

                transaction.Commit();
                return (true, string.Empty, grantedUnits);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"叙事任务提交失败 [{exception.GetType().Name}]: {exception.Message}");
                return (false, "提交失败，请稍后再试", 0);
            }
        }

        /// <summary>读取全部物品的堆叠上限（发奖时按上限拆堆用；走 BagRepository 统一配置缓存）。</summary>
        private static Dictionary<int, int> LoadMaxStacks()
        {
            var stacks = new Dictionary<int, int>();
            foreach ((int itemId, BagRepository.ItemConfig item) in BagRepository.GetConfigMap())
            {
                stacks[itemId] = item.MaxStack;
            }
            return stacks;
        }

        /// <summary>
        /// 玩法事件推进：遍历执行中任务匹配目标（击杀/收集按 targetKey+数量、到达按 zoneKey、对话按 graphName+eventKey），
        /// 进度封顶 need；有变更则落库并返回 true（调用方决定是否推送 2710）
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID（会话绑定）</param>
        /// <param name="eventType">参数名：eventType，类型：int，用途：事件类型（1击杀/2收集/3到达/4对话，与 EQuestGoalType 错位：对话走4）</param>
        /// <param name="targetKey">参数名：targetKey，类型：string，用途：击杀/收集/到达的匹配Key</param>
        /// <param name="count">参数名：count，类型：int，用途：数量（已在上层封顶）</param>
        /// <param name="graphName">参数名：graphName，类型：string，用途：对话图资产名（仅对话事件）</param>
        /// <param name="eventKey">参数名：eventKey，类型：string，用途：对话事件Key（仅对话事件）</param>
        public bool OnGameEvent(int playerId, int eventType, string targetKey, int count, string graphName, string eventKey)
        {
            Dictionary<int, NarrativeQuestConfig> map = configMap;
            if (map == null || map.Count == 0) return false;

            PlayerCacheEntry entry = GetEntry(playerId);
            lock (entry.Gate)
            {
                entry.LastAccess = DateTime.UtcNow;
                bool expired = SweepExpired(entry.Rows, NowMs);
                bool advanced = false;

                foreach (MmQuestRow row in entry.Rows)
                {
                    if (row.State != StateActive) continue;
                    if (!map.TryGetValue(row.QuestId, out NarrativeQuestConfig? config)) continue;

                    for (int i = 0; i < config.Goals.Count && i < row.Progress.Count; i++)
                    {
                        NarrativeGoalConfig goal = config.Goals[i];
                        if (row.Progress[i] >= goal.Need) continue;
                        if (!GoalMatches(goal, eventType, targetKey, graphName, eventKey)) continue;

                        int delta = eventType == 3 ? 1 : count;   // 到达事件固定 +1
                        row.Progress[i] = Math.Min(row.Progress[i] + delta, goal.Need);
                        advanced = true;
                    }
                }

                if (expired || advanced)
                    Flush(playerId, entry.Rows);
                return advanced;
            }
        }

        /// <summary>目标与事件匹配判定（服务端权威的规则核心）</summary>
        private static bool GoalMatches(NarrativeGoalConfig goal, int eventType, string targetKey, string graphName, string eventKey)
        {
            return goal.Type switch
            {
                0 => eventType == 4 && goal.GraphName == graphName && goal.EventKey == eventKey,   // 对话
                1 => eventType == 1 && goal.TargetKey == targetKey,                                 // 击杀
                2 => eventType == 2 && goal.TargetKey == targetKey,                                 // 收集
                3 => eventType == 3 && goal.TargetKey == targetKey,                                 // 到达
                _ => false,
            };
        }

        /// <summary>查任务配置（静态只读快照；未种子时为空表）</summary>
        public NarrativeQuestConfig? GetConfig(int questId)
        {
            Dictionary<int, NarrativeQuestConfig>? map = configMap;
            return map != null && map.TryGetValue(questId, out NarrativeQuestConfig? config) ? config : null;
        }
    }
}
