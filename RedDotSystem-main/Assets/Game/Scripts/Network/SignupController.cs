using Common.Network;

using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 注册界面逻辑
/// 绑定注册面板的用户名/密码/确认密码输入框与注册按钮：
/// 两次密码不一致 → 提醒；校验通过 → 发送注册请求 → 成功后切回登录界面
/// </summary>
public class SignupController : MonoBehaviour
{
    [Header("注册界面 UI 引用")]
    /// <summary>用户名输入框</summary>
    public TMP_InputField usernameInput;

    /// <summary>密码输入框</summary>
    public TMP_InputField passwordInput;

    /// <summary>再次输入密码输入框</summary>
    public TMP_InputField confirmPasswordInput;

    /// <summary>注册按钮</summary>
    public Button registerButton;

    /// <summary>状态反馈文本（提醒"两次密码不一致"、注册结果等）</summary>
    public Text statusText;

    [Header("面板切换引用")]
    /// <summary>注册界面根对象（注册成功后隐藏）</summary>
    public GameObject signupPanel;

    /// <summary>登录界面根对象（注册成功后显示）</summary>
    public GameObject loginPanel;

    // ──────────────────────────────
    // Unity 生命周期
    // ──────────────────────────────

    /// <summary>
    /// 注意：本面板初始为未激活状态，Start 会在面板第一次激活时才执行
    /// </summary>
    private void Start()
    {
        if (registerButton != null)
        {
            registerButton.onClick.AddListener(OnRegisterClicked);
        }
        if (ClientNetworkManager.Instance != null)
        {
            ClientNetworkManager.Instance.StatusChanged += OnNetworkStatus;
            ClientNetworkManager.Instance.RegisterResponseReceived += OnRegisterResponse;
        }
    }

    private void OnDestroy()
    {
        if (ClientNetworkManager.Instance != null)
        {
            ClientNetworkManager.Instance.StatusChanged -= OnNetworkStatus;
            ClientNetworkManager.Instance.RegisterResponseReceived -= OnRegisterResponse;
        }
    }

    // ──────────────────────────────
    // 注册流程
    // ──────────────────────────────

    /// <summary>
    /// 注册按钮点击处理
    /// 校验输入（两次密码不一致则提醒）→ 建立连接 → 发送注册请求
    /// </summary>
    private async void OnRegisterClicked()
    {
        string username = usernameInput != null ? usernameInput.text.Trim() : string.Empty;
        string password = passwordInput != null ? passwordInput.text : string.Empty;
        string confirmPassword = confirmPasswordInput != null ? confirmPasswordInput.text : string.Empty;

        if (username.Length == 0 || password.Length == 0)
        {
            SetStatus("请输入用户名和密码");
            return;
        }

        // 两次密码不一致：仅本地提醒，不发送请求
        if (password != confirmPassword)
        {
            SetStatus("两次密码不一致");
            return;
        }

        if (ClientNetworkManager.Instance == null) return;
        if (!ClientNetworkManager.Instance.IsConnected)
        {
            SetStatus("连接服务器中...");
            bool connected = await ClientNetworkManager.Instance.ConnectAsync(
                ClientNetworkManager.ServerHost, ClientNetworkManager.ServerPort);
            if (!connected) return;
        }

        SetStatus("注册中...");
        _ = ClientNetworkManager.Instance.SendRegister(username, password);
    }

    // ──────────────────────────────
    // 网络事件处理
    // ──────────────────────────────

    /// <summary>连接状态变化处理</summary>
    private void OnNetworkStatus(string message)
    {
        SetStatus(message);
    }

    /// <summary>
    /// 注册响应处理
    /// 注册成功 → 写入数据库完成 → 隐藏注册面板，切回登录面板
    /// </summary>
    private void OnRegisterResponse(RegisterResponse response)
    {
        if (!response.Success)
        {
            SetStatus($"注册失败：{response.Message}");
            return;
        }

        SetStatus("注册成功，请登录");
        if (signupPanel != null) signupPanel.SetActive(false);
        if (loginPanel != null) loginPanel.SetActive(true);
    }

    // ──────────────────────────────
    // 状态显示
    // ──────────────────────────────

    /// <summary>
    /// 更新状态文本的方法
    /// </summary>
    private void SetStatus(string message)
    {
        if (statusText != null) statusText.text = message;
        Debug.Log($"[注册]{message}");
    }
}
