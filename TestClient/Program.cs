// ──────────────────────────────────────────────────────────────
// CompeteTestClient：聊天/好友系统端到端测试假客户端（控制台）
// 帧结构与服务端/Unity客户端一致：[4字节小端长度][4字节消息ID][protobuf体]
//
// 用法：
//   CompeteTestClient --account 账号 --password 密码 --listen
//       登录后常驻监听，打印收到的所有推送（验证对方能收到消息）
//   CompeteTestClient --account 账号 --password 密码 --chat 目标ID --text "内容"
//       登录后给目标发一条消息然后退出
//   CompeteTestClient --account 账号 --password 密码 --chat 目标ID --text "内容" --listen
//       发完继续监听（互发验证最常用）
//   CompeteTestClient --account 账号 --password 密码 --chat 目标ID --sticker 表情键
//       给目标发一条表情包消息（contentType=1，Content 是表情键），用于验证接收端渲染
//   CompeteTestClient --account 账号 --password 密码 --register
//       注册新账号后退出（不做登录）
//   CompeteTestClient --account 账号 --password 密码 --friend-add 目标ID
//       登录后向目标发送好友申请
//   CompeteTestClient --account 账号 --password 密码 --respond 申请人ID --accept | --reject
//       登录后处理一条好友申请（同意/拒绝）
//   CompeteTestClient --account 账号 --password 密码 --friend-list
//       登录后拉取并打印好友列表（Id/名字/在线/未读）
//   CompeteTestClient --account 账号 --password 密码 --requests
//       登录后拉取并打印待处理好友申请列表
//   CompeteTestClient --account 账号 --password 密码 --mail 目标ID --title "标题" --text "正文" [--attach 物品ID:数量,物品ID:数量]
//       登录后以 GM 身份发一封系统邮件（目标ID 0=发给自己；--attach 逗号分隔，可省略=纯文本邮件）
//   CompeteTestClient --account 账号 --password 密码 --mail-list
//       登录后拉取并打印自己的邮箱（ID/已读/已领取/标题/附件）
//   CompeteTestClient --account 账号 --password 密码 --public-mail tag --title "标题" --text "正文" [--attach 物品ID:数量,...] [--min-register|--max-register 毫秒|now]
//       以 GM 身份发布运营公共邮件（延迟创建：玩家拉列表时才实例化；筛选可传 now=当前毫秒）
//   CompeteTestClient --account 账号 --password 密码 --mail-revoke tag
//       以 GM 身份按 tag 撤回公共邮件（软标记；未领附件的已实例化邮件在拉取时被过滤）
//   CompeteTestClient --account 账号 --password 密码 --dungeon-kill 类型
//       登录后上报一次副本击杀（类型 1=直接入包型 2=世界掉落型；掉什么由服务端按规则表 roll）
//   CompeteTestClient --account 账号 --password 密码 --dungeon-pickup 令牌
//       登录后凭令牌拾取一个世界掉落（服务端校验归属与有效期后入包）
//   CompeteTestClient --account 账号 --password 密码 --dungeon-full
//       一口气跑完掉落全流程：击杀A型（直接入包）→ 击杀B型（收令牌）→ 拾取全部令牌
//   CompeteTestClient --account 账号 --password 密码 --activity-list
//       登录后拉取当前活动列表（标题/卡池ID/起止时间）
//   CompeteTestClient --account 账号 --password 密码 --gacha 卡池ID:次数
//       登录后对指定卡池唤取（次数 1 或 10；服务端扣唤取媒介并回背包快照）
//   CompeteTestClient --account 账号 --password 密码 --gacha-history [卡池ID]
//       登录后拉取唤取流水（卡池ID 0 或省略 = 全部卡池）
//   CompeteTestClient --account 账号 --password 密码 --activity-rotate [活动ID]
//       GM 指令：结束当前活动、把目标（省略 = 自动选下一排期）活动提前到现在
//   CompeteTestClient --account 账号 --password 密码 --save-pull duihua
//       登录后拉取自己在 duihua 的场景存档（打印 has_save 与快照字段）
//   CompeteTestClient --account 账号 --password 密码 --save-push duihua --pos 0,0,-6 --rot 15 --cam 30,8,2.8 --dialog DialogueGraph_545_Liuying,12,1 --fired demo_choice_a
//       登录后覆盖式上报场景存档（--pos x,y,z；--rot 度；--cam yaw,pitch,距离；
//       --dialog 图名,节点ID[,1|0]；--fired 逗号分隔；--vars 原始 JSON；NaN/Inf/超大值可用于校验用例）
// ──────────────────────────────────────────────────────────────

using System.Globalization;
using System.Net.Sockets;
using System.Text;

using Common.Enum;
using Common.Network;

using Google.Protobuf;
using MongoDB.Driver;

Console.OutputEncoding = Encoding.UTF8;

// ── 模式：直查 MongoDB messages 集合（测试辅助，不连游戏服务器）──
if (args.Contains("--peek"))
{
    MongoDB.Driver.IMongoClient mongo = new MongoDB.Driver.MongoClient("mongodb://127.0.0.1:27017");
    var messages = mongo.GetDatabase("compete").GetCollection<MongoDB.Bson.BsonDocument>("messages");
    var docs = messages.Find(new MongoDB.Bson.BsonDocument()).SortBy(d => d["_id"]).ToList();
    Console.WriteLine($"[Mongo] messages 共 {docs.Count} 条：");
    foreach (var doc in docs)
    {
        Console.WriteLine($"  conv={doc["conversationId"]} {doc["senderId"]}→{doc["receiverId"]} read={doc["read"]} | {doc["content"]}");
    }
    return;
}

string account = GetArg(args, "--account") ?? string.Empty;
string password = GetArg(args, "--password") ?? string.Empty;
bool wantRemember = args.Contains("--remember");   // 登录请求带 remember_me，回执打印签发的自动登录令牌
string? tokenLoginText = GetArg(args, "--token-login");   // 自动登录：用令牌免密码换会话（无需 --password）
string? targetText = GetArg(args, "--chat");
string? chatText = GetArg(args, "--text");
string? stickerText = GetArg(args, "--sticker");   // 表情包：内容类型=1，Content 传表情键
string? worldText = GetArg(args, "--world");        // 世界频道：公共频道发言（无目标ID）
string? worldBurstText = GetArg(args, "--world-burst");   // 世界频道批量发言条数（配合 --world，每条间隔 3.2s 绕过 3 秒冷却）

// AI Agent（阿福）：--agent "内容" 对话；--agent-raw 不登录直发（验收"未登录拒绝"）；
// --agent-burst N 连发 N 条（每条间隔 3s，验收网关 10 次/分钟限频）；--agent-history 拉最近对话
string? agentText = GetArg(args, "--agent");
bool wantAgentRaw = args.Contains("--agent-raw");
bool wantAgentHistory = args.Contains("--agent-history");
string? agentHistoryBefore = GetArg(args, "--agent-history-before");   // 上拉分页游标测试（毫秒）
string? agentBurstText = GetArg(args, "--agent-burst");
string? autoreplyText = GetArg(args, "--autoreply");
string? friendAddText = GetArg(args, "--friend-add");
string? respondText = GetArg(args, "--respond");
string? mailTargetText = GetArg(args, "--mail");
string? mailTitleText = GetArg(args, "--title");
string? mailAttachText = GetArg(args, "--attach");
string? publicTagText = GetArg(args, "--public-mail");
string? revokeTagText = GetArg(args, "--mail-revoke");
string? dungeonKillText = GetArg(args, "--dungeon-kill");
string? dungeonPickupText = GetArg(args, "--dungeon-pickup");
bool wantDungeonFull = args.Contains("--dungeon-full");
bool wantActivityList = args.Contains("--activity-list");
string? gachaText = GetArg(args, "--gacha");
string? gachaHistoryText = GetArg(args, "--gacha-history");
string? activityRotateText = GetArg(args, "--activity-rotate");
string? signListText = GetArg(args, "--sign-list");       // 拉取签到状态（[活动ID]，省略 = 3号签到活动）
string? signClaimText = GetArg(args, "--sign-claim");     // 签到一次（[活动ID]，省略 = 3号签到活动）
string? grantText = GetArg(args, "--grant");
string? scoreText = GetArg(args, "--score");
bool wantAchievementList = args.Contains("--ach-list");
string? achievementClaimText = GetArg(args, "--ach-claim");
string? achievementGmText = GetArg(args, "--ach-gm");
bool wantBagList = args.Contains("--bag-list");
bool wantBagTidy = args.Contains("--bag-tidy");
string? bagSwapText = GetArg(args, "--bag-swap");
string? mailClaimText = GetArg(args, "--mail-claim");
string? bagDiscardText = GetArg(args, "--bag-discard");
bool wantEquipList = args.Contains("--equip-list");
string? equipText = GetArg(args, "--equip");
string? minRegisterText = GetArg(args, "--min-register");
string? maxRegisterText = GetArg(args, "--max-register");
bool wantFriendList = args.Contains("--friend-list");
bool wantRequests = args.Contains("--requests");
bool wantMailList = args.Contains("--mail-list");
bool wantWorldHistory = args.Contains("--world-history");
bool wantWorldTwice = args.Contains("--world-twice");   // 世界频道连发两条：第二条应被 3 秒冷却拒绝
bool listen = args.Contains("--listen");

