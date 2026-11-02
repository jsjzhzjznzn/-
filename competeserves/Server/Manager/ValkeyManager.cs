using StackExchange.Redis;

namespace CompeteServer.Manager
{
    /// <summary>
    /// Valkey 连接管理与 ZSET 基础操作封装（排行榜的实时数据层）
    /// Valkey 兼容 Redis 协议，直接使用 StackExchange.Redis 客户端
    /// 职责边界：只管"连接 + 通用命令"，不认识排行榜业务——key 命名与 MySQL 编排在 LeaderboardRepository
    /// 可用性约定：Valkey 挂掉时所有操作返回失败（false），服务不崩；排行榜功能降级，MySQL 仍是数据真相
    /// </summary>
    public sealed class ValkeyManager
    {
        private static readonly Lazy<ValkeyManager> LazyInstance = new(() => new ValkeyManager());

        /// <summary>获取连接管理单例。</summary>
        public static ValkeyManager Instance => LazyInstance.Value;

        /// <summary>连接地址，可用环境变量 CS_VALKEY 覆盖（默认 127.0.0.1:6379）。</summary>
        private readonly string connectionString;

        /// <summary>连接多路复用器（懒连接；null = 从未连上过）。</summary>
        private ConnectionMultiplexer? multiplexer;

        /// <summary>重连互斥锁（防止并发请求各自触发重连）。</summary>
        private readonly object connectLock = new();

        /// <summary>是否已向控制台打印过"连接失败"告警（只打一次，避免刷屏）。</summary>
        private bool downLogged;

        /// <summary>熔断到期时间（UtcNow Ticks）。命令失败后的一段时间内 GetDatabase 直接返回 null 快速降级。</summary>
        private long downUntilTicks;

        /// <summary>熔断冷却时长：期间不再对 Valkey 发起任何命令（离线时每条命令 ~2s 超时是降级延迟的主因）。</summary>
        private static readonly TimeSpan DownCooldown = TimeSpan.FromSeconds(30);

        private ValkeyManager()
        {
            string? fromEnv = Environment.GetEnvironmentVariable("CS_VALKEY");
            connectionString = string.IsNullOrWhiteSpace(fromEnv) ? "127.0.0.1:6379" : fromEnv.Trim();
        }

        /// <summary>不包含凭据的连接地址展示（与 MySqlManager.DisplayEndpoint 同风格）。</summary>
        public string DisplayEndpoint => connectionString;

        // ──────────────────────────────
        // 连接管理
        // ──────────────────────────────

        /// <summary>取可用的数据库句柄；连不上返回 null（调用方据此降级）。</summary>
        private IDatabase? GetDatabase()
        {
            // 熔断快速降级：冷却期内不再尝试——Valkey 离线时每条命令要等 ~2s backlog 超时，
            // 不熔断的话一次榜单请求（最多 5 条 Try*）会拖 4-10 秒；熔断后立即走 MySQL 降级
            if (DateTime.UtcNow.Ticks < Interlocked.Read(ref downUntilTicks)) return null;

            try
            {
                // 先把字段读入局部变量，锁内外都用同一引用，避免读 multiplexer 与替换之间的竞态
                ConnectionMultiplexer? mux = multiplexer;
                if (mux == null || !mux.IsConnected)
                {
                    lock (connectLock)
                    {
                        mux = multiplexer;
                        if (mux == null || !mux.IsConnected)
                        {
                            // abortConnect=false：Valkey 暂时不在也不抛异常，由各操作自己容错
                            ConnectionMultiplexer? old = multiplexer;
                            mux = ConnectionMultiplexer.Connect(
                                $"{connectionString},abortConnect=false,connectTimeout=2000,syncTimeout=2000");
                            // 替换与释放旧连接放在同一把锁内：先指向新实例，再 Dispose 旧实例
                            multiplexer = mux;
                            if (!ReferenceEquals(old, mux))
                            {
                                old?.Dispose();
                            }
                            Console.WriteLine($"Valkey 连接成功: {connectionString}");
                            downLogged = false;
                        }
                    }
                }
                return mux!.GetDatabase();
            }
            catch (Exception exception)
            {
                if (!downLogged)
                {
                    downLogged = true;
                    Console.WriteLine($"Valkey 不可用 [{exception.GetType().Name}]: {exception.Message}（排行榜降级，MySQL 仍为数据真相）");
                }
                Interlocked.Exchange(ref downUntilTicks, DateTime.UtcNow.Add(DownCooldown).Ticks);
                return null;
            }
        }

        /// <summary>
        /// 标记进入熔断冷却（各 Try* 命令失败时调用）。
        /// 冷却期内 GetDatabase 直接返回 null：调用方立即拿到 false 走 MySQL 降级，
        /// 不用每条命令再白等一次 ~2s 超时；冷却结束后放行下一次重试。
        /// </summary>
        /// <param name="operation">参数名：operation，类型：string，用途：失败的操作名（日志用）</param>
        /// <param name="exception">参数名：exception，类型：Exception，用途：失败原因（日志用）</param>
        private void MarkDown(string operation, Exception exception)
        {
            Interlocked.Exchange(ref downUntilTicks, DateTime.UtcNow.Add(DownCooldown).Ticks);
            Console.WriteLine($"Valkey {operation} 失败 [{exception.GetType().Name}]: {exception.Message}（进入{DownCooldown.TotalSeconds:0}s熔断冷却）");
        }

