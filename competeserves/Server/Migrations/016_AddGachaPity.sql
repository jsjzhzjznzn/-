-- 016_AddGachaPity.sql
-- 抽卡系统重构（对照"分层随机 / 双保底状态 / 逐抽结算 / 服务端权威 + 重试幂等"）
--
-- 1) banner_config 增列：保底分组、星级权重、UP 概率
-- 2) 新表 gacha_pity：保底状态唯一真相（5★ 计数 + 大保底），流水只做审计
-- 3) 新表 gacha_requests：请求幂等（重试重放，不重复扣媒介/不重新 roll）
-- 4) gacha_records 增列 pity_kind：区分 5★计数 / 十连4★ / 大保底
--
-- 服务端启动时 ActivityRepository.EnsureTable 会做同样的幂等升级（查 information_schema），
-- 手动执行本脚本与之一致；已有数据不需要迁移，首次抽卡/查保底时会从流水惰性反推。

-- ── banner_config：保底分组 + 星级权重 + UP 概率 ──
ALTER TABLE `banner_config`
  ADD COLUMN `pity_group` INT NOT NULL DEFAULT 0
    COMMENT '保底分组：0=本池独立；>0=同组共享（A/B 都设 1 即共享保底）' AFTER `pity5_threshold`,
  ADD COLUMN `star_rates_json` TEXT NULL
    COMMENT '星级权重 JSON：[{star,weight}]；NULL=兼容旧的整池物品权重 roll' AFTER `pity_group`,
  ADD COLUMN `up_rate_5` INT NOT NULL DEFAULT 50
    COMMENT '5★ 出 UP 的百分比（0-100），大保底时忽略' AFTER `star_rates_json`,
  ADD COLUMN `up_rate_4` INT NOT NULL DEFAULT 0
    COMMENT '4★ 出 UP 的百分比（0-100）' AFTER `up_rate_5`;

-- ── 保底状态（唯一真相） ──
CREATE TABLE IF NOT EXISTS `gacha_pity` (
  `player_id`      INT     NOT NULL COMMENT '玩家ID',
  `pity_group`     INT     NOT NULL COMMENT '保底分组（0 视为 banner_id）',
  `pity5_count`    INT     NOT NULL DEFAULT 0 COMMENT '距上次 5★ 的抽数',
  `guaranteed_up`  TINYINT NOT NULL DEFAULT 0 COMMENT '下一次 5★ 是否必 UP（大保底）',
  `total_draws`    INT     NOT NULL DEFAULT 0 COMMENT '该保底组累计抽数（统计用）',
  `updated_at_ms`  BIGINT  NOT NULL COMMENT '最后更新时间（Unix 毫秒）',
  PRIMARY KEY (`player_id`, `pity_group`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='抽卡保底状态（5★计数 + 大保底）';

-- ── 幂等（同一 request_id 只结算一次） ──
CREATE TABLE IF NOT EXISTS `gacha_requests` (
  `player_id`     INT          NOT NULL COMMENT '玩家ID',
  `request_id`    VARCHAR(64)  NOT NULL COMMENT '客户端生成的幂等键',
  `banner_id`     INT          NOT NULL COMMENT '卡池ID',
  `draw_count`    INT          NOT NULL COMMENT '抽数',
  `response_json` MEDIUMTEXT   NOT NULL COMMENT '首次结算的完整回执快照（结果/保底/背包槽位）',
  `created_at_ms` BIGINT       NOT NULL COMMENT '创建时间（Unix 毫秒，7 天后清理）',
  PRIMARY KEY (`player_id`, `request_id`),
  KEY `idx_created` (`created_at_ms`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='抽卡幂等表（重试重放，不重复扣费）';

-- ── 流水：保底类型 ──
ALTER TABLE `gacha_records`
  ADD COLUMN `pity_kind` TINYINT NOT NULL DEFAULT 0
    COMMENT '保底类型：0无 1=5★计数 2=十连4★ 3=大保底' AFTER `is_pity`;

-- ── 种子：A/B 共享保底（pity_group=1），星级权重对齐现有池（5★≈16%），UP 率 80% 复刻现体感 ──
UPDATE `banner_config`
SET `pity_group` = 1,
    `up_rate_5` = 80,
    `up_rate_4` = 0,
    `star_rates_json` = '[{"star":5,"weight":1000},{"star":4,"weight":1000},{"star":2,"weight":1200},{"star":1,"weight":3000}]'
WHERE `banner_id` IN (1, 2) AND `star_rates_json` IS NULL;

-- 幂等记录保留 7 天（启动时也会清理一次）
DELETE FROM `gacha_requests` WHERE `created_at_ms` < (UNIX_TIMESTAMP() * 1000 - 7 * 24 * 3600 * 1000);
