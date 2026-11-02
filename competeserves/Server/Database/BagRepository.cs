using MySqlConnector;

namespace CompeteServer.Database
{
    /// <summary>
    /// 背包槽位快照（下标即槽位，空槽全为 0）。
    /// InstanceId 只对装备行非 0：同一 item_id 的多件武器是不同实例（强化/词条不同），
    /// 靠它区分"这一件"；非装备行恒为 0（见 EquipmentRepository）。
    /// </summary>
    internal struct BagSlot
    {
        public int ItemId;
        public int Count;
        public long InstanceId;

        public BagSlot(int itemId, int count, long instanceId = 0)
        {
            ItemId = itemId;
            Count = count;
            InstanceId = instanceId;
        }
    }

    /// <summary>
    /// 背包仓库类（服务端权威）
    /// 数据分层：item_config = 物品静态配置（放 MySQL，客户端打开背包时随列表响应拉取）
    ///           player_items = 玩家槽位（只存非空槽，空槽不入库）
    /// 槽位空间分两段（同一个 slot_index 连续空间，不新增表/列）：
    ///   武器区 [0, WeaponCapacity) = 装备实例，固定上限，满则转邮件；
    ///   道具区 [WeaponCapacity, ∞) = 可堆叠物品，按需扩容，永不"满"。
    /// 规则集中在本类：容量与扩容、堆叠上限读 item_config.max_stack；数量校验与合并排序都在服务端做，
    /// 客户端只渲染服务端返回的槽位快照（防作弊、规则单点）
    /// 表结构见 Migrations\004_AddBag.sql（EnsureTable 提供幂等建表兜底）
    /// </summary>
    public class BagRepository
    {
        /// <summary>武器区容量（定长槽位数 = 500；客户端 UI 左侧"武器背包"页签的上限，只此一处可改）。</summary>
        public const int WeaponCapacity = 500;

        /// <summary>道具区起始槽位（= 武器区容量）。道具区无固定上限，只在这里划界。</summary>
        public const int ItemRegionStart = WeaponCapacity;

        /// <summary>道具区最小容量（20 行 × 6 列）：一件道具都没有时客户端也要有格子可看。</summary>
        public const int ItemCapacityFloor = 120;

        /// <summary>道具区扩容步长（10 行 × 6 列）：用满后按整行追加，避免每加一件就扩容。</summary>
        public const int ItemCapacityStep = 60;

        /// <summary>单次操作数量上限（防手滑上报天文数字）。</summary>
        public const int MaxOperationCount = 9999;

        /// <summary>
        /// 道具区容量推导（客户端用同一公式，避免改协议）：按最高道具槽向上取整到步长，且不小于基数。
        /// </summary>
        /// <param name="maxSlotIndex">参数名：maxSlotIndex，类型：int，用途：快照里最高的槽位下标（没有道具传 -1）</param>
        /// <returns>返回值类型：int，用途：道具区容量（含基数）</returns>
        public static int ItemCapacityFor(int maxSlotIndex)
        {
            if (maxSlotIndex < ItemRegionStart) return ItemCapacityFloor;
            int used = maxSlotIndex - ItemRegionStart + 1;
            int rounded = ((used + ItemCapacityStep - 1) / ItemCapacityStep) * ItemCapacityStep;
            return Math.Max(ItemCapacityFloor, rounded);
        }

        /// <summary>按最高槽位算出槽位数组总长度（武器区 + 道具区）。</summary>
        internal static int SlotCountFor(int maxSlotIndex) => ItemRegionStart + ItemCapacityFor(maxSlotIndex);

        // ──────────────────────────────
        // 初始化（服务端启动时调用）
        // ──────────────────────────────

        /// <summary>幂等建表：与 Migrations\004_AddBag.sql 等价，表已存在则无副作用。</summary>
        public void EnsureTable()
        {
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();

            using (MySqlCommand command = new(
                """
                CREATE TABLE IF NOT EXISTS `item_config` (
                  `item_id`     INT          NOT NULL,
                  `name`        VARCHAR(64)  NOT NULL,
                  `item_type`   INT          NOT NULL DEFAULT 0,
                  `rarity`      INT          NOT NULL DEFAULT 1,
                  `icon_key`    VARCHAR(128) NOT NULL DEFAULT '',
                  `description` VARCHAR(255) NOT NULL DEFAULT '',
                  `max_stack`   INT          NOT NULL DEFAULT 1,
                  PRIMARY KEY (`item_id`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
                """,
                connection))
            {
                command.ExecuteNonQuery();
            }

            using (MySqlCommand command = new(
                """
                CREATE TABLE IF NOT EXISTS `player_items` (
                  `player_id`  INT NOT NULL,
                  `slot_index` INT NOT NULL,
                  `item_id`    INT NOT NULL,
                  `count`      INT NOT NULL,
                  `instance_id` BIGINT NULL DEFAULT NULL,
                  `updated_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                  PRIMARY KEY (`player_id`, `slot_index`),
                  KEY `idx_player_item` (`player_id`, `item_id`),
                  KEY `idx_player_instance` (`player_id`, `instance_id`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
                """,
                connection))
            {
                command.ExecuteNonQuery();
            }

            // 已有表的升级路径：补 instance_id 列（装备行指向 equipment_instance；MySQL 不支持 ADD COLUMN IF NOT EXISTS，改查 information_schema）
            using (MySqlCommand check = new(
                "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'player_items' AND column_name = 'instance_id'",
                connection))
            {
                if (Convert.ToInt64(check.ExecuteScalar() ?? 0L) == 0)
                {
                    using MySqlCommand alter = new(
                        "ALTER TABLE `player_items` ADD COLUMN `instance_id` BIGINT NULL DEFAULT NULL, ADD KEY `idx_player_instance` (`player_id`, `instance_id`)",
                        connection);
                    alter.ExecuteNonQuery();
                }
            }
        }

