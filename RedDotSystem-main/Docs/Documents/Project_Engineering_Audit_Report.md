# RedDotSystem 全项目工程审计报告

| 项 | 内容 |
|---|---|
| 审计日期 | 2026-09-21 |
| 审计范围 | Unity 客户端（Assets/Scripts 23 个非生成脚本全量精读）、服务端（competeserves/Server 全部 22 个源文件）、场景/资产/项目设置、Packages、双仓库 git 卫生、protobuf 工具链、文档 |
| 审计方式 | 人工逐文件精读 + 编辑器脚本实测（Canvas/EventSystem/TMP raycastTarget/纹理导入/构建列表）+ 场景 YAML 扫描 + git 追踪状态核查 |
| 定位 | 本报告是**问题清单 + 风险评估 + 推荐方向**，不是修改指令。任何修复实施前必须先满足该项的"验证前置"再动手（见 §4 实施原则）。 |
| 上一状态 | 此前不存在任何审计报告（Assets/Documents 为空，本报告为首轮审计产物） |

---

## 0. 执行摘要

项目已经从"红点系统开源框架"演化为一个**完整的双人联机小游戏**：登录/注册 → 好友（申请/同意）→ 微信式聊天（未读红点）→ 挑战/排行榜 → 背包，客户端（Tuanjie 1.9.3 / Unity 2022.3）+ .NET 8 服务端（MySQL + MongoDB + Valkey），protobuf 三端同步。

**总体健康度：架构良好、工程卫生与安全有硬伤。**

- ✅ **强项**：服务端权威数据模型、SQL 全参数化、会话身份绑定（客户端无法伪造身份）、聊天隐私三层设计、虚拟列表、批量红点传播、MySQL 降级路径、幂等建表种子、协议三端同步脚本。这些是修复期间**不许误伤**的资产（见 §3）。
- 🔴 **最严重**：服务端仓库把含明文 MySQL root 密码的 `run_server.bat` 提交进了 git 历史（P0-1）。
- 🟠 **高危设计**：BagGrant 协议对任意玩家开放（刷物品）、成绩纯客户端上报（刷榜）、明文密码存储（P1 系列）——这些部分是"学习项目"的自觉取舍（代码注释已自知），需要产品级决策而非单纯改代码。
- 🟡 **真实缺陷**：1 个客户端协议类型错误、1 个好友关系竞态（可产生单向好友）、发送异常被吞导致乐观 UI 卡 pending、双 EventSystem 刷屏、2 处 N+1 查询、~14MB 无用资源进包、YooAsset 嵌入包未入库。

| 严重级 | 数量 | 一句话概括 |
|---|---|---|
| P0 立即处理 | 1 | 明文 root 密码已入 git 历史 + 服务端仓库无 .gitignore（bin/obj/log 全被跟踪） |
| P1 高风险（需产品决策） | 4 | BagGrant 刷物品；成绩客户端上报刷榜；密码明文存储；QueryPlayer 未登录可枚举玩家 |
| P2 正确性/稳定性/性能 | 10 | 协议类型错、好友竞态、发送异常链路、半开连接、红点传播丢失、Valkey 慢降级、N+1×2、Resources 冗余、git 例外缺失×2 |
| P3 可维护性/打磨 | 11 | 演示残留、双 EventSystem、死代码、Text/TMP 混用、程序化贴图泄漏、文档漂移、音频缺失等 |

---

## 1. 子系统地图（审计时点的现状）

```
客户端 (D:\tuanjieproject\frined\RedDotSystem-main)
├─ Network/ClientNetworkManager.cs   TCP+protobuf，后台收包/主线程分发（BeforeSceneLoad 自举单例）
├─ Friends/FriendUIController.cs    好友三页+聊天区+输入框自适应+光标跟随（826 行，最大文件）
├─ Chat/ChatManager.cs               会话缓存+乐观发送+未读红点桥接
├─ Bag/Bag{Manager,UIController,SlotView}.cs   背包数据层+虚拟网格+拖拽换位
├─ Challenge/ChallengeUIController.cs 挑战玩法+排行榜双面板（虚拟列表）
├─ UI/VirtualScrollList.cs           通用虚拟滚动（好友/聊天/榜单/背包槽共用）
├─ Resource/ItemIconLoader.cs        Resources.Load 同步图标缓存
├─ RedDot/（MH 红点框架）            RedDotSingleton/RedDotNode/RedDotItem + 配置资产
└─ Editor/CompeteServerAutoStart.cs  进 Play 自动拉起服务端（8888 端口探测）

服务端 (D:\tuanjieproject\frined\competeserves\Server, .NET 8)
├─ Network/ SocketServer(accept 循环) → ClientSession(每连接收包循环, 同步处理) → SessionManager(锁保护, MaxSessions=100, 单账号单会话)
├─ Handler/ MessageHandler(路由+账号+背包) / FriendHandler / ChatHandler / LeaderboardHandler
├─ Database/ AccountRepository / FriendRepository / ChatRepository(Mongo) / LeaderboardRepository(MySQL+Valkey) / BagRepository
└─ Manager/ PlayerManager(在线表) / ValkeyManager(ZSET 容错封装) / MySqlManager / MongoManager

协议 (D:\tuanjieproject\frined\protobuf)  protos\*.proto → build_proto.bat → 三端 Generated 同步
数据   MySQL compete 库（accounts/friendships/scores/item_config/player_items）+ MongoDB compete.messages + Valkey ZSET 榜单
```

