# 对话 UI 迁入 UI 框架方案（duihua）

> 目标：把 `duihua` 里"野画布"形态的对话界面（底部对话条 + 选项）改成 `SkierFramework` 登记面板，
> 由 `UIManager` 负责加载/层级/开合，同时保持现有视觉、动效、打字机、选项交互与存档恢复行为不变。
> 本文档为施工蓝图，实施后把实测结论回填 §11。

---

## 0. 结论

**值得做，推荐方案 B（整体迁入框架）。** 收益集中在一致性，而不是修 bug：

| 收益 | 说明 |
|---|---|
| 层级/排序统一 | 现状是场景里手写 `sortingOrder=50` 的画布；迁移后由 `NormalLayer` + `UIManager` 统一排序，不再和 HUD/设置抢层级 |
| 生命周期统一 | 现状跟场景走；迁移后由框架缓存/复用、统一 `OnOpen/OnClose/OnRelease` |
| 可复用 | 以后副本、挑战、其他场景要加对话，`Open(UIType.DialogueView)` 即可，不再复制一整棵场景子树 |
| 可热更 | 未来若对话走 Lua UI（活动面板已有先例），预制体+UIConfig 已就位 |
| 美术迭代 | 改预制体而不是改 `duihua.scene`，避免场景冲突 |

成本约 **1 人天**，主要风险是抽预制体后的排版复核（非 16:9）与"框架缓存实例"带来的状态重置。

---

## 1. 现状与证据

| 事实 | 证据 |
|---|---|
| 对话 UI 是 `duihua.scene` 内的独立画布 | 场景对象 `GenshinDialogCanvas`；`UIConfig.json` 中**没有**任何 Dialogue/Dialog 条目；`UIType` 无对应值 |
| 视图脚本是普通 MonoBehaviour | `Assets/Game/Demo/GenshinDialogView.cs:18`：`class GenshinDialogView : MonoBehaviour, IView`，在 `Start()` 里 `FindFirstObjectByType<DialogueRunner>()` 自绑 |
| 画布参数 | `ScaleWithScreenSize 1920x1080`、`ScreenMatchMode.Match(0.5)`、`RenderMode=ScreenSpaceCamera`、`sortingOrder=50` |
| 框架画布参数 | `UIManager.width/height = 1920x1080`；`UIExtension.ApplyScaler` 用 `ScreenMatchMode.Shrink` → **16:9 下缩放完全一致**，非 16:9 才有差异 |
| 视觉由编辑器工具程序化生成 | `Assets/Game/Demo/Editor/DialogueUiBuilder.cs`，菜单 `Tools/UI/重建对话界面`，会重写 `GenshinDialogView` 的引用并生成三张贴图 |
| 设置面板已经走框架 | `DialogueSceneMenu` → `UIManager.Open(UIType.SettingsView, SettingsViewArgs{pauseWorld, showReturnHome})`；`UIConfig` 中 `SettingsView` 为 `NormalLayer`、`isWindow=true` |
| 框架视图规范 | `UIView.OnInit` 会补 `Canvas + GraphicRaycaster`、整棵子树设 `UI` 层、不加 `CanvasScaler`；类名必须与 `UIType` 同名（`ResolveViewType` 按类名解析） |
| 预制体构建先例 | `BattleLobbyPrefabBuilder.FinishPrefab`：根节点挂 `UIControlData(ctrlItemDatas)` + 视图脚本 → `SaveAsPrefabAsset` |

---

## 2. 方案对比

| 方案 | 内容 | 评价 |
|---|---|---|
| A 保持现状 | 场景内画布 + 自绑 | 最省；但层级/复用/热更都不统一，和项目"框架收敛"方向相悖 |
| **B 整体迁入（推荐）** | `DialogueView : UIView` + 预制体 + `UIConfig`，导演负责开合 | 一次到位；成本集中在前端排版复核 |
| C 双轨 | 框架里挂一个空壳 View，真 UI 仍在场景 | 只买到"名字进了框架"，排序/复用/热更都没解决，反而多一层跳转 —— 不推荐 |
| D 只登记不迁移 | 仅加 `UIType/UIConfig`，驱动不变 | 与 C 同问题 |

