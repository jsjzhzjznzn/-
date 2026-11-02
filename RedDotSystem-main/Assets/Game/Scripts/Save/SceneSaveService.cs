using System;
using System.Threading.Tasks;
using Common.Network;
using UnityEngine;

/// <summary>
/// 场景存档服务（全局单例，跨场景存活）
///
/// 职责：场景存档的唯一网络出口。
///   * 拉取：进入场景时拿回快照（带 2 秒超时，失败由调用方降级为场景默认）
///   * 上报：事件点覆盖式写入；同一时刻只有 1 个请求在途，期间的新快照只保留最新一份（合并写入）
///   * 断线：未连接时保留待发快照，登录/重连后补发一次
///
/// 说明：位置只在事件点上报（对话节点变更/选项/对话结束/打开设置/返回主页/退出/失焦），
/// 不做定时写库——这是与用户确认过的取舍（崩溃最多丢最后一段位移，对话节点不会丢）。
/// </summary>
public class SceneSaveService : MonoBehaviour
{
    /// <summary>单例（跨场景存活）</summary>
    public static SceneSaveService Instance { get; private set; }

    /// <summary>上报等待回执的最长时间（秒）；超时视为本次在途作废并补发最新</summary>
    private const float PushAckTimeoutSeconds = 3f;

    private ClientNetworkManager net;
    private TaskCompletionSource<SaveStatePullResponse> pullWaiter;
    private string pullSceneName;
    private SceneSaveSnapshot pendingSnapshot;   // 待发/补发的最新快照
    private bool pushInFlight;
    private int pushRetryBudget;                 // 回执超时后的自动重试预算（最多 1 次，防无限重发）

    /// <summary>最近一次成功响应里服务端写入的时间（排障用）</summary>
    public long LastServerUpdatedAtMs { get; private set; }

    /// <summary>当前是否具备存档条件（已连接且已登录）</summary>
    public bool IsAvailable
    {
        get
        {
            ClientNetworkManager manager = ClientNetworkManager.Instance;
            return manager != null && manager.IsConnected && manager.MyPlayerId > 0;
        }
    }

