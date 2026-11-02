// 命名空间：CompeteServer
// 属于服务端程序的根命名空间
using CompeteServer.Network;

namespace CompeteServer
{
    /// <summary>
    /// 服务器启动引导类
    /// 负责初始化服务器的所有核心组件和模块
    /// 采用懒汉式单例模式，确保全局只有一个引导实例
    /// </summary>
    public class ServerBootstrap
    {
        // ──────────────────────────────
        // 单例实现
        // ──────────────────────────────

        /// <summary>
        /// 懒加载单例载体
        /// 用途：Lazy&lt;T&gt; 保证多线程首次访问只创建一次实例
        /// </summary>
        private static readonly Lazy<ServerBootstrap> LazyInstance = new(() => new ServerBootstrap());

        /// <summary>
        /// 公开静态属性 - 获取单例实例
        /// 用途：线程安全懒加载的全局访问点
        /// </summary>
        public static ServerBootstrap Instance => LazyInstance.Value;

        // ──────────────────────────────
        // 公共方法
        // ──────────────────────────────

        /// <summary>
        /// 启动服务器引导流程的方法
        /// 依次初始化所有核心模块
        /// </summary>
        public void Start()
        {
            InitManager();
            Console.WriteLine("Bootstrap Init Complete");
        }

        // ──────────────────────────────
        // 私有方法
        // ──────────────────────────────

        /// <summary>
        /// 初始化所有管理器的方法
        /// 按顺序启动网络等模块
        /// </summary>
        private void InitManager()
        {
            // 启动即打印生效的 Agent 并发闸门，便于灰度/排查时确认环境变量是否吃到
            Console.WriteLine($"Agent 并发闸门: {ServerConfig.AgentMaxConcurrency}（CS_AGENT_MAX_CONCURRENCY）");
            Console.WriteLine("初始化Network");
            SocketServer.Instance.Start(ServerConfig.SocketPort);
            Console.WriteLine("初始化完成");
        }
    }
}
