using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 场景加载遮罩（程序化构建，无美术依赖，不依赖 UIManager）。
/// 黑底 + 提示文字 + 进度条，全屏且带 GraphicRaycaster，加载期间天然挡住点击。
/// 常驻 DDOL，加载结束时自毁。
/// </summary>
public class LoadingScreen : MonoBehaviour
{
    /// <summary>加载完成后至少停留这么久，避免进度条一闪而过（0.35 太快，看不清在加载什么）</summary>
    private const float MinimumStay = 1f;

    /// <summary>淡入/淡出时长</summary>
    private const float FadeDuration = 0.22f;

    private CanvasGroup canvasGroup;
    private Image fillImage;
    private TMP_Text percentText;
    private float showTime;
    private bool hiding;

    /// <summary>创建并显示遮罩（调用方不需要手动 Destroy）</summary>
    public static LoadingScreen Show(string sceneName)
    {
        GameObject root = new GameObject("LoadingScreen");
        DontDestroyOnLoad(root);

        LoadingScreen screen = root.AddComponent<LoadingScreen>();
        screen.Build(sceneName);
        return screen;
    }

    /// <summary>设置进度（0~1），同步刷新进度条与百分比</summary>
    public void SetProgress(float value)
    {
        value = Mathf.Clamp01(value);

        if (fillImage != null)
            fillImage.fillAmount = value;

        if (percentText != null)
            percentText.text = Mathf.FloorToInt(value * 100f) + "%";
    }

    /// <summary>保证遮罩至少停留 MinimumStay 秒</summary>
    public async UniTask WaitMinimumStayAsync()
    {
        await UniTask.WaitUntil(() => Time.unscaledTime - showTime >= MinimumStay);
    }

    /// <summary>淡出并在结束时销毁自己</summary>
    public async UniTask HideAsync()
    {
        hiding = true;

        await UniTask.WaitUntil(
            () => this == null || canvasGroup == null || canvasGroup.alpha <= 0.01f);
    }

    private void Build(string sceneName)
    {
        canvasGroup = gameObject.AddComponent<CanvasGroup>();
        canvasGroup.alpha = 0f;

        Canvas canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32760;   // 高于所有业务面板，低于系统级提示

        CanvasScaler scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        gameObject.AddComponent<GraphicRaycaster>();

        // 全屏黑底（顺带吃掉所有点击）
        Image background = CreateImage(transform, "Background", Color.black);
        Stretch(background.rectTransform);

        // 提示文字
        TMP_Text tip = CreateText(transform, "Tip", new Vector2(0f, 40f), new Vector2(1200f, 60f), 34f);
        tip.text = "正在进入 " + SceneDisplayName(sceneName) + " …";
        tip.color = new Color(0.92f, 0.92f, 0.92f, 1f);

        // 进度条底槽
        Image barBackground = CreateImage(transform, "BarBackground", new Color(1f, 1f, 1f, 0.14f));
        barBackground.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        barBackground.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        barBackground.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        barBackground.rectTransform.anchoredPosition = new Vector2(0f, -40f);
        barBackground.rectTransform.sizeDelta = new Vector2(640f, 8f);

        // 进度条填充
        Image barFill = CreateImage(barBackground.transform, "BarFill", new Color(0.95f, 0.82f, 0.45f, 1f));
        barFill.type = Image.Type.Filled;
        barFill.fillMethod = Image.FillMethod.Horizontal;
        barFill.fillOrigin = 0;
        barFill.fillAmount = 0f;
        Stretch(barFill.rectTransform);
        fillImage = barFill;

        // 百分比
        percentText = CreateText(transform, "Percent", new Vector2(0f, -96f), new Vector2(300f, 40f), 22f);
        percentText.text = "0%";
        percentText.color = new Color(0.75f, 0.75f, 0.75f, 1f);

        showTime = Time.unscaledTime;
    }

    private void Update()
    {
        if (canvasGroup == null)
            return;

        float target = hiding ? 0f : 1f;
        canvasGroup.alpha = Mathf.MoveTowards(canvasGroup.alpha, target,
                                              Time.unscaledDeltaTime / FadeDuration);

        if (hiding && canvasGroup.alpha <= 0f)
            Destroy(gameObject);
    }

    private static Image CreateImage(Transform parent, string name, Color color)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);

        Image image = go.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = true;   // 挡住加载期间的点击
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
        // 程序化 TMP 默认是 LiberationSans（无中文字形，会变方块），这里换成项目的中文字体
        ChineseFontLocator.Apply(text);
        return text;
    }

    /// <summary>场景名 → 显示名（加载界面是给玩家看的，不能直接暴露 zhuye/dashijie 这种工程名）</summary>
    private static string SceneDisplayName(string sceneName) => sceneName switch
    {
        "LOGIN" => "登录",
        "zhuye" => "主城",
        "dashijie" => "大世界",
        "tiaozhan" => "挑战",
        "multiplecompete" => "联机对战",
        "fog_hunt" => "雾中寻踪",
        _ => sceneName,
    };

    private static void Stretch(RectTransform rect)    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }
}
