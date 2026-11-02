# competeserves — 仿 EclipseForgottenCrypt 的 protobuf 双端骨架

仿照 `EclipseForgottenCrypt-main` 的前后端架构写成的**最小可跑骨架**，唯一区别：协议层用手写二进制协议换成了 **protobuf**。

- **服务端**：`Server\`（.NET 8 控制台，MySQL 持久化，端口 8888）
- **客户端**：`D:\tuanjieproject\frined\RedDotSystem-main`（Tuanjie 1.9.3 工程，Play 即测试）
- **协议**：`..\protobuf\protos\` 下的 `MessageID.proto`（消息ID枚举）与 `LoginPacket.proto`（消息体），由 `..\protobuf\build_proto.bat` 统一生成

## 目录结构

```
..\protobuf\protos\MessageID.proto     消息ID枚举定义（对应 Eclipse 的 MessageID.cs，C# 命名空间 Common.Enum）
..\protobuf\protos\LoginPacket.proto   消息体定义（对应 Eclipse 的 LoginPacket.cs，C# 命名空间 Common.Network）
..\protobuf\build_proto.bat            统一协议生成脚本（protoc 33.1 → C#，一跑三处同步）

competeserves\
├── run_server.bat            服务端启动脚本（已填好数据库凭据，双击运行）
└── Server\                   .NET 8 服务端工程
    ├── Program.cs            入口：建库建表+种子账号 → 健康检查 → 启动
    ├── ServerBootstrap.cs    启动引导（单例）
    ├── Network\              SocketServer / ClientSession / SessionManager
    ├── Handler\              MessageHandler（按消息ID分发）
    ├── Database\             MySqlManager（CS_DB_* 环境变量）+ AccountRepository
    └── Generated\            MessageID.cs + LoginPacket.cs（build_proto.bat 自动复制过来）
