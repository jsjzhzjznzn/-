# UI 框架化预制体规范（主页 + 每业务一个预制体）

> 适用工程：`D:\tuanjieproject\frined\RedDotSystem-main`（团结引擎 / SkierFramework UI）
> 版本：2026-10-20 第 1 版
> 关系：本文档细化并取代 `Compete战斗系统与UI框架移植方案.md` 的 P8 逐面板做法；P8-2「背包不迁移」的结论继续有效。

---

## 0. 结论先行

| 项 | 定稿 |
|---|---|
| 主界面 | **一个** `HomeView.prefab`，只装入口按钮 + 红点 + 跳转，不放业务面板 |
| 业务面板 | **一个业务一个预制体**（排行榜、挑战、任务、邮件、世界聊天、活动、阿福、副本…），由 `UIManager` 打开/关闭 |
| 背包 | **不迁移**，保留原项目 `beibao` 场景画布 + `BagUIController`（本次已恢复可见） |
| UI 相机 | 场景里**不放**任何 UI 相机；运行时 `UIManager` 自建 `SkierUICamera`（常驻） |
| 绑定方式 | 预制体根挂 `UIControlData`，View 脚本字段标 `[ControlBinding]`，**按字段名匹配** |
| 注册方式 | `UIType` 枚举 + `Assets/GameUI/UIConfig.json` 两条都要加，缺一打不开 |
| 资源收集 | 预制体必须落在 `Assets/GameUI/` 下（YooAsset `GameUI` 组已覆盖） |

一句话流程：**写/改 View 脚本 → 抽预制体 → 填 UIControlData → 注册 UIType/UIConfig → 入口按钮接跳转 → 删场景旧面板 → 回归验证 → 提交**。

---

## 1. 框架模型（写预制体前必须知道的 6 件事）

### 1.1 加载链路

```
UIManager.Open(UIType.X)
  → UIType 名 = uiId
  → UIConfig.json 查条目（path / uiLayer / isWindow / isLuaUI）
  → ResourceManager.InstantiateAsync(path)   // YooAsset，location 自动去扩展名
  → GetOrAddComponent(UIView 子类)           // 预制体根上没有脚本也能补
  → UIView.OnInit(UIControlData, controller) // 绑定 + 加 Canvas/GraphicRaycaster + 全子树设 UI 层
  → SetParent(层的 Canvas) + 按屏幕适配拉伸
  → UIView.OnOpen(userData) → OnAddListener()
```

失败提示含义：

- `未配置uiType:xxx，请检查UIConfig.cs` → `UIConfig.json` 没这条，或 `uiType` 拼写与 `UIType` 不一致。
- 预制体加载失败 → 路径写错 / 预制体不在 `Assets/GameUI` 收集范围内 / 没刷新资源。

### 1.2 UI 层级（`UILayer`，值即基准排序）

| 层 | 值 | 用途 |
|---|---|---|
| SceneLayer | 1000 | 3D 世界空间 UI |
| BackgroundLayer | 2000 | 主界面背景 |
| NormalLayer | 3000 | **默认层**，绝大多数面板 |
| DamageLayer | 3500 | 战斗飘字/血条 |
| InfoLayer | 4000 | 提示条 |
| TopLayer | 5000 | 顶层弹窗 |
| TipLayer | 6000 | Toast |
| BlackMaskLayer | 7000 | 黑屏遮罩 |

同层内每开一个界面排序 +30，关闭归还。

### 1.3 `isWindow` 语义（UIConfig 字段）

- `false`（默认，**全屏页**）：打开时下层界面 `OnPause` 且计入"被遮挡"，适合排行榜、背包式全屏页。
- `true`（**弹窗**）：下层界面暂停但不计遮挡，适合确认框、选人小窗（例：`JoinRoomView`）。

### 1.4 绑定机制（UIControlData + ControlBinding）

- 预制体根上的 `UIControlData.ctrlItemDatas`：`name` = 字段名，`targets[]` = 目标组件。
- View 脚本里的 `public Button closeButton;` 加 `[ControlBinding]`（属性可选路径参数），运行期按 **name == 字段名** 反射赋值。
- 编辑器：选中 `UIControlData` → 右上齿轮 → **复制代码到剪贴板**，粘贴到 View 类里即可生成字段，不要手打错名字。
- **不要删掉重建 `UIControlData`**（会丢全部绑定）；改绑定只增删条目。

### 1.5 尺寸与缩放

- 框架层 Canvas 已统一 `CanvasScaler`：参考分辨率 **1920×1080**，所有挂载到层上的面板按 `UIBlackType.AutoBlack` 拉伸。
- **预制体根不要再挂 `CanvasScaler`**（`UIView.OnInit` 只加 `Canvas` + `GraphicRaycaster`，多挂的 Scaler 会和层缩放语义打架）。
- 旧面板若是 `ConstantPixelSize`（1 单位 = 1 px，1280×720 时代），迁移时按 **1.5 倍** 整体缩放补偿（`FriendView` 实测结论）；先量旧面板在 1280×720 下的像素尺寸，再按 1920×1080 换算，别凭感觉。

### 1.6 相机

