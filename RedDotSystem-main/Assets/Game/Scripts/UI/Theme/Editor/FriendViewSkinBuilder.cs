using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 好友面板（FriendView）视觉重建 —— 原神式「米金书页」风格，可重复执行。
///
/// 做法与 HomeViewSkinBuilder 一致：只改视觉（贴图/配色/字号/圆角/间距）与 RectTransform，
/// 节点名、Button、ScrollRect、VirtualScrollList、UIControlData 绑定全部原样保留；
/// 程序化贴图写进 Resources/ui/Friends（运行时也能按名取到，供选中态高亮使用）。
///
/// 设计要点：
///   · 底色沿用 PanelSkin 的 bg_friend，但盖一层薄米白纱（SkinVeil）压低水墨插画的噪点；
///   · 所有容器从「纯色方块」换成九宫格圆角卡片（米白填充 + 金色细边）；
///   · 头像改成「暖白圆盘 + 金环 + 名字首字」的徽章，在线点挪到头像右下；
///   · 按钮从微信绿改成金色渐变（主操作）/ 米白软底（次要操作）；
///   · 气泡改为米白 / 淡金两种圆角卡片，标题改为「金色细线 + 菱形」的分栏。
/// 菜单：Tools/UI/重做好友面板
/// </summary>
public static class FriendViewSkinBuilder
{
    private const string PrefabPath = "Assets/Game/UI/Friends/FriendView.prefab";
    private const string ArtFolder = "Assets/Game/Resources/ui/Friends";
    private const string ThemeFolder = "Assets/Game/Resources/ui/Theme";
    private const string OrnamentPath = "Assets/Game/UI/Friends/Art/orn_corner.png";
    private const string EmblemPath = "Assets/Game/UI/Friends/Art/emblem_chat.png";
    private const string FontPath = "Assets/Codely/Fonts/NotoSansSC-Regular SDF.asset";

    // ── 配色（米白 + 香槟金 + 墨褐文字；与 Assets/Game/Resources/ui/Theme 底图同族）──
    private static readonly Color Ivory = Hex(0xFF, 0xFC, 0xF5);
    private static readonly Color IvorySoft = Hex(0xFD, 0xF8, 0xEE);
    private static readonly Color Cream = Hex(0xFB, 0xF3, 0xE2);
    private static readonly Color RowFill = Hex(0xFB, 0xF3, 0xE2);
    private static readonly Color RowSelected = Hex(0xF6, 0xE7, 0xC4);
    private static readonly Color Gold = Hex(0xD9, 0xB2, 0x5C);
    private static readonly Color GoldLight = Hex(0xF3, 0xE2, 0xB8);
    private static readonly Color GoldDeep = Hex(0xA9, 0x85, 0x3F);
    private static readonly Color GoldEdge = Hex(0xC8, 0xB0, 0x88);
    private static readonly Color BorderSoft = Hex(0xE2, 0xD2, 0xB2);
    private static readonly Color Ink = Hex(0x43, 0x40, 0x3A);
    private static readonly Color InkSoft = Hex(0x7A, 0x72, 0x64);
    private static readonly Color InkSub = Hex(0x8C, 0x85, 0x78);
    private static readonly Color BadgeRed = Hex(0xC0, 0x57, 0x4C);
    private static readonly Color Jade = Hex(0x4F, 0x8A, 0x7E);

    // ── 版式常量（面板 700x620，中心为原点）──
    private const float HeaderY = 280f;
    private const float HeaderLineY = 248f;

    [MenuItem("Tools/UI/重做好友面板")]
    public static void Build()
    {
        GameObject contents = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (contents == null)
        {
            Debug.LogError("[FriendSkin] 找不到 " + PrefabPath);
            return;
        }

        Transform root = contents.transform;
        Transform panel = Find(root, "FriendPanel");
        if (panel == null)
        {
            Debug.LogError("[FriendSkin] 找不到 FriendPanel");
            PrefabUtility.UnloadPrefabContents(contents);
            return;
        }

        TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
        Sprite plate = EnsurePlate();
        Sprite card = EnsureCard();
        Sprite rowBg = EnsureRow("row", RowFill, BorderSoft, 1.1f);
        Sprite rowSel = EnsureRow("row_sel", RowSelected, Gold, 1.4f);
        Sprite pill = EnsurePill();
        Sprite goldBtn = EnsureGoldButton();
        Sprite softBtn = EnsureSoftButton();
        Sprite ring = EnsureRing();
        Sprite disc = EnsureDisc();
        Sprite diamond = EnsureDiamond();
        Sprite bubbleL = EnsureBubble("bubble_left", Hex(0xFF, 0xFE, 0xFA), Hex(0xDC, 0xCD, 0xB2));
        Sprite bubbleR = EnsureBubble("bubble_right", Hex(0xF7, 0xE7, 0xC6), Hex(0xD8, 0xBC, 0x86));
        Sprite ornament = LoadOrnament();
        Sprite emblem = LoadEmblem();

        int uiLayer = root.gameObject.layer;

        // ── 1) 面板底色修正 + 米白纱 + 四角金饰 ────────────────────────────
        StylePanel(panel, uiLayer, plate, ornament);

        // ── 2) 标题栏 ────────────────────────────────────────────────────
        StyleHeader(panel, font, goldBtn, disc, ring, diamond);

        // ── 3) 分栏线 ────────────────────────────────────────────────────
        Transform separator = Find(panel, "ColumnSeparator");
        if (separator != null)
        {
            SetRect((RectTransform)separator, new Vector2(0f, -12f), new Vector2(1.4f, 470f));
            separator.GetComponent<Image>().color = new Color(Gold.r, Gold.g, Gold.b, 0.42f);
        }
        EnsureImage(panel, "SkinSepDiamond", diamond, Color.white, uiLayer, new Vector2(0f, -12f), new Vector2(14f, 14f));

        // ── 4) 左栏：好友列表页 ──────────────────────────────────────────
        StyleListPage(panel, font, card, rowBg, rowSel, disc, ring, plate);

        // ── 5) 左栏：新的朋友页 ──────────────────────────────────────────
        StyleRequestsPage(panel, font, plate, card, goldBtn, softBtn, disc, ring);

        // ── 6) 左栏：添加朋友页 ──────────────────────────────────────────
        StyleAddPage(panel, font, pill, goldBtn, softBtn, disc, ring);

        // ── 7) 右栏：聊天区 / 输入栏 / 表情面板 / 查询结果 ─────────────────
        StyleChat(panel, font, plate, pill, goldBtn, softBtn, bubbleL, bubbleR, disc, ring, diamond, emblem);

        PrefabUtility.SaveAsPrefabAsset(contents, PrefabPath);
        PrefabUtility.UnloadPrefabContents(contents);
        AssetDatabase.SaveAssets();
        Debug.Log("[FriendSkin] 好友面板已重建：" + PrefabPath);
    }

