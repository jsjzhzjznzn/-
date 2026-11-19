using System;
using System.Collections.Generic;

using Common.Network;

using MH;

using Game.Mail;

using MiMieEventBus;

using MieMieFrameWork;

using UnityEngine;

/// <summary>
/// 邮件数据层（客户端缓存 + 协议收发）
/// 服务端权威：邮件与附件状态全由服务端下发（操作响应回带整表快照），客户端只缓存与分发，不做任何规则判断
/// 缓存：mails = 有效邮件快照（新邮件在前），byId 提供点击反查；UI（MailUIController）只订阅 MailsUpdated 渲染
/// 红点：未读邮件数写入 Key8（系统邮件，叶子节点）→ 由红点树向上汇总到 Key5（邮箱入口角标）；
/// 登录成功即拉列表，面板未开也亮入口角标（微信式，与任务/好友同款）
/// 领取附件后物品进包 → 静默重拉背包（与任务提交同款处理）
/// </summary>
public class MailManager : SingletonBehaviour<MailManager>, IMailService
{
    /// <summary>
    /// 场景加载前同步创建实例。
    /// 必须有这一句：基类的 Instance 是懒创建，而启动链上第一个访问点发生在 await 之后（场景加载中），
    /// 那时新建的 GameObject 会落在尚未加载完成的场景里、Awake 不会被调用（表现为字段全空、Console 报
    /// "Some objects were not cleaned up when closing the scene"）。在 BeforeSceneLoad 同步创建才是安全时机。
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void EnsureCreated()
    {
        _ = Instance;
    }

    // ──────────────────────────────
    // 单例实现
    // ──────────────────────────────



    // ──────────────────────────────
    // 事件（主线程触发，UI 可安全订阅）
    // ──────────────────────────────

    /// <summary>邮件数据变化（列表到达 / 任意操作成功 / 收到新邮件推送，UI 重渲染）</summary>
    /// <summary>全局业务事件总线（邮件对外通知：MailEvents.Updated / MailEvents.Status）</summary>
    public static readonly EventBusCore Bus = new();


    // ──────────────────────────────
    // 私有字段
    // ──────────────────────────────

    /// <summary>有效邮件快照（服务端顺序：新邮件在前）</summary>
    private readonly List<MailInfo> mails = new();

    /// <summary>邮件ID → 邮件（点击反查，避免闭包存数据）</summary>
    private readonly Dictionary<int, MailInfo> mailsById = new();

    /// <summary>邮箱容量（服务端下发，显示"x/N 封"用；未拉过列表为 0）</summary>
    private int capacity;

    /// <summary>是否正在等待列表响应（UI 据此显示加载状态）</summary>
    private bool loading;

    // ──────────────────────────────
    // 对外只读状态
    // ──────────────────────────────

    /// <summary>是否正在加载（打开面板拉列表期间为 true）</summary>
    public bool IsLoading => loading;

    /// <summary>邮箱容量上限（服务端权威，未拉过列表为 0）</summary>
    public int Capacity => capacity;

    /// <summary>当前有效邮件数</summary>
    public int Count => mails.Count;

    /// <summary>全部有效邮件（新邮件在前）</summary>
    public IReadOnlyList<MailInfo> Mails => mails;

    /// <summary>未读邮件数（红点数据源）</summary>
    public int UnreadCount
    {
        get
        {
            int count = 0;
            foreach (MailInfo mail in mails)
            {
                if (!mail.Read) count++;
            }
            return count;
        }
    }

    /// <summary>
    /// 按邮件ID取邮件的方法
    /// </summary>
    /// <param name="mailId">参数名：mailId，类型：int，用途：邮件ID</param>
    /// <returns>返回值类型：MailInfo，用途：邮件（不存在为 null）</returns>
    public MailInfo GetMail(int mailId)
        => mailsById.TryGetValue(mailId, out MailInfo mail) ? mail : null;

    // ──────────────────────────────
    // Unity 生命周期
    // ──────────────────────────────

    protected override void Awake()
    {
        base.Awake();
    }