- `UIManager.Initialize()` 自建 `SkierUIRoot` + `SkierUICamera`（DontDestroyOnLoad，正交，`cullingMask = UI 层`，URP **Overlay** 类型）。
- `UICameraService` 在框架相机注入后只做两件事：把框架相机挂进所有 Base 相机的 Camera Stack、把场景里所有非 WorldSpace 画布改成 `ScreenSpaceCamera + worldCamera = SkierUICamera`。
- 场景里旧的 `UICamera` 会被 `EnsureCameraStacks()` **禁用**，所以留着没用。
- 团结引擎 URP 14.2-t1 分叉实测：**两台 Base 相机会互相顶掉**，UI 相机必须 Overlay —— 不要改成 Base。

---

## 2. 第一步：相机与场景清理

### 2.1 要删的

| 对象 | 位置 | 说明 |
|---|---|---|
| `UICamera`（含 `UICameraRig`） | `zhuye.scene` 根 | 框架运行时会自建并用 `SkierUICamera` 顶替，删掉不影响任何画布 |

### 2.2 不要放的

- `SkierUICamera` **不能**进场景：它由 `UIManager` 运行时创建在 `SkierUIRoot` 下并常驻，场景里再放一个会出现双 UI 相机。
- `SkierUIRoot` 同理，不要手动摆。

### 2.3 删完为什么旧画布还能显示

删除 `UICamera` 后，场景里的 9 个画布（`FriendCanvas / fuben / huodong / shijie / agent / battle / beibao / renwu / youjian`）在世界里仍是 `ScreenSpaceOverlay` 或指向已删相机；`UIManager.Initialize()` 和 `OnSceneLoaded` 会调 `AdoptAllCanvases()` 把它们逐个改到 `SkierUICamera`。**不需要手工重绑**，但迁移后的面板应尽快从场景删掉，避免"场景画布 + 预制体"两份同时存在。

### 2.4 要保留的

- `Main Camera`（Base 相机，UI 相机要挂它的 Stack）
- `EventSystem`（无则框架自建一个 DDOL 的 `EventSystem_UI`，但保留场景这个更稳）
- `RedDotManager` / `MmQuestManager` 等常驻数据层单例

---

## 3. 目录与命名规范

```
Assets/GameUI/<Module>/<Name>View.prefab      # 预制体，必须在 GameUI 下（YooAsset 收集）
Assets/Scripts/UI/<Module>/<Name>View.cs      # UIView 子类（可以是薄适配层）
Assets/Scripts/<Module>/<Xxx>UIController.cs  # 复用原业务逻辑（基类改 UIView）
```

| 规则 | 说明 |
|---|---|
| `UIType` 枚举名 == 预制体名 == `UIConfig.uiType` == 根节点名 | 三处必须完全一致（区分大小写） |
| 已有 `<Xxx>UIController` 逻辑大（>300 行） | 用 P8-1 的"逻辑复用 + 适配层"：`<Name>View : <Xxx>UIController`，不要重写业务 |
| 一个模块多页面（如好友列表/聊天） | 仍是**一个**预制体，内部用 active 切页，不要拆成多个 UIType |
| 预制体依赖（子面板、ListItem） | 放在同目录，作为嵌套预制体引用，不要跨模块引用 |

---

## 4. 预制体根节点规范

| 项 | 要求 | 原因 |
|---|---|---|
| 根组件 | `RectTransform` + `UIControlData` + `<Name>View` | 绑定 + 生命周期入口 |
| 尺寸 | 锚点 `(0,0)-(1,1)`，offset 全 0；内容放子节点 | 框架会按屏幕重新拉伸根，根尺寸不参与布局 |
| 根节点背景 | **不要**在根上挂全屏 `Image` | 会吃掉下层射线；背景放子节点 `Background` |
| Canvas | **不要**挂；确实需要分组排序时只挂 `Canvas`（不挂 `CanvasScaler`） | `UIView.OnInit` 会 `GetOrAddComponent<Canvas>()` 并统一排序 |
| Layer | 全子树 `UI`(5) | 框架 `OnOpen` 会 `SetLayerRecursively(UI)` 兜底；预制体里也设好，避免编辑器预览发黑 |
| 初始 active | 保持业务默认（列表模板行 `false`、面板根 `true`） | 框架不会替你改；`RowTemplate` 之类的 `false` 状态必须写进预制体 |
| 红点 | 入口/列表项上的 `RedDotItem` 原样保留，`m_RedDotType` 不许丢 | 红点是数据驱动，组件丢了角标就不亮 |
| 关闭按钮 | 绑到 View 的 `closeButton`，回调走 `UIManager.Instance.Close(UIType.X)` | 不要只 `SetActive(false)`，否则 order 不归还、下层不恢复 |

---

## 5. 新建一个 UI 面板：标准 8 步

### Step 1 · 写 / 改造 View 脚本

新面板（`Assets/Scripts/UI/<Module>/<Name>View.cs`）：