---

## 2. 发现项明细

> 证据格式：`文件 → 方法/位置`。行号以 2026-09-21 工作区为准，重构后会漂移，定位以方法名为准。

### P0 — 立即处理（无需权衡）

#### P0-1 明文数据库 root 密码已提交进服务端 git 历史；仓库无 .gitignore 【部分修复 2026-09-21：git 清理已完成并本地提交 4033bb2（未推送）；密码轮换经所有者决策暂缓——已确认仓库无远端、风险仅本机，记为遗留风险】
- **位置**：`competeserves/run_server.bat`；`competeserves/.git`（git ls-files 实测）
- **证据**：
  - `run_server.bat`：`set "CS_DB_USER=root"`、`set "CS_DB_PASSWORD=202503113139"`（直连本机 MySQL root）；
  - `git ls-files` 包含 `run_server.bat`、`server_boot.log`、`Server/bin/Debug/net8.0/CompeteServer.exe|.dll|.pdb` 等全部编译产物；
  - `competeserves` 目录**没有 .gitignore**（Get-Content 为空），`git status` 每次编译都出现十几个 bin/obj 变更。
- **影响**：① root 密码永久留在 git 历史（即使删除文件，历史仍可提取），若仓库曾推送远端即构成泄露；② 二进制入库使仓库膨胀且每次构建污染工作区状态，掩盖真实源码变更（当前 `M Server/Handler/MessageHandler.cs` 混在一堆 dll 变更里）。
- **推荐方向**：
  1. **立即轮换 MySQL root 密码**（无论仓库是否推过远端，按已泄露处理最稳妥）；
  2. `git rm --cached run_server.bat server_boot.log`，并给服务端仓库补标准 .NET .gitignore（bin/obj/log）；
  3. 启动脚本改为读取本地不入库的凭据文件（如 `server_env.bat`，加入 .gitignore），模板 `run_server.example.bat` 入库；
  4. 若确认从未推送远端且仅本机使用，可降级处理，但密码轮换与 rm --cached 仍建议执行。
- **验证前置**：确认仓库是否有远端（`git remote -v`）；确认是否有其他协作者/机器克隆。

### P1 — 高风险（先决策，再动代码）

#### P1-1 BagGrant 协议对任意已登录玩家开放（自助刷物品）
- **位置**：服务端 `Handler/MessageHandler.cs → HandleBagGrant`、`Database/BagRepository.cs → Grant`；客户端 `Bag/BagManager.cs → Grant` + `BagUIController → OnGrantClicked`（UI 常驻"加物品"按钮）
- **证据**：HandleBagGrant 仅校验 `session.PlayerId > 0`；Grant 无来源/频率限制，单次上限 `MaxOperationCount=9999`。任何登录玩家发协议即可无限获得任意物品。
- **影响**：背包语义为"测试发放，本期物品唯一来源"（注释自认），但协议层面它是永久敞开的后门。
- **推荐方向**（按投入从低到高）：① 服务端加环境变量开关 `CS_BAG_GRANT_ENABLED`（默认关，学习/演示环境开）；② 或限制仅对新注册 24h 内账号发放一次引导物资；③ 长期把"发放"移出玩家协议，改为 GM 工具通道。
- **验证前置**：确认产品定位——若纯学习项目且不发布，可仅加开关不改变默认行为；若要发布必须关。

#### P1-2 挑战成绩完全由客户端上报（刷榜）
- **位置**：客户端 `Challenge/ChallengeUIController.cs → EndChallenge`（`SendScoreSubmit(ChallengeId, clickCount)`）；服务端 `LeaderboardHandler → HandleScoreSubmit`
- **证据**：点击计数在客户端内存累加；服务端只做范围校验（`0 ≤ score ≤ MaxScore=1_000_000`），无从验证真实性。
- **影响**：改包客户端可任意上报；当前"最高分模式+排行榜"的竞技意义建立在信任之上。
- **推荐方向**：① 接受现状并文档标注（学习项目）；② 最小校验：服务端限频（每玩家每 N 秒仅接受一次上报）+ 按挑战时长做物理上限（5 秒内人类点击上限约 50~60 次/秒 → 上限 300）；③ 完整方案需把挑战逻辑服务端化（超出当前阶段）。
- **验证前置**：与 P1-1 一并做产品决策；若选 ②，先人工实测人类点击频率分布再定上限。

#### P1-3 密码明文存储与明文传输
- **位置**：服务端 `Database/AccountRepository.cs → Login/Register`（`SELECT ... WHERE Account=@Account AND Password=@Password`；注释自述"学习项目采用明文对比"）；客户端 `LoginSceneController/SignupController`（TCP 明文发送）
- **证据**：accounts 表存明文密码；无 TLS；登录失败统一提示"账号或密码错误"（已防账号枚举，这点做得对）。
- **影响**：数据库泄露即密码泄露；TCP 链路可被嗅探。
- **推荐方向**：① 存储侧：注册改 PBKDF2/BCrypt 加盐哈希 + `Login` 改哈希比对；需要一次性迁移脚本（`UPDATE accounts SET Password=<hash>`，用迁移 005）；② 传输侧：学习项目可暂不做 TLS，但要在文档标注；③ 顺带：Register 无密码强度/长度校验（空串由客户端挡，服务端未挡）。
- **验证前置**：确认所有既有账号（test/player8229/yyy/y/ttt 等）可接受"重置密码"式迁移（无法从明文反推哈希原文以外的信息，需全员改密或一次性批量哈希——因当前就是明文，可直接批量哈希迁移，无感）。

