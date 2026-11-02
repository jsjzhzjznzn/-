using System.Collections.Generic;
using System.IO;
using SkierFramework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 对话面板预制体构建 —— 原神式底部对话条 + 右侧竖排选项，输出到 UI 框架目录。
///
/// 与旧版（改场景里的 GenshinDialogCanvas）的区别：
///   · 根节点全屏透明、**不带 Canvas/CanvasScaler/GraphicRaycaster**（UIView.OnInit 会补）
///   · 末尾写入 UIControlData 绑定并保存为 Assets/Game/UI/Dialogue/DialogueView.prefab
///   · 交互提示 [F] 属于 HUD，不在本预制体内（另有迁移菜单）
///
/// 菜单：Tools/UI/重建对话界面预制体（可重复执行）
/// </summary>
public static class DialogueUiBuilder
{
    private const string PrefabPath = "Assets/Game/UI/Dialogue/DialogueView.prefab";
    private const string PrefabDir = "Assets/Game/UI/Dialogue";
    private const string FontPath = "Assets/Codely/Fonts/NotoSansSC-Regular SDF.asset";
    private const string PanelRoundPath = "Assets/Game/Resources/UI/Theme/panel_round.png";
    private const string BtnRoundPath = "Assets/Game/Resources/UI/Theme/btn_round.png";
    private const string TriDownPath = "Assets/Game/Demo/UI/UI_TriDown.png";
    private const string TexDir = "Assets/Game/UI/Dialogue/Textures";

    // 旧的场景画布与 HUD（迁移交互提示用）
    private const string LegacyCanvasName = "GenshinDialogCanvas";
    private const string HudCanvasName = "GenshinMenuCanvas";

    // ── 配色 ─────────────────────────────────────────────
    private static readonly Color Gold = new Color32(0xE8, 0xC4, 0x6A, 0xFF);
    private static readonly Color GoldSoft = new Color(0.91f, 0.77f, 0.42f, 0.45f);
    private static readonly Color Cream = new Color32(0xF2, 0xEC, 0xDF, 0xFF);
    private static readonly Color OptionBg = new Color(0.055f, 0.070f, 0.095f, 0.86f);

    // ── 尺寸（1920×1080 设计尺寸，与旧画布一致）──────────
    private const float PanelHeight = 300f;
    private const float SidePadding = 140f;
    private const float OptionWidth = 760f;
    private const float OptionHeight = 74f;
    private const float OptionGap = 16f;
    private const float OptionRightMargin = 120f;
    private const float OptionBottomMargin = 40f;

