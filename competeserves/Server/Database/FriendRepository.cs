using MySqlConnector;

namespace CompeteServer.Database
{
    /// <summary>
    /// 好友仓库类
    /// 负责 friendships 表的读写：好友列表查询、申请发送、同意/拒绝处理
    /// 双向两行方案：A 与 B 成为好友 = (A→B) 和 (B→A) 各一行，Status=1
    /// 好友申请 = (A→B) 一行，Status=0 待对方同意
    /// </summary>
    public class FriendRepository
    {
        // ──────────────────────────────
        // 好友列表查询
        // ──────────────────────────────

        /// <summary>
        /// 查询好友列表的方法
        /// JOIN friendships 与 accounts 表，返回好友ID与账号名（在线状态由服务端内存补充）
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：当前玩家的账号ID</param>
        /// <returns>返回值类型：List&lt;(int FriendId, string Name)&gt;，用途：好友ID与账号名列表</returns>
        public System.Collections.Generic.List<(int FriendId, string Name)> GetFriends(int playerId)
        {
            List<(int FriendId, string Name)> result = new System.Collections.Generic.List<(int FriendId, string Name)>();
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlCommand command = new(
                    "SELECT f.FriendId, a.Account FROM friendships f " +
                    "JOIN accounts a ON a.Id = f.FriendId " +
                    "WHERE f.OwnerId = @PlayerId AND f.Status = 1 " +
                    "ORDER BY f.CreatedAt",
                    connection);
                command.Parameters.AddWithValue("@PlayerId", playerId);

                using MySqlDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    result.Add((reader.GetInt32(0), reader.GetString(1)));
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"好友列表查询失败 [{exception.GetType().Name}]: {exception.Message}");
            }
            return result;
        }

        // ──────────────────────────────
        // 好友关系校验
        // ──────────────────────────────

        /// <summary>
        /// 校验两人是否为好友的方法
        /// 双向任一行 Status=1 即视为好友（正常情况双向都有行，任一方向存在即可兼容历史数据）
        /// 聊天发送前的准入校验使用
        /// </summary>
        /// <param name="playerA">参数名：playerA，类型：int，用途：一方玩家ID</param>
        /// <param name="playerB">参数名：playerB，类型：int，用途：另一方玩家ID</param>
        /// <returns>返回值类型：bool，用途：是好友返回true；非好友或查询异常返回false（异常时保守拒绝）</returns>
        public bool AreFriends(int playerA, int playerB)
        {
            if (playerA <= 0 || playerB <= 0 || playerA == playerB) return false;

            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlCommand command = new(
                    "SELECT Id FROM friendships WHERE Status = 1 AND " +
                    "((OwnerId = @A AND FriendId = @B) OR (OwnerId = @B AND FriendId = @A))",
                    connection);
                command.Parameters.AddWithValue("@A", playerA);
                command.Parameters.AddWithValue("@B", playerB);

                return command.ExecuteScalar() != null;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"好友关系校验失败 [{exception.GetType().Name}]: {exception.Message}");
                return false;
            }
        }

        // ──────────────────────────────
        // 好友申请
        // ──────────────────────────────

        /// <summary>
        /// 发送好友申请的方法
        /// 校验目标存在、非自己、非重复后写入 friendships（Status=0 待同意）
        /// 返回处理结果与提示信息
        /// </summary>
        /// <param name="ownerId">参数名：ownerId，类型：int，用途：申请人ID</param>
        /// <param name="targetId">参数名：targetId，类型：int，用途：目标玩家ID</param>
        /// <returns>返回值类型：(bool Success, string Message)，用途：成功返回true与提示，失败返回false与原因</returns>
        public (bool Success, string Message) AddFriendRequest(int ownerId, int targetId)
        {
            // 目标是自己：不允许
            if (ownerId == targetId)
                return (false, "不能添加自己为好友");

            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();

                // 第一步：目标玩家必须存在
                using (MySqlCommand targetCommand = new(
                    "SELECT Account FROM accounts WHERE Id = @TargetId",
                    connection))
                {
                    targetCommand.Parameters.AddWithValue("@TargetId", targetId);
                    object? targetAccount = targetCommand.ExecuteScalar();
                    if (targetAccount == null)
                        return (false, "该玩家ID不存在");
                }

                // 第二步：是否已经是好友（任一方向 Status=1 都算）
                using (MySqlCommand friendCommand = new(
                    "SELECT Id FROM friendships WHERE Status = 1 AND " +
                    "((OwnerId = @Me AND FriendId = @Target) OR (OwnerId = @Target AND FriendId = @Me))",
                    connection))
                {
                    friendCommand.Parameters.AddWithValue("@Me", ownerId);
                    friendCommand.Parameters.AddWithValue("@Target", targetId);
                    if (friendCommand.ExecuteScalar() != null)
                        return (false, "你们已经是好友了");
                }

                // 第三步：是否已经申请过（等待对方同意中）
                using (MySqlCommand pendingCommand = new(
                    "SELECT Id FROM friendships WHERE OwnerId = @Me AND FriendId = @Target AND Status = 0",
                    connection))
                {
                    pendingCommand.Parameters.AddWithValue("@Me", ownerId);
                    pendingCommand.Parameters.AddWithValue("@Target", targetId);
                    if (pendingCommand.ExecuteScalar() != null)
                        return (false, "已发送过申请，等待对方同意");
                }

                // 第三步补：对方是否也已向我发过申请（互发申请场景）——
                // 此时若再插一条反向申请，同意方处理时旧行为（INSERT IGNORE）不会升级既有行，
                // 会产生"一边有好友一边没有"的单向关系；改为引导后发者直接去处理先收到的申请
                using (MySqlCommand reversePendingCommand = new(
                    "SELECT Id FROM friendships WHERE OwnerId = @Target AND FriendId = @Me AND Status = 0",
                    connection))
                {
                    reversePendingCommand.Parameters.AddWithValue("@Me", ownerId);
                    reversePendingCommand.Parameters.AddWithValue("@Target", targetId);
                    if (reversePendingCommand.ExecuteScalar() != null)
                        return (false, "对方已向你发送过申请，请在\"新的朋友\"中处理");
                }

                // 第四步：写入申请（Status=0 待同意）
                using (MySqlCommand insertCommand = new(
                    "INSERT INTO friendships (OwnerId, FriendId, Status) VALUES (@Me, @Target, 0)",
                    connection))
                {
                    insertCommand.Parameters.AddWithValue("@Me", ownerId);
                    insertCommand.Parameters.AddWithValue("@Target", targetId);
                    insertCommand.ExecuteNonQuery();
                }

                Console.WriteLine($"好友申请已发送:{ownerId} → {targetId}");
                return (true, "好友申请已发送，等待对方同意");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"好友申请写入失败 [{exception.GetType().Name}]: {exception.Message}");
                return (false, "发送失败，请稍后再试");
            }
        }

        // ──────────────────────────────
        // 待处理申请查询
        // ──────────────────────────────

        /// <summary>
        /// 查询待处理申请的方法
        /// 返回所有申请了我且 Status=0 的申请者（Id 与账号名）
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：当前玩家的账号ID</param>
        /// <returns>返回值类型：List&lt;(int FromId, string Name)&gt;，用途：申请者ID与账号名列表</returns>
        public System.Collections.Generic.List<(int FromId, string Name)> GetPendingRequests(int playerId)
        {
            List<(int FromId, string Name)> result = new System.Collections.Generic.List<(int FromId, string Name)>();
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlCommand command = new(
                    "SELECT f.OwnerId, a.Account FROM friendships f " +
                    "JOIN accounts a ON a.Id = f.OwnerId " +
                    "WHERE f.FriendId = @PlayerId AND f.Status = 0 " +
                    "ORDER BY f.CreatedAt",
                    connection);
                command.Parameters.AddWithValue("@PlayerId", playerId);

                using MySqlDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    result.Add((reader.GetInt32(0), reader.GetString(1)));
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"待处理申请查询失败 [{exception.GetType().Name}]: {exception.Message}");
            }
            return result;
        }

        // ──────────────────────────────
        // 申请回应（同意/拒绝）
        // ──────────────────────────────

        /// <summary>
        /// 回应好友申请的方法
        /// 同意：申请行 Status 置 1 并补上反向行（双向成为好友）
        /// 拒绝：删除申请行
        /// </summary>
        /// <param name="myId">参数名：myId，类型：int，用途：回应者的账号ID</param>
        /// <param name="fromId">参数名：fromId，类型：int，用途：申请者的账号ID</param>
        /// <param name="accept">参数名：accept，类型：bool，用途：true=同意 false=拒绝</param>
        /// <returns>返回值类型：(bool Success, string Message)，用途：处理结果</returns>
        public (bool Success, string Message) RespondToRequest(int myId, int fromId, bool accept)
        {
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlTransaction transaction = connection.BeginTransaction();

                if (accept)
                {
                    // 同意：申请行 Status 置 1
                    using MySqlCommand acceptCommand = new(
                        "UPDATE friendships SET Status = 1 " +
                        "WHERE OwnerId = @From AND FriendId = @Me AND Status = 0",
                        connection, transaction);
                    acceptCommand.Parameters.AddWithValue("@From", fromId);
                    acceptCommand.Parameters.AddWithValue("@Me", myId);
                    int updated = acceptCommand.ExecuteNonQuery();
                    if (updated == 0)
                    {
                        transaction.Rollback();
                        return (false, "申请不存在或已处理");
                    }

                    // 补上反向行（我→申请者，Status=1）；用 ON DUPLICATE KEY 升级而非 INSERT IGNORE——
                    // 互发申请时反向行可能已存在（Status=0），IGNORE 会静默跳过导致单向好友
                    using MySqlCommand reverseCommand = new(
                        "INSERT INTO friendships (OwnerId, FriendId, Status) VALUES (@Me, @From, 1) " +
                        "ON DUPLICATE KEY UPDATE Status = 1",
                        connection, transaction);
                    reverseCommand.Parameters.AddWithValue("@Me", myId);
                    reverseCommand.Parameters.AddWithValue("@From", fromId);
                    reverseCommand.ExecuteNonQuery();
                }
                else
                {
                    // 拒绝：删除申请行
                    using MySqlCommand deleteCommand = new(
                        "DELETE FROM friendships WHERE OwnerId = @From AND FriendId = @Me AND Status = 0",
                        connection, transaction);
                    deleteCommand.Parameters.AddWithValue("@From", fromId);
                    deleteCommand.Parameters.AddWithValue("@Me", myId);
                    deleteCommand.ExecuteNonQuery();
                }

                transaction.Commit();
                string action = accept ? "同意" : "拒绝";
                Console.WriteLine($"好友申请已{action}:{fromId} → {myId}");
                return (true, accept ? "已同意，你们现在是好友了" : "已拒绝该申请");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"好友申请回应失败 [{exception.GetType().Name}]: {exception.Message}");
                return (false, "处理失败，请稍后再试");
            }
        }
    }
}
