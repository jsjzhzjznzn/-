using Cysharp.Threading.Tasks;
using SkierFramework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 设置面板打开参数。
/// 主页打开时不需要暂停世界（纯 UI），对话/副本等有 3D 世界的场景才传 pauseWorld=true 并显示「返回主页」。
/// </summary>
public sealed class SettingsViewArgs
{
    /// <summary>打开期间 PauseManager 计一层 Settings（Time.timeScale=0）</summary>
    public bool pauseWorld;

    /// <summary>显示「返回主页」按钮（回到 zhuye 场景）</summary>
    public bool showReturnHome;
}

/// <summary>
/// 设置面板：音量（主/音乐/音效/语音 + 静音）、全屏、退出登录、退出游戏。
/// 所有取值/持久化都在 <see cref="GameSettings"/> 里，本类只做界面与联动。
/// </summary>
public class SettingsView : UIView
{
    [ControlBinding] public Slider masterSlider;
    [ControlBinding] public TextMeshProUGUI masterValueText;
    [ControlBinding] public Slider musicSlider;
    [ControlBinding] public TextMeshProUGUI musicValueText;
    [ControlBinding] public Slider sfxSlider;
    [ControlBinding] public TextMeshProUGUI sfxValueText;
    [ControlBinding] public Slider voiceSlider;
    [ControlBinding] public TextMeshProUGUI voiceValueText;

    [ControlBinding] public Toggle muteToggle;

    [ControlBinding] public Toggle fullscreenToggle;

    [ControlBinding] public Button logoutButton;
    [ControlBinding] public Button quitButton;
    [ControlBinding] public Button closeButton;
    /// <summary>重看新手引导（主页可用；关面板后从第一步重新开始）</summary>
    [ControlBinding] public Button replayGuideButton;
    /// <summary>回到主页（只在带 3D 世界的场景显示，由 SettingsViewArgs.showReturnHome 控制）</summary>
    [ControlBinding] public Button returnHomeButton;

    [ControlBinding] public GameObject logoutConfirmRoot;
    [ControlBinding] public Button logoutConfirmButton;
    [ControlBinding] public Button logoutCancelButton;

    [ControlBinding] public TextMeshProUGUI versionText;

    /// <summary>界面回填时置 true，避免 Slider.onValueChanged 反过来再写设置</summary>
    private bool _syncing;

    /// <summary>弹窗遮罩上的点击区（点空白关闭），见 <see cref="EnsureBackdropClick"/></summary>
    private Button _backdropButton;

    /// <summary>本次打开是否由本面板计了暂停层（关闭时只回滚自己加的这层）</summary>
    private bool _pauseApplied;

    public override void OnOpen(object userData)
    {
        base.OnOpen(userData);

        var args = userData as SettingsViewArgs;
        bool pauseWorld = args != null && args.pauseWorld;
        bool showReturnHome = args != null && args.showReturnHome;

        if (returnHomeButton != null) returnHomeButton.gameObject.SetActive(showReturnHome);

        // 暂停世界（对话场景需求）：Push 幂等，重复打开不会叠层；关闭时只弹自己这层
        if (pauseWorld && !_pauseApplied)
        {
            PauseManager.Push(PauseManager.Layer.Settings);
            _pauseApplied = true;
        }

        EnsureBackdropClick();
        if (logoutConfirmRoot != null) logoutConfirmRoot.SetActive(false);
        if (versionText != null)
        {
            versionText.text = string.IsNullOrEmpty(Application.version) ? "开发版" : "v" + Application.version;
        }
        RefreshFromSettings();
    }

    public override void OnClose()
    {
        // 先恢复时间再走基类（基类会摘监听/还焦点）
        if (_pauseApplied)
        {
            PauseManager.Pop(PauseManager.Layer.Settings);
            _pauseApplied = false;
        }
        base.OnClose();
    }

