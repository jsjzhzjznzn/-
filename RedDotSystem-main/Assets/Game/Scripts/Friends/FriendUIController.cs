using System.Collections.Generic;

using Common.Network;

using MH;
using SkierFramework;

using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 好友系统 UI 控制器（微信式·接入服务端真实数据）
/// 左栏三页切换：好友列表（服务端真实数据）/ 新的朋友（待处理申请·演示）/ 添加朋友（查询→申请两步流）
/// 右栏：好友列表页时显示聊天，添加朋友页时显示查询结果
/// </summary>
public class FriendUIController : SkierFramework.UIView
{
    [Header("面板")]

    /// <summary>面板关闭按钮</summary>
    [ControlBinding]
    public Button closeButton;

    [Header("左栏·好友列表页")]
    /// <summary>好友列表页根对象</summary>
    [ControlBinding]
    public GameObject listPage;

    /// <summary>标题栏"+"按钮 → 打开添加朋友页</summary>
    [ControlBinding]
    public Button addButton;

    /// <summary>"新的朋友"入口行 → 打开新的朋友页</summary>
    [ControlBinding]
    public Button newFriendEntryButton;

    /// <summary>好友条目容器</summary>
    [ControlBinding]
    public RectTransform friendListContent;

    /// <summary>好友条目模板（未激活）</summary>
    [ControlBinding]
    public GameObject friendItemTemplate;

    /// <summary>占位状态反馈</summary>
    [ControlBinding]
    public TMP_Text statusText;

    [Header("左栏·新的朋友页（待处理申请独立界面）")]
    /// <summary>新的朋友页根对象</summary>
    [ControlBinding]
    public GameObject requestsPage;

    /// <summary>返回按钮（回好友列表页）</summary>
    [ControlBinding]
    public Button requestsBackButton;

    /// <summary>申请条目容器</summary>
    [ControlBinding]
    public RectTransform requestListContent;

    /// <summary>申请条目模板（未激活）</summary>
    [ControlBinding]
    public GameObject requestItemTemplate;

    [Header("左栏·添加朋友页（查询→申请两步流）")]
    /// <summary>添加朋友页根对象</summary>
    [ControlBinding]
    public GameObject addPage;

    /// <summary>返回按钮</summary>
    [ControlBinding]
    public Button addBackButton;

    /// <summary>玩家ID输入框</summary>
    [ControlBinding]
    public TMP_InputField addFriendInput;

    /// <summary>查询按钮（输入ID后点击）</summary>
    [ControlBinding]
    public Button addSearchButton;

    [Header("右栏·查询结果卡片")]
    /// <summary>查询结果卡片（添加朋友页时显示，替代聊天区）</summary>
    [ControlBinding]
    public GameObject searchResultView;

    /// <summary>结果玩家名字</summary>
    [ControlBinding]
    public TMP_Text resultNameText;

    /// <summary>结果玩家ID</summary>
    [ControlBinding]
    public TMP_Text resultIdText;

    /// <summary>结果卡片上的"申请"按钮</summary>
    [ControlBinding]
    public Button resultApplyButton;

    [Header("右栏·聊天区")]
    /// <summary>聊天标题</summary>
    [ControlBinding]
    public TMP_Text chatTitle;

    /// <summary>消息气泡容器</summary>
    [ControlBinding]
    public RectTransform messageListContent;

    /// <summary>好友侧气泡模板（左侧白色，未激活）</summary>
    [ControlBinding]
    public GameObject messageLeftTemplate;

    /// <summary>我方侧气泡模板（右侧微信绿，未激活）</summary>
    [ControlBinding]
    public GameObject messageRightTemplate;

    /// <summary>聊天输入框</summary>
    [ControlBinding]
    public TMP_InputField chatInput;

    /// <summary>发送按钮</summary>
    [ControlBinding]
    public Button sendButton;

    [Header("右栏·表情包（QQ式：点按钮开面板，点表情直接发出）")]
    /// <summary>表情按钮（输入行左侧，开关表情面板）</summary>
    [ControlBinding]
    public Button emojiButton;

    /// <summary>表情按钮底图（面板打开时高亮，让用户知道再点一次会收起）</summary>
    [ControlBinding]
    public Image emojiButtonImage;

    /// <summary>表情面板本体（输入框上方，默认收起）</summary>
    [ControlBinding]
    public GameObject emojiPanel;

    /// <summary>表情面板滚动区（表情多了一屏放不下时滚动；虚拟滚动的宿主，行对象在运行时生成）</summary>
    [ControlBinding]
    public ScrollRect emojiScroll;

    /// <summary>
    /// 表情面板网格容器（虚拟列表的内容层：一行 N 个格子的行走对象池复用，只实例化视口内的行）
    /// 注意：这里的 GridLayoutGroup/ContentSizeFitter 必须摘掉，否则会和虚拟列表的手工摆位打架
    /// </summary>
    [ControlBinding]
    public RectTransform emojiContent;

    /// <summary>表情目录为空时的提示（放不出任何表情时顶替空白面板）</summary>
    [ControlBinding]
    public GameObject emojiEmptyHint;

    [Header("滑动列表")]
    /// <summary>好友列表滚动容器（挂在 FriendList 上）</summary>
    [ControlBinding]
    public ScrollRect friendListScroll;

    /// <summary>消息区滚动容器（挂在 MessageList 上）</summary>
    [ControlBinding]
    public ScrollRect messageScroll;

    // ──────────────────────────────
    // 虚拟列表（只实例化可见条目，滚动时池化复用）
    // ──────────────────────────────

    /// <summary>好友列表虚拟滚动组件（挂在 FriendList 上）</summary>
    private VirtualScrollList friendVirtual;

    /// <summary>消息区虚拟滚动组件（挂在 MessageList 上）</summary>
    private VirtualScrollList chatVirtual;

    /// <summary>表情面板虚拟滚动组件（挂在 EmojiPanel 上，以"行"为单位虚拟化）</summary>
    private VirtualScrollList stickerVirtual;

    /// <summary>池化表情行→格子视图（点击时按"行+列"现取表情键，闭包里不存表情数据防止复用后过期）</summary>
    private readonly Dictionary<RectTransform, StickerRow> stickerRows = new();

    /// <summary>好友数据源（虚拟列表按索引取用）</summary>
    private readonly List<FriendInfo> friends = new();

    /// <summary>池化好友条目→当前绑定索引（点击时反查，闭包里不存好友数据防止过期）</summary>
    private readonly Dictionary<RectTransform, int> friendIndexOf = new();

    /// <summary>当前渲染的会话数据（虚拟列表按索引取用）</summary>
    private IReadOnlyList<ChatMessageItem> currentHistory;

    /// <summary>好友条目高（固定行高）</summary>
    private const float FriendItemHeight = 48f;

    // 气泡布局常量（测量、绑定与虚拟列表布局共用）
    private const float BubbleSideMargin = 12f;   // 气泡距聊天区左右边缘
    private const float BubblePadX = 12f;         // 气泡内文字左右留白
    private const float BubblePadY = 10f;         // 气泡内文字上下留白
    private const float BubbleSpacing = 6f;       // 相邻气泡行距
    private const float ChatTopPadding = 8f;
    private const float ChatBottomPadding = 8f;

    /// <summary>隐藏量宽文本（克隆自气泡 BubbleText，同字体设置；虚拟列表量变高行用）</summary>
    private TMP_Text measureText;

    /// <summary>消息文字占位缓存：MessageId→(宽,高)，避免反复 ForceMeshUpdate</summary>
    private readonly Dictionary<string, Vector2> measuredText = new();

    /// <summary>缓存对应的文本最大宽度（宽度变化时缓存整体失效重测）</summary>
    private float measuredMaxTextWidth = -1f;

    // ──────────────────────────────
    // EventSystem 自愈：已移除 BeforeSceneLoad 版本（zhuye 场景自带 EventSystem，
    // 早于场景加载的自建持久实例会与 LOGIN 场景自带实例叠加成"2 event systems"每帧告警；
    // Start 里的兜底守卫保留，防场景被误删 EventSystem 的情况）
    // ──────────────────────────────

