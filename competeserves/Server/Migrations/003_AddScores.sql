-- 003_AddScores.sql
-- 排行榜成绩表：MySQL 是持久真相层，Valkey ZSET（compete:lb:{challenge_id}）是实时排行层
-- 最高分模式：只有高于历史最高分的成绩才覆盖（服务端用 GREATEST upsert 保证）
-- 服务端启动时也会以 CREATE TABLE IF NOT EXISTS 幂等兜底，手动执行本脚本与之一致

CREATE TABLE IF NOT EXISTS `scores` (
  `challenge_id` INT    NOT NULL DEFAULT 1 COMMENT '挑战ID（不同挑战独立排榜）',
  `player_id`    INT    NOT NULL COMMENT '玩家ID = accounts.Id',
  `score`        BIGINT NOT NULL DEFAULT 0 COMMENT '历史最高分',
  `updated_at`   DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP COMMENT '最近一次入账时间',
  PRIMARY KEY (`challenge_id`, `player_id`),
  KEY `idx_score` (`challenge_id`, `score` DESC)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='挑战成绩（排行榜真相层）';
