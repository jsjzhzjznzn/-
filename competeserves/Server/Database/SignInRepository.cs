// 命名空间：CompeteServer.Database
// 签到仓库（鸣潮式 7 天累计签到）：奖励配置（sign_config）+ 玩家记录（player_sign_records）
// 两道闸：PK(player,sign,day) 保证只能签"累计次数+1"；UNIQUE(player,sign,claim_date) 保证每个自然日一次
// 发奖口径与唤取/任务奖励同源：单事务 + 玩家行锁，满包自动转邮件（MailRepository.InsertOverflowMail）
using MySqlConnector;

namespace CompeteServer.Database
{
    /// <summary>
    /// 签到数据仓库类
    /// 用途：签到活动的建表/种子/状态查询与签到发放（服务端权威：能否签、第几天、发什么全在这里裁决）
    /// </summary>
    public class SignInRepository
    {
        /// <summary>单日奖励行（协议 SignInDayInfo 的数据源）</summary>
        public sealed record DayRow(int Day, int ItemId, string Name, int Rarity, string IconKey, int Count, bool Claimed);

        /// <summary>回执里的背包槽位快照条目</summary>
        public record struct SlotRow(int SlotIndex, int ItemId, int Count, long InstanceId);

        /// <summary>签到操作回执的完整数据（Handler 组包用）</summary>
        public sealed record ClaimResult(
            bool Success, string Message, int SignId, int Day, int TotalDays, int SignedDays, bool TodaySigned,
            List<DayRow> Days, List<SlotRow> Slots,
            List<(int ItemId, string Name, int ItemType, int Rarity, string IconKey, string Description, int MaxStack)> Configs);

        // ──────────────────────────────
        // 建表 / 种子（与 Migrations\014_AddSignIn.sql 等价，幂等双保险）
        // ──────────────────────────────

        /// <summary>
        /// 幂等建表
        /// 用途：sign_config（奖励配置）与 player_sign_records（玩家记录）两张
        /// </summary>
        public void EnsureTable()
        {
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();

            using (MySqlCommand command = new(
                """
                CREATE TABLE IF NOT EXISTS `sign_config` (
                  `sign_id`  INT     NOT NULL COMMENT '签到活动ID = activity_config.activity_id（type=2 的活动）',
                  `day`      TINYINT NOT NULL COMMENT '天次（1 起始；奖励按累计签到次数解锁，断签不回退）',
                  `item_id`  INT     NOT NULL COMMENT '奖励物品ID = item_config.item_id',
                  `count`    INT     NOT NULL DEFAULT 1 COMMENT '数量',
                  PRIMARY KEY (`sign_id`, `day`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='签到奖励配置表（改奖励=改表，零发版）'
                """, connection))
            {
                command.ExecuteNonQuery();
            }

            using (MySqlCommand command = new(
                """
                CREATE TABLE IF NOT EXISTS `player_sign_records` (
                  `player_id`     INT      NOT NULL COMMENT '玩家ID',
                  `sign_id`       INT      NOT NULL COMMENT '签到活动ID',
                  `day`           TINYINT  NOT NULL COMMENT '签的是第几天（= 签到前的累计次数+1）',
                  `claim_date`    CHAR(10) NOT NULL COMMENT '签到日期（服务器本地时区 yyyy-MM-dd；唯一键=每日一签闸）',
                  `claimed_at_ms` BIGINT   NOT NULL COMMENT '签到时间（Unix 毫秒）',
                  PRIMARY KEY (`player_id`, `sign_id`, `day`),
                  UNIQUE KEY `uk_date` (`player_id`, `sign_id`, `claim_date`),
                  KEY `idx_time` (`player_id`, `sign_id`, `claimed_at_ms`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='玩家签到记录表（序列闸 + 每日一签闸）'
                """, connection))
            {
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// 幂等种子：活动3（每日签到）的 7 天奖励
        /// 前几天补给（药剂/矿石），第 6/7 天给唤取媒介形成"签到→攒媒介→抽卡"闭环
        /// </summary>
        public void SeedSignConfigs()
        {
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlCommand seed = new(
                """
                INSERT IGNORE INTO `sign_config` (`sign_id`, `day`, `item_id`, `count`) VALUES
                  (3, 1, 1001, 2),
                  (3, 2, 1003, 10),
                  (3, 3, 1007, 3),
                  (3, 4, 1004, 10),
                  (3, 5, 1002, 2),
                  (3, 6, 2001, 5),
                  (3, 7, 2001, 10)
                """, connection);
            seed.ExecuteNonQuery();
        }

        // ──────────────────────────────
        // 状态查询（签到页 2908 数据源）
        // ──────────────────────────────

        /// <summary>
        /// 查签到状态的方法
        /// 读路径不做活动窗口校验（活动中心侧栏只会出现进行中的活动；读是幂等的，写（Claim）才闸窗口）
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="signId">参数名：signId，类型：int，用途：签到活动ID</param>
        public (bool Success, string Message, int TotalDays, int SignedDays, bool TodaySigned, List<DayRow> Days)
            GetSignState(int playerId, int signId)
        {
            (int totalDays, int signedDays, bool todaySigned, List<DayRow> days) = ReadState(playerId, signId);
            if (totalDays <= 0)
                return (false, "签到奖励未配置", 0, 0, false, days);
            return (true, "OK", totalDays, signedDays, todaySigned, days);
        }

        /// <summary>读状态（不带玩家校验，Claim 主流程锁内复用同一份）</summary>
        private (int TotalDays, int SignedDays, bool TodaySigned, List<DayRow> Days)
            ReadState(int playerId, int signId)
        {
            // ① 奖励配置（静态数据，全量读取按天排序）
            var rewards = new List<(int Day, int ItemId, int Count)>();
            using (MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection())
            {
                using MySqlCommand command = new(
                    """
                    SELECT `day`, `item_id`, `count` FROM `sign_config`
                    WHERE `sign_id` = @SignId ORDER BY `day`
                    """, connection);
                command.Parameters.AddWithValue("@SignId", signId);
                using MySqlDataReader reader = command.ExecuteReader();
                while (reader.Read())
                    rewards.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)));
            }
            if (rewards.Count == 0) return (0, 0, false, new List<DayRow>());

