#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

using TMPro;

/// <summary>
/// 守岸人面板（原阿福）美术层重建：浅色羊皮纸对话区 + 深棕正文 + 金线描边 + 缩放按钮。
///
/// 为什么必须"重新上色"而不是靠 PanelSkin：PanelSkin 只把"亮度>0.42 的 TMP 文字"翻成深色、
/// 只翻"没有 sprite 的深色块"；气泡/消息区都是带 sprite 的九宫格图，不会被翻，
/// 于是出现"深色气泡 + 深棕文字"= 看不清字（2026-10-23 定位）。这里把两边的配色都写成
/// 浅底深字，PanelSkin 再跑一遍也不会改坏（深色字亮度 <0.42 会被跳过）。
///
/// 幂等：重复执行只会覆盖颜色/字号/贴图并补齐缩放按钮，可随时重跑。
/// 菜单：Tools/UI/美化 AgentView 预制体（守岸人）
/// </summary>
public static class AgentViewBeautifier
{
    private const string PrefabPath = "Assets/Game/UI/Agent/AgentView.prefab";
    private const string HomePrefabPath = "Assets/Game/UI/Home/HomeView.prefab";

    private const string AvatarPng = "Assets/Game/Resources/Agent/shouanren_avatar.png";
    private const string HomeIconPng = "Assets/Game/UI/Home/Art/Icons/icon_shouanren.png";

    private const string DisplayName = "守岸人";
    private const string Signature = "你的专属游戏管家 · 背包、任务、天气、玩法问题都能答";
    private const string InputPlaceholder = "和守岸人聊点什么…";

    // ── 浅色主题（与主页原神风一致：米白底 + 深棕字 + 金线）──
    private static readonly Color ChatBg = Hex("FDF8EE");
    private static readonly Color AiBubbleBg = Hex("FFFDF7");
    private static readonly Color PlayerBubbleBg = Hex("F2E1BC");
    private static readonly Color InputBg = Hex("FFFDF8");
    private static readonly Color TextDark = Hex("3B332B");
    private static readonly Color TextMuted = Hex("7A6E5E");
    private static readonly Color PlaceholderColor = Hex("8C8071");
    private static readonly Color TitleColor = Hex("4A3F30");
    private static readonly Color Gold = Hex("C8A860");
    private static readonly Color GoldLine = new Color(0.784f, 0.659f, 0.376f, 0.45f);
    private static readonly Color GoldSoft = Hex("D9B96B");
    private static readonly Color CircleCream = Hex("FBF2DE");

    [MenuItem("Tools/UI/美化 AgentView 预制体（守岸人）")]
    public static void Beautify()
    {
        EnsureSpriteImport(AvatarPng);
        EnsureSpriteImport(HomeIconPng);

        Sprite avatar = AssetDatabase.LoadAssetAtPath<Sprite>(AvatarPng);
        if (avatar == null) Debug.LogError("[守岸人面板] 头像贴图导入失败：" + AvatarPng);

        BeautifyAgentPrefab(avatar);
        BeautifyHomePrefab();

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[守岸人面板] 完成：AgentView.prefab / HomeView.prefab 已更新");
    }

    // ──────────────────────────────
    // AgentView
    // ──────────────────────────────

    private static void BeautifyAgentPrefab(Sprite avatar)
    {
        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null)
        {
            Debug.LogError("[守岸人面板] 预制体打开失败：" + PrefabPath);
            return;
        }