```

协议代码的三处同步（由 `..\protobuf\build_proto.bat` 一次完成）：

```
protobuf\output\csharp\MessageID.cs、LoginPacket.cs                     共享生成产物
RedDotSystem-main\Assets\Scripts\Generated\MessageID.cs、LoginPacket.cs 客户端副本
competeserves\Server\Generated\MessageID.cs、LoginPacket.cs             服务端副本
```

客户端侧文件（位于 RedDotSystem-main）：

```
Assets\Plugins\Google.Protobuf.dll          protobuf 运行时（netstandard2.0）
Assets\Scripts\Generated\MessageID.cs        消息ID枚举（Common.Enum.MessageID）
Assets\Scripts\Generated\LoginPacket.cs      消息体（Common.Network：TestMessage / LoginRequest / LoginResponse）
Assets\Scripts\Network\ClientNetworkManager.cs  网络管理器（收发/主线程分发）
Assets\Scripts\Network\CompeteLoginUI.cs        登录测试界面（Play 时自动创建，纯代码 UI）
```

## 协议说明

帧格式（TCP，与 Eclipse 相同的 4 字节小端长度前缀，解决粘包/半包）：

```
[4字节 长度] [4字节 消息ID(MessageID)] [protobuf 消息体]
```

`MessageID.proto` 当前定义（C# 侧：`Common.Enum.MessageID`）：

| MessageID | 值 | 方向 | 消息体 |
|---|---|---|---|
| LoginRequest | 1001 | 客户端→服务端 | LoginRequest |
| LoginResponse | 1002 | 服务端→客户端 | LoginResponse |
| RegisterRequest | 2001 | 客户端→服务端 | RegisterRequest |
| RegisterResponse | 2002 | 服务端→客户端 | RegisterResponse |

## 运行步骤

### 0. 准备数据库（手动，一次性）

表结构由使用者管理，服务端不会自动建库建表。在 MySQL 客户端（mysql 命令行 / Navicat / Workbench 均可）整体执行一次：

```
Server\Migrations\001_InitAccounts.sql
```

脚本内容：创建 `compete` 库 → 创建 `accounts` 表 → 写入种子测试账号 `test / 123456`（不需要可删掉该段）。已执行过的机器无需重复。

### 1. 启动服务端

打开 `run_server.bat`，把里面两行改成你本机 MySQL 的账号密码：

```bat
set "CS_DB_USER=root"
set "CS_DB_PASSWORD=你的密码"
```

然后双击运行。启动时只做连接健康检查（库或表不存在会提示先执行迁移脚本），看到 `Server Running...` 即成功。

**偷懒方式（已配置）**：客户端工程的编辑器脚本 `Assets\Editor\CompeteServerAutoStart.cs` 会在进入 Play 模式时检测 8888 端口——服务端没在跑就自动执行 `run_server.bat`。弹出的黑窗口就是服务端本体（标题已标注 CompeteServer）：**关闭该窗口 = 停止服务端**；退出 Play 不会杀掉它（便于双客户端调试）。

数据库连接参数支持环境变量覆盖：`CS_DB_HOST`(默认127.0.0.1)、`CS_DB_PORT`(默认3306)、`CS_DB_NAME`(默认compete)。

### 2. 启动客户端

用 Tuanjie 打开 `RedDotSystem-main` 工程，直接点 Play（任意场景都行）。
屏幕中央会自动弹出"Compete 服务器登录测试"面板：

1. 确认服务器地址（默认 `127.0.0.1:8888`）
2. 账号 `test`、密码 `123456` → 点 **登录**
3. 状态文本显示"登录成功！玩家ID:x"即全链路打通，随后自动跳转 `zhuye` 主页面场景（登录界面销毁，网络连接由 ClientNetworkManager 跨场景保持）
4. 没有账号？点登录面板的 **注册** 按钮 → 填写用户名/密码/确认密码 → 点注册（两次密码不一致会提醒"两次密码不一致"；账号重名提示"该账号已被注册"）→ 注册成功自动回到登录面板，用新账号登录即可

> 登录界面由 `CompeteLoginUI` 的 `[RuntimeInitializeOnLoadMethod]` 在 Play 时自动创建，不依赖任何场景；不需要时删除该脚本即可。

## 如何加一个新消息（二次开发）

1. 在 `..\protobuf\protos\MessageID.proto` 里：`MessageID` 枚举加一个值；消息体加到 `..\protobuf\protos\LoginPacket.proto`（或按 Eclipse 风格另建 .proto 分文件）
2. 运行 `..\protobuf\build_proto.bat`（重新生成 C# 并同步到共享目录、客户端与服务端三处）
3. 服务端：`Handler\MessageHandler.cs` 的 switch 加 case 处理并回包
4. 客户端：`ClientNetworkManager.cs` 加 Send 方法 + `HandlePacket` 加 case 抛事件
5. 服务端 `dotnet build`、客户端回 Tuanjie 等编译即可

## 与 Eclipse 原版的对应关系

| EclipseForgottenCrypt | competeserves | 说明 |
|---|---|---|
| Common\Network\PacketWriter/Reader | protobuf + LoginPacket.proto | 序列化方案 |
| Common\Enum\MessageID.cs | MessageID.proto 的 MessageID 枚举（Common.Enum） | 消息号定义 |
| Common\Network\LoginPacket.cs | LoginPacket.proto 的 message（Common.Network） | 消息体定义 |
| Packet{ID,Data} | [4字节ID][protobuf体] 帧 | 封包结构 |
| Network\SocketServer/ClientSession/SessionManager | 同名同结构 | 网络层一致 |
| Handler\MessageHandler | 同名 switch 分发 | 处理中心一致 |
| Database\MySqlManager (EFC_DB_*) | MySqlManager (CS_DB_*) | 仅环境变量前缀不同 |
| Program.cs 启动横幅 + ReadLine 保活 | Sleep(∞) 保活（不依赖控制台输入） | 启动流程一致 |
