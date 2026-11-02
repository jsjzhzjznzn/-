#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using SkierFramework;

using TMPro;

using UnityEditor;

using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 角色页预制体构建器（按参考图复刻的版式）：
/// 暖金雾面背景 + 顶部元素徽记/身份行/头像行 + 左侧六项菜单 + 中央 3D 预览 +
/// 右侧星级、等级经验条、属性行、详细信息、简介 + 右下主按钮。
/// 武器选择页在同一预制体内作为二级页签，不新增 UIType。
/// 菜单：Tools/Equipment/重建角色界面
/// </summary>
public static class CharacterViewBuilder
{
    private const string PrefabPath = "Assets/Game/UI/Character/CharacterView.prefab";
    private const string ArtDir = "Assets/Game/Resources/ui/Character";
    private const string BgPath = ArtDir + "/character_bg_haze.jpg";
    private const string BgSourcePath = ArtDir + "/character_bg_gold.jpg";
    private const string CircleSpritePath = ArtDir + "/avatar_circle.png";
    private const string RingSpritePath = ArtDir + "/avatar_ring.png";
    private const string ScrimSpritePath = ArtDir + "/right_scrim.png";
    private const string PillSpritePath = ArtDir + "/ui_pill.png";
    private const string BarSpritePath = ArtDir + "/ui_bar.png";
    private const string StarSpritePath = ArtDir + "/ui_star.png";
    private const string ChevronSpritePath = ArtDir + "/ui_chevron.png";
    private const string EmblemSpritePath = ArtDir + "/ui_emblem.png";
    private const string IconDir = ArtDir + "/Icons";

    // 参考图（1920×1080 画布）量取的关键尺寸
    private const float PanelRight = 113f;
    private const float PanelTop = 133f;
    private const float PanelWidth = 340f;

    private static readonly Color Cream = new(0.957f, 0.922f, 0.843f, 1f);
    private static readonly Color CreamSoft = new(0.957f, 0.922f, 0.843f, 0.90f);
    private static readonly Color CreamDim = new(0.957f, 0.922f, 0.843f, 0.62f);
    private static readonly Color Ink = new(0.180f, 0.165f, 0.125f, 1f);
    private static readonly Color Cyan = new(0.404f, 0.878f, 0.894f, 1f);

    private static readonly string[] AttrLabels = { "生命值上限", "攻击力", "防御力", "增伤", "减伤", "移速" };

    private static readonly string[] AttrFields =
    {
        "AttrMaxHpText", "AttrAttackText", "AttrDefenseText",
        "AttrDamageUpText", "AttrDamageDownText", "AttrMoveSpeedText",
    };

    private static readonly string[] AttrIcons =
    {
        "icon_hp", "icon_atk", "icon_def", "icon_dmgup", "icon_dmgdown", "icon_speed",
    };

    [MenuItem("Tools/Equipment/重建角色界面")]
    public static void Build()
    {
        EnsureArt();

        var wrapper = new GameObject("CharacterView", typeof(RectTransform));
        RectTransform root = wrapper.GetComponent<RectTransform>();
        Stretch(root);

        var view = wrapper.AddComponent<CharacterView>();
        var controlData = wrapper.AddComponent<UIControlData>();

        // ── 背景与右侧柔化压暗 ──────────────────────────────
        Image background = NewImage(wrapper, Color.white, LoadSprite(BgPath));
        Stretch(background.rectTransform);
        background.raycastTarget = false;

        RectTransform scrimRect = NewRect("RightScrim", root);
        scrimRect.anchorMin = new Vector2(1f, 0f);
        scrimRect.anchorMax = new Vector2(1f, 1f);
        scrimRect.pivot = new Vector2(1f, 0.5f);
        scrimRect.anchoredPosition = Vector2.zero;
        scrimRect.sizeDelta = new Vector2(860f, 0f);
        Image scrim = NewImage(scrimRect.gameObject, new Color(1f, 1f, 1f, 0.8f), LoadSprite(ScrimSpritePath));
        scrim.raycastTarget = false;

        // ── 顶部：元素徽记 / 身份行 / 头像行 / 关闭 ──────────
        Image emblem = NewImage(NewRect("ElementEmblem", root).gameObject, Color.white, LoadSprite(EmblemSpritePath));
        Place(emblem.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(34f, -2f), new Vector2(86f, 86f));
        emblem.raycastTarget = false;

        TMP_Text identity = NewText("IdentityText", root, 30f, Cream, TextAlignmentOptions.Left);
        Place(identity.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(144f, -30f), new Vector2(560f, 38f));
        identity.SetText("元素 / 角色");

        RectTransform avatarRow = NewRect("CharacterScroll", root);
        Place(avatarRow, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -12f), new Vector2(220f, 86f));
        HorizontalLayoutGroup avatarLayout = avatarRow.gameObject.AddComponent<HorizontalLayoutGroup>();
        avatarLayout.spacing = 14f;
        avatarLayout.childAlignment = TextAnchor.UpperCenter;
        avatarLayout.childControlWidth = true;
        avatarLayout.childControlHeight = true;
        avatarLayout.childForceExpandWidth = false;
        avatarLayout.childForceExpandHeight = false;
        ContentSizeFitter avatarFitter = avatarRow.gameObject.AddComponent<ContentSizeFitter>();
        avatarFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        avatarFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        Sprite circle = LoadSprite(CircleSpritePath);
        Sprite ring = LoadSprite(RingSpritePath);

        RectTransform avatarTemplate = NewRect("RowTemplate", avatarRow);
        LayoutElement avatarElement = avatarTemplate.gameObject.AddComponent<LayoutElement>();
        avatarElement.preferredWidth = 68f;
        avatarElement.preferredHeight = 68f;

        RectTransform avatarFrame = NewRect("Frame", avatarTemplate);
        Stretch(avatarFrame);
        Image avatarFrameImage = NewImage(avatarFrame.gameObject, Color.white, circle);
        avatarFrameImage.raycastTarget = false;
        Mask avatarMask = avatarFrame.gameObject.AddComponent<Mask>();
        avatarMask.showMaskGraphic = false;

        RectTransform portraitRect = NewRect("Portrait", avatarFrame);
        portraitRect.anchorMin = new Vector2(0.5f, 1f);
        portraitRect.anchorMax = new Vector2(0.5f, 1f);
        portraitRect.pivot = new Vector2(0.5f, 1f);
        portraitRect.anchoredPosition = Vector2.zero;
        // 立绘是全身图：放大到 1.8 倍、顶端对齐，圆形遮罩里裁出头部
        portraitRect.sizeDelta = new Vector2(122f, 176f);
        Image portrait = NewImage(portraitRect.gameObject, Color.white, null);
        portrait.raycastTarget = false;

        Image avatarRing = NewImage(NewRect("Ring", avatarTemplate).gameObject, new Color(1f, 1f, 1f, 0.35f), ring);
        Stretch(avatarRing.rectTransform);
        // 头像行唯一可点的 Graphic：Frame 被 Mask 吃掉、Portrait/Selected 都关了射线检测
        avatarRing.raycastTarget = true;

