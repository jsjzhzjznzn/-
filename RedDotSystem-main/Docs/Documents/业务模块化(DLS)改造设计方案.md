# 业务模块化（DLS）改造设计方案

## 1 目标与边界

把当前「MonoBehaviour 单例 + 直接访问 `XxxManager.Instance`」的业务层，改造成 Mm-FrameWork 的 DLS 结构：

- 每个业务模块对外只暴露一个公共接口（`IBagService` 这类），跨模块通过 `GameHub` 按接口定位。
- 模块内部按 Data / Logic / Service 划分职责，Service 只做转发与稳定查询。
- 每个模块单独一个 asmdef，跨模块只能引用对方的 `Public` 程序集内容，编译期强制解耦。
- 分阶段推进：先做边界（GameHub + 接口 + 组合根 + asmdef），内部逻辑先原样搬；单模块内部再逐步拆 Data/Logic，不要求一次到位。

本方案不引入 DDD、不引入 Clean Architecture、不改协议、不改服务端。

## 2 现状实测

### 2.1 规模与分层

| 项 | 现状 |
| --- | --- |
| `Assets/Game/Scripts` 下 C# 文件 | 384 个 |
| asmdef | 9 个，全部属于第三方（KCC / BehaviorDesigner / Animancer / GAS / Odin / PerfAgent），游戏代码全部在 `Assembly-CSharp` |
| 框架层 | `Assets/Game/Scripts/Framework`（23 个文件）：`ModuleHub` + `MonoManager` + `AudioManager` + `InputBuffer3D` + `SceneLoader` + `LoadingScreen`，已有 `IManagerBase` / `ManagerAttribute` / `BootPhase` / `ReadyAsync` |
| 业务层 | 20 个 `XxxManager` 单例，全部 `MonoBehaviour` + `public static Instance` + `[RuntimeInitializeOnLoadMethod]` 自举 + `event Action` 通知 UI |
| 网络层 | `ClientNetworkManager` 一个类：64 个 `event Action<TResponse>`、约 60 个 `SendXxx` 方法、被引用 253 处 |
| 事件总线 | `Packages/com.hakisheep.mm-eventbus` 提供 `EventBusCore` / `EventKey` / `EventBusBridge` / `EventBusUnityBootstrap`，目前只有叙事模块的 `NarrativeEventBus` 在用 |
| 跨模块装配 | `Assets/Game/Narrative/CrossModule/CrossModuleInstaller.cs` 已用「组合根 + 推注入」；`mm-mvvm` 里的 `ICrossBusinessModuleService` 是空标记接口，除叙事两个 Cross 服务外无人实现 |

### 2.2 单例耦合度（`XxxManager.Instance` 引用数）

| Manager | 引用数 | 主要消费者 |
| --- | --- | --- |
| `ClientNetworkManager` | 253 | 几乎所有 Manager、登录、各个场景 |
| `BagManager` | 54 | BagView 20、ItemConditionDemoUI 9、QuestView 7、CharacterView 5、ActivityBridge 4、EquipmentManager 3 |
| `ChatManager` | 39 | 聊天三页、红点、引导 |
| `EquipmentManager` | 31 | CharacterView、战斗属性注入 |
| `WorldChatManager` | 21 | 世界频道页 |
| `AchievementManager` | 16 | 成就页、成就 Toast |
| `MailManager` | 15 | 邮件页 |
| `ChallengeManager` / `GuideManager` | 0 | 通过 UIManager / 预制体事件调用 |

### 2.3 三个关键可行性事实

1. **单例没有进资产**：`BagManager` / `ChatManager` / `MailManager` / `AchievementManager` / `EquipmentManager` / `ClientNetworkManager` 的脚本 GUID 在全部 `.unity` / `.prefab` 中出现 0 次，全部靠 `RuntimeInitializeOnLoadMethod` 自举。改造不涉及场景与预制体引用修复。
2. **业务模块依赖很干净**：Bag / Mail / Chat / Achievement 的 `using` 只有 `System*`、`Common.Network`（协议生成代码）、`MH`（红点）、`UnityEngine`。唯一例外是 `ChallengeManager` 依赖 `UIManager`，`ClientNetworkManager` 依赖 `SkierFramework` 与 `TMPro`（登录后切场景与状态提示）。
3. **协议层自洽**：`Assets/Game/Scripts/Generated`（17 个文件，`namespace Common.Network`）只依赖 `Google.Protobuf.dll`（`Assets/ThirdParty/Plugins/Google.Protobuf.dll`），可以独立成程序集。

