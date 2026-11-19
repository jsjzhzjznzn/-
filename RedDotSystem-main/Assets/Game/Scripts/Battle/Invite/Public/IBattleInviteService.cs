namespace Game.Battle.Invite
{
    using System.Collections.Generic;

    using Common.Network;

    using Cysharp.Threading.Tasks;

    using MieMieFrameWork.Business;

    /// <summary>
    /// 对战邀请模块公共服务：邀请状态 + 命令
    /// 信令走 8888 的 3201~3209，房间本身由 NGO 在 7777 建立
    /// </summary>
    public interface IBattleInviteService : IGameService
    {
        /// <summary>当前待处理的邀请（null = 无）</summary>
        BattleInviteNotify CurrentInvite { get; }

        /// <summary>是否已发出邀请、等待对方回应</summary>
        bool AwaitingResponse { get; }

        /// <summary>当前邀请ID</summary>
        long CurrentInviteId { get; }

        /// <summary>被邀请人ID（0 = 未发出）</summary>
        int PendingTargetId { get; }

        /// <summary>被邀请人名字</summary>
        string PendingTargetName { get; }

        /// <summary>邀请过期时间（毫秒时间戳）</summary>
        long PendingExpireAtMs { get; }

        /// <summary>最近一次使用的房间ID</summary>
        string CurrentRoomId { get; }

        /// <summary>已知玩家ID→名字（邀请列表展示用）</summary>
        IReadOnlyDictionary<int, string> KnownPlayerNames { get; }

        /// <summary>确保有房间ID（没有则生成）</summary>
        string EnsureRoomId();

        /// <summary>作为房主启动房间并取得房间ID</summary>
        UniTask<string> EnsureHostRoomAsync();

        /// <summary>发送邀请；失败时 error 带原因</summary>
        bool SendInvite(int targetPlayerId, string targetName, out string error);

        /// <summary>撤销自己发出的邀请</summary>
        void CancelInvite();

        /// <summary>接受当前邀请</summary>
        void Accept();

        /// <summary>拒绝当前邀请</summary>
        void Decline(string reason = "");

        /// <summary>邀请弹窗关闭（清空当前邀请）</summary>
        void OnPopupClosed();
    }
}
