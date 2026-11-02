using MySqlConnector;

namespace CompeteServer.Database
{
    /// <summary>
    /// 装备仓库类（服务端权威）
    /// 三层职责互不重叠：equipment_config 描述"这一类装备"、equipment_instance 是"玩家拥有的这一件"、
    /// player_equipment 记录"谁穿在哪个槽"；背包只管"有哪些"（player_items.instance_id 指向实例）。
    /// 穿脱的归属/角色/槽位校验与跨角色原子转移都在本类单事务内完成，客户端只发意图、渲染回执。
    /// 表结构见 Migrations\018_AddEquipment.sql（EnsureTable 提供幂等建表兜底）。
    /// </summary>
    public class EquipmentRepository
    {
        /// <summary>槽位类型：武器（本期唯一槽位；2/3 预留护甲/饰品）。</summary>
        public const int SlotWeapon = 1;

        /// <summary>
        /// 本期可装备的角色ID（与客户端 BattleHeroCatalog 对齐：0 安比 / 1 艾莲）。
        /// 角色拥有体系未做，所有账号都可用这两名角色；将来加 player_characters 时改这一处。
        /// </summary>
        private static readonly int[] KnownCharIds = { 0, 1 };

        // 穿戴/卸下结果码（与 EquipmentPacket.proto 的 EquipResponse.code 一一对应）
        public const int CodeOk = 0;
        public const int CodeNotLoggedIn = 1;
        public const int CodeInstanceInvalid = 2;
        public const int CodeNotEquipment = 3;
        public const int CodeSlotMismatch = 4;
        public const int CodeCharUnavailable = 5;
        public const int CodeStorageError = 6;

        /// <summary>装备配置行（equipment_config 一行）</summary>
        public sealed record EquipConfig(
            int ItemId, int SlotType, int WeaponType, string AllowedChars, string AttrJson, string ModelKey);

        /// <summary>装备实例行（带"穿在谁身上"，-1/0 表示未装备）</summary>
        public sealed record EquipInstance(
            long InstanceId, int ItemId, int EnhanceLevel, string AffixJson, int EquippedChar, int EquippedSlot);

        /// <summary>穿戴关系行（角色 × 槽位 → 实例）</summary>
        public sealed record EquipSlot(int CharId, int SlotType, long InstanceId);

        /// <summary>3501 回执数据（配置 + 实例 + 槽位，数量级几十条不分页）</summary>
        public sealed record EquipSnapshot(
            List<EquipConfig> Configs, List<EquipInstance> Instances, List<EquipSlot> Slots);

        // ──────────────────────────────
        // 初始化（服务端启动时调用）
        // ──────────────────────────────

