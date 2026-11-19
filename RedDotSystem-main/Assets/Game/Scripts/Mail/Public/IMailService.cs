namespace Game.Mail
{
    using System.Collections.Generic;

    using Common.Network;

    using MieMieFrameWork.Business;

    /// <summary>
    /// 邮件模块公共服务：只读查询 + 命令
    /// 服务端权威，附件领取与已读状态都由服务端回执驱动
    /// </summary>
    public interface IMailService : IGameService
    {
        /// <summary>是否正在加载列表</summary>
        bool IsLoading { get; }

        /// <summary>邮箱容量（服务端下发；未拉过列表为 0）</summary>
        int Capacity { get; }

        /// <summary>当前有效邮件数</summary>
        int Count { get; }

        /// <summary>全部有效邮件（新邮件在前）</summary>
        IReadOnlyList<MailInfo> Mails { get; }

        /// <summary>未读邮件数</summary>
        int UnreadCount { get; }

        /// <summary>按邮件ID取邮件；不存在返回 null</summary>
        MailInfo GetMail(int mailId);

        /// <summary>拉取邮件列表</summary>
        void RequestMails();

        /// <summary>标记已读</summary>
        void MarkRead(int mailId);

        /// <summary>一键领取全部附件</summary>
        void Claim(int mailId);

        /// <summary>删除邮件</summary>
        void Delete(int mailId);
    }
}