    private void Start()
    {
        // ClientNetworkManager 的自举同样发生在 BeforeSceneLoad，Start 时必已存在
        if (ClientNetworkManager.Instance == null || ClientNetworkManager.Instance.Network == null) return;
        ClientNetworkManager.Instance.Network.Subscribe<MailListResponse>(OnMailListResponse);
        ClientNetworkManager.Instance.Network.Subscribe<MailReadResponse>(OnMailReadResponse);
        ClientNetworkManager.Instance.Network.Subscribe<MailClaimResponse>(OnMailClaimResponse);
        ClientNetworkManager.Instance.Network.Subscribe<MailDeleteResponse>(OnMailDeleteResponse);
        ClientNetworkManager.Instance.Network.Subscribe<MailGrantResponse>(OnMailGrantResponse);
        ClientNetworkManager.Instance.Network.Subscribe<MailReceiveNotify>(OnMailReceiveNotify);
        ClientNetworkManager.Instance.LoginResponseReceived += OnLoginResponse;
        ClientNetworkManager.Instance.ConnectionBroken += OnConnectionBroken;
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        if (ClientNetworkManager.Instance == null) return;






        ClientNetworkManager.Instance.LoginResponseReceived -= OnLoginResponse;
        ClientNetworkManager.Instance.ConnectionBroken -= OnConnectionBroken;
    }

    // ──────────────────────────────
    // 请求（UI 调这些，不直接碰网络）
    // ──────────────────────────────

    /// <summary>请求邮件列表（打开面板 / 登录成功时调用）</summary>
    public void RequestMails()
    {
        if (ClientNetworkManager.Instance == null) return;
        loading = true;
        Bus.Publish(MailEvents.Updated);   // 先刷一次让 UI 立刻显示加载状态
        _ = ClientNetworkManager.Instance.SendMailListRequest();
    }

    /// <summary>标记已读（点开详情时调用；服务端幂等）</summary>
    /// <param name="mailId">参数名：mailId，类型：int，用途：邮件ID</param>
    public void MarkRead(int mailId)
    {
        if (mailId <= 0 || ClientNetworkManager.Instance == null) return;
        _ = ClientNetworkManager.Instance.SendMailRead(mailId);
    }

    /// <summary>一键领取全部附件（空间不足/重复领取由服务端拒绝）</summary>
    /// <param name="mailId">参数名：mailId，类型：int，用途：邮件ID</param>
    public void Claim(int mailId)
    {
        if (mailId <= 0 || ClientNetworkManager.Instance == null) return;
        _ = ClientNetworkManager.Instance.SendMailClaim(mailId);
    }

    /// <summary>删除邮件（软删；附件未领也允许，UI 侧提示确认）</summary>
    /// <param name="mailId">参数名：mailId，类型：int，用途：邮件ID</param>
    public void Delete(int mailId)
    {
        if (mailId <= 0 || ClientNetworkManager.Instance == null) return;
        _ = ClientNetworkManager.Instance.SendMailDelete(mailId);
    }

    /// <summary>GM 发件（联调用；收件人 0 = 发给自己，面板测试键走这里）</summary>
    /// <param name="targetPlayerId">参数名：targetPlayerId，类型：int，用途：收件玩家ID（0=自己）</param>
    /// <param name="title">参数名：title，类型：string，用途：标题</param>
    /// <param name="body">参数名：body，类型：string，用途：正文</param>
    /// <param name="items">参数名：items，类型：List，用途：附件（物品ID, 数量），可为空</param>
    public void SendGmMail(int targetPlayerId, string title, string body, List<(int ItemId, int Count)> items)
    {
        if (ClientNetworkManager.Instance == null) return;
        _ = ClientNetworkManager.Instance.SendMailGrant(targetPlayerId, title, body, items);
    }

    // ──────────────────────────────
    // 响应处理
    // ──────────────────────────────

    /// <summary>列表响应：整表替换（服务端权威快照），失败只提示不清缓存</summary>
    private void OnMailListResponse(MailListResponse response)
    {
        loading = false;

        if (!response.Success)
        {
            Bus.Publish(MailEvents.Status, response.Message);
            Bus.Publish(MailEvents.Updated);
            return;
        }

        capacity = response.Capacity;
        ApplyMails(response.Mails);
        NotifyMailsChanged();
    }

    private void OnMailReadResponse(MailReadResponse response)
        => ApplyOperationResult(response.Success, response.Message, response.Mails);