#### P1-4 QueryPlayer 未登录即可枚举全部玩家账号名 【已修复 2026-09-21：补登录校验；raw TCP 未登录测试返回 found=false 无姓名，登录态查询正常】
- **位置**：服务端 `Handler/FriendHandler.cs → HandleQueryPlayer`
- **证据**：该方法**没有** `session.PlayerId <= 0` 拦截（同文件 HandleFriendList/HandleFriendAdd/HandleFriendRespond 都有），任何 TCP 连接无需登录即可按 ID 遍历拿账号名。
- **影响**：用户名枚举，为撞库/骚扰提供名单；与服务端其余 handler 的登录门槛不一致。
- **推荐方向**：补一行登录校验 + 回 `QueryPlayerResponse{Found=false}`（或错误响应）。
- **验证前置**：无（与既有行为只差"未登录场景"，客户端总是在登录后才用该功能，回归成本低）。

### P2 — 正确性 / 稳定性 / 性能缺陷

#### P2-1 客户端发送好友申请列表请求用了错误的消息类型 【已修复 2026-09-21：根因是 proto 缺失 FriendRequestsRequest 消息定义——补 LoginPacket.proto + build_proto.bat 三端生成，客户端改用正确类型；E2E 回归通过】
- **位置**：客户端 `Network/ClientNetworkManager.cs → SendFriendRequestsRequest`
- **证据**：`return SendAsync(MessageID.FriendRequestsRequest, new FriendRequestsResponse());` —— 消息 ID 正确，但消息体实例是 **Response** 类型。当前侥幸可用：两者（默认实例）protobuf 序列化均为 0 字节，服务端按 Request 解析空体成功。一旦任一类型增加字段即静默错乱。
- **推荐方向**：改为 `new FriendRequestsRequest()`。
- **验证前置**：改后跑"新的朋友页拉取列表"E2E（用 TestClient 或双客户端确认 2111/2112 流程）。

#### P2-2 好友"互发申请"竞态 → 可产生单向好友 【已修复 2026-09-21：TestClient 扩展好友/注册动词后先复现（A 列表有 B、B 列表空），再修（反向 pending 拦截 + INSERT ON DUPLICATE KEY UPDATE 升级既有行），修复后回归双向对称。遗留：复现用的 11/12 账号对留下历史单向行，可用 SQL 手动修正】
- **位置**：服务端 `Database/FriendRepository.cs → AddFriendRequest / RespondToRequest`
- **证据**：
  - AddFriendRequest 第③步只查 `(OwnerId=@Me AND FriendId=@Target AND Status=0)`（我发出的重复申请），**不查反向** `(OwnerId=@Target AND FriendId=@Me AND Status=0)`（对方已申请我）；
  - RespondToRequest 同意分支用 `INSERT IGNORE INTO friendships (OwnerId=@Me, FriendId=@From, Status) VALUES (@Me,@From,1)` —— 若 A→B 行已存在（Status=0），INSERT IGNORE **静默忽略**，该行停在 0。
- **场景复现**：A 给 B 发申请时，B 恰好也给 A 发了申请（两条 Status=0 互指）；B 同意 A 的申请 → B→A 行变 1，但 A→B 行仍是 0（INSERT IGNORE 不升级既有行）→ B 的好友列表有 A、A 的列表没有 B（AreFriends 判"任一方向"为真，聊天仍可发，但列表 UI 不对称且 B 的待处理列表永远残留）。
- **推荐方向**：① AddFriendRequest 增查反向 pending，命中则返回"对方已向你发过申请，请在'新的朋友'中处理"（不写库）；② RespondToRequest 同意分支把反向补偿改为 `INSERT ... ON DUPLICATE KEY UPDATE Status = 1`（或先 UPDATE 后补插）。
- **验证前置**：用 TestClient 写双端复现脚本（两账号互发→同意→各自拉列表断言对称）；先复现再改，改后同脚本回归。

#### P2-3 好友申请异常分支返回错误文案 【已修复 2026-09-21：改为"发送失败，请稍后再试"；编译通过】
- **位置**：服务端 `Database/FriendRepository.cs → AddFriendRequest` 的 catch 分支
- **证据**：`return (false, "注册失败，请稍后再试");` —— 从 AccountRepository.Register 复制粘贴的残留，用户在"添加朋友"页看到"注册失败"。
- **推荐方向**：改为"发送失败，请稍后再试"。
- **验证前置**：无（纯文案）。

