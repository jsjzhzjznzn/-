using System.Collections.Generic;
using System.IO;
using SkierFramework;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 主页（HomeView）视觉与排版重建 —— 火影式排布，可重复执行。
///
/// 版式：左上玩家信息、右上设置 + 社交圆钮一排、左侧竖排斜切功能标签（对战/副本/挑战/活动）、
///       右侧角色卡立绘、右下「开始冒险」主按钮。
/// 只改视觉与 RectTransform：按钮对象名、红点 RedDotItem、UIControlData 绑定全部沿用；
/// 新增的「开始冒险」按钮会自动补一条 UIControlData 绑定（对应 HomeView.mainCtaButton）。
/// 菜单：Tools/UI/重做主页面板
/// </summary>
public static class HomeViewSkinBuilder
{
    private const string PrefabPath = "Assets/Game/UI/Home/HomeView.prefab";
    private const string BackgroundPath = "Assets/Game/Resources/ui/Theme/home_bg.png";
    private const string RoundedPath = "Assets/Game/Resources/ui/Theme/panel_round.png";
    private const string CirclePath = "Assets/Game/Resources/ui/Theme/circle.png";
    private const string ChipPath = "Assets/Game/Resources/ui/Theme/btn_round.png";
    private const string FontPath = "Assets/Codely/Fonts/NotoSansSC-Regular SDF.asset";
    private const string IconsFolder = "Assets/Game/UI/Home/Art/Icons";
    private const string ArtFolder = "Assets/Game/UI/Home/Art";
    /// <summary>右侧立绘：项目里现有的角色卡（艾莲）；有专属立绘时替换成同名文件即可</summary>
    private const string CharacterArtPath = "Assets/Game/UI/Battle/Portraits/Snipaste_2026-09-10_18-25-22.png";

    // ── 左侧竖排功能标签 ────────────────────────────────────
    private const float TabWidth = 360f;
    private const float TabHeight = 96f;
    private const float TabGap = 14f;
    private const float TabLeft = 48f;
    /// <summary>第一个标签的中心 y（相对屏幕顶边，向下为负）</summary>
    private const float TabFirstY = -300f;

    // ── 右上社交圆钮 ────────────────────────────────────────
    private const float SocialSize = 84f;
    private const float SocialGap = 10f;
    private const float SocialRight = 40f;
    private const float SocialCenterY = -104f;
    private const float SocialLabelY = -56f;

    // ── 右侧立绘 / 右下主按钮 ───────────────────────────────
    private const float ArtWidth = 380f;
    private const float ArtHeight = 768f;
    private const float CtaWidth = 320f;
    private const float CtaHeight = 88f;

    // ── 配色（浅色主题 + 火影式橙金强调）────────────────────
    private static readonly Color CreamFill = new Color32(0xFF, 0xFD, 0xF8, 0xFF);
    private static readonly Color GoldFrame = new Color32(0xD9, 0xB2, 0x5C, 0xFF);
    private static readonly Color GoldDeep = new Color32(0x7E, 0x5B, 0x18, 0xFF);
    private static readonly Color PrimaryFill = new Color32(0xE8, 0x62, 0x2B, 0xFF);
    private static readonly Color DarkFill = new Color32(0x24, 0x28, 0x30, 0xEC);
    private static readonly Color LabelDark = new Color32(0x5A, 0x47, 0x28, 0xFF);
    private static readonly Color SubtitleLight = new Color32(0xE8, 0xE2, 0xD6, 0xFF);

    private struct Entry
    {
        public string field;
        public string title;
        public string subtitle;
        public string glyph;
        /// <summary>Icons 目录下的图标名（不含扩展名）；缺失时回退单字 glyph</summary>
        public string icon;
    }

    /// <summary>主玩法：左侧竖排斜切标签（第一个=主按钮配橙色）</summary>
    private static readonly Entry[] PlayEntries =
    {
        new Entry { field = "battleButton", title = "对战", subtitle = "联机竞技", glyph = "战", icon = "card_battle" },
        new Entry { field = "challengeButton", title = "挑战", subtitle = "击杀竞速", glyph = "挑", icon = "card_challenge" },
        new Entry { field = "activityButton", title = "活动", subtitle = "唤取 · 签到", glyph = "活", icon = "card_activity" },
    };