    private void OnMailClaimResponse(MailClaimResponse response)
    {
        ApplyOperationResult(response.Success, response.Message, response.Mails);

        // 领取附件物品进包 → 背包数据已过期，静默重拉一次（面板未开时无感）
        if (response.Success) BagManager.Instance?.RequestBag();
    }

    private void OnMailDeleteResponse(MailDeleteResponse response)
        => ApplyOperationResult(response.Success, response.Message, response.Mails);

    /// <summary>GM 发件响应：只提示；发给自己的场景由推送/重拉补齐列表</summary>
    private void OnMailGrantResponse(MailGrantResponse response)
    {
        if (!string.IsNullOrEmpty(response.Message)) Bus.Publish(MailEvents.Status, response.Message);
    }

    /// <summary>
    /// 收到新邮件推送：按邮件ID合并去重（响应重拉与推送可能同帧到达，Replace 语义兜底），
    /// 不在看邮件面板也照常累计——入口红点靠未读数点亮
    /// </summary>
    private void OnMailReceiveNotify(MailReceiveNotify notify)
    {
        if (notify.Mail == null || notify.Mail.MailId <= 0) return;

        int index = mails.FindIndex(mail => mail.MailId == notify.Mail.MailId);
        if (index >= 0) mails[index] = notify.Mail;
        else mails.Insert(0, notify.Mail);   // 新邮件在前

        mailsById[notify.Mail.MailId] = notify.Mail;

        if (!string.IsNullOrEmpty(notify.Message)) Bus.Publish(MailEvents.Status, notify.Message);
        NotifyMailsChanged();
    }

    /// <summary>
    /// 操作响应统一处理：无论成败都用服务端返回的快照整表替换（服务端权威，不做本地预测）；
    /// 失败常见于列表过期或邮件已失效（如公共邮件被撤回后在读路径被软删）——套用快照能让失效条目
    /// 立即从缓存与红点里消失，而不是等下一次拉取
    /// </summary>
    private void ApplyOperationResult(bool success, string message, IEnumerable<MailInfo> snapshot)
    {
        ApplyMails(snapshot);
        if (!string.IsNullOrEmpty(message)) Bus.Publish(MailEvents.Status, message);
        NotifyMailsChanged();
    }

    /// <summary>整表替换并重建索引。</summary>
    private void ApplyMails(IEnumerable<MailInfo> snapshot)
    {
        mails.Clear();
        mailsById.Clear();
        foreach (MailInfo mail in snapshot)
        {
            if (mail.MailId <= 0) continue;
            mails.Add(mail);
            mailsById[mail.MailId] = mail;
        }
    }

    /// <summary>登录成功：清空上一账号的邮件缓存并立即拉取（防串号 + 面板未开也亮入口角标）</summary>
    private void OnLoginResponse(LoginResponse response)
    {
        if (!response.Success) return;

        mails.Clear();
        mailsById.Clear();
        capacity = 0;
        loading = false;
        NotifyMailsChanged();

        RequestMails();
    }

    /// <summary>断线：回执不会再来，必须复位加载态，否则面板永远停在加载中</summary>
    private void OnConnectionBroken()
    {
        loading = false;
        Bus.Publish(MailEvents.Updated);
    }

    /// <summary>
    /// 邮件数据变化统一出口：刷新红点 + 广播给 UI
    /// 所有会改变未读数的路径都必须走这里（漏掉就会出现角标不更新的陈旧红点）
    /// </summary>
    private void NotifyMailsChanged()
    {
        RefreshRedDot();
        Bus.Publish(MailEvents.Updated);
    }

    /// <summary>
    /// 刷新邮箱入口红点
    /// 未读数写入 Key8（系统邮件，叶子节点）→ 红点树自动向上汇总到 Key5（邮箱入口，父节点显示聚合数）；
    /// 写叶子不写父节点（框架规则：父节点的计数由子节点传播，禁止直接 Set 父节点）。
    /// 角标要显示数字必须用 Number 类型（框架只对 Number 显示数字文本，Default 会被隐藏成光点）。
    /// 数量为 0 时框架自动移除该类型，红点熄灭。
    /// 红点系统已改为进程级自举，走 TrySet 后在未就绪时会告警一次而不是静默丢弃
    /// </summary>
    private void RefreshRedDot()
    {
        RedDotSingleton.TrySet(RedDotKeys.MailSystem, UnreadCount, RedDotFlags.Number);
    }
}
