namespace CompeteServer.Manager
{
    /// <summary>
    /// 会话登记：按玩家维度在 Valkey 记录"当前由哪个会话代表该玩家"
    /// 用途是顶号的第二道防线——登录时登记、心跳时校验续期、关闭时比较后删除。
    /// 进程内存字典仍是同进程内的即时权威；本类负责踢线通知丢包后的兜底发现，
    /// 以及进程重启后仍可交叉核对。
    /// 可用性约定：Valkey 不可用时全部返回 false，调用方按"无法判定"处理（不阻断登录、不踢人）
    /// </summary>
    public sealed class SessionRegistry
    {
        private static readonly Lazy<SessionRegistry> LazyInstance = new(() => new SessionRegistry());

        /// <summary>获取会话登记单例。</summary>
        public static SessionRegistry Instance => LazyInstance.Value;

        /// <summary>登记键前缀，与排行榜的 compete:lb:* 命名空间并列。</summary>
        private const string KeyPrefix = "compete:session:";

        /// <summary>
        /// 登记存活时长（秒）。兼作自愈窗口：服务端崩溃时无人续期，键自动消失，
        /// 不会让后续登录被一条僵尸记录永久拒绝
        /// </summary>
        private static readonly TimeSpan SessionTtl = TimeSpan.FromSeconds(60);

        /// <summary>
        /// 校验并续期（原子）。值相等说明本会话仍是当前会话，顺手续期
        /// 返回 1=匹配已续期；-1=值不匹配（已被同账号新登录顶替）；0=键不存在（无法判定）
        /// </summary>
        private const string CheckAndRefreshScript = @"
if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
if redis.call('GET', KEYS[1]) == ARGV[1] then
    redis.call('PEXPIRE', KEYS[1], ARGV[2])
    return 1
end
return -1";

        /// <summary>是否已打印过"登记不可用"告警（只打一次，避免 Valkey 离线时刷屏）。</summary>
        private bool downLogged;

        /// <summary>构造会话登记键。</summary>
        private static string BuildKey(int playerId) => KeyPrefix + playerId;

        /// <summary>
        /// 登记本会话为该玩家的当前会话，覆盖旧登记并设置 TTL
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="sessionEpoch">参数名：sessionEpoch，类型：string，用途：本次登录生成的会话标识</param>
        /// <returns>返回值类型：bool，用途：是否登记成功（false = Valkey 不可用，调用方降级不阻断登录）</returns>
        public bool TryRegister(int playerId, string sessionEpoch)
        {
            if (playerId <= 0 || string.IsNullOrEmpty(sessionEpoch)) return false;
            bool ok = ValkeyManager.Instance.TryStringSet(BuildKey(playerId), sessionEpoch, SessionTtl);
            if (!ok && !downLogged)
            {
                downLogged = true;
                Console.WriteLine("[会话登记] Valkey 不可用，顶号兜底校验降级（内存顶号与踢线通知不受影响）");
            }
            else if (ok)
            {
                downLogged = false;
            }
            return ok;
        }

        /// <summary>
        /// 校验本会话是否仍是当前会话，匹配则续期
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：玩家ID</param>
        /// <param name="sessionEpoch">参数名：sessionEpoch，类型：string，用途：本会话的标识</param>
        /// <param name="stillOwner">参数名：stillOwner，类型：out bool，用途：是否仍是当前会话（命令成功时有效）</param>
        /// <returns>返回值类型：bool，用途：命令是否执行成功（false = 无法判定，调用方不踢人）</returns>
        public bool TryValidate(int playerId, string sessionEpoch, out bool stillOwner)
        {
            stillOwner = false;
            if (playerId <= 0 || string.IsNullOrEmpty(sessionEpoch)) return false;
            // 返回值口径：1=匹配；-1=被顶替；0=键不存在无法判定。后者按"命令成功但无法确认归属"处理，
            // 与 -1 区分开，否则 Valkey 重启会把所有在线会话误踢
            if (!ValkeyManager.Instance.TryEvaluateLong(
                    CheckAndRefreshScript, BuildKey(playerId),
                    new object[] { sessionEpoch, (long)SessionTtl.TotalMilliseconds }, out long code))
            {
                return false;
            }
            if (code == -1) stillOwner = false;
            else if (code == 1) stillOwner = true;
            else return false;   // 0：键不存在，无法判定
            return true;
        }
    }
}
