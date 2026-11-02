-- 006_AddQuestsFull.sql
-- 任务系统完整版：章节任务（子任务表步骤）+ 成就任务 + 生涯数据
--   quest_config 新增 parent_quest_id：非 0 表示本任务是章节任务的步骤（按 quest_id 升序依次执行），
--     章节任务 goal_type=5（步骤容器，服务端以实际子任务数为准）；步骤任务沿用普通目标类型；
--   career_scores：生涯数据（成就任务的进度来源）。多阶段成就不做数据继承，
--     而是拆成多条成就配置用 prev_quest_id 链接（进度实时读生涯值，天然免继承且保留溢出）。
-- 服务端启动时会以幂等方式补列/建表（EnsureTable 查 information_schema），手动执行本脚本与之一致

ALTER TABLE `quest_config`
  ADD COLUMN `parent_quest_id` INT NOT NULL DEFAULT 0 COMMENT '父任务ID（0=顶层；非0=该章节任务的步骤）' AFTER `prev_quest_id`;

CREATE TABLE IF NOT EXISTS `career_scores` (
  `player_id` INT NOT NULL COMMENT '玩家ID = accounts.Id',
  `score_id`  INT NOT NULL COMMENT '生涯指标ID（1 累计挑战次数 / 2 累计发消息数 / 3 累计获得物品数 / 4 累计提交任务数 / 5 历史最高单次挑战分）',
  `value`     INT NOT NULL DEFAULT 0 COMMENT '累计值（只增不减；5 号指标取历史最大）',
  PRIMARY KEY (`player_id`, `score_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='生涯数据（成就任务进度来源）';
