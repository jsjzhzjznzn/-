// 引入网络通信所需的IP地址和Socket类
using System.Net;

// 引入TCP监听器类
using System.Net.Sockets;

// 命名空间：CompeteServer.Network
// 属于服务端网络层，负责管理与客户端的连接
namespace CompeteServer.Network
{
    /// <summary>
    /// TCP服务器类
    /// 负责监听指定端口，接受客户端连接请求
    /// 采用单例模式，确保全局只有一个服务器实例
    /// </summary>
    public class SocketServer
    {
        // ──────────────────────────────
        // 单例实现
        // ──────────────────────────────

        /// <summary>
        /// 懒加载单例载体
        /// 用途：Lazy&lt;T&gt; 保证多线程首次访问只创建一次实例
        /// </summary>
        private static readonly Lazy<SocketServer> LazyInstance = new(() => new SocketServer());

        /// <summary>
        /// 公开静态属性 - 获取单例实例
        /// 用途：线程安全懒加载的全局访问点
        /// </summary>
        public static SocketServer Instance => LazyInstance.Value;

        // ──────────────────────────────
        // 私有字段
        // ──────────────────────────────

        /// <summary>
        /// TCP监听器对象
        /// 类型：TcpListener
        /// 用途：用于监听指定端口上的传入连接请求
        /// </summary>
        private TcpListener listener = null!;

        // ──────────────────────────────
        // 公共方法
        // ──────────────────────────────

        /// <summary>
        /// 启动服务器监听的方法
        /// 开始在指定端口上监听客户端连接
        /// </summary>
        /// <param name="port">参数名：port，类型：int，用途：要监听的端口号</param>
        public void Start(int port)
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            Console.WriteLine($"Socket服务器启动，监听端口:{port}");
            _ = AcceptLoop();
        }

        /// <summary>
        /// 停止监听（优雅停机用）
        /// 关闭 listener 会让 AcceptLoop 的 await 抛出 ObjectDisposedException 而正常退出
        /// </summary>
        public void Stop()
        {
            try
            {
                listener?.Stop();
            }
            catch
            {
                // 停机路径尽力而为：listener 未启动或已关闭均忽略
            }
        }

        // ──────────────────────────────
        // 私有方法
        // ──────────────────────────────

        /// <summary>
        /// 异步接受客户端连接的循环方法
        /// 持续等待并接受新的客户端连接，每接受一个连接就创建一个ClientSession进行处理
        /// </summary>
        private async Task AcceptLoop()
        {
            // 忙刷防护：非停机类异常连续出现达到上限即退出，避免 listener 级异常导致 CPU 空转刷屏
            int consecutiveErrors = 0;
            const int maxConsecutiveErrors = 10;

            while (true)
            {
                try
                {
                    TcpClient client = await listener.AcceptTcpClientAsync();
                    client.NoDelay = true;
                    Console.WriteLine("客户端连接成功");

                    ClientSession session = new ClientSession(client);
                    if (!SessionManager.Instance.TryAdd(session))
                    {
                        Console.WriteLine($"服务器已满，拒绝第 {SessionManager.MaxSessions + 1} 个连接。");
                        client.Close();
                        continue;
                    }
                    session.Start();

                    // 正常接受一次连接即清零异常计数
                    consecutiveErrors = 0;
                }
                catch (ObjectDisposedException)
                {
                    // listener 被 Stop/Dispose：正常停机，退出循环
                    break;
                }
                catch (SocketException e) when (
                    e.SocketErrorCode == SocketError.Interrupted ||
                    e.SocketErrorCode == SocketError.OperationAborted ||
                    e.SocketErrorCode == SocketError.NetworkDown)
                {
                    // 停机导致的阻塞中断：正常退出
                    break;
                }
                catch (Exception e)
                {
                    consecutiveErrors++;
                    Console.WriteLine($"连接异常({consecutiveErrors}/{maxConsecutiveErrors}):{e.Message}");
                    if (consecutiveErrors >= maxConsecutiveErrors)
                    {
                        Console.WriteLine("AcceptLoop 连续异常达到上限，停止接受新连接。");
                        break;
                    }
                    // 短退避，避免异常持续抛出时空转
                    await Task.Delay(100);
                }
            }
        }
    }
}