// 对战好友邀请：--battle-invite 目标ID [--battle-address IP] [--battle-port 端口]
//               --battle-respond 邀请ID [--reject]
//               --battle-auto-accept / --battle-auto-reject（收到邀请时自动回应，模拟好友点按钮）
string? battleInviteText = GetArg(args, "--battle-invite");
string? battleAddressText = GetArg(args, "--battle-address");
string? battlePortText = GetArg(args, "--battle-port");
string? battleRespondText = GetArg(args, "--battle-respond");
string? battleCancelText = GetArg(args, "--battle-cancel");
bool battleAutoAccept = args.Contains("--battle-auto-accept");
bool battleAutoReject = args.Contains("--battle-auto-reject");

// 场景存档：--save-pull 场景名（拉取） / --save-push 场景名（覆盖式上报）
// --pos x,y,z  --rot 度  --cam yaw,pitch,距离  --dialog 图名,节点ID[,1|0]  --fired key1,key2  --vars 原始JSON
string? savePullScene = GetArg(args, "--save-pull");
string? savePushScene = GetArg(args, "--save-push");
string? savePosText = GetArg(args, "--pos");
string? saveRotText = GetArg(args, "--rot");
string? saveCamText = GetArg(args, "--cam");
string? saveDialogText = GetArg(args, "--dialog");
string? saveFiredText = GetArg(args, "--fired");
string? saveVarsText = GetArg(args, "--vars");

if (account.Length == 0 || (tokenLoginText == null && password.Length == 0))
{
    Console.WriteLine("用法: CompeteTestClient --account 账号 --password 密码 [--remember] [--token-login 令牌] [--chat 目标ID --text 内容|--sticker 表情键] [--listen] [--autoreply 回复内容] [--register] [--friend-add 目标ID] [--respond 申请人ID --accept|--reject] [--friend-list] [--requests] [--mail 目标ID --title 标题 --text 正文 --attach 物品ID:数量,...] [--mail-list] [--public-mail tag --title 标题 --text 正文 --attach 物品ID:数量,... [--min-register|--max-register 毫秒|now]] [--mail-revoke tag] [--dungeon-kill 类型] [--dungeon-pickup 令牌] [--dungeon-full] [--activity-list] [--gacha 卡池ID:次数] [--gacha-history 卡池ID] [--activity-rotate 活动ID] [--agent 内容] [--agent-raw] [--agent-burst N] [--agent-history]");
    return;
}

object sendLock = new();   // 发送串行锁：主线程登录与收包线程自动回复共用
int replyCounter = 0;      // 自动回复计数
var pendingTokens = new System.Collections.Concurrent.ConcurrentQueue<string>();   // B型击杀回执里的拾取令牌（--dungeon-full 自动拾取用）

using TcpClient client = new();
await client.ConnectAsync("127.0.0.1", 8888);
await using NetworkStream stream = client.GetStream();
Console.WriteLine($"[连接] 127.0.0.1:8888");

// 后台收包循环（打印登录结果/回执/推送）
_ = Task.Run(() => ReceiveLoop(stream));