        /// <summary>
        /// 幂等种子：写入物品静态配置（INSERT IGNORE，已存在的 item_id 不覆盖，方便手工改库后不被重置）。
        /// 种子条目对应 Assets/Resource/ui 下的真实图标文件，icon_key = 文件名（无扩展名）。
        /// </summary>
        public void SeedItemConfigs()
        {
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlCommand command = new(
                """
                INSERT IGNORE INTO `item_config`
                  (`item_id`, `name`, `item_type`, `rarity`, `icon_key`, `description`, `max_stack`)
                VALUES
                  (1001, '治疗药剂',   1, 2, 'T_IconA80_02_UI',          '使用后恢复少量生命值。', 99),
                  (1002, '高级治疗药剂', 1, 3, 'T_IconA80_03_UI',         '使用后恢复大量生命值。', 99),
                  (1003, '铁矿',       2, 1, 'T_IconWup80_Mo_015_UI',    '常见矿石，用于武器锻造。', 999),
                  (1004, '铜矿',       2, 1, 'T_IconWup80_Mo_016_UI',    '常见矿石，用于武器锻造。', 999),
                  (1005, '银矿',       2, 2, 'T_IconWup80_Mo_017_UI',    '稀有矿石，用于武器锻造。', 999),
                  (1006, '金矿',       2, 3, 'T_IconWup80_Mo_018_UI',    '稀有矿石，用于武器锻造。', 999),
                  (1007, '强化石',     2, 2, 'T_IconWup80_Setpup_001_1_UI', '用于强化武器的基础材料。', 99),
                  (1008, '精炼石·壹',  2, 2, 'T_IconWup80_Setpup_004_1_UI', '精炼材料，等级一。', 99),
                  (1009, '精炼石·贰',  2, 3, 'T_IconWup80_Setpup_004_2_UI', '精炼材料，等级二。', 99),
                  (1010, '精炼石·叁',  2, 4, 'T_IconWup80_Setpup_004_3_UI', '精炼材料，等级三。', 99),
                  (1011, '精炼石·肆',  2, 5, 'T_IconWup80_Setpup_004_4_UI', '精炼材料，等级四。', 99),
                  (1012, '长刃·初',    3, 3, 'T_Luckdraw21010013_UI',    '新手长刃，攻击力平平。', 1),
                  (1013, '迅刀·初',    3, 3, 'T_Luckdraw21020013_UI',    '新手迅刀，出手极快。', 1),
                  (1014, '佩枪·初',    3, 4, 'T_Luckdraw21030013_UI',    '制式佩枪，射程优秀。', 1),
                  (1015, '臂铠·初',    3, 4, 'T_Luckdraw21040013_UI',    '制式臂铠，攻守兼备。', 1),
                  (1016, '音感仪·初',  3, 5, 'T_Luckdraw21050013_UI',    '共鸣装置，增幅显著。', 1),
                  (1017, '长刃·终',    3, 5, 'T_Luckdraw21010064_UI',    '传奇长刃，削铁如泥。', 1),
                  (1018, '迅刀·终',    3, 5, 'T_Luckdraw21020064_UI',    '传奇迅刀，疾风骤雨。', 1),
                  (2001, '唤取媒介',   2, 4, 'T_IconWup80_Setpup_001_1_UI', '用于武器唤取的共鸣结晶，单次唤取消耗 1 枚，十连消耗 10 枚。', 999)
                """,
                connection);
            command.ExecuteNonQuery();

            // 幂等修正：早期种子把 7 件武器的 icon_key 指到了 T_LuckdraWeapon0x
            // （2356x1440 白底展示图，缩到背包格子里是白方块，不适合当图标），
            // 这里只修"仍是旧值"的行 → 换成正式 UI 图标（T_Luckdraw21xxxxx_UI，404x560 透明底）；
            // 手工改过 icon_key 的行不受影响（与 INSERT IGNORE 的语义保持一致）
            using MySqlCommand fixWeaponIcons = new(
                """
                UPDATE `item_config` SET `icon_key` = CASE `item_id`
                  WHEN 1012 THEN 'T_Luckdraw21010013_UI'
                  WHEN 1013 THEN 'T_Luckdraw21020013_UI'
                  WHEN 1014 THEN 'T_Luckdraw21030013_UI'
                  WHEN 1015 THEN 'T_Luckdraw21040013_UI'
                  WHEN 1016 THEN 'T_Luckdraw21050013_UI'
                  WHEN 1017 THEN 'T_Luckdraw21010064_UI'
                  WHEN 1018 THEN 'T_Luckdraw21020064_UI'
                  ELSE `icon_key` END
                WHERE `item_id` IN (1012, 1013, 1014, 1015, 1016, 1017, 1018)
                  AND `icon_key` LIKE 'T_LuckdraWeapon%'
                """,
                connection);
            fixWeaponIcons.ExecuteNonQuery();

            InvalidateConfigCache();   // 种子写完后让缓存重建（与 Quest/Dungeon 同口径）
        }

        // ──────────────────────────────
        // 物品静态配置缓存（item_config 全表唯一读取入口）
        // 静态数据，只有种子和手工改库会变。缓存后背包列表/邮件文案/唤取入包/任务发奖等所有读点
        // 共享同一份快照，不再每条消息全表读库。TTL 只为兼容"手工改库"的习惯：改动最迟 10s 生效
        // （与 QuestRepository/GachaRepository/DungeonRepository 同一口径）。
        // 线程安全：会话消息在线程池上并发处理，沿用"原子引用发布 + 不可变快照"——
        // 发布后的 List/Dictionary 不再修改，读者拿旧引用也只是旧快照；失败读到的空结果不缓存。
        // ──────────────────────────────

        /// <summary>物品静态配置行（item_config 一行；发布到缓存后不再修改）</summary>
        public sealed record ItemConfig(int ItemId, string Name, int ItemType, int Rarity, string IconKey, string Description, int MaxStack);

        /// <summary>配置列表缓存（按 item_id 升序，发布后不可变）</summary>
        private static List<ItemConfig>? configListCache;

        /// <summary>配置索引缓存（itemId → 配置，发布后不可变）</summary>
        private static Dictionary<int, ItemConfig>? configMapCache;

        /// <summary>缓存发布时刻（Environment.TickCount64；0 = 无缓存）</summary>
        private static long configCacheTick;

        /// <summary>缓存有效期：手工改库最迟 10 秒后可见（与 Quest/Gacha/Dungeon 一致）</summary>
        private const long ConfigCacheTtlMs = 10_000;

