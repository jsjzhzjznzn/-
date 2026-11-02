// 命名空间：CompeteServer.Manager
// 阿福主动提醒服务（悬浮球 · 一个 Agent 两个用法，2026-10-17 设计文档）：
// 服务端检测到"邮件附件未领取"等事件 → 调 Python /agent/proactive 让同一个阿福（共用 LangGraph
// 主图与玩家已选说话风格）主动说一句话 → 推 3107 AgentProactiveNotify 给在线会话。
// 被动问答走 3101（AgentHandler），主动关怀走本服务——两个入口共用同一个 Agent 大脑。
//
// 触发源（v1 只做邮件附件未领取，kind 字段已预留扩展）：
//   ① 到达触发：GM 发信 / 任务奖励满包转存 / 背包满转存——带附件的邮件落库且收件人在线（钩子点在三个发信处）
//   ② 巡检触发：后台线程定期查在线玩家"还有几封带附件没领"，一直没领就再温柔提醒一次
//
// 防打扰三道闸（常量集中在此，E2E 可用环境变量缩短巡检间隔）：
//   到达冷却 5 分钟 + 巡检间隔/提醒节奏 3 分钟（2026-10-17 用户定稿）且每次登录会话封顶 2 次 + 全服并发闸 3。
//
// 失败语义：Python 不可达/超时只记日志不推（下轮巡检天然重试），与登录简报同哲学——锦上添花不阻塞主流程；
// Python 侧对 LLM 抖动有模板兜底（那边保证"提醒不丢"），本层只保证"不重试刷账单"。
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;

using Common.Enum;
using Common.Network;

using CompeteServer.Database;
using CompeteServer.Network;

namespace CompeteServer.Manager
{
    /// <summary>
    /// 阿福主动提醒服务类
    /// 用途：事件触发 → 频控 → 调 Python /agent/proactive 生成文案 → 推 3107 给在线会话
    /// </summary>
    public static class AgentReminderService
    {
        /// <summary>提醒类型：邮件附件未领取（与 proto AgentProactiveNotify.kind 注释对齐；后续预留 2任务可提交 3保底临近 4好友申请）</summary>
        public const int KindMailUnclaimed = 1;

        // ──────────────────────────────
        // 频控常量（防打扰三道闸）
        // ──────────────────────────────

        /// <summary>到达提醒冷却：同一玩家两条"到达提醒"至少间隔（毫秒）</summary>
        private const int ArrivalCooldownMs = 5 * 60 * 1000;

        /// <summary>
        /// 巡检提醒与"该玩家最近一次邮件类提醒"的最小间隔（毫秒）：默认 3 分钟（2026-10-17 用户定稿）；
        /// 环境变量 CS_AGENT_REMINDER_GAP_SEC 可覆盖（E2E 缩短用，与巡检间隔是两道独立的闸）
        /// </summary>
        private static readonly int SweepRemindGapMs = ReadSeconds("CS_AGENT_REMINDER_GAP_SEC", 3 * 60) * 1000;

        /// <summary>每次登录会话内巡检提醒封顶（超过就不再"催领"）</summary>
        private const int MaxSweepRemindsPerSession = 2;

        /// <summary>全服同时在途的 proactive LLM 调用上限（防百人同刻群发把账单打爆；超发排队）</summary>
        private const int GlobalConcurrency = 3;

        /// <summary>
        /// 巡检 tick 间隔（毫秒）：默认 3 分钟（2026-10-17 用户定稿"自定义间隔=3分钟"，提醒节奏跟随本间隔）；
        /// 环境变量 CS_AGENT_REMINDER_SWEEP_SEC 可覆盖（E2E 缩短用）
        /// </summary>
        private static readonly int SweepIntervalMs = ReadSeconds("CS_AGENT_REMINDER_SWEEP_SEC", 3 * 60) * 1000;

        /// <summary>调 /agent/proactive 的 HTTP 超时：给 LLM 生成留余量，但别让巡检线程背上 60 秒大包袱</summary>
        private const int HttpTimeoutSeconds = 20;

        /// <summary>HTTP 客户端单例（复用连接池；共享令牌与 AgentHandler 同源——全部出站 Agent 调用一道闸）</summary>
        private static readonly HttpClient http = new()
        {
            Timeout = TimeSpan.FromSeconds(HttpTimeoutSeconds),
        };

