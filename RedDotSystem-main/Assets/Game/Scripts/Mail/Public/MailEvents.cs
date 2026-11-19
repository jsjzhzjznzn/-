namespace Game.Mail
{
    using MiMieEventBus;

    /// <summary>
    /// 邮件模块对外事件
    /// </summary>
    public static class MailEvents
    {
        /// <summary>邮件数据变化（列表到达 / 操作成功 / 新邮件推送）</summary>
        public static readonly EventKey Updated = new EventKey("Mail.Updated");

        /// <summary>提示文案（操作成功/失败的 message）</summary>
        public static readonly EventKey<string> Status = new EventKey<string>("Mail.Status");
    }
}
