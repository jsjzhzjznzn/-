using System.Collections.Generic;

using Common.Network;

using Cysharp.Threading.Tasks;

using DG.Tweening;

using SkierFramework;

using TMPro;

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// AI Agent（守岸人，原阿福）面板控制器
/// 链路：UGUI 面板 →(3101) .NET 网关 → Python Agent(LangGraph) →(3103 流式分片 + 3102 权威终值)
/// 约定（计划文档 §8）：只显示最终回复，思考过程/工具调用不渲染；纯文本+尖括号转全角防富文本注入
/// 流式渲染：3103 分片按 ~12 次/秒合批上屏（打字机），3102 到达后校准成终值（截断/兜底都在 3102 里）
///
/// 表现层（DOTween，全部 unscaled）：面板开合补间、气泡错帧入场、三点思考指示器、
/// 流式高度平滑、平滑滚到底、错误抖动、状态文案自动淡出、入口按钮新消息脉动。
/// 动画只碰 localScale / CanvasGroup.alpha / sizeDelta / ScrollRect 归一化位置——
/// 不碰 anchoredPosition 的 y（那个由 VirtualScrollList 写入，动画会被布局覆写）。
/// </summary>
public class AgentView : UIView, IBeginDragHandler, IDragHandler, IEndDragHandler
{
    [Header("入口与面板")]
    /// <summary>入口按钮已迁到 HomeView（agentButton），这里保留字段仅为兼容脉冲/测试代码，运行时恒为 null</summary>
    public Button entryButton;

    /// <summary>面板关闭按钮</summary>
    [ControlBinding]
    public Button closeButton;

    /// <summary>面板开合渐显（AgentPanel 上的 CanvasGroup；预制体里自解析，见 OnInit）</summary>
    public CanvasGroup panelCanvas;

    /// <summary>面板开合缩放/滑入（AgentPanel 的 RectTransform；预制体里自解析，见 OnInit）</summary>
    public RectTransform panelRect;

    /// <summary>面板当前是否打开（框架 SetActive 的镜像，供内部判断与浮动球查询）</summary>
    private bool _isOpen;

    // ── 拖动与缩放（2026-10-23：面板可拖、可整体等比缩放；位置/档位跨会话记忆）──
    /// <summary>缩放档位（0.80~1.50；默认 1.15 补偿 1920 参考画布，正文等效约 17px）</summary>
    private static readonly float[] ZoomLevels = { 0.80f, 0.90f, 1.00f, 1.15f, 1.30f, 1.50f };
    private const int DefaultZoomIndex = 3;
    private const string PanelPosKeyX = "afu_panel_pos_x";
    private const string PanelPosKeyY = "afu_panel_pos_y";
    private const string PanelZoomKey = "afu_panel_zoom";

    [Header("面板拖动与缩放")]
    /// <summary>缩小按钮（按名字自解析，不额外占用 UIControlData 绑定）</summary>
    public Button zoomOutButton;

    /// <summary>放大按钮</summary>
    public Button zoomInButton;

    private int zoomIndex = DefaultZoomIndex;
    private float zoomLevel = 1.15f;
    private bool panelDragging;
    private Vector2 dragStartPointerLocal;
    private Vector2 dragStartPanelPos;

    /// <summary>拖动起手时的射线命中缓存（判断是否按在滚动区/按钮/输入框上）</summary>
    private readonly List<RaycastResult> dragRaycastHits = new List<RaycastResult>();

    [Header("顶部·守岸人名片")]
    /// <summary>标题：阿福</summary>
    [ControlBinding("ChatTitle")]
    public TMP_Text titleText;

    /// <summary>人设签名一句话</summary>
    [ControlBinding]
    public TMP_Text signatureText;

    /// <summary>AI 生成的头像（为空时保留"福"字占位）</summary>
    [ControlBinding("AvatarMask/Avatar")]
    public Image avatarImage;

    /// <summary>"福"字占位（头像素材就位后隐藏）</summary>
    [ControlBinding("AvatarMask/AvatarGlyph")]
    public GameObject avatarGlyph;

    [Header("聊天区")]
    /// <summary>消息滚动容器（挂 ScrollRect，虚拟列表宿主）</summary>
    [ControlBinding("MessageList")]
    public ScrollRect messageScroll;

    /// <summary>消息气泡容器（虚拟列表的 Content）</summary>
    [ControlBinding("MessageList/Viewport/Content")]
    public RectTransform messageListContent;

    /// <summary>阿福侧气泡模板（左侧深灰，未激活）</summary>
    [ControlBinding("MessageList/Viewport/Content/MessageLeftTemplate")]
    public GameObject bubbleLeftTemplate;

    /// <summary>玩家侧气泡模板（右侧蓝灰，未激活）</summary>
    [ControlBinding("MessageList/Viewport/Content/MessageRightTemplate")]
    public GameObject bubbleRightTemplate;

    /// <summary>聊天输入框（MultiLineSubmit：Enter 发送 / Shift+Enter 换行）</summary>
    [ControlBinding("ChatInput")]
    public TMP_InputField chatInput;

    /// <summary>发送按钮</summary>
    [ControlBinding]
    public Button sendButton;

    /// <summary>状态行：错误提示 / 加载提示（自动淡出）</summary>
    [ControlBinding]
    public TMP_Text statusText;

    [Header("动画")]
    /// <summary>三点思考指示器（等待回复时出现，流式一来就让位）</summary>
    [ControlBinding]
    public GameObject thinkingIndicator;

    /// <summary>三个思考点的父物体（错相循环弹跳）</summary>
    [ControlBinding("ThinkingIndicator/Dots")]
    public Transform thinkingDotsRoot;

    [Header("输入框自适应（学好友面板的 QQ 式长高）")]
    /// <summary>聊天区（输入框长高时同步变矮，顶边不动）</summary>
    [ControlBinding("MessageList")]
    public RectTransform chatAreaRect;

    /// <summary>状态行（跟着输入框顶边上移）</summary>
    [ControlBinding("StatusText")]
    public RectTransform statusRowRect;

    /// <summary>三点思考指示器（与状态行同一行，一起上移）</summary>
    [ControlBinding("ThinkingIndicator")]
    public RectTransform thinkingRect;

    /// <summary>输入框最小高度（单行）</summary>
    public float inputMinHeight = 44f;

    /// <summary>输入框最大高度（到顶后不再长高，改成输入框内部滚动）</summary>
    public float inputMaxHeight = 220f;

    /// <summary>文本 preferredHeight → 输入框高度的换算余量（上下留白之和）</summary>
    public float inputTextPaddingY = 14f;

    // ──────────────────────────────
    // 常量（气泡视觉参数与好友聊天一致）
    // ──────────────────────────────
    private const float BubblePadX = 14f;
    private const float BubblePadY = 12f;
    private const float BubbleSideMargin = 16f;
    private const float BubbleSpacing = 8f;
    private const float ListTopPadding = 16f;
    private const float ListBottomPadding = 16f;
    private const int MaxContentLength = 500;

    /// <summary>守岸人气泡左侧为小头像预留的横向空间（30 头像 + 10 间距）</summary>
    private const float AvatarReserve = 40f;

    /// <summary>气泡与左侧头像之间的间距</summary>
    private const float AvatarGap = 10f;

    private const float AvatarSize = 30f;

    /// <summary>左侧气泡最小高度（不矮于头像）</summary>
    private const float MinBubbleHeight = 30f;

    /// <summary>聊天区最小高度（输入框拉到最大时也不能把消息区压没）</summary>
    private const float MinChatAreaHeight = 140f;

    /// <summary>首次见面欢迎语（无历史时本地显示，不调 LLM，计划 §8.1 A6）</summary>
    private const string WelcomeText = "你好呀，我是守岸人，你的专属游戏管家。背包、任务、天气、玩法问题，都可以直接问我。";