        /// <summary>每玩家提醒账本（playerId → 状态）；登录时重置会话计数，条目常驻（量级=历史玩家数，可忽略）</summary>
        private static readonly ConcurrentDictionary<int, PlayerState> states = new();

        /// <summary>全服并发闸：同时最多 GlobalConcurrency 个 proactive 在途</summary>
        private static readonly SemaphoreSlim gate = new(GlobalConcurrency, GlobalConcurrency);

        /// <summary>巡检线程（后台，随进程退出）</summary>
        private static Thread? sweepThread;

        /// <summary>上次巡检执行时刻（UTC 毫秒）；0=启动后首轮立即跑一次（那时还没人在线，无副作用）</summary>
        private static long lastSweepMs;

        /// <summary>每玩家提醒状态（锁内读写；LastMailRemindMs 到达/巡检共用一个账，避免两类提醒叠着轰炸）</summary>
        private class PlayerState
        {
            /// <summary>最近一次邮件类提醒（到达或巡检）的时刻</summary>
            public long LastMailRemindMs;

            /// <summary>本次登录会话内巡检已提醒次数</summary>
            public int SweepRemindCount;
        }

        static AgentReminderService()
        {
            string token = AgentToken.Shared;
            if (token.Length > 0)
            {
                http.DefaultRequestHeaders.Add("X-Agent-Token", token);
            }
        }

        /// <summary>
        /// 启动巡检线程的方法
        /// 用途：Program.cs 启动时调用；幂等（重复调用不会起第二个线程）
        /// </summary>
        public static void Start()
        {
            if (sweepThread != null) return;
            Thread thread = new(SweepLoop) { IsBackground = true, Name = "AgentReminder" };
            Interlocked.Exchange(ref sweepThread, thread);
            thread.Start();
            Console.WriteLine($"阿福主动提醒服务就绪（巡检间隔 {SweepIntervalMs / 1000}s · 到达冷却 {ArrivalCooldownMs / 60000}分钟 · 并发闸 {GlobalConcurrency}）");
        }

        /// <summary>
        /// 玩家登录成功时重置会话级计数的方法
        /// 用途：MessageHandler 登录成功处调用——巡检封顶按"每次登录会话"计；冷却账跨会话保留（刚提醒过就重登不该再提醒）
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：登录玩家ID</param>
        public static void OnPlayerLogin(int playerId)
        {
            if (playerId <= 0) return;
            PlayerState state = states.GetOrAdd(playerId, _ => new PlayerState());
            lock (state)
            {
                state.SweepRemindCount = 0;
            }
        }

        /// <summary>
        /// 到达触发：一封带附件的邮件刚落库
        /// 用途：GM 发信 / 任务奖励满包转存 / 背包满转存三个钩子点调用；冷却与在线判定都在这里
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：收件玩家ID</param>
        /// <param name="title">参数名：title，类型：string，用途：邮件标题（作为事件数据内联给 LLM）</param>
        /// <param name="itemCount">参数名：itemCount，类型：int，用途：附件物品种数（0=纯文本邮件，不提醒）</param>
        public static void OnMailArrived(int playerId, string title, int itemCount)
        {
            if (itemCount <= 0 || playerId <= 0) return;
            if (SessionManager.Instance.GetSessionByPlayerId(playerId) == null) return;   // 不在线：登录简报兜底汇报，不主动打扰

            PlayerState state = states.GetOrAdd(playerId, _ => new PlayerState());
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (state)
            {
                if (now - state.LastMailRemindMs < ArrivalCooldownMs) return;   // 到达冷却：连发两封只提醒第一条
                state.LastMailRemindMs = now;
            }

            Console.WriteLine($"[AgentReminder] 到达触发 player={playerId} 邮件[{title}]");
            _ = Task.Run(() => FireAsync(playerId, title, 0, 0));
        }

        // ──────────────────────────────
        // 巡检（"一直没领就再提醒一次"）
        // ──────────────────────────────

        /// <summary>巡检主循环：每秒 tick 一次看是否到巡检间隔；异常不退出线程（只记日志）。</summary>
        private static void SweepLoop()
        {
            while (true)
            {
                Thread.Sleep(1000);
                try
                {
                    SweepOnce();
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[AgentReminder] 巡检异常 [{exception.GetType().Name}]: {exception.Message}");
                }
            }
        }

