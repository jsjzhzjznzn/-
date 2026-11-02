using System.Collections.Generic;

using TMPro;

using UnityEngine;
using UnityEngine.UI;

using XLua;

/// <summary>
/// 活动热更层的 uGUI 原语工厂（固定层）
///
/// 为什么存在：热更包的 Lua 要"自己搭界面"，但 Lua 造不了贴图、也不该重复写 uGUI 样板代码。
/// 这里只提供**与原语有关**的能力，不含任何活动业务：
///   1. 程序化贴图（圆角矩形 / 实心 / 圆 / 圆环 / 星形 / 光晕 / 竖向渐变）——白色底，靠 Image.color 着色；
///   2. uGUI 元素工厂（矩形 / 图片 / 文本 / 按钮 / 滚动区）——锚点与 pivot 同源的定位约定；
///   3. 物品图标取图（复用 ItemIconLoader）。
/// 业务语义（标题、文案、布局、交互、动效）全部在热更层的 Lua 里，本类改一次就得重编客户端，
/// 所以只往里加"任何活动都用得上"的东西。
///
/// 定位约定（本项目踩过的坑）：角锚必须配同角 pivot，否则 anchoredPosition 定位的是中心而不是角
/// —— NewRect/Place 默认把 pivot 设成与锚点相同，需要别的组合时用 PlaceEx 显式指定。
///
/// 注意：Lua 侧访问本类需要 [LuaCallCSharp] 登记（见 Assets/Game/Scripts/Lua/Editor/XLuaGenConfig.cs）并重跑 XLua/Generate Code。
/// </summary>
public static class ActivityUi
{
    // ──────────────────────────────
    // 颜色
    // ──────────────────────────────

    /// <summary>#RRGGBB / #RRGGBBAA → Color（Lua 里配色最顺手的形式）</summary>
    public static Color Hex(string hex, float alpha = 1f)
    {
        if (string.IsNullOrEmpty(hex)) return new Color(1f, 1f, 1f, alpha);
        string value = hex.TrimStart('#');
        if (value.Length < 6) return new Color(1f, 1f, 1f, alpha);

        int r = System.Convert.ToInt32(value.Substring(0, 2), 16);
        int g = System.Convert.ToInt32(value.Substring(2, 2), 16);
        int b = System.Convert.ToInt32(value.Substring(4, 2), 16);
        int a = value.Length >= 8 ? System.Convert.ToInt32(value.Substring(6, 2), 16) : Mathf.RoundToInt(Mathf.Clamp01(alpha) * 255f);
        return new Color(r / 255f, g / 255f, b / 255f, a / 255f);
    }

    /// <summary>同色改透明度（Lua 里做禁用态/淡入淡出最常用）</summary>
    public static Color WithAlpha(Color color, float alpha)
    {
        color.a = alpha;
        return color;
    }

    // ──────────────────────────────
    // 布局
    // ──────────────────────────────

