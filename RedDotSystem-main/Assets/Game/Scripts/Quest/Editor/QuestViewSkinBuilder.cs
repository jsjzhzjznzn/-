#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;

using SkierFramework;

using TMPro;

using UnityEditor;

using UnityEngine;

using UnityEngine.UI;

namespace SkierFrameworkUIMigration
{
    /// <summary>
    /// 任务界面构建器（可重复执行）
    /// 菜单：Tools/UI/重建任务界面
    /// 产物：Assets/Game/UI/Quest/QuestView.prefab（+ UIControlData 绑定 + 程序化美术）
    ///
    /// 版式照鸣潮任务界面：左侧竖排导航 / 中间分组任务卡片列表 / 右侧任务详情与奖励，
    /// 以 1920×1080 为基准、四角与边锚定（画面比例变化时不越界）。
    /// 结构节点名与 QuestView 的 [ControlBinding] 字段名一一对应，构建器自己写绑定表。
    /// </summary>
    public static class QuestViewSkinBuilder
    {
        private const string PrefabPath = "Assets/Game/UI/Quest/QuestView.prefab";
        private const string ArtFolder = "Assets/Game/UI/Quest/Art";
        private const string DashijieIconFolder = "Assets/Game/UI/Dashijie/Icons";
        private const string CharacterFolder = "Assets/Game/Resources/ui/Character";
        private const string ThemeFolder = "Assets/Game/Resources/ui/Theme";
        private const string FontPath = "Assets/Codely/Fonts/NotoSansSC-Regular SDF.asset";

        private static readonly Color Gold = new(0.851f, 0.737f, 0.486f, 1f);
        private static readonly Color TextMain = new(0.94f, 0.94f, 0.94f, 1f);
        private static readonly Color TextDim = new(0.72f, 0.72f, 0.72f, 1f);
        private static readonly Color TextFaint = new(0.45f, 0.46f, 0.48f, 1f);
        private static readonly Color DeepBrown = new(0.165f, 0.141f, 0.094f, 1f);

        [MenuItem("Tools/UI/重建任务界面")]
        public static void Build()
        {
            EnsureFolder(ArtFolder);
            EnsureFolder(Path.GetDirectoryName(PrefabPath)?.Replace("\\", "/"));

            Sprite panelSprite = AssetDatabase.LoadAssetAtPath<Sprite>($"{ThemeFolder}/panel_round.png");
            Sprite roundSprite = AssetDatabase.LoadAssetAtPath<Sprite>($"{ThemeFolder}/btn_round.png");
            Sprite circleSprite = AssetDatabase.LoadAssetAtPath<Sprite>($"{DashijieIconFolder}/dashijie_circle.png");
            Sprite chevronSprite = AssetDatabase.LoadAssetAtPath<Sprite>($"{CharacterFolder}/ui_chevron.png");
            Sprite pillSprite = AssetDatabase.LoadAssetAtPath<Sprite>($"{CharacterFolder}/ui_pill.png");
            TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);

            Sprite cardSprite = EnsureRoundedSprite("quest_card", 64, 10, new Color(1f, 1f, 1f, 1f));
            Sprite goldBarSprite = EnsureGradientSprite("quest_section_bar", 256, 26, Gold);
            Sprite chipSprite = EnsureRoundedSprite("quest_chip", 64, 14, new Color(1f, 1f, 1f, 1f));

            var root = new GameObject("QuestView", typeof(RectTransform));
            RectTransform rootRect = root.GetComponent<RectTransform>();
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;

            QuestView view = root.AddComponent<QuestView>();
            UIControlData controlData = root.AddComponent<UIControlData>();

