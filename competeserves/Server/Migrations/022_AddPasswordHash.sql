-- CompeteServer 022 迁移脚本：accounts 表增加密码哈希列
-- 用途：把账号密码从明文改为「每账号随机盐 + PBKDF2-HMAC-SHA256 哈希」存储。
--       本次只加列、不动 Password 列：老账号在首次成功登录时自动回填哈希，
--       回填完成后可另行做一次删列迁移（见 Docs/Documents/账号密码哈希与登录限流方案.md）。
-- 说明：两列均可空，因此迁移对存量行无影响；可重复执行。
-- 执行方式：在 MySQL 客户端整体执行

SET @has_salt := (
  SELECT COUNT(*) FROM information_schema.columns
  WHERE table_schema = DATABASE() AND table_name = 'accounts' AND column_name = 'Salt'
);
SET @sql := IF(@has_salt = 0,
  'ALTER TABLE `accounts` ADD COLUMN `Salt` CHAR(24) NULL COMMENT ''PBKDF2 盐（Base64，16 字节）''',
  'SELECT ''accounts.Salt 已存在，跳过'' AS info');
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @has_hash := (
  SELECT COUNT(*) FROM information_schema.columns
  WHERE table_schema = DATABASE() AND table_name = 'accounts' AND column_name = 'PasswordHash'
);
SET @sql := IF(@has_hash = 0,
  'ALTER TABLE `accounts` ADD COLUMN `PasswordHash` CHAR(44) NULL COMMENT ''PBKDF2 哈希（Base64，32 字节）''',
  'SELECT ''accounts.PasswordHash 已存在，跳过'' AS info');
PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;
