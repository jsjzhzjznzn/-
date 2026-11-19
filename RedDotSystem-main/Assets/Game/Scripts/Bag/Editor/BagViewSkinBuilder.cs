#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;

using TMPro;

using UnityEditor;

using UnityEngine;
using UnityEngine.UI;

namespace BagEditorTools
{
    /// <summary>
    /// 背包界面重建：左侧竖排页签 + 羊皮纸手绘风统一（菜单 Tools/UI/重建背包界面）。
    ///
    /// 幂等约定（与工程内其它构建器一致）：
    ///   · 贴图已是 Sprite 就跳过写入与导入（删掉对应 .png 再重建可强制重生成）；
    ///   · 节点/子节点按名字查找，找不到才新建；
    ///   · 贴图与颜色全部写进预制体，运行期不再生成（BagView 只留加载圆环）。
    /// 只改预制体与 BagView 的序列化字段，不动业务逻辑、不动格子交互。
    /// </summary>
    public static class BagViewSkinBuilder
    {
        private const string PrefabPath = "Assets/Game/UI/Bag/BagView.prefab";
        private const string ArtDir = "Assets/Game/UI/Bag/Art";

        /// <summary>四个功能按钮的底边统一落在面板底部这个高度（视觉同一基准线，且让开底部藤蔓边框）</summary>
        private const float BottomBar = 46f;

        private static readonly Color Paper = Hex(0xF5, 0xEE, 0xDC);       // 纸底
        private static readonly Color Edge = Hex(0xC2, 0xA8, 0x7A);        // 金褐描边
        private static readonly Color Gold = Hex(0xD9, 0xB2, 0x5C);        // 强调金
        private static readonly Color InkDark = Hex(0x4A, 0x3B, 0x21);     // 正文深褐
        private static readonly Color InkStrong = Hex(0x3A, 0x2C, 0x14);   // 关闭键 ×（暖褐圆牌上要更重）
        private static readonly Color InkMid = Hex(0x8C, 0x85, 0x78);      // 次级褐
        private static readonly Color InkBody = Hex(0x43, 0x40, 0x3A);     // 数量角标
        private static readonly Color TanFrame = Hex(0xB8, 0x9E, 0x73);    // 格子常态色（乘在白色底图上）

