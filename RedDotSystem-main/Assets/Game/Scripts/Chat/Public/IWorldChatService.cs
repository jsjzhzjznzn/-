namespace Game.Chat
{
    using System.Collections.Generic;

    using Common.Network;

    using MieMieFrameWork.Business;

    /// <summary>
    /// 世界频道模块公共服务：只读状态 + 命令
    /// </summary>
    public interface IWorldChatService : IGameService
    {
        /// <summary>首页历史是否已加载</summary>
        bool Loaded { get; }

        /// <summary>是否还有更早的历史</summary>
        bool HasMoreHistory { get; }

        /// <summary>是否正在加载更早历史</summary>
        bool IsLoadingOlder { get; }

        /// <summary>频道面板是否处于打开状态</summary>
        bool IsOpen { get; set; }

        /// <summary>当前消息列表（旧的在前）</summary>
        IReadOnlyList<ChatMessageItem> Messages { get; }

        /// <summary>发送冷却剩余秒数</summary>
        float CooldownRemaining { get; }

        /// <summary>打开频道（未加载时触发首页拉取）</summary>
        void Open();

        /// <summary>登录后预热首页历史</summary>
        void Prewarm();

        /// <summary>关闭频道</summary>
        void Close();

        /// <summary>发送文本</summary>
        void Send(string content);

        /// <summary>发送表情</summary>
        void SendSticker(string stickerKey);

        /// <summary>拉取更早的历史</summary>
        void RequestOlder();
    }
}