```csharp
using SkierFramework;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI.Example
{
    /// <summary>示例面板：字段名 = UIControlData 里的 name</summary>
    public class ExampleView : UIView
    {
        [ControlBinding] public Button closeButton;
        [ControlBinding] public RectTransform content;

        public override void OnOpen(object userData)
        {
            base.OnOpen(userData);          // 内部会调 OnAddListener + 排层
            Refresh();
        }

        public override void OnAddListener()
        {
            if (closeButton != null) closeButton.onClick.AddListener(OnCloseClicked);
        }

        public override void OnRemoveListener()
        {
            if (closeButton != null) closeButton.onClick.RemoveListener(OnCloseClicked);
        }

        private void OnCloseClicked() => UIManager.Instance.Close(UIType.ExampleView).Forget();

        private void Refresh() { /* 拉数据 → 刷 UI */ }
    }
}
```

已有控制器（例如 `QuestUIController`）：只改三处 —— 基类 `MonoBehaviour → SkierFramework.UIView`、要绑的 public 字段加 `[ControlBinding]`、纯内部方法按需改 `protected`；业务代码一行不动。

### Step 2 · 抽预制体（两种方式，二选一）

**A. 工具法（推荐，批量迁移用）**

1. 场景里选中面板根（例如 `renwu/QuestPanel`）。
2. 菜单 `Tools/UI/SkierFramework 面板迁移窗口`。
3. 填：资源分组（`Quest`）、预制体名（`QuestView`）、View 类型、是否窗口、层级、是否删除场景源。
4. 点「提取并生成预制体」→ 生成 `Assets/GameUI/<Group>/<Name>.prefab`，自动：
   - 套一层全屏 wrapper（`RectTransform` 拉伸 + View 脚本 + `UIControlData`）
   - 按 `[ControlBinding]` 路径 → 字段名 → 首字母大写 的顺序找节点并写 `ctrlItemDatas`
   - 全子树设 `UI` 层
   - 追加 `UIConfig.json` 条目
5. 手补：`UIType` 枚举项（工具不写枚举）。

**B. 手打法（小面板/微调用）**

1. 场景里把面板拖到 `Assets/GameUI/<Module>/` 存成预制体。
2. 在预制体根加 `RectTransform`（stretch）、`<Name>View`、`UIControlData`。
3. 手动加绑定（见 Step 3）。

### Step 3 · 填 UIControlData

1. 选中预制体根 → `UIControlData` 组件 → 点齿轮 → **复制代码到剪贴板**。
2. 粘到 View 脚本类里（生成 `public Button xxx;` 之类的字段）。
3. 回到预制体，把对应控件拖进 `ctrlItemDatas` 的 `targets`，`name` 必须和字段名一致。
4. 字段类型与控件类型必须匹配（`Button / Image / TMP_Text / ScrollRect / RectTransform`…），否则运行期跳过并告警。

> 校验：运行时 `UIControlData.bindUIRefs` 与字段应为全非空；有 `找不到字段 / 字段为空` 的告警就是名字或类型错了。

### Step 4 · 注册 UIType + UIConfig

```csharp
// Assets/Scripts/UI/Framework/UIViewBase/UIType.cs
public enum UIType
{
    Dating, Xuanze, room, jiaru, bloodpanel,
    SmokeView, LeaderboardView, BattleLobbyView, JoinRoomView, BattleRoomView,
    FriendView,
    ExampleView,   // ← 新面板加在 Max 之前
    Max,           // 哨兵，永远是最后一项
}
```

```json
// Assets/GameUI/UIConfig.json items 末尾追加
{
  "uiType": "ExampleView",
  "path": "Assets/GameUI/Example/ExampleView.prefab",
  "isWindow": false,
  "uiLayer": "NormalLayer",
  "isAutoNavigation": false,
  "isLuaUI": false,
  "luaModuleName": ""
}
```

- `isLuaUI: true` 只给纯 Lua 热更 UI（`LuaOnlyView` 通用壳，`uiType` 可以不进枚举）。
- `isAutoNavigation` 只影响返回键栈，普通页保持 `false`。

### Step 5 · YooAsset 收集

- `Assets/BundleCollectorSetting.asset` 已有 `GameUI` 组：`CollectPath = Assets/GameUI`、`AddressByFileName`、`PackSeparately`。
- 新预制体只要放在 `Assets/GameUI/` 下即被收集，**不需要改配置**；改完执行 `refresh`，打包构建时重建 bundle。
- 路径寻址由 `ResourceManager.NormalizeToLocation` 处理：`Assets/GameUI/X/Y.prefab → Assets/GameUI/X/Y`（去扩展名、`Assets/Resources/` 前缀会剥掉）。

### Step 6 · 接入口

两种写法：

| 场景 | 做法 |
|---|---|
| 纯"点开某面板"的按钮 | 挂 `OpenUIPanelButton`，Inspector 选 `UIType`（零代码） |
| 需要先算数据 / 需要跳转链 | 在 `HomeView` 里 `[ControlBinding] Button` + `UIManager.Open(type, userData)` |

### Step 7 · 关闭

- 一律 `UIManager.Instance.Close(UIType.X)`（归还排序、恢复下层）。
- 页面间"1→2→3 再回退"用 `UIManager.JumpUI(...)`，不要自己 `SetActive`。
- 常驻页（主页）用 `UIManager.Instance.AddResidentUI(UIType.HomeView)`，切场景 `CloseAll` 时不会被关掉（`closeResidentView:false` 默认）。

