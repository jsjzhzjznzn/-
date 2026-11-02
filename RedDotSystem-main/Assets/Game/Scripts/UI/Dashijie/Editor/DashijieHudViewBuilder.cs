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
    /// 大世界 HUD 预制体构建器（可重复执行）
    /// 菜单：Tools/UI/重建大世界 HUD
    /// 产物：Assets/Game/UI/Dashijie/DashijieHudView.prefab
    ///
    /// 布局以 1920×1080 为基准，四角锚定 + 同角 pivot（屏幕比例变化时不越界）：
    ///   左上小地图 + 任务卡 / 右上十个圆形热键 / 底部角色血条 / 左下聊天入口。
    /// 不做：摇杆、攻击/技能/跳跃按钮、交互提示条。
    /// </summary>
    public static class DashijieHudViewBuilder
    {
        private const string PrefabPath = "Assets/Game/UI/Dashijie/DashijieHudView.prefab";
        private const string IconFolder = "Assets/Game/UI/Dashijie/Icons";
        private const string CircleSpritePath = "Assets/Game/UI/Dashijie/Icons/dashijie_circle.png";
        private const string PanelSpritePath = "Assets/Game/Resources/ui/Theme/panel_round.png";
        private const string RoundSpritePath = "Assets/Game/Resources/ui/Theme/btn_round.png";
        private const string FontPath = "Assets/Codely/Fonts/NotoSansSC-Regular SDF.asset";
        private const string MinimapTexturePath = "Assets/Game/Resources/Minimap/dashijie_minimap.png";
        private const string MinimapArrowPath = "Assets/Game/UI/Dashijie/Icons/minimap_arrow.png";
        private const string MinimapRingPath = "Assets/Game/UI/Dashijie/Icons/minimap_ring.png";
        private const string QuestMarkPath = "Assets/Game/UI/Dashijie/Icons/dashijie_quest_mark.png";

        private const float MinimapSize = 240f;
        private const float MinimapRingSize = 248f;

        private static readonly Color PanelColor = new(0.075f, 0.086f, 0.106f, 0.82f);
        private static readonly Color PlateColor = new(0.075f, 0.086f, 0.106f, 0.55f);
        private static readonly Color TextMain = new(0.925f, 0.906f, 0.855f, 1f);
        private static readonly Color TextDim = new(0.639f, 0.647f, 0.663f, 1f);
        private static readonly Color Gold = new(0.910f, 0.769f, 0.416f, 1f);
        private static readonly Color HpGreen = new(0.451f, 0.780f, 0.478f, 1f);
        private static readonly Color HpBack = new(0.055f, 0.063f, 0.075f, 0.85f);

        private const float HotkeySize = 52f;
        private const float HotkeyGap = 8f;

        [MenuItem("Tools/UI/重建大世界 HUD")]
        public static void Build()
        {
            TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
            Sprite panelSprite = LoadSprite(PanelSpritePath);
            Sprite circleSprite = LoadSprite(CircleSpritePath);
            Sprite roundSprite = LoadSprite(RoundSpritePath);

            var wrapper = new GameObject("DashijieHudView", typeof(RectTransform));
            RectTransform wrapperRect = wrapper.GetComponent<RectTransform>();
            wrapperRect.anchorMin = Vector2.zero;
            wrapperRect.anchorMax = Vector2.one;
            wrapperRect.offsetMin = Vector2.zero;
            wrapperRect.offsetMax = Vector2.zero;

            var view = wrapper.AddComponent<DashijieHudView>();
            var controlData = wrapper.AddComponent<UIControlData>();

            // ── 内容根（面板打开时整体隐藏；根节点常驻以便 Update 读热键） ──
            RectTransform content = NewRect("contentGroup", wrapperRect);
            Stretch(content);
            var contentGroup = content.gameObject.AddComponent<CanvasGroup>();

            // ── 左上：小地图（圆形裁剪窗口 + 箭头 + 外环 + N 徽记） ──
            // 窗口本身是 RawImage + MinimapClip 材质；底图靠 uvRect 采样，不移动大 quad。
            RectTransform minimapRoot = NewRect("MinimapRoot", content);
            Place(minimapRoot, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(36f, -32f), new Vector2(MinimapSize, MinimapSize));

            // 底衬（同时是点击入口：地图/外环/箭头的 raycastTarget 全关，点击落到这一层）
            RectTransform minimapBackdrop = NewRect("MinimapBackdrop", minimapRoot);
            Center(minimapBackdrop, MinimapRingSize);
            Image backdropImage = NewImage(minimapBackdrop.gameObject, new(0.055f, 0.063f, 0.075f, 0.9f), circleSprite);
            if (backdropImage != null) backdropImage.raycastTarget = true;
            Button minimapButton = minimapBackdrop.gameObject.AddComponent<Button>();
            minimapButton.targetGraphic = backdropImage;

            // 圆形窗口：Mask 用圆形 sprite 的 alpha 裁剪。
            // 不能用「自定义 shader 按 uv 裁」：RawImage 的 uv 来自 uvRect（0.36~0.64），不是 0~1。
            RectTransform minimapMask = NewRect("MinimapMask", minimapRoot);
            Center(minimapMask, MinimapSize);
            Image maskImage = NewImage(minimapMask.gameObject, Color.white, circleSprite);
            if (maskImage != null) maskImage.raycastTarget = false;
            var mask = minimapMask.gameObject.AddComponent<Mask>();
            mask.showMaskGraphic = false;

            RectTransform minimapMapRect = NewRect("MinimapMap", minimapMask);
            Center(minimapMapRect, MinimapSize);
            EnsureCanvasRenderer(minimapMapRect.gameObject);
            var minimapMapImage = minimapMapRect.gameObject.AddComponent<RawImage>();
            minimapMapImage.texture = AssetDatabase.LoadAssetAtPath<Texture2D>(MinimapTexturePath);
            minimapMapImage.raycastTarget = false;
            minimapMapImage.uvRect = new Rect(0.36f, 0.36f, 0.28f, 0.28f);

            RectTransform minimapRing = NewRect("MinimapRing", minimapRoot);
            Center(minimapRing, MinimapRingSize);
            Sprite ringSprite = AssetDatabase.LoadAssetAtPath<Sprite>(MinimapRingPath);
            Image ringImage = NewImage(minimapRing.gameObject, Color.white, ringSprite);
            if (ringImage != null) ringImage.raycastTarget = false;

            RectTransform minimapArrowRect = NewRect("MinimapArrow", minimapRoot);
            Center(minimapArrowRect, 30f);
            Sprite arrowSprite = AssetDatabase.LoadAssetAtPath<Sprite>(MinimapArrowPath);
            Image arrowImage = NewImage(minimapArrowRect.gameObject, Gold, arrowSprite);
            if (arrowImage != null)
            {
                arrowImage.preserveAspect = true;
                arrowImage.raycastTarget = false;
            }

            TMP_Text northText = NewText("MinimapNorth", minimapRoot, 12f, Gold, TextAlignmentOptions.Center, font);
            Place(northText.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -4f), new Vector2(24f, 16f));
            northText.SetText("N");

            // 任务目标标记：追踪中任务的当前目标 NPC（位置每帧由 DashijieHudView 按世界坐标换算）
            RectTransform questMarker = NewRect("questMarker", minimapRoot);
            Center(questMarker, 22f);
            Sprite questMarkSprite = EnsureQuestMarkSprite();
            Image questMarkImage = NewImage(questMarker.gameObject, Gold, questMarkSprite);
            if (questMarkImage != null)
            {
                questMarkImage.preserveAspect = true;
                questMarkImage.raycastTarget = false;
            }
            questMarker.gameObject.SetActive(false);

            // ── 左上：任务追踪卡（下移到小地图下方） ──
            RectTransform tracker = NewRect("questTrackerRoot", content);
            Place(tracker, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(36f, -296f), new Vector2(360f, 112f));
            Image trackerBg = NewImage(tracker.gameObject, PanelColor, panelSprite);
            if (trackerBg != null)
            {
                trackerBg.type = Image.Type.Sliced;
                trackerBg.raycastTarget = true;
            }
            Button trackerButton = tracker.gameObject.AddComponent<Button>();
            trackerButton.targetGraphic = trackerBg;

            RectTransform accent = NewRect("Accent", tracker);
            Place(accent, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 8f), new Vector2(4f, -16f));
            Image accentImage = NewImage(accent.gameObject, Gold, null);
            if (accentImage != null) accentImage.raycastTarget = false;

            TMP_Text questTitle = NewText("questTitleText", tracker, 19f, TextMain, TextAlignmentOptions.Left, font);
            Place(questTitle.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(20f, -14f), new Vector2(-36f, 28f));
            questTitle.SetText("暂无进行中的任务");

            TMP_Text questGoal = NewText("questGoalText", tracker, 14f, TextDim, TextAlignmentOptions.Left, font);
            Place(questGoal.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(20f, -46f), new Vector2(-120f, 22f));
            questGoal.SetText(string.Empty);

            TMP_Text questProgress = NewText("questProgressText", tracker, 15f, Gold, TextAlignmentOptions.Right, font);
            Place(questProgress.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-20f, -46f), new Vector2(80f, 22f));
            questProgress.SetText(string.Empty);

            // ── 右上：十个功能热键 ──
            int hotkeyCount = 10;
            float rowWidth = hotkeyCount * HotkeySize + (hotkeyCount - 1) * HotkeyGap;
            RectTransform row = NewRect("HotkeyRow", content);
            Place(row, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-28f, -22f), new Vector2(rowWidth, 64f));

            // 行底板：天空等亮背景上圆形按钮会糊掉，垫一层半透明深色圆角片
            RectTransform rowPlate = NewRect("HotkeyRowPlate", row);
            rowPlate.anchorMin = Vector2.zero;
            rowPlate.anchorMax = Vector2.one;
            rowPlate.offsetMin = new Vector2(-10f, -8f);
            rowPlate.offsetMax = new Vector2(10f, 8f);
            Image rowPlateImage = NewImage(rowPlate.gameObject, PlateColor, panelSprite);
            if (rowPlateImage != null)
            {
                rowPlateImage.type = Image.Type.Sliced;
                rowPlateImage.raycastTarget = false;
            }

            CreateHotkey(row, "bagHotkeyButton", 0, "背", "dashijie_icon_bag", "B", circleSprite, font);
            CreateHotkey(row, "questHotkeyButton", 1, "任", "dashijie_icon_quest", "Y", circleSprite, font);
            CreateHotkey(row, "characterHotkeyButton", 2, "角", "dashijie_icon_character", "C", circleSprite, font);
            CreateHotkey(row, "achievementHotkeyButton", 3, "成", "dashijie_icon_achievement", "V", circleSprite, font);
            CreateHotkey(row, "mailHotkeyButton", 4, "邮", "dashijie_icon_mail", "N", circleSprite, font);
            CreateHotkey(row, "friendHotkeyButton", 5, "友", "dashijie_icon_friend", "G", circleSprite, font);
            CreateHotkey(row, "leaderboardHotkeyButton", 6, "榜", "dashijie_icon_leaderboard", "L", circleSprite, font);
            CreateHotkey(row, "worldChatHotkeyButton", 7, "聊", "dashijie_icon_chat", "T", circleSprite, font);
            CreateHotkey(row, "mapHotkeyButton", 8, "图", "dashijie_icon_map", "M", circleSprite, font);
            CreateHotkey(row, "settingsHotkeyButton", 9, "设", "dashijie_icon_settings", "Esc", circleSprite, font);

            // ── 底部中央：角色名 + 等级 + 血条 + 切换提示 ──
            RectTransform playerBar = NewRect("PlayerBar", content);
            Place(playerBar, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 52f), new Vector2(760f, 104f));

            // 血条不再垫整块深色底板（参考图是浮在画面上的），只给条本身加描边保证亮场景可读
            Sprite emblemSprite = EnsureHpEmblemSprite();
            Sprite segmentSprite = EnsureHpSegmentSprite();
            Sprite barSprite = EnsureHpBarSprite();
            Sprite shadowSprite = EnsureHpShadowSprite();

            // 软阴影：场景地面很亮，纯白条与白字直接压上去会糊，垫一层几乎看不见边界的暗底
            RectTransform shadow = NewRect("hpShadow", playerBar);
            shadow.anchorMin = Vector2.zero;
            shadow.anchorMax = Vector2.one;
            shadow.offsetMin = new Vector2(-56f, -14f);
            shadow.offsetMax = new Vector2(56f, 8f);
            Image shadowImage = NewImage(shadow.gameObject, new Color(0f, 0f, 0f, 1f), shadowSprite);
            if (shadowImage != null) shadowImage.raycastTarget = false;

            // 左侧徽记（金色）
            RectTransform emblem = NewRect("hpEmblem", playerBar);
            Place(emblem, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 36f), new Vector2(70f, 70f));
            Image emblemImage = NewImage(emblem.gameObject, new Color(0.98f, 0.84f, 0.50f, 1f), emblemSprite);
            if (emblemImage != null)
            {
                emblemImage.preserveAspect = true;
                emblemImage.raycastTarget = false;
            }

            // 条上方的分段点线（金色小点）
            RectTransform segments = NewRect("hpSegments", playerBar);
            Place(segments, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(52f, 66f), new Vector2(-166f, 10f));
            Image segmentImage = NewImage(segments.gameObject, new Color(0.98f, 0.84f, 0.50f, 0.95f), segmentSprite);
            if (segmentImage != null) segmentImage.raycastTarget = false;

            // 血条：深色描边轨道 + 亮色填充（两端尖角的胶囊）
            RectTransform hpBackRect = NewRect("hpBack", playerBar);
            Place(hpBackRect, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(52f, 38f), new Vector2(-166f, 26f));
            Image hpBackImage = NewImage(hpBackRect.gameObject, new Color(0.10f, 0.11f, 0.13f, 0.92f), barSprite);
            if (hpBackImage != null)
            {
                hpBackImage.raycastTarget = false;
            }

            RectTransform hpFillRect = NewRect("hpFill", hpBackRect);
            Stretch(hpFillRect);
            hpFillRect.offsetMin = new Vector2(2f, 2f);
            hpFillRect.offsetMax = new Vector2(-2f, -2f);
            Image hpFill = NewImage(hpFillRect.gameObject, new Color(0.95f, 0.96f, 0.99f, 1f), barSprite);
            if (hpFill != null)
            {
                hpFill.type = Image.Type.Filled;
                hpFill.fillMethod = Image.FillMethod.Horizontal;
                hpFill.fillOrigin = (int)Image.OriginHorizontal.Left;
                hpFill.fillAmount = 1f;
                hpFill.raycastTarget = false;
            }

            // 数值压在条上（参考图是深色字压在浅色条上）
            TMP_Text hpText = NewText("hpText", hpBackRect, 17f, new Color(0.12f, 0.13f, 0.15f, 1f), TextAlignmentOptions.Center, font);
            Stretch(hpText.rectTransform);
            hpText.SetText("100/100");

            TMP_Text playerName = NewText("playerNameText", playerBar, 20f, new Color(0.98f, 0.96f, 0.90f, 1f), TextAlignmentOptions.Left, font);
            Place(playerName.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(78f, -2f), new Vector2(260f, 26f));
            playerName.SetText("安比");
            AddDropShadow(playerName);

            TMP_Text playerLevel = NewText("playerLevelText", playerBar, 22f, TextMain, TextAlignmentOptions.Left, font);
            Place(playerLevel.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(0f, 38f), new Vector2(100f, 32f));
            playerLevel.SetText("Lv.1");
            AddDropShadow(playerLevel);

            TMP_Text switchHint = NewText("switchHintText", playerBar, 13f, new Color(0.86f, 0.87f, 0.89f, 1f), TextAlignmentOptions.Left, font);
            Place(switchHint.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(78f, 6f), new Vector2(340f, 16f));
            switchHint.SetText("1 安比 · 2 艾莲");
            AddDropShadow(switchHint);

            // ── 左下：聊天入口 ──
            RectTransform chat = NewRect("chatBubbleButton", content);
            Place(chat, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(36f, 36f), new Vector2(HotkeySize, HotkeySize));
            Image chatBg = AddCircleBackground(chat, circleSprite);
            Button chatButton = chat.gameObject.AddComponent<Button>();
            chatButton.targetGraphic = chatBg;

            Sprite chatIcon = LoadIcon("dashijie_icon_chat");
            if (chatIcon != null)
            {
                AddIcon(chat, chatIcon, 30f);
            }
            else
            {
                TMP_Text chatLabel = NewText("Label", chat, 18f, TextMain, TextAlignmentOptions.Center, font);
                Stretch(chatLabel.rectTransform);
                chatLabel.SetText("聊");
            }

            // ── 绑定与保存 ──
            int bindings = BindFields(wrapper.transform, view, controlData);

            wrapper.layer = Layer.UI;
            foreach (Transform child in wrapper.GetComponentsInChildren<Transform>(true))
                child.gameObject.layer = Layer.UI;

            EnsureFolder(Path.GetDirectoryName(PrefabPath)?.Replace("\\", "/"));
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(wrapper, PrefabPath);
            UnityEngine.Object.DestroyImmediate(wrapper);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[DashijieHudView] 生成 {PrefabPath}，绑定 {bindings} 项");
            Selection.activeObject = prefab;
        }

        // ──────────────────────────────
        // 热键
        // ──────────────────────────────

        private static void CreateHotkey(
            RectTransform row, string name, int index, string glyph, string iconName, string keyLabel,
            Sprite sprite, TMP_FontAsset font)
        {
            RectTransform buttonRect = NewRect(name, row);
            buttonRect.anchorMin = new Vector2(0f, 0.5f);
            buttonRect.anchorMax = new Vector2(0f, 0.5f);
            buttonRect.pivot = new Vector2(0f, 0.5f);
            buttonRect.anchoredPosition = new Vector2(index * (HotkeySize + HotkeyGap), 0f);
            buttonRect.sizeDelta = new Vector2(HotkeySize, HotkeySize);

            Image bg = AddCircleBackground(buttonRect, sprite);

            Button button = buttonRect.gameObject.AddComponent<Button>();
            button.targetGraphic = bg;

            // 有 AI 图标就用图标；缺失时回退成中文单字，保证预制体重建不会出现空白按钮
            Sprite icon = LoadIcon(iconName);
            if (icon != null)
            {
                AddIcon(buttonRect, icon, 26f, new Vector2(0f, 3f));
            }
            else
            {
                TMP_Text label = NewText("Label", buttonRect, 20f, TextMain, TextAlignmentOptions.Center, font);
                Stretch(label.rectTransform);
                label.SetText(glyph);
            }

            // 键位角标放在圆内底部中线上，不与图标重叠
            TMP_Text badge = NewText("KeyBadge", buttonRect, 9f, Gold, TextAlignmentOptions.Center, font);
            Place(badge.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 2f), new Vector2(36f, 12f));
            badge.SetText(keyLabel);
        }

        /// <summary>深色半透明圆底（贴图里已烘焙细亮边，见 dashijie_circle.png）</summary>
        private static Image AddCircleBackground(RectTransform buttonRect, Sprite sprite)
        {
            Image image = NewImage(buttonRect.gameObject, Color.white, sprite);
            if (image != null) image.raycastTarget = true;
            return image;
        }

        /// <summary>圆形按钮中心的白线稿图标（等比缩放，不拉伸）</summary>
        private static void AddIcon(RectTransform parent, Sprite icon, float size = 28f, Vector2? offset = null)
        {
            RectTransform rect = NewRect("Icon", parent);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = offset ?? Vector2.zero;
            rect.sizeDelta = new Vector2(size, size);

            Image image = NewImage(rect.gameObject, Color.white, icon);
            if (image != null)
            {
                image.preserveAspect = true;
                image.raycastTarget = false;
            }
        }

        private static Sprite LoadIcon(string name)
        {
            return AssetDatabase.LoadAssetAtPath<Sprite>($"{IconFolder}/{name}.png");
        }

        // ──────────────────────────────
        // 构建工具
        // ──────────────────────────────

        private static RectTransform NewRect(string name, Transform parent)
        {
            GameObject go = new(name, typeof(RectTransform));
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
            string name, Transform parent, float size, Color color, TextAlignmentOptions alignment, TMP_FontAsset font)
        {
            GameObject go = new(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            EnsureCanvasRenderer(go);
            TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
            if (font != null) text.font = font;
            text.fontSize = size;
            text.color = color;
            text.alignment = alignment;
            text.raycastTarget = false;
            text.enableWordWrapping = false;
            // 单行固定文案用 Overflow：Ellipsis 在“行高略大于行框”时会把整段字符全部裁掉（文字直接消失）
            text.overflowMode = TextOverflowModes.Overflow;
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

        /// <summary>居中铺满：锚点/pivot 都在中心，便于旋转与等比缩放</summary>
        private static void Center(RectTransform rect, float size)
        {
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(size, size);
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
            if (target.GetComponent<CanvasRenderer>() == null) target.AddComponent<CanvasRenderer>();
        }

        /// <summary>按名递归查找（HUD 的控件分布在 contentGroup 下的各个区块，不是根的直接子节点）</summary>
        private static Transform FindDeep(Transform root, string name)
        {
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child != root && child.name == name) return child;
            }
            return null;
        }

        private static Sprite LoadSprite(string path) => AssetDatabase.LoadAssetAtPath<Sprite>(path);

        /// <summary>
        /// 任务目标标记（金色四角菱形）：缺失时程序化生成。
        /// 与小地图美术同一套写法（1px 抗锯齿 + Sprite 导入设置），删掉 PNG 重跑菜单可强制重生成。
        /// </summary>
        private static Sprite EnsureQuestMarkSprite()
        {
            Sprite existing = LoadSprite(QuestMarkPath);
            if (existing != null) return existing;

            const int size = 128;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            float center = size * 0.5f;
            float radius = size * 0.40f;

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = Mathf.Abs(x + 0.5f - center);
                float dy = Mathf.Abs(y + 0.5f - center);
                float distance = dx + dy;                                    // 曼哈顿距离 = 菱形
                float alpha = Mathf.Clamp01((radius - distance) / 1.5f);
                // 内部镂空一点点，形成描边感
                float innerAlpha = Mathf.Clamp01((radius - 6f - distance) / 1.5f);
                Color color = new(0.910f, 0.769f, 0.416f, alpha - innerAlpha * 0.35f);
                pixels[y * size + x] = color;
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            File.WriteAllBytes(QuestMarkPath, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);

            AssetDatabase.ImportAsset(QuestMarkPath, ImportAssetOptions.ForceUpdate);
            var importer = (TextureImporter)AssetImporter.GetAtPath(QuestMarkPath);
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spritePixelsPerUnit = 100f;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Bilinear;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }

            return LoadSprite(QuestMarkPath);
        }

        // ──────────────────────────────
        // 血条美术（左侧徽记 / 上方分段点线 / 两端尖角的条）
        // ──────────────────────────────

        private const string HpEmblemPath = "Assets/Game/UI/Dashijie/Icons/hp_emblem.png";
        private const string HpSegmentPath = "Assets/Game/UI/Dashijie/Icons/hp_segment_line.png";
        private const string HpBarPath = "Assets/Game/UI/Dashijie/Icons/hp_bar.png";
        private const string HpShadowPath = "Assets/Game/UI/Dashijie/Icons/hp_shadow.png";

        /// <summary>血条软阴影：椭圆形柔边暗底（只有 alpha，颜色交给 Image.color）</summary>
        private static Sprite EnsureHpShadowSprite()
        {
            Sprite existing = LoadSprite(HpShadowPath);
            if (existing != null) return existing;

            const int width = 128;
            const int height = 32;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];

            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float nx = (x + 0.5f) / width * 2f - 1f;
                float ny = (y + 0.5f) / height * 2f - 1f;
                float d = Mathf.Sqrt(nx * nx * 0.85f + ny * ny * 1.5f);
                float alpha = Mathf.Clamp01(1f - d);
                alpha = Mathf.Pow(alpha, 0.9f) * 0.75f;
                pixels[y * width + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return WriteSprite(HpShadowPath, texture);
        }

        /// <summary>左侧罗盘徽记：外圈点线 + 内圈细环 + 八角星（金色，白色底由 Image.color 染色）</summary>
        private static Sprite EnsureHpEmblemSprite()
        {
            Sprite existing = LoadSprite(HpEmblemPath);
            if (existing != null) return existing;

            const int size = 128;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            float center = size * 0.5f;
            float ringRadius = 42f;
            float dotRadius = 57f;

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - center;
                float dy = y + 0.5f - center;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                float angle = Mathf.Atan2(dy, dx);

                float alpha = 0f;

                // 内圈细环
                alpha = Mathf.Max(alpha, Mathf.Clamp01(2.2f - Mathf.Abs(r - ringRadius)));

                // 外圈点线
                float stepAngle = 12f * Mathf.Deg2Rad;
                float a = Mathf.Repeat(angle, stepAngle) - stepAngle * 0.5f;
                float dotDist = Mathf.Sqrt(Mathf.Pow(r - dotRadius, 2f) + Mathf.Pow(a * dotRadius, 2f));
                alpha = Mathf.Max(alpha, Mathf.Clamp01(2.6f - dotDist));

                // 八角星：外径 34 / 内径 12 的星形多边形
                float starAlpha = InsideStar(dx, dy, 34f, 12f, 8) ? 1f : 0f;
                alpha = Mathf.Max(alpha, starAlpha);

                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(alpha) * 255f));
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return WriteSprite(HpEmblemPath, texture);
        }

        /// <summary>条上方那排金色小点（白色点，靠 Image.color 上色）</summary>
        private static Sprite EnsureHpSegmentSprite()
        {
            Sprite existing = LoadSprite(HpSegmentPath);
            if (existing != null) return existing;

            const int width = 512;
            const int height = 8;
            const float spacing = 9f;
            const float dotRadius = 1.9f;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];

            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float px = Mathf.Repeat(x + 0.5f, spacing) - spacing * 0.5f;
                float py = y + 0.5f - height * 0.5f;
                float dist = Mathf.Sqrt(px * px + py * py);
                float alpha = Mathf.Clamp01(dotRadius - dist);
                pixels[y * width + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return WriteSprite(HpSegmentPath, texture);
        }

        /// <summary>血条形状：两端收成尖角的横向胶囊（白色，轨道/填充各自染色）</summary>
        private static Sprite EnsureHpBarSprite()
        {
            Sprite existing = LoadSprite(HpBarPath);
            if (existing != null) return existing;

            const int width = 256;
            const int height = 24;
            const float cap = 9f;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];
            float halfHeight = height * 0.5f;

            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float py = Mathf.Abs(y + 0.5f - halfHeight);
                float pxLeft = x + 0.5f;
                float pxRight = width - (x + 0.5f);
                float px = Mathf.Min(pxLeft, pxRight);

                // 两端按比例收窄：px<cap 时允许的半高线性减少
                float allowed = px >= cap ? halfHeight : halfHeight * (px / cap);
                float alpha = Mathf.Clamp01(allowed - py);
                pixels[y * width + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return WriteSprite(HpBarPath, texture);
        }

        /// <summary>点是否落在 n 角星内部（外径 outer、内径 inner）</summary>
        private static bool InsideStar(float dx, float dy, float outer, float inner, int points)
        {
            float r = Mathf.Sqrt(dx * dx + dy * dy);
            if (r > outer) return false;

            float angle = Mathf.Atan2(dy, dx);
            float sector = Mathf.PI * 2f / points;
            float half = sector * 0.5f;
            float local = Mathf.Repeat(angle + half, sector) - half;   // -half..half
            // 在该扇区内，星形边界半径随角度从 outer 线性收到 inner（近似）
            float t = Mathf.Abs(local) / half;
            float boundary = Mathf.Lerp(outer, inner, t);
            return r <= boundary;
        }

        /// <summary>写 PNG 并按 Sprite 导入（强制 FullRect，避免低 alpha 图被 Tight 裁没）</summary>
        private static Sprite WriteSprite(string path, Texture2D texture)
        {
            File.WriteAllBytes(path, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spritePixelsPerUnit = 100f;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Bilinear;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.npotScale = TextureImporterNPOTScale.None;

                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);
                settings.spriteMeshType = SpriteMeshType.FullRect;
                importer.SetTextureSettings(settings);
                importer.SaveAndReimport();
            }

            return LoadSprite(path);
        }

        /// <summary>文字投影：亮场景下白字/灰字直接压在地上会糊</summary>
        private static void AddDropShadow(TMP_Text target)
        {
            if (target == null) return;
            var shadow = target.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.8f);
            shadow.effectDistance = new Vector2(1.5f, -1.5f);
        }

        private static void EnsureFolder(string path)
        {            if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) return;
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
                    ? FindDeep(panelRoot, attr.path)
                    : (FindDeep(panelRoot, field.Name)
                       ?? FindDeep(panelRoot, char.ToUpper(field.Name[0]) + field.Name.Substring(1)));

                if (target == null)
                {
                    Debug.LogWarning($"[DashijieHudView] 找不到 {field.DeclaringType.Name}.{field.Name} 的节点");
                    continue;
                }

                UnityEngine.Object component = field.FieldType == typeof(GameObject)
                    ? target.gameObject
                    : target.GetComponent(field.FieldType);
                if (component == null)
                {
                    Debug.LogWarning($"[DashijieHudView] {target.name} 上没有 {field.FieldType.Name}（字段 {field.Name}）");
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
}
#endif
