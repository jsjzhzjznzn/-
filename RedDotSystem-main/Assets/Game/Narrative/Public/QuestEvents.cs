namespace Game.Narrative.Quest
{
    using Common.Network;

    using MiMieEventBus;

    /// <summary>
    /// 叙事任务模块对外事件
    /// </summary>
    public static class QuestEvents
    {
        /// <summary>任务数据变化（快照镜像 / 接取 / 提交 / 放弃 / 推送）</summary>
        public static readonly EventKey Updated = new EventKey("Quest.Updated");

        /// <summary>服务端提示推送（提交成功后的提示文案）</summary>
        public static readonly EventKey<NarrativeQuestToastNotify> Toast = new EventKey<NarrativeQuestToastNotify>("Quest.Toast");
    }
}
