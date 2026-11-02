using System;
using System.Collections;
using System.Collections.Generic;

using Common.Network;

using MH;

using UnityEngine;

/// <summary>
/// 聊天会话缓存管理器
/// 持有全部会话的消息缓存：每人一个槽（Dictionary&lt;对方ID, 消息列表&gt;），不是共用一个数组
/// 订阅网络层聊天事件，把 推送/历史/回执 归位到对应的人——不管当前 UI 正在看谁
/// UI（FriendUIController）只订阅 ConversationUpdated 渲染当前会话，不直接碰网络
/// </summary>
public class ChatManager : MonoBehaviour
{
    // ──────────────────────────────
    // 单例实现
    // ──────────────────────────────

    /// <summary>全局唯一实例（UI 层访问会话缓存的入口）</summary>
    public static ChatManager Instance { get; private set; }

    /// <summary>
    /// 运行时自举
    /// 进入 Play 模式自动创建，无需手动挂载（与 ClientNetworkManager 相同模式）
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance != null) return;
        GameObject holder = new GameObject("ChatManager");
        holder.AddComponent<ChatManager>();
        DontDestroyOnLoad(holder);
    }

    // ──────────────────────────────
    // 事件（主线程触发，UI 可安全订阅）
    // ──────────────────────────────

    /// <summary>某人的会话有更新（参数=对方ID）；UI 据此判断要不要重渲染当前打开的会话</summary>
    public event Action<int> ConversationUpdated;

    /// <summary>乐观消息发送失败（参数=对方ID），UI 提示"发送失败"用</summary>
    public event Action<int> SendFailed;

    // ──────────────────────────────
    // 红点状态
    // ──────────────────────────────

    /// <summary>
    /// UI 当前正在查看的会话（对方ID；0=没在看任何会话）
    /// UI 在 SelectChat/ResetChat/关面板时维护它；推送到达时据此决定"亮红点还是只渲染"
    /// </summary>
    public int CurrentConversation { get; set; }

    // ──────────────────────────────
    // 私有字段
    // ──────────────────────────────

    /// <summary>核心缓存：每人一个槽。key=对方玩家ID，value=该会话消息（旧→新排序）</summary>
    private readonly Dictionary<int, List<ChatMessageItem>> conversations = new();

    /// <summary>乐观项的临时消息ID前缀，Echo 到达后按它定位并替换</summary>
    private const string PendingPrefix = "pending_";

    /// <summary>消息内容类型：文本（默认）</summary>
    public const int ContentTypeText = 0;

    /// <summary>消息内容类型：表情包（此时 content 是表情键，正文为空语义）</summary>
    public const int ContentTypeSticker = 1;

    /// <summary>乐观项计数器（生成唯一临时ID）</summary>
    private int pendingCounter;

    /// <summary>
    /// 在途发送队列（FIFO 配对）
    /// ChatSendResponse 不带目标ID，靠"发送顺序=回执顺序"（TCP 有序 + 服务端串行处理）配对回滚/校准
    /// </summary>
    private readonly Queue<(int FriendId, string PendingId)> inflight = new();

    // ──────────────────────────────
    // 会话加载状态 + 登录消息预热
    // ──────────────────────────────

    /// <summary>
    /// 会话加载状态
    /// 关键：预读会先建空槽，绝不能用"槽是否存在"判断有没有拉到过历史——
    /// 否则预读失败后用户点开会看到空列表且永远不会再拉取
    /// </summary>
    private enum ConversationLoadState { None, Loading, Loaded }

    /// <summary>每个好友会话的加载状态（None=从未请求 / Loading=在途 / Loaded=已成功拿到历史）</summary>
    private readonly Dictionary<int, ConversationLoadState> loadStates = new();

    /// <summary>预读请求在途的会话（peek=true，只读不写）</summary>
    private readonly HashSet<int> peekInFlight = new();

    /// <summary>普通历史请求在途的会话（peek=false，会触发服务端置已读）</summary>
    private readonly HashSet<int> normalInFlight = new();

    /// <summary>各会话请求发出时刻（unscaled），用于超时回退</summary>
    private readonly Dictionary<int, float> requestStartedAt = new();

    /// <summary>超时扫描的复用缓冲，避免每帧扫描时分配</summary>
    private readonly List<int> detachedBuffer = new();

    /// <summary>本次登录是否已经启动过预热（好友列表可能多次到达，只跑一轮）</summary>
    private bool prewarmStarted;

    /// <summary>预热协程句柄（换号/重登时停掉）</summary>
    private Coroutine prewarmRoutine;

    // ──────────────────────────────
    // 消息预热（登录后后台预读最近聊天）
    // ──────────────────────────────

    /// <summary>预热总开关（出问题可一键关闭，退回"点开才拉"）</summary>
    public bool prewarmEnabled = true;

    /// <summary>主页就绪后再等多久开始预热（秒，真实时间）：让主页自身的请求先走完</summary>
    public float prewarmInitialDelay = 0.3f;

    /// <summary>单次登录最多预热的会话数（只统计"确实有历史"的会话，未读/最近活跃优先）</summary>
    public int prewarmMaxConversations = 8;

    /// <summary>等待主页 UI 就绪的上限（秒）：超时后仍开始预热，避免从非 zhuye 入口登录时永不预热</summary>
    public float prewarmHomeReadyTimeout = 10f;

    /// <summary>相邻两个预读请求的间隔（秒，真实时间）：串行节流，避免登录瞬间请求风暴</summary>
    public float prewarmInterval = 0.08f;

    /// <summary>每个会话预读条数：与服务端默认开箱条数一致，避免"预热后比正常打开还少"</summary>
    public int prewarmLimit = 50;

    /// <summary>单会话请求超时（秒）：超时后回退为 None，用户点开可重试</summary>
    public float prewarmTimeout = 8f;

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
        ClientNetworkManager.Instance.ChatSendResponseReceived += OnChatSendResponse;
        ClientNetworkManager.Instance.ChatHistoryResponseReceived += OnChatHistoryResponse;
        ClientNetworkManager.Instance.ChatReceiveNotifyReceived += OnChatReceiveNotify;
        ClientNetworkManager.Instance.LoginResponseReceived += OnLoginResponse;
        ClientNetworkManager.Instance.ConnectionBroken += OnConnectionBroken;

        // 红点种子的兜底数据源：好友/申请列表的响应不一定经过某个 UI 面板（面板可能没打开），
        // 所以在这里（常驻数据层）统一订阅，保证入口角标在未打开好友面板时也能点亮。
        ClientNetworkManager.Instance.FriendListResponseReceived += OnFriendListResponseForBadges;
        ClientNetworkManager.Instance.FriendRequestsResponseReceived += OnFriendRequestsResponseForBadge;
    }

    /// <summary>
    /// 在途请求超时扫描
    /// 服务端正常情况下必有回执；超时说明连接异常或响应丢失，回退为 None 让用户点开时还能重新拉取，
    /// 绝不把会话永久钉在"加载中"（这是预读最危险的失败模式）
    /// </summary>
    private void Update()
    {
        if (requestStartedAt.Count == 0) return;

        float now = Time.unscaledTime;
        detachedBuffer.Clear();
        foreach (KeyValuePair<int, float> pair in requestStartedAt)
        {
            if (now - pair.Value >= prewarmTimeout) detachedBuffer.Add(pair.Key);
        }
        if (detachedBuffer.Count == 0) return;

        foreach (int friendId in detachedBuffer)
        {
            requestStartedAt.Remove(friendId);
            peekInFlight.Remove(friendId);
            normalInFlight.Remove(friendId);
            if (loadStates.TryGetValue(friendId, out ConversationLoadState state)
                && state == ConversationLoadState.Loading)
            {
                loadStates[friendId] = ConversationLoadState.None;
            }
            Debug.LogWarning($"[聊天]会话{friendId}历史请求超时，已回退为未加载（点开可重试）");
        }
    }

    private void OnDestroy()
    {
        if (ClientNetworkManager.Instance == null) return;
        ClientNetworkManager.Instance.ChatSendResponseReceived -= OnChatSendResponse;
        ClientNetworkManager.Instance.ChatHistoryResponseReceived -= OnChatHistoryResponse;
        ClientNetworkManager.Instance.ChatReceiveNotifyReceived -= OnChatReceiveNotify;
        ClientNetworkManager.Instance.LoginResponseReceived -= OnLoginResponse;
        ClientNetworkManager.Instance.ConnectionBroken -= OnConnectionBroken;
        ClientNetworkManager.Instance.FriendListResponseReceived -= OnFriendListResponseForBadges;
        ClientNetworkManager.Instance.FriendRequestsResponseReceived -= OnFriendRequestsResponseForBadge;
    }

    /// <summary>
    /// 好友列表响应 → 红点种子（好友未读动态叶子节点）。
    /// 与 FriendUIController 里的同名逻辑等价且幂等：界面存在时两边都会跑，不会叠加计数。
    /// </summary>
    private void OnFriendListResponseForBadges(FriendListResponse response)
    {
        if (response == null) return;

        RedDotSingleton redDot = RedDotSingleton.Instance;
        if (redDot != null) redDot.BeginBatch();
        try
        {
            foreach (FriendInfo friend in response.Friends)
            {
                EnsureFriendNode(friend.PlayerId);
                SeedUnread(friend.PlayerId, friend.UnreadCount);
            }
        }
        finally
        {
            if (redDot != null) redDot.EndBatch();
        }

        // 好友名单到手 → 启动登录消息预热（只跑一轮；好友有未读的优先）
        SchedulePrewarm(response.Friends);
    }

    /// <summary>待处理好友申请数 → 入口"新的朋友"红点（Key15 叶子，向上汇总到好友入口）</summary>
    private void OnFriendRequestsResponseForBadge(FriendRequestsResponse response)
    {
        if (response == null) return;
        if (RedDotSingleton.Instance != null)
            RedDotSingleton.Instance.Set(RedDotKeys.FriendRequests, response.Count, RedDotFlags.Number);
    }

    // ──────────────────────────────
    // 公开接口
    // ──────────────────────────────

    /// <summary>
    /// 打开会话的方法
    /// 有缓存直接返回（0 网络开销）；无缓存先建空槽再发拉取请求，历史到达后触发 ConversationUpdated
    /// 先建空槽的原因：推送可能比历史响应先到，不建槽会丢
    /// 红点：打开即清该好友未读（微信式）；有未读残留的缓存会话补拉一次历史让服务端置已读
    /// </summary>
    /// <param name="friendId">参数名：friendId，类型：int，用途：对方玩家ID</param>
    /// <returns>返回值类型：IReadOnlyList&lt;ChatMessageItem&gt;，用途：该会话当前缓存（可能为空=拉取中）</returns>
    public IReadOnlyList<ChatMessageItem> OpenConversation(int friendId)
    {
        if (!conversations.TryGetValue(friendId, out List<ChatMessageItem> history))
        {
            history = new List<ChatMessageItem>();
            conversations[friendId] = history;
        }

        int unreadBeforeOpen = GetUnread(friendId);
        ConversationLoadState state = GetLoadState(friendId);

        if (state == ConversationLoadState.None)
        {
            Debug.Log($"[聊天]会话{friendId}未加载，拉取历史");
            RequestHistory(friendId, peek: false);
        }
        else if (unreadBeforeOpen > 0)
        {
            // 有未读：即使已预热过也补发一次普通请求，让服务端置已读。
            // 此时内容已在缓存里并先渲染，用户看不到等待，只是补一次"已读"的权威写入。
            Debug.Log($"[聊天]会话{friendId}有未读，补拉历史置已读");
            RequestHistory(friendId, peek: false);
        }

        // 红点：打开会话即清未读（点击好友→角标消失）
        SetUnread(friendId, 0);
        return history;
    }

    /// <summary>取某会话的加载状态（未记录的视为从未请求）</summary>
    private ConversationLoadState GetLoadState(int friendId)
    {
        return loadStates.TryGetValue(friendId, out ConversationLoadState state)
            ? state
            : ConversationLoadState.None;
    }

    /// <summary>
    /// 发起一次历史请求（统一入口）
    /// peek=true 用于登录预热：只读不写，服务端不置已读；peek=false 用于用户真正打开会话。
    /// 同类请求去重，避免"预热在途时点开"重复拉同一会话。
    /// </summary>
    /// <param name="friendId">参数名：friendId，类型：int，用途：对方玩家ID</param>
    /// <param name="peek">参数名：peek，类型：bool，用途：true=只读不写（预读），false=普通拉取（会置已读）</param>
    private void RequestHistory(int friendId, bool peek)
    {
        if (ClientNetworkManager.Instance == null || !ClientNetworkManager.Instance.IsConnected) return;

        if (peek)
        {
            if (peekInFlight.Contains(friendId) || normalInFlight.Contains(friendId)) return;
            if (GetLoadState(friendId) != ConversationLoadState.None) return;
            peekInFlight.Add(friendId);
        }
        else
        {
            if (normalInFlight.Contains(friendId)) return;
            normalInFlight.Add(friendId);
            // 普通请求会接管这次会话的权威结果，预读不再单独计数
            peekInFlight.Remove(friendId);
        }

        loadStates[friendId] = ConversationLoadState.Loading;
        requestStartedAt[friendId] = Time.unscaledTime;
        _ = ClientNetworkManager.Instance.SendChatHistory(friendId, 0, peek ? prewarmLimit : 0, peek);
    }

    /// <summary>
    /// 安排登录预热：好友列表到达后延迟启动，只跑一轮
    /// 候选顺序：有未读的优先（未读多的在前）→ 其余按好友列表倒序（服务端按创建时间升序，倒序≈最近加的好友）
    /// </summary>
    private void SchedulePrewarm(IReadOnlyList<FriendInfo> friends)
    {
        if (!prewarmEnabled || prewarmStarted) return;
        if (friends == null || friends.Count == 0) return;
        if (ClientNetworkManager.Instance == null || !ClientNetworkManager.Instance.IsConnected) return;

        List<(int FriendId, int Unread)> candidates = BuildPrewarmCandidates(friends);
        if (candidates.Count == 0) return;

        prewarmStarted = true;
        prewarmRoutine = StartCoroutine(PrewarmRoutine(candidates));
    }

    /// <summary>
    /// 按「有未读优先 → 最后消息时间最新优先」生成预热候选
    /// 只收 LastMessageAtMs &gt; 0 的会话：从未聊过的会话预热是纯浪费一次 Mongo 查询；
    /// 已加载/加载中的会话跳过。
    /// </summary>
    private List<(int FriendId, int Unread)> BuildPrewarmCandidates(IReadOnlyList<FriendInfo> friends)
    {
        int myId = ClientNetworkManager.Instance != null ? ClientNetworkManager.Instance.MyPlayerId : 0;

        List<FriendInfo> all = new(friends.Count);
        foreach (FriendInfo friend in friends)
        {
            if (friend == null || friend.PlayerId <= 0 || friend.PlayerId == myId) continue;
            all.Add(friend);
        }

        // 1) 有未读的优先（未读多的在前；有未读必然有历史消息）
        List<FriendInfo> byUnread = new(all);
        byUnread.Sort((a, b) => b.UnreadCount.CompareTo(a.UnreadCount));

        List<FriendInfo> ordered = new(all.Count);
        foreach (FriendInfo friend in byUnread)
        {
            if (friend.UnreadCount > 0) ordered.Add(friend);
        }

        // 2) 其余按最后消息时间倒序（最近聊过的优先），没有任何消息的会话不预热
        List<FriendInfo> byLastMessage = new(all);
        byLastMessage.Sort((a, b) => b.LastMessageAtMs.CompareTo(a.LastMessageAtMs));
        foreach (FriendInfo friend in byLastMessage)
        {
            if (friend.UnreadCount > 0) continue;
            if (friend.LastMessageAtMs <= 0) continue;
            ordered.Add(friend);
        }

        int cap = Mathf.Max(0, prewarmMaxConversations);
        List<(int FriendId, int Unread)> result = new(Mathf.Min(cap, ordered.Count));
        foreach (FriendInfo friend in ordered)
        {
            if (result.Count >= cap) break;
            if (GetLoadState(friend.PlayerId) != ConversationLoadState.None) continue;
            result.Add((friend.PlayerId, friend.UnreadCount));
        }
        return result;
    }

    /// <summary>
    /// 预热协程：等主页 UI 就绪 → 再等一小段 → 逐个串行发预读请求（每两个之间等 prewarmInterval）
    /// 用真实时间等待，避免受游戏 timeScale 影响；主页迟迟不出现时按超时兜底放行。
    /// </summary>
    private IEnumerator PrewarmRoutine(List<(int FriendId, int Unread)> candidates)
    {
        float readyStart = Time.realtimeSinceStartup;
        while (!HomeViewLauncher.IsReady
               && Time.realtimeSinceStartup - readyStart < prewarmHomeReadyTimeout)
        {
            yield return null;
        }

        yield return new WaitForSecondsRealtime(Mathf.Max(0f, prewarmInitialDelay));

        int sent = 0;
        foreach ((int friendId, int unread) in candidates)
        {
            if (ClientNetworkManager.Instance == null || !ClientNetworkManager.Instance.IsConnected) break;
            if (GetLoadState(friendId) != ConversationLoadState.None) continue;

            RequestHistory(friendId, peek: true);
            sent++;
            if (prewarmInterval > 0f) yield return new WaitForSecondsRealtime(prewarmInterval);
        }

        prewarmRoutine = null;
        Debug.Log($"[聊天]消息预热完成：发起{sent}个会话（主页就绪={HomeViewLauncher.IsReady}，上限{prewarmMaxConversations}，每会话{prewarmLimit}条）");
    }

    // ──────────────────────────────
    // 只读状态（E2E / 诊断用）
    // ──────────────────────────────

    /// <summary>某会话是否已成功加载过历史（预热命中判断）</summary>
    public bool IsConversationLoaded(int friendId)
    {
        return GetLoadState(friendId) == ConversationLoadState.Loaded;
    }

    /// <summary>已成功加载的会话数量</summary>
    public int LoadedConversationCount
    {
        get
        {
            int count = 0;
            foreach (ConversationLoadState state in loadStates.Values)
            {
                if (state == ConversationLoadState.Loaded) count++;
            }
            return count;
        }
    }

    /// <summary>当前缓存里的会话槽数量（含预读建的空槽）</summary>
    public int CachedConversationCount => conversations.Count;

    /// <summary>
    /// 用服务端下发的未读数种红点的方法（登录/好友列表到达时由 UI 调用）
    /// 跳过本会话已打开过的会话（其未读由客户端实时管理）与当前正查看的会话
    /// </summary>
    /// <param name="friendId">参数名：friendId，类型：int，用途：对方玩家ID</param>
    /// <param name="serverUnread">参数名：serverUnread，类型：int，用途：服务端聚合的未读条数</param>
    public void SeedUnread(int friendId, int serverUnread)
    {
        if (serverUnread <= 0) return;
        if (conversations.ContainsKey(friendId)) return;   // 打开过的会话以客户端实时状态为准
        if (friendId == CurrentConversation) return;
        SetUnread(friendId, serverUnread);
    }

    // ──────────────────────────────
    // 红点读写（好友未读 = 红点树的动态叶子节点，红点系统是唯一状态源）
    // ──────────────────────────────

    /// <summary>确保某好友的动态红点节点存在（幂等）；好友列表到达/推送到达时调用</summary>
    public void EnsureFriendNode(int friendId)
    {
        if (RedDotSingleton.Instance == null) return;
        RedDotSingleton.Instance.CreateChildNode(RedDotKeys.FriendKey(friendId), RedDotKeys.FriendEntry);
        boundFriendIds.Add(friendId);
    }

    /// <summary>查询某好友当前未读数（红点节点即状态源）</summary>
    private int GetUnread(int friendId)
    {
        return RedDotSingleton.Instance != null
            ? RedDotSingleton.Instance.GetRedDotInfoCount(RedDotKeys.FriendKey(friendId), RedDotFlags.Number)
            : 0;
    }

    /// <summary>设置某好友未读数（0=熄灭角标），经红点系统自动向上汇总到好友入口</summary>
    private void SetUnread(int friendId, int count)
    {
        if (RedDotSingleton.Instance == null) return;
        EnsureFriendNode(friendId);
        RedDotSingleton.Instance.Set(RedDotKeys.FriendKey(friendId), Math.Max(0, count), RedDotFlags.Number);
    }

    /// <summary>已建过红点节点的好友集合（换账号登录时统一摘除动态节点防串号）</summary>
    private readonly HashSet<int> boundFriendIds = new();

    /// <summary>
    /// 乐观发送的方法（文本消息）
    /// 立刻把 pending 项放入缓存并触发更新（UI 即刻显示在右侧），再发协议；
    /// Echo 到达后替换 pending 校准服务端消息ID与时间戳；失败则移除 pending 回滚
    /// </summary>
    /// <param name="friendId">参数名：friendId，类型：int，用途：对方玩家ID</param>
    /// <param name="content">参数名：content，类型：string，用途：消息正文（调用方已做长度校验）</param>
    public void Send(int friendId, string content)
    {
        Send(friendId, content, ContentTypeText);
    }

    /// <summary>
    /// 乐观发送的方法（表情包）
    /// 与文本走同一条链路，只是内容类型标成表情、content 存的是表情键；
    /// 服务端只透传类型不做解释，接收方按键取图——因此加表情不需要动协议结构
    /// </summary>
    /// <param name="friendId">参数名：friendId，类型：int，用途：对方玩家ID</param>
    /// <param name="stickerKey">参数名：stickerKey，类型：string，用途：表情键（StickerCatalog 里的键）</param>
    public void SendSticker(int friendId, string stickerKey)
    {
        Send(friendId, stickerKey, ContentTypeSticker);
    }

    /// <summary>
    /// 乐观发送的统一实现（文本/表情共用）
    /// </summary>
    /// <param name="friendId">参数名：friendId，类型：int，用途：对方玩家ID</param>
    /// <param name="content">参数名：content，类型：string，用途：正文或表情键</param>
    /// <param name="contentType">参数名：contentType，类型：int，用途：消息内容类型（0文本/1表情）</param>
    private void Send(int friendId, string content, int contentType)
    {
        if (ClientNetworkManager.Instance == null) return;

        ChatMessageItem pending = new ChatMessageItem
        {
            MessageId = $"{PendingPrefix}{++pendingCounter}",
            SenderId = ClientNetworkManager.Instance.MyPlayerId,
            ReceiverId = friendId,
            Content = content,
            ContentType = contentType,
            // 本地临时时间戳，仅用于排序占位；Echo 到达后以服务端时间为准
            SentAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        if (!conversations.TryGetValue(friendId, out List<ChatMessageItem> history))
        {
            history = new List<ChatMessageItem>();
            conversations[friendId] = history;
        }
        history.Add(pending);
        inflight.Enqueue((friendId, pending.MessageId));
        ConversationUpdated?.Invoke(friendId);

        _ = ClientNetworkManager.Instance.SendChatSend(friendId, content, contentType);
    }

    // ──────────────────────────────
    // 网络事件处理
    // ──────────────────────────────

    /// <summary>
    /// 发送回执处理
    /// 成功→按在途队列配对，用 Echo 替换该 pending（校准消息ID/时间戳，内容不变无闪烁）
    /// 失败→移除该 pending（回滚）并触发 SendFailed
    /// </summary>
    private void OnChatSendResponse(ChatSendResponse response)
    {
        if (inflight.Count == 0) return;
        (int friendId, string pendingId) = inflight.Dequeue();
        if (!conversations.TryGetValue(friendId, out List<ChatMessageItem> history)) return;

        int index = history.FindIndex(message => message.MessageId == pendingId);
        if (index < 0) return;

        if (response.Success && response.Echo != null)
        {
            history[index] = response.Echo;
            ConversationUpdated?.Invoke(friendId);
        }
        else
        {
            history.RemoveAt(index);
            Debug.Log($"[聊天]发送失败:{response.Message}");
            SendFailed?.Invoke(friendId);
        }
    }

    /// <summary>
    /// 历史响应处理
    /// 服务端列表为权威，覆盖本地；但本地 pending（乐观在途项）服务器里没有，先取出再补回尾部防丢
    /// 推送先到的消息服务器必然已落库并包含在返回列表里，无需特殊处理
    /// </summary>
    private void OnChatHistoryResponse(ChatHistoryResponse response)
    {
        int friendId = response.TargetId;

        // 请求闭环：无论普通拉取还是预热，到这里都算"已加载过历史"
        peekInFlight.Remove(friendId);
        normalInFlight.Remove(friendId);
        requestStartedAt.Remove(friendId);
        loadStates[friendId] = ConversationLoadState.Loaded;

        if (!conversations.TryGetValue(friendId, out List<ChatMessageItem> local))
        {
            local = new List<ChatMessageItem>();
            conversations[friendId] = local;
        }

        List<ChatMessageItem> pendingOnly = new();
        foreach (ChatMessageItem message in local)
        {
            if (message.MessageId.StartsWith(PendingPrefix, StringComparison.Ordinal))
            {
                pendingOnly.Add(message);
            }
        }

        local.Clear();
        local.AddRange(response.Messages);
        local.AddRange(pendingOnly);

        Debug.Log($"[聊天]会话{friendId}历史到达:{response.Count}条");
        ConversationUpdated?.Invoke(friendId);
    }

    /// <summary>
    /// 推送处理
    /// 按发送者归位到对应会话——不管 UI 当前在看谁（这正是"每人一个槽"的意义）
    /// </summary>
    private void OnChatReceiveNotify(ChatReceiveNotify notify)
    {
        if (notify.Message == null) return;
        int friendId = notify.Message.SenderId;

        if (!conversations.TryGetValue(friendId, out List<ChatMessageItem> history))
        {
            history = new List<ChatMessageItem>();
            conversations[friendId] = history;
        }
        history.Add(notify.Message);
        // 隐私：默认只打 来源ID+类型+长度 摘要，消息全文仅在 GameLog 明细开关打开时输出
        GameLog.Info($"[聊天]收到{friendId}的消息(类型{notify.Message.ContentType},长度{notify.Message.Content?.Length ?? 0})");
        if (GameLog.VerboseEnabled) GameLog.Verbose($"[聊天]消息全文:{notify.Message.Content}");

        // 红点：不是正在查看的会话才累计未读（微信式——正聊着就不亮角标）
        if (friendId != CurrentConversation)
        {
            SetUnread(friendId, GetUnread(friendId) + 1);
        }
        ConversationUpdated?.Invoke(friendId);
    }

    /// <summary>
    /// 连接断开处理（可能触发多次，幂等）
    /// 连接死亡意味着所有在途回执永远不会再到达：把 inflight 队列里的乐观项全部回滚，
    /// 否则 pending 气泡永久卡在界面上，且后续正常回执会与错误的 pending FIFO 错位配对
    /// </summary>
    private void OnConnectionBroken()
    {
        // 连接死亡：所有在途历史请求都不会再有回执 → 回退为未加载（用户点开可重试），
        // 否则会话会永久停在 Loading，点开只看到空白
        List<int> loading = new();
        foreach (KeyValuePair<int, ConversationLoadState> pair in loadStates)
        {
            if (pair.Value == ConversationLoadState.Loading) loading.Add(pair.Key);
        }
        foreach (int friendId in loading) loadStates[friendId] = ConversationLoadState.None;
        peekInFlight.Clear();
        normalInFlight.Clear();
        requestStartedAt.Clear();

        if (prewarmRoutine != null)
        {
            StopCoroutine(prewarmRoutine);
            prewarmRoutine = null;
        }

        if (inflight.Count == 0) return;

        HashSet<int> affected = new();
        while (inflight.Count > 0)
        {
            (int friendId, string pendingId) = inflight.Dequeue();
            affected.Add(friendId);
            if (!conversations.TryGetValue(friendId, out List<ChatMessageItem> history)) continue;

            int index = history.FindIndex(message => message.MessageId == pendingId);
            if (index < 0) continue;
            history.RemoveAt(index);
            ConversationUpdated?.Invoke(friendId);
        }

        foreach (int friendId in affected)
        {
            SendFailed?.Invoke(friendId);
        }
        Debug.Log($"[聊天]连接断开，回滚 {affected.Count} 个会话的在途消息");
    }

    /// <summary>
    /// 登录响应处理
    /// 登录成功即清空全部会话缓存与在途队列：换账号重登/断线重连都拿到干净状态，防止串号
    /// 代价是重登后首次打开会话要重新拉一次历史（服务端数据持久，可接受）
    /// </summary>
    private void OnLoginResponse(LoginResponse response)
    {
        if (!response.Success) return;
        conversations.Clear();
        inflight.Clear();
        CurrentConversation = 0;

        // 加载状态与预热排队一并复位：换账号/重登绝不能复用上个账号的"已加载"判断
        loadStates.Clear();
        peekInFlight.Clear();
        normalInFlight.Clear();
        requestStartedAt.Clear();
        prewarmStarted = false;
        if (prewarmRoutine != null)
        {
            StopCoroutine(prewarmRoutine);
            prewarmRoutine = null;
        }

        // 红点：摘掉上一个账号的动态好友节点，防止旧未读串到新账号
        if (RedDotSingleton.Instance != null)
        {
            foreach (int friendId in boundFriendIds)
            {
                RedDotSingleton.Instance.RemoveChildNode(RedDotKeys.FriendKey(friendId), RedDotKeys.FriendEntry);
            }
        }
        boundFriendIds.Clear();

        // 登录即拉好友列表与待处理申请：红点种子随响应到达（不依赖好友面板是否打开）。
        // 好友面板迁移到框架后不再常驻场景，这两条请求必须由数据层在这里发起。
        if (ClientNetworkManager.Instance != null && ClientNetworkManager.Instance.IsConnected)
        {
            _ = ClientNetworkManager.Instance.SendFriendListRequest();
            _ = ClientNetworkManager.Instance.SendFriendRequestsRequest();
        }
    }
}