### Step 8 · 删场景旧面板

- 先验证（Step 见 §8.5），再删场景里的旧根对象与其控制器组件。
- 删完重新保存场景，确认没有 `Missing Script`、没有重复 Canvas 接收同一批点击。

---

## 6. 主页 `HomeView` 设计（核心）

### 6.1 职责边界

| 做 | 不做 |
|---|---|
| 放所有**入口按钮**（好友/排行榜/挑战/任务/邮件/世界/活动/阿福/副本/战斗/背包） | 不放任何业务列表、滚动区、战斗 HUD |
| 按钮点击 → `UIManager.Open(UIType.X)` | 不自己 `SetActive` 打开业务面板 |
| 入口红点（`RedDotItem`）保留在按钮上 | 不在这里写红点数据逻辑（数据层在 Manager） |
| 常驻（`AddResidentUI`） | 不随场景切换销毁 |

### 6.2 节点结构

```
HomeView                     (RectTransform stretch + UIControlData + HomeView)
├── Background               (可选，全屏底图，raycastTarget=false)
├── EntryBar
│   ├── FriendEntryButton    (Button + RedDotItem Key14)
│   ├── LeaderboardEntryButton
│   ├── ChallengeEntryButton
│   ├── QuestEntryButton     (RedDotItem Key16)
│   ├── MailEntryButton      (RedDotItem Key5)
│   ├── WorldEntryButton
│   ├── ActivityEntryButton
│   ├── AgentEntryButton
│   ├── FubenEntryButton
│   └── BattleEntryButton
└── PlayerInfo               (头像/名字/等级，可选)
```

### 6.3 入口对照表（现状 → 目标）

| HomeView 字段 | 原位置 | 目标 | 红点 |
|---|---|---|---|
| `friendButton` | `FriendCanvas/FriendUIRoot/FriendEntryButton` | `UIType.FriendView` | `RedDotKeys.FriendEntry`(14) |
| `leaderboardButton` | `FriendCanvas/FriendUIRoot/LeaderboardEntryButton` | `UIType.LeaderboardView` | — |
| `challengeButton` | `FriendCanvas/FriendUIRoot/ChallengeEntryButton` | `UIType.ChallengeView` | — |
| `battleButton` | `battle/BattleEntryButton` | `UIType.BattleLobbyView` | — |
| `fubenButton` | `fuben/FubenEntryButton` | `SceneManager.LoadScene("fuben")` | — |
| `activityButton` | `huodong/ActivityEntryButton` | `ActivityShell.OpenActivity()`（Lua 热更） | — |
| `agentButton` | `agent/AgentEntryButton` | `UIType.AgentView` | — |
| `worldButton` | `shijie/WorldEntryButton` | `UIType.WorldChatView` | — |
| `questButton` | `renwu/QuestEntryButton` | `UIType.QuestView` | `RedDotKeys.QuestEntry`(16) |
| `mailButton` | `youjian/MailEntryButton` | `UIType.MailView` | `RedDotKeys.MailEntry`(5) |
| （不搬）| `beibao/BagEntryButton` | 留在 `beibao`，`BagUIController` 自己开面板 | — |

> 入口曾"消失"的根因：入口按钮和面板同根，面板根被 `SetActive(false)` 后入口一起消失（`beibao.m_IsActive=0`、`renwu/youjian` 根 inactive）。P9 之后入口全部在 `HomeView` 里，面板预制体不再带入入口。

> 背包/邮件/任务这三个入口原本和面板同根，面板根一关入口就跟着消失 —— 这正是"背包看不见"的根因（`beibao.m_IsActive=0`）。迁移后入口统一搬到 `HomeView`，面板预制体里不再放入口按钮。

### 6.4 `HomeView.cs` 骨架

```csharp
using Cysharp.Threading.Tasks;
using SkierFramework;
using UnityEngine;
using UnityEngine.UI;

/// <summary>主界面：只做入口跳转，常驻不销毁</summary>
public class HomeView : UIView
{
    [ControlBinding] public Button friendButton;
    [ControlBinding] public Button leaderboardButton;
    [ControlBinding] public Button challengeButton;
    [ControlBinding] public Button questButton;
    [ControlBinding] public Button mailButton;
    [ControlBinding] public Button worldButton;
    [ControlBinding] public Button activityButton;
    [ControlBinding] public Button agentButton;
    [ControlBinding] public Button fubenButton;
    [ControlBinding] public Button battleButton;
    [ControlBinding] public Button bagButton;      // 背包走原控制器，不进 UIManager

    public override void OnAddListener()
    {
        friendButton.onClick.AddListener(() => UIManager.Instance.Open(UIType.FriendView).Forget());
        leaderboardButton.onClick.AddListener(() => UIManager.Instance.Open(UIType.LeaderboardView).Forget());
        challengeButton.onClick.AddListener(() => UIManager.Instance.Open(UIType.ChallengeView).Forget());
        questButton.onClick.AddListener(() => UIManager.Instance.Open(UIType.QuestView).Forget());
        mailButton.onClick.AddListener(() => UIManager.Instance.Open(UIType.MailView).Forget());
        worldButton.onClick.AddListener(() => UIManager.Instance.Open(UIType.WorldChatView).Forget());
        activityButton.onClick.AddListener(() => UIManager.Instance.Open(UIType.ActivityView).Forget());
        agentButton.onClick.AddListener(() => UIManager.Instance.Open(UIType.AgentView).Forget());
        fubenButton.onClick.AddListener(() => UIManager.Instance.Open(UIType.DungeonView).Forget());
        battleButton.onClick.AddListener(() => UIManager.Instance.Open(UIType.BattleLobbyView).Forget());
    }

    public override void OnRemoveListener() { /* 逐个 RemoveListener，与上面成对 */ }
}
```

