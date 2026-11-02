// 命名空间：CompeteServer.Handler
// AI Agent（阿福）网关：客户端 ↔ Python Agent 服务（LangGraph :8898）之间的唯一传话人。
// 职责（计划文档 §2.3/§5.9）：会话身份绑定 → 长度校验 → 每玩家限频 → 组装玩家上下文快照 →
//   HTTP 转发（60s 超时不重试）→ 降级文案。
// 内容审核已按 2026-10-16 用户决策取消（计划 §11）：Agent 是私人助手，玩家文本原样直通；
//   私聊/世界频道的违禁词+贝叶斯管道不受影响（ChatHandler 仍走）。
// 安全模型：客户端消息不带玩家 ID（身份只认会话），Agent 服务只监听回环——双方都摸不到彼此的边界外。
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

using Common.Enum;
using Common.Network;

using CompeteServer.Database;
using CompeteServer.Manager;
using CompeteServer.Network;

namespace CompeteServer.Handler
{
    /// <summary>
    /// Agent 消息处理类
    /// 负责玩家与 AI 助手阿福的对话转发与历史回放
    /// </summary>
    public static class AgentHandler
    {
        /// <summary>玩家输入长度上限（与好友聊天同口径，防长文本刷 token）</summary>
        private const int MaxContentLength = 500;

        /// <summary>每玩家限频窗口：60 秒内最多 30 次对话（防刷 LLM 账单，计划 §4.4）</summary>
        private const int RateWindowSeconds = 60;
        private const int RateLimitPerWindow = 30;

        /// <summary>调 Python Agent 的 HTTP 超时（秒）：给 LLM 长回复+工具链留余量（计划 §5.9 A1）</summary>
        private const int HttpTimeoutSeconds = 60;

        /// <summary>
        /// 同时进行的 Agent 转发上限（全局闸门）。
        /// 为什么需要：Agent 单轮最长 55s，没有闸门时 1000 个玩家同时说话会把 1000 条长连接
        /// 一起压到 8898 与 LLM 上游；有闸门则排队，排不进来的快速降级而不是拖垮所有人。
        /// 取值依据：线上审计日志实测 p95 单轮 5.2s，1000 在线按 30s 一句约需 170 并发，默认 600 留 3 倍余量。
        /// 取值来源：CS_AGENT_MAX_CONCURRENCY；往上调的真正瓶颈是 LLM 上游配额与 Node 内存，不是网关线程。
        /// </summary>
        private static readonly int MaxConcurrentForwards = ServerConfig.AgentMaxConcurrency;

        /// <summary>等闸门的时长（ms）：超过就快速降级，不让玩家排到 60s 超时</summary>
        private const int ForwardGateWaitMs = 15_000;

        /// <summary>转发闸门（进程内全局，公平队列由 SemaphoreSlim 保证）</summary>
        private static readonly SemaphoreSlim forwardGate = new(MaxConcurrentForwards, MaxConcurrentForwards);


        /// <summary>Agent 服务不可达/超时/出错的统一降级文案（不重试，防双计费/双记忆）</summary>
        private const string UnavailableMessage = "守岸人暂时不在家，请稍后再试";

        /// <summary>每玩家最近请求时间戳（滑窗限频用，Stopwatch 单调时钟）</summary>
        private static readonly ConcurrentDictionary<int, ConcurrentQueue<long>> rateTicks = new();

        /// <summary>HTTP 客户端单例（复用连接池；Python 服务地址来自 ServerConfig）</summary>
        private static readonly HttpClient http = new(new SocketsHttpHandler
        {
            // 连接池上限略高于闸门：排队中的请求不应因为拿不到连接而失败
            MaxConnectionsPerServer = MaxConcurrentForwards + 100,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        })
        {
            Timeout = TimeSpan.FromSeconds(HttpTimeoutSeconds),
        };