    // ══════════════════════════════════════════════════════════════════
    // 面板 / 标题栏
    // ══════════════════════════════════════════════════════════════════

    private static void StylePanel(Transform panel, int uiLayer, Sprite plate, Sprite ornament)
    {
        // 面板自身保留 PanelSkin 在运行期替换的 bg_friend（这里只兜底配色）
        var img = panel.GetComponent<Image>();
        if (img != null) img.color = IvorySoft;

        // 米白纱：把水墨插画压成很淡的底纹，保住金色角花又让内容区干净
        Image veil = EnsureImage(panel, "SkinVeil", plate, new Color(1f, 0.99f, 0.965f, 0.78f), uiLayer,
            Vector2.zero, new Vector2(-24f, -24f), Image.Type.Sliced);
        veil.rectTransform.anchorMin = Vector2.zero;
        veil.rectTransform.anchorMax = Vector2.one;
        veil.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        veil.rectTransform.offsetMin = new Vector2(12f, 12f);
        veil.rectTransform.offsetMax = new Vector2(-12f, -12f);
        veil.transform.SetSiblingIndex(0);

        // 四角金饰（AI 生成的角花，缺图时自动跳过）
        // 注意：不能用 localScale 负值镜像——矩形围绕角点轴心翻转后会被甩到面板外，
        // 改成「轴心放中心 + 绕 Z 旋转 90° 的整数倍」，纹样就会落进对应的角。
        if (ornament != null)
        {
            MakeCorner(panel, "SkinCornerTL", ornament, uiLayer, new Vector2(0f, 1f), 0f);
            MakeCorner(panel, "SkinCornerTR", ornament, uiLayer, new Vector2(1f, 1f), -90f);
            MakeCorner(panel, "SkinCornerBL", ornament, uiLayer, new Vector2(0f, 0f), 90f);
            MakeCorner(panel, "SkinCornerBR", ornament, uiLayer, new Vector2(1f, 0f), 180f);
        }
    }

    private static void MakeCorner(Transform panel, string name, Sprite sprite, int uiLayer, Vector2 anchor, float zRotation)
    {
        const float size = 104f;
        const float inset = 8f;
        Image img = EnsureImage(panel, name, sprite, new Color(1f, 1f, 1f, 0.5f), uiLayer, Vector2.zero, new Vector2(size, size));
        RectTransform rt = img.rectTransform;
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(
            anchor.x < 0.5f ? inset + size * 0.5f : -inset - size * 0.5f,
            anchor.y < 0.5f ? inset + size * 0.5f : -inset - size * 0.5f);
        rt.localEulerAngles = new Vector3(0f, 0f, zRotation);
        rt.localScale = Vector3.one;
        img.raycastTarget = false;
        img.transform.SetSiblingIndex(1);
    }

    private static void StyleHeader(Transform panel, TMP_FontAsset font, Sprite goldBtn,
        Sprite disc, Sprite ring, Sprite diamond)
    {
        // 标题：左侧大字 + 字距（原神式标题感）；面板内坐标原点在中心
        Transform title = Find(panel, "ListPage/TitleText");
        if (title != null)
        {
            SetRect((RectTransform)title, new Vector2(-236f, HeaderY), new Vector2(190f, 38f));
            StyleText(title.GetComponent<TMP_Text>(), "好友", 27f, Ink, TextAlignmentOptions.Left, FontStyles.Bold, font, 4f);
        }
        // 标题下的一道短金线（替代原来的菱形，避免和面板边缘/按钮抢位置）
        EnsureImage(panel, "SkinTitleRule", null, new Color(Gold.r, Gold.g, Gold.b, 0.5f),
            panel.gameObject.layer, new Vector2(-236f, 256f), new Vector2(150f, 1.4f));
        Transform oldDiamond = panel.Find("SkinTitleDiamond");
        if (oldDiamond != null) Object.DestroyImmediate(oldDiamond.gameObject);

        // 「+ 添加」：金色渐变胶囊
        Transform add = Find(panel, "ListPage/AddButton");
        if (add != null)
        {
            SetRect((RectTransform)add, new Vector2(-60f, HeaderY), new Vector2(86f, 34f));
            StyleButton(add, goldBtn, Image.Type.Sliced, new Color(1f, 1f, 1f, 1f));
            TMP_Text label = FindLabel(add);
            StyleText(label, "+ 添加", 14f, Ink, TextAlignmentOptions.Center, FontStyles.Normal, font, 1f);
        }

        // 关闭键：米白圆盘 + 金环 + 墨色叉（浅色系，避免 PanelSkin 把白字翻成深色）
        Transform close = Find(panel, "CloseButton");
        if (close != null)
        {
            SetRect((RectTransform)close, new Vector2(322f, 282f), new Vector2(34f, 34f));
            StyleButton(close, disc, Image.Type.Simple, Ivory, ring);
            TMP_Text label = FindLabel(close);
            StyleText(label, "×", 18f, Ink, TextAlignmentOptions.Center, FontStyles.Normal, font, 0f);
        }

        // 右栏标题（未选中时是提示语，选中后是好友名）
        Transform chatTitle = Find(panel, "ChatTitle");
        if (chatTitle != null)
        {
            SetRect((RectTransform)chatTitle, new Vector2(175f, HeaderY), new Vector2(300f, 34f));
            StyleText(chatTitle.GetComponent<TMP_Text>(), null, 19f, Ink, TextAlignmentOptions.Center, FontStyles.Bold, font, 2f);
        }

        // 标题下金线（含中点菱形，做分栏的视觉分割）
        EnsureImage(panel, "SkinHeaderLine", null, new Color(Gold.r, Gold.g, Gold.b, 0.38f), panel.gameObject.layer,
            new Vector2(0f, HeaderLineY), new Vector2(640f, 1.2f));
        EnsureImage(panel, "SkinHeaderDiamond", diamond, new Color(1f, 1f, 1f, 0.9f), panel.gameObject.layer,
            new Vector2(0f, HeaderLineY), new Vector2(13f, 13f));
    }

