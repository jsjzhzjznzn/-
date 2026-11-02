// 命名空间：CompeteServer.Network
// Agent 内部数据 API：给 Python Agent 服务（LangGraph 工具）回查玩家真实数据的只读 HTTP 端点。
// 端口 8897（ServerConfig.AgentInternalPort），仅绑定 127.0.0.1——公网不可达，这是 Agent 侧数据接入 A 案的落地（计划文档 §5.9/§6）。
// 端点均为对现有 Repository 的薄包装，无业务逻辑重复：
//   GET /internal/agent/player/{id}/profile → 玩家基本信息（名字/在线）
//   GET /internal/agent/player/{id}/bag     → 背包容量与物品清单
//   GET /internal/agent/player/{id}/quests  → 任务状态（进行中/可提交/可接取）
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;

using CompeteServer.Database;
using CompeteServer.Handler;
using CompeteServer.Manager;

namespace CompeteServer.Network
{
    /// <summary>
    /// Agent 内部数据服务类
    /// 用途：以 HttpListener 向本机 Python Agent 服务提供玩家数据的只读 JSON 接口
    /// </summary>
    internal static class InternalAgentApiServer
    {
        /// <summary>监听端口（默认 8897，集中定义于 ServerConfig）</summary>
        public static int Port => ServerConfig.AgentInternalPort;

        private static HttpListener _listener;

        /// <summary>
        /// 启动内部数据 API
        /// 用途：绑定 127.0.0.1:8897 并进入接收循环（后台线程），线程池处理单个请求
        /// </summary>
        public static void Start()
        {
            if (_listener != null)
                return;

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();

            Thread acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "InternalAgentApi" };
            acceptThread.Start();
        }

