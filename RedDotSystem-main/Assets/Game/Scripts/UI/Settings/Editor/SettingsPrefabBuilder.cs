using System.Collections.Generic;
using System.IO;
using SkierFramework;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 设置面板预制体生成器（可重复执行）。
/// 结构/控件的名字就是 SettingsView 里 [ControlBinding] 字段名，改名会让绑定静默失效。
/// 菜单：Tools/UI/重建设置面板
/// </summary>
public static class SettingsPrefabBuilder
{
    private const string PrefabPath = "Assets/Game/UI/Settings/SettingsView.prefab";
    private const string ThemeDir = "Assets/Game/Resources/UI/Theme";
    private const string FontPath = "Assets/Codely/Fonts/NotoSansSC-Regular SDF.asset";

    private static readonly Color PanelBg = new Color32(0xF6, 0xF1, 0xE7, 0xFF);
    private static readonly Color TextMain = new Color32(0x43, 0x40, 0x3A, 0xFF);
    private static readonly Color TextSub = new Color32(0x8C, 0x85, 0x78, 0xFF);
    private static readonly Color Gold = new Color32(0xD9, 0xB2, 0x5C, 0xFF);
    private static readonly Color Track = new Color32(0xDC, 0xD2, 0xBC, 0xFF);
    private static readonly Color BtnBg = new Color32(0xED, 0xE3, 0xCE, 0xFF);
    private static readonly Color DangerBg = new Color32(0xE9, 0xC9, 0xC0, 0xFF);
    private static readonly Color DangerText = new Color32(0x8A, 0x3B, 0x2E, 0xFF);

    [MenuItem("Tools/UI/重建设置面板")]
    public static void Build()
    {
        EnsureFolder("Assets/Game/UI", "Settings");
        EnsureFolder("Assets/Game/Resources/UI", "Theme");

        Sprite round9 = EnsureRoundedSprite(ThemeDir + "/panel_round.png", 128, 32, 32);
        Sprite round12 = EnsureRoundedSprite(ThemeDir + "/btn_round.png", 64, 12, 12);
        Sprite circle = EnsureRoundedSprite(ThemeDir + "/circle.png", 128, 64, 0);

        TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);

        // ---------- 根 ----------
        GameObject root = new GameObject("SettingsView", typeof(RectTransform));
        RectTransform rootRt = (RectTransform)root.transform;
        rootRt.anchorMin = Vector2.zero;
        rootRt.anchorMax = Vector2.one;
        rootRt.offsetMin = Vector2.zero;
        rootRt.offsetMax = Vector2.zero;

        Image backdrop = NewImage("Backdrop", root.transform, null, new Color(0f, 0f, 0f, 0.55f));
        Stretch((RectTransform)backdrop.transform);

        // ---------- 面板 ----------
        Image panel = NewImage("Panel", root.transform, round9, PanelBg, true);
        RectTransform panelRt = (RectTransform)panel.transform;
        panelRt.anchorMin = panelRt.anchorMax = new Vector2(0.5f, 0.5f);
        panelRt.pivot = new Vector2(0.5f, 0.5f);
        panelRt.anchoredPosition = Vector2.zero;
        panelRt.sizeDelta = new Vector2(960f, 640f);

        var title = NewText("Title", panel.transform, "设置", 34, TextMain, font, FontStyles.Bold);
        TopLeft((RectTransform)title.transform, 44f, 34f, 300f, 48f);
        title.alignment = TextAlignmentOptions.Left;

        var subtitle = NewText("Subtitle", panel.transform, "音量 · 画面", 18, TextSub, font, FontStyles.Normal);
        TopLeft((RectTransform)subtitle.transform, 46f, 78f, 300f, 28f);
        subtitle.alignment = TextAlignmentOptions.Left;

        // 关闭键
        Button closeButton = NewButton("closeButton", panel.transform, "×", round9, new Color32(0xE8, 0xDF, 0xCB, 0xFF), TextMain, font, 30);
        RectTransform closeRt = (RectTransform)closeButton.transform;
        closeRt.anchorMin = closeRt.anchorMax = new Vector2(1f, 1f);
        closeRt.pivot = new Vector2(1f, 1f);
        closeRt.anchoredPosition = new Vector2(-28f, -26f);
        closeRt.sizeDelta = new Vector2(48f, 48f);