    public override void OnAddListener()
    {
        if (masterSlider != null) masterSlider.onValueChanged.AddListener(OnMasterChanged);
        if (musicSlider != null) musicSlider.onValueChanged.AddListener(OnMusicChanged);
        if (sfxSlider != null) sfxSlider.onValueChanged.AddListener(OnSfxChanged);
        if (voiceSlider != null) voiceSlider.onValueChanged.AddListener(OnVoiceChanged);
        if (muteToggle != null) muteToggle.onValueChanged.AddListener(OnMuteChanged);
        if (fullscreenToggle != null) fullscreenToggle.onValueChanged.AddListener(OnFullscreenChanged);
        if (logoutButton != null) logoutButton.onClick.AddListener(OnLogoutClicked);
        if (quitButton != null) quitButton.onClick.AddListener(OnQuitClicked);
        if (closeButton != null) closeButton.onClick.AddListener(OnCloseClicked);
        if (replayGuideButton != null) replayGuideButton.onClick.AddListener(OnReplayGuideClicked);
        if (returnHomeButton != null) returnHomeButton.onClick.AddListener(OnReturnHomeClicked);
        if (logoutConfirmButton != null) logoutConfirmButton.onClick.AddListener(OnLogoutConfirmed);
        if (logoutCancelButton != null) logoutCancelButton.onClick.AddListener(OnLogoutCancelled);

        GameSettings.Changed += RefreshFromSettings;
    }

    public override void OnRemoveListener()
    {
        if (masterSlider != null) masterSlider.onValueChanged.RemoveListener(OnMasterChanged);
        if (musicSlider != null) musicSlider.onValueChanged.RemoveListener(OnMusicChanged);
        if (sfxSlider != null) sfxSlider.onValueChanged.RemoveListener(OnSfxChanged);
        if (voiceSlider != null) voiceSlider.onValueChanged.RemoveListener(OnVoiceChanged);
        if (muteToggle != null) muteToggle.onValueChanged.RemoveListener(OnMuteChanged);
        if (fullscreenToggle != null) fullscreenToggle.onValueChanged.RemoveListener(OnFullscreenChanged);
        if (logoutButton != null) logoutButton.onClick.RemoveListener(OnLogoutClicked);
        if (quitButton != null) quitButton.onClick.RemoveListener(OnQuitClicked);
        if (closeButton != null) closeButton.onClick.RemoveListener(OnCloseClicked);
        if (replayGuideButton != null) replayGuideButton.onClick.RemoveListener(OnReplayGuideClicked);
        if (returnHomeButton != null) returnHomeButton.onClick.RemoveListener(OnReturnHomeClicked);
        if (logoutConfirmButton != null) logoutConfirmButton.onClick.RemoveListener(OnLogoutConfirmed);
        if (logoutCancelButton != null) logoutCancelButton.onClick.RemoveListener(OnLogoutCancelled);

        GameSettings.Changed -= RefreshFromSettings;
    }

    /// <summary>按当前设置刷新界面（滑条/开关/数值文本）</summary>
    private void RefreshFromSettings()
    {
        _syncing = true;
        try
        {
            if (masterSlider != null) masterSlider.SetValueWithoutNotify(GameSettings.MasterVolume);
            if (musicSlider != null) musicSlider.SetValueWithoutNotify(GameSettings.MusicVolume);
            if (sfxSlider != null) sfxSlider.SetValueWithoutNotify(GameSettings.SfxVolume);
            if (voiceSlider != null) voiceSlider.SetValueWithoutNotify(GameSettings.VoiceVolume);

            if (masterValueText != null) masterValueText.text = ToPercent(GameSettings.MasterVolume);
            if (musicValueText != null) musicValueText.text = ToPercent(GameSettings.MusicVolume);
            if (sfxValueText != null) sfxValueText.text = ToPercent(GameSettings.SfxVolume);
            if (voiceValueText != null) voiceValueText.text = ToPercent(GameSettings.VoiceVolume);

            if (muteToggle != null) muteToggle.SetIsOnWithoutNotify(GameSettings.Muted);
            if (fullscreenToggle != null) fullscreenToggle.SetIsOnWithoutNotify(GameSettings.Fullscreen);
        }
        finally
        {
            _syncing = false;
        }
    }

