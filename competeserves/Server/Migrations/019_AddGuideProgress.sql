-- 019_AddGuideProgress.sql
-- 新手引导进度（「退出后回来还在同一步」）：一张表
--   player_guide_progress  一玩家一引导一行，UPSERT 覆盖
-- 语义：
--   * step      = 下一次要播放的步骤下标（0 = 从头）
--   * completed = 1 表示已完成或已跳过，客户端不再自动开场
--   * 服务端启动时也会做 CREATE TABLE IF NOT EXISTS 幂等兜底，手动执行本脚本与之一致
CREATE TABLE IF NOT EXISTS `player_guide_progress` (
  `player_id`     INT         NOT NULL COMMENT '玩家ID = accounts.Id',
  `guide_key`     VARCHAR(32) NOT NULL COMMENT '引导标识（当前仅 zhuye）',
  `step`          INT         NOT NULL DEFAULT 0 COMMENT '下一次要播放的步骤下标（0=从头）',
  `completed`     TINYINT     NOT NULL DEFAULT 0 COMMENT '1=已完成或已跳过，不再自动触发',
  `save_version`  INT         NOT NULL DEFAULT 1 COMMENT '结构版本（改步骤语义时递增）',
  `updated_at_ms` BIGINT      NOT NULL DEFAULT 0 COMMENT '服务端最近写入时间（Unix 毫秒）',
  PRIMARY KEY (`player_id`, `guide_key`),
  KEY `idx_updated` (`updated_at_ms`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='新手引导进度表（一玩家一引导一行）';
