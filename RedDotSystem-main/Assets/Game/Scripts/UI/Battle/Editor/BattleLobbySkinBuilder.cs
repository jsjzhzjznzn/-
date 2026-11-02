#if UNITY_EDITOR
using System.IO;
using SkierFramework;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 对战大厅（BattleLobbyView）浅色皮肤重建器 —— 覆盖式重建，可重复执行。
/// 配色/圆角底图与设置面板同一套（Resources/UI/Theme 下的 panel_round / btn_round / circle），
/// 只重写这一个预制体，不动 BattleRoomView / JoinRoomView / 联机预制体（那些仍由
/// Tools/Battle/P3 一键构建 负责）。
///
/// 控件名就是 BattleLobbyView 里 [ControlBinding] 的字段名，改名会让绑定静默失效：
/// panel / titleText / subtitleText / createRoomButton / joinRoomButton / closeButton / statusText
/// 菜单：Tools/UI/重建对战大厅
/// </summary>
public static class BattleLobbySkinBuilder
{
    private const string PrefabPath = "Assets/Game/UI/Battle/BattleLobbyView.prefab";
    private const string ThemeDir = "Assets/Game/Resources/UI/Theme";
    private const string FontPath = "Assets/Codely/Fonts/NotoSansSC-Regular SDF.asset";

    // ── 色板（与设置面板一致：浅色羊皮纸 + 金色强调）──
    private static readonly Color PanelBg = new Color32(0xF6, 0xF1, 0xE7, 0xFF);
    private static readonly Color TextMain = new Color32(0x43, 0x40, 0x3A, 0xFF);
    private static readonly Color TextSub = new Color32(0x8C, 0x85, 0x78, 0xFF);
    private static readonly Color Gold = new Color32(0xD9, 0xB2, 0x5C, 0xFF);
    private static readonly Color GoldText = new Color32(0x3A, 0x32, 0x22, 0xFF);
    private static readonly Color BtnBg = new Color32(0xED, 0xE3, 0xCE, 0xFF);
    private static readonly Color CloseBg = new Color32(0xE8, 0xDF, 0xCB, 0xFF);

    private const float PanelWidth = 760f;
    private const float PanelHeight = 520f;

    [MenuItem("Tools/UI/重建对战大厅")]
    public static void Build()
    {
        Sprite roundPanel = EnsureRoundedSprite(ThemeDir + "/panel_round.png", 128, 32, 32);
        Sprite roundBtn = EnsureRoundedSprite(ThemeDir + "/btn_round.png", 64, 12, 12);
        Sprite circle = EnsureRoundedSprite(ThemeDir + "/circle.png", 128, 64, 0);

        // 羊皮纸面板底图（金色描边 + 四角藤蔓），九宫格切分：四角装饰不拉伸，中间纸面跟着面板走
        string panelArtPath = FindAssetPath(ThemeDir + "/panel_bg.png", "Assets/Game/Resources/ui/Theme/panel_bg.png");
        Sprite panelArt = panelArtPath != null ? EnsureSlicedSprite(panelArtPath, 200) : null;

        // 背景图：主页虚化底（低分辨率模糊图，本来就是做全屏 backdrop 用的）
        Sprite backdropArt = LoadSprite(ThemeDir + "/blur_home.png", "Assets/Game/Resources/ui/Theme/blur_home.png");
        TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);

        GameObject root = new GameObject("BattleLobbyView", typeof(RectTransform));
        Stretch((RectTransform)root.transform);

        UIControlData data = root.AddComponent<UIControlData>();
        data.ctrlItemDatas = new System.Collections.Generic.List<CtrlItemData>();

        // UIView 本体：UIManager 靠它驱动 Open/Close/绑定，漏了会打不开面板
        root.AddComponent<BattleLobbyView>();