        [MenuItem("Tools/UI/重建背包界面")]
        public static void Build()
        {
            EnsureFolder(ArtDir);

            Sprite cellArt = EnsureArt("bag_cell", 128, 128, new Vector4(24f, 24f, 24f, 24f),
                Chip(128, 128, 10f, 2f, WithA(Color.white, 0.55f), Color.white));
            Sprite chipIdle = EnsureArt("bag_chip", 128, 64, new Vector4(22f, 22f, 22f, 22f),
                Chip(128, 64, 12f, 2f, WithA(Paper, 0.94f), WithA(Edge, 0.85f)));
            Sprite chipActive = EnsureArt("bag_chip_active", 128, 64, new Vector4(22f, 22f, 22f, 22f),
                Chip(128, 64, 12f, 3f, WithA(Hex(0xF0, 0xD9, 0x9E), 0.99f), Hex(0xB7, 0x93, 0x4C)));
            Sprite buttonArt = EnsureArt("bag_btn", 128, 64, new Vector4(22f, 22f, 22f, 22f),
                Chip(128, 64, 10f, 2f, WithA(Paper, 0.94f), WithA(Edge, 0.85f)));
            Sprite closeArt = EnsureArt("bag_close", 64, 64, Vector4.zero,
                Chip(64, 64, 30f, 4f, WithA(Hex(0xE4, 0xCE, 0x9C), 0.99f), Hex(0x7F, 0x62, 0x34)));
            Sprite plateArt = EnsureArt("bag_plate", 128, 128, new Vector4(24f, 24f, 24f, 24f),
                Chip(128, 128, 14f, 3f, WithA(Hex(0xF3, 0xE9, 0xD3), 0.96f), WithA(Hex(0xB2, 0x95, 0x69), 0.95f)));
            Sprite dividerArt = EnsureArt("bag_divider", 16, 640, Vector4.zero, Divider(16, 640));

            GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
            if (root == null)
            {
                Debug.LogError("[背包重建] 打不开预制体：" + PrefabPath);
                return;
            }

            try
            {
                Relayout(root, cellArt, chipIdle, chipActive, buttonArt, closeArt, plateArt, dividerArt);
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[背包重建] 完成：" + PrefabPath + "（7 列 / 左侧竖排页签 / 羊皮纸控件）");
        }

        // ──────────────────────────────
        // 预制体改造
        // ──────────────────────────────

        private static void Relayout(
            GameObject root, Sprite cellArt, Sprite chipIdle, Sprite chipActive,
            Sprite buttonArt, Sprite closeArt, Sprite plateArt, Sprite dividerArt)
        {
            BagView view = root.GetComponent<BagView>();
            if (view == null)
            {
                Debug.LogError("[背包重建] 根节点上没有 BagView");
                return;
            }

            Transform panel = root.transform.Find("BagPanel");
            if (panel == null)
            {
                Debug.LogError("[背包重建] 找不到 BagPanel");
                return;
            }

            // ── 删掉两块与手绘底图冲突的旧件 ──
            Kill(panel, "Background");     // 盖住整个面板（连角花装饰）的网格贴图
            Kill(panel, "WindowFrame");    // 2px 深蓝硬矩形
            RemoveBinding(root.GetComponent<SkierFramework.UIControlData>(), "backgroundImage");

            // ── 标题 + 标题金线 ──
            RectTransform title = Node(panel, "TitleText");
            Place(title, TopLeft, TopLeft, TopLeft, new Vector2(32f, -32f), new Vector2(420f, 56f));
            Text(title, 34f, InkDark);

            RectTransform rule = EnsureChild(panel, "TitleRule");
            rule.SetAsLastSibling();
            Place(rule, TopLeft, TopLeft, TopLeft, new Vector2(36f, -88f), new Vector2(200f, 3f));
            Image ruleImage = EnsureImage(rule.gameObject, null, WithA(Edge, 0.8f));
            ruleImage.raycastTarget = false;

            // ── 容量文案（贴着关闭徽记左侧，字号放大到读得清；与关闭键留出间距）──
            RectTransform capacity = Node(panel, "CapacityText");
            Place(capacity, TopRight, TopRight, TopRight, new Vector2(-132f, -34f), new Vector2(200f, 40f));
            Text(capacity, 24f, InkDark);

            // ── 关闭徽记（羊皮纸圆牌 + 深褐 ×，命中区 44 → 52）──
            RectTransform close = Node(panel, "CloseButton");
            Place(close, TopRight, TopRight, TopRight, new Vector2(-26f, -26f), new Vector2(52f, 52f));
            Image closeImage = EnsureImage(close.gameObject, closeArt, Color.white);
            closeImage.type = Image.Type.Simple;
            Button closeButton = close.GetComponent<Button>();
            if (closeButton != null) closeButton.targetGraphic = closeImage;
            RectTransform closeLabel = Node(close, "Label");
            if (closeLabel == null && close.childCount > 0) closeLabel = close.GetChild(0) as RectTransform;
            if (closeLabel == null) closeLabel = EnsureChild(close, "Label");
            PlaceStretch(closeLabel);
            Text(closeLabel, 30f, InkStrong);
            Align(closeLabel, TextAlignmentOptions.Center);
            TuneColors(closeButton, true);

            // ── 左侧竖排页签（贴标题下方的左侧页面边缘：竖排但不再悬在空白里）──
            LayoutTab(Node(panel, "tabWeaponButton"), "武器背包", new Vector2(216f, -108f), chipIdle, chipActive);
            LayoutTab(Node(panel, "tabItemButton"), "道具背包", new Vector2(216f, -174f), chipIdle, chipActive);

            // ── 槽位区：7 列（让开底图左下挎包与右下卷轴插画）──
            RectTransform scroll = Node(panel, "SlotScroll");
            Place(scroll, LeftMiddle, LeftMiddle, MiddleMiddle, new Vector2(690f, -5f), new Vector2(700f, 700f));

            RectTransform row = view.rowTemplate;
            if (row != null)
            {
                Place(row, TopMiddle, TopMiddle, MiddleMiddle, Vector2.zero, new Vector2(700f, 100f));
                while (row.childCount > 7) UnityEngine.Object.DestroyImmediate(row.GetChild(row.childCount - 1).gameObject);

                for (int i = 0; i < row.childCount; i++)
                {
                    RectTransform cellRect = row.GetChild(i) as RectTransform;
                    Place(cellRect, MiddleMiddle, MiddleMiddle, MiddleMiddle,
                        new Vector2((i - 3) * 100f, 0f), new Vector2(100f, 100f));

                    BagSlotView slot = cellRect != null ? cellRect.GetComponent<BagSlotView>() : null;
                    if (slot == null || slot.frameImage == null) continue;
                    slot.frameImage.sprite = cellArt;
                    slot.frameImage.type = Image.Type.Sliced;
                    slot.frameImage.color = TanFrame;   // 运行期 BagSlotView.Render 会按状态重染
                }
            }

            // ── 竖分隔饰（金褐细线 + 中央菱形，居中不再穿过标题行）──
            RectTransform separator = Node(panel, "Separator");
            if (separator != null)
            {
                Place(separator, LeftMiddle, LeftMiddle, MiddleMiddle, new Vector2(1055f, 0f), new Vector2(16f, 640f));
                Image separatorImage = EnsureImage(separator.gameObject, dividerArt, Color.white);
                separatorImage.type = Image.Type.Simple;
                separatorImage.raycastTarget = false;
            }

            // ── 右区：预览底板 + 详情文字 ──
            RectTransform preview = Node(panel, "PreviewSquare");
            if (preview != null)
            {
                Image previewImage = EnsureImage(preview.gameObject, plateArt, Color.white);
                previewImage.type = Image.Type.Sliced;
            }

            RectTransform detailName = Node(panel, "DetailNameText");
            Place(detailName, DetailAnchor, DetailAnchor, MiddleMiddle, new Vector2(0f, 92f), new Vector2(330f, 32f));
            Text(detailName, 24f, InkDark);

            RectTransform detailMeta = Node(panel, "DetailMetaText");
            Place(detailMeta, DetailAnchor, DetailAnchor, MiddleMiddle, new Vector2(0f, 60f), new Vector2(330f, 24f));
            Text(detailMeta, 16f, InkMid);

            RectTransform detailDesc = Node(panel, "DetailDescText");
            Place(detailDesc, DetailAnchor, DetailAnchor, MiddleMiddle, new Vector2(0f, -46f), new Vector2(330f, 160f));
            Text(detailDesc, 16f, InkBody);
            TMP_Text descTmp = detailDesc != null ? detailDesc.GetComponent<TMP_Text>() : null;
            if (descTmp != null)
            {
                descTmp.enableWordWrapping = true;    // 描述是多行文案，必须允许换行
                descTmp.lineSpacing = 6f;
                descTmp.alignment = TextAlignmentOptions.Top;
            }

            // ── 四个功能按钮：同一贴图、同一底边基准线 ──
            LayoutButton(Node(panel, "GrantButton"), buttonArt, BottomLeft, BottomLeft,
                new Vector2(26f, BottomBar), new Vector2(110f, 40f));
            LayoutButton(Node(panel, "TidyButton"), buttonArt, BottomLeft, BottomLeft,
                new Vector2(152f, BottomBar), new Vector2(110f, 40f));
            LayoutButton(Node(panel, "UseButton"), buttonArt, DetailBottomAnchor, BottomMiddle,
                new Vector2(-78f, BottomBar), new Vector2(150f, 44f));
            LayoutButton(Node(panel, "DiscardButton"), buttonArt, DetailBottomAnchor, BottomMiddle,
                new Vector2(78f, BottomBar), new Vector2(150f, 44f));

            RectTransform status = Node(panel, "StatusText");
            Place(status, BottomMiddle, BottomMiddle, BottomMiddle, new Vector2(0f, 12f), new Vector2(460f, 24f));
            Text(status, 16f, InkMid);

            // 加载圈落在格子区正中（原来在面板正中，横向偏了 30px）
            RectTransform spinner = Node(panel, "LoadingIndicator");
            Place(spinner, MiddleMiddle, MiddleMiddle, MiddleMiddle, new Vector2(-30f, -5f), new Vector2(40f, 40f));

            // ── BagView 上的现值（贴图引用 + 颜色）──
            view.slotFrameColor = TanFrame;
            view.slotSelectedColor = Gold;
            view.tabActiveSprite = chipActive;
            view.tabIdleSprite = chipIdle;
            view.tabActiveTextColor = InkDark;
            view.tabIdleTextColor = InkMid;
            view.spinnerColor = Hex(0xB0, 0x8D, 0x4F);
            view.columns = 7;
            view.cellSize = 100f;
        }

        private static readonly Vector2 TopLeft = new Vector2(0f, 1f);
        private static readonly Vector2 TopMiddle = new Vector2(0.5f, 1f);
        private static readonly Vector2 TopRight = new Vector2(1f, 1f);
        private static readonly Vector2 LeftMiddle = new Vector2(0f, 0.5f);
        private static readonly Vector2 MiddleMiddle = new Vector2(0.5f, 0.5f);
        private static readonly Vector2 BottomLeft = new Vector2(0f, 0f);
        private static readonly Vector2 BottomMiddle = new Vector2(0.5f, 0f);

        /// <summary>右区详情列的锚点：与预览底板/详情文字原来的 0.86 对齐，位置基本不用动</summary>
        private static readonly Vector2 DetailAnchor = new Vector2(0.86f, 0.5f);

        /// <summary>右区详情列 + 面板底：横向跟详情列，纵向贴底（与左下两个按钮同一基准线）</summary>
        private static readonly Vector2 DetailBottomAnchor = new Vector2(0.86f, 0f);

        /// <summary>页签节点：位置/尺寸/贴图/标签字色/左侧金色指示条一次摆好</summary>
        private static void LayoutTab(RectTransform tab, string label, Vector2 position, Sprite idle, Sprite active)
        {
            if (tab == null)
            {
                Debug.LogWarning("[背包重建] 找不到页签节点：" + label);
                return;
            }

            Place(tab, TopLeft, TopLeft, TopLeft, position, new Vector2(120f, 58f));

            Image image = EnsureImage(tab.gameObject, idle, Color.white);
            image.type = Image.Type.Sliced;
            image.raycastTarget = true;

            Button button = tab.GetComponent<Button>();
            if (button != null)
            {
                button.targetGraphic = image;
                TuneColors(button, false);
            }

            RectTransform labelRect = Node(tab, "Label");
            if (labelRect == null && tab.childCount > 0) labelRect = tab.GetChild(0) as RectTransform;
            if (labelRect == null) labelRect = EnsureChild(tab, "Label");
            PlaceStretch(labelRect);
            Text(labelRect, 20f, InkMid);
            Align(labelRect, TextAlignmentOptions.Center);
            TMP_Text tmp = labelRect.GetComponent<TMP_Text>();
            if (tmp != null) tmp.text = label;

            // 左侧金色指示条：只有当前页签显示，运行期由 BagView.SetTabLook 切
            RectTransform indicator = EnsureChild(tab, "Indicator");
            Place(indicator, LeftMiddle, LeftMiddle, LeftMiddle, new Vector2(6f, 0f), new Vector2(5f, 34f));
            Image indicatorImage = EnsureImage(indicator.gameObject, null, Gold);
            indicatorImage.raycastTarget = false;
            indicator.gameObject.SetActive(false);

            // 两态的区别写在贴图里（BagView 运行期换 sprite），这里先留未选中态
            Image tabImage = button != null ? button.targetGraphic as Image : null;
            if (tabImage != null) tabImage.sprite = idle;
            if (tmp != null) tmp.color = InkMid;
            if (active == null) Debug.LogWarning("[背包重建] 选中页签贴图缺失");
        }

        /// <summary>功能按钮：贴图 + 尺寸 + 标签字色</summary>
        private static void LayoutButton(RectTransform buttonRect, Sprite sprite, Vector2 anchor, Vector2 pivot, Vector2 position, Vector2 size)
        {
            if (buttonRect == null) return;

            Place(buttonRect, anchor, anchor, pivot, position, size);

            Image image = EnsureImage(buttonRect.gameObject, sprite, Color.white);
            image.type = Image.Type.Sliced;

            Button button = buttonRect.GetComponent<Button>();
            if (button != null)
            {
                button.targetGraphic = image;
                TuneColors(button, true);
            }

            RectTransform labelRect = Node(buttonRect, "Label");
            if (labelRect == null && buttonRect.childCount > 0) labelRect = buttonRect.GetChild(0) as RectTransform;
            if (labelRect != null)
            {
                PlaceStretch(labelRect);
                Text(labelRect, 18f, InkDark);
                Align(labelRect, TextAlignmentOptions.Center);
            }
        }

        /// <summary>交互态统一成暖色系（正常白 / 悬停微暗 / 按下更暗 / 禁用半透明灰）</summary>
        private static void TuneColors(Button button, bool dimWhenDisabled)
        {
            if (button == null) return;

            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.96f, 0.93f, 0.87f, 1f);
            colors.pressedColor = new Color(0.88f, 0.84f, 0.76f, 1f);
            colors.selectedColor = Color.white;
            colors.disabledColor = dimWhenDisabled
                ? new Color(0.80f, 0.77f, 0.71f, 0.55f)    // 暖调压暗，不用冷灰（浅色纸上冷灰像"网页禁用件"）
                : new Color(0.80f, 0.77f, 0.71f, 0.60f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            button.colors = colors;
            button.transition = Selectable.Transition.ColorTint;
        }

        // ──────────────────────────────
        // 预制体小工具
        // ──────────────────────────────

        /// <summary>按名字取子节点（不含递归查找；面板内节点名唯一）</summary>
        private static RectTransform Node(Transform parent, string name)
        {
            if (parent == null) return null;
            return parent.Find(name) as RectTransform;
        }

        private static void Kill(Transform parent, string name)
        {
            Transform child = parent != null ? parent.Find(name) : null;
            if (child != null) UnityEngine.Object.DestroyImmediate(child.gameObject);
        }

        /// <summary>取或建子节点（新建的挂 Layer.UI，框架画布只渲染该层）</summary>
        private static RectTransform EnsureChild(Transform parent, string name)
        {
            RectTransform existing = Node(parent, name);
            if (existing != null) return existing;

            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            go.layer = SkierFramework.Layer.UI;
            return rt;
        }

        private static Image EnsureImage(GameObject go, Sprite sprite, Color color)
        {
            Image image = go.GetComponent<Image>();
            if (image == null)
            {
                image = go.AddComponent<Image>();
                // Tuanjie 的 uGUI 分叉里 Graphic 基类不声明 RequireComponent(CanvasRenderer)，程序化 AddComponent 要自己补
                if (go.GetComponent<CanvasRenderer>() == null) go.AddComponent<CanvasRenderer>();
            }
            image.sprite = sprite;
            image.color = color;
            return image;
        }

        private static void Place(RectTransform rt, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 position, Vector2 size)
        {
            if (rt == null) return;
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.anchoredPosition = position;
            rt.sizeDelta = size;
            rt.localScale = Vector3.one;
            rt.localRotation = Quaternion.identity;
        }

        private static void PlaceStretch(RectTransform rt)
        {
            Place(rt, Vector2.zero, Vector2.one, MiddleMiddle, Vector2.zero, Vector2.zero);
        }

        private static void Text(RectTransform node, float size, Color color)
        {
            TMP_Text tmp = node != null ? node.GetComponent<TMP_Text>() : null;
            if (tmp == null) return;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.overflowMode = TextOverflowModes.Overflow;   // 固定单行文案用 Overflow，避免行框略矮时整段被裁掉
        }

        private static void Align(RectTransform node, TextAlignmentOptions alignment)
        {
            TMP_Text tmp = node != null ? node.GetComponent<TMP_Text>() : null;
            if (tmp != null) tmp.alignment = alignment;
        }

        /// <summary>删掉 UIControlData 里某个字段的绑定（整表重写，避免 DeleteArrayElementAtIndex 留下空元素）</summary>
        private static void RemoveBinding(SkierFramework.UIControlData data, string fieldName)
        {
            if (data == null) return;

            var so = new SerializedObject(data);
            SerializedProperty list = so.FindProperty("ctrlItemDatas");
            if (list == null) return;

            var keepNames = new List<string>();
            var keepTypes = new List<string>();
            var keepTargets = new List<UnityEngine.Object>();
            int removed = 0;

            for (int i = 0; i < list.arraySize; i++)
            {
                SerializedProperty element = list.GetArrayElementAtIndex(i);
                string name = element.FindPropertyRelative("name").stringValue;
                if (name == fieldName) { removed++; continue; }

                SerializedProperty targets = element.FindPropertyRelative("targets");
                keepNames.Add(name);
                keepTypes.Add(element.FindPropertyRelative("type").stringValue);
                keepTargets.Add(targets.arraySize > 0 ? targets.GetArrayElementAtIndex(0).objectReferenceValue : null);
            }

            list.arraySize = keepNames.Count;
            for (int i = 0; i < keepNames.Count; i++)
            {
                SerializedProperty element = list.GetArrayElementAtIndex(i);
                element.FindPropertyRelative("name").stringValue = keepNames[i];
                element.FindPropertyRelative("type").stringValue = keepTypes[i];
                SerializedProperty targets = element.FindPropertyRelative("targets");
                targets.arraySize = 1;
                targets.GetArrayElementAtIndex(0).objectReferenceValue = keepTargets[i];
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            Debug.Log("[背包重建] 绑定项 " + fieldName + "：" + (removed > 0 ? "已移除" : "本来就没有") + "（余 " + keepNames.Count + " 项）");
        }

        // ──────────────────────────────
        // 贴图生成（幂等）
        // ──────────────────────────────

        private static Sprite EnsureArt(string fileName, int width, int height, Vector4 border, Func<int, int, Color> pixel)
        {
            string path = ArtDir + "/" + fileName + ".png";

            Sprite existing = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (existing != null) return existing;   // 幂等：已是 Sprite 就不重写（要强制重生成就删掉 .png）

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++) texture.SetPixel(x, y, pixel(x, y));
            }
            texture.Apply();

            byte[] png = texture.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(texture);
            File.WriteAllBytes(path, png);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                Debug.LogError("[背包重建] 导入失败：" + path);
                return null;
            }

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.alphaIsTransparency = true;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.spritePixelsPerUnit = 100f;

            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            settings.spriteMeshType = SpriteMeshType.FullRect;   // 低 alpha 图用 Tight 会按不透明像素裁网格，渐变会画不出来
            settings.spriteBorder = border;
            settings.spriteAlignment = (int)SpriteAlignment.Center;
            settings.spriteExtrude = 1;
            importer.SetTextureSettings(settings);
            importer.SaveAndReimport();

            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        /// <summary>圆角纸片画笔：中心 fill、边缘 borderPx 宽 edge，圆角外透明（1px 抗锯齿）</summary>
        private static Func<int, int, Color> Chip(int width, int height, float radius, float borderPx, Color fill, Color edge)
        {
            return (x, y) =>
            {
                float distance = RoundedDistance(x + 0.5f - width * 0.5f, y + 0.5f - height * 0.5f,
                    width * 0.5f, height * 0.5f, radius);
                float coverage = Mathf.Clamp01(0.5f - distance);
                if (coverage <= 0f) return new Color(0f, 0f, 0f, 0f);

                Color color = -distance <= borderPx ? edge : fill;
                color.a *= coverage;
                return color;
            };
        }