        /// <summary>
        /// 读取全部物品配置（按 item_id 升序；10 秒内共享缓存）。全表 item_config 的唯一读取入口。
        /// </summary>
        /// <returns>返回值类型：List，用途：物品配置列表（发布后不可变，调用方只读）</returns>
        public static List<ItemConfig> GetConfigList()
        {
            List<ItemConfig>? cache = configListCache;
            long tick = Interlocked.Read(ref configCacheTick);
            if (cache != null && tick > 0 && Environment.TickCount64 - tick < ConfigCacheTtlMs)
            {
                return cache;
            }

            List<ItemConfig> fresh = LoadConfigsFromDb();
            if (fresh.Count > 0)
            {
                var map = new Dictionary<int, ItemConfig>(fresh.Count);
                foreach (ItemConfig config in fresh) map[config.ItemId] = config;
                configMapCache = map;
                configListCache = fresh;
                Interlocked.Exchange(ref configCacheTick, Environment.TickCount64);
            }
            return fresh;
        }

        /// <summary>
        /// 读取物品配置索引（itemId → 配置；与 GetConfigList 共享同一份缓存快照）。
        /// 单个规则/名字查询一律走这里，不再逐条 SELECT item_config。
        /// </summary>
        /// <returns>返回值类型：Dictionary，用途：物品ID → 配置（发布后不可变，调用方只读）</returns>
        public static Dictionary<int, ItemConfig> GetConfigMap()
        {
            GetConfigList();   // 确保缓存就绪（失败时也会尝试加载一次）
            return configMapCache ?? new Dictionary<int, ItemConfig>();
        }

        /// <summary>主动失效配置缓存（种子写入后自动调用；手工改库后也可调用以立即生效）。</summary>
        public static void InvalidateConfigCache()
        {
            configListCache = null;
            configMapCache = null;
            Interlocked.Exchange(ref configCacheTick, 0);
        }

