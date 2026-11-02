using MySqlConnector;

namespace CompeteServer.Database
{
    /// <summary>
    /// 生涯数据仓库类
    /// 生涯数据 = 从角色有记录开始就持续累计的指标（历史杀怪数、累计挑战次数这类），
    /// 是成就任务的进度来源——成就激活时直接读它，天然避免"数据继承"问题（多阶段成就用 prev 链接即可）。
    /// 本期指标：1 累计挑战次数 / 2 累计发消息数 / 3 累计获得物品数 / 4 累计提交任务数 / 5 历史最高单次挑战分。
    /// 写入口集中在 QuestHandler（玩法事件漏斗），避免各处散写。
    /// 表结构见 Migrations\006_AddQuestsFull.sql（EnsureTable 提供幂等建表兜底）
    /// </summary>
    public class CareerRepository
    {
        // ──────────────────────────────
        // 指标ID（与客户端 QuestManager.CareerScoreId 保持一致）
        // ──────────────────────────────

        /// <summary>累计挑战次数</summary>
        public const int ScoreChallengeCount = 1;

        /// <summary>累计发送消息数</summary>
        public const int ScoreMessageCount = 2;

        /// <summary>累计获得物品数（含任务奖励发放）</summary>
        public const int ScoreItemGain = 3;

        /// <summary>累计提交任务数</summary>
        public const int ScoreQuestSubmit = 4;

        /// <summary>历史最高单次挑战分（只取最大，不累加）</summary>
        public const int ScoreBestChallenge = 5;

        // ── 成就系统扩展统计（2026-10-26） ──
        // career_scores 本质是通用玩家统计表：(player_id, score_id, value)。
        // 1-99 为核心累计/最大值；100+ 为参数化计数（100+敌人类型、200+物品ID 预留）。

        /// <summary>累计添加好友数（双向各记一次，用于"累计加好友"成就）</summary>
        public const int ScoreFriendAdd = 6;

        /// <summary>副本击杀总数</summary>
        public const int ScoreDungeonKill = 7;

        /// <summary>副本拾取次数</summary>
        public const int ScoreDungeonPickup = 8;

        /// <summary>唤取总抽数（单抽 1 / 十连 10）</summary>
        public const int ScoreGachaDraw = 9;

        /// <summary>唤取五星数</summary>
        public const int ScoreGachaFiveStar = 10;

        /// <summary>累计签到天数</summary>
        public const int ScoreSignIn = 11;

        /// <summary>累计领取邮件数（按封计）</summary>
        public const int ScoreMailClaim = 12;

        /// <summary>单次挑战 0 分次数（隐藏成就用）</summary>
        public const int ScoreZeroChallenge = 13;

        /// <summary>按敌人类型统计击杀数：100 + enemyType。</summary>
        /// <param name="enemyType">敌人类型（DungeonRepository.EnemyTypeDirect/World）</param>
        /// <returns>返回值类型：int，用途：统计ID</returns>
        public static int ScoreDungeonKillByType(int enemyType) => 100 + enemyType;

        /// <summary>按物品ID统计获得数：200 + itemId（预留，当前未接入）。</summary>
        /// <param name="itemId">物品ID</param>
        /// <returns>返回值类型：int，用途：统计ID</returns>
        public static int ScoreItemGainByItem(int itemId) => 200 + itemId;

        // ──────────────────────────────
        // 初始化（服务端启动时调用）
        // ──────────────────────────────

        /// <summary>幂等建表：与 Migrations\006_AddQuestsFull.sql 等价，表已存在则无副作用。</summary>
        public void EnsureTable()
        {
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlCommand command = new(
                """
                CREATE TABLE IF NOT EXISTS `career_scores` (
                  `player_id` INT NOT NULL,
                  `score_id`  INT NOT NULL,
                  `value`     INT NOT NULL DEFAULT 0,
                  PRIMARY KEY (`player_id`, `score_id`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
                """,
                connection);
            command.ExecuteNonQuery();
        }

