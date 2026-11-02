-- 014_AddSignIn.sql
-- 签到活动（鸣潮式 7 天累计签到）：两张表
--   sign_config          签到奖励配置（第 N 天发什么）——改奖励=改表，服务器零发版
--   player_sign_records  玩家签到记录（两道闸：PK(player,sign,day) 保证序列只能签 signed+1；
--                        UNIQUE(player,sign,claim_date) 保证每个自然日只能签一次）
-- 同时补一条 activity_config 排期：活动3 = 每日签到（type=2），与唤取活动 A 同窗
-- 服务端启动时也会以 CREATE TABLE IF NOT EXISTS 幂等兜底 + INSERT IGNORE 幂等种子，手动执行本脚本与之一致

CREATE TABLE IF NOT EXISTS `sign_config` (
  `sign_id`  INT     NOT NULL COMMENT '签到活动ID = activity_config.activity_id（type=2 的活动）',
  `day`      TINYINT NOT NULL COMMENT '天次（1 起始；奖励按累计签到次数解锁，断签不回退）',
  `item_id`  INT     NOT NULL COMMENT '奖励物品ID = item_config.item_id',
  `count`    INT     NOT NULL DEFAULT 1 COMMENT '数量',
  PRIMARY KEY (`sign_id`, `day`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='签到奖励配置表（改奖励=改表，零发版）';

CREATE TABLE IF NOT EXISTS `player_sign_records` (
  `player_id`     INT      NOT NULL COMMENT '玩家ID',
  `sign_id`       INT      NOT NULL COMMENT '签到活动ID',
  `day`           TINYINT  NOT NULL COMMENT '签的是第几天（= 签到前的累计次数+1）',
  `claim_date`    CHAR(10) NOT NULL COMMENT '签到日期（服务器本地时区 yyyy-MM-dd；唯一键=每日一签闸）',
  `claimed_at_ms` BIGINT   NOT NULL COMMENT '签到时间（Unix 毫秒）',
  PRIMARY KEY (`player_id`, `sign_id`, `day`),
  UNIQUE KEY `uk_date` (`player_id`, `sign_id`, `claim_date`),
  KEY `idx_time` (`player_id`, `sign_id`, `claimed_at_ms`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='玩家签到记录表（序列闸 + 每日一签闸）';

-- 签到活动排期：与唤取活动 A 同窗（启动前 1 小时 ~ +7 天），客户端活动中心侧栏与唤取并排出现
-- INSERT IGNORE：时间只以首次种子为准，GM 轮换只作用于 type=1 的唤取活动，本行常驻
INSERT IGNORE INTO `activity_config`
  (`activity_id`, `type`, `banner_id`, `content_key`, `content_version`, `title`, `subtitle`, `start_at_ms`, `end_at_ms`, `sort`)
VALUES
  (3, 2, 0, 'sign_in', '1.1.0', '签到', '每日签到领好礼，累计 7 天',
   (UNIX_TIMESTAMP(NOW()) - 3600) * 1000, (UNIX_TIMESTAMP(NOW()) + 7 * 86400) * 1000, 1);

-- 7 天奖励种子（物品全部来自 item_config 既有配置）：
-- 前几天补给（药剂/矿石），第 6/7 天给唤取媒介形成"签到→攒媒介→抽卡"闭环
INSERT IGNORE INTO `sign_config` (`sign_id`, `day`, `item_id`, `count`) VALUES
  (3, 1, 1001, 2),   -- 治疗药剂 ×2
  (3, 2, 1003, 10),  -- 铁矿 ×10
  (3, 3, 1007, 3),   -- 强化石 ×3
  (3, 4, 1004, 10),  -- 铜矿 ×10
  (3, 5, 1002, 2),   -- 高级治疗药剂 ×2
  (3, 6, 2001, 5),   -- 唤取媒介 ×5
  (3, 7, 2001, 10);  -- 唤取媒介 ×10