结论：可行。改造的主要工作量在 `ClientNetworkManager` 的拆解（253 处引用）与各 UI 的取数方式，而不在资产与场景。

## 3 目标架构

### 3.1 分层与引用方向

```
Assembly-CSharp（UI 视图、玩法、场景脚本、GameBootstrap）
        │ 只能向下引用
        ▼
Game.Module.*（业务模块：Bag / Mail / Chat / Achievement / Equipment / …）
        │ 只能引用 Core / Network 与对方 Public 接口
        ▼
Game.Network   Game.RedDot（可选）
        ▼
Game.Core（GameHub 与业务服务契约）
        ▼
Game.Protocol（protobuf 生成代码）
```

规则：

- asmdef 一律 `autoReferenced: true`，`Assembly-CSharp` 才能继续看见它们。
- 模块程序集禁止引用 `Assembly-CSharp`（Unity 不允许），因此模块里需要的任何共用类型必须位于更下层程序集。
- 模块之间只能引用对方的 `Public` 目录与程序集；`Data` / `Logic` 不对模块外暴露。
- 需要互调的两个模块，引用顺序写进 asmdef 的 `references`，形成单向依赖；出现环时必须抽 `FlowService`。

框架层（`ModuleHub` / `AudioManager` / `SceneLoader` / `LoadingScreen`）仍留在 `Assembly-CSharp`，不单独成程序集：`ModuleHub.Boot.cs` 直接调用 `SkierFramework.ResourceManager.Instance.InitializeAsync`，`F_Audio/AudioClipLease.cs` 通过 `SkierFramework.YooAssetService` 加载音频，框架层反向依赖 UI 与资源层。此时若拆出 `Game.Foundation`，只能让它引用 `Game.UIFramework`，形成倒挂；只有先把资源加载下沉为独立服务，框架层才能独立成程序集。业务模块对框架的真实需求只有 `GameHub`，因此不受影响。

### 3.2 GameHub

新增 `Assets/Game/Scripts/Core/GameHub.cs`（命名空间 `MieMieFrameWork.Business`，程序集 `Game.Core`），与 Mm-FrameWork 的实现一致：

```csharp
public interface IGameService { }

public static class GameHub
{
    public static void Register<TService>(TService service) where TService : class, IGameService;
    public static TService Get<TService>() where TService : class, IGameService;   // 未注册返回 null
    public static bool TryGet<TService>(out TService service) where TService : class, IGameService;
    public static void Unregister<TService>() where TService : class, IGameService;
    public static void Clear();
}
```

约束：

- 注册键必须是接口，`Register<T>()` 内部校验 `typeof(T).IsInterface`，直接注册实现类或 `IGameService` 抛异常。
- `Get` 返回 null 视为组装错误，调用方不得用静默跳过掩盖。
- `GameHub` 只允许出现在组合根、Service 组装入口、UI/玩法消费入口；Data、Logic、高频 `Update` 内禁止调用。
- 编辑器关闭域重载时用 `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` 清空字典。

### 3.3 模块目录规范

模块保持现有目录（不搬文件，避免 GUID 与 git 历史断裂），在目录内按需补子目录，asmdef 放在模块根：

```
Assets/Game/Scripts/Bag/
  Bag.asmdef                     ← name: Game.Module.Bag
  Public/
    IBagService.cs
    BagEvents.cs                 ← EventKey 定义
    BagSlotViewData.cs           ← 需要时才有 DTO
  Data/
    BagRuntimeData.cs            ← 槽位快照 / 配置缓存，模块私有
  Logic/
    BagQueryLogic.cs
    BagMutationLogic.cs
  BagService.cs                  ← 实现 IBagService，持有 Data 与 Logic
```

- 类名后缀：`Logic` 只用于业务规则与状态提交，`Service` 只用于公共入口。
- 不做空层：没有配置就不建 `Config`，没有存档就不建 `Save`，简单查询不造空 `Logic`。
- 过渡期允许 `BagService` 继续继承 `MonoBehaviour`（由组合根显式创建），等模块内 `Update`/协程消失后再改纯 C#。

