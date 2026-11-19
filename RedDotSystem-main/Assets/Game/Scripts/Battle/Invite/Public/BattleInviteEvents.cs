namespace Game.Battle.Invite
{
    using Common.Network;

    using MiMieEventBus;

    /// <summary>
    /// 对战邀请模块对外事件
    /// </summary>
    public static class BattleInviteEvents
    {
        /// <summary>邀请结果（接受/拒绝/超时等，UI 决定跳转或提示）</summary>
        public static readonly EventKey<BattleInviteResultNotify> Result = new EventKey<BattleInviteResultNotify>("BattleInvite.Result");

        /// <summary>提示文案（发送失败、对方已离开等）</summary>
        public static readonly EventKey<string> Notice = new EventKey<string>("BattleInvite.Notice");
    }
}
