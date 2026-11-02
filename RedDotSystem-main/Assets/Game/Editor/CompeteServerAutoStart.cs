using System.IO;
using System.Net;
using System.Net.NetworkInformation;

using UnityEditor;

using UnityEngine;

/// <summary>
/// 服务端自动启动器（仅编辑器生效，打包时不包含）
/// 进入 Play 模式时检测 8888 端口，若服务端未运行则自动通过 run_server.bat 拉起
/// 退出 Play 不杀进程，保持服务端常驻（便于双客户端调试）；停止服务端请关闭弹出的窗口
/// </summary>
[InitializeOnLoad]
public static class CompeteServerAutoStart
{
    /// <summary>服务端监听端口（与服务端 SocketServer.Start(8888) 保持一致）</summary>
    private const int ServerPort = 8888;

    /// <summary>run_server.bat 的绝对路径（Unity工程上级目录中的 competeserves）</summary>
    private static readonly string RunServerBat = Path.GetFullPath(Path.Combine(
        Directory.GetParent(Application.dataPath).FullName,
        "..", "competeserves", "run_server.bat"));

    /// <summary>
    /// 静态构造函数 - 注册 Play 模式状态回调
    /// </summary>
    static CompeteServerAutoStart()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    /// <summary>
    /// Play 模式状态变化处理
    /// 在即将进入 Play 模式时检查并拉起服务端
    /// </summary>
    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.ExitingEditMode) return;
        if (IsServerPortListening()) return;

        if (!File.Exists(RunServerBat))
        {
            Debug.LogWarning($"[Compete] 未找到服务端启动脚本: {RunServerBat}，请手动启动服务端");
            return;
        }

        try
        {
            // 弹出独立的 cmd 窗口运行：显示服务端日志，关闭窗口即停止服务端
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = RunServerBat,
                UseShellExecute = true,
            });
            Debug.Log("[Compete] 检测到服务端未运行，已自动启动（dotnet run 首次编译约需数秒，稍候再点登录）");
        }
        catch (System.Exception exception)
        {
            Debug.LogWarning($"[Compete] 自动启动服务端失败 [{exception.GetType().Name}]: {exception.Message}");
        }
    }

    /// <summary>
    /// 检测本机 8888 端口是否已有监听的方法
    /// 直接查询系统 TCP 监听表，不产生真实连接（避免服务端刷"客户端连接/离线"日志）
    /// </summary>
    private static bool IsServerPortListening()
    {
        foreach (IPEndPoint listener in IPGlobalProperties.GetIPGlobalProperties()
                     .GetActiveTcpListeners())
        {
            if (listener.Port == ServerPort) return true;
        }
        return false;
    }
}
