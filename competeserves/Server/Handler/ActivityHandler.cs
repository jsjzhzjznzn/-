// 命名空间：CompeteServer.Handler
// 活动消息处理类：活动列表 / 唤取 / 唤取流水 / GM 轮换（2913 生效后全服广播 2911）
// 身份一律取自会话绑定（session.PlayerId）；GM 指令与 2509 同口径暂不鉴权（产品化时再收紧）
using Common.Enum;
using Common.Network;

using CompeteServer.Database;
using CompeteServer.Manager;
using CompeteServer.Network;

using Google.Protobuf;

namespace CompeteServer.Handler
{
    /// <summary>
    /// 活动消息处理类
    /// 用途：仿鸣潮武器唤取活动的协议处理（排期查询/抽卡/详单/GM 轮换）
    /// </summary>
    public static class ActivityHandler
    {
        /// <summary>
        /// 活动列表处理方法
        /// 返回当前时间窗内的活动 + 服务器时钟（客户端用它算剩余倒计时，防本地时间偏差）
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        public static void HandleActivityList(ClientSession session)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.ActivityListResponse,
                    new ActivityListResponse { Success = false, Message = "请先登录" });
                return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            List<ActivityRepository.ActivityRow> rows = new ActivityRepository().GetActiveActivities(now);

            ActivityListResponse response = new()
            {
                Success = true,
                Message = rows.Count > 0 ? "OK" : "当前没有进行中的活动",
                ServerNowMs = now,
            };
            foreach (ActivityRepository.ActivityRow row in rows)
                response.Activities.Add(FillActivityInfo(row));