        // ---------- 音量区 ----------
        var volTitle = NewText("VolumeSectionTitle", panel.transform, "音量", 22, TextSub, font, FontStyles.Bold);
        TopLeft((RectTransform)volTitle.transform, 46f, 116f, 200f, 30f);
        volTitle.alignment = TextAlignmentOptions.Left;

        float rowTop = 158f;
        float rowGap = 54f;

        Slider masterSlider = NewSliderRow(panel.transform, "Row_Master", "主音量", "masterSlider", "masterValueText", rowTop + rowGap * 0f, round9, circle, font, Track, Gold, TextMain, TextSub, out TextMeshProUGUI masterValueText);
        Slider musicSlider = NewSliderRow(panel.transform, "Row_Music", "音乐", "musicSlider", "musicValueText", rowTop + rowGap * 1f, round9, circle, font, Track, Gold, TextMain, TextSub, out TextMeshProUGUI musicValueText);
        Slider sfxSlider = NewSliderRow(panel.transform, "Row_Sfx", "音效", "sfxSlider", "sfxValueText", rowTop + rowGap * 2f, round9, circle, font, Track, Gold, TextMain, TextSub, out TextMeshProUGUI sfxValueText);
        Slider voiceSlider = NewSliderRow(panel.transform, "Row_Voice", "语音", "voiceSlider", "voiceValueText", rowTop + rowGap * 3f, round9, circle, font, Track, Gold, TextMain, TextSub, out TextMeshProUGUI voiceValueText);

        Toggle muteToggle = NewToggleRow(panel.transform, "muteToggle", "静音", rowTop + rowGap * 4f, round9, circle, font, Track, Gold, TextMain, 240f);
        RectTransform muteRt = (RectTransform)muteToggle.transform;
        muteRt.anchorMin = muteRt.anchorMax = new Vector2(0f, 1f);
        muteRt.pivot = new Vector2(0f, 1f);
        muteRt.anchoredPosition = new Vector2(96f, -(rowTop + rowGap * 4f));
        muteRt.sizeDelta = new Vector2(240f, 32f);

        // 分隔线
        Image divider = NewImage("Divider", panel.transform, null, new Color(0f, 0f, 0f, 0.08f));
        TopLeft((RectTransform)divider.transform, 46f, 400f, 868f, 2f);

        // ---------- 画面区 ----------
        var displayTitle = NewText("DisplaySectionTitle", panel.transform, "画面", 22, TextSub, font, FontStyles.Bold);
        TopLeft((RectTransform)displayTitle.transform, 46f, 424f, 200f, 30f);
        displayTitle.alignment = TextAlignmentOptions.Left;

        Toggle fullscreenToggle = NewToggleRow(panel.transform, "fullscreenToggle", "全屏", 470f, round9, circle, font, Track, Gold, TextMain, 280f);
        RectTransform fsRt = (RectTransform)fullscreenToggle.transform;
        fsRt.anchorMin = fsRt.anchorMax = new Vector2(0f, 1f);
        fsRt.pivot = new Vector2(0f, 1f);
        fsRt.anchoredPosition = new Vector2(96f, -470f);
        fsRt.sizeDelta = new Vector2(280f, 32f);

        // ---------- 底部按钮 ----------
        Button logoutButton = NewButton("logoutButton", panel.transform, "退出登录", round12, BtnBg, TextMain, font, 24);
        PositionButton((RectTransform)logoutButton.transform, 46f, 574f, 200f, 48f);

        Button quitButton = NewButton("quitButton", panel.transform, "退出游戏", round12, DangerBg, DangerText, font, 24);
        PositionButton((RectTransform)quitButton.transform, 262f, 574f, 200f, 48f);

        // 返回主页：只在有 3D 世界的场景显示（SettingsViewArgs.showReturnHome）
        Button returnHomeButton = NewButton("returnHomeButton", panel.transform, "返回主页", round12, BtnBg, Gold, font, 24);
        PositionButton((RectTransform)returnHomeButton.transform, 478f, 574f, 200f, 48f);

