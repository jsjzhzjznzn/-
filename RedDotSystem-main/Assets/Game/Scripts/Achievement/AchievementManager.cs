using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Common.Network;

using MH;

using UnityEngine;

/// <summary>
/// 成就数据层（客户端缓存 + 协议收发）
/// 服务端权威：配置、进度、解锁/领取状态全部来自服务端，客户端只缓存与分发。
/// 红点：可领取数写入 Key17；登录成功即拉列表，面板未开也亮。
/// </summary>
public class AchievementManager : MonoBehaviour
{
    // ──────────────────────────────
    // 单例
    // ──────────────────────────────

    /// <summary>全局唯一实例（UI 层访问成就数据的入口）</summary>
    public static AchievementManager Instance { get; private set; }

    /// <summary>运行时自举：进入 Play 自动创建，无需手动挂载。</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance != null) return;
        GameObject holder = new GameObject("AchievementManager");
        holder.AddComponent<AchievementManager>();
        DontDestroyOnLoad(holder);
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

    // ──────────────────────────────
    // 事件
    // ──────────────────────────────

    /// <summary>成就数据变化（列表到达 / 领取成功 / 服务端推送，UI 重渲染）</summary>
    public event System.Action AchievementsUpdated;

    /// <summary>提示文案（领取结果/失败原因，UI 状态栏显示）</summary>
    public event System.Action<string> StatusChanged;

    /// <summary>解锁推送（UI 弹横幅用）</summary>
    public event System.Action<AchievementUnlockedNotify> Unlocked;

    // ──────────────────────────────
    // 缓存
    // ──────────────────────────────

    private readonly Dictionary<int, AchievementCfgInfo> configs = new();
    private readonly List<AchievementCfgInfo> configOrder = new();
    private readonly Dictionary<int, PlayerAchievementInfo> states = new();
    private AchievementSummary summary = new();
    private readonly HashSet<int> claiming = new();

    /// <summary>是否已拿到过一次列表数据（UI 可据此显示空态/加载态）</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>是否正在请求列表</summary>
    public bool IsLoading { get; private set; }

    /// <summary>服务端汇总（总数/已解锁/可领取/成就点）</summary>
    public AchievementSummary Summary => summary;

    /// <summary>配置稳定顺序（服务端按系列/排序下发，客户端直接使用）</summary>
    public IReadOnlyList<AchievementCfgInfo> Configs => configOrder;

    /// <summary>可领取数量（红点数据源）</summary>
    public int ClaimableCount => summary?.Claimable ?? 0;

    // ──────────────────────────────
    // 生命周期
    // ──────────────────────────────

    private void Start()
    {
        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net == null) return;

        net.AchievementListResponseReceived += OnAchievementListResponse;
        net.AchievementClaimResponseReceived += OnAchievementClaimResponse;
        net.AchievementUnlockedNotifyReceived += OnAchievementUnlockedNotify;
        net.AchievementProgressNotifyReceived += OnAchievementProgressNotify;
        net.LoginResponseReceived += OnLoginResponse;
        net.ConnectionBroken += OnConnectionBroken;

