using MySqlConnector;

namespace CompeteServer.Database
{
    /// <summary>MySQL 连接工厂，连接参数来自环境变量，连接池由 MySqlConnector 管理。</summary>
    public sealed class MySqlManager
    {
        private static readonly Lazy<MySqlManager> LazyInstance = new(() => new MySqlManager());

        /// <summary>带库名的连接串，日常读写使用。</summary>
        private readonly string connectionString;

        /// <summary>获取数据库连接工厂单例。</summary>
        public static MySqlManager Instance => LazyInstance.Value;

        /// <summary>初始化并校验数据库环境变量，绝不保存或输出明文密码。</summary>
        private MySqlManager()
        {
            string host = ReadOptional("CS_DB_HOST", "127.0.0.1");
            string portText = ReadOptional("CS_DB_PORT", "3306");
            string database = ReadOptional("CS_DB_NAME", "compete");
            string user = ReadRequired("CS_DB_USER");
            string password = ReadRequired("CS_DB_PASSWORD");
            if (!uint.TryParse(portText, out uint port) || port == 0 || port > 65535)
                throw new InvalidOperationException("CS_DB_PORT 必须是 1-65535 的有效端口。");

            // ──────────────────────────────
            // 构建连接串（带库名）
            // ──────────────────────────────
            MySqlConnectionStringBuilder builder = new MySqlConnectionStringBuilder
            {
                Server = host,
                Port = port,
                UserID = user,
                Password = password,
                Pooling = true,
                MinimumPoolSize = 0,
                MaximumPoolSize = 50,
                ConnectionTimeout = 8,
                DefaultCommandTimeout = 15,
                CharacterSet = "utf8mb4"
            };

            builder.Database = database;
            connectionString = builder.ConnectionString;

            DisplayEndpoint = $"{host}:{port}/{database}";
        }

        /// <summary>获取不包含账号密码的数据库显示地址。</summary>
        public string DisplayEndpoint { get; }

        /// <summary>创建并打开带库名的短连接，释放后自动归还连接池。</summary>
        public MySqlConnection CreateOpenConnection()
        {
            MySqlConnection connection = new(connectionString);
            connection.Open();
            return connection;
        }

        /// <summary>启动时执行 OpenAsync 和 SELECT 1 健康检查。</summary>
        public async Task CheckHealthAsync()
        {
            await using MySqlConnection connection = new(connectionString);
            await connection.OpenAsync();
            await using MySqlCommand command = new("SELECT 1", connection);
            object? result = await command.ExecuteScalarAsync();
            if (Convert.ToInt32(result) != 1)
                throw new InvalidOperationException("数据库 SELECT 1 健康检查未返回预期结果。");
        }

        /// <summary>读取可选环境变量，空值时使用安全默认值。</summary>
        private static string ReadOptional(string name, string fallback)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        /// <summary>读取必填环境变量，缺失时阻止服务启动。</summary>
        private static string ReadRequired(string name)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"缺少数据库环境变量 {name}。");
            return value;
        }
    }
}