        /// <summary>从数据库读 item_config 全表（失败返回空表且不缓存，不把"空"钉死 10 秒）。</summary>
        private static List<ItemConfig> LoadConfigsFromDb()
        {
            var rows = new List<ItemConfig>();
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlCommand command = new(
                    "SELECT `item_id`, `name`, `item_type`, `rarity`, `icon_key`, `description`, `max_stack` FROM item_config ORDER BY `item_id`",
                    connection);
                using MySqlDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new ItemConfig(
                        reader.GetInt32(0),
                        reader.GetString(1),
                        reader.GetInt32(2),
                        reader.GetInt32(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        Math.Max(1, reader.GetInt32(6))));
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"物品配置读取失败 [{exception.GetType().Name}]: {exception.Message}");
            }
            return rows;
        }

        // ──────────────────────────────
        // 查询
        // ──────────────────────────────

        /// <summary>
        /// 读取玩家非空槽位快照（按 slot_index 升序）。
        /// 只返回非空槽，空槽由客户端按「武器区 + 道具区」长度补齐占位（道具区容量按最高槽位推导）。
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID（服务端会话绑定，不可伪造）</param>
        /// <returns>返回值类型：List，用途：(槽位下标, 物品ID, 数量, 装备实例ID)</returns>
        public List<(int SlotIndex, int ItemId, int Count, long InstanceId)> GetSlots(int playerId)
        {
            var rows = new List<(int SlotIndex, int ItemId, int Count, long InstanceId)>();
            BagSlot[] slots = LoadSlots(playerId);
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i].Count > 0) rows.Add((i, slots[i].ItemId, slots[i].Count, slots[i].InstanceId));
            }
            return rows;
        }

        /// <summary>
        /// 读取某槽位当前的物品ID（0 = 空槽）
        /// 外部模块（如任务系统）在扣减前需要知道"这次操作动的是哪个物品"，用于按物品重算收集类任务进度
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="slotIndex">参数名：slotIndex，类型：int，用途：槽位下标</param>
        /// <returns>返回值类型：int，用途：物品ID（空槽或越界返回 0）</returns>
        public int GetSlotItemId(int playerId, int slotIndex)
        {
            if (slotIndex < 0) return 0;   // 上限由快照长度决定（道具区动态扩容），单行查询不再按定长拦
            try
            {
                // 单行读取即可（历史实现是 LoadSlots 全量加载 500 槽只为取一格，白白多一整表读）
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlCommand command = new(
                    "SELECT `item_id` FROM player_items WHERE `player_id` = @PlayerId AND `slot_index` = @SlotIndex",
                    connection);
                command.Parameters.AddWithValue("@PlayerId", playerId);
                command.Parameters.AddWithValue("@SlotIndex", slotIndex);
                object? result = command.ExecuteScalar();
                return result == null ? 0 : Convert.ToInt32(result);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"槽位物品读取失败 [{exception.GetType().Name}]: {exception.Message}");
                return 0;
            }
        }

        /// <summary>读取全部物品静态配置（背包列表响应里下发给客户端建运行时表；走 TTL 缓存，不再每次全表读库）。</summary>
        /// <returns>返回值类型：List，用途：(物品ID, 名称, 类型, 稀有度, 图标键, 描述, 堆叠上限)</returns>
        public List<(int ItemId, string Name, int ItemType, int Rarity, string IconKey, string Description, int MaxStack)>
            GetAllConfigs()
        {
            List<ItemConfig> configs = GetConfigList();
            var rows = new List<(int, string, int, int, string, string, int)>(configs.Count);
            foreach (ItemConfig config in configs)
            {
                rows.Add((config.ItemId, config.Name, config.ItemType, config.Rarity, config.IconKey, config.Description, config.MaxStack));
            }
            return rows;
        }

        // ──────────────────────────────
        // 操作（全部服务端权威：单事务内 锁玩家主行 → FOR UPDATE 读快照 → 内存修改 → 差异回写 → 提交）
        // ──────────────────────────────

        /// <summary>
        /// 使用物品的方法
        /// 校验槽位归属与数量后扣减（本期无实际效果，只消耗数量）
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="slotIndex">参数名：slotIndex，类型：int，用途：目标槽位下标</param>
        /// <param name="count">参数名：count，类型：int，用途：使用数量</param>
        /// <returns>返回值类型：(bool Success, string Message)，用途：操作结果与提示</returns>
        public (bool Success, string Message) UseItem(int playerId, int slotIndex, int count)
            => ConsumeItem(playerId, slotIndex, count, "使用");

        /// <summary>丢弃物品的方法（数量扣减同"使用"，丢光则清空该槽）。</summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="slotIndex">参数名：slotIndex，类型：int，用途：目标槽位下标</param>
        /// <param name="count">参数名：count，类型：int，用途：丢弃数量</param>
        /// <returns>返回值类型：(bool Success, string Message)，用途：操作结果与提示</returns>
        public (bool Success, string Message) DiscardItem(int playerId, int slotIndex, int count)
            => ConsumeItem(playerId, slotIndex, count, "丢弃");

        /// <summary>
        /// 交换两个槽位的方法（含与空槽交换，是拖拽换位的服务端落地）。
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="fromSlot">参数名：fromSlot，类型：int，用途：起点槽位下标</param>
        /// <param name="toSlot">参数名：toSlot，类型：int，用途：目标槽位下标</param>
        /// <returns>返回值类型：(bool Success, string Message)，用途：操作结果与提示</returns>
        public (bool Success, string Message) SwapSlots(int playerId, int fromSlot, int toSlot)
        {
            if (fromSlot < 0 || toSlot < 0)
                return (false, "槽位下标非法");
            if (fromSlot == toSlot) return (false, "起止槽位相同");
            // 分区后只在同一区内换位：武器区 [0,500) 之间、道具区 [500,…) 之间
            if ((fromSlot < ItemRegionStart) != (toSlot < ItemRegionStart))
                return (false, "不能跨背包换位");

            using IDisposable writeLock = PlayerWriteLock.Acquire(playerId);
            try
            {
                // 读→改→写合并进单事务（主行锁 + FOR UPDATE）：旧实现读写各开连接，跨线程并发会丢更新
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlTransaction transaction = connection.BeginTransaction();
                LockPlayerRow(connection, transaction, playerId);
                BagSlot[] slots = LoadSlotsForUpdate(connection, transaction, playerId);
                if (fromSlot >= slots.Length || toSlot >= slots.Length) return (false, "槽位下标非法");
                (slots[fromSlot], slots[toSlot]) = (slots[toSlot], slots[fromSlot]);
                SaveSlots(connection, transaction, playerId, slots);
                transaction.Commit();
                return (true, "已换位");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"背包换位失败 [{exception.GetType().Name}]: {exception.Message}");
                return (false, "操作失败，请稍后再试");
            }
        }

        /// <summary>
        /// 整理背包的方法（分区整理）
        /// 武器区 [0,500)：装备逐件保留实例ID，按"稀有度降序 → 物品ID → 实例ID"紧凑排在 0 号槽起；
        /// 道具区 [500,…)：同类按 max_stack 合并、按"类型升序 → 稀有度降序 → 物品ID"紧凑排在 500 号槽起，
        /// 并收缩到实际需要（不小于基数）。归类按 item_type 而不是原槽位，因此历史数据里放在武器区的道具
        /// 会在整理时被搬到道具区（自然迁移）。
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <returns>返回值类型：(bool Success, string Message)，用途：操作结果与提示</returns>
        public (bool Success, string Message) Tidy(int playerId)
        {
            // 静态配置在事务外先取，缩短持锁时间
            Dictionary<int, (int ItemType, int Rarity, int MaxStack)> rules = LoadRules();

            using IDisposable writeLock = PlayerWriteLock.Acquire(playerId);
            try
            {
                // 读→改→写合并进单事务（主行锁 + FOR UPDATE）：整理期间若有其他线程写背包，等锁后按最新槽位重排
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlTransaction transaction = connection.BeginTransaction();
                LockPlayerRow(connection, transaction, playerId);
                BagSlot[] slots = LoadSlotsForUpdate(connection, transaction, playerId);

                // 按类型分堆：装备逐件独立（不同实例不能合并）；其余可堆叠物品只记总数
                var totals = new Dictionary<int, int>();
                var equipment = new List<BagSlot>();
                foreach (BagSlot slot in slots)
                {
                    if (slot.Count <= 0) continue;
                    if (slot.InstanceId > 0 || Rule(rules, slot.ItemId).ItemType == 3)
                    {
                        equipment.Add(new BagSlot(slot.ItemId, 1, slot.InstanceId));
                        continue;
                    }
                    totals[slot.ItemId] = totals.TryGetValue(slot.ItemId, out int existing)
                        ? existing + slot.Count
                        : slot.Count;
                }

                // ── 武器区：装备按"稀有度降序 → 物品ID → 实例ID"紧凑排 0 号槽起，超出 500 的部分留在原处（不回退物品） ──
                equipment.Sort((left, right) =>
                {
                    (int _, int lr, int _) = Rule(rules, left.ItemId);
                    (int _, int rr, int _) = Rule(rules, right.ItemId);
                    if (lr != rr) return rr.CompareTo(lr);
                    if (left.ItemId != right.ItemId) return left.ItemId.CompareTo(right.ItemId);
                    return left.InstanceId.CompareTo(right.InstanceId);
                });

                // ── 道具区：按堆叠上限重排（合并不增槽），排序"类型升序 → 稀有度降序 → 物品ID升序" ──
                List<int> ordered = totals.Keys.ToList();
                ordered.Sort((left, right) =>
                {
                    (int lt, int lr, int _) = Rule(rules, left);
                    (int rt, int rr, int _) = Rule(rules, right);
                    if (lt != rt) return lt.CompareTo(rt);
                    if (lr != rr) return rr.CompareTo(lr);
                    return left.CompareTo(right);
                });

                int weaponCount = Math.Min(equipment.Count, WeaponCapacity);
                int itemNeeded = 0;
                foreach (int itemId in ordered)
                {
                    (int _, int _, int maxStack) = Rule(rules, itemId);
                    itemNeeded += (totals[itemId] + maxStack - 1) / maxStack;
                }

                int targetLength = ItemRegionStart + ItemCapacityFor(ItemRegionStart + Math.Max(0, itemNeeded - 1));
                // 装备没排进武器区的（历史 >500 件的极端情况）留在末尾道具区之前不动，避免物品消失
                var tidied = new BagSlot[Math.Max(targetLength, slots.Length)];

                int weaponCursor = 0;
                for (int i = 0; i < weaponCount; i++)
                {
                    tidied[weaponCursor++] = equipment[i];
                }

                int itemCursor = ItemRegionStart;
                foreach (int itemId in ordered)
                {
                    int remaining = totals[itemId];
                    (int _, int _, int maxStack) = Rule(rules, itemId);
                    while (remaining > 0)
                    {
                        if (itemCursor >= tidied.Length) break;
                        int put = Math.Min(maxStack, remaining);
                        tidied[itemCursor++] = new BagSlot(itemId, put);
                        remaining -= put;
                    }
                }

                // 放不下的装备（武器区溢出）与道具区溢出：按原顺序追加到数组末尾的空槽，绝不清空
                int spillCursor = Math.Max(tidied.Length, itemCursor);
                for (int i = weaponCount; i < equipment.Count && spillCursor < tidied.Length; i++)
                {
                    spillCursor = FirstEmptyFrom(tidied, spillCursor);
                    if (spillCursor < 0) break;
                    tidied[spillCursor++] = equipment[i];
                }

                SaveSlots(connection, transaction, playerId, tidied);
                transaction.Commit();
                return (true, "整理完成");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"背包整理失败 [{exception.GetType().Name}]: {exception.Message}");
                return (false, "整理失败，请稍后再试");
            }
        }

        /// <summary>从 from 起找第一个空槽（找不到返回 -1）。</summary>
        private static int FirstEmptyFrom(BagSlot[] slots, int from)
        {
            for (int i = Math.Max(0, from); i < slots.Length; i++)
            {
                if (slots[i].Count <= 0) return i;
            }
            return -1;
        }

        /// <summary>
        /// 测试发放物品的方法（本期物品唯一来源）
        /// 先补已有同类堆叠（不超 max_stack），再占空槽；放不下的部分在 message 里说明
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="itemId">参数名：itemId，类型：int，用途：物品ID（须存在于 item_config）</param>
        /// <param name="count">参数名：count，类型：int，用途：发放数量</param>
        /// <returns>返回值类型：(bool Success, string Message, int Granted)，用途：操作结果与提示、实际放入的单位数（生涯"获得物品"指标用）</returns>
        public (bool Success, string Message, int Granted) Grant(int playerId, int itemId, int count)
        {
            if (itemId <= 0) return (false, "物品ID非法", 0);
            if (count <= 0 || count > MaxOperationCount) return (false, $"数量须在 1-{MaxOperationCount} 之间", 0);

            (bool found, int _, int _, int maxStack) = GetItemRule(itemId);
            if (!found) return (false, "物品不存在", 0);

            using IDisposable writeLock = PlayerWriteLock.Acquire(playerId);
            try
            {
                // 读→改→写合并进单事务（主行锁 + FOR UPDATE）：与唤取/邮件领附件等写路径互斥，防止丢更新
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlTransaction transaction = connection.BeginTransaction();
                LockPlayerRow(connection, transaction, playerId);
                BagSlot[] slots = LoadSlotsForUpdate(connection, transaction, playerId);

                // 先补同类未满堆叠，再占空槽（规则抽成 GrantIntoSlots 与唤取/任务/签到/邮件共用）
                // ref：道具区没有空槽时会在内部扩容，扩容后的数组必须沿用到 SaveSlots
                int remaining = GrantIntoSlots(connection, transaction, playerId, ref slots, itemId, count, maxStack);

                SaveSlots(connection, transaction, playerId, slots);
                transaction.Commit();
                int granted = count - remaining;

                // 背包放不下的剩余转存邮箱（DNF 云幂罐子的教训：宁可玩家晚拿，不能物品凭空消失）；
                // 邮箱也满（容量上限）等转存失败时保留原"未放入"提示——物品丢失但服务端日志可溯源
                if (remaining > 0)
                {
                    (bool mailed, string _, int _) = new MailRepository().SendMail(
                        playerId,
                        "背包已满：物品转入邮箱",
                        $"背包空间不足，{remaining} 个物品已转入邮箱，请清理背包后领取。",
                        new List<(int ItemId, int Count)> { (itemId, remaining) });
                    if (mailed)
                    {
                        // 阿福主动提醒（悬浮球 3107）：满包转存进邮箱的物品主动告诉玩家（fire-and-forget，频控在服务内）
                        CompeteServer.Manager.AgentReminderService.OnMailArrived(
                            playerId, "背包已满：物品转入邮箱", remaining);
                        return (true, $"背包已满，{granted} 个已放入，{remaining} 个转入邮箱", granted);
                    }
                }

                return (true,
                    remaining > 0 ? $"背包已满，{granted} 个已放入，{remaining} 个未放入" : $"已发放 {count} 个",
                    granted);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"物品发放失败 [{exception.GetType().Name}]: {exception.Message}");
                return (false, "发放失败，请稍后再试", 0);
            }
        }

        // ──────────────────────────────
        // 按物品ID跨槽扣减（唤取媒介等"不指定槽位"的消耗）
        // ──────────────────────────────

        /// <summary>
        /// 按物品ID扣减指定数量的方法
        /// 用途：唤取消耗媒介（玩家不指定槽位，服务端跨槽合计校验后扣减）
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="itemId">参数名：itemId，类型：int，用途：物品ID</param>
        /// <param name="count">参数名：count，类型：int，用途：要消耗的数量</param>
        /// <returns>返回值：成功与否、说明、实际消耗数</returns>
        public (bool Success, string Message, int Consumed) TryConsumeByItemId(int playerId, int itemId, int count)
        {
            if (itemId <= 0) return (false, "物品ID非法", 0);
            if (count <= 0 || count > MaxOperationCount) return (false, $"数量须在 1-{MaxOperationCount} 之间", 0);

            (bool found, int _, int _, int _) = GetItemRule(itemId);
            if (!found) return (false, "物品不存在", 0);

            using IDisposable writeLock = PlayerWriteLock.Acquire(playerId);
            try
            {
                // 持有量校验与扣减必须在同一事务同一快照上完成：
                // 旧实现"锁外查持有→事务内全表删插"，并发扣减会互相覆盖（媒介被双扣或多扣）
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlTransaction transaction = connection.BeginTransaction();
                LockPlayerRow(connection, transaction, playerId);
                BagSlot[] slots = LoadSlotsForUpdate(connection, transaction, playerId);

                int owned = CountInSlots(slots, itemId);
                if (owned < count) return (false, $"数量不足（还差 {count - owned} 个）", 0);   // 事务释放即回滚（未作任何写）

                ConsumeFromSlots(slots, itemId, count);
                SaveSlots(connection, transaction, playerId, slots);
                transaction.Commit();
                return (true, $"已消耗 {count} 个", count);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"物品扣减失败 [{exception.GetType().Name}]: {exception.Message}");
                return (false, "操作失败，请稍后再试", 0);
            }
        }

        // ──────────────────────────────
        // 内部工具
        // ──────────────────────────────

        /// <summary>使用/丢弃的共用实现（只差提示词）。</summary>
        private (bool Success, string Message) ConsumeItem(int playerId, int slotIndex, int count, string verb)
        {
            if (slotIndex < 0) return (false, "槽位下标非法");
            if (count <= 0 || count > MaxOperationCount) return (false, $"数量须在 1-{MaxOperationCount} 之间");

            using IDisposable writeLock = PlayerWriteLock.Acquire(playerId);
            try
            {
                // 读→改→写合并进单事务（主行锁 + FOR UPDATE）
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlTransaction transaction = connection.BeginTransaction();
                LockPlayerRow(connection, transaction, playerId);
                BagSlot[] slots = LoadSlotsForUpdate(connection, transaction, playerId);
                if (slotIndex >= slots.Length) return (false, "槽位下标非法");
                if (slots[slotIndex].Count <= 0) return (false, "该格没有物品");
                if (count > slots[slotIndex].Count) return (false, "数量不足");

                long instanceId = slots[slotIndex].InstanceId;
                if (instanceId > 0 && IsInstanceEquipped(connection, transaction, playerId, instanceId))
                {
                    return (false, "请先从角色身上卸下该装备");   // 事务随 return 释放即回滚（未作任何写）
                }

                int left = slots[slotIndex].Count - count;
                slots[slotIndex] = left > 0
                    ? new BagSlot(slots[slotIndex].ItemId, left, instanceId)
                    : default;
                // 装备整件没了：实例行一并删掉，否则装备列表里会留一条背包中不存在的"幽灵武器"
                if (left <= 0 && instanceId > 0)
                {
                    DeleteEquipmentInstance(connection, transaction, playerId, instanceId);
                }

                SaveSlots(connection, transaction, playerId, slots);
                transaction.Commit();
                return (true, $"已{verb} {count} 个");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"背包{verb}失败 [{exception.GetType().Name}]: {exception.Message}");
                return (false, "操作失败，请稍后再试");
            }
        }

        /// <summary>读取玩家槽位为「武器区 + 道具区」长度的数组（下标=槽位，空槽为全 0）。</summary>
        private BagSlot[] LoadSlots(int playerId)
        {
            using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
            using MySqlCommand command = new(
                "SELECT `slot_index`, `item_id`, `count`, `instance_id` FROM player_items WHERE `player_id` = @PlayerId",
                connection);
            command.Parameters.AddWithValue("@PlayerId", playerId);
            return ReadSlots(command);
        }

        /// <summary>
        /// 把查询结果摊成槽位数组：先收行、按最高槽位算出数组长度（道具区动态扩容），再落位。
        /// 只存非空槽，空槽由数组默认值补齐；负索引属脏数据，直接忽略。
        /// </summary>
        private static BagSlot[] ReadSlots(MySqlCommand command)
        {
            var rows = new List<(int Index, BagSlot Slot)>();
            int maxIndex = -1;
            using (MySqlDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    int index = reader.GetInt32(0);
                    if (index < 0) continue;   // 脏数据防御：负槽位忽略
                    rows.Add((index, new BagSlot(
                        reader.GetInt32(1),
                        reader.GetInt32(2),
                        reader.IsDBNull(3) ? 0L : reader.GetInt64(3))));
                    if (index > maxIndex) maxIndex = index;
                }
            }

            var slots = new BagSlot[SlotCountFor(maxIndex)];
            foreach ((int index, BagSlot slot) in rows)
            {
                if (index < slots.Length) slots[index] = slot;   // 脏数据防御：超出本次推导长度的槽忽略
            }
            return slots;
        }

        /// <summary>
        /// 锁玩家主行（accounts.Id = player_id）。
        /// player_items 没有"玩家主行"可锁——空背包时 FOR UPDATE 槽位行锁不住任何东西，
        /// 跨线程读-改-写（本人会话 vs 好友/邮件事件线程）仍会丢更新；
        /// 统一先锁 accounts 这一行，把同一玩家的所有背包写串行化（本方法须是事务第一条语句）。
        /// </summary>
        internal static void LockPlayerRow(MySqlConnection connection, MySqlTransaction transaction, int playerId)
        {
            using MySqlCommand command = new(
                "SELECT `Id` FROM accounts WHERE `Id` = @PlayerId FOR UPDATE", connection, transaction);
            command.Parameters.AddWithValue("@PlayerId", playerId);
            command.ExecuteScalar();   // 行不存在（异常数据）时不加锁继续，与旧读行为一致
        }

        /// <summary>事务内读取槽位（FOR UPDATE：主行锁排队后，读到的即落库前最后一份已提交状态）。</summary>
        internal static BagSlot[] LoadSlotsForUpdate(
            MySqlConnection connection, MySqlTransaction transaction, int playerId)
        {
            using MySqlCommand command = new(
                """
                SELECT `slot_index`, `item_id`, `count`, `instance_id` FROM player_items WHERE `player_id` = @PlayerId
                FOR UPDATE
                """,
                connection, transaction);
            command.Parameters.AddWithValue("@PlayerId", playerId);
            return ReadSlots(command);
        }

        /// <summary>
        /// 差异回写（事务内）：只写"实际变动的槽"——占用且与库内不同的槽 UPSERT、库内有而现在为空的槽 DELETE，其余不动。
        /// 旧实现是"整表 DELETE + 全量 INSERT 非空槽"，一次操作产生的写入量 ∝ 背包占用格数
        /// （加 1 个物品也要把全部非空槽删掉重插，背包越满越慢）；本实现把写入量压到 ∝ 变动槽数。
        /// 调用方须已持有主行锁。本方法先读一次库内当前状态作为 before 快照——同一玩家的写已由
        /// PlayerWriteLock + accounts 主行锁串行化，且本事务此前未写过 player_items，读到即"本次修改前"的库内真值。
        /// </summary>
        internal static void SaveSlots(
            MySqlConnection connection, MySqlTransaction transaction, int playerId, BagSlot[] slots)
        {
            // ── before 快照：事务内读当前已落库的非空槽（用于逐格比对，只写差异）──
            var before = new Dictionary<int, BagSlot>();
            using (MySqlCommand read = new(
                "SELECT `slot_index`, `item_id`, `count`, `instance_id` FROM player_items WHERE `player_id` = @PlayerId",
                connection, transaction))
            {
                read.Parameters.AddWithValue("@PlayerId", playerId);
                using MySqlDataReader reader = read.ExecuteReader();
                while (reader.Read())
                {
                    int index = reader.GetInt32(0);
                    if (index < 0 || index >= slots.Length) continue;   // 脏数据防御：超出本次推导长度的槽忽略
                    before[index] = new BagSlot(
                        reader.GetInt32(1),
                        reader.GetInt32(2),
                        reader.IsDBNull(3) ? 0L : reader.GetInt64(3));
                }
            }

            // 变动槽 UPSERT（新占用的槽走 INSERT，数量/物品变化的槽走 UPDATE，一条语句覆盖两种情况）
            using MySqlCommand upsert = new(
                """
                INSERT INTO player_items (`player_id`, `slot_index`, `item_id`, `count`, `instance_id`)
                VALUES (@PlayerId, @SlotIndex, @ItemId, @Count, @InstanceId)
                ON DUPLICATE KEY UPDATE `item_id` = VALUES(`item_id`), `count` = VALUES(`count`),
                                        `instance_id` = VALUES(`instance_id`)
                """,
                connection, transaction);
            upsert.Parameters.Add("@PlayerId", MySqlDbType.Int32);
            upsert.Parameters.Add("@SlotIndex", MySqlDbType.Int32);
            upsert.Parameters.Add("@ItemId", MySqlDbType.Int32);
            upsert.Parameters.Add("@Count", MySqlDbType.Int32);
            upsert.Parameters.Add("@InstanceId", MySqlDbType.Int64);
            upsert.Prepare();

            // 由占用变空的槽 DELETE（按主键精确删单行，不再整片删）
            using MySqlCommand delete = new(
                "DELETE FROM player_items WHERE `player_id` = @PlayerId AND `slot_index` = @SlotIndex",
                connection, transaction);
            delete.Parameters.Add("@PlayerId", MySqlDbType.Int32);
            delete.Parameters.Add("@SlotIndex", MySqlDbType.Int32);
            delete.Prepare();

            for (int i = 0; i < slots.Length; i++)
            {
                BagSlot slot = slots[i];

                if (slot.Count <= 0)
                {
                    // 现在为空：只在库内确实有该槽时才删（未变动的空槽零操作）
                    if (before.ContainsKey(i))
                    {
                        delete.Parameters["@PlayerId"].Value = playerId;
                        delete.Parameters["@SlotIndex"].Value = i;
                        delete.ExecuteNonQuery();
                    }
                    continue;
                }

                // 与库内完全一致（物品/数量/实例都没变）→ 跳过：这是最常见的情况，零写入
                if (before.TryGetValue(i, out BagSlot current)
                    && current.ItemId == slot.ItemId
                    && current.Count == slot.Count
                    && current.InstanceId == slot.InstanceId) continue;

                upsert.Parameters["@PlayerId"].Value = playerId;
                upsert.Parameters["@SlotIndex"].Value = i;
                upsert.Parameters["@ItemId"].Value = slot.ItemId;
                upsert.Parameters["@Count"].Value = slot.Count;
                upsert.Parameters["@InstanceId"].Value = slot.InstanceId > 0 ? slot.InstanceId : DBNull.Value;
                upsert.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// 把 count 个 itemId 放进内存快照（先补同类未满堆叠，再占空槽），返回放不下的数量。
        /// 装备（item_type=3）逐件建实例并写 instance_id：同名多件是不同实例，不堆叠、每件独占一格。
        /// 供 Grant 与唤取/任务/签到/邮件领取共用——所有入包路径必须同一规则，否则"满包转邮件"的口径会漂移。
        /// </summary>
        internal static int GrantIntoSlots(
            MySqlConnection connection, MySqlTransaction transaction, int playerId,
            ref BagSlot[] slots, int itemId, int count, int maxStack)
        {
            if (IsEquipmentItem(itemId))
            {
                return GrantEquipmentIntoSlots(connection, transaction, playerId, slots, itemId, count);
            }

            int remaining = count;

            // 先补同类未满堆叠（全空间，兼容历史数据把道具放在武器区的情况）
            for (int i = 0; i < slots.Length && remaining > 0; i++)
            {
                if (slots[i].Count <= 0 || slots[i].ItemId != itemId || slots[i].Count >= maxStack) continue;
                int put = Math.Min(maxStack - slots[i].Count, remaining);
                slots[i] = new BagSlot(itemId, slots[i].Count + put);
                remaining -= put;
            }

            // 再占道具区空槽；道具区没有空槽就按步长扩容（武器区不放道具，历史遗留的已在上一步用掉空位）
            while (remaining > 0)
            {
                int free = FindEmptySlot(slots, ItemRegionStart, slots.Length);
                if (free < 0)
                {
                    slots = GrowItemRegion(slots);
                    continue;
                }

                int put = Math.Min(maxStack, remaining);
                slots[free] = new BagSlot(itemId, put);
                remaining -= put;
            }
            return 0;
        }

        /// <summary>道具区扩容：按步长加长数组（原槽位原样保留，新槽为空）。</summary>
        private static BagSlot[] GrowItemRegion(BagSlot[] slots)
        {
            int current = slots.Length - ItemRegionStart;
            int target = Math.Max(ItemCapacityFloor, current + ItemCapacityStep);
            var grown = new BagSlot[ItemRegionStart + target];
            Array.Copy(slots, grown, slots.Length);
            return grown;
        }

        /// <summary>装备入包：每件先落 equipment_instance 再占一个空槽（同一事务，回滚则实例不留）。</summary>
        private static int GrantEquipmentIntoSlots(
            MySqlConnection connection, MySqlTransaction transaction, int playerId,
            BagSlot[] slots, int itemId, int count)
        {
            for (int unit = 0; unit < count; unit++)
            {
                // 武器只进武器区（道具区不放装备）：满则剩余转邮件
                int free = FindEmptySlot(slots, 0, Math.Min(ItemRegionStart, slots.Length));
                if (free < 0) return count - unit;
                long instanceId = CreateEquipmentInstance(connection, transaction, playerId, itemId);
                slots[free] = new BagSlot(itemId, 1, instanceId);
            }
            return 0;
        }

        /// <summary>在快照的 [from, toExclusive) 区间里找第一个空槽（找不到返回 -1）。</summary>
        internal static int FindEmptySlot(BagSlot[] slots, int from, int toExclusive)
        {
            int end = Math.Min(toExclusive, slots.Length);
            for (int i = Math.Max(0, from); i < end; i++)
            {
                if (slots[i].Count <= 0) return i;
            }
            return -1;
        }

        /// <summary>新建一条装备实例并返回自增ID（调用方须在事务内，未提交的实例随回滚消失）。</summary>
        internal static long CreateEquipmentInstance(
            MySqlConnection connection, MySqlTransaction transaction, int playerId, int itemId)
        {
            using MySqlCommand insert = new(
                """
                INSERT INTO `equipment_instance` (`player_id`, `item_id`, `enhance_level`, `affix_json`)
                VALUES (@PlayerId, @ItemId, 0, '[]')
                """,
                connection, transaction);
            insert.Parameters.AddWithValue("@PlayerId", playerId);
            insert.Parameters.AddWithValue("@ItemId", itemId);
            insert.ExecuteNonQuery();
            return insert.LastInsertedId;
        }

        /// <summary>是否装备类物品（item_type=3；走统一配置缓存，与背包其它规则同源）。</summary>
        internal static bool IsEquipmentItem(int itemId)
        {
            return GetConfigMap().TryGetValue(itemId, out ItemConfig? config) && config.ItemType == 3;
        }

        /// <summary>该实例当前是否穿在某个角色身上（穿着的装备禁止使用/丢弃，避免槽位引用悬空）。</summary>
        private static bool IsInstanceEquipped(
            MySqlConnection connection, MySqlTransaction transaction, int playerId, long instanceId)
        {
            using MySqlCommand command = new(
                "SELECT `instance_id` FROM `player_equipment` WHERE `player_id` = @PlayerId AND `instance_id` = @InstanceId LIMIT 1",
                connection, transaction);
            command.Parameters.AddWithValue("@PlayerId", playerId);
            command.Parameters.AddWithValue("@InstanceId", instanceId);
            return command.ExecuteScalar() != null;
        }

        /// <summary>删除一件装备实例（丢弃/交付耗尽时调用；调用方已确认未穿戴）。</summary>
        internal static void DeleteEquipmentInstance(
            MySqlConnection connection, MySqlTransaction transaction, int playerId, long instanceId)
        {
            using MySqlCommand delete = new(
                "DELETE FROM `equipment_instance` WHERE `instance_id` = @InstanceId AND `player_id` = @PlayerId",
                connection, transaction);
            delete.Parameters.AddWithValue("@InstanceId", instanceId);
            delete.Parameters.AddWithValue("@PlayerId", playerId);
            delete.ExecuteNonQuery();
        }

        /// <summary>从内存快照跨槽扣减（调用方须已确认持有量足够），返回实际扣减数。</summary>
        internal static int ConsumeFromSlots(BagSlot[] slots, int itemId, int count)
        {
            int left = count;
            for (int i = 0; i < slots.Length && left > 0; i++)
            {
                if (slots[i].ItemId != itemId || slots[i].Count <= 0) continue;
                int take = Math.Min(slots[i].Count, left);
                int remain = slots[i].Count - take;
                slots[i] = remain > 0 ? new BagSlot(itemId, remain, slots[i].InstanceId) : default;
                left -= take;
            }
            return count - left;
        }

        /// <summary>统计内存快照中某物品的持有总量（跨槽合计）。</summary>
        internal static int CountInSlots(BagSlot[] slots, int itemId)
        {
            int owned = 0;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i].ItemId == itemId) owned += slots[i].Count;
            }
            return owned;
        }

        /// <summary>读取单个物品的规则（类型/稀有度/堆叠上限；不存在返回 Found=false；走统一配置缓存，缺失按"物品不存在"处理，与旧逐条 SELECT 的口径一致）。</summary>
        private static (bool Found, int ItemType, int Rarity, int MaxStack) GetItemRule(int itemId)
        {
            return GetConfigMap().TryGetValue(itemId, out ItemConfig? config)
                ? (true, config.ItemType, config.Rarity, config.MaxStack)
                : (false, 0, 0, 1);
        }

        /// <summary>一次性读取全部物品规则（整理/批量发放用；直接走统一配置缓存，不再二次拷贝）。</summary>
        private static Dictionary<int, (int ItemType, int Rarity, int MaxStack)> LoadRules()
        {
            List<ItemConfig> configs = GetConfigList();
            var rules = new Dictionary<int, (int, int, int)>(configs.Count);
            foreach (ItemConfig config in configs)
            {
                rules[config.ItemId] = (config.ItemType, config.Rarity, config.MaxStack);
            }
            return rules;
        }

        /// <summary>从规则表取（类型/稀有度/堆叠上限），缺失按"未知物品不可堆叠"处理。</summary>
        private static (int ItemType, int Rarity, int MaxStack) Rule(
            Dictionary<int, (int ItemType, int Rarity, int MaxStack)> rules, int itemId)
            => rules.TryGetValue(itemId, out (int ItemType, int Rarity, int MaxStack) rule) ? rule : (0, 1, 1);
    }
}