#### P2-4 TCP 发送异常被 fire-and-forget 吞掉，聊天乐观 UI 可能卡死在 pending 【已修复 2026-09-21：SendRawAsync 捕获发送异常→提示+Disconnect；新增 ConnectionBroken 事件（接收循环结束也触发）；ChatManager 全量回滚在途 pending 并广播 SendFailed。杀服务端 E2E 通过：回滚 1 会话、SendFailed 触发、MyPlayerId 清零】
- **位置**：客户端 `Network/ClientNetworkManager.cs → SendRawAsync`（无 try/catch）；`Chat/ChatManager.cs → Send`（先把 pending 入队 inflight，再 `_ = SendChatSend(...)`）；全部 UI 调用均为 `_ = SendXxx()` 模式
- **证据**：断线/服务端被杀时 `stream.WriteAsync` 抛 IOException → Task 被丢弃 → 异常不可观察（Unity 仅可能打 UnobservedTaskException）；ChatManager 已把消息计入 `inflight` 队列，但对应回执永不到达 → 该 pending 气泡永远显示、不发回滚，且**后续**正常回执会与错误的 pending 配对（FIFO 错位）。
- **推荐方向**：① SendRawAsync 内部 catch 异常 → 经 statusQueue 报"发送失败"+ 置 `receiving=false` 触发断线语义；② ChatManager.Send 在发送抛错时同步移除 pending 并触发 SendFailed（把入队 inflight 移到发送成功之后，或给 Send 传回调）。
- **验证前置**：写"发送中途杀服务端"的断线 E2E（预期：气泡回滚 + 状态栏提示 + 不影响下一条消息配对）。

#### P2-5 `IsConnected` 半开连接陷阱 + 无心跳 【已修复 2026-09-21（发送失败=断线信号 + 接收循环结束统一宣告 ConnectionBroken；TCP keepalive 心跳仍为可选增强未做）】
- **位置**：客户端 `ClientNetworkManager.cs → IsConnected => client != null && client.Connected`
- **证据**：`TcpClient.Connected` 只反映**最后一次 IO** 的状态；拔网线/服务端进程被杀的瞬间它仍为 true，`MyPlayerId` 也残留非 0 → UI 继续允许"发送/挑战上报"，实际全部发进黑洞（与 P2-4 叠加）。
- **推荐方向**：① 发送失败即视为断线（配合 P2-4）；② 可选：TCP keepalive（SocketOptionName.KeepAlive）或应用层心跳；③ 断线时把 `StatusChanged("连接断开")` 推给 UI（现状依赖 ReceiveLoop 抛异常才触发，通常够用）。
- **验证前置**：确认修复后"杀服务端→客户端提示→重启服务端→重连→重登录"全链路可用。

#### P2-6 红点 Key14 好友入口出现"陈旧汇总"传播丢失（已知未修） 【加固 2026-09-21：成因路径已定位并堵住——批量模式（BeginBatch/EndBatch）无 try/finally 保护，中途抛异常会永久卡住批量标志，此后所有 Set 只登记脏节点不再向上传播；机制实测可精确复现"入口陈旧=1而子归零"症状（批外点亮→卡批清零→父停留1），FriendUIController 已改 try/finally 配对，另新增 RedDotSingleton.DumpTree() 诊断工具（count/recount 对照列）。真实链路回归全绿：推送→点亮→打开→清零→全树 count==recount。原始触发场景已不可复现，若再现用 DumpTree 一眼定位断点】
- **位置**：`RedDot/RedDotCore/RedDotSingleton.cs（EndBatch/MarkNodeDirty）+ RedDotNode.cs（CheckRedDotCount）`
- **证据**：2026-09-21 实测观察到好友入口 Key14 显示 1，而其全部子节点（Key15+动态好友节点）已归零——父节点汇总与子节点不一致且未自动收敛。诊断手段已具备（rdCounts/parents 哈希 dump + `CheckRedDotCountNoPropagation` 手工重算对照）。
- **影响**：入口角标常亮不熄，直接伤害红点系统的核心价值（全项目围绕它构建）。
- **推荐方向**：先定向复现（结合 BeginBatch/EndBatch 时序、RemoveChildNode 与 Set 的交错、多父链深度排序），怀疑点：① EndBatch 深度排序对"共享祖先"漏算；② RemoveChildNode 摘链后父节点未重算。补一个最小回归场景后修。
- **验证前置**：这是行为级 bug，修复必须带可复现脚本（红点 Set/Remove 序列 → 断言父节点收敛），不接受"看起来好了"。

#### P2-7 Valkey 不可用时每条命令串行等 ~2s（请求级联 4~6s） 【已修复 2026-09-21：ValkeyManager 加熔断——任一 Try* 命令失败进入 30s 冷却，冷却期 GetDatabase 直接返回 null 快速降级。实测无 Valkey：第一次榜单 4.07s（含两次 ~2s 超时并触发熔断）→ 第二次榜单 0.01s、成绩上报 0.01s（修复前均为 4~6s）】
- **位置**：服务端 `Manager/ValkeyManager.cs → GetDatabase()`（每次 Try* 都检查 `multiplexer.IsConnected` 并尝试 2s 连接）；`LeaderboardRepository → GetLeaderboard`（一次请求最多 5 次 Try* 调用）
- **证据**：无 Valkey 时榜单/上报响应晚 4~6 秒（本项目已实测多次，E2E 必须用轮询等待的根因）。
- **影响**：降级期间用户体验劣化；更重要的是**重连尝试无冷却**——Valkey 明明永久离线，每条命令仍白等 2s。
- **推荐方向**：ValkeyManager 加简单熔断——连接失败后记录 `downUntil = now + 30s`，期间 GetDatabase 直接返回 null（降级路径已能容错）；到期后放行一次重试。
- **验证前置**：确认熔断不会拖慢"Valkey 恢复后 30s 内"的重建（可接受）；降级榜单 E2E 回归。

