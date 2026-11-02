using System.Collections.Generic;

using Common.Network;

using Cysharp.Threading.Tasks;
using SkierFramework;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 邀请好友对战面板（房主侧）
///
/// 数据流：打开时向游戏服拉一次好友列表（带在线状态）→ 行内点「邀请」→
/// BattleInviteManager 发邀请 → 服务端推送好友 → 结果回执回到本面板的状态行。
///
/// 房间必须已经存在（本面板不负责建房，入口在 BattleLobbyView / BattleRoomView）。
/// </summary>
public class BattleInviteView : UIView
{
    [ControlBinding] public RectTransform panel;
    [ControlBinding] public TMP_Text titleText;
    [ControlBinding] public TMP_Text subtitleText;
    [ControlBinding] public TMP_Text addressText;
    [ControlBinding] public Button closeButton;
    [ControlBinding] public RectTransform listContent;
    [ControlBinding] public GameObject rowTemplate;
    [ControlBinding] public TMP_Text statusText;
    [ControlBinding] public TMP_Text pendingText;
    [ControlBinding] public Button cancelInviteButton;

    /// <summary>好友列表行（运行时创建，模板常驻 inactive）</summary>
    private sealed class Row
    {
        public GameObject Go;
        public TMP_Text NameText;
        public TMP_Text StateText;
        public Button InviteButton;
        public TMP_Text InviteLabel;
        public int PlayerId;
        public bool Online;
    }

    private readonly List<Row> rows = new();
    private bool opened;
    private bool lastAwaiting;
    private int lastPendingSecond = -1;   // 上次渲染的剩余秒数：秒没变就不重建字符串（原来每帧 SetText）

    private NetworkManager Nm => BattleNetworkLauncher.NetworkManager;

    public override void OnAddListener()
    {
        base.OnAddListener();
        if (closeButton != null) closeButton.onClick.AddListener(OnClickClose);
        if (cancelInviteButton != null) cancelInviteButton.onClick.AddListener(OnClickCancelInvite);

        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net != null) net.FriendListResponseReceived += OnFriendList;

        BattleInviteManager manager = BattleInviteManager.Instance;
        if (manager != null)
        {
            manager.Notice += OnNotice;
            manager.ResultReceived += OnResult;
        }