        /// <summary>Phase 8（学 ZCode AUTH_TOKEN）：配置了共享令牌就给所有出站 Agent 调用带头。
        /// 加在客户端默认头上一处生效（stream/history/brief 全覆盖）；Python 侧 /agent/* 全端点校验。</summary>
        static AgentHandler()
        {
            string token = AgentToken.Shared;
            if (token.Length > 0)
            {
                http.DefaultRequestHeaders.Add("X-Agent-Token", token);
            }
        }

        /// <summary>
        /// Agent 聊天处理方法
        /// 链路：登录校验 → 长度校验 → 限频 → HTTP 转发 → 回执（内容审核已取消，2026-10-16 计划 §11）
        /// </summary>
        public static async Task HandleAgentChatAsync(ClientSession session, byte[] packet)
        {
            AgentChatRequest request = AgentChatRequest.Parser.ParseFrom(packet.AsSpan(4));

            // 未登录的会话不能跟阿福聊天
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.AgentChatResponse,
                    new AgentChatResponse { Ok = false, Error = "请先登录" });
                return;
            }

            int playerId = session.PlayerId;   // 身份来自登录时绑定的会话，客户端无法伪造
            string content = request.Content.Trim();

            if (content.Length == 0)
            {
                _ = session.Send(MessageID.AgentChatResponse,
                    new AgentChatResponse { Ok = false, Error = "消息内容不能为空" });
                return;
            }
            if (content.Length > MaxContentLength)
            {
                _ = session.Send(MessageID.AgentChatResponse,
                    new AgentChatResponse { Ok = false, Error = $"消息内容不能超过{MaxContentLength}字" });
                return;
            }

            // 内容审核已取消（2026-10-16 计划 §11）：文本原样直通，不在这里打码/拦截

            // 限频：LLM 按 token 计费，滑窗放行；超频拒绝
            if (!TryPassRateLimit(playerId, out int waitSeconds))
            {
                _ = session.Send(MessageID.AgentChatResponse,
                    new AgentChatResponse { Ok = false, Error = $"和守岸人聊天太快啦，请 {waitSeconds} 秒后再试" });
                return;
            }

            // 玩家上下文快照：让闲聊场景免一次工具调用（名字取账号表，在线恒真）
            (bool found, string name) = new AccountRepository().QueryPlayer(playerId);

