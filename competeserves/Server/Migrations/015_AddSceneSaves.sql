-- 015_AddSceneSaves.sql
-- 场景存档（「随时退出、回来还在原处」）：一张表
--   player_scene_saves  玩家在某个场景的最新快照（一玩家一场景一行，UPSERT 覆盖）
-- 语义：
--   * 位置/朝向/相机由客户端上报，服务端只做范围护栏（不模拟位置，不做反作弊判定）
--   * 对话存到「节点级」：dialogue_active + graph + node_id，恢复时该句从头播放
--   * dialogue_fired_json 记录本段对话已发布过的 eventKey，恢复后重复选择不再推进叙事任务（2705）
-- 服务端启动时也会以 CREATE TABLE IF NOT EXISTS 幂等兜底，手动执行本脚本与之一致

CREATE TABLE IF NOT EXISTS `player_scene_saves` (
  `player_id`           INT          NOT NULL COMMENT '玩家ID = accounts.Id',
  `scene_name`          VARCHAR(64)  NOT NULL COMMENT '场景名（白名单：duihua / fuben）',
  `pos_x`               DOUBLE       NOT NULL DEFAULT 0 COMMENT '玩家根节点世界坐标 X',
  `pos_y`               DOUBLE       NOT NULL DEFAULT 0 COMMENT '玩家根节点世界坐标 Y',
  `pos_z`               DOUBLE       NOT NULL DEFAULT 0 COMMENT '玩家根节点世界坐标 Z',
  `rot_y`               DOUBLE       NOT NULL DEFAULT 0 COMMENT '朝向（绕 Y 轴欧拉角，度；存前归一化到 -180..180）',
  `cam_yaw`             DOUBLE       NOT NULL DEFAULT 0 COMMENT '第三人称相机水平角（POV m_HorizontalAxis.Value）',
  `cam_pitch`           DOUBLE       NOT NULL DEFAULT 0 COMMENT '第三人称相机垂直角（POV m_VerticalAxis.Value）',
  `cam_distance`        DOUBLE       NOT NULL DEFAULT 0 COMMENT '第三人称距离（FramingTransposer m_CameraDistance；0=未初始化）',
  `dialogue_active`     TINYINT      NOT NULL DEFAULT 0 COMMENT '退出时是否正在对话中',
  `dialogue_graph`      VARCHAR(128) NOT NULL DEFAULT '' COMMENT '对话图资产名',
  `dialogue_node_id`    INT          NOT NULL DEFAULT 0 COMMENT '当前对话节点 ConfigId（0=无）',
  `dialogue_vars_json`  TEXT         NULL COMMENT '对话变量快照 JSON（前向兼容）',
  `dialogue_fired_json` TEXT         NULL COMMENT '已发布过的 eventKey JSON 数组（防重复推进任务）',
  `save_version`        INT          NOT NULL DEFAULT 1 COMMENT '存档结构版本（改图/改字段时递增）',
  `updated_at_ms`       BIGINT       NOT NULL DEFAULT 0 COMMENT '服务端最近写入时间（Unix 毫秒）',
  PRIMARY KEY (`player_id`, `scene_name`),
  KEY `idx_updated` (`updated_at_ms`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='场景存档表（位置/相机/对话节点，一玩家一场景一行）';