定案 **B**。

---

## 3. 目标形态

### 3.1 预制体结构（`Assets/Game/UI/Dialogue/DialogueView.prefab`）

```
DialogueView                     ← 全屏透明根（anchor 0..1 / offset 0 / pivot .5）
├─ DialogPanel                   ← 底部对话条（anchorMin(0,0) anchorMax(1,0) pivot(0.5,0)，高度沿用现值）
│  ├─ Backdrop                   ← 渐变贴图（dialog_gradient，九宫格/横向拉伸）
│  ├─ Accent                     ← 顶部金色细线
│  ├─ SpeakerRow
│  │  ├─ Diamond                 ← 金色菱形
│  │  └─ SpeakerName             ← TMP（NPC 金 / 玩家冰蓝 / 旁白米白）
│  ├─ Underline                  ← 金色下划线
│  ├─ ContentText                ← 正文 TMP（打字机）
│  └─ ContinueArrow              ← 继续箭头（浮动 + 呼吸）
└─ OptionRoot                    ← 右侧竖排选项容器（默认关）
   ├─ OptionButton_0
   ├─ OptionButton_1
   └─ OptionButton_2
```

要点：

- **根节点不要带 `Canvas / CanvasScaler / GraphicRaycaster`**（`UIView.OnInit` 会补 Canvas+Raycaster；自带 CanvasScaler 会用默认 ConstantPixelSize 打架 —— 登录面板迁移时踩过）。
- 根节点全屏拉伸，`DialogPanel` 锚在底部；选项仍贴右侧竖排（现状如此）。
- 根节点挂：`UIControlData`（绑定下表）+ `DialogueView` 脚本。
- 交互提示 `[F]` 留在 HUD，不进对话面板。

### 3.2 UIControlData 绑定（与 `[ControlBinding]` 字段同名）

| 字段 | 目标 |
|---|---|
| `root` | 根 `DialogueView` |
| `panelRoot` | `DialogPanel` |
| `panelCanvasGroup` | `DialogPanel` 上的 `CanvasGroup` |
| `speakerNameText` / `contentText` | 两个 TMP_Text |
| `continueArrow` | 箭头 `CanvasGroup` |
| `optionRoot` | `OptionRoot` |
| `optionButtons` | `OptionButton_0..2`（`Button[]`） |

### 3.3 层级与排序

- `UIConfig`：`uiLayer = NormalLayer`、`isWindow = false`、`isLuaUI = false`。
- 理由：对话先开、设置后开；`SettingsView` 同为 `NormalLayer`，后开的排在栈顶，能正常盖住对话条。放 `TopLayer` 会把设置压在下面。
- 不再手写 `sortingOrder`：由层 Canvas + `UIView.OnOpen` 的 `SortOrder()` 接管。

---

## 4. 代码改动

### 4.1 枚举与配置

```csharp
// UIType.cs
DialogueView = 23,
Max = 24,
```

```json
// UIConfig.json
{ "uiType": "DialogueView",
  "path": "Assets/Game/UI/Dialogue/DialogueView.prefab",
  "isWindow": false, "uiLayer": "NormalLayer",
  "isAutoNavigation": false, "isLuaUI": false, "luaModuleName": "" }
```

### 4.2 `DialogueView : UIView`

建议**把 `GenshinDialogView` 的逻辑整体搬进 `DialogueView`**（而不是再套一层组件），保持一份实现：

```csharp
public class DialogueView : UIView
{
    [ControlBinding] public GameObject panelRoot;
    [ControlBinding] public CanvasGroup panelCanvasGroup;
    // ... 其余绑定字段与现状一致

    public override void OnInit(UIControlData data, UIViewController controller)
    {
        base.OnInit(data, controller);
        CacheBasePositions();   // 原 Awake 的基准值缓存
        ResetUi();              // 选项关、正文空、箭头关、alpha=0
    }

    public override void OnOpen(object userData)
    {
        base.OnOpen(userData);
        ResetUi();
        Bind((userData as DialogueRunner)?.ViewModel);   // 由导演传入 runner，避免场景里再 Find
    }

    public override void OnClose()
    {
        Unbind();
        KillAllTweens();
        ResetUi();
        base.OnClose();
    }

    private void Update()
    {
        if (vm == null || vm.CurrentNode == null) return;   // 未绑定/未打开直接忽略
        if (PauseManager.IsPaused) return;
        if (Time.frameCount == lastNodeFrame) return;
        // ...（与现状相同的推进/选项键盘逻辑）
    }
}
```

