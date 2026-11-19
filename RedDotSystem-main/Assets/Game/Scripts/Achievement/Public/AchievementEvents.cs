namespace Game.Achievement
{
    using Common.Network;

    using MiMieEventBus;

    /// <summary>
    /// 成就模块对外事件
    /// </summary>
    public static class AchievementEvents
    {
        /// <summary>成就数据变化（列表到达 / 领取结果 / 进度推送）</summary>
        public static readonly EventKey Updated = new EventKey("Achievement.Updated");

        /// <summary>提示文案（操作成功/失败的 message）</summary>
        public static readonly EventKey<string> Status = new EventKey<string>("Achievement.Status");

        /// <summary>成就解锁推送（成就 Toast 使用）</summary>
        public static readonly EventKey<AchievementUnlockedNotify> Unlocked = new EventKey<AchievementUnlockedNotify>("Achievement.Unlocked");
    }
}