> 说明：`ChallengeView / QuestView / MailView / WorldChatView / ActivityView / AgentView / DungeonView` 这些 `UIType` 项随对应面板逐个新增；`bagButton` 暂不接线（背包留在原实现，见 §6.5-4）。

### 6.5 搬按钮的注意事项

1. **坐标**：先把 9 个画布上的入口按钮在 1920×1080 下的屏幕坐标量出来，按同一坐标摆到 `HomeView.EntryBar`（旧画布若是 ConstantPixelSize，先 ×1.5）。
2. **红点**：`RedDotItem` 组件整体搬（含 `m_DotObj`、`m_DotCountText`、`m_RedDotType`），不要重挂新组件后忘记配 Key。
3. **重复入口**：`HomeView` 有了入口后，旧画布里的同名入口按钮要删，否则两套入口两套红点。
4. **背包入口**：留在 `beibao` 里（原 `BagUIController.entryButton` 直接接线），`HomeView` 先不放；等背包也统一时再加一个 `bagButton` 调 `BagUIController` 的公开方法。
5. **首次打开时机**：登录成功 / 主场景加载完成后 `Open(UIType.HomeView)` + `AddResidentUI`，不要挂在场景序列化对象上。

---

## 7. 业务面板清单与顺序

| 序 | 业务 | 原场景根 | 预制体 | View | isWindow | 状态 |
|---|---|---|---|---|---|---|
| 0 | 主界面 | 9 个画布上的入口 | `GameUI/Home/HomeView.prefab` | `HomeView` | false | **完成（P9-1）** 10 个入口按钮 + 红点，常驻 |
| 1 | 好友+聊天 | `FriendCanvas/FriendUIRoot` | `GameUI/Friends/FriendView.prefab` | `FriendView` | false | **完成（P8-1）** 33 绑定，1.5× 缩放 |
| 2 | 排行榜 | — | `GameUI/Leaderboard/LeaderboardView.prefab` | `LeaderboardView` | false | **完成（P2）** |
| 3 | 战斗大厅/房间 | `battle` | `GameUI/Battle/*.prefab` | `BattleLobbyView` 等 | 视面板 | **完成（P3）** |
| 4 | 任务 | `renwu` | `GameUI/Quest/QuestView.prefab` | `QuestView` | false | **完成（P9-2）** 15 绑定 |
| 5 | 邮件 | `youjian` | `GameUI/Mail/MailView.prefab` | `MailView` | false | **完成（P9-3）** 13 绑定 |
| 6 | 世界聊天 | `shijie` | `GameUI/Chat/WorldChatView.prefab` | `WorldChatView` | true | **完成（P9-4）** 8 绑定；不遮挡下层 |
| 7 | 挑战 | `FriendCanvas/.../ChallengePanel` | `GameUI/Challenge/ChallengeView.prefab` | `ChallengeView` | false | **完成（P9-5）** 7 绑定 |
| 8 | 阿福 | `agent` | `GameUI/Agent/AgentView.prefab` | `AgentView` | true | **完成（P9-6）** 17 绑定；保留开合动画 |
| 9 | 活动 | `huodong` | **不做预制体** | Lua 热更（`ActivityShell`） | — | 入口已并入 HomeView，点开仍走热更外壳 |
| 10 | 副本 | `fuben` | **不做预制体** | 切场景 `fuben.scene` | — | 入口已并入 HomeView，点开直接 LoadScene |
| — | 背包 | `beibao` | **不做** | `BagUIController`（原样） | — | 用户定稿：保留原项目实现 |
| — | 战斗 HUD | — | `GameUI/Battle/bloodpanel.prefab` | `bloodpanel` | false | 完成（P6-1） |

顺序原则：**入口少的先做**（任务/邮件），**逻辑重的后做**（阿福），每做完一个删一个画布，场景里的旧画布数量单调递减。

---

## 8. 单面板迁移 SOP（照做即可）

### 8.1 抽离前快照

- `git status` 干净、可回滚；本面板相关文件记一份清单。
- 编辑器里 Play 一次，截图/记下：面板屏幕矩形、字号、按钮位置、初始 active 状态、红点 Key。
- 列出该控制器所有 `public` 序列化字段（这些将是 `[ControlBinding]` 候选）。

### 8.2 逻辑适配（只改接口，不改业务）

1. 基类：`MonoBehaviour → SkierFramework.UIView`。
2. 字段加 `[ControlBinding]`（路径不一致时用 `[ControlBinding("子物体路径")]`）。
3. 打开/关闭：删掉"入口按钮 + `panelRoot.SetActive`"那套，改 `OnOpen` 拉数据、`UIManager.Close` 关闭。
4. 私有方法被适配层调用时改 `protected`。
5. 保留原有的 `Awake/Start` 初始化顺序，别顺手重排。

