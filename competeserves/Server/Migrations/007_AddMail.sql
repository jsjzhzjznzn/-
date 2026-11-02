-- 007_AddMail.sql
-- 邮件系统：MySQL 服务端权威（mails 玩家邮件表）
-- 约定：本期只做系统邮件（不做玩家互发——盗号转移/刷邮件的风控面不引入）；
--       state 为位掩码（1 已读 / 2 已删），删除是软删（置位不物理删行，可溯源可回滚）；
--       items 为 JSON 文本（[{"ItemId":1003,"Count":5}]），附件领取与背包写入走同一 MySQL 事务；
--       item_mask 为附件领取掩码（全领取后非 0，防止重复领取/重放）
-- 服务端启动时也会以 CREATE TABLE IF NOT EXISTS 幂等兜底，手动执行本脚本与之一致

CREATE TABLE IF NOT EXISTS `mails` (
  `id`             INT          NOT NULL AUTO_INCREMENT COMMENT '邮件ID（全局自增，协议 mail_id）',
  `player_id`      INT          NOT NULL COMMENT '收件玩家ID = accounts.Id',
  `sender_name`    VARCHAR(32)  NOT NULL DEFAULT '系统' COMMENT '发件人显示名（系统邮件为固定文案）',
  `title`          VARCHAR(64)  NOT NULL COMMENT '标题',
  `body`           VARCHAR(512) NOT NULL DEFAULT '' COMMENT '正文',
  `items`          TEXT         NOT NULL COMMENT '附件JSON数组 [{"ItemId":1003,"Count":5}]（无附件为 []）',
  `item_mask`      INT          NOT NULL DEFAULT 0 COMMENT '已领取附件掩码（0=未领，全领取后置满位，防重复领取）',
  `state`          INT          NOT NULL DEFAULT 0 COMMENT '状态位掩码：1 已读 / 2 已删（软删）',
  `create_time_ms` BIGINT       NOT NULL COMMENT '创建时间（UTC 毫秒时间戳）',
  `expire_time_ms` BIGINT       NOT NULL DEFAULT 0 COMMENT '过期时间（UTC 毫秒时间戳，0=永不过期）',
  PRIMARY KEY (`id`),
  KEY `idx_player_list` (`player_id`, `state`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='玩家邮件（本期仅系统邮件）';
