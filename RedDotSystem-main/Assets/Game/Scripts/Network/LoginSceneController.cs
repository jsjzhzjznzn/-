using System;

using Common.Network;

using TMPro;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// LOGIN 场景登录逻辑
/// 绑定场景自带的用户名/密码输入框与登录按钮：
/// 点击登录 → 连接服务器 → 数据库验证 → 成功跳转 zhuye 场景
/// 记住账号/自动登录（复选框用户自控）：
///   * 记住账号 = 本地保存账号名，下次启动预填
///   * 自动登录 = 登录成功后服务器签发令牌（库内只存哈希），下次启动用令牌免密码换会话
/// </summary>
public class LoginSceneController : MonoBehaviour
{
    [Header("场景 UI 引用")]
    /// <summary>用户名输入框</summary>
    public TMP_InputField usernameInput;

    /// <summary>密码输入框</summary>
    public TMP_InputField passwordInput;

    /// <summary>登录按钮</summary>
    public Button loginButton;

    /// <summary>状态反馈文本（可选，未指定则仅输出日志）</summary>
    public TextMeshProUGUI statusText;

    [Header("注册入口")]
    /// <summary>注册按钮（点击后切换到注册界面）</summary>
    public Button registerButton;

    /// <summary>注册界面根对象（点击注册时显示）</summary>
    public GameObject signupPanel;

    /// <summary>登录界面根对象（注册界面显示时隐藏）</summary>
    public GameObject loginPanel;

    [Header("记住账号 / 自动登录")]
    /// <summary>「记住账号」复选框（勾选=下次启动预填账号）</summary>
    public Toggle rememberAccountToggle;

    /// <summary>「自动登录」复选框（勾选=登录成功后服务器签发令牌，下次启动免密码直进）</summary>
    public Toggle autoLoginToggle;

    /// <summary>登录成功后跳转的主页面场景名</summary>
    private const string MainSceneName = "zhuye";

    // PlayerPrefs 键（自动登录令牌只存客户端本地，服务端库内只存其哈希）
    // 公开给退出登录逻辑复用，避免两边字符串漂移
    public const string RememberAccountKey = "Login_RememberAccount";
    public const string AutoLoginKey = "Login_AutoLogin";
    public const string AccountKey = "Login_Account";
    public const string TokenKey = "Login_Token";
    public const string TokenExpireKey = "Login_TokenExpireMs";

    /// <summary>本次在途登录使用的账号（回执到达时账号输入框可能已被改动，发送前定格）</summary>
    private string pendingAccount;

    /// <summary>登录在途守卫（自动登录进行中忽略手动点击，反之亦然）</summary>
    private bool loginInflight;

    /// <summary>在途登录是否为令牌登录（失败时据此决定是否清掉本地令牌）</summary>
    private bool tokenLoginAttempted;

    /// <summary>本次会话是否已开始（BeginSession 幂等）</summary>
    private bool sessionActive;

    /// <summary>网络事件是否已订阅（面板实例被框架缓存复用，订阅只能做一次）</summary>
    private bool listenersBound;

    /// <summary>两个复选框的 onValueChanged 是否已订阅（复用面板时避免重复挂监听）</summary>
    private bool toggleListenersBound;

    /// <summary>
    /// 登录成功回调：由框架侧 LoginView 注入，用于关闭登录界面。
    /// 为空时（比如直接把预制体丢在场景里调试）不影响原有流程。
    /// </summary>
    public Action loginSucceeded;

    // ──────────────────────────────
    // 会话生命周期（框架打开/关闭时调用；也兼容预制体直接放进场景的 Start）
    // ──────────────────────────────

    private void Start()
    {
        // 框架加载时会紧接着走 OnOpen → LoginView.BeginSession()，这里靠幂等保护不会重复执行
        BeginSession();
    }

    private void OnDestroy()
    {
        EndSession();
    }

    /// <summary>
    /// 开始一次登录会话：接线按钮/网络事件（只做一次）、恢复勾选与预填、尝试自动登录。
    /// 可重复调用（框架每次打开登录界面都会调）。
    /// </summary>
    public void BeginSession()
    {
        if (sessionActive) return;
        sessionActive = true;

        if (!listenersBound)
        {
            if (loginButton != null)
            {
                loginButton.onClick.AddListener(OnLoginClicked);
            }
            if (registerButton != null)
            {
                registerButton.onClick.AddListener(OnRegisterClicked);
            }
            if (ClientNetworkManager.Instance != null)
            {
                ClientNetworkManager.Instance.StatusChanged += OnNetworkStatus;
                ClientNetworkManager.Instance.LoginResponseReceived += OnLoginResponse;
            }
            listenersBound = true;
        }

        ResetToLoginPage();
        RestoreLoginPreferences();
        TryAutoLogin();
    }