    // ══════════════════════════════════════════════════════════════════
    // 左栏
    // ══════════════════════════════════════════════════════════════════

    private static void StyleListPage(Transform panel, TMP_FontAsset font, Sprite card, Sprite rowBg, Sprite rowSel,
        Sprite disc, Sprite ring, Sprite plate)
    {
        int layer = panel.gameObject.layer;

        // 「新的朋友」入口：卡片行 + 徽章
        Transform entry = Find(panel, "ListPage/NewFriendEntry");
        if (entry != null)
        {
            SetRect((RectTransform)entry, new Vector2(-175f, 214f), new Vector2(296f, 48f));
            StyleButton(entry, card, Image.Type.Sliced, Color.white);

            Transform icon = Find(entry, "EntryIcon");
            if (icon != null)
            {
                SetRect((RectTransform)icon, new Vector2(-118f, 0f), new Vector2(34f, 34f));
                var iconImg = icon.GetComponent<Image>();
                iconImg.sprite = disc;
                iconImg.type = Image.Type.Simple;
                iconImg.color = Ivory;
                iconImg.raycastTarget = false;
                EnsureRing(icon, ring, layer, 40f);
                EnsureGlyph(icon, "+", font, 20f, GoldDeep, layer);
            }

            Transform entryText = Find(entry, "EntryText");
            if (entryText != null)
            {
                SetRect((RectTransform)entryText, new Vector2(-3f, 0f), new Vector2(180f, 30f));
                StyleText(entryText.GetComponent<TMP_Text>(), "新的朋友", 17f, Ink, TextAlignmentOptions.Left, FontStyles.Normal, font, 1f);
            }

            Transform badge = Find(entry, "RequestBadge");
            if (badge != null)
            {
                SetRect((RectTransform)badge, new Vector2(124f, 0f), new Vector2(22f, 22f));
                var badgeImg = badge.GetComponent<Image>();
                badgeImg.sprite = disc;
                badgeImg.type = Image.Type.Simple;
                badgeImg.color = BadgeRed;
                badgeImg.raycastTarget = false;
            }
        }

        // 好友列表容器：米白卡片（行是卡片内的浅色条目）
        Transform list = Find(panel, "ListPage/FriendList");
        if (list != null)
        {
            SetRect((RectTransform)list, new Vector2(-175f, 2f), new Vector2(312f, 356f));
            var listImg = list.GetComponent<Image>();
            listImg.sprite = plate;
            listImg.type = Image.Type.Sliced;
            listImg.color = new Color(1f, 1f, 1f, 0.9f);

            Transform viewport = Find(list, "Viewport");
            if (viewport != null)
            {
                var vrt = (RectTransform)viewport;
                vrt.anchorMin = Vector2.zero;
                vrt.anchorMax = Vector2.one;
                vrt.offsetMin = new Vector2(8f, 8f);
                vrt.offsetMax = new Vector2(-8f, -8f);
            }
        }

        // 好友条目模板：圆角浅卡 + 徽章头像 + 两行文字
        Transform item = Find(panel, "ListPage/FriendList/Viewport/Content/FriendItemTemplate");
        if (item != null)
        {
            var itemRt = (RectTransform)item;
            itemRt.sizeDelta = new Vector2(296f, 44f);
            var itemImg = item.GetComponent<Image>();
            itemImg.sprite = rowBg;
            itemImg.type = Image.Type.Sliced;
            itemImg.color = Color.white;

            Transform avatar = Find(item, "Avatar");
            if (avatar != null)
            {
                SetRect((RectTransform)avatar, new Vector2(-124f, 0f), new Vector2(36f, 36f));
                var avImg = avatar.GetComponent<Image>();
                avImg.sprite = disc;
                avImg.type = Image.Type.Simple;
                avImg.color = Color.white;
                avImg.raycastTarget = false;
                EnsureRing(avatar, ring, layer, 40f);
                EnsureGlyph(avatar, "?", font, 17f, GoldDeep, layer, "AvatarInitial");
            }

            // 两行文字从头像右侧起排（x 是"中心 + 左对齐 + 宽度"的组合，左边距 = 行左缘 + 58）
            Transform nameText = Find(item, "NameText");
            if (nameText != null)
            {
                SetRect((RectTransform)nameText, new Vector2(-8f, 7f), new Vector2(150f, 20f));
                StyleText(nameText.GetComponent<TMP_Text>(), "名字", 16f, Ink, TextAlignmentOptions.Left, FontStyles.Normal, font, 0.5f);
            }
            Transform idText = Find(item, "IdText");
            if (idText != null)
            {
                SetRect((RectTransform)idText, new Vector2(-8f, -9f), new Vector2(150f, 16f));
                StyleText(idText.GetComponent<TMP_Text>(), "ID 0", 11f, InkSoft, TextAlignmentOptions.Left, FontStyles.Normal, font, 0.8f);
            }

            Transform unread = Find(item, "UnreadBadge");
            if (unread != null)
            {
                SetRect((RectTransform)unread, new Vector2(124f, 0f), new Vector2(22f, 22f));
                var unreadImg = unread.GetComponent<Image>();
                unreadImg.sprite = disc;
                unreadImg.type = Image.Type.Simple;
                unreadImg.color = BadgeRed;
                unreadImg.raycastTarget = false;
                Transform unreadText = Find(unread, "UnreadBadgeText");
                if (unreadText != null)
                    StyleText(unreadText.GetComponent<TMP_Text>(), null, 12f, Color.white, TextAlignmentOptions.Center, FontStyles.Bold, font, 0f);
            }

            // 头像右下的在线点（绑定代码按名字找它并写颜色）
            EnsureImage(item, "OnlineDot", disc, new Color(0.62f, 0.62f, 0.62f, 1f), layer,
                new Vector2(-99f, -13f), new Vector2(11f, 11f));
            EnsureImage(item, "OnlineDotRing", ring, new Color(1f, 1f, 1f, 0.85f), layer,
                new Vector2(-99f, -13f), new Vector2(15f, 15f));

            // 卡片式条目不再需要分隔线
            Transform separator = Find(item, "ItemSeparator");
            if (separator != null) Object.DestroyImmediate(separator.gameObject);
        }

        Transform status = Find(panel, "ListPage/StatusText");
        if (status != null)
        {
            SetRect((RectTransform)status, new Vector2(-175f, -200f), new Vector2(312f, 24f));
            StyleText(status.GetComponent<TMP_Text>(), null, 12f, InkSoft, TextAlignmentOptions.Center, FontStyles.Normal, font, 0.5f);

            // 状态行落在底图插画上，垫一层半透明米白条，浅灰字才读得清
            Transform listPage = status.parent;
            Image pill = EnsureImage(listPage, "SkinStatusPill", plate,
                new Color(1f, 0.985f, 0.955f, 0.66f), panel.gameObject.layer,
                new Vector2(-175f, -200f), new Vector2(302f, 23f), Image.Type.Sliced);
            pill.transform.SetSiblingIndex(Mathf.Max(0, status.GetSiblingIndex()));
        }
    }

