using System;
using System.Collections;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using SkierFramework;
using UnityEngine;
using XLua;
using Common.Network;

/// <summary>
/// 活动固定壳（单例 DDOL）：
///   1. 订阅活动/唤取协议，缓存当前活动状态（剩余时间按服务器时钟算）；
///   2. 入口按钮 → 拉活动列表 → 交给 LuaActivityHost 启动热更层 Lua；
///   3. 唤取/流水的异步回执转交 Lua 回调（一次只允许一个在途请求）；
///   4. 活动切换推送 → 打开中则自动换新内容（LuaEnv 重建）。
/// 与其它系统（背包/任务/邮件）的区别：本壳不含任何活动"内容"——
/// 标题、文案、UI、规则全部来自热更包的 Lua 与资源，壳只提供稳定边界。
/// </summary>
public class ActivityShell : MonoBehaviour
{
    public static ActivityShell Instance { get; private set; }

    /// <summary>
    /// 运行时自举：早于场景加载创建系统物体（与 BagManager 等同一模式）
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance != null) return;
        GameObject holder = new GameObject("ActivitySystem");
        holder.AddComponent<ActivityShell>();
        holder.AddComponent<LuaActivityHost>();
        holder.AddComponent<ActivityLoadingTip>();   // 点入口后的可见反馈（热更层起来之前）
    }

    // ──────────────────────────────
    // 当前活动状态（来自最近一次 ActivityListResponse / ActivityChangeNotify）
    // ──────────────────────────────

    /// <summary>活动ID（0 = 无进行中活动）</summary>
    public int CurrentActivityId { get; private set; }
    public int CurrentBannerId { get; private set; }
    public string CurrentTitle { get; private set; } = string.Empty;
    public string CurrentSubtitle { get; private set; } = string.Empty;
    public string CurrentContentVersion { get; private set; } = string.Empty;
    public long CurrentStartAtMs { get; private set; }
    public long CurrentEndAtMs { get; private set; }
    public long ServerNowMs { get; private set; }
    public bool IsPanelOpen { get; private set; }

    /// <summary>
    /// 当前时间窗内的全部活动（活动中心侧栏数据源：唤取/签到等并存，按服务端 sort,activity_id 排序）。
    /// 上面那组 Current* 单活动属性是"列表第一个"的兼容视图，热更层新代码请用本列表。
    /// </summary>
    public IReadOnlyList<ActivityInfo> CurrentActivities { get; private set; } = new List<ActivityInfo>();

    /// <summary>是否待开面板（拉到列表即开）</summary>
    private bool pendingOpen;

    /// <summary>承载面板的框架视图（由 ActivityView 打开时登记；为空 = 走独立画布兜底）</summary>
    private ActivityView attachedView;

    /// <summary>打开会话号：每次开/关自增，异步流程发现号变了就中止（防"关掉之后内容才加载完"）</summary>
    private int openSession;

    /// <summary>打开流程是否在途（防连点重入：以前连点会并发跑多轮「拉列表→建包→起 Lua」）</summary>
    private bool opening;
    private float openingStartedAt;

    /// <summary>在途唤取的 Lua 回调（同一时刻最多一个）</summary>
    private LuaFunction pendingDrawCallback;
    private int pendingDrawBannerId;

    /// <summary>在途唤取的幂等键（重试时复用；服务端命中即重放，不重复扣费）</summary>
    private string pendingDrawRequestId = string.Empty;
    private float pendingDrawStartedAt;

    /// <summary>最近一笔唤取的幂等键与参数：传输层失败后可用同一 request_id 安全重试</summary>
    private string lastDrawRequestId = string.Empty;
    private int lastDrawBannerId;
    private int lastDrawCount;
    private bool lastDrawRetryable;

    /// <summary>唤取回执超时（秒）；超时不代表失败，客户端保留幂等键让玩家手动重试</summary>
    private const float DrawTimeoutSeconds = 12f;

    /// <summary>在途流水的 Lua 回调</summary>
    private LuaFunction pendingHistoryCallback;
    private int pendingHistoryBannerId;

    /// <summary>在途签到状态查询的 Lua 回调</summary>
    private LuaFunction pendingSignInListCallback;
    private int pendingSignInListId;

    /// <summary>在途签到的 Lua 回调</summary>
    private LuaFunction pendingSignInClaimCallback;
    private int pendingSignInClaimId;

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
    /// 订阅协议回执。BeforeSceneLoad 自举顺序不定：本壳可能先于 ClientNetworkManager 创建
    /// （实测 OnEnable 里 Instance 为 null 抛 NRE、订阅全灭、活动永远打不开）——
    /// 所以失败后在 Update 里逐帧重试，直到网络管理器就位为止。
    /// </summary>
    private void TrySubscribe()
    {
        if (subscribed) return;
        ClientNetworkManager network = ClientNetworkManager.Instance;
        if (network == null) return;

        network.ActivityListResponseReceived += OnActivityListResponse;
        network.ActivityChangeNotifyReceived += OnActivityChangeNotify;
        network.GachaDrawResponseReceived += OnGachaDrawResponse;
        network.GachaHistoryResponseReceived += OnGachaHistoryResponse;
        network.SignInListResponseReceived += OnSignInListResponse;
        network.SignInClaimResponseReceived += OnSignInClaimResponse;
        network.LoginResponseReceived += OnLoginResponse;
        network.ConnectionBroken += OnConnectionBroken;
        subscribed = true;
    }

    private bool subscribed;

    /// <summary>销毁时对称退订（与 TrySubscribe 的 5 个事件一一对应），并复位订阅标志</summary>
    private void OnDestroy()
    {
        ClientNetworkManager network = ClientNetworkManager.Instance;
        if (network != null)
        {
            network.ActivityListResponseReceived -= OnActivityListResponse;
            network.ActivityChangeNotifyReceived -= OnActivityChangeNotify;
            network.GachaDrawResponseReceived -= OnGachaDrawResponse;
            network.GachaHistoryResponseReceived -= OnGachaHistoryResponse;
            network.SignInListResponseReceived -= OnSignInListResponse;
            network.SignInClaimResponseReceived -= OnSignInClaimResponse;
            network.LoginResponseReceived -= OnLoginResponse;
            network.ConnectionBroken -= OnConnectionBroken;
        }
        subscribed = false;
    }

    private void Update()
    {
        if (!subscribed) TrySubscribe();
        TickDrawTimeout();
    }

    /// <summary>
    /// 唤取回执超时：保留幂等键与参数，交付一个 retryable 的失败结果，
    /// 让 Lua 解除 busy 并把下一次点击路由到 RetryDraw（服务端若已结算则重放原结果）。
    /// </summary>
    private void TickDrawTimeout()
    {
        if (pendingDrawCallback == null) return;
        if (Time.realtimeSinceStartup - pendingDrawStartedAt < DrawTimeoutSeconds) return;
        FailPendingDraw("唤取请求超时，可安全重试（不会重复扣费）", retryable: true);
    }

    /// <summary>连接断开：在途回执永远不会到达，交付 retryable 失败并保留幂等键。</summary>
    private void OnConnectionBroken()
    {
        if (pendingDrawCallback != null)
            FailPendingDraw("连接已断开，恢复网络后可重试（不会重复扣费）", retryable: true);
    }

    // ──────────────────────────────
    // 入口 / 生命周期
    // ──────────────────────────────

    /// <summary>
    /// 由 ActivityView.OnOpen 调用：登记框架落点（NormalLayer 画布内的 contentRoot）并开始打开流程。
    /// 登记后热更内容不再需要自建画布，层级完全由 UI 框架决定。
    /// </summary>
    public void AttachView(ActivityView view)
    {
        attachedView = view;
        LuaActivityHost.Instance?.SetContentRoot(view != null ? view.contentRoot : null);
    }

    /// <summary>由 ActivityView.OnClose 调用：框架关掉视图 → 拆热更内容（下一帧才释放 LuaEnv）</summary>
    public void OnViewClosed(ActivityView view)
    {
        if (attachedView != view) return;
        attachedView = null;
        ClosePanel();
    }

    /// <summary>
    /// 关闭活动面板的统一入口（Lua 的 × 按钮、登录切号等）：视图在就交给 UIManager 关，
    /// 视图不在（联调/E2E 直调）就直接拆内容。
    /// </summary>
    public void CloseActivityUI()
    {
        if (attachedView != null && UIManager.Instance != null)
        {
            UIManager.Instance.Close(UIType.ActivityView).Forget();
            return;
        }
        ClosePanel();
    }

    /// <summary>
    /// 打开活动（入口按钮与 E2E 直调共用）：先拉列表，回执到达再启动热更层。
    ///
    /// 这里做了三层"一次点击就有交代"的处理（此前实测：未登录/在建包时点入口毫无反馈，
    /// 用户会以为按钮失灵而反复点，看起来像"要多点几次才开"）：
    ///   ① 未登录 → 立刻弹可见提示，而不是只写一条控制台警告；
    ///   ② 已在途 → 忽略重复点击并复用当前流程（不再并发开多轮）；
    ///   ③ 正常 → 立刻显示"活动加载中…"，直到面板起来或失败为止。
    /// </summary>
    public void OpenActivity()
    {
        ClientNetworkManager network = ClientNetworkManager.Instance;
        if (network == null || network.MyPlayerId <= 0)
        {
            Debug.LogWarning("[活动] 请先登录后再打开活动");
            ActivityLoadingTip.Show("请先登录后再打开活动", 2f);
            return;
        }

        if (IsPanelOpen)
        {
            Debug.Log("[活动] 面板已打开，忽略本次入口点击");
            return;
        }

        if (opening)
        {
            Debug.Log($"[活动] 正在打开中（已 {Time.realtimeSinceStartup - openingStartedAt:F1}s），忽略重复点击");
            return;
        }

        opening = true;
        openingStartedAt = Time.realtimeSinceStartup;
        openSession++;
        ActivityLoadingTip.Show("活动加载中…");
        Debug.Log($"[活动] 点击入口，开始拉活动列表（t={openingStartedAt:F2}）");

        pendingOpen = true;
        _ = network.SendActivityListRequest();
    }

    /// <summary>关闭活动面板：拆热更层 UI + 释放 Lua 虚拟机（下次打开重建，内容永远最新）</summary>
    public void ClosePanel()
    {
        openSession++;                         // 作废在途的"拉列表/建包/起 Lua"流程
        pendingOpen = false;
        opening = false;                       // 关掉时清掉在途标记，避免下次点击被误判为"正在打开中"
        ActivityLoadingTip.Hide();
        ReleasePendingCallbacks();
        IsPanelOpen = false;
        LuaActivityHost.Instance?.TearDown();
        Debug.Log("[活动] 面板已关闭（UI 已拆；LuaEnv 待 Lua 调用栈退出后的下一帧释放）");
    }

    /// <summary>登录成功：清上一账号的活动缓存（防串号）+ 后台预热资源包（让首次点入口几乎零等待）</summary>
    private void OnLoginResponse(LoginResponse response)
    {
        if (!response.Success) return;
        if (IsPanelOpen) CloseActivityUI();
        CurrentActivityId = 0;
        CurrentBannerId = 0;
        CurrentTitle = string.Empty;
        CurrentSubtitle = string.Empty;
        CurrentContentVersion = string.Empty;
        ServerNowMs = 0;
        CurrentActivities = new List<ActivityInfo>();

        LuaActivityHost.Instance?.PrewarmPackage();
    }

    // ──────────────────────────────
    // 协议回执
    // ──────────────────────────────

    private void OnActivityListResponse(ActivityListResponse response)
    {
        ApplyActivityState(response.Activities, response.ServerNowMs);

        if (!pendingOpen) return;             // 非本次开面板触发的列表（如热更层主动刷新）
        pendingOpen = false;
        Debug.Log($"[活动] 活动列表到达（banner={CurrentBannerId} [{CurrentTitle}]，共 {CurrentActivities.Count} 个活动），耗时 {Time.realtimeSinceStartup - openingStartedAt:F2}s");

        if (CurrentActivities.Count == 0)
        {
            opening = false;
            Debug.Log("[活动] 当前没有进行中的活动");
            ActivityLoadingTip.Show("当前没有进行中的活动", 2.5f);
            return;
        }

        StartCoroutine(OpenPanelRoutine(openSession));
    }

    private void OnActivityChangeNotify(ActivityChangeNotify notify)
    {
        ApplyActivityState(notify.Activities, notify.ServerNowMs);
        Debug.Log($"[活动] 收到切换推送 → #{CurrentActivityId} [{CurrentTitle}]（内容版本 {CurrentContentVersion}）");

        // 面板打开中：自动换到新活动内容（热更层整套重建 = 本次功能的核心演示点）
        if (IsPanelOpen)
        {
            ReleasePendingCallbacks();
            LuaActivityHost.Instance?.TearDown();
            StartCoroutine(OpenPanelRoutine(openSession));
        }
    }

    private void ApplyActivityState(IReadOnlyList<ActivityInfo> activities, long serverNowMs)
    {
        ServerNowMs = serverNowMs;

        // 完整列表缓存（活动中心侧栏的数据源）：空表也如实记录（服务端排序 = 侧栏顺序）
        var list = new List<ActivityInfo>();
        if (activities != null) list.AddRange(activities);

        // 本地常驻活动（方舟卡牌）：不依赖服务端配置，进活动中心即可见；
        // 追加在末尾，不影响"第一个活动"的兼容语义，也不参与服务端任何计算。
        if (!list.Exists(IsArkCardsActivity)) list.Add(CreateArkCardsActivity());

        CurrentActivities = list;

        // Current* 单活动属性保持"列表第一个"的兼容语义（旧热更层与 GetBannerId 等读它）
        ActivityInfo first = list.Count > 0 ? list[0] : null;
        CurrentActivityId = first?.ActivityId ?? 0;
        CurrentBannerId = first?.BannerId ?? 0;
        CurrentTitle = first?.Title ?? string.Empty;
        CurrentSubtitle = first?.Subtitle ?? string.Empty;
        CurrentContentVersion = first?.ContentVersion ?? string.Empty;
        CurrentStartAtMs = first?.StartAtMs ?? 0;
        CurrentEndAtMs = first?.EndAtMs ?? 0;
    }

    /// <summary>本地常驻活动 id（方舟卡牌），与 ActivityBridge 的 GetActivityInfoAt 一致可见</summary>
    private const int LocalArkCardsActivityId = 9001;

    private static bool IsArkCardsActivity(ActivityInfo info)
    {
        return info != null && info.ActivityId == LocalArkCardsActivityId;
    }

    private static ActivityInfo CreateArkCardsActivity()
    {
        return new ActivityInfo
        {
            ActivityId = LocalArkCardsActivityId,
            Type = 4,
            BannerId = 0,
            ContentKey = "arkcards",
            ContentVersion = "内置",
            Title = "方舟卡牌",
            Subtitle = "明日方舟主题回合制卡牌对战",
        };
    }

    /// <summary>
    /// 开面板协程：确保资源包最新（联机模式=下载增量）→ 重建 LuaEnv → require 活动入口 → 交付上下文。
    /// 每段都打耗时（出问题一眼看出慢在哪一段）；失败给可见提示而不是静默 yield break。
    /// </summary>
    private IEnumerator OpenPanelRoutine(int session)
    {
        LuaActivityHost host = LuaActivityHost.Instance;
        if (host == null)
        {
            opening = false;
            ActivityLoadingTip.Show("活动宿主未就绪", 2.5f);
            yield break;
        }

        float started = Time.realtimeSinceStartup;
        bool ready = false;
        yield return host.EnsurePackageReadyRoutine(value => ready = value);
        float packageSeconds = Time.realtimeSinceStartup - started;

        // 期间被关掉/换了会话（关面板、登录切号、活动轮换）→ 本次流程作废，别再往已关的视图里塞内容
        if (session != openSession)
        {
            opening = false;
            yield break;
        }

        if (!ready)
        {
            opening = false;
            Debug.LogWarning($"[活动] 活动资源包未就绪，打开失败（资源包阶段耗时 {packageSeconds:F2}s，详见上方日志）");
            ActivityLoadingTip.Show("活动资源获取失败，请稍后重试", 3f);
            yield break;
        }
        Debug.Log($"[活动] 资源包就绪，耗时 {packageSeconds:F2}s");

        host.PrepareLuaEnvironment();
        yield return null;

        if (session != openSession)
        {
            opening = false;
            yield break;
        }

        bool launched = host.LaunchActivityModule();
        IsPanelOpen = launched;
        opening = false;

        if (launched)
        {
            ActivityLoadingTip.Hide();
            Debug.Log($"[活动] 面板已打开，总耗时 {Time.realtimeSinceStartup - openingStartedAt:F2}s（其中资源包 {packageSeconds:F2}s）");
        }
        else
        {
            Debug.LogError("[活动] 热更层启动失败（见上方日志）");
            ActivityLoadingTip.Show("活动内容启动失败，请稍后重试", 3f);
        }
    }

    // ──────────────────────────────
    // 唤取 / 流水（单在途 + Lua 回调交付）
    // ──────────────────────────────

    /// <summary>是否有待重试的唤取（传输层失败后由 Lua 决定何时重试）</summary>
    public bool CanRetryDraw => lastDrawRetryable && lastDrawRequestId.Length > 0;

    /// <summary>受理一次唤取请求；同一时刻只允许一个在途（服务端会话消息本就串行）</summary>
    public bool RequestDraw(int bannerId, int count, LuaFunction callback)
    {
        if (pendingDrawCallback != null) return false;   // 上一个还没回来
        if (!TryGetNetwork(out ClientNetworkManager network, callback)) return true;

        // 每次新抽生成一个幂等键：超时/断线重试必须复用它，服务端才不会重复扣费
        string requestId = Guid.NewGuid().ToString("N");
        return StartDraw(network, bannerId, count, requestId, callback);
    }

    /// <summary>
    /// 重试上一笔唤取（复用上一次的 request_id）。
    /// 服务端若已结算 → 直接重放原结果、不再扣媒介；若从未收到 → 正常结算一次。
    /// </summary>
    public bool RequestDrawRetry(LuaFunction callback)
    {
        if (pendingDrawCallback != null) return false;
        if (!CanRetryDraw) return false;
        if (!TryGetNetwork(out ClientNetworkManager network, callback)) return true;

        Debug.Log($"[活动] 重试唤取：卡池{lastDrawBannerId} ×{lastDrawCount} 请求{lastDrawRequestId}");
        return StartDraw(network, lastDrawBannerId, lastDrawCount, lastDrawRequestId, callback);
    }

    private bool TryGetNetwork(out ClientNetworkManager network, LuaFunction callback)
    {
        network = ClientNetworkManager.Instance;
        if (network != null && network.MyPlayerId > 0) return true;
        callback?.Call(BuildDrawError("请先登录"));
        return false;
    }

    private bool StartDraw(ClientNetworkManager network, int bannerId, int count, string requestId, LuaFunction callback)
    {
        pendingDrawBannerId = bannerId;
        pendingDrawRequestId = requestId;
        pendingDrawStartedAt = Time.realtimeSinceStartup;
        pendingDrawCallback = callback;

        // 立刻记下"可重试"目标：请求发出后不管有意外的断线还是超时，都靠它找回原结果
        lastDrawRequestId = requestId;
        lastDrawBannerId = bannerId;
        lastDrawCount = count;
        lastDrawRetryable = false;

        _ = network.SendGachaDrawRequest(bannerId, count, requestId);
        return true;
    }

    private void OnGachaDrawResponse(GachaDrawResponse response)
    {
        // 背包快照由 BagManager 自己的订阅应用（含配置合并与 Capacity 补齐），这里只管 Lua 侧交付
        LuaFunction callback = pendingDrawCallback;
        pendingDrawCallback = null;
        pendingDrawRequestId = string.Empty;
        lastDrawRetryable = false;   // 收到回执（无论成败）就说明这笔已有结论，不再重试
        if (callback == null) return;

        GachaDrawResultData result = new GachaDrawResultData
        {
            success = response.Success,
            message = response.Message,
            pityCount = response.Pity5Count,
            pityThreshold = response.Pity5Threshold,
            totalDraws = response.TotalDraws,
            guaranteedUp = response.GuaranteedUp != 0,
            replayed = response.Replayed,
            count = response.Results.Count,
        };
        result.Fill(response.Results);
        DeliverDrawResult(callback, result);
    }

    /// <summary>把在途唤取转为 retryable 失败（超时/断线用），并保留幂等键供下一次重试。</summary>
    private void FailPendingDraw(string message, bool retryable)
    {
        LuaFunction callback = pendingDrawCallback;
        pendingDrawCallback = null;
        pendingDrawRequestId = string.Empty;
        if (callback == null) return;

        lastDrawRetryable = retryable && lastDrawRequestId.Length > 0;
        Debug.LogWarning($"[活动] {message}（卡池{lastDrawBannerId} ×{lastDrawCount} 请求{lastDrawRequestId}）");

        GachaDrawResultData result = BuildDrawError(message);
        result.retryable = lastDrawRetryable;
        DeliverDrawResult(callback, result);
    }

    private static void DeliverDrawResult(LuaFunction callback, GachaDrawResultData result)
    {
        // Lua 回调抛错绝不能炸穿网络分发循环（调用点在 ClientNetworkManager.Update），就地兜住
        try
        {
            callback.Call(result);
            callback.Dispose();
        }
        catch (Exception exception)
        {
            Debug.LogError($"[活动] 唤取回调 Lua 异常（已吞掉保护网络层）：{exception.Message}");
        }
    }

    /// <summary>受理一次流水查询</summary>
    public bool RequestHistory(int bannerId, LuaFunction callback)
    {
        if (pendingHistoryCallback != null) return false;
        ClientNetworkManager network = ClientNetworkManager.Instance;
        if (network == null || network.MyPlayerId <= 0)
        {
            callback?.Call(BuildHistoryError("请先登录"));
            return true;
        }

        pendingHistoryBannerId = bannerId;
        pendingHistoryCallback = callback;
        _ = network.SendGachaHistoryRequest(bannerId);
        return true;
    }

    private void OnGachaHistoryResponse(GachaHistoryResponse response)
    {
        LuaFunction callback = pendingHistoryCallback;
        pendingHistoryCallback = null;
        if (callback == null) return;

        GachaHistoryResultData result = new GachaHistoryResultData
        {
            success = response.Success,
            message = response.Message,
            totalDraws = response.TotalDraws,
            pityCount = response.Pity5Count,
            guaranteedUp = response.GuaranteedUp,
            count = response.Entries.Count,
        };
        result.Fill(response.Entries);
        try
        {
            callback.Call(result);
            callback.Dispose();
        }
        catch (Exception exception)
        {
            Debug.LogError($"[活动] 流水回调 Lua 异常（已吞掉保护网络层）：{exception.Message}");
        }
    }

    // ──────────────────────────────
    // 签到（单在途 + Lua 回调交付，与唤取/流水同模式）
    // ──────────────────────────────

    /// <summary>受理一次签到状态查询；同一时刻只允许一个在途</summary>
    public bool RequestSignInList(int signId, LuaFunction callback)
    {
        if (pendingSignInListCallback != null) return false;
        ClientNetworkManager network = ClientNetworkManager.Instance;
        if (network == null || network.MyPlayerId <= 0)
        {
            callback?.Call(BuildSignInError("请先登录"));
            return true;
        }

        pendingSignInListId = signId;
        pendingSignInListCallback = callback;
        _ = network.SendSignInListRequest(signId);
        return true;
    }

    private void OnSignInListResponse(SignInListResponse response)
    {
        LuaFunction callback = pendingSignInListCallback;
        pendingSignInListCallback = null;
        if (callback == null) return;

        SignInResultData result = new SignInResultData
        {
            success = response.Success,
            message = response.Message,
            signId = response.SignId,
            totalDays = response.TotalDays,
            signedDays = response.SignedDays,
            todaySigned = response.TodaySigned,
        };
        result.Fill(response.Days);
        try
        {
            callback.Call(result);
            callback.Dispose();
        }
        catch (Exception exception)
        {
            Debug.LogError($"[活动] 签到状态回调 Lua 异常（已吞掉保护网络层）：{exception.Message}");
        }
    }

    /// <summary>受理一次签到；同一时刻只允许一个在途</summary>
    public bool RequestSignInClaim(int signId, LuaFunction callback)
    {
        if (pendingSignInClaimCallback != null) return false;
        ClientNetworkManager network = ClientNetworkManager.Instance;
        if (network == null || network.MyPlayerId <= 0)
        {
            callback?.Call(BuildSignInError("请先登录"));
            return true;
        }

        pendingSignInClaimId = signId;
        pendingSignInClaimCallback = callback;
        _ = network.SendSignInClaimRequest(signId);
        return true;
    }

    private void OnSignInClaimResponse(SignInClaimResponse response)
    {
        // 背包快照由 BagManager 自己的订阅应用（含配置合并与 Capacity 补齐），这里只管 Lua 侧交付
        LuaFunction callback = pendingSignInClaimCallback;
        pendingSignInClaimCallback = null;
        if (callback == null) return;

        SignInResultData result = new SignInResultData
        {
            success = response.Success,
            message = response.Message,
            signId = response.SignId,
            day = response.Day,
            totalDays = response.TotalDays,
            signedDays = response.SignedDays,
            todaySigned = response.TodaySigned,
        };
        result.Fill(response.Days);
        try
        {
            callback.Call(result);
            callback.Dispose();
        }
        catch (Exception exception)
        {
            Debug.LogError($"[活动] 签到回调 Lua 异常（已吞掉保护网络层）：{exception.Message}");
        }
    }

    /// <summary>拆面板前释放所有在途 Lua 回调引用（防 DisposeEnv 被 C# 回调残留拒绝）</summary>
    private void ReleasePendingCallbacks()
    {
        pendingDrawCallback?.Dispose();
        pendingDrawCallback = null;
        pendingDrawRequestId = string.Empty;
        // 面板关闭后重开是一轮新交互：不要让上一轮的幂等键变成一次"意外重试"
        lastDrawRetryable = false;

        pendingHistoryCallback?.Dispose();
        pendingHistoryCallback = null;
        pendingSignInListCallback?.Dispose();
        pendingSignInListCallback = null;
        pendingSignInClaimCallback?.Dispose();
        pendingSignInClaimCallback = null;
    }

    private static GachaDrawResultData BuildDrawError(string message)
    {
        return new GachaDrawResultData { success = false, message = message, count = 0 };
    }

    private static GachaHistoryResultData BuildHistoryError(string message)
    {
        return new GachaHistoryResultData { success = false, message = message, count = 0 };
    }

    private static SignInResultData BuildSignInError(string message)
    {
        return new SignInResultData { success = false, message = message };
    }
}