    /// <summary>社交/系统：右上圆钮一排</summary>
    private static readonly Entry[] SocialEntries =
    {
        new Entry { field = "characterButton", title = "角色", glyph = "色", icon = "icon_character" },
        new Entry { field = "friendButton", title = "好友", glyph = "友", icon = "icon_friend" },
        new Entry { field = "mailButton", title = "邮件", glyph = "邮", icon = "icon_mail" },
        new Entry { field = "questButton", title = "任务", glyph = "任", icon = "icon_quest" },
        new Entry { field = "achievementButton", title = "成就", glyph = "成", icon = "icon_quest" },
        new Entry { field = "leaderboardButton", title = "排行榜", glyph = "榜", icon = "icon_leaderboard" },
        new Entry { field = "worldButton", title = "世界", glyph = "世", icon = "icon_world" },
        new Entry { field = "agentButton", title = "阿福", glyph = "福", icon = "icon_afu" },
        new Entry { field = "bagButton", title = "背包", glyph = "包", icon = "icon_bag" },
    };

    [MenuItem("Tools/UI/重做主页面板")]
    public static void Build()
    {
        GameObject contents = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (contents == null)
        {
            Debug.LogError("[HomeViewSkin] 找不到 " + PrefabPath);
            return;
        }

        Sprite bgSprite = AssetDatabase.LoadAssetAtPath<Sprite>(BackgroundPath);
        Sprite circle = AssetDatabase.LoadAssetAtPath<Sprite>(CirclePath);
        Sprite chip = AssetDatabase.LoadAssetAtPath<Sprite>(ChipPath);
        TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
        Sprite slant = EnsureSlantSprite(Mathf.RoundToInt(TabWidth), Mathf.RoundToInt(TabHeight));

        // ---------- 1) 整屏背景 + 压暗 ----------
        EnsureBackground(contents.transform, bgSprite);
        EnsureOverlay(contents.transform, "HomeScrim", new Color(0.08f, 0.06f, 0.04f, 0.16f), 1,
            Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

        // ---------- 2) 右侧角色立绘（在入口容器之下）----------
        EnsureCharacterArt(contents.transform, LoadCharacterArt());

        // ---------- 3) EntryBar 改为全屏透明容器，清掉旧底栏装饰 ----------
        Transform entryBar = contents.transform.Find("EntryBar");
        if (entryBar == null)
        {
            Debug.LogError("[HomeViewSkin] 找不到 EntryBar");
            PrefabUtility.UnloadPrefabContents(contents);
            return;
        }

        var barRt = (RectTransform)entryBar;
        barRt.anchorMin = Vector2.zero;
        barRt.anchorMax = Vector2.one;
        barRt.pivot = new Vector2(0.5f, 0.5f);
        barRt.offsetMin = Vector2.zero;
        barRt.offsetMax = Vector2.zero;
        var barImg = entryBar.GetComponent<Image>();
        if (barImg != null) { barImg.color = Color.clear; barImg.raycastTarget = false; }
        foreach (var chrome in new[] { "BarBackground", "BarTopLine", "BarShadow", "BarDivider" })
        {
            Transform old = entryBar.Find(chrome);
            if (old != null) Object.DestroyImmediate(old.gameObject);
        }

        // ---------- 4) 左侧竖排斜切功能标签 ----------
        for (int i = 0; i < PlayEntries.Length; i++)
        {
            BuildSlantTab(entryBar, PlayEntries[i], i, slant, font);
        }

        // ---------- 5) 右上社交圆钮一排（新增入口若预制体里还没有节点，就地补建并写入绑定）----------
        for (int i = 0; i < SocialEntries.Length; i++)
        {
            EnsureSocialButton(contents, entryBar, SocialEntries[i]);
            BuildSocialIcon(entryBar, SocialEntries[i], i, circle, font);
        }

        // ---------- 6) 右下主按钮 ----------
        BuildMainCta(contents.transform, slant, font);
        EnsureCtrlBinding(contents, "mainCtaButton", contents.transform.Find("mainCtaButton").GetComponent<Button>());

        // ---------- 7) 顶部：玩家信息胶囊 / 设置键 ----------
        BuildPlayerInfo(contents.transform, circle, chip, font);
        EnsureCtrlBinding(contents, "playerInfoButton", contents.transform.Find("playerInfo").GetComponent<Button>());
        BuildSettingsButton(contents.transform, chip, font);

        PrefabUtility.SaveAsPrefabAsset(contents, PrefabPath);
        PrefabUtility.UnloadPrefabContents(contents);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        bool hasArt = AssetDatabase.LoadAssetAtPath<Sprite>(CharacterArtPath) != null;
        Debug.Log($"[HomeViewSkin] 重排完成（火影式）：左竖标签 {PlayEntries.Length} / 右上圆钮 {SocialEntries.Length} / 立绘={(hasArt ? "有" : "缺失")} / 斜切贴图={(slant != null ? "有" : "缺失")}");
    }

    // ─────────────────────────────────────────────────────────
    // 左侧斜切标签
    // ─────────────────────────────────────────────────────────
    private static void BuildSlantTab(Transform parent, Entry entry, int index, Sprite slant, TMP_FontAsset font)
    {
        Transform t = parent.Find(entry.field);
        if (t == null) { Debug.LogWarning("[HomeViewSkin] 缺少按钮 " + entry.field); return; }

        var rt = (RectTransform)t;
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 0.5f);
        rt.anchoredPosition = new Vector2(TabLeft, TabFirstY - index * (TabHeight + TabGap));
        rt.sizeDelta = new Vector2(TabWidth, TabHeight);
        rt.localScale = Vector3.one;

        bool primary = index == 0;

        // 按钮本体 = 金色斜切外框（命中面）
        var edge = t.GetComponent<Image>();
        if (edge == null) edge = t.gameObject.AddComponent<Image>();
        edge.sprite = slant;
        edge.type = Image.Type.Simple;
        edge.color = GoldFrame;
        edge.raycastTarget = true;

        ClearGenerated(t, "TabFill", "CardInner", "IconBack", "IconFill", "Icon", "Glyph", "Title", "Subtitle", "Label");

        // 内片：主按钮橙、其余深灰
        var fill = CreateImage(t, "TabFill", slant, primary ? PrimaryFill : DarkFill, Image.Type.Simple);
        StretchInset((RectTransform)fill.transform, 3f);

        // 图标/单字（大卡图标按 256 导入：显示 56px 留 4 倍余量，避免压缩发糊）
        Sprite iconSprite = LoadIcon(entry.icon, 256);
        if (iconSprite != null)
        {
            var icon = CreateImage(t, "Icon", iconSprite, Color.white, Image.Type.Simple);
            var irt = (RectTransform)icon.transform;
            irt.anchorMin = irt.anchorMax = new Vector2(0.5f, 0.5f);
            irt.pivot = new Vector2(0.5f, 0.5f);
            irt.anchoredPosition = new Vector2(-104f, 0f);
            irt.sizeDelta = new Vector2(56f, 56f);
        }
        else
        {
            CreateText(t, "Glyph", entry.glyph, 34f, CreamFill, new Vector2(64f, 48f),
                new Vector2(-104f, 0f), TextAlignmentOptions.Center, font, FontStyles.Normal);
        }

        // 标题 + 副标题（左对齐，靠图标右侧）
        CreateText(t, "Title", entry.title, 30f, CreamFill, new Vector2(180f, 36f),
            new Vector2(6f, 16f), TextAlignmentOptions.Left, font, FontStyles.Bold);
        CreateText(t, "Subtitle", entry.subtitle, 15f, primary ? CreamFill : SubtitleLight, new Vector2(180f, 24f),
            new Vector2(6f, -18f), TextAlignmentOptions.Left, font, FontStyles.Normal);
    }