        Image avatarSelected = NewImage(NewRect("Selected", avatarTemplate).gameObject, Cyan, LoadSprite(BarSpritePath));
        Place((RectTransform)avatarSelected.transform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, -6f), new Vector2(42f, 3f));
        avatarSelected.raycastTarget = false;

        Button avatarButton = avatarTemplate.gameObject.AddComponent<Button>();
        avatarButton.targetGraphic = avatarRing;
        avatarButton.transition = Selectable.Transition.None;
        avatarTemplate.gameObject.SetActive(false);

        Button closeButton = NewCircleButton("CloseButton", root, new Vector2(48f, 48f), Cream);
        Place((RectTransform)closeButton.transform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-30f, -20f), new Vector2(48f, 48f));
        AddCrossMark(closeButton.transform, Ink, 21f, 3.4f);

        // ── 左侧菜单：六项，属性/武器可用，其余作为版式占位 ──
        RectTransform menuRoot = NewRect("MenuRoot", root);
        Place(menuRoot, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(95f, -128f), new Vector2(190f, 430f));

        CreateMenuRow(menuRoot, "AttrButton", "属性", 0, true);
        CreateMenuRow(menuRoot, "WeaponButton", "武器", 1, true);
        CreateMenuRow(menuRoot, "RelicButton", "圣遗物", 2, false);
        CreateMenuRow(menuRoot, "ConstellationButton", "命之座", 3, false);
        CreateMenuRow(menuRoot, "TalentButton", "天赋", 4, false);
        CreateMenuRow(menuRoot, "InfoButton", "资料", 5, false);

        // ── 左右切角色箭头 ─────────────────────────────────
        RectTransform switchRoot = NewRect("SwitchRoot", root);
        Stretch(switchRoot);

        Button prevButton = NewIconButton("PrevButton", switchRoot, LoadSprite(ChevronSpritePath), CreamSoft, 46f);
        Place((RectTransform)prevButton.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(48f, -508f), new Vector2(46f, 46f));

        Button nextButton = NewIconButton("NextButton", switchRoot, LoadSprite(ChevronSpritePath), CreamSoft, 46f);
        Place((RectTransform)nextButton.transform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-48f, -508f), new Vector2(46f, 46f));
        nextButton.transform.localScale = new Vector3(-1f, 1f, 1f);

        // ── 中央 3D 预览 ───────────────────────────────────
        RectTransform previewRoot = NewRect("PreviewRoot", root);
        Place(previewRoot, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -128f), new Vector2(760f, 780f));

        Image shadow = NewImage(NewRect("Shadow", previewRoot).gameObject, new Color(0.15f, 0.11f, 0.04f, 0.30f), circle);
        Place((RectTransform)shadow.transform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 22f), new Vector2(330f, 54f));
        shadow.raycastTarget = false;

        GameObject previewRawGo = new GameObject("PreviewRaw", typeof(RectTransform));
        previewRawGo.transform.SetParent(previewRoot, false);
        EnsureCanvasRenderer(previewRawGo);
        Stretch((RectTransform)previewRawGo.transform);
        RawImage previewRaw = previewRawGo.AddComponent<RawImage>();
        previewRaw.color = Color.white;
        previewRaw.raycastTarget = true;

        // ── 右侧属性页 ─────────────────────────────────────
        RectTransform attrPanel = NewRect("AttrPanel", root);
        Place(attrPanel, new Vector2(1f, 1f), new Vector2(1f, 1f),
            new Vector2(-PanelRight, -PanelTop), new Vector2(PanelWidth, 820f));

        TMP_Text nameText = NewText("NameText", attrPanel, 42f, Color.white, TextAlignmentOptions.Left);
        Place(nameText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(PanelWidth, 52f));

        RectTransform starRow = NewRect("StarRow", attrPanel);
        Place(starRow, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -54f), new Vector2(200f, 28f));
        Sprite starSprite = LoadSprite(StarSpritePath);
        for (int i = 0; i < 6; i++)
        {
            Image star = NewImage(NewRect("Star" + i, starRow).gameObject,
                i < 5 ? Color.white : new Color(1f, 1f, 1f, 0.26f), starSprite);
            Image image = star;
            image.rectTransform.anchorMin = new Vector2(0f, 0.5f);
            image.rectTransform.anchorMax = new Vector2(0f, 0.5f);
            image.rectTransform.pivot = new Vector2(0f, 0.5f);
            image.rectTransform.anchoredPosition = new Vector2(i * 27f, 0f);
            image.rectTransform.sizeDelta = new Vector2(26f, 26f);
            image.raycastTarget = false;
        }

        TMP_Text levelText = NewText("LevelText", attrPanel, 32f, Color.white, TextAlignmentOptions.Left);
        Place(levelText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -84f), new Vector2(240f, 40f));

        TMP_Text expText = NewText("ExpText", attrPanel, 15f, Cyan, TextAlignmentOptions.Right);
        Place(expText.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-2f, -92f), new Vector2(170f, 22f));

        RectTransform expBar = NewRect("ExpBar", attrPanel);
        Place(expBar, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -122f), new Vector2(PanelWidth, 7f));
        Image expBarBg = NewImage(expBar.gameObject, new Color(1f, 1f, 1f, 0.18f), LoadSprite(BarSpritePath));
        expBarBg.raycastTarget = false;

        Image expBarFill = NewImage(NewRect("Fill", expBar).gameObject, Cyan, LoadSprite(BarSpritePath));
        Stretch(expBarFill.rectTransform);
        expBarFill.type = Image.Type.Filled;
        expBarFill.fillMethod = Image.FillMethod.Horizontal;
        expBarFill.fillOrigin = 0;
        expBarFill.fillAmount = 1f;
        expBarFill.raycastTarget = false;

        for (int i = 0; i < AttrLabels.Length; i++)
        {
            float y = -158f - i * 34f;

            Image icon = NewImage(NewRect("AttrIcon" + i, attrPanel).gameObject, new Color(1f, 1f, 1f, 0.92f),
                LoadSprite($"{IconDir}/{AttrIcons[i]}.png"));
            Place(icon.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(2f, y - 24.5f), new Vector2(19f, 19f));
            icon.raycastTarget = false;

            TMP_Text label = NewText("AttrLabel" + i, attrPanel, 21f, CreamSoft, TextAlignmentOptions.Left);
            Place(label.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(29f, y), new Vector2(240f, 30f));
            label.SetText(AttrLabels[i]);

            TMP_Text value = NewText(AttrFields[i], attrPanel, 23f, Color.white, TextAlignmentOptions.Right);
            Place(value.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-2f, y), new Vector2(160f, 30f));
            value.SetText("0");
        }

        Button detailButton = NewPillButton("DetailButton", attrPanel, new Vector2(PanelWidth, 40f),
            new Color(1f, 0.96f, 0.89f, 0.20f));
        Place((RectTransform)detailButton.transform, new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(0f, -372f), new Vector2(PanelWidth, 40f));
        TMP_Text detailLabel = NewText("Label", detailButton.transform, 20f, Cream, TextAlignmentOptions.Center);
        Stretch(detailLabel.rectTransform);
        detailLabel.SetText("详细信息");

        TMP_Text desc = NewText("DescText", attrPanel, 16f, CreamSoft, TextAlignmentOptions.TopLeft);
        Place(desc.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -428f), new Vector2(PanelWidth, 110f));
        desc.enableWordWrapping = true;
        desc.lineSpacing = 10f;
        desc.SetText("从世界之外漂流而来的旅行者。");

        // ── 右下主按钮：属性页「升级」/ 武器页「替换 · 强化」 ──
        Button upgradeButton = NewPillButton("UpgradeButton", root, new Vector2(334f, 44f), Cream);
        Place((RectTransform)upgradeButton.transform, new Vector2(1f, 0f), new Vector2(1f, 0f),
            new Vector2(-PanelRight, 62f), new Vector2(334f, 44f));
        TMP_Text upgradeLabel = NewText("Label", upgradeButton.transform, 26f, Ink, TextAlignmentOptions.Center);
        Stretch(upgradeLabel.rectTransform);
        upgradeLabel.SetText("升级");

        Button replaceButton = NewPillButton("ReplaceButton", root, new Vector2(150f, 44f), Cream);
        Place((RectTransform)replaceButton.transform, new Vector2(1f, 0f), new Vector2(1f, 0f),
            new Vector2(-279f, 62f), new Vector2(150f, 44f));
        TMP_Text replaceLabel = NewText("Label", replaceButton.transform, 22f, Ink, TextAlignmentOptions.Center);
        Stretch(replaceLabel.rectTransform);
        replaceLabel.SetText("替换");

        Button enhanceButton = NewPillButton("EnhanceButton", root, new Vector2(150f, 44f), Cream);
        Place((RectTransform)enhanceButton.transform, new Vector2(1f, 0f), new Vector2(1f, 0f),
            new Vector2(-PanelRight, 62f), new Vector2(150f, 44f));
        TMP_Text enhanceLabel = NewText("Label", enhanceButton.transform, 22f, Ink, TextAlignmentOptions.Center);
        Stretch(enhanceLabel.rectTransform);
        enhanceLabel.SetText("强化");

        // ── 右侧武器详情页（与属性页同位置，点「武器」页签时替换） ──
        RectTransform weaponPanel = NewRect("WeaponPanel", root);
        Place(weaponPanel, new Vector2(1f, 1f), new Vector2(1f, 1f),
            new Vector2(-PanelRight, -PanelTop), new Vector2(PanelWidth, 800f));

        TMP_Text wpName = NewText("NameText", weaponPanel, 36f, Color.white, TextAlignmentOptions.Left);
        Place(wpName.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(PanelWidth, 48f));

        TMP_Text wpType = NewText("TypeText", weaponPanel, 17f, CreamDim, TextAlignmentOptions.Left);
        Place(wpType.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -52f), new Vector2(PanelWidth, 24f));

        for (int i = 0; i < 2; i++)
        {
            RectTransform statRow = NewRect("StatRow" + i, weaponPanel);
            Place(statRow, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, -86f - i * 38f), new Vector2(PanelWidth, 34f));

            Image statBg = NewImage(statRow.gameObject, new Color(1f, 1f, 1f, 0.10f), LoadSprite(PillSpritePath));
            statBg.type = Image.Type.Sliced;
            statBg.raycastTarget = false;

            TMP_Text statLabel = NewText("Label", statRow, 19f, CreamSoft, TextAlignmentOptions.Left);
            Place(statLabel.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(12f, 0f), new Vector2(210f, 30f));

            TMP_Text statValue = NewText("Value", statRow, 22f, Color.white, TextAlignmentOptions.Right);
            Place(statValue.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-12f, 0f), new Vector2(160f, 30f));
            statRow.gameObject.SetActive(i == 0);
        }

        RectTransform wpStars = NewRect("StarRow", weaponPanel);
        Place(wpStars, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -168f), new Vector2(200f, 28f));
        for (int i = 0; i < 5; i++)
        {
            Image star = NewImage(NewRect("Star" + i, wpStars).gameObject, Color.white, starSprite);
            star.rectTransform.anchorMin = new Vector2(0f, 0.5f);
            star.rectTransform.anchorMax = new Vector2(0f, 0.5f);
            star.rectTransform.pivot = new Vector2(0f, 0.5f);
            star.rectTransform.anchoredPosition = new Vector2(i * 26f, 0f);
            star.rectTransform.sizeDelta = new Vector2(22f, 22f);
            star.raycastTarget = false;
        }

        TMP_Text wpLevel = NewText("LevelText", weaponPanel, 30f, Color.white, TextAlignmentOptions.Left);
        Place(wpLevel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -206f), new Vector2(170f, 36f));

        RectTransform wpRefine = NewRect("RefineRow", weaponPanel);
        Place(wpRefine, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(180f, -212f), new Vector2(140f, 22f));
        for (int i = 0; i < 5; i++)
        {
            RectTransform diamond = NewRect("Diamond" + i, wpRefine);
            Place(diamond, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(i * 20f, 0f), new Vector2(13f, 13f));
            diamond.localRotation = Quaternion.Euler(0f, 0f, 45f);
            Image diamondImage = NewImage(diamond.gameObject, Color.white, null);
            diamondImage.raycastTarget = false;
        }

        TMP_Text wpEffectTitle = NewText("EffectTitleText", weaponPanel, 19f, new Color(0.94f, 0.86f, 0.63f), TextAlignmentOptions.Left);
        Place(wpEffectTitle.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -250f), new Vector2(300f, 28f));
        wpEffectTitle.SetText("属性加成");

        TMP_Text wpEffect = NewText("EffectText", weaponPanel, 18f, Cream, TextAlignmentOptions.TopLeft);
        Place(wpEffect.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -282f), new Vector2(PanelWidth, 120f));
        wpEffect.enableWordWrapping = true;
        wpEffect.lineSpacing = 8f;

        TMP_Text wpFlavor = NewText("FlavorText", weaponPanel, 15f, CreamDim, TextAlignmentOptions.TopLeft);
        Place(wpFlavor.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -410f), new Vector2(PanelWidth, 120f));
        wpFlavor.enableWordWrapping = true;
        wpFlavor.lineSpacing = 8f;

        TMP_Text wpCompare = NewText("CompareText", weaponPanel, 16f, Color.white, TextAlignmentOptions.TopLeft);
        Place(wpCompare.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -536f), new Vector2(PanelWidth, 70f));
        wpCompare.enableWordWrapping = true;
        wpCompare.gameObject.SetActive(false);

        Button wpUnequip = NewPillButton("UnequipButton", weaponPanel, new Vector2(120f, 36f),
            new Color(1f, 0.96f, 0.89f, 0.16f));
        Place((RectTransform)wpUnequip.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -616f), new Vector2(120f, 36f));
        TMP_Text wpUnequipLabel = NewText("Label", wpUnequip.transform, 17f, Cream, TextAlignmentOptions.Center);
        Stretch(wpUnequipLabel.rectTransform);
        wpUnequipLabel.SetText("卸下");
        weaponPanel.gameObject.SetActive(false);
        replaceButton.gameObject.SetActive(false);
        enhanceButton.gameObject.SetActive(false);

        TMP_Text hint = NewText("HintText", root, 14f, new Color(1f, 1f, 1f, 0.55f), TextAlignmentOptions.Left);
        Place(hint.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(40f, 30f), new Vector2(500f, 22f));
        hint.SetText("拖动模型可旋转视角");

        TMP_Text status = NewText("StatusText", root, 15f, Cyan, TextAlignmentOptions.Left);
        Place(status.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(40f, 56f), new Vector2(700f, 22f));

        // ── 武器选择页：标题 / 返回 / 对比 ─────────────────
        RectTransform pickerRoot = NewRect("PickerRoot", root);
        Stretch(pickerRoot);

        TMP_Text pickerTitle = NewText("PickerTitleText", pickerRoot, 30f, Cream, TextAlignmentOptions.Left);
        Place(pickerTitle.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(100f, -30f), new Vector2(300f, 38f));
        pickerTitle.SetText("武器选择");

        Button backButton = NewIconButton("BackButton", pickerRoot, LoadSprite(ChevronSpritePath), CreamSoft, 42f);
        Place((RectTransform)backButton.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(44f, -22f), new Vector2(42f, 42f));

        Button compareButton = NewPillButton("CompareButton", pickerRoot, new Vector2(110f, 40f),
            new Color(1f, 0.96f, 0.89f, 0.20f));
        Place((RectTransform)compareButton.transform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-96f, -22f), new Vector2(110f, 40f));
        TMP_Text compareLabel = NewText("Label", compareButton.transform, 18f, Cream, TextAlignmentOptions.Center);
        Stretch(compareLabel.rectTransform);
        compareLabel.SetText("对比");

        ScrollRect weaponScroll = NewScroll("WeaponScroll", pickerRoot, new Vector2(440f, 700f));
        Place((RectTransform)weaponScroll.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(95f, -190f), new Vector2(440f, 700f));
        RectTransform weaponViewport = (RectTransform)weaponScroll.transform.Find("Viewport");
        RectTransform weaponContent = (RectTransform)weaponViewport.Find("Content");

        GridLayoutGroup weaponGrid = weaponContent.gameObject.AddComponent<GridLayoutGroup>();
        weaponGrid.cellSize = new Vector2(104f, 110f);
        weaponGrid.spacing = new Vector2(8f, 8f);
        weaponGrid.startCorner = GridLayoutGroup.Corner.UpperLeft;
        weaponGrid.childAlignment = TextAnchor.UpperLeft;
        weaponGrid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        weaponGrid.constraintCount = 4;
        ContentSizeFitter weaponFitter = weaponContent.gameObject.AddComponent<ContentSizeFitter>();
        weaponFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        RectTransform weaponCard = NewRect("RowTemplate", weaponContent);
        Image weaponCardBg = NewImage(weaponCard.gameObject, new Color(1f, 1f, 1f, 0.12f), LoadSprite(PillSpritePath));
        weaponCardBg.type = Image.Type.Sliced;
        weaponCardBg.raycastTarget = true;
        Button weaponCardButton = weaponCard.gameObject.AddComponent<Button>();
        weaponCardButton.targetGraphic = weaponCardBg;
        weaponCardButton.transition = Selectable.Transition.None;

        Image weaponCardIcon = NewImage(NewRect("Icon", weaponCard).gameObject, Color.white, null);
        Place((RectTransform)weaponCardIcon.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -6f), new Vector2(76f, 76f));
        weaponCardIcon.preserveAspect = true;
        weaponCardIcon.raycastTarget = false;

        RectTransform weaponCardStars = NewRect("StarRow", weaponCard);
        Place(weaponCardStars, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -76f), new Vector2(56f, 12f));
        for (int i = 0; i < 5; i++)
        {
            Image star = NewImage(NewRect("Star" + i, weaponCardStars).gameObject, Color.white, starSprite);
            star.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            star.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            star.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            star.rectTransform.anchoredPosition = new Vector2((i - 2) * 11f, 0f);
            star.rectTransform.sizeDelta = new Vector2(10f, 10f);
            star.raycastTarget = false;
        }

        TMP_Text weaponCardLevel = NewText("LevelText", weaponCard, 12f, Color.white, TextAlignmentOptions.Center);
        Place(weaponCardLevel.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -88f), new Vector2(92f, 16f));
        weaponCardLevel.SetText("Lv.1");

        Image weaponCardSelected = NewImage(NewRect("Selected", weaponCard).gameObject, Cream, null);
        Place((RectTransform)weaponCardSelected.transform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 4f), new Vector2(60f, 3f));
        weaponCardSelected.raycastTarget = false;
        weaponCardSelected.gameObject.SetActive(false);
        weaponCard.gameObject.SetActive(false);

        TMP_Text pickerEmpty = NewText("PickerEmptyText", pickerRoot, 18f, CreamDim, TextAlignmentOptions.Center);
        Place(pickerEmpty.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(95f, -190f), new Vector2(440f, 700f));
        pickerEmpty.SetText("暂无武器，可通过唤取或副本获取");

        Button sortButton = NewPillButton("SortButton", pickerRoot, new Vector2(210f, 44f),
            new Color(1f, 0.96f, 0.89f, 0.20f));
        Place((RectTransform)sortButton.transform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(95f, 44f), new Vector2(210f, 44f));
        TMP_Text sortLabel = NewText("Label", sortButton.transform, 18f, Cream, TextAlignmentOptions.Center);
        Stretch(sortLabel.rectTransform);
        sortLabel.SetText("品质顺序");

        Button filterButton = NewIconButton("FilterButton", pickerRoot, LoadSprite(CircleSpritePath),
            new Color(1f, 0.96f, 0.89f, 0.22f), 44f);
        Place((RectTransform)filterButton.transform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(318f, 44f), new Vector2(44f, 44f));
        for (int i = 0; i < 3; i++)
        {
            Image bar = NewImage(NewRect("Bar" + i, filterButton.transform).gameObject, Cream, null);
            bar.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            bar.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            bar.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            bar.rectTransform.anchoredPosition = new Vector2(0f, (i - 1) * 7f);
            bar.rectTransform.sizeDelta = new Vector2(i == 1 ? 14f : 20f, 2.4f);
            bar.raycastTarget = false;
        }
        pickerRoot.gameObject.SetActive(false);

        // ── 跨角色确认 ─────────────────────────────────────
        RectTransform confirmRoot = NewRect("ConfirmRoot", root);
        Stretch(confirmRoot);
        Image confirmScrim = NewImage(confirmRoot.gameObject, new Color(0f, 0f, 0f, 0.62f), null);
        if (confirmScrim != null) confirmScrim.raycastTarget = true;

        RectTransform confirmPanel = NewRect("Panel", confirmRoot);
        Place(confirmPanel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(560f, 240f));
        Image confirmBg = NewImage(confirmPanel.gameObject, new Color(0.16f, 0.13f, 0.08f, 0.96f), LoadSprite(PillSpritePath));
        if (confirmBg != null)
        {
            confirmBg.type = Image.Type.Sliced;
            confirmBg.raycastTarget = true;
        }

        TMP_Text confirmMessage = NewText("MessageText", confirmPanel, 19f, Cream, TextAlignmentOptions.Center);
        Place(confirmMessage.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -50f), new Vector2(-60f, 90f));
        confirmMessage.enableWordWrapping = true;

        Button confirmOk = NewPillButton("ConfirmOkButton", confirmPanel, new Vector2(180f, 56f), Cream);
        Place((RectTransform)confirmOk.transform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(110f, 30f), new Vector2(180f, 56f));
        TMP_Text confirmOkLabel = NewText("Label", confirmOk.transform, 18f, Ink, TextAlignmentOptions.Center);
        Stretch(confirmOkLabel.rectTransform);
        confirmOkLabel.SetText("卸下并装备");

        Button confirmCancel = NewPillButton("ConfirmCancelButton", confirmPanel, new Vector2(180f, 56f),
            new Color(1f, 0.96f, 0.89f, 0.18f));
        Place((RectTransform)confirmCancel.transform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(-110f, 30f), new Vector2(180f, 56f));
        TMP_Text confirmCancelLabel = NewText("Label", confirmCancel.transform, 18f, Cream, TextAlignmentOptions.Center);
        Stretch(confirmCancelLabel.rectTransform);
        confirmCancelLabel.SetText("取消");
        confirmRoot.gameObject.SetActive(false);

        // 头像行必须压在 3D 预览之上：RawImage 是后建的兄弟节点，否则会盖住头顶
        avatarRow.SetSiblingIndex(previewRoot.GetSiblingIndex() + 1);

        view.heroStats = CollectHeroStats();
        int bindings = BindFields(root, view, controlData);

        wrapper.layer = Layer.UI;
        foreach (Transform child in wrapper.GetComponentsInChildren<Transform>(true))
            child.gameObject.layer = Layer.UI;

        EnsureFolder(Path.GetDirectoryName(PrefabPath)?.Replace("\\", "/"));
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(wrapper, PrefabPath);
        UnityEngine.Object.DestroyImmediate(wrapper);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[CharacterView] 生成 {PrefabPath}，绑定 {bindings} 项，角色数据 {view.heroStats?.Length ?? 0} 份");
        Selection.activeObject = prefab;
    }

    /// <summary>按 PlayerSO.characterId 把角色配置挂到下标位置（找不到的角色留空）</summary>
    private static PlayerSO[] CollectHeroStats()
    {
        var stats = new PlayerSO[BattleHeroCatalog.Count];
        string[] guids = AssetDatabase.FindAssets("t:PlayerSO");
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            PlayerSO so = AssetDatabase.LoadAssetAtPath<PlayerSO>(path);
            if (so == null || so.CharacterId < 0 || so.CharacterId >= stats.Length) continue;
            if (stats[so.CharacterId] != null)
            {
                Debug.LogWarning($"[CharacterView] 角色ID {so.CharacterId} 有多份 PlayerSO，保留先找到的 {stats[so.CharacterId].name}");
                continue;
            }
            stats[so.CharacterId] = so;
        }
        return stats;
    }

    /// <summary>左侧菜单一行：胶囊选中底 + 菱形标记 + 文字（未开放的页签只做版式占位）</summary>
    private static void CreateMenuRow(Transform parent, string name, string label, int index, bool unlocked)
    {
        RectTransform row = NewRect(name, parent);
        Place(row, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -index * 70f), new Vector2(190f, 52f));

        Image hit = NewImage(row.gameObject, new Color(1f, 1f, 1f, 0.001f), null);
        hit.raycastTarget = true;
        Button button = row.gameObject.AddComponent<Button>();
        button.targetGraphic = hit;
        button.transition = Selectable.Transition.None;

        Image pill = NewImage(NewRect("Pill", row).gameObject, new Color(1f, 0.96f, 0.87f, 0.13f), LoadSprite(PillSpritePath));
        Stretch(pill.rectTransform);
        pill.type = Image.Type.Sliced;
        pill.raycastTarget = false;
        Outline pillBorder = pill.gameObject.AddComponent<Outline>();
        pillBorder.effectColor = new Color(1f, 0.96f, 0.87f, 0.22f);
        pillBorder.effectDistance = new Vector2(1f, -1f);
        pill.gameObject.SetActive(unlocked && index == 0);

        RectTransform marker = NewRect("Marker", row);
        Place(marker, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(16f, 0f), new Vector2(13f, 13f));
        marker.localRotation = Quaternion.Euler(0f, 0f, 45f);
        Image markerImage = NewImage(marker.gameObject, unlocked ? Cream : CreamDim, null);
        markerImage.raycastTarget = false;

        TMP_Text text = NewText("Label", row, 34f, unlocked ? Cream : CreamDim, TextAlignmentOptions.Left);
        Place(text.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(46f, 0f), new Vector2(150f, 44f));
        text.SetText(label);
    }

    /// <summary>菱形按钮：圆形底 + 十字叉（关闭按钮）</summary>
    private static Button NewCircleButton(string name, Transform parent, Vector2 size, Color color)
    {
        RectTransform rect = NewRect(name, parent);
        rect.sizeDelta = size;
        Image image = NewImage(rect.gameObject, color, LoadSprite(CircleSpritePath));
        image.raycastTarget = true;
        Button button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        return button;
    }

    private static void AddCrossMark(Transform parent, Color color, float length, float thickness)
    {
        for (int i = 0; i < 2; i++)
        {
            Image bar = NewImage(NewRect(i == 0 ? "BarA" : "BarB", parent).gameObject, color, null);
            bar.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            bar.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            bar.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            bar.rectTransform.anchoredPosition = Vector2.zero;
            bar.rectTransform.sizeDelta = new Vector2(length, thickness);
            bar.rectTransform.localRotation = Quaternion.Euler(0f, 0f, i == 0 ? 45f : -45f);
            bar.raycastTarget = false;
        }
    }

    /// <summary>纯图标按钮（左右切角色箭头）</summary>
    private static Button NewIconButton(string name, Transform parent, Sprite sprite, Color color, float size)
    {
        RectTransform rect = NewRect(name, parent);
        rect.sizeDelta = new Vector2(size, size);
        Image image = NewImage(rect.gameObject, color, sprite);
        image.raycastTarget = true;
        Button button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        button.transition = Selectable.Transition.ColorTint;
        return button;
    }

    /// <summary>胶囊按钮：九宫格圆角底 + 可选文字</summary>
    private static Button NewPillButton(string name, Transform parent, Vector2 size, Color color)
    {
        RectTransform rect = NewRect(name, parent);
        rect.sizeDelta = size;
        Image image = NewImage(rect.gameObject, color, LoadSprite(PillSpritePath));
        image.type = Image.Type.Sliced;
        image.raycastTarget = true;
        Button button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        return button;
    }

    private static ScrollRect NewScroll(string name, Transform parent, Vector2 size)
    {
        RectTransform root = NewRect(name, parent);
        root.sizeDelta = size;
        ScrollRect scroll = root.gameObject.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 24f;

        RectTransform viewport = NewRect("Viewport", root);
        Stretch(viewport);
        viewport.gameObject.AddComponent<RectMask2D>();
        Image viewportImage = NewImage(viewport.gameObject, new Color(0f, 0f, 0f, 0f), null);
        if (viewportImage != null) viewportImage.raycastTarget = true;

        RectTransform content = NewRect("Content", viewport);
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.anchoredPosition = Vector2.zero;
        content.sizeDelta = Vector2.zero;

        scroll.viewport = viewport;
        scroll.content = content;
        return scroll;
    }

    // ──────────────────────────────
    // 美术资源（首次构建时生成，之后复用）
    // ──────────────────────────────

    private static void EnsureArt()
    {
        EnsureFolder(ArtDir);
        EnsureFolder(IconDir);
        EnsureHazeBackground();
        EnsureCircle();
        EnsureRing();
        EnsurePill();
        EnsureBar();
        EnsureStar();
        EnsureChevron();
        EnsureEmblem();
        EnsureAttrIcons();
        EnsureScrim();
    }

    /// <summary>用原暖金云海图重排出一张「雾面暖金」背景：大幅柔化 + 压饱和 + 中心柔光</summary>
    private static void EnsureHazeBackground()
    {
        if (AssetDatabase.LoadAssetAtPath<Texture2D>(BgPath) != null) return;
        if (!File.Exists(BgSourcePath))
        {
            Debug.LogWarning("[CharacterView] 缺少背景原图：" + BgSourcePath);
            return;
        }

        var source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        source.LoadImage(File.ReadAllBytes(BgSourcePath));

        const int blurW = 180;
        const int blurH = 100;
        var grid = new Color[blurW * blurH];
        for (int by = 0; by < blurH; by++)
        {
            for (int bx = 0; bx < blurW; bx++)
            {
                int x0 = bx * source.width / blurW;
                int x1 = Mathf.Max(x0 + 1, (bx + 1) * source.width / blurW);
                int y0 = by * source.height / blurH;
                int y1 = Mathf.Max(y0 + 1, (by + 1) * source.height / blurH);
                Color sum = Color.black;
                int count = 0;
                for (int y = y0; y < y1; y += 3)
                {
                    for (int x = x0; x < x1; x += 3)
                    {
                        sum += source.GetPixel(x, y);
                        count++;
                    }
                }
                grid[by * blurW + bx] = count > 0 ? sum / count : Color.black;
            }
        }
        UnityEngine.Object.DestroyImmediate(source);

        // 小图再走一遍 3×3 均值，云团边界更柔但仍保留层次
        for (int pass = 0; pass < 1; pass++)
        {
            var next = new Color[grid.Length];
            for (int y = 0; y < blurH; y++)
            {
                for (int x = 0; x < blurW; x++)
                {
                    Color sum = Color.black;
                    int count = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int sx = Mathf.Clamp(x + dx, 0, blurW - 1);
                            int sy = Mathf.Clamp(y + dy, 0, blurH - 1);
                            sum += grid[sy * blurW + sx];
                            count++;
                        }
                    }
                    next[y * blurW + x] = sum / count;
                }
            }
            grid = next;
        }

        const int width = 1920;
        const int height = 1080;
        var pixels = new Color[width * height];
        for (int y = 0; y < height; y++)
        {
            float v = (float)y / (height - 1);
            for (int x = 0; x < width; x++)
            {
                float u = (float)x / (width - 1);

                Color cloud = SampleGrid(grid, blurW, blurH, u, v);
                // 与原图亮度挂钩的云层明暗，压缩到 ±16%
                float shade = (cloud.grayscale - 0.5f) * 0.32f;

                // 底色：上深下浅的暖金，中段最亮
                Color baseColor = v < 0.5f
                    ? Color.Lerp(new Color(0.44f, 0.36f, 0.19f), new Color(0.60f, 0.50f, 0.27f), v / 0.5f)
                    : Color.Lerp(new Color(0.60f, 0.50f, 0.27f), new Color(0.47f, 0.40f, 0.22f), (v - 0.5f) / 0.5f);

                // 角色身后的椭圆柔光
                float gx = (u - 0.5f) / 0.34f;
                float gy = (v - 0.52f) / 0.46f;
                float glow = Mathf.Exp(-(gx * gx + gy * gy) * 1.7f) * 0.20f;

                // 边缘压暗
                float edge = Mathf.Max(Mathf.Abs(u - 0.5f) * 2f, Mathf.Abs(v - 0.5f) * 2f);
                float vignette = 1f - 0.10f * Mathf.Pow(edge, 2.4f);

                Color color = baseColor * (1f + shade) * vignette
                              + new Color(glow, glow * 0.94f, glow * 0.72f);

                // 轻微去饱和，避免橙味过重
                float gray = color.grayscale;
                color = Color.Lerp(color, new Color(gray, gray, gray), 0.18f);
                color.a = 1f;
                pixels[y * width + x] = color;
            }
        }

        var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
        tex.SetPixels(pixels);
        tex.Apply();
        File.WriteAllBytes(BgPath, tex.EncodeToJPG(90));
        UnityEngine.Object.DestroyImmediate(tex);

        AssetDatabase.ImportAsset(BgPath, ImportAssetOptions.ForceUpdate);
        var importer = AssetImporter.GetAtPath(BgPath) as TextureImporter;
        if (importer != null)
        {
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = false;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.maxTextureSize = 2048;
            importer.sRGBTexture = true;
            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            settings.spriteMeshType = SpriteMeshType.FullRect;
            importer.SetTextureSettings(settings);
            importer.SaveAndReimport();
        }
    }

    private static Color SampleGrid(Color[] grid, int gridW, int gridH, float u, float v)
    {
        float gx = Mathf.Clamp(u * (gridW - 1), 0f, gridW - 1.001f);
        float gy = Mathf.Clamp(v * (gridH - 1), 0f, gridH - 1.001f);
        int x0 = (int)gx;
        int y0 = (int)gy;
        float tx = gx - x0;
        float ty = gy - y0;
        Color c00 = grid[y0 * gridW + x0];
        Color c10 = grid[y0 * gridW + Mathf.Min(x0 + 1, gridW - 1)];
        Color c01 = grid[Mathf.Min(y0 + 1, gridH - 1) * gridW + x0];
        Color c11 = grid[Mathf.Min(y0 + 1, gridH - 1) * gridW + Mathf.Min(x0 + 1, gridW - 1)];
        return Color.Lerp(Color.Lerp(c00, c10, tx), Color.Lerp(c01, c11, tx), ty);
    }

    private static void EnsureCircle()
    {
        if (AssetDatabase.LoadAssetAtPath<Sprite>(CircleSpritePath) != null) return;

        const int size = 128;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color32[size * size];
        float half = size * 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - half;
                float dy = y + 0.5f - half;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp01(half - d);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply();
        WritePng(CircleSpritePath, tex);
        ImportSprite(CircleSpritePath, Vector4.zero, 256);
    }

    /// <summary>头像选中描边：环形贴图</summary>
    private static void EnsureRing()
    {
        if (AssetDatabase.LoadAssetAtPath<Sprite>(RingSpritePath) != null) return;

        SaveMaskSprite(RingSpritePath, 128, (u, v) =>
        {
            float dx = u - 0.5f;
            float dy = v - 0.5f;
            float r = Mathf.Sqrt(dx * dx + dy * dy);
            return r <= 0.49f && r >= 0.435f;
        }, Vector4.zero);
        ImportSprite(RingSpritePath, Vector4.zero, 256);
    }

    /// <summary>九宫格胶囊：64×64 圆角 20，border 20 可在任意宽高下保持圆头</summary>
    private static void EnsurePill()
    {
        if (AssetDatabase.LoadAssetAtPath<Sprite>(PillSpritePath) != null) return;

        SaveMaskSprite(PillSpritePath, 64, (u, v) =>
        {
            float dx = Mathf.Abs(u - 0.5f);
            float dy = Mathf.Abs(v - 0.5f);
            const float radius = 20f / 64f;
            float qx = Mathf.Max(dx - (0.5f - radius), 0f);
            float qy = Mathf.Max(dy - (0.5f - radius), 0f);
            return Mathf.Sqrt(qx * qx + qy * qy) <= radius;
        }, new Vector4(20f, 20f, 20f, 20f));
        ImportSprite(PillSpritePath, new Vector4(20f, 20f, 20f, 20f), 128);
    }

    /// <summary>细长条（经验条）：胶囊形状，直接拉伸使用</summary>
    private static void EnsureBar()
    {
        if (AssetDatabase.LoadAssetAtPath<Sprite>(BarSpritePath) != null) return;

        SaveMaskSprite(BarSpritePath, 64, (u, v) => true, Vector4.zero, 64, 16, (u, v) =>
            SegmentDistance(u * 64f, v * 16f, 8f, 8f, 56f, 8f) <= 8f);
        ImportSprite(BarSpritePath, Vector4.zero, 128);
    }

    /// <summary>四角星（星级）</summary>
    private static void EnsureStar()
    {
        if (AssetDatabase.LoadAssetAtPath<Sprite>(StarSpritePath) != null) return;

        SaveMaskSprite(StarSpritePath, 128, (u, v) => IsFourPointStar(u, v, 0.5f, 0.5f, 0.47f, 0.60f), Vector4.zero);
        ImportSprite(StarSpritePath, Vector4.zero, 256);
    }

    /// <summary>切角色箭头（左向）</summary>
    private static void EnsureChevron()
    {
        if (AssetDatabase.LoadAssetAtPath<Sprite>(ChevronSpritePath) != null) return;

        SaveMaskSprite(ChevronSpritePath, 96, (u, v) =>
            SegmentDistance(u, v, 0.70f, 0.10f, 0.34f, 0.50f) <= 0.085f
            || SegmentDistance(u, v, 0.34f, 0.50f, 0.70f, 0.90f) <= 0.085f
            || (Mathf.Abs(u - 0.55f) + Mathf.Abs(v - 0.50f) <= 0.10f), Vector4.zero);
        ImportSprite(ChevronSpritePath, Vector4.zero, 256);
    }

    /// <summary>元素徽记：金色圆牌 + 象牙外圈 + 中心菱形</summary>
    private static void EnsureEmblem()
    {
        if (AssetDatabase.LoadAssetAtPath<Sprite>(EmblemSpritePath) != null) return;

        const int size = 256;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                pixels[y * size + x] = EmblemShade((x + 0.5f) / size, (y + 0.5f) / size);
            }
        }
        tex.SetPixels(pixels);
        tex.Apply();
        WritePng(EmblemSpritePath, tex);
        ImportSprite(EmblemSpritePath, Vector4.zero, 256);
    }

    private static Color EmblemShade(float u, float v)
    {
        float dx = u - 0.5f;
        float dy = v - 0.5f;
        float r = Mathf.Sqrt(dx * dx + dy * dy);

        float alpha = Mathf.Clamp01((0.474f - r) / 0.006f);
        if (alpha <= 0f) return new Color(0f, 0f, 0f, 0f);

        Color body = Color.Lerp(new Color(0.80f, 0.62f, 0.27f), new Color(0.97f, 0.90f, 0.66f),
            Mathf.Clamp01((v - 0.04f) / 0.92f));
        body = Color.Lerp(body, new Color(1f, 0.96f, 0.79f), Mathf.Clamp01(1f - r * 2.6f) * 0.45f);

        Color color = body;
        if (r > 0.432f) color = new Color(0.96f, 0.93f, 0.83f);
        else if (r > 0.386f && r < 0.398f) color = Color.Lerp(body, new Color(0.99f, 0.96f, 0.86f), 0.78f);

        float diamond = Mathf.Abs(dx) + Mathf.Abs(dy);
        if (diamond <= 0.152f) color = new Color(0.98f, 0.95f, 0.86f);
        if (diamond <= 0.074f) color = new Color(0.72f, 0.52f, 0.20f);
        if (Mathf.Abs(dx) < 0.042f && Mathf.Abs(Mathf.Abs(dy) - 0.255f) < 0.042f) color = new Color(0.98f, 0.95f, 0.86f);
        if (Mathf.Abs(dy) < 0.042f && Mathf.Abs(Mathf.Abs(dx) - 0.255f) < 0.042f) color = new Color(0.98f, 0.95f, 0.86f);

        color.a = alpha;
        return color;
    }

    /// <summary>属性行小图标（白色剪影，19px 显示）</summary>
    private static void EnsureAttrIcons()
    {
        foreach (string icon in AttrIcons)
        {
            string path = $"{IconDir}/{icon}.png";
            if (AssetDatabase.LoadAssetAtPath<Sprite>(path) != null) continue;

            SaveMaskSprite(path, 48, (u, v) => IconShape(icon, u, v), Vector4.zero);
            ImportSprite(path, Vector4.zero, 96);
        }
    }

    private static bool IconShape(string icon, float u, float v)
    {
        switch (icon)
        {
            case "icon_hp":
            {
                float dx = u - 0.5f;
                float dy = v - 0.406f;
                if (dx * dx + dy * dy <= 0.186f * 0.186f) return true;
                if (v < 0.406f || v > 0.84f) return false;
                float halfWidth = 0.186f * (0.84f - v) / (0.84f - 0.406f);
                return Mathf.Abs(dx) <= halfWidth;
            }
            case "icon_atk":
                return SegmentDistance(u, v, 0.68f, 0.86f, 0.40f, 0.36f) <= 0.045f
                       || SegmentDistance(u, v, 0.28f, 0.44f, 0.50f, 0.22f) <= 0.036f
                       || SegmentDistance(u, v, 0.34f, 0.30f, 0.20f, 0.14f) <= 0.042f;
            case "icon_def":
                return ShieldShape(u, v, -1f);
            case "icon_dmgup":
                return IsFourPointStar(u, v, 0.5f, 0.5f, 0.34f, 0.58f);
            case "icon_dmgdown":
                return ShieldShape(u, v, 0.48f);
            case "icon_speed":
                return SegmentDistance(u, v, 0.26f, 0.74f, 0.52f, 0.50f) <= 0.055f
                       || SegmentDistance(u, v, 0.26f, 0.26f, 0.52f, 0.50f) <= 0.055f
                       || SegmentDistance(u, v, 0.52f, 0.74f, 0.79f, 0.50f) <= 0.055f
                       || SegmentDistance(u, v, 0.52f, 0.26f, 0.79f, 0.50f) <= 0.055f;
            default:
                return false;
        }
    }

    /// <summary>盾形；slotY &gt;= 0 时在盾面中间挖一条横槽（减伤图标）</summary>
    private static bool ShieldShape(float u, float v, float slotY)
    {
        if (v > 0.85f || v < 0.13f) return false;
        float t = (0.85f - v) / 0.72f;
        float halfWidth = 0.31f * (1f - 0.82f * t * t);
        if (v > 0.74f)
        {
            float top = (v - 0.74f) / 0.11f;
            halfWidth *= Mathf.Sqrt(Mathf.Max(0f, 1f - top * top));
        }
        if (Mathf.Abs(u - 0.5f) > halfWidth) return false;
        if (slotY > 0f && v > slotY - 0.045f && v < slotY + 0.045f && Mathf.Abs(u - 0.5f) < 0.19f) return false;
        return true;
    }

    private static bool IsFourPointStar(float u, float v, float cx, float cy, float radius, float exponent)
    {
        float nx = Mathf.Abs(u - cx) / radius;
        float ny = Mathf.Abs(v - cy) / radius;
        if (nx > 1.2f || ny > 1.2f) return false;
        return Mathf.Pow(nx, exponent) + Mathf.Pow(ny, exponent) <= 1f;
    }

    /// <summary>生成右侧渐隐压暗贴图（左侧透明 → 右侧黑 32%）</summary>
    private static void EnsureScrim()
    {
        if (AssetDatabase.LoadAssetAtPath<Sprite>(ScrimSpritePath) != null) return;

        const int width = 256;
        const int height = 8;
        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
        var pixels = new Color32[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float t = Mathf.Clamp01((x / (width - 1f) - 0.1f) / 0.9f);
                float a = t * t * (3f - 2f * t) * 0.42f;
                pixels[y * width + x] = new Color32(0, 0, 0, (byte)(a * 255f));
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply();
        WritePng(ScrimSpritePath, tex);
        ImportSprite(ScrimSpritePath, Vector4.zero, 256);
    }

    // ──────────────────────────────
    // 资源写入工具
    // ──────────────────────────────

    /// <summary>用形状函数（含 4×4 超采样）生成白色 alpha 贴图</summary>
    private static void SaveMaskSprite(string path, int size, Func<float, float, bool> inside, Vector4 border)
        => SaveMaskSprite(path, size, inside, border, size, size, null);

    private static void SaveMaskSprite(
        string path, int size, Func<float, float, bool> inside, Vector4 border,
        int width, int height, Func<float, float, bool> pixelInside)
    {
        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
        var pixels = new Color32[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float u = (x + 0.5f) / width;
                float v = (y + 0.5f) / height;
                int hits = 0;
                for (int sy = 0; sy < 4; sy++)
                {
                    for (int sx = 0; sx < 4; sx++)
                    {
                        float su = (x + (sx + 0.5f) / 4f) / width;
                        float sv = (y + (sy + 0.5f) / 4f) / height;
                        bool hit = pixelInside != null ? pixelInside(su, sv) : inside(su, sv);
                        if (hit) hits++;
                    }
                }
                pixels[y * width + x] = new Color32(255, 255, 255, (byte)(hits * 255 / 16));
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply();
        WritePng(path, tex);
    }

    private static void WritePng(string path, Texture2D tex)
    {
        EnsureFolder(Path.GetDirectoryName(path)?.Replace("\\", "/"));
        File.WriteAllBytes(path, tex.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
    }

    private static void ImportSprite(string path, Vector4 border, int maxSize)
    {
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null) return;

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = false;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.maxTextureSize = maxSize;

        var settings = new TextureImporterSettings();
        importer.ReadTextureSettings(settings);
        settings.spriteBorder = border;
        settings.spriteMeshType = SpriteMeshType.FullRect;
        importer.SetTextureSettings(settings);
        importer.SaveAndReimport();
    }

    private static float SegmentDistance(float px, float py, float ax, float ay, float bx, float by)
    {
        float vx = bx - ax;
        float vy = by - ay;
        float wx = px - ax;
        float wy = py - ay;
        float lengthSquared = vx * vx + vy * vy;
        float t = lengthSquared > 0f ? Mathf.Clamp01((wx * vx + wy * vy) / lengthSquared) : 0f;
        float dx = wx - vx * t;
        float dy = wy - vy * t;
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    // ──────────────────────────────
    // 基础节点工具
    // ──────────────────────────────

    private static RectTransform NewRect(string name, Transform parent)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    private static Image NewImage(GameObject target, Color color, Sprite sprite)
    {
        EnsureCanvasRenderer(target);
        Image image = target.GetComponent<Image>();
        if (image == null) image = target.AddComponent<Image>();
        image.color = color;
        image.sprite = sprite;
        return image;
    }

    private static TMP_Text NewText(
        string name, Transform parent, float size, Color color, TextAlignmentOptions alignment)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        EnsureCanvasRenderer(go);
        TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
        text.fontSize = size;
        text.color = color;
        text.alignment = alignment;
        text.raycastTarget = false;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Overflow;

        // 亮背景上保证可读：统一加 1px 深色阴影
        Shadow shadow = go.AddComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, 0.72f);
        shadow.effectDistance = new Vector2(1.5f, -1.5f);
        shadow.useGraphicAlpha = true;

        Shadow shadow2 = go.AddComponent<Shadow>();
        shadow2.effectColor = new Color(0f, 0f, 0f, 0.42f);
        shadow2.effectDistance = new Vector2(-1f, 1f);
        shadow2.useGraphicAlpha = true;
        return text;
    }

    private static void Place(
        RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 anchoredPosition, Vector2 sizeDelta)
    {
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = anchorMin;
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = sizeDelta;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    private static void EnsureCanvasRenderer(GameObject target)
    {
        if (target.GetComponent<CanvasRenderer>() == null)
            target.AddComponent<CanvasRenderer>();
    }

    private static Sprite LoadSprite(string path)
    {
        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }

    private static void EnsureFolder(string path)
    {
        if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path)?.Replace("\\", "/");
        string leaf = Path.GetFileName(path);
        if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }

    private static int BindFields(Transform panelRoot, UIView view, UIControlData controlData)
    {
        SerializedObject so = new(controlData);
        SerializedProperty list = so.FindProperty("ctrlItemDatas");
        list.arraySize = 0;

        foreach (FieldInfo field in view.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            ControlBindingAttribute attr = field.GetCustomAttribute<ControlBindingAttribute>();
            if (attr == null) continue;

            Transform target = !string.IsNullOrEmpty(attr.path)
                ? panelRoot.Find(attr.path)
                : (panelRoot.Find(field.Name) ?? panelRoot.Find(char.ToUpper(field.Name[0]) + field.Name.Substring(1)));

            if (target == null)
            {
                Debug.LogWarning($"[CharacterView] 找不到 {field.DeclaringType.Name}.{field.Name} 的节点");
                continue;
            }

            UnityEngine.Object component = field.FieldType == typeof(GameObject)
                ? target.gameObject
                : target.GetComponent(field.FieldType);
            if (component == null)
            {
                Debug.LogWarning($"[CharacterView] {target.name} 上没有 {field.FieldType.Name}（字段 {field.Name}）");
                continue;
            }

            field.SetValue(view, component);

            list.arraySize += 1;
            SerializedProperty element = list.GetArrayElementAtIndex(list.arraySize - 1);
            element.FindPropertyRelative("name").stringValue = field.Name;
            element.FindPropertyRelative("type").stringValue =
                component is GameObject ? "GameObject" : component.GetType().Name;
            SerializedProperty targets = element.FindPropertyRelative("targets");
            targets.arraySize = 1;
            targets.GetArrayElementAtIndex(0).objectReferenceValue = component;
        }

        so.ApplyModifiedPropertiesWithoutUndo();
        return list.arraySize;
    }
}
#endif
