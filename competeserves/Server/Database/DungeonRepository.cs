using System.Collections.Concurrent;

using MySqlConnector;

namespace CompeteServer.Database
{
    /// <summary>
    /// 副本掉落仓库类
    /// 掉落的"真相"分两层：
    ///   ① dungeon_drop_rule 掉落规则表（静态配置：哪种敌人掉什么、概率多少、数量区间）——掉什么由服务端说了算；
    ///   ② 世界掉落令牌账本（内存 ConcurrentDictionary：形态B击杀后先生成 60 秒限时令牌，物品尚未入包，
    ///     玩家走近掉落物发起拾取时凭令牌入包；过期即作废，令牌归属绑定玩家、不可冒领）。
    /// 形态A（鸣潮式直接入包）不走令牌账本：击杀回执时直接 Grant 入包。
    /// 表结构见 Migrations\011_AddDungeon.sql（EnsureTable 提供幂等建表兜底）
    /// </summary>
    public class DungeonRepository
    {
        // ──────────────────────────────
        // 常量（与客户端 DungeonManager 保持一致）
        // ──────────────────────────────

        /// <summary>直接入包型敌人（击杀 → 服务端 roll → 直接进背包，鸣潮式）</summary>
        public const int EnemyTypeDirect = 1;

        /// <summary>世界掉落型敌人（击杀 → 服务端 roll → 限时令牌 → 靠近拾取入包）</summary>
        public const int EnemyTypeWorld = 2;

        /// <summary>令牌有效期（毫秒）：掉落物在世界里存在的时间，过期作废</summary>
        private const long TokenTtlMs = 60_000;

        /// <summary>每玩家待拾取令牌上限（防刷屏/防内存滥用，超出则拒绝新的击杀上报）</summary>
        private const int MaxPendingPerPlayer = 16;

        /// <summary>单次击杀最多掉落的物品种类数（控制回执条目与场面数量）</summary>
        private const int MaxDropsPerKill = 3;

        // ──────────────────────────────
        // 初始化（服务端启动时调用）
        // ──────────────────────────────

