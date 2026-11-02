using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

using Common.Network;

using DG.Tweening;

using TMPro;

/// <summary>
/// 阿福悬浮球（灵宝式主动提醒 · 一个 Agent 两个用法）
/// 服务端 AgentReminderService 检测事件（邮件附件未领取）→ Python 让同一个阿福主动说话 → 3107 推送；
/// 本组件是主动消息的展示端：全场景常驻（DontDestroyOnLoad）悬浮球 + 球旁消息气泡（7 秒自动消失）。
///
/// 画布：Bootstrap 单例自建（照 UiFx 先例）——sortingOrder=5900 高于框架全部 UI 层
/// （UILayer：Background 2000 … TopLayer 5000），低于 TipLayer(6000) 与 BlackMaskLayer(7000)；
/// ScreenSpaceCamera 由 UICameraService.ConvertCanvas 转换，跨场景的 worldCamera 重绑由该服务的 1 秒 sweep 兜底。
/// 显隐：未登录时仅在 zhuye 主页显示（编辑器直接播主场景也能看到），登录后全场景常驻；
/// zhuye 的 AgentPanel 打开期间隐藏（球与面板是同一个角色）——由
/// AgentView 在开/关面板时调 NotifyAgentPanelOpen 通知，本球不轮询。
/// 消息分流：3107/3104 到达时若 AgentView.IsPanelOpen（zhuye 面板开着）则不弹气泡——
/// 那边进聊天流；面板关着/其它场景 → 本球弹气泡（新消息顶替旧消息，不排队）。
/// 交互：按住球体拖动（钳制屏幕内，PlayerPrefs 记位置跨会话）；单击球体 zhuye 开阿福面板、
/// 其它场景弹本地提示（单击动作延迟 0.3s 执行，给双击留判定窗口）；右键球体弹出选项菜单
/// （打开阿福 / 关闭悬浮窗）；双击球体彻底隐藏——本次登录会话内不再出现（重登恢复），
/// 期间提醒只进阿福面板历史（2026-10-17 用户定稿）；关闭/隐藏后从主页打开一次阿福面板即恢复；
/// 气泡 raycastTarget=false 不挡点击。
/// 时间语义：全部 DOTween SetUpdate(true)（unscaled）——暂停中主动提醒照常出现（服务器世界不暂停）。
/// </summary>
public class AgentFloatBall : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
{
    // ──────────────────────────────
    // 单例自举（照 UiFx / ChatManager 先例：运行时 Bootstrap + DontDestroyOnLoad）
    // ──────────────────────────────

