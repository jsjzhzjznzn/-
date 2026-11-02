using Common.Network;

using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 顶号观察者：收到 KickoutNotify 后把玩家送回登录场景并给出可见提示
/// 必须跨场景常驻——被顶号时玩家已在主场景 zhuye，LoginSceneController 随场景销毁、来不及收事件；
/// 断开连接与清凭据由 ClientNetworkManager 自己在收包分支内完成（那些字段都是私有的），本类只负责 UX
/// </summary>
public class KickoutWatcher : MonoBehaviour
{
    /// <summary>全局唯一实例</summary>
    public static KickoutWatcher Instance { get; private set; }

    /// <summary>提示停留时长（秒）</summary>
    private const int TipSeconds = 3;

    /// <summary>是否已成功订阅（网络管理器自举顺序不定，未就绪时逐帧重试）</summary>
    private bool subscribed;

    /// <summary>
    /// 运行时自举：早于场景加载创建系统物体（与 ActivityShell / BagManager 同一模式）
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance != null) return;
        GameObject holder = new GameObject("KickoutSystem");
        holder.AddComponent<KickoutWatcher>();
    }

    private void Awake()
    {
        if (Instance != null)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnEnable()
    {
        TrySubscribe();
    }

    /// <summary>
    /// 订阅被顶号事件。ClientNetworkManager 与本类都在 BeforeSceneLoad 自举，顺序不定：
    /// 本壳可能先创建，此时 Instance 为 null、订阅全灭（ActivityShell 里踩过，改为逐帧重试直到成功）
    /// </summary>
    private void TrySubscribe()
    {
        if (subscribed) return;
        ClientNetworkManager network = ClientNetworkManager.Instance;
        if (network == null) return;
        network.KickoutNotifyReceived += OnKickout;
        subscribed = true;
    }

    private void Update()
    {
        if (!subscribed) TrySubscribe();
    }

    private void OnDestroy()
    {
        if (subscribed && ClientNetworkManager.Instance != null)
        {
            ClientNetworkManager.Instance.KickoutNotifyReceived -= OnKickout;
        }
        if (Instance == this) Instance = null;
    }

    /// <summary>
    /// 被顶号处理：回登录场景 + 屏幕提示
    /// 面板层的 timeScale 不用管：背包/邮件等面板的 PausePanel 在场景卸载时 OnDisable 会 Pop，暂停栈自愈
    /// </summary>
    private void OnKickout(KickoutNotify notify)
    {
        string reason = string.IsNullOrEmpty(notify.Reason) ? "账号在其它设备登录" : notify.Reason;

        // 提示必须在新场景里弹：ActivityLoadingTip 不是 DDOL，加载途中创建会随旧场景卸载一起消失
        System.Action showTip = () => ActivityLoadingTip.Show($"{reason}，请重新登录", TipSeconds);

        if (SceneManager.GetActiveScene().name != ClientNetworkManager.LoginSceneName)
        {
            SceneLoader.LoadAsync(ClientNetworkManager.LoginSceneName, showTip).Forget();
            return;
        }

        showTip();
    }
}
