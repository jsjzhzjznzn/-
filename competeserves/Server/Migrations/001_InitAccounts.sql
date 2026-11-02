-- ============================================================
-- CompeteServer 初始化脚本
-- 用途：手动创建 compete 数据库与 accounts 账号表
--       （服务端不再自动建表，表结构由使用者管理，服务端只负责使用）
-- 执行方式：在 MySQL 客户端（mysql -u root -p、Navicat、Workbench 等）中整体执行
-- 注意：若通过 CS_DB_NAME 环境变量改过库名，请同步修改下面的库名
-- ============================================================

-- 1. 创建数据库（已存在则跳过）
CREATE DATABASE IF NOT EXISTS `compete` DEFAULT CHARACTER SET utf8mb4;

USE `compete`;

-- 2. 创建账号表（已存在则跳过）
CREATE TABLE IF NOT EXISTS `accounts` (
    Id INT AUTO_INCREMENT PRIMARY KEY,
    Account VARCHAR(64) NOT NULL,
    Password VARCHAR(128) NOT NULL,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE KEY UQ_Account (Account)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 3. 种子测试账号（已存在则跳过；客户端登录测试用 test/123456，不需要可删除此段）
INSERT IGNORE INTO `accounts` (`Account`, `Password`) VALUES ('test', '123456');
