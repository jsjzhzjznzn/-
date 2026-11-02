using System.Collections.Generic;

using Common.Network;

using Cysharp.Threading.Tasks;

using SkierFramework;

using TMPro;

using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 世界频道界面控制器（shijie Canvas）
/// 布局照任务/邮件面板：深色扁平 HUD——左上标题、右上红色关闭键、顶/底分隔线，
/// 中间是世界频道消息流（VirtualScrollList 变高行虚拟列表，只实例化视口内的行），
/// 底部是输入框 + 发送键 + 状态行
/// 数据全部来自 WorldChatManager（服务端广播 + 历史快照），本类只做渲染与交互转发
/// </summary>
public class WorldChatView : UIView
{
    // ──────────────────────────────
    // 序列化引用（编辑器搭建脚本注入）
    // ──────────────────────────────

    [Header("面板")]
    [ControlBinding]
    public Button closeButton;

    [Header("顶部")]
    [ControlBinding]
    public TMP_Text titleText;

    [Header("消息流")]
    [ControlBinding]
    public ScrollRect worldScroll;

    [ControlBinding("WorldScroll/Viewport/Content")]
    public RectTransform listContent;

    [ControlBinding("WorldScroll/Viewport/Content/RowTemplate")]
    public RectTransform rowTemplate;

    [Header("输入区")]
    [ControlBinding("WorldInput")]
    public TMP_InputField inputField;

    [ControlBinding]
    public Button sendButton;

    [Header("底部提示")]
    [ControlBinding]
    public TMP_Text statusText;

    [Header("视觉参数")]

    /// <summary>列表行宽（与 RowTemplate 尺寸一致；需 ≤ 视口宽）</summary>
    public float rowWidth = 620f;

    /// <summary>单条消息正文的最大长度（与服务端 ChatRepository.MaxContentLength 对齐）</summary>
    public int maxInputLength = 500;

    /// <summary>昵称颜色（HUD 强调色：钢蓝）</summary>
    public string nameColorHex = "#7FB2D9";

    // ──────────────────────────────
    // 私有字段
    // ──────────────────────────────

    /// <summary>列表虚拟组件</summary>
    private VirtualScrollList worldVirtual;

    /// <summary>量高文本：克隆行模板正文后挂在面板外（y=100000）被裁剪，仅用于量宽高</summary>
    private TMP_Text measurer;

    /// <summary>行高缓存（key=消息ID）：VirtualScrollList 每次 RefreshData 会逐行询问高度</summary>
    private readonly Dictionary<string, float> rowHeights = new();

    /// <summary>行内正文的左右留白（昵称与正文共用一个 TMP，靠行内边距留白）</summary>
    private const float RowPaddingX = 8f;

    /// <summary>行内正文的上下留白</summary>
    private const float RowPaddingY = 6f;

    /// <summary>测量未就绪时的兜底行高</summary>
    private const float FallbackRowHeight = 28f;

    /// <summary>虚拟列表内容顶部留白（与 VirtualScrollList.Configure 保持一致）</summary>
    private const float RowTopPadding = 8f;

    /// <summary>虚拟列表内容底部留白（与 Configure 保持一致）</summary>
    private const float RowBottomPadding = 8f;

    /// <summary>相邻行间距（与 Configure 保持一致；前置插入后重算滚动偏移要用它）</summary>
    private const float RowSpacing = 2f;

    /// <summary>上次渲染时列表首条消息ID：用于识别"本次是前置插入更早历史"（而不是普通追加/覆盖）</summary>
    private string lastFirstMessageId = string.Empty;

    /// <summary>发送键上的文字（冷却时显示倒计时）</summary>
    private TMP_Text sendButtonLabel;

    // ──────────────────────────────
    // Unity 生命周期
    // ──────────────────────────────

