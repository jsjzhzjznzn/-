using UnityEngine;

/// <summary>
/// 全局日志开关（静态工具类）
/// Info/Warn/Error：普通日志，受 enabled 总开关控制
/// Verbose：高频/隐私（消息全文等）日志，默认不输出；置 VerboseEnabled=true 后在摘要之外再打全文
/// 用法示例：
///   GameLog.Info("[模块] 普通日志");
///   GameLog.Info("[聊天] 收到 1001 的消息(长度 23)");            // 摘要默认放行
///   if (GameLog.VerboseEnabled) GameLog.Info("[聊天] 全文：" + content); // 全文仅开关打开时
/// </summary>
public static class GameLog
{
    /// <summary>普通日志总开关（false 时 Info/Warn/Error 全部静默）</summary>
    public static bool enabled = true;

    /// <summary>全文/明细日志开关（默认关：涉隐私或高频的全文输出仅在此开关打开时打印）</summary>
    public static bool VerboseEnabled = false;

    public static void Info(string message)
    {
        if (enabled) Debug.Log(message);
    }

    public static void Warn(string message)
    {
        if (enabled) Debug.LogWarning(message);
    }

    public static void Error(string message)
    {
        if (enabled) Debug.LogError(message);
    }

    /// <summary>明细日志：仅 VerboseEnabled 打开时输出（全文/高频内容走这里）。</summary>
    public static void Verbose(string message)
    {
        if (enabled && VerboseEnabled) Debug.Log(message);
    }
}
