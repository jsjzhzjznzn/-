using MH;

/// <summary>
/// 聊天系统用到的红点Key别名
/// 代码里不出现裸的 Key14/Key15，改Key时只动这里；动态好友Key的取值约定也集中在这里
/// </summary>
public static class RedDotKeys
{
    /// <summary>好友入口（主页"好友"按钮角标）= 所有子节点之和（聊天未读 + 好友申请）</summary>
    public const ERedDotKeyType FriendEntry = (ERedDotKeyType)14;

    /// <summary>新的朋友（待处理好友申请数量）</summary>
    public const ERedDotKeyType FriendRequests = (ERedDotKeyType)15;

    /// <summary>任务入口（可提交任务数量）</summary>
    public const ERedDotKeyType QuestEntry = (ERedDotKeyType)16;

    /// <summary>成就入口（可领取成就数量；Key17，独立于任务 Key16）</summary>
    public const ERedDotKeyType AchievementEntry = (ERedDotKeyType)17;

    /// <summary>邮箱入口（框架预留 Key5"邮箱"：主界面 ← 邮箱 ← 系统邮件/个人邮件）＝所有子节点之和（未读邮件数）</summary>
    public const ERedDotKeyType MailEntry = (ERedDotKeyType)5;

    /// <summary>系统邮件（未读系统邮件数，写入叶子由 Key5 向上汇总；本期全部邮件都是系统邮件）</summary>
    public const ERedDotKeyType MailSystem = (ERedDotKeyType)8;

    /// <summary>动态好友未读Key：100000 + 好友ID（远离 KeyAsset 配置段，避免冲突）</summary>
    public static ERedDotKeyType FriendKey(int friendId) => (ERedDotKeyType)(100000 + friendId);
}
