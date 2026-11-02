using System.Text.Json;

using MySqlConnector;

namespace CompeteServer.Database
{
    /// <summary>
    /// 邮件仓库类（服务端权威，本期仅系统邮件）
    /// 表结构见 Migrations\007_AddMail.sql（EnsureTable 提供幂等建表兜底）
    /// 核心保证：领取附件与背包写入在同一个 MySQL 事务内完成——
    ///   行锁（FOR UPDATE）锁定邮件 → 归属/过期/重复领取校验 → 内存模拟背包空间（全部放得下才落账，
    ///   放不下整单拒绝，不做部分发放）→ 槽位整表回写 → 置领取掩码与已读位 → 提交；
    ///   任何一步失败即回滚，附件不会凭空消失也不会被重复领取（防重放）
    /// </summary>
    public class MailRepository
    {
        /// <summary>邮箱容量上限（有效邮件数：未删除且未过期；防止刷爆，超出拒绝收件）。</summary>
        public const int MailCapacity = 100;

        /// <summary>单封邮件附件种类上限（合并同ID后计算；附件数量上限沿用 BagRepository.MaxOperationCount）。</summary>
        public const int MaxMailItems = 8;

        /// <summary>标题最大长度（与表列宽一致，超出截断）。</summary>
        public const int MaxTitleLength = 64;

        /// <summary>正文最大长度（与表列宽一致，超出截断）。</summary>
        public const int MaxBodyLength = 512;

        /// <summary>状态位：已读</summary>
        private const int StateRead = 1;

        /// <summary>状态位：已删除（软删，行保留可溯源）</summary>
        private const int StateDeleted = 2;

        /// <summary>附件 JSON 的序列化行结构（items 列的元素；属性名与列内 JSON 字段一致）</summary>
        private sealed class MailItemRow
        {
            /// <summary>物品ID = item_config.item_id</summary>
            public int ItemId { get; set; }

            /// <summary>数量（≥1）</summary>
            public int Count { get; set; }
        }

        /// <summary>邮件数据（仓库层统一出参，处理器据此拼协议）</summary>
        public sealed class MailData
        {
            /// <summary>邮件ID</summary>
            public int MailId;

            /// <summary>发件人显示名</summary>
            public string SenderName = "系统";

            /// <summary>标题</summary>
            public string Title = string.Empty;

            /// <summary>正文</summary>
            public string Body = string.Empty;

            /// <summary>创建时间（UTC 毫秒）</summary>
            public long CreateTimeMs;

            /// <summary>过期时间（UTC 毫秒；0 = 永不过期）</summary>
            public long ExpireTimeMs;

            /// <summary>是否已读</summary>
            public bool Read;

            /// <summary>附件是否已领取（掩码非 0）</summary>
            public bool Claimed;

            /// <summary>附件列表（物品ID, 数量）</summary>
            public List<(int ItemId, int Count)> Items = new();
        }

        // ──────────────────────────────
        // 初始化（服务端启动时调用）
        // ──────────────────────────────

        /// <summary>幂等建表：与 Migrations\007/008 等价，表已存在则无副作用。</summary>
        public void EnsureTable()
        {
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();

            using (MySqlCommand command = new(
                """
                CREATE TABLE IF NOT EXISTS `mails` (
                  `id`             INT          NOT NULL AUTO_INCREMENT,
                  `player_id`      INT          NOT NULL,
                  `sender_name`    VARCHAR(32)  NOT NULL DEFAULT '系统',
                  `title`          VARCHAR(64)  NOT NULL,
                  `body`           VARCHAR(512) NOT NULL DEFAULT '',
                  `items`          TEXT         NOT NULL,
                  `item_mask`      INT          NOT NULL DEFAULT 0,
                  `state`          INT          NOT NULL DEFAULT 0,
                  `create_time_ms` BIGINT       NOT NULL,
                  `expire_time_ms` BIGINT       NOT NULL DEFAULT 0,
                  PRIMARY KEY (`id`),
                  KEY `idx_player_list` (`player_id`, `state`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
                """,
                connection))
            {
                command.ExecuteNonQuery();
            }

            // 运营公共邮件全局表（延迟创建：玩家拉列表时才实例化到个人邮箱）
            using (MySqlCommand command = new(
                """
                CREATE TABLE IF NOT EXISTS `public_mails` (
                  `id`              INT          NOT NULL AUTO_INCREMENT,
                  `tag`             VARCHAR(64)  NOT NULL,
                  `title`           VARCHAR(64)  NOT NULL,
                  `body`            VARCHAR(512) NOT NULL DEFAULT '',
                  `items`           TEXT         NOT NULL,
                  `state`           INT          NOT NULL DEFAULT 0,
                  `min_register_ms`  BIGINT       NOT NULL DEFAULT 0,
                  `max_register_ms`  BIGINT       NOT NULL DEFAULT 0,
                  `create_time_ms`  BIGINT       NOT NULL,
                  `expire_time_ms`  BIGINT       NOT NULL DEFAULT 0,
                  PRIMARY KEY (`id`),
                  KEY `idx_tag` (`tag`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
                """,
                connection))
            {
                command.ExecuteNonQuery();
            }

            // 公共邮件玩家实例化去重表（该玩家该封已实例化过就不再重复发）
            using (MySqlCommand command = new(
                """
                CREATE TABLE IF NOT EXISTS `player_public_mail` (
                  `player_id`      INT NOT NULL,
                  `public_mail_id` INT NOT NULL,
                  `created_at`     DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                  PRIMARY KEY (`player_id`, `public_mail_id`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
                """,
                connection))
            {
                command.ExecuteNonQuery();
            }

            // 已有 mails 表的升级路径：补 public_id 列（MySQL 不支持 ADD COLUMN IF NOT EXISTS，改查 information_schema）
            using (MySqlCommand check = new(
                "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'mails' AND column_name = 'public_id'",
                connection))
            {
                if (Convert.ToInt64(check.ExecuteScalar() ?? 0L) == 0)
                {
                    using MySqlCommand alter = new(
                        "ALTER TABLE `mails` ADD COLUMN `public_id` INT NOT NULL DEFAULT 0 AFTER `state`",
                        connection);
                    alter.ExecuteNonQuery();
                }
            }
        }