            Console.WriteLine($"活动列表返回:玩家{session.PlayerId} {rows.Count}个活动");
            _ = session.Send(MessageID.ActivityListResponse, response);
        }

        /// <summary>
        /// 唤取处理方法
        /// 服务端权威全流程（扣媒介→roll→入包→流水），回执带操作后的背包快照（与 BagUseResponse 同模式）
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的唤取请求消息体</param>
        public static void HandleGachaDraw(ClientSession session, ReadOnlySpan<byte> body)
        {
            GachaDrawRequest request = GachaDrawRequest.Parser.ParseFrom(body);

            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.GachaDrawResponse,
                    new GachaDrawResponse { Success = false, Message = "请先登录" });
                return;
            }
            if (request.BannerId <= 0)
            {
                _ = session.Send(MessageID.GachaDrawResponse,
                    new GachaDrawResponse { Success = false, Message = "卡池ID非法" });
                return;
            }

            GachaRepository.DrawOutcome outcome =
                new GachaRepository().Draw(session.PlayerId, request.BannerId, request.Count, request.RequestId);

            GachaDrawResponse response = new()
            {
                Success = outcome.Success,
                Message = outcome.Message,
                Pity5Count = outcome.Pity5Count,
                Pity5Threshold = outcome.Pity5Threshold,
                TotalDraws = outcome.TotalDraws,
                GuaranteedUp = outcome.GuaranteedUp ? 1 : 0,
                Replayed = outcome.Replayed,
            };

            if (outcome.Success)
            {
                foreach (GachaRepository.DrawResultItem item in outcome.Results)
                {
                    response.Results.Add(new GachaResultItem
                    {
                        ItemId = item.ItemId,
                        Name = item.Name,
                        Rarity = item.Rarity,
                        IconKey = item.IconKey,
                        IsUp = item.IsUp,
                        IsPity = item.IsPity,
                        IsNew = item.IsNew,
                        PityKind = item.PityKind,
                    });
                }
                foreach ((int slotIndex, int itemId, int count, long instanceId) in outcome.Slots)
                {
                    response.Slots.Add(new BagSlotInfo
                    {
                        SlotIndex = slotIndex,
                        ItemId = itemId,
                        Count = count,
                        InstanceId = instanceId,
                    });
                }
                foreach (var config in outcome.Configs)
                {
                    response.Configs.Add(new ItemConfigInfo
                    {
                        ItemId = config.ItemId,
                        Name = config.Name,
                        ItemType = config.ItemType,
                        Rarity = config.Rarity,
                        IconKey = config.IconKey,
                        Desc = config.Description,
                        MaxStack = config.MaxStack,
                    });
                }
            }

            // 成就/任务统计：只对首次结算生效（重放不能重复计数）
            if (outcome.Success && !outcome.Replayed)
            {
                AchievementService.ReportGacha(session.PlayerId, request.Count,
                    outcome.Results.Count(item => item.Star == 5));
                foreach ((int itemId, int count) in outcome.GrantedItems)
                {
                    QuestHandler.RaiseItemGained(session.PlayerId, itemId, count);
                }
            }

            _ = session.Send(MessageID.GachaDrawResponse, response);
        }

        /// <summary>
        /// 唤取流水处理方法（鸣潮"唤取记录"）
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的流水请求消息体</param>
        public static void HandleGachaHistory(ClientSession session, ReadOnlySpan<byte> body)
        {
            GachaHistoryRequest request = GachaHistoryRequest.Parser.ParseFrom(body);

            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.GachaHistoryResponse,
                    new GachaHistoryResponse { Success = false, Message = "请先登录" });
                return;
            }

            GachaRepository repository = new();
            List<GachaRepository.HistoryRow> entries =
                repository.GetHistory(session.PlayerId, request.BannerId);
            GachaRepository.PitySnapshot pity = request.BannerId > 0
                ? repository.GetPitySnapshot(session.PlayerId, request.BannerId)
                : new GachaRepository.PitySnapshot(0, false, 0);

            // 物品名/稀有度/图标一次取齐
            Dictionary<int, (string Name, int Rarity, string IconKey)> itemInfo = new();
            foreach (var config in new BagRepository().GetAllConfigs())
                itemInfo[config.ItemId] = (config.Name, config.Rarity, config.IconKey);

            GachaHistoryResponse response = new()
            {
                Success = true,
                Message = "OK",
                TotalDraws = repository.GetTotalDraws(session.PlayerId, request.BannerId),
                Pity5Count = pity.Pity5Count,
                GuaranteedUp = pity.GuaranteedUp,
            };
            foreach (GachaRepository.HistoryRow entry in entries)
            {
                (string name, int rarity, string iconKey) = itemInfo.GetValueOrDefault(entry.ItemId, ($"物品{entry.ItemId}", 0, string.Empty));
                response.Entries.Add(new GachaHistoryEntry
                {
                    BannerId = entry.BannerId,
                    ItemId = entry.ItemId,
                    Name = name,
                    Rarity = rarity,
                    IconKey = iconKey,
                    IsPity = entry.IsPity,
                    PityKind = entry.PityKind,
                    CreatedAtMs = entry.CreatedAtMs,
                });
            }

            Console.WriteLine($"唤取流水返回:玩家{session.PlayerId} {entries.Count}条");
            _ = session.Send(MessageID.GachaHistoryResponse, response);
        }

        /// <summary>
        /// GM 轮换处理方法（联调/演示指令）
        /// 改排期后把切换结果全服广播（2911），在线客户端实时刷新活动状态
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：发起指令的会话</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的轮换请求消息体</param>
        public static void HandleActivityGmRotate(ClientSession session, ReadOnlySpan<byte> body)
        {
            ActivityGmRotateRequest request = ActivityGmRotateRequest.Parser.ParseFrom(body);

            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.ActivityGmRotateResponse,
                    new ActivityGmRotateResponse { Success = false, Message = "请先登录" });
                return;
            }

            (bool success, string message, List<ActivityRepository.ActivityRow> rows) =
                new ActivityRepository().GmRotate(request.TargetActivityId);

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            ActivityGmRotateResponse response = new()
            {
                Success = success,
                Message = message,
                ServerNowMs = now,
            };
            foreach (ActivityRepository.ActivityRow row in rows)
                response.Activities.Add(FillActivityInfo(row));
            _ = session.Send(MessageID.ActivityGmRotateResponse, response);

            // 轮换成功 → 全服广播当前活动状态（拼 [消息ID+消息体] 载荷走 SessionManager.Broadcast）
            if (success)
            {
                ActivityChangeNotify notify = new() { ServerNowMs = now };
                foreach (ActivityRepository.ActivityRow row in rows)
                    notify.Activities.Add(FillActivityInfo(row));
                byte[] notifyBody = notify.ToByteArray();
                byte[] payload = new byte[4 + notifyBody.Length];
                BitConverter.GetBytes((int)MessageID.ActivityChangeNotify).CopyTo(payload, 0);
                Buffer.BlockCopy(notifyBody, 0, payload, 4, notifyBody.Length);
                // 分批扇出是异步的，GM 指令本身不等它；Broadcast 内每个会话都走 SafeSend，不会抛
                _ = SessionManager.Instance.Broadcast(payload);
                Console.WriteLine($"活动切换已广播:当前 {rows.Count} 个活动（{string.Join("、", rows.Select(r => r.Title))}）");
            }
        }

        /// <summary>
        /// 签到状态处理方法（签到页 2908 数据源）
        /// 读路径幂等：不做窗口校验（活动中心侧栏只会出现进行中的活动；写路径 Claim 才闸窗口）
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的签到状态请求消息体</param>
        public static void HandleSignInList(ClientSession session, ReadOnlySpan<byte> body)
        {
            SignInListRequest request = SignInListRequest.Parser.ParseFrom(body);

            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.SignInListResponse,
                    new SignInListResponse { Success = false, Message = "请先登录" });
                return;
            }
            if (request.SignId <= 0)
            {
                _ = session.Send(MessageID.SignInListResponse,
                    new SignInListResponse { Success = false, Message = "签到活动ID非法" });
                return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            (bool success, string message, int totalDays, int signedDays, bool todaySigned, List<SignInRepository.DayRow> days) =
                new SignInRepository().GetSignState(session.PlayerId, request.SignId);

            SignInListResponse response = new()
            {
                Success = success,
                Message = message,
                SignId = request.SignId,
                TotalDays = totalDays,
                SignedDays = signedDays,
                TodaySigned = todaySigned,
                ServerNowMs = now,
            };
            foreach (SignInRepository.DayRow day in days)
                response.Days.Add(FillSignInDay(day));

            Console.WriteLine($"签到状态返回:玩家{session.PlayerId} 活动{request.SignId} 已签{signedDays}/{totalDays} 今日已签={todaySigned}");
            _ = session.Send(MessageID.SignInListResponse, response);
        }

        /// <summary>
        /// 签到处理方法（服务端权威：第几天/每日一次/窗口全在仓库裁决，回执带完整签到快照 + 背包快照）
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的签到请求消息体</param>
        public static void HandleSignInClaim(ClientSession session, ReadOnlySpan<byte> body)
        {
            SignInClaimRequest request = SignInClaimRequest.Parser.ParseFrom(body);

            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.SignInClaimResponse,
                    new SignInClaimResponse { Success = false, Message = "请先登录" });
                return;
            }

            SignInRepository.ClaimResult result = new SignInRepository().Claim(session.PlayerId, request.SignId);

            SignInClaimResponse response = new()
            {
                Success = result.Success,
                Message = result.Message,
                SignId = result.SignId,
                Day = result.Day,
                TotalDays = result.TotalDays,
                SignedDays = result.SignedDays,
                TodaySigned = result.TodaySigned,
            };
            foreach (SignInRepository.DayRow day in result.Days)
                response.Days.Add(FillSignInDay(day));
            if (result.Success)
            {
                foreach (SignInRepository.SlotRow slot in result.Slots)
                    response.Slots.Add(new BagSlotInfo
                    {
                        SlotIndex = slot.SlotIndex,
                        ItemId = slot.ItemId,
                        Count = slot.Count,
                        InstanceId = slot.InstanceId,
                    });
                foreach (var config in result.Configs)
                {
                    response.Configs.Add(new ItemConfigInfo
                    {
                        ItemId = config.ItemId,
                        Name = config.Name,
                        ItemType = config.ItemType,
                        Rarity = config.Rarity,
                        IconKey = config.IconKey,
                        Desc = config.Description,
                        MaxStack = config.MaxStack,
                    });
                }

                // 收集类任务/生涯统计联动（与邮件领取同口径；收集类任务进度=实时聚合背包持有量，
                // 转邮箱的部分会在玩家领取邮件时再次触发，天然不重不漏）
                SignInRepository.DayRow? reward = result.Days.FirstOrDefault(d => d.Day == result.Day);
                if (reward != null)
                {
                    QuestHandler.RaiseItemGained(session.PlayerId, reward.ItemId, reward.Count);
                }

                // 成就：累计签到天数
                AchievementService.ReportStat(session.PlayerId, CareerRepository.ScoreSignIn, 1);
            }

            _ = session.Send(MessageID.SignInClaimResponse, response);
        }

        /// <summary>排期行 → 协议条目。</summary>
        private static ActivityInfo FillActivityInfo(ActivityRepository.ActivityRow row) => new()
        {
            ActivityId = row.ActivityId,
            Type = row.Type,
            BannerId = row.BannerId,
            ContentKey = row.ContentKey,
            ContentVersion = row.ContentVersion,
            Title = row.Title,
            Subtitle = row.Subtitle,
            StartAtMs = row.StartAtMs,
            EndAtMs = row.EndAtMs,
        };

        /// <summary>仓库天次行 → 协议条目。</summary>
        private static SignInDayInfo FillSignInDay(SignInRepository.DayRow day) => new()
        {
            Day = day.Day,
            ItemId = day.ItemId,
            Name = day.Name,
            IconKey = day.IconKey,
            Rarity = day.Rarity,
            Count = day.Count,
            Claimed = day.Claimed,
        };
    }
}
