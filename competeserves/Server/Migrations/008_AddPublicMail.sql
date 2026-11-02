-- 008_AddPublicMail.sql
-- 运营公共邮件：全局表 + 玩家实例化去重表 + 个人邮件关联列
-- 核心设计（延迟创建/读裂变）：公共邮件只写 public_mails 全局表，不直接写全服玩家邮箱；
--   玩家拉取邮件列表时，服务端把"符合条件且未实例化"的公共邮件实例化到个人 mails 并记录去重。
--   避免大规模写库与僵尸号无效写入，也让撤回成为可能（已发放的邮件不宜撤回）。
-- 撤回 = 软标记（state=2，可逆可溯源，不物理删除）：未领附件的已实例化邮件在拉取时被软删、领取时被拒绝。
-- 服务端启动时也会以 CREATE TABLE IF NOT EXISTS 幂等兜底（mails.public_id 由 EnsureTable 查
--   information_schema 幂等补列，MySQL 不支持 ADD COLUMN IF NOT EXISTS）；手动重复执行本脚本的
--   ALTER 段会因列已存在报错——已执行过就跳过该段。

-- 1. 运营公共邮件全局表
CREATE TABLE IF NOT EXISTS `public_mails` (
  `id`               INT          NOT NULL AUTO_INCREMENT COMMENT '公共邮件ID（全局自增）',
  `tag`              VARCHAR(64)  NOT NULL COMMENT '运营标签（撤回按 tag 匹配；同 tag 可多次发送、一次全撤）',
  `title`            VARCHAR(64)  NOT NULL COMMENT '标题',
  `body`             VARCHAR(512) NOT NULL DEFAULT '' COMMENT '正文',
  `items`            TEXT         NOT NULL COMMENT '附件JSON数组 [{"ItemId":1003,"Count":5}]（无附件为 []）',
  `state`            INT          NOT NULL DEFAULT 0 COMMENT '0 有效 / 2 已撤回（软标记，可逆可溯源）',
  `min_register_ms`  BIGINT       NOT NULL DEFAULT 0 COMMENT '注册时间筛选下限（UTC 毫秒，0=不限；新玩家礼包类）',
  `max_register_ms`  BIGINT       NOT NULL DEFAULT 0 COMMENT '注册时间筛选上限（UTC 毫秒，0=不限；存量补偿类）',
  `create_time_ms`   BIGINT       NOT NULL COMMENT '创建时间（UTC 毫秒时间戳）',
  `expire_time_ms`   BIGINT       NOT NULL DEFAULT 0 COMMENT '过期时间（UTC 毫秒时间戳，0=永不过期）',
  PRIMARY KEY (`id`),
  KEY `idx_tag` (`tag`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='运营公共邮件（全局，延迟创建）';

-- 2. 玩家实例化去重表（= 玩家身上的"已领取公共邮件ID集合"；该玩家该封已实例化过就不再重复发）
CREATE TABLE IF NOT EXISTS `player_public_mail` (
  `player_id`      INT NOT NULL COMMENT '玩家ID = accounts.Id',
  `public_mail_id` INT NOT NULL COMMENT '公共邮件ID = public_mails.id',
  `created_at`     DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP COMMENT '实例化时间',
  PRIMARY KEY (`player_id`, `public_mail_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='公共邮件玩家实例化去重记录';

-- 3. 个人邮件表加关联列（撤回校验用；列已存在时本段会报错，跳过即可，服务端启动会幂等补列）
ALTER TABLE `mails` ADD COLUMN `public_id` INT NOT NULL DEFAULT 0 COMMENT '关联公共邮件ID（0=普通系统邮件；撤回校验）' AFTER `state`;
