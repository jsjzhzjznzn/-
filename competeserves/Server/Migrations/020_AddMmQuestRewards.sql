-- 叙事任务奖励字段
-- 任务提交时按 mm_quest_config.rewards_json 逐项发放（同一事务，背包满转邮件）
-- 幂等：已存在的列不重复添加（MySQL 5.7 无 ADD COLUMN IF NOT EXISTS，靠 information_schema 判断）

SET @has_rewards := (
  SELECT COUNT(*) FROM information_schema.columns
  WHERE table_schema = DATABASE() AND table_name = 'mm_quest_config' AND column_name = 'rewards_json'
);

SET @sql := IF(@has_rewards = 0,
  'ALTER TABLE `mm_quest_config` ADD COLUMN `rewards_json` TEXT NOT NULL COMMENT ''提交奖励JSON数组（[{ItemId,Count}]）'' AFTER `goals_json`',
  'SELECT ''mm_quest_config.rewards_json 已存在，跳过'' AS info');

PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;
