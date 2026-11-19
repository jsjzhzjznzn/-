namespace Game.Chat
{
    using MiMieEventBus;

    /// <summary>
    /// 私聊模块对外事件
    /// </summary>
    public static class ChatEvents
    {
        /// <summary>某会话消息变化（参数为好友ID，UI 据此刷新列表）</summary>
        public static readonly EventKey<int> ConversationUpdated = new EventKey<int>("Chat.ConversationUpdated");

        /// <summary>发送失败（参数为好友ID，UI 回滚乐观消息）</summary>
        public static readonly EventKey<int> SendFailed = new EventKey<int>("Chat.SendFailed");
    }
}