    /// <summary>绑定完成后的一次性初始化：订阅数据层 + 建立虚拟列表（实例常驻，只做一次）</summary>
    public override void OnInit(UIControlData uIControlData, UIViewController controller)
    {
        base.OnInit(uIControlData, controller);

        if (inputField != null) inputField.characterLimit = maxInputLength;
        if (sendButton != null) sendButtonLabel = sendButton.GetComponentInChildren<TMP_Text>();

        if (WorldChatManager.Instance != null)
        {
            WorldChatManager.Instance.WorldUpdated += OnWorldUpdated;
            WorldChatManager.Instance.SendFailed += SetStatus;
        }

        SetupWorldList();

        // 上拉加载更早历史：滚到顶部时由 ScrollRect 回调触发
        if (worldScroll != null) worldScroll.onValueChanged.AddListener(OnWorldScrollChanged);
    }

    /// <summary>每次打开重新接线（关闭时框架走 OnRemoveListener 摘掉）</summary>
    public override void OnAddListener()
    {
        if (closeButton != null) closeButton.onClick.AddListener(OnCloseClicked);
        if (sendButton != null) sendButton.onClick.AddListener(OnSubmit);
        if (inputField != null) inputField.onSubmit.AddListener(OnSubmitFromInput);
    }

    public override void OnRemoveListener()
    {
        if (closeButton != null) closeButton.onClick.RemoveListener(OnCloseClicked);
        if (sendButton != null) sendButton.onClick.RemoveListener(OnSubmit);
        if (inputField != null) inputField.onSubmit.RemoveListener(OnSubmitFromInput);
    }

    public override void OnOpen(object userData)
    {
        base.OnOpen(userData);

        SetStatus(string.Empty);
        WorldChatManager.Instance?.Open();   // 已预热/已缓存则直接复用；未加载才发请求
        // 登录预热还没回来（例如登录后 1 秒内就点开）：给一次加载提示，避免空白面板像"没内容"
        if (WorldChatManager.Instance != null && !WorldChatManager.Instance.Loaded)
        {
            SetStatus(LoadingFirstText);
        }
        RefreshList(true);                    // 打开即贴底（看最新）
    }

    public override void OnClose()
    {
        base.OnClose();
        WorldChatManager.Instance?.Close();
    }

    private void OnSubmitFromInput(string _) => OnSubmit();

    private void OnDestroy()
    {
        if (worldScroll != null) worldScroll.onValueChanged.RemoveListener(OnWorldScrollChanged);
        if (WorldChatManager.Instance == null) return;
        WorldChatManager.Instance.WorldUpdated -= OnWorldUpdated;
        WorldChatManager.Instance.SendFailed -= SetStatus;
    }

    private void Update()
    {
        // 发送键冷却显示：真正的限速在服务端，这里只做本地节流与倒计时
        if (sendButton == null || sendButtonLabel == null) return;
        float remaining = WorldChatManager.Instance != null ? WorldChatManager.Instance.CooldownRemaining : 0f;
        bool cooling = remaining > 0.05f;
        sendButton.interactable = !cooling;
        sendButtonLabel.text = cooling ? $"{Mathf.CeilToInt(remaining)}s" : "发送";
    }

    // ──────────────────────────────
    // 面板开关
    // ──────────────────────────────

    private void OnCloseClicked()
    {
        UIManager.Instance.Close(UIType.WorldChatView).Forget();
    }

    /// <summary>面板已打开时收到更新：数据变化 → 重渲染；原本贴底则继续贴底（微信式）</summary>
    private void OnWorldUpdated()
    {
        if (!gameObject.activeSelf) return;
        RefreshList(IsListAtBottom());

        // 首页历史到达：收掉打开时的加载提示
        if (statusText != null && statusText.text == LoadingFirstText
            && WorldChatManager.Instance != null && WorldChatManager.Instance.Loaded)
        {
            SetStatus(string.Empty);
        }

        // 上拉加载已结束：把"正在加载更早的消息…"收掉（加载失败/无更多由对应分支给提示）
        if (statusText != null && statusText.text == LoadingOlderText
            && WorldChatManager.Instance != null && !WorldChatManager.Instance.IsLoadingOlder)
        {
            SetStatus(string.Empty);
        }
    }

