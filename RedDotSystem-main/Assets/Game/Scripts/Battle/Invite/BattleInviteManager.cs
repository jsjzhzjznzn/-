using System;
using System.Collections.Generic;

using Common.Network;

using Cysharp.Threading.Tasks;
using SkierFramework;
using UnityEngine;

/// <summary>
/// 对战好友邀请中枢（全局常驻，DDOL）
///
/// 职责边界：
///   - 它是**唯一**订阅邀请推送的地方，所以无论玩家停在哪个界面都能弹出邀请卡片；
///   - 它只做"状态 + 网络动作"，界面表现交给 BattleInviteView / BattleInvitePopupView；
///   - 房主侧（发起/等待/取消）与好友侧（收到/同意/拒绝）都收在这里，避免状态散落在两个面板里。
///
/// 与 ChatManager 同模式：运行时不挂场景，Bootstrap 自动创建。
/// </summary>
public class BattleInviteManager : MonoBehaviour
{
    /// <summary>全局唯一实例</summary>
    public static BattleInviteManager Instance { get; private set; }

    /// <summary>好友侧：当前正在展示的邀请（null=没有）</summary>
    public BattleInviteNotify CurrentInvite { get; private set; }

    /// <summary>房主侧：是否有一条邀请正在等待回应</summary>
    public bool AwaitingResponse { get; private set; }

    /// <summary>房主侧：当前待回应邀请的凭据（0=没有）</summary>
    public long CurrentInviteId { get; private set; }

    /// <summary>房主侧：当前待回应邀请的目标</summary>
    public int PendingTargetId { get; private set; }

    /// <summary>房主侧：当前待回应邀请的目标昵称</summary>
    public string PendingTargetName { get; private set; } = string.Empty;

    /// <summary>房主侧：当前邀请的过期时间（Unix 毫秒）</summary>
    public long PendingExpireAtMs { get; private set; }

    /// <summary>房主侧：本次房间标识（GUID，首次邀请时生成）</summary>
    public string CurrentRoomId { get; private set; } = string.Empty;

    /// <summary>房主侧：邀请结果到达（同意/拒绝/过期/取消），邀请面板据此刷新状态行</summary>
    public event Action<BattleInviteResultNotify> ResultReceived;

    /// <summary>通用轻提示（面板没开时由调用方决定是否忽略）</summary>
    public event Action<string> Notice;

    /// <summary>
    /// 已知玩家昵称（playerId → name）
    /// 来源：邀请结果推送里带的 target/host 名字。NGO 侧房间界面用它把 clientId 映射成昵称。
    /// </summary>
    public readonly Dictionary<int, string> KnownPlayerNames = new();

    private readonly Queue<BattleInviteNotify> pendingInvites = new();
    private bool showingPopup;
    private long pendingAcceptInviteId;
    private bool sessionHooked;

