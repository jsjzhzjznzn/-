using System;
using System.Collections.Generic;
using Common.Network;
using UnityEngine;
using XLua;

/// <summary>
/// 活动热更桥（Lua 侧访问客户端固定壳的唯一 API 面）
///
/// 设计原则：热更层的 Lua 不直接摸 ClientNetworkManager / YooAsset / BagManager 这些固定层类型，
/// 一切经本桥收口——桥的签名保持稳定，桥后的实现可以随时换（这才是"业务在 Lua、壳固定"的边界）。
/// 以后活动类型增多，往本类加方法即可（加完记得 XLua/Generate Code）。
///
/// Lua 用法：
///   local bridge = CS.ActivityBridge
///   bridge.Log("hi")                          -- 日志
///   local remainMs = bridge:GetRemainMs()       -- 剩余时间（毫秒，服务器时钟）
///   local go = bridge:Instantiate("ActivityPanel")   -- 从热更包实例化 prefab（自动挂在活动画布下）
///   bridge:Draw(bannerId, 10, function(result) ... end)  -- 唤取，异步回调 result 为 GachaDrawResultData
/// </summary>
public static class ActivityBridge
{
    /// <summary>写一行日志（与 Unity Console 贯通，E2E 断言也走这里）</summary>
    public static void Log(object message)
    {
        Debug.Log($"[activity] {message}");
    }

    // ──────────────────────────────
    // 当前活动上下文（来自最近一次活动列表响应）
    // ──────────────────────────────

    public static int GetActivityId() => ActivityShell.Instance != null ? ActivityShell.Instance.CurrentActivityId : 0;
    public static int GetBannerId() => ActivityShell.Instance != null ? ActivityShell.Instance.CurrentBannerId : 0;
    public static string GetTitle() => ActivityShell.Instance?.CurrentTitle ?? string.Empty;
    public static string GetSubtitle() => ActivityShell.Instance?.CurrentSubtitle ?? string.Empty;
    public static string GetContentVersion() => ActivityShell.Instance?.CurrentContentVersion ?? string.Empty;
    public static long GetStartAtMs() => ActivityShell.Instance?.CurrentStartAtMs ?? 0;
    public static long GetEndAtMs() => ActivityShell.Instance?.CurrentEndAtMs ?? 0;
    public static long GetServerNowMs() => ActivityShell.Instance != null ? ActivityShell.Instance.ServerNowMs : 0;

    /// <summary>活动剩余毫秒（服务器时钟口径：end_at_ms - 最近一次响应里的 server_now_ms，本地不参与）</summary>
    public static long GetRemainMs()
    {
        return Math.Max(0, GetEndAtMs() - GetServerNowMs());
    }

    /// <summary>是否还有进行中的活动（无活动时 Lua 侧应显示"暂无活动"）</summary>
    public static bool HasActivity() => GetBannerId() > 0;

    // ──────────────────────────────
    // 活动列表（活动中心侧栏数据源：唤取/签到等多活动并存，按服务端排序）
    // ──────────────────────────────

    /// <summary>当前进行中的活动数（0 = 无活动，Lua 侧应显示空态）</summary>
    public static int GetActivityCount()
    {
        return ActivityShell.Instance != null ? ActivityShell.Instance.CurrentActivities.Count : 0;
    }

    /// <summary>
    /// 按下标取活动条目（0 起始，顺序 = 服务端下发的 sort/activity_id 排序 = 侧栏顺序）。
    /// 越界返回 null。条目是纯数据对象 ActivityEntryData（含 type：1=唤取 2=签到）。
    /// </summary>
    public static ActivityEntryData GetActivityInfoAt(int index)
    {
        ActivityShell shell = ActivityShell.Instance;
        if (shell == null || index < 0 || index >= shell.CurrentActivities.Count) return null;

        ActivityInfo info = shell.CurrentActivities[index];
        return new ActivityEntryData
        {
            activityId = info.ActivityId,
            type = info.Type,
            bannerId = info.BannerId,
            title = info.Title,
            subtitle = info.Subtitle,
            contentVersion = info.ContentVersion,
            startAtMs = info.StartAtMs,
            endAtMs = info.EndAtMs,
        };
    }

    // ──────────────────────────────
    // 热更资源
    // ──────────────────────────────

    /// <summary>
    /// 从热更包同步实例化 prefab（自动挂到活动画布下、层设为 UI）
    /// 所有 Lua 建的 UI 必须经这里，面板拆解时宿主才能整树回收
    /// </summary>
    /// <param name="location">资源定位地址（收集规则 AddressByFileName = 文件名）</param>
    public static GameObject Instantiate(string location)
    {
        GameObject instance = LuaActivityHost.Instance != null
            ? LuaActivityHost.Instance.InstantiateFromPackage(location)
            : null;
        if (instance != null) instance.layer = LayerMask.NameToLayer("UI");
        return instance;
    }

    /// <summary>从热更包同步加载 Sprite（banner 主视觉等，importer 须为 Sprite 类型）</summary>
    public static Sprite LoadSprite(string location)
    {
        return LuaActivityHost.Instance != null ? LuaActivityHost.Instance.LoadSpriteFromPackage(location) : null;
    }

    /// <summary>从热更包同步加载文本资产（活动配置 json 等），失败返回 null</summary>
    public static string LoadText(string location)
    {
        return LuaActivityHost.Instance != null ? LuaActivityHost.Instance.LoadTextFromPackage(location) : null;
    }

