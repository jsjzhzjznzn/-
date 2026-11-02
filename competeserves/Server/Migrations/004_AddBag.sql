-- 004_AddBag.sql
-- 背包系统：MySQL 服务端权威（item_config 物品静态配置 + player_items 玩家槽位）
-- 约定：player_items 只存非空槽（空槽不入库）；容量是服务端常量 BagRepository.BagCapacity（默认 60），
--       客户端按容量补齐定长占位，槽位下标 slot_index 即格子上标
-- 服务端启动时也会以 CREATE TABLE IF NOT EXISTS 幂等兜底，手动执行本脚本与之一致

CREATE TABLE IF NOT EXISTS `item_config` (
  `item_id`     INT          NOT NULL COMMENT '物品ID（客户端与协议共用）',
  `name`        VARCHAR(64)  NOT NULL COMMENT '物品名',
  `item_type`   INT          NOT NULL DEFAULT 0 COMMENT '物品类型：0 未知 / 1 消耗品 / 2 材料 / 3 武器',
  `rarity`      INT          NOT NULL DEFAULT 1 COMMENT '稀有度：1 普通 / 2 精良 / 3 稀有 / 4 史诗 / 5 传说（决定格子底色）',
  `icon_key`    VARCHAR(128) NOT NULL DEFAULT '' COMMENT '图标资源键（YooAsset 地址，如 T_IconA80_02_UI）',
  `description` VARCHAR(255) NOT NULL DEFAULT '' COMMENT '描述文本（列名用 description，避开 MySQL 关键字 desc）',
  `max_stack`   INT          NOT NULL DEFAULT 1 COMMENT '单格堆叠上限（1 = 不可堆叠）',
  PRIMARY KEY (`item_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='物品静态配置（背包）';

CREATE TABLE IF NOT EXISTS `player_items` (
  `player_id`  INT NOT NULL COMMENT '玩家ID = accounts.Id',
  `slot_index` INT NOT NULL COMMENT '槽位下标（0 起始，0..BagCapacity-1）',
  `item_id`    INT NOT NULL COMMENT '物品ID = item_config.item_id',
  `count`      INT NOT NULL COMMENT '堆叠数量（>=1，空槽不落库）',
  `updated_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP COMMENT '最近变更时间',
  PRIMARY KEY (`player_id`, `slot_index`),
  KEY `idx_player_item` (`player_id`, `item_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='玩家背包槽位（只存非空槽）';
