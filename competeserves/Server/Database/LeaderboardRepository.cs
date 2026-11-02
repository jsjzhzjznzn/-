using MySqlConnector;

using CompeteServer.Manager;

namespace CompeteServer.Database
{
    /// <summary>
    /// 排行榜仓库类（挑战得分榜）
    /// 数据分层：MySQL scores 表 = 持久真相；Valkey ZSET = 实时排行缓存
    /// 最高分模式：只有高于历史最高分的成绩才入账（MySQL GREATEST + Valkey ZADD GT 双保险）
    /// Valkey 不可用时功能降级：成绩照常入 MySQL，榜单查询自动降级直查 MySQL（idx_score 索引），功能不中断
    /// 读路径的两次回源保护：空榜写带 TTL 的哨兵键（穿透）、同键重建走进程内闸门（击穿）
    /// 表结构见 Migrations\003_AddScores.sql（EnsureTable 提供幂等建表兜底）
    /// </summary>
    public class LeaderboardRepository
    {
        /// <summary>默认挑战ID（当前只有 1 号挑战）。</summary>
        public const int DefaultChallengeId = 1;

        /// <summary>击杀竞速挑战ID（排行榜分数为反转后的耗时）</summary>
        public const int KillRaceChallengeId = 2;

        /// <summary>迷雾信标小游戏ID（分数 = 评级分×10000 + 剩余时间×10 + 探索率）</summary>
        public const int FogHuntChallengeId = 3;

        /// <summary>
        /// 已知挑战ID白名单：challengeId 由客户端上传，放行未知 ID 等于允许为"根本不存在的榜"
        /// 反复回源全表扫描。新增挑战在此登记（或将来改为读活动/挑战配置表）
        /// </summary>
        private static readonly HashSet<int> KnownChallengeIds = new() { DefaultChallengeId, KillRaceChallengeId, FogHuntChallengeId };

        /// <summary>挑战ID是否受支持（上报与查询两条入口都先过这一关）。</summary>
        public static bool IsValidChallengeId(int challengeId) => KnownChallengeIds.Contains(challengeId);

        /// <summary>成绩合法性上限（防手滑上报天文数字把 ZSET score 搞脏）。</summary>
        /// <remarks>ZSET 的 score 是 double，long 隐式升上去；超过 2^53 会静默丢精度，调高此值前先想清楚。</remarks>
        public const long MaxScore = 1_000_000;

        /// <summary>ZSET 键名：每个挑战一个有序集合，member=玩家ID，score=最高分。</summary>
        private static string Key(int challengeId) => $"compete:lb:{challengeId}";

        /// <summary>空榜哨兵键：ZSET 存不出"空键"，用字符串标记表达"确认过这个挑战没人上榜"。</summary>
        private static string EmptySentinelKey(int challengeId) => $"compete:lb:{challengeId}:empty";

        /// <summary>空榜哨兵有效期：期内不再回源；到期放行一次，新成绩一上榜 ZADD 就会建出真键。</summary>
        private static readonly TimeSpan EmptySentinelTtl = TimeSpan.FromMinutes(1);

        /// <summary>等他人重建的时限（毫秒）：超时不再排队压 DB，本轮直接按缓存现状读。</summary>
        private const int RebuildWaitMs = 1500;

        /// <summary>按 ZSET 键名登记的重建闸门（键数量 = 挑战数量，不需要回收）。</summary>
        private static readonly Dictionary<string, SemaphoreSlim> rebuildGates = new();

        // ──────────────────────────────
        // 初始化（服务端启动时调用）
        // ──────────────────────────────

        /// <summary>幂等建表：与 Migrations\003_AddScores.sql 等价，表已存在则无副作用。</summary>
        public void EnsureTable()
        {
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlCommand command = new(
                """
                CREATE TABLE IF NOT EXISTS `scores` (
                  `challenge_id` INT    NOT NULL DEFAULT 1,
                  `player_id`    INT    NOT NULL,
                  `score`        BIGINT NOT NULL DEFAULT 0,
                  `updated_at`   DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                  PRIMARY KEY (`challenge_id`, `player_id`),
                  KEY `idx_score` (`challenge_id`, `score` DESC)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
                """,
                connection);
            command.ExecuteNonQuery();
        }

