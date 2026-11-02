// 命名空间：CompeteServer.Network
// 活动热更内容静态文件服务：给 YooAsset 联机模式（HostPlayMode）当资源 CDN。
// 端口 8899，根目录 = 仓库根的 HotContent 目录（客户端的 ActivityPackagePublisher 直接把补丁文件铺到这里）。
// 失败仅告警：文件服务挂了只影响活动包下载/更新，不影响游戏逻辑端口 8888。
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace CompeteServer.Network
{
    /// <summary>
    /// 活动热更内容静态服务类
    /// 用途：以 HttpListener 对外提供 HotContent 目录的文件下载（activity.version / *.bytes / *.hash / *.bundle）
    /// </summary>
    internal static class HotContentServer
    {
        /// <summary>监听端口（与游戏 TCP 端口分离，避免协议混跑；默认 8899，随 ServerConfig 集中定义）</summary>
        public static int Port => ServerConfig.HotContentPort;

        /// <summary>单文件响应大小上限（20MB），超限返回 413，防止超大文件占满内存/带宽</summary>
        private const long MaxResponseBytes = 20 * 1024 * 1024;

        /// <summary>客户端拼 URL 用的主机前缀（本机联调固定 127.0.0.1）</summary>
        public const string HostPrefix = "http://127.0.0.1";

        private static HttpListener _listener;
        private static string _root;

        /// <summary>静态服务根目录（解析一次，供启动日志与诊断）</summary>
        public static string RootDirectory => _root;

        /// <summary>
        /// 启动静态文件服务
        /// 用途：绑定 127.0.0.1:8899 并进入接收循环（后台线程），线程池处理单个请求
        /// </summary>
        public static void Start()
        {
            if (_listener != null)
                return;

            // 定位 HotContent 根：优先 exe 所在仓库根（bin/Debug/net8.0 上溯 4 级），退回当前工作目录上溯 1 级（dotnet run 时工作目录=Server）
            string rootByExe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "HotContent"));
            string rootByCwd = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "HotContent"));
            _root = Directory.Exists(rootByExe) ? rootByExe : rootByCwd;
            Directory.CreateDirectory(_root);   // 允许目录暂不存在（首次未构建时服务先起来，404 由客户端报）

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();

            Thread acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "HotContentServer" };
            acceptThread.Start();
        }

        /// <summary>接收循环（独立线程，阻塞式取请求后丢线程池处理）</summary>
        private static void AcceptLoop()
        {
            while (_listener != null && _listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = _listener.GetContext();
                }
                catch (Exception)
                {
                    break;   // 监听器被关闭（进程退出）
                }
                ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
            }
        }

        /// <summary>
        /// 处理单个下载请求
        /// 用途：GET /文件名 → HotContent 根下同名文件；带路径穿越防护；命中打印访问日志（热更 E2E 证据）
        /// </summary>
        private static void HandleRequest(HttpListenerContext context)
        {
            try
            {
                if (context.Request.HttpMethod != "GET")
                {
                    context.Response.StatusCode = 405;
                    context.Response.Close();
                    return;
                }

                string relative = Uri.UnescapeDataString(context.Request.Url.AbsolutePath.TrimStart('/'));
                string physical = Path.GetFullPath(Path.Combine(_root, relative));

                // 路径穿越防护：用 GetRelativePath 规范化比较，要求候选路径仍在 _root 目录之内
                // （不用 StartsWith，避免 /HotContent 与 /HotContentEvil 同前缀绕过）
                if (!IsUnderRoot(_root, physical))
                {
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    return;
                }

                if (File.Exists(physical) == false)
                {
                    Console.WriteLine($"[HotContent] 404 {context.Request.Url.AbsolutePath}");
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                // 大小上限校验：超限返回 413，避免把超大文件读入内存/占满带宽
                long size = new FileInfo(physical).Length;
                if (size > MaxResponseBytes)
                {
                    Console.WriteLine($"[HotContent] 413 {relative} ({size} bytes 超过上限 {MaxResponseBytes})");
                    context.Response.StatusCode = 413;
                    context.Response.Close();
                    return;
                }

                // 流式传输：直接把文件流拷贝到响应流，避免 File.ReadAllBytes 一次性载入内存
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/octet-stream";
                context.Response.ContentLength64 = size;
                using (FileStream fileStream = File.OpenRead(physical))
                {
                    fileStream.CopyTo(context.Response.OutputStream);
                }
                Console.WriteLine($"[HotContent] 200 {relative} ({size} bytes)");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[HotContent] 请求处理失败 [{exception.GetType().Name}]: {exception.Message}");
                try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
            }
            finally
            {
                try { context.Response.Close(); } catch { }
            }
        }

        /// <summary>
        /// 判断候选绝对路径是否位于根目录之内（路径穿越防护）
        /// 依据：取相对路径后，只要它为空/指向根自身/以 ".." 起始或本身是绝对路径，即视为越界
        /// </summary>
        private static bool IsUnderRoot(string root, string candidate)
        {
            string rel = Path.GetRelativePath(root, candidate);
            if (string.IsNullOrEmpty(rel) || rel == ".")
            {
                return false;   // 命中的是根目录本身，不作为文件提供
            }

            if (Path.IsPathRooted(rel))
            {
                return false;   // 相对结果仍带根（跨盘/绝对路径），越界
            }

            // 拒绝 ".."、"..\x"、"../x"，防止上跳
            return rel != ".."
                && !rel.StartsWith(".." + Path.DirectorySeparatorChar)
                && !rel.StartsWith(".." + Path.AltDirectorySeparatorChar);
        }
    }
}
