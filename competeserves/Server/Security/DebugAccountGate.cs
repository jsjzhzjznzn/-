using System.Collections.Generic;

namespace CompeteServer.Security
{
    /// <summary>
    /// 调试能力闸门类
    /// 把「仅用于联调的能力」（如背包测试发放）限制在白名单账号上。
    /// 白名单来源：环境变量 CS_DEBUG_ACCOUNT_IDS，逗号分隔的账号 ID；未配置或为空表示全部禁止。
    /// 默认禁止而非常开：漏配环境变量时应当失败在「功能不可用」，而不是失败在「人人可用」
    /// </summary>
    public static class DebugAccountGate
    {
        /// <summary>是否已解析过环境变量（环境变量在进程生命周期内不变，只解析一次）</summary>
        private static bool parsed;

        /// <summary>白名单账号ID集合</summary>
        private static readonly HashSet<int> AllowedIds = new();

        /// <summary>
        /// 判断某个账号是否允许使用调试能力
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：账号ID（已由会话绑定，客户端不可伪造）</param>
        /// <returns>返回值类型：bool，用途：在白名单内返回 true</returns>
        public static bool IsAllowed(int playerId)
        {
            EnsureParsed();
            return playerId > 0 && AllowedIds.Contains(playerId);
        }

        /// <summary>
        /// 解析环境变量（幂等）
        /// 启动时与首次判定时都会调用；解析结果通过启动日志打印一次，便于本机联调确认
        /// </summary>
        public static void EnsureParsed()
        {
            if (parsed) return;
            parsed = true;

            string raw = Environment.GetEnvironmentVariable("CS_DEBUG_ACCOUNT_IDS") ?? string.Empty;
            foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(part, out int id) && id > 0) AllowedIds.Add(id);
            }

            Console.WriteLine(AllowedIds.Count == 0
                ? "[调试闸门] CS_DEBUG_ACCOUNT_IDS 未配置，背包测试发放对所有账号关闭"
                : $"[调试闸门] 背包测试发放仅对 {AllowedIds.Count} 个账号开放: {string.Join(",", AllowedIds)}");
        }
    }
}
