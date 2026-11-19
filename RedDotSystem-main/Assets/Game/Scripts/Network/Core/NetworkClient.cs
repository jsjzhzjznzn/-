namespace Game.Network
{
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
    /// TCP 传输层：连接、收发、按消息类型分发
    /// 帧结构：[4字节小端长度头][4字节消息ID][protobuf消息体]
    /// 收包在后台线程入队，主线程每帧 Pump 一次完成解析与回调
    /// </summary>
    public sealed class NetworkClient : INetworkService, IDisposable
    {
        /// <summary>心跳发送间隔（秒），需显著小于 ReceiveTimeoutSeconds</summary>
        private const int HeartbeatIntervalSeconds = 15;

        /// <summary>读超时（秒）：超过该时长收不到任何完整帧即判定断线（可容忍两次心跳丢失+回执延迟）</summary>
        private const int ReceiveTimeoutSeconds = 45;

        /// <summary>每帧最多处理的包数，防止极端情况卡帧</summary>
        private const int MaxPacketsPerFrame = 64;

        /// <summary>发送锁，串行化写入操作防止多线程交错</summary>
        private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);

        /// <summary>收包队列：后台线程收包、主线程取包处理</summary>
        private readonly ConcurrentQueue<byte[]> packetQueue = new ConcurrentQueue<byte[]>();

        /// <summary>状态消息队列：把后台线程的状态文本转移到主线程</summary>
        private readonly ConcurrentQueue<string> statusQueue = new ConcurrentQueue<string>();

        /// <summary>主线程动作队列：把后台线程检测到的连接事件转移到主线程派发</summary>
        private readonly ConcurrentQueue<Action> mainThreadActions = new ConcurrentQueue<Action>();

        /// <summary>
        /// 按消息类型登记的回调。
        /// 进程内共享而不是实例字段：ClientNetworkManager 的单例可能在场景还无效时被创建、
        /// 加载完成后被销毁重建，此时会换一个新的 NetworkClient。若处理器跟着实例走，
        /// 各业务 Manager 在 Start 里注册的回调会随旧实例一起消失（现象：登录正常但私聊、历史、
        /// 推送类消息全部收不到），因此回调表必须跨实例存活。
        /// </summary>
        private static readonly Dictionary<Type, List<Action<IMessage>>> handlers = new Dictionary<Type, List<Action<IMessage>>>();

        /// <summary>
        /// 每次进入 Play 清空回调表：关闭域重载时静态字段会跨会话残留，
        /// 上一轮的委托仍挂在里面会重复派发给已销毁的对象
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetHandlers() => handlers.Clear();

        private TcpClient client;
        private NetworkStream stream;
        private bool receiving;
        private bool connecting;
        private DateTime nextHeartbeatAt;
        private bool disposed;

        /// <summary>连接状态文本（主线程触发，UI 可安全订阅）</summary>
        public event Action<string> StatusChanged;

        /// <summary>连接已不可用（主线程触发，可能重复触发，订阅方需幂等）</summary>
        public event Action ConnectionBroken;

        public bool IsConnected => client != null && client.Connected;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Disconnect();
            sendLock.Dispose();
        }

        // ──────────────────────────────
        // 订阅与分发
        // ──────────────────────────────

        public IDisposable Subscribe<TMessage>(Action<TMessage> handler) where TMessage : class, IMessage
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            Type messageType = typeof(TMessage);
            if (!handlers.TryGetValue(messageType, out List<Action<IMessage>> list))
            {
                list = new List<Action<IMessage>>();
                handlers.Add(messageType, list);
            }

            Action<IMessage> wrapper = message => handler((TMessage)message);
            list.Add(wrapper);

            return new NetworkSubscription(() =>
            {
                if (handlers.TryGetValue(messageType, out List<Action<IMessage>> current))
                {
                    current.Remove(wrapper);
                    if (current.Count == 0)
                        handlers.Remove(messageType);
                }
            });
        }

        /// <summary>
        /// 把一条已解析的消息派发给订阅者（仅主线程调用）
        /// </summary>
        private void Dispatch<TMessage>(TMessage message) where TMessage : class, IMessage
        {
            if (!handlers.TryGetValue(typeof(TMessage), out List<Action<IMessage>> list))
                return;

            for (int i = 0; i < list.Count; i++)
                list[i].Invoke(message);
        }

        // ──────────────────────────────
        // 连接管理
        // ──────────────────────────────

        /// <summary>
        /// 连接服务器；重入时直接返回 false
        /// </summary>
        public async Task<bool> ConnectAsync(string host, int port)
        {
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
        /// 断开连接并清理传输资源
        /// </summary>
        public void Disconnect()
        {
            CloseTransport();
        }

        /// <summary>
        /// 关闭当前传输层；接收循环随之结束并广播 ConnectionBroken
        /// </summary>
        private void CloseTransport()
        {
            receiving = false;
            stream?.Close();
            client?.Close();
            stream = null;
            client = null;
        }

        /// <summary>
        /// 心跳：到点且仍在连接状态就发 PingRequest；链路死亡由读超时/发送失败兜底
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

        // ──────────────────────────────
        // 主线程泵
        // ──────────────────────────────

        /// <summary>
        /// 主线程每帧调用一次：心跳、解析收包、派发状态与连接事件
        /// </summary>
        public void Pump()
        {
            TickHeartbeat();

            int processed = 0;
            while (processed < MaxPacketsPerFrame && packetQueue.TryDequeue(out byte[] data))
            {
                try
                {
                    HandlePacket(data);
                }
                catch (Exception exception)
                {
                    // 坏包意味着字节流已失步（protobuf 解析失败/未知结构），继续处理后续包只会错上加错
                    Debug.LogError($"[Compete网络]坏包中断处理，断开连接: {exception.Message}");
                    Disconnect();
                    break;
                }
                processed++;
            }

            while (statusQueue.TryDequeue(out string status))
                StatusChanged?.Invoke(status);

            // 先处理完本帧收到的包，再宣告连接死亡，保证回执先于回滚
            while (mainThreadActions.TryDequeue(out Action action))
                action?.Invoke();
        }

        // ──────────────────────────────
        // 收包
        // ──────────────────────────────

        /// <summary>
        /// 后台接收循环：持续读取完整数据包并入队（4字节长度前缀解决粘包/半包）
        /// </summary>
        private async Task ReceiveLoop(TcpClient loopClient, NetworkStream loopStream)
        {
            try
            {
                // 整条连接共用一个 CTS：Tuanjie 运行时没有 CancellationTokenSource.TryReset，
                // 用 CancelAfter 在每次成功读到数据后重新计时，等价于"从最后一次收到数据起算"的读超时
                using CancellationTokenSource readCts = new CancellationTokenSource();
                byte[] header = new byte[4];
                while (receiving)
                {
                    readCts.CancelAfter(TimeSpan.FromSeconds(ReceiveTimeoutSeconds));
                    if (!await ReadExactly(loopStream, readCts.Token, header, 4)) break;
                    int length = BitConverter.ToInt32(header, 0);
                    // 帧体至少含4字节消息ID；1~3 为坏帧（会让主线程 BitConverter 越界）
                    if (length < 4 || length > 1024 * 1024)
                        throw new InvalidDataException($"非法数据包长度:{length}");
                    byte[] data = new byte[length];
                    if (!await ReadExactly(loopStream, readCts.Token, data, length)) break;
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

            // 只有当前连接的循环结束时才宣告断线，避免旧循环把新连接判死
            if (loopClient == client)
                mainThreadActions.Enqueue(() => ConnectionBroken?.Invoke());
        }

        /// <summary>
        /// 精确读取指定字节数；读超时或对端关闭返回 false
        /// </summary>
        private static async Task<bool> ReadExactly(NetworkStream loopStream, CancellationToken loopToken, byte[] buffer, int length)
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

        // ──────────────────────────────
        // 发送
        // ──────────────────────────────

        public Task SendAsync(MessageID id, IMessage message)
        {
            byte[] body = message.ToByteArray();
            byte[] payload = new byte[4 + body.Length];
            BitConverter.GetBytes((int)id).CopyTo(payload, 0);
            Buffer.BlockCopy(body, 0, payload, 4, body.Length);
            return SendRawAsync(payload);
        }

        /// <summary>
        /// 原始发送（补4字节长度头）
        /// </summary>
        private async Task SendRawAsync(byte[] data)
        {
            // 用局部引用发送：await 期间 Disconnect 可能把共享字段置空
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
                // 发送失败是比 TcpClient.Connected 更可靠的断线信号
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
        // 解析
        // ──────────────────────────────

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
                Dispatch(LoginResponse.Parser.ParseFrom(body));
                break;

            case MessageID.TokenLoginResponse:
                // 自动登录响应复用 LoginResponse 体
                Dispatch(LoginResponse.Parser.ParseFrom(body));
                break;

            case MessageID.KickoutNotify:
                Dispatch(KickoutNotify.Parser.ParseFrom(body));
                break;

            case MessageID.RegisterResponse:
                Dispatch(RegisterResponse.Parser.ParseFrom(body));
                break;

            case MessageID.FriendListResponse:
                Dispatch(FriendListResponse.Parser.ParseFrom(body));
                break;

            case MessageID.QueryPlayerResponse:
                Dispatch(QueryPlayerResponse.Parser.ParseFrom(body));
                break;

            case MessageID.FriendAddResponse:
                Dispatch(FriendAddResponse.Parser.ParseFrom(body));
                break;

            case MessageID.FriendRespondResponse:
                Dispatch(FriendRespondResponse.Parser.ParseFrom(body));
                break;

            case MessageID.FriendRequestsResponse:
                Dispatch(FriendRequestsResponse.Parser.ParseFrom(body));
                break;

            case MessageID.ChatSendResponse:
                Dispatch(ChatSendResponse.Parser.ParseFrom(body));
                break;

            case MessageID.ChatHistoryResponse:
                Dispatch(ChatHistoryResponse.Parser.ParseFrom(body));
                break;

            case MessageID.ChatReceiveNotify:
                Dispatch(ChatReceiveNotify.Parser.ParseFrom(body));
                break;

            case MessageID.ChatUnreadSnapshotNotify:
                Dispatch(ChatUnreadSnapshotNotify.Parser.ParseFrom(body));
                break;

            case MessageID.WorldChatSendResponse:
                Dispatch(WorldChatSendResponse.Parser.ParseFrom(body));
                break;

            case MessageID.WorldChatHistoryResponse:
                Dispatch(WorldChatHistoryResponse.Parser.ParseFrom(body));
                break;

            case MessageID.WorldChatNotify:
                Dispatch(WorldChatNotify.Parser.ParseFrom(body));
                break;

            case MessageID.AgentChatResponse:
                Dispatch(AgentChatResponse.Parser.ParseFrom(body));
                break;

            case MessageID.AgentChatChunk:
                Dispatch(AgentChatChunk.Parser.ParseFrom(body));
                break;

            case MessageID.AgentBriefNotify:
                Dispatch(AgentBriefNotify.Parser.ParseFrom(body));
                break;

            case MessageID.AgentHistoryResponse:
                Dispatch(AgentHistoryResponse.Parser.ParseFrom(body));
                break;

            case MessageID.AgentProactiveNotify:
                Dispatch(AgentProactiveNotify.Parser.ParseFrom(body));
                break;

            case MessageID.ScoreSubmitResponse:
                Dispatch(ScoreSubmitResponse.Parser.ParseFrom(body));
                break;

            case MessageID.LeaderboardResponse:
                Dispatch(LeaderboardResponse.Parser.ParseFrom(body));
                break;

            case MessageID.ChallengeStartResponse:
                Dispatch(ChallengeStartResponse.Parser.ParseFrom(body));
                break;

            case MessageID.BagListResponse:
                Dispatch(BagListResponse.Parser.ParseFrom(body));
                break;

            case MessageID.BagUseResponse:
                Dispatch(BagUseResponse.Parser.ParseFrom(body));
                break;

            case MessageID.BagDiscardResponse:
                Dispatch(BagDiscardResponse.Parser.ParseFrom(body));
                break;

            case MessageID.BagTidyResponse:
                Dispatch(BagTidyResponse.Parser.ParseFrom(body));
                break;

            case MessageID.BagSwapResponse:
                Dispatch(BagSwapResponse.Parser.ParseFrom(body));
                break;

            case MessageID.BagGrantResponse:
                Dispatch(BagGrantResponse.Parser.ParseFrom(body));
                break;

            case MessageID.AchievementListResponse:
                Dispatch(AchievementListResponse.Parser.ParseFrom(body));
                break;

            case MessageID.AchievementClaimResponse:
                Dispatch(AchievementClaimResponse.Parser.ParseFrom(body));
                break;

            case MessageID.AchievementUnlockedNotify:
                Dispatch(AchievementUnlockedNotify.Parser.ParseFrom(body));
                break;

            case MessageID.EquipmentListResponse:
                Dispatch(EquipmentListResponse.Parser.ParseFrom(body));
                break;

            case MessageID.EquipResponse:
                Dispatch(EquipResponse.Parser.ParseFrom(body));
                break;

            case MessageID.GuidePullResponse:
                Dispatch(GuidePullResponse.Parser.ParseFrom(body));
                break;

            case MessageID.GuidePushResponse:
                Dispatch(GuidePushResponse.Parser.ParseFrom(body));
                break;

            case MessageID.AchievementProgressNotify:
                Dispatch(AchievementProgressNotify.Parser.ParseFrom(body));
                break;

            case MessageID.AchievementGmResponse:
                Dispatch(AchievementGmResponse.Parser.ParseFrom(body));
                break;

            case MessageID.MailListResponse:
                Dispatch(MailListResponse.Parser.ParseFrom(body));
                break;

            case MessageID.MailReadResponse:
                Dispatch(MailReadResponse.Parser.ParseFrom(body));
                break;

            case MessageID.MailClaimResponse:
                Dispatch(MailClaimResponse.Parser.ParseFrom(body));
                break;

            case MessageID.MailDeleteResponse:
                Dispatch(MailDeleteResponse.Parser.ParseFrom(body));
                break;

            case MessageID.MailGrantResponse:
                Dispatch(MailGrantResponse.Parser.ParseFrom(body));
                break;

            case MessageID.MailReceiveNotify:
                Dispatch(MailReceiveNotify.Parser.ParseFrom(body));
                break;

            case MessageID.NarrativeQuestSyncResponse:
                Dispatch(NarrativeQuestSyncResponse.Parser.ParseFrom(body));
                break;

            case MessageID.NarrativeQuestAcceptResponse:
                Dispatch(NarrativeQuestAcceptResponse.Parser.ParseFrom(body));
                break;

            case MessageID.NarrativeQuestSubmitResponse:
                Dispatch(NarrativeQuestSubmitResponse.Parser.ParseFrom(body));
                break;

            case MessageID.NarrativeQuestStateNotify:
                Dispatch(NarrativeQuestStateNotify.Parser.ParseFrom(body));
                break;

            case MessageID.NarrativeQuestAbandonResponse:
                Dispatch(NarrativeQuestAbandonResponse.Parser.ParseFrom(body));
                break;

            case MessageID.NarrativeQuestToastNotify:
                Dispatch(NarrativeQuestToastNotify.Parser.ParseFrom(body));
                break;

            case MessageID.ActivityListResponse:
                Dispatch(ActivityListResponse.Parser.ParseFrom(body));
                break;

            case MessageID.GachaDrawResponse:
                Dispatch(GachaDrawResponse.Parser.ParseFrom(body));
                break;

            case MessageID.GachaHistoryResponse:
                Dispatch(GachaHistoryResponse.Parser.ParseFrom(body));
                break;

            case MessageID.SignInListResponse:
                Dispatch(SignInListResponse.Parser.ParseFrom(body));
                break;

            case MessageID.SignInClaimResponse:
                Dispatch(SignInClaimResponse.Parser.ParseFrom(body));
                break;

            case MessageID.ActivityChangeNotify:
                Dispatch(ActivityChangeNotify.Parser.ParseFrom(body));
                break;

            case MessageID.ActivityGmRotateResponse:
                Dispatch(ActivityGmRotateResponse.Parser.ParseFrom(body));
                break;

            // ── 对战好友邀请（信令层）──
            case MessageID.BattleInviteResponse:
                Dispatch(BattleInviteResponse.Parser.ParseFrom(body));
                break;

            case MessageID.BattleInviteNotify:
                Dispatch(BattleInviteNotify.Parser.ParseFrom(body));
                break;

            case MessageID.BattleInviteRespondResponse:
                Dispatch(BattleInviteRespondResponse.Parser.ParseFrom(body));
                break;

            case MessageID.BattleInviteResultNotify:
                Dispatch(BattleInviteResultNotify.Parser.ParseFrom(body));
                break;

            case MessageID.BattleInviteCancelResponse:
                Dispatch(BattleInviteCancelResponse.Parser.ParseFrom(body));
                break;

            case MessageID.BattleInviteCancelledNotify:
                Dispatch(BattleInviteCancelledNotify.Parser.ParseFrom(body));
                break;

            case MessageID.SaveStatePullResponse:
                Dispatch(SaveStatePullResponse.Parser.ParseFrom(body));
                break;

            case MessageID.SaveStatePushResponse:
                Dispatch(SaveStatePushResponse.Parser.ParseFrom(body));
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

        /// <summary>
        /// 状态上报（任意线程可调用，安全转移到主线程）
        /// </summary>
        private void OnStatus(string message)
        {
            Debug.Log($"[Compete网络]{message}");
            statusQueue.Enqueue(message);
        }
    }
}