    // ──────────────────────────────
    // 自举
    // ──────────────────────────────

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance != null) return;
        GameObject holder = new GameObject("BattleInviteManager");
        holder.AddComponent<BattleInviteManager>();
        DontDestroyOnLoad(holder);
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    private void OnEnable()
    {
        HookSession();
    }

    private void OnDisable()
    {
        UnhookSession();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        // ClientNetworkManager 可能比本组件晚创建（同为 BeforeSceneLoad 自举），惰性补订阅
        if (!sessionHooked) HookSession();
    }

    private void HookSession()
    {
        if (sessionHooked) return;
        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net == null) return;

        sessionHooked = true;
        net.BattleInviteResponseReceived += OnInviteResponse;
        net.BattleInviteNotifyReceived += OnInviteNotify;
        net.BattleInviteRespondResponseReceived += OnRespondResponse;
        net.BattleInviteResultNotifyReceived += OnResultNotify;
        net.BattleInviteCancelResponseReceived += OnCancelResponse;
        net.BattleInviteCancelledNotifyReceived += OnCancelledNotify;
        net.LoginResponseReceived += OnLoginResponse;
        net.ConnectionBroken += OnConnectionBroken;
    }

    private void UnhookSession()
    {
        if (!sessionHooked) return;
        sessionHooked = false;

        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net == null) return;

        net.BattleInviteResponseReceived -= OnInviteResponse;
        net.BattleInviteNotifyReceived -= OnInviteNotify;
        net.BattleInviteRespondResponseReceived -= OnRespondResponse;
        net.BattleInviteResultNotifyReceived -= OnResultNotify;
        net.BattleInviteCancelResponseReceived -= OnCancelResponse;
        net.BattleInviteCancelledNotifyReceived -= OnCancelledNotify;
        net.LoginResponseReceived -= OnLoginResponse;
        net.ConnectionBroken -= OnConnectionBroken;
    }

    // ──────────────────────────────
    // 房主侧：发起 / 取消
    // ──────────────────────────────

    /// <summary>
    /// 确保"我是房主"（没有房间就建一个），成功后返回 null；失败返回原因
    /// 邀请入口、房间界面都走它，避免各处重复判断
    /// </summary>
    public async UniTask<string> EnsureHostRoomAsync()
    {
        if (BattleNetworkLauncher.IsHost) return null;

        if (BattleNetworkLauncher.HasSession)
            return "你已在别人的房间里";

        string error = await BattleNetworkLauncher.StartHostAsync();
        if (string.IsNullOrEmpty(error))
            CurrentRoomId = BattleNetworkLauncher.CreateRoomId();
        return error;
    }

    /// <summary>取（必要时生成）本次房间标识；会话销毁后自动换新</summary>
    public string EnsureRoomId()
    {
        if (!BattleNetworkLauncher.HasSession || string.IsNullOrEmpty(CurrentRoomId))
            CurrentRoomId = BattleNetworkLauncher.CreateRoomId();
        return CurrentRoomId;
    }

    /// <summary>
    /// 给某个好友发邀请
    /// </summary>
    /// <param name="targetPlayerId">好友ID</param>
    /// <param name="targetName">好友昵称（面板行数据，用于"等待 XXX 回应"文案）</param>
    /// <param name="error">失败原因（成功时为空）</param>
    /// <returns>true=请求已发出（是否送达由服务端回执决定）</returns>
    public bool SendInvite(int targetPlayerId, string targetName, out string error)
    {
        error = null;

        if (!BattleNetworkLauncher.IsHost)
        {
            error = "只有房主可以邀请好友";
            return false;
        }
        if (AwaitingResponse)
        {
            error = "已有待回应的邀请，请先取消";
            return false;
        }

        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net == null)
        {
            error = "网络未就绪";
            return false;
        }

        string roomId = EnsureRoomId();
        string address = BattleNetworkLauncher.GetLocalIPv4();
        if (string.IsNullOrEmpty(address))
        {
            error = "取不到本机地址，无法发起邀请";
            return false;
        }

        // ClientNetworkManager.SendAsync 返回 System.Threading.Tasks.Task（不是 UniTask），用弃元忽略
        _ = net.SendBattleInvite(targetPlayerId, roomId, address, BattleNetworkLauncher.DefaultPort);

        PendingTargetId = targetPlayerId;
        PendingTargetName = string.IsNullOrEmpty(targetName) ? $"玩家{targetPlayerId}" : targetName;
        return true;
    }

    /// <summary>房主撤回当前待回应的邀请</summary>
    public void CancelInvite()
    {
        if (!AwaitingResponse && CurrentInviteId == 0) return;

        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net != null)
            _ = net.SendBattleInviteCancel(EnsureRoomId(), CurrentInviteId);

        ResetHostSideState();
    }

    private void ResetHostSideState()
    {
        AwaitingResponse = false;
        CurrentInviteId = 0;
        PendingTargetId = 0;
        PendingTargetName = string.Empty;
        PendingExpireAtMs = 0;
    }

    // ──────────────────────────────
    // 好友侧：同意 / 拒绝
    // ──────────────────────────────

    /// <summary>同意当前邀请：先回服务端，拿到房主地址后再直连</summary>
    public void Accept()
    {
        BattleInviteNotify invite = CurrentInvite;
        if (invite == null) return;
        if (BattleNetworkLauncher.HasSession)
        {
            Decline("正在对战中");
            return;
        }

        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net == null) return;

        pendingAcceptInviteId = invite.InviteId;
        _ = net.SendBattleInviteRespond(invite.InviteId, true);
    }

    /// <summary>拒绝当前邀请（立即收起弹窗，不等服务端回执）</summary>
    public void Decline(string reason = "")
    {
        BattleInviteNotify invite = CurrentInvite;
        if (invite == null) return;

        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net != null)
            _ = net.SendBattleInviteRespond(invite.InviteId, false, reason);

        DismissPopup();
    }

    /// <summary>弹窗关闭回调（由 BattleInvitePopupView.OnClose 调用）</summary>
    public void OnPopupClosed()
    {
        showingPopup = false;
        CurrentInvite = null;
        pendingAcceptInviteId = 0;
        TryShowNext();
    }

    private void DismissPopup()
    {
        if (UIManager.Instance != null && UIManager.Instance.IsOpen(UIType.BattleInvitePopupView))
            UIManager.Instance.Close(UIType.BattleInvitePopupView).Forget();
        else
            OnPopupClosed();
    }

    // ──────────────────────────────
    // 网络回调
    // ──────────────────────────────

    private void OnInviteNotify(BattleInviteNotify notify)
    {
        if (notify == null || notify.InviteId == 0) return;

        // 已在房间/对战中：自动拒绝，只留一条轻提示（不能把玩家从对局里拽出来）
        if (BattleNetworkLauncher.HasSession)
        {
            ClientNetworkManager net = ClientNetworkManager.Instance;
            if (net != null) _ = net.SendBattleInviteRespond(notify.InviteId, false, "正在对战中");
            RaiseNotice($"{notify.FromPlayerName} 邀请你对战，但你正在对战中");
            return;
        }

        pendingInvites.Enqueue(notify);
        TryShowNext();
    }

    private void TryShowNext()
    {
        if (showingPopup || pendingInvites.Count == 0) return;

        // 排队期间状态可能已经变化（建房/进房），逐条自动拒绝
        if (BattleNetworkLauncher.HasSession)
        {
            ClientNetworkManager net = ClientNetworkManager.Instance;
            while (pendingInvites.Count > 0)
            {
                BattleInviteNotify stale = pendingInvites.Dequeue();
                if (net != null) _ = net.SendBattleInviteRespond(stale.InviteId, false, "正在对战中");
            }
            return;
        }

        CurrentInvite = pendingInvites.Dequeue();
        showingPopup = true;

        if (UIManager.Instance == null)
        {
            // 没有 UI 框架（例如纯逻辑场景）：直接拒绝，避免邀请挂着不处理
            ClientNetworkManager net = ClientNetworkManager.Instance;
            if (net != null) _ = net.SendBattleInviteRespond(CurrentInvite.InviteId, false, "界面不可用");
            OnPopupClosed();
            return;
        }

        UIManager.Instance.Open(UIType.BattleInvitePopupView, CurrentInvite).Forget();
    }

    private void OnInviteResponse(BattleInviteResponse response)
    {
        if (response == null) return;

        if (!response.Success)
        {
            // 房主邀请失败：清掉等待态并提示
            ResetHostSideState();
            RaiseNotice(response.Message);
            return;
        }

        AwaitingResponse = true;
        CurrentInviteId = response.InviteId;
        PendingExpireAtMs = response.ExpireAtMs;
        if (!string.IsNullOrEmpty(response.RoomId)) CurrentRoomId = response.RoomId;

        // 昵称由好友列表行在发邀请时提供，这里只补一次空值兜底
        if (string.IsNullOrEmpty(PendingTargetName))
            PendingTargetName = $"玩家{PendingTargetId}";

        RaiseNotice(response.Message);
    }

    private async void OnRespondResponse(BattleInviteRespondResponse response)
    {
        if (response == null) return;

        // 只有"我们主动点了同意"才关心这份回执；拒绝的回执走静默
        if (pendingAcceptInviteId == 0) return;
        pendingAcceptInviteId = 0;

        if (!response.Success)
        {
            RaiseNotice(response.Message);
            return;
        }

        string error = await BattleNetworkLauncher.StartClientAsync(response.HostAddress);
        if (!string.IsNullOrEmpty(error))
        {
            RaiseNotice("进入房间失败：" + error);
            return;
        }

        // 记住房主昵称：房间里的"对手"一栏要用（NGO 侧没有账号信息）
        if (response.HostPlayerId > 0 && !string.IsNullOrEmpty(response.HostPlayerName))
            KnownPlayerNames[response.HostPlayerId] = response.HostPlayerName;

        DismissPopup();
        CloseBattlePanelsThenOpenRoom();
    }

    private static void CloseBattlePanelsThenOpenRoom()
    {
        if (UIManager.Instance == null) return;

        if (UIManager.Instance.IsOpen(UIType.BattleInviteView))
            UIManager.Instance.Close(UIType.BattleInviteView).Forget();
        if (UIManager.Instance.IsOpen(UIType.BattleLobbyView))
            UIManager.Instance.Close(UIType.BattleLobbyView).Forget();
        if (UIManager.Instance.IsOpen(UIType.JoinRoomView))
            UIManager.Instance.Close(UIType.JoinRoomView).Forget();

        UIManager.Instance.Open(UIType.BattleRoomView).Forget();
    }

    private void OnResultNotify(BattleInviteResultNotify notify)
    {
        if (notify == null) return;

        if (notify.TargetPlayerId > 0 && !string.IsNullOrEmpty(notify.TargetPlayerName))
            KnownPlayerNames[notify.TargetPlayerId] = notify.TargetPlayerName;

        ResetHostSideState();
        RaiseNotice(notify.Message);
        ResultReceived?.Invoke(notify);
    }

    private void OnCancelResponse(BattleInviteCancelResponse response)
    {
        if (response == null) return;
        ResetHostSideState();
        RaiseNotice(response.Message);
    }

    private void OnCancelledNotify(BattleInviteCancelledNotify notify)
    {
        if (notify == null) return;

        // 排队里的同一条邀请直接丢掉
        if (pendingInvites.Count > 0)
        {
            Queue<BattleInviteNotify> keep = new();
            while (pendingInvites.Count > 0)
            {
                BattleInviteNotify item = pendingInvites.Dequeue();
                if (item.InviteId != notify.InviteId) keep.Enqueue(item);
            }
            while (keep.Count > 0) pendingInvites.Enqueue(keep.Dequeue());
        }

        if (CurrentInvite != null && CurrentInvite.InviteId == notify.InviteId)
        {
            RaiseNotice(notify.Message);
            DismissPopup();
        }
    }

    private void OnLoginResponse(LoginResponse response)
    {
        if (response == null || !response.Success) return;

        // 换账号：清空所有与上一账号相关的状态，防串号
        pendingInvites.Clear();
        KnownPlayerNames.Clear();
        CurrentInvite = null;
        CurrentRoomId = string.Empty;
        ResetHostSideState();
        showingPopup = false;
    }

    private void OnConnectionBroken()
    {
        pendingInvites.Clear();
        ResetHostSideState();
        if (showingPopup) DismissPopup();
    }

    // ──────────────────────────────
    // 工具
    // ──────────────────────────────

    private void RaiseNotice(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        Debug.Log("[BattleInvite] " + message);
        Notice?.Invoke(message);
    }
}