#### P2-8 两处 N+1 查询（好友未读、榜单玩家名） 【已修复 2026-09-21：好友未读改 ChatRepository.CountUnreadBatch（$in+$group 一次聚合，实测 unread 与 Mongo 事实精确一致）；榜单玩家名改 AccountRepository.GetNamesByIds（动态参数化 IN，TopN≤100 一次往返；实测榜单名字正确非空）】
- **位置**：服务端 `Handler/FriendHandler.cs → HandleFriendList`（每好友一次 `chatRepository.CountUnread` Mongo count）；`Handler/LeaderboardHandler.cs → HandleLeaderboard`（每榜单行一次 `accounts.QueryPlayer` MySQL，Top100=100 次往返）
- **证据**：好友 12 人 = 12 次 Mongo count/每次拉列表；榜单 100 行 = 100 次单行 SELECT。
- **影响**：当前数据量无感，但这是随规模线性恶化的热点（好友列表每开一次面板都跑）。
- **推荐方向**：① 未读数改一次聚合（`$match conversationId in [...]` + `$group by receiverId` 或按 (conversationId,receiverId,read) 建计数）；② 榜单名字改 `WHERE Id IN (...)` 一次取回再内存配对。
- **验证前置**：好友数/榜单条数小，改前先确认不值得过早优化——建议与 P2-7 一起做（同属"榜单/列表请求延迟"主题）。

#### P2-9 Resources 目录制冗余 ~14MB（含 7 张 2356×1440 无用大图） 【已修复 2026-09-21（超预期）：实测 64 张中 46 张未被 item_config 引用，全部经 AssetDatabase.MoveAsset 移至 Assets/Art/Archive（GUID 保留、引用不断），22.3MB 移出 Resources 随包发布；18 张保留图标 maxTextureSize 统一收敛 512；ItemIconLoader 冒烟 18/18 可加载】
- **位置**：`Assets/Resources/ui`（64 张纹理，磁盘 23.8MB）；item_config 实际引用的图标键仅 18 个
- **证据**（编辑器实测）：
  - 未被 item_config 引用却随包发布的：`T_LuckdraWeapon01~07.png`（2356×1440 白底展示图，共 ~10.8MB——早期图标方案残留，DB 种子已切换为 T_Luckdraw21xxxxx_UI）、`00000002A7D71578.png`（2.8MB）等约 46 张；
  - 全部纹理 maxTextureSize=2048（图标类应 ≤512）；
  - 旧目录 `Assets/Resource/Card`（24 张卡图 10MB）不在 Resources 内、不进包，属演示残留（并入 P3-5 处理）。
- **影响**：Resources 目录内容**无条件进包**；本项目 0 个场景引用这些大图，纯粹是死重。
- **推荐方向**：① 把未引用图移出 `Assets/Resources`（如 `Assets/Art/Archive/`），或删除 T_LuckdraWeapon01-07（仓库历史仍可找回）；② 图标类导入预设 maxSize=512；③ 若走 YooAsset（见 P3-9），图标整体迁出 Resources 是更彻底的解法。
- **验证前置**：移动前 grep 确认无代码引用（ItemIconLoader 仅两个目录、失败记忆机制保证找不到不炸；服务端 icon_key 清单以 BagRepository.SeedItemConfigs 为准）。

#### P2-10 YooAsset 嵌入包被 .gitignore 排除（克隆即缺包）+ UnityInsight 索引库被跟踪 【已修复 2026-09-21：.gitignore 加 `!/Packages/com.tuyoogame.yooasset/` 例外并提交（433ea04，1600 文件入库）；UnityInsight db 已 git rm --cached】
- **位置**：`.gitignore:80`（`/[Pp]ackages/*`，仅白名单 manifest.json/packages-lock.json）；`.codely-cli/UnityInsight/index.*.db`
- **证据**：`git check-ignore -v` 确认 `Packages/com.tuyoogame.yooasset/**` 命中忽略规则；`git ls-files Packages/` 只有 manifest/lock 两个文件 → 3.0.5 嵌入包不在版本控制。另一侧 `git status` 常驻 `M .codely-cli/UnityInsight/index.bbb7...db`——该文件在忽略规则生效**前**已被提交（规则不作用于已跟踪文件）。
- **影响**：任何人克隆 Unity 仓库后工程因缺 YooAsset 包而无法还原（虽然当前代码未使用它，但 packages-lock 引用 embedded 源）；UnityInsight db 每次索引更新都污染 git status。
- **推荐方向**：`.gitignore` 加例外 `!/Packages/com.tuyoogame.yooasset/`；`git rm --cached .codely-cli/UnityInsight/*.db`。
- **验证前置**：无（不影响运行时）。

### P3 — 可维护性与打磨