            // ── 全屏压暗底板（点击关闭） ──
            // 参考图是"模糊场景 + 深色面板"；这里没有全屏模糊，改用接近不透明的深底压掉场景，
            // 否则亮场景下文字与卡片会发灰，看起来像一层半透明蒙版。
            RectTransform backdrop = NewRect("BackdropButton", rootRect);
            Stretch(backdrop);
            Image backdropImage = NewImage(backdrop.gameObject, new Color(0.012f, 0.014f, 0.018f, 1f), null);
            if (backdropImage != null) backdropImage.raycastTarget = true;
            Button backdropButton = backdrop.gameObject.AddComponent<Button>();
            backdropButton.targetGraphic = backdropImage;

            // 左侧列表底板：比全屏底再亮一档，把列表与右侧详情分开
            RectTransform listPlate = NewRect("ListPlate", rootRect);
            listPlate.anchorMin = new Vector2(0f, 0f);
            listPlate.anchorMax = new Vector2(0f, 1f);
            listPlate.offsetMin = new Vector2(120f, 56f);
            listPlate.offsetMax = new Vector2(652f, -104f);
            Image listPlateImage = NewImage(listPlate.gameObject, new Color(0.055f, 0.060f, 0.070f, 1f), cardSprite);
            if (listPlateImage != null)
            {
                listPlateImage.type = Image.Type.Sliced;
                listPlateImage.raycastTarget = false;
            }

            // 右侧详情底板：比列表再暗一点，形成"列表 / 详情"两栏层次，同时彻底压掉背后的 3D 场景
            RectTransform detailPlate = NewRect("DetailPlate", rootRect);
            detailPlate.anchorMin = Vector2.zero;
            detailPlate.anchorMax = Vector2.one;
            detailPlate.offsetMin = new Vector2(672f, 56f);
            detailPlate.offsetMax = new Vector2(-56f, -104f);
            Image detailPlateImage = NewImage(detailPlate.gameObject, new Color(0.038f, 0.042f, 0.050f, 1f), cardSprite);
            if (detailPlateImage != null)
            {
                detailPlateImage.type = Image.Type.Sliced;
                detailPlateImage.raycastTarget = false;
            }

            // ── 顶部标题与关闭 ──
            TMP_Text title = NewText("TitleText", rootRect, 34f, TextMain, TextAlignmentOptions.Left, font);
            Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(56f, -32f), new Vector2(360f, 52f));
            title.SetText("<color=#D9BC7C>◆</color>  任务");

