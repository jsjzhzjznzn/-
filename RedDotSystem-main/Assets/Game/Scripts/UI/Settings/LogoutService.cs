using Cysharp.Threading.Tasks;
using SkierFramework;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 退出登录：断线 → 清本地自动登录令牌 → 释放框架 UI → 回 LOGIN 场景。
/// 各数据管理器（背包/邮件/任务/聊天）的缓存不在这里清，它们各自在登录成功回执里
/// 做「换账号清缓存」（既有逻辑，重登即清），这里再清一遍反而容易漏掉新增管理器。
/// </summary>
public static class LogoutService
{
    public static bool IsLoggingOut { get; private set; }

    public static void Logout()
    {
        if (IsLoggingOut) return;
        IsLoggingOut = true;
        RunAsync().Forget();
    }

    private static async UniTaskVoid RunAsync()
    {
        // 等当前按钮回调退出栈再销毁面板自身，避免"在控件回调里销毁控件"
        await UniTask.Yield(PlayerLoopTiming.Update);

        if (UIManager.Instance != null)
        {
            // 含常驻主页：ReleaseAll 会跳过常驻视图，退出登录时主页会浮在登录场景上
            await UIManager.Instance.ReleaseAllIncludingResidentAsync();
        }

        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net != null)
        {
            net.Disconnect();
        }

        // 令牌必须清掉，否则回登录页会立刻被 TryAutoLogin 用旧令牌登回去
        LoginSceneController.ClearStoredLogin();

        Debug.Log("[Logout] 已退出登录，返回登录场景");
        IsLoggingOut = false;
        await SceneLoader.LoadAsync(ClientNetworkManager.LoginSceneName);
    }
}