            // ② 玩家进度：累计次数 + 今天是否已签
            using (MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection())
            {
                using MySqlCommand command = new(
                    """
                    SELECT COUNT(*), EXISTS(
                      SELECT 1 FROM `player_sign_records`
                      WHERE `player_id` = @PlayerId AND `sign_id` = @SignId AND `claim_date` = @Today
                    )
                    FROM `player_sign_records`
                    WHERE `player_id` = @PlayerId AND `sign_id` = @SignId
                    """, connection);
                command.Parameters.AddWithValue("@PlayerId", playerId);
                command.Parameters.AddWithValue("@SignId", signId);
                command.Parameters.AddWithValue("@Today", TodayDate());
                using MySqlDataReader reader = command.ExecuteReader();
                reader.Read();
                int signedDays = reader.GetInt32(0);
                bool todaySigned = Convert.ToInt32(reader.GetInt32(1)) != 0;

                // ③ 合成天次快照（claimed = day ≤ signedDays）
                Dictionary<int, BagRepository.ItemConfig> configs = BagRepository.GetConfigMap();
                var days = new List<DayRow>();
                foreach ((int day, int itemId, int count) in rewards)
                {
                    BagRepository.ItemConfig config = configs.GetValueOrDefault(itemId);
                    days.Add(new DayRow(day, itemId,
                        config?.Name ?? $"物品{itemId}", config?.Rarity ?? 0, config?.IconKey ?? string.Empty,
                        count, day <= signedDays));
                }
                return (rewards.Count, signedDays, todaySigned, days);
            }
        }

        // ──────────────────────────────
        // 签到主流程（2909-2910 数据源）
        // ──────────────────────────────