        try
        {
            Transform panel = root.transform.Find("AgentPanel");
            if (panel == null)
            {
                Debug.LogError("[守岸人面板] 找不到 AgentPanel");
                return;
            }

            // ── 标题区 ──
            SetText(panel, "ChatTitle", DisplayName, 24f, TitleColor);
            SetText(panel, "SignatureText", Signature, 14f, Hex("8A7C68"));
            SetText(panel, "OnlineText", "在线", 12f, Hex("8A7C68"));

            // "在线"要让开三字标题（原布局按两字"阿福"排的，改名后会压到"人"字上）
            SetPosition(panel, "OnlineDot", new Vector2(-168f, 277f));
            SetPosition(panel, "OnlineText", new Vector2(-136f, 277f));

            Image separator = FindImage(panel, "Separator");
            if (separator != null) separator.color = GoldLine;

            // ── 聊天区：米白纸面 + 金线内框 ──
            Image chatBg = FindImage(panel, "MessageList");
            if (chatBg != null) chatBg.color = ChatBg;

            Image chatFrame = FindImage(panel, "MessageList/Frame");
            if (chatFrame != null) chatFrame.color = GoldLine;

            // 视口裁切：气泡滚出圆角区域时不该露在纸面外
            Transform viewport = panel.Find("MessageList/Viewport");
            if (viewport != null && viewport.GetComponent<RectMask2D>() == null)
            {
                viewport.gameObject.AddComponent<RectMask2D>();
            }

            // ── 气泡：守岸人（近白）/ 玩家（淡金），统一深棕正文 ──
            BeautifyBubble(panel, "MessageList/Viewport/Content/MessageLeftTemplate", AiBubbleBg, avatar);
            BeautifyBubble(panel, "MessageList/Viewport/Content/MessageRightTemplate", PlayerBubbleBg, null);

            // ── 输入区 ──
            Image inputBg = FindImage(panel, "ChatInput");
            if (inputBg != null) inputBg.color = InputBg;

            Image inputFrame = FindImage(panel, "ChatInput/Frame");
            if (inputFrame != null) inputFrame.color = GoldLine;

            SetText(panel, "ChatInput/Text Area/Text", null, 16f, TextDark);
            SetText(panel, "ChatInput/Text Area/Placeholder", InputPlaceholder, 16f, PlaceholderColor);

            Image sendBg = FindImage(panel, "SendButton");
            if (sendBg != null) sendBg.color = GoldSoft;
            SetText(panel, "SendButton/Label", "发送", 16f, Hex("3B332B"));

            // ── 状态行 / 思考指示器 ──
            SetText(panel, "StatusText", null, 14f, TextMuted);

            Image thinking = FindImage(panel, "ThinkingIndicator");
            if (thinking != null) thinking.color = AiBubbleBg;

            for (int i = 0; i < 3; i++)
            {
                Image dot = FindImage(panel, "ThinkingIndicator/Dots/Dot" + i);
                if (dot != null) dot.color = Gold;
            }

            // ── 头像 ──
            if (avatar != null)
            {
                Image avatarImage = FindImage(panel, "AvatarMask/Avatar");
                if (avatarImage != null) avatarImage.sprite = avatar;

                Transform glyph = panel.Find("AvatarMask/AvatarGlyph");
                if (glyph != null)
                {
                    glyph.gameObject.SetActive(false);
                    TMP_Text glyphText = glyph.GetComponent<TMP_Text>();
                    if (glyphText != null) glyphText.text = "守";
                }
            }

            Image avatarRing = FindImage(panel, "AvatarRing");
            if (avatarRing != null) avatarRing.color = new Color(Gold.r, Gold.g, Gold.b, 0.85f);

            Image closeBg = FindImage(panel, "CloseButton");
            if (closeBg != null) closeBg.color = CircleCream;
            SetText(panel, "CloseButton/Label", "×", 22f, Hex("6B5A3E"));

            // ── 缩放按钮 ──
            EnsureZoomButton(panel as RectTransform, "ZoomOutButton", "-", new Vector2(198f, 272f));
            EnsureZoomButton(panel as RectTransform, "ZoomInButton", "+", new Vector2(244f, 272f));

            EditorUtility.SetDirty(root);
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    private static void BeautifyBubble(Transform panel, string path, Color bg, Sprite avatar)
    {
        Transform bubble = panel.Find(path);
        if (bubble == null) return;

        Image bgImage = bubble.GetComponent<Image>();
        if (bgImage != null) bgImage.color = bg;

        Image tail = bubble.Find("BubbleTail")?.GetComponent<Image>();
        if (tail != null) tail.color = bg;

        TMP_Text text = bubble.Find("BubbleText")?.GetComponent<TMP_Text>();
        if (text != null)
        {
            text.color = TextDark;
            text.fontSize = 17f;
            text.lineSpacing = 8f;          // 行距 +8%：长段落更透气
            text.enableWordWrapping = true;
            text.raycastTarget = false;
        }

        Image msgAvatar = bubble.Find("MsgAvatar")?.GetComponent<Image>();
        if (msgAvatar != null)
        {
            if (avatar != null) msgAvatar.sprite = avatar;
            msgAvatar.rectTransform.sizeDelta = new Vector2(30f, 30f);
        }
    }

    /// <summary>补一个圆形缩放按钮（已存在则只刷新颜色/位置，保持幂等）</summary>
    private static void EnsureZoomButton(RectTransform parent, string name, string glyph, Vector2 pos)
    {
        if (parent == null) return;

        Transform existing = parent.Find(name);
        Button button;
        if (existing == null)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(40f, 40f);
            rt.anchoredPosition = pos;

            Image image = go.GetComponent<Image>();
            image.sprite = LoadAgentSprite("afu_circle");
            image.type = Image.Type.Simple;

            button = go.GetComponent<Button>();
            button.targetGraphic = image;

            // 金线圆环（比按钮小 2px，贴在内侧）
            var ringGo = new GameObject("Ring", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var ringRt = (RectTransform)ringGo.transform;
            ringRt.SetParent(rt, false);
            ringRt.anchorMin = ringRt.anchorMax = new Vector2(0.5f, 0.5f);
            ringRt.pivot = new Vector2(0.5f, 0.5f);
            ringRt.sizeDelta = new Vector2(38f, 38f);
            ringRt.anchoredPosition = Vector2.zero;
            Image ring = ringGo.GetComponent<Image>();
            ring.sprite = LoadAgentSprite("afu_ring");
            ring.raycastTarget = false;

            var labelGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            var labelRt = (RectTransform)labelGo.transform;
            labelRt.SetParent(rt, false);
            labelRt.anchorMin = labelRt.anchorMax = new Vector2(0.5f, 0.5f);
            labelRt.pivot = new Vector2(0.5f, 0.5f);
            labelRt.sizeDelta = new Vector2(40f, 40f);
            labelRt.anchoredPosition = Vector2.zero;
            var label = labelGo.GetComponent<TMP_Text>();
            label.text = glyph;
            label.fontSize = 22f;
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;
        }
        else
        {
            button = existing.GetComponent<Button>();
            existing.localScale = Vector3.one;
            ((RectTransform)existing).sizeDelta = new Vector2(40f, 40f);
            ((RectTransform)existing).anchoredPosition = pos;
        }

        if (button == null) return;

        Image bg = button.GetComponent<Image>();
        if (bg != null) bg.color = CircleCream;

        Transform ringChild = button.transform.Find("Ring");
        if (ringChild != null)
        {
            Image ring = ringChild.GetComponent<Image>();
            if (ring != null) ring.color = new Color(Gold.r, Gold.g, Gold.b, 0.85f);
        }

        TMP_Text labelText = button.transform.Find("Label")?.GetComponent<TMP_Text>();
        if (labelText != null)
        {
            labelText.text = glyph;
            labelText.color = Hex("6B5A3E");
            labelText.fontSize = 22f;
            labelText.raycastTarget = false;
        }
    }

    // ──────────────────────────────
    // HomeView 入口（名字 + 头像）
    // ──────────────────────────────

    private static void BeautifyHomePrefab()
    {
        GameObject root = PrefabUtility.LoadPrefabContents(HomePrefabPath);
        if (root == null)
        {
            Debug.LogError("[守岸人面板] 预制体打开失败：" + HomePrefabPath);
            return;
        }

        try
        {
            Transform entry = FindDeep(root.transform, "agentButton");
            if (entry == null)
            {
                Debug.LogWarning("[守岸人面板] HomeView 里找不到 agentButton，跳过头像/改名");
                return;
            }

            TMP_Text label = entry.Find("Label")?.GetComponent<TMP_Text>();
            if (label != null) label.text = DisplayName;

            Image icon = entry.Find("Icon")?.GetComponent<Image>();
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(HomeIconPng);
            if (icon != null && sprite != null) icon.sprite = sprite;

            EditorUtility.SetDirty(root);
            PrefabUtility.SaveAsPrefabAsset(root, HomePrefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // ──────────────────────────────
    // 工具
    // ──────────────────────────────

    private static Transform FindDeep(Transform root, string name)
    {
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform found = FindDeep(root.GetChild(i), name);
            if (found != null) return found;
        }
        return null;
    }

    private static Image FindImage(Transform panel, string path)
    {
        Transform target = panel.Find(path);
        return target != null ? target.GetComponent<Image>() : null;
    }

    private static void SetPosition(Transform panel, string path, Vector2 anchoredPos)
    {
        if (panel.Find(path) is RectTransform rt) rt.anchoredPosition = anchoredPos;
    }

    private static void SetText(Transform panel, string path, string text, float fontSize, Color color)
    {
        Transform target = panel.Find(path);
        if (target == null) return;

        TMP_Text tmp = target.GetComponent<TMP_Text>();
        if (tmp == null) return;

        if (text != null) tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.color = color;
        if (tmp.GetComponent<Graphic>() != null) tmp.raycastTarget = false;
    }

    private static Sprite LoadAgentSprite(string fileName)
    {
        return AssetDatabase.LoadAssetAtPath<Sprite>($"Assets/Game/Resources/Agent/ui/{fileName}.png");
    }

    /// <summary>确保 PNG 按 UI Sprite 导入（新拷进来的素材默认可能是 Default）</summary>
    private static void EnsureSpriteImport(string path)
    {
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null) return;

        bool dirty = false;
        if (importer.textureType != TextureImporterType.Sprite)
        {
            importer.textureType = TextureImporterType.Sprite;
            dirty = true;
        }
        if (importer.spriteImportMode != SpriteImportMode.Single)
        {
            importer.spriteImportMode = SpriteImportMode.Single;
            dirty = true;
        }
        if (importer.mipmapEnabled)
        {
            importer.mipmapEnabled = false;
            dirty = true;
        }
        if (!importer.alphaIsTransparency)
        {
            importer.alphaIsTransparency = true;
            dirty = true;
        }
        if (dirty) importer.SaveAndReimport();
    }

    private static Color Hex(string hex)
    {
        return ColorUtility.TryParseHtmlString("#" + hex, out Color color) ? color : Color.magenta;
    }
}
#endif