    private static string ToPercent(float value) => Mathf.RoundToInt(value * 100f) + "%";

    private void OnMasterChanged(float value) { if (!_syncing) GameSettings.SetMasterVolume(value); }
    private void OnMusicChanged(float value) { if (!_syncing) GameSettings.SetMusicVolume(value); }
    private void OnSfxChanged(float value) { if (!_syncing) GameSettings.SetSfxVolume(value); }
    private void OnVoiceChanged(float value) { if (!_syncing) GameSettings.SetVoiceVolume(value); }
    private void OnMuteChanged(bool value) { if (!_syncing) GameSettings.SetMuted(value); }
    private void OnFullscreenChanged(bool value) { if (!_syncing) GameSettings.SetFullscreen(value); }

    private void OnCloseClicked()
    {
        if (UIManager.Instance != null) UIManager.Instance.Close(UIType.SettingsView).Forget();
    }

    /// <summary>
    /// 重看新手引导：先关面板（引导要盖在主页上），再从第一步重新开始。
    /// 引导进度会覆盖服务端为 0，走得中途退出也仍然从断点继续。
    /// </summary>
    private async void OnReplayGuideClicked()
    {
        if (UIManager.Instance != null)
            await UIManager.Instance.Close(UIType.SettingsView);

        GuideManager.ReplayFromStart();
    }

    /// <summary>
    /// 返回主页：先关面板（OnClose 里恢复 Time.timeScale），再走统一场景加载（带进度条）。
    /// 顺序不能反——暂停中直接 LoadScene 会把 0 倍速带进主页。
    /// </summary>
    private async void OnReturnHomeClicked()
    {
        // 先落一次场景存档（对话进度/位置/相机），再关面板与切场景。
        // Flush 自带 1s 超时，服务端不可用时不会卡住返回主页。
        await FlushSceneSave();

        if (UIManager.Instance != null)
            await UIManager.Instance.Close(UIType.SettingsView);

        await SceneLoader.LoadAsync("zhuye");
    }

    /// <summary>
    /// 弹窗模式（UIConfig.isWindow=true）下主页仍可见，遮罩就是本弹窗的空白区：
    /// 点遮罩关闭。Button 运行时补挂（预制体只序列化了 Image），不写进资产。
    /// </summary>
    private void EnsureBackdropClick()
    {
        if (_backdropButton != null) return;
        Transform backdrop = transform.Find("Backdrop");
        if (backdrop == null) return;
        _backdropButton = backdrop.GetComponent<Button>();
        if (_backdropButton == null) _backdropButton = backdrop.gameObject.AddComponent<Button>();
        _backdropButton.transition = Selectable.Transition.None;
        _backdropButton.onClick.AddListener(OnCloseClicked);
    }

    private void OnLogoutClicked()
    {
        if (logoutConfirmRoot != null) logoutConfirmRoot.SetActive(true);
    }

    private void OnLogoutCancelled()
    {
        if (logoutConfirmRoot != null) logoutConfirmRoot.SetActive(false);
    }

    private void OnLogoutConfirmed()
    {
        LogoutService.Logout();
    }

    private async void OnQuitClicked()
    {
        // 退出前尽力落一次存档（best-effort：真正的保证来自事件点即时上报）
        await FlushSceneSave();

#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    /// <summary>
    /// 场景存档落库（有存档服务时等待送达，超时/未连接直接放行）
    /// </summary>
    private static async System.Threading.Tasks.Task FlushSceneSave()
    {
        if (SceneSaveService.Instance == null) return;

        try
        {
            await SceneSaveService.Instance.FlushAsync(1f);
        }
        catch (System.Exception exception)
        {
            UnityEngine.Debug.LogWarning($"[Settings] 场景存档落库失败（继续退出/返回）：{exception.Message}");
        }
    }
}