    // ──────────────────────────────
    // 发送
    // ──────────────────────────────

    /// <summary>
    /// 发送键/回车提交：本地校验（非空、长度、已连接、冷却）后交给管理器乐观发送
    /// </summary>
    private void OnSubmit()
    {
        if (inputField == null) return;
        string text = inputField.text.Trim();
        if (text.Length == 0)
        {
            SetStatus("请输入内容");
            return;
        }
        if (text.Length > maxInputLength)
        {
            SetStatus($"消息内容不能超过{maxInputLength}字");
            return;
        }
        if (ClientNetworkManager.Instance == null || !ClientNetworkManager.Instance.IsConnected)
        {
            SetStatus("未连接服务器，无法发言");
            return;
        }
        if (WorldChatManager.Instance == null) return;
        if (WorldChatManager.Instance.CooldownRemaining > 0.05f)
        {
            SetStatus($"发言太快，请 {Mathf.CeilToInt(WorldChatManager.Instance.CooldownRemaining)} 秒后再试");
            return;
        }

        WorldChatManager.Instance.Send(text);
        inputField.text = string.Empty;
        inputField.ActivateInputField();   // 保持焦点，方便连续发言
        SetStatus(string.Empty);
    }

    private void SetStatus(string message)
    {
        if (statusText == null) return;
        statusText.text = message ?? string.Empty;
    }

    // ──────────────────────────────
    // 虚拟列表接入
    // ──────────────────────────────

    /// <summary>
    /// 接入世界频道虚拟列表的方法
    /// 行高按内容自适应（变高行）、单池；CreateItem 克隆 RowTemplate 到 Content
    /// </summary>
    private void SetupWorldList()
    {
        if (worldScroll == null || listContent == null || rowTemplate == null) return;

        // 量高文本：克隆行模板正文，挂在面板外（被视口裁剪），只用于量宽高（与好友聊天气泡同款做法）
        TMP_Text templateText = rowTemplate.GetComponentInChildren<TMP_Text>(true);
        if (templateText != null)
        {
            measurer = Instantiate(templateText, transform);
            measurer.name = "RowMeasurer";
            measurer.gameObject.SetActive(true);
            measurer.rectTransform.anchorMin = new Vector2(0f, 0f);
            measurer.rectTransform.anchorMax = new Vector2(0f, 0f);
            measurer.rectTransform.pivot = new Vector2(0f, 1f);
            measurer.rectTransform.anchoredPosition = new Vector2(0f, 100000f);   // 远在屏幕外，不影响显示
        }

        worldVirtual = worldScroll.GetComponent<VirtualScrollList>();
        if (worldVirtual == null) worldVirtual = worldScroll.gameObject.AddComponent<VirtualScrollList>();
        worldVirtual.Configure(RowTopPadding, RowBottomPadding, RowSpacing);
        worldVirtual.GetItemCount = () => WorldChatManager.Instance != null ? WorldChatManager.Instance.Messages.Count : 0;
        worldVirtual.GetItemHeight = MeasureRowHeight;
        worldVirtual.CreateItem = _ =>
        {
            RectTransform row = Instantiate(rowTemplate, listContent);
            row.gameObject.SetActive(true);
            return row;
        };
        worldVirtual.BindItem = BindRow;
    }

