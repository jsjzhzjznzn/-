using MySqlConnector;

namespace CompeteServer.Database
{
    /// <summary>
    /// 新手引导进度行（player_guide_progress）
    /// 一玩家一引导一行：step 记住"下一步该播哪一步"，completed 记住"别再自动弹"。
    /// </summary>
    public sealed class GuideProgressRow
    {
        /// <summary>玩家ID（会话绑定，不由客户端提供）</summary>
        public int PlayerId { get; init; }

        /// <summary>引导标识（当前仅 zhuye）</summary>
        public string GuideKey { get; init; } = string.Empty;

        /// <summary>下一次要播放的步骤下标（0 = 从头）</summary>
        public int Step { get; init; }

        /// <summary>是否已完成或已跳过</summary>
        public bool Completed { get; init; }

        /// <summary>服务端最近写入时间（Unix 毫秒）</summary>
        public long UpdatedAtMs { get; init; }
    }

    /// <summary>
    /// 新手引导进度仓库类
    /// 「随时退出、回来还在同一步」的服务端账本：进主页拉取，每推进一步覆盖式上报。
    /// 只做存储与范围护栏，不参与引导内容与表现。
    /// </summary>
    public class GuideProgressRepository
    {
        /// <summary>步骤下标上限（护栏：客户端传任意大整数也只落到这里）</summary>
        public const int MaxStep = 64;

        /// <summary>引导标识最大长度（与表列一致）</summary>
        public const int MaxGuideKeyLength = 32;

        /// <summary>当前结构版本</summary>
        public const int CurrentSaveVersion = 1;

        /// <summary>
        /// 幂等建表方法
        /// 与 Migrations\019_AddGuideProgress.sql 等价，已有表无副作用
        /// </summary>
        public void EnsureTable()
        {
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlCommand command = new(
                @"CREATE TABLE IF NOT EXISTS `player_guide_progress` (
                  `player_id`     INT         NOT NULL COMMENT '玩家ID = accounts.Id',
                  `guide_key`     VARCHAR(32) NOT NULL COMMENT '引导标识（当前仅 zhuye）',
                  `step`          INT         NOT NULL DEFAULT 0,
                  `completed`     TINYINT     NOT NULL DEFAULT 0,
                  `save_version`  INT         NOT NULL DEFAULT 1,
                  `updated_at_ms` BIGINT      NOT NULL DEFAULT 0,
                  PRIMARY KEY (`player_id`, `guide_key`),
                  KEY `idx_updated` (`updated_at_ms`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='新手引导进度表（一玩家一引导一行）'",
                connection);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// 读取进度方法
        /// 没有记录 → 返回 null（客户端再按新号/老号规则决定是否自动开场）
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="guideKey">参数名：guideKey，类型：string，用途：引导标识</param>
        /// <returns>返回值类型：GuideProgressRow?，用途：进度行（无记录为 null）</returns>
        public GuideProgressRow? Get(int playerId, string guideKey)
        {
            if (playerId <= 0 || !IsValidKey(guideKey)) return null;

            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlCommand command = new(
                @"SELECT step, completed, updated_at_ms
                  FROM player_guide_progress
                  WHERE player_id = @PlayerId AND guide_key = @GuideKey",
                connection);
            command.Parameters.AddWithValue("@PlayerId", playerId);
            command.Parameters.AddWithValue("@GuideKey", guideKey);

            using MySqlDataReader reader = command.ExecuteReader();
            if (!reader.Read()) return null;

            return new GuideProgressRow
            {
                PlayerId = playerId,
                GuideKey = guideKey,
                Step = reader.GetInt32(0),
                Completed = reader.GetInt32(1) != 0,
                UpdatedAtMs = reader.GetInt64(2),
            };
        }

        /// <summary>
        /// 覆盖式写入进度方法
        /// 步骤下标越界时钳制到 0..MaxStep；服务端时间戳总是取当前时间
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="guideKey">参数名：guideKey，类型：string，用途：引导标识</param>
        /// <param name="step">参数名：step，类型：int，用途：下一次要播放的步骤下标</param>
        /// <param name="completed">参数名：completed，类型：bool，用途：是否已完成或已跳过</param>
        /// <returns>返回值类型：(bool Success, string Message, int Step, bool Completed, long UpdatedAtMs)，用途：写入结果</returns>
        public (bool Success, string Message, int Step, bool Completed, long UpdatedAtMs) Upsert(
            int playerId, string guideKey, int step, bool completed)
        {
            if (playerId <= 0) return (false, "请先登录", 0, false, 0);
            if (!IsValidKey(guideKey)) return (false, "引导标识非法", 0, false, 0);

            int clampedStep = Math.Clamp(step, 0, MaxStep);
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlCommand command = new(
                @"INSERT INTO player_guide_progress
                    (player_id, guide_key, step, completed, save_version, updated_at_ms)
                  VALUES
                    (@PlayerId, @GuideKey, @Step, @Completed, @SaveVersion, @UpdatedAtMs)
                  ON DUPLICATE KEY UPDATE
                    step = VALUES(step),
                    completed = VALUES(completed),
                    save_version = VALUES(save_version),
                    updated_at_ms = VALUES(updated_at_ms)",
                connection);
            command.Parameters.AddWithValue("@PlayerId", playerId);
            command.Parameters.AddWithValue("@GuideKey", guideKey);
            command.Parameters.AddWithValue("@Step", clampedStep);
            command.Parameters.AddWithValue("@Completed", completed ? 1 : 0);
            command.Parameters.AddWithValue("@SaveVersion", CurrentSaveVersion);
            command.Parameters.AddWithValue("@UpdatedAtMs", nowMs);
            command.ExecuteNonQuery();

            return (true, "OK", clampedStep, completed, nowMs);
        }

        /// <summary>
        /// 引导标识校验方法
        /// 非空且不超过列宽（当前只接受小写字母、数字与下划线，避免奇怪键污染表）
        /// </summary>
        /// <param name="guideKey">参数名：guideKey，类型：string，用途：引导标识</param>
        /// <returns>返回值类型：bool，用途：是否合法</returns>
        public static bool IsValidKey(string? guideKey)
        {
            if (string.IsNullOrEmpty(guideKey)) return false;
            if (guideKey.Length > MaxGuideKeyLength) return false;

            foreach (char c in guideKey)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';
                if (!ok) return false;
            }
            return true;
        }
    }
}