    /// <summary>建一个空矩形（只带 RectTransform，可当容器）</summary>
    public static RectTransform NewRect(Transform parent, string name)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        RectTransform rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.localScale = Vector3.one;
        return rt;
    }

    /// <summary>建一个按锚点定位的矩形（pivot = 锚点，位置即"锚点处 + 偏移"）</summary>
    public static RectTransform NewRect(Transform parent, string name, float anchorX, float anchorY,
        float posX, float posY, float width, float height)
    {
        RectTransform rt = NewRect(parent, name);
        Place(rt, anchorX, anchorY, posX, posY, width, height);
        return rt;
    }

    /// <summary>按锚点摆位（pivot 自动跟锚点同角）</summary>
    public static void Place(RectTransform rt, float anchorX, float anchorY, float posX, float posY, float width, float height)
    {
        PlaceEx(rt, anchorX, anchorY, anchorX, anchorY, posX, posY, width, height);
    }

    /// <summary>按锚点摆位 + 显式 pivot（锚点与 pivot 组合自由，例：顶部通栏 anchor(0.5,1) pivot(0.5,1)）</summary>
    public static void PlaceEx(RectTransform rt, float anchorX, float anchorY, float pivotX, float pivotY,
        float posX, float posY, float width, float height)
    {
        if (rt == null) return;
        rt.anchorMin = new Vector2(anchorX, anchorY);
        rt.anchorMax = new Vector2(anchorX, anchorY);
        rt.pivot = new Vector2(pivotX, pivotY);
        rt.anchoredPosition = new Vector2(posX, posY);
        rt.sizeDelta = new Vector2(width, height);
    }

    /// <summary>拉伸铺满父级（left/top/right/bottom 为内缩边距，全 0 = 满铺）</summary>
    public static void Stretch(RectTransform rt, float left, float top, float right, float bottom)
    {
        if (rt == null) return;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(left, bottom);
        rt.offsetMax = new Vector2(-right, -top);
    }

    /// <summary>改写尺寸（不碰锚点/位置）</summary>
    public static void Resize(RectTransform rt, float width, float height)
    {
        if (rt != null) rt.sizeDelta = new Vector2(width, height);
    }

    /// <summary>清空所有子物体（重建界面前调用；Destroy 走延迟，下一帧才真正消失）</summary>
    public static void Clear(Transform parent)
    {
        if (parent == null) return;
        for (int i = parent.childCount - 1; i >= 0; i--)
            Object.Destroy(parent.GetChild(i).gameObject);
    }

    // ──────────────────────────────
    // 按名字找节点 / 绑事件 / 克隆模板
    //   ——「预制体当结构 + Lua 当逻辑」模式的核心 API：
    //      美术在 Editor 里摆好层级并命名，Lua 只认名字不持引用，改版式不必改 Lua
    // ──────────────────────────────

    /// <summary>按相对路径找物体（例："TopBar/Title"，支持多级）</summary>
    public static GameObject FindGo(Transform root, string path)
    {
        Transform found = FindTransform(root, path);
        return found != null ? found.gameObject : null;
    }

    /// <summary>按相对路径找 RectTransform</summary>
    public static RectTransform FindRect(Transform root, string path)
    {
        return FindTransform(root, path) as RectTransform;
    }

    /// <summary>按相对路径找 TMP 文本</summary>
    public static TextMeshProUGUI FindLabel(Transform root, string path)
    {
        Transform found = FindTransform(root, path);
        return found != null ? found.GetComponent<TextMeshProUGUI>() : null;
    }

    /// <summary>按相对路径找滚动区</summary>
    public static ScrollRect FindScroll(Transform root, string path)
    {
        Transform found = FindTransform(root, path);
        return found != null ? found.GetComponent<ScrollRect>() : null;
    }

    /// <summary>按相对路径找图片</summary>
    public static Image FindImage(Transform root, string path)
    {
        Transform found = FindTransform(root, path);
        return found != null ? found.GetComponent<Image>() : null;
    }

    /// <summary>按相对路径找按钮</summary>
    public static Button FindButton(Transform root, string path)
    {
        Transform found = FindTransform(root, path);
        return found != null ? found.GetComponent<Button>() : null;
    }

    /// <summary>按相对路径找按钮并挂上 Lua 点击回调（Lua 里最常用的一行式绑定）</summary>
    public static Button BindButton(Transform root, string path, LuaFunction callback)
    {
        Button button = FindButton(root, path);
        if (button == null)
        {
            Debug.LogWarning($"[活动] 预制体里找不到按钮：{path}");
            return null;
        }
        return OnClick(button, callback);
    }

    /// <summary>按相对路径设显隐</summary>
    public static void SetActive(Transform root, string path, bool active)
    {
        GameObject go = FindGo(root, path);
        if (go != null) go.SetActive(active);
    }

    /// <summary>克隆预制体内的模板（模板自身保持 inactive，克隆体自动激活）</summary>
    public static GameObject Clone(GameObject template, Transform parent)
    {
        if (template == null || parent == null) return null;
        GameObject clone = Object.Instantiate(template, parent, false);
        clone.name = template.name;
        clone.SetActive(true);
        return clone;
    }

    /// <summary>整块透明度/可交互（页面淡入淡出用）</summary>
    public static CanvasGroup Group(GameObject go, float alpha, bool interactable)
    {
        if (go == null) return null;
        CanvasGroup group = go.GetComponent<CanvasGroup>();
        if (group == null) group = go.AddComponent<CanvasGroup>();
        group.alpha = alpha;
        group.interactable = interactable;
        group.blocksRaycasts = interactable;
        return group;
    }

    /// <summary>按路径取 CanvasGroup 并一次性设置</summary>
    public static CanvasGroup SetGroup(Transform root, string path, float alpha, bool interactable)
    {
        GameObject go = FindGo(root, path);
        return go != null ? Group(go, alpha, interactable) : null;
    }

    /// <summary>进度条填充（按背景宽度的百分比改填充宽度；填充须左锚）</summary>
    public static void SetBar(RectTransform fill, RectTransform background, float percent)
    {
        if (fill == null || background == null) return;
        // 点锚元素在首帧布局前 rect.width 可能还是 0，用 sizeDelta 兜底（进度条都是点锚+固定宽）
        float width = background.rect.width;
        if (width <= 0.01f) width = background.sizeDelta.x;
        float target = Mathf.Max(0f, width * Mathf.Clamp01(percent));
        fill.anchorMin = new Vector2(0f, 0f);
        fill.anchorMax = new Vector2(0f, 1f);
        fill.pivot = new Vector2(0f, 0.5f);
        fill.anchoredPosition = new Vector2(0f, 0f);
        fill.sizeDelta = new Vector2(target, 0f);
    }

    /// <summary>把物体整棵子树转成"纯展示"（关射线，防挡住上层按钮）</summary>
    public static void NoRaycast(GameObject go)
    {
        if (go == null) return;
        Graphic[] graphics = go.GetComponentsInChildren<Graphic>(true);
        foreach (Graphic graphic in graphics) graphic.raycastTarget = false;
    }

    private static Transform FindTransform(Transform root, string path)
    {
        if (root == null || string.IsNullOrEmpty(path)) return null;
        Transform found = root.Find(path);
        if (found == null) Debug.LogWarning($"[活动] 预制体节点不存在：{path}");
        return found;
    }

    // ──────────────────────────────
    // 图片
    // ──────────────────────────────

    /// <summary>空图（不接收射线；sprite/color/type 由 Lua 自己设）</summary>
    public static Image NewImage(Transform parent, string name)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        RectTransform rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.localScale = Vector3.one;
        // Tuanjie 的 uGUI 分叉里 Graphic 基类不再声明 RequireComponent(CanvasRenderer)，统一手动补
        if (go.GetComponent<CanvasRenderer>() == null) go.AddComponent<CanvasRenderer>();
        Image image = go.AddComponent<Image>();
        image.raycastTarget = false;
        return image;
    }

    /// <summary>按锚点定位的空图</summary>
    public static Image NewImage(Transform parent, string name, float anchorX, float anchorY,
        float posX, float posY, float width, float height)
    {
        Image image = NewImage(parent, name);
        Place(image.rectTransform, anchorX, anchorY, posX, posY, width, height);
        return image;
    }

    /// <summary>设成 9-slice 圆角图（白底，靠 Image.color 着色；可任意拉伸不变形）</summary>
    public static Image SetRounded(Image image, int radius)
    {
        if (image == null) return null;
        image.sprite = Rounded(radius);
        image.type = Image.Type.Sliced;
        image.color = Color.white;
        return image;
    }

    /// <summary>设成实心矩形（纯色块）</summary>
    public static Image SetSolid(Image image)
    {
        if (image == null) return null;
        image.sprite = Solid();
        image.type = Image.Type.Simple;
        return image;
    }

    /// <summary>换图并指定是否 9-slice</summary>
    public static Image SetSprite(Image image, Sprite sprite, bool sliced)
    {
        if (image == null) return null;
        image.sprite = sprite;
        image.type = sliced ? Image.Type.Sliced : Image.Type.Simple;
        return image;
    }

    /// <summary>按亮度给图片着色（保留原 sprite 的 alpha）</summary>
    public static Image Tint(Image image, Color color)
    {
        if (image != null) image.color = color;
        return image;
    }

    // ──────────────────────────────
    // 文本
    // ──────────────────────────────

    /// <summary>简版：白字左对齐（Lua 侧最常用，避免每次传 color/align 与枚举）</summary>
    public static TextMeshProUGUI NewLabel(Transform parent, string name, string text, int fontSize)
    {
        return NewLabel(parent, name, text, fontSize, Color.white, TextAlignmentOptions.TopLeft);
    }

    /// <summary>TMP 文本（不接收射线；中文走 TMP 全局回退字体 NotoSansSC）</summary>
    public static TextMeshProUGUI NewLabel(Transform parent, string name, string text, int fontSize,
        Color color, TextAlignmentOptions align)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        RectTransform rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.localScale = Vector3.one;
        if (go.GetComponent<CanvasRenderer>() == null) go.AddComponent<CanvasRenderer>();

        TextMeshProUGUI label = go.AddComponent<TextMeshProUGUI>();
        label.raycastTarget = false;
        label.text = text ?? string.Empty;
        label.fontSize = fontSize;
        label.color = color;
        label.alignment = align;
        return label;
    }

    /// <summary>按锚点定位的 TMP 文本</summary>
    public static TextMeshProUGUI NewLabel(Transform parent, string name, string text, int fontSize,
        Color color, TextAlignmentOptions align, float anchorX, float anchorY,
        float posX, float posY, float width, float height)
    {
        TextMeshProUGUI label = NewLabel(parent, name, text, fontSize, color, align);
        Place(label.rectTransform, anchorX, anchorY, posX, posY, width, height);
        return label;
    }

    // ──────────────────────────────
    // 按钮
    // ──────────────────────────────

    /// <summary>
    /// 建一个按钮（自带圆角底图 + 居中文字标签）。返回 Button：
    /// 取底图 = NewGraphic(button)；标签 = button.transform.GetChild(0)。
    /// </summary>
    public static Button NewButton(Transform parent, string name, string labelText, int fontSize, float width, float height)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        RectTransform rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.localScale = Vector3.one;
        if (go.GetComponent<CanvasRenderer>() == null) go.AddComponent<CanvasRenderer>();

        Image background = go.AddComponent<Image>();
        background.raycastTarget = true;          // 按钮必须接收射线
        SetRounded(background, 12);

        Button button = go.AddComponent<Button>();
        button.targetGraphic = background;

        TextMeshProUGUI label = NewLabel(rt, "Label", labelText, fontSize, Hex("#FFFFFF"), TextAlignmentOptions.Center);
        Stretch(label.rectTransform, 8f, 4f, 8f, 4f);

        rt.sizeDelta = new Vector2(width, height);
        return button;
    }

    /// <summary>按钮底图</summary>
    public static Image NewGraphic(Button button)
    {
        return button != null ? button.GetComponent<Image>() : null;
    }

    /// <summary>按钮文字</summary>
    public static TextMeshProUGUI NewButtonLabel(Button button)
    {
        if (button == null || button.transform.childCount == 0) return null;
        return button.transform.GetChild(0).GetComponent<TextMeshProUGUI>();
    }

    /// <summary>取组件的 RectTransform（Lua 侧不必写 GetComponent(typeof(...))）</summary>
    public static RectTransform RectOf(Component component)
    {
        return component != null ? component.transform as RectTransform : null;
    }

    /// <summary>取物体的 RectTransform</summary>
    public static RectTransform RectOf(GameObject gameObject)
    {
        return gameObject != null ? gameObject.transform as RectTransform : null;
    }

    // ──────────────────────────────
    // Lua 点击回调的登记与生命周期
    //   LuaFunction 本质是"Lua 侧的引用"：面板关闭时要先把它们释放掉，才能安全销毁 LuaEnv；
    //   而 LuaEnv 的销毁**绝不能发生在 Lua 调用栈内部**（细则见 LuaActivityHost.TearDown 注释）
    // ──────────────────────────────

    private static readonly List<LuaFunction> clickCallbacks = new List<LuaFunction>();
    private static bool acceptingClicks = true;

    /// <summary>一次面板会话开始：放开点击，清空上一轮的登记</summary>
    public static void BeginSession()
    {
        clickCallbacks.Clear();
        acceptingClicks = true;
    }

    /// <summary>会话结束：立刻禁止点击（关闭与重开之间的窗口期内，残留按钮不再触发 Lua）</summary>
    public static void EndSession()
    {
        acceptingClicks = false;
    }

    /// <summary>
    /// 释放所有登记过的 Lua 点击回调。必须在 LuaEnv.Dispose **之前**调用，
    /// 且调用点必须已经在 Lua 调用栈之外（否则等于在 Lua 里自杀）
    /// </summary>
    public static void ReleaseClickCallbacks()
    {
        for (int i = 0; i < clickCallbacks.Count; i++)
        {
            try
            {
                clickCallbacks[i]?.Dispose();
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning($"[活动] 释放按钮回调异常（已忽略）：{exception.Message}");
            }
        }
        clickCallbacks.Clear();
    }

    /// <summary>给按钮挂点击回调（Lua function 参数由 xLua 原生支持，无需委托生成代码）</summary>
    public static Button OnClick(Button button, LuaFunction callback)
    {
        if (button == null || callback == null) return button;
        clickCallbacks.Add(callback);                      // 登记，面板关闭时统一释放
        button.onClick.AddListener(() =>
        {
            if (!acceptingClicks) return;                  // 面板已关闭：忽略窗口期内的误触
            try
            {
                callback.Call();
            }
            catch (System.Exception exception)
            {
                // Lua 回调抛异常绝不能炸穿 uGUI 事件循环
                Debug.LogError($"[活动] 按钮回调 Lua 异常（已吞掉）：{exception.Message}");
            }
        });
        return button;
    }

    /// <summary>按钮可用态 + 视觉联动（禁用时自动变暗）</summary>
    public static Button SetInteractable(Button button, bool interactable, Color enabledColor)
    {
        if (button == null) return null;
        button.interactable = interactable;
        Image image = NewGraphic(button);
        if (image != null)
        {
            Color target = interactable ? enabledColor : new Color(enabledColor.r * 0.45f, enabledColor.g * 0.45f, enabledColor.b * 0.45f, 0.75f);
            image.color = target;
        }
        return button;
    }

    // ──────────────────────────────
    // 滚动区（唤取详单）
    // ──────────────────────────────

    /// <summary>
    /// 建三段式滚动区：返回 ScrollRect（root），
    /// 视口 = root/Viewport（RectMask2D），内容 = root/Viewport/Content（顶锚，按行高自己撑高）。
    /// Lua 用法：ActivityUi.ScrollContent(rect) 拿 Content，往里塞行即可。
    /// </summary>
    public static ScrollRect NewScroll(Transform parent, string name, float anchorX, float anchorY,
        float posX, float posY, float width, float height)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        RectTransform rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.localScale = Vector3.one;
        if (go.GetComponent<CanvasRenderer>() == null) go.AddComponent<CanvasRenderer>();

        ScrollRect scroll = go.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 40f;
        scroll.inertia = false;
        Place(rt, anchorX, anchorY, posX, posY, width, height);

        RectTransform viewport = NewRect(rt, "Viewport");
        Stretch(viewport, 0f, 0f, 0f, 0f);
        viewport.gameObject.AddComponent<RectMask2D>();

        RectTransform content = NewRect(viewport, "Content");
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.anchoredPosition = Vector2.zero;
        content.sizeDelta = new Vector2(0f, height);

        scroll.viewport = viewport;
        scroll.content = content;
        return scroll;
    }

    /// <summary>滚动区的内容容器</summary>
    public static RectTransform ScrollContent(ScrollRect scroll)
    {
        return scroll != null ? scroll.content : null;
    }

    /// <summary>按内容高度撑开内容容器（行高 × 行数；滚到底自动钳位）</summary>
    public static void SetScrollHeight(ScrollRect scroll, float contentHeight)
    {
        if (scroll == null || scroll.content == null) return;
        scroll.content.sizeDelta = new Vector2(scroll.content.sizeDelta.x, contentHeight);
    }

    /// <summary>滚回顶部（重建列表后调用）</summary>
    public static void ScrollToTop(ScrollRect scroll)
    {
        if (scroll == null) return;
        Canvas.ForceUpdateCanvases();
        scroll.verticalNormalizedPosition = 1f;
    }

    // ──────────────────────────────
    // 物品图标
    // ──────────────────────────────

    /// <summary>按 iconKey 取物品图标（走项目统一的 ItemIconLoader，找不到返回 null）</summary>
    public static Sprite ItemIcon(string iconKey)
    {
        return ItemIconLoader.Get(iconKey);
    }

    // ──────────────────────────────
    // 程序化贴图（白色底 + 透明通道；静态缓存按参数键控）
    // ──────────────────────────────

    private static readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>();

    /// <summary>9-slice 圆角矩形（白色；纹理 = 4×radius，边框 = radius+2）</summary>
    public static Sprite Rounded(int radius)
    {
        radius = Mathf.Clamp(radius, 2, 64);
        string key = "rounded:" + radius;
        if (sprites.TryGetValue(key, out Sprite cached)) return cached;

        int size = Mathf.Max(16, radius * 4);
        int border = Mathf.Min(radius + 2, size / 2);
        Texture2D texture = NewTexture(size, size);
        Color[] pixels = new Color[size * size];
        float half = size * 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float distance = SdRoundBox(new Vector2(x + 0.5f - half, y + 0.5f - half), new Vector2(half, half), radius);
                pixels[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - distance));
            }
        }
        texture.SetPixels(pixels);
        texture.Apply(false, false);
        Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f,
            0, SpriteMeshType.FullRect, new Vector4(border, border, border, border));
        sprites[key] = sprite;
        return sprite;
    }

    /// <summary>纯白实心（1×1，可任意拉伸）</summary>
    public static Sprite Solid()
    {
        const string key = "solid";
        if (sprites.TryGetValue(key, out Sprite cached)) return cached;

        Texture2D texture = NewTexture(2, 2);
        texture.SetPixels(new[] { Color.white, Color.white, Color.white, Color.white });
        texture.Apply(false, false);
        Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, 2f, 2f), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        sprites[key] = sprite;
        return sprite;
    }

    /// <summary>实心圆（白色，带抗锯齿）</summary>
    public static Sprite Circle(int size)
    {
        size = Mathf.Clamp(size, 8, 256);
        string key = "circle:" + size;
        if (sprites.TryGetValue(key, out Sprite cached)) return cached;
        return sprites[key] = Disc(size, 0f);
    }

    /// <summary>圆环（白色，thickness 为环宽像素）</summary>
    public static Sprite Ring(int size, int thickness)
    {
        size = Mathf.Clamp(size, 8, 256);
        thickness = Mathf.Clamp(thickness, 1, size / 2);
        string key = "ring:" + size + ":" + thickness;
        if (sprites.TryGetValue(key, out Sprite cached)) return cached;
        return sprites[key] = Disc(size, thickness);
    }

    /// <summary>五角星（白色，辉光/评级用）</summary>
    public static Sprite Star(int size)
    {
        size = Mathf.Clamp(size, 8, 128);
        string key = "star:" + size;
        if (sprites.TryGetValue(key, out Sprite cached)) return cached;

        Texture2D texture = NewTexture(size, size);
        Color[] pixels = new Color[size * size];
        float half = size * 0.5f;
        Vector2[] polygon = StarPolygon(half * 0.94f, half * 0.42f, half);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                Vector2 point = new Vector2(x + 0.5f - half, y + 0.5f - half);
                pixels[y * size + x] = new Color(1f, 1f, 1f, InsidePolygon(polygon, point) ? 1f : 0f);
            }
        }
        texture.SetPixels(pixels);
        texture.Apply(false, false);
        Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        sprites[key] = sprite;
        return sprite;
    }

    /// <summary>径向光晕（中心白 → 边缘透明；叠 5★ 卡面用）</summary>
    public static Sprite SoftGlow(int size)
    {
        size = Mathf.Clamp(size, 16, 256);
        string key = "glow:" + size;
        if (sprites.TryGetValue(key, out Sprite cached)) return cached;

        Texture2D texture = NewTexture(size, size);
        Color[] pixels = new Color[size * size];
        float half = size * 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float distance = Vector2.Distance(new Vector2(x + 0.5f - half, y + 0.5f - half), Vector2.zero) / half;
                float alpha = Mathf.Clamp01(1f - distance);
                pixels[y * size + x] = new Color(1f, 1f, 1f, alpha * alpha);
            }
        }
        texture.SetPixels(pixels);
        texture.Apply(false, false);
        Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        sprites[key] = sprite;
        return sprite;
    }

    /// <summary>竖向渐变（上白不透明 → 下透明；给文字压底做可读性）</summary>
    public static Sprite VerticalFade(int height)
    {
        height = Mathf.Clamp(height, 8, 256);
        string key = "vfade:" + height;
        if (sprites.TryGetValue(key, out Sprite cached)) return cached;

        Texture2D texture = NewTexture(4, height);
        Color[] pixels = new Color[4 * height];
        for (int y = 0; y < height; y++)
        {
            float alpha = 1f - (float)y / (height - 1);
            for (int x = 0; x < 4; x++) pixels[y * 4 + x] = new Color(1f, 1f, 1f, alpha);
        }
        texture.SetPixels(pixels);
        texture.Apply(false, false);
        Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, 4f, height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        sprites[key] = sprite;
        return sprite;
    }

    // ──────────────────────────────
    // 内部工具
    // ──────────────────────────────

    private static Texture2D NewTexture(int width, int height)
    {
        return new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave,
        };
    }

    /// <summary>圆/圆环纹理（thickness = 0 为实心）</summary>
    private static Sprite Disc(int size, float thickness)
    {
        Texture2D texture = NewTexture(size, size);
        Color[] pixels = new Color[size * size];
        float half = size * 0.5f;
        float radius = half - 1f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float distance = Vector2.Distance(new Vector2(x + 0.5f - half, y + 0.5f - half), Vector2.zero);
                float alpha;
                if (thickness <= 0f) alpha = Mathf.Clamp01(radius - distance + 0.5f);
                else alpha = Mathf.Clamp01(thickness * 0.5f - Mathf.Abs(distance - (radius - thickness * 0.5f)) + 0.5f);
                pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
            }
        }
        texture.SetPixels(pixels);
        texture.Apply(false, false);
        return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
    }

    /// <summary>圆角矩形的有向距离场（<0 在内部）</summary>
    private static float SdRoundBox(Vector2 point, Vector2 halfSize, float radius)
    {
        Vector2 delta = new Vector2(Mathf.Abs(point.x), Mathf.Abs(point.y)) - (halfSize - Vector2.one * radius);
        Vector2 outside = new Vector2(Mathf.Max(delta.x, 0f), Mathf.Max(delta.y, 0f));
        return outside.magnitude + Mathf.Min(Mathf.Max(delta.x, delta.y), 0f) - radius;
    }

    private static Vector2[] StarPolygon(float outerRadius, float innerRadius, float half)
    {
        Vector2[] points = new Vector2[10];
        for (int i = 0; i < 10; i++)
        {
            float radius = (i % 2 == 0) ? outerRadius : innerRadius;
            float angle = Mathf.PI / 2f + i * Mathf.PI / 5f;   // 从正上方开始，顺时针
            points[i] = new Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);
        }
        return points;
    }

    private static bool InsidePolygon(Vector2[] polygon, Vector2 point)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            if ((polygon[i].y > point.y) != (polygon[j].y > point.y) &&
                point.x < (polygon[j].x - polygon[i].x) * (point.y - polygon[i].y) / (polygon[j].y - polygon[i].y) + polygon[i].x)
                inside = !inside;
        }
        return inside;
    }
}
