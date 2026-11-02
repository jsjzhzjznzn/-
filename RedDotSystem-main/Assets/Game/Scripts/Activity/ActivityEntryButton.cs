using Cysharp.Threading.Tasks;
using SkierFramework;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 老的"活动"入口按钮组件（zhuye 场景 huodong Canvas 的 ActivityEntryButton）。
/// 主页入口已改由 HomeView 走 UIManager；本组件保留给直进场景的联调/E2E，
/// 框架已初始化时同样走登记好的活动视图。
/// </summary>
public class ActivityEntryButton : MonoBehaviour
{
    private void Start()
    {
        Button button = GetComponent<Button>();
        if (button != null)
        {
            button.onClick.AddListener(OpenActivity);
        }
    }

    /// <summary>打开活动（onClick 运行时接线；E2E 也可直调）</summary>
    public void OpenActivity()
    {
        UIManager ui = UIManager.Instance;
        // 框架已初始化（主页打开过）→ 走 NormalLayer 的活动视图；否则退回热更外壳自带画布
        if (ui != null && ui.GetLayerCanvas(UILayer.NormalLayer) != null)
        {
            ui.Open(UIType.ActivityView).Forget();
            return;
        }

        if (ActivityShell.Instance != null) ActivityShell.Instance.OpenActivity();
    }
}