        /// <summary>启动健康检查：连接 + PING，失败抛异常（由调用方决定告警还是阻止启动）。</summary>
        public async Task CheckHealthAsync()
        {
            ConnectionMultiplexer mux = await ConnectionMultiplexer.ConnectAsync(
                $"{connectionString},abortConnect=false,connectTimeout=2000,syncTimeout=2000");
            try
            {
                await mux.GetDatabase().PingAsync();
            }
            catch
            {
                mux.Dispose();
                throw;
            }
            // 健康检查建立的连接保留下来复用给后续操作
            multiplexer = mux;
        }

        // ──────────────────────────────
        // ZSET 基础操作（全部容错：失败返回 false，不抛出）
        // ──────────────────────────────

        /// <summary>EXISTS：判断键是否存在（用于"缓存缺失 → 用 MySQL 重建一次"的判定）。</summary>
        /// <param name="exists">参数名：exists，类型：out bool，用途：键是否存在（命令执行成功时有效）</param>
        /// <returns>返回值类型：bool，用途：命令是否执行成功（false = Valkey 不可用，此时 exists 无意义）</returns>
        public bool TryKeyExists(string key, out bool exists)
        {
            exists = false;
            IDatabase? db = GetDatabase();
            if (db == null) return false;
            try
            {
                exists = db.KeyExists(key);
                return true;
            }
            catch (Exception exception)
            {
                MarkDown("EXISTS", exception);
                return false;
            }
        }

        /// <summary>
        /// SET 一个带过期的标记键（调用方用它记"这个榜单我确认过是空的"，避免空结果反复回源打库）。
        /// 存在性判定复用 TryKeyExists，不新读值
        /// </summary>
        /// <param name="ttl">参数名：ttl，类型：TimeSpan，用途：标记存活时长，到期自动放行下一次回源</param>
        /// <returns>返回值类型：bool，用途：命令是否执行成功（false = Valkey 不可用）</returns>
        public bool TrySetFlag(string key, TimeSpan ttl)
        {
            IDatabase? db = GetDatabase();
            if (db == null) return false;
            try
            {
                db.StringSet(key, "1", ttl);
                return true;
            }
            catch (Exception exception)
            {
                MarkDown("SET", exception);
                return false;
            }
        }

        /// <summary>ZADD GT：仅当新分数高于现有分数时写入（最高分模式的核心）。</summary>
        /// <param name="key">参数名：key，类型：string，用途：ZSET 键名</param>
        /// <param name="member">参数名：member，类型：string，用途：成员（玩家ID字符串）</param>
        /// <param name="score">参数名：score，类型：double，用途：本次成绩</param>
        /// <param name="changed">参数名：changed，类型：out bool，用途：本次是否真的刷新了纪录（元素新增也算）</param>
        /// <returns>返回值类型：bool，用途：命令是否执行成功（false = Valkey 不可用）</returns>
        public bool TryZAddIfGreater(string key, string member, double score, out bool changed)
        {
            changed = false;
            IDatabase? db = GetDatabase();
            if (db == null) return false;
            try
            {
                bool added = db.SortedSetAdd(key, member, score, SortedSetWhen.GreaterThan);
                changed = added;
                return true;
            }
            catch (Exception exception)
            {
                MarkDown("ZADD", exception);
                return false;
            }
        }

        /// <summary>ZSCORE：读取成员当前分数（成员不存在返回 false）。</summary>
        public bool TryZScore(string key, string member, out double score)
        {
            score = 0;
            IDatabase? db = GetDatabase();
            if (db == null) return false;
            try
            {
                double? value = db.SortedSetScore(key, member);
                score = value ?? 0;
                return value.HasValue;
            }
            catch (Exception exception)
            {
                MarkDown("ZSCORE", exception);
                return false;
            }
        }

        /// <summary>ZREVRANK：成员按分数降序的名次（0 起始；未上榜返回 false）。</summary>
        public bool TryZRevRank(string key, string member, out long rank)
        {
            rank = 0;
            IDatabase? db = GetDatabase();
            if (db == null) return false;
            try
            {
                long? value = db.SortedSetRank(key, member, Order.Descending);
                rank = (value ?? 0) + 1;   // 换算为 1 起始名次
                return value.HasValue;
            }
            catch (Exception exception)
            {
                MarkDown("ZREVRANK", exception);
                return false;
            }
        }

        /// <summary>ZREVRANGE WITHSCORES：按分数降序取前 N 名。</summary>
        /// <param name="rows">参数名：rows，类型：out List，用途：(member, score) 降序列表</param>
        public bool TryZRevRangeTop(string key, int topN, out List<(string Member, double Score)> rows)
        {
            rows = new List<(string, double)>();
            IDatabase? db = GetDatabase();
            if (db == null) return false;
            try
            {
                SortedSetEntry[] entries = db.SortedSetRangeByRankWithScores(key, 0, topN - 1, Order.Descending);
                foreach (SortedSetEntry entry in entries)
                {
                    rows.Add((entry.Element.ToString(), entry.Score));
                }
                return true;
            }
            catch (Exception exception)
            {
                MarkDown("ZREVRANGE", exception);
                return false;
            }
        }

        /// <summary>整键重建：删旧键后批量写入（启动/修复时由 MySQL 真相数据重建 ZSET）。</summary>
        /// <param name="rows">参数名：rows，类型：IEnumerable，用途：(member, score) 全量数据</param>
        public bool TryRebuild(string key, IEnumerable<(string Member, double Score)> rows)
        {
            IDatabase? db = GetDatabase();
            if (db == null) return false;
            try
            {
                db.KeyDelete(key);
                SortedSetEntry[] entries = rows
                    .Select(row => new SortedSetEntry(row.Member, row.Score))
                    .ToArray();
                if (entries.Length > 0)
                {
                    db.SortedSetAdd(key, entries);
                }
                return true;
            }
            catch (Exception exception)
            {
                MarkDown("重建", exception);
                return false;
            }
        }
    }
}