        /// <summary>幂等建表：与 Migrations\011_AddDungeon.sql 等价，表已存在则无副作用。</summary>
        public void EnsureTable()
        {
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlCommand command = new(
                """
                CREATE TABLE IF NOT EXISTS `dungeon_drop_rule` (
                  `id`         INT          NOT NULL AUTO_INCREMENT,
                  `enemy_type` INT          NOT NULL,
                  `item_id`    INT          NOT NULL,
                  `chance`     FLOAT        NOT NULL DEFAULT 0.5,
                  `min_count`  INT          NOT NULL DEFAULT 1,
                  `max_count`  INT          NOT NULL DEFAULT 1,
                  `enabled`    TINYINT      NOT NULL DEFAULT 1,
                  PRIMARY KEY (`id`),
                  UNIQUE KEY `uk_type_item` (`enemy_type`, `item_id`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
                """,
                connection);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// 幂等种子：写入掉落规则（INSERT IGNORE，已存在的 (enemy_type, item_id) 不覆盖，方便手工改库调概率）。
        /// 规则按"每行独立判定"执行：命中 chance 就掉 min~max 个；单次击杀最多 MaxDropsPerKill 种。
        /// </summary>
        public void SeedDropRules()
        {
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlCommand command = new(
                """
                INSERT IGNORE INTO `dungeon_drop_rule`
                  (`enemy_type`, `item_id`, `chance`, `min_count`, `max_count`, `enabled`)
                VALUES
                  (1, 1003, 0.65, 1, 2, 1),   -- A型·拾金鼠：铁矿
                  (1, 1004, 0.40, 1, 2, 1),   -- A型·拾金鼠：铜矿
                  (1, 1001, 0.15, 1, 1, 1),   -- A型·拾金鼠：治疗药剂
                  (2, 1005, 0.55, 1, 3, 1),   -- B型·藏宝龟：银矿
                  (2, 1006, 0.20, 1, 2, 1),   -- B型·藏宝龟：金矿
                  (2, 1007, 0.35, 1, 1, 1)    -- B型·藏宝龟：强化石
                """,
                connection);
            command.ExecuteNonQuery();
            InvalidateRuleCache();   // 种子写完后让缓存重建（与 QuestRepository 同口径）
        }

        // ──────────────────────────────
        // 掉落规则（静态配置，10 秒 TTL 缓存：手工改库最迟 10 秒生效）
        // ──────────────────────────────

        /// <summary>掉落规则行</summary>
        public sealed record DropRule(int EnemyType, int ItemId, double Chance, int MinCount, int MaxCount);

        /// <summary>规则缓存（原子引用发布 + 不可变快照，会话消息在线程池上并发处理）</summary>
        private static List<DropRule>? ruleCache;

        /// <summary>缓存发布时间（0 = 失效）</summary>
        private static long ruleCacheTick;

        /// <summary>缓存有效期</summary>
        private const long RuleCacheTtlMs = 10_000;

        /// <summary>取全部启用规则（命中缓存直接返回，过期则重读库并原子发布；空结果不缓存，数据库故障时不把"空"钉死 10 秒）。</summary>
        private static List<DropRule> GetRules()
        {
            List<DropRule>? cache = ruleCache;
            long tick = Interlocked.Read(ref ruleCacheTick);
            if (cache != null && tick > 0 && Environment.TickCount64 - tick < RuleCacheTtlMs)
            {
                return cache;
            }

            var fresh = new List<DropRule>();
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlCommand command = new(
                    "SELECT `enemy_type`, `item_id`, `chance`, `min_count`, `max_count` FROM `dungeon_drop_rule` WHERE `enabled` = 1",
                    connection);
                using MySqlDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    fresh.Add(new DropRule(reader.GetInt32(0), reader.GetInt32(1), reader.GetFloat(2), reader.GetInt32(3), reader.GetInt32(4)));
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"掉落规则读取失败 [{exception.GetType().Name}]: {exception.Message}");
            }

            if (fresh.Count > 0)
            {
                ruleCache = fresh;
                Interlocked.Exchange(ref ruleCacheTick, Environment.TickCount64);
            }
            return fresh;
        }

        /// <summary>缓存失效（手工改库/种子写入后调用，下次读取重建）。</summary>
        public static void InvalidateRuleCache()
        {
            ruleCache = null;
            Interlocked.Exchange(ref ruleCacheTick, 0);
        }

        // ──────────────────────────────
        // 掉落裁决（击杀回执时调用）
        // ──────────────────────────────

        /// <summary>
        /// 按掉落规则 roll 本次击杀掉落的方法。
        /// 每行规则独立判定：命中 chance 掉 min~max 个；总种类数封顶 MaxDropsPerKill。
        /// </summary>
        /// <param name="enemyType">参数名：enemyType，类型：int，用途：敌人类型（1=直接入包型 2=世界掉落型）</param>
        /// <returns>返回值类型：List，用途：roll 出的掉落条目（物品ID, 数量），可能为空</returns>
        public List<(int ItemId, int Count)> RollDrops(int enemyType)
        {
            var drops = new List<(int ItemId, int Count)>();
            foreach (DropRule rule in GetRules())
            {
                if (rule.EnemyType != enemyType) continue;
                if (Random.Shared.NextDouble() >= rule.Chance) continue;
                int count = rule.MaxCount > rule.MinCount
                    ? Random.Shared.Next(rule.MinCount, rule.MaxCount + 1)
                    : rule.MinCount;
                drops.Add((rule.ItemId, count));
                if (drops.Count >= MaxDropsPerKill) break;
            }
            return drops;
        }

        // ──────────────────────────────
        // 物品名查询（掉落回执要展示名字，客户端免查配置表）
        // ──────────────────────────────

