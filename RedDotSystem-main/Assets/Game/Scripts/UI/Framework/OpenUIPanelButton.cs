using Cysharp.Threading.Tasks;
using SkierFramework;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 通用「点击打开 UI 面板」按钮：挂在按钮物体上，在 Inspector 选 uiType 即可。
/// 用于把旧场景入口按钮从手写控制器接线迁到框架（P8 各面板通用）。
/// </summary>
[RequireComponent(typeof(Button))]
public class OpenUIPanelButton : MonoBehaviour
{
    [Tooltip("要打开的面板；Max 表示未配置（点击无反应）")]
    [SerializeField] private UIType uiType = UIType.Max;

    private Button button;

    private void Awake()
    {
        button = GetComponent<Button>();
        if (button != null) button.onClick.AddListener(OnClicked);
    }

    private void OnDestroy()
    {
        if (button != null) button.onClick.RemoveListener(OnClicked);
    }

    private void OnClicked()
    {
        if (uiType == UIType.Max)
        {
            Debug.LogWarning($"[OpenUIPanelButton] {name} 未配置 uiType，点击被忽略");
            return;
        }
        UIManager.Instance.Open(uiType).Forget();
    }

    /// <summary>运行时改目标（测试/动态入口用）</summary>
    public void SetUIType(UIType type) => uiType = type;
}
