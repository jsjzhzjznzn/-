namespace Game.Chat
{
    using MiMieEventBus;

    /// <summary>
    /// 世界频道模块对外事件
    /// </summary>
    public static class WorldChatEvents
    {
        /// <summary>消息列表变化（首页到达 / 更早历史到达 / 新消息推送）</summary>
        public static readonly EventKey Updated = new EventKey("WorldChat.Updated");

        /// <summary>发送失败（UI 回滚乐观消息并提示）</summary>
        public static readonly EventKey<string> SendFailed = new EventKey<string>("WorldChat.SendFailed");
    }
}
