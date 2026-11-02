// 命名空间：CompeteServer.Database
// 活动系统数据访问层：排期表（activity_config）的读写与 GM 轮换
// 卡池配置（banner_config）与流水（gacha_records）见 GachaRepository
using MySqlConnector;

namespace CompeteServer.Database
{
    /// <summary>
    /// 活动数据仓库类
    /// 用途：活动排期的建表/种子/查询，以及 GM 轮换（结束当前活动、把目标活动提前到现在）
    /// </summary>
    public class ActivityRepository
    {
        /// <summary>活动排期行（读路径只暴露当前时间窗内的行）</summary>
        public sealed record ActivityRow(
            int ActivityId, int Type, int BannerId, string ContentKey, string ContentVersion,
            string Title, string Subtitle, long StartAtMs, long EndAtMs);

        /// <summary>轮换后补发的窗口时长（目标活动已过期时，按 7 天重新开窗，支持 A↔B 反复演示）</summary>
        private const long RotateFallbackWindowMs = 7L * 24 * 3600 * 1000;

        /// <summary>签到活动的续期窗口：签到是常驻活动，过期后自动续 30 天（奖励进度在 player_sign_records，不受影响）</summary>
        private const long SignInRenewWindowMs = 30L * 24 * 3600 * 1000;

        /// <summary>
        /// 幂等建表
        /// 用途：与 Migrations\012_AddActivity.sql 等价（activity_config / banner_config / gacha_records 三张）
        /// </summary>
        public void EnsureTable()
        {
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();

            using (MySqlCommand command = new(
                """
                CREATE TABLE IF NOT EXISTS `activity_config` (
                  `activity_id`     INT          NOT NULL COMMENT '活动ID',
                  `type`            INT          NOT NULL DEFAULT 1 COMMENT '活动类型：1 武器唤取 2 每日签到',
                  `banner_id`       INT          NOT NULL COMMENT '关联卡池ID = banner_config.banner_id',
                  `content_key`     VARCHAR(64)  NOT NULL COMMENT '内容包键（展示/日志用）',
                  `content_version` VARCHAR(32)  NOT NULL COMMENT '内容版本（与热更包版本同步 bump）',
                  `title`           VARCHAR(128) NOT NULL COMMENT '活动标题',
                  `subtitle`        VARCHAR(128) NOT NULL DEFAULT '' COMMENT '活动副标题',
                  `start_at_ms`     BIGINT       NOT NULL COMMENT '开始时间（Unix 毫秒）',
                  `end_at_ms`       BIGINT       NOT NULL COMMENT '结束时间（Unix 毫秒）',
                  `sort`            INT          NOT NULL DEFAULT 0 COMMENT '同窗多活动排序（小者靠前）',
                  PRIMARY KEY (`activity_id`),
                  KEY `idx_time` (`start_at_ms`, `end_at_ms`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='活动排期表（当前时间窗内即视为进行中）'
                """, connection))
            {
                command.ExecuteNonQuery();
            }

            using (MySqlCommand command = new(
                """
                CREATE TABLE IF NOT EXISTS `banner_config` (
                  `banner_id`       INT          NOT NULL COMMENT '卡池ID',
                  `title`           VARCHAR(128) NOT NULL COMMENT '卡池标题',
                  `cost_item_id`    INT          NOT NULL COMMENT '唤取货币物品ID（2001 唤取媒介）',
                  `cost_single`     INT          NOT NULL DEFAULT 1 COMMENT '单抽消耗',
                  `cost_ten`        INT          NOT NULL DEFAULT 10 COMMENT '十连消耗',
                  `pity5_threshold` INT          NOT NULL DEFAULT 30 COMMENT '5★保底阈值（N 抽内必出 5★）',
                  `pool_json`       TEXT         NOT NULL COMMENT '物品池 JSON：[{item_id,star,weight,is_up}]',
                  PRIMARY KEY (`banner_id`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='唤取卡池配置（换池零代码）'
                """, connection))
            {
                command.ExecuteNonQuery();
            }

            using (MySqlCommand command = new(
                """
                CREATE TABLE IF NOT EXISTS `gacha_records` (
                  `player_id`      INT     NOT NULL COMMENT '玩家ID',
                  `banner_id`      INT     NOT NULL COMMENT '卡池ID',
                  `draw_seq`       INT     NOT NULL COMMENT '该卡池内第几抽（1 起始连续）',
                  `item_id`        INT     NOT NULL COMMENT '抽到的物品ID',
                  `star`           TINYINT NOT NULL COMMENT '稀有度（1-5，冗余便于统计）',
                  `is_pity`        TINYINT NOT NULL DEFAULT 0 COMMENT '是否保底触发',
                  `created_at_ms`  BIGINT  NOT NULL COMMENT '抽取时间（Unix 毫秒）',
                  PRIMARY KEY (`player_id`, `banner_id`, `draw_seq`),
                  KEY `idx_time` (`player_id`, `banner_id`, `created_at_ms`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='唤取流水表'
                """, connection))
            {
                command.ExecuteNonQuery();
            }

            // 抽卡重构（分层随机 / 双保底 / 幂等）的增量结构，与 Migrations\016_AddGachaPity.sql 等价
            EnsureGachaV2Schema(connection);
        }