    private static AgentFloatBall instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        EnsureInstance();
    }

    /// <summary>确保悬浮球单例存在（运行时自举 / 主页显式打开都会走这里，幂等）。</summary>
    public static void EnsureInstance()
    {
        if (instance != null) return;
        GameObject holder = new GameObject("AfuBall");
        holder.AddComponent<AgentFloatBall>();
        DontDestroyOnLoad(holder);
    }

    // ──────────────────────────────
    // 常量（视觉与交互参数，与阿福面板同族）
    // ──────────────────────────────

    /// <summary>球体直径（px）</summary>
    private const float BallSize = 64f;

    /// <summary>球心距屏幕边缘的最小留白（px）</summary>
    private const float ScreenMargin = 12f;

    /// <summary>球默认位（左上角锚：x 向右、y 向下为负；设计文档定稿：左上角）</summary>
    private static readonly Vector2 DefaultPos = new(60f, -60f);

    /// <summary>气泡与球的间距（px）</summary>
    private const float BubbleGap = 10f;

    /// <summary>气泡文字区最大宽度（px）——超过换行撑高（"先量后撑"，与好友聊天同手法）</summary>
    private const float BubbleTextMaxWidth = 300f;

    /// <summary>气泡内边距（左右 12 / 上下 10，与阿福聊天气泡一致）</summary>
    private const float BubblePadX = 12f;
    private const float BubblePadY = 10f;

    /// <summary>气泡停留时长（秒，之后自动淡出）</summary>
    private const float BubbleHoldSeconds = 7f;

    /// <summary>本地提示的停留时长（秒，比提醒短）</summary>
    private const float HintHoldSeconds = 3f;

    // ── 右键菜单（贴球弹出，两个选项）──
    /// <summary>菜单宽度（px）</summary>
    private const float MenuWidth = 168f;

    /// <summary>单个选项高度（px）</summary>
    private const float MenuItemHeight = 42f;

    /// <summary>菜单内边距（px）</summary>
    private const float MenuPad = 6f;

    /// <summary>菜单与球的间距（px）</summary>
    private const float MenuGap = 8f;

    /// <summary>菜单项底色（常态/按下）</summary>
    private static readonly Color MenuItemColor = Hex("3A4150");
    private static readonly Color MenuItemHoverColor = Hex("4A5468");

    /// <summary>点按与拖动的分界位移（px）</summary>
    private const float DragThreshold = 6f;

    /// <summary>双击判定窗口（秒）：窗口内第二次点按 = 双击彻底隐藏（本次登录会话）</summary>
    private const float DoubleClickWindow = 0.3f;

    /// <summary>球体呼吸幅度与周期</summary>
    private const float BreathScale = 1.04f;
    private const float BreathSeconds = 1.4f;

    // ── 配色（与阿福面板深色 HUD 族一致）──
    private static readonly Color BubbleBgColor = Hex("2E333D");
    private static readonly Color TextMainColor = Hex("E0E0E0");
    private static readonly Color AccentGold = Hex("E8C46A");

    /// <summary>球位置持久化键（x/y 两个 float；换设备/重装即回默认位）</summary>
    private const string PosKeyX = "afu_ball_pos_x";
    private const string PosKeyY = "afu_ball_pos_y";

    // ──────────────────────────────
    // 运行时状态
    // ──────────────────────────────

    private Canvas canvas;
    private RectTransform canvasRect;
    private RectTransform ballRoot;
    private RectTransform visualRoot;
    private RectTransform bubbleRoot;
    private Image bubbleBg;
    private Image bubbleTail;
    private TMP_Text bubbleText;
    private CanvasGroup bubbleGroup;

    /// <summary>隐藏量宽文本（挂在常激活的画布根下、屏外 y=100000——照阿福面板 AgentBubbleMeasurer 先例）。
    /// 实测坑（2026-10-17）：TMP 对 inactive 对象 ForceMeshUpdate 的 renderedWidth 不可靠（同段文字
    /// inactive 量 120 / active 量 172.8），直接在气泡上量会偶发窄量 → 文字"一字一行"竖排。</summary>
    private TMP_Text measureText;

    /// <summary>气泡当前的淡出序列（新消息到达时掐掉重来）</summary>
    private Sequence bubbleLife;

    /// <summary>右键菜单根（默认隐藏；点空白或选完即收起）</summary>
    private RectTransform menuRoot;

    /// <summary>菜单的全屏透明遮罩：承接"点空白收起"，菜单收起时失活，不挡其它 UI</summary>
    private GameObject menuBlocker;

    /// <summary>面板开着（zhuye 的 AgentView 通知；面板销毁/切场景会翻回 false）</summary>
    private bool panelOpen;
    /// <summary>对战房间打开时临时隐藏：房间是全屏界面，左上角球会压住标题/副标题</summary>
    private bool battleRoomOpen;

    /// <summary>主页已打开：未登录直接播 zhuye（编辑器调试/主场景单播）时也允许球出现；
    /// 场景切换时按场景名重算，登录成功后本字段不再影响全场景显隐。</summary>
    private bool homeOpened;

    /// <summary>玩家双击彻底隐藏（2026-10-17 用户定稿：本次登录会话内不再出现，重登恢复；
    /// 期间 3107/3104 不上球——提醒照常产生并归档，进阿福面板历史可看）</summary>
    private bool hiddenByUser;

    /// <summary>上一次点按时刻（双击判定用；-10=很久以前）</summary>
    private float lastClickTime = -10f;

    /// <summary>等待双击窗口期满后执行的单击动作（开面板/本地提示；窗口内的第二次点按会取消它转隐藏）</summary>
    private bool pendingSingleClick;

    /// <summary>已登录（LoginResponse 成功后置位；断线清零）</summary>
    private bool loggedIn;

    /// <summary>拖动会话进行中（按下后位移超过阈值置位，松手复位）</summary>
    private bool dragging;

    /// <summary>按下时的累积位移（判定点按 or 拖动）</summary>
    private Vector2 pressPosition;

    /// <summary>网络事件是否已订阅（ClientNetworkManager 可能晚于本球自举，懒订阅）</summary>
    private bool netSubscribed;

    /// <summary>呼吸补间（常驻循环）</summary>
    private Tween breathTween;

    /// <summary>当前球位（anchoredPosition，左上角锚坐标系）</summary>
    private Vector2 ballPos = DefaultPos;

    // ──────────────────────────────
    // 生命周期
    // ──────────────────────────────

    /// <summary>当前单例（E2E 断言用；不存在为 null）</summary>
    public static AgentFloatBall Instance => instance;

    /// <summary>球当前是否可见（登录且面板没开）</summary>
    public bool IsBallVisible => ballRoot != null && ballRoot.gameObject.activeSelf;

    /// <summary>当前气泡正文（无气泡为空串）</summary>
    public string CurrentBubbleText => bubbleText != null ? bubbleText.text : string.Empty;

    /// <summary>气泡是否在场（显示中，含淡出过程）</summary>
    public bool HasBubble => bubbleRoot != null && bubbleRoot.gameObject.activeSelf;

    /// <summary>球心位置（canvas 坐标，E2E 断言用）</summary>
    public Vector2 BallPosition => ballRoot != null ? ballRoot.anchoredPosition : Vector2.zero;

    /// <summary>玩家是否已双击隐藏（E2E 断言用；重登复位）</summary>
    public bool IsHiddenByUser => hiddenByUser;

    private void Awake()
    {
        instance = this;
        homeOpened = SceneManager.GetActiveScene().name == "zhuye";
        BuildCanvas();
        LoadSavedPosition();
        ApplyVisibility();

        // 切场景后重新评估可见性：zhuye 卸载会销毁面板与控制器（panelOpen 语义失效），球要自己回来
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDestroy()
    {
        if (instance == this) instance = null;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        UnsubscribeNet();
        breathTween?.Kill();
        bubbleLife?.Kill();
        if (visualRoot != null) visualRoot.DOKill();
        if (bubbleRoot != null) bubbleRoot.DOKill();
    }

    private void Update()
    {
        // 懒订阅：ClientNetworkManager 可能晚于本球自举（Bootstrap 阶段顺序不保证）
        if (!netSubscribed)
        {
            ClientNetworkManager net = ClientNetworkManager.Instance;
            if (net != null) SubscribeNet(net);
        }

        // 单击动作等双击窗口期满再执行（窗口内的第二次点按会取消它并转"彻底隐藏"）
        if (pendingSingleClick && (!IsBallVisible || Time.unscaledTime - lastClickTime > DoubleClickWindow))
        {
            pendingSingleClick = false;
            if (IsBallVisible) ClickBall();
        }

        // 视口尺寸变化（Game View 拖宽窄/分辨率切换）→ 把球钳回屏内
        if (ballRoot != null && ballRoot.gameObject.activeSelf)
        {
            ClampPosition();
        }
    }

    // ──────────────────────────────
    // 画布与球体构建（程序化，零场景依赖）
    // ──────────────────────────────

    private void BuildCanvas()
    {
        GameObject canvasGo = new GameObject("AfuBallCanvas", typeof(Canvas));
        canvasGo.transform.SetParent(transform, false);
        canvas = canvasGo.GetComponent<Canvas>();
        // 框架层画布按 UILayer 枚举排序（Background 2000 … BlackMask 7000），
        // 300 会被所有面板盖住；抬到 TopLayer(5000) 之上、TipLayer(6000) 与全屏淡入之下。
        canvas.sortingOrder = 5900;
        canvasGo.AddComponent<GraphicRaycaster>();   // 球体要点要拖

        // UI 迁移：camera space + UI 相机（照 UiFx 先例；跨场景重绑由 UICameraService 的 sweep 兜底）
        UICameraService.ConvertCanvas(canvas, UICameraService.UICamera);
        UICameraService.SetLayerRecursive(canvasGo.transform, UICameraService.UiLayer);

        canvasRect = canvasGo.GetComponent<RectTransform>();

        // ── 球体根（左上角锚 + 中心轴心；位置=ballPos，拖动只改这一个值）──
        ballRoot = NewRect(canvasRect, "BallRoot");
        ballRoot.anchorMin = ballRoot.anchorMax = new Vector2(0f, 1f);
        ballRoot.pivot = new Vector2(0.5f, 0.5f);
        ballRoot.sizeDelta = Vector2.one * BallSize;
        ballRoot.anchoredPosition = ballPos;

        // 呼吸放在 Visual 子层：缩放补间不与球根的位置/命中混淆
        visualRoot = NewRect(ballRoot, "Visual");
        visualRoot.anchorMin = visualRoot.anchorMax = Vector2.zero;
        visualRoot.anchorMax = Vector2.one;
        visualRoot.offsetMin = visualRoot.offsetMax = Vector2.zero;

        // 底光（金色柔光，比球大一圈）
        Image glow = NewImage(visualRoot, "Glow", SpriteOrNull("Agent/ui/afu_glow"),
            WithAlpha(AccentGold, 0.22f), Vector2.one * (BallSize * 1.35f));

        // 圆形遮罩 + 头像（素材缺失时回落"福"字圆底，面板同款降级语义）
        GameObject maskGo = new GameObject("AvatarMask", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Mask));
        maskGo.transform.SetParent(visualRoot, false);
        RectTransform maskRect = maskGo.GetComponent<RectTransform>();
        maskRect.sizeDelta = Vector2.one * BallSize;
        maskRect.anchoredPosition = Vector2.zero;
        Image maskImage = maskGo.GetComponent<Image>();
        maskImage.sprite = SpriteOrNull("Agent/ui/afu_circle");
        maskImage.color = Color.white;
        maskImage.raycastTarget = true;   // 球体的命中面（点击/拖动从它冒泡到本组件）
        maskGo.GetComponent<Mask>().showMaskGraphic = false;

        Sprite avatarSprite = SpriteOrNull("Agent/shouanren_avatar");
        if (avatarSprite != null)
        {
            Image avatar = NewImage(maskGo.transform, "Avatar", avatarSprite, Color.white, Vector2.one * BallSize);
            avatar.preserveAspect = true;
        }
        else
        {
            TMP_Text glyph = NewText(maskGo.transform, "AvatarGlyph", 24, "守", AccentGold, TextAlignmentOptions.Center);
            glyph.rectTransform.sizeDelta = Vector2.one * BallSize;
        }

        // 描边圆环
        Image ring = NewImage(visualRoot, "Ring", SpriteOrNull("Agent/ui/afu_ring"),
            WithAlpha(AccentGold, 0.85f), Vector2.one * (BallSize + 4f));

        // 待机呼吸（unscaled：暂停中球仍是"活"的）
        visualRoot.localScale = Vector3.one;
        breathTween = visualRoot.DOScale(BreathScale, BreathSeconds)
            .SetEase(Ease.InOutSine)
            .SetLoops(-1, LoopType.Yoyo)
            .SetUpdate(true);

        // ── 气泡根（球根的兄弟：不受球体缩放影响，可自由越出球的范围）──
        bubbleRoot = NewRect(canvasRect, "BubbleRoot");
        bubbleRoot.anchorMin = bubbleRoot.anchorMax = new Vector2(0f, 1f);
        bubbleRoot.pivot = new Vector2(0f, 0.5f);
        bubbleRoot.gameObject.SetActive(false);

        bubbleBg = NewImage(bubbleRoot, "BubbleBg", SpriteOrNull("Agent/ui/afu_round_md"), BubbleBgColor, Vector2.one * 100f);
        bubbleBg.type = SlicedType(bubbleBg.sprite);
        bubbleBg.raycastTarget = false;
        // 底图拉伸铺满气泡根：文字量出来的尺寸写在根上，底图跟根走（固定占位 100x100 不扩就是"小方块"bug）
        RectTransform bgRect = bubbleBg.rectTransform;
        bgRect.anchorMin = Vector2.zero;
        bgRect.anchorMax = Vector2.one;
        bgRect.offsetMin = Vector2.zero;
        bgRect.offsetMax = Vector2.zero;

        bubbleGroup = bubbleRoot.gameObject.GetComponent<CanvasGroup>();
        if (bubbleGroup == null) bubbleGroup = bubbleRoot.gameObject.AddComponent<CanvasGroup>();
        bubbleGroup.blocksRaycasts = false;
        bubbleGroup.interactable = false;

        bubbleText = NewText(bubbleRoot, "BubbleText", 15, string.Empty, TextMainColor, TextAlignmentOptions.MidlineLeft);
        bubbleText.enableWordWrapping = true;
        bubbleText.overflowMode = TextOverflowModes.Overflow;
        bubbleText.raycastTarget = false;

        bubbleTail = NewImage(bubbleRoot, "BubbleTail", SpriteOrNull("Agent/ui/afu_tail_left"), BubbleBgColor, new Vector2(9f, 14f));
        bubbleTail.raycastTarget = false;

        // 隐藏量宽器：常激活、屏外（量宽不依赖气泡根的激活状态，见字段注释）
        measureText = NewText(canvasRect, "BallBubbleMeasurer", 15, string.Empty, TextMainColor, TextAlignmentOptions.MidlineLeft);
        measureText.enableWordWrapping = true;
        measureText.overflowMode = TextOverflowModes.Overflow;
        measureText.raycastTarget = false;
        ((RectTransform)measureText.transform).anchoredPosition = new Vector2(0f, 100000f);

        // 右键菜单最后建：同一父节点下后建的在上面，遮罩必须在菜单下面
        BuildContextMenu();
    }

    // ──────────────────────────────
    // 右键菜单（打开阿福 / 关闭悬浮窗）
    // ──────────────────────────────

    /// <summary>菜单是否展开（E2E 断言用）</summary>
    public bool IsMenuOpen => menuRoot != null && menuRoot.gameObject.activeSelf;

    /// <summary>
    /// 构建右键菜单：全屏透明遮罩 + 贴球弹出的小面板（两个选项）。
    /// 遮罩先建（渲染在下、接射线），点空白处收起；菜单收起时遮罩一起失活，不挡其它 UI。
    /// </summary>
    private void BuildContextMenu()
    {
        menuBlocker = new GameObject("BallMenuBlocker", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        menuBlocker.transform.SetParent(canvasRect, false);
        menuBlocker.layer = UICameraService.UiLayer;

        RectTransform blockerRect = menuBlocker.GetComponent<RectTransform>();
        blockerRect.anchorMin = Vector2.zero;
        blockerRect.anchorMax = Vector2.one;
        blockerRect.offsetMin = Vector2.zero;
        blockerRect.offsetMax = Vector2.zero;

        Image blockerImage = menuBlocker.GetComponent<Image>();
        blockerImage.color = new Color(0f, 0f, 0f, 0f);   // 完全透明，只负责接点击
        blockerImage.raycastTarget = true;
        AddClick(blockerImage, CloseMenu);

        menuRoot = NewRect(canvasRect, "BallContextMenu");
        menuRoot.anchorMin = menuRoot.anchorMax = new Vector2(0f, 1f);
        menuRoot.pivot = new Vector2(0f, 0.5f);
        menuRoot.sizeDelta = new Vector2(MenuWidth, MenuItemHeight * 2f + MenuPad * 2f);

        Image menuBg = NewImage(menuRoot, "MenuBg", SpriteOrNull("Agent/ui/afu_round_md"), BubbleBgColor, menuRoot.sizeDelta);
        menuBg.type = SlicedType(menuBg.sprite);
        menuBg.raycastTarget = true;   // 点菜单空白处也收起，且不让点击穿透到遮罩
        RectTransform menuBgRect = menuBg.rectTransform;
        menuBgRect.anchorMin = Vector2.zero;
        menuBgRect.anchorMax = Vector2.one;
        menuBgRect.offsetMin = Vector2.zero;
        menuBgRect.offsetMax = Vector2.zero;
        AddClick(menuBg, CloseMenu);

        BuildMenuItem("打开守岸人", 0, () => { CloseMenu(); ClickBall(); });
        BuildMenuItem("关闭悬浮窗", 1, () => { CloseMenu(); HideByUser(); });

        menuRoot.gameObject.SetActive(false);
        menuBlocker.SetActive(false);
    }

    /// <summary>菜单项：一行底板 + 居中文本，整行可点</summary>
    private void BuildMenuItem(string label, int index, UnityEngine.Events.UnityAction onClick)
    {
        Image item = NewImage(menuRoot, "MenuItem" + index, SpriteOrNull("Agent/ui/afu_round_md"), MenuItemColor,
            new Vector2(MenuWidth - MenuPad * 2f, MenuItemHeight - 4f));
        item.type = SlicedType(item.sprite);
        item.raycastTarget = true;

        RectTransform itemRect = item.rectTransform;
        itemRect.anchorMin = itemRect.anchorMax = new Vector2(0.5f, 1f);
        itemRect.pivot = new Vector2(0.5f, 1f);
        itemRect.anchoredPosition = new Vector2(0f, -(MenuPad + index * MenuItemHeight));

        AddClick(item, onClick);

        TMP_Text text = NewText(itemRect, "Label", 15, label, TextMainColor, TextAlignmentOptions.Center);
        RectTransform textRect = text.rectTransform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
    }

    /// <summary>贴球弹出菜单：球在右半屏翻到左侧，纵向钳进屏幕</summary>
    private void OpenMenu()
    {
        if (menuRoot == null) return;

        HideBubble();   // 菜单和气泡别叠在一起

        Rect rect = canvasRect.rect;
        float halfHeight = menuRoot.sizeDelta.y * 0.5f;
        bool menuOnLeft = ballPos.x > rect.width * 0.5f;

        menuRoot.pivot = new Vector2(menuOnLeft ? 1f : 0f, 0.5f);
        float x = menuOnLeft
            ? ballPos.x - BallSize * 0.5f - MenuGap
            : ballPos.x + BallSize * 0.5f + MenuGap;
        float y = Mathf.Clamp(ballPos.y,
            -rect.height + halfHeight + ScreenMargin,
            -halfHeight - ScreenMargin);
        menuRoot.anchoredPosition = new Vector2(x, y);

        menuBlocker.SetActive(true);
        menuRoot.gameObject.SetActive(true);
    }

    /// <summary>收起菜单（幂等）</summary>
    private void CloseMenu()
    {
        if (menuRoot != null && menuRoot.gameObject.activeSelf)
            menuRoot.gameObject.SetActive(false);

        if (menuBlocker != null && menuBlocker.activeSelf)
            menuBlocker.SetActive(false);
    }

    // ──────────────────────────────
    // 网络事件（懒订阅）
    // ──────────────────────────────

    private void SubscribeNet(ClientNetworkManager net)
    {
        net.LoginResponseReceived += OnLoginResponse;
        net.ConnectionBroken += OnConnectionBroken;
        net.AgentProactiveNotifyReceived += OnProactiveNotify;
        net.AgentBriefNotifyReceived += OnBriefNotify;
        netSubscribed = true;

        // 已登录的会话里球才自举（比如编辑器中途重编译）：直接补一次可见性评估
        loggedIn = net.MyPlayerId > 0;
        ApplyVisibility();
    }

    private void UnsubscribeNet()
    {
        if (!netSubscribed) return;
        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net != null)
        {
            net.LoginResponseReceived -= OnLoginResponse;
            net.ConnectionBroken -= OnConnectionBroken;
            net.AgentProactiveNotifyReceived -= OnProactiveNotify;
            net.AgentBriefNotifyReceived -= OnBriefNotify;
        }
        netSubscribed = false;
    }

    private void OnLoginResponse(LoginResponse response)
    {
        loggedIn = response.Success;
        hiddenByUser = false;   // 双击隐藏只管本次登录会话，重登恢复（2026-10-17 用户定稿）
        if (!loggedIn) HideBubble();
        ApplyVisibility();
    }

    private void OnConnectionBroken()
    {
        loggedIn = false;
        HideBubble();
        ApplyVisibility();
    }

    /// <summary>3107 主动提醒到达：面板开着不弹（那边进聊天流）；关着/其它场景 → 球旁气泡。</summary>
    private void OnProactiveNotify(AgentProactiveNotify notify)
    {
        if (string.IsNullOrEmpty(notify.Content)) return;
        if (AgentView.IsPanelOpen) return;
        ShowMessage(notify.Content, BubbleHoldSeconds);
    }

    /// <summary>3104 登录简报到达：面板开着不弹（现状进面板）；关着 → 悬浮球"上线问好"（灵宝式）。</summary>
    private void OnBriefNotify(AgentBriefNotify notify)
    {
        if (string.IsNullOrEmpty(notify.Content)) return;
        if (AgentView.IsPanelOpen) return;
        ShowMessage(notify.Content, BubbleHoldSeconds);
    }

    // ──────────────────────────────
    // 面板开合通知（AgentView 调用；不轮询）
    // ──────────────────────────────

    /// <summary>zhuye 的阿福面板开/关时通知悬浮球隐藏/回来（同一角色不同台面，不同屏出现）。</summary>
    public static void NotifyAgentPanelOpen(bool open)
    {
        if (instance == null) return;
        instance.panelOpen = open;

        // 打开面板 = 玩家主动找阿福 → 之前右键/双击收起来的球一并恢复，
        // 否则"关掉悬浮窗"之后没有任何可见入口能把它叫回来
        if (open) instance.hiddenByUser = false;

        instance.ApplyVisibility();
    }

    /// <summary>
    /// 主页打开时显式通知：保证 HomeView/主场景流程里球一定被拉起（未登录也能看到，
    /// 用于编辑器直接播 zhuye 和主场景单场景调试）。幂等，可重复调用。
    /// </summary>
    public static void NotifyHomeOpened()
    {
        EnsureInstance();
        if (instance == null) return;
        instance.homeOpened = true;
        instance.ApplyVisibility();
    }

    /// <summary>对战房间打开/关闭时临时隐藏悬浮球（只在 UI 层做显隐，不影响会话与提醒）</summary>
    public static void NotifyBattleRoomOpen(bool open)
    {
        EnsureInstance();
        if (instance == null) return;
        instance.battleRoomOpen = open;
        instance.ApplyVisibility();
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        // zhuye 卸载后面板与控制器都没了：panelOpen 语义失效，按当前存活状态重评
        panelOpen = AgentView.IsPanelOpen;
        // 未登录直接播主场景（编辑器调试/主场景启动）时也要能看到球；其它场景仍要求已登录
        homeOpened = scene.name == "zhuye";
        battleRoomOpen = false;
        ApplyVisibility();
    }

    /// <summary>可见性唯一真相：登录了或已到主页、面板没开、也没被双击隐藏才显示；隐藏时带走气泡。</summary>
    private void ApplyVisibility()
    {
        bool visible = (loggedIn || homeOpened) && !panelOpen && !hiddenByUser && !battleRoomOpen;
        if (ballRoot != null && ballRoot.gameObject.activeSelf != visible)
        {
            ballRoot.gameObject.SetActive(visible);
            if (!visible)
            {
                HideBubble();
                CloseMenu();
            }
        }
    }

    // ──────────────────────────────
    // 消息气泡（顶替制：新消息掐掉旧生命周期重来）
    // ──────────────────────────────

    /// <summary>
    /// 在球旁弹一条消息
    /// 球被拖到屏幕右半侧时气泡翻转到球左侧（尾巴同侧镜像）；文字按 300px 最大宽"先量后撑"。
    /// </summary>
    /// <param name="content">消息正文（会被转全角防富文本注入）</param>
    /// <param name="holdSeconds">停留时长（秒）</param>
    public void ShowMessage(string content, float holdSeconds)
    {
        if (bubbleRoot == null || bubbleText == null) return;
        if (!IsBallVisible) return;   // 球都隐藏了（未登录/面板开着）不展示

        bubbleLife?.Kill();
        bubbleRoot.gameObject.SetActive(true);   // 先激活：后续设置都在活对象上进行

        // 量文字（常激活量宽器，文字区固定最大宽，量实际占位——别在 inactive 的气泡上量，会窄量）
        measureText.text = EscapeRichText(content);
        measureText.rectTransform.sizeDelta = new Vector2(BubbleTextMaxWidth, 40f);
        measureText.ForceMeshUpdate();
        Vector2 textSize = new Vector2(
            Mathf.Min(BubbleTextMaxWidth, measureText.renderedWidth) + 1f,
            measureText.renderedHeight);
        Vector2 bubbleSize = new Vector2(Mathf.Max(textSize.x + 2f * BubblePadX, 60f),
                                         Mathf.Max(textSize.y + 2f * BubblePadY, 36f));

        // 气泡摆位：默认球右侧、垂直居中球心；球在右半屏翻转到左侧
        bool bubbleOnLeft = ballPos.x > canvasRect.rect.width * 0.5f;
        bubbleRoot.sizeDelta = bubbleSize;
        bubbleRoot.pivot = bubbleOnLeft ? new Vector2(1f, 0.5f) : new Vector2(0f, 0.5f);
        float x = bubbleOnLeft
            ? ballPos.x - BallSize * 0.5f - BubbleGap
            : ballPos.x + BallSize * 0.5f + BubbleGap;
        bubbleRoot.anchoredPosition = new Vector2(x, ballPos.y);

        // 文本回填气泡中心（正文在量宽之后才上屏）
        bubbleText.text = measureText.text;
        bubbleText.rectTransform.anchorMin = bubbleText.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        bubbleText.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        bubbleText.rectTransform.anchoredPosition = Vector2.zero;
        bubbleText.rectTransform.sizeDelta = textSize;

        // 尾巴：贴气泡靠近球那一侧的底角（与阿福聊天气泡同款锚定手法）
        bubbleTail.sprite = SpriteOrNull(bubbleOnLeft ? "Agent/ui/afu_tail_right" : "Agent/ui/afu_tail_left");
        bubbleTail.rectTransform.anchorMin = bubbleTail.rectTransform.anchorMax =
            bubbleOnLeft ? new Vector2(1f, 0f) : new Vector2(0f, 0f);
        bubbleTail.rectTransform.pivot = bubbleOnLeft ? new Vector2(0f, 0.5f) : new Vector2(1f, 0.5f);
        bubbleTail.rectTransform.anchoredPosition = new Vector2(bubbleOnLeft ? -4f : 4f, 9f);

        // 入场：0.85→1 回弹 + 淡入 → 停留 → 淡出收场（全程 unscaled：暂停中照常；根已在方法开头激活）
        bubbleRoot.localScale = Vector3.one * 0.85f;
        bubbleGroup.alpha = 0f;
        bubbleRoot.DOKill();
        bubbleGroup.DOKill();
        bubbleLife = DOTween.Sequence().SetUpdate(true);
        bubbleLife.Join(bubbleRoot.DOScale(1f, 0.25f).SetEase(Ease.OutBack, 0.6f));
        bubbleLife.Join(bubbleGroup.DOFade(1f, 0.18f));
        bubbleLife.AppendInterval(holdSeconds);
        bubbleLife.Append(bubbleGroup.DOFade(0f, 0.4f).SetEase(Ease.InQuad));
        bubbleLife.OnComplete(HideBubble);
    }

    /// <summary>立即收起气泡（断线/隐藏/新会话时）。</summary>
    private void HideBubble()
    {
        bubbleLife?.Kill();
        bubbleLife = null;
        if (bubbleRoot != null)
        {
            bubbleRoot.DOKill();
            bubbleGroup?.DOKill();
            bubbleRoot.gameObject.SetActive(false);
        }
    }

    // ──────────────────────────────
    // 拖动与点击（球体唯一输入面）
    // ──────────────────────────────

    public void OnPointerDown(PointerEventData eventData)
    {
        if (!IsBallVisible) return;
        dragging = false;
        pressPosition = eventData.position;
        breathTween?.Pause();   // 拖动时停呼吸，跟手不"喘"
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (!IsBallVisible) return;
        if (!dragging && Vector2.Distance(eventData.position, pressPosition) < DragThreshold) return;
        dragging = true;

        ballPos += eventData.delta;   // ConstantPixelSize：屏幕像素位移=画布单位
        ClampPosition();

        // 气泡在场就跟着球走（按当前左右侧重新摆位）
        if (HasBubble)
        {
            bool bubbleOnLeft = ballPos.x > canvasRect.rect.width * 0.5f;
            bubbleRoot.pivot = bubbleOnLeft ? new Vector2(1f, 0.5f) : new Vector2(0f, 0.5f);
            bubbleRoot.anchoredPosition = new Vector2(
                bubbleOnLeft ? ballPos.x - BallSize * 0.5f - BubbleGap : ballPos.x + BallSize * 0.5f + BubbleGap,
                ballPos.y);
        }
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        breathTween?.Play();
        bool wasDragging = dragging;
        dragging = false;
        if (!IsBallVisible) return;

        if (wasDragging)
        {
            SavePosition();
            return;
        }

        // 右键：弹出选项菜单（打开阿福 / 关闭悬浮窗），不参与单击/双击判定
        if (eventData.button == PointerEventData.InputButton.Right)
        {
            if (IsMenuOpen) CloseMenu();
            else OpenMenu();
            return;
        }

        if (IsMenuOpen) CloseMenu();   // 菜单开着时左键 = 先收起

        // 双击=彻底隐藏（本次登录会话）；单击动作延迟到窗口期满执行（Update 里），给双击留判定时间
        float now = Time.unscaledTime;
        if (now - lastClickTime <= DoubleClickWindow)
        {
            lastClickTime = -10f;
            pendingSingleClick = false;
            HideByUser();
            return;
        }
        lastClickTime = now;
        pendingSingleClick = true;
    }

    /// <summary>
    /// 双击彻底隐藏（本次登录会话）：先弹一条确认气泡再隐身——气泡是球的兄弟节点，
    /// 球隐了气泡还能把这句话说完再淡出；不走 ApplyVisibility 的 HideBubble（那会立刻掐掉确认气泡）。
    /// 期间 3107/3104 到达不再上球（ShowMessage 的可见性守卫挡住），提醒照常归档进面板历史；重登恢复。
    /// </summary>
    private void HideByUser()
    {
        if (hiddenByUser) return;
        ShowMessage("守岸人先躲起来啦，主页打开守岸人面板就能叫我回来", 2.5f);
        hiddenByUser = true;
        if (ballRoot != null && ballRoot.gameObject.activeSelf) ballRoot.gameObject.SetActive(false);
    }

    /// <summary>点按球体：zhuye 打开阿福面板（同一角色换台面）；其它场景给本地提示。</summary>
    private void ClickBall()
    {
        if (!AgentView.TryOpenFromFloatBall())
        {
            ShowMessage("主界面才能和我聊天哦", HintHoldSeconds);
        }
    }

    // ──────────────────────────────
    // 位置持久化与钳制
    // ──────────────────────────────

    /// <summary>把球心钳在屏内（留边距）；球位写入 ballRoot。</summary>
    private void ClampPosition()
    {
        Rect rect = canvasRect.rect;
        float half = BallSize * 0.5f;
        float minX = half + ScreenMargin;
        float maxX = Mathf.Max(minX, rect.width - half - ScreenMargin);
        float maxY = -(half + ScreenMargin);
        float minY = -Mathf.Max(half + ScreenMargin, rect.height - half - ScreenMargin);   // 小窗口退化时两头同值，安全
        ballPos = new Vector2(
            Mathf.Clamp(ballPos.x, minX, maxX),
            Mathf.Clamp(ballPos.y, minY, maxY));
        ballRoot.anchoredPosition = ballPos;
    }

    private void LoadSavedPosition()
    {
        if (PlayerPrefs.HasKey(PosKeyX) && PlayerPrefs.HasKey(PosKeyY))
        {
            ballPos = new Vector2(PlayerPrefs.GetFloat(PosKeyX), PlayerPrefs.GetFloat(PosKeyY));
        }
        if (canvasRect != null) ClampPosition();
    }

    private void SavePosition()
    {
        PlayerPrefs.SetFloat(PosKeyX, ballPos.x);
        PlayerPrefs.SetFloat(PosKeyY, ballPos.y);
        PlayerPrefs.Save();
    }

    // ──────────────────────────────
    // 程序化 UI 小工具（Tuanjie fork：Graphic 只带 RectTransform，CanvasRenderer 要自己补）
    // ──────────────────────────────

    /// <summary>给任意 Graphic 挂一次点击回调（EventTrigger，不依赖 Button 的过渡效果）</summary>
    private static void AddClick(Graphic target, UnityEngine.Events.UnityAction onClick)
    {
        if (target == null || onClick == null) return;

        EventTrigger trigger = target.gameObject.GetComponent<EventTrigger>();
        if (trigger == null) trigger = target.gameObject.AddComponent<EventTrigger>();

        EventTrigger.Entry entry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
        entry.callback.AddListener(_ => onClick());
        trigger.triggers.Add(entry);
    }

    private static RectTransform NewRect(Transform parent, string name)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        go.layer = UICameraService.UiLayer;
        return go.GetComponent<RectTransform>();
    }

    private static Image NewImage(Transform parent, string name, Sprite sprite, Color color, Vector2 size)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.transform.SetParent(parent, false);
        go.layer = UICameraService.UiLayer;
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = size;
        rect.anchoredPosition = Vector2.zero;
        Image image = go.GetComponent<Image>();
        image.sprite = sprite;
        image.color = color;
        image.type = SlicedType(sprite);
        image.raycastTarget = false;
        return image;
    }

    private static TMP_Text NewText(Transform parent, string name, float fontSize, string text,
        Color color, TextAlignmentOptions alignment)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        go.layer = UICameraService.UiLayer;
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(200f, 30f);
        rect.anchoredPosition = Vector2.zero;
        TMP_Text tmp = go.GetComponent<TMP_Text>();
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.color = color;
        tmp.alignment = alignment;
        tmp.raycastTarget = false;
        return tmp;
    }

    /// <summary>带九宫格边界的 sprite 才用 Sliced（否则 Simple），照 AgentPanelBuilder 口径。</summary>
    private static Image.Type SlicedType(Sprite sprite)
    {
        return sprite != null && sprite.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple;
    }

    private static Sprite SpriteOrNull(string resourcesPath)
    {
        return Resources.Load<Sprite>(resourcesPath);
    }

    /// <summary>尖括号转全角：防内容里的标签被 TMP 富文本执行（与 AgentView 同款）。</summary>
    private static string EscapeRichText(string content)
    {
        return (content ?? string.Empty).Replace('<', '＜').Replace('>', '＞');
    }

    private static Color Hex(string hex)
    {
        return ColorUtility.TryParseHtmlString("#" + hex, out Color color) ? color : Color.magenta;
    }

    private static Color WithAlpha(Color color, float alpha)
    {
        color.a = alpha;
        return color;
    }
}
