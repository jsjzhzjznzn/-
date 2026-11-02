using MySqlConnector;

namespace CompeteServer.Database
{
    /// <summary>
    /// 账号仓库类
    /// 负责账号的登录验证、注册、好友列表查询；表结构由使用者提前手动建好（见 Migrations\001_InitAccounts.sql），服务端只负责使用
    /// </summary>
    public class AccountRepository
    {
        // ──────────────────────────────
        // 登录验证
        // ──────────────────────────────

        /// <summary>
        /// 登录验证方法
        /// 按账号密码精确匹配查询账号ID
        /// 注意：学习项目采用明文对比，生产环境应改为加盐哈希后再比对
        /// </summary>
        /// <param name="account">参数名：account，类型：string，用途：玩家输入的账号</param>
        /// <param name="password">参数名：password，类型：string，用途：玩家输入的密码</param>
        /// <returns>返回值类型：int?，用途：验证通过返回账号ID，失败返回null</returns>
        public int? Login(string account, string password)
        {
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlCommand command = new(
                    "SELECT Id FROM accounts WHERE Account = @Account AND Password = @Password",
                    connection);
                command.Parameters.AddWithValue("@Account", account);
                command.Parameters.AddWithValue("@Password", password);

                object? result = command.ExecuteScalar();
                return result == null ? null : Convert.ToInt32(result);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"登录查询失败 [{exception.GetType().Name}]: {exception.Message}");
                return null;
            }
        }

        // ──────────────────────────────
        // 注册账号
        // ──────────────────────────────

        /// <summary>
        /// 注册账号方法
        /// 先查重再插入：账号已存在则失败，成功后新账号即可用于登录
        /// 注意：学习项目采用明文存储，生产环境应改为加盐哈希后再入库
        /// </summary>
        /// <param name="account">参数名：account，类型：string，用途：玩家输入的账号</param>
        /// <param name="password">参数名：password，类型：string，用途：玩家输入的密码</param>
        /// <returns>返回值类型：(bool Success, string Message)，用途：成功返回true与提示信息，失败返回false与原因</returns>
        public (bool Success, string Message) Register(string account, string password)
        {
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();

                // 第一步：查重，账号已被注册则直接失败
                using (MySqlCommand checkCommand = new(
                    "SELECT Id FROM accounts WHERE Account = @Account",
                    connection))
                {
                    checkCommand.Parameters.AddWithValue("@Account", account);
                    if (checkCommand.ExecuteScalar() != null)
                        return (false, "该账号已被注册");
                }

                // 第二步：插入新账号（UNIQUE KEY UQ_Account 也会兜底并发重名）
                using (MySqlCommand insertCommand = new(
                    "INSERT INTO accounts (Account, Password) VALUES (@Account, @Password)",
                    connection))
                {
                    insertCommand.Parameters.AddWithValue("@Account", account);
                    insertCommand.Parameters.AddWithValue("@Password", password);
                    insertCommand.ExecuteNonQuery();
                }

                Console.WriteLine($"新账号注册成功:{account}");
                return (true, "注册成功");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"注册写入失败 [{exception.GetType().Name}]: {exception.Message}");
                return (false, "注册失败，请稍后再试");
            }
        }

        /// <summary>
        /// 按账号名查玩家ID的方法
        /// 自动登录令牌校验用：以账号定位玩家后再校验令牌归属，找不到即自动登录失败
        /// </summary>
        /// <param name="account">参数名：account，类型：string，用途：账号名</param>
        /// <returns>返回值类型：int?，用途：找到返回玩家ID，不存在返回null</returns>
        public int? GetPlayerIdByAccount(string account)
        {
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlCommand command = new(
                    "SELECT Id FROM accounts WHERE Account = @Account",
                    connection);
                command.Parameters.AddWithValue("@Account", account);

                object? result = command.ExecuteScalar();
                return result == null ? null : Convert.ToInt32(result);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"按账号查询玩家失败 [{exception.GetType().Name}]: {exception.Message}");
                return null;
            }
        }

        // ──────────────────────────────
        // 查询玩家
        // ──────────────────────────────

        /// <summary>
        /// 查询玩家的方法
        /// 按玩家ID（accounts.Id）查找账号是否存在，返回账号名
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：要查询的玩家ID</param>
        /// <returns>返回值类型：(bool Found, string Name)，用途：找到返回true与账号名，不存在返回false与空名</returns>
        public (bool Found, string Name) QueryPlayer(int playerId)
        {
            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();
                using MySqlCommand command = new(
                    "SELECT Account FROM accounts WHERE Id = @PlayerId",
                    connection);
                command.Parameters.AddWithValue("@PlayerId", playerId);

                object? result = command.ExecuteScalar();
                return result != null ? (true, Convert.ToString(result) ?? string.Empty) : (false, string.Empty);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"查询玩家失败 [{exception.GetType().Name}]: {exception.Message}");
                return (false, string.Empty);
            }
        }

        /// <summary>
        /// 按玩家ID批量取账号名的方法
        /// 榜单等列表场景用：一次 IN 查询取回全部名字（替代逐行 QueryPlayer 的 N+1 次往返）
        /// </summary>
        /// <param name="playerIds">参数名：playerIds，类型：IEnumerable&lt;int&gt;，用途：玩家ID集合</param>
        /// <returns>返回值类型：Dictionary&lt;int, string&gt;，用途：玩家ID→账号名（不存在的ID不在表中）</returns>
        public Dictionary<int, string> GetNamesByIds(IEnumerable<int> playerIds)
        {
            var result = new Dictionary<int, string>();
            List<int> ids = playerIds.Distinct().ToList();
            if (ids.Count == 0) return result;

            try
            {
                using MySqlConnection connection = MySqlManager.Instance.CreateOpenConnection();

                // 动态参数化 IN 列表：ID 值全部走参数不拼 SQL（每ID一个参数，规模即榜单 TopN≤100）
                List<string> parameterNames = new();
                using MySqlCommand command = new(string.Empty, connection);
                for (int i = 0; i < ids.Count; i++)
                {
                    string parameterName = $"@Id{i}";
                    parameterNames.Add(parameterName);
                    command.Parameters.AddWithValue(parameterName, ids[i]);
                }
                command.CommandText = "SELECT Id, Account FROM accounts WHERE Id IN (" + string.Join(", ", parameterNames) + ")";

                using MySqlDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    result[reader.GetInt32(0)] = reader.GetString(1);
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"批量查询玩家名失败 [{exception.GetType().Name}]: {exception.Message}");
            }
            return result;
        }
    }
}