        // ──────────────────────────────
        // 查询
        // ──────────────────────────────

        /// <summary>
        /// 读取玩家全部生涯指标（scoreId → 累计值）
        /// 成就快照与任务列表响应都用它做进度来源
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <returns>返回值类型：Dictionary，用途：指标ID → 累计值（无记录的指标不出现，调用方按 0 处理）</returns>
        public Dictionary<int, int> GetValues(int playerId)
        {
            var values = new Dictionary<int, int>();
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlCommand command = new(
                    "SELECT `score_id`, `value` FROM career_scores WHERE `player_id` = @PlayerId",
                    connection);
                command.Parameters.AddWithValue("@PlayerId", playerId);
                using MySqlDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    values[reader.GetInt32(0)] = reader.GetInt32(1);
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"生涯数据读取失败 [{exception.GetType().Name}]: {exception.Message}");
            }
            return values;
        }

        /// <summary>读取单个指标（无记录为 0）。</summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="scoreId">参数名：scoreId，类型：int，用途：指标ID</param>
        /// <returns>返回值类型：int，用途：累计值</returns>
        public int GetValue(int playerId, int scoreId)
            => GetValues(playerId).TryGetValue(scoreId, out int value) ? value : 0;

        // ──────────────────────────────
        // 写入（经 QuestHandler 的玩法事件漏斗调用）
        // ──────────────────────────────

        /// <summary>
        /// 累加指标的方法（UPSERT：不存在则插入 delta，存在则加 delta）
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="scoreId">参数名：scoreId，类型：int，用途：指标ID</param>
        /// <param name="delta">参数名：delta，类型：int，用途：增量（可为负但生涯指标通常只增）</param>
        /// <returns>返回值类型：int，用途：累加后的新值（失败返回 -1）</returns>
        public int Add(int playerId, int scoreId, int delta)
        {
            if (playerId <= 0 || scoreId <= 0) return -1;
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlCommand command = new(
                    """
                    INSERT INTO career_scores (`player_id`, `score_id`, `value`)
                    VALUES (@PlayerId, @ScoreId, @Delta)
                    ON DUPLICATE KEY UPDATE `value` = `value` + @Delta
                    """,
                    connection);
                command.Parameters.AddWithValue("@PlayerId", playerId);
                command.Parameters.AddWithValue("@ScoreId", scoreId);
                command.Parameters.AddWithValue("@Delta", delta);
                command.ExecuteNonQuery();
                return GetValue(playerId, scoreId);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"生涯数据累加失败 [{exception.GetType().Name}]: {exception.Message}");
                return -1;
            }
        }

        /// <summary>
        /// 记录"越大越优"类指标的方法（只保留历史最大，如最高单次挑战分）
        /// 与任务的 progress 字段分开存——生涯里保留原值，任务进度语义不变（越大越接近完成）
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="scoreId">参数名：scoreId，类型：int，用途：指标ID</param>
        /// <param name="value">参数名：value，类型：int，用途：本次观测值</param>
        /// <returns>返回值类型：int，用途：记录后的历史最大值（失败返回 -1）</returns>
        public int SetMax(int playerId, int scoreId, int value)
        {
            if (playerId <= 0 || scoreId <= 0) return -1;
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlCommand command = new(
                    """
                    INSERT INTO career_scores (`player_id`, `score_id`, `value`)
                    VALUES (@PlayerId, @ScoreId, @Value)
                    ON DUPLICATE KEY UPDATE `value` = GREATEST(`value`, @Value)
                    """,
                    connection);
                command.Parameters.AddWithValue("@PlayerId", playerId);
                command.Parameters.AddWithValue("@ScoreId", scoreId);
                command.Parameters.AddWithValue("@Value", value);
                command.ExecuteNonQuery();
                return GetValue(playerId, scoreId);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"生涯数据取大失败 [{exception.GetType().Name}]: {exception.Message}");
                return -1;
            }
        }
    }
}
