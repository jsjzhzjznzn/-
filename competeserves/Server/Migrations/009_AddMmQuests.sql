-- 009_AddMmQuests.sql
-- 叙事任务系统（Mm-DialogSystem 框架）：MySQL 玩家叙事任务状态表
-- 定位：Mm 框架的客户端权威任务系统（对话/击杀/收集/到达目标由客户端事件推进），
--       服务器只做"状态快照的持久化"，不做规则校验（与 2501 服务端权威任务体系并行）；
-- 约定：progress_json 为各目标进度的 JSON 数组（如 "[1,0,3]"，无目标为 "[]"，与邮件 items JSON 同款先例）；
--       写入为整表替换（单事务 DELETE + 批量 INSERT），快照式幂等，客户端每次推送全量状态；
--       remain_seconds 为限时任务剩余秒数（0 = 无限时），客户端恢复时据此重启限时计时。
-- 服务端启动时也会以 CREATE TABLE IF NOT EXISTS 幂等兜底，手动执行本脚本与之等价。
CREATE TABLE IF NOT EXISTS `mm_player_quests` (
  `player_id`      INT          NOT NULL COMMENT '玩家ID = accounts.Id',
  `quest_id`       INT          NOT NULL COMMENT '任务ID（客户端 QuestData 配置侧定义）',
  `state`          TINYINT      NOT NULL DEFAULT 0 COMMENT '任务状态（EQuestState：未激活0/可用1/执行中2/提交3/失败4）',
  `progress_json`  TEXT         NOT NULL COMMENT '各目标进度JSON数组 [1,0,3]（与 QuestData.goalList 索引对应）',
  `accepted_at`    FLOAT        NOT NULL DEFAULT 0 COMMENT '接取时间（框架原样字段）',
  `remain_seconds` FLOAT        NOT NULL DEFAULT 0 COMMENT '限时剩余秒数（0=无限时）',
  `updated_at`     TIMESTAMP    NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP COMMENT '最近一次快照时间',
  PRIMARY KEY (`player_id`, `quest_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='玩家叙事任务状态（Mm框架·客户端权威·服务器持久化）';
