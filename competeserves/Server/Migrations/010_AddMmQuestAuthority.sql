-- 010_AddMmQuestAuthority.sql
-- 叙事任务系统升级为「服务端权威」（鸣潮式：客户端只报事件，服务端推进任务逻辑）：
-- ① 新增 mm_quest_config 配置表：任务静态配置由客户端 QuestData SO 经编辑器导出工具
--    （Tools/Mm叙事任务/导出配置到服务器）生成 Config/mm_quest_config.json，服务端启动时幂等种子；
--    对话型目标以 graph_name（对话图资产名）为匹配键（服务端不引用 SO 资产）；
-- ② mm_player_quests 补列 expire_time_ms：服务端权威的限时绝对截止时间（UTC 毫秒，0=无限时），
--    接取时由服务端计算，惰性判定到期（任何访问时检查，无需后台计时器）；
--    remain_seconds 列保留为查询时刻的剩余秒数快照（下发客户端重启限时计时用）；
-- 服务端启动时也会以 CREATE TABLE IF NOT EXISTS / information_schema 幂等兜底，手动执行本脚本与之等价。
CREATE TABLE IF NOT EXISTS `mm_quest_config` (
  `quest_id`       INT          NOT NULL COMMENT '任务ID（客户端 QuestData 配置侧定义）',
  `category`       TINYINT      NOT NULL DEFAULT 1 COMMENT '分类（0主线/1支线，展示用）',
  `title`          VARCHAR(128) NOT NULL DEFAULT '' COMMENT '任务标题（导出自 QuestData.title）',
  `description`    VARCHAR(512) NOT NULL DEFAULT '' COMMENT '任务描述',
  `accept_mode`    TINYINT      NOT NULL DEFAULT 1 COMMENT '接取模式（0系统派发/1手动接取）',
  `time_limit_sec` FLOAT        NOT NULL DEFAULT 0 COMMENT '限时秒数（0=无限时）',
  `prereq_csv`     VARCHAR(128) NOT NULL DEFAULT '' COMMENT '前置任务ID列表（逗号分隔，空=无前置）',
  `goals_json`     TEXT         NOT NULL COMMENT '目标配置JSON数组 [{"type":1,"targetKey":"demo_enemy","count":3,"graphName":"","eventKey":""}]（type：对话0/击杀1/收集2/到达3）',
  `updated_at`     TIMESTAMP    NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP COMMENT '最近导入时间',
  PRIMARY KEY (`quest_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='叙事任务静态配置（服务端权威引擎的规则来源，由导出工具种子）';

-- mm_player_quests 补列（幂等：重复执行报列已存在可忽略；服务端启动时的兜底逻辑等价）
ALTER TABLE `mm_player_quests`
  ADD COLUMN `expire_time_ms` BIGINT NOT NULL DEFAULT 0 COMMENT '限时绝对截止时间（UTC毫秒，0=无限时；接取时服务端计算）';
