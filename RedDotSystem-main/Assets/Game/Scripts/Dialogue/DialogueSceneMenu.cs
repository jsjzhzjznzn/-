using Cysharp.Threading.Tasks;
using SkierFramework;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 对话场景的菜单入口：HUD「设置」按钮与 Esc 都走这里。
///
/// 与主页的区别：这里打开设置要**暂停世界**（PauseManager.Layer.Settings → Time.timeScale=0），
/// 并且设置面板要显示「返回主页」（回到 zhuye）。这些通过 SettingsViewArgs 传给 SettingsView，
/// 主页面板不传参 → 不暂停、不显示返回主页，行为不变。
/// </summary>
public class DialogueSceneMenu : MonoBehaviour
{
    [Header("入口")]
    [SerializeField] private Button settingsButton;
    [SerializeField] private KeyCode settingsKey = KeyCode.Escape;

    /// <summary>开合在途守卫，避免连点/按键重复触发</summary>
    private bool busy;

    private void OnEnable()
    {
        if (settingsButton != null) settingsButton.onClick.AddListener(ToggleSettings);
    }

    private void OnDisable()
    {
        if (settingsButton != null) settingsButton.onClick.RemoveListener(ToggleSettings);
    }

    private void Update()
    {
        if (Input.GetKeyDown(settingsKey)) ToggleSettings();
    }

    public void ToggleSettings()
    {
        if (busy) return;
        if (UIManager.Instance == null) return;

        if (UIManager.Instance.IsOpen(UIType.SettingsView))
            CloseSettingsAsync().Forget();
        else
            OpenSettingsAsync().Forget();
    }

    private async UniTaskVoid OpenSettingsAsync()
    {
        busy = true;
        try
        {
            await UIManager.Instance.Open(UIType.SettingsView, new SettingsViewArgs
            {
                pauseWorld = true,
                showReturnHome = true,
            });
        }
        finally
        {
            busy = false;
        }
    }

    private async UniTaskVoid CloseSettingsAsync()
    {
        busy = true;
        try
        {
            await UIManager.Instance.Close(UIType.SettingsView);
        }
        finally
        {
            busy = false;
        }
    }
}
