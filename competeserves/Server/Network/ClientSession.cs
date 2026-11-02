using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;

using Common.Enum;
using Common.Network;

using Google.Protobuf;

using CompeteServer.Handler;
using CompeteServer.Manager;

namespace CompeteServer.Network
{
    /// <summary>
    /// 客户端会话类
    /// 一个TCP连接对应一个ClientSession实例，负责处理与该客户端的通信
    /// 帧结构：[4字节小端长度头][4字节消息ID][protobuf消息体]
    /// </summary>
    public class ClientSession
    {
        /// <summary>
        /// 空闲超时（秒）：超过该时长未收到任何完整帧即视为僵尸连接并回收
        /// 在线客户端由心跳帧（PingRequest）维持活性；TestClient 等旧工具超时后重连即可
        /// </summary>
        private const int IdleTimeoutSeconds = 60;

        /// <summary>接收循环的取消源：每收到完整帧重置一次计时，超时取消让 ReadAsync 抛异常退出</summary>
        private readonly CancellationTokenSource recvCts = new();

        /// <summary>
        /// 会话内待处理帧的有界队列容量。
        /// 读循环只拆帧入队，处理由本会话唯一的 worker 串行消费：
        /// 慢 handler 只拖慢自己会话的队列，不阻塞收包；队列满即丢帧，兼作 per-session 背压阀
        /// </summary>
        private const int MaxPacketBacklog = 100;