    private static void StyleRequestsPage(Transform panel, TMP_FontAsset font, Sprite plate, Sprite card,
        Sprite goldBtn, Sprite softBtn, Sprite disc, Sprite ring)
    {
        int layer = panel.gameObject.layer;

        Transform back = Find(panel, "RequestsPage/RequestsBackButton");
        if (back != null)
        {
            SetRect((RectTransform)back, new Vector2(-320f, HeaderY), new Vector2(34f, 34f));
            StyleButton(back, disc, Image.Type.Simple, Ivory, ring);
            StyleText(FindLabel(back), "<", 18f, Ink, TextAlignmentOptions.Center, FontStyles.Bold, font, 0f);
        }

        Transform title = Find(panel, "RequestsPage/RequestsTitle");
        if (title != null)
        {
            SetRect((RectTransform)title, new Vector2(-150f, HeaderY), new Vector2(280f, 36f));
            StyleText(title.GetComponent<TMP_Text>(), null, 20f, Ink, TextAlignmentOptions.Center, FontStyles.Bold, font, 3f);
        }

        Transform list = Find(panel, "RequestsPage/RequestList");
        if (list != null)
        {
            SetRect((RectTransform)list, new Vector2(-175f, 8f), new Vector2(312f, 396f));
            var img = list.GetComponent<Image>();
            img.sprite = plate;
            img.type = Image.Type.Sliced;
            img.color = new Color(1f, 1f, 1f, 0.9f);
        }

        Transform item = Find(panel, "RequestsPage/RequestList/RequestItemTemplate");
        if (item != null)
        {
            var rt = (RectTransform)item;
            rt.sizeDelta = new Vector2(296f, 58f);
            var img = item.GetComponent<Image>();
            img.sprite = card;
            img.type = Image.Type.Sliced;
            img.color = Color.white;

            Transform avatar = Find(item, "Avatar");
            if (avatar != null)
            {
                SetRect((RectTransform)avatar, new Vector2(-108f, 0f), new Vector2(38f, 38f));
                var avImg = avatar.GetComponent<Image>();
                avImg.sprite = disc;
                avImg.type = Image.Type.Simple;
                avImg.color = Color.white;
                EnsureRing(avatar, ring, layer, 44f);
            }

            Transform info = Find(item, "InfoText");
            if (info != null)
            {
                SetRect((RectTransform)info, new Vector2(-25f, 0f), new Vector2(120f, 30f));
                StyleText(info.GetComponent<TMP_Text>(), null, 15f, Ink, TextAlignmentOptions.Left, FontStyles.Normal, font, 0.5f);
            }

            Transform accept = Find(item, "AcceptButton");
            if (accept != null)
            {
                SetRect((RectTransform)accept, new Vector2(78f, 0f), new Vector2(66f, 32f));
                StyleButton(accept, goldBtn, Image.Type.Sliced, Color.white);
                StyleText(FindLabel(accept), "接受", 14f, Ink, TextAlignmentOptions.Center, FontStyles.Bold, font, 1f);
            }

            Transform reject = Find(item, "RejectButton");
            if (reject != null)
            {
                SetRect((RectTransform)reject, new Vector2(136f, 0f), new Vector2(56f, 32f));
                StyleButton(reject, softBtn, Image.Type.Sliced, Color.white);
                StyleText(FindLabel(reject), "拒绝", 14f, InkSoft, TextAlignmentOptions.Center, FontStyles.Normal, font, 1f);
            }

            Transform separator = Find(item, "ItemSeparator");
            if (separator != null) Object.DestroyImmediate(separator.gameObject);
        }
    }

