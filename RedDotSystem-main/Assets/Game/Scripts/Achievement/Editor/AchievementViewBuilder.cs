#if UNITY_EDITOR
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
    /// 成就面板预制体构建器（可重复执行）
    /// 菜单：Tools/UI/构建 AchievementView 预制体
    /// 产物：Assets/Game/UI/Achievement/AchievementView.prefab
    /// </summary>
    public static class AchievementViewBuilder
    {
        private const string PrefabPath = "Assets/Game/UI/Achievement/AchievementView.prefab";
        private const string PanelSpritePath = "Assets/Game/Resources/ui/Theme/panel_round.png";
        private const string ButtonSpritePath = "Assets/Game/Resources/ui/Theme/btn_round.png";

        private static readonly Color PanelColor = new(0.149f, 0.169f, 0.200f, 1f);
        private static readonly Color SubPanelColor = new(0.180f, 0.200f, 0.239f, 1f);
        private static readonly Color TextMain = new(0.878f, 0.878f, 0.878f, 1f);
        private static readonly Color TextDim = new(0.541f, 0.561f, 0.588f, 1f);
        private static readonly Color Accent = new(0.498f, 0.698f, 0.851f, 1f);
        private static readonly Color Gold = new(0.910f, 0.769f, 0.416f, 1f);
        private static readonly Color Danger = new(0.690f, 0.290f, 0.290f, 1f);

        [MenuItem("Tools/UI/构建 AchievementView 预制体")]
        public static void Build()
        {
            var wrapper = new GameObject("AchievementView", typeof(RectTransform));
            RectTransform wrapperRect = wrapper.GetComponent<RectTransform>();
            wrapperRect.anchorMin = Vector2.zero;
            wrapperRect.anchorMax = Vector2.one;
            wrapperRect.offsetMin = Vector2.zero;
            wrapperRect.offsetMax = Vector2.zero;

            var view = wrapper.AddComponent<AchievementView>();
            var controlData = wrapper.AddComponent<UIControlData>();

            // ── 主面板 ──
            RectTransform panel = NewRect("Panel", wrapperRect);
            Place(panel, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(880f, 620f));
            Image panelImage = NewImage(panel.gameObject, PanelColor, LoadSprite(PanelSpritePath));
            if (panelImage != null)
            {
                panelImage.type = Image.Type.Sliced;
                panelImage.raycastTarget = true;
            }

            // 顶部标题
            TMP_Text title = NewText("TitleText", panel, 24f, TextMain, TextAlignmentOptions.Left);
            Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(28f, -18f), new Vector2(560f, 44f));
            title.SetText("成就");

            // 关闭键
            Button closeButton = NewButton("CloseButton", panel, new Vector2(44f, 44f), Danger);
            Place((RectTransform)closeButton.transform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-18f, -14f), new Vector2(44f, 44f));
            TMP_Text closeLabel = NewText("Label", closeButton.transform, 26f, Color.white, TextAlignmentOptions.Center);
            Stretch(closeLabel.rectTransform);
            closeLabel.SetText("×");

            // 全部领取
            Button claimAllButton = NewButton("ClaimAllButton", panel, new Vector2(170f, 38f), new Color(0.302f, 0.392f, 0.490f, 1f));
            Place((RectTransform)claimAllButton.transform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-76f, -17f), new Vector2(170f, 38f));
            TMP_Text claimAllText = NewText("Label", claimAllButton.transform, 16f, Color.white, TextAlignmentOptions.Center);
            Stretch(claimAllText.rectTransform);
            claimAllText.SetText("全部领取");

            // ── 左列列表 ──
            ScrollRect scroll = NewScroll("AchievementScroll", panel, new Vector2(400f, 480f));
            Place((RectTransform)scroll.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, -78f), new Vector2(400f, 480f));

            RectTransform viewport = (RectTransform)scroll.transform.Find("Viewport");
            RectTransform content = (RectTransform)viewport.Find("Content");

            // 行模板
            RectTransform rowTemplate = NewRect("RowTemplate", content);
            Place(rowTemplate, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(360f, 64f));
            Image rowBg = NewImage(rowTemplate.gameObject, SubPanelColor, null);
            if (rowBg != null) rowBg.raycastTarget = true;
            Button rowButton = rowTemplate.gameObject.AddComponent<Button>();
            rowButton.targetGraphic = rowBg;

            Image badge = NewImage(NewRect("Badge", rowTemplate).gameObject, Gold, null);
            Place((RectTransform)badge.transform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(10f, 0f), new Vector2(40f, 40f));
            TMP_Text badgeText = NewText("BadgeText", badge.transform, 18f, Color.white, TextAlignmentOptions.Center);
            Stretch(badgeText.rectTransform);
            badgeText.SetText("?");

            TMP_Text name = NewText("NameText", rowTemplate, 16f, TextMain, TextAlignmentOptions.Left);
            Place(name.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(60f, -8f), new Vector2(-150f, 24f));

            TMP_Text progress = NewText("ProgressText", rowTemplate, 13f, TextDim, TextAlignmentOptions.Left);
            Place(progress.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(60f, 8f), new Vector2(-150f, 20f));

            TMP_Text points = NewText("PointsText", rowTemplate, 14f, Gold, TextAlignmentOptions.Right);
            Place(points.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-12f, -8f), new Vector2(70f, 24f));

            TMP_Text state = NewText("StateText", rowTemplate, 13f, TextDim, TextAlignmentOptions.Right);
            Place(state.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-12f, 8f), new Vector2(90f, 20f));

            RectTransform separator = NewRect("Separator", rowTemplate);
            Place(separator, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(10f, 0f), new Vector2(-20f, 1f));
            Image separatorImage = NewImage(separator.gameObject, new Color(1f, 1f, 1f, 0.08f), null);
            if (separatorImage != null) separatorImage.raycastTarget = false;

            rowTemplate.gameObject.SetActive(false);

            // 空态/加载
            TMP_Text empty = NewText("EmptyText", panel, 16f, TextDim, TextAlignmentOptions.Center);
            Place(empty.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, -78f), new Vector2(400f, 480f));
            empty.SetText("读取中…");

            // ── 右列详情 ──
            TMP_Text detailTitle = NewText("DetailTitle", panel, 22f, TextMain, TextAlignmentOptions.Left);
            Place(detailTitle.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(452f, -84f), new Vector2(400f, 34f));

            TMP_Text detailMeta = NewText("DetailMeta", panel, 14f, TextDim, TextAlignmentOptions.Left);
            Place(detailMeta.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(452f, -122f), new Vector2(400f, 22f));

            TMP_Text detailDesc = NewText("DetailDesc", panel, 15f, TextMain, TextAlignmentOptions.TopLeft);
            Place(detailDesc.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(452f, -152f), new Vector2(400f, 64f));
            detailDesc.enableWordWrapping = true;

            TMP_Text detailConditions = NewText("DetailConditions", panel, 15f, TextMain, TextAlignmentOptions.TopLeft);
            Place(detailConditions.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(452f, -224f), new Vector2(400f, 130f));
            detailConditions.enableWordWrapping = true;

            TMP_Text detailReward = NewText("DetailReward", panel, 15f, Gold, TextAlignmentOptions.TopLeft);
            Place(detailReward.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(452f, -366f), new Vector2(400f, 60f));
            detailReward.enableWordWrapping = true;

            Button claimButton = NewButton("ClaimButton", panel, new Vector2(200f, 46f), Accent);
            Place((RectTransform)claimButton.transform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(452f, 28f), new Vector2(200f, 46f));
            TMP_Text claimButtonText = NewText("Label", claimButton.transform, 17f, Color.white, TextAlignmentOptions.Center);
            Stretch(claimButtonText.rectTransform);
            claimButtonText.SetText("领取奖励");

            // 底部状态栏
            TMP_Text status = NewText("StatusText", panel, 14f, TextDim, TextAlignmentOptions.Left);
            Place(status.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(24f, 10f), new Vector2(640f, 24f));

            // ── 绑定 ──
            int bindings = BindFields(panel, view, controlData);

            wrapper.layer = Layer.UI;
            foreach (Transform child in wrapper.GetComponentsInChildren<Transform>(true))
            {
                child.gameObject.layer = Layer.UI;
            }

            EnsureFolder(Path.GetDirectoryName(PrefabPath)?.Replace("\\", "/"));
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(wrapper, PrefabPath);
            Object.DestroyImmediate(wrapper);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[AchievementView] 生成 {PrefabPath}，绑定 {bindings} 项");
            Selection.activeObject = prefab;
        }

        // ──────────────────────────────
        // 构建工具
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
            return text;
        }

        private static Button NewButton(string name, Transform parent, Vector2 size, Color color)
        {
            RectTransform rect = NewRect(name, parent);
            rect.sizeDelta = size;
            Image image = NewImage(rect.gameObject, color, LoadSprite(ButtonSpritePath));
            if (image != null)
            {
                image.type = Image.Type.Sliced;
                image.raycastTarget = true;
            }
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
            content.sizeDelta = new Vector2(0f, 0f);

            scroll.viewport = viewport;
            scroll.content = content;
            return scroll;
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
            {
                target.AddComponent<CanvasRenderer>();
            }
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
                    Debug.LogWarning($"[AchievementView] 找不到 {field.DeclaringType.Name}.{field.Name} 的节点");
                    continue;
                }

                Object component = field.FieldType == typeof(GameObject)
                    ? target.gameObject
                    : target.GetComponent(field.FieldType);
                if (component == null)
                {
                    Debug.LogWarning($"[AchievementView] {target.name} 上没有 {field.FieldType.Name}（字段 {field.Name}）");
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