    [MenuItem("Tools/UI/重建对话界面预制体")]
    public static void BuildPrefab()
    {
        TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
        Sprite panelRound = AssetDatabase.LoadAssetAtPath<Sprite>(PanelRoundPath);
        Sprite btnRound = AssetDatabase.LoadAssetAtPath<Sprite>(BtnRoundPath);
        Sprite triDown = AssetDatabase.LoadAssetAtPath<Sprite>(TriDownPath);
        if (font == null) Debug.LogWarning("[DialogueUiBuilder] 字体未找到：" + FontPath);

        Sprite gradient = EnsureGradientSprite();
        Sprite white = EnsureWhiteSprite();
        Sprite outline = EnsureOutlineSprite();

        // ── 根：全屏透明（不加 Canvas / CanvasScaler / GraphicRaycaster）──
        var root = new GameObject("DialogueView", typeof(RectTransform));
        var rootRt = (RectTransform)root.transform;
        Stretch(rootRt);
        rootRt.anchorMin = Vector2.zero;
        rootRt.anchorMax = Vector2.one;
        rootRt.pivot = new Vector2(0.5f, 0.5f);
        rootRt.localScale = Vector3.one;

        // ── 底部对话条 ──────────────────────────────────
        var panel = NewRect(root.transform, "DialogPanel");
        var panelRt = (RectTransform)panel;
        panelRt.anchorMin = new Vector2(0f, 0f);
        panelRt.anchorMax = new Vector2(1f, 0f);
        panelRt.pivot = new Vector2(0.5f, 0f);
        panelRt.anchoredPosition = Vector2.zero;
        panelRt.sizeDelta = new Vector2(0f, PanelHeight);

        var panelCg = panel.gameObject.AddComponent<CanvasGroup>();
        panelCg.alpha = 0f;
        panelCg.blocksRaycasts = false;

        var backdrop = NewImage(panel, "Backdrop", gradient, Color.white);
        Stretch((RectTransform)backdrop.transform);
        backdrop.type = Image.Type.Simple;
        backdrop.raycastTarget = false;

        var topLine = NewImage(panel, "TopLine", white, new Color(Gold.r, Gold.g, Gold.b, 0.22f));
        var topLineRt = (RectTransform)topLine.transform;
        topLineRt.anchorMin = new Vector2(0f, 1f);
        topLineRt.anchorMax = new Vector2(1f, 1f);
        topLineRt.pivot = new Vector2(0.5f, 1f);
        topLineRt.anchoredPosition = Vector2.zero;
        topLineRt.sizeDelta = new Vector2(0f, 2f);
        topLine.raycastTarget = false;

        // ── 说话人名牌 ──────────────────────────────────
        var speakerRow = NewRect(panel, "SpeakerRow");
        var srRt = (RectTransform)speakerRow;
        srRt.anchorMin = new Vector2(0f, 1f);
        srRt.anchorMax = new Vector2(0f, 1f);
        srRt.pivot = new Vector2(0f, 1f);
        srRt.anchoredPosition = new Vector2(SidePadding, -48f);
        srRt.sizeDelta = new Vector2(600f, 52f);

        var diamond = NewImage(speakerRow, "Diamond", white, Gold);
        var dRt = (RectTransform)diamond.transform;
        dRt.anchorMin = dRt.anchorMax = new Vector2(0f, 0.5f);
        dRt.pivot = new Vector2(0f, 0.5f);
        dRt.anchoredPosition = Vector2.zero;
        dRt.sizeDelta = new Vector2(12f, 12f);
        dRt.localRotation = Quaternion.Euler(0f, 0f, 45f);
        diamond.raycastTarget = false;

        var speaker = NewText(speakerRow, "SpeakerName", string.Empty, 34f, Gold, font, FontStyles.Bold);
        var spRt = (RectTransform)speaker.transform;
        spRt.anchorMin = new Vector2(0f, 0.5f);
        spRt.anchorMax = new Vector2(0f, 0.5f);
        spRt.pivot = new Vector2(0f, 0.5f);
        spRt.anchoredPosition = new Vector2(28f, 0f);
        spRt.sizeDelta = new Vector2(520f, 46f);
        speaker.alignment = TextAlignmentOptions.MidlineLeft;
        speaker.raycastTarget = false;
        AddShadow(speaker, new Color(0f, 0f, 0f, 0.55f), new Vector2(0f, -2f));

        var underline = NewImage(speakerRow, "Underline", white, GoldSoft);
        var uRt = (RectTransform)underline.transform;
        uRt.anchorMin = new Vector2(0f, 0f);
        uRt.anchorMax = new Vector2(0f, 0f);
        uRt.pivot = new Vector2(0f, 0f);
        uRt.anchoredPosition = new Vector2(28f, 0f);
        uRt.sizeDelta = new Vector2(96f, 2f);
        underline.raycastTarget = false;

        // ── 正文 ────────────────────────────────────────
        var content = NewText(panel, "ContentText", string.Empty, 30f, Cream, font, FontStyles.Normal);
        var cRt = (RectTransform)content.transform;
        cRt.anchorMin = new Vector2(0f, 0f);
        cRt.anchorMax = new Vector2(1f, 1f);
        cRt.offsetMin = new Vector2(SidePadding, 54f);
        cRt.offsetMax = new Vector2(-SidePadding, -112f);
        content.alignment = TextAlignmentOptions.TopLeft;
        content.enableWordWrapping = true;
        content.lineSpacing = 16f;
        content.raycastTarget = false;
        AddShadow(content, new Color(0f, 0f, 0f, 0.5f), new Vector2(0f, -1.5f));

        // ── 继续箭头 ────────────────────────────────────
        var arrowImg = NewImage(panel, "ContinueArrow", triDown, Gold);
        var aRt = (RectTransform)arrowImg.transform;
        aRt.anchorMin = new Vector2(1f, 0f);
        aRt.anchorMax = new Vector2(1f, 0f);
        aRt.pivot = new Vector2(1f, 0f);
        aRt.anchoredPosition = new Vector2(-SidePadding + 36f, 34f);
        aRt.sizeDelta = new Vector2(30f, 30f);
        arrowImg.raycastTarget = false;
        var arrowCg = arrowImg.gameObject.AddComponent<CanvasGroup>();
        arrowCg.alpha = 0f;

        // ── 选项列表（右侧竖排）─────────────────────────
        var optionRoot = NewRect(root.transform, "OptionRoot");
        var orRt = (RectTransform)optionRoot;
        orRt.anchorMin = new Vector2(1f, 0f);
        orRt.anchorMax = new Vector2(1f, 0f);
        orRt.pivot = new Vector2(1f, 0f);
        orRt.anchoredPosition = new Vector2(-OptionRightMargin, PanelHeight + OptionBottomMargin);
        orRt.sizeDelta = new Vector2(OptionWidth, OptionHeight * 3f + OptionGap * 2f);

        var optionCg = optionRoot.gameObject.AddComponent<CanvasGroup>();
        optionCg.alpha = 1f;

        var optionButtons = new List<Button>();
        for (int i = 0; i < 3; i++)
            optionButtons.Add(BuildOptionButton(optionRoot, i, panelRound, outline, white, font));

        // ── 视图脚本与绑定 ──────────────────────────────
        var view = root.AddComponent<DialogueView>();
        view.panelRoot = panel.gameObject;
        view.panelCanvasGroup = panelCg;
        view.speakerNameText = speaker;
        view.contentText = content;
        view.continueArrow = arrowCg;
        view.optionRoot = optionRoot.gameObject;
        view.optionButtons = optionButtons.ToArray();

        var bindings = new List<CtrlItemData>();
        Bind(bindings, "panelRoot", panel.gameObject);
        Bind(bindings, "panelCanvasGroup", panelCg);
        Bind(bindings, "speakerNameText", speaker);
        Bind(bindings, "contentText", content);
        Bind(bindings, "continueArrow", arrowCg);
        Bind(bindings, "optionRoot", optionRoot.gameObject);
        Bind(bindings, "optionButtons", optionButtons, "Button");

        var data = root.AddComponent<UIControlData>();
        data.ctrlItemDatas = bindings;
        data.subUIItemDatas = new List<SubUIItemData>();

        // ── 保存预制体 ──────────────────────────────────
        if (!Directory.Exists(PrefabDir)) Directory.CreateDirectory(PrefabDir);
        AssetDatabase.Refresh();

        AssetDatabase.DeleteAsset(PrefabPath);
        GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets();

        if (saved == null)
        {
            Debug.LogError("[DialogueUiBuilder] 预制体保存失败：" + PrefabPath);
            return;
        }

        Selection.activeObject = saved;
        EditorGUIUtility.PingObject(saved);
        Debug.Log($"[DialogueUiBuilder] 对话面板预制体 -> {PrefabPath}（绑定 {bindings.Count} 项，选项 {optionButtons.Count} 个）");
    }