    /// <summary>当前选中的聊天对象ID（0=未选择）</summary>
    private int selectedId;

    /// <summary>右栏「未选中好友」的提示层（视觉新增，代码按名字找，缺失时静默跳过）</summary>
    private GameObject chatEmptyHint;

    /// <summary>好友条目底图缓存（普通/选中），来自 Resources/ui/Friends</summary>
    private static Sprite rowNormalSprite;
    private static Sprite rowSelectedSprite;

    /// <summary>消息内容上限（与服务端 ChatRepository.MaxContentLength 保持一致）</summary>
    private const int MaxChatContentLength = 500;

    // ──────────────────────────────
    // QQ式输入框（自动增高）
    // ──────────────────────────────

    /// <summary>输入框最小高度（单行）</summary>
    private const float MinInputHeight = 40f;

    /// <summary>输入框最大高度（超过后不再增高，文字区内部滚动查看）</summary>
    private const float MaxInputHeight = 220f;

    /// <summary>输入框内文字上下留白合计（高度=文字高+此值）</summary>
    private const float InputTextPaddingY = 14f;

    /// <summary>消息区底边与输入框顶部的间距</summary>
    private const float ListInputGap = 12f;

    /// <summary>查询到的待申请目标ID（0=未查询）</summary>
    private int pendingTargetId;

    // ──────────────────────────────
    // 表情包（QQ式）常量
    // ──────────────────────────────

    /// <summary>表情气泡的最大边长（正方形包围盒，按原图宽高比内缩，不拉伸变形）</summary>
    private const float StickerMaxSize = 150f;

    /// <summary>认不出的表情键的占位文案（新旧表情包不一致时兜底，界面不留空白）</summary>
    private const string UnknownStickerText = "[表情]";

    /// <summary>表情面板网格列数（沿用原场景 Content 上 GridLayoutGroup 的固定列数）</summary>
    private const int StickerGridColumns = 4;

    /// <summary>表情格子边长（px）</summary>
    private const float StickerCellSize = 72f;

    /// <summary>表情格子间距（px）</summary>
    private const float StickerCellSpacing = 8f;

    /// <summary>表情网格内容区上下留白（虚拟列表首尾内边距）</summary>
    private const float StickerGridPadding = 6f;

    /// <summary>一行格子的总宽（列数×边长 + 列间距；行在内容区里水平居中）</summary>
    private const float StickerRowWidth = StickerGridColumns * StickerCellSize + (StickerGridColumns - 1) * StickerCellSpacing;

    /// <summary>表情按钮底色：常态</summary>
    private static readonly Color EmojiButtonIdleColor = new(0.85f, 0.85f, 0.85f, 1f);

    /// <summary>表情按钮底色：面板打开中（浅绿，和发送键的微信绿区分开）</summary>
    private static readonly Color EmojiButtonOpenColor = new(0.62f, 0.87f, 0.72f, 1f);

    // ──────────────────────────────
    // Unity 生命周期
    // ──────────────────────────────

    private void Start()
    {
        // EventSystem 自愈（zhuye 单场景直接 Play 时兜底）
        if (EventSystem.current == null)
        {
            GameObject eventSystemHolder = new GameObject("EventSystem_Persistent",
                typeof(EventSystem), typeof(StandaloneInputModule));
            Object.DontDestroyOnLoad(eventSystemHolder);
        }

        // 按钮接线
        if (closeButton != null) closeButton.onClick.AddListener(OnCloseClicked);
        if (sendButton != null) sendButton.onClick.AddListener(OnSendClicked);
        if (addButton != null) addButton.onClick.AddListener(ShowAddPage);
        if (newFriendEntryButton != null) newFriendEntryButton.onClick.AddListener(ShowRequestsPage);
        if (requestsBackButton != null) requestsBackButton.onClick.AddListener(ShowListPage);
        if (addBackButton != null) addBackButton.onClick.AddListener(ShowListPage);
        if (addSearchButton != null) addSearchButton.onClick.AddListener(OnSearchClicked);
        if (resultApplyButton != null) resultApplyButton.onClick.AddListener(OnResultApplyClicked);
        if (emojiButton != null) emojiButton.onClick.AddListener(OnEmojiButtonClicked);

        // 表情面板：接入虚拟滚动（默认收起；目录为空时显示提示）
        SetEmojiPanelOpen(false);
        SetupStickerGrid();

        // QQ式输入框：Enter 提交 / 内容变化时自动增高
        if (chatInput != null)
        {
            chatInput.onSubmit.AddListener(OnChatSubmit);
            chatInput.onValueChanged.AddListener(OnChatInputChanged);
        }
        ResizeChatInput();

        // 虚拟列表接入（好友列表等高单池 / 消息区变高按左右分池）
        SetupVirtualLists();

        // 网络事件订阅
        if (ClientNetworkManager.Instance != null)
        {
            ClientNetworkManager.Instance.StatusChanged += OnNetworkStatus;
            ClientNetworkManager.Instance.FriendListResponseReceived += OnFriendListResponse;
            ClientNetworkManager.Instance.QueryPlayerResponseReceived += OnQueryPlayerResponse;
            ClientNetworkManager.Instance.FriendAddResponseReceived += OnFriendAddResponse;
            ClientNetworkManager.Instance.FriendRequestsResponseReceived += OnFriendRequestsResponse;
            ClientNetworkManager.Instance.FriendRespondResponseReceived += OnFriendRespondResponse;
        }

        // 聊天会话缓存事件订阅（渲染与数据分离：数据在 ChatManager，这里只管画）
        if (ChatManager.Instance != null)
        {
            ChatManager.Instance.ConversationUpdated += OnConversationUpdated;
            ChatManager.Instance.SendFailed += OnSendFailed;
        }

        if (statusText != null) statusText.text = string.Empty;

        // 登录进主界面即拉好友列表与待处理申请：红点种子随响应到达，
        // 不打开面板也能点亮"好友"入口角标（微信式）
        if (ClientNetworkManager.Instance != null && ClientNetworkManager.Instance.IsConnected)
        {
            _ = ClientNetworkManager.Instance.SendFriendListRequest();
            _ = ClientNetworkManager.Instance.SendFriendRequestsRequest();
        }

        // 登录进入主界面不自动弹出好友面板（场景里保持展开仅为方便编辑布局），由右上角"好友"入口打开
        ShowListPage();
        ResetChat();
    }

    private void OnDestroy()
    {
        if (ClientNetworkManager.Instance != null)
        {
            ClientNetworkManager.Instance.StatusChanged -= OnNetworkStatus;
            ClientNetworkManager.Instance.FriendListResponseReceived -= OnFriendListResponse;
            ClientNetworkManager.Instance.QueryPlayerResponseReceived -= OnQueryPlayerResponse;
            ClientNetworkManager.Instance.FriendAddResponseReceived -= OnFriendAddResponse;
            ClientNetworkManager.Instance.FriendRequestsResponseReceived -= OnFriendRequestsResponse;
            ClientNetworkManager.Instance.FriendRespondResponseReceived -= OnFriendRespondResponse;
        }
        if (ChatManager.Instance != null)
        {
            ChatManager.Instance.ConversationUpdated -= OnConversationUpdated;
            ChatManager.Instance.SendFailed -= OnSendFailed;
        }
    }

    // ──────────────────────────────
    // 好友申请与回应的网络事件处理
    // ──────────────────────────────

    /// <summary>好友申请响应处理（添加朋友页的申请结果）</summary>
    private void OnFriendAddResponse(FriendAddResponse response)
    {
        SetStatus(response.Success ? response.Message : $"发送失败：{response.Message}");
    }

