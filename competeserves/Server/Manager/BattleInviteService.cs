using System.Collections.Concurrent;
using System.Diagnostics;

namespace CompeteServer.Manager
{
    /// <summary>
    /// 一条对战邀请（内存记录，不落库）
    ///
    /// 语义：短命信令。服务端重启即全部失效是正确行为——房主的对战房间本来就只活在他自己的进程里，
    /// 服务端重启后那条邀请指向的房间大概率也不在了。
    /// </summary>
    public sealed class BattleInvite
    {
        /// <summary>邀请ID（服务端生成，同意/拒绝/取消时原样带回）</summary>
        public long InviteId;

        /// <summary>邀请方（房主）玩家ID</summary>
        public int InviterId;

        /// <summary>邀请方昵称（创建时解析一次，避免每次推送都查账号表）</summary>
        public string InviterName = string.Empty;

        /// <summary>被邀请方玩家ID</summary>
        public int TargetId;

        /// <summary>被邀请方昵称（创建时解析一次；过期/断线推送也要用，否则会退化成"玩家52"）</summary>
        public string TargetName = string.Empty;

        /// <summary>房主对战地址（房主客户端自报，服务端只做透传）</summary>
        public string HostAddress = string.Empty;

        /// <summary>房主对战端口（当前固定 7777）</summary>
        public int HostPort;

        /// <summary>房主本次房间标识（GUID，用于取消与日志排查）</summary>
        public string RoomId = string.Empty;

        /// <summary>过期时间（Unix 毫秒）</summary>
        public long ExpireAtMs;
    }

    /// <summary>
    /// 对战邀请注册表（纯内存）
    ///
    /// 设计要点：
    ///   1. 一个房主同时只能有 1 条待回应邀请（1v1 房间先到先得会让第二个人拿到"房间已满"）；
    ///   2. 邀请是单次的：同意/拒绝/过期/取消都立即销毁记录，靠 ConcurrentDictionary 的原子摘除防重放；
    ///   3. 1.5s 冷却挡连点；60s TTL 挡挂机；
    ///   4. 归属校验：只有 TargetId 本人能回应/取消，房主本人的身份只认会话绑定。
    /// </summary>
    public static class BattleInviteService
    {
        /// <summary>邀请有效期（毫秒）</summary>
        public const int TtlMs = 60_000;

        /// <summary>同一房主两次邀请的最小间隔（毫秒）</summary>
        public const int InviterCooldownMs = 1_500;

        /// <summary>结果码：对方同意</summary>
        public const int ResultAccepted = 1;
        /// <summary>结果码：对方拒绝</summary>
        public const int ResultDeclined = 2;
        /// <summary>结果码：邀请过期</summary>
        public const int ResultExpired = 3;
        /// <summary>结果码：房主取消 / 房主下线</summary>
        public const int ResultCancelled = 4;

        /// <summary>邀请ID → 邀请</summary>
        private static readonly ConcurrentDictionary<long, BattleInvite> invites = new();

        /// <summary>房主玩家ID → 当前待回应的邀请ID（一条上限的实现手段）</summary>
        private static readonly ConcurrentDictionary<int, long> currentByInviter = new();

        /// <summary>房主玩家ID → 上次成功创建邀请的单调时钟（限频）</summary>
        private static readonly ConcurrentDictionary<int, long> lastInviteTicks = new();

        private static long sequence;

        /// <summary>当前存活的邀请条数（诊断/日志用）</summary>
        public static int Count => invites.Count;

        /// <summary>
        /// 创建邀请（调用方必须先完成：登录校验、好友校验、目标在线校验）
        ///
        /// 这里只做"房间/邀请"层面的记账：参数合法性 → 限频 → 房主单条上限 → 落表。
        /// </summary>
        /// <param name="inviterId">房主玩家ID（来自会话绑定）</param>
        /// <param name="inviterName">房主昵称（调用方已解析）</param>
        /// <param name="targetId">被邀请好友ID</param>
        /// <param name="hostAddress">房主自报的对战地址</param>
        /// <param name="hostPort">房主对战端口</param>
        /// <param name="roomId">房主本次房间标识</param>
        /// <param name="invite">输出：创建成功的邀请</param>
        /// <param name="error">输出：失败原因（中文，可直接下发）</param>
        /// <returns>true=创建成功</returns>
        public static bool TryCreate(
            int inviterId,
            string inviterName,
            int targetId,
            string targetName,
            string hostAddress,
            int hostPort,
            string roomId,
            out BattleInvite? invite,
            out string error)
        {
            invite = null;
            error = string.Empty;

            if (inviterId <= 0 || targetId <= 0)
            {
                error = "参数错误";
                return false;
            }
            if (inviterId == targetId)
            {
                error = "不能邀请自己";
                return false;
            }
            if (string.IsNullOrWhiteSpace(hostAddress) || hostPort <= 0)
            {
                error = "房主对战地址无效";
                return false;
            }

            // 限频：纯内存，不产生数据库访问
            long now = Stopwatch.GetTimestamp();
            if (lastInviteTicks.TryGetValue(inviterId, out long last))
            {
                double elapsedMs = (now - last) * 1000.0 / Stopwatch.Frequency;
                if (elapsedMs < InviterCooldownMs)
                {
                    error = "邀请太频繁，请稍后再试";
                    return false;
                }
            }

            // 房主单条上限：映射指向的邀请若已被销毁，说明是陈旧映射，清掉后继续
            if (currentByInviter.TryGetValue(inviterId, out long existingId) && !invites.ContainsKey(existingId))
            {
                currentByInviter.TryRemove(new KeyValuePair<int, long>(inviterId, existingId));
            }

            long inviteId = Interlocked.Increment(ref sequence);
            BattleInvite record = new()
            {
                InviteId = inviteId,
                InviterId = inviterId,
                InviterName = string.IsNullOrWhiteSpace(inviterName) ? $"玩家{inviterId}" : inviterName,
                TargetId = targetId,
                TargetName = string.IsNullOrWhiteSpace(targetName) ? $"玩家{targetId}" : targetName,
                HostAddress = hostAddress.Trim(),
                HostPort = hostPort,
                RoomId = roomId ?? string.Empty,
                ExpireAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + TtlMs,
            };

            // TryAdd 是"一条上限"的真正闸门：并发创建只有第一个能过
            if (!currentByInviter.TryAdd(inviterId, inviteId))
            {
                error = "已有待回应的邀请，请先取消";
                return false;
            }

            invites[inviteId] = record;
            lastInviteTicks[inviterId] = now;
            invite = record;
            return true;
        }