    /// <summary>
    /// 结束本次登录会话：摘掉网络监听、清在途状态。框架关闭登录界面时调用。
    /// </summary>
    public void EndSession()
    {
        if (!sessionActive) return;
        sessionActive = false;

        if (listenersBound)
        {
            if (ClientNetworkManager.Instance != null)
            {
                ClientNetworkManager.Instance.StatusChanged -= OnNetworkStatus;
                ClientNetworkManager.Instance.LoginResponseReceived -= OnLoginResponse;
            }
            listenersBound = false;
        }

        loginInflight = false;
        tokenLoginAttempted = false;
    }

    /// <summary>回到登录卡片：隐藏注册卡片、清空密码与状态提示（复用面板时恢复初始视图）</summary>
    public void ResetToLoginPage()
    {
        if (loginPanel != null) loginPanel.SetActive(true);
        if (signupPanel != null) signupPanel.SetActive(false);
        if (passwordInput != null) passwordInput.text = string.Empty;
        SetStatus(string.Empty);
    }

    // ──────────────────────────────
    // 登录流程
    // ──────────────────────────────

    /// <summary>
    /// 登录按钮点击处理
    /// 校验输入 → 建立连接 → 发送登录请求（勾选自动登录时随请求要求服务器签发令牌）
    /// </summary>
    private async void OnLoginClicked()
    {
        if (loginInflight) return;
        string username = usernameInput != null ? usernameInput.text.Trim() : string.Empty;
        string password = passwordInput != null ? passwordInput.text : string.Empty;

        if (username.Length == 0 || password.Length == 0)
        {
            SetStatus("请输入用户名和密码");
            return;
        }

        if (ClientNetworkManager.Instance == null) return;

        loginInflight = true;
        tokenLoginAttempted = false;
        // 玩家主动点登录 = 认可这次登录，解除"被顶号后抑制自动登录"的闸门
        ClientNetworkManager.AutoLoginSuppressedByKick = false;
        pendingAccount = username;
        if (!ClientNetworkManager.Instance.IsConnected)
        {
            SetStatus("连接服务器中...");
            bool connected = await ClientNetworkManager.Instance.ConnectAsync(
                ClientNetworkManager.ServerHost, ClientNetworkManager.ServerPort);
            if (!connected)
            {
                loginInflight = false;
                return;
            }
        }

        SetStatus("登录中...");
        _ = ClientNetworkManager.Instance.SendLogin(username, password, IsAutoLoginChecked);
    }

    /// <summary>
    /// 注册按钮点击处理
    /// 隐藏登录面板，显示注册面板
    /// </summary>
    private void OnRegisterClicked()
    {
        if (loginPanel != null) loginPanel.SetActive(false);
        if (signupPanel != null) signupPanel.SetActive(true);
    }

    // ──────────────────────────────
    // 自动登录（令牌换会话）
    // ──────────────────────────────

