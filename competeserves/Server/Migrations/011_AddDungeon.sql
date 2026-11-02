-- 011_AddDungeon.sql
-- 副本掉落系统：dungeon_drop_rule 掉落规则表（服务端权威——客户端只上报击杀，掉什么/掉多少由本表裁决）
-- 形态A（鸣潮式直接入包）与形态B（世界掉落+限时令牌拾取）共用本表，区别只在入包时机：A 击杀即入包，B 拾取时入包
-- 世界掉落的令牌账本是纯内存（60 秒过期），不落库——掉落物本来就是限时存在的东西
-- 服务端启动时也会以 CREATE TABLE IF NOT EXISTS 幂等兜底，手动执行本脚本与之一致

CREATE TABLE IF NOT EXISTS `dungeon_drop_rule` (
  `id`         INT     NOT NULL AUTO_INCREMENT COMMENT '规则行ID',
  `enemy_type` INT     NOT NULL COMMENT '敌人类型：1 直接入包型（拾金鼠）/ 2 世界掉落型（藏宝龟）',
  `item_id`    INT     NOT NULL COMMENT '物品ID = item_config.item_id',
  `chance`     FLOAT   NOT NULL DEFAULT 0.5 COMMENT '掉落概率（0~1，每行独立判定）',
  `min_count`  INT     NOT NULL DEFAULT 1 COMMENT '单次掉落数量下限',
  `max_count`  INT     NOT NULL DEFAULT 1 COMMENT '单次掉落数量上限（区间内随机）',
  `enabled`    TINYINT NOT NULL DEFAULT 1 COMMENT '是否启用（0=停用该行，便于临时调整掉落）',
  PRIMARY KEY (`id`),
  UNIQUE KEY `uk_type_item` (`enemy_type`, `item_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='副本掉落规则（掉什么由服务端裁决）';

-- 种子规则（与 DungeonRepository.SeedDropRules 的 INSERT IGNORE 一致；已存在的行不覆盖，方便手工改库调概率）
INSERT IGNORE INTO `dungeon_drop_rule` (`enemy_type`, `item_id`, `chance`, `min_count`, `max_count`, `enabled`) VALUES
  (1, 1003, 0.65, 1, 2, 1),   -- A型·拾金鼠：铁矿
  (1, 1004, 0.40, 1, 2, 1),   -- A型·拾金鼠：铜矿
  (1, 1001, 0.15, 1, 1, 1),   -- A型·拾金鼠：治疗药剂
  (2, 1005, 0.55, 1, 3, 1),   -- B型·藏宝龟：银矿
  (2, 1006, 0.20, 1, 2, 1),   -- B型·藏宝龟：金矿
  (2, 1007, 0.35, 1, 1, 1);   -- B型·藏宝龟：强化石