        /// <summary>启动重建：以 MySQL 为真相全量重建某挑战的 ZSET（无条件覆盖，幂等；Valkey 不可用则只告警）。</summary>
        public void RebuildValkey(int challengeId) => RebuildFromMySql(challengeId);

        /// <summary>
        /// 读路径的按需重建：先过同键闸门（击穿防护），拿到锁后 double-check——
        /// 排队期间别人可能已经重建好、或已确认是空榜，那就没必要再扫一遍全表
        /// </summary>
        private void RebuildIfMissing(int challengeId)
        {
            SemaphoreSlim gate = GetRebuildGate(Key(challengeId));
            if (!gate.Wait(RebuildWaitMs)) return;
            try
            {
                // double-check ①：Valkey 又不通了就不必白扫一遍全表，调用方自会走 MySQL 降级
                if (!ValkeyManager.Instance.TryKeyExists(Key(challengeId), out bool exists)) return;
                // double-check ②：排队期间已被他人重建，或已确认空榜（哨兵在有效期内）
                if (exists) return;
                if (ValkeyManager.Instance.TryKeyExists(EmptySentinelKey(challengeId), out bool emptyFlagged) && emptyFlagged) return;

                RebuildFromMySql(challengeId);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>取（或建）该键的重建闸门。</summary>
        private static SemaphoreSlim GetRebuildGate(string key)
        {
            lock (rebuildGates)
            {
                if (!rebuildGates.TryGetValue(key, out SemaphoreSlim? gate))
                {
                    gate = new SemaphoreSlim(1, 1);
                    rebuildGates[key] = gate;
                }
                return gate;
            }
        }

        /// <summary>
        /// 从 MySQL 真相回填缓存的方法（启动强制重建与读路径按需重建共用）
        /// 空榜不写 ZSET（ZSET 存不出空键），改打一条带 TTL 的哨兵：期内后续查询直接认定空榜，
        /// 不再每次都全表扫——这正是"没人上榜的榜被反复查"会打穿数据库的形态
        /// </summary>
        private void RebuildFromMySql(int challengeId)
        {
            List<(long PlayerId, long Score)> rows = LoadAll(challengeId);
            if (rows.Count == 0)
            {
                ValkeyManager.Instance.TrySetFlag(EmptySentinelKey(challengeId), EmptySentinelTtl);
                Console.WriteLine($"排行榜缓存重建: 挑战{challengeId} 无人上榜 → 写空榜哨兵（{EmptySentinelTtl.TotalSeconds:0}s 内不再回源）");
                return;
            }

            bool ok = ValkeyManager.Instance.TryRebuild(
                Key(challengeId),
                rows.Select(row => (row.PlayerId.ToString(), (double)row.Score)));
            Console.WriteLine(ok
                ? $"排行榜缓存重建完成: 挑战{challengeId} 共{rows.Count}人"
                : $"排行榜缓存重建失败（Valkey 不可用），读取时将再次尝试");
        }

        // ──────────────────────────────
        // 成绩上报（最高分模式）
        // ──────────────────────────────

        /// <summary>
        /// 提交成绩的方法
        /// 写路径：先 MySQL GREATEST upsert（落袋为安）→ 再 Valkey ZADD GT（实时榜）
        /// 读回排名来自 Valkey 实时数据；Valkey 不可用时成绩仍入账，排名返回 0 并在 message 说明
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID（服务端会话绑定，不可伪造）</param>
        /// <param name="challengeId">参数名：challengeId，类型：int，用途：挑战ID</param>
        /// <param name="score">参数名：score，类型：long，用途：本次挑战成绩</param>
        /// <returns>返回值类型：(bool Success, string Message, long BestScore, long Rank, bool NewRecord)，用途：入账结果与实时排名</returns>
        public (bool Success, string Message, long BestScore, long Rank, bool NewRecord) SubmitScore(
            int playerId, int challengeId, long score)
        {
            long before = GetBestScore(playerId, challengeId);
            bool newRecord = score > before;

            // 第一步：MySQL 落袋（数据真相，永远先写）
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlCommand command = new(
                    """
                    INSERT INTO scores (challenge_id, player_id, score)
                    VALUES (@ChallengeId, @PlayerId, @Score)
                    ON DUPLICATE KEY UPDATE score = GREATEST(score, VALUES(score))
                    """,
                    connection);
                command.Parameters.AddWithValue("@ChallengeId", challengeId);
                command.Parameters.AddWithValue("@PlayerId", playerId);
                command.Parameters.AddWithValue("@Score", score);
                command.ExecuteNonQuery();
            }
            catch (Exception exception)
            {
                Console.WriteLine($"成绩落库失败 [{exception.GetType().Name}]: {exception.Message}");
                return (false, "成绩记录失败，请稍后再试", 0, 0, false);
            }

            long best = Math.Max(before, score);

            // 第二步：Valkey 实时榜（ZADD GT 与 MySQL GREATEST 同语义，双保险）
            bool cacheOk = ValkeyManager.Instance.TryZAddIfGreater(Key(challengeId), playerId.ToString(), score, out _);

            long rank = 0;
            if (cacheOk)
            {
                ValkeyManager.Instance.TryZRevRank(Key(challengeId), playerId.ToString(), out rank);
            }

            string message = !cacheOk
                ? "成绩已记录（排行榜缓存暂不可用）"
                : newRecord
                    ? (rank > 0 ? $"新纪录！当前排名第{rank}" : "新纪录！")
                    : "未超过最高分";
            Console.WriteLine($"成绩上报:{playerId} 挑战{challengeId} 本次={score} 最高={best} 新纪录={newRecord} 排名={rank}");
            return (true, message, best, rank, newRecord);
        }

        /// <summary>查询玩家在某挑战的当前最高分（无记录返回 0）。</summary>
        private long GetBestScore(int playerId, int challengeId)
        {
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlCommand command = new(
                    "SELECT score FROM scores WHERE challenge_id = @ChallengeId AND player_id = @PlayerId",
                    connection);
                command.Parameters.AddWithValue("@ChallengeId", challengeId);
                command.Parameters.AddWithValue("@PlayerId", playerId);
                object? result = command.ExecuteScalar();
                return result == null ? 0 : Convert.ToInt64(result);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"最高分查询失败 [{exception.GetType().Name}]: {exception.Message}");
                return 0;
            }
        }