### 3.4 生命周期（组合根）

新增 `Assets/Game/Scripts/Bootstrap/GameBootstrap.cs`（`Assembly-CSharp`，`RuntimeInitializeOnLoadMethod(BeforeSceneLoad)` 自举）：

```
1. await ModuleHubBootstrap.EnsureAsync();
2. await ModuleHub.Instance.ReadyAsync();
3. 创建各模块 Service（new 或 AddComponent 到 DontDestroyOnLoad 根）
4. GameHub.Register<IXxxService>(service)
5. 完成后写 GameBootstrap.IsReady，UI 侧 GameBootstrap.ReadyAsync() 之后再取服务
```

它必须留在 `Assembly-CSharp`：模块程序集不能反向引用 `Assembly-CSharp`，而组合根必须同时看见框架与全部模块；放在模块或框架程序集里都会成环。当前 `RegisterModules()` 为空，阶段 2 起逐个补模块。

- 登录成功、场景切换都不重建服务；只有真正换号 / 退出登录才 `Unregister` 并释放模块（对应 `GameHub.Clear()` 仅在框架销毁时调用）。
- 释放顺序为注册顺序的逆序。
- 模块自己持有的事件订阅用 `IDisposable` 令牌，释放时统一 `Dispose`。

### 3.5 网络层拆解

现状把「连接 + 收发 + 64 个回包事件 + 登录后切场景 + 状态提示」放在同一个类里，是模块化最大的障碍。拆成两层：

**Game.Network（新 asmdef，纯 C#）**

```csharp
public interface INetworkService : IGameService
{
    bool IsConnected { get; }
    Task<bool> ConnectAsync(string host, int port);
    void Disconnect();
    Task SendAsync(MessageID id, IMessage message);
    IDisposable Subscribe<TMessage>(Action<TMessage> handler) where TMessage : class, IMessage;
}
```

- 内部实现 `NetworkClient`：socket、`[4B 长度][4B 消息 ID][body]` 拆包、`MessageID → 反序列化类型`分发、心跳、`Pump()` 主线程泵。
- 回包按消息类型分发，订阅方拿到 `IDisposable` 令牌；同一类型可以有多个订阅者。
- 只依赖 `Game.Protocol`、`Game.Core` 与 `System.Net.Sockets`，不依赖 UI、不依赖场景。

**ClientNetworkManager（留在 Assembly-CSharp，改造为外壳）**

- 保留 `Instance`、登录会话（凭据缓存、被顶号、自动重连）、`Send*` 入口与场景/提示行为，内部转发给 `NetworkClient`。
- `Update` 只做一件事：调用 `network.Pump()`（主线程入队解析，与改造前同一节奏）。
- 64 个 `event Action<TResponse>` 现在由 `BindMessages()` 从 `Subscribe<T>` 统一转发，属于过渡门面：消费者迁移到 `INetworkService` 或模块 Service 时逐个删掉对应转发，全部清空后删除事件本身。
- 登录成功后切 `LOGIN → zhuye`、`LoadingScreen`、`KickoutWatcher` 等 UI 行为留在这一层，不下沉到 `Game.Network`。

模块拿网络的方式：`GameHub.Get<INetworkService>()`，在 `Start`（或组合根创建的构造流程）里订阅自己的协议族，例如 `BagService` 订阅 `BagListResponse` / `BagUseResponse` / `BagGrantResponse`。

### 3.6 事件

- **跨模块状态通知**：`EventBusCore` + `EventKey`（`BagEvents.Updated`、`MailEvents.UnreadChanged`）。模块在业务提交完成后 `Publish`，UI 与其他模块 `Subscribe`，订阅令牌随对象 `Dispose`。
- **模块公共接口只放命令与只读查询**，不放 C# `event`、不放 `Action` 回调注入口。
- **UI 取数**：一次性数据用 `IBagService.GetSlot(i)` 这类只读查询；持续刷新用事件订阅。
- 现有 `event Action BagUpdated` 等全部迁移为 `EventKey`，过渡期可在 Service 上保留 `[Obsolete]` 转发属性，但同一个状态只允许一条通知路径，避免双发。

## 4 模块清单与实施阶段