    /// <summary>滚到顶部时拉更早的一页（"上拉加载更早"入口）；到顶但已无更早历史时给一次提示</summary>
    private void OnWorldScrollChanged(Vector2 _)
    {
        if (worldScroll == null || worldVirtual == null) return;
        if (worldScroll.verticalNormalizedPosition < 0.999f) return;   // 只有真正贴顶才触发

        // 内容不足一屏时不触发（否则会一直"到顶"连拉，把整条频道一次拉干）
        RectTransform viewport = worldScroll.viewport;
        if (viewport != null && worldVirtual.ContentHeight <= viewport.rect.height + 0.5f) return;

        var manager = WorldChatManager.Instance;
        if (manager == null || !manager.Loaded) return;
        if (manager.IsLoadingOlder) return;

        if (!manager.HasMoreHistory)
        {
            SetStatus("没有更早的消息了");
            return;
        }
        manager.RequestOlder();
        SetStatus(LoadingOlderText);
    }

    /// <summary>上拉加载中的状态行文案（加载结束后按原文案匹配清除）</summary>
    private const string LoadingOlderText = "正在加载更早的消息…";

    /// <summary>首次打开且首页历史尚未就绪（预热未回/无预热）时的状态行文案</summary>
    private const string LoadingFirstText = "正在加载频道消息…";

    /// <summary>
    /// 重渲染列表
    /// stickBottom=true 时重渲染后贴底；若本次是因为"前置插入更早历史"导致的数据变化，
    /// 则改为把【原本首条消息】钉在视口顶部（保持用户正在看的位置不跳）
    /// </summary>
    private void RefreshList(bool stickBottom)
    {
        if (worldVirtual == null) return;

        IReadOnlyList<ChatMessageItem> messages =
            WorldChatManager.Instance != null ? WorldChatManager.Instance.Messages : null;

        // 判断本次是不是"前置插入"：原首条消息仍在列表中，但已不在第 0 位
        int prepended = 0;
        string oldFirstId = lastFirstMessageId;
        if (messages != null && !string.IsNullOrEmpty(oldFirstId))
        {
            if (messages.Count == 0 || messages[0].MessageId != oldFirstId)
            {
                for (int i = 1; i < messages.Count; i++)
                {
                    if (messages[i].MessageId == oldFirstId) { prepended = i; break; }
                }
            }
        }

        // 数据被裁剪/清空（换账号、条数变化）时行高缓存失效，重建一次
        if (messages != null && !CacheShapeMatches(messages))
        {
            rowHeights.Clear();
            cachedCount = messages.Count;
            cachedFirstId = messages.Count > 0 ? messages[0].MessageId : string.Empty;
        }

        worldVirtual.RefreshData();

        if (prepended > 0)
        {
            // 前置了 prepended 行：把内容往下推这么多像素，用户看到的还是同一条消息
            float addedHeight = 0f;
            for (int i = 0; i < prepended; i++) addedHeight += MeasureRowHeight(i) + RowSpacing;
            PreserveScrollAfterPrepend(addedHeight);
        }
        else if (stickBottom)
        {
            worldVirtual.ScrollToBottom();
        }

        lastFirstMessageId = messages != null && messages.Count > 0 ? messages[0].MessageId : string.Empty;
    }

    /// <summary>
    /// 前置插入后保持视线位置：滚动偏移从 0 变成 addedHeight（同一条消息仍在视口顶部）
    /// 归一化位置 = 1 - 偏移/可滚动高度（1=顶部、0=底部）
    /// </summary>
    private void PreserveScrollAfterPrepend(float addedHeight)
    {
        if (worldScroll == null || worldVirtual == null) return;
        Canvas.ForceUpdateCanvases();   // Content 尺寸刚变，先让 ScrollRect 的 bounds 跟上，否则赋值空操作
        float viewportHeight = worldScroll.viewport != null ? worldScroll.viewport.rect.height : 0f;
        float scrollable = Mathf.Max(0f, worldVirtual.ContentHeight - viewportHeight);
        if (scrollable <= 0.5f) return;
        worldScroll.verticalNormalizedPosition = Mathf.Clamp01(1f - addedHeight / scrollable);
        worldVirtual.RefreshView();
    }