        // 组件可能在登录回执之后才激活（例如直接从 zhuye 进 Play 的手动联调）
        if (net.MyPlayerId > 0) RequestAchievements();
    }

    private void OnDestroy()
    {
        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net == null) return;

        net.AchievementListResponseReceived -= OnAchievementListResponse;
        net.AchievementClaimResponseReceived -= OnAchievementClaimResponse;
        net.AchievementUnlockedNotifyReceived -= OnAchievementUnlockedNotify;
        net.AchievementProgressNotifyReceived -= OnAchievementProgressNotify;
        net.LoginResponseReceived -= OnLoginResponse;
        net.ConnectionBroken -= OnConnectionBroken;
    }

    // ──────────────────────────────
    // 协议
    // ──────────────────────────────

    /// <summary>请求成就列表（打开面板 / 登录后点亮红点）。</summary>
    public void RequestAchievements()
    {
        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net == null || !net.IsConnected || net.MyPlayerId <= 0) return;
        if (IsLoading) return;

        IsLoading = true;
        AchievementsUpdated?.Invoke();
        _ = SendListAsync(net);
    }

    private async Task SendListAsync(ClientNetworkManager net)
    {
        try
        {
            await net.SendAchievementListRequest();
        }
        catch (System.Exception exception)
        {
            IsLoading = false;
            StatusChanged?.Invoke($"成就读取失败：{exception.Message}");
            AchievementsUpdated?.Invoke();
        }
    }

    /// <summary>领取单个成就奖励（幂等，重复点击由 claiming 拦截）。</summary>
    /// <param name="achievementId">成就ID</param>
    public void Claim(int achievementId)
    {
        if (achievementId <= 0 || claiming.Contains(achievementId)) return;
        if (StateOf(achievementId) != 2) return;

        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net == null || !net.IsConnected) return;

        claiming.Add(achievementId);
        _ = SendClaimAsync(net, new[] { achievementId });
    }

    /// <summary>一键领取全部可领取成就（服务端一次事务处理多个ID）。</summary>
    public void ClaimAll()
    {
        List<int> ids = states.Values
            .Where(state => state.State == 2 && !claiming.Contains(state.AchievementId))
            .Select(state => state.AchievementId)
            .ToList();
        if (ids.Count == 0) return;

        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net == null || !net.IsConnected) return;

        foreach (int id in ids) claiming.Add(id);
        _ = SendClaimAsync(net, ids);
    }

    private async Task SendClaimAsync(ClientNetworkManager net, IEnumerable<int> ids)
    {
        try
        {
            await net.SendAchievementClaim(ids);
        }
        catch (System.Exception exception)
        {
            claiming.Clear();
            StatusChanged?.Invoke($"领取失败：{exception.Message}");
            AchievementsUpdated?.Invoke();
        }
    }

    // ──────────────────────────────
    // 服务端回执
    // ──────────────────────────────

    private void OnLoginResponse(LoginResponse response)
    {
        if (response == null || !response.Success) return;
        ResetForAccountSwitch();
        RequestAchievements();
    }

    private void OnConnectionBroken()
    {
        IsLoading = false;
        claiming.Clear();
        AchievementsUpdated?.Invoke();
    }

    private void OnAchievementListResponse(AchievementListResponse response)
    {
        IsLoading = false;
        if (response == null || !response.Success)
        {
            StatusChanged?.Invoke(response?.Message ?? "成就读取失败");
            AchievementsUpdated?.Invoke();
            return;
        }

        ApplyConfigs(response.Configs);
        ApplyStates(response.Achievements);
        if (response.Summary != null) summary = response.Summary;

        IsLoaded = true;
        NotifyChanged();
    }

    private void OnAchievementClaimResponse(AchievementClaimResponse response)
    {
        claiming.Clear();
        if (response == null)
        {
            AchievementsUpdated?.Invoke();
            return;
        }

        ApplyStates(response.Achievements);
        if (response.Summary != null) summary = response.Summary;

        if (response.Success)
        {
            if (response.Granted.Count > 0)
            {
                string rewardText = string.Join("、",
                    response.Granted.Select(item => $"{item.Name} x{item.Count}"));
                StatusChanged?.Invoke($"{response.Message}　获得：{rewardText}");
            }
            else
            {
                StatusChanged?.Invoke(response.Message);
            }
            BagManager.Instance?.RequestBag();
        }
        else
        {
            StatusChanged?.Invoke(response.Message);
        }

        NotifyChanged();
    }

    private void OnAchievementUnlockedNotify(AchievementUnlockedNotify notify)
    {
        if (notify == null) return;

        long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (AchievementUnlockedInfo item in notify.Items)
        {
            if (states.TryGetValue(item.AchievementId, out PlayerAchievementInfo state))
            {
                state.State = 2;
                if (state.Target > 0) state.Progress = state.Target;
                if (state.UnlockedAtMs == 0) state.UnlockedAtMs = nowMs;
            }
            else
            {
                states[item.AchievementId] = new PlayerAchievementInfo
                {
                    AchievementId = item.AchievementId,
                    State = 2,
                    Progress = 0,
                    Target = TargetOf(item.AchievementId),
                    UnlockedAtMs = nowMs,
                };
            }
        }

        if (notify.Summary != null) summary = notify.Summary;
        Unlocked?.Invoke(notify);
        NotifyChanged();
    }

    private void OnAchievementProgressNotify(AchievementProgressNotify notify)
    {
        if (notify == null) return;
        foreach (PlayerAchievementInfo state in notify.Achievements)
        {
            if (state.AchievementId > 0) states[state.AchievementId] = state;
        }
        if (notify.Summary != null) summary = notify.Summary;
        NotifyChanged();
    }

    // ──────────────────────────────
    // 查询
    // ──────────────────────────────

    /// <summary>取成就配置（不存在返回 null）。</summary>
    public AchievementCfgInfo GetConfig(int achievementId)
        => configs.TryGetValue(achievementId, out AchievementCfgInfo config) ? config : null;

    /// <summary>取玩家成就状态（没有行返回 null，按锁定处理）。</summary>
    public PlayerAchievementInfo GetState(int achievementId)
        => states.TryGetValue(achievementId, out PlayerAchievementInfo state) ? state : null;

    /// <summary>玩家成就状态值（0=未激活/锁定，1进行中 2可领取 3已领取）。</summary>
    public int StateOf(int achievementId) => GetState(achievementId)?.State ?? 0;

    /// <summary>是否正在领取该成就。</summary>
    public bool IsClaiming(int achievementId) => claiming.Contains(achievementId);

    /// <summary>列表显示进度（不超过目标）。</summary>
    public int DisplayProgress(int achievementId)
    {
        PlayerAchievementInfo state = GetState(achievementId);
        if (state == null) return 0;
        int target = Mathf.Max(1, state.Target);
        return Mathf.Clamp(state.Progress, 0, target);
    }

    /// <summary>目标值（优先状态里的 target，回退配置首条件）。</summary>
    public int TargetOf(int achievementId)
    {
        PlayerAchievementInfo state = GetState(achievementId);
        if (state != null && state.Target > 0) return state.Target;
        AchievementCfgInfo config = GetConfig(achievementId);
        if (config != null && config.Conditions.Count > 0) return Mathf.Max(1, config.Conditions[0].Target);
        return 1;
    }

    // ──────────────────────────────
    // 内部
    // ──────────────────────────────

    private void ApplyConfigs(IEnumerable<AchievementCfgInfo> source)
    {
        configs.Clear();
        configOrder.Clear();
        if (source == null) return;
        foreach (AchievementCfgInfo config in source)
        {
            if (config.AchievementId <= 0) continue;
            configs[config.AchievementId] = config;
            configOrder.Add(config);
        }
    }

    private void ApplyStates(IEnumerable<PlayerAchievementInfo> source)
    {
        states.Clear();
        if (source == null) return;
        foreach (PlayerAchievementInfo state in source)
        {
            if (state.AchievementId > 0) states[state.AchievementId] = state;
        }
    }

    private void ResetForAccountSwitch()
    {
        configs.Clear();
        configOrder.Clear();
        states.Clear();
        summary = new AchievementSummary();
        claiming.Clear();
        IsLoaded = false;
        IsLoading = false;
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        RefreshRedDot();
        AchievementsUpdated?.Invoke();
    }

    private void RefreshRedDot()
    {
        RedDotSingleton redDot = RedDotSingleton.Instance;
        if (redDot == null) return;
        redDot.Set(RedDotKeys.AchievementEntry, ClaimableCount, RedDotFlags.Number);
    }
}
