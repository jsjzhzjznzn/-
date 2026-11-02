// 命名空间：CompeteServer
// 服务端端口等运行参数的集中定义（避免散落在各文件里硬编码）
namespace CompeteServer
{
    /// <summary>
    /// 服务器配置常量类
    /// 用途：统一管理网络端口，带默认值并支持环境变量覆盖（风格与 CS_VALKEY / CS_DB_* 一致）
    /// </summary>
    public static class ServerConfig
    {
        /// <summary>TCP 游戏服务端口（默认 8888，可用环境变量 CS_SOCKET_PORT 覆盖）。</summary>
        public static readonly int SocketPort = ReadPort("CS_SOCKET_PORT", 8888);

        /// <summary>活动热更内容静态服务端口（默认 8899，可用环境变量 CS_HOTCONTENT_PORT 覆盖）。</summary>
        public static readonly int HotContentPort = ReadPort("CS_HOTCONTENT_PORT", 8899);

        /// <summary>Agent 内部数据 API 端口（默认 8897，可用环境变量 CS_AGENT_INTERNAL_PORT 覆盖）。
        /// 仅 127.0.0.1：给 Python Agent 服务的工具回查玩家数据（profile/bag/quests），公网不可达。</summary>
        public static readonly int AgentInternalPort = ReadPort("CS_AGENT_INTERNAL_PORT", 8897);

        /// <summary>Python Agent 服务（LangGraph 大脑）地址（默认 http://127.0.0.1:8898，可用环境变量 CS_AGENT_PYTHON_URL 覆盖）。
        /// 网关转发 /agent/chat 与 /agent/history 都以它为根。</summary>
        public static readonly string AgentPythonBaseUrl =
            System.Environment.GetEnvironmentVariable("CS_AGENT_PYTHON_URL")?.Trim().TrimEnd('/')
            is { Length: > 0 } url ? url : "http://127.0.0.1:8898";

        /// <summary>
        /// Agent 转发的全局并发上限（默认 600，可用环境变量 CS_AGENT_MAX_CONCURRENCY 覆盖）。
        /// 依据：1000 并发在线按 30s 一句估算需要约 170 个并发轮次（p95 5.2s），600 留 3 倍余量；
        /// 往上调的真正瓶颈是 LLM 上游配额与 Node 进程内存，不是网关线程。
        /// </summary>
        public static readonly int AgentMaxConcurrency = ReadCount("CS_AGENT_MAX_CONCURRENCY", 600, 1, 5000);

        /// <summary>读取数量：落在 [min,max] 内才采信，否则回退默认值（防脏环境变量）。</summary>
        private static int ReadCount(string name, int fallback, int min, int max)
        {
            string? value = System.Environment.GetEnvironmentVariable(name);
            return int.TryParse(value?.Trim(), out int n) && n >= min && n <= max ? n : fallback;
        }

        /// <summary>读取端口：合法整数则用之，否则回退默认值。</summary>
        private static int ReadPort(string name, int fallback)
        {
            string? value = System.Environment.GetEnvironmentVariable(name);
            return int.TryParse(value?.Trim(), out int port) && port > 0 && port <= 65535
                ? port
                : fallback;
        }
    }
}