        /// <summary>幂等建表：与 Migrations\018_AddEquipment.sql 等价，表已存在则无副作用。</summary>
        public void EnsureTable()
        {
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();

            using (MySqlCommand command = new(
                """
                CREATE TABLE IF NOT EXISTS `equipment_config` (
                  `item_id`       INT          NOT NULL,
                  `slot_type`     INT          NOT NULL DEFAULT 1,
                  `weapon_type`   INT          NOT NULL DEFAULT 0,
                  `allowed_chars` VARCHAR(64)  NOT NULL DEFAULT '',
                  `attr_json`     TEXT         NOT NULL,
                  `model_key`     VARCHAR(128) NOT NULL DEFAULT '',
                  PRIMARY KEY (`item_id`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
                """,
                connection))
            {
                command.ExecuteNonQuery();
            }

            using (MySqlCommand command = new(
                """
                CREATE TABLE IF NOT EXISTS `equipment_instance` (
                  `instance_id`   BIGINT       NOT NULL AUTO_INCREMENT,
                  `player_id`     INT          NOT NULL,
                  `item_id`       INT          NOT NULL,
                  `enhance_level` INT          NOT NULL DEFAULT 0,
                  `affix_json`    TEXT         NOT NULL,
                  `created_at`    DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
                  PRIMARY KEY (`instance_id`),
                  KEY `idx_player` (`player_id`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
                """,
                connection))
            {
                command.ExecuteNonQuery();
            }

            using (MySqlCommand command = new(
                """
                CREATE TABLE IF NOT EXISTS `player_equipment` (
                  `player_id`   INT      NOT NULL,
                  `char_id`     INT      NOT NULL,
                  `slot_type`   INT      NOT NULL,
                  `instance_id` BIGINT   NOT NULL,
                  `updated_at`  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                  PRIMARY KEY (`player_id`, `char_id`, `slot_type`),
                  KEY `idx_instance` (`instance_id`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
                """,
                connection))
            {
                command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// 幂等种子：写入首批武器配置（INSERT IGNORE，已存在的 item_id 不覆盖，方便策划改表后不被重置）。
        /// 数值只用于验证"装备确实影响伤害"，最终数值由策划定；attr_json.type 是 AttrType 整数值
        /// （0 DamageUp / 1 DamageDown / 2 MoveSpeed / 3 Attack / 4 Defense，枚举只允许末尾追加）。
        /// </summary>
        public void SeedConfigs()
        {
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlCommand command = new(
                """
                INSERT IGNORE INTO `equipment_config`
                  (`item_id`, `slot_type`, `weapon_type`, `allowed_chars`, `attr_json`, `model_key`)
                VALUES
                  (1012, 1, 1, '', '[{"type":3,"value":24},{"type":0,"value":0.04}]', ''),
                  (1013, 1, 2, '', '[{"type":3,"value":22},{"type":2,"value":0.03}]', ''),
                  (1014, 1, 3, '', '[{"type":3,"value":32},{"type":0,"value":0.06}]', ''),
                  (1015, 1, 4, '', '[{"type":3,"value":30},{"type":1,"value":0.05}]', ''),
                  (1016, 1, 5, '', '[{"type":3,"value":44},{"type":0,"value":0.10}]', ''),
                  (1017, 1, 1, '', '[{"type":3,"value":48},{"type":0,"value":0.08}]', ''),
                  (1018, 1, 2, '', '[{"type":3,"value":46},{"type":2,"value":0.06}]', '')
                """,
                connection);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// 历史武器回填：把"装备类物品但还没有实例ID"的背包行补建实例并回写引用。
        /// 装备系统之前发放的武器（抽卡/掉落/邮件/任务）都只有 item_id + count，没有实例就穿不上。
        /// 逐行处理且只认 instance_id IS NULL 的行，重复执行无副作用。
        /// </summary>
        public void BackfillInstances()
        {
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlTransaction transaction = connection.BeginTransaction();

            var pending = new List<(int PlayerId, int SlotIndex, int ItemId)>();
            using (MySqlCommand select = new(
                """
                SELECT i.`player_id`, i.`slot_index`, i.`item_id`
                FROM `player_items` i
                JOIN `equipment_config` c ON c.`item_id` = i.`item_id`
                WHERE i.`instance_id` IS NULL
                FOR UPDATE
                """,
                connection, transaction))
            using (MySqlDataReader reader = select.ExecuteReader())
            {
                while (reader.Read())
                {
                    pending.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)));
                }
            }

            foreach ((int playerId, int slotIndex, int itemId) in pending)
            {
                long instanceId = BagRepository.CreateEquipmentInstance(connection, transaction, playerId, itemId);
                using MySqlCommand update = new(
                    "UPDATE `player_items` SET `instance_id` = @InstanceId, `count` = 1 WHERE `player_id` = @PlayerId AND `slot_index` = @SlotIndex",
                    connection, transaction);
                update.Parameters.AddWithValue("@InstanceId", instanceId);
                update.Parameters.AddWithValue("@PlayerId", playerId);
                update.Parameters.AddWithValue("@SlotIndex", slotIndex);
                update.ExecuteNonQuery();
            }

            transaction.Commit();
            if (pending.Count > 0)
            {
                Console.WriteLine($"装备历史回填：{pending.Count} 件已有武器补建实例");
            }
        }

        // ──────────────────────────────
        // 查询
        // ──────────────────────────────

