using System.Collections.Generic;

using Common.Network;

using TMPro;

using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 成就解锁横幅（独立画布，不依赖任何场景）
/// 服务端 3405 推送到达时逐条展示"成就解锁 · 名称 +N 成就点"，1.6 秒/条，队列播放。
/// 只做表现：不参与任何业务判断；raycastTarget=false，不挡点击。
/// </summary>
public class AchievementToast : MonoBehaviour
{
    private const int SortingOrder = 320;
    private const float BannerWidth = 420f;
    private const float BannerHeight = 74f;

    public static AchievementToast Instance { get; private set; }

    private readonly Queue<AchievementUnlockedInfo> queue = new();
    private RectTransform root;
    private bool playing;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance != null) return;
        GameObject holder = new GameObject("AchievementToast");
        holder.AddComponent<AchievementToast>();
        DontDestroyOnLoad(holder);
    }

    private void Awake()
    {
        if (Instance != null)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        BuildCanvas();
    }

    private void Start()
    {
        if (AchievementManager.Instance != null)
        {
            AchievementManager.Instance.Unlocked += OnUnlocked;
        }
    }

    private void OnDestroy()
    {
        if (AchievementManager.Instance != null)
        {
            AchievementManager.Instance.Unlocked -= OnUnlocked;
        }
    }

    private void OnUnlocked(AchievementUnlockedNotify notify)
    {
        if (notify == null) return;
        foreach (AchievementUnlockedInfo item in notify.Items)
        {
            queue.Enqueue(item);
        }
        if (!playing) StartCoroutine(PlayQueue());
    }

    private System.Collections.IEnumerator PlayQueue()
    {
        playing = true;
        while (queue.Count > 0)
        {
            AchievementUnlockedInfo item = queue.Dequeue();
            yield return ShowOne(item);
            yield return new WaitForSecondsRealtime(0.18f);
        }
        playing = false;
    }

    private System.Collections.IEnumerator ShowOne(AchievementUnlockedInfo item)
    {
        GameObject banner = new GameObject("AchievementUnlockBanner", typeof(RectTransform));
        RectTransform rect = banner.GetComponent<RectTransform>();
        rect.SetParent(root, false);
        rect.anchorMin = new Vector2(0.5f, 1f);
        rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.anchoredPosition = new Vector2(0f, 76f);
        rect.sizeDelta = new Vector2(BannerWidth, BannerHeight);

        EnsureCanvasRenderer(banner);
        Image background = banner.AddComponent<Image>();
        background.color = new Color(0.10f, 0.11f, 0.13f, 0.94f);
        background.raycastTarget = false;

        // 稀有度色条
        GameObject stripeObj = new GameObject("Stripe", typeof(RectTransform));
        RectTransform stripe = stripeObj.GetComponent<RectTransform>();
        stripe.SetParent(rect, false);
        stripe.anchorMin = new Vector2(0f, 0f);
        stripe.anchorMax = new Vector2(0f, 1f);
        stripe.pivot = new Vector2(0f, 0.5f);
        stripe.sizeDelta = new Vector2(5f, 0f);
        EnsureCanvasRenderer(stripeObj);
        Image stripeImage = stripeObj.AddComponent<Image>();
        stripeImage.color = RarityColor(item.Rarity);
        stripeImage.raycastTarget = false;

        // 标题
        GameObject titleObj = new GameObject("Title", typeof(RectTransform));
        RectTransform titleRect = titleObj.GetComponent<RectTransform>();
        titleRect.SetParent(rect, false);
        titleRect.anchorMin = new Vector2(0f, 1f);
        titleRect.anchorMax = new Vector2(1f, 1f);
        titleRect.pivot = new Vector2(0.5f, 1f);
        titleRect.offsetMin = new Vector2(20f, -34f);
        titleRect.offsetMax = new Vector2(-16f, -8f);
        EnsureCanvasRenderer(titleObj);
        TextMeshProUGUI title = titleObj.AddComponent<TextMeshProUGUI>();
        title.fontSize = 18f;
        title.color = new Color(0.95f, 0.90f, 0.76f, 1f);
        title.alignment = TextAlignmentOptions.Left;
        title.raycastTarget = false;
        title.SetText("成就解锁 · " + item.Name);

        // 成就点
        GameObject pointsObj = new GameObject("Points", typeof(RectTransform));
        RectTransform pointsRect = pointsObj.GetComponent<RectTransform>();
        pointsRect.SetParent(rect, false);
        pointsRect.anchorMin = new Vector2(0f, 0f);
        pointsRect.anchorMax = new Vector2(1f, 0f);
        pointsRect.pivot = new Vector2(0.5f, 0f);
        pointsRect.offsetMin = new Vector2(20f, 8f);
        pointsRect.offsetMax = new Vector2(-16f, 30f);
        EnsureCanvasRenderer(pointsObj);
        TextMeshProUGUI points = pointsObj.AddComponent<TextMeshProUGUI>();
        points.fontSize = 15f;
        points.color = RarityColor(item.Rarity);
        points.alignment = TextAlignmentOptions.Left;
        points.raycastTarget = false;
        points.SetText($"+{item.Points} 成就点");

        CanvasGroup group = banner.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        group.blocksRaycasts = false;

        const float fade = 0.18f;
        for (float t = 0f; t < fade; t += Time.unscaledDeltaTime)
        {
            group.alpha = Mathf.Clamp01(t / fade);
            rect.anchoredPosition = new Vector2(0f, Mathf.Lerp(96f, 76f, group.alpha));
            yield return null;
        }
        group.alpha = 1f;
        rect.anchoredPosition = new Vector2(0f, 76f);

        yield return new WaitForSecondsRealtime(1.1f);

        for (float t = 0f; t < fade; t += Time.unscaledDeltaTime)
        {
            group.alpha = 1f - Mathf.Clamp01(t / fade);
            yield return null;
        }

        Destroy(banner);
    }

    private void BuildCanvas()
    {
        GameObject canvasObj = new GameObject("AchievementToastCanvas");
        canvasObj.transform.SetParent(transform, false);

        Canvas canvas = canvasObj.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = SortingOrder;

        CanvasScaler scaler = canvasObj.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        root = canvasObj.GetComponent<RectTransform>();
        root.anchorMin = Vector2.zero;
        root.anchorMax = Vector2.one;
        root.offsetMin = Vector2.zero;
        root.offsetMax = Vector2.zero;

        // 与项目 UI 相机体系对齐（Overlay → ScreenSpaceCamera；失败时保持 Overlay 也能显示）
        Camera uiCamera = UICameraService.EnsureUICamera();
        if (uiCamera != null)
        {
            UICameraService.ConvertCanvas(canvas, uiCamera);
        }
        UICameraService.SetLayerRecursive(canvasObj.transform, UICameraService.UiLayer);
    }

    /// <summary>Tuanjie 的 uGUI 分叉里 Graphic 基类不声明 CanvasRenderer，程序化 UI 必须自己补。</summary>
    private static void EnsureCanvasRenderer(GameObject target)
    {
        if (target.GetComponent<CanvasRenderer>() == null)
        {
            target.AddComponent<CanvasRenderer>();
        }
    }

    private static Color RarityColor(int rarity) => rarity switch
    {
        4 => new Color(0.62f, 0.78f, 0.95f, 1f),
        3 => new Color(0.91f, 0.77f, 0.42f, 1f),
        2 => new Color(0.72f, 0.75f, 0.80f, 1f),
        _ => new Color(0.66f, 0.49f, 0.35f, 1f),
    };
}
