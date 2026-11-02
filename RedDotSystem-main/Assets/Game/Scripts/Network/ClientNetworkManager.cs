using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Common.Enum;
using Common.Network;

using Google.Protobuf;

using UnityEngine;

/// <summary>
/// 客户端网络管理器
/// 负责 TCP 连接、protobuf 消息收发与主线程分发
/// 帧结构：[4字节小端长度头][4字节消息ID][protobuf消息体]
/// </summary>
public class ClientNetworkManager : MonoBehaviour
{
    // ──────────────────────────────
    // 服务器连接参数（登录/注册等界面共用，避免常量散落多处漂移）
    // ──────────────────────────────

    /// <summary>联调服务器地址（与服务端 SocketServer.Start 的监听地址一致）</summary>
    public const string ServerHost = "127.0.0.1";

    /// <summary>联调服务器端口</summary>
    public const int ServerPort = 8888;

    /// <summary>
    /// 登录场景名（Build Settings 第 0 项 Assets/Game/Scenes/LOGIN.scene，场景名即文件名去后缀）
    /// 用途：被顶号后回登录界面等"跨场景跳转"统一引用，避免字面量散落多处漂移
    /// </summary>
    public const string LoginSceneName = "LOGIN";

    // ──────────────────────────────
    // 心跳与自动重连参数
    // ──────────────────────────────

    /// <summary>心跳发送间隔（秒），需显著小于 ReceiveTimeoutSeconds</summary>
    private const int HeartbeatIntervalSeconds = 15;

    /// <summary>读超时（秒）：超过该时长收不到任何完整帧即判定断线（可容忍两次心跳丢失+回执延迟）</summary>
    private const int ReceiveTimeoutSeconds = 45;

    /// <summary>自动重连基础退避（毫秒），指数递增封顶</summary>
    private const int ReconnectBaseDelayMs = 1000;

    /// <summary>自动重连最大退避（毫秒）</summary>
    private const int ReconnectMaxDelayMs = 15000;

    /// <summary>自动重连最大尝试次数（失败后放弃，等待用户手动重连）</summary>
    private const int ReconnectMaxAttempts = 10;

    // ──────────────────────────────
    // 单例实现
    // ──────────────────────────────

    /// <summary>
    /// 全局唯一实例
    /// 类型：ClientNetworkManager
    /// 用途：供 UI 层访问网络功能的入口
    /// </summary>
    public static ClientNetworkManager Instance { get; private set; }