        /// <summary>读取全部装备配置（7 条量级，直接读库，不做缓存避免手工改表后不生效）。</summary>
        public List<EquipConfig> GetConfigs()
        {
            var rows = new List<EquipConfig>();
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlCommand command = new(
                "SELECT `item_id`, `slot_type`, `weapon_type`, `allowed_chars`, `attr_json`, `model_key` FROM `equipment_config` ORDER BY `item_id`",
                connection);
            using MySqlDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new EquipConfig(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5)));
            }
            return rows;
        }

        /// <summary>
        /// 读取玩家的装备全量快照（配置 + 本人实例 + 本人槽位）。
        /// 实例带"穿在谁身上"（LEFT JOIN 槽位表，未装备为 -1/0），客户端据此标注"已装备：某某"。
        /// </summary>
        public EquipSnapshot Load(int playerId)
        {
            List<EquipConfig> configs = GetConfigs();
            var instances = new List<EquipInstance>();
            var slots = new List<EquipSlot>();

            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();

            using (MySqlCommand command = new(
                """
                SELECT i.`instance_id`, i.`item_id`, i.`enhance_level`, i.`affix_json`,
                       COALESCE(e.`char_id`, -1), COALESCE(e.`slot_type`, 0)
                FROM `equipment_instance` i
                LEFT JOIN `player_equipment` e ON e.`player_id` = i.`player_id` AND e.`instance_id` = i.`instance_id`
                WHERE i.`player_id` = @PlayerId
                ORDER BY i.`instance_id`
                """,
                connection))
            {
                command.Parameters.AddWithValue("@PlayerId", playerId);
                using MySqlDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    instances.Add(new EquipInstance(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2),
                        reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5)));
                }
            }

            using (MySqlCommand command = new(
                "SELECT `char_id`, `slot_type`, `instance_id` FROM `player_equipment` WHERE `player_id` = @PlayerId ORDER BY `char_id`, `slot_type`",
                connection))
            {
                command.Parameters.AddWithValue("@PlayerId", playerId);
                using MySqlDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    slots.Add(new EquipSlot(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt64(2)));
                }
            }

            return new EquipSnapshot(configs, instances, slots);
        }

        // ──────────────────────────────
        // 穿戴 / 卸下（单事务）
        // ──────────────────────────────

        /// <summary>
        /// 穿戴或卸下的方法（instanceId=0 表示清空该槽）。
        /// 校验全部在写之前完成，任何一条不通过直接返回失败码，不产生"半个槽位换了一半"的中间态。
        /// 同一件武器被另一个角色穿着时做原子转移：同事务删旧槽、写新槽（客户端负责二次确认）。
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID（会话绑定，不可伪造）</param>
        /// <param name="charId">参数名：charId，类型：int，用途：角色ID</param>
        /// <param name="slotType">参数名：slotType，类型：int，用途：槽位类型（1=武器）</param>
        /// <param name="instanceId">参数名：instanceId，类型：long，用途：要穿上的实例ID（0=卸下）</param>
        /// <returns>返回值类型：(bool Success, int Code, string Message, bool Unchanged)，用途：结果码与幂等命中标记</returns>
        public (bool Success, int Code, string Message, bool Unchanged) SetSlot(
            int playerId, int charId, int slotType, long instanceId)
        {
            if (playerId <= 0) return (false, CodeNotLoggedIn, "请先登录", false);

            EquipConfig? config = null;
            if (instanceId > 0)
            {
                EquipInstance? instance = FindInstance(playerId, instanceId);
                if (instance == null) return (false, CodeInstanceInvalid, "装备不存在或不属于你", false);

                config = GetConfigs().FirstOrDefault(c => c.ItemId == instance.ItemId);
                if (config == null) return (false, CodeNotEquipment, "该物品不是装备", false);
                if (config.SlotType != slotType) return (false, CodeSlotMismatch, "槽位类型不匹配", false);
            }
            else if (slotType != SlotWeapon)
            {
                return (false, CodeSlotMismatch, "槽位类型不匹配", false);
            }

            if (!KnownCharIds.Contains(charId)) return (false, CodeCharUnavailable, "角色不可用", false);
            if (config != null && !IsCharAllowed(config, charId))
            {
                return (false, CodeCharUnavailable, "该角色不能使用这件装备", false);
            }

            using IDisposable writeLock = PlayerWriteLock.Acquire(playerId);
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlTransaction transaction = connection.BeginTransaction();
                BagRepository.LockPlayerRow(connection, transaction, playerId);

                // 目标槽当前内容（行锁）：幂等判定与覆盖都基于这一份
                long currentInstanceId = 0;
                using (MySqlCommand select = new(
                    """
                    SELECT `instance_id` FROM `player_equipment`
                    WHERE `player_id` = @PlayerId AND `char_id` = @CharId AND `slot_type` = @SlotType
                    FOR UPDATE
                    """,
                    connection, transaction))
                {
                    select.Parameters.AddWithValue("@PlayerId", playerId);
                    select.Parameters.AddWithValue("@CharId", charId);
                    select.Parameters.AddWithValue("@SlotType", slotType);
                    object? result = select.ExecuteScalar();
                    currentInstanceId = result == null ? 0 : Convert.ToInt64(result);
                }

                if (instanceId == currentInstanceId)
                {
                    transaction.Commit();
                    return (true, CodeOk, instanceId > 0 ? "该装备已在此槽位" : "该槽位本就没有装备", true);
                }

                // 同一实例若穿在别处：同事务删旧槽（原子转移，不会出现两个角色同时持有）
                int previousChar = -1;
                if (instanceId > 0)
                {
                    using MySqlCommand select = new(
                        """
                        SELECT `char_id` FROM `player_equipment`
                        WHERE `player_id` = @PlayerId AND `instance_id` = @InstanceId AND NOT (`char_id` = @CharId AND `slot_type` = @SlotType)
                        """,
                        connection, transaction);
                    select.Parameters.AddWithValue("@PlayerId", playerId);
                    select.Parameters.AddWithValue("@InstanceId", instanceId);
                    select.Parameters.AddWithValue("@CharId", charId);
                    select.Parameters.AddWithValue("@SlotType", slotType);
                    object? result = select.ExecuteScalar();
                    if (result != null)
                    {
                        previousChar = Convert.ToInt32(result);
                        using MySqlCommand delete = new(
                            "DELETE FROM `player_equipment` WHERE `player_id` = @PlayerId AND `instance_id` = @InstanceId",
                            connection, transaction);
                        delete.Parameters.AddWithValue("@PlayerId", playerId);
                        delete.Parameters.AddWithValue("@InstanceId", instanceId);
                        delete.ExecuteNonQuery();
                    }
                }

                if (instanceId > 0)
                {
                    using MySqlCommand upsert = new(
                        """
                        INSERT INTO `player_equipment` (`player_id`, `char_id`, `slot_type`, `instance_id`)
                        VALUES (@PlayerId, @CharId, @SlotType, @InstanceId)
                        ON DUPLICATE KEY UPDATE `instance_id` = VALUES(`instance_id`)
                        """,
                        connection, transaction);
                    upsert.Parameters.AddWithValue("@PlayerId", playerId);
                    upsert.Parameters.AddWithValue("@CharId", charId);
                    upsert.Parameters.AddWithValue("@SlotType", slotType);
                    upsert.Parameters.AddWithValue("@InstanceId", instanceId);
                    upsert.ExecuteNonQuery();
                }
                else
                {
                    using MySqlCommand delete = new(
                        "DELETE FROM `player_equipment` WHERE `player_id` = @PlayerId AND `char_id` = @CharId AND `slot_type` = @SlotType",
                        connection, transaction);
                    delete.Parameters.AddWithValue("@PlayerId", playerId);
                    delete.Parameters.AddWithValue("@CharId", charId);
                    delete.Parameters.AddWithValue("@SlotType", slotType);
                    delete.ExecuteNonQuery();
                }

                transaction.Commit();

                string message = instanceId <= 0
                    ? "已卸下"
                    : previousChar >= 0 ? $"已装备（该武器原属角色{previousChar}，已自动转移）" : "已装备";
                Console.WriteLine($"装备变更:玩家{playerId} 角色{charId} 槽{slotType} → 实例{instanceId}{(previousChar >= 0 ? $"（原角色{previousChar}）" : string.Empty)}");
                return (true, CodeOk, message, false);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"装备变更失败 [{exception.GetType().Name}]: {exception.Message}（player={playerId} char={charId} instance={instanceId}）");
                return (false, CodeStorageError, "操作失败，请稍后再试", false);
            }
        }

        // ──────────────────────────────
        // 内部工具
        // ──────────────────────────────

        /// <summary>按实例ID读取本人实例（不存在或不属于本人返回 null，对应失败码 2）。</summary>
        private static EquipInstance? FindInstance(int playerId, long instanceId)
        {
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlCommand command = new(
                """
                SELECT i.`instance_id`, i.`item_id`, i.`enhance_level`, i.`affix_json`,
                       COALESCE(e.`char_id`, -1), COALESCE(e.`slot_type`, 0)
                FROM `equipment_instance` i
                LEFT JOIN `player_equipment` e ON e.`player_id` = i.`player_id` AND e.`instance_id` = i.`instance_id`
                WHERE i.`player_id` = @PlayerId AND i.`instance_id` = @InstanceId
                """,
                connection);
            command.Parameters.AddWithValue("@PlayerId", playerId);
            command.Parameters.AddWithValue("@InstanceId", instanceId);
            using MySqlDataReader reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            return new EquipInstance(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2),
                reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5));
        }

        /// <summary>角色白名单判断（空 = 全部角色可用；否则逗号分隔的 char_id 列表）。</summary>
        private static bool IsCharAllowed(EquipConfig config, int charId)
        {
            if (string.IsNullOrWhiteSpace(config.AllowedChars)) return true;
            foreach (string part in config.AllowedChars.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(part, out int allowed) && allowed == charId) return true;
            }
            return false;
        }
    }
}