| 阶段 | 模块 | 现有文件 | 新接口 | 依赖 |
| --- | --- | --- | --- | --- |
| 0 | 协议 | `Scripts/Generated` | （无） | Google.Protobuf |
| 0 | 业务契约 | `Scripts/Core` | `IGameService` / `GameHub` | 无 |
| 1 | 网络 | `Scripts/Network` | `INetworkService` | Protocol、Core |
| 2 | 背包（样板） | `Scripts/Bag` | `IBagService` | Protocol、Core、Network |
| 3 | 邮件 | `Scripts/Mail` | `IMailService` | + Bag（领取入包后刷新） |
| 3 | 成就 | `Scripts/Achievement` | `IAchievementService` | + Bag、RedDot |
| 3 | 装备 | `Scripts/Equipment` | `IEquipmentService` | + Bag、RedDot |
| 3 | 聊天 | `Scripts/Chat`（ChatManager + WorldChatManager） | `IChatService`、`IWorldChatService` | + RedDot |
| 3 | 引导 | `Scripts/Guide` | `IGuideService` | + UIFramework |
| 3 | 挑战 | `Scripts/Challenge` | `IChallengeService` | + UIFramework |
| 3 | 任务（叙事） | `Scripts/Narrative` | `IQuestService` | + Bag、Mail |
| 3 | 活动 | `Scripts/Activity` | `IActivityService` | + LuaEnv |
| 4 | 战斗 / 大世界 / 小游戏 | `Scripts/Battle`、`World`、`MiniGame` | 按子系统再切 | 视模块而定 |

阶段 0–2 完成后即可验证整套机制；阶段 3 各模块互不阻塞，可按需求优先级并行；阶段 4 只做边界，不拆内部。

## 5 样板模块：背包

选背包做样板，原因是它协议清晰、无场景引用、消费者集中（10 个文件）、且被邮件/成就/装备依赖，能验证跨模块调用。

### 5.1 接口

```csharp
namespace Game.Bag
{
    public interface IBagService : IGameService
    {
        int Capacity { get; }
        int ItemCapacity { get; }
        int SlotCount { get; }
        bool IsLoading { get; }
        bool IsWeaponSlot(int index);
        IReadOnlyList<ItemConfigInfo> AllConfigs { get; }
        BagSlotInfo GetSlot(int index);
        ItemConfigInfo GetConfig(int itemId);
        int GetItemCount(int itemId);

        void RequestBag();
        void UseItem(int slotIndex, int count = 1);
        void DiscardItem(int slotIndex, int count = 1);
        void Tidy();
        void SwapSlots(int fromSlot, int toSlot);
        void Grant(int itemId, int count);
    }
}
```

接口只保留 UI 真正需要的成员；`OnLoginResponse` / `OnGachaDrawResponse` / `OnSignInClaimResponse` 这类别的模块的事件留在模块内部处理。

### 5.2 内部划分

| 位置 | 内容 |
| --- | --- |
| `Data/BagRuntimeData` | `slots`、`configs`、`configOrder`、`loading`、容量常量，模块私有 |
| `Logic/BagQueryLogic` | 槽位查询、容量推导、计数统计 |
| `Logic/BagMutationLogic` | `Use/Discard/Tidy/Swap/Grant` 的本地校验与缓存提交（服务端权威不变） |
| `BagService` | 订阅 `BagListResponse` / `BagUseResponse` / `BagDiscardResponse` / `BagTidyResponse` / `BagSwapResponse` / `BagGrantResponse`，更新 Data 后 `Publish(BagEvents.Updated)` |

服务端仍是唯一权威，Logic 只做客户端缓存的一致性维护，不新增规则。

### 5.3 事件

```csharp
public static class BagEvents
{
    public static readonly EventKey Updated = new EventKey("Bag.Updated");
    public static readonly EventKey<string> Status = new EventKey<string>("Bag.Status");
}
```

### 5.4 UI 与其它模块的改造

