using Common.Enum;
using Common.Network;

using CompeteServer.Database;
using CompeteServer.Manager;
using CompeteServer.Network;

namespace CompeteServer.Handler
{
    /// <summary>
    /// 对战好友邀请处理器
    ///
    /// 定位：**只做信令**。对战流量仍由房主进程里的 NGO Listen Server（7777）承载，
    /// 这里负责三件事：
    ///   1. 校验「谁在邀请谁」（登录态 / 好友关系 / 目标在线 / 房主配额）；
    ///   2. 把房主自报的对战地址转交给被邀请方（地址不经过第三方客户端）；
    ///   3. 把同意 / 拒绝 / 过期 / 取消 四种结果推回邀请方。
    ///
    /// 身份一律取自会话绑定（session.PlayerId），与聊天系统同口径，客户端无法伪造。
    /// </summary>
    public static class BattleHandler
    {
        /// <summary>过期巡检间隔（毫秒）：邀请 TTL 60s，5s 巡检足够及时</summary>
        private const int SweepIntervalMs = 5_000;

        private static Timer? sweeper;

        // ──────────────────────────────
        // 请求入口
        // ──────────────────────────────

        /// <summary>
        /// 发送对战邀请
        /// 链路：登录校验 → 参数校验 → 好友校验 → 在线校验 → 创建邀请 → 回执房主 → 推送好友
        /// </summary>
        public static void HandleBattleInvite(ClientSession session, ReadOnlySpan<byte> body)
        {
            BattleInviteRequest request = BattleInviteRequest.Parser.ParseFrom(body);

            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.BattleInviteResponse,
                    new BattleInviteResponse { Success = false, Message = "请先登录" });
                return;
            }

            int inviterId = session.PlayerId;
            int targetId = request.TargetPlayerId;

            // 过期邀请先清掉再接受新请求：否则房主会因为一条早已失效的记录被"已有待回应的邀请"挡住
            NotifyExpired(BattleInviteService.SweepExpired());

            if (targetId <= 0)
            {
                _ = session.Send(MessageID.BattleInviteResponse,
                    new BattleInviteResponse { Success = false, Message = "邀请目标无效" });
                return;
            }

            if (!new FriendRepository().AreFriends(inviterId, targetId))
            {
                Console.WriteLine($"对战邀请被拒（非好友）:{inviterId} → {targetId}");
                _ = session.Send(MessageID.BattleInviteResponse,
                    new BattleInviteResponse { Success = false, Message = "对方不是你的好友" });
                return;
            }

            // 在线判定以会话为准：推送要靠它，PlayerManager 在线但无会话的窗口期按离线处理
            ClientSession? targetSession = SessionManager.Instance.GetSessionByPlayerId(targetId);
            if (targetSession == null)
            {
                _ = session.Send(MessageID.BattleInviteResponse,
                    new BattleInviteResponse { Success = false, Message = "对方不在线" });
                return;
            }

            (bool found, string inviterName) = new AccountRepository().QueryPlayer(inviterId);
            string displayName = found && !string.IsNullOrEmpty(inviterName) ? inviterName : $"玩家{inviterId}";

            (bool targetFound, string targetName) = new AccountRepository().QueryPlayer(targetId);
            string targetDisplayName = targetFound && !string.IsNullOrEmpty(targetName) ? targetName : $"玩家{targetId}";

            if (!BattleInviteService.TryCreate(
                    inviterId,
                    displayName,
                    targetId,
                    targetDisplayName,
                    request.HostAddress,
                    request.HostPort,
                    request.RoomId,
                    out BattleInvite? invite,
                    out string error) || invite == null)
            {
                Console.WriteLine($"对战邀请创建失败:{inviterId} → {targetId} | {error}");
                _ = session.Send(MessageID.BattleInviteResponse,
                    new BattleInviteResponse { Success = false, Message = error });
                return;
            }

            // 创建与推送之间目标可能刚好掉线：再确认一次，失败就撤销，避免房主对着一条"已送达"的邀请空等
            targetSession = SessionManager.Instance.GetSessionByPlayerId(targetId);
            if (targetSession == null)
            {
                BattleInviteService.Cancel(inviterId, invite.InviteId, string.Empty);
                _ = session.Send(MessageID.BattleInviteResponse,
                    new BattleInviteResponse { Success = false, Message = "对方不在线" });
                return;
            }

