-- 005_AddQuests.sql
-- 任务系统：MySQL 服务端权威（quest_config 任务静态配置 + quest_reward 任务奖励 + player_quests 玩家任务进度）
-- 设计取舍（每任务只含一个目标）：把"目标"合并进任务本身，用组合任务代替组合目标，
--   从而让目标直接复用任务天然具备的发奖/扣道具能力；任务不超过两层，不做子任务。
-- 进度来源分治：累计类（挑战成绩/加好友/发消息）存 player_quests.progress，由玩法事件推进；
--   收集类为"交付"语义，进度 = 服务端实时统计的持有量（player_items 聚合），提交时校验并扣除。
-- 状态：1 进行中 / 2 已完成可提交 / 3 已提交；未接取 = 无记录（放弃 = 删行回到未接取）
-- 服务端启动时也会以 CREATE TABLE IF NOT EXISTS 幂等兜底，手动执行本脚本与之一致

CREATE TABLE IF NOT EXISTS `quest_config` (
  `quest_id`        INT          NOT NULL COMMENT '任务ID（客户端与协议共用）',
  `name`            VARCHAR(64)  NOT NULL COMMENT '任务名',
  `description`     VARCHAR(255) NOT NULL DEFAULT '' COMMENT '任务描述',
  `category`        INT          NOT NULL DEFAULT 1 COMMENT '分类：1 主线 / 2 支线',
  `goal_type`       INT          NOT NULL COMMENT '目标类型：1 挑战成绩 / 2 收集物品 / 3 加好友 / 4 发消息',
  `goal_param1`     INT          NOT NULL DEFAULT 0 COMMENT '目标参数1（挑战成绩=挑战ID，0=任意；收集物品=物品ID）',
  `goal_param2`     INT          NOT NULL DEFAULT 0 COMMENT '目标参数2（挑战成绩=分数阈值，0=不限）',
  `target_progress` INT          NOT NULL DEFAULT 1 COMMENT '目标进度（达成所需次数/数量）',
  `prev_quest_id`   INT          NOT NULL DEFAULT 0 COMMENT '前置任务ID（0=无前置）',
  PRIMARY KEY (`quest_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='任务静态配置';

CREATE TABLE IF NOT EXISTS `quest_reward` (
  `quest_id` INT NOT NULL COMMENT '任务ID = quest_config.quest_id',
  `item_id`  INT NOT NULL COMMENT '奖励物品ID = item_config.item_id',
  `count`    INT NOT NULL COMMENT '奖励数量（>=1）',
  PRIMARY KEY (`quest_id`, `item_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='任务奖励（提交时发放）';

CREATE TABLE IF NOT EXISTS `player_quests` (
  `player_id`    INT      NOT NULL COMMENT '玩家ID = accounts.Id',
  `quest_id`     INT      NOT NULL COMMENT '任务ID = quest_config.quest_id',
  `state`        INT      NOT NULL DEFAULT 1 COMMENT '状态：1 进行中 / 2 已完成可提交 / 3 已提交',
  `progress`     INT      NOT NULL DEFAULT 0 COMMENT '累计进度（收集类进度由实时统计得出，此列不参与）',
  `accepted_at`  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '接取时间',
  `finished_at`  DATETIME NULL COMMENT '进入已完成（可提交）的时间',
  `submitted_at` DATETIME NULL COMMENT '提交时间',
  PRIMARY KEY (`player_id`, `quest_id`),
  KEY `idx_player_state` (`player_id`, `state`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='玩家任务进度（未接取不落库）';