        /// <summary>
        /// 单轮巡检：到间隔就对每个在线玩家查"带附件未领封数"，有且距上次提醒够久、会话没超封顶 → 触发
        /// 查询口径与 InternalAgentApiServer.BuildMailSummary 同源（GetMails 已过滤删除与过期）
        /// </summary>
        private static void SweepOnce()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - lastSweepMs < SweepIntervalMs) return;
            lastSweepMs = now;

            foreach (int playerId in SessionManager.Instance.GetOnlinePlayerIds())
            {
                PlayerState state = states.GetOrAdd(playerId, _ => new PlayerState());

                // 会话封顶与间隔闸：先原子占位（过了闸才查库，防"查完库发现超顶"的白查）
                lock (state)
                {
                    if (state.SweepRemindCount >= MaxSweepRemindsPerSession) continue;
                    if (now - state.LastMailRemindMs < SweepRemindGapMs) continue;
                }

                int claimable = 0;
                int unread = 0;
                foreach (MailRepository.MailData mail in new MailRepository().GetMails(playerId))
                {
                    if (!mail.Read) unread++;
                    if (!mail.Claimed && mail.Items.Count > 0) claimable++;
                }
                if (claimable <= 0) continue;

                lock (state)
                {
                    state.SweepRemindCount++;
                    state.LastMailRemindMs = now;
                }

                Console.WriteLine($"[AgentReminder] 巡检触发 player={playerId} 未领附件{claimable}封 未读{unread}封（会话第{state.SweepRemindCount}次）");
                _ = Task.Run(() => FireAsync(playerId, string.Empty, claimable, unread));
            }
        }

        // ──────────────────────────────
        // 触发 → Python → 推送
        // ──────────────────────────────

        /// <summary>
        /// 生成并推送一条主动提醒的方法
        /// 链路：并发闸 → 会话还在 → POST /agent/proactive（LLM 生成+归档在 Python 侧）→ 推 3107
        /// </summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：目标玩家ID</param>
        /// <param name="title">参数名：title，类型：string，用途：到达触发的邮件标题（巡检触发传空，Python 按"还有N封没领"措辞）</param>
        /// <param name="claimable">参数名：claimable，类型：int，用途：带附件未领封数（巡检触发时有值）</param>
        /// <param name="unread">参数名：unread，类型：int，用途：未读封数（巡检触发时有值）</param>
        private static async Task FireAsync(int playerId, string title, int claimable, int unread)
        {
            await gate.WaitAsync();
            try
            {
                ClientSession? session = SessionManager.Instance.GetSessionByPlayerId(playerId);
                if (session == null) return;   // 触发后中途掉线：不推（下次登录简报兜底）

                object payload = new
                {
                    player_id = playerId,
                    kind = KindMailUnclaimed,
                    data = new { title, claimable, unread },
                };
                using StringContent json = new(
                    JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await http.PostAsync(
                    $"{ServerConfig.AgentPythonBaseUrl}/agent/proactive", json);
                response.EnsureSuccessStatusCode();

                using JsonDocument document = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync());
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("ok", out JsonElement okElement) || !okElement.GetBoolean()) return;

                string message = root.TryGetProperty("message", out JsonElement messageElement)
                    ? messageElement.GetString() ?? string.Empty : string.Empty;
                if (message.Length == 0) return;

                long createdMs = 0;
                if (root.TryGetProperty("created_ms", out JsonElement createdElement)
                    && createdElement.TryGetInt64(out long created))
                {
                    createdMs = created;
                }

                Console.WriteLine($"Agent 主动提醒已推:{playerId} kind={KindMailUnclaimed} | {message.Length}字 | {message}");
                _ = session.Send(MessageID.AgentProactiveNotify, new AgentProactiveNotify
                {
                    Kind = KindMailUnclaimed,
                    Content = message,
                    CreatedMs = createdMs,
                });
            }
            catch (Exception exception)
            {
                // Python 不可达/超时/异常：不推不重试（防双计费/刷日志），下轮巡检天然重试
                Console.Error.WriteLine($"Agent 主动提醒失败:{playerId} [{exception.GetType().Name}] {exception.Message}");
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>读环境变量秒数：合法正整数则用之，否则回退默认值（非法值静默回退，别让巡检线程起不来）。</summary>
        private static int ReadSeconds(string name, int fallbackSeconds)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            return int.TryParse(value?.Trim(), out int seconds) && seconds > 0
                ? seconds
                : fallbackSeconds;
        }
    }
}