        // ──────────────────────────────
        // 查询
        // ──────────────────────────────

        /// <summary>
        /// 读取玩家全部有效邮件（未删除且未过期，按邮件ID降序 = 新邮件在前）
        /// 读取前先做撤回清理：关联的公共邮件已被撤回、且附件尚未领取的个人邮件 → 软删（已领取的保留，物品已到手撤不回）；
        /// 清理放在读路径，Read/Claim/Delete 的快照响应同样生效，玩家无感知
        /// 邮件量小不做分页，客户端整表替换
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：收件玩家ID</param>
        /// <returns>返回值类型：List，用途：有效邮件列表</returns>
        public List<MailData> GetMails(int playerId)
        {
            var mails = new List<MailData>();
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();

                // 撤回清理：公共邮件已撤回 且 该玩家未领附件的实例化邮件 → 软删（标记法，行保留可溯源）
                using (MySqlCommand cleanup = new(
                    """
                    UPDATE `mails` m
                    JOIN `public_mails` p ON p.`id` = m.`public_id`
                    SET m.`state` = m.`state` | 2
                    WHERE m.`player_id` = @PlayerId AND (m.`state` & 2) = 0 AND m.`item_mask` = 0
                      AND p.`state` = 2
                    """,
                    connection))
                {
                    cleanup.Parameters.AddWithValue("@PlayerId", playerId);
                    cleanup.ExecuteNonQuery();
                }

                using MySqlCommand command = new(
                    """
                    SELECT `id`, `sender_name`, `title`, `body`, `items`, `item_mask`, `state`, `create_time_ms`, `expire_time_ms`
                    FROM `mails`
                    WHERE `player_id` = @PlayerId AND (`state` & 2) = 0
                      AND (`expire_time_ms` = 0 OR `expire_time_ms` > @NowMs)
                    ORDER BY `id` DESC
                    """,
                    connection);
                command.Parameters.AddWithValue("@PlayerId", playerId);
                command.Parameters.AddWithValue("@NowMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                using MySqlDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    mails.Add(ReadMail(reader));
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"邮件列表读取失败 [{exception.GetType().Name}]: {exception.Message}");
            }
            return mails;
        }