    /// <summary>好友申请回应响应处理（新的朋友页的同意/拒绝结果）</summary>
    private void OnFriendRespondResponse(FriendRespondResponse response)
    {
        SetStatus(response.Success ? response.Message : $"处理失败：{response.Message}");

        // 处理成功后同时刷新好友列表和待处理申请列表（申请消失 + 好友列表+1）
        if (response.Success && ClientNetworkManager.Instance != null)
        {
            _ = ClientNetworkManager.Instance.SendFriendListRequest();
            _ = ClientNetworkManager.Instance.SendFriendRequestsRequest();
        }
    }

    // ──────────────────────────────
    // 网络事件处理
    // ──────────────────────────────

    /// <summary>连接状态变化处理</summary>
    private void OnNetworkStatus(string message)
    {
        SetStatus(message);
    }

    /// <summary>
    /// 好友列表响应处理：真实数据驱动虚拟列表
    /// 只存数据源并刷新可视区（条目由虚拟列表按需实例化/复用）；红点种子逻辑不变
    /// </summary>
    private void OnFriendListResponse(FriendListResponse response)
    {
        if (friendItemTemplate == null || friendListContent == null) return;

        friends.Clear();
        ChatManager chat = ChatManager.Instance;
        RedDotSingleton redDot = RedDotSingleton.Instance;

        // 批量模式：所有好友的红点种子一次性传播，避免父节点（好友入口）被反复重算。
        // 必须配对 finally：BeginBatch 之后中途抛异常而 EndBatch 没执行，批量模式会永久卡住——
        // 之后所有 Set 只登记脏节点、不再向上传播，父节点（Key14）就停在旧汇总值（陈旧红点）
        if (redDot != null) redDot.BeginBatch();
        try
        {
            foreach (FriendInfo friend in response.Friends)
            {
                friends.Add(friend);
                if (chat != null) chat.EnsureFriendNode(friend.PlayerId);
            }

            // 未读红点种子：服务端聚合的离线未读（跳过已打开/正在看的会话，由 ChatManager 判定）
            if (chat != null)
            {
                foreach (FriendInfo friend in response.Friends)
                {
                    chat.SeedUnread(friend.PlayerId, friend.UnreadCount);
                }
            }
        }
        finally
        {
            if (redDot != null) redDot.EndBatch();
        }

        if (friendVirtual != null)
        {
            friendVirtual.RefreshData();
            // 列表重建后回到顶部
            friendVirtual.ScrollToTop();
        }
    }

    /// <summary>
    /// 重置聊天区为未选中状态
    /// 微信式：进入"新的朋友/添加朋友"页时右栏不再残留上一个好友的会话
    /// </summary>
    protected void ResetChat()
    {
        selectedId = 0;
        // 离开会话：之后这个人来的消息才计未读红点
        if (ChatManager.Instance != null) ChatManager.Instance.CurrentConversation = 0;
        if (chatTitle != null) chatTitle.text = "选择好友开始聊天";
        if (chatInput != null) chatInput.text = string.Empty;
        // 离开会话顺带收起表情面板（QQ 式：换会话不该残留上个会话的面板状态）
        SetEmojiPanelOpen(false);
        RebuildMessages(null);
        RefreshRowStates();
        UpdateChatEmptyHint();
    }

    // ──────────────────────────────
    // 面板与页面切换（微信式左栏导航）
    // ──────────────────────────────

    /// <summary>关闭面板（框架版视图重写它改成 UIManager.Close）</summary>
    protected virtual void OnCloseClicked()
    {
        ResetChat();   // 关面板=离开会话：之后来的消息才计未读红点
    }

    /// <summary>显示好友列表页（右栏恢复聊天视图）</summary>
    protected void ShowListPage()
    {
        if (listPage != null) listPage.SetActive(true);
        if (requestsPage != null) requestsPage.SetActive(false);
        if (addPage != null) addPage.SetActive(false);
        ShowChatView();
        UpdateChatEmptyHint();
    }

    /// <summary>显示新的朋友页（待处理申请独立界面，右栏保持占位）</summary>
    private void ShowRequestsPage()
    {
        if (listPage != null) listPage.SetActive(false);
        if (requestsPage != null) requestsPage.SetActive(true);
        if (addPage != null) addPage.SetActive(false);
        ResetChat();

        // 新的朋友页右栏不显示聊天占位，改为页面专属提示
        if (chatTitle != null) chatTitle.text = "好友申请";

        // 进入新的朋友页：向服务端拉取待处理申请列表
        if (ClientNetworkManager.Instance != null && ClientNetworkManager.Instance.IsConnected)
        {
            _ = ClientNetworkManager.Instance.SendFriendRequestsRequest();
        }
    }

    /// <summary>显示添加朋友页（右栏切换为查询结果视图）</summary>
    private void ShowAddPage()
    {
        if (listPage != null) listPage.SetActive(false);
        if (requestsPage != null) requestsPage.SetActive(false);
        if (addPage != null) addPage.SetActive(true);
        ShowSearchView();
        ResetSearchCard();
        UpdateChatEmptyHint();
    }

    // ──────────────────────────────
    // 右栏视图切换
    // ──────────────────────────────

    /// <summary>右栏显示聊天视图（好友列表/新的朋友页时）</summary>
    private void ShowChatView()
    {
        // 切换整个滚动区（含背景），而不是只切 Content——否则添加朋友页会残留空消息区背景
        if (messageScroll != null) messageScroll.gameObject.SetActive(true);
        if (chatInput != null) chatInput.gameObject.SetActive(true);
        if (sendButton != null) sendButton.gameObject.SetActive(true);
        if (emojiButton != null) emojiButton.gameObject.SetActive(true);
        if (searchResultView != null) searchResultView.SetActive(false);
    }

    /// <summary>右栏显示查询结果视图（添加朋友页时）</summary>
    private void ShowSearchView()
    {
        if (messageScroll != null) messageScroll.gameObject.SetActive(false);
        if (chatInput != null) chatInput.gameObject.SetActive(false);
        if (sendButton != null) sendButton.gameObject.SetActive(false);
        if (emojiButton != null) emojiButton.gameObject.SetActive(false);
        SetEmojiPanelOpen(false);
        if (searchResultView != null) searchResultView.SetActive(true);
    }

    /// <summary>查询结果卡片重置为空状态</summary>
    private void ResetSearchCard()
    {
        if (resultNameText != null) resultNameText.text = "输入玩家ID后点击查询";
        if (resultIdText != null) resultIdText.text = string.Empty;
        if (resultApplyButton != null)
        {
            resultApplyButton.interactable = true;
            resultApplyButton.gameObject.SetActive(false);
        }
    }

    // ──────────────────────────────
    // 添加朋友页：查询 → 申请
    // ──────────────────────────────

    /// <summary>
    /// 查询按钮点击处理
    /// 发送查询玩家协议到服务端（按玩家ID查询账号）
    /// 结果由 QueryPlayerResponse 驱动显示在结果卡片
    /// </summary>
    private void OnSearchClicked()
    {
        string input = addFriendInput != null ? addFriendInput.text.Trim() : string.Empty;
        if (input.Length == 0)
        {
            SetStatus("请先输入要查询的玩家ID");
            return;
        }
        if (!int.TryParse(input, out int searchId))
        {
            SetStatus("玩家ID必须是数字");
            return;
        }

        if (ClientNetworkManager.Instance == null) return;
        if (!ClientNetworkManager.Instance.IsConnected)
        {
            SetStatus("未连接服务器，请先登录");
            return;
        }

        SetStatus("查询中...");
        _ = ClientNetworkManager.Instance.SendQueryPlayer(searchId);
    }