#### P3-1 双 EventSystem（每帧警告刷屏，历史遗留） 【已修复 2026-09-21：删除 FriendUIController 的 BeforeSceneLoad 自建版本，保留 Start 守卫；Play 模式日志实测 0 条 "2 event systems" 警告】
- **位置**：`Friends/FriendUIController.cs → EnsureEventSystem（[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]）+ Start 兜底`；`LOGIN.scene` 与 `zhuye.scene` 各自带 1 个 EventSystem（YAML/编辑器实测 zhuye 场景内 EventSystem 数=1）
- **证据**：BeforeSceneLoad 时机早于场景对象 Awake——从 LOGIN 进 Play 时此刻 `EventSystem.current==null` → 创建 `EventSystem_Persistent`（DontDestroyOnLoad）→ LOGIN 场景加载后又出现自己的 EventSystem → 双活，每帧 "There are 2 event systems in the scene"（编辑器实测复现过，污染日志数千行）。
- **推荐方向**：删除 BeforeSceneLoad 版本（zhuye 场景已有自带 EventSystem；Start 兜底保留可防场景被误删 ES 的情况）；或改 `AfterSceneLoad`。
- **验证前置**：三路径回归——①LOGIN 直接 Play；②zhuye 直接 Play；③LOGIN 登录 → 切 zhuye——按钮点击全部正常、无 2-event-systems 警告。

#### P3-2 死代码：GetFriends 双实现 【已修复 2026-09-22：FriendHandler 改指 FriendRepository.GetFriends（语义归属正确），删除 AccountRepository 中的同构实现（改前已逐字节 diff 确认两者相同）；好友列表 E2E 回归通过】
- **位置**：`AccountRepository.cs → GetFriends` 与 `FriendRepository.cs → GetFriends` 完全同构；FriendHandler 实际调用 **AccountRepository** 版。
- **推荐方向**：保留 FriendRepository 版（语义归属正确），FriendHandler 改引用后删除 AccountRepository 版。
- **验证前置**：好友列表 E2E。

#### P3-3 演示残留（开源框架时代遗留）
- **证据清单**：`Scripts/UI_Test.cs`（MH 命名空间，Key6-12 演示按钮处理器）；`Scripts/Demo/TMPInputAutoSize.cs`（实验性质，与 FriendUIController 内置实现重复）；`Scenes/RedDotSampleScene.unity`（不在 Build 列表）；`Assets/Resource/Card` 24 张卡图；`ERedDotKeyType.cs` 的 Key1-13 演示键（Key13 Description="111" 占位）；`Prefabs/RedDotItem_Free/New/Tip` 变体。
- **推荐方向**：集中移入 `Assets/Samples/RedDot/`（或删除，git 可找回）；ERedDotKeyType 清理需先确认 RedDotConfigAsset 里没有引用这些键，且**不要跑 RedDotKeyWindow 的"生成枚举"**（见 P3-8）。
- **验证前置**：grep Key1-13 在业务代码的引用（UI_Test 之外应为 0）；确认无场景/Prefab 引用要删的资产。

#### P3-4 一致性小项（一次性顺手修） 【部分修复 2026-09-22：ServerHost/ServerPort 常量集中到 ClientNetworkManager（登录/注册控制器改引用）；chatTitle 三处赋值（ResetChat/新的朋友页/SelectChat）补判空。遗留：状态文本 legacy Text→TMP 未动；申请列表全量重建未动（量小可接受）】
- `LoginSceneController/SignupController` 的状态文本用 legacy `Text`（全项目其余为 TMP）；
- `ServerHost/ServerPort` 常量在两个 Controller 重复定义（应集中到一个静态 ServerConfig）；
- `FriendUIController → ResetChat` 中 `chatTitle.text` 无空保护（同文件其他字段全部判空，独此一处）；
- `OnFriendRequestsResponse` 仍是全量 Instantiate/Destroy 重建（申请列表小，可接受，记录不强制）。

#### P3-5 程序化贴图每场景加载泄漏 + 微性能 【贴图泄漏已修复 2026-09-22：BagUIController 三张程序化贴图改静态缓存（按尺寸键控，判空用 == 兼容域重载关闭）。倒计时零分配项已尝试并**回退**：TMP 的 SetText(string,float) 走自带轻量格式解析器不支持 F1 语法，实测输出"剩余 F3.5s"（字面 F）——已在代码留注释防止再次踩坑；该处每帧一个短字符串的分配本来就可忽略】
- **位置**：`Bag/BagUIController.cs → ApplyProceduralSprites`（Awake 每次新建 3 张 Texture2D，不 Destroy）；`Challenge/ChallengeUIController.Update` 挑战期间每帧字符串插值 SetText。
- **推荐方向**：贴图与 Sprite 缓存为静态（或 OnDestroy 释放）；倒计时文本用 `SetText("剩余 {0:F1}s", remainTime)`（TMP 零分配重载）。
- **验证前置**：无行为变化，切场景多次后 Profiler 对比确认。

#### P3-6 服务端小项清单
- `ClientSession`：`SessionID = GetHashCode()`（可能碰撞，仅日志用，建议自增 ID）；`Close()` 里 `sendLock.Dispose()` 与在途 `Send` 有竞态（Broadcast fire-and-forget 会吞异常）；
- `Program.Main`：`Thread.Sleep(Timeout.Infinite)` 无优雅停机（Ctrl+C 直接杀）；后续可加 `Console.CancelKeyPress` 关监听器；
- `HandleFriendRequests` 未登录时静默 return（不回包，客户端无反馈，建议回错误响应）；
- `SessionManager.MaxSessions=100` 达到上限时新连接被直接 Close（可回一条"服务器已满"再关）。