        if (Nm != null) Nm.OnClientConnectedCallback += OnNmClientConnected;
    }

    public override void OnRemoveListener()
    {
        if (closeButton != null) closeButton.onClick.RemoveListener(OnClickClose);
        if (cancelInviteButton != null) cancelInviteButton.onClick.RemoveListener(OnClickCancelInvite);

        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net != null) net.FriendListResponseReceived -= OnFriendList;

        BattleInviteManager manager = BattleInviteManager.Instance;
        if (manager != null)
        {
            manager.Notice -= OnNotice;
            manager.ResultReceived -= OnResult;
        }

        if (Nm != null) Nm.OnClientConnectedCallback -= OnNmClientConnected;

        base.OnRemoveListener();
    }

    public override void OnOpen(object userData)
    {
        base.OnOpen(userData);
        opened = true;
        lastAwaiting = false;

        if (titleText != null) titleText.SetText("邀请好友对战");
        if (subtitleText != null) subtitleText.SetText("选择在线好友发起 1v1 邀请");
        SetStatus(string.Empty);
        RefreshAddress();

        // 打开即拉一次好友列表：好友面板可能没开过，不能依赖别人的缓存
        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net != null) _ = net.SendFriendListRequest();

        RefreshPending();
    }

    public override void OnClose()
    {
        opened = false;
        ClearRows();
        base.OnClose();
    }

    private void Update()
    {
        BattleInviteManager manager = BattleInviteManager.Instance;
        if (manager == null) return;

        if (manager.AwaitingResponse && manager.PendingExpireAtMs > 0)
        {
            long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long leftMs = manager.PendingExpireAtMs - nowMs;
            int seconds = Mathf.Max(0, Mathf.CeilToInt(leftMs / 1000f));
            // 秒数一秒才变一次：只在变化时 SetText，避免每帧一次字符串分配 + TMP 重排
            if (pendingText != null && seconds != lastPendingSecond)
            {
                lastPendingSecond = seconds;
                pendingText.SetText($"等待 {manager.PendingTargetName} 回应… 剩余 {seconds} 秒");
            }
        }
        else
        {
            lastPendingSecond = -1;   // 非等待态清零：下一次邀请立即刷新
        }

        // 等待态变化时刷新行按钮可用性（避免每帧遍历）
        if (lastAwaiting != manager.AwaitingResponse)
        {
            lastAwaiting = manager.AwaitingResponse;
            lastPendingSecond = -1;   // 等待对象可能变了：强制刷新一次倒计时文本
            RefreshRowInteractable();
            RefreshPending();
        }
    }

    // ---------------- 好友列表 ----------------

    private void OnFriendList(FriendListResponse response)
    {
        if (!opened || response == null) return;
        Rebuild(response);
    }

    private void Rebuild(FriendListResponse response)
    {
        ClearRows();
        if (rowTemplate == null || listContent == null) return;

        rowTemplate.SetActive(false);

        foreach (FriendInfo friend in response.Friends)
        {
            GameObject go = Instantiate(rowTemplate, listContent);
            go.SetActive(true);

            Row row = new()
            {
                Go = go,
                PlayerId = friend.PlayerId,
                Online = friend.Online,
                NameText = FindText(go, "NameText"),
                StateText = FindText(go, "StateText"),
                InviteButton = FindButton(go, "InviteButton"),
            };

            // 按钮文字是 CreateButton 生成的 "Text (TMP)" 子物体，不额外改名，直接取按钮下第一个文本
            if (row.InviteButton != null)
                row.InviteLabel = row.InviteButton.GetComponentInChildren<TMP_Text>(true);

            if (row.NameText != null)
                row.NameText.SetText(string.IsNullOrEmpty(friend.Name) ? $"玩家{friend.PlayerId}" : friend.Name);

            if (row.StateText != null)
            {
                row.StateText.SetText(friend.Online ? "在线" : "离线");
                row.StateText.color = friend.Online
                    ? new Color(0.157f, 0.780f, 0.443f)
                    : new Color(0.541f, 0.561f, 0.588f);
            }

            int capturedId = friend.PlayerId;
            if (row.InviteButton != null)
                row.InviteButton.onClick.AddListener(() => OnClickInvite(capturedId));

            rows.Add(row);
        }

        if (rows.Count == 0)
            SetStatus("还没有好友，先去加个好友吧");

        RefreshRowInteractable();
    }

    private void RefreshRowInteractable()
    {
        BattleInviteManager manager = BattleInviteManager.Instance;
        bool awaiting = manager != null && manager.AwaitingResponse;
        bool roomFull = IsRoomFull();

        foreach (Row row in rows)
        {
            if (row.InviteButton == null) continue;

            row.InviteButton.interactable = row.Online && !awaiting && !roomFull;

            if (row.InviteLabel != null)
            {
                if (!row.Online) row.InviteLabel.SetText("离线");
                else if (awaiting) row.InviteLabel.SetText("等待中");
                else if (roomFull) row.InviteLabel.SetText("房间已满");
                else row.InviteLabel.SetText("邀请");
            }
        }
    }

    private void ClearRows()
    {
        foreach (Row row in rows)
        {
            if (row.Go != null) Destroy(row.Go);
        }
        rows.Clear();
    }

    // ---------------- 交互 ----------------

    private void OnClickInvite(int playerId)
    {
        if (BattleInviteManager.Instance == null)
        {
            SetStatus("邀请服务未就绪");
            return;
        }

        if (IsRoomFull())
        {
            SetStatus("房间已满（1v1 只能两人）");
            RefreshRowInteractable();
            return;
        }

        string targetName = ResolveRowName(playerId);
        if (!BattleInviteManager.Instance.SendInvite(playerId, targetName, out string error))
        {
            SetStatus(error);
            return;
        }

        SetStatus($"已发送邀请给 {targetName}");
        RefreshRowInteractable();
        RefreshPending();
    }

    private void OnClickCancelInvite()
    {
        BattleInviteManager.Instance?.CancelInvite();
        SetStatus("已取消邀请");
        RefreshPending();
        RefreshRowInteractable();
    }

    private void OnClickClose()
    {
        UIManager.Instance.Close(UIType.BattleInviteView).Forget();
    }

    private void OnNotice(string message)
    {
        if (!opened) return;
        SetStatus(message);
    }

    private void OnResult(BattleInviteResultNotify notify)
    {
        if (!opened || notify == null) return;

        // accepted 时房主仍在房间里等对方连接，这里只更新文案；
        // 真正的"进房成功"由 NGO 的 OnClientConnected 回调告知（见 OnNmClientConnected）
        if (notify.Result == 1)
            SetStatus($"{notify.TargetPlayerName} 已接受，正在连接…");
        else
            SetStatus(notify.Message);

        RefreshPending();
        RefreshRowInteractable();
    }

    private void OnNmClientConnected(ulong clientId)
    {
        if (!opened) return;
        if (Nm == null || !Nm.IsServer) return;
        if (!IsRoomFull()) return;

        SetStatus("对方已加入房间");
        UIManager.Instance.Close(UIType.BattleInviteView).Forget();
    }

    // ---------------- 展示 ----------------

    private void RefreshAddress()
    {
        if (addressText == null) return;
        if (!BattleNetworkLauncher.IsHost)
        {
            addressText.SetText("只有房主可以发起邀请");
            return;
        }
        addressText.SetText($"你的地址 {BattleNetworkLauncher.GetLocalIPv4()}:{BattleNetworkLauncher.DefaultPort}");
    }

    private void RefreshPending()
    {
        BattleInviteManager manager = BattleInviteManager.Instance;
        bool awaiting = manager != null && manager.AwaitingResponse;

        if (pendingText != null)
            pendingText.gameObject.SetActive(awaiting);

        if (cancelInviteButton != null)
            cancelInviteButton.gameObject.SetActive(awaiting);

        if (awaiting && manager != null && pendingText != null)
            pendingText.SetText($"等待 {manager.PendingTargetName} 回应…");
    }

    private void SetStatus(string message)
    {
        if (statusText != null) statusText.SetText(message ?? string.Empty);
    }

    private bool IsRoomFull()
    {
        NetworkManager nm = Nm;
        // ConnectedClientsIds 含房主自己：>=2 表示已经有一位好友进房
        return nm != null && nm.IsServer && nm.ConnectedClientsIds.Count >= 2;
    }

    private string ResolveRowName(int playerId)
    {
        foreach (Row row in rows)
        {
            if (row.PlayerId != playerId || row.NameText == null) continue;
            return row.NameText.text;
        }
        return $"玩家{playerId}";
    }

    private static TMP_Text FindText(GameObject root, string childName)
    {
        Transform child = root.transform.Find(childName);
        return child != null ? child.GetComponent<TMP_Text>() : null;
    }

    private static Button FindButton(GameObject root, string childName)
    {
        Transform child = root.transform.Find(childName);
        return child != null ? child.GetComponent<Button>() : null;
    }
}