            RectTransform close = NewRect("CloseButton", rootRect);
            Place(close, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-56f, -30f), new Vector2(48f, 48f));
            Image closeImage = NewImage(close.gameObject, new Color(0.10f, 0.105f, 0.12f, 0.9f), circleSprite);
            // 叉号自己必须收点击：关掉 raycastTarget 时点击会落到背后的全屏 BackdropButton 上，
            // 只要那一小块被任何东西盖住，这一次点击就丢了（表现为"点叉号没反应"）
            if (closeImage != null) closeImage.raycastTarget = true;
            Button closeButton = close.gameObject.AddComponent<Button>();
            closeButton.targetGraphic = closeImage;
            TMP_Text closeLabel = NewText("Label", close, 24f, TextMain, TextAlignmentOptions.Center, font);
            Stretch(closeLabel.rectTransform);
            closeLabel.SetText("×");   // U+00D7：Noto Sans SC 有字形（✕ 会变方块）

            // ── 左侧竖排导航 ──
            RectTransform navBar = NewRect("NavBar", rootRect);
            Place(navBar, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(56f, -124f), new Vector2(56f, 420f));
            BuildNavButton(navBar, "NavQuestButton", "dashijie_icon_quest", true, circleSprite, font);
            BuildNavButton(navBar, "NavAchievementButton", "dashijie_icon_achievement", false, circleSprite, font);
            BuildNavButton(navBar, "NavMapButton", "dashijie_icon_map", false, circleSprite, font);
            BuildNavButton(navBar, "NavCharacterButton", "dashijie_icon_character", false, circleSprite, font);
            BuildNavButton(navBar, "NavSettingsButton", "dashijie_icon_settings", false, circleSprite, font);

            // ── 中间：分组任务列表 ──
            RectTransform scrollRect = NewRect("QuestScroll", rootRect);
            scrollRect.anchorMin = new Vector2(0f, 0f);
            scrollRect.anchorMax = new Vector2(0f, 1f);
            scrollRect.offsetMin = new Vector2(136f, 72f);
            scrollRect.offsetMax = new Vector2(636f, -120f);

            ScrollRect scroll = scrollRect.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 28f;

            RectTransform viewport = NewRect("Viewport", scrollRect);
            Stretch(viewport);
            viewport.gameObject.AddComponent<RectMask2D>();
            Image viewportImage = NewImage(viewport.gameObject, new Color(1f, 1f, 1f, 0.002f), null);   // ScrollRect 要求 viewport 有 Graphic
            if (viewportImage != null) viewportImage.raycastTarget = true;

            RectTransform content = NewRect("Content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = new Vector2(0f, 0f);
            content.offsetMax = new Vector2(0f, 0f);

            var vertical = content.gameObject.AddComponent<VerticalLayoutGroup>();
            vertical.childAlignment = TextAnchor.UpperCenter;
            vertical.childControlWidth = true;
            vertical.childControlHeight = true;
            vertical.childForceExpandWidth = true;
            vertical.childForceExpandHeight = false;
            vertical.spacing = 10f;
            vertical.padding = new RectOffset(0, 0, 4, 4);

            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.viewport = viewport;
            scroll.content = content;

            BuildSectionTemplate(content, goldBarSprite, font);
            BuildCardTemplate(content, cardSprite, chevronSprite, font);

            // ── 右侧：任务详情 ──
            RectTransform detail = NewRect("Detail", rootRect);
            detail.anchorMin = Vector2.zero;
            detail.anchorMax = Vector2.one;
            detail.offsetMin = new Vector2(688f, 96f);
            detail.offsetMax = new Vector2(-72f, -120f);

            TMP_Text detailTitle = NewText("DetailTitle", detail, 30f, TextMain, TextAlignmentOptions.Left, font);
            Place(detailTitle.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 0f), new Vector2(0f, 44f));
            detailTitle.SetText("任务");

            // 当前目标胶囊：底色 + 文本（一个 GameObject 只能有一个 Graphic，文本放在子节点）
            RectTransform chip = NewRect("DetailGoalChip", detail);
            Place(chip, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -56f), new Vector2(360f, 36f));
            Image chipImage = NewImage(chip.gameObject, new Color(0.10f, 0.105f, 0.12f, 0.82f), chipSprite);
            if (chipImage != null)
            {
                chipImage.type = Image.Type.Sliced;
                chipImage.raycastTarget = false;
            }
            TMP_Text chipLabel = NewText("Label", chip, 16f, TextMain, TextAlignmentOptions.Center, font);
            chipLabel.rectTransform.anchorMin = Vector2.zero;
            chipLabel.rectTransform.anchorMax = Vector2.one;
            chipLabel.rectTransform.offsetMin = new Vector2(14f, 0f);
            chipLabel.rectTransform.offsetMax = new Vector2(-14f, 0f);
            chipLabel.SetText("当前目标");

            TMP_Text region = NewText("DetailRegion", detail, 15f, TextFaint, TextAlignmentOptions.Right, font);
            Place(region.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(0f, -58f), new Vector2(320f, 32f));
            region.SetText(string.Empty);

            TMP_Text desc = NewText("DetailDesc", detail, 17f, TextDim, TextAlignmentOptions.TopLeft, font);
            desc.rectTransform.anchorMin = new Vector2(0f, 1f);
            desc.rectTransform.anchorMax = new Vector2(1f, 1f);
            desc.rectTransform.pivot = new Vector2(0.5f, 1f);
            desc.rectTransform.anchoredPosition = new Vector2(0f, -108f);
            desc.rectTransform.sizeDelta = new Vector2(0f, 96f);
            desc.enableWordWrapping = true;
            desc.SetText(string.Empty);

            TMP_Text goals = NewText("DetailGoals", detail, 16f, TextDim, TextAlignmentOptions.TopLeft, font);
            goals.rectTransform.anchorMin = new Vector2(0f, 1f);
            goals.rectTransform.anchorMax = new Vector2(1f, 1f);
            goals.rectTransform.pivot = new Vector2(0.5f, 1f);
            goals.rectTransform.anchoredPosition = new Vector2(0f, -216f);
            goals.rectTransform.sizeDelta = new Vector2(0f, 120f);
            goals.enableWordWrapping = true;
            goals.SetText(string.Empty);

            // 奖励区
            RectTransform rewardSection = NewRect("RewardSection", detail);
            rewardSection.anchorMin = new Vector2(0f, 1f);
            rewardSection.anchorMax = new Vector2(1f, 1f);
            rewardSection.pivot = new Vector2(0.5f, 1f);
            rewardSection.anchoredPosition = new Vector2(0f, -352f);
            rewardSection.sizeDelta = new Vector2(0f, 110f);

            TMP_Text rewardTitle = NewText("RewardTitle", rewardSection, 15f, Gold, TextAlignmentOptions.Left, font);
            Place(rewardTitle.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 0f), new Vector2(0f, 24f));
            rewardTitle.SetText("完成任务时可获得");

            RectTransform rewardContent = NewRect("RewardContent", rewardSection);
            Place(rewardContent, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -30f), new Vector2(0f, 74f));
            var rewardLayout = rewardContent.gameObject.AddComponent<HorizontalLayoutGroup>();
            rewardLayout.childAlignment = TextAnchor.UpperLeft;
            rewardLayout.spacing = 12f;
            rewardLayout.childControlWidth = false;
            rewardLayout.childControlHeight = false;
            rewardLayout.childForceExpandWidth = false;
            rewardLayout.childForceExpandHeight = false;

            BuildRewardCell(rewardContent, cardSprite, font);

            // 主按钮（右下）
            RectTransform primary = NewRect("PrimaryButton", detail);
            Place(primary, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(0f, 8f), new Vector2(200f, 56f));
            Image primaryImage = NewImage(primary.gameObject, new Color(0.86f, 0.86f, 0.84f, 1f), roundSprite);
            Button primaryButton = primary.gameObject.AddComponent<Button>();
            primaryButton.targetGraphic = primaryImage;
            TMP_Text primaryLabel = NewText("Label", primary, 22f, new Color(0.13f, 0.13f, 0.13f, 1f), TextAlignmentOptions.Center, font);
            Stretch(primaryLabel.rectTransform);
            primaryLabel.SetText("追踪");

            TMP_Text status = NewText("StatusText", detail, 14f, TextFaint, TextAlignmentOptions.Left, font);
            Place(status.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 20f), new Vector2(-220f, 24f));
            status.SetText(string.Empty);

            // ── 层级与保存 ──
            root.layer = Layer.UI;
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                child.gameObject.layer = Layer.UI;

            int bindings = BindFields(rootRect, view, controlData);

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            UnityEngine.Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (prefab == null)
            {
                Debug.LogError($"[QuestView] 预制体保存失败：{PrefabPath}");
                return;
            }

            Debug.Log($"[QuestView] 已生成 {PrefabPath}，绑定 {bindings} 项");
            Selection.activeObject = prefab;
        }

        // ──────────────────────────────
        // 模板节点
        // ──────────────────────────────

        /// <summary>分组标题条：金色渐隐横条 + 标题 + 英文水印</summary>
        private static void BuildSectionTemplate(RectTransform content, Sprite barSprite, TMP_FontAsset font)
        {
            RectTransform section = NewRect("SectionTemplate", content);
            section.sizeDelta = new Vector2(0f, 30f);
            var element = section.gameObject.AddComponent<LayoutElement>();
            element.preferredHeight = 30f;
            element.minHeight = 30f;

            RectTransform bar = NewRect("Bar", section);
            Stretch(bar);
            Image barImage = NewImage(bar.gameObject, new Color(1f, 1f, 1f, 0.72f), barSprite);
            if (barImage != null) barImage.raycastTarget = false;

            TMP_Text label = NewText("Label", section, 15f, DeepBrown, TextAlignmentOptions.Left, font);
            Place(label.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(12f, 0f), new Vector2(-24f, 0f));
            label.SetText("主线任务");

            TMP_Text watermark = NewText("Watermark", section, 12f, new Color(1f, 1f, 1f, 0.10f), TextAlignmentOptions.Right, font);
            Place(watermark.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(-12f, 0f), new Vector2(-160f, 0f));
            watermark.SetText("MAIN");

            section.gameObject.SetActive(false);
        }

        /// <summary>任务卡片：底色 + 名称 + 副标题 + 展开箭头 + 金色下划线 + 目标行容器</summary>
        private static void BuildCardTemplate(RectTransform content, Sprite cardSprite, Sprite chevronSprite, TMP_FontAsset font)
        {
            RectTransform card = NewRect("CardTemplate", content);
            card.sizeDelta = new Vector2(0f, 96f);

            var layout = card.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.spacing = 4f;
            layout.padding = new RectOffset(16, 16, 10, 10);

            Image background = NewImage(card.gameObject, new Color(0.09f, 0.09f, 0.10f, 0.92f), cardSprite);
            if (background != null)
            {
                background.type = Image.Type.Sliced;
                background.raycastTarget = true;
            }
            Button button = card.gameObject.AddComponent<Button>();
            button.targetGraphic = background;

            TMP_Text name = NewText("Name", card, 20f, TextMain, TextAlignmentOptions.Left, font);
            AddLayoutHeight(name.gameObject, 26f);
            name.overflowMode = TextOverflowModes.Overflow;
            name.SetText("任务名称");

            TMP_Text subtitle = NewText("Subtitle", card, 13f, TextFaint, TextAlignmentOptions.Left, font);
            AddLayoutHeight(subtitle.gameObject, 20f);
            subtitle.overflowMode = TextOverflowModes.Overflow;
            subtitle.SetText("主线任务 · 进行中");

            RectTransform chevron = NewRect("Arrow", card);
            Place(chevron, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-14f, -14f), new Vector2(18f, 18f));
            chevron.pivot = new Vector2(0.5f, 0.5f);
            Image chevronImage = NewImage(chevron.gameObject, TextDim, chevronSprite);
            if (chevronImage != null)
            {
                chevronImage.preserveAspect = true;
                chevronImage.raycastTarget = false;
            }
            // 默认朝下（收起）；选中时由运行时旋成朝上
            chevron.localEulerAngles = new Vector3(0f, 0f, 180f);
            var chevronLayout = chevron.gameObject.AddComponent<LayoutElement>();
            chevronLayout.ignoreLayout = true;

            RectTransform underline = NewRect("Underline", card);
            Place(underline, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 0f), new Vector2(0f, 3f));
            Image underlineImage = NewImage(underline.gameObject, Gold, null);
            if (underlineImage != null) underlineImage.raycastTarget = false;
            var underlineLayout = underline.gameObject.AddComponent<LayoutElement>();
            underlineLayout.ignoreLayout = true;
            underline.gameObject.SetActive(false);

            RectTransform goalRoot = NewRect("GoalRoot", card);
            var goalLayout = goalRoot.gameObject.AddComponent<VerticalLayoutGroup>();
            goalLayout.childAlignment = TextAnchor.UpperLeft;
            goalLayout.childControlWidth = true;
            goalLayout.childControlHeight = true;
            goalLayout.childForceExpandWidth = true;
            goalLayout.childForceExpandHeight = false;
            goalLayout.spacing = 6f;
            goalLayout.padding = new RectOffset(4, 0, 6, 2);
            var goalFitter = goalRoot.gameObject.AddComponent<ContentSizeFitter>();
            goalFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            RectTransform goal = NewRect("GoalTemplate", goalRoot);
            goal.sizeDelta = new Vector2(0f, 24f);
            var goalElement = goal.gameObject.AddComponent<LayoutElement>();
            goalElement.minHeight = 24f;
            goalElement.preferredHeight = 24f;

            var goalRow = goal.gameObject.AddComponent<HorizontalLayoutGroup>();
            goalRow.childAlignment = TextAnchor.MiddleLeft;
            goalRow.spacing = 8f;
            goalRow.childControlWidth = false;
            goalRow.childControlHeight = true;
            goalRow.childForceExpandWidth = false;
            goalRow.childForceExpandHeight = false;

            RectTransform bullet = NewRect("Bullet", goal);
            bullet.sizeDelta = new Vector2(10f, 10f);
            Image bulletImage = NewImage(bullet.gameObject, new Color(0.55f, 0.56f, 0.58f, 1f), null);
            if (bulletImage != null) bulletImage.raycastTarget = false;
            var bulletLayout = bullet.gameObject.AddComponent<LayoutElement>();
            bulletLayout.minWidth = 10f;
            bulletLayout.preferredWidth = 10f;

            TMP_Text goalLabel = NewText("Label", goal, 14f, TextDim, TextAlignmentOptions.Left, font);
            var goalLabelLayout = goalLabel.gameObject.AddComponent<LayoutElement>();
            goalLabelLayout.minHeight = 24f;
            goalLabelLayout.preferredHeight = 24f;
            goalLabelLayout.flexibleWidth = 1f;
            goalLabel.overflowMode = TextOverflowModes.Overflow;
            goalLabel.SetText("目标");

            goal.gameObject.SetActive(false);
            card.gameObject.SetActive(false);
        }

        /// <summary>奖励格：深色底 + 图标 + 右下数量</summary>
        private static void BuildRewardCell(RectTransform parent, Sprite cardSprite, TMP_FontAsset font)
        {
            RectTransform cell = NewRect("CellTemplate", parent);
            cell.sizeDelta = new Vector2(66f, 66f);
            var element = cell.gameObject.AddComponent<LayoutElement>();
            element.minWidth = 66f;
            element.preferredWidth = 66f;
            element.minHeight = 66f;
            element.preferredHeight = 66f;

            Image background = NewImage(cell.gameObject, new Color(0.155f, 0.160f, 0.175f, 1f), cardSprite);
            if (background != null)
            {
                background.type = Image.Type.Sliced;
                background.raycastTarget = false;
            }

            RectTransform icon = NewRect("Icon", cell);
            icon.anchorMin = Vector2.zero;
            icon.anchorMax = Vector2.one;
            icon.offsetMin = new Vector2(7f, 7f);
            icon.offsetMax = new Vector2(-7f, -7f);
            Image iconImage = NewImage(icon.gameObject, Color.white, null);
            if (iconImage != null)
            {
                iconImage.preserveAspect = true;
                iconImage.raycastTarget = false;
                iconImage.enabled = false;   // 没有图标时隐藏，保留底框
            }

            TMP_Text count = NewText("Count", cell, 13f, TextMain, TextAlignmentOptions.BottomRight, font);
            Place(count.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(4f, 2f), new Vector2(-8f, 18f));
            count.overflowMode = TextOverflowModes.Overflow;
            count.SetText("1");

            cell.gameObject.SetActive(false);
        }

        private static void BuildNavButton(RectTransform navBar, string name, string iconName, bool selected,
            Sprite circleSprite, TMP_FontAsset font)
        {
            int index = navBar.childCount;
            RectTransform button = NewRect(name, navBar);
            button.anchorMin = new Vector2(0.5f, 1f);
            button.anchorMax = new Vector2(0.5f, 1f);
            button.pivot = new Vector2(0.5f, 1f);
            button.anchoredPosition = new Vector2(0f, -index * 78f);
            button.sizeDelta = new Vector2(56f, 56f);

            Image background = NewImage(button.gameObject, new Color(0.10f, 0.105f, 0.12f, selected ? 0.95f : 0.55f), circleSprite);
            Button navButton = button.gameObject.AddComponent<Button>();
            navButton.targetGraphic = background;

            Sprite iconSprite = AssetDatabase.LoadAssetAtPath<Sprite>($"{DashijieIconFolder}/{iconName}.png");
            RectTransform icon = NewRect("Icon", button);
            icon.anchorMin = Vector2.zero;
            icon.anchorMax = Vector2.one;
            icon.offsetMin = new Vector2(12f, 12f);
            icon.offsetMax = new Vector2(-12f, -12f);
            Image iconImage = NewImage(icon.gameObject, selected ? Gold : new Color(1f, 1f, 1f, 0.62f), iconSprite);
            if (iconImage != null)
            {
                iconImage.preserveAspect = true;
                iconImage.raycastTarget = false;
            }

            if (!selected) return;

            // 选中态：外圈金色描边
            RectTransform ring = NewRect("Ring", button);
            ring.anchorMin = Vector2.zero;
            ring.anchorMax = Vector2.one;
            ring.offsetMin = new Vector2(-3f, -3f);
            ring.offsetMax = new Vector2(3f, 3f);
            Image ringImage = NewImage(ring.gameObject, new Color(Gold.r, Gold.g, Gold.b, 0.55f), circleSprite);
            if (ringImage != null) ringImage.raycastTarget = false;
            ring.SetAsFirstSibling();
        }

        // ──────────────────────────────
        // 绑定
        // ──────────────────────────────

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
                    ? FindDeep(panelRoot, attr.path)
                    : (FindDeep(panelRoot, field.Name)
                       ?? FindDeep(panelRoot, char.ToUpper(field.Name[0]) + field.Name.Substring(1)));

                if (target == null)
                {
                    Debug.LogWarning($"[QuestView] 找不到 {field.DeclaringType.Name}.{field.Name} 的节点");
                    continue;
                }

                UnityEngine.Object component = field.FieldType == typeof(GameObject)
                    ? target.gameObject
                    : target.GetComponent(field.FieldType);
                if (component == null)
                {
                    Debug.LogWarning($"[QuestView] {target.name} 上没有 {field.FieldType.Name}（字段 {field.Name}）");
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

        /// <summary>
        /// 按路径找节点：逐段解析，每段先找直接子节点，找不到再在子树里按名字找。
        /// （面板层级较深，纯直接子节点查找会把嵌套节点判成"找不到"。）
        /// </summary>
        private static Transform FindDeep(Transform root, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            Transform current = root;
            foreach (string segment in path.Split('/'))
            {
                if (string.IsNullOrEmpty(segment)) continue;
                current = ResolveChild(current, segment);
                if (current == null) return null;
            }
            return current;
        }

        private static Transform ResolveChild(Transform root, string name)
        {
            Transform direct = root.Find(name);
            if (direct != null) return direct;

            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child != root && child.name == name) return child;
            }
            return null;
        }

        // ──────────────────────────────
        // 节点工具
        // ──────────────────────────────

        private static RectTransform NewRect(string name, Transform parent)
        {
            GameObject go = new(name, typeof(RectTransform));
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.localScale = Vector3.one;
            return rect;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        /// <summary>四角锚定 + 同角 pivot（角/边锚定配中心 pivot 会让元素沉出屏幕）</summary>
        private static void Place(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 position, Vector2 size)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = new Vector2((anchorMin.x + anchorMax.x) * 0.5f, (anchorMin.y + anchorMax.y) * 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        private static Image NewImage(GameObject target, Color color, Sprite sprite)
        {
            EnsureCanvasRenderer(target);
            Image image = target.AddComponent<Image>();
            image.color = color;
            image.sprite = sprite;
            image.raycastTarget = false;
            if (sprite != null && sprite.border != Vector4.zero) image.type = Image.Type.Sliced;
            return image;
        }

        private static TMP_Text NewText(string name, Transform parent, float size, Color color,
            TextAlignmentOptions alignment, TMP_FontAsset font)
        {
            GameObject go = new(name, typeof(RectTransform));
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.localScale = Vector3.one;

            EnsureCanvasRenderer(go);
            TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
            ConfigureText(text, size, color, alignment, font);
            return text;
        }

        private static void ConfigureText(TMP_Text text, float size, Color color,
            TextAlignmentOptions alignment, TMP_FontAsset font)
        {
            text.fontSize = size;
            text.color = color;
            text.alignment = alignment;
            text.raycastTarget = false;
            // 固定单行控件用 Overflow：Ellipsis 在行框略矮时会把整段裁掉
            text.overflowMode = TextOverflowModes.Overflow;
            if (font != null) text.font = font;
        }

        private static void AddLayoutHeight(GameObject target, float height)
        {
            var element = target.GetComponent<LayoutElement>() ?? target.AddComponent<LayoutElement>();
            element.minHeight = height;
            element.preferredHeight = height;
        }

        private static void EnsureCanvasRenderer(GameObject target)
        {
            // Tuanjie 的 uGUI 分叉里 Graphic 基类不声明 RequireComponent(CanvasRenderer)
            if (target.GetComponent<CanvasRenderer>() == null) target.AddComponent<CanvasRenderer>();
        }

        // ──────────────────────────────
        // 程序化美术
        // ──────────────────────────────

        /// <summary>圆角白块（染色基底要近白，深色底图乘亮色会变暗橄榄像禁用）</summary>
        private static Sprite EnsureRoundedSprite(string name, int size, int radius, Color color)
        {
            string path = $"{ArtFolder}/{name}.png";
            Sprite existing = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (existing != null) return existing;

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float alpha = RoundedAlpha(x + 0.5f, y + 0.5f, size, radius);
                pixels[y * size + x] = new Color(color.r, color.g, color.b, color.a * alpha);
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            File.WriteAllBytes(path, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);

            ImportSprite(path, new Vector4(radius, radius, radius, radius));
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        /// <summary>左侧亮、向右渐隐的金色横条（分组标题条底）</summary>
        private static Sprite EnsureGradientSprite(string name, int width, int height, Color color)
        {
            string path = $"{ArtFolder}/{name}.png";
            Sprite existing = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (existing != null) return existing;

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];
            int radius = height / 2;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float rounded = RoundedAlpha(x + 0.5f, y + 0.5f, height, radius, width);
                float fade = Mathf.Lerp(1f, 0f, (float)x / (width - 1));
                pixels[y * width + x] = new Color(color.r, color.g, color.b, rounded * Mathf.Lerp(0.15f, 0.92f, fade));
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            File.WriteAllBytes(path, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);

            ImportSprite(path, new Vector4(0f, 0f, 0f, 0f), width, height);
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        /// <summary>圆角遮罩（宽高不同时按 height 作圆角半径基准，带 1px 抗锯齿）</summary>
        private static float RoundedAlpha(float x, float y, int size, int radius, int width = -1)
        {
            int w = width > 0 ? width : size;
            int h = size;
            float cx = Mathf.Min(x, w - x);
            float cy = Mathf.Min(y, h - y);
            if (cx >= radius || cy >= radius) return 1f;

            float dx = radius - cx;
            float dy = radius - cy;
            float distance = Mathf.Sqrt(dx * dx + dy * dy);
            return Mathf.Clamp01(radius - distance + 0.5f);
        }

        private static void ImportSprite(string path, Vector4 border, int width = 0, int height = 0)
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            if (importer == null) return;

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = 100f;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.npotScale = TextureImporterNPOTScale.None;
            if (border != Vector4.zero)
            {
                TextureImporterSettings settings = new();
                importer.ReadTextureSettings(settings);
                settings.spriteBorder = border;
                importer.SetTextureSettings(settings);
            }
            importer.SaveAndReimport();
        }

        private static void EnsureFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path)?.Replace("\\", "/");
            string leaf = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
#endif
