-- ============================================================
-- CompeteServer 002 迁移脚本：好友系统
-- 用途：创建 friendships 好友关系表（一张表存所有玩家的好友关系）
--       双向两行方案：A 与 B 成为好友 = (A→B) 和 (B→A) 各存一行
-- 执行方式：在 MySQL 客户端（mysql 命令行 / Navicat / Workbench 均可）整体执行
-- 注意：若通过 CS_DB_NAME 环境变量改过库名，请同步修改下面的库名
-- ============================================================

USE `compete`;

-- 好友关系表（已存在则跳过）
CREATE TABLE IF NOT EXISTS `friendships` (
    Id INT AUTO_INCREMENT PRIMARY KEY,                    -- 关系行主键
    OwnerId INT NOT NULL,                                  -- 关系所属者（accounts.Id）：谁的好友列表
    FriendId INT NOT NULL,                                 -- 好友是谁（accounts.Id）
    Status TINYINT NOT NULL,                               -- 0=已申请待同意  1=已同意成为好友
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, -- 关系建立时间
    UNIQUE KEY UQ_Pair (OwnerId, FriendId)                 -- 同一对 (我,对方) 只允许一行，防重复申请
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
