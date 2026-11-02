using System;
using System.Collections.Generic;
using System.IO;
using DG.Tweening;
using SkierFramework;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// P3 一键构建：
///   1. 对战大厅 / 加入房间 / 对战房间 三个 UIView 预制体（深色 HUD，与项目现有面板一致）
///   2. NetworkManager + UnityTransport + RoomState 的常驻联机预制体
///   3. 选人 3D 预览占位预制体（P7 换成安比/艾莲）
///   4. zhuye 顶部“对战”入口按钮
///
/// 菜单：Tools/Battle/P3 一键构建
/// 可重复执行：已存在的预制体会被覆盖，入口按钮会先删后建。
/// </summary>
public static class BattleLobbyPrefabBuilder
{
    private const string BattleUiDir = "Assets/Game/UI/Battle";
    private const string BattleResDir = "Assets/Game/Resources/Battle";
    private const string HeroPreviewDir = "Assets/Game/Resources/Battle/Heroes";
    private const string FontPath = "Assets/Codely/Fonts/NotoSansSC-Regular SDF.asset";

    private static readonly Color PanelBg = new Color(0.976f, 0.961f, 0.925f, 0.95f);
    private static readonly Color SubBg = new Color(0.996f, 0.988f, 0.965f, 0.96f);
    private static readonly Color Primary = new Color(0.239f, 0.220f, 0.188f, 1f);
    private static readonly Color Secondary = new Color(0.545f, 0.506f, 0.443f, 1f);
    private static readonly Color Accent = new Color(0.474f, 0.612f, 0.706f, 1f);
    private static readonly Color Green = new Color(0.427f, 0.659f, 0.494f, 1f);
    private static readonly Color Red = new Color(0.784f, 0.478f, 0.427f, 1f);
    private static readonly Color Steel = new Color(0.545f, 0.506f, 0.443f, 1f);

    // ── 原神风浅色主题（明亮磨砂 + 细金边 + 深棕文字）──
    private static readonly Color Ink = new Color(0.239f, 0.220f, 0.188f, 1f);
    private static readonly Color Raised = new Color(0.996f, 0.988f, 0.965f, 0.96f);
    private static readonly Color Deep = new Color(0.902f, 0.925f, 0.941f, 0.92f);
    private static readonly Color Gold = new Color(0.788f, 0.647f, 0.392f, 1f);
    private static readonly Color GoldDim = new Color(0.788f, 0.647f, 0.392f, 0.45f);
    private static readonly Color Cream = new Color(0.239f, 0.220f, 0.188f, 1f);
    private static readonly Color Muted = new Color(0.545f, 0.506f, 0.443f, 1f);
    private static readonly Color Success = new Color(0.427f, 0.659f, 0.494f, 1f);
    private static readonly Color Danger = new Color(0.784f, 0.478f, 0.427f, 1f);
    private static readonly Color Line = new Color(0.545f, 0.478f, 0.353f, 0.22f);

    /// <summary>主页那套程序化 9 宫格贴图（BarPanel/EntryButton），房间复用保持风格一致</summary>
    private const string HomeArtDir = "Assets/Game/UI/Home/Art";
    private const string ThemeArtDir = "Assets/Game/Resources/ui/Theme";
    private const string PanelSpritePath = ThemeArtDir + "/panel_round.png";
    private const string ButtonSpritePath = ThemeArtDir + "/btn_round.png";
    private const string ShadowSpritePath = HomeArtDir + "/BarShadow.png";
    private const string CircleSpritePath = ThemeArtDir + "/circle.png";
    private const string BackdropSpritePath = ThemeArtDir + "/home_bg.png";

    private static TMP_FontAsset _font;

    [MenuItem("Tools/Battle/P3 一键构建", priority = 1)]
    public static void BuildAll()
    {
        EnsureFolder(BattleUiDir);
        EnsureFolder(BattleResDir);
        EnsureFolder(HeroPreviewDir);

        _font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
        if (_font == null)
            Debug.LogWarning($"[BattleBuilder] 未找到中文字体 {FontPath}，TMP 将使用默认字体 + 全局回退");

        BuildLobbyView();
        BuildJoinRoomView();
        BuildRoomView();
        BuildInviteView();
        BuildInvitePopupView();
        BuildNetworkingPrefab();
        BuildHeroPreviewPrefabs();
        BuildZhuyeEntryButton();

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[BattleBuilder] P3 构建完成");
    }

    /// <summary>
    /// 只重建大厅 / 加入房间 / 选人房间三个 UI。
    /// 刻意不调用 BuildNetworkingPrefab：那个流程会重建联机预制体，最容易再次触发
    /// 嵌入式 UnityTransport 脚本绑定失效的问题。
    /// </summary>
    [MenuItem("Tools/Battle/只重建大厅与选人界面（不动联机预制体）", priority = 0)]
    public static void RebuildLobbyAndRoomUi()
    {
        _font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
        if (_font == null)
            Debug.LogWarning($"[BattleBuilder] 未找到中文字体 {FontPath}，TMP 将使用默认字体 + 全局回退");

        BuildLobbyView();
        BuildJoinRoomView();
        BuildRoomView();

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[BattleBuilder] 大厅 / 加入房间 / 选人界面已重建（未触碰联机预制体）");
    }

    // =====================================================================
    // 大厅
    // =====================================================================
    private static void BuildLobbyView()
    {
        var root = CreateRoot("BattleLobbyView");

        CreateBackdrop(root.transform, 0.28f);

        var panel = CreateUI("Panel", root.transform, Center(), Center(), CenterPivot(), Vector2.zero, new Vector2(760, 500));
        var panelImage = AddImage(panel, PanelBg);
        ApplyNineSlice(panelImage, PanelSpritePath, PanelBg);
        AddShadow(panel.GetComponent<RectTransform>());
        AddBorder(panel.GetComponent<RectTransform>(), GoldDim, 1.5f);

        var bindings = new List<CtrlItemData>();
        Bind(bindings, "panel", panel.GetComponent<RectTransform>());

        var title = CreateAccentTitle(panel.transform, "TitleText", "对战", new Vector2(28f, -24f),
            new Vector2(300f, 42f), 34f);
        Bind(bindings, "titleText", title);

        var subtitle = CreateText(panel.transform, "SubtitleText", "房主即服务端 · 端口 7777", 15, Muted,
            TextAlignmentOptions.TopLeft, new Vector2(44, -72), new Vector2(620, 24),
            new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));
        Bind(bindings, "subtitleText", subtitle);

        CreateDivider(panel.transform, false, -106f, 28f);