        // 全屏背景：虚化底压暗（没图就退回纯黑遮罩），同时吃掉下层点击
        Image backdrop = backdropArt != null
            ? NewImage("Backdrop", root.transform, backdropArt, new Color(0.52f, 0.50f, 0.52f, 1f))
            : NewImage("Backdrop", root.transform, null, new Color(0f, 0f, 0f, 0.55f));
        Stretch(backdrop.rectTransform);

        // 面板投影 + 面板
        Image shadow = NewImage("PanelShadow", root.transform, roundPanel, new Color(0f, 0f, 0f, 0.16f), true);
        Center(shadow.rectTransform, PanelWidth + 18f, PanelHeight + 18f, 0f, -10f);
        shadow.raycastTarget = false;

        // 面板底：有羊皮纸底图就直接铺图（白色不改色），没有才退回纯色圆角
        Image panel = panelArt != null
            ? NewImage("Panel", root.transform, panelArt, Color.white, true)
            : NewImage("Panel", root.transform, roundPanel, PanelBg, true);
        Center(panel.rectTransform, PanelWidth, PanelHeight, 0f, 0f);

        // 标题居中：底图四角有藤蔓装饰，靠左会压在花枝上
        TextMeshProUGUI title = NewText("TitleText", panel.transform, "对战", 44, TextMain, font, FontStyles.Bold);
        TopCenter(title.rectTransform, 66f, 520f, 56f);
        title.alignment = TextAlignmentOptions.Center;

        TextMeshProUGUI subtitle = NewText("SubtitleText", panel.transform, "房主即服务端 · 端口 7777", 18, TextSub, font, FontStyles.Normal);
        TopCenter(subtitle.rectTransform, 122f, 520f, 28f);
        subtitle.alignment = TextAlignmentOptions.Center;

        Image divider = NewImage("Divider", panel.transform, null, new Color(Gold.r, Gold.g, Gold.b, 0.45f));
        TopLeft(divider.rectTransform, 96f, 162f, PanelWidth - 192f, 2f);
        divider.raycastTarget = false;

        // 关闭键（圆形）
        Button closeButton = NewButton("closeButton", panel.transform, "×", circle, CloseBg, TextMain, font, 30);
        RectTransform closeRt = closeButton.GetComponent<RectTransform>();
        closeRt.anchorMin = closeRt.anchorMax = new Vector2(1f, 1f);
        closeRt.pivot = new Vector2(1f, 1f);
        closeRt.anchoredPosition = new Vector2(-54f, -52f);
        closeRt.sizeDelta = new Vector2(40f, 40f);

        // 两个主操作
        Button createButton = NewActionButton("createRoomButton", panel.transform, roundBtn, font,
            Gold, GoldText, "创建房间", "作为房主开局 · 好友填写你的 IP 加入", 202f);
        Button joinButton = NewActionButton("joinRoomButton", panel.transform, roundBtn, font,
            BtnBg, TextMain, "加入房间", "输入房主的 IP 后连接", 310f);

        // 底部提示
        TextMeshProUGUI status = NewText("StatusText", panel.transform,
            "创建房间后把本机 IP 告诉好友；加入房间需要填写房主 IP", 15, TextSub, font, FontStyles.Normal);
        RectTransform statusRt = status.rectTransform;
        statusRt.anchorMin = statusRt.anchorMax = new Vector2(0.5f, 0f);
        statusRt.pivot = new Vector2(0.5f, 0f);
        statusRt.anchoredPosition = new Vector2(0f, 58f);
        statusRt.sizeDelta = new Vector2(PanelWidth - 176f, 48f);
        status.alignment = TextAlignmentOptions.Center;
        status.enableWordWrapping = true;

        Bind(data, "panel", panel.rectTransform);
        Bind(data, "titleText", title);
        Bind(data, "subtitleText", subtitle);
        Bind(data, "createRoomButton", createButton);
        Bind(data, "joinRoomButton", joinButton);
        Bind(data, "closeButton", closeButton);
        Bind(data, "statusText", status);