### 8.3 生成预制体 + 绑定

按 §5 Step 2/3。生成后立刻在预制体上核对：

- 根：`UIControlData` + View 脚本 + `RectTransform` stretch；
- 绑定条目数 == 字段数；每条 `targets` 非空；
- 全子树 Layer = UI；
- 关闭按钮/列表模板/滚动区指向正确节点。

### 8.4 入口切换 + 删旧

1. 入口按钮挂 `OpenUIPanelButton`（或 `HomeView` 里接线）。
2. Play 验证新面板打开正常。
3. 删场景旧根 + 旧控制器组件 + 旧画布（若画布只服务这个面板）。
4. 保存场景。

### 8.5 回归验证（每个面板必过）

1. `UIManager.Open(UIType.X)`：实例出现，`layer=5`，`parent=NormalLayer`，`worldCamera=SkierUICamera`，`sortingOrder ≥ 3000`。
2. 绑定全非空，无 `字段为空/找不到` 告警。
3. 面板内容与迁移前逐项对比（标题、列表条数、滚动、按钮文案、红点）。
4. 关闭 → `IsOpen=false`，下层界面恢复；再打开复用同一实例。
5. 场景里不存在同名旧面板，无 `Missing Script`，Console 无红字。
6. 视口 1920×1080 与 1280×720 各看一次（拉伸/留边正确）。

### 8.6 提交约定（每个改动一次提交）

```
refactor(ui): <面板> 抽出 <Name>View 预制体并接入框架（P9-x）
```

- 一次提交只包含一个面板（脚本 + 预制体 + meta + UIConfig + UIType + 场景）。
- 文档更新单独提交：`docs(ui): <说明>`。
- 回滚：`git revert <sha>` 即可整体撤销（场景与预制体同进同退）。

---

## 9. 已知坑（本项目实测，踩过的别再踩）

| 现象 | 原因 | 处理 |
|---|---|---|
| 面板逻辑正常但画面全黑/不可见 | 预制体全在 `Default` 层，UI 相机只渲染 `UI` 层 | `UIView.OnInit` 已 `SetLayerRecursively(UI)`；预制体里也设好 |
| 面板大小不对、字巨大/巨小 | 预制体根自挂 `CanvasScaler`（默认 ConstantPixelSize） | 删掉，交给层 Canvas |
| 旧面板在新框架里小一圈 | 旧画布 1 单位 = 1 px（1280×720），框架参考 1920×1080 | 整体 ×1.5（`FriendView` 实测） |
| 打开报"未配置 uiType" | 只加了 `UIType` 没加 `UIConfig`，或名字不一致 | 两处同名 |
| 预制体加载失败 | 不在 `Assets/GameUI` 下 / 资源未刷新 | 移到 GameUI、`refresh`、重打 bundle |
| 绑定某字段永远为 null | `ctrlItemDatas.name` 与字段名不一致 / 类型不匹配 | 齿轮复制代码，别手打 |
| 绑定全丢 | 删除重建了 `UIControlData` | 只增删条目，不重建 |
| 入口/面板消失 | 入口按钮和面板同根，根被 `SetActive(false)` | 入口搬到 `HomeView`（本次背包就是这个） |
| 列表模板行多出一条空行 | 迁移时把 `RowTemplate` 设成 active | 保持 `false` |
| 关闭后面板还在 / 下层黑 | 关闭只 `SetActive(false)` | 走 `UIManager.Close` |
| 有两层黑边/留白 | 层 Canvas 已做黑边，面板内再套一层适配 | 面板只做布局，不做屏幕适配 |
| UI 被 3D 挡住 / 3D 全黑 | 把 UI 相机改成 Base 类型 | 必须 Overlay，并挂到 Base 的 Stack |
| 背包迁移后只剩白底 + 灰块 | 程序化背景瓦片/格子边框贴图路径在框架画布下失效 | **不迁移**（已定稿）；若重开先查贴图生成与 TMP 可见性 |
| 绑定全为空 / 只绑上前几个字段 | `UIControlData.type` 写了字段类型名 `TMP_Text`，而框架只认 `_typeMap` 的键（`TextMeshProUGUI`/`Button`/`Image`/`RectTransform`…），`FindCorrectComponent` 直接返回 null 并中断整张表的绑定 | 迁移工具一律写 `component.GetType().Name`；已绑定的预制体要重写 type 后重绑 |
| 绑定工具抛 `GetComponent requires ... 'GameObject'` | `GameObject` 类型字段不能走 `GetComponent` | 迁移器里 `field.FieldType == typeof(GameObject)` 时直接取 `target.gameObject`，type 写 `GameObject` |
| 面板里"指向自己"的字段（如 `panelRect`/`panelCanvas` 指向面板根）绑不上 | 绑定路径相对**面板根**，无法用路径表示根自身 | 在 `OnInit` 里自解析（`transform.Find("AgentPanel")` / `GetComponentInChildren<CanvasGroup>()`），不要加 `[ControlBinding]` |
| 编辑器里 Play 后 MCP 全部超时（`ping not answered`） | Play 模式被暂停（`isPaused=true`），MCP 的 ping 依赖帧推进 | 先恢复/停止播放（UnitySkills REST `POST /skill/editor_stop` 或 `editor_pause` 可救），别在暂停状态下继续调工具 |
| 播放中改脚本后游戏卡住 | 编译触发 Unity 暂停播放并等待重载 | **改脚本前先 Stop**；Play 只做验证 |
| 搬家后的入口按钮位置偏了 | 只复制了 `anchoredPosition`，父物体尺寸不同 | 锚点/轴心/尺寸/缩放一起复制（本项目的 PanelMigrator 已这样做） |