        var close = CreateStyledButton(panel.transform, "CloseButton", "×", ButtonTone.Danger,
            new Vector2(-20, -20), new Vector2(40, 40), out var closeLabel,
            new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1));
        closeLabel.fontSize = 24f;
        Bind(bindings, "closeButton", close);

        var create = CreateStyledButton(panel.transform, "CreateRoomButton", "创建房间", ButtonTone.Primary,
            new Vector2(0, 52), new Vector2(380, 64), out _);
        Bind(bindings, "createRoomButton", create);

        var join = CreateStyledButton(panel.transform, "JoinRoomButton", "加入房间", ButtonTone.Secondary,
            new Vector2(0, -22), new Vector2(380, 60), out _);
        Bind(bindings, "joinRoomButton", join);

        // 邀请好友对战：一键建房 + 打开邀请面板（P4 新增）
        var invite = CreateStyledButton(panel.transform, "InviteButton", "邀请好友对战", ButtonTone.Ghost,
            new Vector2(0, -96), new Vector2(380, 54), out _);
        Bind(bindings, "inviteButton", invite);

        CreateDivider(panel.transform, true, 116f, 28f);

        var myIp = CreateText(panel.transform, "MyIpText", string.Empty, 13, GoldDim, TextAlignmentOptions.BottomLeft,
            new Vector2(28, 58), new Vector2(700, 22),
            new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0));
        Bind(bindings, "myIpText", myIp);

        var status = CreateText(panel.transform, "StatusText", string.Empty, 15, Muted, TextAlignmentOptions.BottomLeft,
            new Vector2(28, 18), new Vector2(700, 40),
            new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0));
        Bind(bindings, "statusText", status);

        FinishPrefab(root, BattleUiDir + "/BattleLobbyView.prefab", bindings, typeof(BattleLobbyView));
    }

    // =====================================================================
    // 加入房间
    // =====================================================================
    private static void BuildJoinRoomView()
    {
        var root = CreateRoot("JoinRoomView");

        CreateBackdrop(root.transform, 0.30f);

        var panel = CreateUI("Panel", root.transform, Center(), Center(), CenterPivot(), Vector2.zero, new Vector2(600, 400));
        var panelImage = AddImage(panel, PanelBg);
        ApplyNineSlice(panelImage, PanelSpritePath, PanelBg);
        AddShadow(panel.GetComponent<RectTransform>());
        AddBorder(panel.GetComponent<RectTransform>(), GoldDim, 1.5f);

        var bindings = new List<CtrlItemData>();
        Bind(bindings, "panel", panel.GetComponent<RectTransform>());

        var title = CreateAccentTitle(panel.transform, "TitleText", "加入房间", new Vector2(28f, -24f),
            new Vector2(300f, 40f), 30f);
        Bind(bindings, "titleText", title);

        var hint = CreateText(panel.transform, "HintText", "输入房主 IP（端口固定 7777）", 15, Muted,
            TextAlignmentOptions.TopLeft, new Vector2(44, -74), new Vector2(500, 24),
            new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));
        Bind(bindings, "hintText", hint);

        CreateText(panel.transform, "IpLabel", "房主 IP", 14, Gold, TextAlignmentOptions.TopLeft,
            new Vector2(44, -112), new Vector2(200, 22),
            new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));

        var input = CreateInputField(panel.transform, "IpInput", "127.0.0.1",
            new Vector2(0, 15), new Vector2(500, 60));
        var inputImage = input.GetComponent<Image>();
        ApplyNineSlice(inputImage, PanelSpritePath, Deep);
        AddBorder(input.GetComponent<RectTransform>(), GoldDim, 1.5f, ButtonSpritePath);
        Bind(bindings, "ipInput", input);

        var back = CreateStyledButton(panel.transform, "BackButton", "返回", ButtonTone.Ghost,
            new Vector2(-118, -100), new Vector2(210, 56), out _);
        Bind(bindings, "backButton", back);

        var join = CreateStyledButton(panel.transform, "JoinButton", "加入", ButtonTone.Primary,
            new Vector2(118, -100), new Vector2(210, 56), out _);
        Bind(bindings, "joinButton", join);

        CreateDivider(panel.transform, true, 64f, 28f);

        var status = CreateText(panel.transform, "StatusText", string.Empty, 15, Muted,
            TextAlignmentOptions.BottomLeft, new Vector2(28, 18), new Vector2(544, 36),
            new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0));
        Bind(bindings, "statusText", status);

        FinishPrefab(root, BattleUiDir + "/JoinRoomView.prefab", bindings, typeof(JoinRoomView));
    }

    // =====================================================================
    // 对战房间
    // =====================================================================
    private static void BuildRoomView()
    {
        var root = CreateRoot("BattleRoomView");

        // 全屏深色底：房间是整屏界面，不再套一层小窗（对齐 compete 的 room）
        CreateBackdrop(root.transform, 0.22f);

        var panel = CreateUI("Panel", root.transform, Vector2.zero, Vector2.one, CenterPivot(), Vector2.zero, Vector2.zero);
        var panelRt = panel.GetComponent<RectTransform>();
        panelRt.offsetMin = Vector2.zero;
        panelRt.offsetMax = Vector2.zero;

        var bindings = new List<CtrlItemData>();
        Bind(bindings, "panel", panelRt);

        // ---- 顶部标题/说明 ----
        var title = CreateAccentTitle(panel.transform, "TitleText", "对战房间", new Vector2(44f, -28f),
            new Vector2(520f, 44f), 34f);
        Bind(bindings, "titleText", title);

        var subtitle = CreateText(panel.transform, "SubtitleText", "选择角色 → 准备，双方就绪后自动进入战斗", 15,
            Muted, TextAlignmentOptions.TopLeft,
            new Vector2(60, -80), new Vector2(900, 24), new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));

        CreateDivider(panel.transform, false, -112f, 44f);

        // 右上操作：邀请 / 退出 / 关闭
        var close = CreateStyledButton(panel.transform, "CloseButton", "×", ButtonTone.Danger,
            new Vector2(-20, -20), new Vector2(40, 40), out var closeLabel,
            new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1));
        closeLabel.fontSize = 24f;
        Bind(bindings, "closeButton", close);

        var exit = CreateStyledButton(panel.transform, "ExitButton", "退出房间", ButtonTone.Danger,
            new Vector2(-72, -20), new Vector2(150, 52), out _,
            new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1));
        Bind(bindings, "exitButton", exit);

        // 房间内邀请好友（只有房主可见，客机由代码隐藏）
        var invite = CreateStyledButton(panel.transform, "InviteButton", "邀请好友", ButtonTone.Secondary,
            new Vector2(-234, -20), new Vector2(170, 52), out _,
            new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1));
        Bind(bindings, "inviteButton", invite);

        // ---- 双方大预览（左我方 / 右对手）----
        var selfCard = BuildRoomCard(panel.transform, "SelfCard", "我方", -450f, true);
        Bind(bindings, "selfCard", selfCard.root);
        Bind(bindings, "selfPreview", selfCard.preview);
        Bind(bindings, "selfNameText", selfCard.name);
        Bind(bindings, "selfPrepareText", selfCard.prepare);
        Bind(bindings, "selfEmptyText", selfCard.empty);

        var enemyCard = BuildRoomCard(panel.transform, "EnemyCard", "对手", 450f, false);
        Bind(bindings, "enemyCard", enemyCard.root);
        Bind(bindings, "enemyPreview", enemyCard.preview);
        Bind(bindings, "enemyNameText", enemyCard.name);
        Bind(bindings, "enemyPlayerText", enemyCard.player);
        Bind(bindings, "enemyPrepareText", enemyCard.prepare);
        Bind(bindings, "enemyEmptyText", enemyCard.empty);
        enemyCard.preview.gameObject.SetActive(false);
        enemyCard.prepare.transform.parent.gameObject.SetActive(false);

        // ---- 中央 VS 徽章 ----
        var vsBadge = CreateUI("VsBadge", panel.transform, Center(), Center(), CenterPivot(),
            new Vector2(0, 150), new Vector2(130, 130));
        var vsOuter = AddImage(vsBadge, Gold);
        var circleSprite = AssetDatabase.LoadAssetAtPath<Sprite>(CircleSpritePath);
        if (circleSprite != null)
        {
            vsOuter.sprite = circleSprite;
            vsOuter.type = Image.Type.Simple;
        }
        vsOuter.raycastTarget = false;

        var vsInner = CreateUI("Inner", vsBadge.transform, Center(), Center(), CenterPivot(),
            Vector2.zero, new Vector2(116, 116));
        var vsInnerImage = AddImage(vsInner, Raised);
        if (circleSprite != null)
        {
            vsInnerImage.sprite = circleSprite;
            vsInnerImage.type = Image.Type.Simple;
        }
        vsInnerImage.raycastTarget = false;

        var vs = CreateText(vsInner.transform, "VsText", "VS", 44, Gold, TextAlignmentOptions.Center,
            Vector2.zero, Vector2.zero, Vector2.zero, Vector2.one);
        vs.fontStyle = FontStyles.Bold;

        // ---- 底部：选人 / 准备 ----
        var anbi = CreateStyledButton(panel.transform, "AnbiButton", "安比", ButtonTone.Secondary,
            new Vector2(-600, -430), new Vector2(240, 140), out var anbiLabel);
        anbiLabel.fontSize = 30f;
        AddSelectedMark(anbi);
        Bind(bindings, "anbiButton", anbi);

        var ellen = CreateStyledButton(panel.transform, "EllenButton", "艾莲", ButtonTone.Secondary,
            new Vector2(-340, -430), new Vector2(240, 140), out var ellenLabel);
        ellenLabel.fontSize = 30f;
        AddSelectedMark(ellen);
        Bind(bindings, "ellenButton", ellen);

        var ready = CreateStyledButton(panel.transform, "ReadyButton", "准备", ButtonTone.Primary,
            new Vector2(120, -430), new Vector2(300, 92), out var readyLabel);
        readyLabel.fontSize = 30f;
        Bind(bindings, "readyButton", ready);
        Bind(bindings, "readyButtonLabel", readyLabel);

        // 房主专属“开始游戏”：双方准备后可点（客户端隐藏）
        var startBtn = CreateStyledButton(panel.transform, "StartButton", "开始游戏", ButtonTone.Primary,
            new Vector2(470, -430), new Vector2(240, 92), out var startLabel);
        startLabel.fontSize = 30f;
        Bind(bindings, "startButton", startBtn);
        Bind(bindings, "startButtonLabel", startLabel);

        // ---- 页脚 ----
        var footer = CreateUI("Footer", panel.transform, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0.5f, 0),
            Vector2.zero, new Vector2(0, 56));
        var footerImage = AddImage(footer, new Color(0.976f, 0.961f, 0.925f, 0.72f));
        footerImage.raycastTarget = false;
        // 页脚必须在按钮之前绘制，否则会盖住角色/准备按钮的下沿
        footer.transform.SetAsFirstSibling();
        CreateDivider(panel.transform, true, 56f, 0f);

        var ip = CreateText(panel.transform, "IpText", string.Empty, 14, Muted, TextAlignmentOptions.BottomRight,
            new Vector2(-44, 24), new Vector2(620, 26), new Vector2(1, 0), new Vector2(1, 0), new Vector2(1, 0));
        Bind(bindings, "ipText", ip);

        var status = CreateText(panel.transform, "StatusText", string.Empty, 15, Muted, TextAlignmentOptions.Bottom,
            new Vector2(0, 24), new Vector2(900, 26), new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0));
        Bind(bindings, "statusText", status);

        // ---- 居中飘字 ----
        var toast = CreateUI("Toast", panel.transform, Center(), Center(), CenterPivot(), new Vector2(0, 330),
            new Vector2(640, 56));
        var toastImage = AddImage(toast, PanelBg);
        ApplyNineSlice(toastImage, PanelSpritePath, PanelBg);
        AddBorder(toast.GetComponent<RectTransform>(), GoldDim, 1.2f, ButtonSpritePath);
        var toastGroup = toast.gameObject.AddComponent<CanvasGroup>();
        toastGroup.alpha = 0f;
        toastGroup.blocksRaycasts = false;
        toastGroup.interactable = false;
        Bind(bindings, "toastGroup", toastGroup);

        var toastText = CreateText(toast.transform, "ToastText", string.Empty, 20, Cream, TextAlignmentOptions.Center,
            Vector2.zero, Vector2.zero, Vector2.zero, Vector2.one);
        var toastTextRect = toastText.rectTransform;
        toastTextRect.offsetMin = new Vector2(16, 6);
        toastTextRect.offsetMax = new Vector2(-16, -6);
        Bind(bindings, "toastText", toastText);

        FinishPrefab(root, BattleUiDir + "/BattleRoomView.prefab", bindings, typeof(BattleRoomView));
    }

    private struct RoomCardParts
    {
        public RectTransform root;
        public RawImage preview;
        public TMP_Text name;
        public TMP_Text player;
        public TMP_Text prepare;
        public TMP_Text empty;
    }

    /// <summary>房间里的单侧大预览卡：描边底板 + 预览井 + 名字 + 准备胶囊 + 空态提示</summary>
    private static RoomCardParts BuildRoomCard(Transform parent, string name, string header, float x, bool isSelf)
    {
        var card = CreateUI(name, parent, Center(), Center(), CenterPivot(), new Vector2(x, 30), new Vector2(660, 740));
        var cardRt = card.GetComponent<RectTransform>();
        var cardImage = AddImage(card, Raised);
        ApplyNineSlice(cardImage, PanelSpritePath, Raised);
        AddShadow(cardRt);
        AddBorder(cardRt, GoldDim, 1.5f);

        AddCircle(card.transform, "HeaderDot", new Vector2(28, -32), 10f, isSelf ? Accent : Danger,
            new Vector2(0, 1), new Vector2(0, 1));
        CreateText(card.transform, "HeaderText", header, 22, isSelf ? Accent : Danger, TextAlignmentOptions.TopLeft,
            new Vector2(46, -22), new Vector2(220, 30), new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));

        // 准备状态胶囊：底图在父节点，文字是子节点（一个 GameObject 只能挂一个 Graphic）。
        // BattleRoomView 通过 SetPrepareVisible 同时切换两者。
        var prepareBadge = CreateUI("PrepareBadge", card.transform, new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1),
            new Vector2(-24, -18), new Vector2(130, 34));
        var prepareBg = AddImage(prepareBadge, new Color(0.788f, 0.647f, 0.392f, 0.18f));
        ApplyNineSlice(prepareBg, PanelSpritePath, new Color(0.788f, 0.647f, 0.392f, 0.18f));
        AddBorder(prepareBadge.GetComponent<RectTransform>(), GoldDim, 1f, ButtonSpritePath);
        prepareBg.raycastTarget = false;

        var prepare = CreateText(prepareBadge.transform, "PrepareText", "未准备", 16, Cream,
            TextAlignmentOptions.Center, Vector2.zero, Vector2.zero, Vector2.zero, Vector2.one);
        prepare.margin = new Vector4(8, 2, 8, 2);
        prepare.enableWordWrapping = false;
        prepare.overflowMode = TextOverflowModes.Overflow;

        // 预览井：内凹深色底 + 细描边，和卡片拉开层次
        var well = CreateUI("PreviewWell", card.transform, Center(), Center(), CenterPivot(),
            new Vector2(0, 10), new Vector2(620, 560));
        var wellImage = AddImage(well, Deep);
        ApplyNineSlice(wellImage, PanelSpritePath, Deep);
        AddBorder(well.GetComponent<RectTransform>(), Line, 1f);

        var preview = CreateRawImage(card.transform, "Preview", new Vector2(0, 10), new Vector2(600, 540));

        var empty = CreateText(card.transform, "EmptyText", isSelf ? "选择角色后显示 3D 预览" : "等待对手选择",
            18, Muted, TextAlignmentOptions.Center,
            new Vector2(0, 10), new Vector2(600, 540), Center(), Center());

        var dividerGo = CreateUI("NameDivider", card.transform, new Vector2(0.5f, 0), new Vector2(0.5f, 0),
            new Vector2(0.5f, 0), new Vector2(0, 94), new Vector2(560, 1));
        var dividerImage = AddImage(dividerGo, Line);
        dividerImage.raycastTarget = false;

        var nameText = CreateText(card.transform, "NameText", "未选择", 30, Cream, TextAlignmentOptions.Center,
            new Vector2(0, 56), new Vector2(600, 38), new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0));
        nameText.fontStyle = FontStyles.Bold;

        // 玩家昵称（我方固定"我"；对手由 BattleRoomView 用邀请结果里的名字回填）
        var playerText = CreateText(card.transform, "PlayerNameText", isSelf ? "我" : "等待好友加入…",
            17, Muted, TextAlignmentOptions.Center,
            new Vector2(0, 24), new Vector2(600, 22), new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0.5f, 0));

        return new RoomCardParts
        {
            root = cardRt,
            preview = preview,
            name = nameText,
            player = playerText,
            prepare = prepare,
            empty = empty
        };
    }

    // =====================================================================
    // 邀请好友面板
    // =====================================================================
    private static void BuildInviteView()
    {
        var root = CreateRoot("BattleInviteView");

        CreateBackdrop(root.transform, 0.55f);

        var panel = CreateUI("Panel", root.transform, Center(), Center(), CenterPivot(), Vector2.zero, new Vector2(720, 560));
        AddImage(panel, PanelBg);

        var bindings = new List<CtrlItemData>();
        Bind(bindings, "panel", panel.GetComponent<RectTransform>());

        var title = CreateText(panel.transform, "TitleText", "邀请好友对战", 30, Primary, TextAlignmentOptions.TopLeft,
            new Vector2(28, -24), new Vector2(360, 40), new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));
        Bind(bindings, "titleText", title);

        var subtitle = CreateText(panel.transform, "SubtitleText", "选择在线好友发起 1v1 邀请", 16, Secondary,
            TextAlignmentOptions.TopLeft, new Vector2(28, -66), new Vector2(520, 24),
            new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));
        Bind(bindings, "subtitleText", subtitle);

        var address = CreateText(panel.transform, "AddressText", string.Empty, 14, Accent, TextAlignmentOptions.TopLeft,
            new Vector2(28, -92), new Vector2(560, 22), new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1));
        Bind(bindings, "addressText", address);

        var close = CreateButton(panel.transform, "CloseButton", "×", Red,
            new Vector2(-16, -16), new Vector2(44, 44), out _,
            new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 1));
        Bind(bindings, "closeButton", close);

        // ---- 好友列表（ScrollRect + 垂直布局 + 内容随行数撑高）----
        var scrollGo = CreateUI("ListScroll", panel.transform, Center(), Center(), CenterPivot(),
            new Vector2(0, -6), new Vector2(664, 320));
        var scroll = scrollGo.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 30f;

        var viewport = CreateUI("Viewport", scrollGo.transform, Vector2.zero, Vector2.one, CenterPivot(),
            Vector2.zero, Vector2.zero);
        var viewportRect = viewport.GetComponent<RectTransform>();
        viewportRect.offsetMin = Vector2.zero;
        viewportRect.offsetMax = Vector2.zero;
        viewport.AddComponent<RectMask2D>();
        // 透明 Image 让 ScrollRect 能吃到拖拽（没有 Graphic 的 RectTransform 收不到事件）
        var viewportImage = viewport.AddComponent<Image>();
        viewportImage.color = new Color(1f, 1f, 1f, 0f);
        scroll.viewport = viewportRect;

        var content = CreateUI("ListContent", viewport.transform,
            new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0, 0));
        var contentRect = content.GetComponent<RectTransform>();
        contentRect.pivot = new Vector2(0.5f, 1f);
        contentRect.anchoredPosition = Vector2.zero;
        contentRect.sizeDelta = new Vector2(0, 0);

        var layout = content.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(0, 0, 0, 0);
        layout.spacing = 6;
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = false;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        var fitter = content.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        scroll.content = contentRect;

        // ---- 行模板（常驻 inactive，运行时克隆）----
        var row = CreateUI("RowTemplate", content.transform,
            new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0.5f, 1), Vector2.zero, new Vector2(664, 64));
        AddImage(row, SubBg);
        var rowLayout = row.AddComponent<LayoutElement>();
        rowLayout.preferredHeight = 64;
        rowLayout.minHeight = 64;

        CreateText(row.transform, "NameText", "好友名字", 20, Primary, TextAlignmentOptions.MidlineLeft,
            new Vector2(18, 0), new Vector2(300, 30), new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f));

        CreateText(row.transform, "StateText", "在线", 16, Green, TextAlignmentOptions.MidlineRight,
            new Vector2(-136, 0), new Vector2(90, 26), new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(1, 0.5f));

        CreateButton(row.transform, "InviteButton", "邀请", Steel,
            new Vector2(-14, 0), new Vector2(110, 44), out _,
            new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(1, 0.5f));

        Bind(bindings, "listContent", contentRect);
        Bind(bindings, "rowTemplate", row);
        row.SetActive(false);

        // ---- 底部：待回应 / 取消 / 状态 ----
        var pending = CreateText(panel.transform, "PendingText", string.Empty, 16, Accent, TextAlignmentOptions.BottomLeft,
            new Vector2(28, 62), new Vector2(430, 24), new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0));
        Bind(bindings, "pendingText", pending);

        var cancelInvite = CreateButton(panel.transform, "CancelInviteButton", "取消邀请", Steel,
            new Vector2(-24, 56), new Vector2(150, 44), out _,
            new Vector2(1, 0), new Vector2(1, 0), new Vector2(1, 0));
        Bind(bindings, "cancelInviteButton", cancelInvite);

        var status = CreateText(panel.transform, "StatusText", string.Empty, 15, Secondary, TextAlignmentOptions.BottomLeft,
            new Vector2(28, 24), new Vector2(660, 24), new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0));
        Bind(bindings, "statusText", status);

        FinishPrefab(root, BattleUiDir + "/BattleInviteView.prefab", bindings, typeof(BattleInviteView));
    }

    // =====================================================================
    // 收到邀请弹窗（右下角浮卡，三角洲式：不铺遮罩、滑入、限时自动收）
    // =====================================================================
    [MenuItem("Tools/Battle/只重建邀请弹窗", priority = 2)]
    public static void BuildInvitePopupOnly()
    {
        _font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
        EnsureFolder(BattleUiDir);

        BuildInvitePopupView();

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[BattleBuilder] 邀请弹窗重建完成");
    }

    private static void BuildInvitePopupView()
    {
        var root = CreateRoot("BattleInvitePopupView");

        // 卡片贴在屏幕右下角；不给全屏遮罩，卡片之外的点击照常落到下层界面
        const float cardW = 400f;
        const float cardH = 140f;
        var restPos = new Vector2(-32f, 32f);
        var offPos = new Vector2(cardW + 80f, 32f);

        var panel = CreateUI("Panel", root.transform, new Vector2(1, 0), new Vector2(1, 0), new Vector2(1, 0),
            restPos, new Vector2(cardW, cardH));
        AddImage(panel, PanelBg);

        var bindings = new List<CtrlItemData>();
        Bind(bindings, "panel", panel.GetComponent<RectTransform>());

        // 左侧强调条撑起"浮窗"轮廓
        var accentBar = CreateUI("AccentBar", panel.transform, Vector2.zero, Vector2.zero, Vector2.zero,
            Vector2.zero, new Vector2(5, cardH));
        AddImage(accentBar, Green);

        var title = CreateText(panel.transform, "TitleText", "对战邀请", 13, Accent, TextAlignmentOptions.MidlineLeft,
            new Vector2(20, 104), new Vector2(220, 20), Vector2.zero, Vector2.zero);
        Bind(bindings, "titleText", title);

        var fromName = CreateText(panel.transform, "FromNameText", "玩家", 22, Primary, TextAlignmentOptions.MidlineLeft,
            new Vector2(20, 72), new Vector2(250, 30), Vector2.zero, Vector2.zero);
        Bind(bindings, "fromNameText", fromName);

        var message = CreateText(panel.transform, "MessageText", "邀请你进行 1v1 对战", 13, Secondary,
            TextAlignmentOptions.MidlineLeft, new Vector2(20, 50), new Vector2(250, 20), Vector2.zero, Vector2.zero);
        Bind(bindings, "messageText", message);

        var countdown = CreateText(panel.transform, "CountdownText", "剩余 60 秒", 12, Accent,
            TextAlignmentOptions.MidlineLeft, new Vector2(20, 28), new Vector2(250, 18), Vector2.zero, Vector2.zero);
        Bind(bindings, "countdownText", countdown);

        // 进度条：底槽 + 左侧填充。视图层只 DOTween 缩 fill 的 scaleX，不需要任何贴图/Image.Filled
        var bar = CreateUI("CountdownBar", panel.transform, Vector2.zero, Vector2.zero, Vector2.zero,
            new Vector2(20, 16), new Vector2(250, 4));
        AddImage(bar, new Color(0.098f, 0.110f, 0.129f, 1f));

        var fill = CreateUI("Fill", bar.transform, Vector2.zero, Vector2.one, new Vector2(0, 0.5f),
            Vector2.zero, Vector2.zero);
        var fillRect = fill.GetComponent<RectTransform>();
        fillRect.offsetMin = Vector2.zero;
        fillRect.offsetMax = Vector2.zero;
        AddImage(fill, Green);
        Bind(bindings, "countdownFill", fillRect);

        var status = CreateText(panel.transform, "StatusText", string.Empty, 11, Secondary, TextAlignmentOptions.MidlineLeft,
            new Vector2(20, 0), new Vector2(250, 14), Vector2.zero, Vector2.zero);
        Bind(bindings, "statusText", status);

        var accept = CreateButton(panel.transform, "AcceptButton", "同意", Green,
            new Vector2(288, 74), new Vector2(96, 36), out var acceptLabel, Vector2.zero, Vector2.zero);
        Bind(bindings, "acceptButton", accept);
        Bind(bindings, "acceptLabel", acceptLabel);

        var decline = CreateButton(panel.transform, "DeclineButton", "拒绝", Steel,
            new Vector2(288, 30), new Vector2(96, 36), out var declineLabel, Vector2.zero, Vector2.zero);
        Bind(bindings, "declineButton", decline);
        Bind(bindings, "declineLabel", declineLabel);

        // 开/关动画交给框架 UIViewAnim（UIViewController 会 await）：滑入 + 淡入 + 轻微回弹同时播
        var anim = root.AddComponent<UIViewAnim>();
        anim.targets.Add(panel.transform);
        anim.openSteps = new List<UIViewAnimStep>
        {
            new UIViewAnimStep
            {
                targetIndex = 0, prop = UIAnimPropType.AnchoredPos,
                fromPos = offPos, toPos = restPos,
                duration = 0.24f, ease = Ease.OutCubic, unscaledTime = true,
            },
            new UIViewAnimStep
            {
                prop = UIAnimPropType.Alpha, fromAlpha = 0f, toAlpha = 1f,
                duration = 0.16f, ease = Ease.OutQuad, unscaledTime = true, joinWithPrevious = true,
            },
            new UIViewAnimStep
            {
                targetIndex = 0, prop = UIAnimPropType.LocalScale,
                fromScale = Vector3.one * 0.92f, toScale = Vector3.one,
                duration = 0.30f, ease = Ease.OutBack, unscaledTime = true, joinWithPrevious = true,
            },
        };
        anim.closeSteps = new List<UIViewAnimStep>
        {
            new UIViewAnimStep
            {
                targetIndex = 0, prop = UIAnimPropType.AnchoredPos,
                fromPos = restPos, toPos = offPos,
                duration = 0.18f, ease = Ease.InCubic, unscaledTime = true,
            },
            new UIViewAnimStep
            {
                prop = UIAnimPropType.Alpha, fromAlpha = 1f, toAlpha = 0f,
                duration = 0.16f, ease = Ease.InQuad, unscaledTime = true, joinWithPrevious = true,
            },
        };

        FinishPrefab(root, BattleUiDir + "/BattleInvitePopupView.prefab", bindings, typeof(BattleInvitePopupView));
    }

    // =====================================================================
    // 联机预制体
    // =====================================================================
    private static void BuildNetworkingPrefab()
    {
        string path = BattleResDir + "/BattleNetworking.prefab";
        AssetDatabase.DeleteAsset(path);

        var go = new GameObject("BattleNetworking");
        var manager = go.AddComponent<Unity.Netcode.NetworkManager>();
        var transport = go.AddComponent<Unity.Netcode.Transports.UTP.UnityTransport>();
        var roomState = go.AddComponent<RoomState>();

        if (manager.NetworkConfig == null)
            manager.NetworkConfig = new Unity.Netcode.NetworkConfig();
        manager.NetworkConfig.EnableSceneManagement = true;
        manager.NetworkConfig.NetworkTransport = transport;
        EditorUtility.SetDirty(manager);

        transport.ConnectionData.Address = BattleNetworkLauncher.DefaultAddress;
        transport.ConnectionData.Port = BattleNetworkLauncher.DefaultPort;
        EditorUtility.SetDirty(transport);

        var roomSo = new SerializedObject(roomState);
        var heroCount = roomSo.FindProperty("HeroCount");
        if (heroCount != null) heroCount.intValue = BattleHeroCatalog.Count;
        var sceneName = roomSo.FindProperty("BattleSceneName");
        if (sceneName != null) sceneName.stringValue = "multiplecompete";
        var loadScene = roomSo.FindProperty("LoadBattleSceneWhenReady");
        if (loadScene != null) loadScene.boolValue = false;
        var hostStart = roomSo.FindProperty("RequireHostStart");
        if (hostStart != null) hostStart.boolValue = true;
        roomSo.ApplyModifiedPropertiesWithoutUndo();

        PrefabUtility.SaveAsPrefabAsset(go, path);
        UnityEngine.Object.DestroyImmediate(go);
        Debug.Log($"[BattleBuilder] 联机预制体 -> {path}");
    }

    // =====================================================================
    // 选人预览占位模型（P7 换成安比/艾莲）
    // =====================================================================
    private static void BuildHeroPreviewPrefabs()
    {
        BuildHeroPreviewFromPrefab("Assets/Content/Resource/人物/Real/安比test.prefab", HeroPreviewDir + "/AnbiPreview.prefab");
        BuildHeroPreviewFromPrefab("Assets/Content/Resource/人物/Real/le_Size02_Ellen_Ani_Idle (1).prefab", HeroPreviewDir + "/EllenPreview.prefab");
    }

    /// <summary>直接用现成的角色预制体做选人预览（compete 房间用的就是 Real 下这两个模型）</summary>
    private static void BuildHeroPreviewFromPrefab(string srcPrefabPath, string prefabPath)
    {
        var src = AssetDatabase.LoadAssetAtPath<GameObject>(srcPrefabPath);
        if (src == null)
        {
            Debug.LogError($"[BattleBuilder] 找不到预览模型 {srcPrefabPath}");
            return;
        }

        AssetDatabase.DeleteAsset(prefabPath);
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(src);
        try
        {
            PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(instance);
        }
        Debug.Log($"[BattleBuilder] 选人预览 -> {prefabPath}（源 {srcPrefabPath}）");
    }

    private static void BuildHeroPreview(string fbxPath, string prefabPath)
    {
        var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
        if (fbx == null)
        {
            Debug.LogError($"[BattleBuilder] 找不到预览模型 {fbxPath}");
            return;
        }

        AssetDatabase.DeleteAsset(prefabPath);
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
        try
        {
            foreach (var cam in instance.GetComponentsInChildren<Camera>(true)) UnityEngine.Object.DestroyImmediate(cam);
            foreach (var light in instance.GetComponentsInChildren<Light>(true)) UnityEngine.Object.DestroyImmediate(light);
            foreach (var listener in instance.GetComponentsInChildren<AudioListener>(true)) UnityEngine.Object.DestroyImmediate(listener);

            PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
            Debug.Log($"[BattleBuilder] 预览模型 -> {prefabPath}（源：{fbxPath}）");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(instance);
        }
    }

    // =====================================================================
    // zhuye 入口按钮
    // =====================================================================
    private static void BuildZhuyeEntryButton()
    {
        if (!UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().path.EndsWith("zhuye.scene"))
        {
            Debug.LogWarning("[BattleBuilder] 当前活动场景不是 zhuye.scene，跳过入口按钮");
            return;
        }

        var old = GameObject.Find("battle");
        if (old != null) UnityEngine.Object.DestroyImmediate(old);

        var fuben = GameObject.Find("fuben");
        Camera worldCamera = null;
        int sortingOrder = 10;
        float planeDistance = 100f;
        if (fuben != null)
        {
            var c = fuben.GetComponent<Canvas>();
            if (c != null)
            {
                worldCamera = c.worldCamera;
                sortingOrder = c.sortingOrder;
                planeDistance = c.planeDistance;
            }
        }

        var canvasGo = new GameObject("battle", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = worldCamera;
        canvas.planeDistance = planeDistance;
        canvas.sortingOrder = sortingOrder;

        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.scaleFactor = 1f;

        var button = CreateButton(canvasGo.transform, "BattleEntryButton", "对战", Green,
            new Vector2(-1179.6f, -24f), new Vector2(110, 44), out _,
            new Vector2(1, 1), new Vector2(1, 1));
        button.gameObject.AddComponent<BattleEntryButton>();

        canvasGo.SetLayerRecursively(Layer.UI);

        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();
        Debug.Log("[BattleBuilder] zhuye 入口按钮已创建（对战，x=-1179.6）");
    }

    // =====================================================================
    // 基础构件
    // =====================================================================

    private enum ButtonTone { Primary, Secondary, Ghost, Danger }

    /// <summary>套 9 宫格贴图并保留指定颜色（ApplyRounded 会强制白色，不适合有色调的按钮）</summary>
    private static void ApplyNineSlice(Image image, string spritePath, Color tint, bool fillCenter = true)
    {
        if (image == null) return;
        var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
        if (sprite != null)
        {
            image.sprite = sprite;
            image.type = Image.Type.Sliced;
            image.fillCenter = fillCenter;
        }
        image.color = tint;
    }

    /// <summary>在内缩区域画一圈 9 宫格描边（fillCenter=false，不会盖住内容）</summary>
    private static void AddBorder(RectTransform parent, Color color, float inset, string spritePath = null)
    {
        var go = CreateUI("Border", parent, Vector2.zero, Vector2.one, CenterPivot(), Vector2.zero, Vector2.zero);
        var rt = go.GetComponent<RectTransform>();
        rt.offsetMin = new Vector2(inset, inset);
        rt.offsetMax = new Vector2(-inset, -inset);
        var image = AddImage(go, color);
        // 面板用 32px 边框、按钮/输入框用 12px 边框：混用会把细控件画出一条粗横带
        ApplyNineSlice(image, spritePath ?? PanelSpritePath, color, fillCenter: false);
        image.raycastTarget = false;
    }

    /// <summary>给面板/卡片加一层下沉投影（放在目标之前，不参与射线）</summary>
    private static void AddShadow(RectTransform target)
    {
        if (target == null) return;
        var go = CreateUI(target.name + "Shadow", target.parent, Vector2.zero, Vector2.one, CenterPivot(), Vector2.zero, Vector2.zero);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = target.anchorMin;
        rt.anchorMax = target.anchorMax;
        rt.pivot = target.pivot;
        rt.anchoredPosition = target.anchoredPosition + new Vector2(0f, -8f);
        rt.sizeDelta = target.sizeDelta + new Vector2(12f, 12f);
        go.transform.SetSiblingIndex(target.GetSiblingIndex());

        var image = AddImage(go, new Color(0.25f, 0.20f, 0.14f, 0.18f));
        var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(ShadowSpritePath);
        if (sprite != null)
        {
            image.sprite = sprite;
            image.type = Image.Type.Sliced;
        }
        image.raycastTarget = false;
    }

    private static Image AddCircle(Transform parent, string name, Vector2 pos, float size, Color color,
        Vector2? anchorMin = null, Vector2? pivot = null)
    {
        var go = CreateUI(name, parent, anchorMin ?? Center(), anchorMin ?? Center(), pivot ?? CenterPivot(), pos, new Vector2(size, size));
        var image = AddImage(go, color);
        var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(CircleSpritePath);
        if (sprite != null)
        {
            image.sprite = sprite;
            image.type = Image.Type.Simple;
        }
        image.raycastTarget = false;
        return image;
    }

    /// <summary>从顶部或底部画一条通栏分隔线</summary>
    private static void CreateDivider(Transform parent, bool fromBottom, float y, float inset)
    {
        Vector2 anchor = fromBottom ? new Vector2(0f, 0f) : new Vector2(0f, 1f);
        Vector2 pivot = fromBottom ? new Vector2(0.5f, 0f) : new Vector2(0.5f, 1f);
        var go = CreateUI("Divider", parent, anchor, new Vector2(1f, anchor.y), pivot, new Vector2(0f, y), new Vector2(-inset * 2f, 1f));
        var image = AddImage(go, Line);
        image.raycastTarget = false;
    }

    /// <summary>金色竖条 + 标题（标题与副标题共用左基线）</summary>
    private static TMP_Text CreateAccentTitle(Transform parent, string name, string content, Vector2 pos,
        Vector2 size, float fontSize)
    {
        var bar = CreateUI("AccentBar", parent, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
            new Vector2(pos.x, pos.y + 4f), new Vector2(4f, size.y - 8f));
        var barImage = AddImage(bar, Gold);
        barImage.raycastTarget = false;

        var title = CreateText(parent, name, content, fontSize, Cream, TextAlignmentOptions.TopLeft,
            new Vector2(pos.x + 16f, pos.y), size, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f));
        title.fontStyle = FontStyles.Bold;
        return title;
    }

    /// <summary>统一按钮：Primary 金色 / Secondary 描边 / Ghost 透明 / Danger 暗红</summary>
    private static Button CreateStyledButton(Transform parent, string name, string label, ButtonTone tone,
        Vector2 pos, Vector2 size, out TMP_Text labelText,
        Vector2? anchorMin = null, Vector2? anchorMax = null, Vector2? pivot = null)
    {
        Color baseColor;
        Color textColor;
        Color borderColor;
        switch (tone)
        {
            case ButtonTone.Primary:
                baseColor = new Color(0.925f, 0.835f, 0.647f, 1f);
                textColor = Primary;
                borderColor = Gold;
                break;
            case ButtonTone.Danger:
                baseColor = Danger; textColor = Color.white; borderColor = new Color(0.65f, 0.38f, 0.34f, 1f);
                break;
            case ButtonTone.Ghost:
                baseColor = new Color(1f, 1f, 1f, 0.35f); textColor = Secondary; borderColor = new Color(0.788f, 0.647f, 0.392f, 0.35f);
                break;
            default:
                baseColor = Raised; textColor = Primary; borderColor = GoldDim;
                break;
        }

        var go = CreateUI(name, parent, anchorMin ?? Center(), anchorMax ?? Center(), pivot ?? CenterPivot(), pos, size);
        var image = AddImage(go, baseColor);
        ApplyNineSlice(image, ButtonSpritePath, baseColor);
        image.raycastTarget = true;

        var button = go.AddComponent<Button>();
        button.targetGraphic = image;
        var colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(1f, 0.96f, 0.86f, 1f);
        colors.pressedColor = new Color(0.82f, 0.80f, 0.74f, 1f);
        colors.selectedColor = Color.white;
        colors.disabledColor = new Color(0.85f, 0.85f, 0.85f, 0.50f);
        colors.fadeDuration = 0.08f;
        button.colors = colors;

        AddBorder((RectTransform)go.transform, borderColor, 1.5f, ButtonSpritePath);

        float fontSize = size.y >= 80f ? 30f : (size.y >= 54f ? 24f : 22f);
        labelText = CreateText(go.transform, "Text (TMP)", label, fontSize, textColor, TextAlignmentOptions.Center,
            Vector2.zero, Vector2.zero, Vector2.zero, Vector2.one);
        var labelRect = labelText.rectTransform;
        labelRect.offsetMin = new Vector2(8f, 4f);
        labelRect.offsetMax = new Vector2(-8f, -4f);
        labelText.fontStyle = FontStyles.Bold;
        return button;
    }

    /// <summary>英雄卡右上角的选中标记（金色圆点，默认隐藏，由 BattleRoomView 切换）</summary>
    private static void AddSelectedMark(Button button)
    {
        if (button == null) return;
        var go = CreateUI("SelectedMark", button.transform, new Vector2(1f, 1f), new Vector2(1f, 1f),
            new Vector2(1f, 1f), new Vector2(-12f, -10f), new Vector2(20f, 20f));
        var image = AddImage(go, Gold);
        var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(CircleSpritePath);
        if (sprite != null)
        {
            image.sprite = sprite;
            image.type = Image.Type.Simple;
        }
        image.raycastTarget = false;
        go.SetActive(false);
    }

    private static GameObject CreateRoot(string name)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        rt.pivot = CenterPivot();
        return go;
    }

    private static GameObject CreateUI(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax,
        Vector2 pivot, Vector2 pos, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        return go;
    }

    private static Image AddImage(GameObject go, Color color)
    {
        // Tuanjie 的 uGUI 分叉里 CanvasRenderer 不是自动补的，程序化建图形必须自己加
        if (go.GetComponent<CanvasRenderer>() == null) go.AddComponent<CanvasRenderer>();
        var image = go.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = true;
        return image;
    }

    /// <summary>给 Image 套 9 宫格圆角贴图（找不到贴图就保持纯色）</summary>
    private static void ApplyRounded(Image image, string spritePath)
    {
        if (image == null || string.IsNullOrEmpty(spritePath)) return;
        var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
        if (sprite == null) return;
        image.sprite = sprite;
        image.type = Image.Type.Sliced;
        image.color = Color.white;
    }

    private static void CreateFullScreenImage(Transform parent, string name, Color color)
    {
        var go = CreateUI(name, parent, Vector2.zero, Vector2.one, CenterPivot(), Vector2.zero, Vector2.zero);
        var rt = go.GetComponent<RectTransform>();
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        AddImage(go, color);
    }

    /// <summary>
    /// 原神风全屏背景：明亮幻想广场（home_bg）+ 一层浅色薄雾。
    /// 背景不参与射线；薄雾用于压低建筑细节，保证前景浅色面板/深棕文字可读。
    /// </summary>
    private static void CreateBackdrop(Transform parent, float veilAlpha)
    {
        var go = CreateUI("Backdrop", parent, Vector2.zero, Vector2.one, CenterPivot(), Vector2.zero, Vector2.zero);
        var rt = go.GetComponent<RectTransform>();
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        var image = AddImage(go, Color.white);
        var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(BackdropSpritePath);
        if (sprite != null)
        {
            image.sprite = sprite;
            image.type = Image.Type.Simple;
            image.preserveAspect = false;
        }
        else
        {
            image.color = new Color(0.90f, 0.92f, 0.94f, 1f);
        }
        image.raycastTarget = false;
        go.transform.SetAsFirstSibling();

        var veil = CreateUI("BackdropVeil", parent, Vector2.zero, Vector2.one, CenterPivot(), Vector2.zero, Vector2.zero);
        var veilRt = veil.GetComponent<RectTransform>();
        veilRt.offsetMin = Vector2.zero;
        veilRt.offsetMax = Vector2.zero;
        var veilImage = AddImage(veil, new Color(0.98f, 0.96f, 0.92f, veilAlpha));
        veilImage.raycastTarget = false;
        veil.transform.SetSiblingIndex(1);
    }

    private static TMP_Text CreateText(Transform parent, string name, string content, float fontSize, Color color,
        TextAlignmentOptions alignment, Vector2 pos, Vector2 size, Vector2 anchorMin, Vector2 anchorMax,
        Vector2? pivot = null)
    {
        var go = CreateUI(name, parent, anchorMin, anchorMax, pivot ?? CenterPivot(), pos, size);
        if (go.GetComponent<CanvasRenderer>() == null) go.AddComponent<CanvasRenderer>();
        var text = go.AddComponent<TextMeshProUGUI>();
        if (_font != null) text.font = _font;
        text.text = content;
        text.fontSize = fontSize;
        text.color = color;
        text.alignment = alignment;
        text.raycastTarget = false;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Overflow;
        return text;
    }

    private static Button CreateButton(Transform parent, string name, string label, Color color,
        Vector2 pos, Vector2 size, out TMP_Text labelText)
        => CreateButton(parent, name, label, color, pos, size, out labelText, Center(), Center());

    private static Button CreateButton(Transform parent, string name, string label, Color color,
        Vector2 pos, Vector2 size, out TMP_Text labelText, Vector2 anchorMin, Vector2 anchorMax,
        Vector2? pivot = null)
    {
        var go = CreateUI(name, parent, anchorMin, anchorMax, pivot ?? CenterPivot(), pos, size);
        var image = AddImage(go, color);
        var button = go.AddComponent<Button>();
        button.targetGraphic = image;

        labelText = CreateText(go.transform, "Text (TMP)", label, 22, Color.white, TextAlignmentOptions.Center,
            Vector2.zero, Vector2.zero, Vector2.zero, Vector2.one);
        var rt = labelText.rectTransform;
        rt.offsetMin = new Vector2(6, 4);
        rt.offsetMax = new Vector2(-6, -4);
        return button;
    }

    private static RawImage CreateRawImage(Transform parent, string name, Vector2 pos, Vector2 size)
    {
        var go = CreateUI(name, parent, Center(), Center(), CenterPivot(), pos, size);
        if (go.GetComponent<CanvasRenderer>() == null) go.AddComponent<CanvasRenderer>();
        var raw = go.AddComponent<RawImage>();
        raw.color = Color.white;
        raw.raycastTarget = true;
        return raw;
    }

    private static TMP_InputField CreateInputField(Transform parent, string name, string placeholderText,
        Vector2 pos, Vector2 size)
    {
        var go = CreateUI(name, parent, Center(), Center(), CenterPivot(), pos, size);
        var background = AddImage(go, SubBg);

        var input = go.AddComponent<TMP_InputField>();
        input.targetGraphic = background;
        input.lineType = TMP_InputField.LineType.SingleLine;
        input.contentType = TMP_InputField.ContentType.Standard;
        input.characterLimit = 64;

        var area = CreateUI("Text Area", go.transform, Vector2.zero, Vector2.one, CenterPivot(), Vector2.zero, Vector2.zero);
        var areaRect = area.GetComponent<RectTransform>();
        areaRect.offsetMin = new Vector2(14, 8);
        areaRect.offsetMax = new Vector2(-14, -8);
        area.AddComponent<RectMask2D>();

        var text = CreateText(area.transform, "Text", string.Empty, 20, Primary, TextAlignmentOptions.MidlineLeft,
            Vector2.zero, Vector2.zero, Vector2.zero, Vector2.one);
        var textRect = text.rectTransform;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
        text.enableWordWrapping = false;

        var placeholder = CreateText(area.transform, "Placeholder", placeholderText, 20,
            new Color(0.545f, 0.506f, 0.443f, 0.45f), TextAlignmentOptions.MidlineLeft,
            Vector2.zero, Vector2.zero, Vector2.zero, Vector2.one);
        var placeholderRect = placeholder.rectTransform;
        placeholderRect.offsetMin = Vector2.zero;
        placeholderRect.offsetMax = Vector2.zero;

        input.textViewport = areaRect;
        input.textComponent = text;
        input.placeholder = placeholder;
        return input;
    }

    private static void Bind(List<CtrlItemData> bindings, string fieldName, UnityEngine.Object target)
    {
        string typeName;
        if (target is Button) typeName = "Button";
        else if (target is TMP_InputField) typeName = "TMP_InputField";
        else if (target is TextMeshProUGUI) typeName = "TextMeshProUGUI";
        else if (target is RawImage) typeName = "RawImage";
        else if (target is CanvasGroup) typeName = "CanvasGroup";
        else if (target is RectTransform) typeName = "RectTransform";
        else if (target is Image) typeName = "Image";
        else typeName = target != null ? target.GetType().Name : "GameObject";

        bindings.Add(new CtrlItemData
        {
            name = fieldName,
            type = typeName,
            targets = new UnityEngine.Object[] { target },
        });
    }

    private static void FinishPrefab(GameObject root, string path, List<CtrlItemData> bindings, System.Type viewType = null)
    {
        var data = root.AddComponent<UIControlData>();
        data.ctrlItemDatas = bindings;
        data.subUIItemDatas = new List<SubUIItemData>();

        // 视图脚本必须挂在根上：UIManager 按 UIConfig 的 uiType 解析并 GetComponent
        if (viewType != null && root.GetComponent(viewType) == null)
            root.AddComponent(viewType);

        AssetDatabase.DeleteAsset(path);
        PrefabUtility.SaveAsPrefabAsset(root, path);
        UnityEngine.Object.DestroyImmediate(root);
        Debug.Log($"[BattleBuilder] 面板预制体 -> {path}（绑定 {bindings.Count} 项）");
    }

    private static Vector2 Center() => new Vector2(0.5f, 0.5f);
    private static Vector2 CenterPivot() => new Vector2(0.5f, 0.5f);

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
        string leaf = Path.GetFileName(path);
        if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }
}