#### P3-7 YooAsset 半配置（决策项）
- **证据**：`Assets/BundleCollectorSetting.asset` 已创建（用户 2026-09-21 晚新建）但反射显示 **PackageName 为空串**、AutoCollectShaders=1 —— 当前状态无法构建；图标仍走 `ItemIconLoader`（Resources.Load）双轨并存。
- **推荐方向**：二选一——① 明确采用：补全 PackageName/收集组（收集 Assets/Resources/ui 或迁移后的目录），构建 Simulate 模式联调，ItemIconLoader 增加 YooAsset 分支（保留 Resources 回退）；② 明确搁置：把 BundleCollectorSetting.asset 移出 Assets（避免半配置入库），图标继续 Resources（先做 P2-9 减重）。
- **验证前置**：用户决策（本项不是缺陷，是未完成的开始）。

#### P3-8 编辑器工具风险：RedDotKeyWindow"生成枚举"会重写 ERedDotKeyType.cs
- **证据**：历史实测——该功能重写枚举文件，虽保留 asset 里的键但会覆盖手工维护的格式/注释（Key14/15 是手写的）。
- **推荐方向**：在窗口加确认对话框，或文档/团队约定禁用该按钮；键值变更一律手改 + 三端提交。
- **验证前置**：无。

#### P3-9 文档漂移
- **证据**：README.md 只描述红点框架（框架文档 红点系统详细文档/学习指南 质量很高）；游戏化改造后的双端架构、协议约定（帧结构、会话身份绑定、MessageID 命名空间分裂 Common.Enum/Common.Network）、启动方法（服务端环境变量/端口）、测试方法论（E2E 踩坑经验）全部没有工程文档——知识散落在会话记忆里。
- **推荐方向**：新增 `Assets/Documents/ARCHITECTURE.md`（架构图/协议表/启动手册/测试方法论），把本报告 §1 作为起点。
- **验证前置**：无。

#### P3-10 出包配置（发布前再动）
- QualitySettings：默认档 Ultra（vSync=1、AA=2x、shadows=All、aniso=ForceEnable）——纯 2D UI 项目建议自建一档（vSync 按需、关阴影、AA 按需）；
- 图标类纹理 maxTextureSize 统一 512（并入 P2-9）；
- 字体资产合计 ~60MB（MapleMono ttf 17MB + 晴圆 ttf 14.6MB + NotoSansSC SDF 16.5MB + otfs 8MB）：确认 SDF 图集模式与源字体引用关系，出包前评估裁剪（MapleMono/晴圆 SDF 的图集缺字问题历史上有坑，NotoSansSC 已作全局回退，需评估前两者是否可只留其一）。
- **验证前置**：打包一次看实际包体与文本渲染（中英混排、生僻字）再决定。

#### P3-11 全项目无音频
- **证据**：编辑器实测 `AudioClip` 资产数 = 0。挑战点击、按钮、消息到达、错误提示全部无声。
- **推荐方向**：后续可按事件清单补 SFX（挑战点击/倒计时结束/按钮点击/消息提示），音频三要素（加载方式、AudioMixer 分组、音量策略）先定后做。
- **验证前置**：用户决定优先级（纯打磨项）。

---

## 3. 值得保留的设计（修复时的"禁改"清单）

1. **服务端权威 + 会话身份绑定**：所有请求不带玩家 ID，身份来自 `session.PlayerId`（登录时 TryBindPlayer 绑定）——客户端无法伪造他人身份。修改任何 handler 时不得破坏该约定。
2. **SQL 全参数化**：抽查全部 Repository 无一字符串拼接 SQL。
3. **单账号单会话**：`SessionManager.TryBindPlayer` 拒绝第二连接占用同一账号。
4. **聊天隐私三层**：会话身份推导 + AreFriends 准入 + conversationId 仓库内部生成（方法签名不暴露会话键）。
5. **VirtualScrollList**：好友/聊天/榜单/背包四列表共用，前缀和 + 二分 + 对象池，含 OnEnable 早到自愈。已知注意点（顶锚 Content anchoredPosition 符号、ForceUpdateCanvases 时机）勿"顺手重构"。
6. **红点框架扩展方式**：动态子节点（100000+好友ID）+ RedDotItem.Bind 运行时改绑 + BeginBatch/EndBatch 批量传播 + 换账号摘链。
7. **背包整表快照语义**：操作响应统一回带最新快照，客户端整表替换（无本地预测、无对账问题）。
8. **MySQL 降级路径**：Valkey 挂 → 榜单直查 idx_score 索引 + COUNT 名次，成绩永远先落 MySQL。
9. **幂等基建**：EnsureTable + INSERT IGNORE 种子 + 启动重建 ZSET + Mongo 幂等索引。
10. **protobuf 三端同步**：`protobuf/build_proto.bat` 一次生成双端 Generated，流程本身健全。
11. **E2E 方法论**（团队既有经验）：全新 Play 会话、协程 + WaitUntil 轮询、服务端管道日志——修复验证必须沿用，见 §5。

---