        /// <summary>圆角矩形的有符号距离（负数在内部，绝对值即离边缘的深度）</summary>
        private static float RoundedDistance(float px, float py, float halfWidth, float halfHeight, float radius)
        {
            float qx = Mathf.Abs(px) - (halfWidth - radius);
            float qy = Mathf.Abs(py) - (halfHeight - radius);
            float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude;
            // 末尾的 -radius 不能少：漏掉它会把形状整体内缩一个圆角半径、并且四角不圆（曾经因此把 64px 的圆形徽记只画出中间 4×4 的点）
            return outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
        }

        /// <summary>竖分隔饰画笔：居中细线 + 中央菱形金饰，两端渐隐</summary>
        private static Func<int, int, Color> Divider(int width, int height)
        {
            float centerX = width * 0.5f - 0.5f;
            float centerY = height * 0.5f - 0.5f;

            return (x, y) =>
            {
                float dx = Mathf.Abs(x - centerX);
                float dy = Mathf.Abs(y - centerY);
                if (dx + dy <= 10f) return WithA(Gold, 0.95f);              // 中央菱形

                if (dx <= 1f)
                {
                    float fade = Mathf.Clamp01(Mathf.Min(y, height - 1 - y) / 56f);
                    return WithA(Edge, 0.65f * fade);                       // 细线两端渐隐
                }
                return new Color(0f, 0f, 0f, 0f);
            };
        }

        private static Color Hex(int r, int g, int b) => new Color(r / 255f, g / 255f, b / 255f, 1f);

        private static Color WithA(Color color, float alpha) => new Color(color.r, color.g, color.b, alpha);

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace("\\", "/");
            string leaf = Path.GetFileName(path);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
#endif