    /// <summary>
    /// 把交互提示 [F] 从旧对话画布挪到 HUD 画布（幂等）。
    /// 提示属于探索 HUD，不属于对话面板；删旧画布前必须先迁移，否则导演的引用会变成 null。
    /// </summary>
    [MenuItem("Tools/UI/对话界面/把交互提示移到 HUD")]
    public static void MigrateInteractionHintToHud()
    {
        var scene = SceneManager.GetActiveScene();
        GameObject legacyCanvas = GameObject.Find(LegacyCanvasName);
        GameObject hudCanvas = GameObject.Find(HudCanvasName);
        if (hudCanvas == null)
        {
            Debug.LogError("[DialogueUiBuilder] 找不到 HUD 画布 " + HudCanvasName + "，请先打开 duihua.scene");
            return;
        }

        Transform hint = legacyCanvas != null ? legacyCanvas.transform.Find("InteractionHint") : null;
        if (hint == null) hint = hudCanvas.transform.Find("InteractionHint");

        if (hint == null)
        {
            Debug.LogWarning("[DialogueUiBuilder] 未找到 InteractionHint（可能已迁移或已删除）");
            return;
        }

        if (hint.parent != hudCanvas.transform)
        {
            var rt = (RectTransform)hint;
            Vector2 anchored = rt.anchoredPosition;
            rt.SetParent(hudCanvas.transform, false);
            rt.anchorMin = new Vector2(0.5f, 0f);
            rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = anchored;
            rt.localScale = Vector3.one;
            Debug.Log("[DialogueUiBuilder] 交互提示已迁移到 HUD 画布（导演引用保持不变）");
        }
        else
        {
            Debug.Log("[DialogueUiBuilder] 交互提示已在 HUD 画布，无需迁移");
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
    }

    private static Button BuildOptionButton(Transform parent, int index, Sprite panelRound, Sprite outline, Sprite white, TMP_FontAsset font)
    {
        var go = new GameObject("OptionButton_" + index, typeof(RectTransform));
        go.transform.SetParent(parent, false);

        var rt = (RectTransform)go.transform;
        rt.anchorMin = new Vector2(1f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(1f, 1f);
        rt.anchoredPosition = new Vector2(0f, -index * (OptionHeight + OptionGap));
        rt.sizeDelta = new Vector2(OptionWidth, OptionHeight);

        var bg = go.AddComponent<Image>();
        bg.sprite = panelRound;
        bg.type = Image.Type.Sliced;
        bg.color = OptionBg;
        bg.raycastTarget = true;

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = bg;
        btn.transition = Selectable.Transition.ColorTint;
        var colors = btn.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
        colors.pressedColor = new Color(0.72f, 0.72f, 0.72f, 1f);
        colors.selectedColor = Color.white;
        colors.fadeDuration = 0.08f;
        btn.colors = colors;

        go.AddComponent<CanvasGroup>();

        // 描边（透明中心的圆角环，只画边框不盖住底色）
        var border = NewImage(go.transform, "Border", outline, new Color(Gold.r, Gold.g, Gold.b, 0.30f), true);
        Stretch((RectTransform)border.transform);
        border.type = Image.Type.Sliced;
        border.raycastTarget = false;

        // 左侧强调条
        var accent = NewImage(go.transform, "Accent", white, Gold);
        var acRt = (RectTransform)accent.transform;
        acRt.anchorMin = new Vector2(0f, 0f);
        acRt.anchorMax = new Vector2(0f, 1f);
        acRt.pivot = new Vector2(0f, 0.5f);
        acRt.anchoredPosition = new Vector2(2f, 0f);
        acRt.sizeDelta = new Vector2(4f, -18f);
        accent.raycastTarget = false;

        var label = NewText(go.transform, "Label", string.Empty, 26f, Cream, font, FontStyles.Normal);
        var lRt = (RectTransform)label.transform;
        lRt.anchorMin = Vector2.zero;
        lRt.anchorMax = Vector2.one;
        lRt.offsetMin = new Vector2(36f, 0f);
        lRt.offsetMax = new Vector2(-24f, 0f);
        label.alignment = TextAlignmentOptions.MidlineLeft;
        label.enableWordWrapping = true;
        label.raycastTarget = false;
        AddShadow(label, new Color(0f, 0f, 0f, 0.45f), new Vector2(0f, -1f));

        var fx = go.AddComponent<DialogueOptionButtonFx>();
        var fxSo = new SerializedObject(fx);
        fxSo.FindProperty("border").objectReferenceValue = border;
        fxSo.FindProperty("accent").objectReferenceValue = accent;
        fxSo.ApplyModifiedPropertiesWithoutUndo();

        return btn;
    }

    // ── 绑定 ─────────────────────────────────────────────

    private static void Bind(List<CtrlItemData> bindings, string fieldName, Object target)
    {
        if (target == null)
        {
            Debug.LogWarning("[DialogueUiBuilder] 绑定目标为空：" + fieldName);
            return;
        }

        string typeName;
        if (target is Button) typeName = "Button";
        else if (target is TextMeshProUGUI) typeName = "TextMeshProUGUI";
        else if (target is CanvasGroup) typeName = "CanvasGroup";
        else if (target is Image) typeName = "Image";
        else typeName = target.GetType().Name;   // GameObject 等

        bindings.Add(new CtrlItemData
        {
            name = fieldName,
            type = typeName,
            targets = new Object[] { target },
        });
    }

    private static void Bind(List<CtrlItemData> bindings, string fieldName, List<Button> targets, string typeName)
    {
        bindings.Add(new CtrlItemData
        {
            name = fieldName,
            type = typeName,
            targets = targets.ConvertAll(b => (Object)b).ToArray(),
        });
    }

    // ── 基础控件 ─────────────────────────────────────────

    private static RectTransform NewRect(Transform parent, string name)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    private static Image NewImage(Transform parent, string name, Sprite sprite, Color color, bool sliced = false)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.sprite = sprite;
        img.color = color;
        img.type = sliced ? Image.Type.Sliced : Image.Type.Simple;
        img.raycastTarget = false;
        return img;
    }

    private static TextMeshProUGUI NewText(Transform parent, string name, string text, float size, Color color,
        TMP_FontAsset font, FontStyles style)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var tmp = go.AddComponent<TextMeshProUGUI>();
        if (font != null)
        {
            tmp.font = font;
            tmp.fontSharedMaterial = font.material;
        }
        tmp.text = text;
        tmp.fontSize = size;
        tmp.color = color;
        tmp.fontStyle = style;
        tmp.alignment = TextAlignmentOptions.Left;
        tmp.raycastTarget = false;
        return tmp;
    }