        /// <summary>
        /// 取出并销毁一条邀请（同意/拒绝共用）
        ///
        /// 归属校验放在摘除之前：第三方即使猜到 inviteId 也不能把别人的邀请销毁掉。
        /// 摘除用"键值对比较"保证并发下只有一个调用者拿到同一条记录。
        /// </summary>
        /// <param name="inviteId">邀请ID</param>
        /// <param name="responderId">回应者玩家ID（必须等于 TargetId）</param>
        /// <param name="invite">输出：被摘除的邀请</param>
        /// <returns>true=摘除成功（调用方可以继续处理）</returns>
        public static bool TryTake(long inviteId, int responderId, out BattleInvite? invite)
        {
            invite = null;

            if (!invites.TryGetValue(inviteId, out BattleInvite? found)) return false;
            if (found.TargetId != responderId) return false;

            if (!invites.TryRemove(new KeyValuePair<long, BattleInvite>(inviteId, found))) return false;

            currentByInviter.TryRemove(new KeyValuePair<int, long>(found.InviterId, inviteId));
            invite = found;
            return true;
        }

        /// <summary>
        /// 读取一条邀请（只读，不摘除；用于错误提示）
        /// </summary>
        public static bool TryGet(long inviteId, out BattleInvite? invite)
        {
            return invites.TryGetValue(inviteId, out invite);
        }

        /// <summary>
        /// 房主主动取消
        /// </summary>
        /// <param name="inviterId">调用者（会话绑定的玩家ID）</param>
        /// <param name="inviteId">指定邀请ID；0 = 按 roomId 批量取消</param>
        /// <param name="roomId">房间标识（inviteId=0 时生效；为空则取消该房主全部邀请）</param>
        /// <returns>被销毁的邀请列表（调用方据此通知被邀请方"邀请已取消"）</returns>
        public static List<BattleInvite> Cancel(int inviterId, long inviteId, string roomId)
        {
            List<BattleInvite> removed = new();

            if (inviteId > 0)
            {
                if (!invites.TryGetValue(inviteId, out BattleInvite? found)) return removed;
                if (found.InviterId != inviterId) return removed;
                if (!invites.TryRemove(new KeyValuePair<long, BattleInvite>(inviteId, found))) return removed;

                currentByInviter.TryRemove(new KeyValuePair<int, long>(inviterId, inviteId));
                removed.Add(found);
                return removed;
            }

            foreach (KeyValuePair<long, BattleInvite> kv in invites)
            {
                if (kv.Value.InviterId != inviterId) continue;
                if (!string.IsNullOrEmpty(roomId) && !string.Equals(kv.Value.RoomId, roomId, StringComparison.Ordinal)) continue;
                if (!invites.TryRemove(new KeyValuePair<long, BattleInvite>(kv.Key, kv.Value))) continue;

                currentByInviter.TryRemove(new KeyValuePair<int, long>(inviterId, kv.Key));
                removed.Add(kv.Value);
            }
            return removed;
        }

        /// <summary>
        /// 玩家下线时清理：他作为房主的邀请全部作废
        /// </summary>
        public static List<BattleInvite> RemoveAsInviter(int inviterId)
        {
            return Cancel(inviterId, 0, string.Empty);
        }

        /// <summary>
        /// 玩家下线时清理：他作为被邀请方的邀请全部作废（房主会收到"对方已离线"）
        /// </summary>
        public static List<BattleInvite> RemoveAsTarget(int targetId)
        {
            List<BattleInvite> removed = new();
            foreach (KeyValuePair<long, BattleInvite> kv in invites)
            {
                if (kv.Value.TargetId != targetId) continue;
                if (!invites.TryRemove(new KeyValuePair<long, BattleInvite>(kv.Key, kv.Value))) continue;

                currentByInviter.TryRemove(new KeyValuePair<int, long>(kv.Value.InviterId, kv.Key));
                removed.Add(kv.Value);
            }
            return removed;
        }

        /// <summary>
        /// 清理过期邀请并返回（通知由调用方负责：这里不碰 SessionManager，保持注册表纯净可测）
        /// </summary>
        public static List<BattleInvite> SweepExpired()
        {
            List<BattleInvite> expired = new();
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            foreach (KeyValuePair<long, BattleInvite> kv in invites)
            {
                if (kv.Value.ExpireAtMs > nowMs) continue;
                if (!invites.TryRemove(new KeyValuePair<long, BattleInvite>(kv.Key, kv.Value))) continue;

                currentByInviter.TryRemove(new KeyValuePair<int, long>(kv.Value.InviterId, kv.Key));
                expired.Add(kv.Value);
            }
            return expired;
        }

        /// <summary>
        /// 账号切换/测试用：清空全部邀请（正常流程不会调用）
        /// </summary>
        public static void Clear()
        {
            invites.Clear();
            currentByInviter.Clear();
            lastInviteTicks.Clear();
        }
    }
}