        /// <summary>
        /// 抽卡 v2 增量结构（幂等）：
        ///   banner_config 补 4 列、gacha_records 补 pity_kind、新建 gacha_pity / gacha_requests
        /// MySQL 无 ADD COLUMN IF NOT EXISTS，一律查 information_schema（与 MailRepository/QuestRepository 同口径）
        /// </summary>
        private static void EnsureGachaV2Schema(MySqlConnection connection)
        {
            EnsureColumn(connection, "banner_config", "pity_group",
                "ALTER TABLE `banner_config` ADD COLUMN `pity_group` INT NOT NULL DEFAULT 0 COMMENT '保底分组：0=本池独立；>0=同组共享' AFTER `pity5_threshold`");
            EnsureColumn(connection, "banner_config", "star_rates_json",
                "ALTER TABLE `banner_config` ADD COLUMN `star_rates_json` TEXT NULL COMMENT '星级权重 JSON：[{star,weight}]；NULL=旧整池权重 roll' AFTER `pity_group`");
            EnsureColumn(connection, "banner_config", "up_rate_5",
                "ALTER TABLE `banner_config` ADD COLUMN `up_rate_5` INT NOT NULL DEFAULT 50 COMMENT '5★ 出 UP 的百分比（0-100），大保底时忽略' AFTER `star_rates_json`");
            EnsureColumn(connection, "banner_config", "up_rate_4",
                "ALTER TABLE `banner_config` ADD COLUMN `up_rate_4` INT NOT NULL DEFAULT 0 COMMENT '4★ 出 UP 的百分比（0-100）' AFTER `up_rate_5`");
            EnsureColumn(connection, "gacha_records", "pity_kind",
                "ALTER TABLE `gacha_records` ADD COLUMN `pity_kind` TINYINT NOT NULL DEFAULT 0 COMMENT '保底类型：0无 1=5★计数 2=十连4★ 3=大保底' AFTER `is_pity`");

            using (MySqlCommand command = new(
                """
                CREATE TABLE IF NOT EXISTS `gacha_pity` (
                  `player_id`      INT     NOT NULL COMMENT '玩家ID',
                  `pity_group`     INT     NOT NULL COMMENT '保底分组（0 视为 banner_id）',
                  `pity5_count`    INT     NOT NULL DEFAULT 0 COMMENT '距上次 5★ 的抽数',
                  `guaranteed_up`  TINYINT NOT NULL DEFAULT 0 COMMENT '下一次 5★ 是否必 UP（大保底）',
                  `total_draws`    INT     NOT NULL DEFAULT 0 COMMENT '该保底组累计抽数（统计用）',
                  `updated_at_ms`  BIGINT  NOT NULL COMMENT '最后更新时间（Unix 毫秒）',
                  PRIMARY KEY (`player_id`, `pity_group`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='抽卡保底状态（5★计数 + 大保底）'
                """, connection))
            {
                command.ExecuteNonQuery();
            }

            using (MySqlCommand command = new(
                """
                CREATE TABLE IF NOT EXISTS `gacha_requests` (
                  `player_id`     INT          NOT NULL COMMENT '玩家ID',
                  `request_id`    VARCHAR(64)  NOT NULL COMMENT '客户端生成的幂等键',
                  `banner_id`     INT          NOT NULL COMMENT '卡池ID',
                  `draw_count`    INT          NOT NULL COMMENT '抽数',
                  `response_json` MEDIUMTEXT   NOT NULL COMMENT '首次结算的完整回执快照',
                  `created_at_ms` BIGINT       NOT NULL COMMENT '创建时间（Unix 毫秒，7 天后清理）',
                  PRIMARY KEY (`player_id`, `request_id`),
                  KEY `idx_created` (`created_at_ms`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='抽卡幂等表（重试重放，不重复扣费）'
                """, connection))
            {
                command.ExecuteNonQuery();
            }

            // 演示池升级到分层随机：只填 NULL 行，策划手工改过的不覆盖
            using (MySqlCommand seedV2 = new(
                """
                UPDATE `banner_config`
                SET `pity_group` = 1, `up_rate_5` = 80, `up_rate_4` = 0,
                    `star_rates_json` = '[{"star":5,"weight":1000},{"star":4,"weight":1000},{"star":2,"weight":1200},{"star":1,"weight":3000}]'
                WHERE `banner_id` IN (1, 2) AND `star_rates_json` IS NULL
                """, connection))
            {
                seedV2.ExecuteNonQuery();
            }

            // 幂等快照保留 7 天（抽卡路径也会以低概率顺带清理，避免启动时长事务）
            using (MySqlCommand cleanup = new(
                "DELETE FROM `gacha_requests` WHERE `created_at_ms` < @Cutoff", connection))
            {
                cleanup.Parameters.AddWithValue("@Cutoff", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 7L * 24 * 3600 * 1000);
                cleanup.ExecuteNonQuery();
            }
        }