| 文件 | 改动 |
| --- | --- |
| Bag/BagView.cs | 事件订阅改为 BagManager.Bus.Subscribe(BagEvents.Updated / BagEvents.Status, ...)，令牌在 OnRemoveListener 释放；HasItem 改走 GameHub.Get<IBagService>() |
| Quest/QuestView.cs | 订阅 BagEvents.Updated；RequestBag 改走 IBagService |
| Equipment/CharacterView.cs | 订阅 BagEvents.Updated |
| Equipment/EquipmentManager.cs | 跨模块调用改 GameHub.Get<IBagService>().RequestBag() |
| Activity/ActivityBridge.cs | 只读查询改走 IBagService |
| Activity/ActivityShell.cs | 抽卡/签到回执改订阅 INetworkService（原事件已删除） |
| Narrative/ItemConditionDemoUI.cs | 订阅 BagEvents.Status；Grant 改走 IBagService |
| Narrative/BagFactsBinder.cs | 订阅 BagEvents.Updated |

BagManager 现实现 IBagService，其 Instance 自举与生命周期保持不变；组合根只登记接口。原来通过 BagManager.Instance.Xxx 直接读写的数据访问已全部改为接口调用，事件与数据访问两条路径各自唯一。

### 5.5 asmdef

`json
{
  "name": "Game.Module.Bag",
  "references": ["Game.Protocol", "Game.Core", "Game.Network", "MiMieEventBus"],
  "autoReferenced": true
}
`

asmdef 放在 Bag/Public/：Assembly-CSharp 不能按目录被程序集引用，接口与事件必须落在模块程序集内部；Data / Logic 拆分（5.2）在接口稳定后按需进行，当前业务规则仍集中在 BagManager。
## 6 程序集拆分清单

| 程序集 | 目录 | references | 说明 |
| --- | --- | --- | --- |
| `Game.Protocol` | `Assets/Game/Scripts/Generated` | （预编译）`Google.Protobuf.dll` 自动引用 | 生成代码，禁止手改 |
| `Game.Core` | `Assets/Game/Scripts/Core` | 无 | `GameHub` 与业务服务契约，模块唯一必须依赖的地基 |
| `Game.Network` | `Assets/Game/Scripts/Network` | `Game.Protocol`、`Game.Core`、`UniTask` | 传输与分发 |
| `Game.RedDot` | `Assets/Game/Scripts/RedDot` | `Unity.TextMeshPro`、`UnityEngine.UI` | MH 红点框架 |
| `Game.Module.*` | 各模块目录 | `Game.Protocol`、`Game.Core`、`Game.Network`、`Game.RedDot`（按需） | 一个业务模块一个 |
| `Game.UIFramework` | `Assets/Game/Scripts/UI/Framework` | `Game.Core`、`YooAsset`、`XLua`、`UniTask`、`DOTween`、`Unity.TextMeshPro`、`UnityEngine.UI` | 含 `Editor/` 子目录与 `UnityEditor` 依赖，需同时拆 `Game.UIFramework.Editor` |
| `Game.Foundation` | `Assets/Game/Scripts/Framework` | `Game.Core`、`Game.UIFramework`、`UniTask`、`Unity.TextMeshPro`、`UnityEngine.UI` | 只有在资源加载从 UI 层下沉之后才成立，当前不拆 |
| `Assembly-CSharp` | 其余 | 自动引用以上全部 | UI 视图、玩法、场景脚本、`GameBootstrap` |

拆分顺序固定：Protocol → Core → Network → 各模块 → UIFramework → Foundation。核心链路（Protocol / Core）先立住，业务模块即可开工；UI 框架与框架层因为互有反向依赖，放在最后成对处理。每一步都要能编译通过再进下一步。

`Assets/ThirdParty` 下的 asmdef（Animancer、BehaviorDesigner、GAS、KCC、Odin）按需加入 `references`，不加预编译 DLL 到 `precompiledReferences` 之外的东西。

## 7 实施步骤

每一步独立可编译、可单独提交。