        /// <summary>会话内帧队列（元素为不含长度头的完整帧体），DropWrite：写满即拒，不阻塞读循环</summary>
        private readonly Channel<byte[]> packetChannel = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(MaxPacketBacklog)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = true
            });

        /// <summary>本会话唯一的顺序处理 worker：串行消费 packetChannel，保帧序、防同会话并发</summary>
        private Task processWorker;

        /// <summary>
        /// 每会话请求速率上限（个/秒，长期均值）。挡的是"海量小帧"型脚本：
        /// 单帧 1MB 上界只拦大帧，拦不住 100 个脚本各发 1000 小帧/秒 = 10 万帧/秒打进 handler 与 DB
        /// </summary>
        private const int RequestRatePerSecond = 20;

        /// <summary>令牌桶容量（突发额度）：开一个界面连发几个请求属正常行为，不应误伤</summary>
        private const int RequestBurstCapacity = 40;

        /// <summary>
        /// 当前令牌数。仅读循环单线程顺序访问（一帧一次判定，await 之间不并发），故不加锁；
        /// 初值给满桶：连接建立瞬间允许一次突发，符合"新会话立刻拉列表/背包"的真实行为
        /// </summary>
        private double tokens = RequestBurstCapacity;

        /// <summary>上次回填令牌的时间戳（Stopwatch 单调时钟，不受系统改时间影响）</summary>
        private long lastRefillTimestamp = Stopwatch.GetTimestamp();

        /// <summary>累计限流拒绝数（会被其它线程读，故用 Interlocked 更新）</summary>
        private long rateRejectedCount;

        /// <summary>关闭幂等标记（0=未关闭，1=已关闭），防止读循环退出与发送失败重复清理</summary>
        private int closed;

        /// <summary>被顶号标记（0=未被顶，1=已被同账号的新登录顶下）</summary>
        private int evicted;

        /// <summary>踢线通知的发送宽限期（毫秒）：活客户端几毫秒写完，僵尸连接最多等这么久就强关</summary>
        private const int KickNotifyGraceMs = 2000;

        /// <summary>
        /// TCP客户端对象
        /// 类型：TcpClient
        /// 用途：底层的TCP连接对象，用于与客户端通信
        /// </summary>
        private TcpClient client;

        /// <summary>
        /// 网络数据流
        /// 类型：NetworkStream
        /// 用途：用于读写网络数据的流对象
        /// </summary>
        private NetworkStream stream;

        /// <summary>
        /// 发送锁
        /// 类型：SemaphoreSlim
        /// 用途：确保数据发送操作的线程安全，限制并发数为1
        /// </summary>
        private readonly SemaphoreSlim sendLock = new(1, 1);

        /// <summary>
        /// 会话ID
        /// 类型：int
        /// 用途：当前会话的唯一标识ID，只读
        /// </summary>
        public int SessionID { get; private set; }

        /// <summary>
        /// 玩家ID
        /// 类型：int
        /// 用途：登录成功后绑定的账号唯一标识ID
        /// </summary>
        public int PlayerId { get; set; }

        /// <summary>
        /// 构造函数 - 创建客户端会话实例
        /// </summary>
        /// <param name="client">参数名：client，类型：TcpClient，用途：底层的TCP连接对象</param>
        public ClientSession(TcpClient client)
        {
            this.client = client;
            stream = client.GetStream();
            SessionID = GetHashCode();
        }

        /// <summary>
        /// 启动会话的方法
        /// 输出日志并开始异步接收循环与顺序处理 worker
        /// </summary>
        public void Start()
        {
            Console.WriteLine($"Session启动:{SessionID}");
            processWorker = ProcessLoop();
            _ = ReceiveLoop();
        }

        /// <summary>
        /// 异步接收循环方法
        /// 持续从网络流中读取完整数据包并入会话队列，不在此线程做业务处理（4字节长度前缀解决粘包/半包）
        /// </summary>
        private async Task ReceiveLoop()
        {
            try
            {
                recvCts.CancelAfter(TimeSpan.FromSeconds(IdleTimeoutSeconds));
                while (true)
                {
                    byte[] header = new byte[4];
                    if (!await ReadExactly(header, 4)) break;
                    int length = BitConverter.ToInt32(header, 0);
                    // 帧体至少含4字节消息ID，1~3 为坏帧；上界防内存耗尽
                    if (length < 4 || length > 1024 * 1024)
                        throw new InvalidDataException($"非法数据包长度:{length}");
                    byte[] data = new byte[length];
                    if (!await ReadExactly(data, length)) break;
                    // 收到完整帧即重置空闲计时（TryReset 仅在未取消时成功；已取消则读操作已抛出，退出循环）
                    if (recvCts.TryReset())
                    {
                        recvCts.CancelAfter(TimeSpan.FromSeconds(IdleTimeoutSeconds));
                    }
                    // 令牌桶先于入队判定：超速帧不进队列，直接保护 worker 与 DB
                    if (!TryConsumeToken()) continue;

                    // 只入队不处理：慢 handler 只影响本会话队列深度，读循环持续收包、超时计时持续刷新
                    // DropWrite 下 TryWrite 恒返回 false 表示积压超上限，丢帧并由客户端超时/重试兜底
                    if (!packetChannel.Writer.TryWrite(data))
                    {
                        Console.WriteLine($"Session队列已满({MaxPacketBacklog})，丢弃1帧:{SessionID}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"Session空闲超时({IdleTimeoutSeconds}s无数据):{SessionID}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Session异常:{e.Message}");
            }
            Close();
        }

        /// <summary>
        /// 令牌桶取令牌：按距上次判定的时间连续回填（无需定时器、无后台线程），无令牌即判超速
        /// 仅由本会话读循环单线程调用，字段不加锁；拒绝计数用 Interlocked 因其会被其它线程读取
        /// </summary>
        /// <returns>返回值类型：bool，用途：true=放行，false=超出速率上限应丢弃该帧</returns>
        private bool TryConsumeToken()
        {
            long now = Stopwatch.GetTimestamp();
            double elapsedSeconds = (now - lastRefillTimestamp) / (double)Stopwatch.Frequency;
            lastRefillTimestamp = now;

            tokens = Math.Min(RequestBurstCapacity, tokens + elapsedSeconds * RequestRatePerSecond);
            if (tokens < 1.0)
            {
                long total = Interlocked.Increment(ref rateRejectedCount);
                // 采样打日志：被限流的往往是脚本，逐条打印等于把"同步控制台成为瓶颈"这个问题重新请回来
                if (total == 1 || total % 100 == 0)
                {
                    Console.WriteLine($"Session请求超限(> {RequestRatePerSecond}/s) 累计拒绝{total}帧:{SessionID}");
                }
                return false;
            }

            tokens -= 1.0;
            return true;
        }

        /// <summary>
        /// 本会话累计被限流拒绝的帧数
        /// 类型：long
        /// 用途：供巡检/风控识别异常账号，长期非零的会话即脚本或客户端 bug
        /// </summary>
        public long RateRejectedCount => Interlocked.Read(ref rateRejectedCount);

        /// <summary>
        /// 标记本会话已被同账号的新登录顶下（由 SessionManager 在锁内调用）
        /// 只声明"我不再负责在线表清理"，身份字段 PlayerId 保持原值——
        /// handler 在顶部守卫之后还会反复读 session.PlayerId 落库，降级为 0 会写出幽灵玩家数据
        /// </summary>
        internal void MarkEvicted() => Interlocked.Exchange(ref evicted, 1);

        /// <summary>是否已被顶号（跨线程可见）</summary>
        private bool IsEvicted => Volatile.Read(ref evicted) != 0;

        /// <summary>
        /// 会话顺序处理 worker：单消费者按到达顺序取帧执行 HandlePacket
        /// 会话内串行是不变量——禁止在此并发派发，否则同玩家"读后写" handler（查余额→扣费）会出现双花
        /// </summary>
        private async Task ProcessLoop()
        {
            try
            {
                await foreach (byte[] data in packetChannel.Reader.ReadAllAsync(recvCts.Token))
                {
                    // 已被顶号：队列里剩下的最多 100 帧不再执行（身份已不属于本连接，继续写库等于替新会话抢操作权）
                    if (IsEvicted) break;

                    try
                    {
                        // await 只释放线程，不改变顺序：本会话仍是单消费者，上一帧处理完才取下帧
                        await HandlePacketAsync(data);
                    }
                    catch (Exception e)
                    {
                        // worker 必须活着：单帧异常不得终止本会话的处理循环
                        Console.WriteLine($"Session处理帧异常:{SessionID} {e.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 会话关闭（超时/断线/异常）随 recvCts 退出，属正常收尾
            }
            catch (Exception e)
            {
                Console.WriteLine($"Session处理循环异常:{SessionID} {e.Message}");
            }
        }

        /// <summary>
        /// 精确读取指定字节数的方法
        /// 确保从网络流中读取到足够的数据
        /// </summary>
        /// <param name="buffer">参数名：buffer，类型：byte[]，用途：存储读取数据的缓冲区</param>
        /// <param name="length">参数名：length，类型：int，用途：需要读取的字节数</param>
        /// <returns>返回值类型：Task&lt;bool&gt;，用途：读取成功返回true，连接断开返回false</returns>
        private async Task<bool> ReadExactly(byte[] buffer, int length)
        {
            int offset = 0;
            while (offset < length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), recvCts.Token);
                if (read <= 0) return false;
                offset += read;
            }
            return true;
        }

        /// <summary>
        /// 数据包处理方法
        /// 解析消息ID与protobuf消息体，并交由MessageHandler处理
        /// 消息体以 Span 切片直接传给解析，不再拷贝出独立 byte[]（每帧少一次分配+一次 memcpy）
        /// </summary>
        /// <param name="data">参数名：data，类型：byte[]，用途：收到的原始数据包字节数组（不含长度头）</param>
        private async Task HandlePacketAsync(byte[] data)
        {
            // 帧结构：[4字节消息ID][protobuf消息体]
            int messageId = BitConverter.ToInt32(data, 0);

            try
            {
                // 心跳包不逐条打日志，避免在线人多时刷屏
                if (messageId != (int)MessageID.PingRequest)
                {
                    Console.WriteLine($"收到消息:{messageId}");
                }
                await MessageHandler.Handle(this, messageId, data);
            }
            catch (Exception exception)
            {
                // 单个消息处理失败不应销毁玩家的整个 TCP 会话。
                Console.WriteLine(
                    $"消息处理失败 ID={messageId} [{exception.GetType().Name}]: {exception.Message}");
            }
        }

        /// <summary>
        /// 发送字节数据的方法
        /// 在数据前附加4字节长度头并异步发送
        /// </summary>
        /// <param name="data">参数名：data，类型：byte[]，用途：要发送的字节数据（不含长度头）</param>
        public async Task Send(byte[] data)
        {
            await sendLock.WaitAsync();
            try
            {
                if (stream == null)
                {
                    Console.WriteLine($"发送被忽略(流已关闭):{SessionID}");
                    return;
                }
                byte[] header = BitConverter.GetBytes(data.Length);
                await stream.WriteAsync(header, recvCts.Token);
                await stream.WriteAsync(data, recvCts.Token);
            }
            catch (Exception e)
            {
                // 发送失败说明连接已不可用：记录并关闭会话，避免调用方 fire-and-forget 吞掉后静默泄漏
                Console.WriteLine($"发送失败 Session={SessionID}: {e.Message}");
                Close();
            }
            finally
            {
                sendLock.Release();
            }
        }

        /// <summary>
        /// 发送protobuf消息的方法
        /// 组装[消息ID+消息体]后走统一的长度前缀发送路径
        /// </summary>
        /// <param name="id">参数名：id，类型：MessageID，用途：protobuf消息ID枚举值</param>
        /// <param name="message">参数名：message，类型：IMessage，用途：protobuf消息对象</param>
        public Task Send(MessageID id, IMessage message)
        {
            byte[] body = message.ToByteArray();
            byte[] payload = new byte[4 + body.Length];
            BitConverter.GetBytes((int)id).CopyTo(payload, 0);
            Buffer.BlockCopy(body, 0, payload, 4, body.Length);
            return Send(payload);
        }

        /// <summary>
        /// 顶号踢人方法：先尽力把 KickoutNotify 写完，再关闭本会话
        /// 调用方必须在全局锁之外调用（Send 会在调用线程同步序列化， uncontended 时还会同步续跑写流）
        /// </summary>
        /// <param name="reason">参数名：reason，类型：string，用途：客户端可直接展示的踢线原因</param>
        public void KickOut(string reason)
        {
            _ = KickAsync(reason);
        }

        /// <summary>
        /// 推送踢线通知并关闭会话
        /// 顺序不能反：Close 里的 recvCts.Cancel() 会取消在途的 WriteAsync，先关等于通知丢包。
        /// WhenAny 的宽限期是防僵尸连接——对端彻底不可达时写会一直挂着，否则 Close 要拖到 60s 空闲超时
        /// </summary>
        private async Task KickAsync(string reason)
        {
            try
            {
                await Task.WhenAny(
                    Send(MessageID.KickoutNotify, new KickoutNotify { Reason = reason }),
                    Task.Delay(KickNotifyGraceMs));
            }
            finally
            {
                Close();
            }
        }

        /// <summary>
        /// 关闭会话的方法
        /// 清理资源并从管理器中移除
        /// </summary>
        private void Close()
        {
            // 幂等：读循环退出与发送失败都可能触发关闭
            if (Interlocked.Exchange(ref closed, 1) != 0) return;

            Console.WriteLine($"客户端离线:{SessionID}");

            recvCts.Cancel();

            // 先封写端再取消：worker 若在等帧会随 recvCts 退出；若正有积压则很快排空自然结束。
            // worker 不持有非资源句柄，随会话交给 GC，Close 保持同步不阻塞（读循环的 finally 语义）
            packetChannel.Writer.TryComplete();

            SessionManager.Instance.Remove(this);

            // 已登录的玩家：同步从在线列表移除（聊天/好友系统的在线状态以此为准）。
            // 被顶号时必须跳过：RemovePlayer 只按 playerId 删、不校验会话，而在线记录已被新会话
            // AddPlayer 覆盖，这里再删就把【新会话】的记录抹掉 → 新玩家连着却不在线，好友 online 与聊天推送全错
            if (PlayerId > 0 && !IsEvicted)
            {
                PlayerManager.Instance.RemovePlayer(PlayerId);

                // 对战邀请跟着会话走：房主掉线要通知被邀请方别再连，被邀请方掉线要告诉房主
                BattleHandler.OnPlayerDisconnected(PlayerId);
            }

            stream?.Close();
            client?.Close();
            // sendLock 不再 Dispose：并发的 Send 可能在 Release 前等待它，Dispose 会让其抛 ObjectDisposedException；
            // 该 SemaphoreSlim 不持有非资源句柄，随会话一起交给 GC 回收即可
        }
    }
}
