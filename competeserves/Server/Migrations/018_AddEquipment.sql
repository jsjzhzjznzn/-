-- 018_AddEquipment.sql
-- 装备系统（武器槽 + 独立实例 + 服务端权威穿脱）
--
-- 分层：
--   1) equipment_config   = 装备静态定义（与 item_config 同主键一对一扩展）
--   2) equipment_instance = 玩家真正拥有的那一件（唯一ID/强化/词条）
--   3) player_equipment   = 角色 × 槽位 → 实例（"穿在哪"的唯一真相）
--   4) player_items.instance_id = 背包格指向实例（"有哪些"仍由背包管）
--
-- 服务端启动时 EquipmentRepository.EnsureTable / SeedConfigs / BackfillInstances
-- 与 BagRepository.EnsureTable 会做同样的幂等操作（查 information_schema / INSERT IGNORE），
-- 手动执行本脚本与之一致；重复执行无副作用。
--
-- attr_json 约定：{"type":<AttrType 整数>,"value":<数值>} 数组。
--   AttrType 当前顺序：0 DamageUp / 1 DamageDown / 2 MoveSpeed / 3 Attack / 4 Defense
--   枚举只允许末尾追加，配置里的数字才长期有效。

-- ── 装备配置（静态） ──
CREATE TABLE IF NOT EXISTS `equipment_config` (
  `item_id`       INT          NOT NULL COMMENT '物品ID（对应 item_config.item_id）',
  `slot_type`     INT          NOT NULL DEFAULT 1 COMMENT '1=武器（2 护甲 / 3 饰品 预留）',
  `weapon_type`   INT          NOT NULL DEFAULT 0 COMMENT '1长刃 2迅刀 3佩枪 4臂铠 5音感仪',
  `allowed_chars` VARCHAR(64)  NOT NULL DEFAULT '' COMMENT '可用角色白名单，空=全部；如 "0,1"',
  `attr_json`     TEXT         NOT NULL COMMENT '属性加成 JSON 数组',
  `model_key`     VARCHAR(128) NOT NULL DEFAULT '' COMMENT '武器外观模型键（本期空，预留挂点）',
  PRIMARY KEY (`item_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='装备静态定义';

-- ── 装备实例（玩家拥有的一件） ──
CREATE TABLE IF NOT EXISTS `equipment_instance` (
  `instance_id`   BIGINT       NOT NULL AUTO_INCREMENT COMMENT '实例ID（player_items.instance_id 指向它）',
  `player_id`     INT          NOT NULL COMMENT '拥有者',
  `item_id`       INT          NOT NULL COMMENT '物品ID',
  `enhance_level` INT          NOT NULL DEFAULT 0 COMMENT '强化等级（本期恒 0）',
  `affix_json`    TEXT         NOT NULL COMMENT '词条 JSON 数组（本期 "[]"）',
  `created_at`    DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '获得时间',
  PRIMARY KEY (`instance_id`),
  KEY `idx_player` (`player_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='装备实例';

-- ── 穿戴关系（唯一真相；主键天然阻止同角色同槽穿两件） ──
CREATE TABLE IF NOT EXISTS `player_equipment` (
  `player_id`   INT      NOT NULL COMMENT '玩家ID',
  `char_id`     INT      NOT NULL COMMENT '角色ID（0 安比 / 1 艾莲）',
  `slot_type`   INT      NOT NULL COMMENT '槽位类型（1=武器）',
  `instance_id` BIGINT   NOT NULL COMMENT '穿在该槽上的实例ID',
  `updated_at`  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP COMMENT '最近变更时间',
  PRIMARY KEY (`player_id`, `char_id`, `slot_type`),
  KEY `idx_instance` (`instance_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='角色装备槽位';

-- ── 背包扩展：装备行引用实例（非装备行为 NULL）──
-- MySQL 不支持 ADD COLUMN IF NOT EXISTS，重复执行本脚本会报 1060；
-- 服务端启动时的 BagRepository.EnsureTable 用 information_schema 判断后再加列，可安全重复执行。
ALTER TABLE `player_items`
  ADD COLUMN `instance_id` BIGINT NULL DEFAULT NULL COMMENT '装备实例ID（NULL=非装备行）',
  ADD KEY `idx_player_instance` (`player_id`, `instance_id`);

-- ── 首批武器配置（数值为验证用占位，最终由策划定表） ──
INSERT IGNORE INTO `equipment_config`
  (`item_id`, `slot_type`, `weapon_type`, `allowed_chars`, `attr_json`, `model_key`)
VALUES
  (1012, 1, 1, '', '[{"type":3,"value":24},{"type":0,"value":0.04}]', ''),
  (1013, 1, 2, '', '[{"type":3,"value":22},{"type":2,"value":0.03}]', ''),
  (1014, 1, 3, '', '[{"type":3,"value":32},{"type":0,"value":0.06}]', ''),
  (1015, 1, 4, '', '[{"type":3,"value":30},{"type":1,"value":0.05}]', ''),
  (1016, 1, 5, '', '[{"type":3,"value":44},{"type":0,"value":0.10}]', ''),
  (1017, 1, 1, '', '[{"type":3,"value":48},{"type":0,"value":0.08}]', ''),
  (1018, 1, 2, '', '[{"type":3,"value":46},{"type":2,"value":0.06}]', '');

-- ── 历史武器回填（一次性） ──
-- 装备系统上线前发放的武器只有 item_id + count，没有实例就穿不上。
-- 等价 SQL 如下；服务端启动时的 BackfillInstances 逐行处理 instance_id IS NULL 的行，重复执行无副作用。
-- UPDATE 需要拿到INSERT生成的自增值，故由程序完成，这里只保留说明。
