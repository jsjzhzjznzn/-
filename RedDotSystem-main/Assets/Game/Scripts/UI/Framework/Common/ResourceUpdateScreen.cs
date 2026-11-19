using System;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 启动期资源热更界面（程序化构建，无美术依赖，不依赖 UIManager）。
///
/// 进入游戏后先检查热更：有待下载内容时显示黑底遮罩与进度条，
/// 下载完成后自动淡出销毁，调用方继续进入登录界面；
/// 无更新时 <see cref="RunAsync"/> 立即返回，不出现任何界面。
///
/// 与 LoadingScreen 的区别：那个是场景切换遮罩，这个是"启动期下载"，
/// 需要展示文件数与字节数，因此额外提供状态行。
/// </summary>
public class ResourceUpdateScreen : MonoBehaviour
{
    private const float FadeDuration = 0.22f;

    private CanvasGroup canvasGroup;
    private Image fillImage;
    private TMP_Text percentText;
    private TMP_Text statusText;
    private bool hiding;

    /// <summary>
    /// 执行一次启动期热更：有更新才显示界面，完成后隐藏。
    /// 失败不阻断进入游戏，只记录错误由调用方决定提示方式。
    /// </summary>
    /// <param name="checkAndDownload">检查并下载；进度 0~1，状态文案</param>
    public static async UniTask<bool> RunAsync(
        Func<Action<float, string>, UniTask<bool>> checkAndDownload)
    {
        ResourceUpdateScreen screen = null;
        bool shown = false;

        bool ok = await checkAndDownload((progress, status) =>
        {
            if (!shown)
            {
                // 首次回调才开始显示：无更新时界面完全不出现
                shown = true;
                screen = Show();
            }
            screen.Apply(progress, status);
        });

        if (shown && screen != null)
            await screen.HideAsync();

        return ok;
    }

    private static ResourceUpdateScreen Show()
    {
        GameObject root = new GameObject("ResourceUpdateScreen");
        DontDestroyOnLoad(root);

        ResourceUpdateScreen screen = root.AddComponent<ResourceUpdateScreen>();
        screen.Build();
        return screen;
    }

    private void Apply(float progress, string status)
    {
        progress = Mathf.Clamp01(progress);

        if (fillImage != null)
            fillImage.fillAmount = progress;

        if (percentText != null)
            percentText.text = Mathf.FloorToInt(progress * 100f) + "%";
    }

    private async UniTask HideAsync()
    {
        hiding = true;

        await UniTask.WaitUntil(
            () => this == null || canvasGroup == null || canvasGroup.alpha <= 0.01f);
    }

    private void Build()
    {
        canvasGroup = gameObject.AddComponent<CanvasGroup>();
        // 与 LoadingScreen 一致：创建即全量可见，不靠帧率驱动的淡入
        canvasGroup.alpha = 1f;

        Canvas canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32770;   // 高于场景加载遮罩(32760)，启动期下载在最上层

        CanvasScaler scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        gameObject.AddComponent<GraphicRaycaster>();

        Image background = CreateImage(transform, "Background", Color.black);
        Stretch(background.rectTransform);

        TMP_Text tip = CreateText(transform, "Tip", new Vector2(0f, 72f), new Vector2(1200f, 60f), 34f);
        tip.text = "正在更新资源…";
        tip.color = new Color(0.92f, 0.92f, 0.92f, 1f);

        Image barBackground = CreateImage(transform, "BarBackground", new Color(1f, 1f, 1f, 0.14f));
        barBackground.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        barBackground.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        barBackground.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        barBackground.rectTransform.anchoredPosition = new Vector2(0f, 0f);
        barBackground.rectTransform.sizeDelta = new Vector2(640f, 8f);

        Image barFill = CreateImage(barBackground.transform, "BarFill", new Color(0.95f, 0.82f, 0.45f, 1f));
        barFill.type = Image.Type.Filled;
        barFill.fillMethod = Image.FillMethod.Horizontal;
        barFill.fillOrigin = 0;
        barFill.fillAmount = 0f;
        Stretch(barFill.rectTransform);
        fillImage = barFill;

        percentText = CreateText(transform, "Percent", new Vector2(0f, -48f), new Vector2(300f, 40f), 22f);
        percentText.text = "0%";
        percentText.color = new Color(0.75f, 0.75f, 0.75f, 1f);

        statusText = CreateText(transform, "Status", new Vector2(0f, -96f), new Vector2(1200f, 40f), 20f);
        statusText.text = "首次启动或版本更新时需要下载资源，请保持网络畅通";
        statusText.color = new Color(0.60f, 0.60f, 0.60f, 1f);
    }

    private void Update()
    {
        if (!hiding || canvasGroup == null)
            return;

        canvasGroup.alpha = Mathf.MoveTowards(canvasGroup.alpha, 0f,
                                              Time.unscaledDeltaTime / FadeDuration);

        if (canvasGroup.alpha <= 0f)
            Destroy(gameObject);
    }

    private static Image CreateImage(Transform parent, string name, Color color)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);

        Image image = go.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = true;   // 下载期间挡住点击
        return image;
    }

    private static TMP_Text CreateText(Transform parent, string name, Vector2 anchoredPosition,
                                       Vector2 size, float fontSize)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);

        RectTransform rect = (RectTransform)go.transform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = size;

        TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
        text.fontSize = fontSize;
        text.alignment = TextAlignmentOptions.Center;
        text.raycastTarget = false;
        ChineseFontLocator.Apply(text);
        return text;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }
}
