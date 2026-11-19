-- CompeteServer 021 迁移脚本：好友表补 (FriendId, Status) 索引
-- 用途：FriendRepository.GetPendingRequests 的查询条件以 FriendId + Status 开头
--       （WHERE f.FriendId = @PlayerId AND f.Status = 0 ORDER BY f.CreatedAt），
--       而原有唯一键 UQ_Pair 只有 (OwnerId, FriendId) 一个左前缀，该查询用不上它，
--       执行计划是整表 ALL 扫描 + Using filesort。补索引后按 (FriendId, Status) 定位，
--       并顺带消除排序（索引本身有序）。
-- 说明：好友列表/已否成为好友等按 OwnerId 开头的查询继续走 UQ_Pair，不受影响。
-- 执行方式：在 MySQL 客户端整体执行；服务端启动时也会做同样的幂等检查（见 FriendRepository.EnsureTable）

SET @has_idx := (
  SELECT COUNT(*) FROM information_schema.statistics
  WHERE table_schema = DATABASE() AND table_name = 'friendships' AND index_name = 'idx_friend_status'
);

SET @sql := IF(@has_idx = 0,
  'ALTER TABLE `friendships` ADD KEY `idx_friend_status` (`FriendId`, `Status`)',
  'SELECT ''friendships.idx_friend_status 已存在，跳过'' AS info');

PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;