---

## 10. 整体验收（2026-10-20 实测）

- [x] 场景 `zhuye` 根节点只剩：`Main Camera / Directional Light / RedDotManager / MmQuestManager / EventSystem / GameEntry / beibao`。
- [x] 场景无 `UICamera`；运行时有且只有一个 `SkierUICamera`（`SkierUIRoot` 下，Overlay）。
- [x] 每个业务都能从 HomeView 入口打开 → 关闭 → 再打开（好友/排行榜/战斗/副本/活动/任务/邮件/世界/挑战/阿福逐个验证）。
- [x] `CloseAll(null,false)` 后只剩 `HomeView(Clone)` 处于 active。
- [x] 红点：好友入口 Key14 由数据层驱动，面板不打开也亮（`RedDotItem` 随按钮搬进 HomeView）。
- [ ] 1280×720 下未逐面板复测（1920×1080 通过；HomeView 用锚点+anchoredPosition 复制，理论上随 CanvasScaler 等比缩放）。
- [x] Console 无红字、无 `Missing Script`、无重复 `EventSystem`。
- [x] 9 个面板的 `UIControlData` 绑定数与字段数一致且无 null 字段（33/15/13/8/7/17 + 排行榜/战斗大厅/好友）。

---

## 11.5 P10：登录后再开主页 + 背包收进主页（2026-10-20）

**主页打开时机**

- `HomeViewLauncher` 不再 `Start` 立刻 Open：已登录（`ClientNetworkManager.MyPlayerId > 0`，从登录/战斗场景回来）直接开；否则订阅 `LoginResponseReceived`，登录成功后再 `Open(HomeView)` + `AddResidentUI`。
- 未登录时主页不出现（登录 UI 自己负责前置流程）。

**背包（仍是原项目场景面板，不做预制体）**

| 项 | 处理 |
|---|---|
| 入口 | `beibao/BagEntryButton` 搬进 `HomeView.prefab`（`bagButton`），主页 11 个入口统一；`beibao` 画布只剩 `BagPanel` |
| 调用 | `BagUIController.Instance.OpenPanel() / ClosePanel()`（新增）；框架不接管背包 |
| 层级 | `beibao` 画布 `overrideSorting=true, sortingOrder=3100`，压在 HomeView（层画布 3030）之上 |
| 窗口化 | 面板从全屏铺满 → 居中 1440×800 窗口，满屏背光遮罩 `BagDim`（黑 55%）+ 2px 描边；点遮罩即关面板，同时挡住主页点击 |
| 槽位 | 6 列 ×140 → **8 列 ×100**（`RowTemplate` 重建 8 格、`columns=8`、`cellSize=100`、行高=cellSize） |
| 右列 | 详情宽 330、预览 160×160、使用/丢弃 150×38、标题 28 号、状态行下移到页脚 |

> 背包仍走 `panelRoot.SetActive`，与框架的 `UIManager` 并行；只要保证 `beibao` 的 sortingOrder 高于 HomeView 层即可正常盖住。

## 11.6 P11/P12：背包预制体化 + 主页入口栏重排（2026-10-20）

**背包（P11，已改为框架预制体）**

- `BagUIController → BagView`（`UIView`，18 项 `[ControlBinding]`），`Assets/GameUI/Bag/BagView.prefab`，`UIType.BagView` + UIConfig（NormalLayer, isWindow=false）；`zhuye` 里的 `beibao` 画布**已删除**。
- 关键修正：程序化贴图必须在 `base.OnInit` 之后生成（放 `Awake` 时绑定还没发生，引用为 null —— 这正是 P8-2 背包"只剩白底灰块"的根因）；虚拟列表必须放到 `OnOpen`（`OnInit` 时面板还没被拉伸，会按 0 尺寸算成 1 行）。
- 行列/格子尺寸改为以 `RowTemplate` 为准（8 列 ×100），背景网格瓦片同步 cellSize。

**主页入口栏（P12）**

- 11 个入口从"右上角散落 + 110×44 绿块"改成**底部整条 EntryBar 1920×132**，按钮 140×86、间距 152、水平居中，顺序：好友/排行榜/挑战/任务/邮件/世界/活动/阿福/副本/对战/背包。
- 新增 `Assets/GameUI/Home/Art/BarPanel.png`、`EntryButton.png`：程序化生成的 9 宫格圆角贴图（深底 + 亮边，border 已配好），按钮 `Image.Type.Sliced` + `ColorTint` 高亮。
- 文字 20 号居中浅色；红点角标固定按钮右上角内缩 6px；绑定与跳转逻辑不变（11/11）。

