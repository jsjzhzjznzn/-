// 命名空间：CompeteServer.Manager
// Agent 共享令牌（Phase 8，模仿 ZCode AUTH_TOKEN）：网关 ↔ Python Agent 双向认证的共享密钥。
// 背景：回环（127.0.0.1）是第一道门，但回环 ≠ 认证——本机任何进程都能向 8898/8897 发
// {player_id: 任意} 问数据。加上共享令牌后，没有密钥的本机进程也进不来（纵深防御）。
// 存放：Server/Config/agent_token.txt（已 gitignore，与 AgentServer/.env 的
//   AGENT_INTERNAL_TOKEN 同值）。文件缺失 = 未配置 → fail-open（回环仍守门），启动时告警。
using System;
using System.IO;

namespace CompeteServer.Manager
{
    /// <summary>
    /// Agent 共享令牌：一次性懒加载 Config/agent_token.txt
    /// </summary>
    internal static class AgentToken
    {
        private static string _cached = string.Empty;
        private static bool _loaded;

        /// <summary>共享令牌值；未配置返回空串（调用方按 fail-open 处理）</summary>
        public static string Shared
        {
            get
            {
                if (!_loaded)
                {
                    _loaded = true;
                    try
                    {
                        _cached = File.ReadAllText("Config/agent_token.txt").Trim();
                    }
                    catch
                    {
                        _cached = string.Empty;
                    }
                    Console.WriteLine(_cached.Length > 0
                        ? $"[AgentToken] 已启用（{_cached.Length} 字符，8898/8897 双向校验）"
                        : "[AgentToken] 未配置：Agent 认证 fail-open（回环仍是第一道门；建议生成 Config/agent_token.txt）");
                }
                return _cached;
            }
        }

        /// <summary>校验请求方带来的令牌（未配置=放行；配置了必须逐字节相等）</summary>
        public static bool IsValid(string? provided)
        {
            string expected = Shared;
            return expected.Length > 0 && string.Equals(expected, provided ?? string.Empty, StringComparison.Ordinal);
        }
    }
}
