using MongoDB.Bson;
using MongoDB.Driver;

namespace CompeteServer.Database
{
    /// <summary>MongoDB 客户端持有者，连接参数来自环境变量；客户端自带连接池且全进程唯一，请勿按次新建。</summary>
    public sealed class MongoManager
    {
        private static readonly Lazy<MongoManager> LazyInstance = new(() => new MongoManager());

        /// <summary>全进程唯一的 MongoClient，线程安全、内部管理连接池。</summary>
        private readonly IMongoClient client;

        /// <summary>默认库句柄，日常读写使用。</summary>
        private readonly IMongoDatabase database;

        /// <summary>熔断到期时间（UtcNow Ticks）。操作失败后的一段时间内 TryGetCollection 直接返回 false 快速降级。</summary>
        private long downUntilTicks;

        /// <summary>熔断冷却时长：期间不再对 Mongo 发起任何操作。
        /// 会话消息是串行分发的，Mongo 离线时一次选服超时会把它后面排队的所有消息全部堵住
        /// （登录后客户端自动拉好友列表 → 未读数聚合打 Mongo → 紧接着的活动列表等请求全被拖慢数秒）。</summary>
        private static readonly TimeSpan DownCooldown = TimeSpan.FromSeconds(30);

        /// <summary>获取数据库客户端持有者单例。</summary>
        public static MongoManager Instance => LazyInstance.Value;

        /// <summary>初始化并校验 Mongo 环境变量，绝不保存或输出明文密码。</summary>
        private MongoManager()
        {
            string host = ReadOptional("CS_MONGO_HOST", "127.0.0.1");
            string portText = ReadOptional("CS_MONGO_PORT", "27017");
            string databaseName = ReadOptional("CS_MONGO_NAME", "compete");
            if (!uint.TryParse(portText, out uint port) || port == 0 || port > 65535)
                throw new InvalidOperationException("CS_MONGO_PORT 必须是 1-65535 的有效端口。");

            // ──────────────────────────────
            // 构建客户端设置（账号密码可选，本机单机默认未开鉴权）
            // ──────────────────────────────
            MongoClientSettings settings = new()
            {
                Server = new MongoServerAddress(host, (int)port),
                // 选服/连接超时压到 2s：驱动默认 8s 太长——会话消息串行处理，一次超时会堵住整条消息队列
                ConnectTimeout = TimeSpan.FromSeconds(2),
                ServerSelectionTimeout = TimeSpan.FromSeconds(2),
                MinConnectionPoolSize = 0,
                MaxConnectionPoolSize = 50
            };

            string? user = ReadOptionalNullable("CS_MONGO_USER");
            string? password = ReadOptionalNullable("CS_MONGO_PASSWORD");
            if (user != null && password != null)
                settings.Credential = MongoCredential.CreateCredential(databaseName, user, password);

            client = new MongoClient(settings);
            database = client.GetDatabase(databaseName);

            DisplayEndpoint = $"{host}:{port}/{databaseName}";
        }

        /// <summary>获取不包含账号密码的数据库显示地址。</summary>
        public string DisplayEndpoint { get; }

        /// <summary>获取集合句柄，集合不存在时由首次写入自动创建。</summary>
        public IMongoCollection<TDocument> GetCollection<TDocument>(string name)
            => database.GetCollection<TDocument>(name);

        /// <summary>
        /// 取集合句柄；熔断冷却期内返回 null（调用方按"Mongo 暂不可用"降级，不用再白等一次选服超时）。
        /// </summary>
        /// <param name="name">参数名：name，类型：string，用途：集合名</param>
        /// <returns>返回值类型：IMongoCollection?，用途：集合句柄（null = 熔断中，Mongo 暂不可用）</returns>
        public IMongoCollection<TDocument>? GetCollectionIfAvailable<TDocument>(string name)
        {
            if (DateTime.UtcNow.Ticks < Interlocked.Read(ref downUntilTicks)) return null;
            return database.GetCollection<TDocument>(name);
        }

        /// <summary>
        /// 标记进入熔断冷却（Mongo 操作失败时调用）。
        /// 冷却期内 TryGetCollection 直接返回 false，调用方立刻走降级分支。
        /// </summary>
        /// <param name="operation">参数名：operation，类型：string，用途：失败的操作名（日志用）</param>
        /// <param name="exception">参数名：exception，类型：Exception，用途：失败原因（日志用）</param>
        public void MarkDown(string operation, Exception exception)
        {
            Interlocked.Exchange(ref downUntilTicks, DateTime.UtcNow.Add(DownCooldown).Ticks);
            Console.WriteLine($"Mongo {operation} 失败 [{exception.GetType().Name}]: {exception.Message}（进入{DownCooldown.TotalSeconds:0}s熔断冷却，聊天功能降级）");
        }

        /// <summary>启动时执行 ping 健康检查。</summary>
        public async Task CheckHealthAsync()
        {
            try
            {
                BsonDocumentCommand<BsonDocument> command = new(new BsonDocument("ping", 1));
                await database.RunCommandAsync(command);
            }
            catch (Exception exception)
            {
                // 启动即连不上 → 立刻进入熔断，别让服务起来后的第一个客户端请求再白等一次选服超时
                MarkDown("健康检查", exception);
                throw;
            }
        }

        /// <summary>读取可选环境变量，空值时使用安全默认值。</summary>
        private static string ReadOptional(string name, string fallback)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        /// <summary>读取可选环境变量，空值或缺失时返回 null（用于账号密码等可不填项）。</summary>
        private static string? ReadOptionalNullable(string name)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