要点：

- **不要实现框架 `UIViewAnim`**：面板淡入/滑入继续用视图自己的 DOTween，避免"暂停时开面板卡在动画首帧"的老问题。
- `Bind/Unbind` 仍走 Mm 的 `IView` 接口（`DialogueViewModel` 不变）。
- `IsTyping / CurrentShownText` 等测试钩子保留。

### 4.3 导演驱动开合（`GenshinDialogDirector`）

```csharp
public async UniTask StartDialogueAsync()
{
    if (InDialogue || runner == null) return;
    if (PauseManager.IsPaused) return;
    if (interactionZone == null || !interactionZone.PlayerInRange) return;

    InDialogue = true;
    if (playerController != null) playerController.InputLocked = true;
    // 相机、转向、指针等与现在一致

    await UIManager.Instance.Open(UIType.DialogueView, runner);   // 先开 UI
    runner.StartDialog();                                          // 再触发节点（View 已绑定）
}
```

- **存档恢复路径也必须先 Open**：`RestoreDialogue(nodeId, ...)` 里先 `await Open` 再 `PlayGraphFrom`，否则恢复时第一句的 `NodeChanged` 没有 View 接收。
- 对话结束：`DialogEnded` → 播完 0.25s 淡出后再 `UIManager.Close(UIType.DialogueView)`（用 DOTween 的 `OnComplete` 或协程等待；不能立刻关，否则淡出看不到）。
- `TryStartDialogue()` 保留为同步入口（内部 `StartDialogueAsync().Forget()`），交互区回调与 E2E 直调不变。
- 关闭时机与 `PauseManager`：暂停中不关闭面板（暂停发生在对话中，关闭会打断冻结中的表现）。

### 4.4 场景与构建工具

- `duihua.scene`：删除 `GenshinDialogCanvas` 整棵子树（迁移验收通过后再删，见 §10 回滚）。
- `DialogueUiBuilder`：从"改场景对象"改为"生成预制体"——
  - 复用现有构建代码（渐变条/描边贴图/字体/配色不变）；
  - 根用 `CreateRoot` 全屏拉伸；
  - 末尾 `FinishPrefab(root, "Assets/Game/UI/Dialogue/DialogueView.prefab", bindings, typeof(DialogueView))`（照 `BattleLobbyPrefabBuilder.FinishPrefab`）；
  - 菜单改名 `Tools/UI/重建对话界面预制体`，可重复执行。
- `GenshinDialogView.cs`：迁移完成后删除（逻辑已并入 `DialogueView`），避免两份实现漂移。

---

## 5. 关键坑（必须逐条处理）