        /// <summary>接收循环（独立线程，阻塞式取请求后丢线程池处理）</summary>
        private static void AcceptLoop()
        {
            while (_listener != null && _listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = _listener.GetContext();
                }
                catch (Exception)
                {
                    break;   // 监听器被关闭（进程退出）
                }
                ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
            }
        }

        /// <summary>
        /// 处理单个数据查询请求
        /// 用途：GET /internal/agent/player/{id}/{kind} 查数据；POST .../mail/claim 为唯一写端点（Phase 9）
        /// </summary>
        private static void HandleRequest(HttpListenerContext context)
        {
            try
            {
                if (context.Request.HttpMethod == "POST")
                {
                    HandleWriteRequest(context);
                    return;
                }

                if (context.Request.HttpMethod != "GET")
                {
                    context.Response.StatusCode = 405;
                    context.Response.Close();
                    return;
                }

                // Phase 8（学 ZCode AUTH_TOKEN）：配置了共享令牌就必须带上正确值——
                // 回环 ≠ 认证，这道闸挡"本机其它进程直接问 {player_id: 任何人} 的数据"
                if (!AgentToken.IsValid(context.Request.Headers["X-Agent-Token"]))
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    Console.Error.WriteLine("[AgentApi] 401 令牌校验失败（来源进程未持共享密钥）");
                    return;
                }

                // 解析 /internal/agent/player/{id}/{kind}；路径不合法一律 404（不暴露路由细节）
                string[] segments = context.Request.Url.AbsolutePath.Trim('/').Split('/');
                if (segments.Length == 3 && segments[0] == "internal"
                    && segments[1] == "agent" && segments[2] == "announcements")
                {
                    RespondJson(context, 200, BuildAnnouncements());   // Phase 10：全局更新公告（无玩家维度）
                    return;
                }
                if (segments.Length != 5
                    || segments[0] != "internal" || segments[1] != "agent"
                    || segments[2] != "player" || !int.TryParse(segments[3], out int playerId)
                    || playerId <= 0)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                object payload = segments[4] switch
                {
                    "profile" => BuildProfile(playerId),
                    "bag" => BuildBag(playerId),
                    "quests" => BuildQuests(playerId),
                    "mail_summary" => BuildMailSummary(playerId),
                    "gacha_status" => BuildGachaStatus(playerId),
                    "friend_list" => BuildFriendList(playerId),
                    "leaderboard" => BuildLeaderboard(playerId),
                    _ => null,
                };

                if (payload == null)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength64 = json.Length;
                context.Response.OutputStream.Write(json, 0, json.Length);
                Console.WriteLine($"[AgentApi] 200 /{segments[3]}/{segments[4]}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[AgentApi] 请求处理失败 [{exception.GetType().Name}]: {exception.Message}");
                try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
            }
            finally
            {
                try { context.Response.Close(); } catch { }
            }
        }

        /// <summary>
        /// 处理写请求（Phase 9：帮领邮件——当前唯一写端点，计划 §13"服务端权限+二次确认+审计"的落地样例）
        /// 用途：POST /internal/agent/player/{id}/mail/claim，body {"mail_id":N}；
        ///   令牌校验 → 玩家必须在线 → 复用 MailHandler.ExecuteClaim（领取事务业务逻辑单份）→
        ///   推 2606 给会话（客户端邮件缓存/背包按既有事件自刷新）→ [AgentWrite] 审计日志。
        /// 安全语义：归属/过期/重复领取校验全在 Claim 事务内（幂等安全）；二次确认在对话层
        ///   由工具描述强制（阿福必须先复述内容征得同意）。
        /// </summary>
        private static void HandleWriteRequest(HttpListenerContext context)
        {
            try
            {
                if (!AgentToken.IsValid(context.Request.Headers["X-Agent-Token"]))
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    Console.Error.WriteLine("[AgentApi] 401 写请求令牌校验失败");
                    return;
                }

                string[] segments = context.Request.Url.AbsolutePath.Trim('/').Split('/');
                if (segments.Length != 6
                    || segments[0] != "internal" || segments[1] != "agent"
                    || segments[2] != "player" || !int.TryParse(segments[3], out int playerId)
                    || playerId <= 0 || segments[4] != "mail" || segments[5] != "claim")
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                using JsonDocument body = JsonDocument.Parse(context.Request.InputStream);
                if (!body.RootElement.TryGetProperty("mail_id", out JsonElement mailElement)
                    || !mailElement.TryGetInt32(out int mailId) || mailId <= 0)
                {
                    RespondJson(context, 400, new { ok = false, message = "bad_request（缺 mail_id）" });
                    return;
                }

                // 写操作仅限在线会话：客户端要能实时收到 2606 推送（缓存自刷新），也天然限制"离线代领"
                ClientSession? session = SessionManager.Instance.GetSessionByPlayerId(playerId);
                if (session == null)
                {
                    RespondJson(context, 409, new { ok = false, message = "玩家不在线，Agent 写操作仅限在线时执行" });
                    return;
                }

                (bool success, string message, List<(int ItemId, int Count)> granted) =
                    MailHandler.ExecuteClaim(session, playerId, mailId);

                Console.WriteLine($"[AgentWrite] 领取邮件 player={playerId} mail={mailId} → {message}（附件{granted.Count}种）");
                RespondJson(context, 200, new
                {
                    ok = success,
                    message,
                    granted = granted.Select(g => new { item_id = g.ItemId, count = g.Count }).ToList(),
                });
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[AgentWrite] 处理失败 [{exception.GetType().Name}]: {exception.Message}");
                try { RespondJson(context, 500, new { ok = false, message = "internal_error" }); } catch { }
            }
        }

        private static void RespondJson(HttpListenerContext context, int statusCode, object payload)
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = json.Length;
            context.Response.OutputStream.Write(json, 0, json.Length);
            try { context.Response.Close(); } catch { }
        }

        /// <summary>
        /// 更新公告（Phase 10）：读 Config/announcements.json（游戏公告的权威源头，策划/开发者直接编辑此文件）。
        /// 缓存按文件 LastWriteTime 失效——改完公告立即生效，无需重启。
        /// </summary>
        private static object BuildAnnouncements()
        {
            const string path = "Config/announcements.json";
            lock (_announcementLock)
            {
                DateTime modified = File.GetLastWriteTime(path);
                if (_announcementCache == null || modified != _announcementCacheModified)
                {
                    string json = File.ReadAllText(path);
                    JsonDocument document = JsonDocument.Parse(json);
                    // 新版在前（阿福按此口径讲"最新一次更新"）：按 date_ms 降序
                    List<(long DateMs, object Item)> ordered = new();
                    foreach (JsonElement element in document.RootElement.EnumerateArray())
                    {
                        ordered.Add((element.GetProperty("date_ms").GetInt64(), new
                        {
                            version = element.GetProperty("version").GetString(),
                            title = element.GetProperty("title").GetString(),
                            date_ms = element.GetProperty("date_ms").GetInt64(),
                            content = element.GetProperty("content").GetString(),
                        }));
                    }
                    _announcementCache = new
                    {
                        items = ordered.OrderByDescending(entry => entry.DateMs)
                            .Select(entry => entry.Item)
                            .ToList(),
                    };
                    _announcementCacheModified = modified;
                }
                return _announcementCache;
            }
        }
        private static readonly object _announcementLock = new();
        private static object? _announcementCache;
        private static DateTime _announcementCacheModified;

        /// <summary>玩家基本信息：昵称 + 在线状态（Agent 的 context 快照同源）</summary>
        private static object BuildProfile(int playerId)
        {
            (bool found, string name) = new AccountRepository().QueryPlayer(playerId);
            return new
            {
                id = playerId,
                name = found ? name : string.Empty,
                exists = found,
                online = SessionManager.Instance.GetSessionByPlayerId(playerId) != null,
            };
        }

        /// <summary>背包清单：容量/已用/物品（名称+数量+稀有度，从静态配置表补齐展示信息）</summary>
        private static object BuildBag(int playerId)
        {
            List<(int SlotIndex, int ItemId, int Count, long InstanceId)> slots = new BagRepository().GetSlots(playerId);
            Dictionary<int, BagRepository.ItemConfig> configs = BagRepository.GetConfigMap();

            List<object> items = slots
                .Where(slot => slot.Count > 0)
                .Select(slot =>
                {
                    configs.TryGetValue(slot.ItemId, out BagRepository.ItemConfig? config);
                    return (object)new
                    {
                        slot_index = slot.SlotIndex,
                        item_id = slot.ItemId,
                        count = slot.Count,
                        name = config?.Name ?? $"物品{slot.ItemId}",
                        rarity = config?.Rarity ?? 0,
                    };
                })
                .ToList();

            return new
            {
                capacity = BagRepository.WeaponCapacity,
                used = items.Count,
                items,
            };
        }

        /// <summary>任务状态：进行中（含进度）/可提交/可接取（按前置链推导），条目带名字与进度文本</summary>
        private static object BuildQuests(int playerId)
        {
            QuestRepository repository = new QuestRepository();
            List<QuestConfig> configs = repository.GetConfigs();
            Dictionary<int, PlayerQuestRow> rows = repository.GetPlayerQuests(playerId);

            List<object> active = new();
            List<object> submittable = new();
            List<object> acceptable = new();

            foreach (PlayerQuestRow row in rows.Values)
            {
                QuestConfig? config = configs.Find(config => config.QuestId == row.QuestId);
                if (config == null) continue;

                // 状态口径与任务系统一致：1=进行中 2=已完成待提交 3=已提交（终态不下发）
                if (row.State == QuestRepository.StateProcessing)
                    active.Add(new { id = row.QuestId, name = config.Name, progress = $"{row.Progress}/{config.TargetProgress}" });
                else if (row.State == QuestRepository.StateFinished)
                    submittable.Add(new { id = row.QuestId, name = config.Name, progress = $"{row.Progress}/{config.TargetProgress}" });
            }

            // 可接取 = 玩家没有该任务的行，且前置任务已提交（成就类不进可接取池，由系统自动激活）
            foreach (QuestConfig config in configs)
            {
                if (config.Category == QuestRepository.CategoryAchievement) continue;
                if (rows.ContainsKey(config.QuestId)) continue;
                bool prevDone = config.PrevQuestId == 0
                    || (rows.TryGetValue(config.PrevQuestId, out PlayerQuestRow? prev) && prev.State == QuestRepository.StateSubmitted);
                if (prevDone)
                    acceptable.Add(new { id = config.QuestId, name = config.Name, description = config.Description });
            }

            return new { active, submittable, acceptable };
        }

        /// <summary>
        /// 邮件摘要：总数/未读数/待领附件数 + 最近几封标题
        /// 用途：登录简报（Phase 5 ②）与 get_mail_summary 工具同源；GetMails 已过滤删除与过期
        /// </summary>
        private static object BuildMailSummary(int playerId)
        {
            List<MailRepository.MailData> mails = new MailRepository().GetMails(playerId);
            int unread = 0;
            int claimable = 0;
            List<object> recent = new();
            foreach (MailRepository.MailData mail in mails)
            {
                if (!mail.Read) unread++;
                if (!mail.Claimed && mail.Items.Count > 0) claimable++;
                if (recent.Count < 10)
                {
                    recent.Add(new
                    {
                        mail_id = mail.MailId,   // Phase 9：带 mail_id 供 claim_mail_attachments 工具引用
                        title = mail.Title,
                        read = mail.Read,
                        claimed = mail.Claimed,
                        has_items = mail.Items.Count > 0,
                    });
                }
            }
            return new { total = mails.Count, unread, claimable, recent };
        }

        /// <summary>
        /// 抽卡保底状态：各卡池的累计抽数与距 5 星保底的差值
        /// 用途：登录简报（Phase 5 ②）与 get_gacha_status 工具同源
        /// </summary>
        private static object BuildGachaStatus(int playerId)
        {
            GachaRepository repository = new();
            List<object> banners = new();
            foreach (GachaRepository.BannerConfig banner in GachaRepository.GetBanners())
            {
                (int total, int pity5) = repository.GetPityState(playerId, banner.BannerId);
                banners.Add(new
                {
                    banner_id = banner.BannerId,
                    title = banner.Title,
                    total_draws = total,
                    pity5_count = pity5,
                    pity5_threshold = banner.Pity5Threshold,
                    pity5_remaining = Math.Max(0, banner.Pity5Threshold - pity5),
                });
            }
            return new { banners };
        }

        /// <summary>好友列表：ID+昵称+在线状态（get_friend_list 工具数据源）</summary>
        private static object BuildFriendList(int playerId)
        {
            List<(int FriendId, string Name)> friends = new FriendRepository().GetFriends(playerId);
            List<object> list = friends
                .Select(f => (object)new
                {
                    id = f.FriendId,
                    name = f.Name,
                    online = SessionManager.Instance.GetSessionByPlayerId(f.FriendId) != null,
                })
                .ToList();
            return new { count = list.Count, friends = list };
        }

        /// <summary>排行榜：默认挑战 Top10 + 我的名次（名字批量补齐；get_leaderboard 工具数据源）</summary>
        private static object BuildLeaderboard(int playerId)
        {
            (bool success, _, List<(long Rank, int PlayerId, long Score)> rows, long myRank, long myScore) =
                new LeaderboardRepository().GetLeaderboard(
                    LeaderboardRepository.DefaultChallengeId, 10, playerId);

            Dictionary<int, string> names = new AccountRepository()
                .GetNamesByIds(rows.Select(row => row.PlayerId));

            List<object> top = rows
                .Select(row => (object)new
                {
                    rank = row.Rank,
                    player_id = row.PlayerId,
                    name = names.GetValueOrDefault(row.PlayerId, $"玩家{row.PlayerId}"),
                    score = row.Score,
                })
                .ToList();

            return new { success, top, my_rank = myRank, my_score = myScore };
        }
    }
}