            Console.WriteLine($"对战邀请已创建:{inviterId}({displayName}) → {targetId} | invite={invite.InviteId} room={invite.RoomId} addr={invite.HostAddress}:{invite.HostPort}");

            _ = session.Send(MessageID.BattleInviteResponse, new BattleInviteResponse
            {
                Success = true,
                Message = "邀请已发送",
                InviteId = invite.InviteId,
                ExpireAtMs = invite.ExpireAtMs,
                RoomId = invite.RoomId,
            });

            _ = targetSession.Send(MessageID.BattleInviteNotify, new BattleInviteNotify
            {
                InviteId = invite.InviteId,
                FromPlayerId = invite.InviterId,
                FromPlayerName = invite.InviterName,
                HostAddress = invite.HostAddress,
                HostPort = invite.HostPort,
                ExpireAtMs = invite.ExpireAtMs,
                RoomId = invite.RoomId,
            });
        }

        /// <summary>
        /// 回应对战邀请（同意/拒绝）
        /// 归属校验与原子摘除都在 BattleInviteService.TryTake 里完成：并发同意只有一个能成功。
        /// </summary>
        public static void HandleBattleInviteRespond(ClientSession session, ReadOnlySpan<byte> body)
        {
            BattleInviteRespondRequest request = BattleInviteRespondRequest.Parser.ParseFrom(body);

            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.BattleInviteRespondResponse,
                    new BattleInviteRespondResponse { Success = false, Message = "请先登录" });
                return;
            }

            int responderId = session.PlayerId;

            if (!BattleInviteService.TryTake(request.InviteId, responderId, out BattleInvite? invite) || invite == null)
            {
                // 区分"失效"与"越权"：两种都给明确文案，避免客户端只能显示通用错误
                string message = BattleInviteService.TryGet(request.InviteId, out BattleInvite? alive) && alive != null
                    ? "无权回应此邀请"
                    : "邀请已失效或已过期";

                Console.WriteLine($"对战邀请回应失败:{responderId} invite={request.InviteId} | {message}");
                _ = session.Send(MessageID.BattleInviteRespondResponse,
                    new BattleInviteRespondResponse { Success = false, Message = message });
                return;
            }

            (bool found, string responderName) = new AccountRepository().QueryPlayer(responderId);
            string displayName = found && !string.IsNullOrEmpty(responderName) ? responderName : $"玩家{responderId}";

            if (!request.Accept)
            {
                string reason = string.IsNullOrWhiteSpace(request.Reason) ? "已拒绝" : request.Reason.Trim();
                Console.WriteLine($"对战邀请被拒绝:{invite.InviterId} ← {responderId}({displayName}) | {reason}");

                _ = session.Send(MessageID.BattleInviteRespondResponse,
                    new BattleInviteRespondResponse { Success = true, Message = "已拒绝", RoomId = invite.RoomId });

                PushResult(invite.InviterId, new BattleInviteResultNotify
                {
                    InviteId = invite.InviteId,
                    TargetPlayerId = invite.TargetId,
                    TargetPlayerName = displayName,
                    Result = BattleInviteService.ResultDeclined,
                    Message = $"{displayName} {reason}",
                    RoomId = invite.RoomId,
                });
                return;
            }

            Console.WriteLine($"对战邀请已接受:{invite.InviterId} ← {responderId}({displayName}) | addr={invite.HostAddress}:{invite.HostPort}");

            // 先回执被邀请方（他会立刻去连 7777），再把结果通知房主
            _ = session.Send(MessageID.BattleInviteRespondResponse, new BattleInviteRespondResponse
            {
                Success = true,
                Message = "正在进入房间",
                HostAddress = invite.HostAddress,
                HostPort = invite.HostPort,
                HostPlayerId = invite.InviterId,
                HostPlayerName = invite.InviterName,
                RoomId = invite.RoomId,
            });

            PushResult(invite.InviterId, new BattleInviteResultNotify
            {
                InviteId = invite.InviteId,
                TargetPlayerId = invite.TargetId,
                TargetPlayerName = displayName,
                Result = BattleInviteService.ResultAccepted,
                Message = $"{displayName} 已接受邀请，正在连接…",
                RoomId = invite.RoomId,
            });
        }

        /// <summary>
        /// 取消对战邀请（房主主动撤回）
        /// </summary>
        public static void HandleBattleInviteCancel(ClientSession session, ReadOnlySpan<byte> body)
        {
            BattleInviteCancelRequest request = BattleInviteCancelRequest.Parser.ParseFrom(body);

            if (session.PlayerId <= 0)
            {
                _ = session.Send(MessageID.BattleInviteCancelResponse,
                    new BattleInviteCancelResponse { Success = false, Message = "请先登录" });
                return;
            }

            List<BattleInvite> removed = BattleInviteService.Cancel(session.PlayerId, request.InviteId, request.RoomId);
            foreach (BattleInvite invite in removed)
            {
                PushCancelled(invite.TargetId, invite, "房主已取消邀请");
            }

            Console.WriteLine($"对战邀请取消:{session.PlayerId} | 取消{removed.Count}条");
            _ = session.Send(MessageID.BattleInviteCancelResponse, new BattleInviteCancelResponse
            {
                Success = true,
                Message = removed.Count > 0 ? $"已取消 {removed.Count} 条邀请" : "没有待取消的邀请",
                Cancelled = removed.Count,
            });
        }

        // ──────────────────────────────
        // 过期巡检 / 断线清理
        // ──────────────────────────────

        /// <summary>
        /// 启动过期巡检（幂等）：服务启动时调一次
        /// </summary>
        public static void StartExpirySweeper()
        {
            if (sweeper != null) return;
            sweeper = new Timer(_ =>
            {
                try
                {
                    NotifyExpired(BattleInviteService.SweepExpired());
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"对战邀请过期巡检异常 [{exception.GetType().Name}]: {exception.Message}");
                }
            }, null, SweepIntervalMs, SweepIntervalMs);
            Console.WriteLine($"对战邀请服务就绪（TTL {BattleInviteService.TtlMs / 1000}s，巡检 {SweepIntervalMs / 1000}s）");
        }

        /// <summary>
        /// 会话关闭时清理该玩家相关的邀请（房主下线 → 通知被邀请方；被邀请方下线 → 通知房主）
        /// </summary>
        public static void OnPlayerDisconnected(int playerId)
        {
            if (playerId <= 0) return;

            foreach (BattleInvite invite in BattleInviteService.RemoveAsInviter(playerId))
            {
                PushCancelled(invite.TargetId, invite, "房主已离线");
            }

            foreach (BattleInvite invite in BattleInviteService.RemoveAsTarget(playerId))
            {
                PushResult(invite.InviterId, new BattleInviteResultNotify
                {
                    InviteId = invite.InviteId,
                    TargetPlayerId = invite.TargetId,
                    TargetPlayerName = invite.TargetName,
                    Result = BattleInviteService.ResultCancelled,
                    Message = "对方已离线",
                    RoomId = invite.RoomId,
                });
            }
        }

        /// <summary>
        /// 推送"邀请过期"给房主、推送"邀请失效"给被邀请方
        /// </summary>
        private static void NotifyExpired(List<BattleInvite> expired)
        {
            foreach (BattleInvite invite in expired)
            {
                PushResult(invite.InviterId, new BattleInviteResultNotify
                {
                    InviteId = invite.InviteId,
                    TargetPlayerId = invite.TargetId,
                    TargetPlayerName = invite.TargetName,
                    Result = BattleInviteService.ResultExpired,
                    Message = "邀请已过期",
                    RoomId = invite.RoomId,
                });
                PushCancelled(invite.TargetId, invite, "邀请已过期");
            }
        }

        // ──────────────────────────────
        // 推送工具
        // ──────────────────────────────

        /// <summary>把结果推给邀请方（离线则静默丢弃：客户端重进房间时状态自然重置）</summary>
        private static void PushResult(int inviterId, BattleInviteResultNotify notify)
        {
            ClientSession? session = SessionManager.Instance.GetSessionByPlayerId(inviterId);
            if (session == null) return;
            _ = session.Send(MessageID.BattleInviteResultNotify, notify);
        }

        /// <summary>把"邀请已失效"推给被邀请方，让他的弹窗立刻收起（不必等本地倒计时）</summary>
        private static void PushCancelled(int targetId, BattleInvite invite, string message)
        {
            ClientSession? session = SessionManager.Instance.GetSessionByPlayerId(targetId);
            if (session == null) return;
            _ = session.Send(MessageID.BattleInviteCancelledNotify, new BattleInviteCancelledNotify
            {
                InviteId = invite.InviteId,
                RoomId = invite.RoomId,
                Message = message,
            });
        }
    }
}