        /// <summary>
        /// 签到的方法（服务端权威）
        /// 顺序：窗口/配置校验（锁外）→ 单事务 + 玩家主行锁内：读进度（今日已签？序列天次？）→
        /// 写签到记录 → 奖励入包（满包转邮件）→ 一次提交
        /// 任何一步失败整单回滚：不存在"记了签到没发奖"或"发了奖没记录"
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="signId">参数名：signId，类型：int，用途：签到活动ID</param>
        public ClaimResult Claim(int playerId, int signId)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // 窗口校验：签到活动必须在 activity_config 里有 type=2 的排期且处于时间窗内（与唤取不同：签到的"活动"就是它自己）
            using (MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection())
            {
                using MySqlCommand command = new(
                    """
                    SELECT `start_at_ms`, `end_at_ms` FROM `activity_config`
                    WHERE `activity_id` = @Id AND `type` = 2
                    """, connection);
                command.Parameters.AddWithValue("@Id", signId);
                using MySqlDataReader reader = command.ExecuteReader();
                if (!reader.Read())
                    return BuildFailure(signId, "签到活动不存在", ReadState(playerId, signId));
                long startAt = reader.GetInt64(0);
                long endAt = reader.GetInt64(1);
                if (now < startAt)
                    return BuildFailure(signId, "签到活动尚未开始", ReadState(playerId, signId));
                if (now >= endAt)
                    return BuildFailure(signId, "签到活动已结束", ReadState(playerId, signId));
            }

            // 奖励配置：锁外读一次拿目标天次的物品（静态数据，事务内不再依赖）
            (int totalDays, _, _, List<DayRow> beforeDays) = ReadState(playerId, signId);
            if (totalDays <= 0)
                return BuildFailure(signId, "签到奖励未配置", (0, 0, false, beforeDays));
            int nextDay = beforeDays.Count(d => d.Claimed) + 1;   // 已签天数 = claimed 数
            DayRow? target = beforeDays.FirstOrDefault(d => d.Day == nextDay);
            if (target == null)
                return BuildFailure(signId, "签到奖励已全部领取", (totalDays, totalDays, true, beforeDays));

            string today = TodayDate();
            BagRepository bag = new();
            int granted = 0;
            bool overflowed = false;

            using IDisposable writeLock = PlayerWriteLock.Acquire(playerId);
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlTransaction transaction = connection.BeginTransaction();
                BagRepository.LockPlayerRow(connection, transaction, playerId);

                // 锁内重读进度：同玩家并发签到被行锁排队，今日已签/序列天次以这份为准
                using (MySqlCommand progress = new(
                    """
                    SELECT COUNT(*), EXISTS(
                      SELECT 1 FROM `player_sign_records`
                      WHERE `player_id` = @PlayerId AND `sign_id` = @SignId AND `claim_date` = @Today
                    )
                    FROM `player_sign_records`
                    WHERE `player_id` = @PlayerId AND `sign_id` = @SignId
                    """, connection, transaction))
                {
                    progress.Parameters.AddWithValue("@PlayerId", playerId);
                    progress.Parameters.AddWithValue("@SignId", signId);
                    progress.Parameters.AddWithValue("@Today", today);
                    using MySqlDataReader reader = progress.ExecuteReader();
                    reader.Read();
                    int signedDays = reader.GetInt32(0);
                    bool todaySigned = reader.GetInt32(1) != 0;
                    if (todaySigned)
                    {
                        return BuildFailure(signId, "今日已签到，明天再来吧",
                            (totalDays, signedDays, true, MarkClaimed(beforeDays, signedDays)));
                    }
                    if (signedDays + 1 != target.Day)
                    {
                        // 进度在锁外读之后被并发改变（理论不可达：行锁+写锁双闸，防御性回滚）
                        return BuildFailure(signId, "签到状态已变化，请刷新后重试",
                            (totalDays, signedDays, todaySigned, MarkClaimed(beforeDays, signedDays)));
                    }
                }

                // 奖励入包：只改内存快照，放不下的部分记 overflow（同事务转邮箱）
                BagSlot[] slots = BagRepository.LoadSlotsForUpdate(connection, transaction, playerId);
                Dictionary<int, BagRepository.ItemConfig> configMap = BagRepository.GetConfigMap();
                int maxStack = configMap.TryGetValue(target.ItemId, out BagRepository.ItemConfig? config)
                    ? Math.Max(1, config.MaxStack) : 1;
                granted = target.Count - BagRepository.GrantIntoSlots(
                    connection, transaction, playerId, ref slots, target.ItemId, target.Count, maxStack);
                overflowed = granted < target.Count;
                BagRepository.SaveSlots(connection, transaction, playerId, slots);