    // ─────────────────────────────────────────────────────────
    // 右上社交圆钮
    // ─────────────────────────────────────────────────────────
    /// <summary>
    /// 预制体里缺少的入口按钮就地补建（RectTransform + Image + Button），并登记 UIControlData 绑定。
    /// 新入口只改 SocialEntries 即可：节点缺失时自动创建，排位由 BuildSocialIcon 统一计算。
    /// </summary>
    private static void EnsureSocialButton(GameObject root, Transform entryBar, Entry entry)
    {
        if (entryBar.Find(entry.field) != null) return;

        var go = new GameObject(entry.field, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(entryBar, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(SocialSize, SocialSize);

        var image = go.GetComponent<Image>();
        image.raycastTarget = true;
        go.GetComponent<Button>().targetGraphic = image;

        EnsureCtrlBinding(root, entry.field, go.GetComponent<Button>());
        Debug.Log("[HomeViewSkin] 补建入口按钮 " + entry.field);
    }

    private static void BuildSocialIcon(Transform parent, Entry entry, int index, Sprite circle, TMP_FontAsset font)
    {
        Transform t = parent.Find(entry.field);
        if (t == null) { Debug.LogWarning("[HomeViewSkin] 缺少按钮 " + entry.field); return; }

        var rt = (RectTransform)t;
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        float centerX = -(SocialRight + (SocialEntries.Length - 1 - index) * (SocialSize + SocialGap) + SocialSize * 0.5f);
        rt.anchoredPosition = new Vector2(centerX, SocialCenterY);
        rt.sizeDelta = new Vector2(SocialSize, SocialSize);
        rt.localScale = Vector3.one;

        // 按钮本体 = 金色圆环，内嵌米白圆片
        var ring = t.GetComponent<Image>();
        if (ring == null) ring = t.gameObject.AddComponent<Image>();
        ring.sprite = circle;
        ring.type = Image.Type.Simple;
        ring.color = GoldFrame;
        ring.raycastTarget = true;

        ClearGenerated(t, "Fill", "CardInner", "TabFill", "IconBack", "IconFill", "Icon", "Glyph", "Label", "Title", "Subtitle");

        var fill = CreateImage(t, "Fill", circle, CreamFill, Image.Type.Simple);
        var fillRt = (RectTransform)fill.transform;
        fillRt.anchorMin = fillRt.anchorMax = new Vector2(0.5f, 0.5f);
        fillRt.pivot = new Vector2(0.5f, 0.5f);
        fillRt.anchoredPosition = Vector2.zero;
        fillRt.sizeDelta = new Vector2(SocialSize - 8f, SocialSize - 8f);

        Sprite socialIcon = LoadIcon(entry.icon, 128);
        if (socialIcon != null)
        {
            var icon = CreateImage(t, "Icon", socialIcon, Color.white, Image.Type.Simple);
            var irt = (RectTransform)icon.transform;
            irt.anchorMin = irt.anchorMax = new Vector2(0.5f, 0.5f);
            irt.pivot = new Vector2(0.5f, 0.5f);
            irt.anchoredPosition = new Vector2(0f, 3f);
            irt.sizeDelta = new Vector2(46f, 46f);
        }
        else
        {
            CreateText(t, "Glyph", entry.glyph, 34f, GoldDeep, new Vector2(60f, 50f),
                new Vector2(0f, 3f), TextAlignmentOptions.Center, font, FontStyles.Normal);
        }

        // 标签统一挂按钮下方
        var label = FindLabel(t);
        if (label == null)
        {
            label = CreateText(t, "Label", entry.title, 15f, LabelDark, new Vector2(120f, 22f),
                new Vector2(0f, SocialLabelY), TextAlignmentOptions.Center, font, FontStyles.Normal);
        }
        var labelRt = (RectTransform)label.transform;
        labelRt.anchorMin = labelRt.anchorMax = new Vector2(0.5f, 0.5f);
        labelRt.pivot = new Vector2(0.5f, 0.5f);
        labelRt.anchoredPosition = new Vector2(0f, SocialLabelY);
        labelRt.sizeDelta = new Vector2(120f, 22f);
        label.text = entry.title;
        label.fontSize = 15f;
        label.color = LabelDark;
        label.alignment = TextAlignmentOptions.Center;
        label.fontStyle = FontStyles.Normal;
        label.raycastTarget = false;
        if (font != null && label.font == null) { label.font = font; label.fontSharedMaterial = font.material; }

        // 红点角标压到圆钮右上
        foreach (var badgeName in new[] { "EntryBadge", "QuestBadge", "MailBadge" })
        {
            Transform badge = t.Find(badgeName);
            if (badge == null) continue;
            var brt = (RectTransform)badge;
            brt.anchorMin = brt.anchorMax = new Vector2(0.5f, 0.5f);
            brt.pivot = new Vector2(0.5f, 0.5f);
            brt.anchoredPosition = new Vector2(32f, 32f);
            brt.sizeDelta = new Vector2(24f, 24f);
            var bimg = badge.GetComponent<Image>();
            if (bimg != null)
            {
                bimg.sprite = circle;
                bimg.type = Image.Type.Simple;
                bimg.color = new Color32(0xE6, 0x33, 0x30, 0xFF);
                bimg.raycastTarget = false;
            }
            foreach (var bt in badge.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                bt.fontSize = 12f;
                bt.color = Color.white;
                bt.alignment = TextAlignmentOptions.Center;
                bt.raycastTarget = false;
            }
        }
    }

    // ─────────────────────────────────────────────────────────
    // 右下主按钮
    // ─────────────────────────────────────────────────────────
    private static void BuildMainCta(Transform root, Sprite slant, TMP_FontAsset font)
    {
        Transform t = root.Find("mainCtaButton");
        if (t == null)
        {
            var go = new GameObject("mainCtaButton", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(root, false);
            t = go.transform;
        }

        var rt = (RectTransform)t;
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(1f, 0f);
        rt.anchoredPosition = new Vector2(-48f, 44f);
        rt.sizeDelta = new Vector2(CtaWidth, CtaHeight);
        rt.localScale = Vector3.one;

        var edge = t.GetComponent<Image>();
        if (edge == null) edge = t.gameObject.AddComponent<Image>();
        edge.sprite = slant;
        edge.type = Image.Type.Simple;
        edge.color = GoldFrame;
        edge.raycastTarget = true;

        var btn = t.GetComponent<Button>();
        if (btn == null) btn = t.gameObject.AddComponent<Button>();
        btn.targetGraphic = edge;

        ClearGenerated(t, "CtaFill", "Label", "Icon", "Title", "Subtitle");

        var fill = CreateImage(t, "CtaFill", slant, PrimaryFill, Image.Type.Simple);
        StretchInset((RectTransform)fill.transform, 4f);

        CreateText(t, "Label", "开始冒险", 34f, CreamFill, new Vector2(250f, 52f),
            new Vector2(8f, 0f), TextAlignmentOptions.Center, font, FontStyles.Bold);
    }

    // ─────────────────────────────────────────────────────────
    // 右侧立绘
    // ─────────────────────────────────────────────────────────
    private static void EnsureCharacterArt(Transform root, Sprite art)
    {
        Transform existing = root.Find("HomeCharacterArt");
        GameObject go = existing != null ? existing.gameObject : new GameObject("HomeCharacterArt", typeof(RectTransform), typeof(Image));
        var rt = (RectTransform)go.transform;
        if (existing == null)
        {
            rt.SetParent(root, false);
            rt.SetSiblingIndex(2);   // 背景/压暗之上、入口容器之下
        }

        rt.anchorMin = rt.anchorMax = new Vector2(1f, 0.5f);
        rt.pivot = new Vector2(1f, 0.5f);
        rt.anchoredPosition = new Vector2(-24f, -24f);
        rt.sizeDelta = new Vector2(ArtWidth, ArtHeight);

        var img = go.GetComponent<Image>();
        if (img == null) img = go.AddComponent<Image>();
        img.sprite = art;
        img.type = Image.Type.Simple;
        img.preserveAspect = true;
        img.raycastTarget = false;
        img.color = art != null ? Color.white : new Color(0.20f, 0.22f, 0.26f, 0.55f);
    }

    private static Sprite LoadCharacterArt()
    {
        if (!File.Exists(CharacterArtPath)) return null;
        var importer = AssetImporter.GetAtPath(CharacterArtPath) as TextureImporter;
        if (importer != null && importer.textureType != TextureImporterType.Sprite)
        {
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.maxTextureSize = 1024;
            importer.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Sprite>(CharacterArtPath);
    }

    // ─────────────────────────────────────────────────────────
    // 顶部信息
    // ─────────────────────────────────────────────────────────
    private static void BuildPlayerInfo(Transform root, Sprite circle, Sprite chip, TMP_FontAsset font)
    {
        Transform info = root.Find("playerInfo");
        if (info == null)
        {
            var go = new GameObject("playerInfo", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(root, false);
            info = go.transform;
        }
        var rt = (RectTransform)info;
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(24f, -24f);
        rt.sizeDelta = new Vector2(320f, 60f);

        var bg = info.GetComponent<Image>();
        if (bg == null) bg = info.gameObject.AddComponent<Image>();
        bg.sprite = chip;
        bg.type = Image.Type.Sliced;
        bg.color = GoldFrame;
        bg.raycastTarget = false;

        // 整枚名片是「共鸣者影像簿」的入口：按钮挂在根上，内片负责接收射线
        var infoButton = info.GetComponent<Button>();
        if (infoButton == null) infoButton = info.gameObject.AddComponent<Button>();
        infoButton.targetGraphic = null;
        infoButton.transition = Selectable.Transition.None;

        ClearGenerated(info, "Avatar", "AvatarFill", "AvatarGlyph", "InfoFill");

        var infoFill = CreateImage(info, "InfoFill", chip, new Color(0.99f, 0.97f, 0.93f, 0.97f), Image.Type.Sliced);
        StretchInset((RectTransform)infoFill.transform, 2f);
        infoFill.GetComponent<Image>().raycastTarget = true;

        var avatar = CreateImage(info, "Avatar", circle, GoldFrame, Image.Type.Simple);
        var art = (RectTransform)avatar.transform;
        art.anchorMin = art.anchorMax = new Vector2(0f, 0.5f);
        art.pivot = new Vector2(0.5f, 0.5f);
        art.anchoredPosition = new Vector2(35f, 0f);
        art.sizeDelta = new Vector2(46f, 46f);

        var avatarFill = CreateImage(avatar.transform, "AvatarFill", circle, CreamFill, Image.Type.Simple);
        var afrt = (RectTransform)avatarFill.transform;
        afrt.anchorMin = afrt.anchorMax = new Vector2(0.5f, 0.5f);
        afrt.pivot = new Vector2(0.5f, 0.5f);
        afrt.anchoredPosition = Vector2.zero;
        afrt.sizeDelta = new Vector2(39f, 39f);

        CreateText(avatar.transform, "AvatarGlyph", "冒", 21f, GoldDeep, new Vector2(36f, 30f),
            Vector2.zero, TextAlignmentOptions.Center, font, FontStyles.Normal);

        Transform textT = info.Find("playerInfoText");
        if (textT == null)
        {
            var go = new GameObject("playerInfoText", typeof(RectTransform));
            go.transform.SetParent(info, false);
            textT = go.transform;
        }
        var trt = (RectTransform)textT;
        trt.anchorMin = new Vector2(0f, 0.5f);
        trt.anchorMax = new Vector2(0f, 0.5f);
        trt.pivot = new Vector2(0f, 0.5f);
        trt.anchoredPosition = new Vector2(68f, 0f);
        trt.sizeDelta = new Vector2(236f, 34f);

        var text = textT.GetComponent<TextMeshProUGUI>();
        if (text == null) text = textT.gameObject.AddComponent<TextMeshProUGUI>();
        if (font != null) { text.font = font; text.fontSharedMaterial = font.material; }
        text.fontSize = 20f;
        text.color = UITheme.TextMain;
        text.alignment = TextAlignmentOptions.Left;
        text.raycastTarget = false;
        // 内片/头像是在这之后创建的，文本必须回到最上层，否则会被米白内片盖住
        textT.SetAsLastSibling();
    }

    private static void BuildSettingsButton(Transform root, Sprite chip, TMP_FontAsset font)
    {
        Transform settings = root.Find("settingsButton");
        if (settings == null) return;

        var rt = (RectTransform)settings;
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(1f, 1f);
        rt.anchoredPosition = new Vector2(-24f, -24f);
        rt.sizeDelta = new Vector2(104f, 44f);

        var bg = settings.GetComponent<Image>();
        if (bg != null)
        {
            bg.sprite = chip;
            bg.type = Image.Type.Sliced;
            bg.color = GoldFrame;
            bg.raycastTarget = true;
        }

        ClearGenerated(settings, "Fill");
        var fill = CreateImage(settings, "Fill", chip, new Color(0.99f, 0.97f, 0.93f, 0.97f), Image.Type.Sliced);
        StretchInset((RectTransform)fill.transform, 2f);
        fill.transform.SetSiblingIndex(0);

        foreach (var text in settings.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            text.text = "设置";
            text.fontSize = 18f;
            text.color = UITheme.TextMain;
            text.alignment = TextAlignmentOptions.Center;
            text.raycastTarget = false;
            if (font != null) { text.font = font; text.fontSharedMaterial = font.material; }
            Stretch((RectTransform)text.transform);
        }
    }

    // ─────────────────────────────────────────────────────────
    // 斜切贴图（平行四边形，Simple 拉伸；固定尺寸标签用，避免九宫格把斜边拉变形）
    // ─────────────────────────────────────────────────────────
    private static Sprite EnsureSlantSprite(int w, int h)
    {
        string path = ArtFolder + "/SlantTab.png";
        var existing = AssetDatabase.LoadAssetAtPath<Sprite>(path);
        if (existing != null) return existing;

        if (!AssetDatabase.IsValidFolder(ArtFolder)) AssetDatabase.CreateFolder("Assets/Game/UI/Home", "Art");

        int slant = Mathf.RoundToInt(h * 0.28f);   // 上边右移量 = 火影式斜切角
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        var px = new Color32[w * h];
        for (int y = 0; y < h; y++)
        {
            float t = h <= 1 ? 1f : (float)y / (h - 1);        // y=0 底边
            int start = Mathf.RoundToInt(slant * t);
            int end = start + (w - slant);
            for (int x = 0; x < w; x++)
            {
                px[y * w + x] = (x >= start && x < end)
                    ? new Color32(255, 255, 255, 255)
                    : new Color32(255, 255, 255, 0);
            }
        }
        tex.SetPixels32(px);
        tex.Apply();
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);

        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer != null)
        {
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.maxTextureSize = 512;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.filterMode = FilterMode.Bilinear;
            importer.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }

    // ─────────────────────────────────────────────────────────
    // 工具
    // ─────────────────────────────────────────────────────────
    private static void EnsureBackground(Transform root, Sprite bgSprite)
    {
        Transform background = root.Find("HomeBackground");
        if (background == null)
        {
            var go = new GameObject("HomeBackground", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(root, false);
            rt.SetAsFirstSibling();
            background = go.transform;
        }
        var bgImage = background.GetComponent<Image>();
        if (bgImage == null) bgImage = background.gameObject.AddComponent<Image>();
        bgImage.sprite = bgSprite;
        bgImage.type = Image.Type.Simple;
        bgImage.color = bgSprite != null ? Color.white : new Color(0.10f, 0.12f, 0.15f, 1f);
        bgImage.raycastTarget = false;
        Stretch((RectTransform)background);
    }

    private static void EnsureOverlay(Transform parent, string name, Color color, int siblingIndex,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 sizeDelta)
    {
        Transform existing = parent.Find(name);
        GameObject go = existing != null ? existing.gameObject : new GameObject(name, typeof(RectTransform), typeof(Image));
        var rt = (RectTransform)go.transform;
        if (existing == null) rt.SetParent(parent, false);

        rt.SetSiblingIndex(Mathf.Clamp(siblingIndex, 0, parent.childCount - 1));
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = offsetMin;
        rt.offsetMax = Vector2.zero;

        var img = go.GetComponent<Image>();
        if (img == null) img = go.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        img.sprite = null;
        img.type = Image.Type.Simple;
    }

    /// <summary>新增的按钮要登记进 UIControlData，运行时 BindDataTo 才能按字段名绑定</summary>
    private static void EnsureCtrlBinding(GameObject root, string name, Object target)
    {
        var data = root.GetComponentInChildren<UIControlData>(true);
        if (data == null)
        {
            Debug.LogError("[HomeViewSkin] 找不到 UIControlData，绑定未登记：" + name);
            return;
        }
        if (data.ctrlItemDatas == null) data.ctrlItemDatas = new List<CtrlItemData>();
        var item = data.ctrlItemDatas.Find(x => x.name == name);
        if (item == null)
        {
            item = new CtrlItemData { name = name, type = "Button" };
            data.ctrlItemDatas.Add(item);
        }
        item.type = "Button";
        item.targets = new[] { target };
        EditorUtility.SetDirty(data);
    }

    private static GameObject CreateImage(Transform parent, string name, Sprite sprite, Color color, Image.Type type)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        var img = go.GetComponent<Image>();
        img.sprite = sprite;
        img.type = sprite != null && sprite.border.sqrMagnitude > 0f ? Image.Type.Sliced : type;
        img.color = color;
        img.raycastTarget = false;
        return go;
    }

    private static TextMeshProUGUI CreateText(Transform parent, string name, string value, float size,
        Color color, Vector2 sizeDelta, Vector2 pos, TextAlignmentOptions align, TMP_FontAsset font, FontStyles styles)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = sizeDelta;

        var text = go.AddComponent<TextMeshProUGUI>();
        if (font != null)
        {
            text.font = font;
            text.fontSharedMaterial = font.material;
        }
        text.text = value;
        text.fontSize = size;
        text.color = color;
        text.alignment = align;
        text.fontStyle = styles;
        text.raycastTarget = false;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Overflow;
        return text;
    }

    /// <summary>读取 Icons 目录下的图标；顺带保证按 Sprite 导入。缺图返回 null，调用方回退单字。</summary>
    private static Sprite LoadIcon(string iconName, int targetMax)
    {
        if (string.IsNullOrEmpty(iconName)) return null;
        string path = IconsFolder + "/" + iconName + ".png";
        if (!File.Exists(path)) return null;

        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer != null)
        {
            // 按显示尺寸给导入分辨率：1024 纹理直接压到 50px 会因采样与压缩而发糊，这里给 2~3 倍。
            if (importer.textureType != TextureImporterType.Sprite ||
                importer.spriteImportMode != SpriteImportMode.Single ||
                importer.maxTextureSize != targetMax ||
                importer.textureCompression != TextureImporterCompression.Uncompressed ||
                importer.mipmapEnabled)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.maxTextureSize = targetMax;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.filterMode = FilterMode.Bilinear;
                importer.SaveAndReimport();
            }
        }
        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }

    private static TextMeshProUGUI FindLabel(Transform button)
    {
        foreach (var t in button.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            string n = t.gameObject.name;
            if (n == "Label" || n == "Text (TMP)") return t;
        }
        return null;
    }

    private static void ClearGenerated(Transform parent, params string[] names)
    {
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            var child = parent.GetChild(i);
            foreach (var n in names)
            {
                if (child.name == n) { Object.DestroyImmediate(child.gameObject); break; }
            }
        }
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private static void StretchInset(RectTransform rt, float inset)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(inset, inset);
        rt.offsetMax = new Vector2(-inset, -inset);
    }
}