    // ──────────────────────────────
    // 运行时状态
    // ──────────────────────────────

    /// <summary>当前场景的控制器实例（zhuye 专属；其它场景恒 null → IsPanelOpen=false，悬浮球据此分流消息）</summary>
    private static AgentView _activeInstance;

    /// <summary>面板是否开着（悬浮球判定"3107/3104 进面板还是弹气泡"的依据）</summary>
    public static bool IsPanelOpen =>
        _activeInstance != null && _activeInstance.gameObject.activeSelf;

    /// <summary>一行对话数据（role 0=玩家 1=阿福）</summary>
    private class AgentRow
    {
        public int Role;
        public string Content;

        /// <summary>流式渲染中（量宽绕开缓存：中间态字符串不进字典防膨胀）</summary>
        public bool Streaming;

        /// <summary>入场序号（>0 = 本条是"新产生的"，首次上屏播入场动画；历史/更早分页=0 不播）</summary>
        public int Seq;

        /// <summary>已经播过入场动画（滚动换绑、池化复用都不重播）</summary>
        public bool Animated;

        /// <summary>上一次绑定的气泡高度（流式行高度平滑用）</summary>
        public float LastHeight;
    }

    /// <summary>当前渲染的对话（含乐观追加的玩家消息）</summary>
    private readonly List<AgentRow> rows = new List<AgentRow>();

    /// <summary>虚拟列表（变高气泡，按 role 分池）</summary>
    private VirtualScrollList chatVirtual;

    /// <summary>隐藏量宽文本（克隆气泡文本，ForceMeshUpdate 量占位）</summary>
    private TMP_Text measureText;

    /// <summary>文字测量缓存（键=角色+内容；聊天区宽度变化时整体失效）</summary>
    private readonly Dictionary<string, Vector2> measuredText = new Dictionary<string, Vector2>();
    private float measuredAreaWidth = -1f;

    /// <summary>历史是否已拉取过（打开面板拉一次；重登重置）</summary>
    private bool historyLoaded;
    /// <summary>是否在等阿福回复（等待中锁发送+显示三点指示器）</summary>
    private bool waitingReply;

    /// <summary>流式渲染中：阿福回复行的索引（-1=当前无流式行）</summary>
    private int streamingRowIndex = -1;

    /// <summary>流式已上屏累计文本</summary>
    private readonly System.Text.StringBuilder streamingText = new System.Text.StringBuilder();

    /// <summary>待上屏的流式增量（合批节流，防每个 token 触发全量刷新）</summary>
    private readonly System.Text.StringBuilder pendingStreamDelta = new System.Text.StringBuilder();

    /// <summary>上次流式上屏时刻（Time.unscaledTime：暂停中照常流式）</summary>
    private float lastStreamFlushTime;

    /// <summary>还有更早历史可翻（3106 has_more；上拉分页 Phase 5 ③）</summary>
    private bool hasMoreHistory;

    /// <summary>上拉加载更早中（防重复触发）</summary>
    private bool isLoadingOlder;

    /// <summary>已加载最早一条的时间戳（上拉游标，UTC 毫秒）</summary>
    private long oldestLoadedMs;

    /// <summary>本次历史请求在途的游标（0=首页整表；>0=更早一页前置插入）</summary>
    private long pendingBeforeMs;

    /// <summary>面板静止位（开合补间回到这里）</summary>
    private Vector2 panelRestPos;

    /// <summary>本次新行序号分配器（>0 才播入场动画；历史行恒为 0）</summary>
    private int seqCounter;

    /// <summary>同帧到达的新气泡错帧计数（LateUpdate 清零）</summary>
    private int entranceQueue;

    /// <summary>平滑滚到底的补间（用户一上手就掐掉，别和手抢）</summary>
    private Tween scrollTween;

    /// <summary>状态文案淡出补间</summary>
    private Tween statusTween;

    /// <summary>入口按钮脉动补间</summary>
    private Tween entryPulseTween;

    /// <summary>自家补间正在写滚动位置（区别于用户滚动：用户一动就掐掉自动滚动）</summary>
    private bool scrollAutoDriving;

    /// <summary>面板正在播开合补间（动画期间忽略入口点击，防手快连点把开关状态搞乱）</summary>
    private bool panelBusy;

    /// <summary>开合动画的"忙碌"计时：用独立延时补间解除——不依赖开合补间本身，
    /// 中途被 Kill（例如收起打断了展开）也不会把标志位卡死</summary>
    private Tween panelBusyTween;

    // ── 输入框自适应（底锚布局）──
    /// <summary>输入框 RectTransform（底锚 + 底轴心：只往上长）</summary>
    private RectTransform inputRect;

    /// <summary>面板半高（面板局部坐标：底边 -panelHalfHeight、顶边 +panelHalfHeight）</summary>
    private float panelHalfHeight;

    /// <summary>面板底边到输入框底边的距离（底锚布局下 = 输入框 anchoredPosition.y）</summary>
    private float inputBottomFromBottom;

    /// <summary>状态行中心相对"输入框顶边"的偏移（最小高度时标定一次，之后跟着走）</summary>
    private float statusGap;

    /// <summary>聊天区底边相对"输入框顶边"的偏移（最小高度时标定一次）</summary>
    private float chatGap;

    /// <summary>聊天区顶边（面板局部 y，固定不动：输入框长高只从下方吃高度）</summary>
    private float chatTopLocal;

    /// <summary>布局参数是否已标定（Start 时面板可能是 inactive，矩形要等布局齐了才有意义）</summary>
    private bool inputLayoutReady;

    /// <summary>上一帧的光标位置（只在光标变化时跟随，避免每帧强制可见把用户的滚轮弹回去）</summary>
    private int lastCaretIndex = -1;

    /// <summary>视口世界角点临时数组（避免每帧分配）</summary>
    private static readonly Vector3[] ViewportCorners = new Vector3[4];

    /// <summary>绑定完成后的一次性初始化：订阅网络回调 + 建虚拟列表 + 量布局（实例常驻，只做一次）</summary>
    public override void OnInit(UIControlData uIControlData, UIViewController controller)
    {
        base.OnInit(uIControlData, controller);

        _activeInstance = this;

        // wrapper 只负责全屏容器，动画作用在 AgentPanel 本身：绑定器按面板根解析不到自身，这里补解析
        if (panelRect == null) panelRect = transform.Find("AgentPanel") as RectTransform;
        if (panelCanvas == null && panelRect != null) panelCanvas = panelRect.GetComponent<CanvasGroup>();

        if (panelCanvas != null) panelCanvas.alpha = 1f;
        if (panelRect != null) panelRestPos = panelRect.anchoredPosition;

        ResolveZoomButtons();
        LoadPanelTransform();
        ApplyZoom(zoomLevel, false);

        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net != null)
        {
            net.AgentChatResponseReceived += OnAgentChatResponse;
            net.AgentChatChunkReceived += OnAgentChatChunk;
            net.AgentBriefNotifyReceived += OnAgentBriefNotify;
            net.AgentHistoryResponseReceived += OnAgentHistoryResponse;
            net.AgentProactiveNotifyReceived += OnAgentProactiveNotify;
            net.LoginResponseReceived += OnLoginResponse;
        }