                // 写签到记录（PK/唯一键冲突 = 并发重复签，按今日已签处理）
                try
                {
                    using MySqlCommand insert = new(
                        """
                        INSERT INTO `player_sign_records` (`player_id`, `sign_id`, `day`, `claim_date`, `claimed_at_ms`)
                        VALUES (@PlayerId, @SignId, @Day, @Today, @Now)
                        """, connection, transaction);
                    insert.Parameters.AddWithValue("@PlayerId", playerId);
                    insert.Parameters.AddWithValue("@SignId", signId);
                    insert.Parameters.AddWithValue("@Day", target.Day);
                    insert.Parameters.AddWithValue("@Today", today);
                    insert.Parameters.AddWithValue("@Now", now);
                    insert.ExecuteNonQuery();
                }
                catch (MySqlException exception) when (exception.Number == 1062)
                {
                    return BuildFailure(signId, "今日已签到，明天再来吧",
                        (totalDays, target.Day - 1, true, MarkClaimed(beforeDays, target.Day - 1)));
                }

                if (overflowed)
                {
                    MailRepository.InsertOverflowMail(connection, transaction, playerId,
                        "背包已满：签到奖励转入邮箱",
                        "背包空间不足，部分签到奖励已转入邮箱，请清理背包后领取。",
                        new List<(int, int)> { (target.ItemId, target.Count - granted) });
                }

                transaction.Commit();
            }
            catch (Exception exception)
            {
                Console.WriteLine($"签到失败 [{exception.GetType().Name}]: {exception.Message}（player={playerId} sign={signId}）");
                return BuildFailure(signId, "签到失败，请稍后再试", ReadState(playerId, signId));
            }

            // 回执数据：签到后的完整天次快照 + 操作后的背包快照 + 涉及物品配置
            (_, int signedAfter, _, List<DayRow> afterDays) = ReadState(playerId, signId);
            List<SlotRow> postSlots = bag.GetSlots(playerId).Select(s => new SlotRow(s.SlotIndex, s.ItemId, s.Count, s.InstanceId)).ToList();
            var rewardConfigs = bag.GetAllConfigs().Where(c => c.ItemId == target.ItemId).ToList();

            string message = overflowed
                ? $"签到成功：{target.Name}×{granted} 已入包，{target.Count - granted} 个转入邮箱（背包空间不足）"
                : $"签到成功：{target.Name}×{target.Count} 已放入背包";
            Console.WriteLine($"签到:玩家{playerId} 活动{signId} 第{target.Day}天 → {target.Name}×{target.Count}{(overflowed ? $"（转邮箱{target.Count - granted}）" : "")}");
            return new ClaimResult(true, message, signId, target.Day, totalDays, signedAfter, true,
                afterDays, postSlots, rewardConfigs);
        }

        /// <summary>失败回执组装（带当前状态快照，客户端可直接重渲染）</summary>
        private static ClaimResult BuildFailure(int signId, string message,
            (int TotalDays, int SignedDays, bool TodaySigned, List<DayRow> Days) state)
        {
            return new ClaimResult(false, message, signId, 0, state.TotalDays, state.SignedDays, state.TodaySigned,
                state.Days, new List<SlotRow>(), new List<(int, string, int, int, string, string, int)>());
        }

        /// <summary>把读路径的天次快照按指定已签数重打 claimed 位（失败回执用，不重查库）</summary>
        private static List<DayRow> MarkClaimed(List<DayRow> days, int signedDays)
        {
            return days.Select(d => d with { Claimed = d.Day <= signedDays }).ToList();
        }

        /// <summary>
        /// "今天"的口径：服务器本地时区的自然日（yyyy-MM-dd）。
        /// E2E/联调可用环境变量 CS_SIGN_TODAY 覆盖（逐次实时读取不缓存）——验证跨天连续签到不用等真实日期。
        /// </summary>
        private static string TodayDate()
        {
            string? overridden = Environment.GetEnvironmentVariable("CS_SIGN_TODAY");
            if (!string.IsNullOrWhiteSpace(overridden)) return overridden.Trim();
            return DateTime.Now.ToString("yyyy-MM-dd");
        }
    }
}