    /// <summary>
    /// 查询玩家响应处理
    /// 找到 → 结果卡片显示玩家信息与申请按钮；未找到 → 显示提示
    /// </summary>
    private void OnQueryPlayerResponse(QueryPlayerResponse response)
    {
        if (resultNameText != null) resultNameText.text = response.Found ? response.Name : "未找到该玩家";
        if (resultIdText != null) resultIdText.text = response.Found ? $"Id:{response.PlayerId}" : string.Empty;
        if (resultApplyButton != null)
        {
            // 新查询结果到达：恢复可点（发送后的禁用只防本张卡片连点）
            resultApplyButton.interactable = true;
            resultApplyButton.gameObject.SetActive(response.Found);
        }
        SetStatus(response.Found
            ? $"查询到玩家：{response.Name}（Id:{response.PlayerId}）"
            : $"未找到玩家ID:{response.PlayerId}");

        // 记住查询到的目标ID（申请按钮要用它）
        pendingTargetId = response.Found ? response.PlayerId : 0;
    }

    /// <summary>
    /// 结果卡片"申请"按钮点击处理
    /// 发送好友申请协议到服务端（写 friendships 表 Status=0）
    /// </summary>
    private void OnResultApplyClicked()
    {
        if (ClientNetworkManager.Instance == null || pendingTargetId <= 0) return;

        SetStatus("发送申请中...");
        _ = ClientNetworkManager.Instance.SendFriendAddRequest(pendingTargetId);
        resultApplyButton.interactable = false;
        resultApplyButton.GetComponentInChildren<TMP_Text>().text = "已发送";
    }

    // ──────────────────────────────
    // 好友列表页（服务端真实数据）
    // ──────────────────────────────


    // ──────────────────────────────
    // 新的朋友页（待处理申请·真实数据）
    // ──────────────────────────────

    /// <summary>
    /// 待处理申请列表响应处理
    /// 用服务端返回的真实申请数据重建条目，接受/拒绝按钮绑定申请人ID
    /// </summary>
    private void OnFriendRequestsResponse(FriendRequestsResponse response)
    {
        if (requestItemTemplate == null || requestListContent == null) return;

        // 清空旧条目（保留模板）
        foreach (Transform child in requestListContent)
        {
            if (child.gameObject == requestItemTemplate) continue;
            Destroy(child.gameObject);
        }

        // 红点更新（走红点系统：新的朋友 Key15，向上自动汇总到好友入口）
        if (RedDotSingleton.Instance != null)
        {
            RedDotSingleton.Instance.Set(RedDotKeys.FriendRequests, response.Count, RedDotFlags.Number);
        }

        // 真实数据实例化
        for (int i = 0; i < response.Requests.Count; i++)
        {
            FriendInfo requester = response.Requests[i];
            int fromPlayerId = requester.PlayerId;

            GameObject item = Instantiate(requestItemTemplate, requestListContent);
            item.name = $"Request_{requester.Name}";
            item.SetActive(true);
            item.GetComponent<RectTransform>().anchoredPosition =
                new Vector2(0, 180 - i * 64);

            // 防御式查找：模板缺子件时跳过对应填充并告警一次，不抛空引用（与 BindFriendItem 同风格）
            TMP_Text infoText = item.transform.Find("InfoText")?.GetComponent<TMP_Text>();
            if (infoText != null)
            {
                infoText.text = $"{requester.Name}  Id:{requester.PlayerId}";
            }
            else
            {
                WarnMissingRequestChild("InfoText");
            }

            Button acceptButton = item.transform.Find("AcceptButton")?.GetComponent<Button>();
            if (acceptButton != null)
            {
                acceptButton.onClick.AddListener(() => RespondToRequest(fromPlayerId, true));
            }
            else
            {
                WarnMissingRequestChild("AcceptButton");
            }

            Button rejectButton = item.transform.Find("RejectButton")?.GetComponent<Button>();
            if (rejectButton != null)
            {
                rejectButton.onClick.AddListener(() => RespondToRequest(fromPlayerId, false));
            }
            else
            {
                WarnMissingRequestChild("RejectButton");
            }
        }
    }

    /// <summary>已告警过的缺失子件名（同名只报一次，防止每次重建列表刷屏）</summary>
    private readonly HashSet<string> warnedRequestChildren = new();

    /// <summary>申请条目模板子件缺失时只告警一次的方法</summary>
    private void WarnMissingRequestChild(string childName)
    {
        if (warnedRequestChildren.Add(childName))
        {
            Debug.LogWarning($"[好友UI]申请条目模板缺少子件：{childName}，已跳过对应填充");
        }
    }

    /// <summary>接受/拒绝好友申请（发送回应协议到服务端）</summary>
    private void RespondToRequest(int fromPlayerId, bool accept)
    {
        if (ClientNetworkManager.Instance == null) return;
        _ = ClientNetworkManager.Instance.SendFriendRespondRequest(fromPlayerId, accept);
        SetStatus(accept ? "已同意好友申请" : "已拒绝好友申请");
    }

    // ──────────────────────────────
    // 右栏聊天区
    // ──────────────────────────────

    /// <summary>
    /// 切换聊天对象
    /// 更新标题并从 ChatManager 会话缓存渲染：有缓存直接画（0网络），无缓存自动拉历史
    /// </summary>
    private void SelectChat(int friendId, string friendName)
    {
        selectedId = friendId;
        if (chatTitle != null) chatTitle.text = friendName;
        RefreshRowStates();
        UpdateChatEmptyHint();

        if (ChatManager.Instance == null) return;
        // 先声明"正在看这个会话"再打开：本次打开与随后的推送都不计未读红点（微信式）
        ChatManager.Instance.CurrentConversation = friendId;
        IReadOnlyList<ChatMessageItem> history = ChatManager.Instance.OpenConversation(friendId);
        if (history.Count == 0) SetStatus($"正在加载与{friendName}的聊天记录...");
        RebuildMessages(history);
    }

    /// <summary>
    /// 会话更新回调（ChatManager 触发）
    /// 只关心当前打开的会话；其他人的更新已进缓存，切回时直接读取
    /// </summary>
    private void OnConversationUpdated(int friendId)
    {
        if (friendId != selectedId || ChatManager.Instance == null) return;
        RebuildMessages(ChatManager.Instance.OpenConversation(friendId));

        // 历史/推送到达即收掉"正在加载..."：该文案以前发出后没有清除点，
        // 预热命中时也不该残留。只清本类自己写的加载提示，不碰其它业务提示。
        if (statusText != null && statusText.text.StartsWith("正在加载"))
        {
            statusText.text = string.Empty;
        }
    }

    /// <summary>乐观消息发送失败：ChatManager 已回滚 pending，这里重渲染并提示</summary>
    private void OnSendFailed(int friendId)
    {
        SetStatus("发送失败，请检查网络");
        if (friendId == selectedId && ChatManager.Instance != null)
        {
            RebuildMessages(ChatManager.Instance.OpenConversation(friendId));
        }
    }

    /// <summary>发送按钮处理（与 Enter 提交共用一条路径）</summary>
    private void OnSendClicked()
    {
        SubmitChat();
    }

    /// <summary>
    /// Enter 提交处理（MultiLineSubmit：Enter 发送，Shift+Enter 换行）
    /// 发送后保持焦点在输入框（QQ 行为），可连续打字
    /// </summary>
    private void OnChatSubmit(string _)
    {
        SubmitChat();
        if (chatInput != null) chatInput.ActivateInputField();
    }

    /// <summary>
    /// 提交聊天输入的方法
    /// 校验后走 ChatManager 乐观发送：气泡立刻显示右侧，服务端 Echo 到达后自动校准
    /// 清空输入框会触发 onValueChanged，输入框随之收回单行高度
    /// </summary>
    private void SubmitChat()
    {
        string text = chatInput != null ? chatInput.text.Trim() : string.Empty;
        if (selectedId <= 0 || text.Length == 0 || chatInput == null) return;

        if (ClientNetworkManager.Instance == null || !ClientNetworkManager.Instance.IsConnected)
        {
            SetStatus("未连接服务器，请先登录");
            return;
        }
        if (text.Length > MaxChatContentLength)
        {
            SetStatus($"消息不能超过{MaxChatContentLength}字");
            return;
        }

        ChatManager.Instance.Send(selectedId, text);
        chatInput.text = string.Empty;
    }

    // ──────────────────────────────
    // QQ式输入框自动增高
    // ──────────────────────────────