        // ──────────────────────────────
        // 榜单查询（只走 Valkey；缺失经同键闸门重建，确认空榜则认哨兵不回源）
        // ──────────────────────────────

        /// <summary>
        /// 拉取排行榜的方法
        /// 优先走 Valkey 实时榜（key 缺失经同键闸门重建一次；确认空榜则以哨兵挡住后续回源）；Valkey 不可用时降级直查 MySQL，功能不中断
        /// TopN 降序条目 + 查询者自己的实时排名与分数；条目只含 playerId，名字由 MessageHandler 层补齐
        /// </summary>
        /// <param name="challengeId">参数名：challengeId，类型：int，用途：挑战ID</param>
        /// <param name="topN">参数名：topN，类型：int，用途：取前几名（1-100，越界收敛）</param>
        /// <param name="myPlayerId">参数名：myPlayerId，类型：int，用途：查询者ID（用于附带自己的排名）</param>
        /// <returns>返回值类型：(bool Success, string Message, List Rows, long MyRank, long MyScore)，用途：榜单数据；Rows=(名次,玩家ID,分数)</returns>
        public (bool Success, string Message, List<(long Rank, int PlayerId, long Score)> Rows, long MyRank, long MyScore)
            GetLeaderboard(int challengeId, int topN, int myPlayerId)
        {
            topN = Math.Clamp(topN <= 0 ? 10 : topN, 1, 100);
            string key = Key(challengeId);

            // 缓存缺失（Valkey 重启/被清/这榜本来就空）→ 按需重建一次再读：同键只放一个线程回源，
            // 空榜由哨兵键在 TTL 内挡住后续回源；Valkey 不可用时 RebuildIfMissing 里的判定自然全 false，直接落下面的降级分支
            bool cacheCommandOk = ValkeyManager.Instance.TryKeyExists(key, out bool exists);
            if (cacheCommandOk && !exists)
            {
                Console.WriteLine($"排行榜缓存缺失: 挑战{challengeId} → 从 MySQL 重建");
                RebuildIfMissing(challengeId);
            }

            if (!ValkeyManager.Instance.TryZRevRangeTop(key, topN, out List<(string Member, double Score)> rows))
            {
                // Valkey 不可用 → 降级直查 MySQL（走 idx_score 索引），榜单功能不中断
                Console.WriteLine($"排行榜缓存不可用: 挑战{challengeId} → 降级直查 MySQL");
                return GetLeaderboardFromMySql(challengeId, topN, myPlayerId);
            }

            var result = new List<(long Rank, int PlayerId, long Score)>();
            long rank = 1;
            foreach ((string member, double score) in rows)
            {
                result.Add((rank, int.Parse(member), (long)score));
                rank++;
            }

            long myRank = ValkeyManager.Instance.TryZRevRank(key, myPlayerId.ToString(), out long r) ? r : 0;
            ValkeyManager.Instance.TryZScore(key, myPlayerId.ToString(), out double myScore);

            return (true, "OK", result, myRank, (long)myScore);
        }

