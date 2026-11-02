// 命名空间：CompeteServer.Network
// 属于服务端网络层，负责管理客户端连接会话
namespace CompeteServer.Network
{
    /// <summary>
    /// 会话管理器类
    /// 负责管理所有在线的客户端连接会话（Session）
    /// 提供添加、删除、绑定玩家、广播等功能
    /// 采用单例模式，确保全局只有一个会话管理器实例
    /// </summary>
    public class SessionManager
    {
        /// <summary>
        /// 当前服务端允许的最大并发连接数。
        /// 目标容量 1000 并发在线，留一倍余量：断线重连、顶号替换会短暂双占。
        /// 注意：这个数只挡 TCP 连接；真正的吞吐瓶颈在 Agent 转发（见 AgentHandler 的并发闸门）。
        /// </summary>
        public const int MaxSessions = 2000;

        // ──────────────────────────────
        // 单例实现
        // ──────────────────────────────

        /// <summary>
        /// 懒加载单例载体
        /// 用途：Lazy&lt;T&gt; 保证多线程首次访问只创建一次实例
        /// </summary>
        private static readonly Lazy<SessionManager> LazyInstance = new(() => new SessionManager());

        /// <summary>
        /// 公开静态属性 - 获取单例实例
        /// 用途：线程安全懒加载的全局访问点
        /// </summary>
        public static SessionManager Instance => LazyInstance.Value;

        // ──────────────────────────────
        // 私有字段
        // ──────────────────────────────

        /// <summary>
        /// 在线客户端Session列表
        /// 类型：List&lt;ClientSession&gt;
        /// 用途：存储所有在线的客户端会话对象
        /// </summary>
        private readonly List<ClientSession> sessions;

        /// <summary>
        /// 线程锁对象
        /// 类型：object
        /// 用途：防止多个异步线程同时修改List导致数据异常
        /// </summary>
        private readonly object locker;

        // ──────────────────────────────
        // 构造函数
        // ──────────────────────────────

        /// <summary>
        /// 私有构造函数
        /// 初始化会话列表和线程锁，防止外部实例化
        /// </summary>
        private SessionManager()
        {
            sessions = new List<ClientSession>();
            locker = new object();
        }

        // ──────────────────────────────
        // 公共方法
        // ──────────────────────────────

        /// <summary>
        /// 添加客户端Session的方法
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：要添加的客户端会话对象</param>
        /// <returns>返回值类型：bool，用途：未超出人数上限时返回true</returns>
        public bool TryAdd(ClientSession session)
        {
            lock (locker)
            {
                if (sessions.Count >= MaxSessions) return false;
                sessions.Add(session);
                Console.WriteLine($"当前在线:{sessions.Count}");
                return true;
            }
        }

        /// <summary>
        /// 将已登录的账号绑定到当前连接，并保证同一个账号同时只有一个在线会话。
        /// 同账号已有别的在线会话时执行【顶号】：在锁内摘掉旧会话并标记，
        /// 由调用方在【锁外】推送 KickoutNotify 并关闭它（Send 会在调用线程序列化并启动写流，不能放进全局锁）。
        /// 摘册 + 标记 + 绑定必须同锁完成：拆成两次加锁会让两个并发登录都查不到对方而同时绑定成功。
        /// </summary>
        /// <param name="session">准备绑定的客户端连接。</param>
        /// <param name="playerId">数据库账号主键。</param>
        /// <param name="evicted">输出：被顶号下线的旧会话；无旧会话时为 null，需在锁外交给调用方踢下。</param>
        /// <returns>绑定成功返回真；会话不在册或该连接已绑定过身份则返回假。</returns>
        public bool TryBindPlayer(ClientSession session, int playerId, out ClientSession? evicted)
        {
            evicted = null;
            if (session == null || playerId <= 0) return false;
            lock (locker)
            {
                if (!sessions.Contains(session) || session.PlayerId > 0) return false;

                evicted = sessions.Find(other =>
                    other != session && other.PlayerId == playerId);
                if (evicted != null)
                {
                    sessions.Remove(evicted);
                    evicted.MarkEvicted();
                }

                session.PlayerId = playerId;
                return true;
            }
        }

        /// <summary>
        /// 按玩家ID查找会话的方法
        /// 聊天等系统向指定玩家主动推送消息时使用（PlayerEntity 不持有会话引用，只能从这里查）
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：已登录玩家的账号ID</param>
        /// <returns>返回值类型：ClientSession?，用途：在线返回其会话，未登录或不在线返回null</returns>
        public ClientSession? GetSessionByPlayerId(int playerId)
        {
            if (playerId <= 0) return null;
            lock (locker)
            {
                return sessions.Find(session => session.PlayerId == playerId);
            }
        }

