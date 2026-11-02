using System;
using System.Collections;
using System.Collections.Generic;

using Common.Network;

using UnityEngine;

/// <summary>
/// 世界频道缓存管理器
/// 与私聊 ChatManager 的区别：世界频道是全体在线玩家共享的一条公共消息流——
/// 没有"会话槽"（只有一条线性列表）、没有未读/红点、发送后由服务端广播给所有人（包括自己）
/// 数据流：UI（WorldChatUIController）只订阅 WorldUpdated 重渲染，不直接碰网络
/// </summary>
public class WorldChatManager : MonoBehaviour
{
    // ──────────────────────────────
    // 单例实现
    // ──────────────────────────────

    /// <summary>全局唯一实例（UI 层访问世界频道缓存的入口）</summary>
    public static WorldChatManager Instance { get; private set; }

    /// <summary>
    /// 运行时自举
    /// 进入 Play 模式自动创建，无需手动挂载（与 ClientNetworkManager / ChatManager 相同模式）
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance != null) return;
        GameObject holder = new GameObject("WorldChatManager");
        holder.AddComponent<WorldChatManager>();
        DontDestroyOnLoad(holder);
    }

    // ──────────────────────────────
    // 事件（主线程触发，UI 可安全订阅）
    // ──────────────────────────────

    /// <summary>世界频道有更新（历史到达/新消息到达/乐观项校准或回滚）；UI 据此重渲染</summary>
    public event Action WorldUpdated;

    /// <summary>乐观消息被拒绝或回滚（参数=可直接展示的原因文案）；UI 提示用</summary>
    public event Action<string> SendFailed;

    // ──────────────────────────────
    // 常量
    // ──────────────────────────────

    /// <summary>服务端每人发送冷却秒数（与 ChatHandler.WorldSendCooldownMs 对齐）：客户端只做按钮节流显示</summary>
    public const float SendCooldownSeconds = 3f;

    /// <summary>
    /// 单页拉取条数（世界频道翻页粒度）
    /// 首页与"上拉加载更早"共用它：返回条数小于它即视为没有更早的历史
    /// </summary>
    public static int HistoryPageSize = 50;

    /// <summary>消息内容类型：文本（默认）</summary>
    public const int ContentTypeText = 0;

    /// <summary>消息内容类型：表情包（此时 content 是表情键）</summary>
    public const int ContentTypeSticker = 1;

    /// <summary>本地缓存条数上限：世界频道是流式频道，超出后从头部裁剪，不无限增长（也是"上拉加载"的硬上限）</summary>
    private const int MaxCachedMessages = 500;

    /// <summary>乐观项的临时消息ID前缀，回执到达后按它定位</summary>
    private const string PendingPrefix = "pending_";

    /// <summary>本机账号名缓存键：乐观气泡先显示自己的名字，回执到达后与服务端下发的昵称对齐</summary>
    private const string AccountPrefsKey = "Login_Account";

    // ──────────────────────────────
    // 状态
    // ──────────────────────────────

    /// <summary>消息缓存（旧→新排序，可直接从上到下渲染）</summary>
    private readonly List<ChatMessageItem> messages = new();

    /// <summary>已缓存的消息ID集合：广播去重用（服务端把消息广播给包括发送者在内的全部在线会话）</summary>
    private readonly HashSet<string> messageIds = new();

    /// <summary>
    /// 在途发送队列（FIFO 配对）
    /// WorldChatSendResponse 不带内容标识，靠"发送顺序=回执顺序"（TCP 有序 + 服务端同会话串行处理）配对
    /// </summary>
    private readonly Queue<string> inflight = new();

    /// <summary>乐观项计数器（生成唯一临时ID）</summary>
    private int pendingCounter;

    /// <summary>是否已成功拉取过历史（登录/换账号后复位，下次打开面板重新拉）</summary>
    public bool Loaded { get; private set; }

    /// <summary>
    /// 是否还可能存在更早的历史（"上拉加载更早"的开关）
    /// 上一页返回条数小于页大小、或已到缓存上限时为 false
    /// </summary>
    public bool HasMoreHistory { get; private set; } = true;

    /// <summary>是否正在拉取更早的一页（UI 据此显示"加载中"并防重复触发）</summary>
    public bool IsLoadingOlder { get; private set; }

    /// <summary>已缓存最早一条的服务端时间戳，作为翻页游标（beforeMs）</summary>
    private long oldestLoadedMs;

    /// <summary>
    /// 在途历史请求的游标（-1=无在途；0=首页；&gt;0=更早页）
    /// 历史响应里不带游标，靠它区分"整表覆盖"与"前置插入"
    /// </summary>
    private long pendingBeforeMs = -1;

    /// <summary>面板是否处于打开状态（世界频道本期没有未读概念，仅作 UI/测试语义）</summary>
    public bool IsOpen { get; set; }

    /// <summary>上次成功发送的本地时刻（unscaled），用于发送按钮的冷却显示</summary>
    private float lastSendTime = float.NegativeInfinity;

    /// <summary>当前缓存的消息（旧→新，只读）</summary>
    public IReadOnlyList<ChatMessageItem> Messages => messages;

    // ──────────────────────────────
    // 登录消息预热
    // ──────────────────────────────

    /// <summary>预热总开关（出问题可一键关闭，退回"打开面板才拉"）</summary>
    public bool prewarmEnabled = true;

    /// <summary>主页就绪后再延迟多久开始预读首页历史（秒，真实时间）：让主页自身请求先走完</summary>
    public float prewarmDelay = 0.5f;

    /// <summary>等待主页 UI 就绪的上限（秒）：超时后仍开始预热，避免从非 zhuye 入口登录时永不预热</summary>
    public float prewarmHomeReadyTimeout = 10f;

    /// <summary>历史请求超时（秒）：超时回退为未加载，下次打开面板可重试</summary>
    public float prewarmTimeout = 10f;

    /// <summary>预热协程句柄（换号/重登时停掉）</summary>
    private Coroutine prewarmRoutine;

    /// <summary>在途历史请求的发出时刻（unscaled，-1=无在途）</summary>
    private float pendingSince = -1f;

    /// <summary>距离下次可发送还剩几秒（0=可发送）：纯本地节流显示，真正的限速在服务端权威判定</summary>
    public float CooldownRemaining => Mathf.Max(0f, SendCooldownSeconds - (Time.unscaledTime - lastSendTime));

    /// <summary>本机账号名（仅用于乐观项占位；服务端回执会带权威昵称）</summary>
    private static string MyAccountName
    {
        get
        {
            string account = PlayerPrefs.GetString(AccountPrefsKey, string.Empty);
            return string.IsNullOrEmpty(account) ? "我" : account;
        }
    }

    // ──────────────────────────────
    // Unity 生命周期
    // ──────────────────────────────

    private void Awake()
    {
        if (Instance != null)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {
        // ClientNetworkManager 的自举同样发生在 BeforeSceneLoad，Start 时必已存在
        if (ClientNetworkManager.Instance == null) return;
        ClientNetworkManager.Instance.WorldChatSendResponseReceived += OnWorldChatSendResponse;
        ClientNetworkManager.Instance.WorldChatHistoryResponseReceived += OnWorldChatHistoryResponse;
        ClientNetworkManager.Instance.WorldChatNotifyReceived += OnWorldChatNotify;
        ClientNetworkManager.Instance.LoginResponseReceived += OnLoginResponse;
        ClientNetworkManager.Instance.ConnectionBroken += OnConnectionBroken;
    }

    private void OnDestroy()
    {
        if (ClientNetworkManager.Instance == null) return;
        ClientNetworkManager.Instance.WorldChatSendResponseReceived -= OnWorldChatSendResponse;
        ClientNetworkManager.Instance.WorldChatHistoryResponseReceived -= OnWorldChatHistoryResponse;
        ClientNetworkManager.Instance.WorldChatNotifyReceived -= OnWorldChatNotify;
        ClientNetworkManager.Instance.LoginResponseReceived -= OnLoginResponse;
        ClientNetworkManager.Instance.ConnectionBroken -= OnConnectionBroken;
    }

    // ──────────────────────────────
    // 公开接口
    // ──────────────────────────────

    /// <summary>
    /// 打开世界频道的方法
    /// 首次打开（或换账号后）拉取历史；已缓存则 0 网络开销直接复用。
    /// 首页请求在途（含登录预热）时不重复发：复用即将到达的那一份结果。
    /// </summary>
    public void Open()
    {
        IsOpen = true;
        if (Loaded || ClientNetworkManager.Instance == null) return;
        if (pendingBeforeMs == 0) return;   // 首页请求在途（预热/上一次打开），等它回来即可
        Debug.Log("[世界频道]首次打开，拉取历史");
        RequestHistoryPage(0);
    }

    /// <summary>
    /// 登录预热：不等用户打开面板，后台预读首页历史
    /// 与 Open 共用同一条请求链，响应到达后 Loaded=true，之后打开面板即秒开
    /// </summary>
    public void Prewarm()
    {
        if (!prewarmEnabled || Loaded) return;
        if (ClientNetworkManager.Instance == null || !ClientNetworkManager.Instance.IsConnected) return;
        if (pendingBeforeMs == 0 || prewarmRoutine != null) return;
        prewarmRoutine = StartCoroutine(PrewarmRoutine());
    }

    /// <summary>延迟启动预热协程：先等主页 UI 就绪，再等一小段；真实时间等待，不受 timeScale 影响</summary>
    private IEnumerator PrewarmRoutine()
    {
        float readyStart = Time.realtimeSinceStartup;
        while (!HomeViewLauncher.IsReady
               && Time.realtimeSinceStartup - readyStart < prewarmHomeReadyTimeout)
        {
            yield return null;
        }

        yield return new WaitForSecondsRealtime(Mathf.Max(0f, prewarmDelay));
        prewarmRoutine = null;

        if (Loaded) yield break;
        if (ClientNetworkManager.Instance == null || !ClientNetworkManager.Instance.IsConnected) yield break;
        if (pendingBeforeMs == 0) yield break;

        Debug.Log($"[世界频道]登录预热，拉取首页历史{HistoryPageSize}条（主页就绪={HomeViewLauncher.IsReady}）");
        RequestHistoryPage(0);
    }

    /// <summary>
    /// 在途请求超时扫描：服务端正常必有回执，超时说明响应丢失/连接异常。
    /// 回退为未加载并清掉在途标记，用户下次打开面板可重新拉取。
    /// </summary>
    private void Update()
    {
        if (pendingSince < 0f) return;
        if (Time.unscaledTime - pendingSince < prewarmTimeout) return;

        pendingSince = -1f;
        pendingBeforeMs = -1;
        IsLoadingOlder = false;
        Debug.LogWarning("[世界频道]历史请求超时，已回退为未加载（下次打开可重试）");
    }

    /// <summary>关闭世界频道（仅置状态标记，缓存保留供下次秒开）</summary>
    public void Close()
    {
        IsOpen = false;
    }

    /// <summary>
    /// 乐观发送一条文本消息的方法
    /// 立刻把 pending 项追加到列表尾部并触发更新（UI 即刻显示），再发协议；
    /// 回执到达后用服务端 Echo 替换 pending（校准消息ID/时间戳/昵称），失败则移除 pending 回滚
    /// </summary>
    /// <param name="content">参数名：content，类型：string，用途：消息正文（调用方已做长度校验）</param>
    public void Send(string content)
    {
        Send(content, ContentTypeText);
    }

    /// <summary>
    /// 乐观发送一条表情消息的方法
    /// 与文本走同一条链路，只是内容类型标成表情、content 存的是表情键
    /// </summary>
    /// <param name="stickerKey">参数名：stickerKey，类型：string，用途：表情键（StickerCatalog 里的键）</param>
    public void SendSticker(string stickerKey)
    {
        Send(stickerKey, ContentTypeSticker);
    }

    /// <summary>
    /// 乐观发送的统一实现（文本/表情共用）
    /// </summary>
    /// <param name="content">参数名：content，类型：string，用途：正文或表情键</param>
    /// <param name="contentType">参数名：contentType，类型：int，用途：消息内容类型（0文本/1表情）</param>
    private void Send(string content, int contentType)
    {
        if (ClientNetworkManager.Instance == null) return;

        string pendingId = $"{PendingPrefix}{++pendingCounter}";
        ChatMessageItem pending = new ChatMessageItem
        {
            MessageId = pendingId,
            SenderId = ClientNetworkManager.Instance.MyPlayerId,
            ReceiverId = 0,   // 公共频道没有具体接收人
            Content = content,
            ContentType = contentType,
            SenderName = MyAccountName,
            // 本地临时时间戳，仅用于排序占位；回执到达后以服务端时间为准
            SentAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        Append(pending);
        inflight.Enqueue(pendingId);
        WorldUpdated?.Invoke();

        _ = ClientNetworkManager.Instance.SendWorldChatSend(content, contentType);
    }

    /// <summary>
    /// 上拉加载更早历史的方法
    /// 由 UI 在"滚到列表顶部且还有更早历史"时调用；同一时刻只允许一页在途
    /// </summary>
    public void RequestOlder()
    {
        if (!Loaded || IsLoadingOlder || !HasMoreHistory) return;
        if (oldestLoadedMs <= 0) return;   // 还没有可用的翻页游标
        IsLoadingOlder = true;
        Debug.Log($"[世界频道]上拉加载更早历史 beforeMs={oldestLoadedMs}");
        RequestHistoryPage(oldestLoadedMs);
    }

    /// <summary>发起一页历史请求（beforeMs=0 为首页）</summary>
    private void RequestHistoryPage(long beforeMs)
    {
        if (ClientNetworkManager.Instance == null) return;
        pendingBeforeMs = beforeMs;
        pendingSince = Time.unscaledTime;
        _ = ClientNetworkManager.Instance.SendWorldChatHistory(beforeMs, HistoryPageSize);
    }

    // ──────────────────────────────
    // 网络事件处理
    // ──────────────────────────────

    /// <summary>
    /// 发送回执处理
    /// 成功→按在途队列配对，移除 pending 并补入服务端 Echo（若广播已先到达同一条消息，则只移除不重复追加）
    /// 失败→移除 pending（回滚）并触发 SendFailed
    /// </summary>
    private void OnWorldChatSendResponse(WorldChatSendResponse response)
    {
        if (inflight.Count == 0) return;
        string pendingId = inflight.Dequeue();
        int index = messages.FindIndex(message => message.MessageId == pendingId);

        if (response.Success && response.Echo != null)
        {
            lastSendTime = Time.unscaledTime;   // 服务端已受理：客户端按钮进入冷却
            messageIds.Remove(pendingId);
            if (index >= 0) messages.RemoveAt(index);

            // 广播可能比回执先到：同一条消息已按服务端 MessageId 入列时不再重复追加
            Append(response.Echo);

            WorldUpdated?.Invoke();
            return;
        }

        if (index >= 0)
        {
            messageIds.Remove(pendingId);
            messages.RemoveAt(index);
        }
        Debug.Log($"[世界频道]发送失败:{response.Message}");
        SendFailed?.Invoke(string.IsNullOrEmpty(response.Message) ? "发送失败" : response.Message);
        WorldUpdated?.Invoke();
    }

    /// <summary>
    /// 历史响应处理
    /// 两种语义靠"在途游标"区分：
    /// ①首页（beforeMs=0）：服务端列表为权威，整体覆盖本地（本地 pending 乐观项先取出再补回尾部）
    /// ②更早页（beforeMs&gt;0）：按 MessageId 去重后正序前置插入到列表头部，不清空既有缓存
    /// </summary>
    private void OnWorldChatHistoryResponse(WorldChatHistoryResponse response)
    {
        bool isOlderPage = pendingBeforeMs > 0;
        pendingBeforeMs = -1;
        pendingSince = -1f;
        IsLoadingOlder = false;

        if (isOlderPage)
        {
            List<ChatMessageItem> older = new();
            foreach (ChatMessageItem message in response.Messages)
            {
                if (message == null) continue;
                // 去重：翻页边界可能与已有缓存重叠（同一毫秒多条时游标为严格小于，正常不重叠，重叠也能兜住）
                if (!string.IsNullOrEmpty(message.MessageId) && !messageIds.Add(message.MessageId)) continue;
                older.Add(message);
            }
            if (older.Count > 0) messages.InsertRange(0, older);
            Trim();

            // 返回条数不足一页 或 已到缓存上限 → 没有更早的了（UI 据此停止上拉并提示）
            if (response.Messages.Count < HistoryPageSize || messages.Count >= MaxCachedMessages) HasMoreHistory = false;
            UpdateOldestCursor();

            Debug.Log($"[世界频道]更早历史到达:{response.Count}条(前置新增{older.Count}) 共{messages.Count}条 hasMore={HasMoreHistory}");
            WorldUpdated?.Invoke();
            return;
        }

        List<ChatMessageItem> pendingOnly = new();
        foreach (ChatMessageItem message in messages)
        {
            if (message.MessageId.StartsWith(PendingPrefix, StringComparison.Ordinal))
            {
                pendingOnly.Add(message);
            }
        }

        messages.Clear();
        messageIds.Clear();
        foreach (ChatMessageItem message in response.Messages) Append(message);
        foreach (ChatMessageItem message in pendingOnly) Append(message);

        HasMoreHistory = response.Messages.Count >= HistoryPageSize && messages.Count > 0;
        UpdateOldestCursor();
        Loaded = true;
        Debug.Log($"[世界频道]历史到达:{response.Count}条 hasMore={HasMoreHistory}");
        WorldUpdated?.Invoke();
    }

    /// <summary>
    /// 刷新翻页游标：取列表里最早一条"服务端消息"的时间戳（跳过乐观 pending 项）
    /// </summary>
    private void UpdateOldestCursor()
    {
        oldestLoadedMs = 0;
        foreach (ChatMessageItem message in messages)
        {
            if (message.MessageId.StartsWith(PendingPrefix, StringComparison.Ordinal)) continue;
            if (message.SentAtMs > 0)
            {
                oldestLoadedMs = message.SentAtMs;
                return;
            }
        }
    }

    /// <summary>
    /// 广播处理
    /// 服务端把每条世界消息广播给全部在线会话（含发送者自己）：按 MessageId 去重后追加到尾部
    /// </summary>
    private void OnWorldChatNotify(WorldChatNotify notify)
    {
        if (notify.Message == null) return;
        Append(notify.Message);
        WorldUpdated?.Invoke();
    }

    /// <summary>
    /// 连接断开处理（可能触发多次，幂等）
    /// 连接死亡意味着在途回执永远不会再到达：全部 pending 回滚，否则乐观气泡永久卡在列表里
    /// </summary>
    private void OnConnectionBroken()
    {
        IsLoadingOlder = false;
        pendingBeforeMs = -1;
        pendingSince = -1f;
        if (prewarmRoutine != null)
        {
            StopCoroutine(prewarmRoutine);
            prewarmRoutine = null;
        }
        if (inflight.Count == 0) return;

        while (inflight.Count > 0)
        {
            string pendingId = inflight.Dequeue();
            int index = messages.FindIndex(message => message.MessageId == pendingId);
            if (index < 0) continue;
            messageIds.Remove(pendingId);
            messages.RemoveAt(index);
        }
        Debug.Log("[世界频道]连接断开，回滚在途消息");
        SendFailed?.Invoke("连接已断开");
        WorldUpdated?.Invoke();
    }

    /// <summary>
    /// 登录响应处理
    /// 登录成功即清空缓存与在途队列：换账号重登/断线重连都拿到干净状态，防止串号
    /// （世界频道是公共流，重登后重新拉一次历史即可补齐）
    /// </summary>
    private void OnLoginResponse(LoginResponse response)
    {
        if (!response.Success) return;
        messages.Clear();
        messageIds.Clear();
        inflight.Clear();
        Loaded = false;
        IsOpen = false;
        lastSendTime = float.NegativeInfinity;
        HasMoreHistory = true;
        IsLoadingOlder = false;
        pendingBeforeMs = -1;
        pendingSince = -1f;
        oldestLoadedMs = 0;

        if (prewarmRoutine != null)
        {
            StopCoroutine(prewarmRoutine);
            prewarmRoutine = null;
        }

        // 登录成功即排队预读：用户即使不打开面板，最近的世界频道消息也会先到内存
        Prewarm();
    }

    // ──────────────────────────────
    // 内部工具
    // ──────────────────────────────

    /// <summary>追加一条消息（按 MessageId 去重）并裁剪超上限的历史</summary>
    private void Append(ChatMessageItem message)
    {
        if (message == null) return;
        if (!string.IsNullOrEmpty(message.MessageId) && !messageIds.Add(message.MessageId)) return;
        messages.Add(message);
        Trim();
    }

    /// <summary>从头部裁剪超出上限的消息（同时清理去重集合）</summary>
    private void Trim()
    {
        while (messages.Count > MaxCachedMessages)
        {
            messageIds.Remove(messages[0].MessageId);
            messages.RemoveAt(0);
        }
    }
}