1. **阶段 0（已完成）**：新增 `Core/GameHub.cs`（程序集 `Game.Core`）与 `Bootstrap/GameBootstrap.cs`；`Generated` 建 `Game.Protocol` asmdef；全量编译 0 error，Play 模式下 `GameBootstrap.IsReady=True`、`ModuleHub.IsReady=True`、`GameHub.Count=0`。
2. **阶段 1（已完成）**：`Game.Network` 程序集落地 `NetworkClient` 与 `INetworkService`；`ClientNetworkManager` 退化为外壳，64 个事件由 `BindMessages()` 统一转发保留为过渡门面；`GameBootstrap` 注册 `INetworkService`。实测：编译 0 error；登录回包同时送达 2 个 `Subscribe<LoginResponse>` 与旧 `LoginResponseReceived`；`GameHub.Get<INetworkService>()` 非空；`BagListRequest` 返回 success=True、capacity=500、24 个槽位并驱动 `BagManager` 刷新。事件删除随阶段 3 各模块迁移逐步进行。
3. **阶段 2（已完成）**：`Bag` 建 `Game.Module.Bag` 程序集，落 `IBagService` / `BagEvents`，`BagManager` 实现接口并改为总线发布，消费文件切到接口与事件，抽卡/签到回执切到 `INetworkService` 并删除对应过渡事件。实测：编译 0 error；`GameHub.Get<IBagService>()` 与 `BagManager.Instance` 是同一实例；登录后 `BagEvents.Updated` 触发、`RequestBag` 拉到 capacity=500 / 24 件 / item1001=19；`SwapSlots` 回执触发 `BagEvents.Status="已换位"`；背包界面渲染与改造前一致。`Data` / `Logic` 拆分在接口稳定后按需进行。
4. **阶段 3（进行中）**：按第 4 节表格逐模块推进，每次只动一个模块。
   - **邮件（已完成）**：`Game.Module.Mail` + `IMailService` / `MailEvents`；`MailView` 改订阅模块事件；删除 6 个网络过渡事件。实测 capacity=100 / 14 封 / 未读 11，界面正常。
   - **成就（已完成）**：`Game.Module.Achievement` + `IAchievementService` / `AchievementEvents`（含 `Unlocked` 推送）；成就页与 Toast 改订阅；删除 5 个过渡事件。实测 28 条配置、可领取 6、首条 4201 进度 30/30。
   - **装备（已完成）**：`Game.Module.Equipment` + `IEquipmentService` / `EquipmentEvents`（`Changed` / `Status` / `Result`）；`AttrType`、`EquipBattleSnapshot`、`EquipAttrTotals` 等契约类型随模块进入 `Public`；角色页、背包页、战斗侧（`Player` / `RoomState` / `BattleSpawnManager`）改走接口；删除 2 个过渡事件。实测 17 件武器、首件实例 16（item 1014）攻击 32、卸下→装上往返 `GetEquippedChar` 1→-1→1，角色页渲染正常。
   - **聊天（已完成）**：`Game.Module.Chat` + `IChatService` / `IWorldChatService`（含 `ChatEvents` / `WorldChatEvents`）；私聊与世界频道两个管理器同属一个模块程序集；好友页与世界频道页改订阅；删除 6 个网络过渡事件。实测 3 个会话缓存（会话 4 共 13 条）、世界频道 50 条、两个 `Updated` 事件正常触发，界面渲染正常。
   - **引导（已完成）**：`Game.Module.Guide` + `IGuideService`；`GuideStep` / `GuideSteps` 移入 `Public`；进度拉取改订阅 `INetworkService`（临时订阅用令牌 Dispose），设置面板重看引导改走 `GameHub`。实测服务端进度 step=16 completed=True、重看命令切到 zhuye 并从第 0 步开始。
   - **挑战（不拆）**：`ChallengeManager` 是 tiaozhan 场景总控（持有 player/monster 引用、驱动 HUD、判定胜负），没有跨模块查询或命令，拆出的接口会是空转发，违反"不造空层"；继续留在 `Assembly-CSharp`。
   - **任务（暂缓）**：`MmQuestRuntime` 的契约类型 `QuestData` 是 `ScriptableObject : IQuestBehaviour, IModelConfig`，属于 Mm 叙事框架的 MVVM 层；要暴露成接口必须先把这套 QuestSystem 抽成独立程序集，属于独立任务，不在本阶段强行拆。
   - **任务（边界已落地，程序集待抽）**：`IQuestService` / `QuestEvents` 放在 `Assets/Game/Narrative/Public`，`MmQuestRuntime` 实现接口并把 `QuestsUpdated` / `ToastReceived` 换成总线发布；`QuestView`、`DashijieHudView`、`WorldQuestDirector` 的方法签名由 `MmQuestRuntime` 改为 `IQuestService`，取数改走 `GameHub`。实测 4 条任务、可接取 3、追踪解析到 3001，任务面板渲染正常。**未拆 asmdef 的原因**：`QuestData` 是 `ScriptableObject : IQuestBehaviour, IModelConfig`，且 `QuestSystem` 运行时依赖 `DialogueGraph`（对话系统的 ScriptableObject），对话侧另有 31 个 Editor 文件引用 `Miemie.Narrative.GraphViewFrame.Editor`（`Assembly-CSharp-Editor` 不能被 asmdef 引用），要拆必须把「对话 + 任务」整套 Mm 叙事栈一次性抽成运行时/编辑器两个程序集，属独立任务。
   - **活动（保持现状，已由用户确认）**：`ActivityShell` 的公共方法签名带 `LuaFunction` 回调（`RequestDraw(int, int, LuaFunction)` 等），并持有 `ActivityView`；当前形态抽接口会把 XLua 与 UI 类型一起带进模块，且热更包里的 Lua 已在调用这些签名，改签名会破坏已发布的 Lua 内容。不拆。
   - **对战邀请（已完成）**：`Game.Module.BattleInvite` + `IBattleInviteService` / `BattleInviteEvents`（`Result` / `Notice`）；`BattleInviteManager` 实现接口、6 个信令回执改订阅 `INetworkService`；`BattleInviteView` / `BattleInvitePopupView` / `BattleLobbyView` / `BattleRoomView` 改走接口与事件。实测服务已注册（`GameHub` 共 10 个），`EnsureRoomId` 返回房间号，`SendInvite` 在非房主时返回明确错误「只有房主可以邀请好友」。
