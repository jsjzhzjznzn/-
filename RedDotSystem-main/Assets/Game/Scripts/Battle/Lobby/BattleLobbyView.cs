using Cysharp.Threading.Tasks;
using SkierFramework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 对战大厅入口（移植自 compete 的 Xuanze）：创建房间 / 加入房间 / 关闭。
/// 房主即服务端（Listen Server），加入方输入房主 IP 后通过 UnityTransport 连接 7777。
/// </summary>
public class BattleLobbyView : UIView
{
    [ControlBinding] public RectTransform panel;
    [ControlBinding] public TMP_Text titleText;
    [ControlBinding] public TMP_Text subtitleText;
    [ControlBinding] public TMP_Text myIpText;
    [ControlBinding] public Button createRoomButton;
    [ControlBinding] public Button inviteButton;
    [ControlBinding] public Button joinRoomButton;
    [ControlBinding] public Button closeButton;
    [ControlBinding] public TMP_Text statusText;

    public override void OnAddListener()
    {
        base.OnAddListener();
        if (createRoomButton != null) createRoomButton.onClick.AddListener(OnCreateRoomClicked);
        if (inviteButton != null) inviteButton.onClick.AddListener(OnInviteClicked);
        if (joinRoomButton != null) joinRoomButton.onClick.AddListener(OnJoinRoomClicked);
        if (closeButton != null) closeButton.onClick.AddListener(OnCloseClicked);
    }

    public override void OnRemoveListener()
    {
        if (createRoomButton != null) createRoomButton.onClick.RemoveListener(OnCreateRoomClicked);
        if (inviteButton != null) inviteButton.onClick.RemoveListener(OnInviteClicked);
        if (joinRoomButton != null) joinRoomButton.onClick.RemoveListener(OnJoinRoomClicked);
        if (closeButton != null) closeButton.onClick.RemoveListener(OnCloseClicked);
        base.OnRemoveListener();
    }

    public override void OnOpen(object userData)
    {
        base.OnOpen(userData);
        RefreshFooter();
    }

    /// <summary>从房间返回时补回页脚提示（OnOpen 只在首次打开时执行）</summary>
    public override void OnResume()
    {
        base.OnResume();
        RefreshFooter();
    }

    /// <summary>
    /// 房间关闭并销毁联机会话后调用：框架恢复下层大厅（OnResume）发生在房间 OnClose 之前，
    /// 那时 HasSession 还是 true，所以这里补一次刷新，避免页脚停留在“已存在联机会话”。
    /// </summary>
    public static void NotifySessionClosed()
    {
        BattleLobbyView view = UnityEngine.Object.FindObjectOfType<BattleLobbyView>();
        if (view != null) view.RefreshFooter();
    }

    private void RefreshFooter()
    {
        if (statusText != null)
        {
            // 只覆盖本类写的两条默认提示，不吞掉真正的错误信息（端口被占用等）
            bool isDefaultHint = string.IsNullOrEmpty(statusText.text)
                || statusText.text.StartsWith("已存在联机会话")
                || statusText.text.StartsWith("创建房间后把本机 IP");
            if (isDefaultHint)
            {
                SetStatus(BattleNetworkLauncher.HasSession
                    ? "已存在联机会话，进入房间可继续当前对局"
                    : "创建房间后把本机 IP 告诉好友；加入房间需要填写房主 IP");
            }
        }

        if (myIpText != null)
        {
            myIpText.SetText($"我的 IP：{BattleNetworkLauncher.GetLocalIPv4()} · 端口 {BattleNetworkLauncher.DefaultPort}");
        }
    }

    private async void OnCreateRoomClicked()
    {
        string error = await BattleNetworkLauncher.StartHostAsync();
        if (string.IsNullOrEmpty(error))
        {
            SetStatus(string.Empty);
            UIManager.Instance.Open(UIType.BattleRoomView).Forget();
            return;
        }
        SetStatus(error);
    }

    private void OnJoinRoomClicked()
    {
        UIManager.Instance.Open(UIType.JoinRoomView).Forget();
    }

    /// <summary>
    /// 邀请好友对战：没有房间就先建房（房主即服务端），然后进房间并打开邀请面板
    ///
    /// 顺序不能反：BattleRoomView.OnClose 会销毁联机会话，房主必须停在房间界面等好友。
    /// </summary>
    private async void OnInviteClicked()
    {
        if (BattleInviteManager.Instance == null)
        {
            SetStatus("邀请服务未就绪");
            return;
        }

        string error = await BattleInviteManager.Instance.EnsureHostRoomAsync();
        if (!string.IsNullOrEmpty(error))
        {
            SetStatus(error);
            return;
        }

        SetStatus(string.Empty);

        if (!UIManager.Instance.IsOpen(UIType.BattleRoomView))
            await UIManager.Instance.Open(UIType.BattleRoomView);

        UIManager.Instance.Open(UIType.BattleInviteView).Forget();
    }

    private void OnCloseClicked()
    {
        UIManager.Instance.Close(UIType.BattleLobbyView).Forget();
    }

    private void SetStatus(string content)
    {
        if (statusText != null) statusText.SetText(content ?? string.Empty);
    }
}