    private static void AddShadow(Graphic target, Color color, Vector2 distance)
    {
        var shadow = target.gameObject.AddComponent<Shadow>();
        shadow.effectColor = color;
        shadow.effectDistance = distance;
        shadow.useGraphicAlpha = true;
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    // ── 程序化贴图（与旧版一致，避免视觉回归）─────────────

    private static Sprite EnsureGradientSprite()
    {
        string path = TexDir + "/dialog_gradient.png";
        if (!Directory.Exists(TexDir)) Directory.CreateDirectory(TexDir);

        const int h = 256;
        var tex = new Texture2D(4, h, TextureFormat.RGBA32, false);
        for (int y = 0; y < h; y++)
        {
            float v = y / (h - 1f);                    // 0 = 底部
            float a = 0.94f * Mathf.Pow(1f - v, 1.5f); // 底部最暗、顶部透明
            var c = new Color(0.03f, 0.042f, 0.062f, a);
            for (int x = 0; x < 4; x++) tex.SetPixel(x, y, c);
        }
        tex.Apply();
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);

        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = false;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.filterMode = FilterMode.Bilinear;
        importer.SaveAndReimport();

        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }

    /// <summary>圆角描边（透明中心），用于选项按钮边框：9 宫格拉伸后只剩 2px 金边。</summary>
    private static Sprite EnsureOutlineSprite()
    {
        string path = TexDir + "/dialog_outline.png";
        if (!Directory.Exists(TexDir)) Directory.CreateDirectory(TexDir);

        const int size = 64;
        const float radius = 14f;
        const float thickness = 2f;

        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float outer = RoundedCoverage(x, y, size, radius);
                float inner = RoundedCoverage(x, y, size, radius - thickness, thickness);
                float a = Mathf.Clamp01(outer - inner);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        }
        tex.Apply();
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);

        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.spriteBorder = new Vector4(24f, 24f, 24f, 24f);
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = false;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.SaveAndReimport();

        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }

    /// <summary>圆角矩形覆盖率（1=完全在内，0=完全在外，边缘 1px 抗锯齿）。inset 用于挖掉内圈。</summary>
    private static float RoundedCoverage(int x, int y, int size, float radius, float inset = 0f)
    {
        float half = size * 0.5f - inset;
        float r = Mathf.Max(0.5f, radius);
        float px = Mathf.Abs(x + 0.5f - size * 0.5f) - (half - r);
        float py = Mathf.Abs(y + 0.5f - size * 0.5f) - (half - r);
        float dx = Mathf.Max(px, 0f);
        float dy = Mathf.Max(py, 0f);
        float d = Mathf.Sqrt(dx * dx + dy * dy) + Mathf.Min(Mathf.Max(px, py), 0f) - r;
        return Mathf.Clamp01(0.5f - d);
    }

    private static Sprite EnsureWhiteSprite()
    {
        string path = TexDir + "/dialog_white.png";
        if (!Directory.Exists(TexDir)) Directory.CreateDirectory(TexDir);

        if (!File.Exists(path))
        {
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                    tex.SetPixel(x, y, Color.white);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
        }

        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }
}