        var versionText = NewText("versionText", panel.transform, "开发版", 16, TextSub, font, FontStyles.Normal);
        RectTransform verRt = (RectTransform)versionText.transform;
        verRt.anchorMin = verRt.anchorMax = new Vector2(1f, 0f);
        verRt.pivot = new Vector2(1f, 0f);
        verRt.anchoredPosition = new Vector2(-30f, 22f);
        verRt.sizeDelta = new Vector2(220f, 24f);
        versionText.alignment = TextAlignmentOptions.Right;

        // ---------- 退出确认 ----------
        GameObject confirmRoot = new GameObject("logoutConfirmRoot", typeof(RectTransform));
        RectTransform confirmRt = (RectTransform)confirmRoot.transform;
        confirmRt.SetParent(panel.transform, false);
        Stretch(confirmRt);

        Image confirmMask = NewImage("ConfirmMask", confirmRoot.transform, null, new Color(0f, 0f, 0f, 0.45f));
        Stretch((RectTransform)confirmMask.transform);

        Image confirmBox = NewImage("ConfirmBox", confirmRoot.transform, round9, PanelBg, true);
        RectTransform boxRt = (RectTransform)confirmBox.transform;
        boxRt.anchorMin = boxRt.anchorMax = new Vector2(0.5f, 0.5f);
        boxRt.pivot = new Vector2(0.5f, 0.5f);
        boxRt.anchoredPosition = Vector2.zero;
        boxRt.sizeDelta = new Vector2(560f, 260f);

        var confirmText = NewText("ConfirmText", confirmBox.transform, "确定要退出登录吗？", 28, TextMain, font, FontStyles.Bold);
        RectTransform ctRt = (RectTransform)confirmText.transform;
        ctRt.anchorMin = ctRt.anchorMax = new Vector2(0.5f, 1f);
        ctRt.pivot = new Vector2(0.5f, 1f);
        ctRt.anchoredPosition = new Vector2(0f, -48f);
        ctRt.sizeDelta = new Vector2(480f, 44f);
        confirmText.alignment = TextAlignmentOptions.Center;

        var confirmHint = NewText("ConfirmHint", confirmBox.transform, "下次进入需要使用账号密码重新登录", 18, TextSub, font, FontStyles.Normal);
        RectTransform chRt = (RectTransform)confirmHint.transform;
        chRt.anchorMin = chRt.anchorMax = new Vector2(0.5f, 1f);
        chRt.pivot = new Vector2(0.5f, 1f);
        chRt.anchoredPosition = new Vector2(0f, -98f);
        chRt.sizeDelta = new Vector2(480f, 28f);
        confirmHint.alignment = TextAlignmentOptions.Center;

        Button confirmButton = NewButton("logoutConfirmButton", confirmBox.transform, "确定退出", round12, DangerBg, DangerText, font, 22);
        RectTransform cbRt = (RectTransform)confirmButton.transform;
        cbRt.anchorMin = cbRt.anchorMax = new Vector2(0.5f, 0f);
        cbRt.pivot = new Vector2(0.5f, 0f);
        cbRt.anchoredPosition = new Vector2(-110f, 40f);
        cbRt.sizeDelta = new Vector2(190f, 46f);

        Button cancelButton = NewButton("logoutCancelButton", confirmBox.transform, "取消", round12, BtnBg, TextMain, font, 22);
        RectTransform canRt = (RectTransform)cancelButton.transform;
        canRt.anchorMin = canRt.anchorMax = new Vector2(0.5f, 0f);
        canRt.pivot = new Vector2(0.5f, 0f);
        canRt.anchoredPosition = new Vector2(110f, 40f);
        canRt.sizeDelta = new Vector2(190f, 46f);

        confirmRoot.SetActive(false);

