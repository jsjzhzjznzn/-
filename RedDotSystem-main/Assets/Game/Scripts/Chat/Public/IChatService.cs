namespace Game.Chat
{
    using System.Collections.Generic;

    using Common.Network;

    using MieMieFrameWork.Business;

    /// <summary>
    /// 私聊模块公共服务：会话缓存查询 + 发送
    /// 服务端权威，发送失败由 ChatEvents.SendFailed 通知
    /// </summary>
    public interface IChatService : IGameService
    {
        /// <summary>当前打开的会话（好友ID，0 = 未打开）</summary>
        int CurrentConversation { get; set; }

        /// <summary>已缓存的历史会话数</summary>
        int CachedConversationCount { get; }

        /// <summary>某会话是否已拉到历史</summary>
        bool IsConversationLoaded(int friendId);

        /// <summary>打开会话并返回消息列表（未加载时返回空表并触发拉取）</summary>
        IReadOnlyList<ChatMessageItem> OpenConversation(int friendId);

        /// <summary>用服务端未读数初始化红点</summary>
        void SeedUnread(int friendId, int serverUnread);

        /// <summary>确保好友节点存在（红点树用）</summary>
        void EnsureFriendNode(int friendId);

        /// <summary>发送文本</summary>
        void Send(int friendId, string content);

        /// <summary>发送表情</summary>
        void SendSticker(int friendId, string stickerKey);
    }
}