    // ──────────────────────────────
    // 背包只读（展示用；真相永远以服务端响应快照为准）
    // ──────────────────────────────

    /// <summary>按物品ID统计持有量（唤取媒介余量显示）</summary>
    public static int GetItemCount(int itemId)
    {
        return BagManager.Instance != null ? BagManager.Instance.GetItemCount(itemId) : 0;
    }

    /// <summary>
    /// 拉一次背包列表（背包是"开面板才拉"的缓存，活动界面显示媒介余量/抽卡后刷新时先调一次，
    /// 快照到达会经 BagUpdated 广播，界面可据此重读 GetItemCount）
    /// </summary>
    public static void RefreshBag()
    {
        BagManager.Instance?.RequestBag();
    }

    /// <summary>
    /// 取物品图标键（来自服务端 item_config，随背包列表下发）——热更层不必硬编码图标名；未加载到返回空串
    /// </summary>
    public static string GetItemIconKey(int itemId)
    {
        var config = BagManager.Instance != null ? BagManager.Instance.GetConfig(itemId) : null;
        return config != null ? config.IconKey : string.Empty;
    }

    /// <summary>
    /// 测试发放（联调便利，走服务端 BagGrant）：发放后服务端会回背包快照，界面自行刷新余量。
    /// 正式运营前应改为"仅调试账号可用"，或直接下掉调用点。
    /// </summary>
    public static void GrantItem(int itemId, int count)
    {
        BagManager.Instance?.Grant(itemId, count);
    }

    // ──────────────────────────────
    // 网络动作（异步，回调在主线程触发）
    // ──────────────────────────────

    /// <summary>
    /// 发起唤取；回执经回调交付 result（GachaDrawResultData）。
    /// 同一时刻只允许一个在途唤取；并发调用返回 false（旧的继续等）。
    /// </summary>
    /// <param name="bannerId">卡池ID</param>
    /// <param name="count">次数（1/10）</param>
    /// <param name="callback">Lua function(result)，任何结果（含失败）都会回调一次</param>
    /// <returns>是否已受理</returns>
    public static bool Draw(int bannerId, int count, LuaFunction callback)
    {
        return ActivityShell.Instance != null && ActivityShell.Instance.RequestDraw(bannerId, count, callback);
    }

    /// <summary>
    /// 重试上一笔唤取（复用同一个 request_id）：服务端若已结算则重放原结果，不会重复扣媒介。
    /// 仅在 result.retryable = true（超时/断线）后使用；无待重试请求时返回 false。
    /// </summary>
    public static bool RetryDraw(LuaFunction callback)
    {
        return ActivityShell.Instance != null && ActivityShell.Instance.RequestDrawRetry(callback);
    }

    /// <summary>是否有一笔可安全重试的唤取（Lua 用它决定下一次点击走 Draw 还是 RetryDraw）</summary>
    public static bool CanRetryDraw()
    {
        return ActivityShell.Instance != null && ActivityShell.Instance.CanRetryDraw;
    }

    /// <summary>拉取唤取流水；回调交付 result（GachaHistoryResultData）</summary>
    public static bool RequestHistory(int bannerId, LuaFunction callback)
    {
        return ActivityShell.Instance != null && ActivityShell.Instance.RequestHistory(bannerId, callback);
    }

    /// <summary>
    /// 拉取签到状态（7 天奖励表 + 玩家进度）；回调交付 result（SignInResultData）。
    /// 同一时刻只允许一个在途；并发调用返回 false（旧的继续等）。
    /// </summary>
    /// <param name="signId">签到活动ID（ActivityEntryData.activityId，type=2 条目）</param>
    /// <param name="callback">Lua function(result)，任何结果（含失败）都会回调一次</param>
    public static bool RequestSignInList(int signId, LuaFunction callback)
    {
        return ActivityShell.Instance != null && ActivityShell.Instance.RequestSignInList(signId, callback);
    }

    /// <summary>
    /// 签到一次（第几天/每日一签由服务端裁决）；回调交付 result（SignInResultData，成功时带背包快照已由 BagManager 应用）。
    /// 同一时刻只允许一个在途；并发调用返回 false（旧的继续等）。
    /// </summary>
    public static bool ClaimSignIn(int signId, LuaFunction callback)
    {
        return ActivityShell.Instance != null && ActivityShell.Instance.RequestSignInClaim(signId, callback);
    }

    /// <summary>刷新活动列表（倒计时到点/运营切换后主动拉新）</summary>
    public static void RequestActivityList()
    {
        if (ClientNetworkManager.Instance != null) _ = ClientNetworkManager.Instance.SendActivityListRequest();
    }

    // ──────────────────────────────
    // 生命周期
    // ──────────────────────────────

    /// <summary>当前活动面板是否打开</summary>
    public static bool IsPanelOpen() => ActivityShell.Instance != null && ActivityShell.Instance.IsPanelOpen;

    /// <summary>
    /// 请求关闭活动面板（Lua 的关闭按钮调用）：走 UIManager 关框架视图，
    /// 视图不在（联调直调）时退回直接拆内容；宿主负责拆 UI 与重建态。
    /// </summary>
    public static void CloseActivity()
    {
        if (ActivityShell.Instance != null) ActivityShell.Instance.CloseActivityUI();
    }
}