| # | 坑 | 现象 | 处理 |
|---|---|---|---|
| 1 | 框架缓存实例 | 第二次对话带着上次的正文/选项/半透明面板 | `OnOpen` 里 `ResetUi()`：清正文、关选项、Kill Tween、alpha=0、重置锚点 |
| 2 | 关闭瞬间 Kill 补间 | 框架 `OnClose` 会 `UIAnim.Kill`；无 `UIViewAnim` 时立刻隐藏 | 淡出由视图自己播，播完再 `Close` |
| 3 | 全屏根挡住点击 | 根有 Canvas 但无 Image，本身不吃射线；子图形才吃 | 保持现状：对话条/选项 `raycastTarget=true`，其余装饰 `false` |
| 4 | 输入守卫 | 关闭后仍响应鼠标/空格推进 | `Update` 首行判 `vm != null && vm.CurrentNode != null`；`OnClose` 里 `Unbind` |
| 5 | 缩放语义差异 | `Match(0.5)` → `Shrink`，非 16:9 下底部条高度/选项位置可能偏 | 预制体按 1920×1080 摆；用 `Shrink` 在 16:9/21:9/4:3 三档截图复核（底部锚定天然安全，主要看选项右侧竖排） |
| 6 | 关闭后 UI 层残留 Canvas | `UIView.OnInit` 补的 Canvas 常驻 | 正常（框架面板都如此）；但 `overrideSorting` 由 `OnOpen` 设置，关闭后无需还原 |
| 7 | 存档恢复顺序 | 恢复后对话条不出现 / 第一句丢失 | 先 `Open`（await）→ 再 `PlayGraphFrom` |
| 8 | 事件重复订阅 | 框架实例复用后 `Bind` 被多次调用 | `Bind()` 内部先 `Unbind()`（现状已如此），`OnOpen` 只传一次 |
| 9 | 暂停中开面板 | 面板淡入卡在 0.07 alpha | 动效走视图自己的 DOTween（scaled）——对话开始时世界未暂停，无影响；且不引入 `UIViewAnim` |
| 10 | 场景里遗漏引用 | 删除画布后 `duihua` 还有其他脚本引用它 | 迁移前 grep `GenshinDialogCanvas` / `GenshinDialogView` 引用；目前仅 `DialogueUiBuilder` 与导演链路 |

---

## 6. 文件影响面

### 6.1 新增

| 路径 | 说明 |
|---|---|
| `Assets/Game/UI/Dialogue/DialogueView.prefab` | 对话面板预制体（由构建工具生成） |
| `Assets/Game/UI/Dialogue/Textures/*` | 渐变/描边/箭头贴图（沿用现有生成逻辑，若已存在则复用） |
| `Assets/Game/Scripts/Dialogue/DialogueView.cs` | `UIView` 实现（承接 `GenshinDialogView` 全部逻辑） |

### 6.2 修改

| 路径 | 改动 |
|---|---|
| `Assets/Game/Scripts/UI/Framework/UIViewBase/UIType.cs` | `DialogueView = 23`、`Max = 24` |
| `Assets/Game/UI/UIConfig.json` | 新增条目 |
| `Assets/Game/Demo/GenshinDialogDirector.cs` | 开/关面板、恢复路径先 Open、关闭延后到淡出结束 |
| `Assets/Game/Demo/Editor/DialogueUiBuilder.cs` | 改为生成预制体 + 写 `UIControlData` |
| `Assets/Game/Scenes/duihua.scene` | 删除 `GenshinDialogCanvas`（验收后） |

### 6.3 删除

| 路径 | 说明 |
|---|---|
| `Assets/Game/Demo/GenshinDialogView.cs` | 逻辑并入 `DialogueView` 后删除 |

### 6.4 不动

- `DialogueRunner` / `DialogueViewModel` / 对话图资产 / 存档系统（`SceneSaveService`、`DialogueSceneSaveController`）——迁移只改"UI 由谁加载"，不改对话与存档语义。
- `DialogueSceneMenu`（设置面板已走框架，行为不变）。

---

## 7. E2E 验收

| # | 用例 | 期望 |
|---|---|---|
| 1 | 进入 duihua → 按 F 触发对话 | 面板由框架打开（`UIManager.IsOpen(DialogueView)=true`），对话条淡入滑入正常 |
| 2 | 打字机 + 推进 | 与迁移前一致：打字中点击先补全，再点击进下一句 |
| 3 | 说话人切换 | 正反打同时切换镜头与名牌颜色，弹一下 |
| 4 | 选项节点 | 选项逐条右侧滑入、数字键/鼠标可选、选后关闭选项列表 |
| 5 | 对话结束 | 淡出后关闭面板（`IsOpen=false`），`InDialogue=false`，输入恢复 |
| 6 | 第二次触发对话 | **不带上一次的正文/选项/透明度**（缓存复用重置） |
| 7 | Esc 打开设置 | 设置面板在对话条之上（层级正确），`timeScale=0`，对话冻结 |
| 8 | 设置中「返回主页」 | 存档 Flush 后切 zhuye；对话面板随场景卸载（无残留） |
| 9 | 存档恢复（对话中退出） | 进场景后对话面板**先出现**，再恢复节点与正文 |
| 10 | 恢复后推进 | 正常进入下一句（ViewModel 绑定正确） |
| 11 | 关闭面板后点击 HUD | 不再被对话 UI 吃点击 |
| 12 | 分辨率 | 1920×1080 / 2560×1080(21:9) / 1440×1080(4:3) 三档截图：底部条贴底、选项不越界 |
| 13 | UI 相机 | 面板在 `SkierUICamera` 栈里可见（不是黑屏/不渲染） |
| 14 | 字体/中文 | NotoSansSC，无缺字/方块 |
| 15 | 静置 5 秒 | 无每帧告警（DOTween 空引用、Canvas 重复等） |

