using Common.Enum;
using Common.Network;

using CompeteServer.Database;
using CompeteServer.Manager;
using CompeteServer.Network;

namespace CompeteServer.Handler
{
    /// <summary>
    /// 邮件消息处理类（自 MessageHandler 路由转发）
    /// 负责邮件列表 / 置读 / 领取附件 / 删除 / GM 发件的消息处理；
    /// 身份一律取自会话绑定（session.PlayerId），归属校验在仓库层（防跨玩家操作）
    /// 领取附件后物品进包 → 逐物品接入任务系统（收集类重算 + 生涯获得物品），与 BagGrant 同口径
    /// </summary>
    public static class MailHandler
    {
        // ──────────────────────────────
        // 消息处理
        // ──────────────────────────────

        /// <summary>
        /// 邮件列表处理方法
        /// 返回邮箱容量 + 全部有效邮件（未删除且未过期，新邮件在前），客户端整表替换；
        /// 拉取前先同步运营公共邮件（延迟创建的读路径：符合条件的公共邮件此时才实例化到个人邮箱）
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        public static void HandleMailList(ClientSession session)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.MailListResponse,
                    new MailListResponse { Success = false, Message = "请先登录" });
                return;
            }

            MailRepository repository = new();
            int instantiated = repository.SyncPublicMails(session.PlayerId);
            MailListResponse response = new()
            {
                Success = true,
                Message = "OK",
                Capacity = MailRepository.MailCapacity,
            };
            response.Mails.AddRange(BuildMailInfos(repository.GetMails(session.PlayerId)));

            Console.WriteLine($"邮件列表返回:{session.PlayerId} {response.Mails.Count}封邮件{(instantiated > 0 ? $"（本次实例化公共邮件 {instantiated} 封）" : string.Empty)}");
            _ = session.Send(MessageID.MailListResponse, response);
        }

        /// <summary>置读处理方法（点开详情时调用；幂等——已读再标不报错）。</summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的请求消息体</param>
        public static void HandleMailRead(ClientSession session, ReadOnlySpan<byte> body)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.MailReadResponse,
                    new MailReadResponse { Success = false, Message = "请先登录" });
                return;
            }

            MailReadRequest request = MailReadRequest.Parser.ParseFrom(body);
            MailRepository repository = new();
            (bool success, string message) = repository.MarkRead(session.PlayerId, request.MailId);

            // 无论成败都回最新快照（失败可能是列表过期，客户端整表对齐）
            Console.WriteLine($"邮件置读:{session.PlayerId} 邮件{request.MailId} → {message}");
            _ = session.Send(MessageID.MailReadResponse, new MailReadResponse
            {
                Success = success,
                Message = message,
                Mails = { BuildMailInfos(repository.GetMails(session.PlayerId)) },
            });
        }

        /// <summary>
        /// 领取附件处理方法（一键领取全部附件；服务端单事务内校验与落账，背包放不下整单拒绝）
        /// 领取成功后逐物品接入任务系统（与 BagGrant 同口径：收集类重算 + 生涯"累计获得物品"）；
        /// 背包快照已过期，客户端收到响应后自行重拉（与任务提交同款处理，协议不回背包）
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的请求消息体</param>
        public static void HandleMailClaim(ClientSession session, ReadOnlySpan<byte> body)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.MailClaimResponse,
                    new MailClaimResponse { Success = false, Message = "请先登录" });
                return;
            }

            MailClaimRequest request = MailClaimRequest.Parser.ParseFrom(body);
            _ = ExecuteClaim(session, session.PlayerId, request.MailId);
        }

        /// <summary>
        /// 领取邮件附件的核心执行（Phase 9 抽出，协议处理器与 Agent 内部写 API 共用——业务逻辑单份）：
        /// 事务领取 → 推 2606 给会话（客户端邮件缓存/背包按既有事件刷新）→ 收集类任务/生涯事件。
        /// </summary>
        /// <returns>领取结果（success/message/granted 物品清单），供 Agent 写 API 转述给阿福</returns>
        public static (bool success, string message, List<(int ItemId, int Count)> granted) ExecuteClaim(
            ClientSession? pushTo, int playerId, int mailId)
        {
            MailRepository repository = new();
            (bool success, string message, List<(int ItemId, int Count)> granted) =
                repository.Claim(playerId, mailId);

            Console.WriteLine($"邮件领取:{playerId} 邮件{mailId} → {message}");
            if (pushTo != null)
            {
                _ = pushTo.Send(MessageID.MailClaimResponse, new MailClaimResponse
                {
                    Success = success,
                    Message = message,
                    Mails = { BuildMailInfos(repository.GetMails(playerId)) },
                });
            }

            // 物品增加 → 收集类任务重算 + 生涯"累计获得物品"按实际单位数累计（镜像 BagGrant 的接入）
            if (success)
            {
                foreach ((int itemId, int count) in granted)
                {
                    QuestHandler.RaiseItemGained(playerId, itemId, count);
                }

                // 成就：累计领取邮件数（按封计，无论附件多少）
                AchievementService.ReportStat(playerId, CareerRepository.ScoreMailClaim, 1);
            }
            return (success, message, granted);
        }

        /// <summary>删除邮件处理方法（软删：置状态位不物理删行；附件未领取也允许删）。</summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的请求消息体</param>
        public static void HandleMailDelete(ClientSession session, ReadOnlySpan<byte> body)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.MailDeleteResponse,
                    new MailDeleteResponse { Success = false, Message = "请先登录" });
                return;
            }

            MailDeleteRequest request = MailDeleteRequest.Parser.ParseFrom(body);
            MailRepository repository = new();
            (bool success, string message) = repository.Delete(session.PlayerId, request.MailId);

            Console.WriteLine($"邮件删除:{session.PlayerId} 邮件{request.MailId} → {message}");
            _ = session.Send(MessageID.MailDeleteResponse, new MailDeleteResponse
            {
                Success = success,
                Message = message,
                Mails = { BuildMailInfos(repository.GetMails(session.PlayerId)) },
            });
        }

        /// <summary>
        /// GM 发件处理方法（本期邮件唯一来源，联调用；风险面同 BagGrant——审计 P1-1，待产品决策统一收口）
        /// 收件人可以是发起者自己（面板测试键）或指定玩家；收件人在线时推送 MailReceiveNotify 实时点亮红点
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的请求消息体</param>
        public static void HandleMailGrant(ClientSession session, ReadOnlySpan<byte> body)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.MailGrantResponse,
                    new MailGrantResponse { Success = false, Message = "请先登录" });
                return;
            }

            MailGrantRequest request = MailGrantRequest.Parser.ParseFrom(body);
            int targetId = request.TargetPlayerId > 0 ? request.TargetPlayerId : session.PlayerId;

            // 收件人存在性校验（防给不存在的玩家ID塞孤儿邮件行）
            AccountRepository account = new();
            (bool found, string targetName) = account.QueryPlayer(targetId);
            if (!found)
            {
                _ = session.Send(MessageID.MailGrantResponse,
                    new MailGrantResponse { Success = false, Message = $"收件人不存在（ID {targetId}）" });
                return;
            }

            var items = new List<(int ItemId, int Count)>();
            foreach (MailItemInfo item in request.Items)
            {
                items.Add((item.ItemId, item.Count));
            }
            MailRepository repository = new();
            (bool success, string message, int mailId) =
                repository.SendMail(targetId, request.Title, request.Body, items);

            Console.WriteLine($"邮件GM发件:{session.PlayerId} → 收件人{targetId}({targetName}) 标题[{request.Title}] 附件{items.Count}种 → {message}");
            _ = session.Send(MessageID.MailGrantResponse, new MailGrantResponse { Success = success, Message = message });

            // 收件人在线 → 推送新邮件（面板与红点实时刷新；发给自己的场景客户端收响应后也会重拉列表）
            if (success)
            {
                MailRepository.MailData? mail = repository.GetMail(mailId);
                if (mail != null)
                {
                    PushMailNotify(targetId, mail, $"收到来自系统的邮件【{mail.Title}】");

                    // 阿福主动提醒（悬浮球 3107）：带附件的新邮件让阿福主动说一句；
                    // 在线判定/到达冷却/并发闸都在服务内，纯文本邮件（Items.Count=0）会被服务忽略
                    AgentReminderService.OnMailArrived(targetId, mail.Title, mail.Items.Count);
                }
            }
        }

        /// <summary>
        /// GM 发公共邮件处理方法（运营邮件：写全局表，延迟创建——玩家拉列表时才实例化到个人邮箱）
        /// 不逐个推送在线玩家：延迟创建设计下运营邮件无需实时，玩家下次拉列表自然收到
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的请求消息体</param>
        public static void HandleMailPublicGrant(ClientSession session, ReadOnlySpan<byte> body)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.MailPublicGrantResponse,
                    new MailPublicGrantResponse { Success = false, Message = "请先登录" });
                return;
            }

            MailPublicGrantRequest request = MailPublicGrantRequest.Parser.ParseFrom(body);
            var items = new List<(int ItemId, int Count)>();
            foreach (MailItemInfo item in request.Items)
            {
                items.Add((item.ItemId, item.Count));
            }

            MailRepository repository = new();
            (bool success, string message, int publicMailId) = repository.SendPublicMail(
                request.Tag, request.Title, request.Body, items, request.MinRegisterMs, request.MaxRegisterMs);

            Console.WriteLine($"公共邮件发布:{session.PlayerId} tag[{request.Tag}] 标题[{request.Title}] 附件{items.Count}种 → {message}");
            _ = session.Send(MessageID.MailPublicGrantResponse, new MailPublicGrantResponse
            {
                Success = success,
                Message = message,
                PublicMailId = publicMailId,
            });
        }

        /// <summary>
        /// GM 撤回公共邮件处理方法（按 tag 软标记撤回：未领附件的已实例化邮件在拉取/领取时被过滤）
        /// </summary>
        /// <param name="session">参数名：session，类型：ClientSession，用途：客户端的会话连接对象</param>
        /// <param name="body">参数名：body，类型：byte[]，用途：protobuf序列化的请求消息体</param>
        public static void HandleMailPublicRevoke(ClientSession session, ReadOnlySpan<byte> body)
        {
            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.MailPublicRevokeResponse,
                    new MailPublicRevokeResponse { Success = false, Message = "请先登录" });
                return;
            }

            MailPublicRevokeRequest request = MailPublicRevokeRequest.Parser.ParseFrom(body);
            MailRepository repository = new();
            (bool success, string message) = repository.RevokePublicMail(request.Tag);

            Console.WriteLine($"公共邮件撤回:{session.PlayerId} tag[{request.Tag}] → {message}");
            _ = session.Send(MessageID.MailPublicRevokeResponse, new MailPublicRevokeResponse
            {
                Success = success,
                Message = message,
            });
        }

        // ──────────────────────────────
        // 内部工具
        // ──────────────────────────────

        /// <summary>新邮件推送：只推给在线收件人（不在线不必推，上线拉列表即补齐）。</summary>
        private static void PushMailNotify(int playerId, MailRepository.MailData mail, string message)
        {
            ClientSession? session = SessionManager.Instance.GetSessionByPlayerId(playerId);
            if (session == null) return;

            _ = session.Send(MessageID.MailReceiveNotify, new MailReceiveNotify
            {
                Mail = BuildMailInfo(mail, LoadItemNames()),
                Message = message,
            });
        }

        /// <summary>批量构造协议邮件列表（一次性补齐物品名，避免逐封查库）。</summary>
        private static List<MailInfo> BuildMailInfos(List<MailRepository.MailData> mails)
        {
            Dictionary<int, string> itemNames = LoadItemNames();
            var infos = new List<MailInfo>(mails.Count);
            foreach (MailRepository.MailData mail in mails)
            {
                infos.Add(BuildMailInfo(mail, itemNames));
            }
            return infos;
        }

        /// <summary>构造单封协议邮件（附件物品名由调用方传入的名字表补齐）。</summary>
        private static MailInfo BuildMailInfo(MailRepository.MailData mail, Dictionary<int, string> itemNames)
        {
            MailInfo info = new()
            {
                MailId = mail.MailId,
                Title = mail.Title,
                Body = mail.Body,
                SenderName = mail.SenderName,
                CreateTimeMs = mail.CreateTimeMs,
                ExpireTimeMs = mail.ExpireTimeMs,
                Read = mail.Read,
                Claimed = mail.Claimed,
            };
            foreach ((int itemId, int count) in mail.Items)
            {
                info.Items.Add(new MailItemInfo
                {
                    ItemId = itemId,
                    Count = count,
                    Name = itemNames.TryGetValue(itemId, out string? name) ? name : $"#{itemId}",
                });
            }
            return info;
        }

        /// <summary>读取物品名表（附件只存物品ID，下发前补齐名字；与 QuestHandler 同款）。</summary>
        private static Dictionary<int, string> LoadItemNames()
        {
            var names = new Dictionary<int, string>();
            foreach ((int itemId, string name, int _, int _, string _, string _, int _) in
                     new BagRepository().GetAllConfigs())
            {
                names[itemId] = name;
            }
            return names;
        }
    }
}