        /// <summary>补列助手：列不存在才 ALTER（MySQL 无 ADD COLUMN IF NOT EXISTS）。</summary>
        private static void EnsureColumn(MySqlConnection connection, string table, string column, string alterSql)
        {
            using MySqlCommand check = new(
                "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = @Table AND column_name = @Column",
                connection);
            check.Parameters.AddWithValue("@Table", table);
            check.Parameters.AddWithValue("@Column", column);
            if (Convert.ToInt64(check.ExecuteScalar() ?? 0L) > 0) return;

            using MySqlCommand alter = new(alterSql, connection);
            alter.ExecuteNonQuery();
        }

        /// <summary>
        /// 幂等种子：排期（活动A 已开始~+7天；活动B +7~+14天）与卡池（A=长刃UP / B=迅刀UP）
        /// 时间是"首次种子时刻"的相对值，INSERT IGNORE 后续启动不刷新——GM 轮换后就以轮换结果为准
        /// </summary>
        public void SeedActivityConfigs()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long sevenDays = 7L * 24 * 3600 * 1000;

            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();

            using (MySqlCommand seed = new(
                $"""
                INSERT IGNORE INTO `activity_config`
                  (`activity_id`, `type`, `banner_id`, `content_key`, `content_version`, `title`, `subtitle`, `start_at_ms`, `end_at_ms`, `sort`)
                VALUES
                  (1, 1, 1, 'weapon_banner', '1.0.0', '长刃唤取·苍鳞', '5★ 长刃·终 概率大幅提升', {now - 3600_000}, {now + sevenDays}, 0),
                  (2, 1, 2, 'weapon_banner', '2.0.0', '迅刀唤取·疾风', '5★ 迅刀·终 概率大幅提升', {now + sevenDays}, {now + sevenDays * 2}, 0),
                  (3, 2, 0, 'sign_in', '1.1.0', '签到', '每日签到领好礼，累计 7 天', {now - 3600_000}, {now + sevenDays}, 1),
                  (4, 3, 0, 'fog_hunt', '1.0.0', '迷雾信标', '限时探索 · 找齐 3 枚信标并撤离', {now - 3600_000}, {now + sevenDays}, 2)
                """, connection))
            {
                seed.ExecuteNonQuery();
            }

            // 卡池与 Migrations\012 的 INSERT 完全一致（池内容改动=改库+InvalidateBannerCache，最迟 10 秒生效）
            using (MySqlCommand seedBanner = new(
                """
                INSERT IGNORE INTO `banner_config` (`banner_id`, `title`, `cost_item_id`, `cost_single`, `cost_ten`, `pity5_threshold`, `pool_json`) VALUES
                  (1, '武器唤取·苍鳞', 2001, 1, 10, 30,
                   '[{"item_id":1017,"star":5,"weight":800,"is_up":1},{"item_id":1016,"star":5,"weight":100,"is_up":0},{"item_id":1018,"star":5,"weight":100,"is_up":0},{"item_id":1014,"star":4,"weight":500,"is_up":0},{"item_id":1015,"star":4,"weight":500,"is_up":0},{"item_id":1003,"star":1,"weight":1500,"is_up":0},{"item_id":1004,"star":1,"weight":1500,"is_up":0},{"item_id":1007,"star":2,"weight":1200,"is_up":0}]'),
                  (2, '武器唤取·疾风', 2001, 1, 10, 30,
                   '[{"item_id":1018,"star":5,"weight":800,"is_up":1},{"item_id":1016,"star":5,"weight":100,"is_up":0},{"item_id":1017,"star":5,"weight":100,"is_up":0},{"item_id":1014,"star":4,"weight":500,"is_up":0},{"item_id":1015,"star":4,"weight":500,"is_up":0},{"item_id":1003,"star":1,"weight":1500,"is_up":0},{"item_id":1004,"star":1,"weight":1500,"is_up":0},{"item_id":1007,"star":2,"weight":1200,"is_up":0}]')
                """, connection))
            {
                seedBanner.ExecuteNonQuery();
            }