---

## 8. 风险与对策

| # | 风险 | 影响 | 对策 |
|---|---|---|---|
| R1 | 抽预制体后排版错位 | 视觉回归 | 先在旧画布旁生成新预制体做对照截图，确认后再删场景画布 |
| R2 | 框架画布 `Shrink` 与旧 `Match` 在非 16:9 差异 | 选项贴边/条高偏 | §7 #12 三档分辨率门禁；必要时把选项容器改成相对锚点而非固定 x |
| R3 | 缓存实例状态残留 | 第二次对话异常 | §5 #1 的 `ResetUi` + 用例 #6 |
| R4 | 关闭与淡出竞争 | 淡出被截断或面板卡住 | 淡出完成回调里 `Close`，并加超时兜底（0.4s） |
| R5 | 恢复路径漏开 UI | 恢复后无面板 | 用例 #9；`RestoreDialogue` 内 `await Open` 后再 `PlayGraphFrom` |
| R6 | 两份视图实现漂移 | 改了一处没改另一处 | 迁移完成后删除 `GenshinDialogView.cs` |
| R7 | 删除场景画布导致别的引用断 | 场景缺脚本 | 迁移前 grep 引用清单；删除后 `refresh` 确认 0 error |

---

## 9. 实施步骤

| 阶段 | 内容 | 产出 | 预估 |
|---|---|---|---|
| P0 | `DialogueView` 类 + `UIType`/`UIConfig` | 可编译、可在检查器看到条目 | 1h |
| P1 | 构建工具改为生成预制体（含 UIControlData 绑定） | `DialogueView.prefab` 可独立预览 | 3h |
| P2 | 导演改为 Open/Close 驱动（含恢复路径、淡出后关闭） | 主流程可玩 | 2h |
| P3 | 分辨率/排版复核 + 状态重置 | 三档截图通过 | 2h |
| P4 | 删除场景画布与旧脚本、E2E（§7 15 项）、回填文档 | 验收记录 | 3h |

合计约 1 人天。

---

## 10. 回滚方案

- 迁移前先提交一次（旧画布 + 旧脚本在 git 里可回退）。
- 验收未通过时：`git checkout` 恢复 `duihua.scene` 与 `GenshinDialogView.cs`，删除 UIConfig 条目与 `DialogueView` 预制体即可回到现状；存档系统不受影响（它只依赖 `DialogueRunner`）。

---

## 11. 实施记录与实测结论

实施日期：2026-10-25（P0–P4 全部落地，E2E 通过）。

### 11.1 实际落地

| 层 | 文件/改动 |
|---|---|
| 枚举/配置 | `UIType.DialogueView = 23`（Max=24）、`UIConfig.json` 条目（`NormalLayer`、`isWindow=false`、路径 `Assets/Game/UI/Dialogue/DialogueView.prefab`） |
| 视图 | `Assets/Game/Scripts/Dialogue/DialogueView.cs`（`UIView + IView`，承接旧 `GenshinDialogView` 全部逻辑；`OnOpen` 重置 + `Bind(runner.ViewModel)`；`OnClose/OnRelease` 解绑并 Kill 补间） |
| 预制体 | `Assets/Game/UI/Dialogue/DialogueView.prefab`（全屏透明根 + 底部 1920×300 对话条 + 右侧竖排选项；根无 Canvas/CanvasScaler，由 `UIView.OnInit` 补；`UIControlData` 7 条绑定齐全） |
| 构建工具 | `DialogueUiBuilder` 改为生成预制体并写 `UIControlData`（重跑不会动场景） |
| 导演 | `TryStartDialogue` / `RestoreDialogue` 都先 `UIManager.Open(UIType.DialogueView, runner)` 再驱动节点；`DialogEnded` 后等 0.3s 淡出再 `Close` |
| 场景 | `duihua.scene` 删除旧 `GenshinDialogCanvas` 整棵子树（-2800 行） |
| 清理 | 删除 `GenshinDialogView.cs/.meta`（逻辑已并入，避免两份实现漂移） |