- **场景存档（已完成）**：`Game.Module.SceneSave` + `ISceneSaveService`；`SceneSaveService` 实现接口，拉取/上报回执改订阅 `INetworkService`；设置面板退出登录前的冲刷改走 `GameHub`；组合根主动 `EnsureExists()` 并注册（该服务只在大世界等场景使用，不放进 `WaitUntil` 等待，避免其它场景卡启动）。实测：服务已注册（`GameHub` 共 11 个），登录前 `IsAvailable=false`、登录后为 true，`PullAsync("dashijie")` 返回 sceneName=dashijie 的快照（只做只读拉取，不主动上报以免改动玩家落位存档）。
- **守岸人 Agent（不拆）**：会话状态与协议订阅都在 `AgentView`（`UIView`，还实现拖拽接口）内部，抽服务必须把状态搬出界面，属于重构内部而非「只做边界」；对外只有 `AgentFloatBall` 的两个静态通知（`NotifyHomeOpened` / `NotifyBattleRoomOpen`）。
   - **成绩与排行榜（不拆）**：三个消费点（`ChallengeView` / `FogHuntGame` / `LeaderboardView`）各自直接收发成绩与榜单回执，没有任何需要模块持有的状态；做成服务只会是 `INetworkService` 的空转发，违反「不造空层」。
   - **好友（不拆）**：工程里没有好友管理器，好友列表状态由 `FriendUIController`（界面本地）与 `ChatManager`（红点角标）各持一部分；新建 `FriendManager` 属于新增抽象，不是迁移既有代码。
5. **阶段 4**：战斗、大世界、小游戏只做边界（Service + 接口 + 注册），不拆内部逻辑。实测这三者是场景总控（持有场景对象引用、驱动 HUD/表现），与挑战同性质，同样不拆；战斗侧的跨模块数据（装备快照、邀请信令）已通过 `IEquipmentService` / `IBattleInviteService` 暴露。

## 7.1 收口状态

业务模块化到此结束：`GameHub` 注册 11 个服务，业务回包事件从 64 个降到 24 个，剩余事件全部属于上述「不做模块」的系统（Agent 5、活动 7、成绩与排行榜 2、好友 4、连接状态若干）。它们继续以 `ClientNetworkManager` 的过渡事件形式存在，不构成模块边界问题——这些消费点本来就只在单个界面或单个玩法内部使用。

文件搬移必须用 `AssetDatabase.MoveAsset`（保留 `.meta` 与 GUID），禁止直接用文件系统移动。

## 8 文件影响面

新增：

- `Assets/Game/Scripts/Core/GameHub.cs` + `Game.Core.asmdef`
- `Assets/Game/Scripts/Bootstrap/GameBootstrap.cs`
- `Assets/Game/Scripts/Generated/Game.Protocol.asmdef`
- `Assets/Game/Scripts/Network/Core/INetworkService.cs`、`NetworkClient.cs` + `Game.Network.asmdef`
- 各模块的 `Public/`、`Data/`、`Logic/`、`<Name>Service.cs`
- 每个程序集一个 `.asmdef`