    /// <summary>行高缓存是否仍然对应当前数据（条数与被裁剪的头部没变）</summary>
    private bool CacheShapeMatches(IReadOnlyList<ChatMessageItem> messages)
    {
        if (messages.Count != cachedCount) return false;
        string firstId = messages.Count > 0 ? messages[0].MessageId : string.Empty;
        return firstId == cachedFirstId;
    }

    /// <summary>行是否贴在底部（内容不足一屏时恒真）</summary>
    private bool IsListAtBottom()
    {
        if (worldVirtual == null || worldScroll == null) return true;
        RectTransform viewport = worldScroll.viewport;
        if (viewport == null) return true;
        if (worldVirtual.ContentHeight <= viewport.rect.height + 0.5f) return true;
        return worldScroll.verticalNormalizedPosition <= 0.002f;
    }

    /// <summary>
    /// 行换绑：把「昵称：内容」渲染成一行可换行的富文本
    /// 行是池化复用的，换绑时必须重置子物体状态与尺寸
    /// </summary>
    private void BindRow(int index, RectTransform row)
    {
        IReadOnlyList<ChatMessageItem> messages =
            WorldChatManager.Instance != null ? WorldChatManager.Instance.Messages : null;
        if (messages == null || index < 0 || index >= messages.Count) return;

        ChatMessageItem message = messages[index];
        float height = MeasureRowHeight(index);

        row.sizeDelta = new Vector2(rowWidth, height);
        row.anchoredPosition = new Vector2(0f, row.anchoredPosition.y);

        TMP_Text text = row.GetComponentInChildren<TMP_Text>(true);
        if (text == null) return;
        text.text = BuildRowText(message);
        text.rectTransform.sizeDelta = new Vector2(rowWidth - 2f * RowPaddingX, height - 2f * RowPaddingY);
        text.rectTransform.anchoredPosition = new Vector2(RowPaddingX, -RowPaddingY);
    }

    /// <summary>
    /// 组装一行文本：昵称染色 + 正文（表情消息降级显示为占位文案）
    /// 正文里的尖括号替换为全角，避免玩家输入被当作富文本标签解释
    /// </summary>
    private string BuildRowText(ChatMessageItem message)
    {
        string name = string.IsNullOrEmpty(message.SenderName) ? $"玩家{message.SenderId}" : message.SenderName;
        string body = message.ContentType == WorldChatManager.ContentTypeSticker
            ? "［表情］"
            : EscapeRichText(message.Content);
        return $"<color={nameColorHex}>{EscapeRichText(name)}</color>：{body}";
    }

    /// <summary>把可能被 TMP 当作标签的尖括号换成全角，防止玩家输入注入富文本标记</summary>
    private static string EscapeRichText(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        return raw.Replace('<', '＜').Replace('>', '＞');
    }

    /// <summary>
    /// 量第 index 行的高度（带缓存）：设置量高文本宽度 → ForceMeshUpdate → 读实际渲染高度
    /// </summary>
    private float MeasureRowHeight(int index)
    {
        IReadOnlyList<ChatMessageItem> messages =
            WorldChatManager.Instance != null ? WorldChatManager.Instance.Messages : null;
        if (messages == null || index < 0 || index >= messages.Count) return FallbackRowHeight;

        ChatMessageItem message = messages[index];
        if (rowHeights.TryGetValue(message.MessageId, out float cached)) return cached;
        if (measurer == null) return FallbackRowHeight;

        measurer.text = BuildRowText(message);
        measurer.rectTransform.sizeDelta = new Vector2(rowWidth - 2f * RowPaddingX, measurer.rectTransform.sizeDelta.y);
        measurer.ForceMeshUpdate();
        float height = measurer.renderedHeight + 2f * RowPaddingY + 2f;   // 上下留白 + 行间距余量
        height = Mathf.Max(FallbackRowHeight, height);

        rowHeights[message.MessageId] = height;
        return height;
    }

    /// <summary>行高缓存对应的数据形态（条数 + 头部消息ID）</summary>
    private int cachedCount = -1;

    private string cachedFirstId = string.Empty;
}
