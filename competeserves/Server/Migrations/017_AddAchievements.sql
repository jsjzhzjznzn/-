-- 017_AddAchievements.sql
-- 独立成就系统（对照《游戏成就系统开发思路》的五条原则）
--
-- 分层：
--   1) achievement_config / achievement_condition / achievement_reward = 静态定义
--   2) player_achievements = 玩家进度 + 解锁 + 领取（四层分离，进度不依赖界面）
--   3) career_scores（既有表） = 通用玩家统计（累计值/最大值），成就条件读它
--   4) 实时条件（持有物品/好友数量/账号年龄）按需查询，不落冗余
--
-- 与任务系统的边界：
--   成就独立一套表/协议/UI；旧 category=3 任务成就迁移到本系统后，
--   quest_config/quest_reward/player_quests 中的旧行会被删除（见文件末尾）。
--
-- 服务端启动时 AchievementRepository.EnsureTable / SeedConfigs / MigrateLegacyAchievements
-- 会做同样的幂等操作（查 information_schema / INSERT IGNORE / 条件删除），
-- 手动执行本脚本与之一致；重复执行无副作用。

-- ── 成就定义（静态配置） ──
CREATE TABLE IF NOT EXISTS `achievement_config` (
  `achievement_id`      INT          NOT NULL COMMENT '成就ID（全服唯一；旧任务成就沿用 4001 等原ID）',
  `name`                VARCHAR(64)  NOT NULL COMMENT '成就名',
  `description`         VARCHAR(255) NOT NULL DEFAULT '' COMMENT '描述/达成提示',
  `series`              VARCHAR(32)  NOT NULL DEFAULT '成长' COMMENT '系列：战斗/成长/社交/活动/隐藏',
  `rarity`              INT          NOT NULL DEFAULT 1 COMMENT '稀有度：1铜 2银 3金 4白金',
  `points`              INT          NOT NULL DEFAULT 0 COMMENT '成就点（仅展示）',
  `hidden`              TINYINT      NOT NULL DEFAULT 0 COMMENT '1=未解锁前显示 ???',
  `prev_achievement_id` INT          NOT NULL DEFAULT 0 COMMENT '阶段链上一阶段ID；0=无前置',
  `sort`                INT          NOT NULL DEFAULT 0 COMMENT '同系列内排序权重（越小越靠前）',
  `enabled`             TINYINT      NOT NULL DEFAULT 1 COMMENT '1=启用；0=保留配置但不展示',
  PRIMARY KEY (`achievement_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='成就静态定义';

-- ── 成就条件（1..N，默认 AND） ──
CREATE TABLE IF NOT EXISTS `achievement_condition` (
  `achievement_id` INT          NOT NULL COMMENT '成就ID',
  `seq`            INT          NOT NULL COMMENT '条件序号（从1开始，越小越先展示）',
  `condition_type` INT          NOT NULL COMMENT '1统计值 2持有物品 3好友数量 4账号注册天数 5指定日期前注册',
  `param1`         INT          NOT NULL DEFAULT 0 COMMENT '参数1：统计ID / 物品ID / yyyyMMdd',
  `param2`         INT          NOT NULL DEFAULT 0 COMMENT '参数2：预留',
  `target`         INT          NOT NULL DEFAULT 1 COMMENT '目标值',
  `text`           VARCHAR(128) NOT NULL DEFAULT '' COMMENT '条件展示文案（服务端直接下发）',
  PRIMARY KEY (`achievement_id`, `seq`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='成就条件（多行=AND）';

-- ── 成就奖励 ──
CREATE TABLE IF NOT EXISTS `achievement_reward` (
  `achievement_id` INT NOT NULL COMMENT '成就ID',
  `item_id`        INT NOT NULL COMMENT '奖励物品ID',
  `count`          INT NOT NULL COMMENT '奖励数量',
  PRIMARY KEY (`achievement_id`, `item_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='成就奖励';

-- ── 玩家成就（没有行 = 未激活/锁定） ──
CREATE TABLE IF NOT EXISTS `player_achievements` (
  `player_id`      INT      NOT NULL COMMENT '玩家ID',
  `achievement_id` INT      NOT NULL COMMENT '成就ID',
  `state`          TINYINT  NOT NULL DEFAULT 1 COMMENT '1进行中 2已解锁待领取 3已领取',
  `progress`       INT      NOT NULL DEFAULT 0 COMMENT '主条件当前值（列表显示）',
  `unlocked_at`    DATETIME NULL COMMENT '解锁时间',
  `claimed_at`     DATETIME NULL COMMENT '领取时间',
  PRIMARY KEY (`player_id`, `achievement_id`),
  KEY `idx_player_state` (`player_id`, `state`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='玩家成就进度/解锁/领取';

-- ── 旧成就迁移（category=3 的任务成就） ──
-- 迁移映射（新定义由 AchievementRepository.SeedConfigs 写入）：
--   4001 挑战新秀 / 4002 挑战狂人 / 4003 挑战大师 / 4101 社交达人 / 4201 小有家资 / 4301 任务大师
-- 旧状态口径：quest state 1/2/3 → achievement state 1/2/3；finished_at→unlocked_at；submitted_at→claimed_at
INSERT INTO `player_achievements` (`player_id`, `achievement_id`, `state`, `progress`, `unlocked_at`, `claimed_at`)
SELECT pq.`player_id`, pq.`quest_id`,
       CASE pq.`state` WHEN 3 THEN 3 WHEN 2 THEN 2 ELSE 1 END,
       pq.`progress`,
       pq.`finished_at`,
       pq.`submitted_at`
FROM `player_quests` pq
JOIN `quest_config` qc ON qc.`quest_id` = pq.`quest_id` AND qc.`category` = 3
ON DUPLICATE KEY UPDATE
  `state`       = GREATEST(`player_achievements`.`state`, VALUES(`state`)),
  `progress`    = GREATEST(`player_achievements`.`progress`, VALUES(`progress`)),
  `unlocked_at` = COALESCE(`player_achievements`.`unlocked_at`, VALUES(`unlocked_at`)),
  `claimed_at`  = COALESCE(`player_achievements`.`claimed_at`, VALUES(`claimed_at`));

-- 删除旧任务定义/奖励/玩家行：任务面板不再显示成就，避免与新系统重复发奖
DELETE FROM `quest_reward` WHERE `quest_id` IN (4001, 4002, 4003, 4101, 4201, 4301);
DELETE FROM `player_quests` WHERE `quest_id` IN (4001, 4002, 4003, 4101, 4201, 4301);
DELETE FROM `quest_config` WHERE `quest_id` IN (4001, 4002, 4003, 4101, 4201, 4301);
