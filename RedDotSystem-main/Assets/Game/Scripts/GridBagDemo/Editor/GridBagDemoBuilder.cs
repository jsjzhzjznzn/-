using System.Collections.Generic;
using System.IO;

using TMPro;

using UnityEditor;
using UnityEditor.SceneManagement;

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace GridBagDemo.Editor
{
    /// <summary>
    /// 网格背包 Demo 的构建器（菜单 Tools/Demo/重建网格背包 Demo）。
    ///
    /// 负责：①幂等生成物品定义资产；②按「物品 × 4 朝向」烘焙形状衬底 Sprite（已存在则跳过）；
    /// ③生成预制体 GridBagDemo.prefab；④在 beibao 场景放下实例并登记 Build Settings。
    /// 所有节点层级都在这里生成并序列化到预制体，运行时不做 Find，也不做程序化美术生成。
    /// 物品图标直接复用 Assets/Game/Art/UI 下的现有图，不新增美术。
    /// </summary>
    public static class GridBagDemoBuilder
    {
        private const string DefFolder = "Assets/Game/Scripts/GridBagDemo/Defs";

        private const string UIFolder = "Assets/Game/UI/GridBagDemo";

        private const string ShapeFolder = UIFolder + "/Shapes";

        private const string PrefabPath = UIFolder + "/GridBagDemo.prefab";

        private const string ScenePath = "Assets/Game/Scenes/biebao.scene";

        private const string SourceCellSprite = "Assets/Game/UI/Bag/Art/bag_cell.png";

        private const string BackgroundPath = "Assets/Game/Art/UI/Theme/home_bg.png";

        private const int BagWidth = 10;

        private const int BagHeight = 6;

        private const int LootColumns = 6;

        /// <summary>清单物品数量（Specs 末尾的 LootCount 个留给货物清单，其余为背包初始物品）</summary>
        private const int LootCount = 4;

        /// <summary>物品图标尺寸（80×80 方形图，清单格与拖影按其原尺寸换算）</summary>
        private const float LootIconSize = 48f;

        /// <summary>形状衬底单格像素：一张衬图的边长 = 格数 × 该值（含 1px 外描边）</summary>
        private const int ShapeCellPixels = 32;

        private static readonly Color PanelColor = new(0.09f, 0.10f, 0.12f, 0.94f);

        private static readonly Color PanelHeaderColor = new(0.16f, 0.18f, 0.22f, 1f);

        private static readonly Color TextColor = new(0.90f, 0.91f, 0.93f, 1f);

        private static readonly Color SubTextColor = new(0.62f, 0.65f, 0.70f, 1f);

        private static readonly Color LootSlotColor = new(0.15f, 0.16f, 0.19f, 1f);

        private struct ItemSpec
        {
            public string Id;
            public string Name;
            public string Icon;
            public int Weight;
            public int Width;
            public int Height;
            public int[] Mask;
        }

        private static readonly ItemSpec[] Specs =
        {
            R("T_Luckdraw21010013_UI", "灰烬步枪", 4500, 1, 4),
            R("T_Luckdraw21010064_UI", "冷战步枪", 3900, 1, 4),
            R("T_Luckdraw21020013_UI", "制式手枪", 1100, 1, 2),
            R("T_Luckdraw21020064_UI", "穿甲手枪", 980, 1, 2),
            R("T_Luckdraw21030013_UI", "精工手枪", 1050, 1, 2),
            R("T_Luckdraw21040013_UI", "滑膛霰弹枪", 3600, 2, 3),
            R("T_Luckdraw21050013_UI", "信号弩", 2900, 2, 2),
            R("T_IconWup80_Mo_017_UI", "标准弹匣", 320, 2, 1),
            R("T_IconWup80_Mo_016_UI", "穿甲弹弹药", 150, 1, 1),
            R("T_IconWup80_Mo_017_UI", "备用弹匣", 310, 1, 2),
            R("T_IconWup80_Setpup_001_1_UI", "机械齿轮", 260, 2, 2),
            R("T_IconWup80_Setpup_004_1_UI", "掩体用防弹盾", 6200, 2, 3),
            R("T_IconA80_02_UI", "战术罗盘", 210, 1, 1),
            R("T_IconA80_03_UI", "信号无人机", 1800, 2, 2),
            L("T_IconWup80_Setpup_004_2_UI", "破损弹匣", 140, 2, 2),
            L("T_IconWup80_Mo_015_UI", "消音器", 420, 1, 3),
            L("T_IconWup80_Setpup_004_3_UI", "记忆芯片", 60, 1, 1),
            L("T_IconWup80_Setpup_004_4_UI", "金色饰品", 380, 1, 1),
        };

        private static ItemSpec R(string id, string name, int weight, int w, int h)
            => new() { Id = id, Name = name, Icon = id, Weight = weight, Width = w, Height = h, Mask = null };

        /// <summary>L 形物品：左上竖条 + 底部横条（用于演示非矩形占位与空透镜被填）</summary>
        private static ItemSpec L(string id, string name, int weight, int w, int h)
        {
            var mask = new int[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    mask[y * w + x] = (x == 0 || y == h - 1) ? 1 : 0;
            return new ItemSpec { Id = id, Name = name, Icon = id, Weight = weight, Width = w, Height = h, Mask = mask };
        }

        [MenuItem("Tools/Demo/重建网格背包 Demo")]
        public static void Rebuild()
        {
            EnsureFolders();
            List<GridItemDef> defs = EnsureDefs();
            List<Sprite> shapes = EnsureShapes(defs);

            GameObject prefab = BuildPrefab(defs, shapes);
            PlaceInScene(prefab);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            GameLog.Info($"[GridBagDemo] 重建完成：物品 {defs.Count} 件，形状衬底 {shapes.Count} 张，预制体 {PrefabPath}");
        }

        // ──────────────────────────────
        // 资产
        // ──────────────────────────────

        private static void EnsureFolders()
        {
            foreach (string folder in new[] { DefFolder, UIFolder, ShapeFolder })
            {
                if (AssetDatabase.IsValidFolder(folder)) continue;
                string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
                AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
            }
        }

        private static List<GridItemDef> EnsureDefs()
        {
            var defs = new List<GridItemDef>();
            for (int index = 0; index < Specs.Length; index++)
            {
                ItemSpec spec = Specs[index];
                // 同一张图标会被多件物品复用（标准弹匣/备用弹匣），资产文件名带序号保证唯一
                string uniqueId = $"{index:00}_{spec.Id}";
                string path = $"{DefFolder}/{uniqueId}.asset";
                var def = AssetDatabase.LoadAssetAtPath<GridItemDef>(path);
                if (def == null)
                {
                    def = ScriptableObject.CreateInstance<GridItemDef>();
                    AssetDatabase.CreateAsset(def, path);
                }

                def.SetInfo(uniqueId, spec.Name, spec.Icon, spec.Weight);
                if (spec.Mask != null) def.SetShape(spec.Width, spec.Height, spec.Mask);
                else def.SetRectShape(spec.Width, spec.Height);
                EditorUtility.SetDirty(def);
                defs.Add(def);
            }
            return defs;
        }

        /// <summary>
        /// 形状衬底：每个物品 4 张（朝向 0~3），白色纯色 + 1px 深色描边区域外透明。
        /// 已存在就直接复用（幂等：不重写、不重新导入，避免无谓的资源 ID 消耗）。
        /// </summary>
        private static List<Sprite> EnsureShapes(List<GridItemDef> defs)
        {
            var sprites = new List<Sprite>();
            foreach (GridItemDef def in defs)
            {
                for (int rotation = 0; rotation < 4; rotation++)
                {
                    string path = $"{ShapeFolder}/{def.ItemId}_{rotation}.png";
                    Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                    if (sprite == null) sprite = CreateShapeTexture(def, rotation, path);
                    sprites.Add(sprite);
                }
            }
            return sprites;
        }

        private static Sprite CreateShapeTexture(GridItemDef def, int rotation, string path)
        {
            var item = new GridItem(def) { Rotation = rotation };
            int w = item.SizeX;
            int h = item.SizeY;
            int width = w * ShapeCellPixels;
            int height = h * ShapeCellPixels;

            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];
            var fill = new Color32(255, 255, 255, 235);
            var edge = new Color32(120, 200, 255, 255);
            var clear = new Color32(0, 0, 0, 0);

            for (int py = 0; py < height; py++)
            {
                for (int px = 0; px < width; px++)
                {
                    int cx = px / ShapeCellPixels;
                    int cy = py / ShapeCellPixels;
                    // 贴图 y 向上，形状位图 y 向下：行号需要翻转
                    bool filled = item.IsFilled(cx, h - 1 - cy, rotation);
                    if (!filled) { pixels[py * width + px] = clear; continue; }

                    int lx = px % ShapeCellPixels;
                    int ly = py % ShapeCellPixels;
                    bool border = lx == 0 || ly == 0 || lx == ShapeCellPixels - 1 || ly == ShapeCellPixels - 1;
                    pixels[py * width + px] = border ? edge : fill;
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                // 半透明纯色衬底必须 FullRect：Tight 会按不透明像素裁网格，渐变/描边几乎画不出来
                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);
                settings.spriteMeshType = SpriteMeshType.FullRect;
                importer.SetTextureSettings(settings);
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.maxTextureSize = 256;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.filterMode = FilterMode.Bilinear;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        // ──────────────────────────────
        // 预制体
        // ──────────────────────────────

        private static GameObject BuildPrefab(List<GridItemDef> defs, List<Sprite> shapes)
        {
            TMP_FontAsset font = LoadChineseFont();
            Sprite cell = AssetDatabase.LoadAssetAtPath<Sprite>(SourceCellSprite);
            Sprite background = LoadSprite(BackgroundPath);

            var rootGo = new GameObject("GridBagDemo", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(GridBagDemo));
            var rootRect = (RectTransform)rootGo.transform;
            rootRect.sizeDelta = new Vector2(1920f, 1080f);

            var canvas = rootGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = rootGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var demo = rootGo.GetComponent<GridBagDemo>();

            var esGo = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            esGo.transform.SetParent(rootGo.transform, false);

            // 背景：铺满 + 压暗，避免浅色主页图抢视线
            RectTransform backdrop = NewRect("Backdrop", rootGo.transform);
            Stretch(backdrop);
            var backdropImage = backdrop.gameObject.AddComponent<Image>();
            backdropImage.sprite = background;
            backdropImage.color = new Color(0.42f, 0.45f, 0.50f, 1f);
            backdropImage.raycastTarget = false;

            RectTransform dragLayer = NewRect("DragLayer", rootGo.transform);
            Stretch(dragLayer);

            RectTransform sheet = NewRect("Sheet", rootGo.transform);
            Center(sheet, new Vector2(1760f, 880f));

            Image sheetBg = sheet.gameObject.AddComponent<Image>();
            sheetBg.color = PanelColor;
            sheetBg.raycastTarget = true;

            // ── 左：背包面板
            RectTransform bagPanel = NewRect("BagPanel", sheet);
            AnchorTo(bagPanel, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, -24f), new Vector2(756f, 832f));

            RectTransform bagHeader = BuildHeader(bagPanel, "网格背包 Demo", font);
            AddStatsLabels(NewRectAt("Stats", bagHeader,
                new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-28f, 0f), new Vector2(420f, 44f)), font,
                out TMP_Text capacityLabel, out TMP_Text weightLabel);
            BuildHint(bagPanel, "左键拖动整块 · 右键/中键点击旋转 · 拖到面板外丢弃 · 拖到别的物品上互换", font);

            RectTransform bagArea = NewRect("BagArea", bagPanel);
            Center(bagArea, GridBagLayout.GridSize(BagWidth, BagHeight, GridBagView.CellSize, GridBagView.Spacing));
            bagArea.anchoredPosition = new Vector2(0f, -30f);

            // 背包区域必须有 Graphic 才能收到射线（与框架 uGUI 分叉一致，自己补 CanvasRenderer）
            var bagBg = bagArea.gameObject.AddComponent<Image>();
            bagBg.color = new Color(0f, 0f, 0f, 0.28f);
            bagBg.raycastTarget = true;

            RectTransform cellsRoot = NewRect("Cells", bagArea);
            Stretch(cellsRoot);
            RectTransform itemsRoot = NewRect("Items", bagArea);
            Stretch(itemsRoot);
            itemsRoot.gameObject.AddComponent<CanvasGroup>().blocksRaycasts = false;

            RectTransform preview = NewRect("DropPreview", itemsRoot);
            var previewImage = preview.gameObject.AddComponent<Image>();
            previewImage.raycastTarget = false;
            previewImage.enabled = false;

            GridBagItemView itemTemplate = BuildItemTemplate(itemsRoot, font);

            var dragTarget = bagArea.gameObject.AddComponent<GridBagDragTarget>();
            dragTarget.Configure(demo);
            var bagView = bagArea.gameObject.AddComponent<GridBagView>();

            // ── 右：货物清单面板
            RectTransform lootPanel = NewRect("LootPanel", sheet);
            AnchorTo(lootPanel, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-24f, -24f), new Vector2(916f, 832f));
            BuildHeader(lootPanel, "货物清单（拖到背包里）", font);
            BuildHint(lootPanel, "拖到背包空白处自动吸附到最近的合法位置；拖回面板外则保持未入包", font);

            RectTransform lootViewport = NewRect("LootViewport", lootPanel);
            AnchorTo(lootViewport, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(28f, 28f), new Vector2(-56f, -136f));
            lootViewport.gameObject.AddComponent<RectMask2D>();

            RectTransform lootContent = NewRect("LootContent", lootViewport);
            AnchorTo(lootContent, new Vector2(0f, 1f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero);

            var grid = lootContent.gameObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(140f, 168f);
            grid.spacing = new Vector2(10f, 10f);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = LootColumns;
            grid.childAlignment = TextAnchor.UpperLeft;

            var fitter = lootContent.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            GridBagLootSlotView lootTemplate = BuildLootTemplate(lootContent, font);

            var lootView = lootPanel.gameObject.AddComponent<GridBagLootView>();

            // 清单可滚动：ScrollRect 挂视口，内容越界时滚轮可用
            var scroll = lootViewport.gameObject.AddComponent<ScrollRect>();
            scroll.content = lootContent;
            scroll.viewport = lootViewport;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;

            // 拖影
            RectTransform ghostRect = NewRect("DragGhost", dragLayer);
            Center(ghostRect, Vector2.zero);
            var ghostImage = ghostRect.gameObject.AddComponent<Image>();
            ghostImage.raycastTarget = false;
            ghostImage.preserveAspect = true;
            ghostImage.color = new Color(1f, 1f, 1f, 0.85f);
            ghostImage.enabled = false;

            // 提示
            RectTransform toast = NewRect("Toast", sheet);
            Center(toast, new Vector2(560f, 56f));
            // 整张面板（Sheet）底边正下方：Sheet 高 880，Toast 半高 28，再留 14
            toast.anchoredPosition = new Vector2(0f, -482f);
            Image toastBg = toast.gameObject.AddComponent<Image>();
            toastBg.color = new Color(0.06f, 0.07f, 0.09f, 0.92f);
            var toastGroup = toast.gameObject.AddComponent<CanvasGroup>();
            toastGroup.alpha = 0f;
            toastGroup.blocksRaycasts = false;
            TMP_Text toastText = NewText("Text", toast, font, 24f, TextColor, TextAlignmentOptions.Center);

            // ── 序列化引用
            AssignBagView(bagView, cellsRoot, itemsRoot, itemTemplate, previewImage, dragTarget);
            AssignLootView(lootView, lootContent, lootTemplate);

            var serialized = new SerializedObject(demo);
            serialized.FindProperty("initialItems").arraySize = 0;
            serialized.FindProperty("lootItems").arraySize = 0;
            SerializedProperty initialProp = serialized.FindProperty("initialItems");
            SerializedProperty lootProp = serialized.FindProperty("lootItems");
            // 清单物品是 Specs 里最后 LootCount 个（L 形贵重品 + 消音器 + 芯片 + 饰品）
            int initialCount = defs.Count - LootCount;
            for (int i = 0; i < defs.Count; i++)
            {
                if (i < initialCount) AddObject(initialProp, defs[i]);
                else AddObject(lootProp, defs[i]);
            }
            serialized.FindProperty("bagWidth").intValue = BagWidth;
            serialized.FindProperty("bagHeight").intValue = BagHeight;
            serialized.FindProperty("bagView").objectReferenceValue = bagView;
            serialized.FindProperty("lootView").objectReferenceValue = lootView;
            serialized.FindProperty("dragLayer").objectReferenceValue = dragLayer;
            serialized.FindProperty("dragGhost").objectReferenceValue = ghostImage;
            serialized.FindProperty("cellSprite").objectReferenceValue = cell;
            serialized.FindProperty("capacityText").objectReferenceValue = capacityLabel;
            serialized.FindProperty("weightText").objectReferenceValue = weightLabel;
            serialized.FindProperty("toastText").objectReferenceValue = toastText;
            serialized.FindProperty("toastGroup").objectReferenceValue = toastGroup;

            SerializedProperty shapeProp = serialized.FindProperty("shapeSprites");
            shapeProp.arraySize = shapes.Count;
            for (int i = 0; i < shapes.Count; i++)
                shapeProp.GetArrayElementAtIndex(i).objectReferenceValue = shapes[i];

            serialized.ApplyModifiedPropertiesWithoutUndo();

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(rootGo, PrefabPath);
            Object.DestroyImmediate(rootGo);
            return prefab;
        }

        private static void AssignBagView(GridBagView view, RectTransform cellsRoot, RectTransform itemsRoot,
            GridBagItemView template, Image preview, GridBagDragTarget dragTarget)
        {
            var serialized = new SerializedObject(view);
            serialized.FindProperty("cellsRoot").objectReferenceValue = cellsRoot;
            serialized.FindProperty("itemsRoot").objectReferenceValue = itemsRoot;
            serialized.FindProperty("itemViewTemplate").objectReferenceValue = template;
            serialized.FindProperty("previewImage").objectReferenceValue = preview;
            serialized.FindProperty("dragTarget").objectReferenceValue = dragTarget;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void AssignLootView(GridBagLootView view, RectTransform content, GridBagLootSlotView template)
        {
            var serialized = new SerializedObject(view);
            serialized.FindProperty("slotsRoot").objectReferenceValue = content;
            serialized.FindProperty("slotTemplate").objectReferenceValue = template;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        // ──────────────────────────────
        // 子节点
        // ──────────────────────────────────────────────

        private static RectTransform BuildHeader(RectTransform panel, string title, TMP_FontAsset font)
        {
            RectTransform header = NewRect("Header", panel);
            AnchorTo(header, new Vector2(0f, 1f), new Vector2(1f, 1f), Vector2.zero, new Vector2(0f, 76f));
            Image headerBg = header.gameObject.AddComponent<Image>();
            headerBg.color = PanelHeaderColor;
            headerBg.raycastTarget = false;

            RectTransform titleRect = NewRect("Title", header);
            AnchorTo(titleRect, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(28f, 0f), new Vector2(560f, 44f));
            TMP_Text titleText = titleRect.gameObject.AddComponent<TextMeshProUGUI>();
            titleText.font = font;
            titleText.fontSize = 30f;
            titleText.color = TextColor;
            titleText.alignment = TextAlignmentOptions.MidlineLeft;
            titleText.overflowMode = TextOverflowModes.Overflow;
            titleText.enableWordWrapping = false;
            titleText.text = title;
            return header;
        }

        /// <summary>标题栏右侧的容量/重量两段文字</summary>
        private static void AddStatsLabels(RectTransform statsRoot, TMP_FontAsset font,
            out TMP_Text capacityLabel, out TMP_Text weightLabel)
        {
            TMP_Text capacity = NewText("Capacity", statsRoot, font, 26f, TextColor, TextAlignmentOptions.MidlineRight);
            AnchorTo((RectTransform)capacity.transform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), Vector2.zero, new Vector2(260f, 40f));

            TMP_Text weight = NewText("Weight", statsRoot, font, 22f, SubTextColor, TextAlignmentOptions.MidlineLeft);
            AnchorTo((RectTransform)weight.transform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(140f, 36f));

            capacity.SetText("容量 0 / 0");
            weight.SetText("重量 0");
            capacityLabel = capacity;
            weightLabel = weight;
        }

        private static void BuildHint(RectTransform panel, string hint, TMP_FontAsset font)
        {
            TMP_Text text = NewText("Hint", panel, font, 20f, SubTextColor, TextAlignmentOptions.MidlineLeft);
            AnchorTo((RectTransform)text.transform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(28f, -86f), new Vector2(-28f, 30f));
            text.text = hint;
            text.enableWordWrapping = true;
        }

        private static GridBagItemView BuildItemTemplate(Transform parent, TMP_FontAsset font)
        {
            RectTransform root = NewRect("ItemTemplate", parent);
            Center(root, new Vector2(64f, 64f));
            var view = root.gameObject.AddComponent<GridBagItemView>();

            RectTransform shape = NewRect("Shape", root);
            Stretch(shape);
            Image shapeImage = shape.gameObject.AddComponent<Image>();
            shapeImage.raycastTarget = false;
            shapeImage.color = new Color(1f, 1f, 1f, 0.92f);

            RectTransform icon = NewRect("Icon", root);
            Center(icon, new Vector2(52f, 52f));
            Image iconImage = icon.gameObject.AddComponent<Image>();
            iconImage.raycastTarget = false;
            iconImage.preserveAspect = true;

            TMP_Text weight = NewText("Weight", root, font, 18f, new Color(1f, 0.85f, 0.45f, 1f), TextAlignmentOptions.BottomRight);
            AnchorTo((RectTransform)weight.transform, Vector2.one, Vector2.one, new Vector2(-4f, -2f), new Vector2(48f, 22f));

            var serialized = new SerializedObject(view);
            serialized.FindProperty("shapeImage").objectReferenceValue = shapeImage;
            serialized.FindProperty("iconImage").objectReferenceValue = iconImage;
            serialized.FindProperty("weightText").objectReferenceValue = weight;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return view;
        }

        private static GridBagLootSlotView BuildLootTemplate(Transform parent, TMP_FontAsset font)
        {
            RectTransform root = NewRect("LootTemplate", parent);
            root.sizeDelta = new Vector2(140f, 168f);
            Image bg = root.gameObject.AddComponent<Image>();
            bg.color = LootSlotColor;
            bg.raycastTarget = true;
            var slot = root.gameObject.AddComponent<GridBagLootSlotView>();

            RectTransform icon = NewRect("Icon", root);
            Center(icon, new Vector2(LootIconSize, LootIconSize));
            icon.anchoredPosition = new Vector2(0f, 18f);
            Image iconImage = icon.gameObject.AddComponent<Image>();
            iconImage.raycastTarget = false;
            iconImage.preserveAspect = true;

            TMP_Text name = NewText("Name", root, font, 18f, TextColor, TextAlignmentOptions.Center);
            AnchorTo((RectTransform)name.transform, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(6f, 8f), new Vector2(-12f, 44f));
            name.enableWordWrapping = true;

            var serialized = new SerializedObject(slot);
            serialized.FindProperty("iconImage").objectReferenceValue = iconImage;
            serialized.FindProperty("nameText").objectReferenceValue = name;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return slot;
        }

        // ──────────────────────────────
        // 场景
        // ──────────────────────────────

        private static void PlaceInScene(GameObject prefab)
        {
            if (!File.Exists(ScenePath))
            {
                GameLog.Warn($"[GridBagDemo] 场景不存在：{ScenePath}（预制体已生成，手动建一个场景放置即可）");
                return;
            }

            // 只在「活动场景就是 beibao」时自动放置，避免把实例写进别的场景
            Scene active = SceneManager.GetActiveScene();
            if (active.path != ScenePath)
            {
                GameLog.Warn($"[GridBagDemo] 当前活动场景是 {active.path}，未自动放置实例；打开 {ScenePath} 后再执行一次即可");
            }
            else
            {
                GameObject existing = GameObject.Find("GridBagDemo");
                if (existing != null) Object.DestroyImmediate(existing);

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                instance.transform.SetParent(null, false);
                SceneManager.MoveGameObjectToScene(instance, active);
                EditorSceneManager.MarkSceneDirty(active);
            }

            EnsureInBuildSettings();
        }

        private static void EnsureInBuildSettings()
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (scenes.Exists(s => s.path == ScenePath)) return;
            scenes.Add(new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
            GameLog.Info($"[GridBagDemo] 已登记 Build Settings：{ScenePath}");
        }

        // ──────────────────────────────
        // 工具
        // ──────────────────────────────

        private static TMP_FontAsset LoadChineseFont()
        {
            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/Game/Resources/Fonts/NotoSansSC-Regular SDF.asset");
            if (font == null) GameLog.Warn("[GridBagDemo] 未找到中文字体，文本可能显示为方块");
            return font;
        }

        private static Sprite LoadSprite(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null && importer.textureType != TextureImporterType.Sprite)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        private static void AddObject(SerializedProperty arrayProperty, Object value)
        {
            int index = arrayProperty.arraySize;
            arrayProperty.InsertArrayElementAtIndex(index);
            arrayProperty.GetArrayElementAtIndex(index).objectReferenceValue = value;
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            return rect;
        }

        private static RectTransform NewRectAt(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 position, Vector2 size)
        {
            RectTransform rect = NewRect(name, parent);
            AnchorTo(rect, anchorMin, anchorMax, position, size);
            return rect;
        }

        private static TMP_Text NewText(string name, Transform parent, TMP_FontAsset font, float size, Color color, TextAlignmentOptions alignment)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<TextMeshProUGUI>();
            text.font = font;
            text.fontSize = size;
            text.color = color;
            text.alignment = alignment;
            text.overflowMode = TextOverflowModes.Overflow;
            text.raycastTarget = false;
            return text;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = Vector2.zero;
        }

        private static void Center(RectTransform rect, Vector2 size)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = size;
        }

        private static void AnchorTo(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 position, Vector2 size)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = anchorMax == anchorMin ? anchorMin : new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }
    }
}
