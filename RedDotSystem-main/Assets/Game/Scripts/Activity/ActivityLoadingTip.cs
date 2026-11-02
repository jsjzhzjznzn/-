using SkierFramework;
using TMPro;

using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 活动加载提示（固定层，极简版）：点入口之后、热更层起来之前，用户得有个可见交代。
/// 覆盖三种情况：
///   ① 还没登录就点了 → "请先登录后再打开活动"
///   ② 资源包初始化/增量下载中 → "活动加载中…"（这段时间以前完全没反馈，看起来像点了没反应）
///   ③ 打开失败 → 失败原因（自动消失）
/// 与活动面板同为程序化 UI，但画布 sortingOrder 更高一层；面板就绪后隐藏。
/// </summary>
public class ActivityLoadingTip : MonoBehaviour
{
    // 盖住所有面板层（活动面板已登记在 NormalLayer），但不盖黑幕：TipLayer(6000) 之上、BlackMaskLayer(7000) 之下
    private const int SortingOrder = (int)UILayer.TipLayer + 500;
    private const string LoadingMessage = "活动加载中…";

    private static ActivityLoadingTip instance;

    private Canvas canvas;
    private CanvasGroup group;
    private TextMeshProUGUI label;
    private float hideAt = -1f;

    /// <summary>由 ActivityShell 自举时同挂一个物体（保证与壳同生命周期）</summary>
    public static ActivityLoadingTip Instance => instance;

    private void Awake()
    {
        instance = this;
    }

    private void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    /// <summary>显示一条提示（autoHideSeconds &gt; 0 时到点自动淡出；否则一直显示到 Hide）</summary>
    public static void Show(string message, float autoHideSeconds = 0f)
    {
        if (instance == null)
        {
            Debug.LogWarning($"[活动] 加载提示未自举：{message}");
            return;
        }
        instance.ShowInternal(message, autoHideSeconds);
    }

    /// <summary>隐藏提示</summary>
    public static void Hide()
    {
        instance?.HideInternal();
    }

    /// <summary>当前是否正在显示"加载中"（用于防重复建提示）</summary>
    public static bool IsShowingLoading => instance != null && instance.label != null
        && instance.label.text == LoadingMessage && instance.group != null && instance.group.alpha > 0f;

    // ──────────────────────────────
    // 内部
    // ──────────────────────────────

    private void ShowInternal(string message, float autoHideSeconds)
    {
        EnsureBuilt();
        label.text = message;
        group.alpha = 1f;
        gameObject.SetActive(true);
        hideAt = autoHideSeconds > 0f ? Time.unscaledTime + autoHideSeconds : -1f;
    }

    private void HideInternal()
    {
        hideAt = -1f;
        if (group != null) group.alpha = 0f;
    }

    private void Update()
    {
        // 淡出（不删物体，复用即可）
        if (group != null && group.alpha > 0f)
        {
            if (hideAt > 0f && Time.unscaledTime >= hideAt)
            {
                group.alpha = Mathf.MoveTowards(group.alpha, 0f, Time.unscaledDeltaTime * 3f);
            }
        }
    }

    private void EnsureBuilt()
    {
        if (canvas != null) return;

        GameObject go = new GameObject("ActivityLoadingTipCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(CanvasGroup));
        go.transform.SetParent(transform, false);
        go.layer = LayerMask.NameToLayer("UI");

        canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = SortingOrder;

        CanvasScaler scaler = go.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        group = go.GetComponent<CanvasGroup>();
        group.blocksRaycasts = false;          // 提示不吃点击
        group.interactable = false;

        Image background = ActivityUi.NewImage(canvas.transform, "Bg");
        ActivityUi.SetRounded(background, 16);
        background.color = ActivityUi.Hex("#0C141B", 0.86f);
        ActivityUi.Place(background.rectTransform, 0.5f, 0.5f, 0f, -320f, 560f, 88f);

        label = ActivityUi.NewLabel(background.rectTransform, "Text", LoadingMessage, 28,
            ActivityUi.Hex("#E6EDF3"), TextAlignmentOptions.Center);
        ActivityUi.Stretch(label.rectTransform, 16f, 8f, 16f, 8f);

        group.alpha = 0f;
    }
}