            // 幂等修正：早期种子把填充物统一标了 3★，与 item_config 的真实稀有度（铁矿/铜矿=1、强化石=2）不一致，
            // 结果卡的星级显示会错档——只修"仍含 3★ 池"的行，手工改过的不受影响（与 BagRepository 图标修正同口径）
            using (MySqlCommand fixStars = new(
                """
                UPDATE `banner_config` SET `pool_json` = REPLACE(REPLACE(`pool_json`,
                  '{"item_id":1003,"star":3', '{"item_id":1003,"star":1'),
                  '{"item_id":1004,"star":3', '{"item_id":1004,"star":1')
                WHERE `pool_json` LIKE '%"star":3%'
                """, connection))
            {
                fixStars.ExecuteNonQuery();
            }
            using (MySqlCommand fixStone = new(
                """
                UPDATE `banner_config` SET `pool_json` = REPLACE(`pool_json`,
                  '{"item_id":1007,"star":3', '{"item_id":1007,"star":2')
                WHERE `pool_json` LIKE '%"item_id":1007,"star":3%'
                """, connection))
            {
                fixStone.ExecuteNonQuery();
            }

            // 签到是常驻活动：种子窗口是"首次启动 + 7 天"，过期后自动续期，
            // 否则活动中心只显示已过期之外的唤取活动，看起来像"签到被删了"
            int renewed = RenewExpiredSignIn(connection, now);
            if (renewed > 0)
                Console.WriteLine($"签到活动已过期，自动续期 {SignInRenewWindowMs / (24 * 3600 * 1000)} 天（{renewed} 行）");

            // 联调专用卡池（不挂在 activity_config，UI 不可见；只有 CS_GACHA_TEST_BANNER=1 时写入）
            if (Environment.GetEnvironmentVariable("CS_GACHA_TEST_BANNER") == "1")
                SeedGachaTestBanners(connection);

            GachaRepository.InvalidateBannerCache();   // 种子写完后让卡池缓存重建
        }

        /// <summary>
        /// 过期签到活动自动续期（只动 type=2 且已过期的行）：
        /// start 提前 1 小时（立刻生效）、end = now + 30 天。
        /// 签到进度在 player_sign_records（按天次/日期），续期不会重置或补签。
        /// </summary>
        private static int RenewExpiredSignIn(MySqlConnection connection, long nowMs)
        {
            using MySqlCommand command = new(
                """
                UPDATE `activity_config`
                SET `start_at_ms` = @Start, `end_at_ms` = @End
                WHERE `type` = 2 AND `end_at_ms` <= @Now
                """, connection);
            command.Parameters.AddWithValue("@Start", nowMs - 3600_000);
            command.Parameters.AddWithValue("@End", nowMs + SignInRenewWindowMs);
            command.Parameters.AddWithValue("@Now", nowMs);
            return command.ExecuteNonQuery();
        }