        /// <summary>查物品名（走 BagRepository 的统一 item_config TTL 缓存，不再各自复制一份；查不到返回"物品{ID}"兜底，绝不因缺名阻塞掉落流程）。</summary>
        /// <param name="itemId">参数名：itemId，类型：int，用途：物品ID</param>
        /// <returns>返回值类型：string，用途：物品名</returns>
        public string GetItemName(int itemId)
        {
            return BagRepository.GetConfigMap().TryGetValue(itemId, out BagRepository.ItemConfig? config)
                ? config.Name
                : $"物品{itemId}";
        }

        // ──────────────────────────────
        // 世界掉落令牌账本（内存：重启即清空；客户端按过期时间先于服务端隐藏掉落物兜底）
        // ──────────────────────────────

        /// <summary>待拾取掉落（令牌 → 归属玩家/物品/数量/过期时间）</summary>
        private sealed record PendingDrop(int PlayerId, int ItemId, int Count, long ExpireAtMs);

        /// <summary>令牌账本（形态B专用；令牌不可伪造、归属绑定、60 秒过期）</summary>
        private static readonly ConcurrentDictionary<string, PendingDrop> pendingDrops = new();

        /// <summary>
        /// 创建待拾取令牌的方法（形态B击杀回执时调用）。
        /// 先清扫过期令牌，再校验该玩家的待拾取上限，最后入账并返回令牌与过期时间。
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：归属玩家ID（拾取时校验）</param>
        /// <param name="itemId">参数名：itemId，类型：int，用途：物品ID</param>
        /// <param name="count">参数名：count，类型：int，用途：数量</param>
        /// <returns>返回值类型：(string?, long)，用途：(令牌, 过期UTC毫秒)；超过上限返回 (null, 0)</returns>
        public (string? Token, long ExpireMs) CreatePendingDrop(int playerId, int itemId, int count)
        {
            SweepExpired();

            int owned = 0;
            foreach (PendingDrop drop in pendingDrops.Values)
            {
                if (drop.PlayerId == playerId && ++owned >= MaxPendingPerPlayer)
                {
                    return (null, 0);
                }
            }

            string token = Guid.NewGuid().ToString("N");
            long expireMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + TokenTtlMs;
            pendingDrops[token] = new PendingDrop(playerId, itemId, count, expireMs);
            return (token, expireMs);
        }

        /// <summary>
        /// 拾取令牌的方法（原子摘除：TryRemove 保证同一令牌只能被消费一次）。
        /// 校验链：存在 → 未过期 → 归属本人；不存在与归属不符返回同一文案，防令牌探测。
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：拾取者玩家ID（会话绑定）</param>
        /// <param name="token">参数名：token，类型：string，用途：击杀回执下发的令牌</param>
        /// <returns>返回值类型：(bool, string, int, int)，用途：(是否认领成功, 失败文案, 物品ID, 数量)</returns>
        public (bool Success, string Message, int ItemId, int Count) TryPickup(int playerId, string token)
        {
            if (string.IsNullOrEmpty(token) || !pendingDrops.TryRemove(token, out PendingDrop? drop))
            {
                return (false, "掉落物已过期或不存在", 0, 0);
            }
            if (drop.ExpireAtMs < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            {
                return (false, "掉落物已过期", drop.ItemId, drop.Count);
            }
            if (drop.PlayerId != playerId)
            {
                return (false, "掉落物已过期或不存在", 0, 0);
            }
            return (true, "OK", drop.ItemId, drop.Count);
        }

        /// <summary>清扫过期令牌（创建时顺手执行，不需要独立定时器）。</summary>
        private static void SweepExpired()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach ((string token, PendingDrop drop) in pendingDrops)
            {
                if (drop.ExpireAtMs < now)
                {
                    pendingDrops.TryRemove(token, out _);
                }
            }
        }
    }
}