        SetupVirtualList();
        CaptureInputLayout();
        if (chatInput != null) chatInput.onValueChanged.AddListener(OnChatInputChanged);
        ResizeChatInput(true);
        ApplyAvatarSprite();
        SetThinking(false);
    }

    private void OnDestroy()
    {
        if (_activeInstance == this)
        {
            _activeInstance = null;
            AgentFloatBall.NotifyAgentPanelOpen(false);   // 控制器没了（切场景）：悬浮球按"面板关"回来
        }
        if (messageScroll != null) messageScroll.onValueChanged.RemoveListener(OnMessageScrollChanged);
        if (chatInput != null) chatInput.onValueChanged.RemoveListener(OnChatInputChanged);
        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net != null)
        {
            net.AgentChatResponseReceived -= OnAgentChatResponse;
            net.AgentChatChunkReceived -= OnAgentChatChunk;
            net.AgentBriefNotifyReceived -= OnAgentBriefNotify;
            net.AgentHistoryResponseReceived -= OnAgentHistoryResponse;
            net.AgentProactiveNotifyReceived -= OnAgentProactiveNotify;
            net.LoginResponseReceived -= OnLoginResponse;
        }

        // 补间随对象一起走，别留在 DOTween 里打到已销毁的目标上
        scrollTween?.Kill();
        statusTween?.Kill();
        entryPulseTween?.Kill();
        panelBusyTween?.Kill();
        AgentTweenFx.StopThinkingDots(thinkingDotsRoot);
        if (panelRect != null) panelRect.DOKill();
        if (panelCanvas != null) panelCanvas.DOKill();
        if (entryButton != null) entryButton.transform.DOKill();
    }

    private void LateUpdate()
    {
        entranceQueue = 0;   // 错帧计数按帧清零：同帧同批才错开

        // 光标跟随轮询：方向键/鼠标点选光标不触发 onValueChanged，靠光标位置变化补跟；
        // 光标没动就绝不插手，否则每帧强制可见会把用户滚轮翻看的内容弹回光标处（好友面板踩过）
        if (chatInput == null || !chatInput.isFocused)
        {
            lastCaretIndex = -1;
            return;
        }
        int caret = chatInput.caretPosition;
        if (caret == lastCaretIndex) return;
        lastCaretIndex = caret;
        ScrollCaretIntoView(false);
    }

    // ──────────────────────────────
    // 面板开关（与其它面板互斥）
    // ──────────────────────────────

    public override void OnAddListener()
    {
        if (closeButton != null) closeButton.onClick.AddListener(ClosePanel);
        if (sendButton != null) sendButton.onClick.AddListener(OnSendClicked);
        if (chatInput != null) chatInput.onSubmit.AddListener(OnSubmitFromInput);
        if (zoomOutButton != null) zoomOutButton.onClick.AddListener(StepZoomOut);
        if (zoomInButton != null) zoomInButton.onClick.AddListener(StepZoomIn);
    }

    public override void OnRemoveListener()
    {
        if (closeButton != null) closeButton.onClick.RemoveListener(ClosePanel);
        if (sendButton != null) sendButton.onClick.RemoveListener(OnSendClicked);
        if (chatInput != null) chatInput.onSubmit.RemoveListener(OnSubmitFromInput);
        if (zoomOutButton != null) zoomOutButton.onClick.RemoveListener(StepZoomOut);
        if (zoomInButton != null) zoomInButton.onClick.RemoveListener(StepZoomIn);
    }

    private void OnSubmitFromInput(string _) => OnSendClicked();

    public override void OnOpen(object userData)
    {
        base.OnOpen(userData);
        _isOpen = true;

        // 面板本体的激活由本类负责：框架只激活"视图根"（全屏透明 wrapper），
        // 预制体里 AgentPanel 可能是 inactive —— 曾因此出现"面板打开了却看不见"（2026-10-21）。
        if (panelRect != null && !panelRect.gameObject.activeSelf)
        {
            panelRect.gameObject.SetActive(true);
        }

        AgentFloatBall.NotifyAgentPanelOpen(true);   // 球与面板是同一个角色：面板上台，球隐身
        StopEntryPulse();
        SetPanelBusy(AgentTweenFx.PanelOpenDuration + 0.06f);
        ApplyZoom(zoomLevel, false);   // 重新激活后面板位置/缩放可能被框架复位：先钉回玩家上次的摆放
        AgentTweenFx.ShowPanel(panelRect, panelCanvas, panelRestPos, zoomLevel, null);
        ResizeChatInput(true);   // 面板重新激活后矩形才可靠：按当前文本重算一次输入框与联动布局

        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net == null || net.MyPlayerId <= 0)
        {
            // 未登录：面板可开但不聊天（与活动面板的提示口径一致）
            rows.Clear();
            chatVirtual?.RefreshData();
            SetThinking(false);
            SetStatus("请先登录后再找守岸人聊天");
            return;
        }

        if (!historyLoaded)
        {
            SetStatus("正在打开与守岸人的对话…");
            _ = net.SendAgentHistory(20);
        }
        else
        {
            SetStatus(string.Empty);
            chatVirtual?.RefreshData();
            ScrollToBottom(false);
        }
    }

    /// <summary>关闭键：先播收起动画，动画结束再交给框架关（框架负责 SetActive/排序归还）</summary>
    private void ClosePanel()
    {
        if (!_isOpen) return;
        SetStatus(string.Empty);
        SetThinking(false);
        scrollTween?.Kill();
        scrollTween = null;
        SetPanelBusy(AgentTweenFx.PanelCloseDuration + 0.02f);
        AgentTweenFx.HidePanel(panelRect, panelCanvas, panelRestPos, zoomLevel, () =>
        {
            UIManager.Instance.Close(UIType.AgentView).Forget();
        });
    }

    public override void OnClose()
    {
        base.OnClose();
        _isOpen = false;
        AgentFloatBall.NotifyAgentPanelOpen(false);   // 面板收场，球回来
        panelBusy = false;
        panelBusyTween?.Kill();
        panelBusyTween = null;
    }

    // ──────────────────────────────
    // 面板拖动与缩放（2026-10-23）
    // 拖动挂在视图根（全屏 wrapper）上：按下点落在面板矩形内、且不在滚动区/按钮/输入框上才接管；
    // 缩放走整块 localScale（内部是绝对坐标布局，改尺寸不会重排，等比缩放才不会破版）。
    // ──────────────────────────────

    /// <summary>缩放档位索引（E2E 断言用）</summary>
    public int ZoomIndex => zoomIndex;

    /// <summary>当前缩放倍数（E2E 断言用）</summary>
    public float ZoomLevel => zoomLevel;

    /// <summary>面板静止位（开合补间/持久化的唯一真相）</summary>
    public Vector2 PanelRestPosition => panelRestPos;

    /// <summary>按名字解析缩放按钮（预制体由 AgentViewBeautifier 生成，不走 UIControlData）</summary>
    private void ResolveZoomButtons()
    {
        if (panelRect == null) return;
        if (zoomOutButton == null) zoomOutButton = panelRect.Find("ZoomOutButton")?.GetComponent<Button>();
        if (zoomInButton == null) zoomInButton = panelRect.Find("ZoomInButton")?.GetComponent<Button>();
    }

    /// <summary>读取上次的摆放（位置 + 缩放档位）；没有存档就用默认值</summary>
    private void LoadPanelTransform()
    {
        if (panelRect == null) return;

        if (PlayerPrefs.HasKey(PanelZoomKey))
        {
            zoomIndex = NearestZoomIndex(PlayerPrefs.GetFloat(PanelZoomKey, ZoomLevels[DefaultZoomIndex]));
            zoomLevel = ZoomLevels[zoomIndex];
        }

        if (PlayerPrefs.HasKey(PanelPosKeyX) && PlayerPrefs.HasKey(PanelPosKeyY))
        {
            panelRestPos = new Vector2(PlayerPrefs.GetFloat(PanelPosKeyX), PlayerPrefs.GetFloat(PanelPosKeyY));
        }
    }

    private static int NearestZoomIndex(float value)
    {
        int best = DefaultZoomIndex;
        float bestDelta = float.MaxValue;
        for (int i = 0; i < ZoomLevels.Length; i++)
        {
            float delta = Mathf.Abs(ZoomLevels[i] - value);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                best = i;
            }
        }
        return best;
    }

    private void StepZoomOut() => StepZoom(-1);

    private void StepZoomIn() => StepZoom(1);

    /// <summary>按档位缩放（开合动画期间忽略，避免和补间抢 localScale）</summary>
    private void StepZoom(int delta)
    {
        if (panelRect == null || panelBusy) return;

        int next = Mathf.Clamp(zoomIndex + delta, 0, ZoomLevels.Length - 1);
        if (next == zoomIndex)
        {
            UpdateZoomButtons();
            return;
        }

        zoomIndex = next;
        ApplyZoom(ZoomLevels[zoomIndex], true);
    }

    /// <summary>应用缩放：写 localScale、按新尺寸重新钳制位置、刷新按钮可用态</summary>
    private void ApplyZoom(float value, bool save)
    {
        zoomLevel = Mathf.Clamp(value, ZoomLevels[0], ZoomLevels[ZoomLevels.Length - 1]);

        if (panelRect != null)
        {
            panelRect.localScale = Vector3.one * zoomLevel;
            panelRect.anchoredPosition = ClampPanelPosition(panelRect.anchoredPosition);
            panelRestPos = panelRect.anchoredPosition;
        }

        UpdateZoomButtons();
        if (save) SavePanelTransform();
    }

    private void UpdateZoomButtons()
    {
        if (zoomOutButton != null) zoomOutButton.interactable = zoomIndex > 0;
        if (zoomInButton != null) zoomInButton.interactable = zoomIndex < ZoomLevels.Length - 1;
    }

    /// <summary>
    /// 把面板钳在父级矩形内（按缩放后的实际尺寸；面板锚点为居中，所以 anchoredPosition 以父级中心为原点）。
    /// 面板比父级还大时钳到居中，保证不会整块跑出屏幕。
    /// </summary>
    private Vector2 ClampPanelPosition(Vector2 pos)
    {
        if (panelRect == null) return pos;
        RectTransform parent = panelRect.parent as RectTransform;
        if (parent == null) return pos;

        Vector2 size = panelRect.rect.size * zoomLevel;
        Vector2 parentSize = parent.rect.size;
        float maxX = Mathf.Max(0f, (parentSize.x - size.x) * 0.5f);
        float maxY = Mathf.Max(0f, (parentSize.y - size.y) * 0.5f);
        return new Vector2(Mathf.Clamp(pos.x, -maxX, maxX), Mathf.Clamp(pos.y, -maxY, maxY));
    }

    private void SavePanelTransform()
    {
        PlayerPrefs.SetFloat(PanelPosKeyX, panelRestPos.x);
        PlayerPrefs.SetFloat(PanelPosKeyY, panelRestPos.y);
        PlayerPrefs.SetFloat(PanelZoomKey, zoomLevel);
        PlayerPrefs.Save();
    }

    /// <summary>按下点是否落在交互控件上（滚动区/按钮/输入框自己处理拖拽，面板不抢）</summary>
    private bool IsPointerOnInteractive(PointerEventData eventData)
    {
        if (EventSystem.current == null) return false;

        dragRaycastHits.Clear();
        EventSystem.current.RaycastAll(eventData, dragRaycastHits);
        for (int i = 0; i < dragRaycastHits.Count; i++)
        {
            GameObject hit = dragRaycastHits[i].gameObject;
            if (hit == null) continue;
            if (hit.GetComponentInParent<ScrollRect>() != null) return true;
            if (hit.GetComponentInParent<Selectable>() != null) return true;
        }
        return false;
    }

    public void OnBeginDrag(PointerEventData eventData)
    {
        if (!_isOpen || panelRect == null) return;
        if (eventData.button != PointerEventData.InputButton.Left) return;
        if (panelBusy) return;   // 开合动画期间不接管，避免和补间抢 anchoredPosition
        if (!RectTransformUtility.RectangleContainsScreenPoint(panelRect, eventData.position, eventData.pressEventCamera)) return;
        if (IsPointerOnInteractive(eventData)) return;

        RectTransform parent = panelRect.parent as RectTransform;
        if (parent == null) return;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, eventData.position, eventData.pressEventCamera, out dragStartPointerLocal)) return;

        dragStartPanelPos = panelRect.anchoredPosition;
        panelDragging = true;

        // 拖动即接管坐标：掐掉可能还在跑的开合补间
        panelRect.DOKill();
        panelCanvas?.DOKill();
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (!panelDragging || panelRect == null) return;

        RectTransform parent = panelRect.parent as RectTransform;
        if (parent == null) return;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, eventData.position, eventData.pressEventCamera, out Vector2 local)) return;

        // 位移在父级空间里算（面板自身缩放不影响父级坐标），再按缩放后的尺寸钳制
        panelRect.anchoredPosition = ClampPanelPosition(dragStartPanelPos + (local - dragStartPointerLocal));
        panelRestPos = panelRect.anchoredPosition;
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        if (!panelDragging) return;
        panelDragging = false;
        SavePanelTransform();
    }

    // ──────────────────────────────
    // 悬浮球入口（一个 Agent 两个用法：球是主动消息的台面，面板是对话的台面）
    // ──────────────────────────────

    /// <summary>悬浮球点按入口：打开阿福面板；返回 false=当前场景没有面板（调用方给本地提示）。</summary>
    public static bool TryOpenFromFloatBall()
    {
        if (_activeInstance != null)
        {
            _activeInstance.OpenFromFloatBall();
            return true;
        }

        // 首次从悬浮球进入：AgentView 尚未实例化（UIManager 懒加载），不能因为 _activeInstance 为空就拒绝。
        // 阿福面板只在 zhuye 提供；其它场景仍返回 false，由悬浮球弹本地提示。
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "zhuye") return false;
        UIManager.Instance.Open(UIType.AgentView).Forget();
        return true;
    }

    /// <summary>悬浮球专用的打开入口（开合动画期间的连点守卫与入口按钮一致）。</summary>
    private void OpenFromFloatBall()
    {
        if (panelBusy) return;
        if (_isOpen) return;
        _isOpen = true;
        UIManager.Instance.Open(UIType.AgentView).Forget();
    }

    /// <summary>换号/重登：清空本地对话缓存，等下次打开重新拉历史（防串号）</summary>
    private void OnLoginResponse(LoginResponse response)
    {
        if (!response.Success) return;
        rows.Clear();
        historyLoaded = false;
        measuredText.Clear();
        waitingReply = false;
        ClearStreamingState();
        hasMoreHistory = false;
        isLoadingOlder = false;
        oldestLoadedMs = 0;
        pendingBeforeMs = 0;
        if (sendButton != null) sendButton.interactable = true;
        SetThinking(false);
        if (_isOpen && ClientNetworkManager.Instance != null)
        {
            SetStatus("正在打开与守岸人的对话…");
            _ = ClientNetworkManager.Instance.SendAgentHistory(20);
        }
    }

    // ──────────────────────────────
    // 发送与回执
    // ──────────────────────────────

    private void OnSendClicked()
    {
        if (waitingReply || !_isOpen) return;

        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net == null || net.MyPlayerId <= 0)
        {
            SetStatus("请先登录后再找守岸人聊天");
            ShakeInput();
            return;
        }
        if (!net.IsConnected)
        {
            SetStatus("网络未连接，请稍后再试", true);
            ShakeInput();
            return;
        }

        string content = chatInput != null ? chatInput.text.Trim() : string.Empty;
        if (content.Length == 0)
        {
            SetStatus("消息内容不能为空", true);
            ShakeInput();
            return;
        }
        if (content.Length > MaxContentLength)
        {
            SetStatus($"消息内容不能超过{MaxContentLength}字", true);
            ShakeInput();
            return;
        }

        // 乐观显示：玩家消息先上屏（网关拒绝时回滚这一行）
        rows.Add(new AgentRow { Role = 0, Content = content, Seq = NextSeq() });
        chatVirtual?.RefreshData();
        ScrollToBottom(true);

        waitingReply = true;
        if (sendButton != null) sendButton.interactable = false;
        SetStatus(string.Empty);
        SetThinking(true);

        if (chatInput != null)
        {
            chatInput.text = string.Empty;
            chatInput.ActivateInputField();   // 发送后保焦点，连续对话不用再点输入框
        }

        _ = net.SendAgentChat(content);
    }

    private void OnAgentChatResponse(AgentChatResponse response)
    {
        if (!_isOpen)
        {
            // 面板已关：回复照常入库，下次打开从历史里看到（消息不丢，§5.9 A1）；
            // 顺手用入口按钮脉动提示"阿福回话了"
            waitingReply = false;
            ClearStreamingState();
            SetThinking(false);
            if (sendButton != null) sendButton.interactable = true;
            if (response.Ok) PulseEntry();
            return;
        }

        waitingReply = false;
        SetThinking(false);
        if (sendButton != null) sendButton.interactable = true;

        if (response.Ok)
        {
            // 流式行已渲染过正文：校准成权威终值（respond 截断/兜底都在 3102 里）；无流式行=整段到达，直接新增
            if (streamingRowIndex >= 0 && streamingRowIndex < rows.Count)
            {
                rows[streamingRowIndex].Content = response.Reply;
                rows[streamingRowIndex].Streaming = false;
            }
            else
            {
                rows.Add(new AgentRow { Role = 1, Content = response.Reply, Seq = NextSeq() });
            }
            ClearStreamingState();
            SetStatus(string.Empty);
        }
        else
        {
            // 网关拒绝（限频/Agent 不可达）：流式行与乐观行都回滚，让界面与真实历史一致
            RemoveStreamingRow();
            RemoveLastPlayerRow();
            SetStatus(response.Error, true);
            ShakeStatus();
        }

        chatVirtual?.RefreshData();
        ScrollToBottom(true);
    }

    /// <summary>3104 登录简报推送到达（Phase 5 ②）：面板开着就上屏；关着只做入口脉动——简报已归档服务端历史，下次开面板自然看到（悬浮球也会弹"上线问好"）</summary>
    private void OnAgentBriefNotify(AgentBriefNotify notify)
    {
        if (string.IsNullOrEmpty(notify.Content)) return;
        if (!_isOpen)
        {
            PulseEntry();
            return;
        }
        rows.Add(new AgentRow { Role = 1, Content = notify.Content, Seq = NextSeq() });
        chatVirtual?.RefreshData();
        ScrollToBottom(true);
    }

    /// <summary>3107 主动提醒到达（悬浮球·一个 Agent 两个用法）：面板开着进聊天流（与简报同款上屏）；
    /// 关着由悬浮球弹气泡——球自己订阅并按 IsPanelOpen 分流，这里不抢不重复。</summary>
    private void OnAgentProactiveNotify(AgentProactiveNotify notify)
    {
        if (string.IsNullOrEmpty(notify.Content)) return;
        if (!_isOpen) return;   // 面板关着：展示职责在悬浮球

        rows.Add(new AgentRow { Role = 1, Content = notify.Content, Seq = NextSeq() });
        chatVirtual?.RefreshData();
        ScrollToBottom(true);
    }

    /// <summary>3103 流式分片到达：增量入待上屏缓冲；首个分片创建流式行，reset 撤行回思考态</summary>
    private void OnAgentChatChunk(AgentChatChunk chunk)
    {
        if (!waitingReply || !_isOpen) return;   // 面板关/非在途：分片忽略（3102 终值兜底）

        if (chunk.Reset)
        {
            // 工具调用轮的前导文本作废：撤掉流式行，回思考态等真答案
            if (streamingRowIndex >= 0)
            {
                RemoveStreamingRow();
                chatVirtual?.RefreshData();
            }
            SetThinking(true);
            return;
        }

        if (chunk.Delta.Length == 0) return;
        pendingStreamDelta.Append(chunk.Delta);

        if (streamingRowIndex < 0)
        {
            // 首个分片：流式行上屏占位（正文走 Update 节流刷新），思考指示器让位
            rows.Add(new AgentRow { Role = 1, Content = string.Empty, Streaming = true, Seq = NextSeq() });
            streamingRowIndex = rows.Count - 1;
            SetThinking(false);
        }
    }

    /// <summary>流式增量上屏：合并进流式行并刷新（Update 节流调用）</summary>
    private void FlushStreamDelta()
    {
        lastStreamFlushTime = Time.unscaledTime;
        streamingText.Append(pendingStreamDelta);
        pendingStreamDelta.Length = 0;

        if (streamingRowIndex >= 0 && streamingRowIndex < rows.Count)
        {
            rows[streamingRowIndex].Content = streamingText.ToString();
            chatVirtual?.RefreshData();
            ScrollToBottom(true);
        }
    }

    /// <summary>清空流式渲染状态（不动 rows，行归属由调用方处理）</summary>
    private void ClearStreamingState()
    {
        streamingRowIndex = -1;
        streamingText.Length = 0;
        pendingStreamDelta.Length = 0;
    }

    /// <summary>撤掉流式行（若在）：半截回复从面板上移除</summary>
    private void RemoveStreamingRow()
    {
        if (streamingRowIndex >= 0 && streamingRowIndex < rows.Count) rows.RemoveAt(streamingRowIndex);
        ClearStreamingState();
    }

    private void OnAgentHistoryResponse(AgentHistoryResponse response)
    {
        ClearStreamingState();   // 历史替换后流式行索引已失效
        bool isOlderPage = response.Ok && pendingBeforeMs > 0;
        pendingBeforeMs = 0;

        if (!response.Ok)
        {
            if (isLoadingOlder)
            {
                isLoadingOlder = false;
                SetStatus("更早的历史加载失败，请稍后再试", true);
            }
            else
            {
                rows.Clear();
                historyLoaded = true;
                SetStatus("历史加载失败，请稍后再试", true);
                chatVirtual?.RefreshData();
            }
            return;
        }

        if (isOlderPage)
        {
            // 更早一页：前置插入（旧→新），把原首条钉在视口顶部防跳视（世界频道同款口径）
            isLoadingOlder = false;
            hasMoreHistory = response.HasMore;
            if (response.Items.Count > 0)
            {
                oldestLoadedMs = response.Items[0].CreatedMs;
                float addedHeight = 0f;
                for (int i = 0; i < response.Items.Count; i++)
                {
                    // Seq 缺省 0：翻出来的旧消息不播入场动画
                    AgentRow olderRow = new AgentRow { Role = response.Items[i].Role, Content = response.Items[i].Content };
                    addedHeight += MeasureRow(olderRow).y + BubbleSpacing;
                    rows.Insert(i, olderRow);
                }
                chatVirtual?.RefreshData();
                PreserveScrollAfterPrepend(addedHeight);
            }
            SetStatus(string.Empty);
            return;
        }

        // 首页整表替换（历史行 Seq=0：整屏旧消息一起淡入会很吵，直接就到齐）
        rows.Clear();
        foreach (AgentChatMessage item in response.Items)
        {
            rows.Add(new AgentRow { Role = item.Role, Content = item.Content });
        }
        // 首次见面：无历史时显示本地欢迎语（灵宝式打招呼的最小版，不调 LLM）——这条走入场动画
        if (rows.Count == 0) rows.Add(new AgentRow { Role = 1, Content = WelcomeText, Seq = NextSeq() });
        oldestLoadedMs = response.Items.Count > 0 ? response.Items[0].CreatedMs : 0;
        hasMoreHistory = response.HasMore;
        historyLoaded = true;
        isLoadingOlder = false;
        waitingReply = false;
        SetThinking(false);
        if (sendButton != null) sendButton.interactable = true;

        SetStatus(string.Empty);
        chatVirtual?.RefreshData();
        ScrollToBottom(false);
    }

    /// <summary>滚到顶部拉更早一页（Phase 5 ③"上拉加载更早"入口，世界频道同款守卫）</summary>
    private void OnMessageScrollChanged(Vector2 position)
    {
        if (messageScroll == null || chatVirtual == null) return;

        // 自家补间写位置时不算"用户滚动"；用户一上手（滚轮/拖拽）就让自动滚动让位
        if (scrollAutoDriving) return;
        if (scrollTween != null && scrollTween.IsActive())
        {
            scrollTween.Kill();
            scrollTween = null;
        }

        if (!historyLoaded || isLoadingOlder || waitingReply) return;
        if (messageScroll.verticalNormalizedPosition < 0.999f) return;   // 只有真正贴顶才触发

        // 内容不足一屏时不触发（否则一直"到顶"连拉，把历史一次拉干）
        RectTransform viewport = messageScroll.viewport;
        if (viewport != null && chatVirtual.ContentHeight <= viewport.rect.height + 0.5f) return;

        if (!hasMoreHistory)
        {
            if (statusText != null && statusText.text.Length == 0) SetStatus("没有更早的消息了", true);
            return;
        }

        isLoadingOlder = true;
        pendingBeforeMs = oldestLoadedMs;
        SetStatus("正在加载更早的消息…");
        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net != null) _ = net.SendAgentHistory(20, oldestLoadedMs);
    }

    /// <summary>前置插入后保持视线位置：归一化位置 = 1 - 新增高度/可滚动高度（原首条仍钉视口顶）</summary>
    private void PreserveScrollAfterPrepend(float addedHeight)
    {
        if (messageScroll == null || chatVirtual == null) return;
        scrollTween?.Kill();
        scrollTween = null;
        Canvas.ForceUpdateCanvases();   // Content 尺寸刚变，先让 ScrollRect bounds 跟上，否则赋值空操作
        RectTransform viewport = messageScroll.viewport;
        float viewportHeight = viewport != null ? viewport.rect.height : 0f;
        float scrollable = Mathf.Max(0f, chatVirtual.ContentHeight - viewportHeight);
        if (scrollable <= 0.5f) return;
        messageScroll.verticalNormalizedPosition = Mathf.Clamp01(1f - addedHeight / scrollable);
        chatVirtual?.RefreshView();
    }

    /// <summary>回滚最后一条乐观玩家消息（网关拒绝时）</summary>
    private void RemoveLastPlayerRow()
    {
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            if (rows[i].Role == 0)
            {
                rows.RemoveAt(i);
                break;
            }
        }
    }

    // ──────────────────────────────
    // 每帧：流式合批上屏
    // ──────────────────────────────

    private void Update()
    {
        // 流式合批上屏：增量攒着 ~12 次/秒刷一次（暂停中照常——unscaledTime）；
        // "思考中"由三点指示器补间负责，这里不再改状态文案
        if (streamingRowIndex >= 0 && pendingStreamDelta.Length > 0
            && Time.unscaledTime - lastStreamFlushTime >= 0.08f)
        {
            FlushStreamDelta();
        }
    }

    // ──────────────────────────────
    // 状态行 / 思考指示器 / 提示反馈
    // ──────────────────────────────

    /// <summary>设置状态行文案（transient=true 表示是"一次性提示"，几秒后自动淡出清空）。</summary>
    private void SetStatus(string text, bool transient = false)
    {
        if (statusText == null) return;

        statusTween?.Kill();
        statusTween = null;

        statusText.text = text ?? string.Empty;
        Color color = statusText.color;
        color.a = 1f;
        statusText.color = color;

        if (transient && !string.IsNullOrEmpty(text))
        {
            statusTween = AgentTweenFx.FadeStatus(statusText);
            if (statusTween != null)
            {
                statusTween.OnComplete(() =>
                {
                    if (statusText == null) return;
                    statusText.text = string.Empty;
                    Color restore = statusText.color;
                    restore.a = 1f;
                    statusText.color = restore;
                });
            }
        }
    }

    /// <summary>三点指示器显隐（等待回复时跳，流式/结束时停）。</summary>
    private void SetThinking(bool visible)
    {
        if (thinkingIndicator == null) return;

        if (visible)
        {
            bool wasHidden = !thinkingIndicator.activeSelf;
            if (wasHidden) thinkingIndicator.SetActive(true);
            if (wasHidden || thinkingDotsRoot == null) AgentTweenFx.StartThinkingDots(thinkingDotsRoot);
        }
        else
        {
            AgentTweenFx.StopThinkingDots(thinkingDotsRoot);
            if (thinkingIndicator.activeSelf) thinkingIndicator.SetActive(false);
        }
    }

    /// <summary>输入框左右抖动（本地校验没过：空消息/超长/未登录）。</summary>
    private void ShakeInput()
    {
        if (chatInput != null) AgentTweenFx.Shake((RectTransform)chatInput.transform);
    }

    /// <summary>状态行抖动（服务端拒绝）。</summary>
    private void ShakeStatus()
    {
        if (statusText != null) AgentTweenFx.Shake((RectTransform)statusText.transform);
    }

    /// <summary>入口按钮脉动（关着面板时阿福回话了）。</summary>
    private void PulseEntry()
    {
        if (entryButton == null) return;
        entryPulseTween?.Kill();
        entryPulseTween = AgentTweenFx.PulseEntry(entryButton.transform);
    }

    private void StopEntryPulse()
    {
        entryPulseTween?.Kill();
        entryPulseTween = null;
        if (entryButton != null) AgentTweenFx.StopEntryPulse(entryButton.transform);
    }

    /// <summary>开合动画期间置忙（seconds 后用独立补间解除）。</summary>
    private void SetPanelBusy(float seconds)
    {
        panelBusy = true;
        panelBusyTween?.Kill();
        panelBusyTween = DOVirtual.DelayedCall(seconds, () => panelBusy = false).SetUpdate(true);
    }

    /// <summary>头像素材就位则替换"福"字占位（素材缺失时保持占位，不影响面板可用）。</summary>
    private void ApplyAvatarSprite()
    {
        if (avatarImage == null || avatarImage.sprite != null) return;
        Sprite sprite = Resources.Load<Sprite>("Agent/shouanren_avatar");
        if (sprite == null) return;
        avatarImage.sprite = sprite;
        avatarImage.enabled = true;
        if (avatarGlyph != null) avatarGlyph.SetActive(false);
    }

    // ──────────────────────────────
    // 输入框自适应（学好友面板的 QQ 式长高）
    // ──────────────────────────────

    /// <summary>
    /// 标定输入框自适应所需的布局常量（Start / 面板刚打开时调用）
    /// 约定（Builder 保证）：输入框"底锚 + 底轴心"，状态行/思考指示器/聊天区"中心锚 + 中心轴心"；
    /// 两个 gap 由"最小高度时的初始布局"反推，所以以后在 Builder 里挪位置不必回来改这里。
    /// </summary>
    private void CaptureInputLayout()
    {
        if (panelRect == null || chatInput == null || chatAreaRect == null) return;

        inputRect = (RectTransform)chatInput.transform;
        panelHalfHeight = panelRect.rect.height * 0.5f;
        inputBottomFromBottom = inputRect.anchoredPosition.y;   // 底锚+底轴心：底边距面板底边
        float baseInputTop = -panelHalfHeight + inputBottomFromBottom + Mathf.Max(inputMinHeight, 1f);

        statusGap = (statusRowRect != null ? statusRowRect.anchoredPosition.y : baseInputTop + 34f) - baseInputTop;
        chatGap = (chatAreaRect.anchoredPosition.y - chatAreaRect.sizeDelta.y * 0.5f) - baseInputTop;
        chatTopLocal = chatAreaRect.anchoredPosition.y + chatAreaRect.sizeDelta.y * 0.5f;
        inputLayoutReady = true;
    }

    /// <summary>输入内容变化：重算输入框高度 + 把光标滚进可视区（清空后自动收回单行）。</summary>
    private void OnChatInputChanged(string _)
    {
        ResizeChatInput(false);
        ScrollCaretIntoView(true);
    }

    /// <summary>
    /// 输入框高度自适应：文字换到几行就长到几行，到 inputMaxHeight 封顶（之后交给输入框内部滚动）
    /// 高度只往上长（底锚），同时让聊天区变矮、状态行上移——"让位"而不是盖住最近几条消息
    /// </summary>
    /// <param name="force">参数名：force，类型：bool，用途：面板刚打开/高度没变也要重排一次联动布局</param>
    private void ResizeChatInput(bool force)
    {
        if (chatInput == null || chatInput.textComponent == null) return;

        if (!inputLayoutReady) CaptureInputLayout();
        if (inputRect == null || !inputLayoutReady) return;

        float preferred = chatInput.textComponent.preferredHeight;
        float target = Mathf.Clamp(preferred + inputTextPaddingY,
                                   Mathf.Max(inputMinHeight, 1f),
                                   Mathf.Max(inputMaxHeight, inputMinHeight));

        bool heightChanged = Mathf.Abs(inputRect.sizeDelta.y - target) >= 0.5f;
        if (!heightChanged && !force) return;   // 高度没变：每次敲键都省下重排

        if (heightChanged) inputRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, target);
        ApplyInputLayout(target);
        // 视口从下方变矮时 ScrollRect 自己保持贴底/贴顶语义，这里不额外滚动，
        // 免得把正在翻看历史的用户每敲一个字就拽回最新消息
    }

    /// <summary>按输入框高度重排"聊天区/状态行/思考指示器"（顶边都不动，只从下方让高度）。</summary>
    private void ApplyInputLayout(float inputHeight)
    {
        float inputTopLocal = -panelHalfHeight + inputBottomFromBottom + inputHeight;

        float statusY = inputTopLocal + statusGap;
        if (statusRowRect != null) statusRowRect.anchoredPosition = new Vector2(statusRowRect.anchoredPosition.x, statusY);
        if (thinkingRect != null) thinkingRect.anchoredPosition = new Vector2(thinkingRect.anchoredPosition.x, statusY);

        if (chatAreaRect != null)
        {
            float bottom = inputTopLocal + chatGap;
            float height = Mathf.Max(chatTopLocal - bottom, MinChatAreaHeight);
            chatAreaRect.sizeDelta = new Vector2(chatAreaRect.sizeDelta.x, height);
            chatAreaRect.anchoredPosition = new Vector2(chatAreaRect.anchoredPosition.x, chatTopLocal - height * 0.5f);
        }

        if (chatVirtual != null) chatVirtual.RefreshView();   // 视口变矮/变高：可见行数要重算
    }

    /// <summary>
    /// 把光标所在行滚进输入框可视区
    /// TMP 多行输入框打字超出视口不会自动跟随（实测），这里按光标字符的世界坐标手动滚动文本；
    /// 直接改文本 anchoredPosition 不会被 TMP 覆写（实测），滚轮仍走 TMP 原生 OnScroll。
    /// 滚动范围与 TMP 一致：[0, 文字总高 - 视口高]，anchoredPos.y 增大 = 内容上移看更下面
    /// </summary>
    /// <param name="forceUpdate">参数名：forceUpdate，类型：bool，用途：文本刚变化时先强制重建网格再取行坐标</param>
    private void ScrollCaretIntoView(bool forceUpdate)
    {
        if (chatInput == null || chatInput.textComponent == null || chatInput.textViewport == null) return;

        TMP_Text text = chatInput.textComponent;
        string value = chatInput.text;
        if (forceUpdate) text.ForceMeshUpdate();

        TMP_TextInfo info = text.textInfo;
        if (info.characterCount == 0)
        {
            text.rectTransform.anchoredPosition = Vector2.zero;   // 空文本复位，防残留偏移挡住新输入首行
            lastCaretIndex = chatInput.caretPosition;
            return;
        }

        int caret = Mathf.Clamp(chatInput.caretPosition, 0, value.Length);
        TMP_CharacterInfo character = info.characterInfo[Mathf.Min(caret, info.characterCount - 1)];

        Transform textTransform = text.transform;
        float lineHeight = info.lineInfo[info.lineCount - 1].lineHeight;
        float topWorldY = textTransform.TransformPoint(new Vector3(0f, character.topRight.y, 0f)).y;
        float bottomWorldY = textTransform.TransformPoint(new Vector3(0f, character.bottomRight.y, 0f)).y;

        // 文本以换行结尾时，光标实际落在最后一行的下一行
        if (caret >= value.Length && value.EndsWith("\n"))
        {
            topWorldY += lineHeight;
            bottomWorldY += lineHeight;
        }

        RectTransform viewport = chatInput.textViewport;
        viewport.GetWorldCorners(ViewportCorners);
        float viewTop = ViewportCorners[2].y;
        float viewBottom = ViewportCorners[0].y;

        float delta = 0f;
        if (topWorldY > viewTop) delta = viewTop - topWorldY;                    // 光标在视口上方：内容下移
        else if (bottomWorldY < viewBottom) delta = viewBottom - bottomWorldY;   // 光标在视口下方：内容上移
        if (Mathf.Abs(delta) < 0.01f) return;

        RectTransform textRect = text.rectTransform;
        float maxScroll = Mathf.Max(0f, text.preferredHeight - viewport.rect.height);
        textRect.anchoredPosition = new Vector2(0f, Mathf.Clamp(textRect.anchoredPosition.y + delta, 0f, maxScroll));
    }

    // ──────────────────────────────
    // 虚拟列表（复用好友聊天的量高气泡模式）
    // ──────────────────────────────

    private void SetupVirtualList()
    {
        if (messageScroll == null) return;

        chatVirtual = messageScroll.GetComponent<VirtualScrollList>();
        if (chatVirtual == null) chatVirtual = messageScroll.gameObject.AddComponent<VirtualScrollList>();

        chatVirtual.Configure(ListTopPadding, ListBottomPadding, BubbleSpacing);
        chatVirtual.GetItemCount = () => rows.Count;
        chatVirtual.GetItemHeight = index => MeasureRow(rows[index]).y;
        chatVirtual.GetPoolKey = index => rows[index].Role;   // 玩家/阿福 分池
        chatVirtual.CreateItem = index => Instantiate(
            rows[index].Role == 0 ? bubbleRightTemplate : bubbleLeftTemplate,
            messageListContent).GetComponent<RectTransform>();
        chatVirtual.BindItem = BindRow;
        // 回收时掐掉在跑的入场/高度补间：池里的对象不该带着旧补间复活
        chatVirtual.RecycleItem = AgentTweenFx.ResetBubble;

        // 上拉加载更早：滚到顶触发（Phase 5 ③）
        messageScroll.onValueChanged.AddListener(OnMessageScrollChanged);

        // 隐藏量宽文本：克隆气泡文本（同字体/字号/换行），挂滚动区根下（不在 Content，不被清理）
        if (measureText == null && bubbleLeftTemplate != null && messageListContent != null)
        {
            TMP_Text templateText = bubbleLeftTemplate.transform.Find("BubbleText").GetComponent<TMP_Text>();
            measureText = Instantiate(templateText, messageListContent.parent);
            measureText.gameObject.name = "AgentBubbleMeasurer";
            measureText.raycastTarget = false;
            measureText.text = string.Empty;
            ((RectTransform)measureText.transform).anchoredPosition = new Vector2(0f, 100000f);
        }
    }

    /// <summary>下一个新行序号（>0 才播入场动画）。</summary>
    private int NextSeq()
    {
        return ++seqCounter;
    }

    /// <summary>本帧内同批新气泡的错帧延迟（每帧清零，一批十条也只错开 0.2s 封顶）。</summary>
    private float NextEntranceDelay()
    {
        float delay = Mathf.Min(AgentTweenFx.BubbleStaggerMax, entranceQueue * AgentTweenFx.BubbleStaggerStep);
        entranceQueue++;
        return delay;
    }

    /// <summary>某角色的气泡文字最大宽度（阿福侧要给左侧小头像让位）。</summary>
    private float MaxTextWidth(int role)
    {
        float areaWidth = messageListContent != null ? messageListContent.rect.width : 0f;
        float reserve = role == 0 ? 0f : AvatarReserve;
        return Mathf.Max(120f, areaWidth - 2f * (BubbleSideMargin + BubblePadX) - reserve);
    }

    /// <summary>量文字占位（带缓存；聊天区宽度变化时缓存整体失效）。</summary>
    private Vector2 MeasureText(string content, int role)
    {
        float areaWidth = messageListContent != null ? messageListContent.rect.width : 0f;
        if (Mathf.Abs(measuredAreaWidth - areaWidth) > 0.5f)
        {
            measuredText.Clear();
            measuredAreaWidth = areaWidth;
        }

        string key = role + "|" + content;
        if (measuredText.TryGetValue(key, out Vector2 size)) return size;

        size = MeasureTextRaw(content, role);
        measuredText[key] = size;
        return size;
    }

    /// <summary>不带缓存的量宽（流式行专用：中间态字符串不进缓存字典，防膨胀）。</summary>
    private Vector2 MeasureTextUncached(string content, int role)
    {
        return MeasureTextRaw(content, role);
    }

    private Vector2 MeasureTextRaw(string content, int role)
    {
        if (measureText == null) return new Vector2(60f, 20f);

        float maxTextWidth = MaxTextWidth(role);
        measureText.text = content ?? string.Empty;
        measureText.rectTransform.sizeDelta = new Vector2(maxTextWidth, measureText.rectTransform.sizeDelta.y);
        measureText.ForceMeshUpdate();
        return new Vector2(Mathf.Min(maxTextWidth, measureText.renderedWidth) + 1f, measureText.renderedHeight);
    }

    /// <summary>量一条消息的气泡尺寸（文字占位 + 内边距；流式行绕开缓存）。</summary>
    private Vector2 MeasureRow(AgentRow row)
    {
        if (row == null) return new Vector2(60f, 20f);
        Vector2 textSize = row.Streaming ? MeasureTextUncached(row.Content, row.Role) : MeasureText(row.Content, row.Role);
        float height = textSize.y + 2f * BubblePadY;
        if (row.Role != 0) height = Mathf.Max(height, MinBubbleHeight);
        return new Vector2(textSize.x + 2f * BubblePadX, height);
    }

    /// <summary>绑定气泡（池化复用）：按测量撑开、填内容、左右定位；新行播一次入场动画。</summary>
    private void BindRow(int index, RectTransform item)
    {
        AgentRow row = rows[index];
        bool fromMe = row.Role == 0;

        AgentTweenFx.ResetBubble(item);   // 池化复用：先杀旧补间并复位缩放/透明度

        Vector2 textSize = row.Streaming ? MeasureTextUncached(row.Content, row.Role) : MeasureText(row.Content, row.Role);
        Vector2 bubbleSize = new Vector2(textSize.x + 2f * BubblePadX, textSize.y + 2f * BubblePadY);
        if (!fromMe) bubbleSize.y = Mathf.Max(bubbleSize.y, MinBubbleHeight);

        item.name = $"Msg_{index}";   // 对齐家族命名约定（FriendUIController.BindChatBubble 同款）
        item.sizeDelta = bubbleSize;

        TMP_Text text = item.transform.Find("BubbleText")?.GetComponent<TMP_Text>();
        if (text != null)
        {
            text.text = EscapeRichText(row.Content);
            text.rectTransform.sizeDelta = textSize;
            text.rectTransform.anchoredPosition = Vector2.zero;   // 文本回填气泡中心
        }

        // 玩家靠右、阿福靠左（x 按气泡实际宽计算，y 由虚拟列表写入）；
        // 阿福侧额外让出小头像的横向空间，头像才不会顶出聊天区左缘
        float half = messageListContent.rect.width * 0.5f;
        float x = fromMe
            ? half - BubbleSideMargin - bubbleSize.x * 0.5f
            : -half + BubbleSideMargin + AvatarReserve + bubbleSize.x * 0.5f;
        item.anchoredPosition = new Vector2(x, item.anchoredPosition.y);

        if (!fromMe && item.transform.Find("MsgAvatar") is RectTransform avatar)
        {
            // 小头像贴在气泡左下（相对气泡中心：左移半个气泡+间距+半径，下移到气泡底边附近）
            avatar.anchoredPosition = new Vector2(-(bubbleSize.x * 0.5f) - AvatarGap - AvatarSize * 0.5f,
                                                  -(bubbleSize.y * 0.5f) + 14f);
        }

        // 入场动画：只给"新产生的行"播一次（历史整表、更早分页、滚动换绑都不播）
        if (row.Seq > 0 && !row.Animated)
        {
            row.Animated = true;
            AgentTweenFx.BubbleIn(item, item.GetComponent<CanvasGroup>(), NextEntranceDelay());
        }

        // 流式行高度平滑：从上一帧的气泡高度过渡到新高度（逐行跳变会显得"卡"）
        if (row.Streaming && row.LastHeight > 0.5f && Mathf.Abs(row.LastHeight - bubbleSize.y) > 0.5f)
        {
            item.sizeDelta = new Vector2(bubbleSize.x, row.LastHeight);
            item.DOSizeDelta(bubbleSize, 0.09f).SetEase(Ease.OutQuad).SetUpdate(true);
        }
        row.LastHeight = bubbleSize.y;
    }

    /// <summary>尖括号转全角：防内容里的标签被 TMP 富文本执行（照世界频道先例，§8.2 A5）</summary>
    private static string EscapeRichText(string content)
    {
        return (content ?? string.Empty).Replace('<', '＜').Replace('>', '＞');
    }

    /// <summary>平滑滚到最新一条（smooth=false 用于整表就位/面板重开：直接到底不拖尾）。</summary>
    private void ScrollToBottom(bool smooth)
    {
        if (chatVirtual == null || messageScroll == null) return;

        scrollTween?.Kill();
        scrollTween = null;

        if (!smooth)
        {
            Canvas.ForceUpdateCanvases();
            messageScroll.verticalNormalizedPosition = 0f;
            chatVirtual.RefreshView();
            return;
        }

        scrollTween = AgentTweenFx.SmoothScrollToBottom(
            () => messageScroll.verticalNormalizedPosition,
            SetScrollPosition,
            chatVirtual.ContentHeight,
            () => chatVirtual?.RefreshView());
    }

    /// <summary>补间驱动滚动位置（打标记，好让"用户手滚"与"自动滚动"区分开）。</summary>
    private void SetScrollPosition(float value)
    {
        if (messageScroll == null) return;
        scrollAutoDriving = true;
        messageScroll.verticalNormalizedPosition = value;
        scrollAutoDriving = false;
    }
}