    /// <summary>
    /// 尝试自动登录方法
    /// 勾选了自动登录且本地存有未过期令牌 → 连服并发令牌登录，成功直进 zhuye；
    /// 失败清本地令牌并回到登录界面（令牌大概率已失效，避免每次启动都白试一次）
    /// </summary>
    private async void TryAutoLogin()
    {
        // 被顶号后本轮不自动登录：TryAutoLogin 读的是 PlayerPrefs 里的令牌，光清内存凭据挡不住，
        // 否则回登录界面就立刻用令牌顶回对面 → 两端无限互踢 + 无限重载场景（玩家手动点登录后解除）
        if (ClientNetworkManager.AutoLoginSuppressedByKick)
        {
            SetStatus("账号在其它设备登录，请手动登录");
            return;
        }

        if (!IsAutoLoginChecked) return;
        string account = PlayerPrefs.GetString(AccountKey, string.Empty);
        string token = PlayerPrefs.GetString(TokenKey, string.Empty);
        long expireMs = long.TryParse(PlayerPrefs.GetString(TokenExpireKey, "0"), out long parsed) ? parsed : 0;

        if (account.Length == 0 || token.Length == 0) return;
        // 本地判断已过期就不再打扰服务器
        if (expireMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            ClearStoredToken();
            SetStatus("自动登录已过期，请重新登录");
            return;
        }

        if (ClientNetworkManager.Instance == null) return;
        loginInflight = true;
        tokenLoginAttempted = true;
        pendingAccount = account;
        SetStatus("自动登录中...");

        if (!ClientNetworkManager.Instance.IsConnected)
        {
            bool connected = await ClientNetworkManager.Instance.ConnectAsync(
                ClientNetworkManager.ServerHost, ClientNetworkManager.ServerPort);
            if (!connected)
            {
                loginInflight = false;
                tokenLoginAttempted = false;
                SetStatus("自动登录失败：无法连接服务器，请手动登录");
                return;
            }
        }
        _ = ClientNetworkManager.Instance.SendTokenLogin(account, token);
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
    /// 登录响应处理（密码登录与令牌登录共用同一响应）
    /// 验证通过 → 按勾选状态保存账号/令牌并跳转 zhuye；失败 → 显示原因（令牌登录失败顺带清掉本地令牌）
    /// </summary>
    private void OnLoginResponse(LoginResponse response)
    {
        loginInflight = false;

        if (response.Success)
        {
            PersistLoginPreferences(response);
            SetStatus($"登录成功！玩家ID:{response.PlayerId}");
            // 先让框架侧收起登录界面（面板实例常驻 DDOL，不关会跟到主城），再切场景
            loginSucceeded?.Invoke();
            SceneLoader.LoadAsync(MainSceneName).Forget();
            return;
        }

        // 令牌登录失败：令牌已失效（过期/被新登录覆盖/伪造），清掉避免下次启动再白试
        // （"账号已在线"冲突的极端情形也会清——随下次密码登录重新签发，不影响正确性）
        if (tokenLoginAttempted)
        {
            ClearStoredToken();
            tokenLoginAttempted = false;
        }
        SetStatus($"登录失败：{response.Message}");
    }

    // ──────────────────────────────
    // 本地偏好（记住账号 / 自动登录）
    // ──────────────────────────────

    /// <summary>「自动登录」复选框当前勾选状态（未接线视为未勾选）</summary>
    private bool IsAutoLoginChecked => autoLoginToggle != null && autoLoginToggle.isOn;

    /// <summary>
    /// 恢复本地登录偏好方法（Start 调用）
    /// 复选框勾选状态 + 预填上次记住的账号
    /// </summary>
    private void RestoreLoginPreferences()
    {
        if (rememberAccountToggle != null)
        {
            rememberAccountToggle.isOn = PlayerPrefs.GetInt(RememberAccountKey, 0) == 1;
        }
        if (autoLoginToggle != null)
        {
            autoLoginToggle.isOn = PlayerPrefs.GetInt(AutoLoginKey, 0) == 1;
        }
        if (!toggleListenersBound)
        {
            // 面板实例由框架缓存复用，onValueChanged 只能挂一次，否则每次打开都会叠加
            if (rememberAccountToggle != null)
                rememberAccountToggle.onValueChanged.AddListener(_ => SaveToggleStates());
            if (autoLoginToggle != null)
                autoLoginToggle.onValueChanged.AddListener(_ => SaveToggleStates());
            toggleListenersBound = true;
        }
        if (rememberAccountToggle != null && rememberAccountToggle.isOn && usernameInput != null)
        {
            string account = PlayerPrefs.GetString(AccountKey, string.Empty);
            if (account.Length > 0) usernameInput.text = account;
        }
    }

    /// <summary>复选框勾选状态即时保存（账号与令牌只在登录成功后保存）</summary>
    private void SaveToggleStates()
    {
        if (rememberAccountToggle != null)
            PlayerPrefs.SetInt(RememberAccountKey, rememberAccountToggle.isOn ? 1 : 0);
        if (autoLoginToggle != null)
            PlayerPrefs.SetInt(AutoLoginKey, autoLoginToggle.isOn ? 1 : 0);
        PlayerPrefs.Save();
    }

    /// <summary>
    /// 登录成功后保存本地偏好方法
    /// 记住账号=存账号名；自动登录=存令牌与过期时间（服务端滑动续期，响应里可能带新过期时间）
    /// </summary>
    /// <param name="response">参数名：response，类型：LoginResponse，用途：登录成功响应（可能携带新签发/续期的令牌）</param>
    private void PersistLoginPreferences(LoginResponse response)
    {
        bool rememberAccount = rememberAccountToggle != null && rememberAccountToggle.isOn;
        bool autoLogin = IsAutoLoginChecked;

        if (rememberAccount && pendingAccount.Length > 0)
            PlayerPrefs.SetString(AccountKey, pendingAccount);
        else
            PlayerPrefs.DeleteKey(AccountKey);

        // 勾了自动登录且响应带令牌（新签发或滑动续期）→ 更新本地保存；否则清掉（取消勾选后下次不再自动登录）
        if (autoLogin && response.AutoLoginToken.Length > 0)
        {
            PlayerPrefs.SetString(TokenKey, response.AutoLoginToken);
            PlayerPrefs.SetString(TokenExpireKey, response.AutoLoginTokenExpireMs.ToString());
        }
        else
        {
            ClearStoredToken();
        }

        SaveToggleStates();
    }

    /// <summary>清掉本地保存的自动登录令牌</summary>
    private void ClearStoredToken()
    {
        PlayerPrefs.DeleteKey(TokenKey);
        PlayerPrefs.DeleteKey(TokenExpireKey);
        PlayerPrefs.Save();
    }

    /// <summary>
    /// 退出登录用：清掉本地自动登录凭据（令牌必须清，账号名默认保留方便下次输入）。
    /// </summary>
    public static void ClearStoredLogin(bool clearAccountName = false)
    {
        PlayerPrefs.DeleteKey(TokenKey);
        PlayerPrefs.DeleteKey(TokenExpireKey);
        if (clearAccountName) PlayerPrefs.DeleteKey(AccountKey);
        PlayerPrefs.Save();
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
        Debug.Log($"[登录]{message}");
    }
}
