using Common.Network;
using Cysharp.Threading.Tasks;
using SkierFramework;
using UnityEngine;

/// <summary>
/// 主场景启动入口：**登录成功后**才打开常驻的 HomeView（入口栏）。
/// 挂在 zhuye 场景的 GameEntry 空物体上即可，不参与任何面板布局。
/// 从 LOGIN 场景进来时（已登录）直接开；在 zhuye 里等登录响应再开。
/// </summary>
public class HomeViewLauncher : MonoBehaviour
{
    /// <summary>
    /// 主页 UI 是否已经就绪（HomeView 打开并登记为常驻）。
    /// 聊天消息预热等"登录后台任务"以此为准，避免在登录转场/界面构建期间抢请求队列。
    /// </summary>
    public static bool IsReady { get; private set; }

    /// <summary>主页 UI 就绪回调（每次进入主页触发一次；订阅方需自行处理重复进入/换场景）</summary>
    public static event System.Action HomeReady;

    /// <summary>进入 Play 时复位静态状态（关闭 Domain Reload 时静态字段会残留）</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        IsReady = false;
        HomeReady = null;
    }

    private bool opened;

    private void Start()
    {
        // 主页是纯 UI 场景：进入即放开光标。光标状态跨场景保留，对话/战斗场景把它锁上后
        // 直接切回主页会整页点不动（鼠标钉在屏幕中心）。
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net == null)
        {
            Debug.LogWarning("[HomeViewLauncher] ClientNetworkManager 不存在，主界面不会自动打开");
            return;
        }

        // 已登录（从登录场景/战斗场景回来）：直接开
        if (net.MyPlayerId > 0)
        {
            OpenHome().Forget();
            return;
        }

        net.LoginResponseReceived += OnLoginResponse;
    }

    private void OnDestroy()
    {
        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net != null) net.LoginResponseReceived -= OnLoginResponse;
    }

    private void OnLoginResponse(LoginResponse response)
    {
        if (!response.Success) return;

        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net != null) net.LoginResponseReceived -= OnLoginResponse;

        OpenHome().Forget();
    }

    private async UniTask OpenHome()
    {
        if (opened) return;
        opened = true;

        await UIManager.Instance.Open(UIType.HomeView);
        UIManager.Instance.AddResidentUI(UIType.HomeView);
        PreloadEntryViews();

        // 主页已可交互：通知后台任务（聊天预热）可以开始，避免占据登录高峰的请求队列
        IsReady = true;
        HomeReady?.Invoke();
    }

    /// <summary>
    /// 入口栏面板预热名单（C# 视图）：登录后异步把 prefab 加载/实例化好，
    /// 玩家第一次点入口按钮时不必再等资源加载（实测冷启动首点曾有 ~0.5s 尖峰，热开只要 5~40ms）。
    /// 只预热"点了必开"的常驻入口；活动视图只预热外壳，真正的内容/Lua 仍由热更层现场构建
    /// （联机面板不在这里预加载，有自己的初始化时机）。
    /// </summary>
    private static readonly string[] EntryViewIds =
    {
        "FriendView", "MailView", "QuestView", "BagView", "LeaderboardView", "WorldChatView",
        "AgentView", "SettingsView", "BattleLobbyView", "ActivityView",
    };

    private static void PreloadEntryViews()
    {
        foreach (string uiId in EntryViewIds)
        {
            UIManager.Instance.Preload(uiId);
        }
    }
}