### 11.2 E2E（Play，1920×1080）

| # | 用例 | 实测 |
|---|---|---|
| 1 | 进入 duihua → 触发对话 | `IsOpen(DialogueView)=true`、`InDialogue=true`、视图已绑定 |
| 2 | 打字机 | 1.2s 后 `CurrentShownText == node.DialogText`（`match=true`） |
| 3 | 选项节点 | `optionRoot=true`、3 个按钮激活、可用选项 3 |
| 4 | 选择选项 | 跳到目标节点（10→11），`optionRoot` 自动关闭 |
| 5 | 对话结束 | `CurrentNode=null`、`InDialogue=false`、淡出后 `IsOpen=false`（真的由框架关闭） |
| 6 | 第二次对话 | 重新打开后 `bound=true`、`optionRoot=false`、正文不带上次残留（`staleText=false`） |
| 7 | Esc 设置 | `IsOpen(SettingsView)=true`、`timeScale=0`、对话面板仍在、当前节点不变；再按 Esc 恢复 `timeScale=1` |
| 8 | 存档恢复 | 存档中的节点 1 → 重进后 `IsOpen=true`、`InDialogue=true`、`nodeMatch=true`、`textMatch=true`（先开 UI 再跳节点的顺序验证成功） |

### 11.3 三档分辨率（数据断言，非目测）

| 分辨率 | 对话条屏幕矩形 | 选项矩形（index 0..2） | Canvas scale |
|---|---|---|---|
| 1920×1080 | (0,0)-(1920,300) | x 1040..1800 / y 340..594 | 1.00 |
| 2560×1080 (21:9) | (0,0)-(2560,400) | x 1387..2400 / y 453..792 | 1.33 |
| 1440×1080 (4:3) | (0,0)-(1440,300) | x 560..1320 / y 340..594 | 1.00 |

三档均为「底边贴屏底、横向满宽、选项全部在屏幕内」，无越界/裁切。

### 11.4 视觉复核与一处修整

- 多模态模型多次判定「没有底部渐变条」，但像素级验证相反：
  - UI 相机直渲不可用（Overlay 相机单独渲染全透明）；
  - 用 `sprite=null + 品红` 验证：底部 0..45 行（180 行采样）整行命中，确认**几何与绘制链路正常**；
  - 用运行时生成的「底部不透明→顶部透明」探针贴图验证 UV 映射：亮行集中在底部，**映射方向正常**（贴图底部 → 屏幕底部）；
  - 最终录像逐行亮度：`y0=0.209 → y16=0.341 → y32=0.551 → y48=0.639`，即底部明显被压暗、向上渐隐，渐变条确实存在。
- 结论：VLM 对「近黑渐变叠在暗场景上」的判断不可靠，本项以像素数据为准。
- 顺手把 `dialog_gradient.png` 的 alpha 曲线调强（底部 0.94，0.55 高度处已 0.75，顶部 0），让正文/名牌所在的下半段有更实的压暗，改善对比度；朝向保持原样（未翻转）。

### 11.5 未做/遗留

- 未引入 `UIViewAnim`（有意）：淡入滑入仍由视图自己的 DOTween 驱动，避免暂停时卡在动画首帧。
- 场景卸载时仍会出现 DOTween 对已销毁 `CanvasGroup/RectTransform` 的告警（既有表现，与本次迁移无关）。
- 未做逐帧视觉门禁的 15 项全量截图，改用「数据断言 + 两段录屏 + 像素亮度剖面」替代；如需美术级验收，可再按 §7 #12 出三档截图。