// 保活心跳：服务端 60s 无数据判定僵尸连接并回收，测试工具同样每 15s 发一次 Ping
_ = Task.Run(async () =>
{
    try
    {
        while (true)
        {
            await Task.Delay(15000);
            await SendAsync(stream, MessageID.PingRequest, new PingRequest
            {
                ClientMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
        }
    }
    catch
    {
        // 连接已断，心跳自然停止
    }
});

// ── 模式：注册新账号（注册后不登录，打印结果即退出）──
if (args.Contains("--register"))
{
    await SendAsync(stream, MessageID.RegisterRequest, new RegisterRequest
    {
        Account = account,
        Password = password,
    });
    await Task.Delay(1500);
    Console.WriteLine("[退出]");
    return;
}

// ── 模式：自动登录（--token-login 令牌，免密码换会话，打印结果即退出）──
if (tokenLoginText != null)
{
    await SendAsync(stream, MessageID.TokenLoginRequest, new TokenLoginRequest
    {
        Account = account,
        Token = tokenLoginText,
    });
    await Task.Delay(1500);
    Console.WriteLine("[退出]");
    return;
}

// ── 模式：未登录直发 Agent 消息（--agent "内容" --agent-raw：验收网关"未登录拒绝"，打印结果即退出）──
if (agentText != null && wantAgentRaw)
{
    await SendAsync(stream, MessageID.AgentChatRequest, new AgentChatRequest { Content = agentText });
    Console.WriteLine($"[已发送(未登录)] Agent: {agentText}");
    await Task.Delay(2000);
    Console.WriteLine("[退出]");
    return;
}

// 登录（身份由服务端会话绑定，与真实客户端完全一致；--remember 时服务端签发自动登录令牌随回执下发）
await SendAsync(stream, MessageID.LoginRequest, new LoginRequest
{
    Account = account,
    Password = password,
    RememberMe = wantRemember,
});
await Task.Delay(1500);   // 等登录响应（测试工具用简单等待即可）

// 可选：发送好友申请
if (friendAddText != null && int.TryParse(friendAddText, out int addTargetId))
{
    await SendAsync(stream, MessageID.FriendAddRequest, new FriendAddRequest
    {
        TargetId = addTargetId,
    });
    Console.WriteLine($"[已发送] 好友申请 → 玩家{addTargetId}");
    await Task.Delay(800);
}

// 可选：处理好友申请（--respond 申请人ID，必须配合 --accept 或 --reject）
if (respondText != null && int.TryParse(respondText, out int respondFromId))
{
    bool hasAccept = args.Contains("--accept");
    bool hasReject = args.Contains("--reject");
    if (hasAccept == hasReject)
    {
        Console.WriteLine("[参数错误] --respond 需要且只需要 --accept 或 --reject 之一");
    }
    else
    {
        await SendAsync(stream, MessageID.FriendRespondRequest, new FriendRespondRequest
        {
            FromPlayerId = respondFromId,
            Accept = hasAccept,
        });
        Console.WriteLine($"[已发送] {(hasAccept ? "同意" : "拒绝")} 玩家{respondFromId}的好友申请");
        await Task.Delay(800);
    }
}

// 可选：拉取好友列表
if (wantFriendList)
{
    await SendAsync(stream, MessageID.FriendListRequest, new FriendListRequest());
    await Task.Delay(800);}

// 可选：对战邀请（房主侧）
if (battleInviteText != null && int.TryParse(battleInviteText, out int battleTargetId))
{
    string battleAddress = string.IsNullOrWhiteSpace(battleAddressText) ? "127.0.0.1" : battleAddressText!;
    int battlePort = int.TryParse(battlePortText, out int parsedPort) ? parsedPort : 7777;
    await SendAsync(stream, MessageID.BattleInviteRequest, new BattleInviteRequest
    {
        TargetPlayerId = battleTargetId,
        RoomId = "testclient-room",
        HostAddress = battleAddress,
        HostPort = battlePort,
    });
    Console.WriteLine($"[已发送] 对战邀请 → 玩家{battleTargetId}（{battleAddress}:{battlePort}）");
    await Task.Delay(1200);

    // 同一会话内再发一次：验证服务端"每房主只允许一条待回应邀请"的守卫
    if (args.Contains("--battle-invite-twice"))
    {
        await Task.Delay(600);   // 累计 1.8s，越过 1.5s 冷却，只让"单条上限"生效
        await SendAsync(stream, MessageID.BattleInviteRequest, new BattleInviteRequest
        {
            TargetPlayerId = battleTargetId,
            RoomId = "testclient-room",
            HostAddress = battleAddress,
            HostPort = battlePort,
        });
        Console.WriteLine($"[已发送] 第二条对战邀请（应被服务端拒绝）");
        await Task.Delay(2000);   // 超过 1.5s 冷却，专门验证"每房主一条待回应邀请"的守卫
    }
}

// 可选：取消对战邀请（--battle-cancel 邀请ID，0 = 按房间全取消）
if (battleCancelText != null && long.TryParse(battleCancelText, out long battleCancelId))
{
    await SendAsync(stream, MessageID.BattleInviteCancelRequest, new BattleInviteCancelRequest
    {
        RoomId = "testclient-room",
        InviteId = battleCancelId,
    });
    Console.WriteLine($"[已发送] 取消邀请 {battleCancelId}");
    await Task.Delay(1000);
}

// 可选：回应对战邀请（被邀请方）
if (battleRespondText != null && long.TryParse(battleRespondText, out long battleInviteId))
{
    bool accept = !args.Contains("--reject");
    await SendAsync(stream, MessageID.BattleInviteRespondRequest, new BattleInviteRespondRequest
    {
        InviteId = battleInviteId,
        Accept = accept,
        Reason = accept ? string.Empty : "测试拒绝",
    });
    Console.WriteLine($"[已发送] {(accept ? "同意" : "拒绝")}邀请 {battleInviteId}");
    await Task.Delay(1000);
}

// 可选：拉取待处理申请列表
if (wantRequests)
{
    await SendAsync(stream, MessageID.FriendRequestsRequest, new FriendRequestsRequest());
    await Task.Delay(800);
}

// 可选：发一条消息（--sticker 时按表情包发送：contentType=1，Content 是表情键）
if (targetText != null && (chatText != null || stickerText != null) && int.TryParse(targetText, out int targetId))
{
    bool isSticker = stickerText != null;
    await SendAsync(stream, MessageID.ChatSendRequest, new ChatSendRequest
    {
        TargetId = targetId,
        Content = isSticker ? stickerText! : chatText!,
        ContentType = isSticker ? 1 : 0,
    });
    Console.WriteLine($"[已发送]{(isSticker ? "(表情)" : string.Empty)} → 玩家{targetId}: {(isSticker ? stickerText : chatText)}");
    await Task.Delay(500);   // 等回执打印出来再退出
}

// 可选：世界频道发言（--world "内容"；公共频道无目标ID，服务端落库后广播给全部在线玩家）
if (worldText != null)
{
    await SendAsync(stream, MessageID.WorldChatSendRequest, new WorldChatSendRequest
    {
        Content = worldText,
        ContentType = 0,
    });
    Console.WriteLine($"[已发送] 世界频道: {worldText}");
    await Task.Delay(800);   // 等回执与广播打印出来
}

// 可选：世界频道连发两条（--world 内容 + --world-twice）：验证服务端 3 秒/人冷却
if (worldText != null && wantWorldTwice)
{
    await SendAsync(stream, MessageID.WorldChatSendRequest, new WorldChatSendRequest { Content = worldText, ContentType = 0 });
    Console.WriteLine($"[已发送] 世界频道#1: {worldText}");
    await Task.Delay(200);
    await SendAsync(stream, MessageID.WorldChatSendRequest, new WorldChatSendRequest { Content = worldText + "-第二条", ContentType = 0 });
    Console.WriteLine($"[已发送] 世界频道#2（应被冷却拒绝）: {worldText}-第二条");
    await Task.Delay(1200);
}

// 可选：世界频道批量发言（--world 前缀 + --world-burst N）：每条间隔 3.2s 绕过服务端 3 秒冷却，用于压出长历史
if (worldText != null && worldBurstText != null && int.TryParse(worldBurstText, out int worldBurstCount))
{
    for (int i = 1; i <= worldBurstCount; i++)
    {
        await SendAsync(stream, MessageID.WorldChatSendRequest, new WorldChatSendRequest
        {
            Content = $"{worldText} #{i}",
            ContentType = 0,
        });
        Console.WriteLine($"[已发送] 世界频道批量 #{i}/{worldBurstCount}");
        await Task.Delay(3200);
    }
}

// 可选：拉取世界频道历史（--world-history）
if (wantWorldHistory){
    await SendAsync(stream, MessageID.WorldChatHistoryRequest, new WorldChatHistoryRequest());
    await Task.Delay(800);
}

// 可选：和阿福聊一句（--agent "内容"；LLM 生成需要几秒，等待窗口放宽）
if (agentText != null && !wantAgentRaw)
{
    await SendAsync(stream, MessageID.AgentChatRequest, new AgentChatRequest { Content = agentText });
    Console.WriteLine($"[已发送] Agent: {agentText}");
    await Task.Delay(15000);
}

// 可选：连发 N 条给阿福（--agent 前缀 + --agent-burst N，每条间隔 3s）：验收网关 10 次/分钟限频（第 11 条应被拒）
if (agentText != null && !wantAgentRaw && agentBurstText != null && int.TryParse(agentBurstText, out int agentBurstCount))
{
    for (int i = 1; i <= agentBurstCount; i++)
    {
        await SendAsync(stream, MessageID.AgentChatRequest, new AgentChatRequest { Content = $"{agentText} #{i}" });
        Console.WriteLine($"[已发送] Agent 批量 #{i}/{agentBurstCount}");
        await Task.Delay(3000);
    }
    await Task.Delay(5000);   // 留时间打印最后的回执（含被限频拒绝的那条）
}

// 可选：拉取与阿福的最近对话（--agent-history，跨会话记忆的可见入口）
if (wantAgentHistory)
{
    await SendAsync(stream, MessageID.AgentHistoryRequest, new AgentHistoryRequest { Limit = 20 });
    await Task.Delay(3000);
}

// 可选：带游标拉更早一页（--agent-history-before 毫秒，Phase 5 ③ 上拉分页协议验证）
if (agentHistoryBefore != null && long.TryParse(agentHistoryBefore, out long beforeMs))
{
    await SendAsync(stream, MessageID.AgentHistoryRequest, new AgentHistoryRequest { Limit = 20, BeforeMs = beforeMs });
    await Task.Delay(3000);
}

// 可选：GM 发一封系统邮件（--mail 目标ID 0=自己；正文沿用 --text；附件 --attach 物品ID:数量,...）
if (mailTargetText != null && int.TryParse(mailTargetText, out int mailTargetId))
{
    MailGrantRequest mailRequest = new()
    {
        TargetPlayerId = mailTargetId,
        Title = mailTitleText ?? "测试邮件",
        Body = chatText ?? string.Empty,
    };
    mailRequest.Items.AddRange(ParseAttachList(mailAttachText));
    await SendAsync(stream, MessageID.MailGrantRequest, mailRequest);
    Console.WriteLine($"[已发送] 系统邮件 → 收件人{(mailTargetId == 0 ? "(自己)" : mailTargetId)} 标题[{mailRequest.Title}] 附件{mailRequest.Items.Count}种");
    await Task.Delay(800);
}

// 可选：GM 发布运营公共邮件（--public-mail tag；延迟创建——玩家拉列表时才实例化到个人邮箱）
if (publicTagText != null)
{
    MailPublicGrantRequest publicRequest = new()
    {
        Tag = publicTagText,
        Title = mailTitleText ?? "运营邮件",
        Body = chatText ?? string.Empty,
        MinRegisterMs = ParseTimeArg(minRegisterText),
        MaxRegisterMs = ParseTimeArg(maxRegisterText),
    };
    publicRequest.Items.AddRange(ParseAttachList(mailAttachText));
    await SendAsync(stream, MessageID.MailPublicGrantRequest, publicRequest);
    Console.WriteLine($"[已发送] 公共邮件发布 tag[{publicTagText}] 标题[{publicRequest.Title}] 附件{publicRequest.Items.Count}种");
    await Task.Delay(800);
}

// 可选：GM 撤回运营公共邮件（--mail-revoke tag；软标记，未领附件的已实例化邮件在拉取/领取时被过滤）
if (revokeTagText != null)
{
    await SendAsync(stream, MessageID.MailPublicRevokeRequest, new MailPublicRevokeRequest
    {
        Tag = revokeTagText,
    });
    Console.WriteLine($"[已发送] 撤回公共邮件 tag[{revokeTagText}]");
    await Task.Delay(800);
}

// 可选：拉取自己的邮箱
if (wantMailList)
{
    await SendAsync(stream, MessageID.MailListRequest, new MailListRequest());
    await Task.Delay(800);
}

// 可选：上报一次副本击杀（--dungeon-kill 类型；1=直接入包型 2=世界掉落型）
if (dungeonKillText != null && int.TryParse(dungeonKillText, out int dungeonKillType))
{
    await SendAsync(stream, MessageID.DungeonKillReportRequest, new DungeonKillReportRequest
    {
        EnemyType = dungeonKillType,
    });
    Console.WriteLine($"[已发送] 击杀上报 类型{dungeonKillType}（掉什么由服务端 roll）");
    await Task.Delay(800);
}

// 可选：凭令牌拾取一个世界掉落（--dungeon-pickup 令牌）
if (dungeonPickupText != null)
{
    await SendAsync(stream, MessageID.DungeonPickupRequest, new DungeonPickupRequest
    {
        DropToken = dungeonPickupText,
    });
    Console.WriteLine($"[已发送] 拾取令牌 {dungeonPickupText[..Math.Min(8, dungeonPickupText.Length)]}…");
    await Task.Delay(800);
}

// 可选：副本掉落全流程（击杀A直接入包 → 击杀B收令牌 → 逐个拾取）
if (wantDungeonFull)
{
    await SendAsync(stream, MessageID.DungeonKillReportRequest, new DungeonKillReportRequest { EnemyType = 1 });
    Console.WriteLine("[已发送] 击杀上报 类型1（直接入包）");
    await Task.Delay(1200);

    await SendAsync(stream, MessageID.DungeonKillReportRequest, new DungeonKillReportRequest { EnemyType = 2 });
    Console.WriteLine("[已发送] 击杀上报 类型2（世界掉落）");
    await Task.Delay(1500);   // 等回执把令牌收齐

    string[] tokens = pendingTokens.ToArray();
    Console.WriteLine($"[全流程] 收到令牌 {tokens.Length} 个，开始逐个拾取");
    foreach (string token in tokens)
    {
        await SendAsync(stream, MessageID.DungeonPickupRequest, new DungeonPickupRequest { DropToken = token });
        await Task.Delay(600);
    }

    // 重复拾取第一个令牌：预期失败（令牌已被原子摘除）
    if (tokens.Length > 0)
    {
        await Task.Delay(600);
        await SendAsync(stream, MessageID.DungeonPickupRequest, new DungeonPickupRequest { DropToken = tokens[0] });
        Console.WriteLine("[全流程] 重复拾取同一令牌（预期失败）");
        await Task.Delay(800);
    }

    // 伪造令牌：预期失败
    await SendAsync(stream, MessageID.DungeonPickupRequest, new DungeonPickupRequest { DropToken = "fake_token_should_fail" });
    Console.WriteLine("[全流程] 伪造令牌拾取（预期失败）");
    await Task.Delay(800);
}

// 可选：拉取场景存档（--save-pull 场景名；打印 has_save 与快照字段）
if (savePullScene != null)
{
    await SendAsync(stream, MessageID.SaveStatePullRequest, new SaveStatePullRequest { SceneName = savePullScene });
    Console.WriteLine($"[已发送] 场景存档拉取 场景[{savePullScene}]");
    await Task.Delay(800);
}

// 可选：上报场景存档（--save-push 场景名；覆盖式写入，缺省字段保持 0）
if (savePushScene != null)
{
    SceneSaveSnapshot snapshot = new() { SceneName = savePushScene };

    if (savePosText != null)
    {
        if (TryParseFloatTriple(savePosText, out float posX, out float posY, out float posZ))
        {
            snapshot.PosX = posX;
            snapshot.PosY = posY;
            snapshot.PosZ = posZ;
        }
        else
        {
            Console.WriteLine($"[参数错误] --pos 需要 x,y,z，收到: {savePosText}");
        }
    }

    if (saveRotText != null && TryParseFloatToken(saveRotText, out float rotY))
        snapshot.RotY = rotY;

    if (saveCamText != null)
    {
        if (TryParseFloatTriple(saveCamText, out float camYaw, out float camPitch, out float camDistance))
        {
            snapshot.CamYaw = camYaw;
            snapshot.CamPitch = camPitch;
            snapshot.CamDistance = camDistance;
        }
        else
        {
            Console.WriteLine($"[参数错误] --cam 需要 yaw,pitch,距离，收到: {saveCamText}");
        }
    }
    else
    {
        snapshot.CamDistance = 2.8f;   // 不传 --cam 时给个合理默认，便于验证恢复链路
    }

    if (saveDialogText != null)
    {
        string[] dialogParts = saveDialogText.Split(',');
        if (dialogParts.Length >= 2)
        {
            snapshot.DialogueGraph = dialogParts[0];
            if (int.TryParse(dialogParts[1], out int dialogNodeId)) snapshot.DialogueNodeId = dialogNodeId;
            snapshot.DialogueActive = dialogParts.Length < 3 || dialogParts[2] != "0";
        }
        else
        {
            Console.WriteLine($"[参数错误] --dialog 需要 图名,节点ID[,1|0]，收到: {saveDialogText}");
        }
    }

    if (saveFiredText != null)
    {
        snapshot.DialogueFiredJson = "[" + string.Join(",",
            saveFiredText.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(key => "\"" + key.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) + "]";
    }

    if (saveVarsText != null) snapshot.DialogueVarsJson = saveVarsText;

    await SendAsync(stream, MessageID.SaveStatePushRequest, new SaveStatePushRequest { Snapshot = snapshot });
    Console.WriteLine($"[已发送] 场景存档上报 场景[{snapshot.SceneName}] pos=({snapshot.PosX},{snapshot.PosY},{snapshot.PosZ}) rot={snapshot.RotY} cam=({snapshot.CamYaw},{snapshot.CamPitch},{snapshot.CamDistance}) dialog={snapshot.DialogueActive}/{snapshot.DialogueGraph}#{snapshot.DialogueNodeId}");
    await Task.Delay(1000);
}

// 可选：GM 发放物品（--grant 物品ID:数量，如 --grant 2001:60 发唤取媒介）
if (grantText != null)
{
    string[] grantParts = grantText.Split(':');
    if (grantParts.Length == 2 && int.TryParse(grantParts[0], out int grantItemId) && int.TryParse(grantParts[1], out int grantCount))
    {
        await SendAsync(stream, MessageID.BagGrantRequest, new BagGrantRequest
        {
            ItemId = grantItemId,
            Count = grantCount,
        });
        Console.WriteLine($"[已发送] GM发放 物品{grantItemId} ×{grantCount}");
    }
    else
    {
        Console.WriteLine("[参数错误] --grant 需要 物品ID:数量（如 --grant 2001:60）");
    }
    await Task.Delay(800);
}

// 可选：拉取当前活动列表
if (wantActivityList)
{
    await SendAsync(stream, MessageID.ActivityListRequest, new ActivityListRequest());
    await Task.Delay(800);
}

// 可选：唤取（--gacha 卡池ID:次数[:幂等键]，如 --gacha 1:10）
// 同一幂等键重复发送 = 验证重放（第二次不应再扣媒介/重新 roll）
if (gachaText != null)
{
    string[] parts = gachaText.Split(':');
    if (parts.Length >= 2 && int.TryParse(parts[0], out int gachaBannerId) && int.TryParse(parts[1], out int gachaCount))
    {
        string requestId = parts.Length >= 3 && parts[2].Length > 0
            ? parts[2]
            : "tc_" + Guid.NewGuid().ToString("N");
        await SendAsync(stream, MessageID.GachaDrawRequest, new GachaDrawRequest
        {
            BannerId = gachaBannerId,
            Count = gachaCount,
            RequestId = requestId,
        });
        Console.WriteLine($"[已发送] 唤取 卡池{gachaBannerId} ×{gachaCount} 请求={requestId}");
    }
    else
    {
        Console.WriteLine("[参数错误] --gacha 需要 卡池ID:次数[:幂等键]（如 --gacha 1:10）");
    }
    await Task.Delay(800);
}

// 可选：拉取唤取流水（--gacha-history [卡池ID]，0 或省略 = 全部）
if (gachaHistoryText != null)
{
    int historyBannerId = 0;
    if (gachaHistoryText.Length > 0 && int.TryParse(gachaHistoryText, out int parsedBannerId))
        historyBannerId = parsedBannerId;
    await SendAsync(stream, MessageID.GachaHistoryRequest, new GachaHistoryRequest { BannerId = historyBannerId });
    Console.WriteLine($"[已发送] 唤取流水查询 卡池{(historyBannerId == 0 ? "全部" : historyBannerId.ToString())}");
    await Task.Delay(800);
}

// 可选：GM 轮换活动（--activity-rotate [活动ID]，省略或 0 = 自动切下一排期）
if (activityRotateText != null)
{
    int rotateTarget = 0;
    if (activityRotateText.Length > 0 && int.TryParse(activityRotateText, out int parsedTargetId))
        rotateTarget = parsedTargetId;
    await SendAsync(stream, MessageID.ActivityGmRotateRequest, new ActivityGmRotateRequest { TargetActivityId = rotateTarget });
    Console.WriteLine($"[已发送] GM活动轮换 目标={(rotateTarget == 0 ? "自动下一排期" : $"活动{rotateTarget}")}");
    await Task.Delay(1200);
}

// 可选：拉取签到状态（--sign-list [活动ID]，省略 = 3号签到活动）
if (signListText != null)
{
    int signId = 3;
    if (signListText.Length > 0 && int.TryParse(signListText, out int parsedSignId))
        signId = parsedSignId;
    await SendAsync(stream, MessageID.SignInListRequest, new SignInListRequest { SignId = signId });
    Console.WriteLine($"[已发送] 签到状态查询 活动{signId}");
    await Task.Delay(800);
}

// 可选：签到一次（--sign-claim [活动ID]，省略 = 3号签到活动）
if (signClaimText != null)
{
    int signId = 3;
    if (signClaimText.Length > 0 && int.TryParse(signClaimText, out int parsedSignId))
        signId = parsedSignId;
    await SendAsync(stream, MessageID.SignInClaimRequest, new SignInClaimRequest { SignId = signId });
    Console.WriteLine($"[已发送] 签到 活动{signId}");
    await Task.Delay(800);
}

// 可选：挑战比分上报（--score 90），用于挑战/生涯/成就统计
if (scoreText != null && int.TryParse(scoreText, out int scoreValue))
{
    await SendAsync(stream, MessageID.ScoreSubmitRequest, new ScoreSubmitRequest
    {
        ChallengeId = 1,
        Score = scoreValue,
    });
    Console.WriteLine($"[已发送] 挑战比分 {scoreValue}");
    await Task.Delay(800);
}

// 可选：成就列表（--ach-list）
if (wantAchievementList)
{
    await SendAsync(stream, MessageID.AchievementListRequest, new AchievementListRequest());
    Console.WriteLine("[已发送] 成就列表");
    await Task.Delay(1000);
}

// 可选：领取成就（--ach-claim 4201 或 --ach-claim 4201,4301）
if (achievementClaimText != null)
{
    AchievementClaimRequest claim = new();
    foreach (string part in achievementClaimText.Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        if (int.TryParse(part.Trim(), out int achievementId) && achievementId > 0)
            claim.AchievementIds.Add(achievementId);
    }
    await SendAsync(stream, MessageID.AchievementClaimRequest, claim);
    Console.WriteLine($"[已发送] 领取成就 [{string.Join(',', claim.AchievementIds)}]");
    await Task.Delay(1000);
}

// 可选：成就 GM（--ach-gm 4201:unlock / 4201:reset / 4201:progress:25）
if (achievementGmText != null)
{
    string[] parts = achievementGmText.Split(':');
    if (parts.Length >= 2 && int.TryParse(parts[0], out int gmAchievementId))
    {
        AchievementGmRequest gm = new() { AchievementId = gmAchievementId };
        string mode = parts[1].Trim().ToLowerInvariant();
        if (mode == "unlock") gm.Unlock = true;
        else if (mode == "reset") gm.Reset = true;
        else if (mode == "progress" && parts.Length >= 3 && int.TryParse(parts[2], out int gmProgress))
            gm.Progress = gmProgress;
        await SendAsync(stream, MessageID.AchievementGmRequest, gm);
        Console.WriteLine($"[已发送] 成就GM 成就{gmAchievementId} 模式={mode} 进度={gm.Progress} 解锁={gm.Unlock} 重置={gm.Reset}");
    }
    else
    {
        Console.WriteLine("[参数错误] --ach-gm 需要 成就ID:unlock|reset|progress[:值]");
    }
    await Task.Delay(800);
}

// 可选：背包列表（--bag-list），用于核对槽位与装备实例ID
if (wantBagList)
{
    await SendAsync(stream, MessageID.BagListRequest, new BagListRequest());
    Console.WriteLine("[已发送] 背包列表");
    await Task.Delay(1000);
}

// 可选：领取邮件附件（--mail-claim 邮件ID），验证附件入包时装备建实例
if (mailClaimText != null && int.TryParse(mailClaimText, out int claimMailId))
{
    await SendAsync(stream, MessageID.MailClaimRequest, new MailClaimRequest { MailId = claimMailId });
    Console.WriteLine($"[已发送] 领取邮件 #{claimMailId}");
    await Task.Delay(1000);
}

// 可选：丢弃物品（--bag-discard 槽位:数量），验证穿着的装备不能丢
if (bagDiscardText != null)
{
    string[] parts = bagDiscardText.Split(':');
    if (parts.Length == 2 && int.TryParse(parts[0], out int discardSlot) && int.TryParse(parts[1], out int discardCount))
    {
        await SendAsync(stream, MessageID.BagDiscardRequest,
            new BagDiscardRequest { SlotIndex = discardSlot, Count = discardCount });
        Console.WriteLine($"[已发送] 丢弃 槽{discardSlot} ×{discardCount}");
    }
    else
    {
        Console.WriteLine("[参数错误] --bag-discard 需要 槽位:数量（如 --bag-discard 5:1）");
    }
    await Task.Delay(1000);
}

// 可选：装备列表（--equip-list）
if (wantEquipList)
{
    await SendAsync(stream, MessageID.EquipmentListRequest, new EquipmentListRequest());
    Console.WriteLine("[已发送] 装备列表");
    await Task.Delay(1000);
}

// 可选：整理背包（--bag-tidy），验证装备实例引用在重排后不变
if (wantBagTidy)
{
    await SendAsync(stream, MessageID.BagTidyRequest, new BagTidyRequest());
    Console.WriteLine("[已发送] 整理背包");
    await Task.Delay(1000);
}

// 可选：拖拽换位（--bag-swap 起点:终点），验证实例ID跟着槽位走
if (bagSwapText != null)
{
    string[] parts = bagSwapText.Split(':');
    if (parts.Length == 2 && int.TryParse(parts[0], out int fromSlot) && int.TryParse(parts[1], out int toSlot))
    {
        await SendAsync(stream, MessageID.BagSwapRequest, new BagSwapRequest { FromSlot = fromSlot, ToSlot = toSlot });
        Console.WriteLine($"[已发送] 背包换位 {fromSlot}↔{toSlot}");
    }
    else
    {
        Console.WriteLine("[参数错误] --bag-swap 需要 起点:终点（如 --bag-swap 0:5）");
    }
    await Task.Delay(1000);
}

// 可选：穿戴/卸下（--equip 角色ID:槽位:实例ID，实例ID=0 表示卸下该槽）
if (equipText != null)
{
    string[] parts = equipText.Split(':');
    if (parts.Length == 3
        && int.TryParse(parts[0], out int equipCharId)
        && int.TryParse(parts[1], out int equipSlotType)
        && long.TryParse(parts[2], out long equipInstanceId))
    {
        await SendAsync(stream, MessageID.EquipRequest, new EquipRequest
        {
            CharId = equipCharId,
            SlotType = equipSlotType,
            InstanceId = equipInstanceId,
        });
        Console.WriteLine($"[已发送] 穿戴 角色{equipCharId} 槽{equipSlotType} 实例{equipInstanceId}");
    }
    else
    {
        Console.WriteLine("[参数错误] --equip 需要 角色ID:槽位:实例ID（如 --equip 0:1:88012）");
    }
    await Task.Delay(1000);
}

if (listen)
{
    Console.WriteLine("[监听中] Ctrl+C 退出");
    await Task.Delay(Timeout.Infinite);
}
else if (friendAddText != null || respondText != null || wantFriendList || wantRequests || mailTargetText != null || wantMailList || publicTagText != null || revokeTagText != null || dungeonKillText != null || dungeonPickupText != null || wantDungeonFull || wantActivityList || gachaText != null || gachaHistoryText != null || activityRotateText != null || signListText != null || signClaimText != null || grantText != null || scoreText != null || wantAchievementList || achievementClaimText != null || achievementGmText != null || wantBagList || wantEquipList || equipText != null || wantBagTidy || bagSwapText != null || mailClaimText != null || bagDiscardText != null)
{
    await Task.Delay(1200);   // 非监听的动作模式：留时间给响应打印完
}

Console.WriteLine("[退出]");

// ──────────────────────────────────────────────────────────────
// 收发实现
// ──────────────────────────────────────────────────────────────

async Task ReceiveLoop(NetworkStream stream)
{
    byte[] header = new byte[4];
    while (true)
    {
        if (!await ReadExactly(stream, header, 4))
        {
            Console.WriteLine("[连接断开]");
            return;
        }
        int length = BitConverter.ToInt32(header, 0);
        if (length <= 0 || length > 1024 * 1024) return;

        byte[] data = new byte[length];
        if (!await ReadExactly(stream, data, length))
        {
            Console.WriteLine("[连接断开]");
            return;
        }

        int messageId = BitConverter.ToInt32(data, 0);
        byte[] body = new byte[data.Length - 4];
        Buffer.BlockCopy(data, 4, body, 0, body.Length);

        switch ((MessageID)messageId)
        {
            case MessageID.LoginResponse:
                LoginResponse login = LoginResponse.Parser.ParseFrom(body);
                if (login.Success)
                {
                    string tokenInfo = login.AutoLoginToken.Length > 0
                        ? $" 令牌={login.AutoLoginToken} 过期={DateTimeOffset.FromUnixTimeMilliseconds(login.AutoLoginTokenExpireMs).ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                        : " 无令牌";
                    Console.WriteLine($"[登录成功] 账号={login.PlayerId}{tokenInfo}");
                }
                else
                {
                    Console.WriteLine($"[登录失败] {login.Message}");
                }
                break;

            case MessageID.TokenLoginResponse:
                LoginResponse tokenLogin = LoginResponse.Parser.ParseFrom(body);
                Console.WriteLine(tokenLogin.Success
                    ? $"[自动登录成功] 账号={tokenLogin.PlayerId} 续期至 {DateTimeOffset.FromUnixTimeMilliseconds(tokenLogin.AutoLoginTokenExpireMs).ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                    : $"[自动登录失败] {tokenLogin.Message}");
                break;

            case MessageID.RegisterResponse:
                RegisterResponse register = RegisterResponse.Parser.ParseFrom(body);
                Console.WriteLine(register.Success
                    ? $"[注册成功] {register.Message}"
                    : $"[注册失败] {register.Message}");
                break;

            case MessageID.FriendAddResponse:
                FriendAddResponse friendAdd = FriendAddResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[好友申请结果] success={friendAdd.Success} | {friendAdd.Message}");
                break;

            case MessageID.FriendRespondResponse:
                FriendRespondResponse respond = FriendRespondResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[申请处理结果] success={respond.Success} | {respond.Message}");
                break;

            case MessageID.FriendListResponse:
                FriendListResponse friendList = FriendListResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[好友列表] 共{friendList.Count}人:");
                foreach (FriendInfo friend in friendList.Friends)
                {
                    Console.WriteLine($"  Id={friend.PlayerId} {friend.Name} online={friend.Online} unread={friend.UnreadCount}");
                }
                break;

            case MessageID.FriendRequestsResponse:
                FriendRequestsResponse requests = FriendRequestsResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[待处理申请] 共{requests.Count}条:");
                foreach (FriendInfo requester in requests.Requests)
                {
                    Console.WriteLine($"  Id={requester.PlayerId} {requester.Name}");
                }
                break;

            case MessageID.BattleInviteResponse:
            {
                BattleInviteResponse battleInviteResp = BattleInviteResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[对战邀请结果] success={battleInviteResp.Success} | {battleInviteResp.Message} | invite={battleInviteResp.InviteId} expireIn={battleInviteResp.ExpireAtMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}ms");
                break;
            }

            case MessageID.BattleInviteNotify:
            {
                BattleInviteNotify battleNotify = BattleInviteNotify.Parser.ParseFrom(body);
                Console.WriteLine($"[收到对战邀请] invite={battleNotify.InviteId} 来自 {battleNotify.FromPlayerName}({battleNotify.FromPlayerId}) 地址 {battleNotify.HostAddress}:{battleNotify.HostPort} room={battleNotify.RoomId}");
                if (battleAutoAccept || battleAutoReject)
                {
                    await SendAsync(stream, MessageID.BattleInviteRespondRequest, new BattleInviteRespondRequest
                    {
                        InviteId = battleNotify.InviteId,
                        Accept = battleAutoAccept,
                        Reason = battleAutoAccept ? string.Empty : "自动拒绝测试",
                    });
                    Console.WriteLine($"[自动回应] {(battleAutoAccept ? "同意" : "拒绝")}邀请 {battleNotify.InviteId}");
                }
                break;
            }

            case MessageID.BattleInviteRespondResponse:
            {
                BattleInviteRespondResponse battleRespondResp = BattleInviteRespondResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[邀请回应结果] success={battleRespondResp.Success} | {battleRespondResp.Message} | host={battleRespondResp.HostPlayerName}({battleRespondResp.HostPlayerId}) {battleRespondResp.HostAddress}:{battleRespondResp.HostPort}");
                break;
            }

            case MessageID.BattleInviteResultNotify:
            {
                BattleInviteResultNotify battleResult = BattleInviteResultNotify.Parser.ParseFrom(body);
                Console.WriteLine($"[邀请结果推送] invite={battleResult.InviteId} target={battleResult.TargetPlayerName}({battleResult.TargetPlayerId}) result={battleResult.Result} | {battleResult.Message}");
                break;
            }

            case MessageID.BattleInviteCancelResponse:
            {
                BattleInviteCancelResponse battleCancel = BattleInviteCancelResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[取消邀请结果] success={battleCancel.Success} | {battleCancel.Message} | cancelled={battleCancel.Cancelled}");
                break;
            }

            case MessageID.BattleInviteCancelledNotify:
            {
                BattleInviteCancelledNotify battleCancelled = BattleInviteCancelledNotify.Parser.ParseFrom(body);
                Console.WriteLine($"[邀请已失效] invite={battleCancelled.InviteId} | {battleCancelled.Message}");
                break;
            }

            case MessageID.ChatSendResponse:
                ChatSendResponse sendResponse = ChatSendResponse.Parser.ParseFrom(body);
                Console.WriteLine(sendResponse.Success
                    ? $"[回执] 落库成功 MsgId={sendResponse.Echo?.MessageId} 类型{sendResponse.Echo?.ContentType} 内容=[{sendResponse.Echo?.Content}]"
                    : $"[发送失败] {sendResponse.Message}");
                break;

            case MessageID.ChatReceiveNotify:
                ChatReceiveNotify notify = ChatReceiveNotify.Parser.ParseFrom(body);
                Console.WriteLine($"[收到消息] 来自{notify.Message.SenderId}(类型{notify.Message.ContentType}): {notify.Message.Content}");
                // 自动回复模式：模拟真实聊天对象，收到即回，实现全自动闭环测试
                if (autoreplyText != null)
                {
                    int replyTarget = notify.Message.SenderId;
                    int n = ++replyCounter;
                    await SendAsync(stream, MessageID.ChatSendRequest, new ChatSendRequest
                    {
                        TargetId = replyTarget,
                        Content = $"{autoreplyText} #{n}",
                    });
                    Console.WriteLine($"[自动回复] → 玩家{replyTarget}: {autoreplyText} #{n}");
                }
                break;

            case MessageID.WorldChatSendResponse:
                WorldChatSendResponse worldSend = WorldChatSendResponse.Parser.ParseFrom(body);
                Console.WriteLine(worldSend.Success
                    ? $"[世界回执] 成功 MsgId={worldSend.Echo?.MessageId} 昵称={worldSend.Echo?.SenderName} 内容=[{worldSend.Echo?.Content}]"
                    : $"[世界发送失败] {worldSend.Message}");
                break;

            case MessageID.AgentChatResponse:
                AgentChatResponse agentReply = AgentChatResponse.Parser.ParseFrom(body);
                Console.WriteLine(agentReply.Ok
                    ? $"[阿福回复] 耗时{agentReply.ElapsedMs}ms 内容=[{agentReply.Reply}]"
                    : $"[阿福回复失败] {agentReply.Error}");
                break;

            case MessageID.AgentChatChunk:
                // Phase 5 流式分片：网关 3103 逐段转发，末尾 3102 权威校准（打字机协议链路验证）
                AgentChatChunk agentChunk = AgentChatChunk.Parser.ParseFrom(body);
                Console.WriteLine(agentChunk.Reset
                    ? $"[流式重置] seq={agentChunk.Seq} 本轮前导文本作废"
                    : $"[流式分片] seq={agentChunk.Seq} len={agentChunk.Delta.Length} 增量=[{agentChunk.Delta}]");
                break;

            case MessageID.AgentBriefNotify:
                // Phase 5 ② 登录主动简报：登录成功后服务端主动推的待办汇总
                AgentBriefNotify briefNotify = AgentBriefNotify.Parser.ParseFrom(body);
                Console.WriteLine($"[阿福简报] {briefNotify.Content}");
                break;

            case MessageID.AgentProactiveNotify:
                // 悬浮球 3107 主动提醒：服务端事件触发（邮件附件未领取）→ 阿福主动说话（LLM 生成）
                AgentProactiveNotify proactive = AgentProactiveNotify.Parser.ParseFrom(body);
                Console.WriteLine($"[阿福主动提醒] kind={proactive.Kind} {proactive.CreatedMs} 内容=[{proactive.Content}]");
                break;

            case MessageID.AgentHistoryResponse:
                AgentHistoryResponse agentHistory = AgentHistoryResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[阿福历史] ok={agentHistory.Ok} 共{agentHistory.Items.Count}条 has_more={agentHistory.HasMore}:");
                foreach (AgentChatMessage agentItem in agentHistory.Items)
                {
                    Console.WriteLine($"  [{agentItem.CreatedMs}] {(agentItem.Role == 1 ? "阿福" : "玩家")}: {agentItem.Content}");
                }
                break;

            case MessageID.WorldChatNotify:
                WorldChatNotify worldNotify = WorldChatNotify.Parser.ParseFrom(body);
                Console.WriteLine($"[世界广播] 来自{worldNotify.Message.SenderId}|{worldNotify.Message.SenderName}: {worldNotify.Message.Content}");
                break;

            case MessageID.WorldChatHistoryResponse:
                WorldChatHistoryResponse worldHistory = WorldChatHistoryResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[世界历史] 共{worldHistory.Count}条:");
                foreach (ChatMessageItem item in worldHistory.Messages)
                {
                    Console.WriteLine($"  Id={item.MessageId} 来自{item.SenderId}|{item.SenderName}: {item.Content}");
                }
                break;

            case MessageID.ChatHistoryResponse:
                ChatHistoryResponse history = ChatHistoryResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[历史] 与{history.TargetId}共{history.Count}条");
                break;

            case MessageID.MailGrantResponse:
                MailGrantResponse mailGrant = MailGrantResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[发件结果] success={mailGrant.Success} | {mailGrant.Message}");
                break;

            case MessageID.MailListResponse:
                MailListResponse mailList = MailListResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[邮箱] 共{mailList.Mails.Count}/{mailList.Capacity}封:");
                foreach (MailInfo mail in mailList.Mails)
                {
                    string attach = mail.Items.Count == 0
                        ? "无附件"
                        : string.Join(",", mail.Items.Select(item => $"{item.Name ?? "#" + item.ItemId} x{item.Count}"));
                    Console.WriteLine($"  #{mail.MailId} [{(mail.Read ? "已读" : "未读")}{(mail.Claimed ? "|已领取" : string.Empty)}] {mail.SenderName}·{mail.Title} 附件: {attach}");
                }
                break;

            case MessageID.MailReceiveNotify:
                MailReceiveNotify mailNotify = MailReceiveNotify.Parser.ParseFrom(body);
                Console.WriteLine($"[收到邮件] #{mailNotify.Mail.MailId} {mailNotify.Mail.SenderName}·{mailNotify.Mail.Title} | {mailNotify.Message}");
                break;

            case MessageID.MailPublicGrantResponse:
                MailPublicGrantResponse publicGrant = MailPublicGrantResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[公共邮件发布结果] success={publicGrant.Success} id={publicGrant.PublicMailId} | {publicGrant.Message}");
                break;

            case MessageID.MailPublicRevokeResponse:
                MailPublicRevokeResponse publicRevoke = MailPublicRevokeResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[公共邮件撤回结果] success={publicRevoke.Success} | {publicRevoke.Message}");
                break;

            case MessageID.DungeonKillReportResponse:
                DungeonKillReportResponse killReport = DungeonKillReportResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[击杀回执] success={killReport.Success} | {killReport.Message}");
                foreach (DungeonDropInfo drop in killReport.Drops)
                {
                    string tokenText = drop.DropToken.Length > 0
                        ? $"令牌={drop.DropToken[..8]}… 过期={drop.ExpireMs}"
                        : "(已入包)";
                    Console.WriteLine($"  掉落 {drop.ItemName}×{drop.Count} {tokenText}");
                    if (drop.DropToken.Length > 0) pendingTokens.Enqueue(drop.DropToken);
                }
                break;

            case MessageID.SaveStatePullResponse:
                SaveStatePullResponse savePull = SaveStatePullResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[存档拉取] success={savePull.Success} has_save={savePull.HasSave} | {savePull.Message}");
                if (savePull.HasSave && savePull.Snapshot != null)
                {
                    SceneSaveSnapshot loaded = savePull.Snapshot;
                    Console.WriteLine($"  scene={loaded.SceneName} pos=({loaded.PosX},{loaded.PosY},{loaded.PosZ}) rot={loaded.RotY}");
                    Console.WriteLine($"  cam=({loaded.CamYaw},{loaded.CamPitch},{loaded.CamDistance}) dialogue={loaded.DialogueActive}/{loaded.DialogueGraph}#{loaded.DialogueNodeId}");
                    Console.WriteLine($"  vars={loaded.DialogueVarsJson} fired={loaded.DialogueFiredJson} updated={loaded.UpdatedAtMs}");
                }
                break;

            case MessageID.SaveStatePushResponse:
                SaveStatePushResponse savePush = SaveStatePushResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[存档上报] success={savePush.Success} serverUpdated={savePush.ServerUpdatedAtMs} | {savePush.Message}");
                break;

            case MessageID.DungeonPickupResponse:
                DungeonPickupResponse pickup = DungeonPickupResponse.Parser.ParseFrom(body);
                Console.WriteLine(pickup.Success
                    ? $"[拾取成功] {pickup.Item?.ItemName}×{pickup.Item?.Count} | {pickup.Message}"
                    : $"[拾取失败] {pickup.Message}");
                break;

            case MessageID.ActivityListResponse:
                ActivityListResponse activityList = ActivityListResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[活动列表] success={activityList.Success} | {activityList.Message}");
                foreach (ActivityInfo activity in activityList.Activities)
                {
                    long remainMs = activity.EndAtMs - activityList.ServerNowMs;
                    Console.WriteLine($"  #{activity.ActivityId} [{activity.Title}] {activity.Subtitle} 卡池={activity.BannerId} 内容版本={activity.ContentVersion} 剩余={TimeSpan.FromMilliseconds(remainMs):d\\.hh\\:mm\\:ss}");
                }
                break;

            case MessageID.GachaDrawResponse:
                GachaDrawResponse gacha = GachaDrawResponse.Parser.ParseFrom(body);
                Console.WriteLine(gacha.Success
                    ? $"[唤取结果] {gacha.Message} | 保底={gacha.Pity5Count}/{gacha.Pity5Threshold} 累计={gacha.TotalDraws}抽 大保底={(gacha.GuaranteedUp != 0 ? "是" : "否")}{(gacha.Replayed ? " [重放]" : string.Empty)}"
                    : $"[唤取失败] {gacha.Message}");
                foreach (GachaResultItem item in gacha.Results)
                {
                    string starText = item.Rarity >= 5 ? "5★" : item.Rarity >= 4 ? "4★" : $"{item.Rarity}★";
                    string flags = string.Concat(
                        item.IsUp ? " UP" : string.Empty,
                        PityKindText(item.PityKind),
                        item.IsNew ? " 新获得" : string.Empty);
                    Console.WriteLine($"  {starText} {item.Name}{flags}");
                }
                if (gacha.Success)
                    Console.WriteLine($"  背包快照 {gacha.Slots.Count} 个非空槽（{gacha.Slots.Sum(s => s.Count)} 件物品）");
                break;

            case MessageID.GachaHistoryResponse:
                GachaHistoryResponse gachaHistory = GachaHistoryResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[唤取流水] 共{gachaHistory.Entries.Count}条 | 累计{gachaHistory.TotalDraws}抽 保底={gachaHistory.Pity5Count} 大保底={(gachaHistory.GuaranteedUp ? "是" : "否")}");
                foreach (GachaHistoryEntry entry in gachaHistory.Entries)
                {
                    Console.WriteLine($"  [{entry.BannerId}] {entry.Name}{PityKindText(entry.PityKind)} at={DateTimeOffset.FromUnixTimeMilliseconds(entry.CreatedAtMs).LocalDateTime:MM-dd HH:mm:ss}");
                }
                break;

            case MessageID.SignInListResponse:
                SignInListResponse signList = SignInListResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[签到状态] success={signList.Success} | {signList.Message} | 活动{signList.SignId} 已签 {signList.SignedDays}/{signList.TotalDays} 今日已签={signList.TodaySigned}");
                foreach (SignInDayInfo day in signList.Days)
                    Console.WriteLine($"  第{day.Day}天 {day.Name}×{day.Count} {(day.Claimed ? "[已领]" : "[未领]")}");
                break;

            case MessageID.SignInClaimResponse:
                SignInClaimResponse signClaim = SignInClaimResponse.Parser.ParseFrom(body);
                Console.WriteLine(signClaim.Success
                    ? $"[签到成功] {signClaim.Message} | 本次第{signClaim.Day}天 已签 {signClaim.SignedDays}/{signClaim.TotalDays} 今日已签={signClaim.TodaySigned}"
                    : $"[签到失败] {signClaim.Message}");
                foreach (SignInDayInfo day in signClaim.Days)
                    Console.WriteLine($"  第{day.Day}天 {day.Name}×{day.Count} {(day.Claimed ? "[已领]" : "[未领]")}");
                if (signClaim.Success)
                    Console.WriteLine($"  背包快照 {signClaim.Slots.Count} 个非空槽（{signClaim.Slots.Sum(s => s.Count)} 件物品）");
                break;

            case MessageID.ActivityGmRotateResponse:
                ActivityGmRotateResponse rotate = ActivityGmRotateResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[活动轮换] success={rotate.Success} | {rotate.Message}");
                foreach (ActivityInfo activity in rotate.Activities)
                    Console.WriteLine($"  当前活动 #{activity.ActivityId} [{activity.Title}] 卡池={activity.BannerId}");
                break;

            case MessageID.ActivityChangeNotify:
                ActivityChangeNotify activityNotify = ActivityChangeNotify.Parser.ParseFrom(body);
                Console.WriteLine($"[活动切换推送] {activityNotify.Activities.Count} 个活动:");
                foreach (ActivityInfo activity in activityNotify.Activities)
                    Console.WriteLine($"  #{activity.ActivityId} [{activity.Title}] {activity.Subtitle} 卡池={activity.BannerId} 版本={activity.ContentVersion}");
                break;

            case MessageID.AchievementListResponse:
                AchievementListResponse achList = AchievementListResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[成就列表] success={achList.Success} | {achList.Message} | 配置{achList.Configs.Count} 状态{achList.Achievements.Count} | 总数{achList.Summary.Total} 已解锁{achList.Summary.Unlocked} 已领取{achList.Summary.Claimed} 可领取{achList.Summary.Claimable} 点数{achList.Summary.Points}/{achList.Summary.TotalPoints}");
                foreach (AchievementCfgInfo cfg in achList.Configs)
                {
                    Console.WriteLine($"  成就#{cfg.AchievementId} [{cfg.Name}] 系列={cfg.Series} 稀有度={cfg.Rarity} 点数={cfg.Points} 隐藏={cfg.Hidden} 前置={cfg.PrevAchievementId}");
                    foreach (AchievementConditionInfo condition in cfg.Conditions)
                        Console.WriteLine($"    条件{condition.Seq}: type={condition.ConditionType} p1={condition.Param1} {condition.Current}/{condition.Target} | {condition.Text}");
                }
                foreach (PlayerAchievementInfo state in achList.Achievements)
                    Console.WriteLine($"  玩家成就#{state.AchievementId} state={state.State} progress={state.Progress}/{state.Target} unlocked={state.UnlockedAtMs} claimed={state.ClaimedAtMs}");
                break;

            case MessageID.AchievementClaimResponse:
                AchievementClaimResponse achClaim = AchievementClaimResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[成就领取] success={achClaim.Success} | {achClaim.Message} | 本次领取[{string.Join(',', achClaim.ClaimedIds)}]");
                foreach (AchievementRewardInfo reward in achClaim.Granted)
                    Console.WriteLine($"  奖励 {reward.Name}×{reward.Count}");
                Console.WriteLine($"  汇总: 已解锁{achClaim.Summary.Unlocked}/{achClaim.Summary.Total} 已领取{achClaim.Summary.Claimed} 可领取{achClaim.Summary.Claimable} 点数{achClaim.Summary.Points}/{achClaim.Summary.TotalPoints}");
                break;

            case MessageID.AchievementUnlockedNotify:
                AchievementUnlockedNotify achUnlock = AchievementUnlockedNotify.Parser.ParseFrom(body);
                Console.WriteLine($"[成就解锁推送] {achUnlock.Items.Count} 条:");
                foreach (AchievementUnlockedInfo item in achUnlock.Items)
                    Console.WriteLine($"  解锁 成就#{item.AchievementId} [{item.Name}] +{item.Points}点 稀有度={item.Rarity}");
                break;

            case MessageID.AchievementGmResponse:
                AchievementGmResponse achGm = AchievementGmResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[成就GM] success={achGm.Success} | {achGm.Message} | 可领取={achGm.Summary.Claimable}");
                foreach (PlayerAchievementInfo state in achGm.Achievements)
                    Console.WriteLine($"  成就#{state.AchievementId} state={state.State} progress={state.Progress}/{state.Target}");
                break;

            case MessageID.BagGrantResponse:
                BagGrantResponse bagGrant = BagGrantResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[发放结果] success={bagGrant.Success} | {bagGrant.Message}");
                break;

            case MessageID.BagDiscardResponse:
            {
                BagDiscardResponse discard = BagDiscardResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[丢弃结果] success={discard.Success} | {discard.Message}");
                foreach (BagSlotInfo slot in discard.Slots)
                    Console.WriteLine($"  槽{slot.SlotIndex} 物品{slot.ItemId} ×{slot.Count} 实例={slot.InstanceId}");
                break;
            }

            case MessageID.MailClaimResponse:
            {
                MailClaimResponse mailClaim = MailClaimResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[领取结果] success={mailClaim.Success} | {mailClaim.Message}");
                break;
            }

            case MessageID.BagTidyResponse:
            {
                BagTidyResponse tidy = BagTidyResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[整理结果] success={tidy.Success} | {tidy.Message}");
                foreach (BagSlotInfo slot in tidy.Slots)
                    Console.WriteLine($"  槽{slot.SlotIndex} 物品{slot.ItemId} ×{slot.Count} 实例={slot.InstanceId}");
                break;
            }

            case MessageID.BagSwapResponse:
            {
                BagSwapResponse swap = BagSwapResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[换位结果] success={swap.Success} | {swap.Message}");
                foreach (BagSlotInfo slot in swap.Slots)
                    Console.WriteLine($"  槽{slot.SlotIndex} 物品{slot.ItemId} ×{slot.Count} 实例={slot.InstanceId}");
                break;
            }

            case MessageID.BagListResponse:
            {
                BagListResponse bagList = BagListResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[背包列表] success={bagList.Success} capacity={bagList.Capacity} 非空槽={bagList.Slots.Count}");
                foreach (BagSlotInfo slot in bagList.Slots)
                    Console.WriteLine($"  槽{slot.SlotIndex} 物品{slot.ItemId} ×{slot.Count} 实例={slot.InstanceId}");
                break;
            }

            case MessageID.EquipmentListResponse:
            {
                EquipmentListResponse equipList = EquipmentListResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[装备列表] success={equipList.Success} | {equipList.Message} | 配置={equipList.Configs.Count} 实例={equipList.Instances.Count} 槽位={equipList.Slots.Count}");
                foreach (EquipConfigInfo config in equipList.Configs)
                    Console.WriteLine($"  配置 物品{config.ItemId} 槽={config.SlotType} 武器类型={config.WeaponType} 白名单={config.AllowedChars} 属性={config.AttrJson}");
                foreach (EquipInstanceInfo instance in equipList.Instances)
                    Console.WriteLine($"  实例 {instance.InstanceId} 物品={instance.ItemId} 强化={instance.EnhanceLevel} 穿戴者={instance.EquippedChar} 槽={instance.EquippedSlot}");
                foreach (EquipSlotInfo slot in equipList.Slots)
                    Console.WriteLine($"  槽位 角色{slot.CharId} 槽{slot.SlotType} → 实例{slot.InstanceId}");
                break;
            }

            case MessageID.EquipResponse:
            {
                EquipResponse equip = EquipResponse.Parser.ParseFrom(body);
                Console.WriteLine($"[穿戴结果] success={equip.Success} code={equip.Code} unchanged={equip.Unchanged} | {equip.Message} | 槽位={equip.Slots.Count} 实例={equip.Instances.Count}");
                foreach (EquipSlotInfo slot in equip.Slots)
                    Console.WriteLine($"  槽位 角色{slot.CharId} 槽{slot.SlotType} → 实例{slot.InstanceId}");
                foreach (EquipInstanceInfo instance in equip.Instances)
                    Console.WriteLine($"  实例 {instance.InstanceId} 物品={instance.ItemId} 穿戴者={instance.EquippedChar} 槽={instance.EquippedSlot}");
                break;
            }

            case MessageID.KickoutNotify:
                KickoutNotify kickout = KickoutNotify.Parser.ParseFrom(body);
                Console.WriteLine($"[被顶号] {kickout.Reason}");
                break;

            default:
                Console.WriteLine($"[其他消息] ID={messageId}");
                break;
        }
    }
}

Task SendAsync(NetworkStream stream, MessageID id, IMessage message)
{
    byte[] payload = new byte[4 + message.CalculateSize()];
    BitConverter.GetBytes((int)id).CopyTo(payload, 0);
    message.WriteTo(payload.AsSpan(4));

    byte[] header = BitConverter.GetBytes(payload.Length);
    lock (sendLock)
    {
        stream.Write(header, 0, header.Length);       // 测试工具：锁内同步写，避免收发线程交错
        stream.Write(payload, 0, payload.Length);
    }
    return Task.CompletedTask;
}

async Task<bool> ReadExactly(NetworkStream stream, byte[] buffer, int length)
{
    int offset = 0;
    while (offset < length)
    {
        int read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset));
        if (read <= 0) return false;
        offset += read;
    }
    return true;
}

static string? GetArg(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }
    return null;
}

// 解析 --attach 物品ID:数量,物品ID:数量 为附件列表（格式错误的条目打印告警后跳过）
static List<MailItemInfo> ParseAttachList(string? attachText)
{
    var items = new List<MailItemInfo>();
    if (attachText == null) return items;
    foreach (string piece in attachText.Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        string[] parts = piece.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out int itemId) && int.TryParse(parts[1], out int itemCount))
        {
            items.Add(new MailItemInfo { ItemId = itemId, Count = itemCount });
        }
        else
        {
            Console.WriteLine($"[参数错误] 附件格式应为 物品ID:数量，收到: {piece}");
        }
    }
    return items;
}

// 解析注册时间筛选参数（--min-register/--max-register）："now"=当前毫秒，纯数字=毫秒，缺省=0（不限）
static long ParseTimeArg(string? text)
{
    if (string.IsNullOrWhiteSpace(text)) return 0;
    if (string.Equals(text.Trim(), "now", StringComparison.OrdinalIgnoreCase))
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
    return long.TryParse(text, out long value) ? value : 0;
}

// 解析单个浮点参数：接受 nan / inf / -inf（服务端校验用例用），其余按不变文化解析
static bool TryParseFloatToken(string text, out float value)
{
    string token = text.Trim();
    switch (token.ToLowerInvariant())
    {
        case "nan":
            value = float.NaN;
            return true;
        case "inf":
        case "+inf":
            value = float.PositiveInfinity;
            return true;
        case "-inf":
            value = float.NegativeInfinity;
            return true;
    }
    return float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}

// 解析 "a,b,c" 三个浮点参数（--pos / --cam 共用）
static bool TryParseFloatTriple(string text, out float a, out float b, out float c)
{
    a = 0f;
    b = 0f;
    c = 0f;
    string[] parts = text.Split(',');
    return parts.Length == 3
           && TryParseFloatToken(parts[0], out a)
           && TryParseFloatToken(parts[1], out b)
           && TryParseFloatToken(parts[2], out c);
}

// 保底类型文案（0 无 / 1 5★计数 / 2 十连4★ / 3 大保底）
static string PityKindText(int kind) => kind switch
{
    1 => " 5★保底",
    2 => " 十连4★保底",
    3 => " 大保底",
    _ => string.Empty,
};
