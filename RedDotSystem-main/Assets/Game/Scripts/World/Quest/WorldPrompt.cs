using System.Collections;
using System.Collections.Generic;

using TMPro;

using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 大世界通用提示（自举常驻，独立画布）：交互提示 + 一次性提示条。
///
/// 为什么不用 HUD 的文本节点：任务提交提示会出现在任务面板打开的时候（HUD 内容整块被隐藏），
/// 所以提示必须挂在比 HUD 更高、且不随 HUD 显隐的画布上。
/// 字体：动态创建的 TMP 默认字体可能没有中文字形，这里从场景已有 TMP 里借一个能显示中文的字体。
/// </summary>
public sealed class WorldPrompt : MonoBehaviour
{
    private const int SortingOrder = 340;

    public static WorldPrompt Instance { get; private set; }

    private CanvasGroup interactGroup;
    private TMP_Text interactText;

    private CanvasGroup toastGroup;
    private TMP_Text toastText;
    private Coroutine toastRoutine;

    /// <summary>已借到能显示中文的字体（借到后不再每次遍历场景）</summary>
    private bool fontResolved;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance != null) return;

        GameObject holder = new GameObject("WorldPrompt");
        holder.AddComponent<WorldPrompt>();
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

        BuildCanvas();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // ──────────────────────────────
    // 对外接口
    // ──────────────────────────────

    /// <summary>设置交互提示（空串/ null 隐藏）。同一帧内多个来源时后者生效。</summary>
    public static void SetInteract(string content)
    {
        if (Instance == null) return;

        bool show = !string.IsNullOrEmpty(content);
        if (show) Instance.EnsureFonts();
        if (Instance.interactText != null && show) Instance.interactText.SetText(content);
        if (Instance.interactGroup != null)
        {
            Instance.interactGroup.alpha = show ? 1f : 0f;
            Instance.interactGroup.blocksRaycasts = false;
        }
    }

    /// <summary>弹一条一次性提示（任务完成等），到时间自动淡出</summary>
    public static void Toast(string content, float seconds = 2.6f)
    {
        if (Instance == null || string.IsNullOrEmpty(content)) return;
        Instance.EnsureFonts();
        if (Instance.toastRoutine != null) Instance.StopCoroutine(Instance.toastRoutine);
        Instance.toastRoutine = Instance.StartCoroutine(Instance.ShowToast(content, seconds));
    }

    /// <summary>
    /// 字体懒补：自举发生在游戏启动瞬间，那时场景里还没有 UI，借不到中文字体；
    /// 因此每次显示时再试一次，借到就把标记置上，后续不再遍历。
    /// </summary>
    private void EnsureFonts()
    {
        if (fontResolved) return;

        ApplyFont(interactText);
        ApplyFont(toastText);

        bool ok = (interactText == null || interactText.font == null || interactText.font.HasCharacter('中'))
                  && (toastText == null || toastText.font == null || toastText.font.HasCharacter('中'));
        if (ok) fontResolved = true;
    }

    /// <summary>借字体：统一走项目的中文字体定位器（与加载界面同一套规则）</summary>
    private static void ApplyFont(TMP_Text target) => ChineseFontLocator.Apply(target);

    private IEnumerator ShowToast(string content, float seconds)
    {
        if (toastText != null) toastText.SetText(content);

        yield return Fade(toastGroup, 0f, 1f, 0.16f);
        yield return new WaitForSecondsRealtime(Mathf.Max(0.3f, seconds));
        yield return Fade(toastGroup, 1f, 0f, 0.22f);

        toastRoutine = null;
    }

    private static IEnumerator Fade(CanvasGroup group, float from, float to, float duration)
    {
        if (group == null) yield break;

        for (float t = 0f; t < duration; t += Time.unscaledDeltaTime)
        {
            group.alpha = Mathf.Lerp(from, to, t / duration);
            yield return null;
        }
        group.alpha = to;
    }

    // ──────────────────────────────
    // 搭建
    // ──────────────────────────────

    private void BuildCanvas()
    {
        GameObject canvasObj = new GameObject("WorldPromptCanvas");
        canvasObj.transform.SetParent(transform, false);

        Canvas canvas = canvasObj.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = SortingOrder;
        canvasObj.AddComponent<CanvasScaler>();
        canvasObj.AddComponent<GraphicRaycaster>();

        // 交互提示：右侧（对话选项也在这条竖线上，视线不用来回跳）
        interactGroup = CreateGroup(canvasObj.transform, "InteractPrompt", new Vector2(1f, 0.34f),
            new Vector2(-90f, 0f), new Vector2(440f, 44f), 20f, out interactText);
        interactGroup.alpha = 0f;
        AlignRight(interactGroup.GetComponent<RectTransform>());

        // 一次性提示：顶部偏下，与成就横幅错开
        toastGroup = CreateGroup(canvasObj.transform, "Toast", new Vector2(0.5f, 1f),
            new Vector2(0f, -132f), new Vector2(560f, 44f), 19f, out toastText);
        toastGroup.alpha = 0f;

        ApplyFont(interactText);
        ApplyFont(toastText);
    }

    /// <summary>右对齐：锚点与 pivot 都贴右边，避免不同分辨率下飘到屏幕外</summary>
    private static void AlignRight(RectTransform rect)
    {
        if (rect == null) return;
        rect.pivot = new Vector2(1f, 0.5f);
        rect.anchoredPosition = new Vector2(-90f, 0f);
    }

    private static CanvasGroup CreateGroup(Transform parent, string name, Vector2 anchor, Vector2 position,
        Vector2 size, float fontSize, out TMP_Text text)
    {
        GameObject root = new GameObject(name, typeof(RectTransform));
        RectTransform rect = root.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = new Vector2(0.5f, anchor.y);

        // 角锚点必须配同角 pivot，这里统一用中心 pivot + 中心对齐，避免偏移
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;

        CanvasGroup group = root.AddComponent<CanvasGroup>();
        group.blocksRaycasts = false;
        group.interactable = false;

        GameObject bgObj = new GameObject("Background", typeof(RectTransform));
        RectTransform bgRect = bgObj.GetComponent<RectTransform>();
        bgRect.SetParent(rect, false);
        bgRect.anchorMin = Vector2.zero;
        bgRect.anchorMax = Vector2.one;
        bgRect.offsetMin = Vector2.zero;
        bgRect.offsetMax = Vector2.zero;
        EnsureCanvasRenderer(bgObj);
        Image bg = bgObj.AddComponent<Image>();
        bg.color = new Color(0.06f, 0.07f, 0.08f, 0.72f);
        bg.raycastTarget = false;

        // 圆角胶囊底（项目通用 panel_round；拿不到就退回方角，不影响可读性）
        Sprite pill = Resources.Load<Sprite>("ui/Theme/panel_round");
        if (pill != null)
        {
            bg.sprite = pill;
            bg.type = Image.Type.Sliced;
        }

        GameObject textObj = new GameObject("Label", typeof(RectTransform));
        RectTransform textRect = textObj.GetComponent<RectTransform>();
        textRect.SetParent(rect, false);
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(14f, 0f);
        textRect.offsetMax = new Vector2(-14f, 0f);
        EnsureCanvasRenderer(textObj);

        TextMeshProUGUI label = textObj.AddComponent<TextMeshProUGUI>();
        label.fontSize = fontSize;
        label.color = new Color(0.94f, 0.94f, 0.94f, 1f);
        label.alignment = TextAlignmentOptions.Center;
        label.raycastTarget = false;
        // 固定单行：给足行框高度并用 Overflow，避免 Ellipsis 把整段裁掉
        label.overflowMode = TextOverflowModes.Overflow;
        label.enableWordWrapping = false;

        text = label;
        return group;
    }

    /// <summary>Tuanjie 的 uGUI 分叉里 Graphic 基类不声明 RequireComponent(CanvasRenderer)，程序化创建要自己补</summary>
    private static void EnsureCanvasRenderer(GameObject target)
    {
        if (target.GetComponent<CanvasRenderer>() == null) target.AddComponent<CanvasRenderer>();
    }
}