        // ---------- 绑定 ----------
        var data = root.AddComponent<UIControlData>();
        data.ctrlItemDatas = new List<CtrlItemData>();
        Bind(data, "masterSlider", masterSlider);
        Bind(data, "masterValueText", masterValueText);
        Bind(data, "musicSlider", musicSlider);
        Bind(data, "musicValueText", musicValueText);
        Bind(data, "sfxSlider", sfxSlider);
        Bind(data, "sfxValueText", sfxValueText);
        Bind(data, "voiceSlider", voiceSlider);
        Bind(data, "voiceValueText", voiceValueText);
        Bind(data, "muteToggle", muteToggle);
        Bind(data, "fullscreenToggle", fullscreenToggle);
        Bind(data, "logoutButton", logoutButton);
        Bind(data, "quitButton", quitButton);
        Bind(data, "returnHomeButton", returnHomeButton);
        Bind(data, "closeButton", closeButton);
        Bind(data, "logoutConfirmRoot", confirmRoot);
        Bind(data, "logoutConfirmButton", confirmButton);
        Bind(data, "logoutCancelButton", cancelButton);
        Bind(data, "versionText", versionText);

        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        int bindingCount = data.ctrlItemDatas.Count;
        UnityEngine.Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[SettingsPrefabBuilder] 已生成 {PrefabPath}（prefab={(prefab != null)}，绑定 {bindingCount} 项）");
    }

    // ==================== 控件构造 ====================

    private static Slider NewSliderRow(Transform parent, string rowName, string label, string sliderName, string valueName,
        float top, Sprite round9, Sprite circle, TMP_FontAsset font, Color track, Color fill, Color textMain, Color textSub,
        out TextMeshProUGUI valueText)
    {
        GameObject row = new GameObject(rowName, typeof(RectTransform));
        RectTransform rowRt = (RectTransform)row.transform;
        rowRt.SetParent(parent, false);
        TopLeft(rowRt, 60f, top, 900f, 40f);

        var labelText = NewText("Label", row.transform, label, 24, textMain, font, FontStyles.Normal);
        RectTransform lt = (RectTransform)labelText.transform;
        lt.anchorMin = lt.anchorMax = new Vector2(0f, 0.5f);
        lt.pivot = new Vector2(0f, 0.5f);
        lt.anchoredPosition = new Vector2(0f, 0f);
        lt.sizeDelta = new Vector2(120f, 34f);
        labelText.alignment = TextAlignmentOptions.Left;

        Slider slider = NewSlider(sliderName, row.transform, round9, circle, track, fill);
        RectTransform st = (RectTransform)slider.transform;
        st.anchorMin = st.anchorMax = new Vector2(0f, 0.5f);
        st.pivot = new Vector2(0f, 0.5f);
        st.anchoredPosition = new Vector2(150f, 0f);
        st.sizeDelta = new Vector2(420f, 28f);

        valueText = NewText(valueName, row.transform, "100%", 22, textSub, font, FontStyles.Normal);
        RectTransform vt = (RectTransform)valueText.transform;
        vt.anchorMin = vt.anchorMax = new Vector2(0f, 0.5f);
        vt.pivot = new Vector2(0f, 0.5f);
        vt.anchoredPosition = new Vector2(596f, 0f);
        vt.sizeDelta = new Vector2(110f, 30f);
        valueText.alignment = TextAlignmentOptions.Left;

        return slider;
    }

    private static Slider NewSlider(string name, Transform parent, Sprite round9, Sprite circle, Color track, Color fill)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var slider = go.AddComponent<Slider>();

        Image bg = NewImage("Background", go.transform, round9, track, true);
        RectTransform bgRt = (RectTransform)bg.transform;
        bgRt.anchorMin = new Vector2(0f, 0.5f);
        bgRt.anchorMax = new Vector2(1f, 0.5f);
        bgRt.pivot = new Vector2(0.5f, 0.5f);
        bgRt.anchoredPosition = Vector2.zero;
        bgRt.sizeDelta = new Vector2(0f, 10f);
        bg.raycastTarget = true;

        GameObject fillArea = new GameObject("Fill Area", typeof(RectTransform));
        RectTransform faRt = (RectTransform)fillArea.transform;
        faRt.SetParent(go.transform, false);
        faRt.anchorMin = new Vector2(0f, 0.5f);
        faRt.anchorMax = new Vector2(1f, 0.5f);
        faRt.pivot = new Vector2(0.5f, 0.5f);
        faRt.anchoredPosition = Vector2.zero;
        faRt.sizeDelta = new Vector2(-16f, 10f);

        Image fillImg = NewImage("Fill", fillArea.transform, round9, fill, true);
        RectTransform fillRt = (RectTransform)fillImg.transform;
        fillRt.anchorMin = new Vector2(0f, 0f);
        fillRt.anchorMax = new Vector2(1f, 1f);
        fillRt.offsetMin = Vector2.zero;
        fillRt.offsetMax = Vector2.zero;
        fillImg.raycastTarget = false;

        GameObject handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
        RectTransform haRt = (RectTransform)handleArea.transform;
        haRt.SetParent(go.transform, false);
        haRt.anchorMin = new Vector2(0f, 0.5f);
        haRt.anchorMax = new Vector2(1f, 0.5f);
        haRt.pivot = new Vector2(0.5f, 0.5f);
        haRt.anchoredPosition = Vector2.zero;
        haRt.sizeDelta = new Vector2(-20f, 28f);

        Image handle = NewImage("Handle", handleArea.transform, circle, Color.white);
        RectTransform handleRt = (RectTransform)handle.transform;
        handleRt.sizeDelta = new Vector2(26f, 26f);

        slider.fillRect = fillRt;
        slider.handleRect = handleRt;
        slider.targetGraphic = handle;
        slider.direction = Slider.Direction.LeftToRight;
        slider.minValue = 0f;
        slider.maxValue = 1f;
        slider.value = 1f;
        return slider;
    }

    private static Toggle NewToggleRow(Transform parent, string name, string label, float top, Sprite round9, Sprite circle,
        TMP_FontAsset font, Color track, Color fill, Color textMain, float width)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var toggle = go.AddComponent<Toggle>();

        Image box = NewImage("Background", go.transform, round9, track, true);
        RectTransform boxRt = (RectTransform)box.transform;
        boxRt.anchorMin = boxRt.anchorMax = new Vector2(0f, 0.5f);
        boxRt.pivot = new Vector2(0f, 0.5f);
        boxRt.anchoredPosition = Vector2.zero;
        boxRt.sizeDelta = new Vector2(28f, 28f);

        Image check = NewImage("Checkmark", box.transform, circle, fill);
        RectTransform checkRt = (RectTransform)check.transform;
        checkRt.anchorMin = checkRt.anchorMax = new Vector2(0.5f, 0.5f);
        checkRt.pivot = new Vector2(0.5f, 0.5f);
        checkRt.anchoredPosition = Vector2.zero;
        checkRt.sizeDelta = new Vector2(18f, 18f);

        var labelText = NewText("Label", go.transform, label, 24, textMain, font, FontStyles.Normal);
        RectTransform lt = (RectTransform)labelText.transform;
        lt.anchorMin = lt.anchorMax = new Vector2(0f, 0.5f);
        lt.pivot = new Vector2(0f, 0.5f);
        lt.anchoredPosition = new Vector2(40f, 0f);
        lt.sizeDelta = new Vector2(width - 40f, 32f);
        labelText.alignment = TextAlignmentOptions.Left;
        labelText.raycastTarget = false;

        toggle.targetGraphic = box;
        toggle.graphic = check;
        toggle.isOn = false;
        return toggle;
    }

    private static Button NewButton(string name, Transform parent, string label, Sprite sprite, Color bgColor, Color textColor,
        TMP_FontAsset font, int fontSize)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);

        Image img = go.AddComponent<Image>();
        img.sprite = sprite;
        img.type = sprite != null && sprite.border.sqrMagnitude > 0f ? Image.Type.Sliced : Image.Type.Simple;
        img.color = bgColor;
        img.raycastTarget = true;

        var button = go.AddComponent<Button>();
        button.targetGraphic = img;
        ColorBlock colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(0.95f, 0.95f, 0.95f, 1f);
        colors.pressedColor = new Color(0.82f, 0.82f, 0.82f, 1f);
        colors.disabledColor = new Color(0.7f, 0.7f, 0.7f, 0.6f);
        button.colors = colors;

        var text = NewText("Label", go.transform, label, fontSize, textColor, font, FontStyles.Bold);
        Stretch((RectTransform)text.transform);
        text.alignment = TextAlignmentOptions.Center;
        text.raycastTarget = false;

        return button;
    }

    private static TextMeshProUGUI NewText(string name, Transform parent, string content, int fontSize, Color color,
        TMP_FontAsset font, FontStyles style)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var text = go.AddComponent<TextMeshProUGUI>();
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
        var img = go.AddComponent<Image>();
        img.sprite = sprite;
        img.type = sliced && sprite != null && sprite.border.sqrMagnitude > 0f ? Image.Type.Sliced : Image.Type.Simple;
        img.color = color;
        return img;
    }

    // ==================== 布局辅助 ====================

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    /// <summary>左上角锚点定位（anchor/pivot 都是左上，y 向下为正）</summary>
    private static void TopLeft(RectTransform rt, float x, float top, float width, float height)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -top);
        rt.sizeDelta = new Vector2(width, height);
    }

    private static void PositionButton(RectTransform rt, float x, float top, float width, float height)
    {
        TopLeft(rt, x, top, width, height);
    }

    private static void Bind(UIControlData data, string name, Component component)
    {
        data.ctrlItemDatas.Add(new CtrlItemData
        {
            name = name,
            type = component != null ? component.GetType().Name : string.Empty,
            targets = new UnityEngine.Object[] { component },
        });
    }

    private static void Bind(UIControlData data, string name, GameObject go)
    {
        data.ctrlItemDatas.Add(new CtrlItemData
        {
            name = name,
            type = "GameObject",
            targets = new UnityEngine.Object[] { go },
        });
    }

    private static void EnsureFolder(string parent, string child)
    {
        string full = parent + "/" + child;
        if (!AssetDatabase.IsValidFolder(full)) AssetDatabase.CreateFolder(parent, child);
    }

    /// <summary>
    /// 生成（或复用）一张白色圆角矩形贴图并导入为九宫格 Sprite，用于面板/按钮底图。
    /// border 为九宫格边距（0 表示整图拉伸，如圆形）。
    /// </summary>
    private static Sprite EnsureRoundedSprite(string assetPath, int size, int radius, int border)
    {
        string fullPath = Path.Combine(Directory.GetCurrentDirectory(), assetPath);
        if (!File.Exists(fullPath))
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color32[] pixels = new Color32[size * size];
            float half = size * 0.5f;
            float r = Mathf.Clamp(radius, 0f, half);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // 圆角矩形 SDF：到内部矩形的距离 - 圆角半径
                    float px = Mathf.Abs(x + 0.5f - half) - (half - r);
                    float py = Mathf.Abs(y + 0.5f - half) - (half - r);
                    float outside = Mathf.Sqrt(Mathf.Max(px, 0f) * Mathf.Max(px, 0f) + Mathf.Max(py, 0f) * Mathf.Max(py, 0f));
                    float dist = outside + Mathf.Min(Mathf.Max(px, py), 0f) - r;
                    float alpha = Mathf.Clamp01(0.5f - dist);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            File.WriteAllBytes(fullPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        }

        TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (importer != null)
        {
            bool dirty = false;
            if (importer.textureType != TextureImporterType.Sprite) { importer.textureType = TextureImporterType.Sprite; dirty = true; }
            if (importer.spriteImportMode != SpriteImportMode.Single) { importer.spriteImportMode = SpriteImportMode.Single; dirty = true; }
            if (importer.alphaIsTransparency == false) { importer.alphaIsTransparency = true; dirty = true; }
            if (importer.mipmapEnabled) { importer.mipmapEnabled = false; dirty = true; }
            if (importer.filterMode != FilterMode.Bilinear) { importer.filterMode = FilterMode.Bilinear; dirty = true; }
            if (importer.wrapMode != TextureWrapMode.Clamp) { importer.wrapMode = TextureWrapMode.Clamp; dirty = true; }
            if (importer.maxTextureSize < size) { importer.maxTextureSize = Mathf.Max(256, size * 2); dirty = true; }
            if (importer.textureCompression != TextureImporterCompression.Uncompressed) { importer.textureCompression = TextureImporterCompression.Uncompressed; dirty = true; }

            TextureImporterSettings settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            if (settings.spriteBorder != new Vector4(border, border, border, border))
            {
                settings.spriteBorder = new Vector4(border, border, border, border);
                settings.spritePixelsPerUnit = 100f;
                importer.SetTextureSettings(settings);
                dirty = true;
            }
            if (dirty) importer.SaveAndReimport();
        }

        return AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
    }
}