修改：

- `Assets/Game/Scripts/Network/ClientNetworkManager.cs`（事件删除、转发 INetworkService、登录流程保留）
- 20 个 `XxxManager`（改为实现接口、由组合根创建、去掉 `[RuntimeInitializeOnLoadMethod]`）
- 约 40 个 UI / 玩法文件里的 `XxxManager.Instance` 调用
- `Assets/Game/Scripts/Narrative/CrossModule/CrossModuleInstaller.cs`（叙事模块接入 GameHub 后可删除推注入，改为统一从 GameHub 取）

不改：服务端、`../protobuf`、所有 `.unity` / `.prefab`、热更 Lua、第三方插件。

## 9 验收

编译与静态：

- 全量编译 0 error；新增 asmdef 后无 `CS0234` / `CS0246`。
- 模块程序集的 `references` 中没有 `Assembly-CSharp`，模块之间无循环引用。
- `GameHub.Register` 只以接口为键；全仓 `XxxManager.Instance` 数量按阶段归零。

运行时（Play 模式）：

| 场景 | 断言 |
| --- | --- |
| 登录 → 主页 | `GameHub.Get<INetworkService>() != null`，连接成功，`GameBootstrap.IsReady` 为 true |
| 打开背包 | `IBagService` 返回槽位与配置，`BagEvents.Updated` 触发一次，界面与改造前一致 |
| 使用 / 丢弃 / 整理 / 换位 | 服务端回包后缓存刷新，红点与计数与改造前一致 |
| 邮件领取入包 | `IMailService` 调用 `IBagService.RequestBag()`，背包立即可见新物品 |
| 装备穿脱 | `IEquipmentService` 与 `IBagService` 交叉调用无循环依赖异常 |
| 退出登录 → 重新登录 | 服务注销/重建一次，无残留旧实例、无重复订阅 |
| 切换场景（主页 → 大世界 → 挑战 → 联机） | 服务跨场景存活，场景内 UI 重新订阅后数据仍然正确 |

回归基线：每个阶段迁移前后各抓一次同一场景的 Console（error/warning 数量）与关键界面截图，对比无新增错误。

## 10 风险与对策

| 风险 | 对策 |
| --- | --- |
| `ClientNetworkManager` 253 处引用，改订阅机制容易漏改 | 先删事件后逐个编译报错驱动迁移，按错误清单推进；不保留双路径 |
| asmdef 拆分后模块误引 `Assembly-CSharp` 类型导致编译失败 | 拆之前先做依赖盘点（本方案第 2.3 节已完成）；遇到模块需要玩法类型时，把该类型下沉到更下层程序集，而不是反向引用 |
| `Assembly-CSharp` 自动引用缺失导致 UI 找不到模块类型 | 所有模块 asmdef 保持 `autoReferenced: true` |
| UI 框架含 `Editor/` 与 `UnityEditor` 依赖 | UI 框架单独拆 `Game.UIFramework` + `Game.UIFramework.Editor`，放到最后一步 |
| 事件双发（旧的 `event Action` 与新的 `EventKey` 同时存在） | 同一状态只保留一条通知路径，迁移时先删旧事件再补订阅 |
| 单例生命周期变化导致初始化顺序问题 | 统一由 `GameBootstrap` 在 `ModuleHub.ReadyAsync()` 之后创建，UI 侧等待 `GameBootstrap.ReadyAsync()` |
| 编辑器图形资源 ID 溢出（长时间多轮 Play） | 遵循 `.codely-cli/skills/unity-editor-gfx-budget/SKILL.md`：一批改动一次 refresh，一轮只重建一次 |
| Play 模式下改 `.cs` 打断用户测试 | 改脚本前先 `unity_editor stop`，改完刷新再进 Play |

## 11 本方案不做的事

- 不引入 GAS 那套运行时模块体系，`Assets/ThirdParty/GAS` 保持现状。
- 不把战斗、大世界、小游戏内部拆成 Data/Logic/Service，阶段 4 只做边界。
- 不改服务端协议、不改表结构、不改热更 Lua 的模块划分。
- 不为「以后可能要用」预留接口、DIContainer、生命周期钩子。