        /// <summary>
        /// MySQL 降级查询榜单的方法（Valkey 不可用时的兜底）
        /// TopN 走 idx_score (challenge_id, score DESC) 索引排序（并列按 player_id 稳定）；
        /// 我的名次 = 比我分高的人数 + 1；我的分数复用 GetBestScore 直查
        /// </summary>
        /// <param name="challengeId">参数名：challengeId，类型：int，用途：挑战ID</param>
        /// <param name="topN">参数名：topN，类型：int，用途：取前几名</param>
        /// <param name="myPlayerId">参数名：myPlayerId，类型：int，用途：查询者ID（用于附带自己的排名）</param>
        /// <returns>返回值类型：(bool Success, string Message, List Rows, long MyRank, long MyScore)，用途：榜单数据；Rows=(名次,玩家ID,分数)</returns>
        private (bool Success, string Message, List<(long Rank, int PlayerId, long Score)> Rows, long MyRank, long MyScore)
            GetLeaderboardFromMySql(int challengeId, int topN, int myPlayerId)
        {
            try
            {
                var rows = new List<(long Rank, int PlayerId, long Score)>();
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using (MySqlCommand command = new(
                    "SELECT player_id, score FROM scores WHERE challenge_id = @ChallengeId ORDER BY score DESC, player_id ASC LIMIT @TopN",
                    connection))
                {
                    command.Parameters.AddWithValue("@ChallengeId", challengeId);
                    command.Parameters.AddWithValue("@TopN", topN);
                    using MySqlDataReader reader = command.ExecuteReader();
                    long rank = 1;
                    while (reader.Read())
                    {
                        rows.Add((rank++, reader.GetInt32(0), reader.GetInt64(1)));
                    }
                }

                long myScore = GetBestScore(myPlayerId, challengeId);
                long myRank = 0;
                if (myScore > 0)
                {
                    using MySqlCommand command = new(
                        "SELECT COUNT(*) FROM scores WHERE challenge_id = @ChallengeId AND score > @MyScore",
                        connection);
                    command.Parameters.AddWithValue("@ChallengeId", challengeId);
                    command.Parameters.AddWithValue("@MyScore", myScore);
                    myRank = Convert.ToInt64(command.ExecuteScalar()) + 1;
                }

                return (true, "OK（MySQL降级）", rows, myRank, myScore);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"榜单 MySQL 降级查询失败 [{exception.GetType().Name}]: {exception.Message}");
                return (false, "排行榜暂不可用，请稍后再试", new List<(long, int, long)>(), 0, 0);
            }
        }

        /// <summary>全量读取某挑战的成绩（重建 ZSET 用）。</summary>
        private List<(long PlayerId, long Score)> LoadAll(int challengeId)
        {
            var rows = new List<(long PlayerId, long Score)>();
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlCommand command = new(
                    "SELECT player_id, score FROM scores WHERE challenge_id = @ChallengeId",
                    connection);
                command.Parameters.AddWithValue("@ChallengeId", challengeId);
                using MySqlDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add((reader.GetInt64(0), reader.GetInt64(1)));
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"榜单全量读取失败 [{exception.GetType().Name}]: {exception.Message}");
            }
            return rows;
        }
    }
}
