-- 013_AddAutoLogin.sql
-- 自动登录（remember me）：令牌换会话
--   auto_login_tokens  每玩家一行的活跃令牌（PRIMARY KEY player_id 即"每玩家单令牌"约束）
-- 服务端启动时也会以 CREATE TABLE IF NOT EXISTS 幂等兜底，手动执行本脚本与之一致
-- 安全设计：
--   * 库里只存 SHA-256 哈希（token_hash），明文令牌只在登录响应里出现一次，不落任何日志
--   * 令牌 = 32 字节加密随机数的 hex（64 字符），不可预测
--   * 滑动过期：每次令牌登录成功把 expires_at_ms 顺延 TTL（默认 7 天，env CS_AUTOLOGIN_TOKEN_TTL_SECONDS 可覆盖）
--   * 重新勾选"自动登录"的密码登录会 UPSERT 覆盖旧令牌（旧令牌立即作废，等效令牌轮换）

CREATE TABLE IF NOT EXISTS `auto_login_tokens` (
  `player_id`       INT       NOT NULL COMMENT '玩家ID = accounts.Id（一玩家一活跃令牌）',
  `token_hash`      CHAR(64)  NOT NULL COMMENT '明文令牌的 SHA-256 hex（不存明文）',
  `created_at_ms`   BIGINT    NOT NULL COMMENT '签发时间（Unix 毫秒）',
  `expires_at_ms`   BIGINT    NOT NULL COMMENT '过期时间（Unix 毫秒，滑动续期）',
  `last_used_at_ms` BIGINT    NOT NULL COMMENT '最近一次令牌登录时间（Unix 毫秒）',
  PRIMARY KEY (`player_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='自动登录令牌表（remember me，库内只存哈希）';