## 4. 分阶段实施路线图

> 原则（按优先级）：正确性 > 现有功能行为 > 稳定性 > 可验证性 > 性能 > 可维护性。每项修复先满足该项"验证前置"，修完必须跑 §5 验证矩阵对应行，禁止"顺手重构"清单 §3 内的机制。

### Phase 0 — 安全止血（~0.5 天，可全部当天完成）
| 项 | 动作 | 风险 |
|---|---|---|
| P0-1 | 轮换 MySQL root 密码；服务端仓库补 .gitignore + git rm --cached（run_server.bat/server_boot.log/bin/obj）；启动脚本凭据外置 | 低（注意轮换后同步更新客户端无影响——客户端不直连 DB） |
| P1-4 | QueryPlayer 补登录校验 | 极低 |
| P2-1 | SendFriendRequestsRequest 改正确类型 | 极低 |
| P2-3 | 好友申请 catch 文案修正 | 零 |

### Phase 1 — 正确性与稳定性（1~2 天）
| 项 | 动作 | 验证 |
|---|---|---|
| P2-2 | 好友互发申请竞态修复（先 TestClient 复现脚本，再修，再回归） | 双端对称断言 |
| P2-4 + P2-5 | 发送异常链路：SendRawAsync 容错 + ChatManager 发送失败回滚 + 断线状态上报 | 杀服务端中断线 E2E |
| P3-1 | EventSystem 双保险收敛（删 BeforeSceneLoad 版） | 三场景路径回归 |
| P2-10 | .gitignore 加 YooAsset 例外 + UnityInsight db rm --cached | git status 干净 |

### Phase 2 — 性能与已知 Bug（1~2 天）
| 项 | 动作 | 验证 |
|---|---|---|
| P2-6 | 红点 Key14 传播丢失：最小复现脚本 → 定位 → 修复 | 脚本断言父节点收敛 |
| P2-7 | Valkey 熔断（down 状态 TTL） | 无 Valkey 降级 E2E（响应应 <1s） |
| P2-8 | 未读聚合 + 榜单批量取名 | 好友列表/榜单响应断言 |
| P2-9 | Resources 清理 + 图标 maxSize=512 | 打包体积对比 + 图标显示回归 |

### Phase 3 — 工程化与打磨（按需排期）
P1-1/P1-2（产品决策后实施）、P1-3（密码哈希迁移 005）、P3-2/3/4/5/6（小项清理）、P3-7（YooAsset 决策）、P3-9（ARCHITECTURE.md）、P3-10/11（出包与音频）。

### 验证矩阵（每阶段收尾必跑）
| 场景 | 通过标准 |
|---|---|
| 注册→登录→好友列表→申请→同意（双客户端） | 全链路无报错，列表对称 |
| 聊天：开面板→发消息→Echo 校准→对端推送→未读红点 | pending 归零、红点数准确、打开即清 |
| 背包：加物品→使用/丢弃→整理→拖拽换位（0↔499） | 服务端快照与 UI 一致 |
| 挑战→上报→榜单（含无 Valkey 降级） | 排名正确、降级响应 <1s（熔断后） |
| 断线（杀服务端）→重启→重连→重登录 | 状态栏提示、无卡死 pending、重登缓存清空 |
| 场景三路径（LOGIN Play / zhuye Play / LOGIN→zhuye） | 无 2-EventSystem 警告、按钮可点 |

---

## 5. 审计覆盖清单

- **客户端源码**：Assets/Scripts 全部 23 个非生成脚本（FriendUIController/ClientNetworkManager/BagUIController/BagManager/BagSlotView/ChallengeUIController/ChatManager/VirtualScrollList/ItemIconLoader/LoginSceneController/SignupController/UI_Test/TMPInputAutoSize/RedDotKeys/RedDot 全框架 9 文件）+ Editor/CompeteServerAutoStart.cs 逐文件精读。
- **服务端源码**：competeserves/Server 全部 22 个 .cs（Program/ServerBootstrap/SocketServer/ClientSession/SessionManager/PlayerManager/MessageHandler/FriendHandler/ChatHandler/LeaderboardHandler/Account/Friend/Chat/Leaderboard/Bag Repository/MySql/Mongo/Valkey Manager）逐文件精读。
- **工程实测**：活动场景 Canvas×6/Raycaster 配对、EventSystem 数、69 个 TMP_Text 的 raycastTarget 逐个审计、ScrollRect 灵敏度、纹理导入器 88 张逐个统计、Assets Top15 大文件、音频=0、Build 场景列表、PlayerSettings/Quality、YooAsset 收集器配置反射、BundleCollectorSetting。
- **仓库卫生**：双仓库 git ls-files/check-ignore/status、run_server.bat 凭据、.gitignore/ignore.conf/.codelyignore、文件编码抽验（UTF-8 无 BOM，显示乱码为 PowerShell 读取编码问题，文件本身健康）。
- **工具链**：protobuf 目录、build_proto.bat、Migrations 001-004、CompeteServer.csproj（net8.0/Nullable enable）。
- **文档**：README、红点系统详细文档、学习指南（目录级扫描）。

*本报告由 Codely 生成于 2026-09-21；修复实施时请同步更新各项状态（建议在 §2 各项后追加 `【已修复 日期 方式】` 标记）。*
