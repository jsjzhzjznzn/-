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
    /// 大世界地图预制体构建器（可重复执行）。
    /// 菜单：Tools/UI/重建大世界地图
    /// 产物：Assets/Game/UI/Dashijie/DashijieMapView.prefab
    ///
    /// 结构：全屏 Backdrop（点击关闭）→ MapPanel → MapClip(RectMask2D) → MapContent(RawImage 底图) → PlayerMark。
    /// 缩放改 MapContent.sizeDelta、平移改 anchoredPosition，标记随内容一起缩放，见 DashijieMapView。
    /// </summary>
    public static class DashijieMapViewBuilder
    {
        private const string PrefabPath = "Assets/Game/UI/Dashijie/DashijieMapView.prefab";
        private const string PanelSpritePath = "Assets/Game/Resources/ui/Theme/panel_round.png";
        private const string CircleSpritePath = "Assets/Game/UI/Dashijie/Icons/dashijie_circle.png";
        private const string ArrowSpritePath = "Assets/Game/UI/Dashijie/Icons/minimap_arrow.png";
        private const string QuestMarkSpritePath = "Assets/Game/UI/Dashijie/Icons/dashijie_quest_mark.png";
        private const string MapTexturePath = "Assets/Game/Resources/Minimap/dashijie_minimap.png";
        private const string FontPath = "Assets/Codely/Fonts/NotoSansSC-Regular SDF.asset";

        private static readonly Color PlateColor = new(0.075f, 0.086f, 0.106f, 0.96f);
        private static readonly Color TextMain = new(0.925f, 0.906f, 0.855f, 1f);
        private static readonly Color TextDim = new(0.639f, 0.647f, 0.663f, 1f);
        private static readonly Color Gold = new(0.910f, 0.769f, 0.416f, 1f);

        // 面板铺满屏幕只留 10px 边距；地图窗口铺满面板（内容按窗口自适应，运行时重算）
        private const float ScreenMargin = 10f;
        private const float DefaultContentSize = 1840f;

        [MenuItem("Tools/UI/重建大世界地图")]
        public static void Build()
        {
            TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
            Sprite panelSprite = AssetDatabase.LoadAssetAtPath<Sprite>(PanelSpritePath);
            Sprite circleSprite = AssetDatabase.LoadAssetAtPath<Sprite>(CircleSpritePath);
            Sprite arrowSprite = AssetDatabase.LoadAssetAtPath<Sprite>(ArrowSpritePath);
            var mapTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(MapTexturePath);

            var wrapper = new GameObject("DashijieMapView", typeof(RectTransform));
            RectTransform wrapperRect = wrapper.GetComponent<RectTransform>();
            Stretch(wrapperRect);

            var view = wrapper.AddComponent<DashijieMapView>();
            var controlData = wrapper.AddComponent<UIControlData>();

            // ── 全屏底板（点击关闭） ──
            RectTransform backdrop = NewRect("Backdrop", wrapperRect);
            Stretch(backdrop);
            Image backdropImage = NewImage(backdrop.gameObject, new Color(0.02f, 0.024f, 0.03f, 0.88f), null);
            if (backdropImage != null) backdropImage.raycastTarget = true;
            Button backdropButton = backdrop.gameObject.AddComponent<Button>();
            backdropButton.targetGraphic = backdropImage;

            // ── 面板：铺满屏幕，只留 10px 边距（几乎全屏） ──
            RectTransform panel = NewRect("MapPanel", wrapperRect);
            panel.anchorMin = Vector2.zero;
            panel.anchorMax = Vector2.one;
            panel.offsetMin = new Vector2(ScreenMargin, ScreenMargin);
            panel.offsetMax = new Vector2(-ScreenMargin, -ScreenMargin);
            Image panelImage = NewImage(panel.gameObject, PlateColor, panelSprite);
            if (panelImage != null) panelImage.type = Image.Type.Sliced;

            // ── 地图窗口：铺满面板内部（RectMask2D 裁切，内容随缩放/拖拽移动） ──
            RectTransform clip = NewRect("MapClip", panel);
            Stretch(clip);
            clip.gameObject.AddComponent<RectMask2D>();

            // ── 地图内容（RawImage = 底图，本地单位 = 底图像素） ──
            RectTransform content = NewRect("MapContent", clip);
            content.anchorMin = new Vector2(0.5f, 0.5f);
            content.anchorMax = new Vector2(0.5f, 0.5f);
            content.pivot = new Vector2(0.5f, 0.5f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(DefaultContentSize, DefaultContentSize);
            EnsureCanvasRenderer(content.gameObject);
            var mapImage = content.gameObject.AddComponent<RawImage>();
            mapImage.texture = mapTexture;
            mapImage.raycastTarget = false;
            mapImage.uvRect = new Rect(0f, 0f, 1f, 1f);

            // ── 顶部标题（压在图上，垫半透明底板保证可读） ──
            RectTransform titlePlate = NewRect("TitlePlate", panel);
            Place(titlePlate, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -16f), new Vector2(330f, 56f));
            Image titlePlateImage = NewImage(titlePlate.gameObject, new Color(0.02f, 0.024f, 0.03f, 0.55f), panelSprite);
            if (titlePlateImage != null)
            {
                titlePlateImage.type = Image.Type.Sliced;
                titlePlateImage.raycastTarget = false;
            }

            TMP_Text title = NewText("Title", panel, 28f, TextMain, TextAlignmentOptions.Left, font);
            Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(40f, -28f), new Vector2(420f, 40f));
            title.SetText("大世界地图");

            // ── 玩家标记（子节点，随地图一起缩放） ──
            RectTransform mark = NewRect("PlayerMark", content);
            mark.anchorMin = new Vector2(0.5f, 0.5f);
            mark.anchorMax = new Vector2(0.5f, 0.5f);
            mark.pivot = new Vector2(0.5f, 0.5f);
            mark.anchoredPosition = Vector2.zero;
            mark.sizeDelta = new Vector2(34f, 34f);
            Image markImage = NewImage(mark.gameObject, Gold, arrowSprite);
            if (markImage != null)
            {
                markImage.preserveAspect = true;
                markImage.raycastTarget = false;
            }

            // ── 任务目标标记（同样挂在内容矩形下，随缩放/平移一起动） ──
            RectTransform questMark = NewRect("QuestMarker", content);
            questMark.anchorMin = new Vector2(0.5f, 0.5f);
            questMark.anchorMax = new Vector2(0.5f, 0.5f);
            questMark.pivot = new Vector2(0.5f, 0.5f);
            questMark.anchoredPosition = Vector2.zero;
            questMark.sizeDelta = new Vector2(30f, 30f);
            Sprite questMarkSprite = AssetDatabase.LoadAssetAtPath<Sprite>(QuestMarkSpritePath);
            if (questMarkSprite == null) questMarkSprite = circleSprite;
            Image questMarkImage = NewImage(questMark.gameObject, Gold, questMarkSprite);
            if (questMarkImage != null)
            {
                questMarkImage.preserveAspect = true;
                questMarkImage.raycastTarget = false;
            }
            questMark.gameObject.SetActive(false);

            // 底部提示（同样压在图上，垫半透明底板）
            RectTransform hintPlate = NewRect("HintPlate", panel);
            Place(hintPlate, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(-330f, 12f), new Vector2(660f, 40f));
            Image hintPlateImage = NewImage(hintPlate.gameObject, new Color(0.02f, 0.024f, 0.03f, 0.55f), panelSprite);
            if (hintPlateImage != null)
            {
                hintPlateImage.type = Image.Type.Sliced;
                hintPlateImage.raycastTarget = false;
            }

            TMP_Text hint = NewText("HintText", panel, 16f, TextDim, TextAlignmentOptions.Center, font);
            Place(hint.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(-360f, 22f), new Vector2(720f, 24f));
            hint.SetText("M / Esc 关闭 · 滚轮缩放 · 拖拽平移");

            // ── 关闭钮 ──
            RectTransform close = NewRect("CloseButton", panel);
            Place(close, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-34f, -28f), new Vector2(52f, 52f));
            Image closeBackground = NewImage(close.gameObject, Color.white, circleSprite);
            if (closeBackground != null) closeBackground.raycastTarget = true;
            Button closeButton = close.gameObject.AddComponent<Button>();
            closeButton.targetGraphic = closeBackground;
            TMP_Text closeLabel = NewText("Label", close, 24f, TextMain, TextAlignmentOptions.Center, font);
            Stretch(closeLabel.rectTransform);
            closeLabel.SetText("×");

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

            Debug.Log($"[DashijieMapView] 生成 {PrefabPath}，绑定 {bindings} 项");
            Selection.activeObject = prefab;
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
            // 单行固定文案用 Overflow：Ellipsis 在行高略大于行框时会把字符整段裁掉
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

        private static Transform FindDeep(Transform root, string name)
        {
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child != root && child.name == name) return child;
            }
            return null;
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
                    ? FindDeep(panelRoot, attr.path)
                    : (FindDeep(panelRoot, field.Name)
                       ?? FindDeep(panelRoot, char.ToUpper(field.Name[0]) + field.Name.Substring(1)));

                if (target == null)
                {
                    Debug.LogWarning($"[DashijieMapView] 找不到 {field.DeclaringType.Name}.{field.Name} 的节点");
                    continue;
                }

                UnityEngine.Object component = field.FieldType == typeof(GameObject)
                    ? target.gameObject
                    : target.GetComponent(field.FieldType);
                if (component == null)
                {
                    Debug.LogWarning($"[DashijieMapView] {target.name} 上没有 {field.FieldType.Name}（字段 {field.Name}）");
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