            try
            {
                // 全局闸门：超时抢不到槽位就快速降级，不占着线程排到客户端 60s 超时
                if (!await forwardGate.WaitAsync(ForwardGateWaitMs))
                {
                    Console.WriteLine($"Agent 转发排队超时:{playerId}");
                    _ = session.Send(MessageID.AgentChatResponse,
                        new AgentChatResponse { Ok = false, Error = UnavailableMessage });
                    return;
                }

                try
                {
                    // 流式转发（Phase 5 打字机）：3103 分片逐段下发，末尾 3102 权威校准；
                    // 异常/超时统一降级文案，不自动重试（A1 契约：防双计费/双记忆）
                    string? error = await ForwardChatStreamingAsync(
                        session, playerId, content, found ? name : $"玩家{playerId}");
                    if (error != null)
                    {
                        Console.WriteLine($"Agent 对话失败:{playerId} | {error}");
                        _ = session.Send(MessageID.AgentChatResponse,
                            new AgentChatResponse { Ok = false, Error = error });
                    }
                }
                finally
                {
                    forwardGate.Release();
                }
            }
            catch (Exception exception)
            {
                // A1 降级契约：Python 挂了/超时 → 统一文案；不重试（防双计费/双记忆）
                Console.Error.WriteLine($"Agent 服务不可达:{playerId} [{exception.GetType().Name}] {exception.Message}");
                _ = session.Send(MessageID.AgentChatResponse,
                    new AgentChatResponse { Ok = false, Error = UnavailableMessage });
            }
        }

        /// <summary>
        /// 流式转发：POST /agent/chat/stream（ndjson 事件流）→ 逐事件转 3103 分片 → done 后补发 3102 终值
        /// </summary>
        /// <returns>成功返回 null（3102 已在内部发送）；失败返回给客户端展示的错误文案</returns>
        private static async Task<string?> ForwardChatStreamingAsync(
            ClientSession session, int playerId, string content, string displayName)
        {
            object payload = new
            {
                player_id = playerId,
                content,
                context = new { name = displayName, online = true },
            };

            using StringContent json = new(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            // ResponseHeadersRead：拿到响应头就返回，正文边到边读（否则 HttpClient 先整段缓冲，流式失效）
            using HttpRequestMessage request = new(
                HttpMethod.Post, $"{ServerConfig.AgentPythonBaseUrl}/agent/chat/stream")
            {
                Content = json,
            };
            using HttpResponseMessage response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using Stream stream = await response.Content.ReadAsStreamAsync();
            using StreamReader reader = new(stream, Encoding.UTF8);

            int seq = 0;
            string? reply = null;
            long elapsedMs = 0;
            string? error = null;
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                if (line.Length == 0) continue;
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                string type = root.TryGetProperty("type", out JsonElement typeElement)
                    ? typeElement.GetString() ?? string.Empty : string.Empty;

                switch (type)
                {
                    case "delta":
                        _ = session.Send(MessageID.AgentChatChunk, new AgentChatChunk
                        {
                            Seq = seq++,
                            Delta = root.TryGetProperty("text", out JsonElement textElement)
                                ? textElement.GetString() ?? string.Empty : string.Empty,
                        });
                        break;

                    case "reset":
                        // 工具调用轮的前导文本作废：客户端清空已累计的流式文字
                        _ = session.Send(MessageID.AgentChatChunk,
                            new AgentChatChunk { Seq = seq++, Reset = true });
                        break;

                    case "done":
                        reply = root.TryGetProperty("reply", out JsonElement replyElement)
                            ? replyElement.GetString() ?? string.Empty : string.Empty;
                        if (root.TryGetProperty("elapsed_ms", out JsonElement elapsedElement)
                            && elapsedElement.TryGetInt64(out long elapsed))
                        {
                            elapsedMs = elapsed;
                        }
                        break;

                    case "error":
                        error = root.TryGetProperty("error", out JsonElement errorElement)
                            ? errorElement.GetString() ?? UnavailableMessage : UnavailableMessage;
                        break;
                }
            }

            if (error != null) return error;
            if (reply == null) return UnavailableMessage;   // 流中断：没等到 done 事件

            Console.WriteLine($"Agent 对话完成:{playerId} | {elapsedMs}ms | {reply.Length}字 | 流式分片{seq}");
            _ = session.Send(MessageID.AgentChatResponse, new AgentChatResponse
            {
                Ok = true,
                Reply = reply,
                ElapsedMs = elapsedMs,
            });
            return null;
        }

        /// <summary>
        /// Agent 历史回放处理方法
        /// 打开 AgentPanel 时拉取与阿福的最近对话（跨会话记忆的可见入口）
        /// </summary>
        public static async Task HandleAgentHistoryAsync(ClientSession session, byte[] packet)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.AgentHistoryResponse,
                    new AgentHistoryResponse { Ok = false });
                return;
            }

            AgentHistoryRequest request = AgentHistoryRequest.Parser.ParseFrom(packet.AsSpan(4));

            try
            {
                // before_ms>0=上拉取更早一页（客户端前置插入）；0=首页拉最近 N 条
                object payload = new
                {
                    player_id = session.PlayerId,
                    limit = request.Limit <= 0 ? 20 : request.Limit,
                    before_ms = request.BeforeMs,
                };
                using StringContent json = new(
                    JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await http
                    .PostAsync($"{ServerConfig.AgentPythonBaseUrl}/agent/history", json);
                response.EnsureSuccessStatusCode();

                using JsonDocument document = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                JsonElement root = document.RootElement;

                AgentHistoryResponse history = new() { Ok = true };
                if (root.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in items.EnumerateArray())
                    {
                        long createdMs = 0;
                        if (item.TryGetProperty("ts", out JsonElement ts) && ts.TryGetInt64(out long created))
                        {
                            createdMs = created;
                        }

                        history.Items.Add(new AgentChatMessage
                        {
                            Role = item.TryGetProperty("role", out JsonElement role) && role.GetInt32() == 1 ? 1 : 0,
                            Content = item.TryGetProperty("content", out JsonElement text) ? text.GetString() ?? string.Empty : string.Empty,
                            CreatedMs = createdMs,
                        });
                    }
                }
                if (root.TryGetProperty("has_more", out JsonElement hasMore)
                    && hasMore.ValueKind == JsonValueKind.True)
                {
                    history.HasMore = true;
                }

                Console.WriteLine($"Agent 历史返回:{session.PlayerId} | {history.Items.Count}条");
                _ = session.Send(MessageID.AgentHistoryResponse, history);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Agent 历史拉取失败:{session.PlayerId} [{exception.GetType().Name}] {exception.Message}");
                _ = session.Send(MessageID.AgentHistoryResponse, new AgentHistoryResponse { Ok = false });
            }
        }

        /// <summary>
        /// 登录主动简报（Phase 5 ②）：问 Python 要一条"阿福上线问好+待办汇总"，推 3104 给客户端。
        /// 确定性文案（Python 内部组装，零 LLM 成本）；后台执行不阻塞登录回执，失败只记日志。
        /// </summary>
        public static void PushLoginBrief(ClientSession session, int playerId)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    object payload = new { player_id = playerId };
                    using StringContent json = new(
                        JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                    using HttpResponseMessage response = await http.PostAsync(
                        $"{ServerConfig.AgentPythonBaseUrl}/agent/brief", json);
                    response.EnsureSuccessStatusCode();

                    using JsonDocument document = JsonDocument.Parse(
                        await response.Content.ReadAsStringAsync());
                    JsonElement root = document.RootElement;
                    if (!root.TryGetProperty("ok", out JsonElement okElement) || !okElement.GetBoolean()) return;
                    if (!root.TryGetProperty("brief", out JsonElement briefElement)) return;
                    string brief = briefElement.GetString() ?? string.Empty;
                    if (brief.Length == 0) return;

                    long createdMs = 0;
                    if (root.TryGetProperty("created_ms", out JsonElement createdElement)
                        && createdElement.TryGetInt64(out long created))
                    {
                        createdMs = created;
                    }

                    Console.WriteLine($"Agent 简报已推:{playerId} | {brief.Length}字");
                    _ = session.Send(MessageID.AgentBriefNotify, new AgentBriefNotify
                    {
                        Content = brief,
                        CreatedMs = createdMs,
                    });
                }
                catch (Exception exception)
                {
                    // 简报是锦上添花：Python 没起来/内部 API 挂了都只记日志，不影响登录
                    Console.Error.WriteLine($"Agent 简报推送失败:{playerId} [{exception.GetType().Name}] {exception.Message}");
                }
            });
        }

        /// <summary>
        /// 滑窗限频判定：60 秒窗口内不足 RateLimitPerWindow 次则放行并记录
        /// </summary>
        /// <param name="playerId">玩家ID</param>
        /// <param name="waitSeconds">被限时还需等待的秒数（放行时为 0）</param>
        private static bool TryPassRateLimit(int playerId, out int waitSeconds)
        {
            long now = Stopwatch.GetTimestamp();
            double windowTicks = RateWindowSeconds * Stopwatch.Frequency;
            waitSeconds = 0;

            ConcurrentQueue<long> queue = rateTicks.GetOrAdd(playerId, _ => new ConcurrentQueue<long>());

            lock (queue)
            {
                // 淘汰滑出窗口的旧记录
                while (queue.TryPeek(out long tick) && now - tick >= windowTicks)
                {
                    queue.TryDequeue(out _);
                }

                if (queue.Count >= RateLimitPerWindow)
                {
                    queue.TryPeek(out long oldest);
                    double elapsedSeconds = (now - oldest) / (double)Stopwatch.Frequency;
                    waitSeconds = Math.Max(1, (int)Math.Ceiling(RateWindowSeconds - elapsedSeconds));
                    return false;
                }

                queue.Enqueue(now);
                return true;
            }
        }
    }
}