        /// <summary>
        /// 联调专用卡池（确定性验证 5★计数保底 / 十连4★保底 / 大保底 / 共享保底）：
        ///   91 = 3 抽必 5★（5★权重 1，其余 3★）
        ///   92 = 十连必 4★（4★权重 1，其余 3★）
        ///   93 = 每抽 5★，UP 率 0 → 第一只必歪，第二只必 UP（大保底）
        ///   94/95 = 同 pity_group（999），验证换池保底延续
        /// INSERT IGNORE：已存在不覆盖，策划手工调试不受影响。
        /// </summary>
        private static void SeedGachaTestBanners(MySqlConnection connection)
        {
            // REPLACE：测试卡池允许每次启动覆盖为确定配置（只影响 91-95，不碰演示池 1/2）
            // 注意：pool_json 的 star 必须与 item_config.rarity 一致（填充物用 1003 铁矿=1★）
            using MySqlCommand seed = new(
                """
                REPLACE INTO `banner_config`
                  (`banner_id`, `title`, `cost_item_id`, `cost_single`, `cost_ten`, `pity5_threshold`, `pool_json`,
                   `pity_group`, `star_rates_json`, `up_rate_5`, `up_rate_4`)
                VALUES
                  (91, '[TEST] 3抽必5星', 2001, 1, 10, 3,
                   '[{"item_id":1017,"star":5,"weight":1,"is_up":1},{"item_id":1016,"star":5,"weight":1,"is_up":0},{"item_id":1003,"star":1,"weight":999,"is_up":0}]',
                   91, '[{"star":5,"weight":1},{"star":1,"weight":999}]', 100, 0),
                  (92, '[TEST] 十连必4星', 2001, 1, 10, 99,
                   '[{"item_id":1014,"star":4,"weight":1,"is_up":0},{"item_id":1003,"star":1,"weight":999,"is_up":0}]',
                   92, '[{"star":4,"weight":1},{"star":1,"weight":999}]', 0, 0),
                  (93, '[TEST] 大保底', 2001, 1, 10, 99,
                   '[{"item_id":1017,"star":5,"weight":1,"is_up":1},{"item_id":1016,"star":5,"weight":1,"is_up":0}]',
                   93, '[{"star":5,"weight":1000}]', 0, 0),
                  (94, '[TEST] 共享保底A', 2001, 1, 10, 3,
                   '[{"item_id":1017,"star":5,"weight":1,"is_up":1},{"item_id":1003,"star":1,"weight":999,"is_up":0}]',
                   999, '[{"star":5,"weight":1},{"star":1,"weight":999}]', 100, 0),
                  (95, '[TEST] 共享保底B', 2001, 1, 10, 3,
                   '[{"item_id":1018,"star":5,"weight":1,"is_up":1},{"item_id":1003,"star":1,"weight":999,"is_up":0}]',
                   999, '[{"star":5,"weight":1},{"star":1,"weight":999}]', 100, 0)
                """, connection);
            seed.ExecuteNonQuery();
            Console.WriteLine("联调卡池已就绪：91=3抽必5星 92=十连必4星 93=大保底 94/95=共享保底");
        }