## 11.7 P13：对战选人房间对齐 compete（2026-10-20）

**布局**（`Assets/GameUI/Battle/BattleRoomView.prefab`，整屏）

| 区域 | 内容 |
|---|---|
| 左右 | 我方 / 对手 660×740 大预览卡（圆角 9 宫格底 + 3D 预览 + 名字 + 准备状态 + 空态提示） |
| 中间 | VS |
| 底部 | 安比 / 艾莲 两个 220×130 大按钮 + 准备（300×96）+ 房主「开始游戏」（240×96） |
| 顶部 | 标题 + 副标题，右上关闭 × 与退出房间 |
| 其他 | 右下 IP（房主显示本机 IP:7777）、底部状态行、居中飘字 |

**选人预览模型**（与 compete 一致）

- 安比：`Assets/Resource/人物/Real/安比test.prefab`
- 艾莲：`Assets/Resource/人物/Real/le_Size02_Ellen_Ani_Idle (1).prefab`
- 构建期复制成 `Assets/Resources/Battle/Heroes/{AnbiPreview,EllenPreview}.prefab`，运行期 `Resources.Load` + `UIModelManager.LoadModelToRawImage(GameObject,...)`；卸载时自己 Destroy 实例再 `UnLoadModelByRawImage(raw, false)`（否则会报"不是本模块实例化出去的对象"并泄漏）。

**联机链路（与 compete 的 RoomState 对照）**

| 环节 | compete | 本项目 |
|---|---|---|
| 点英雄 | 本地预览 + SelectChar 上报 | 同：本地预览 + `MsgSelectChar`（房主直调 `ServerSetCharId`） |
| 服务端 | 记录并 `PeerSelectChar` 转发 | 同：`ServerSetCharId` → 广播 `MsgPeerSelectChar` + 本地事件 |
| 对手看到 | `roleenemy` 实时换模型 | 同：`OnPeerCharIdChanged` → `UpdateEnemyPreview` + 飘字「对手选择了 X」 |
| 打开房间 | 拉一次对手状态 | 同：`MsgQueryPeerChar`（客户端）/ 直接读字典（房主） |
| 准备 | `ServerSetReady`，广播 PeerReady | 同 |
| 开局 | 双方准备 → 服务端自动 LoadScene | **改为房主手动**：`RequireHostStart=true`，房主按钮双方准备后可点 → `ServerStartBattle()` → `NetworkSceneManager.LoadScene("multiplecompete")` |

**联机预制体加载修正**：`BattleNetworking.prefab` 从 `Assets/Prefabs/Battle` 移到 `Assets/Resources/Battle`（原来不在 YooAsset 收集范围，创建房间直接失败），`BattleNetworkLauncher` 改 `Resources.Load`。

## 11. 进度跟踪

| 日期 | 面板 | 状态 | 提交 |
|---|---|---|---|
| 2026-10-19 | 排行榜 | 完成（P2） | 6d2601d |
| 2026-10-20 | 好友/聊天 | 完成（P8-1） | b7dcefe |
| 2026-10-20 | 背包 | 定稿不迁移；`beibao` 重新启用（入口修复） | cda5502 |
| 2026-10-20 | 本文档 | 完成 | 4acab70 |
| 2026-10-20 | 主页 HomeView（P9-1） | 完成：5 → 10 个入口按钮 | 247d0f1 |
| 2026-10-20 | 任务 QuestView（P9-2） | 完成：15 绑定 | 见 `git log --grep=P9-2` |
| 2026-10-20 | UIControlData.type 修正 | 完成（TMP_Text → TextMeshProUGUI） | 21869f1 |
| 2026-10-20 | 邮件 MailView（P9-3） | 完成：13 绑定 + 通用 `PanelMigrator` | 见 `git log --grep=P9-3` |
| 2026-10-20 | 世界聊天 WorldChatView（P9-4） | 完成：8 绑定 | 见 `git log --grep=P9-4` |
| 2026-10-20 | 挑战 ChallengeView（P9-5）+ 删场景 UICamera | 完成：7 绑定 | 见 `git log --grep=P9-5` |
| 2026-10-20 | 阿福 AgentView（P9-6） | 完成：17 绑定 | 见 `git log --grep=P9-6` |
| — | 活动 / 副本 | 不需要预制体：热更外壳 / 切场景 | — |

### 11.1 P9 收尾后的场景与预制体

- `zhuye.scene` 根节点只剩：`Main Camera / Directional Light / RedDotManager / MmQuestManager / EventSystem / GameEntry / beibao`。
- 已无任何 `Canvas` 常驻画布、无 `UICamera`；运行时 `SkierUIRoot`（DDOL）下才有 `SkierUICamera` + 8 个层画布。
- `Assets/GameUI/` 下：`Home / Friends / Leaderboard / Battle / Quest / Mail / Chat / Challenge / Agent` + `SmokeView.prefab`。
- 每个面板都有对应的 `Editor/*ViewBuilder.cs`（`Tools/UI/构建 <Name>View 预制体`），后续改布局可一键重建。