    private static void StyleAddPage(Transform panel, TMP_FontAsset font, Sprite pill, Sprite goldBtn, Sprite softBtn,
        Sprite disc, Sprite ring)
    {
        Transform back = Find(panel, "AddPage/AddBackButton");
        if (back != null)
        {
            SetRect((RectTransform)back, new Vector2(-320f, HeaderY), new Vector2(34f, 34f));
            StyleButton(back, disc, Image.Type.Simple, Ivory, ring);
            StyleText(FindLabel(back), "<", 18f, Ink, TextAlignmentOptions.Center, FontStyles.Bold, font, 0f);
        }

        Transform title = Find(panel, "AddPage/AddTitle");
        if (title != null)
        {
            SetRect((RectTransform)title, new Vector2(-150f, HeaderY), new Vector2(280f, 36f));
            StyleText(title.GetComponent<TMP_Text>(), null, 20f, Ink, TextAlignmentOptions.Center, FontStyles.Bold, font, 3f);
        }

        Transform input = Find(panel, "AddPage/AddFriendInput");
        if (input != null)
        {
            SetRect((RectTransform)input, new Vector2(-175f, 196f), new Vector2(310f, 44f));
            var img = input.GetComponent<Image>();
            img.sprite = pill;
            img.type = Image.Type.Sliced;
            img.color = Color.white;

            Transform area = Find(input, "Text Area");
            if (area != null)
            {
                var rt = (RectTransform)area;
                rt.offsetMin = new Vector2(16f, 4f);
                rt.offsetMax = new Vector2(-16f, -4f);
            }
            Transform placeholder = Find(input, "Text Area/Placeholder");
            if (placeholder != null) StyleText(placeholder.GetComponent<TMP_Text>(), null, 15f, InkSub, TextAlignmentOptions.Left, FontStyles.Normal, font, 0f);
            Transform text = Find(input, "Text Area/Text");
            if (text != null) StyleText(text.GetComponent<TMP_Text>(), null, 15f, Ink, TextAlignmentOptions.Left, FontStyles.Normal, font, 0f);
        }

        Transform search = Find(panel, "AddPage/AddSearchButton");
        if (search != null)
        {
            SetRect((RectTransform)search, new Vector2(-175f, 136f), new Vector2(120f, 42f));
            StyleButton(search, goldBtn, Image.Type.Sliced, Color.white);
            StyleText(FindLabel(search), "查询", 16f, Ink, TextAlignmentOptions.Center, FontStyles.Bold, font, 2f);
        }

        Transform hint = Find(panel, "AddPage/AddHintText");
        if (hint != null)
        {
            SetRect((RectTransform)hint, new Vector2(-175f, 84f), new Vector2(310f, 24f));
            StyleText(hint.GetComponent<TMP_Text>(), null, 11f, InkSub, TextAlignmentOptions.Center, FontStyles.Normal, font, 0.5f);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // 右栏
    // ══════════════════════════════════════════════════════════════════

    private static void StyleChat(Transform panel, TMP_FontAsset font, Sprite plate, Sprite pill, Sprite goldBtn,
        Sprite softBtn, Sprite bubbleL, Sprite bubbleR, Sprite disc, Sprite ring, Sprite diamond, Sprite emblem)
    {
        int layer = panel.gameObject.layer;

        // ── 聊天区卡片 ──
        Transform messages = Find(panel, "MessageList");
        if (messages != null)
        {
            var rt = (RectTransform)messages;
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(175f, -60f);
            rt.sizeDelta = new Vector2(340f, 430f);

            var img = messages.GetComponent<Image>();
            img.sprite = plate;
            img.type = Image.Type.Sliced;
            // 暖纸色：白色/淡金气泡压在上面才有层次（纯白卡片上白气泡会"消失"）
            img.color = new Color(0.988f, 0.963f, 0.918f, 0.96f);

            Transform viewport = Find(messages, "Viewport");
            if (viewport != null)
            {
                var vrt = (RectTransform)viewport;
                vrt.anchorMin = Vector2.zero;
                vrt.anchorMax = Vector2.one;
                vrt.offsetMin = new Vector2(8f, 8f);
                vrt.offsetMax = new Vector2(-8f, -8f);
            }
        }

        // ── 气泡模板：对方=米白、我方=淡金，都是圆角细边卡片 ──
        StyleBubbleTemplate(Find(panel, "MessageList/Viewport/Content/MessageLeftTemplate"), bubbleL, font, Ink);
        StyleBubbleTemplate(Find(panel, "MessageList/Viewport/Content/MessageRightTemplate"), bubbleR, font, Ink);

        // ── 空态提示（代码按名字显隐）──
        var hint = Find(panel, "ChatEmptyHint");
        if (hint == null)
        {
            var go = new GameObject("ChatEmptyHint", typeof(RectTransform));
            go.layer = layer;
            go.transform.SetParent(panel, false);
            hint = go.transform;
        }
        var hintRt = (RectTransform)hint;
        hintRt.anchorMin = hintRt.anchorMax = new Vector2(0.5f, 0.5f);
        hintRt.pivot = new Vector2(0.5f, 0.5f);
        hintRt.anchoredPosition = new Vector2(175f, 20f);
        hintRt.sizeDelta = new Vector2(300f, 220f);
        hint.gameObject.SetActive(true);

        if (emblem != null)
        {
            EnsureImage(hint, "Emblem", emblem, new Color(1f, 1f, 1f, 0.78f), layer,
                new Vector2(0f, 34f), new Vector2(104f, 104f));
        }
        else
        {
            EnsureImage(hint, "Emblem", diamond, new Color(1f, 1f, 1f, 0.5f), layer,
                new Vector2(0f, 34f), new Vector2(48f, 48f));
        }
        EnsureImage(hint, "Rule", null, new Color(Gold.r, Gold.g, Gold.b, 0.5f), layer,
            new Vector2(0f, -24f), new Vector2(104f, 1f));

        EnsureText(hint, "HintText", "从左侧选择一位好友开始聊天", 14f, InkSub,
            TextAlignmentOptions.Center, FontStyles.Normal, font, 1f, layer,
            new Vector2(0f, -52f), new Vector2(280f, 26f));

        // ── 输入栏 ──
        Transform input = Find(panel, "ChatInput");
        if (input != null)
        {
            SetRect((RectTransform)input, new Vector2(147.5f, 45f), new Vector2(195f, 40f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f));
            var img = input.GetComponent<Image>();
            img.sprite = pill;
            img.type = Image.Type.Sliced;
            img.color = Color.white;

            Transform placeholder = Find(input, "Text Area/Placeholder");
            if (placeholder != null) StyleText(placeholder.GetComponent<TMP_Text>(), null, 15f, InkSub, TextAlignmentOptions.Left, FontStyles.Normal, font, 0f);
            Transform text = Find(input, "Text Area/Text");
            if (text != null) StyleText(text.GetComponent<TMP_Text>(), null, 15f, Ink, TextAlignmentOptions.Left, FontStyles.Normal, font, 0f);
        }

        Transform send = Find(panel, "SendButton");
        if (send != null)
        {
            SetRect((RectTransform)send, new Vector2(290f, 45f), new Vector2(80f, 40f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f));
            StyleButton(send, goldBtn, Image.Type.Sliced, Color.white);
            StyleText(FindLabel(send), "发送", 16f, Ink, TextAlignmentOptions.Center, FontStyles.Bold, font, 2f);
        }

        Transform emoji = Find(panel, "EmojiButton");
        if (emoji != null)
        {
            SetRect((RectTransform)emoji, new Vector2(25f, 45f), new Vector2(40f, 40f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f));
            StyleButton(emoji, disc, Image.Type.Simple, Ivory, ring);
            StyleText(FindLabel(emoji), "表情", 13f, Ink, TextAlignmentOptions.Center, FontStyles.Normal, font, 0.5f);
            Transform emojiRing = Find(emoji, "SkinRing");
            if (emojiRing != null) emojiRing.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.85f);
        }

        Transform emojiPanel = Find(panel, "EmojiPanel");
        if (emojiPanel != null)
        {
            SetRect((RectTransform)emojiPanel, new Vector2(175f, 90f), new Vector2(340f, 150f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f));
            var img = emojiPanel.GetComponent<Image>();
            img.sprite = plate;
            img.type = Image.Type.Sliced;
            img.color = new Color(1f, 1f, 1f, 0.97f);

            Transform viewport = Find(emojiPanel, "Viewport");
            if (viewport != null)
            {
                var vrt = (RectTransform)viewport;
                vrt.anchorMin = Vector2.zero;
                vrt.anchorMax = Vector2.one;
                vrt.offsetMin = new Vector2(8f, 8f);
                vrt.offsetMax = new Vector2(-8f, -8f);
            }
            Transform emptyHint = Find(emojiPanel, "EmptyHint");
            if (emptyHint != null)
                StyleText(emptyHint.GetComponent<TMP_Text>(), null, 12f, InkSub, TextAlignmentOptions.Center, FontStyles.Normal, font, 0f);
        }

        // ── 查询结果卡片 ──
        Transform result = Find(panel, "SearchResultView");
        if (result != null)
        {
            SetRect((RectTransform)result, new Vector2(175f, 45f), new Vector2(340f, 430f));
            var img = result.GetComponent<Image>();
            img.sprite = plate;
            img.type = Image.Type.Sliced;
            img.color = new Color(1f, 1f, 1f, 0.95f);

            Transform avatar = Find(result, "ResultAvatar");
            if (avatar != null)
            {
                SetRect((RectTransform)avatar, new Vector2(0f, 96f), new Vector2(92f, 92f));
                var avImg = avatar.GetComponent<Image>();
                avImg.sprite = disc;
                avImg.type = Image.Type.Simple;
                avImg.color = Color.white;
                EnsureRing(avatar, ring, layer, 104f);
            }

            Transform name = Find(result, "ResultNameText");
            if (name != null)
            {
                SetRect((RectTransform)name, new Vector2(0f, 18f), new Vector2(300f, 40f));
                StyleText(name.GetComponent<TMP_Text>(), null, 22f, Ink, TextAlignmentOptions.Center, FontStyles.Bold, font, 2f);
            }
            Transform id = Find(result, "ResultIdText");
            if (id != null)
            {
                SetRect((RectTransform)id, new Vector2(0f, -22f), new Vector2(300f, 28f));
                StyleText(id.GetComponent<TMP_Text>(), null, 13f, InkSub, TextAlignmentOptions.Center, FontStyles.Normal, font, 0.5f);
            }
            Transform apply = Find(result, "ResultApplyButton");
            if (apply != null)
            {
                SetRect((RectTransform)apply, new Vector2(0f, -132f), new Vector2(200f, 48f));
                StyleButton(apply, goldBtn, Image.Type.Sliced, Color.white);
                StyleText(FindLabel(apply), "申请", 17f, Ink, TextAlignmentOptions.Center, FontStyles.Bold, font, 3f);
            }
        }
    }

    private static void StyleBubbleTemplate(Transform template, Sprite sprite, TMP_FontAsset font, Color textColor)
    {
        if (template == null) return;

        var img = template.GetComponent<Image>();
        if (img != null)
        {
            img.sprite = sprite;
            img.type = Image.Type.Sliced;
            img.color = Color.white;
        }

        Transform text = Find(template, "BubbleText");
        if (text != null)
            StyleText(text.GetComponent<TMP_Text>(), null, 15f, textColor, TextAlignmentOptions.MidlineLeft, FontStyles.Normal, font, 0.3f);
    }

    // ══════════════════════════════════════════════════════════════════
    // 组件样式
    // ══════════════════════════════════════════════════════════════════

    private static void StyleButton(Transform button, Sprite sprite, Image.Type type, Color color, Sprite ringSprite = null)
    {
        var img = button.GetComponent<Image>();
        if (img == null)
        {
            img = button.gameObject.AddComponent<Image>();
            if (button.GetComponent<CanvasRenderer>() == null) button.gameObject.AddComponent<CanvasRenderer>();
        }
        img.sprite = sprite;
        img.type = type;
        img.color = color;

        var btn = button.GetComponent<Button>();
        if (btn != null)
        {
            btn.targetGraphic = img;
            ColorBlock colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1f, 0.975f, 0.9f, 1f);
            colors.pressedColor = new Color(0.92f, 0.86f, 0.74f, 1f);
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(0.86f, 0.85f, 0.82f, 0.62f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.08f;
            btn.colors = colors;
        }

        if (ringSprite != null) EnsureRing(button, ringSprite, button.gameObject.layer, 42f, -2f);
    }

    /// <summary>给头像/圆形按钮补一圈金环（尺寸略大于本体，子物体，不吃点击）</summary>
    private static void EnsureRing(Transform parent, Sprite ringSprite, int layer, float size, float inset = 0f)
    {
        Image ring = EnsureImage(parent, "SkinRing", ringSprite, new Color(1f, 1f, 1f, 0.95f), layer, Vector2.zero, new Vector2(size, size));
        ring.raycastTarget = false;
        ring.transform.SetAsLastSibling();
        if (inset != 0f) ring.rectTransform.anchoredPosition = new Vector2(inset, inset);
    }

    /// <summary>圆形徽章中央的首字（好友头像用名字首字，缺省 "?"）</summary>
    private static void EnsureGlyph(Transform parent, string glyph, TMP_FontAsset font, float size, Color color,
        int layer, string name = "Glyph")
    {
        Transform existing = Find(parent, name);
        TextMeshProUGUI text;
        if (existing == null)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer));
            go.layer = layer;
            go.transform.SetParent(parent, false);
            text = go.AddComponent<TextMeshProUGUI>();
        }
        else
        {
            text = existing.GetComponent<TextMeshProUGUI>();
            if (text == null) text = existing.gameObject.AddComponent<TextMeshProUGUI>();
        }

        var rt = text.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        ConfigureText(text, glyph, size, color, TextAlignmentOptions.Center, FontStyles.Bold, font, 0f);
        text.raycastTarget = false;
    }

    private static void StyleText(TMP_Text text, string value, float size, Color color,
        TextAlignmentOptions align, FontStyles style, TMP_FontAsset font, float spacing)
    {
        if (text == null) return;
        if (value != null) text.text = value;
        ConfigureText(text, null, size, color, align, style, font, spacing);
    }

    private static void ConfigureText(TMP_Text text, string value, float size, Color color,
        TextAlignmentOptions align, FontStyles style, TMP_FontAsset font, float spacing)
    {
        if (text == null) return;
        if (value != null) text.text = value;
        if (font != null)
        {
            text.font = font;
            text.fontSharedMaterial = font.material;
        }
        text.fontSize = size;
        text.color = color;
        text.alignment = align;
        text.fontStyle = style;
        text.characterSpacing = spacing;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Overflow;
        text.raycastTarget = false;
    }

    private static TMP_Text FindLabel(Transform button)
    {
        if (button == null) return null;
        foreach (var t in button.GetComponentsInChildren<TMP_Text>(true))
        {
            string n = t.gameObject.name;
            if (n == "Label" || n == "Text (TMP)") return t;
        }
        return button.GetComponentInChildren<TMP_Text>(true);
    }

    // ══════════════════════════════════════════════════════════════════
    // 工具
    // ══════════════════════════════════════════════════════════════════

    private static Transform Find(Transform root, string path)
    {
        if (root == null) return null;
        if (path.IndexOf('/') < 0) return root.Find(path);
        return root.Find(path);
    }

    private static void SetRect(RectTransform rt, Vector2 pos, Vector2 size)
    {
        SetRect(rt, pos, size, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
    }

    private static void SetRect(RectTransform rt, Vector2 pos, Vector2 size, Vector2 anchor, Vector2 pivot)
    {
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }

    /// <summary>新建/复用一个纯文本节点（Graphic 只能有一个，所以文本不能挂在 Image 上）</summary>
    private static TextMeshProUGUI EnsureText(Transform parent, string name, string value, float size, Color color,
        TextAlignmentOptions align, FontStyles style, TMP_FontAsset font, float spacing, int layer,
        Vector2 pos, Vector2 delta)
    {
        Transform existing = parent.Find(name);
        GameObject go;
        if (existing == null)
        {
            go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer));
            go.layer = layer;
            go.transform.SetParent(parent, false);
        }
        else
        {
            go = existing.gameObject;
            if (go.GetComponent<CanvasRenderer>() == null) go.AddComponent<CanvasRenderer>();
        }

        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = delta;

        var text = go.GetComponent<TextMeshProUGUI>();
        if (text == null) text = go.AddComponent<TextMeshProUGUI>();
        ConfigureText(text, value, size, color, align, style, font, spacing);
        text.raycastTarget = false;
        return text;
    }

    private static Image EnsureImage(Transform parent, string name, Sprite sprite, Color color, int layer,
        Vector2 pos, Vector2 size, Image.Type type = Image.Type.Simple)    {
        Transform existing = parent.Find(name);
        GameObject go;
        if (existing == null)
        {
            go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.layer = layer;
            go.transform.SetParent(parent, false);
        }
        else
        {
            go = existing.gameObject;
            if (go.GetComponent<CanvasRenderer>() == null) go.AddComponent<CanvasRenderer>();
        }

        var img = go.GetComponent<Image>();
        if (img == null) img = go.AddComponent<Image>();

        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;

        img.sprite = sprite;
        img.type = sprite != null && sprite.border.sqrMagnitude > 0f ? Image.Type.Sliced : type;
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    // ══════════════════════════════════════════════════════════════════
    // 程序化贴图（写入 Resources/ui/Friends，九宫格边距随图导入）
    // ══════════════════════════════════════════════════════════════════

    private static Sprite EnsurePlate() => EnsureNine("plate", 96, 20f, 1.6f, Ivory, GoldEdge, 32);

    private static Sprite EnsureCard() => EnsureNine("card", 64, 12f, 1.2f, Cream, BorderSoft, 22);

    private static Sprite EnsurePill() => EnsureNine("pill", 64, 22f, 1.2f, Ivory, BorderSoft, 22);

    private static Sprite EnsureBubble(string name, Color fill, Color border) =>
        EnsureNine(name, 48, 14f, 1.2f, fill, border, 18);

    private static Sprite EnsureRow(string name, Color fill, Color? border, float borderWidth)
    {
        float bw = border.HasValue ? borderWidth : 0f;
        return EnsureNine(name, 64, 11f, bw, fill, border ?? Color.clear, 20);
    }

    private static Sprite EnsureGoldButton()
    {
        string path = ArtFolder + "/btn_gold.png";
        int w = 64, h = 40;
        var px = new Color32[w * h];
        for (int y = 0; y < h; y++)
        {
            float t = (float)y / (h - 1);
            Color fill = Color.Lerp(Hex(0xD9, 0xB2, 0x5C), Hex(0xF3, 0xE2, 0xB8), t * t);
            for (int x = 0; x < w; x++)
            {
                float dx = x + 0.5f - w * 0.5f;
                float dy = y + 0.5f - h * 0.5f;
                float d = SdRoundRect(dx, dy, w * 0.5f, h * 0.5f, 14f);
                float mask = Mathf.Clamp01(0.5f - d);
                if (mask <= 0f) { px[y * w + x] = new Color32(0, 0, 0, 0); continue; }

                float borderMask = Mathf.Clamp01(0.5f - Mathf.Abs(d + 0.9f));
                Color c = Color.Lerp(fill, GoldDeep, borderMask);
                // 顶部内侧一道高光，做出金属渐变感
                float hl = Mathf.Clamp01(1f - Mathf.Abs(d + 3.2f)) * Mathf.Clamp01((t - 0.55f) * 4f) * 0.35f;
                c = Color.Lerp(c, Color.white, hl);
                px[y * w + x] = ToColor32(c, mask);
            }
        }
        return WriteSprite(path, w, h, px, new Vector4(20, 14, 20, 14));
    }

    private static Sprite EnsureSoftButton()
    {
        string path = ArtFolder + "/btn_soft.png";
        int w = 64, h = 40;
        var px = new Color32[w * h];
        Color fill = Hex(0xF3, 0xED, 0xE1);
        Color border = Hex(0xD8, 0xCD, 0xB8);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float dx = x + 0.5f - w * 0.5f;
                float dy = y + 0.5f - h * 0.5f;
                float d = SdRoundRect(dx, dy, w * 0.5f, h * 0.5f, 14f);
                float mask = Mathf.Clamp01(0.5f - d);
                if (mask <= 0f) { px[y * w + x] = new Color32(0, 0, 0, 0); continue; }
                float borderMask = Mathf.Clamp01(0.5f - Mathf.Abs(d + 0.9f));
                px[y * w + x] = ToColor32(Color.Lerp(fill, border, borderMask), mask);
            }
        }
        return WriteSprite(path, w, h, px, new Vector4(20, 14, 20, 14));
    }

    private static Sprite EnsureRing()
    {
        string path = ArtFolder + "/ring_gold.png";
        int s = 128;
        var px = new Color32[s * s];
        float radius = 56f;
        for (int y = 0; y < s; y++)
        {
            for (int x = 0; x < s; x++)
            {
                float dx = x + 0.5f - s * 0.5f;
                float dy = y + 0.5f - s * 0.5f;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                float ring = Mathf.Clamp01(0.5f - Mathf.Abs(dist - radius) + 1.4f);
                float inner = Mathf.Clamp01(0.5f - Mathf.Abs(dist - (radius - 3.2f))) * 0.8f;
                float a = Mathf.Clamp01(ring);
                if (a <= 0f && inner <= 0f) { px[y * s + x] = new Color32(0, 0, 0, 0); continue; }
                Color c = Color.Lerp(Gold, GoldLight, Mathf.Clamp01((dy + radius) / (radius * 2f)));
                c = Color.Lerp(c, Color.white, inner * 0.5f);
                px[y * s + x] = ToColor32(c, Mathf.Max(a, inner * 0.6f));
            }
        }
        return WriteSprite(path, s, s, px, Vector4.zero);
    }

    private static Sprite EnsureDisc()
    {
        string path = ArtFolder + "/disc_warm.png";
        int s = 128;
        var px = new Color32[s * s];
        float radius = 60f;
        for (int y = 0; y < s; y++)
        {
            for (int x = 0; x < s; x++)
            {
                float dx = x + 0.5f - s * 0.5f;
                float dy = y + 0.5f - s * 0.5f;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                float mask = Mathf.Clamp01(radius - dist + 0.5f);
                if (mask <= 0f) { px[y * s + x] = new Color32(0, 0, 0, 0); continue; }
                float t = Mathf.Clamp01(dist / radius);
                Color c = Color.Lerp(Hex(0xFB, 0xF2, 0xDF), Hex(0xEB, 0xDC, 0xBE), t * t);
                px[y * s + x] = ToColor32(c, mask);
            }
        }
        return WriteSprite(path, s, s, px, Vector4.zero);
    }

    private static Sprite EnsureDiamond()
    {
        string path = ArtFolder + "/diamond.png";
        int s = 32;
        var px = new Color32[s * s];
        for (int y = 0; y < s; y++)
        {
            for (int x = 0; x < s; x++)
            {
                float dx = Mathf.Abs(x + 0.5f - s * 0.5f);
                float dy = Mathf.Abs(y + 0.5f - s * 0.5f);
                float d = dx + dy - (s * 0.5f - 2f);
                float mask = Mathf.Clamp01(0.5f - d);
                if (mask <= 0f) { px[y * s + x] = new Color32(0, 0, 0, 0); continue; }
                px[y * s + x] = ToColor32(Color.Lerp(Gold, GoldLight, 0.35f), mask);
            }
        }
        return WriteSprite(path, s, s, px, Vector4.zero);
    }

    /// <summary>通用九宫格圆角贴图：fill 填充 + border 细边（borderWidth=0 时无描边）</summary>
    private static Sprite EnsureNine(string name, int size, float radius, float borderWidth,
        Color fill, Color border, int slice)
    {
        string path = ArtFolder + "/" + name + ".png";
        var px = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - size * 0.5f;
                float dy = y + 0.5f - size * 0.5f;
                float d = SdRoundRect(dx, dy, size * 0.5f - 0.5f, size * 0.5f - 0.5f, radius);
                float mask = Mathf.Clamp01(0.5f - d);
                if (mask <= 0f) { px[y * size + x] = new Color32(0, 0, 0, 0); continue; }

                Color c = fill;
                if (borderWidth > 0f)
                {
                    float borderMask = Mathf.Clamp01(0.5f - Mathf.Abs(d + borderWidth * 0.5f));
                    c = Color.Lerp(fill, border, borderMask);
                }
                px[y * size + x] = ToColor32(c, mask * fill.a);
            }
        }
        return WriteSprite(path, size, size, px, new Vector4(slice, slice, slice, slice));
    }

    private static Sprite LoadOrnament()
    {
        EnsureArtImporter(OrnamentPath);
        return AssetDatabase.LoadAssetAtPath<Sprite>(OrnamentPath);
    }

    private static Sprite LoadEmblem()
    {
        EnsureArtImporter(EmblemPath);
        return AssetDatabase.LoadAssetAtPath<Sprite>(EmblemPath);
    }

    private static void EnsureArtImporter(string path)
    {
        if (!File.Exists(path)) return;
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null) return;
        if (importer.textureType == TextureImporterType.Sprite
            && importer.spriteImportMode == SpriteImportMode.Single
            && importer.alphaIsTransparency
            && !importer.mipmapEnabled
            && importer.textureCompression == TextureImporterCompression.Uncompressed) return;

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = false;
        importer.maxTextureSize = 512;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.filterMode = FilterMode.Bilinear;
        SetFullRect(importer);
        importer.SaveAndReimport();
    }

    /// <summary>Tuanjie 的 TextureImporter 没有 spriteMeshType 属性，九宫格需要 FullRect，只能走 settings</summary>
    private static void SetFullRect(TextureImporter importer)
    {
        var settings = new TextureImporterSettings();
        importer.ReadTextureSettings(settings);
        settings.spriteMeshType = SpriteMeshType.FullRect;
        importer.SetTextureSettings(settings);
    }

    private static Sprite WriteSprite(string path, int w, int h, Color32[] px, Vector4 border)
    {
        if (!Directory.Exists(ArtFolder)) Directory.CreateDirectory(ArtFolder);

        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
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
            importer.maxTextureSize = 256;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.filterMode = FilterMode.Bilinear;
            SetFullRect(importer);
            importer.spriteBorder = border;
            importer.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }

    private static float SdRoundRect(float px, float py, float halfW, float halfH, float r)
    {
        float qx = Mathf.Abs(px) - (halfW - r);
        float qy = Mathf.Abs(py) - (halfH - r);
        float ax = Mathf.Max(qx, 0f);
        float ay = Mathf.Max(qy, 0f);
        return Mathf.Sqrt(ax * ax + ay * ay) + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
    }

    private static Color32 ToColor32(Color c, float a)
    {
        return new Color32(
            (byte)Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255),
            (byte)Mathf.Clamp(Mathf.RoundToInt(a * 255f), 0, 255));
    }

    private static Color Hex(int r, int g, int b, int a = 255) => new Color32((byte)r, (byte)g, (byte)b, (byte)a);
}