        /// <summary>
        /// 快照当前在线玩家ID列表的方法
        /// 用途：后台巡检类服务（阿福主动提醒等）遍历在线玩家；锁内快照、锁外消费，遍历中会话增删不影响快照
        /// </summary>
        /// <returns>返回值类型：List&lt;int&gt;，用途：当前已绑定身份（PlayerId&gt;0）的在线玩家ID快照</returns>
        public List<int> GetOnlinePlayerIds()
        {
            lock (locker)
            {
                List<int> ids = new(sessions.Count);
                foreach (ClientSession session in sessions)
                {
                    if (session.PlayerId > 0) ids.Add(session.PlayerId);
                }
                return ids;
            }
        }

        /// <summary>
        /// 移除客户端Session的方法
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：要移除的客户端会话对象</param>
        public void Remove(ClientSession session)
        {
            lock (locker)
            {
                sessions.Remove(session);
                Console.WriteLine($"当前在线:{sessions.Count}");
            }
        }

        /// <summary>
        /// 全服广播单批的目标会话数：扇出压力与在线人数解耦的关键常量。
        /// 一次性 fire-and-forget 上万个 Send 会同时抢 sendLock、同时向线程池灌入续作，
        /// 慢客户端还会让挂起的写不可控；分批把瞬时并发钉在常量级，与人数无关
        /// </summary>
        private const int BroadcastBatchSize = 300;

        /// <summary>
        /// 广播消息给所有在线客户端的方法（分批扇出，不阻塞调用方）
        /// 先复制列表再遍历发送，避免遍历过程中列表被修改
        /// </summary>
        /// <param name="data">参数名：data，类型：byte[]，用途：要广播的字节数据（不含长度头），全批复用同一份、不重复序列化</param>
        /// <returns>返回值类型：Task，用途：扇出完成的信号；调用方不关心时可 fire-and-forget</returns>
        public async Task Broadcast(byte[] data)
        {
            List<ClientSession> copy;

            lock (locker)
            {
                copy = new List<ClientSession>(sessions);
            }

            for (int offset = 0; offset < copy.Count; offset += BroadcastBatchSize)
            {
                if (offset > 0)
                {
                    // 让出线程池：上一批的写续作先落地，再放下一批。
                    // 用 Task.Yield 而非 Delay(1)，只保证线程池喘息、不引入定时器与最低粒度（约15.6ms）
                    await Task.Yield();
                }

                int end = Math.Min(offset + BroadcastBatchSize, copy.Count);
                for (int i = offset; i < end; i++)
                {
                    _ = SafeSend(copy[i], data);
                }
            }
        }

        /// <summary>
        /// 安全发送辅助方法
        /// 用途：包裹原 fire-and-forget 的 session.Send，捕获单个连接发送失败的异常，避免广播循环被一个坏连接打断
        /// 说明：ClientSession.Close 为私有，无法在此主动关闭连接；仅记录日志，实际回收依赖读循环异常退出
        /// 及 ClientSession 即将加入的空闲超时（由会话自身清理）
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：目标会话</param>
        /// <param name="data">参数名：data，类型：byte[]，用途：要发送的字节数据（不含长度头）</param>
        private static async Task SafeSend(ClientSession session, byte[] data)
        {
            try
            {
                await session.Send(data);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[SessionManager] 发送失败 SessionID={session.SessionID} [{exception.GetType().Name}]: {exception.Message}");
            }
        }

        // ──────────────────────────────
        // 停机
        // ──────────────────────────────

        /// <summary>
        /// 停机时清空跟踪的会话集合
        /// 说明：ClientSession.Close 为私有方法，SessionManager 无权直接关闭底层连接；
        /// 这里仅解除管理器对会话的引用并记录数量，TCP 连接随进程退出由操作系统回收
        /// </summary>
        public void CloseAll()
        {
            lock (locker)
            {
                int count = sessions.Count;
                sessions.Clear();
                Console.WriteLine($"[SessionManager] 关闭全部会话，共 {count} 个（连接随进程退出回收）");
            }
        }

        // ──────────────────────────────
        // 属性
        // ──────────────────────────────

        /// <summary>
        /// 当前在线人数
        /// 类型：int
        /// 用途：获取当前在线的客户端会话数量
        /// </summary>
        public int Count
        {
            get
            {
                lock (locker)
                {
                    return sessions.Count;
                }
            }
        }
    }
}