        /// <summary>
        /// 按邮件ID读取单封（不过滤删除/过期——推送刚插入的邮件时用，调用方保证场景）
        /// </summary>
        /// <param name="mailId">参数名：mailId，类型：int，用途：邮件ID</param>
        /// <returns>返回值类型：MailData，用途：邮件数据（不存在返回 null）</returns>
        public MailData? GetMail(int mailId)
        {
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlCommand command = new(
                    """
                    SELECT `id`, `sender_name`, `title`, `body`, `items`, `item_mask`, `state`, `create_time_ms`, `expire_time_ms`
                    FROM `mails` WHERE `id` = @MailId
                    """,
                    connection);
                command.Parameters.AddWithValue("@MailId", mailId);
                using MySqlDataReader reader = command.ExecuteReader();
                return reader.Read() ? ReadMail(reader) : null;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"邮件读取失败 [{exception.GetType().Name}]: {exception.Message}");
                return null;
            }
        }

        // ──────────────────────────────
        // 操作
        // ──────────────────────────────

        /// <summary>
        /// 标记已读的方法（幂等：已读再标不报错，只提示已有状态）
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：收件玩家ID（归属校验，防跨玩家操作）</param>
        /// <param name="mailId">参数名：mailId，类型：int，用途：邮件ID</param>
        /// <returns>返回值类型：(bool Success, string Message)，用途：操作结果与提示</returns>
        public (bool Success, string Message) MarkRead(int playerId, int mailId)
        {
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                int state = SelectState(connection, playerId, mailId);
                if (state < 0) return (false, "邮件不存在");
                if ((state & StateDeleted) != 0) return (false, "邮件不存在");

                if ((state & StateRead) == 0)
                {
                    using MySqlCommand update = new(
                        "UPDATE `mails` SET `state` = `state` | 1 WHERE `id` = @MailId AND `player_id` = @PlayerId",
                        connection);
                    update.Parameters.AddWithValue("@MailId", mailId);
                    update.Parameters.AddWithValue("@PlayerId", playerId);
                    update.ExecuteNonQuery();
                }
                return (true, "已读");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"邮件置读失败 [{exception.GetType().Name}]: {exception.Message}");
                return (false, "操作失败，请稍后再试");
            }
        }

        /// <summary>
        /// 删除邮件的方法（软删：置状态位，不物理删行；附件未领取也允许删，由客户端确认框提示）
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：收件玩家ID（归属校验）</param>
        /// <param name="mailId">参数名：mailId，类型：int，用途：邮件ID</param>
        /// <returns>返回值类型：(bool Success, string Message)，用途：操作结果与提示</returns>
        public (bool Success, string Message) Delete(int playerId, int mailId)
        {
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                int state = SelectState(connection, playerId, mailId);
                if (state < 0) return (false, "邮件不存在");
                if ((state & StateDeleted) != 0) return (true, "已删除");   // 幂等：重复删除视为成功

                using MySqlCommand update = new(
                    "UPDATE `mails` SET `state` = `state` | 2 WHERE `id` = @MailId AND `player_id` = @PlayerId",
                    connection);
                update.Parameters.AddWithValue("@MailId", mailId);
                update.Parameters.AddWithValue("@PlayerId", playerId);
                update.ExecuteNonQuery();
                return (true, "已删除");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"邮件删除失败 [{exception.GetType().Name}]: {exception.Message}");
                return (false, "删除失败，请稍后再试");
            }
        }

        /// <summary>
        /// 领取附件的方法（一键领取全部附件，核心原子性保证）
        /// 单事务：行锁锁定邮件 → 归属/过期/重复领取校验 → 内存模拟背包空间（先补同类未满堆叠再占空槽，
        /// 全部放得下才落账，放不下整单拒绝）→ 槽位整表回写 → 置附件掩码与已读位 → 提交；
        /// 失败即回滚，附件不会被部分发放也不会被重复领取
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：收件玩家ID（归属校验）</param>
        /// <param name="mailId">参数名：mailId，类型：int，用途：邮件ID</param>
        /// <returns>返回值类型：(bool Success, string Message, List Granted)，用途：结果与提示、实际发放的附件（物品ID, 数量；任务系统接入用）</returns>
        public (bool Success, string Message, List<(int ItemId, int Count)> Granted) Claim(int playerId, int mailId)
        {
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlTransaction transaction = connection.BeginTransaction();

                int state;
                int itemMask;
                int publicId;
                string itemsJson;
                long expireTimeMs;
                using (MySqlCommand select = new(
                    """
                    SELECT `state`, `item_mask`, `items`, `expire_time_ms`, `public_id`
                    FROM `mails` WHERE `id` = @MailId AND `player_id` = @PlayerId
                    FOR UPDATE
                    """,
                    connection, transaction))
                {
                    select.Parameters.AddWithValue("@MailId", mailId);
                    select.Parameters.AddWithValue("@PlayerId", playerId);
                    using MySqlDataReader reader = select.ExecuteReader();
                    if (!reader.Read())
                    {
                        return (false, "邮件不存在", new List<(int, int)>());
                    }
                    state = reader.GetInt32(0);
                    itemMask = reader.GetInt32(1);
                    itemsJson = reader.GetString(2);
                    expireTimeMs = reader.GetInt64(3);
                    publicId = reader.GetInt32(4);
                }

                // 校验链：删除 → 过期 → 公共邮件已撤回 → 无附件 → 重复领取（顺序从"不存在"到"不可再领"）
                if ((state & StateDeleted) != 0) return (false, "邮件不存在", new List<(int, int)>());
                if (expireTimeMs > 0 && expireTimeMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    return (false, "邮件已过期", new List<(int, int)>());
                if (publicId > 0 && IsPublicMailRevoked(connection, transaction, publicId))
                    return (false, "该邮件已撤回", new List<(int, int)>());

                List<(int ItemId, int Count)> items = ParseItems(itemsJson);
                if (items.Count == 0) return (false, "该邮件没有附件", new List<(int, int)>());
                if (itemMask != 0) return (false, "附件已领取过", new List<(int, int)>());

                // 内存模拟发放：全部放得下才落账（与背包/任务发奖同规则——先补同类未满堆叠，再占空槽）
                BagSlot[] slots = BagRepository.LoadSlotsForUpdate(connection, transaction, playerId);
                Dictionary<int, int> maxStacks = LoadMaxStacks();
                foreach ((int itemId, int count) in items)
                {
                    int maxStack = maxStacks.TryGetValue(itemId, out int stack) ? Math.Max(1, stack) : 1;
                    int remaining = BagRepository.GrantIntoSlots(
                        connection, transaction, playerId, ref slots, itemId, count, maxStack);
                    if (remaining > 0)
                    {
                        return (false, "背包空间不足，请先清理背包", new List<(int, int)>());   // 整单拒绝，附件保留
                    }
                }

                BagRepository.SaveSlots(connection, transaction, playerId, slots);

                using (MySqlCommand update = new(
                    """
                    UPDATE `mails` SET `item_mask` = @ItemMask, `state` = `state` | 1
                    WHERE `id` = @MailId AND `player_id` = @PlayerId
                    """,
                    connection, transaction))
                {
                    update.Parameters.AddWithValue("@ItemMask", (1 << items.Count) - 1);   // 全部附件领取位置满位
                    update.Parameters.AddWithValue("@MailId", mailId);
                    update.Parameters.AddWithValue("@PlayerId", playerId);
                    update.ExecuteNonQuery();
                }

                transaction.Commit();
                return (true, "已领取：" + string.Join("、", items.Select(item => $"{ItemName(item.ItemId)} x{item.Count}")), items);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"邮件领取失败 [{exception.GetType().Name}]: {exception.Message}");
                return (false, "领取失败，请稍后再试", new List<(int, int)>());
            }
        }

        /// <summary>
        /// 发送系统邮件的方法（本期邮件唯一来源：GM/联调指令与后续发奖路径统一走这里）
        /// 容量按"有效邮件数"校验（未删除且未过期），超出拒绝收件
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：收件玩家ID（存在性由调用方校验）</param>
        /// <param name="title">参数名：title，类型：string，用途：标题（空则默认"系统邮件"，超长截断）</param>
        /// <param name="body">参数名：body，类型：string，用途：正文（超长截断）</param>
        /// <param name="items">参数名：items，类型：List，用途：附件（可为空 = 纯文本邮件；物品须存在于 item_config）</param>
        /// <returns>返回值类型：(bool Success, string Message, int MailId)，用途：结果与提示、新邮件ID（失败为 0）</returns>
        public (bool Success, string Message, int MailId) SendMail(
            int playerId, string title, string body, List<(int ItemId, int Count)> items)
        {
            (bool valid, string error, Dictionary<int, int> merged) = ValidateItems(items);
            if (!valid) return (false, error, 0);
            (string safeTitle, string safeBody) = SanitizeText(title, body, "系统邮件");

            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                using (MySqlCommand count = new(
                    """
                    SELECT COUNT(*) FROM `mails`
                    WHERE `player_id` = @PlayerId AND (`state` & 2) = 0
                      AND (`expire_time_ms` = 0 OR `expire_time_ms` > @NowMs)
                    """,
                    connection))
                {
                    count.Parameters.AddWithValue("@PlayerId", playerId);
                    count.Parameters.AddWithValue("@NowMs", nowMs);
                    if (Convert.ToInt64(count.ExecuteScalar()) >= MailCapacity)
                        return (false, "邮箱已满，收件失败", 0);
                }

                string itemsJson = JsonSerializer.Serialize(
                    merged.Select(pair => new MailItemRow { ItemId = pair.Key, Count = pair.Value }));

                using MySqlCommand insert = new(
                    """
                    INSERT INTO `mails`
                      (`player_id`, `sender_name`, `title`, `body`, `items`, `item_mask`, `state`, `create_time_ms`, `expire_time_ms`)
                    VALUES
                      (@PlayerId, '系统', @Title, @Body, @Items, 0, 0, @CreateTimeMs, 0)
                    """,
                    connection);
                insert.Parameters.AddWithValue("@PlayerId", playerId);
                insert.Parameters.AddWithValue("@Title", safeTitle);
                insert.Parameters.AddWithValue("@Body", safeBody);
                insert.Parameters.AddWithValue("@Items", itemsJson);
                insert.Parameters.AddWithValue("@CreateTimeMs", nowMs);
                insert.ExecuteNonQuery();

                int mailId = (int)insert.LastInsertedId;
                return (true, "已发送", mailId);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"邮件发送失败 [{exception.GetType().Name}]: {exception.Message}");
                return (false, "发送失败，请稍后再试", 0);
            }
        }

        /// <summary>
        /// 事务内直插"背包放不下转存"邮件（发奖路径统一兜底：任务奖励/背包发放放不下的剩余转邮箱）
        /// 与发奖落库共用同一个事务：奖励提交与转存邮件要么都成功要么都回滚——不出现"奖励没到、邮件也没来"。
        /// 邮箱满（容量 100）时放弃转入并大声告警（剩余物品丢失，属极端中的极端，日志可溯源）
        /// </summary>
        /// <param name="connection">参数名：connection，类型：MySqlConnection，用途：调用方事务的连接</param>
        /// <param name="transaction">参数名：transaction，类型：MySqlTransaction，用途：调用方事务</param>
        /// <param name="playerId">参数名：playerId，类型：int，用途：收件玩家ID</param>
        /// <param name="title">参数名：title，类型：string，用途：邮件标题（如"任务奖励转入邮箱"）</param>
        /// <param name="body">参数名：body，类型：string，用途：邮件正文</param>
        /// <param name="items">参数名：items，类型：List，用途：放不下的物品（物品ID, 数量）</param>
        /// <returns>返回值类型：int，用途：新邮件ID（放弃/失败为 0，调用方据日志溯源）</returns>
        public static int InsertOverflowMail(
            MySqlConnection connection, MySqlTransaction transaction,
            int playerId, string title, string body, List<(int ItemId, int Count)> items)
        {
            try
            {
                long active;
                using (MySqlCommand count = new(
                    """
                    SELECT COUNT(*) FROM `mails`
                    WHERE `player_id` = @PlayerId AND (`state` & 2) = 0
                      AND (`expire_time_ms` = 0 OR `expire_time_ms` > @NowMs)
                    """,
                    connection, transaction))
                {
                    count.Parameters.AddWithValue("@PlayerId", playerId);
                    count.Parameters.AddWithValue("@NowMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    active = Convert.ToInt64(count.ExecuteScalar());
                }
                if (active >= MailCapacity)
                {
                    Console.WriteLine($"警告：玩家{playerId} 邮箱已满，转存邮件放弃（物品丢失）：[{title}] {string.Join("、", items.Select(item => $"物品{item.ItemId}x{item.Count}"))}");
                    return 0;
                }

                string itemsJson = JsonSerializer.Serialize(
                    items.Select(pair => new MailItemRow { ItemId = pair.ItemId, Count = pair.Count }));
                using MySqlCommand insert = new(
                    """
                    INSERT INTO `mails`
                      (`player_id`, `sender_name`, `title`, `body`, `items`, `item_mask`, `state`, `create_time_ms`, `expire_time_ms`)
                    VALUES
                      (@PlayerId, '系统', @Title, @Body, @Items, 0, 0, @CreateTimeMs, 0)
                    """,
                    connection, transaction);
                insert.Parameters.AddWithValue("@PlayerId", playerId);
                insert.Parameters.AddWithValue("@Title", title);
                insert.Parameters.AddWithValue("@Body", body);
                insert.Parameters.AddWithValue("@Items", itemsJson);
                insert.Parameters.AddWithValue("@CreateTimeMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                insert.ExecuteNonQuery();
                return (int)insert.LastInsertedId;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"转存邮件插入失败 [{exception.GetType().Name}]: {exception.Message}");
                return 0;
            }
        }

        // ──────────────────────────────
        // 运营公共邮件（全局表 + 延迟创建 + 撤回）
        // ──────────────────────────────

        /// <summary>
        /// 发布运营公共邮件的方法（写全局表，不写玩家邮箱）
        /// 延迟创建（读裂变）：玩家拉取邮件列表时才由 SyncPublicMails 实例化到个人邮箱——
        /// 避免大规模写库与僵尸号无效写入，同时让撤回成为可能（已发放的邮件不宜撤回）
        /// </summary>
        /// <param name="tag">参数名：tag，类型：string，用途：运营标签（撤回按 tag 匹配；空则默认按标题）</param>
        /// <param name="title">参数名：title，类型：string，用途：标题</param>
        /// <param name="body">参数名：body，类型：string，用途：正文</param>
        /// <param name="items">参数名：items，类型：List，用途：附件（可为空）</param>
        /// <param name="minRegisterMs">参数名：minRegisterMs，类型：long，用途：注册时间筛选下限（0=不限；新玩家礼包类）</param>
        /// <param name="maxRegisterMs">参数名：maxRegisterMs，类型：long，用途：注册时间筛选上限（0=不限；存量补偿类）</param>
        /// <returns>返回值类型：(bool Success, string Message, int PublicMailId)，用途：结果与提示、新公共邮件ID</returns>
        public (bool Success, string Message, int PublicMailId) SendPublicMail(
            string tag, string title, string body, List<(int ItemId, int Count)> items, long minRegisterMs, long maxRegisterMs)
        {
            (bool valid, string error, Dictionary<int, int> merged) = ValidateItems(items);
            if (!valid) return (false, error, 0);

            string safeTag = string.IsNullOrWhiteSpace(tag) ? string.Empty : tag.Trim();
            if (safeTag.Length == 0) safeTag = string.IsNullOrWhiteSpace(title) ? "untagged" : title.Trim();
            if (safeTag.Length > MaxTitleLength) safeTag = safeTag[..MaxTitleLength];
            (string safeTitle, string safeBody) = SanitizeText(title, body, "运营邮件");

            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                string itemsJson = JsonSerializer.Serialize(
                    merged.Select(pair => new MailItemRow { ItemId = pair.Key, Count = pair.Value }));

                using MySqlCommand insert = new(
                    """
                    INSERT INTO `public_mails`
                      (`tag`, `title`, `body`, `items`, `state`, `min_register_ms`, `max_register_ms`, `create_time_ms`, `expire_time_ms`)
                    VALUES
                      (@Tag, @Title, @Body, @Items, 0, @MinRegisterMs, @MaxRegisterMs, @CreateTimeMs, 0)
                    """,
                    connection);
                insert.Parameters.AddWithValue("@Tag", safeTag);
                insert.Parameters.AddWithValue("@Title", safeTitle);
                insert.Parameters.AddWithValue("@Body", safeBody);
                insert.Parameters.AddWithValue("@Items", itemsJson);
                insert.Parameters.AddWithValue("@MinRegisterMs", minRegisterMs < 0 ? 0 : minRegisterMs);
                insert.Parameters.AddWithValue("@MaxRegisterMs", maxRegisterMs < 0 ? 0 : maxRegisterMs);
                insert.Parameters.AddWithValue("@CreateTimeMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                insert.ExecuteNonQuery();

                return (true, $"公共邮件已发布（tag={safeTag}）", (int)insert.LastInsertedId);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"公共邮件发布失败 [{exception.GetType().Name}]: {exception.Message}");
                return (false, "发布失败，请稍后再试", 0);
            }
        }

        /// <summary>
        /// 撤回公共邮件的方法（软标记：state=2，可逆可溯源，不物理删除；同 tag 一次全撤）
        /// 只影响"未领附件"的已实例化邮件（拉取时被软删、领取时被拒）；已领取附件的邮件保留——物品已到手撤不回
        /// </summary>
        /// <param name="tag">参数名：tag，类型：string，用途：要撤回的运营标签</param>
        /// <returns>返回值类型：(bool Success, string Message)，用途：结果与提示（含撤回数）</returns>
        public (bool Success, string Message) RevokePublicMail(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return (false, "tag 不能为空");
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlCommand update = new(
                    "UPDATE `public_mails` SET `state` = 2 WHERE `tag` = @Tag AND `state` = 0",
                    connection);
                update.Parameters.AddWithValue("@Tag", tag.Trim());
                int affected = update.ExecuteNonQuery();
                return affected > 0
                    ? (true, $"已撤回 {affected} 封公共邮件（tag={tag.Trim()}）")
                    : (false, "没有可撤回的公共邮件（tag 不存在或已撤回）");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"公共邮件撤回失败 [{exception.GetType().Name}]: {exception.Message}");
                return (false, "撤回失败，请稍后再试");
            }
        }

        /// <summary>
        /// 同步运营公共邮件的方法（延迟创建的读路径；拉列表前调用）
        /// 把"有效、未过期、未实例化、注册时间匹配"的公共邮件逐封实例化到个人邮箱并记录去重；
        /// 逐封小事务（邮件行 + 去重记录原子），单封失败不影响其余；邮箱满则跳过（不写去重，下次拉取再试）
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <returns>返回值类型：int，用途：本次实例化的邮件数</returns>
        public int SyncPublicMails(int playerId)
        {
            int instantiated = 0;
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();

                // 玩家注册时间（毫秒）——注册时间筛选条件的比较基准；查不到（账号异常）按 0 处理，等于"无筛选命中"
                long registerMs = 0;
                using (MySqlCommand register = new(
                    "SELECT CAST(UNIX_TIMESTAMP(`CreatedAt`) AS SIGNED) * 1000 FROM accounts WHERE `Id` = @PlayerId",
                    connection))
                {
                    register.Parameters.AddWithValue("@PlayerId", playerId);
                    object? result = register.ExecuteScalar();
                    if (result != null && result != DBNull.Value) registerMs = Convert.ToInt64(result);
                }
                if (registerMs <= 0) return 0;

                // 候选：有效 + 未过期 + 未实例化 + 注册时间在筛选区间内
                var candidates = new List<(int Id, string Title, string Body, string Items)>();
                using (MySqlCommand select = new(
                    """
                    SELECT p.`id`, p.`title`, p.`body`, p.`items`
                    FROM `public_mails` p
                    WHERE p.`state` = 0
                      AND (p.`expire_time_ms` = 0 OR p.`expire_time_ms` > @NowMs)
                      AND NOT EXISTS (SELECT 1 FROM `player_public_mail` d
                                     WHERE d.`player_id` = @PlayerId AND d.`public_mail_id` = p.`id`)
                      AND (p.`min_register_ms` = 0 OR @RegisterMs >= p.`min_register_ms`)
                      AND (p.`max_register_ms` = 0 OR @RegisterMs <= p.`max_register_ms`)
                    """,
                    connection))
                {
                    select.Parameters.AddWithValue("@NowMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    select.Parameters.AddWithValue("@PlayerId", playerId);
                    select.Parameters.AddWithValue("@RegisterMs", registerMs);
                    using MySqlDataReader reader = select.ExecuteReader();
                    while (reader.Read())
                    {
                        candidates.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
                    }
                }

                if (candidates.Count == 0) return 0;

                // 当前有效邮件数（容量约束：实例化也要占邮箱容量，满则留待下次）
                int activeCount = 0;
                using (MySqlCommand count = new(
                    """
                    SELECT COUNT(*) FROM `mails`
                    WHERE `player_id` = @PlayerId AND (`state` & 2) = 0
                      AND (`expire_time_ms` = 0 OR `expire_time_ms` > @NowMs)
                    """,
                    connection))
                {
                    count.Parameters.AddWithValue("@PlayerId", playerId);
                    count.Parameters.AddWithValue("@NowMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    activeCount = Convert.ToInt32(count.ExecuteScalar());
                }

                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                foreach ((int id, string title, string body, string items) in candidates)
                {
                    if (activeCount >= MailCapacity)
                    {
                        Console.WriteLine($"公共邮件同步跳过：玩家{playerId} 邮箱已满（#{id} {title} 留待下次拉取）");
                        break;
                    }

                    using MySqlTransaction transaction = connection.BeginTransaction();
                    using (MySqlCommand insertMail = new(
                        """
                        INSERT INTO `mails`
                          (`player_id`, `sender_name`, `title`, `body`, `items`, `item_mask`, `state`, `create_time_ms`, `expire_time_ms`, `public_id`)
                        VALUES
                          (@PlayerId, '运营', @Title, @Body, @Items, 0, 0, @CreateTimeMs, 0, @PublicId)
                        """,
                        connection, transaction))
                    {
                        insertMail.Parameters.AddWithValue("@PlayerId", playerId);
                        insertMail.Parameters.AddWithValue("@Title", title);
                        insertMail.Parameters.AddWithValue("@Body", body);
                        insertMail.Parameters.AddWithValue("@Items", items);   // 附件原样沿用公共邮件定义
                        insertMail.Parameters.AddWithValue("@CreateTimeMs", nowMs);
                        insertMail.Parameters.AddWithValue("@PublicId", id);
                        insertMail.ExecuteNonQuery();
                    }
                    using (MySqlCommand insertDedup = new(
                        "INSERT INTO `player_public_mail` (`player_id`, `public_mail_id`) VALUES (@PlayerId, @PublicId)",
                        connection, transaction))
                    {
                        insertDedup.Parameters.AddWithValue("@PlayerId", playerId);
                        insertDedup.Parameters.AddWithValue("@PublicId", id);
                        insertDedup.ExecuteNonQuery();
                    }
                    transaction.Commit();

                    activeCount++;
                    instantiated++;
                    Console.WriteLine($"公共邮件实例化：玩家{playerId} ← 公共邮件#{id} {title}");
                }
            }
            catch (Exception exception)
            {
                // 同步失败只告警不抛出——邮件列表仍按已有数据返回，下次拉取重试
                Console.WriteLine($"公共邮件同步失败 [{exception.GetType().Name}]: {exception.Message}");
            }
            return instantiated;
        }

        // ──────────────────────────────
        // 内部工具
        // ──────────────────────────────

        /// <summary>
        /// 附件校验与合并（系统邮件与公共邮件共用）：
        /// 数量范围沿用背包上限、同ID合并、种类上限 8、物品必须存在于 item_config
        /// </summary>
        private (bool Valid, string Error, Dictionary<int, int> Merged) ValidateItems(List<(int ItemId, int Count)> items)
        {
            var merged = new Dictionary<int, int>();
            foreach ((int itemId, int count) in items)
            {
                if (itemId <= 0 || count <= 0 || count > BagRepository.MaxOperationCount)
                    return (false, "附件数量须在 1-" + BagRepository.MaxOperationCount + " 之间", merged);
                merged[itemId] = merged.TryGetValue(itemId, out int existing) ? existing + count : count;
            }
            if (merged.Count > MaxMailItems) return (false, $"附件种类最多 {MaxMailItems} 种", merged);
            if (merged.Count > 0)
            {
                Dictionary<int, int> maxStacks = LoadMaxStacks();
                foreach (int itemId in merged.Keys)
                {
                    if (!maxStacks.ContainsKey(itemId)) return (false, $"物品{itemId}不存在", merged);
                }
            }
            return (true, string.Empty, merged);
        }

        /// <summary>标题/正文清洗（空标题用默认文案，超长截断）。</summary>
        private static (string Title, string Body) SanitizeText(string title, string body, string defaultTitle)
        {
            string safeTitle = string.IsNullOrWhiteSpace(title) ? defaultTitle : title.Trim();
            if (safeTitle.Length > MaxTitleLength) safeTitle = safeTitle[..MaxTitleLength];
            string safeBody = body ?? string.Empty;
            if (safeBody.Length > MaxBodyLength) safeBody = safeBody[..MaxBodyLength];
            return (safeTitle, safeBody);
        }

        /// <summary>公共邮件是否已撤回（行不存在视为已撤回——fail closed，宁可不发也不误发）。</summary>
        private static bool IsPublicMailRevoked(MySqlConnection connection, MySqlTransaction transaction, int publicId)
        {
            using MySqlCommand command = new(
                "SELECT `state` FROM `public_mails` WHERE `id` = @PublicId", connection, transaction);
            command.Parameters.AddWithValue("@PublicId", publicId);
            object? result = command.ExecuteScalar();
            return result == null || result == DBNull.Value || Convert.ToInt32(result) == 2;
        }

        /// <summary>读取邮件行（列序与查询语句一致；items 解析失败按无附件处理——历史脏数据防御）。</summary>
        private static MailData ReadMail(MySqlDataReader reader)
        {
            var mail = new MailData
            {
                MailId = reader.GetInt32(0),
                SenderName = reader.GetString(1),
                Title = reader.GetString(2),
                Body = reader.GetString(3),
                Claimed = reader.GetInt32(5) != 0,
                Read = (reader.GetInt32(6) & StateRead) != 0,
                CreateTimeMs = reader.GetInt64(7),
                ExpireTimeMs = reader.GetInt64(8),
                Items = ParseItems(reader.GetString(4)),
            };
            return mail;
        }

        /// <summary>解析附件 JSON（异常与非法条目按空处理，不让单封坏邮件拖垮列表）。</summary>
        private static List<(int ItemId, int Count)> ParseItems(string json)
        {
            var items = new List<(int, int)>();
            if (string.IsNullOrWhiteSpace(json)) return items;
            try
            {
                foreach (MailItemRow row in JsonSerializer.Deserialize<List<MailItemRow>>(json) ?? new List<MailItemRow>())
                {
                    if (row.ItemId > 0 && row.Count > 0) items.Add((row.ItemId, row.Count));
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"邮件附件解析失败 [{exception.GetType().Name}]: {exception.Message}");
            }
            return items;
        }

        /// <summary>读取邮件当前状态位（-1 = 不存在或非本人邮件；归属校验内建在查询里）。</summary>
        private static int SelectState(MySqlConnection connection, int playerId, int mailId)
        {
            using MySqlCommand command = new(
                "SELECT `state` FROM `mails` WHERE `id` = @MailId AND `player_id` = @PlayerId",
                connection);
            command.Parameters.AddWithValue("@MailId", mailId);
            command.Parameters.AddWithValue("@PlayerId", playerId);
            object? result = command.ExecuteScalar();
            return result == null ? -1 : Convert.ToInt32(result);
        }

        /// <summary>读取物品名（领取明细文案用；走 BagRepository 统一配置缓存，查不到回退成 #ID）。</summary>
        private static string ItemName(int itemId)
        {
            return BagRepository.GetConfigMap().TryGetValue(itemId, out BagRepository.ItemConfig? config)
                ? config.Name
                : $"#{itemId}";
        }

        /// <summary>读取全部物品的堆叠上限（模拟发放按上限拆堆用；走 BagRepository 统一配置缓存，不再全表读 item_config）。</summary>
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