    /// <summary>
    /// 确保单例存在（场景侧控制器 Start 调用；重复调用无副作用）
    /// </summary>
    public static SceneSaveService EnsureExists()
    {
        if (Instance != null) return Instance;

        GameObject host = new("SceneSaveService");
        DontDestroyOnLoad(host);
        return host.AddComponent<SceneSaveService>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {
        BindNetwork();
    }

    private void Update()
    {
        // ClientNetworkManager 由网络层自举，创建时机可能晚于本服务，发现实例变化就重绑
        if (ClientNetworkManager.Instance != net) BindNetwork();
    }

    private void OnDestroy()
    {
        UnbindNetwork();
        if (Instance == this) Instance = null;
    }

    // ──────────────────────────────
    // 拉取
    // ──────────────────────────────

    /// <summary>
    /// 拉取某场景的存档
    /// </summary>
    /// <param name="sceneName">场景名（服务端白名单：dashijie）</param>
    /// <param name="timeoutSeconds">超时（秒），超时返回 null 由调用方走场景默认</param>
    /// <returns>存档快照；无档/未登录/超时/失败均为 null</returns>
    public async Task<SceneSaveSnapshot> PullAsync(string sceneName, float timeoutSeconds = 2f)
    {
        if (string.IsNullOrEmpty(sceneName)) return null;

        if (!IsAvailable)
        {
            Debug.LogWarning($"[SceneSave] 未连接或未登录，跳过存档拉取（场景={sceneName}）");
            return null;
        }

        pullSceneName = sceneName;
        pullWaiter = new TaskCompletionSource<SaveStatePullResponse>();

        try
        {
            await ClientNetworkManager.Instance.SendSaveStatePullRequest(sceneName);
        }
        catch (Exception exception)
        {
            pullWaiter = null;
            Debug.LogWarning($"[SceneSave] 存档拉取请求发送失败：{exception.Message}");
            return null;
        }

        Task winner = await Task.WhenAny(
            pullWaiter.Task,
            Task.Delay(TimeSpan.FromSeconds(Mathf.Max(0.1f, timeoutSeconds))));

        if (winner != pullWaiter.Task)
        {
            pullWaiter = null;
            Debug.LogWarning($"[SceneSave] 存档拉取超时 {timeoutSeconds:0.##}s（场景={sceneName}），按首次进入处理");
            return null;
        }

        SaveStatePullResponse response = pullWaiter.Task.Result;
        pullWaiter = null;
        pullSceneName = null;

        if (!response.Success)
        {
            Debug.LogWarning($"[SceneSave] 存档拉取失败：{response.Message}");
            return null;
        }

        if (!response.HasSave || response.Snapshot == null) return null;
        if (!string.Equals(response.Snapshot.SceneName, sceneName, StringComparison.Ordinal))
        {
            Debug.LogWarning($"[SceneSave] 存档场景不匹配（期望 {sceneName}，收到 {response.Snapshot.SceneName}），已忽略");
            return null;
        }

        return response.Snapshot;
    }

    // ──────────────────────────────
    // 上报
    // ──────────────────────────────

    /// <summary>
    /// 排队上报快照（事件点调用）
    /// 在途时只保留最新一份——场景位置/对话节点天然可合并，中间态没有保留价值
    /// </summary>
    /// <param name="snapshot">完整快照（不是增量）</param>
    public void QueuePush(SceneSaveSnapshot snapshot)
    {
        if (snapshot == null) return;

        pendingSnapshot = snapshot;
        if (pushInFlight) return;

        FlushPending();
    }

    /// <summary>
    /// 立即把在途/待发快照发出去（返回主页、退出游戏、断线重连前调用）
    /// </summary>
    /// <param name="timeoutSeconds">等待送达的最长时间</param>
    /// <returns>是否已确认送达（超时/未连接为 false，快照仍保留待补发）</returns>
    public async Task<bool> FlushAsync(float timeoutSeconds = 1f)
    {
        if (pendingSnapshot != null && !pushInFlight) FlushPending();

        float deadline = Time.realtimeSinceStartup + Mathf.Max(0.1f, timeoutSeconds);
        while (Time.realtimeSinceStartup < deadline)
        {
            if (!pushInFlight && pendingSnapshot == null) return true;
            if (!IsAvailable) return false;
            await Task.Yield();
        }

        return !pushInFlight && pendingSnapshot == null;
    }

    private void FlushPending()
    {
        if (pushInFlight || pendingSnapshot == null) return;

        if (!IsAvailable)
        {
            // 未连接：保留最新快照，等登录/重连后由 OnLoginResponse 补发
            return;
        }

        SceneSaveSnapshot snapshot = pendingSnapshot;
        pendingSnapshot = null;
        pushInFlight = true;
        _ = SendPushAsync(snapshot);
    }

    private async Task SendPushAsync(SceneSaveSnapshot snapshot)
    {
        try
        {
            await ClientNetworkManager.Instance.SendSaveStatePushRequest(snapshot);
        }
        catch (Exception exception)
        {
            pushInFlight = false;
            pendingSnapshot ??= snapshot;
            Debug.LogWarning($"[SceneSave] 存档上报发送失败，已保留待补发：{exception.Message}");
            return;
        }

        // 回执兜底：服务端没回（断线/超时）就作废本次在途并补发最新，避免永久卡在 in-flight；
        // 自动重试只做一次，之后保留待发快照等下一个事件点（防服务端异常时变成无限重发）
        await Task.Delay(TimeSpan.FromSeconds(PushAckTimeoutSeconds));
        if (!pushInFlight) return;

        pushInFlight = false;
        pendingSnapshot ??= snapshot;

        if (pushRetryBudget < 1)
        {
            pushRetryBudget++;
            Debug.LogWarning("[SceneSave] 存档回执超时，自动重试一次");
            FlushPending();
        }
        else
        {
            Debug.LogWarning("[SceneSave] 存档回执连续超时，已保留最新快照等下次事件点补发");
        }
    }

    // ──────────────────────────────
    // 网络事件
    // ──────────────────────────────

    private void BindNetwork()
    {
        UnbindNetwork();
        net = ClientNetworkManager.Instance;
        if (net == null) return;

        net.SaveStatePullResponseReceived += OnPullResponse;
        net.SaveStatePushResponseReceived += OnPushResponse;
        net.LoginResponseReceived += OnLoginResponse;
    }

    private void UnbindNetwork()
    {
        if (net == null) return;

        net.SaveStatePullResponseReceived -= OnPullResponse;
        net.SaveStatePushResponseReceived -= OnPushResponse;
        net.LoginResponseReceived -= OnLoginResponse;
        net = null;
    }

    private void OnPullResponse(SaveStatePullResponse response)
    {
        if (pullWaiter == null) return;
        // 场景不匹配时让本次等待继续（可能是上一次超时请求的迟到回执）
        if (response.HasSave && response.Snapshot != null && !string.IsNullOrEmpty(pullSceneName)
            && !string.Equals(response.Snapshot.SceneName, pullSceneName, StringComparison.Ordinal))
        {
            return;
        }

        pullWaiter.TrySetResult(response);
    }

    private void OnPushResponse(SaveStatePushResponse response)
    {
        pushInFlight = false;
        pushRetryBudget = 0;
        if (response.Success) LastServerUpdatedAtMs = response.ServerUpdatedAtMs;

        if (pendingSnapshot != null) FlushPending();
    }

    private void OnLoginResponse(Common.Network.LoginResponse response)
    {
        // 登录/重连成功：补发断线期间积压的最新快照
        if (pendingSnapshot != null) FlushPending();
    }
}