        // 规范：UI 节点一律 layer=5(UI)。运行期 UIView.OnInit 会再修一次，但预制体本身要合规
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            t.gameObject.layer = 5;

        Directory.CreateDirectory(Path.GetDirectoryName(PrefabPath) ?? "Assets");
        PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);
        AssetDatabase.Refresh();
        Debug.Log("[BattleLobbySkin] 预制体已重建：" + PrefabPath);
    }

    /// <summary>主操作按钮：圆角底 + 主标题 + 小字说明（说明让两个按钮自己解释会发生什么）</summary>
    private static Button NewActionButton(string name, Transform parent, Sprite sprite, TMP_FontAsset font,
        Color bg, Color textColor, string label, string caption, float top)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);

        Image img = go.AddComponent<Image>();
        img.sprite = sprite;
        img.type = sprite != null && sprite.border.sqrMagnitude > 0f ? Image.Type.Sliced : Image.Type.Simple;
        img.color = bg;
        img.raycastTarget = true;

        RectTransform rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = new Vector2(0f, -top);
        rt.sizeDelta = new Vector2(PanelWidth - 160f, 86f);

        Button button = go.AddComponent<Button>();
        button.targetGraphic = img;
        ColorBlock colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(0.97f, 0.95f, 0.9f, 1f);
        colors.pressedColor = new Color(0.88f, 0.85f, 0.78f, 1f);
        colors.disabledColor = new Color(0.8f, 0.8f, 0.8f, 0.5f);
        colors.fadeDuration = 0.08f;
        button.colors = colors;

        TextMeshProUGUI main = NewText("Label", go.transform, label, 28, textColor, font, FontStyles.Bold);
        RectTransform mainRt = main.rectTransform;
        mainRt.anchorMin = new Vector2(0f, 0.5f);
        mainRt.anchorMax = new Vector2(1f, 0.5f);
        mainRt.pivot = new Vector2(0.5f, 0.5f);
        mainRt.anchoredPosition = new Vector2(0f, 12f);
        mainRt.sizeDelta = new Vector2(-32f, 36f);
        main.alignment = TextAlignmentOptions.Center;

        Color captionColor = new Color(textColor.r, textColor.g, textColor.b, 0.62f);
        TextMeshProUGUI sub = NewText("Caption", go.transform, caption, 14, captionColor, font, FontStyles.Normal);
        RectTransform subRt = sub.rectTransform;
        subRt.anchorMin = new Vector2(0f, 0.5f);
        subRt.anchorMax = new Vector2(1f, 0.5f);
        subRt.pivot = new Vector2(0.5f, 0.5f);
        subRt.anchoredPosition = new Vector2(0f, -20f);
        subRt.sizeDelta = new Vector2(-32f, 20f);
        sub.alignment = TextAlignmentOptions.Center;

        return button;
    }

    // ==================== 通用小工具 ====================

    private static Button NewButton(string name, Transform parent, string label, Sprite sprite, Color bgColor,
        Color textColor, TMP_FontAsset font, int fontSize)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);

        Image img = go.AddComponent<Image>();
        img.sprite = sprite;
        img.type = sprite != null && sprite.border.sqrMagnitude > 0f ? Image.Type.Sliced : Image.Type.Simple;
        img.color = bgColor;
        img.raycastTarget = true;

        Button button = go.AddComponent<Button>();
        button.targetGraphic = img;
        ColorBlock colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(0.95f, 0.95f, 0.95f, 1f);
        colors.pressedColor = new Color(0.82f, 0.82f, 0.82f, 1f);
        colors.disabledColor = new Color(0.7f, 0.7f, 0.7f, 0.6f);
        button.colors = colors;

        TextMeshProUGUI text = NewText("Label", go.transform, label, fontSize, textColor, font, FontStyles.Bold);
        Stretch(text.rectTransform);
        text.alignment = TextAlignmentOptions.Center;

        return button;
    }

    private static TextMeshProUGUI NewText(string name, Transform parent, string content, int fontSize, Color color,
        TMP_FontAsset font, FontStyles style)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);

        TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
        if (font != null)
        {
            text.font = font;
            text.fontSharedMaterial = font.material;
        }
        text.text = content;
        text.fontSize = fontSize;
        text.color = color;
        text.fontStyle = style;
        text.raycastTarget = false;
        text.enableWordWrapping = false;
        return text;
    }

    private static Image NewImage(string name, Transform parent, Sprite sprite, Color color, bool sliced = false)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);

        Image img = go.AddComponent<Image>();
        img.sprite = sprite;
        img.type = sliced && sprite != null && sprite.border.sqrMagnitude > 0f ? Image.Type.Sliced : Image.Type.Simple;
        img.color = color;
        img.raycastTarget = true;
        return img;
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private static void Center(RectTransform rt, float width, float height, float x, float y)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(width, height);
    }

    private static void TopCenter(RectTransform rt, float top, float width, float height)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = new Vector2(0f, -top);
        rt.sizeDelta = new Vector2(width, height);
    }

    private static void TopLeft(RectTransform rt, float x, float top, float width, float height)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -top);
        rt.sizeDelta = new Vector2(width, height);
    }

    private static void Bind(UIControlData data, string name, Component component)
    {
        data.ctrlItemDatas.Add(new CtrlItemData
        {
            name = name,
            type = component != null ? component.GetType().Name : string.Empty,
            targets = new Object[] { component },
        });
    }

    /// <summary>按候选路径找第一个存在的资源（Resources 下 UI/ui 两种大小写目录历史上都出现过）</summary>
    private static string FindAssetPath(params string[] paths)
    {
        foreach (string path in paths)
        {
            if (AssetDatabase.LoadAssetAtPath<Sprite>(path) != null) return path;
        }
        return null;
    }

    /// <summary>读取已有底图并确保九宫格边距（图缺失返回 null，不生成）</summary>
    private static Sprite EnsureSlicedSprite(string assetPath, int border)
    {
        Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
        if (sprite == null) return null;

        TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (importer != null)
        {
            if (importer.textureType != TextureImporterType.Sprite)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.SaveAndReimport();
            }

            Vector4 target = new Vector4(border, border, border, border);
            if (importer.spriteBorder != target)
            {
                importer.spriteBorder = target;
                importer.SaveAndReimport();
            }
        }

        return AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
    }

    private static Sprite LoadSprite(params string[] paths)
    {
        foreach (string path in paths)
        {
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite != null) return sprite;
        }
        return null;
    }

    /// <summary>生成（或复用）白色圆角贴图并导入为九宫格 Sprite（与设置面板共用同一批资源）</summary>
    private static Sprite EnsureRoundedSprite(string assetPath, int size, int radius, int border)
    {
        string fullPath = Path.Combine(Directory.GetCurrentDirectory(), assetPath);
        if (!File.Exists(fullPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? "Assets");

            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float r = Mathf.Clamp(radius, 0, size / 2);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float alpha = 1f;
                    if (r > 0f)
                    {
                        float cx = Mathf.Clamp(x + 0.5f, r, size - r);
                        float cy = Mathf.Clamp(y + 0.5f, r, size - r);
                        float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                        alpha = Mathf.Clamp01(r - dist + 0.5f);
                    }
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            tex.Apply();
            File.WriteAllBytes(fullPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
        }

        TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (importer != null)
        {
            bool changed = false;
            if (importer.textureType != TextureImporterType.Sprite) { importer.textureType = TextureImporterType.Sprite; changed = true; }
            if (importer.spriteBorder != new Vector4(border, border, border, border)) { importer.spriteBorder = new Vector4(border, border, border, border); changed = true; }
            if (importer.mipmapEnabled) { importer.mipmapEnabled = false; changed = true; }
            if (changed) importer.SaveAndReimport();
        }

        return AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
    }
}
#endif