    /// <summary>
    /// 运行时自举
    /// 用途：进入 Play 模式后自动创建网络管理器，无需在场景中手动挂载
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance != null) return;
        GameObject holder = new GameObject("ClientNetworkManager");
        holder.AddComponent<ClientNetworkManager>();
        DontDestroyOnLoad(holder);
    }

    // ──────────────────────────────
    // 事件
    // ──────────────────────────────

    /// <summary>连接状态变化（主线程触发，UI 可安全订阅）</summary>
    public event Action<string> StatusChanged;

    /// <summary>登录响应到达（主线程触发）</summary>
    public event Action<LoginResponse> LoginResponseReceived;

    /// <summary>
    /// 被顶号推送到达（主线程触发，服务端主动推送、非请求响应）
    /// 用途：账号在其它设备登录时，由 KickoutWatcher 回登录界面并提示；连接已由本类自行断开
    /// </summary>
    public event Action<KickoutNotify> KickoutNotifyReceived;

    /// <summary>注册响应到达（主线程触发）</summary>
    public event Action<RegisterResponse> RegisterResponseReceived;

    /// <summary>好友列表响应到达（主线程触发）</summary>
    public event Action<FriendListResponse> FriendListResponseReceived;

    /// <summary>查询玩家响应到达（主线程触发）</summary>
    public event Action<QueryPlayerResponse> QueryPlayerResponseReceived;

    /// <summary>好友申请响应到达（主线程触发）</summary>
    public event Action<FriendAddResponse> FriendAddResponseReceived;

    /// <summary>好友申请回应响应到达（主线程触发）</summary>
    public event Action<FriendRespondResponse> FriendRespondResponseReceived;

    /// <summary>待处理申请列表响应到达（主线程触发）</summary>
    public event Action<FriendRequestsResponse> FriendRequestsResponseReceived;

    /// <summary>发送聊天消息响应到达（主线程触发）</summary>
    public event Action<ChatSendResponse> ChatSendResponseReceived;

    /// <summary>聊天历史响应到达（主线程触发）</summary>
    public event Action<ChatHistoryResponse> ChatHistoryResponseReceived;

    /// <summary>聊天消息推送到达（主线程触发，服务端主动推给接收方，非请求响应）</summary>
    public event Action<ChatReceiveNotify> ChatReceiveNotifyReceived;

    /// <summary>世界频道发送响应到达（主线程触发）</summary>
    public event Action<WorldChatSendResponse> WorldChatSendResponseReceived;

    /// <summary>世界频道历史响应到达（主线程触发）</summary>
    public event Action<WorldChatHistoryResponse> WorldChatHistoryResponseReceived;

    /// <summary>世界频道消息广播到达（主线程触发，服务端主动广播给全部在线会话，非请求响应）</summary>
    public event Action<WorldChatNotify> WorldChatNotifyReceived;

    /// <summary>Agent（阿福）聊天响应到达（主线程触发：最终回复+耗时，思考过程不出现）</summary>
    public event Action<AgentChatResponse> AgentChatResponseReceived;

    /// <summary>Agent（阿福）流式分片到达（主线程触发：3102 终值之前的增量文本打字机）</summary>
    public event Action<AgentChatChunk> AgentChatChunkReceived;

    /// <summary>Agent（阿福）登录简报推送到达（主线程触发：服务端登录成功后主动推的待办汇总）</summary>
    public event Action<AgentBriefNotify> AgentBriefNotifyReceived;

    /// <summary>Agent（阿福）历史回放响应到达（主线程触发：最近对话，旧→新）</summary>
    public event Action<AgentHistoryResponse> AgentHistoryResponseReceived;

    /// <summary>Agent（阿福）主动提醒推送到达（主线程触发：悬浮球 3107——服务端事件触发的主动消息，面板关着走悬浮球气泡）</summary>
    public event Action<AgentProactiveNotify> AgentProactiveNotifyReceived;

    /// <summary>成绩上报响应（排行榜：入账结果+最高分+实时排名）</summary>
    public event Action<ScoreSubmitResponse> ScoreSubmitResponseReceived;

    /// <summary>排行榜查询响应（TopN 条目+我的排名）</summary>
    public event Action<LeaderboardResponse> LeaderboardResponseReceived;

    /// <summary>背包列表响应（槽位快照 + 物品静态配置表）</summary>
    public event Action<BagListResponse> BagListResponseReceived;

    /// <summary>背包使用响应（操作后的槽位快照）</summary>
    public event Action<BagUseResponse> BagUseResponseReceived;

    /// <summary>背包丢弃响应（操作后的槽位快照）</summary>
    public event Action<BagDiscardResponse> BagDiscardResponseReceived;

    /// <summary>背包整理响应（整理后的槽位快照）</summary>
    public event Action<BagTidyResponse> BagTidyResponseReceived;

    /// <summary>背包换位响应（换位后的槽位快照）</summary>
    public event Action<BagSwapResponse> BagSwapResponseReceived;

    /// <summary>物品发放响应（测试发放后的槽位快照）</summary>
    public event Action<BagGrantResponse> BagGrantResponseReceived;

    /// <summary>成就列表响应（配置 + 玩家状态 + 汇总）</summary>
    public event Action<AchievementListResponse> AchievementListResponseReceived;

    /// <summary>成就领取响应（发放明细 + 操作后的快照）</summary>
    public event Action<AchievementClaimResponse> AchievementClaimResponseReceived;

    /// <summary>成就解锁推送（服务端主动推给在线玩家，非请求响应）</summary>
    public event Action<AchievementUnlockedNotify> AchievementUnlockedNotifyReceived;

    /// <summary>成就进度推送（预留；服务端当前只在解锁时推 3405）</summary>
    public event Action<AchievementProgressNotify> AchievementProgressNotifyReceived;

    /// <summary>成就 GM 响应（联调用）</summary>
    public event Action<AchievementGmResponse> AchievementGmResponseReceived;

    /// <summary>装备列表响应（配置 + 本人实例 + 槽位）</summary>
    public event Action<EquipmentListResponse> EquipmentListResponseReceived;

    /// <summary>穿戴/卸下响应（结果码 + 操作后的槽位与实例）</summary>
    public event Action<EquipResponse> EquipResponseReceived;

    /// <summary>新手引导进度拉取响应（是否有记录 + 下一步下标 + 完成标记）</summary>
    public event Action<GuidePullResponse> GuidePullResponseReceived;

    /// <summary>新手引导进度上报响应（服务端落库后的步骤下标与完成标记）</summary>
    public event Action<GuidePushResponse> GuidePushResponseReceived;

    /// <summary>邮件列表响应（邮箱容量 + 全部有效邮件快照）</summary>
    public event Action<MailListResponse> MailListResponseReceived;

    /// <summary>邮件置读响应（操作后的邮件快照）</summary>
    public event Action<MailReadResponse> MailReadResponseReceived;

    /// <summary>领取附件响应（操作后的邮件快照）</summary>
    public event Action<MailClaimResponse> MailClaimResponseReceived;

    /// <summary>删除邮件响应（操作后的邮件快照）</summary>
    public event Action<MailDeleteResponse> MailDeleteResponseReceived;

    /// <summary>GM 发件响应（发件结果）</summary>
    public event Action<MailGrantResponse> MailGrantResponseReceived;

    /// <summary>收到新邮件推送（服务端主动推给在线收件人，非请求响应）</summary>
    public event Action<MailReceiveNotify> MailReceiveNotifyReceived;

    /// <summary>叙事任务拉取响应（Mm框架·登录后拉取玩家任务状态快照）</summary>
    public event Action<NarrativeQuestSyncResponse> NarrativeQuestSyncResponseReceived;

    /// <summary>叙事任务接取响应（服务端权威·回执带全量状态快照）</summary>
    public event Action<NarrativeQuestAcceptResponse> NarrativeQuestAcceptResponseReceived;

    /// <summary>叙事任务提交响应（服务端权威·回执带全量状态快照）</summary>
    public event Action<NarrativeQuestSubmitResponse> NarrativeQuestSubmitResponseReceived;

    /// <summary>叙事任务状态推送（服务端权威·事件推进/限时到期后服务端主动推全量快照）</summary>
    public event Action<NarrativeQuestStateNotify> NarrativeQuestStateNotifyReceived;

    /// <summary>叙事任务放弃响应（服务端权威·回执带全量状态快照）</summary>
    public event Action<NarrativeQuestAbandonResponse> NarrativeQuestAbandonResponseReceived;

    /// <summary>叙事任务提示推送（提交成功后服务端推的提示文案，交由提示条展示）</summary>
    public event Action<NarrativeQuestToastNotify> NarrativeQuestToastNotifyReceived;

    /// <summary>活动列表响应（当前时间窗内的活动 + 服务器时钟，倒计时用它算防本地偏差）</summary>
    public event Action<ActivityListResponse> ActivityListResponseReceived;

    /// <summary>唤取响应（结果列表 + 保底计数 + 操作后的背包快照与涉及物品配置）</summary>
    public event Action<GachaDrawResponse> GachaDrawResponseReceived;

    /// <summary>唤取流水响应（鸣潮"唤取详单"，倒序）</summary>
    public event Action<GachaHistoryResponse> GachaHistoryResponseReceived;

    /// <summary>签到状态响应（7 天奖励表与玩家签到进度快照）</summary>
    public event Action<SignInListResponse> SignInListResponseReceived;

    /// <summary>签到响应（签到结果 + 完整天次快照 + 操作后的背包快照与涉及物品配置）</summary>
    public event Action<SignInClaimResponse> SignInClaimResponseReceived;

    /// <summary>活动切换推送（GM 轮换生效后服务端全服广播当前活动状态）</summary>
    public event Action<ActivityChangeNotify> ActivityChangeNotifyReceived;

    /// <summary>GM 轮换活动响应（联调/演示指令）</summary>
    public event Action<ActivityGmRotateResponse> ActivityGmRotateResponseReceived;

    /// <summary>对战邀请创建响应（房主侧：邀请是否送达 + 凭据/过期时间）</summary>
    public event Action<BattleInviteResponse> BattleInviteResponseReceived;

    /// <summary>对战邀请推送（好友侧：服务端主动推送，弹邀请卡片）</summary>
    public event Action<BattleInviteNotify> BattleInviteNotifyReceived;

    /// <summary>回应对战邀请响应（好友侧：同意时带房主地址，据此直连 7777）</summary>
    public event Action<BattleInviteRespondResponse> BattleInviteRespondResponseReceived;

    /// <summary>邀请结果推送（房主侧：同意/拒绝/过期/取消）</summary>
    public event Action<BattleInviteResultNotify> BattleInviteResultNotifyReceived;

    /// <summary>取消邀请响应（房主侧：实际取消条数）</summary>
    public event Action<BattleInviteCancelResponse> BattleInviteCancelResponseReceived;

    /// <summary>邀请失效推送（好友侧：房主取消/超时/下线，客户端立刻收起弹窗）</summary>
    public event Action<BattleInviteCancelledNotify> BattleInviteCancelledNotifyReceived;

    /// <summary>场景存档拉取响应（进入场景时恢复位置/相机/对话节点；has_save=false 走场景默认）</summary>
    public event Action<SaveStatePullResponse> SaveStatePullResponseReceived;

    /// <summary>场景存档上报响应（仅确认与排障用，玩法不依赖）</summary>
    public event Action<SaveStatePushResponse> SaveStatePushResponseReceived;

    /// <summary>
    /// 连接已不可用（主线程触发，可能触发多次，订阅方需幂等）
    /// 触发时机：发送失败 / 接收循环结束（对端关闭、网络异常、主动断开）。
    /// 用途：让依赖连接的在途状态得到清理（如 ChatManager 回滚未收到回执的乐观消息）
    /// </summary>
    public event Action ConnectionBroken;

    // ──────────────────────────────
    // 私有字段
    // ──────────────────────────────

    /// <summary>TCP 客户端对象，用途：底层连接</summary>
    private TcpClient client;

    /// <summary>网络数据流，用途：读写网络数据</summary>
    private NetworkStream stream;

    /// <summary>发送锁，用途：串行化写入操作防止多线程交错</summary>
    private readonly SemaphoreSlim sendLock = new(1, 1);

    /// <summary>收包队列，用途：后台线程收包、主线程取包处理</summary>
    private readonly ConcurrentQueue<byte[]> packetQueue = new();

    /// <summary>状态消息队列，用途：把后台线程的状态文本转移到主线程</summary>
    private readonly ConcurrentQueue<string> statusQueue = new();

    /// <summary>主线程动作队列，用途：把后台线程检测到的连接事件转移到主线程派发</summary>
    private readonly ConcurrentQueue<Action> mainThreadActions = new();

    /// <summary>接收循环运行标记</summary>
    private bool receiving;

    /// <summary>连接中标记，用途：防止连点/并发重入导致共享字段被交错置空</summary>
    private bool connecting;

    /// <summary>自动重连开关：连接成功后置 true，主动 Disconnect/退出应用时置 false</summary>
    private volatile bool autoReconnect;

    /// <summary>
    /// 被顶号后抑制自动登录（本进程内有效，用户主动点"登录"时复位）
    /// 为什么必须有：LoginSceneController.TryAutoLogin 读的是 PlayerPrefs（Login_AutoLogin/Login_Token），
    /// 不是内存凭据。只清 cached* 的话，回 LOGIN 场景会立刻用本地令牌自动登录、把对面踢了，
    /// 对面同理 → 两端无限互踢 + 无限重载场景。
    /// 刻意不清 PlayerPrefs 里的令牌：它是服务端签发的有效凭据，被顶不该作废玩家的"自动登录"勾选
    /// </summary>
    public static bool AutoLoginSuppressedByKick;

    /// <summary>重连循环运行标记，防止并发启动多个重连循环</summary>
    private bool reconnecting;

    /// <summary>上次成功连接使用的地址（自动重连用）</summary>
    private string lastHost;

    /// <summary>上次成功连接使用的端口（自动重连用）</summary>
    private int lastPort = ServerPort;

    /// <summary>登录凭据缓存（仅内存驻留，重连成功后自动重新登录）</summary>
    private string cachedAccount;

    /// <summary>登录凭据缓存（仅内存驻留，重连成功后自动重新登录）</summary>
    private string cachedPassword;

    /// <summary>自动登录令牌缓存（仅内存驻留；令牌登录的会话重连时用它免密码恢复，密码缓存存在时优先密码）</summary>
    private string cachedToken;

    /// <summary>下次发送心跳的时间（主线程 Update 使用）</summary>
    private DateTime nextHeartbeatAt;

    // ──────────────────────────────
    // 属性
    // ──────────────────────────────

    /// <summary>是否已连接服务器</summary>
    public bool IsConnected => client != null && client.Connected;

    /// <summary>
    /// 自己的玩家ID
    /// 用途：登录成功后由服务端下发并缓存；聊天等模块用它判断消息是"我发的"还是"对方发的"
    /// 说明：未登录或已断线时为0
    /// </summary>
    public int MyPlayerId { get; private set; }

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

    private void Update()
    {
        // 心跳：到点发 PingRequest，服务端回 Pong；链路死亡由读超时/发送失败兜底
        TickHeartbeat();

        // 每帧最多处理 64 个包，防止极端情况下卡帧
        int processed = 0;
        while (processed < 64 && packetQueue.TryDequeue(out byte[] data))
        {
            try
            {
                HandlePacket(data);
            }
            catch (Exception exception)
            {
                // 坏包意味着字节流已失步（protobuf 解析失败/未知结构），继续处理后续包只会错上加错：
                // 记录后断开连接，由订阅方清理在途状态
                Debug.LogError($"[Compete网络]坏包中断处理，断开连接: {exception.Message}");
                Disconnect();
                break;
            }
            processed++;
        }

        // 主线程派发状态消息
        while (statusQueue.TryDequeue(out string status))
        {
            StatusChanged?.Invoke(status);
        }

        // 主线程派发连接事件（先处理完本帧收到的包，再宣告连接死亡，保证回执先于回滚）
        while (mainThreadActions.TryDequeue(out Action action))
        {
            action?.Invoke();
        }
    }

    private void OnDestroy()
    {
        Disconnect();
    }

    private void OnApplicationQuit()
    {
        Disconnect();
    }

    // ──────────────────────────────
    // 连接管理
    // ──────────────────────────────

    /// <summary>
    /// 连接服务器的方法
    /// </summary>
    /// <param name="host">参数名：host，类型：string，用途：服务器地址</param>
    /// <param name="port">参数名：port，类型：int，用途：服务器端口</param>
    /// <returns>返回值类型：Task&lt;bool&gt;，用途：连接成功返回true</returns>
    public async Task<bool> ConnectAsync(string host, int port)
    {
        // 重入保护：上一次连接流程未结束时忽略新的连接请求（防连点/并发竞态）
        if (connecting) return false;
        connecting = true;
        try
        {
            CloseTransport();
            // 用局部引用操作新连接，await 恢复时不依赖共享字段（防止并发 Disconnect 置空引发空引用）
            TcpClient newClient = new TcpClient();
            client = newClient;
            await newClient.ConnectAsync(host, port);
            newClient.NoDelay = true;
            NetworkStream newStream = newClient.GetStream();
            stream = newStream;
            // 每连接独立的局部引用：旧接收循环不会被"复活"去读新流
            receiving = true;
            lastHost = host;
            lastPort = port;
            autoReconnect = true;
            nextHeartbeatAt = DateTime.Now.AddSeconds(HeartbeatIntervalSeconds);
            _ = ReceiveLoop(newClient, newStream);
            OnStatus($"已连接 {host}:{port}");
            return true;
        }
        catch (Exception exception)
        {
            OnStatus($"连接失败:{exception.Message}");
            return false;
        }
        finally
        {
            connecting = false;
        }
    }

    /// <summary>
    /// 断开连接并清理资源的方法（主动断开会同时关闭自动重连）
    /// </summary>
    public void Disconnect()
    {
        autoReconnect = false;
        CloseTransport();
    }

    /// <summary>
    /// 关闭当前传输层（不动 autoReconnect 标记）
    /// 用途：ConnectAsync 内部换连接时复用，避免误关自动重连开关
    /// </summary>
    private void CloseTransport()
    {
        receiving = false;
        MyPlayerId = 0;   // 断线后身份失效，避免残留旧账号的ID
        stream?.Close();
        client?.Close();
        stream = null;
        client = null;
    }

    // ──────────────────────────────
    // 心跳与自动重连
    // ──────────────────────────────

    /// <summary>
    /// 心跳循环（主线程 Update 调用）：到点且仍在连接状态就发 PingRequest
    /// 服务端回 PongResponse；若链路死了，发送失败/读超时会走断线路径，这里不用额外判活
    /// </summary>
    private void TickHeartbeat()
    {
        if (!receiving || stream == null) return;
        if (DateTime.Now < nextHeartbeatAt) return;
        nextHeartbeatAt = DateTime.Now.AddSeconds(HeartbeatIntervalSeconds);
        _ = SendAsync(MessageID.PingRequest, new PingRequest
        {
            ClientMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }

    /// <summary>
    /// 自动重连循环：指数退避重连，成功后若有登录凭据缓存则自动重新登录
    /// 触发时机：非主动断开的接收循环退出；上限 ReconnectMaxAttempts 次后放弃（等待用户手动重连）
    /// </summary>
    private async Task ReconnectLoopAsync()
    {
        if (reconnecting) return;
        reconnecting = true;
        try
        {
            for (int attempt = 0; attempt < ReconnectMaxAttempts && autoReconnect; attempt++)
            {
                int delayMs = Math.Min(
                    ReconnectBaseDelayMs * (1 << Math.Min(attempt, 8)),
                    ReconnectMaxDelayMs);
                OnStatus($"连接已断开，{delayMs / 1000}s 后自动重连（第 {attempt + 1}/{ReconnectMaxAttempts} 次）");
                await Task.Delay(delayMs);
                if (!autoReconnect) return; // 等待期间用户主动断开/退出应用
                bool ok = await ConnectAsync(lastHost, lastPort);
                if (!ok) continue;
                // ConnectAsync 成功路径会重新置 autoReconnect=true
                if (!string.IsNullOrEmpty(cachedAccount))
                {
                    // 密码优先（会话是密码登录建立的）；令牌登录的会话没有密码，用令牌恢复
                    if (!string.IsNullOrEmpty(cachedPassword))
                    {
                        await SendLogin(cachedAccount, cachedPassword);
                        OnStatus("已重连并自动登录");
                    }
                    else if (!string.IsNullOrEmpty(cachedToken))
                    {
                        await SendTokenLogin(cachedAccount, cachedToken);
                        OnStatus("已重连并自动登录");
                    }
                    else
                    {
                        OnStatus("已重连，等待登录");
                    }
                }
                else
                {
                    OnStatus("已重连，等待登录");
                }
                return;
            }
            OnStatus("自动重连失败次数过多，已停止（请手动重连）");
        }
        finally
        {
            reconnecting = false;
        }
    }

    // ──────────────────────────────
    // 收包
    // ──────────────────────────────

    /// <summary>
    /// 后台接收循环方法
    /// 持续读取完整数据包并入队，等待主线程处理（4字节长度前缀解决粘包/半包）
    /// </summary>
    private async Task ReceiveLoop(TcpClient loopClient, NetworkStream loopStream)
    {
        try
        {
            while (receiving)
            {
                // 每帧独立超时窗：Tuanjie 运行时没有 CancellationTokenSource.TryReset（.NET 6+ API），
                // 改为每帧新建带超时的 CTS，收到完整帧后下一帧自然重新计时
                using CancellationTokenSource frameCts = new CancellationTokenSource(TimeSpan.FromSeconds(ReceiveTimeoutSeconds));
                byte[] header = new byte[4];
                if (!await ReadExactly(loopStream, frameCts.Token, header, 4)) break;
                int length = BitConverter.ToInt32(header, 0);
                // 帧体至少含4字节消息ID；1~3 为坏帧（原先只拦 <=0，会让 BitConverter 在主线程抛越界）
                if (length < 4 || length > 1024 * 1024)
                    throw new InvalidDataException($"非法数据包长度:{length}");
                byte[] data = new byte[length];
                if (!await ReadExactly(loopStream, frameCts.Token, data, length)) break;
                packetQueue.Enqueue(data);
            }
        }
        catch (OperationCanceledException)
        {
            OnStatus($"连接已 {ReceiveTimeoutSeconds}s 未收到任何数据，判定断线");
        }
        catch (Exception exception)
        {
            // Disconnect 主动关流也会走到这里，不算异常
            OnStatus($"连接断开:{exception.Message}");
        }

        // 接收循环结束（对端关闭 / 网络异常 / 读超时 / Disconnect 关流）= 连接已不可用。
        // 统一在此宣告断线，订阅方据此清理在途状态（事件可重复触发，订阅方需幂等）
        mainThreadActions.Enqueue(() => ConnectionBroken?.Invoke());

        // 非主动断线且允许自动重连时，后台启动退避重连
        if (autoReconnect && loopClient == client)
        {
            _ = ReconnectLoopAsync();
        }
    }

    /// <summary>
    /// 精确读取指定字节数的方法（带每连接取消源，读超时由外层 ReceiveLoop 兜底）
    /// </summary>
    private async Task<bool> ReadExactly(NetworkStream loopStream, CancellationToken loopToken, byte[] buffer, int length)
    {
        int offset = 0;
        while (offset < length)
        {
            int read = await loopStream.ReadAsync(buffer.AsMemory(offset, length - offset), loopToken);
            if (read <= 0) return false;
            offset += read;
        }
        return true;
    }

    /// <summary>
    /// 数据包处理方法（主线程调用）
    /// 解析消息ID与protobuf消息体并分发到对应事件
    /// </summary>
    private void HandlePacket(byte[] data)
    {
        // 帧结构：[4字节消息ID][protobuf消息体]
        int messageId = BitConverter.ToInt32(data, 0);
        byte[] body = new byte[data.Length - 4];
        Buffer.BlockCopy(data, 4, body, 0, body.Length);

        switch ((MessageID)messageId)
        {
            case MessageID.PongResponse:
                // 心跳回执：仅作链路活性证据（收包即重置读超时），RTT 调试日志留在开关内
                PongResponse pong = PongResponse.Parser.ParseFrom(body);
                long rttMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - pong.ClientMs;
                if (Debug.isDebugBuild) Debug.Log($"[Compete网络]心跳RTT:{rttMs}ms");
                break;

            case MessageID.LoginResponse:
                LoginResponse loginResponse = LoginResponse.Parser.ParseFrom(body);
                // 缓存自己的玩家ID（消息气泡左右分栏、未读数统计都要用）
                if (loginResponse.Success) MyPlayerId = loginResponse.PlayerId;
                LoginResponseReceived?.Invoke(loginResponse);
                break;

            case MessageID.TokenLoginResponse:
                // 自动登录响应复用 LoginResponse 体，成功路径与密码登录完全一致
                LoginResponse tokenLoginResponse = LoginResponse.Parser.ParseFrom(body);
                if (tokenLoginResponse.Success) MyPlayerId = tokenLoginResponse.PlayerId;
                LoginResponseReceived?.Invoke(tokenLoginResponse);
                break;

            case MessageID.KickoutNotify:
                // 被顶号：传输层自卫必须在这里做——autoReconnect 与 cached* 凭据都是私有字段，
                // 外部订阅者只能 Disconnect、清不掉凭据；若后台读循环抢先重连，就会拿旧凭据自动重登
                KickoutNotify kickout = KickoutNotify.Parser.ParseFrom(body);
                cachedAccount = null;
                cachedPassword = null;
                cachedToken = null;
                AutoLoginSuppressedByKick = true;
                Debug.LogWarning($"[Compete网络]被顶号下线：{kickout.Reason}");
                Disconnect();
                KickoutNotifyReceived?.Invoke(kickout);
                break;

            case MessageID.RegisterResponse:
                RegisterResponseReceived?.Invoke(RegisterResponse.Parser.ParseFrom(body));
                break;

            case MessageID.FriendListResponse:
                FriendListResponseReceived?.Invoke(FriendListResponse.Parser.ParseFrom(body));
                break;

            case MessageID.QueryPlayerResponse:
                QueryPlayerResponseReceived?.Invoke(QueryPlayerResponse.Parser.ParseFrom(body));
                break;

            case MessageID.FriendAddResponse:
                FriendAddResponseReceived?.Invoke(FriendAddResponse.Parser.ParseFrom(body));
                break;

            case MessageID.FriendRespondResponse:
                FriendRespondResponseReceived?.Invoke(FriendRespondResponse.Parser.ParseFrom(body));
                break;

            case MessageID.FriendRequestsResponse:
                FriendRequestsResponseReceived?.Invoke(FriendRequestsResponse.Parser.ParseFrom(body));
                break;

            case MessageID.ChatSendResponse:
                ChatSendResponseReceived?.Invoke(ChatSendResponse.Parser.ParseFrom(body));
                break;

            case MessageID.ChatHistoryResponse:
                ChatHistoryResponseReceived?.Invoke(ChatHistoryResponse.Parser.ParseFrom(body));
                break;

            case MessageID.ChatReceiveNotify:
                ChatReceiveNotifyReceived?.Invoke(ChatReceiveNotify.Parser.ParseFrom(body));
                break;

            case MessageID.WorldChatSendResponse:
                WorldChatSendResponseReceived?.Invoke(WorldChatSendResponse.Parser.ParseFrom(body));
                break;

            case MessageID.WorldChatHistoryResponse:
                WorldChatHistoryResponseReceived?.Invoke(WorldChatHistoryResponse.Parser.ParseFrom(body));
                break;

            case MessageID.WorldChatNotify:
                WorldChatNotifyReceived?.Invoke(WorldChatNotify.Parser.ParseFrom(body));
                break;

            case MessageID.AgentChatResponse:
                AgentChatResponseReceived?.Invoke(AgentChatResponse.Parser.ParseFrom(body));
                break;

            case MessageID.AgentChatChunk:
                AgentChatChunkReceived?.Invoke(AgentChatChunk.Parser.ParseFrom(body));
                break;

            case MessageID.AgentBriefNotify:
                AgentBriefNotifyReceived?.Invoke(AgentBriefNotify.Parser.ParseFrom(body));
                break;

            case MessageID.AgentHistoryResponse:
                AgentHistoryResponseReceived?.Invoke(AgentHistoryResponse.Parser.ParseFrom(body));
                break;

            case MessageID.AgentProactiveNotify:
                AgentProactiveNotifyReceived?.Invoke(AgentProactiveNotify.Parser.ParseFrom(body));
                break;

            case MessageID.ScoreSubmitResponse:
                ScoreSubmitResponseReceived?.Invoke(ScoreSubmitResponse.Parser.ParseFrom(body));
                break;

            case MessageID.LeaderboardResponse:
                LeaderboardResponseReceived?.Invoke(LeaderboardResponse.Parser.ParseFrom(body));
                break;

            case MessageID.BagListResponse:
                BagListResponseReceived?.Invoke(BagListResponse.Parser.ParseFrom(body));
                break;

            case MessageID.BagUseResponse:
                BagUseResponseReceived?.Invoke(BagUseResponse.Parser.ParseFrom(body));
                break;

            case MessageID.BagDiscardResponse:
                BagDiscardResponseReceived?.Invoke(BagDiscardResponse.Parser.ParseFrom(body));
                break;

            case MessageID.BagTidyResponse:
                BagTidyResponseReceived?.Invoke(BagTidyResponse.Parser.ParseFrom(body));
                break;

            case MessageID.BagSwapResponse:
                BagSwapResponseReceived?.Invoke(BagSwapResponse.Parser.ParseFrom(body));
                break;

            case MessageID.BagGrantResponse:
                BagGrantResponseReceived?.Invoke(BagGrantResponse.Parser.ParseFrom(body));
                break;

            case MessageID.AchievementListResponse:
                AchievementListResponseReceived?.Invoke(AchievementListResponse.Parser.ParseFrom(body));
                break;

            case MessageID.AchievementClaimResponse:
                AchievementClaimResponseReceived?.Invoke(AchievementClaimResponse.Parser.ParseFrom(body));
                break;

            case MessageID.AchievementUnlockedNotify:
                AchievementUnlockedNotifyReceived?.Invoke(AchievementUnlockedNotify.Parser.ParseFrom(body));
                break;

            case MessageID.EquipmentListResponse:
                EquipmentListResponseReceived?.Invoke(EquipmentListResponse.Parser.ParseFrom(body));
                break;

            case MessageID.EquipResponse:
                EquipResponseReceived?.Invoke(EquipResponse.Parser.ParseFrom(body));
                break;

            case MessageID.GuidePullResponse:
                GuidePullResponseReceived?.Invoke(GuidePullResponse.Parser.ParseFrom(body));
                break;

            case MessageID.GuidePushResponse:
                GuidePushResponseReceived?.Invoke(GuidePushResponse.Parser.ParseFrom(body));
                break;

            case MessageID.AchievementProgressNotify:
                AchievementProgressNotifyReceived?.Invoke(AchievementProgressNotify.Parser.ParseFrom(body));
                break;

            case MessageID.AchievementGmResponse:
                AchievementGmResponseReceived?.Invoke(AchievementGmResponse.Parser.ParseFrom(body));
                break;

            case MessageID.MailListResponse:
                MailListResponseReceived?.Invoke(MailListResponse.Parser.ParseFrom(body));
                break;

            case MessageID.MailReadResponse:
                MailReadResponseReceived?.Invoke(MailReadResponse.Parser.ParseFrom(body));
                break;

            case MessageID.MailClaimResponse:
                MailClaimResponseReceived?.Invoke(MailClaimResponse.Parser.ParseFrom(body));
                break;

            case MessageID.MailDeleteResponse:
                MailDeleteResponseReceived?.Invoke(MailDeleteResponse.Parser.ParseFrom(body));
                break;

            case MessageID.MailGrantResponse:
                MailGrantResponseReceived?.Invoke(MailGrantResponse.Parser.ParseFrom(body));
                break;

            case MessageID.MailReceiveNotify:
                MailReceiveNotifyReceived?.Invoke(MailReceiveNotify.Parser.ParseFrom(body));
                break;

            case MessageID.NarrativeQuestSyncResponse:
                NarrativeQuestSyncResponseReceived?.Invoke(NarrativeQuestSyncResponse.Parser.ParseFrom(body));
                break;

            case MessageID.NarrativeQuestAcceptResponse:
                NarrativeQuestAcceptResponseReceived?.Invoke(NarrativeQuestAcceptResponse.Parser.ParseFrom(body));
                break;

            case MessageID.NarrativeQuestSubmitResponse:
                NarrativeQuestSubmitResponseReceived?.Invoke(NarrativeQuestSubmitResponse.Parser.ParseFrom(body));
                break;

            case MessageID.NarrativeQuestStateNotify:
                NarrativeQuestStateNotifyReceived?.Invoke(NarrativeQuestStateNotify.Parser.ParseFrom(body));
                break;

            case MessageID.NarrativeQuestAbandonResponse:
                NarrativeQuestAbandonResponseReceived?.Invoke(NarrativeQuestAbandonResponse.Parser.ParseFrom(body));
                break;

            case MessageID.NarrativeQuestToastNotify:
                NarrativeQuestToastNotifyReceived?.Invoke(NarrativeQuestToastNotify.Parser.ParseFrom(body));
                break;

            case MessageID.ActivityListResponse:
                ActivityListResponseReceived?.Invoke(ActivityListResponse.Parser.ParseFrom(body));
                break;

            case MessageID.GachaDrawResponse:
                GachaDrawResponseReceived?.Invoke(GachaDrawResponse.Parser.ParseFrom(body));
                break;

            case MessageID.GachaHistoryResponse:
                GachaHistoryResponseReceived?.Invoke(GachaHistoryResponse.Parser.ParseFrom(body));
                break;

            case MessageID.SignInListResponse:
                SignInListResponseReceived?.Invoke(SignInListResponse.Parser.ParseFrom(body));
                break;

            case MessageID.SignInClaimResponse:
                SignInClaimResponseReceived?.Invoke(SignInClaimResponse.Parser.ParseFrom(body));
                break;

            case MessageID.ActivityChangeNotify:
                ActivityChangeNotifyReceived?.Invoke(ActivityChangeNotify.Parser.ParseFrom(body));
                break;

            case MessageID.ActivityGmRotateResponse:
                ActivityGmRotateResponseReceived?.Invoke(ActivityGmRotateResponse.Parser.ParseFrom(body));
                break;

            // ── 对战好友邀请（信令层）──
            case MessageID.BattleInviteResponse:
                BattleInviteResponseReceived?.Invoke(BattleInviteResponse.Parser.ParseFrom(body));
                break;

            case MessageID.BattleInviteNotify:
                BattleInviteNotifyReceived?.Invoke(BattleInviteNotify.Parser.ParseFrom(body));
                break;

            case MessageID.BattleInviteRespondResponse:
                BattleInviteRespondResponseReceived?.Invoke(BattleInviteRespondResponse.Parser.ParseFrom(body));
                break;

            case MessageID.BattleInviteResultNotify:
                BattleInviteResultNotifyReceived?.Invoke(BattleInviteResultNotify.Parser.ParseFrom(body));
                break;

            case MessageID.BattleInviteCancelResponse:
                BattleInviteCancelResponseReceived?.Invoke(BattleInviteCancelResponse.Parser.ParseFrom(body));
                break;

            case MessageID.BattleInviteCancelledNotify:
                BattleInviteCancelledNotifyReceived?.Invoke(BattleInviteCancelledNotify.Parser.ParseFrom(body));
                break;

            case MessageID.SaveStatePullResponse:
                SaveStatePullResponseReceived?.Invoke(SaveStatePullResponse.Parser.ParseFrom(body));
                break;

            case MessageID.SaveStatePushResponse:
                SaveStatePushResponseReceived?.Invoke(SaveStatePushResponse.Parser.ParseFrom(body));
                break;

            default:
                Debug.LogWarning($"未知消息:{messageId}");
                break;
        }
    }

    // ──────────────────────────────
    // 发包
    // ──────────────────────────────

    /// <summary>
    /// 发送 protobuf 消息的方法
    /// 组装[消息ID+消息体]后走统一的长度前缀发送路径
    /// </summary>
    /// <param name="id">参数名：id，类型：MessageID，用途：消息ID枚举值</param>
    /// <param name="message">参数名：message，类型：IMessage，用途：protobuf消息对象</param>
    public Task SendAsync(MessageID id, IMessage message)
    {
        byte[] body = message.ToByteArray();
        byte[] payload = new byte[4 + body.Length];
        BitConverter.GetBytes((int)id).CopyTo(payload, 0);
        Buffer.BlockCopy(body, 0, payload, 4, body.Length);
        return SendRawAsync(payload);
    }

    /// <summary>
    /// 发送登录请求的方法
    /// </summary>
    /// <param name="account">参数名：account，类型：string，用途：账号</param>
    /// <param name="password">参数名：password，类型：string，用途：密码</param>
    /// <param name="rememberMe">参数名：rememberMe，类型：bool，用途：是否勾选"下次自动登录"（true=登录成功后服务器签发令牌随响应下发）</param>
    public Task SendLogin(string account, string password, bool rememberMe = false)
    {
        // 缓存凭据（仅内存）：断线自动重连成功后用它免登录恢复会话
        cachedAccount = account;
        cachedPassword = password;
        cachedToken = null;   // 密码在手，重连不走令牌
        return SendAsync(MessageID.LoginRequest, new LoginRequest
        {
            Account = account,
            Password = password,
            RememberMe = rememberMe,
        });
    }

    /// <summary>
    /// 发送自动登录请求的方法（令牌换会话：用上次 remember_me 登录时保存的令牌免密码登录）
    /// </summary>
    /// <param name="account">参数名：account，类型：string，用途：上次登录的账号名</param>
    /// <param name="token">参数名：token，类型：string，用途：上次登录响应里下发的自动登录令牌</param>
    /// <returns>返回值类型：Task，用途：发送任务的句柄</returns>
    public Task SendTokenLogin(string account, string token)
    {
        // 缓存（仅内存）：令牌登录的会话断线后没有密码可回退，重连用令牌恢复
        cachedAccount = account;
        cachedPassword = null;
        cachedToken = token;
        return SendAsync(MessageID.TokenLoginRequest, new TokenLoginRequest
        {
            Account = account,
            Token = token,
        });
    }

    /// <summary>
    /// 发送注册请求的方法
    /// </summary>
    /// <param name="account">参数名：account，类型：string，用途：账号</param>
    /// <param name="password">参数名：password，类型：string，用途：密码</param>
    public Task SendRegister(string account, string password)
    {
        return SendAsync(MessageID.RegisterRequest, new RegisterRequest
        {
            Account = account,
            Password = password,
        });
    }

    /// <summary>
    /// 发送好友列表请求的方法（空消息体，身份由服务端会话绑定）
    /// </summary>
    public Task SendFriendListRequest()
    {
        return SendAsync(MessageID.FriendListRequest, new FriendListRequest());
    }

    /// <summary>
    /// 发送查询玩家请求的方法
    /// </summary>
    /// <param name="playerId">参数名：playerId，类型：int，用途：要查询的玩家ID</param>
    public Task SendQueryPlayer(int playerId)
    {
        return SendAsync(MessageID.QueryPlayerRequest, new QueryPlayerRequest
        {
            PlayerId = playerId,
        });
    }

    /// <summary>
    /// 发送成绩上报请求的方法（排行榜：服务端按"只保留最高分"入账）
    /// </summary>
    /// <param name="challengeId">参数名：challengeId，类型：int，用途：挑战ID</param>
    /// <param name="score">参数名：score，类型：long，用途：本次挑战成绩</param>
    public Task SendScoreSubmit(int challengeId, long score)
    {
        return SendAsync(MessageID.ScoreSubmitRequest, new ScoreSubmitRequest
        {
            ChallengeId = challengeId,
            Score = score,
        });
    }

    /// <summary>
    /// 发送引导进度拉取请求的方法（身份由服务端会话绑定）
    /// </summary>
    /// <param name="guideKey">参数名：guideKey，类型：string，用途：引导标识（当前 zhuye）</param>
    public Task SendGuidePullRequest(string guideKey)
    {
        return SendAsync(MessageID.GuidePullRequest, new GuidePullRequest
        {
            GuideKey = guideKey,
        });
    }

    /// <summary>
    /// 发送引导进度上报请求的方法（覆盖式，服务端钳制步骤下标）
    /// </summary>
    /// <param name="guideKey">参数名：guideKey，类型：string，用途：引导标识</param>
    /// <param name="step">参数名：step，类型：int，用途：下一次要播放的步骤下标</param>
    /// <param name="completed">参数名：completed，类型：bool，用途：是否已完成或已跳过</param>
    public Task SendGuidePushRequest(string guideKey, int step, bool completed)
    {
        return SendAsync(MessageID.GuidePushRequest, new GuidePushRequest
        {
            GuideKey = guideKey,
            Step = step,
            Completed = completed,
        });
    }

    /// <summary>
    /// 发送排行榜查询请求的方法
    /// </summary>
    /// <param name="challengeId">参数名：challengeId，类型：int，用途：挑战ID</param>
    /// <param name="topN">参数名：topN，类型：int，用途：取前几名（0 = 服务端默认 10）</param>
    public Task SendLeaderboardRequest(int challengeId, int topN)
    {
        return SendAsync(MessageID.LeaderboardRequest, new LeaderboardRequest
        {
            ChallengeId = challengeId,
            TopN = topN,
        });
    }

    /// <summary>
    /// 发送背包列表请求的方法（空消息体，身份由服务端会话绑定）
    /// </summary>
    public Task SendBagListRequest()
    {
        return SendAsync(MessageID.BagListRequest, new BagListRequest());
    }

    /// <summary>
    /// 发送使用物品请求的方法
    /// </summary>
    /// <param name="slotIndex">参数名：slotIndex，类型：int，用途：目标槽位下标</param>
    /// <param name="count">参数名：count，类型：int，用途：使用数量</param>
    public Task SendBagUse(int slotIndex, int count)
    {
        return SendAsync(MessageID.BagUseRequest, new BagUseRequest
        {
            SlotIndex = slotIndex,
            Count = count,
        });
    }

    /// <summary>
    /// 发送丢弃物品请求的方法
    /// </summary>
    /// <param name="slotIndex">参数名：slotIndex，类型：int，用途：目标槽位下标</param>
    /// <param name="count">参数名：count，类型：int，用途：丢弃数量</param>
    public Task SendBagDiscard(int slotIndex, int count)
    {
        return SendAsync(MessageID.BagDiscardRequest, new BagDiscardRequest
        {
            SlotIndex = slotIndex,
            Count = count,
        });
    }

    /// <summary>
    /// 发送整理背包请求的方法（合并堆叠 + 排序，规则在服务端）
    /// </summary>
    public Task SendBagTidy()
    {
        return SendAsync(MessageID.BagTidyRequest, new BagTidyRequest());
    }

    /// <summary>
    /// 发送槽位换位请求的方法（拖拽换位的服务端落地）
    /// </summary>
    /// <param name="fromSlot">参数名：fromSlot，类型：int，用途：起点槽位下标</param>
    /// <param name="toSlot">参数名：toSlot，类型：int，用途：目标槽位下标</param>
    public Task SendBagSwap(int fromSlot, int toSlot)
    {
        return SendAsync(MessageID.BagSwapRequest, new BagSwapRequest
        {
            FromSlot = fromSlot,
            ToSlot = toSlot,
        });
    }

    /// <summary>
    /// 发送物品发放请求的方法（测试发放，本期物品唯一来源）
    /// </summary>
    /// <param name="itemId">参数名：itemId，类型：int，用途：物品ID（须存在于 item_config）</param>
    /// <param name="count">参数名：count，类型：int，用途：发放数量</param>
    public Task SendBagGrant(int itemId, int count)
    {
        return SendAsync(MessageID.BagGrantRequest, new BagGrantRequest
        {
            ItemId = itemId,
            Count = count,
        });
    }

    /// <summary>
    /// 发送成就列表请求的方法（空消息体，身份由服务端会话绑定）
    /// </summary>
    public Task SendAchievementListRequest()
    {
        return SendAsync(MessageID.AchievementListRequest, new AchievementListRequest());
    }

    /// <summary>
    /// 发送领取成就奖励请求的方法（服务端事务 + 幂等）
    /// </summary>
    /// <param name="achievementIds">要领取的成就ID（首期一次一个）</param>
    public Task SendAchievementClaim(IEnumerable<int> achievementIds)
    {
        AchievementClaimRequest request = new();
        if (achievementIds != null)
        {
            foreach (int id in achievementIds)
            {
                if (id > 0) request.AchievementIds.Add(id);
            }
        }
        return SendAsync(MessageID.AchievementClaimRequest, request);
    }

    /// <summary>
    /// 发送成就 GM 请求（联调：改进度/强制解锁/重置）
    /// </summary>
    public Task SendAchievementGm(int achievementId, int progress, bool unlock, bool reset)
    {
        return SendAsync(MessageID.AchievementGmRequest, new AchievementGmRequest
        {
            AchievementId = achievementId,
            Progress = progress,
            Unlock = unlock,
            Reset = reset,
        });
    }

    /// <summary>
    /// 发送装备列表请求的方法（空消息体，身份由服务端会话绑定）
    /// </summary>
    public Task SendEquipmentListRequest()
    {
        return SendAsync(MessageID.EquipmentListRequest, new EquipmentListRequest());
    }

    /// <summary>
    /// 发送穿戴/卸下请求的方法（instanceId=0 表示卸下该槽，校验与转移都在服务端）
    /// </summary>
    /// <param name="charId">角色ID</param>
    /// <param name="slotType">槽位类型（1=武器）</param>
    /// <param name="instanceId">要穿上的实例ID（0=卸下）</param>
    public Task SendEquip(int charId, int slotType, long instanceId)
    {
        return SendAsync(MessageID.EquipRequest, new EquipRequest
        {
            CharId = charId,
            SlotType = slotType,
            InstanceId = instanceId,
        });
    }

    /// <summary>
    /// 发送邮件列表请求的方法（空消息体，一次拿全部有效邮件）
    /// </summary>
    public Task SendMailListRequest()
    {
        return SendAsync(MessageID.MailListRequest, new MailListRequest());
    }

    /// <summary>
    /// 发送标记已读请求的方法（点开详情时调用，服务端幂等）
    /// </summary>
    /// <param name="mailId">参数名：mailId，类型：int，用途：邮件ID</param>
    public Task SendMailRead(int mailId)
    {
        return SendAsync(MessageID.MailReadRequest, new MailReadRequest
        {
            MailId = mailId,
        });
    }

    /// <summary>
    /// 发送领取附件请求的方法（一键领取全部附件，服务端单事务校验并整单发放）
    /// </summary>
    /// <param name="mailId">参数名：mailId，类型：int，用途：邮件ID</param>
    public Task SendMailClaim(int mailId)
    {
        return SendAsync(MessageID.MailClaimRequest, new MailClaimRequest
        {
            MailId = mailId,
        });
    }

    /// <summary>
    /// 发送删除邮件请求的方法（软删，附件未领取也允许删）
    /// </summary>
    /// <param name="mailId">参数名：mailId，类型：int，用途：邮件ID</param>
    public Task SendMailDelete(int mailId)
    {
        return SendAsync(MessageID.MailDeleteRequest, new MailDeleteRequest
        {
            MailId = mailId,
        });
    }

    /// <summary>
    /// 发送 GM 发件请求的方法（本期邮件唯一来源，联调用；收件人 0 = 发给自己）
    /// </summary>
    /// <param name="targetPlayerId">参数名：targetPlayerId，类型：int，用途：收件玩家ID（0=自己）</param>
    /// <param name="title">参数名：title，类型：string，用途：标题</param>
    /// <param name="body">参数名：body，类型：string，用途：正文</param>
    /// <param name="items">参数名：items，类型：List，用途：附件（物品ID, 数量），可为空</param>
    public Task SendMailGrant(int targetPlayerId, string title, string body, List<(int ItemId, int Count)> items)
    {
        MailGrantRequest request = new()
        {
            TargetPlayerId = targetPlayerId,
            Title = title,
            Body = body,
        };
        foreach ((int itemId, int count) in items)
        {
            request.Items.Add(new MailItemInfo { ItemId = itemId, Count = count });
        }
        return SendAsync(MessageID.MailGrantRequest, request);
    }

    /// <summary>
    /// 发送叙事任务拉取请求的方法（Mm框架·登录成功后拉取玩家任务状态快照）
    /// </summary>
    public Task SendNarrativeQuestSyncRequest()
    {
        return SendAsync(MessageID.NarrativeQuestSyncRequest, new NarrativeQuestSyncRequest());
    }

    /// <summary>
    /// 发送叙事任务玩法事件上报的方法（服务端权威模式：客户端只报事件，任务逻辑由服务端推进）
    /// </summary>
    /// <param name="eventType">参数名：eventType，类型：int，用途：事件类型（1击杀/2收集/3到达/4对话，对应 ENarrativeEventType）</param>
    /// <param name="targetKey">参数名：targetKey，类型：string，用途：击杀/收集/到达的匹配Key（对话事件传空）</param>
    /// <param name="count">参数名：count，类型：int，用途：数量（击杀/收集可批量，到达传1）</param>
    /// <param name="graphName">参数名：graphName，类型：string，用途：对话图资产名（仅对话事件）</param>
    /// <param name="eventKey">参数名：eventKey，类型：string，用途：对话事件Key（仅对话事件）</param>
    public Task SendNarrativeQuestEventReport(int eventType, string targetKey, int count, string graphName = "", string eventKey = "")
    {
        return SendAsync(MessageID.NarrativeQuestEventReport, new NarrativeQuestEventReport
        {
            EventType = (Common.Network.ENarrativeEventType)eventType,
            TargetKey = targetKey ?? string.Empty,
            Count = count,
            GraphName = graphName ?? string.Empty,
            EventKey = eventKey ?? string.Empty,
        });
    }

    /// <summary>
    /// 发送叙事任务接取请求的方法（服务端校验前置链/未过期，回执带全量快照）
    /// </summary>
    /// <param name="questId">参数名：questId，类型：int，用途：要接取的任务ID</param>
    public Task SendNarrativeQuestAccept(int questId)
    {
        return SendAsync(MessageID.NarrativeQuestAcceptRequest, new NarrativeQuestAcceptRequest { QuestId = questId });
    }

    /// <summary>
    /// 发送叙事任务提交请求的方法（服务端校验执行中/全目标达标，回执带全量快照）
    /// </summary>
    /// <param name="questId">参数名：questId，类型：int，用途：要提交的任务ID</param>
    public Task SendNarrativeQuestSubmit(int questId)
    {
        return SendAsync(MessageID.NarrativeQuestSubmitRequest, new NarrativeQuestSubmitRequest { QuestId = questId });
    }

    /// <summary>
    /// 发送叙事任务放弃请求的方法（服务端校验执行中，回执带全量快照）
    /// </summary>
    /// <param name="questId">参数名：questId，类型：int，用途：要放弃的任务ID</param>
    public Task SendNarrativeQuestAbandon(int questId)
    {
        return SendAsync(MessageID.NarrativeQuestAbandonRequest, new NarrativeQuestAbandonRequest { QuestId = questId });
    }

    /// <summary>
    /// 发送活动列表请求的方法（打开活动入口/收到切换推送后刷新）
    /// </summary>
    public Task SendActivityListRequest()
    {
        return SendAsync(MessageID.ActivityListRequest, new ActivityListRequest());
    }

    /// <summary>
    /// 发送唤取请求的方法（服务端权威：扣媒介→roll→入包，回执带背包快照）
    /// </summary>
    /// <param name="bannerId">参数名：bannerId，类型：int，用途：卡池ID（来自 ActivityInfo.banner_id）</param>
    /// <param name="count">参数名：count，类型：int，用途：次数（1 单抽 / 10 十连）</param>
    /// <param name="requestId">参数名：requestId，类型：string，用途：幂等键（重试必须复用同一个值；空=不幂等）</param>
    public Task SendGachaDrawRequest(int bannerId, int count, string requestId)
    {
        return SendAsync(MessageID.GachaDrawRequest, new GachaDrawRequest
        {
            BannerId = bannerId,
            Count = count,
            RequestId = requestId ?? string.Empty,
        });
    }

    /// <summary>
    /// 发送唤取流水请求的方法（唤取详单；bannerId=0 为全部卡池）
    /// </summary>
    /// <param name="bannerId">参数名：bannerId，类型：int，用途：卡池ID（0=全部）</param>
    public Task SendGachaHistoryRequest(int bannerId)
    {
        return SendAsync(MessageID.GachaHistoryRequest, new GachaHistoryRequest
        {
            BannerId = bannerId,
        });
    }

    /// <summary>
    /// 发送签到状态请求的方法（打开签到页时拉 7 天奖励表与玩家进度）
    /// </summary>
    /// <param name="signId">参数名：signId，类型：int，用途：签到活动ID（ActivityInfo.type=2 条目的 activity_id）</param>
    public Task SendSignInListRequest(int signId)
    {
        return SendAsync(MessageID.SignInListRequest, new SignInListRequest
        {
            SignId = signId,
        });
    }

    /// <summary>
    /// 发送签到请求的方法（服务端裁决第几天与每日一签，回执带背包快照）
    /// </summary>
    /// <param name="signId">参数名：signId，类型：int，用途：签到活动ID</param>
    public Task SendSignInClaimRequest(int signId)
    {
        return SendAsync(MessageID.SignInClaimRequest, new SignInClaimRequest
        {
            SignId = signId,
        });
    }

    /// <summary>
    /// 发送 GM 轮换活动请求的方法（联调/演示指令：结束当前活动、把目标活动提前到现在）
    /// </summary>
    /// <param name="targetActivityId">参数名：targetActivityId，类型：int，用途：目标活动ID（0=自动选下一排期）</param>
    public Task SendActivityGmRotateRequest(int targetActivityId)
    {
        return SendAsync(MessageID.ActivityGmRotateRequest, new ActivityGmRotateRequest
        {
            TargetActivityId = targetActivityId,
        });
    }

    /// <summary>
    /// 发送好友申请请求的方法
    /// </summary>
    /// <param name="targetId">参数名：targetId，类型：int，用途：目标玩家ID</param>
    public Task SendFriendAddRequest(int targetId)
    {
        return SendAsync(MessageID.FriendAddRequest, new FriendAddRequest
        {
            TargetId = targetId,
        });
    }

    /// <summary>
    /// 发送好友申请回应请求的方法
    /// </summary>
    /// <param name="fromPlayerId">参数名：fromPlayerId，类型：int，用途：申请人ID</param>
    /// <param name="accept">参数名：accept，类型：bool，用途：true=同意 false=拒绝</param>
    public Task SendFriendRespondRequest(int fromPlayerId, bool accept)
    {
        return SendAsync(MessageID.FriendRespondRequest, new FriendRespondRequest
        {
            FromPlayerId = fromPlayerId,
            Accept = accept,
        });
    }

    /// <summary>
    /// 发送待处理申请列表请求的方法（空消息体）
    /// </summary>
    public Task SendFriendRequestsRequest()
    {
        return SendAsync(MessageID.FriendRequestsRequest, new FriendRequestsRequest());
    }

    /// <summary>
    /// 发送聊天消息的方法
    /// 只带目标ID与内容：发送者身份由服务端会话绑定，客户端不传也无法伪造
    /// </summary>
    /// <param name="targetId">参数名：targetId，类型：int，用途：接收方玩家ID</param>
    /// <param name="content">参数名：content，类型：string，用途：消息正文（或表情键，见 contentType）</param>
    /// <param name="contentType">参数名：contentType，类型：int，用途：0=文本（默认）1=表情包</param>
    public Task SendChatSend(int targetId, string content, int contentType = 0)
    {
        return SendAsync(MessageID.ChatSendRequest, new ChatSendRequest
        {
            TargetId = targetId,
            Content = content,
            ContentType = contentType,
        });
    }

    /// <summary>
    /// 发送聊天历史请求的方法
    /// 同样只带对方ID，己方身份由服务端会话绑定
    /// </summary>
    /// <param name="targetId">参数名：targetId，类型：int，用途：要拉取与谁的历史消息</param>
    /// <param name="beforeMs">参数名：beforeMs，类型：long，用途：翻页游标（UTC毫秒），0=从最新开始</param>
    /// <param name="limit">参数名：limit，类型：int，用途：本次拉取条数，0=使用服务端默认值</param>
    /// <param name="peek">参数名：peek，类型：bool，用途：true=预读模式（只读不写，不置已读），默认 false 保持原行为</param>
    public Task SendChatHistory(int targetId, long beforeMs = 0, int limit = 0, bool peek = false)
    {
        return SendAsync(MessageID.ChatHistoryRequest, new ChatHistoryRequest
        {
            TargetId = targetId,
            BeforeMs = beforeMs,
            Limit = limit,
            Peek = peek,
        });
    }

    /// <summary>
    /// 发送世界频道消息的方法
    /// 只带内容：发送者身份由服务端会话绑定，服务端落库后广播给全部在线玩家
    /// </summary>
    /// <param name="content">参数名：content，类型：string，用途：消息正文（或表情键，见 contentType）</param>
    /// <param name="contentType">参数名：contentType，类型：int，用途：0=文本（默认）1=表情包</param>
    public Task SendWorldChatSend(string content, int contentType = 0)
    {
        return SendAsync(MessageID.WorldChatSendRequest, new WorldChatSendRequest
        {
            Content = content,
            ContentType = contentType,
        });
    }

    /// <summary>
    /// 发送世界频道历史请求的方法
    /// 不需要任何身份参数，己方身份由服务端会话绑定
    /// </summary>
    /// <param name="beforeMs">参数名：beforeMs，类型：long，用途：翻页游标（UTC毫秒），0=从最新开始</param>
    /// <param name="limit">参数名：limit，类型：int，用途：本次拉取条数，0=使用服务端默认值</param>
    public Task SendWorldChatHistory(long beforeMs = 0, int limit = 0)
    {
        return SendAsync(MessageID.WorldChatHistoryRequest, new WorldChatHistoryRequest
        {
            BeforeMs = beforeMs,
            Limit = limit,
        });
    }

    /// <summary>
    /// 发送 Agent（阿福）聊天消息的方法
    /// 只带内容：发送者身份由服务端会话绑定，网关层做登录校验/限长/限频后转发给 Agent 服务
    /// （内容审核已按 2026-10-16 用户决策取消，文本原样直通）
    /// </summary>
    /// <param name="content">参数名：content，类型：string，用途：消息正文（≤500 字，网关校验）</param>
    public Task SendAgentChat(string content)
    {
        return SendAsync(MessageID.AgentChatRequest, new AgentChatRequest
        {
            Content = content,
        });
    }

    /// <summary>
    /// 发送 Agent（阿福）历史回放请求的方法
    /// 打开 AgentPanel 拉最近对话（跨会话记忆的可见入口）；上拉加载更早时带 beforeMs 游标
    /// </summary>
    /// <param name="limit">参数名：limit，类型：int，用途：拉取条数，0=使用服务端默认值 20</param>
    /// <param name="beforeMs">参数名：beforeMs，类型：long，用途：游标时间（UTC 毫秒），>0=取早于该时间的更早一页</param>
    public Task SendAgentHistory(int limit = 0, long beforeMs = 0)
    {
        return SendAsync(MessageID.AgentHistoryRequest, new AgentHistoryRequest
        {
            Limit = limit,
            BeforeMs = beforeMs,
        });
    }

    // ──────────────────────────────
    // 对战好友邀请
    // ──────────────────────────────

    /// <summary>
    /// 发送对战邀请（房主 → 服务端）
    /// 服务端校验好友关系与在线状态后，把邀请推给目标好友
    /// </summary>
    /// <param name="targetPlayerId">参数名：targetPlayerId，类型：int，用途：被邀请的好友ID</param>
    /// <param name="roomId">参数名：roomId，类型：string，用途：房主本次房间标识（GUID）</param>
    /// <param name="hostAddress">参数名：hostAddress，类型：string，用途：房主自报的对战地址</param>
    /// <param name="hostPort">参数名：hostPort，类型：int，用途：房主对战端口（默认 7777）</param>
    public Task SendBattleInvite(int targetPlayerId, string roomId, string hostAddress, int hostPort = 7777)
    {
        return SendAsync(MessageID.BattleInviteRequest, new BattleInviteRequest
        {
            TargetPlayerId = targetPlayerId,
            RoomId = roomId ?? string.Empty,
            HostAddress = hostAddress ?? string.Empty,
            HostPort = hostPort,
        });
    }

    /// <summary>
    /// 回应对战邀请（被邀请方 → 服务端）
    /// </summary>
    /// <param name="inviteId">参数名：inviteId，类型：long，用途：邀请凭据（不可伪造）</param>
    /// <param name="accept">参数名：accept，类型：bool，用途：true=同意 false=拒绝</param>
    /// <param name="reason">参数名：reason，类型：string，用途：拒绝原因（如"正在对战中"），可为空</param>
    public Task SendBattleInviteRespond(long inviteId, bool accept, string reason = "")
    {
        return SendAsync(MessageID.BattleInviteRespondRequest, new BattleInviteRespondRequest
        {
            InviteId = inviteId,
            Accept = accept,
            Reason = reason ?? string.Empty,
        });
    }

    /// <summary>
    /// 取消对战邀请（房主 → 服务端）
    /// </summary>
    /// <param name="roomId">参数名：roomId，类型：string，用途：房间标识（inviteId=0 时按房间批量取消）</param>
    /// <param name="inviteId">参数名：inviteId，类型：long，用途：指定邀请ID，0=按房间全取消</param>
    public Task SendBattleInviteCancel(string roomId, long inviteId = 0)
    {
        return SendAsync(MessageID.BattleInviteCancelRequest, new BattleInviteCancelRequest
        {
            RoomId = roomId ?? string.Empty,
            InviteId = inviteId,
        });
    }

    /// <summary>
    /// 发送场景存档拉取请求的方法
    /// 进入带存档的场景时调用；回执 has_save=false 表示首次进入（走场景默认）
    /// </summary>
    /// <param name="sceneName">参数名：sceneName，类型：string，用途：场景名（服务端白名单：duihua / fuben）</param>
    public Task SendSaveStatePullRequest(string sceneName)
    {
        return SendAsync(MessageID.SaveStatePullRequest, new SaveStatePullRequest
        {
            SceneName = sceneName ?? string.Empty,
        });
    }

    /// <summary>
    /// 发送场景存档上报请求的方法（事件点覆盖式写入，不是增量）
    /// </summary>
    /// <param name="snapshot">参数名：snapshot，类型：SceneSaveSnapshot，用途：完整快照（位置/相机/对话节点）</param>
    public Task SendSaveStatePushRequest(SceneSaveSnapshot snapshot)
    {
        return SendAsync(MessageID.SaveStatePushRequest, new SaveStatePushRequest
        {
            Snapshot = snapshot,
        });
    }

    /// <summary>
    /// 原始发送方法（带4字节长度头）
    /// </summary>
    private async Task SendRawAsync(byte[] data)
    {
        // 用局部引用发送：await 期间 Disconnect 可能把共享字段置空，不能依赖它们
        NetworkStream currentStream = stream;
        if (currentStream == null)
        {
            // 静默返回会让调用方以为已发出：记警告，由订阅方的超时/回执逻辑兜底
            Debug.LogWarning($"[Compete网络]未连接，忽略发送消息 {(MessageID)BitConverter.ToInt32(data, 0)}");
            return;
        }

        byte[] header = BitConverter.GetBytes(data.Length);
        await sendLock.WaitAsync();
        try
        {
            await currentStream.WriteAsync(header);
            await currentStream.WriteAsync(data);
        }
        catch (Exception exception)
        {
            // 发送失败是比 TcpClient.Connected 更可靠的断线信号（后者只反映最后一次 IO）：
            // 置为断线状态、提示 UI，并让订阅方（ChatManager）回滚在途的乐观消息
            OnStatus($"发送失败，连接断开:{exception.Message}");
            mainThreadActions.Enqueue(() => ConnectionBroken?.Invoke());
            Disconnect();
        }
        finally
        {
            sendLock.Release();
        }
    }

    // ──────────────────────────────
    // 状态上报
    // ──────────────────────────────

    /// <summary>
    /// 状态上报方法（任意线程可调用，安全转移到主线程）
    /// </summary>
    private void OnStatus(string message)
    {
        Debug.Log($"[Compete网络]{message}");
        statusQueue.Enqueue(message);
    }
}