    /// <summary>输入内容变化：重新自适应输入框高度，并把光标滚进可视区</summary>
    private void OnChatInputChanged(string _)
    {
        ResizeChatInput();
        ScrollCaretIntoView();
    }

    /// <summary>
    /// 输入框高度自适应的方法
    /// 底锚+底轴心：高度变化时向上长高；文字换到几行就长到几行，超过上限后停止增高、由输入框内部滚动
    /// </summary>
    private void ResizeChatInput()
    {
        if (chatInput == null || chatInput.textComponent == null) return;

        float preferred = chatInput.textComponent.preferredHeight;
        float height = Mathf.Clamp(preferred + InputTextPaddingY, MinInputHeight, MaxInputHeight);
        ((RectTransform)chatInput.transform).SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);

        SyncMessageListBottom();
    }

    /// <summary>
    /// 消息区底边跟随输入框（或表情面板）的方法
    /// 输入框长高 / 表情面板展开时消息区同步变矮（QQ式联动）；消息区顶锚+顶轴心，改高度即可，顶边不动
    /// 让位而不是盖住：面板压在消息区上会挡住最近几条消息，这里改成"消息区让出这一块高度"
    /// </summary>
    private void SyncMessageListBottom()
    {
        if (messageScroll == null || chatInput == null) return;

        RectTransform inputRect = (RectTransform)chatInput.transform;
        RectTransform listRect = (RectTransform)messageScroll.transform;
        RectTransform panelRect = (RectTransform)listRect.parent;

        // 输入框顶边距面板底：底锚 anchoredPosition.y + 当前高度
        float inputTopFromBottom = inputRect.anchoredPosition.y + inputRect.rect.height;

        // 表情面板开着时，消息区底边跟着面板顶边走（面板自己占了一块高度）
        float bottomEdge = inputTopFromBottom;
        if (emojiPanel != null && emojiPanel.activeSelf)
        {
            RectTransform emojiRect = (RectTransform)emojiPanel.transform;
            bottomEdge = emojiRect.anchoredPosition.y + emojiRect.rect.height;
        }

        // 可用高度 = 面板总高 - 消息区顶部偏移 - 间距 - 底边占位
        float height = panelRect.rect.height - Mathf.Abs(listRect.anchoredPosition.y)
            - ListInputGap - bottomEdge;
        listRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, Mathf.Max(height, 120f));

        // 视口变矮/变高：虚拟列表的可见行数要重算
        if (chatVirtual != null) chatVirtual.RefreshView();
    }

    // ──────────────────────────────
    // 表情包（QQ式）
    // ──────────────────────────────

    /// <summary>表情按钮点击：开关表情面板</summary>
    private void OnEmojiButtonClicked()
    {
        SetEmojiPanelOpen(emojiPanel != null && !emojiPanel.activeSelf);
    }

    /// <summary>
    /// 开关表情面板的方法
    /// 打开时消息区同步让出高度（不遮最近消息），按钮底色变浅绿提示"再点一次收起"
    /// </summary>
    /// <param name="open">参数名：open，类型：bool，用途：true=展开 false=收起</param>
    private void SetEmojiPanelOpen(bool open)
    {
        // 压矮之前先判断是否停在最新消息处：是的话压矮后继续贴底，
        // 否则最后一条会被切掉一半（QQ 式——打开表情面板时最新消息要始终看得见）
        bool pinnedToBottom = IsMessageListAtBottom();

        if (emojiPanel != null && emojiPanel.activeSelf != open)
        {
            // 面板一直收着时虚拟列表不工作（组件在未激活物体上不渲染），内容高恒为 0——
            // 首次打开渲染完要停在第一行，之后每次打开保留玩家上次滚到的位置
            bool firstRender = open && stickerVirtual != null && stickerVirtual.ContentHeight <= 0f;

            emojiPanel.SetActive(open);

            if (open && stickerVirtual != null)
            {
                stickerVirtual.RefreshData();   // 激活后视图尺寸才有效，此时再建可视区的行
                if (firstRender) stickerVirtual.ScrollToTop();
            }
        }

        if (emojiButtonImage != null) emojiButtonImage.color = open ? EmojiButtonOpenColor : EmojiButtonIdleColor;

        SyncMessageListBottom();
        if (open && pinnedToBottom && chatVirtual != null) chatVirtual.ScrollToBottom();
    }

    /// <summary>
    /// 判断消息区是否停在最新消息处（底部）的方法
    /// 内容不足一屏时恒算贴底；读 ScrollRect 的归一化位置（0=底 1=顶），
    /// Content 高度由虚拟列表写，读之前布局已经结算过，取值可靠
    /// </summary>
    private bool IsMessageListAtBottom()
    {
        if (messageScroll == null || chatVirtual == null) return false;
        if (chatVirtual.ContentHeight <= ((RectTransform)messageScroll.transform).rect.height) return true;

        float position = messageScroll.verticalNormalizedPosition;
        return !float.IsNaN(position) && position <= 0.001f;
    }

    /// <summary>
    /// 接入表情面板虚拟滚动的方法
    /// 表情清单来自 StickerCatalog（Resources/biaoq 目录自动扫描）：往目录里丢图 = 面板多一个表情，
    /// 场景与代码都不用改；渲染以"行"为单位（一行 StickerGridColumns 个格子），
    /// 只实例化视口内可见的行，行回池后换绑复用——表情再多也只存在十来个格子物体
    /// </summary>
    private void SetupStickerGrid()
    {
        // 空目录提示（面板里没有格子可放时顶替空白）
        if (emojiEmptyHint != null) emojiEmptyHint.SetActive(StickerCatalog.All.Count == 0);

        if (emojiScroll == null || emojiContent == null) return;

        stickerVirtual = emojiScroll.GetComponent<VirtualScrollList>();
        if (stickerVirtual == null) stickerVirtual = emojiScroll.gameObject.AddComponent<VirtualScrollList>();
        stickerVirtual.Configure(StickerGridPadding, StickerGridPadding, StickerCellSpacing);
        stickerVirtual.GetItemCount = () => StickerRowCount;
        stickerVirtual.GetItemHeight = _ => StickerCellSize;
        stickerVirtual.CreateItem = _ => CreateStickerRow();
        stickerVirtual.BindItem = BindStickerRow;
        // 不能把行从 stickerRows 里删掉（虚拟列表"回收再重建"时行会回池，出池复用时还要靠这张表
        // 取回自己的格子视图——删了会让换绑静默失效）；行对象跟着本控制器一起销毁，表无需清理

        // 面板此刻是收起的（未激活物体上的组件不工作，这里是空操作）；真正渲染在打开面板时触发
        stickerVirtual.RefreshData();
    }

    /// <summary>表情行数（= ceil(表情数 / 每行列数)）</summary>
    private int StickerRowCount
    {
        get
        {
            int count = StickerCatalog.All.Count;
            return count == 0 ? 0 : (count + StickerGridColumns - 1) / StickerGridColumns;
        }
    }

    /// <summary>
    /// 创建一行表情格子的方法（池空时调用）
    /// 行对象在运行时搭建（场景里不放模板）：格子按列等距摆开、行宽固定，水平居中由换绑写入；
    /// 单击回调捕获"行对象 + 列号"，键在点击时现取——行被复用后不会点到上一条表情
    /// </summary>
    private RectTransform CreateStickerRow()
    {
        GameObject rowObject = new("StickerRow", typeof(RectTransform));
        RectTransform rowRect = (RectTransform)rowObject.transform;
        rowRect.SetParent(emojiContent, false);
        rowRect.sizeDelta = new Vector2(StickerRowWidth, StickerCellSize);

        StickerRow row = new()
        {
            Icons = new Image[StickerGridColumns],
            Keys = new string[StickerGridColumns],
        };

        for (int c = 0; c < StickerGridColumns; c++)
        {
            // CanvasRenderer 显式带上：本项目 uGUI 分叉的 Graphic 基类不声明 RequireComponent
            GameObject cellObject = new($"StickerCell_{c}",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            RectTransform cellRect = (RectTransform)cellObject.transform;
            cellRect.SetParent(rowRect, false);
            cellRect.anchorMin = new Vector2(0.5f, 0.5f);
            cellRect.anchorMax = new Vector2(0.5f, 0.5f);
            cellRect.pivot = new Vector2(0.5f, 0.5f);
            cellRect.sizeDelta = new Vector2(StickerCellSize, StickerCellSize);
            // 第 c 列中心 = 行左边缘 + 半格 + c×(边长 + 间距)
            cellRect.anchoredPosition = new Vector2(
                -StickerRowWidth * 0.5f + StickerCellSize * 0.5f + c * (StickerCellSize + StickerCellSpacing), 0f);

            Image icon = cellObject.GetComponent<Image>();
            icon.preserveAspect = true;   // 表情不是方的也等比缩放，不要拉变形

            Button button = cellObject.GetComponent<Button>();
            button.targetGraphic = icon;
            int column = c;   // 闭包捕获副本：循环变量不能直接进 lambda
            button.onClick.AddListener(() => OnStickerCellClicked(row, column));

            row.Icons[c] = icon;
        }

        stickerRows[rowRect] = row;
        return rowRect;
    }

    /// <summary>
    /// 行换绑：第 row 行的 N 个格子依次对应表情索引 row*N+0..N-1（末行不满的格子隐藏）
    /// 贴图、表情键、行宽都在这里刷新；y（垂直位置）由虚拟列表随后写入
    /// </summary>
    private void BindStickerRow(int row, RectTransform rowRect)
    {
        if (!stickerRows.TryGetValue(rowRect, out StickerRow view))
        {
            // 正常不会发生（行与视图一一对应）；真出现说明行少登记了，报警别静默跳过换绑
            Debug.LogWarning($"[表情]行视图缺失，跳过换绑：{rowRect.name}");
            return;
        }

        IReadOnlyList<StickerInfo> stickers = StickerCatalog.All;
        int baseIndex = row * StickerGridColumns;

        for (int c = 0; c < StickerGridColumns; c++)
        {
            int index = baseIndex + c;
            bool hasSticker = index < stickers.Count;

            view.Keys[c] = hasSticker ? stickers[index].Key : null;
            view.Icons[c].sprite = hasSticker ? stickers[index].Thumbnail : null;
            view.Icons[c].gameObject.SetActive(hasSticker);
        }

        rowRect.sizeDelta = new Vector2(StickerRowWidth, StickerCellSize);
        rowRect.anchoredPosition = new Vector2(0f, rowRect.anchoredPosition.y);   // x：行在内容区水平居中
    }

    /// <summary>点击表情格子：按"行+列"现取当前绑定的表情键，再走统一的发表情流程（池复用安全）</summary>
    private void OnStickerCellClicked(StickerRow row, int column)
    {
        if (row == null || row.Keys == null) return;
        if (column < 0 || column >= row.Keys.Length) return;

        string key = row.Keys[column];
        if (string.IsNullOrEmpty(key)) return;   // 末行空位：没有表情可发
        OnStickerClicked(key);
    }

    /// <summary>
    /// 一行表情格子（行对象池化复用）：格子物体固定 StickerGridColumns 个，换绑只改贴图与当前键
    /// </summary>
    private sealed class StickerRow
    {
        /// <summary>格子图标</summary>
        public Image[] Icons;

        /// <summary>格子当前绑定的表情键（null=空位，末行不满时用）</summary>
        public string[] Keys;
    }

    /// <summary>
    /// 点击表情格子：直接作为一条消息发给当前会话（QQ 式，不经过输入框）
    /// 发送后收起面板，避免挡住刚发出去的气泡
    /// </summary>
    /// <param name="stickerKey">参数名：stickerKey，类型：string，用途：表情键</param>
    private void OnStickerClicked(string stickerKey)
    {
        if (selectedId <= 0)
        {
            SetStatus("请先选择好友再发表情");
            return;
        }
        if (ClientNetworkManager.Instance == null || !ClientNetworkManager.Instance.IsConnected)
        {
            SetStatus("未连接服务器，请先登录");
            return;
        }
        if (ChatManager.Instance == null) return;

        ChatManager.Instance.SendSticker(selectedId, stickerKey);
        SetEmojiPanelOpen(false);
        SetStatus("已发送表情");
    }

    // ──────────────────────────────
    // 输入框光标跟随滚动
    // ──────────────────────────────

    /// <summary>视口世界角点临时数组（避免每帧分配）</summary>
    private static readonly Vector3[] ViewportCorners = new Vector3[4];

    /// <summary>上一帧的光标位置（只在光标变化时跟随，避免每帧强制可见把用户的滚轮滚动弹回去）</summary>
    private int lastCaretIndex = -1;

    /// <summary>
    /// 光标跟随轮询
    /// 方向键/鼠标点击移动光标不触发 onValueChanged，靠轮询光标位置变化来跟随；
    /// 光标没动时绝不干预——用户滚轮翻看已输入内容不被弹回
    /// </summary>
    private void LateUpdate()
    {
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

    /// <summary>
    /// 把光标所在行滚进输入框可视区的方法
    /// TMP 多行输入框打字超出视口不会自动跟随（实测确认），这里按光标字符的世界坐标手动滚动文本；
    /// 直接改文本 anchoredPosition 不会被 TMP 覆写（实测确认），滚轮仍走 TMP 原生 OnScroll
    /// 滚动范围与 TMP 一致：[0, 文字总高 - 视口高]，anchoredPos.y 增大 = 内容上移看更下面
    /// </summary>
    /// <param name="forceUpdate">参数名：forceUpdate，类型：bool，用途：文本刚变化时先强制重建网格再取行坐标</param>
    private void ScrollCaretIntoView(bool forceUpdate = true)
    {
        if (chatInput == null || chatInput.textComponent == null || chatInput.textViewport == null) return;

        TMP_Text txt = chatInput.textComponent;
        string value = chatInput.text;
        if (forceUpdate) txt.ForceMeshUpdate();

        TMP_TextInfo info = txt.textInfo;
        if (info.characterCount == 0)
        {
            // 空文本：复位滚动，避免残留旧偏移挡住新输入的首行
            txt.rectTransform.anchoredPosition = Vector2.zero;
            return;
        }

        // 光标对应的可见字符（光标在末尾时取最后一个字符）
        int caret = Mathf.Clamp(chatInput.caretPosition, 0, value.Length);
        TMP_CharacterInfo character = info.characterInfo[Mathf.Min(caret, info.characterCount - 1)];

        // 光标行上下沿：字符角点是文本局部坐标，经当前滚动偏移变换到世界
        Transform textTransform = txt.transform;
        float lineHeight = info.lineInfo[info.lineCount - 1].lineHeight;
        float topWorldY = textTransform.TransformPoint(new Vector3(0f, character.topRight.y, 0f)).y;
        float bottomWorldY = textTransform.TransformPoint(new Vector3(0f, character.bottomRight.y, 0f)).y;

        // 文本以换行结尾时，光标实际位于最后一行的下一行
        if (caret >= value.Length && value.EndsWith("\n"))
        {
            topWorldY += lineHeight;
            bottomWorldY += lineHeight;
        }

        // 视口世界矩形
        RectTransform viewport = chatInput.textViewport;
        viewport.GetWorldCorners(ViewportCorners);
        float viewTop = ViewportCorners[2].y;
        float viewBottom = ViewportCorners[0].y;

        // 光标行不在视口内才滚动
        float delta = 0f;
        if (topWorldY > viewTop) delta = viewTop - topWorldY;                       // 光标在视口上方：内容下移
        else if (bottomWorldY < viewBottom) delta = viewBottom - bottomWorldY;      // 光标在视口下方：内容上移
        if (Mathf.Abs(delta) < 0.01f) return;

        RectTransform textRect = txt.rectTransform;
        float maxScroll = Mathf.Max(0f, txt.preferredHeight - viewport.rect.height);
        textRect.anchoredPosition = new Vector2(
            0f, Mathf.Clamp(textRect.anchoredPosition.y + delta, 0f, maxScroll));
    }

    // ──────────────────────────────
    // 虚拟列表接入
    // ──────────────────────────────

    /// <summary>
    /// 初始化两个虚拟列表回调的方法
    /// 好友列表：等高条目、单池，Bind 时顺带把未读角标换绑到对应好友（RedDotItem.Bind 支持运行时改绑）
    /// 消息区：变高气泡、按我方/对方分池，行高由隐藏量宽文本按 MessageId 缓存测量
    /// </summary>
    private void SetupVirtualLists()
    {
        if (friendListScroll != null)
        {
            friendVirtual = friendListScroll.GetComponent<VirtualScrollList>();
            if (friendVirtual == null) friendVirtual = friendListScroll.gameObject.AddComponent<VirtualScrollList>();
            friendVirtual.Configure(4f, 4f, 4f);
            friendVirtual.GetItemCount = () => friends.Count;
            friendVirtual.GetItemHeight = _ => FriendItemHeight;
            friendVirtual.CreateItem = _ =>
            {
                RectTransform item = Instantiate(friendItemTemplate, friendListContent).GetComponent<RectTransform>();
                item.GetComponent<Button>().onClick.AddListener(() => OnFriendItemClicked(item));
                return item;
            };
            friendVirtual.BindItem = BindFriendItem;
            friendVirtual.RecycleItem = item => friendIndexOf.Remove(item);
        }

        if (messageScroll != null)
        {
            chatVirtual = messageScroll.GetComponent<VirtualScrollList>();
            if (chatVirtual == null) chatVirtual = messageScroll.gameObject.AddComponent<VirtualScrollList>();
            chatVirtual.Configure(ChatTopPadding, ChatBottomPadding, BubbleSpacing);
            chatVirtual.GetItemCount = () => currentHistory?.Count ?? 0;
            chatVirtual.GetItemHeight = index => MeasureMessage(currentHistory[index]).y;
            chatVirtual.GetPoolKey = index => IsFromMe(currentHistory[index]);
            chatVirtual.CreateItem = index => Instantiate(
                IsFromMe(currentHistory[index]) ? messageRightTemplate : messageLeftTemplate,
                messageListContent).GetComponent<RectTransform>();
            chatVirtual.BindItem = BindChatBubble;
        }

        // 隐藏量宽文本：克隆气泡文本（同字体/字号/换行设置），挂在滚动区根下（不在 Content，不会被清理）
        // Raycast 关闭 + 位置远移视口外，只用来 ForceMeshUpdate 量文字占位
        if (measureText == null && messageLeftTemplate != null && messageListContent != null)
        {
            TMP_Text templateText = messageLeftTemplate.transform.Find("BubbleText").GetComponent<TMP_Text>();
            measureText = Instantiate(templateText, messageListContent.parent);
            measureText.gameObject.name = "BubbleMeasurer";
            measureText.raycastTarget = false;
            measureText.text = string.Empty;
            ((RectTransform)measureText.transform).anchoredPosition = new Vector2(0f, 100000f);
        }
    }

    /// <summary>好友条目点击：按池化条目当前绑定的索引反查数据，避免闭包过期</summary>
    private void OnFriendItemClicked(RectTransform item)
    {
        if (!friendIndexOf.TryGetValue(item, out int index)) return;
        if (index < 0 || index >= friends.Count) return;
        SelectChat(friends[index].PlayerId, friends[index].Name);
    }

    /// <summary>绑定好友条目（池化复用）：数据填充 + 未读角标换绑到该好友</summary>
    private void BindFriendItem(int index, RectTransform item)
    {
        FriendInfo friend = friends[index];
        item.name = $"Friend_{friend.Name}";
        friendIndexOf[item] = index;

        RedDotItem dotItem = item.GetComponent<RedDotItem>();
        if (dotItem != null) dotItem.Bind(RedDotKeys.FriendKey(friend.PlayerId));

        // 防御式查找：模板改版删掉某个部件（如 OnlineDot 已移除）时跳过对应填充，不空引用
        TMP_Text nameText = item.transform.Find("NameText")?.GetComponent<TMP_Text>();
        if (nameText != null) nameText.text = friend.Name;
        TMP_Text idText = item.transform.Find("IdText")?.GetComponent<TMP_Text>();
        if (idText != null) idText.text = $"ID {friend.PlayerId}";
        Image onlineDot = item.transform.Find("OnlineDot")?.GetComponent<Image>();
        if (onlineDot != null)
        {
            onlineDot.color = friend.Online
                ? new Color(0.027f, 0.757f, 0.376f, 1f)
                : new Color(0.62f, 0.62f, 0.62f, 1f);
        }

        // x：等高条目居中（y 由虚拟列表写入）
        item.anchoredPosition = new Vector2(0f, item.anchoredPosition.y);

        // 头像徽章：名字首字（视觉元素，模板没有该节点时静默跳过）
        TMP_Text avatarInitial = item.transform.Find("Avatar/AvatarInitial")?.GetComponent<TMP_Text>();
        if (avatarInitial != null)
        {
            string display = string.IsNullOrEmpty(friend.Name) ? "?" : friend.Name;
            avatarInitial.text = char.IsHighSurrogate(display[0]) && display.Length > 1
                ? display.Substring(0, 2)
                : display.Substring(0, 1).ToUpperInvariant();
        }

        ApplyRowState(item, friend.PlayerId == selectedId);
    }

    /// <summary>
    /// 好友条目选中态：当前正在聊的那一行换成金色描边的卡片（视觉反馈，贴图缺失时不改）
    /// </summary>
    private static void ApplyRowState(RectTransform item, bool selected)
    {
        if (item == null) return;
        Image background = item.GetComponent<Image>();
        if (background == null) return;

        Sprite sprite = LoadRowSprite(selected);
        if (sprite == null) return;
        background.sprite = sprite;
        background.type = Image.Type.Sliced;
        background.color = Color.white;
    }

    /// <summary>刷新所有已实例化条目的选中态（切换会话 / 关闭会话时调用；池化条目按绑定索引反查）</summary>
    private void RefreshRowStates()
    {
        if (friendListContent == null) return;

        for (int i = 0; i < friendListContent.childCount; i++)
        {
            RectTransform child = friendListContent.GetChild(i) as RectTransform;
            if (child == null || !child.gameObject.activeSelf) continue;
            if (!friendIndexOf.TryGetValue(child, out int index)) continue;
            if (index < 0 || index >= friends.Count) continue;
            ApplyRowState(child, friends[index].PlayerId == selectedId);
        }
    }

    private static Sprite LoadRowSprite(bool selected)
    {
        if (rowNormalSprite == null && !selected) rowNormalSprite = Resources.Load<Sprite>("ui/Friends/row");
        if (rowSelectedSprite == null && selected) rowSelectedSprite = Resources.Load<Sprite>("ui/Friends/row_sel");
        return selected ? rowSelectedSprite : rowNormalSprite;
    }

    /// <summary>
    /// 右栏空态提示（"从左侧选择一位好友开始聊天"）显隐：
    /// 只在好友列表页且没选中会话时显示；选人后由气泡区接管
    /// </summary>
    private void UpdateChatEmptyHint()
    {
        if (chatEmptyHint == null)
        {
            foreach (Transform t in transform.GetComponentsInChildren<Transform>(true))
            {
                if (t.name != "ChatEmptyHint") continue;
                chatEmptyHint = t.gameObject;
                break;
            }
        }
        if (chatEmptyHint == null) return;

        bool listPageActive = listPage == null || listPage.activeSelf;
        chatEmptyHint.SetActive(selectedId <= 0 && listPageActive);
    }

    /// <summary>判断消息是否我方发的（左右分栏用）</summary>
    private bool IsFromMe(ChatMessageItem message)
    {
        int myId = ClientNetworkManager.Instance != null ? ClientNetworkManager.Instance.MyPlayerId : 0;
        return message.SenderId == myId;
    }

    /// <summary>
    /// 量文字占位的方法（带缓存）
    /// 给文本最大可用宽度强制生成换行布局，量出实际宽高；结果按 MessageId 缓存，
    /// 同一条消息不管切多少次会话都只量一次；聊天区宽度变化时缓存整体失效重测
    /// </summary>
    /// <param name="messageId">参数名：messageId，类型：string，用途：缓存键（乐观项用 pending 前缀ID同样唯一）</param>
    /// <param name="content">参数名：content，类型：string，用途：消息正文</param>
    /// <returns>返回值类型：Vector2，用途：文字实际占位 (宽, 高)，气泡再按此加内边距</returns>
    private Vector2 MeasureText(string messageId, string content)
    {
        float areaWidth = messageListContent.rect.width;
        float maxTextWidth = Mathf.Max(120f, areaWidth - 2f * (BubbleSideMargin + BubblePadX));

        if (Mathf.Abs(measuredMaxTextWidth - maxTextWidth) > 0.5f)
        {
            measuredText.Clear();
            measuredMaxTextWidth = maxTextWidth;
        }
        if (measuredText.TryGetValue(messageId, out Vector2 size)) return size;

        if (measureText == null) return new Vector2(60f, 20f);   // 量宽文本未就绪时的兜底

        measureText.text = content ?? string.Empty;
        measureText.rectTransform.sizeDelta = new Vector2(maxTextWidth, measureText.rectTransform.sizeDelta.y);
        measureText.ForceMeshUpdate();
        size = new Vector2(Mathf.Min(maxTextWidth, measureText.renderedWidth) + 1f, measureText.renderedHeight);
        measuredText[messageId] = size;
        return size;
    }

    /// <summary>
    /// 判断消息是不是要按表情渲染的方法
    /// 只有「内容类型=表情」且「本客户端认识这个表情键」才返回 true；
    /// 不认识（对方用了更新版本的表情包）时降级走文字气泡，绝不显示空白
    /// </summary>
    /// <param name="message">参数名：message，类型：ChatMessageItem，用途：要判断的消息</param>
    /// <param name="sticker">参数名：sticker，类型：out StickerInfo，用途：取到的表情条目</param>
    /// <returns>返回值类型：bool，用途：true=按表情渲染</returns>
    private bool TryGetSticker(ChatMessageItem message, out StickerInfo sticker)
    {
        sticker = null;
        if (message == null || message.ContentType != ChatManager.ContentTypeSticker) return false;
        return StickerCatalog.TryGet(message.Content, out sticker);
    }

    /// <summary>
    /// 取消息用于显示的文本的方法
    /// 表情消息的 content 是表情键（不是给人看的正文），认不出这个键时用占位文案顶上
    /// </summary>
    private string GetDisplayText(ChatMessageItem message)
    {
        if (message.ContentType == ChatManager.ContentTypeSticker) return UnknownStickerText;
        return message.Content ?? string.Empty;
    }

    /// <summary>表情在气泡里的显示尺寸（按原图宽高比缩放到边长上限，不拉伸变形）</summary>
    private static Vector2 MeasureStickerSize(StickerInfo sticker)
    {
        float aspect = sticker.Aspect;   // 宽/高
        if (aspect >= 1f) return new Vector2(StickerMaxSize, StickerMaxSize / aspect);
        return new Vector2(StickerMaxSize * aspect, StickerMaxSize);
    }

    /// <summary>
    /// 量一条消息气泡尺寸的方法（文字与表情共用）
    /// 文字：按最大宽度强制换行后量实际占位，再套气泡内边距；
    /// 表情：QQ 式不带气泡底，气泡尺寸=表情尺寸本身（等比缩放，不变形）
    /// </summary>
    /// <param name="message">参数名：message，类型：ChatMessageItem，用途：要量的消息</param>
    /// <returns>返回值类型：Vector2，用途：气泡尺寸（宽, 高）</returns>
    private Vector2 MeasureMessage(ChatMessageItem message)
    {
        if (message == null) return new Vector2(60f, 20f);
        if (TryGetSticker(message, out StickerInfo sticker)) return MeasureStickerSize(sticker);

        Vector2 textSize = MeasureText(message.MessageId, GetDisplayText(message));
        return new Vector2(textSize.x + 2f * BubblePadX, textSize.y + 2f * BubblePadY);
    }

    /// <summary>
    /// 绑定消息气泡（池化复用）：按测量占位撑开气泡、填内容（文字或表情）、左右定位
    /// 池化复用要点：每次绑定都要把「文字/表情两条路径」的状态显式复位——
    /// 上一轮是表情的气泡改绑文字时必须停掉动画并清掉图，否则文字气泡会顶着上一张表情
    /// </summary>
    private void BindChatBubble(int index, RectTransform item)
    {
        ChatMessageItem message = currentHistory[index];
        bool fromMe = IsFromMe(message);
        bool isSticker = TryGetSticker(message, out StickerInfo sticker);
        Vector2 bubbleSize = MeasureMessage(message);

        item.name = $"Msg_{message.MessageId}";
        item.sizeDelta = bubbleSize;

        // 表情消息不要气泡底色（QQ 式只有图）；文字消息用模板自带底色
        Image bubbleBackground = item.GetComponent<Image>();
        if (bubbleBackground != null) bubbleBackground.enabled = !isSticker;

        TMP_Text text = item.transform.Find("BubbleText")?.GetComponent<TMP_Text>();
        Image stickerImage = item.transform.Find("BubbleSticker")?.GetComponent<Image>();
        StickerAnimation stickerAnimation = stickerImage != null
            ? stickerImage.GetComponent<StickerAnimation>()
            : null;

        if (isSticker && stickerImage != null)
        {
            if (text != null) text.gameObject.SetActive(false);
            stickerImage.gameObject.SetActive(true);
            stickerImage.rectTransform.sizeDelta = bubbleSize;
            stickerImage.rectTransform.anchoredPosition = Vector2.zero;   // 图回填气泡中心
            if (stickerAnimation != null) stickerAnimation.Play(sticker);
        }
        else
        {
            if (stickerAnimation != null) stickerAnimation.Stop();
            if (stickerImage != null)
            {
                stickerImage.sprite = null;
                stickerImage.gameObject.SetActive(false);
            }

            Vector2 textSize = MeasureText(message.MessageId, GetDisplayText(message));
            if (text != null)
            {
                text.gameObject.SetActive(true);
                text.text = GetDisplayText(message);
                text.rectTransform.sizeDelta = textSize;
                text.rectTransform.anchoredPosition = Vector2.zero;   // 文本回填气泡中心
            }
        }

        // 我方靠右、对方靠左（x 按气泡实际宽计算，y 由虚拟列表写入）
        float areaWidth = messageListContent.rect.width;
        float x = areaWidth * 0.5f - bubbleSize.x * 0.5f - BubbleSideMargin;
        item.anchoredPosition = new Vector2(fromMe ? x : -x, item.anchoredPosition.y);
    }

    /// <summary>
    /// 切换/更新当前渲染的会话（虚拟列表）
    /// 只实例化可见气泡，滚动时池化换绑；高度按文字测量缓存
    /// </summary>
    private void RebuildMessages(IReadOnlyList<ChatMessageItem> history)
    {
        if (messageListContent == null) return;

        currentHistory = history;
        if (chatVirtual == null) return;

        chatVirtual.RefreshData();

        // 最新消息在底部：滚动到底部展示
        chatVirtual.ScrollToBottom();
    }

    // ──────────────────────────────
    // 状态显示
    // ──────────────────────────────

    /// <summary>更新占位状态提示</summary>
    private void SetStatus(string message)
    {
        if (statusText != null) statusText.text = message;
        Debug.Log($"[好友UI]{message}");
    }
}
