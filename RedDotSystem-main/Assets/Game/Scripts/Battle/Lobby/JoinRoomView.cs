using System.Net;
using Cysharp.Threading.Tasks;
using SkierFramework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 加入房间（移植自 compete 的 jiaru）：输入房主 IP，UnityTransport 连接 7777 后进入房间界面。
/// </summary>
public class JoinRoomView : UIView
{
    [ControlBinding] public RectTransform panel;
    [ControlBinding] public TMP_Text titleText;
    [ControlBinding] public TMP_Text hintText;
    [ControlBinding] public TMP_InputField ipInput;
    [ControlBinding] public Button joinButton;
    [ControlBinding] public Button backButton;
    [ControlBinding] public TMP_Text statusText;

    private bool _connecting;

    public override void OnAddListener()
    {
        base.OnAddListener();
        if (joinButton != null) joinButton.onClick.AddListener(OnJoinClicked);
        if (backButton != null) backButton.onClick.AddListener(OnBackClicked);
        if (ipInput != null) ipInput.onSubmit.AddListener(OnIpSubmitted);
    }

    public override void OnRemoveListener()
    {
        if (joinButton != null) joinButton.onClick.RemoveListener(OnJoinClicked);
        if (backButton != null) backButton.onClick.RemoveListener(OnBackClicked);
        if (ipInput != null) ipInput.onSubmit.RemoveListener(OnIpSubmitted);
        base.OnRemoveListener();
    }

    public override void OnOpen(object userData)
    {
        base.OnOpen(userData);
        _connecting = false;
        SetStatus(string.Empty);
        if (ipInput != null)
        {
            if (string.IsNullOrWhiteSpace(ipInput.text))
                ipInput.text = BattleNetworkLauncher.DefaultAddress;
            ipInput.ActivateInputField();
        }
    }

    private void OnIpSubmitted(string _)
    {
        TryJoin();
    }

    private void OnJoinClicked()
    {
        TryJoin();
    }

    private async void TryJoin()
    {
        if (_connecting) return;

        string ip = ipInput != null ? ipInput.text : string.Empty;
        ip = string.IsNullOrWhiteSpace(ip) ? BattleNetworkLauncher.DefaultAddress : ip.Trim();
        if (!IPAddress.TryParse(ip, out _))
        {
            SetStatus("请输入有效的 IPv4 地址");
            return;
        }

        _connecting = true;
        string error = await BattleNetworkLauncher.StartClientAsync(ip);
        if (!string.IsNullOrEmpty(error))
        {
            _connecting = false;
            SetStatus(error);
            return;
        }

        SetStatus($"正在连接 {ip}:{BattleNetworkLauncher.DefaultPort} …");
        UIManager.Instance.Close(UIType.JoinRoomView).Forget();
        UIManager.Instance.Close(UIType.BattleLobbyView).Forget();
        UIManager.Instance.Open(UIType.BattleRoomView).Forget();
    }

    private void OnBackClicked()
    {
        UIManager.Instance.Close(UIType.JoinRoomView).Forget();
    }

    private void SetStatus(string content)
    {
        if (statusText != null) statusText.SetText(content ?? string.Empty);
    }
}
