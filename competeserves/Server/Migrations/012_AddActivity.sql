-- 012_AddActivity.sql
-- 活动系统（仿鸣潮武器唤取）：三张表
--   activity_config  活动排期（哪个时间段、挂哪个卡池、内容版本指针）
--   banner_config    卡池（费用/保底阈值/物品池 JSON）——换卡池=改表，服务器零发版
--   gacha_records    唤取流水（draw_seq 承载保底计数，star 冗余便于统计查询）
-- 服务端启动时也会以 CREATE TABLE IF NOT EXISTS 幂等兜底 + INSERT IGNORE 幂等种子，手动执行本脚本与之一致
-- 活动切换的"运营动作"：UPDATE activity_config 把当前活动 end_at_ms 提前到现在、下一排期 start_at_ms 提前到现在
-- （GM 协议 2913 ActivityGmRotateRequest 做的就是这件事，演示用）

CREATE TABLE IF NOT EXISTS `activity_config` (
  `activity_id`     INT          NOT NULL COMMENT '活动ID',
  `type`            INT          NOT NULL DEFAULT 1 COMMENT '活动类型：1 武器唤取（本期唯一）',
  `banner_id`       INT          NOT NULL COMMENT '关联卡池ID = banner_config.banner_id',
  `content_key`     VARCHAR(64)  NOT NULL COMMENT '内容包键（展示/日志用，热更包真实版本由 YooAsset 版本文件管理）',
  `content_version` VARCHAR(32)  NOT NULL COMMENT '内容版本（与热更包版本同步 bump，如 1.0.0）',
  `title`           VARCHAR(128) NOT NULL COMMENT '活动标题（如 长刃唤取·苍鳞）',
  `subtitle`        VARCHAR(128) NOT NULL DEFAULT '' COMMENT '活动副标题（如 5★长刃·终 概率大幅提升）',
  `start_at_ms`     BIGINT       NOT NULL COMMENT '开始时间（Unix 毫秒）',
  `end_at_ms`       BIGINT       NOT NULL COMMENT '结束时间（Unix 毫秒）',
  `sort`            INT          NOT NULL DEFAULT 0 COMMENT '同窗多活动排序（小者靠前）',
  PRIMARY KEY (`activity_id`),
  KEY `idx_time` (`start_at_ms`, `end_at_ms`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='活动排期表（当前时间窗内即视为进行中）';

CREATE TABLE IF NOT EXISTS `banner_config` (
  `banner_id`      INT          NOT NULL COMMENT '卡池ID',
  `title`          VARCHAR(128) NOT NULL COMMENT '卡池标题',
  `cost_item_id`   INT          NOT NULL COMMENT '唤取货币物品ID = item_config.item_id（2001 唤取媒介）',
  `cost_single`    INT          NOT NULL DEFAULT 1 COMMENT '单抽消耗',
  `cost_ten`       INT          NOT NULL DEFAULT 10 COMMENT '十连消耗',
  `pity5_threshold` INT         NOT NULL DEFAULT 30 COMMENT '5★保底阈值（N 抽内必出 5★；演示节奏设 30，可改）',
  `pool_json`      TEXT         NOT NULL COMMENT '物品池 JSON：[{item_id,star,weight,is_up}]，star=稀有度，weight=权重，is_up=UP 物',
  PRIMARY KEY (`banner_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='唤取卡池配置（池内容纯数据，换池零代码）';

CREATE TABLE IF NOT EXISTS `gacha_records` (
  `player_id`      INT     NOT NULL COMMENT '玩家ID',
  `banner_id`      INT     NOT NULL COMMENT '卡池ID',
  `draw_seq`       INT     NOT NULL COMMENT '该卡池内第几抽（1 起始连续；保底计数 = total - 最近5星seq）',
  `item_id`        INT     NOT NULL COMMENT '抽到的物品ID',
  `star`           TINYINT NOT NULL COMMENT '稀有度（1-5，冗余存储便于统计查询）',
  `is_pity`        TINYINT NOT NULL DEFAULT 0 COMMENT '是否保底触发（5★计数保底 / 10连4★保底替换）',
  `created_at_ms`  BIGINT  NOT NULL COMMENT '抽取时间（Unix 毫秒）',
  PRIMARY KEY (`player_id`, `banner_id`, `draw_seq`),
  KEY `idx_time` (`player_id`, `banner_id`, `created_at_ms`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='唤取流水表（详单 + 保底计数依据）';

-- 唤取货币物品（与 BagRepository.SeedItemConfigs 里的 INSERT IGNORE 一致，已存在不覆盖）
-- 图标复用现有 80x80 晶石图（T_IconWup80_Setpup_001_1_UI）
INSERT IGNORE INTO `item_config`
  (`item_id`, `name`, `item_type`, `rarity`, `icon_key`, `description`, `max_stack`)
VALUES
  (2001, '唤取媒介', 2, 4, 'T_IconWup80_Setpup_001_1_UI', '用于武器唤取的共鸣结晶，单次唤取消耗 1 枚，十连消耗 10 枚。', 999);

-- 种子排期与卡池的时间是"首次种子时刻"的相对值（INSERT IGNORE 后续启动不刷新）：
-- 活动A 启动前 1 小时 ~ +7 天（先处于进行中）；活动B +7 天 ~ +14 天（GM 轮换把它提前到现在即完成 A→B 切换）
-- 卡池 A：UP 5★长刃·终(1017) 占 5 星权重 80%，歪 1016/1018；4★ 佩枪/臂铠；低星矿石填充
-- 卡池 B：UP 5★迅刀·终(1018)，结构镜像
-- star 与 item_config.rarity 对齐（5★武器=5 / 佩枪臂铠=4 / 铁铜矿=1 / 强化石=2）——保底分层与结果卡显示同一口径
-- 总权重 6200：5★≈16%（演示用高概率）、4★≈16%、其余≈68%
INSERT IGNORE INTO `banner_config` (`banner_id`, `title`, `cost_item_id`, `cost_single`, `cost_ten`, `pity5_threshold`, `pool_json`) VALUES
  (1, '武器唤取·苍鳞', 2001, 1, 10, 30,
   '[{"item_id":1017,"star":5,"weight":800,"is_up":1},{"item_id":1016,"star":5,"weight":100,"is_up":0},{"item_id":1018,"star":5,"weight":100,"is_up":0},{"item_id":1014,"star":4,"weight":500,"is_up":0},{"item_id":1015,"star":4,"weight":500,"is_up":0},{"item_id":1003,"star":1,"weight":1500,"is_up":0},{"item_id":1004,"star":1,"weight":1500,"is_up":0},{"item_id":1007,"star":2,"weight":1200,"is_up":0}]'),
  (2, '武器唤取·疾风', 2001, 1, 10, 30,
   '[{"item_id":1018,"star":5,"weight":800,"is_up":1},{"item_id":1016,"star":5,"weight":100,"is_up":0},{"item_id":1017,"star":5,"weight":100,"is_up":0},{"item_id":1014,"star":4,"weight":500,"is_up":0},{"item_id":1015,"star":4,"weight":500,"is_up":0},{"item_id":1003,"star":1,"weight":1500,"is_up":0},{"item_id":1004,"star":1,"weight":1500,"is_up":0},{"item_id":1007,"star":2,"weight":1200,"is_up":0}]');