        /// <summary>
        /// 查询当前时间窗内的活动
        /// 用途：活动列表协议（2902）与轮换后的状态回执
        /// </summary>
        /// <param name="nowMs">参数名：nowMs，类型：long，用途：服务器当前时间（Unix 毫秒）</param>
        public List<ActivityRow> GetActiveActivities(long nowMs)
        {
            var result = new List<ActivityRow>();
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlCommand command = new(
                """
                SELECT `activity_id`, `type`, `banner_id`, `content_key`, `content_version`, `title`, `subtitle`, `start_at_ms`, `end_at_ms`
                FROM `activity_config`
                WHERE `start_at_ms` <= @Now AND `end_at_ms` > @Now
                ORDER BY `sort`, `activity_id`
                """, connection);
            command.Parameters.AddWithValue("@Now", nowMs);
            using MySqlDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new ActivityRow(
                    reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    reader.GetInt64(7), reader.GetInt64(8)));
            }
            return result;
        }

        /// <summary>
        /// 查询全部活动排期（轮换目标选择用）
        /// </summary>
        private List<ActivityRow> GetAllActivities()
        {
            var result = new List<ActivityRow>();
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlCommand command = new(
                """
                SELECT `activity_id`, `type`, `banner_id`, `content_key`, `content_version`, `title`, `subtitle`, `start_at_ms`, `end_at_ms`
                FROM `activity_config`
                ORDER BY `start_at_ms`, `activity_id`
                """, connection);
            using MySqlDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new ActivityRow(
                    reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    reader.GetInt64(7), reader.GetInt64(8)));
            }
            return result;
        }

        /// <summary>
        /// GM 轮换活动的方法（联调/演示指令，与 2509 同口径不鉴权）
        /// 语义 = 运营改排期：结束当前活动（end 提前到现在），把目标活动提前到现在开始
        /// 目标活动已过期时按 7 天重新开窗，支持 A↔B 反复演示
        /// 只作用于 type=1（唤取）活动：签到（type=2）常驻，不会被"结束当前/切下一排期"误伤；
        /// 但显式指定一个 type=2 活动时，等价于"给签到续期"（start 提前 1 小时、end = now + 30 天）。
        /// </summary>
        /// <param name="targetActivityId">参数名：targetActivityId，类型：int，用途：目标活动ID（0 = 自动选下一排期）</param>
        /// <returns>返回值：成功与否、说明、轮换后的当前活动列表</returns>
        public (bool Success, string Message, List<ActivityRow> Activities) GmRotate(int targetActivityId)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            List<ActivityRow> allRows = GetAllActivities();

            // 显式指定签到活动 → 只续期，不动正在进行的唤取活动
            if (targetActivityId > 0)
            {
                ActivityRow? signIn = allRows.FirstOrDefault(a => a.ActivityId == targetActivityId && a.Type == 2);
                if (signIn != null)
                {
                    using MySqlConnection renewConnection = MySqlManager.Instance.CreateOpenConnection();
                    using MySqlCommand renew = new(
                        "UPDATE `activity_config` SET `start_at_ms` = @Start, `end_at_ms` = @End WHERE `activity_id` = @Id",
                        renewConnection);
                    renew.Parameters.AddWithValue("@Start", now - 3600_000);
                    renew.Parameters.AddWithValue("@End", now + SignInRenewWindowMs);
                    renew.Parameters.AddWithValue("@Id", signIn.ActivityId);
                    renew.ExecuteNonQuery();
                    Console.WriteLine($"签到活动续期:{signIn.Title} → 30 天");
                    return (true, $"已续期[{signIn.Title}] {SignInRenewWindowMs / (24 * 3600 * 1000)} 天", GetActiveActivities(now));
                }
            }

            List<ActivityRow> all = allRows.Where(a => a.Type == 1).ToList();
            if (all.Count == 0)
                return (false, "没有配置任何活动", new List<ActivityRow>());

            ActivityRow? current = all.FirstOrDefault(a => a.StartAtMs <= now && a.EndAtMs > now);
            ActivityRow? target;
            if (targetActivityId > 0)
            {
                target = all.FirstOrDefault(a => a.ActivityId == targetActivityId);
                if (target == null)
                    return (false, $"活动 {targetActivityId} 不存在", GetActiveActivities(now));
                if (current != null && current.ActivityId == target.ActivityId)
                    return (true, $"活动[{current.Title}]本就在进行中，无需轮换", GetActiveActivities(now));
            }
            else
            {
                // 自动：当前活动之后最近开始的未进行活动（A→B→A 循环演示时选"被结束的那个"也合理，按 start 最近优先）
                target = all
                    .Where(a => current == null || a.ActivityId != current.ActivityId)
                    .OrderBy(a => a.StartAtMs >= now ? a.StartAtMs : long.MaxValue)   // 未开始的优先
                    .ThenBy(a => Math.Abs(a.StartAtMs - now))
                    .FirstOrDefault();
                if (target == null)
                    return (false, "没有可切换的目标活动", GetActiveActivities(now));
            }

            // 单事务改两行排期：结束当前 + 提前目标（目标已过期则重开 7 天窗口）
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlTransaction transaction = connection.BeginTransaction();
            try
            {
                if (current != null)
                {
                    using MySqlCommand endCurrent = new(
                        "UPDATE `activity_config` SET `end_at_ms` = @Now WHERE `activity_id` = @Id AND `end_at_ms` > @Now",
                        connection, transaction);
                    endCurrent.Parameters.AddWithValue("@Now", now);
                    endCurrent.Parameters.AddWithValue("@Id", current.ActivityId);
                    endCurrent.ExecuteNonQuery();
                }

                long targetEnd = target.EndAtMs > now ? target.EndAtMs : now + RotateFallbackWindowMs;
                using MySqlCommand startTarget = new(
                    "UPDATE `activity_config` SET `start_at_ms` = @Now, `end_at_ms` = @End WHERE `activity_id` = @Id",
                    connection, transaction);
                startTarget.Parameters.AddWithValue("@Now", now);
                startTarget.Parameters.AddWithValue("@End", targetEnd);
                startTarget.Parameters.AddWithValue("@Id", target.ActivityId);
                startTarget.ExecuteNonQuery();

                transaction.Commit();
            }
            catch (Exception exception)
            {
                transaction.Rollback();
                Console.WriteLine($"活动轮换失败 [{exception.GetType().Name}]: {exception.Message}");
                return (false, "轮换失败，请稍后再试", GetActiveActivities(now));
            }

            List<ActivityRow> after = GetActiveActivities(now);
            string endedText = current != null ? $"结束[{current.Title}]、" : "";
            Console.WriteLine($"活动轮换:{endedText}开始[{target.Title}]");
            return (true, $"已{endedText}开启[{target.Title}]", after);
        }
    }
}
