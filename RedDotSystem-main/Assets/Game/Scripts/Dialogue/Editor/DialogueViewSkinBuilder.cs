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
    /// 对话界面构建器（可重复执行）
    /// 菜单：Tools/UI/重建对话界面
    /// 产物：Assets/Game/UI/Dialogue/DialogueView.prefab
    ///
    /// 版式照鸣潮过场对话：不画对话框，只留底部暗渐变；
    /// 说话人居中金色大字 + 细金线，台词居中白色带投影，选项是居中的圆角胶囊列表，
    /// 右上角三个胶囊按钮（自动 / 跳过 / 隐藏）。
    /// 预制体根不带 Canvas（框架补），以 1920×1080 为基准。
    /// </summary>
    public static class DialogueViewSkinBuilder
    {
        private const string PrefabPath = "Assets/Game/UI/Dialogue/DialogueView.prefab";
        private const string TextureFolder = "Assets/Game/UI/Dialogue/Textures";
        private const string PanelSpritePath = "Assets/Game/Resources/ui/Theme/panel_round.png";
        private const string FontPath = "Assets/Codely/Fonts/NotoSansSC-Regular SDF.asset";

        private const int OptionCount = 3;

        private static readonly Color Gold = new(0.851f, 0.737f, 0.486f, 1f);
        private static readonly Color TextMain = new(0.96f, 0.96f, 0.96f, 1f);
        private static readonly Color TextDim = new(0.80f, 0.81f, 0.83f, 1f);

        [MenuItem("Tools/UI/重建对话界面")]
        public static void Build()
        {
            EnsureFolder(TextureFolder);

            TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
            Sprite pillSprite = AssetDatabase.LoadAssetAtPath<Sprite>(PanelSpritePath);
            Sprite bottomFade = EnsureBottomFadeSprite();
            Sprite optionBorder = EnsureRoundedBorderSprite();
            Sprite arrowSprite = EnsureArrowSprite();

            var root = new GameObject("DialogueView", typeof(RectTransform));
            RectTransform rootRect = root.GetComponent<RectTransform>();
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;

            DialogueView view = root.AddComponent<DialogueView>();
            CanvasGroup rootGroup = root.AddComponent<CanvasGroup>();
            rootGroup.alpha = 1f;
            UIControlData controlData = root.AddComponent<UIControlData>();

            // ── 面板根（整体做淡入/滑入的就是它） ──
            RectTransform panel = NewRect("DialogPanel", rootRect);
            Stretch(panel);
            CanvasGroup panelGroup = panel.gameObject.AddComponent<CanvasGroup>();

            // 底部暗渐变：给台词提供可读背景，但不出现"对话框"边界
            RectTransform gradient = NewRect("BottomFade", panel);
            gradient.anchorMin = Vector2.zero;
            gradient.anchorMax = new Vector2(1f, 0.55f);
            gradient.offsetMin = Vector2.zero;
            gradient.offsetMax = Vector2.zero;
            Image gradientImage = NewImage(gradient.gameObject, new Color(0f, 0f, 0f, 0.92f), bottomFade);
            if (gradientImage != null) gradientImage.raycastTarget = false;

            // 说话人（居中、金色、带投影）
            TMP_Text speaker = NewText("SpeakerName", panel, 36f, Gold, TextAlignmentOptions.Center, font);
            speaker.rectTransform.anchorMin = new Vector2(0.5f, 0f);
            speaker.rectTransform.anchorMax = new Vector2(0.5f, 0f);
            speaker.rectTransform.pivot = new Vector2(0.5f, 0f);
            speaker.rectTransform.anchoredPosition = new Vector2(0f, 300f);
            speaker.rectTransform.sizeDelta = new Vector2(900f, 50f);
            speaker.overflowMode = TextOverflowModes.Overflow;
            AddShadow(speaker, 2.5f);

            RectTransform underline = NewRect("SpeakerUnderline", panel);
            underline.anchorMin = new Vector2(0.5f, 0f);
            underline.anchorMax = new Vector2(0.5f, 0f);
            underline.pivot = new Vector2(0.5f, 0f);
            underline.anchoredPosition = new Vector2(0f, 286f);
            underline.sizeDelta = new Vector2(96f, 2f);
            Image underlineImage = NewImage(underline.gameObject, new Color(Gold.r, Gold.g, Gold.b, 0.85f), null);
            if (underlineImage != null) underlineImage.raycastTarget = false;

            // 台词（居中白色、两行以内、带投影）
            TMP_Text content = NewText("ContentText", panel, 30f, TextMain, TextAlignmentOptions.Center, font);
            content.rectTransform.anchorMin = new Vector2(0.5f, 0f);
            content.rectTransform.anchorMax = new Vector2(0.5f, 0f);
            content.rectTransform.pivot = new Vector2(0.5f, 0f);
            content.rectTransform.anchoredPosition = new Vector2(0f, 176f);
            content.rectTransform.sizeDelta = new Vector2(1200f, 104f);
            content.enableWordWrapping = true;
            content.lineSpacing = 12f;
            AddShadow(content, 2f);

            // 继续提示（小三角，居中）
            RectTransform arrow = NewRect("ContinueArrow", panel);
            arrow.anchorMin = new Vector2(0.5f, 0f);
            arrow.anchorMax = new Vector2(0.5f, 0f);
            arrow.pivot = new Vector2(0.5f, 0f);
            arrow.anchoredPosition = new Vector2(0f, 132f);
            arrow.sizeDelta = new Vector2(20f, 20f);
            Image arrowImage = NewImage(arrow.gameObject, new Color(Gold.r, Gold.g, Gold.b, 0.9f), arrowSprite);
            if (arrowImage != null)
            {
                arrowImage.preserveAspect = true;
                arrowImage.raycastTarget = false;
            }
            CanvasGroup arrowGroup = arrow.gameObject.AddComponent<CanvasGroup>();

            // ── 右上角功能按钮 ──
            RectTransform buttonBar = NewRect("ButtonBar", panel);
            buttonBar.anchorMin = new Vector2(1f, 1f);
            buttonBar.anchorMax = new Vector2(1f, 1f);
            buttonBar.pivot = new Vector2(1f, 1f);
            buttonBar.anchoredPosition = new Vector2(-40f, -32f);
            buttonBar.sizeDelta = new Vector2(320f, 44f);
            var barLayout = buttonBar.gameObject.AddComponent<HorizontalLayoutGroup>();
            barLayout.childAlignment = TextAnchor.MiddleRight;
            barLayout.spacing = 10f;
            barLayout.childControlWidth = false;
            barLayout.childControlHeight = false;
            barLayout.childForceExpandWidth = false;
            barLayout.childForceExpandHeight = false;

            BuildBarButton(buttonBar, "AutoButton", "自动", pillSprite, font);
            BuildBarButton(buttonBar, "SkipButton", "跳过", pillSprite, font);
            BuildBarButton(buttonBar, "HideButton", "隐藏", pillSprite, font);

            // ── 选项列表（右侧竖排胶囊，参考鸣潮过场里的选项位置） ──
            RectTransform optionRoot = NewRect("OptionRoot", rootRect);
            optionRoot.anchorMin = new Vector2(1f, 0.5f);
            optionRoot.anchorMax = new Vector2(1f, 0.5f);
            optionRoot.pivot = new Vector2(1f, 0.5f);
            optionRoot.anchoredPosition = new Vector2(-72f, -20f);
            optionRoot.sizeDelta = new Vector2(560f, 260f);
            CanvasGroup optionGroup = optionRoot.gameObject.AddComponent<CanvasGroup>();

            for (int i = 0; i < OptionCount; i++)
                BuildOptionButton(optionRoot, i, pillSprite, optionBorder, font);

            optionRoot.gameObject.SetActive(false);

            // ── 层级与保存 ──
            root.layer = Layer.UI;
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                child.gameObject.layer = Layer.UI;

            // 动效参数：轻微上滑即可（原版 46 太大，居中字幕会整体跳动）
            SetPrivateFloat(view, "panelSlide", 12f);
            SetPrivateFloat(view, "fadeInDuration", 0.22f);

            int bindings = BindFields(rootRect, view, controlData);

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            UnityEngine.Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (prefab == null)
            {
                Debug.LogError($"[DialogueView] 预制体保存失败：{PrefabPath}");
                return;
            }

            Debug.Log($"[DialogueView] 已生成 {PrefabPath}，绑定 {bindings} 项");
            Selection.activeObject = prefab;
        }

        // ──────────────────────────────
        // 节点
        // ──────────────────────────────

        private static void BuildBarButton(RectTransform bar, string name, string label, Sprite pillSprite, TMP_FontAsset font)
        {
            RectTransform button = NewRect(name, bar);
            button.sizeDelta = new Vector2(92f, 38f);
            var element = button.gameObject.AddComponent<LayoutElement>();
            element.preferredWidth = 92f;
            element.preferredHeight = 38f;
            element.minWidth = 92f;
            element.minHeight = 38f;

            Image background = NewImage(button.gameObject, new Color(0.06f, 0.07f, 0.08f, 0.72f), pillSprite);
            if (background != null)
            {
                background.type = Image.Type.Sliced;
                background.raycastTarget = true;
            }
            Button navButton = button.gameObject.AddComponent<Button>();
            navButton.targetGraphic = background;

            TMP_Text text = NewText("Label", button, 17f, TextDim, TextAlignmentOptions.Center, font);
            Stretch(text.rectTransform);
            text.overflowMode = TextOverflowModes.Overflow;
            text.SetText(label);
        }

        private static void BuildOptionButton(RectTransform parent, int index, Sprite pillSprite, Sprite borderSprite,
            TMP_FontAsset font)
        {
            RectTransform button = NewRect($"OptionButton_{index}", parent);
            button.anchorMin = new Vector2(1f, 0.5f);
            button.anchorMax = new Vector2(1f, 0.5f);
            button.pivot = new Vector2(1f, 0.5f);
            button.anchoredPosition = new Vector2(0f, 60f - index * 74f);
            button.sizeDelta = new Vector2(560f, 62f);

            Image background = NewImage(button.gameObject, new Color(0.055f, 0.062f, 0.072f, 0.86f), pillSprite);
            if (background != null)
            {
                background.type = Image.Type.Sliced;
                background.raycastTarget = true;
            }
            Button optionButton = button.gameObject.AddComponent<Button>();
            optionButton.targetGraphic = background;
            button.gameObject.AddComponent<CanvasGroup>();

            RectTransform border = NewRect("Border", button);
            Stretch(border);
            Image borderImage = NewImage(border.gameObject, new Color(Gold.r, Gold.g, Gold.b, 0.30f), borderSprite);
            if (borderImage != null)
            {
                borderImage.type = Image.Type.Sliced;
                borderImage.raycastTarget = false;
            }

            RectTransform accent = NewRect("Accent", button);
            accent.anchorMin = new Vector2(0f, 0.5f);
            accent.anchorMax = new Vector2(0f, 0.5f);
            accent.pivot = new Vector2(0f, 0.5f);
            accent.anchoredPosition = new Vector2(10f, 0f);
            accent.sizeDelta = new Vector2(3f, 28f);
            Image accentImage = NewImage(accent.gameObject, Gold, null);
            if (accentImage != null) accentImage.raycastTarget = false;

            TMP_Text label = NewText("Label", button, 24f, TextMain, TextAlignmentOptions.Left, font);
            label.rectTransform.anchorMin = Vector2.zero;
            label.rectTransform.anchorMax = Vector2.one;
            label.rectTransform.offsetMin = new Vector2(30f, 0f);
            label.rectTransform.offsetMax = new Vector2(-24f, 0f);
            label.overflowMode = TextOverflowModes.Overflow;
            AddShadow(label, 1.5f);
            label.SetText($"选项{index + 1}");

            DialogueOptionButtonFx fx = button.gameObject.AddComponent<DialogueOptionButtonFx>();
            var so = new SerializedObject(fx);
            so.FindProperty("border").objectReferenceValue = borderImage;
            so.FindProperty("accent").objectReferenceValue = accent;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void AddShadow(TMP_Text target, float distance)
        {
            if (target == null) return;
            Shadow shadow = target.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.75f);
            shadow.effectDistance = new Vector2(distance, -distance);
        }

        // ──────────────────────────────
        // 绑定
        // ──────────────────────────────

        private static int BindFields(Transform panelRoot, DialogueView view, UIControlData controlData)
        {
            SerializedObject so = new(controlData);
            SerializedProperty list = so.FindProperty("ctrlItemDatas");
            list.arraySize = 0;

            int count = 0;
            foreach (FieldInfo field in view.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                ControlBindingAttribute attr = field.GetCustomAttribute<ControlBindingAttribute>();
                if (attr == null) continue;

                if (field.FieldType == typeof(Button[]))
                {
                    var buttons = new Button[OptionCount];
                    for (int i = 0; i < OptionCount; i++)
                    {
                        Transform node = FindDeep(panelRoot, $"OptionButton_{i}");
                        if (node == null) { Debug.LogWarning($"[DialogueView] 找不到 OptionButton_{i}"); continue; }
                        buttons[i] = node.GetComponent<Button>();
                    }
                    field.SetValue(view, buttons);
                    count += WriteBinding(list, field.Name, buttons);
                    continue;
                }

                Transform target = !string.IsNullOrEmpty(attr.path) ? FindDeep(panelRoot, attr.path) : FindDeep(panelRoot, field.Name);
                if (target == null) target = FindDeep(panelRoot, char.ToUpper(field.Name[0]) + field.Name.Substring(1));
                // 根节点自身的组件（如整个面板的 CanvasGroup）不在子树里，单独兜底
                if (target == null && panelRoot.GetComponent(field.FieldType) != null) target = panelRoot;
                if (target == null)
                {
                    Debug.LogWarning($"[DialogueView] 找不到 {field.DeclaringType.Name}.{field.Name} 的节点");
                    continue;
                }

                UnityEngine.Object component = field.FieldType == typeof(GameObject) ? target.gameObject : target.GetComponent(field.FieldType);
                if (component == null)
                {
                    Debug.LogWarning($"[DialogueView] {target.name} 上没有 {field.FieldType.Name}（字段 {field.Name}）");
                    continue;
                }

                field.SetValue(view, component);
                count += WriteBinding(list, field.Name, component);
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            return count;
        }

        private static int WriteBinding(SerializedProperty list, string fieldName, params UnityEngine.Object[] targets)
        {
            list.arraySize += 1;
            SerializedProperty element = list.GetArrayElementAtIndex(list.arraySize - 1);
            element.FindPropertyRelative("name").stringValue = fieldName;
            element.FindPropertyRelative("type").stringValue = targets[0].GetType().Name;
            SerializedProperty targetArray = element.FindPropertyRelative("targets");
            targetArray.arraySize = targets.Length;
            for (int i = 0; i < targets.Length; i++)
                targetArray.GetArrayElementAtIndex(i).objectReferenceValue = targets[i];
            return targets.Length;
        }

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
        // 小工具
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
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static Image NewImage(GameObject target, Color color, Sprite sprite)
        {
            EnsureCanvasRenderer(target);
            Image image = target.AddComponent<Image>();
            image.color = color;
            image.sprite = sprite;
            image.raycastTarget = false;
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
            text.fontSize = size;
            text.color = color;
            text.alignment = alignment;
            text.raycastTarget = false;
            text.overflowMode = TextOverflowModes.Overflow;
            if (font != null) text.font = font;
            return text;
        }

        private static void EnsureCanvasRenderer(GameObject target)
        {
            if (target.GetComponent<CanvasRenderer>() == null) target.AddComponent<CanvasRenderer>();
        }

        private static void SetPrivateFloat(object target, string fieldName, float value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            field?.SetValue(target, value);
        }

        private static void EnsureFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path)?.Replace("\\", "/");
            string leaf = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        // ──────────────────────────────
        // 程序化美术
        // ──────────────────────────────

        /// <summary>底部暗渐变：上方全透明、下方深色（给台词做可读背景，没有硬边界）</summary>
        private static Sprite EnsureBottomFadeSprite()
        {
            string path = $"{TextureFolder}/dialogue_bottom_fade.png";
            Sprite existing = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (existing != null) return existing;

            const int width = 4;
            const int height = 256;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                float t = (float)y / (height - 1);                 // 0 = 底部，1 = 顶部
                float alpha = Mathf.Pow(1f - t, 1.3f) * 0.80f;     // 越靠下越深，最高 0.80
                byte a = (byte)(Mathf.Clamp01(alpha) * 255f);
                for (int x = 0; x < width; x++)
                    pixels[y * width + x] = new Color32(255, 255, 255, a);   // 用白色，靠 Image.color 染色
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            File.WriteAllBytes(path, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
            ImportSprite(path);
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        /// <summary>选项边框：圆角矩形描边（中间透明），供悬停时调 alpha</summary>
        private static Sprite EnsureRoundedBorderSprite()
        {
            string path = $"{TextureFolder}/dialogue_option_border.png";
            Sprite existing = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (existing != null) return existing;

            const int size = 64;
            const float radius = 12f;
            const float thickness = 1.6f;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float cx = Mathf.Min(x + 0.5f, size - (x + 0.5f));
                float cy = Mathf.Min(y + 0.5f, size - (y + 0.5f));
                float distance;
                if (cx >= radius || cy >= radius)
                    distance = Mathf.Min(cx, cy);
                else
                {
                    float dx = radius - cx;
                    float dy = radius - cy;
                    distance = radius - Mathf.Sqrt(dx * dx + dy * dy);
                }

                // 只保留最外一圈 thickness 像素
                float alpha = distance <= thickness ? Mathf.Clamp01(thickness - distance) : 0f;
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            File.WriteAllBytes(path, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
            ImportSprite(path, new Vector4(radius, radius, radius, radius));
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        /// <summary>继续提示：向下的实心小三角</summary>
        private static Sprite EnsureArrowSprite()
        {
            string path = $"{TextureFolder}/dialogue_continue_arrow.png";
            Sprite existing = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (existing != null) return existing;

            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            float half = size * 0.5f;

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float px = x + 0.5f - half;
                float py = y + 0.5f - half;
                // 倒三角：|px| 随 py 增大而收窄
                float t = (py + half) / size;                  // 0 底 → 1 顶
                float widthAtY = Mathf.Lerp(1f, 0f, t) * half;
                float alpha = Mathf.Abs(px) <= widthAtY ? 1f : 0f;
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            File.WriteAllBytes(path, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
            ImportSprite(path);
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        private static void ImportSprite(string path, Vector4 border = default)
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

            TextureImporterSettings settings = new();
            importer.ReadTextureSettings(settings);
            // 必须 FullRect：Tight 会按不透明像素裁网格，渐变图大片透明 → 几乎画不出来
            settings.spriteMeshType = SpriteMeshType.FullRect;
            if (border != Vector4.zero) settings.spriteBorder = border;
            importer.SetTextureSettings(settings);
            importer.SaveAndReimport();
        }
    }
}
