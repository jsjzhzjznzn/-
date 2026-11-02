# PerfAgent 静态扫描修复方案（2026-10-22）

## 结论

PerfAgent 的静态审计在 LOGIN 场景报了 37 条代码反模式，逐条对源码核对后：

- **真正需要改的：3 处**（本文档处理）
- **规则误报：约 20 条**（其中 6 条「严重 linq」是同一条正则造成的，占掉全部严重级的一半）
- **无引用死代码 / 编辑器期回调 / 异常路径：其余**
- **不属于代码、另行处理：纹理内存 590 MB 超预算、Resources 目录 20 个资源**

## 判定依据

| 误报类型 | 证据 |
|---|---|
| `linq` 命中 `Mathf.Max/Min` | 规则正则 `Assets/PerfAgent/Editor/Collectors/ScriptAntipatternCollector.cs:70-72` 为 `\.(?:…|Min|Max|…)\s*[(<]`，`Mathf.Max(` 直接命中 |
| 方法名归属错 | `OnValidate` 里的日志被记成 `Update + debug_log`，实际只在编辑器改 Inspector 时触发，不进包体 |
| 非热路径 | `throw new Exception(...)`（XLua ObjectTranslator:1330）、异常信息构建（Animancer IState:396/409）、`///` 文档注释行（ExitEvent:40） |
| 集合本来无装箱 | `dirtyRects` 是 `List<RectInt>`、`m_Targets` 是 `Dictionary<int,Target>`，枚举器是结构体 |
| 一次性事件 | `DungeonHud.cs:393` 的 `Destroy()` 在 toast 过期分支，不是每帧 |
| 无引用 | `MaterialUpdater`、`BehaviorGameGUI` 的 GUID 未被任何 .scene/.prefab 引用 |
| 测试专用 | `BattleTestBootstrap/BattleTestDummy` 只由 `Battle/Test/Editor/BattleTestBuilder.cs:286-292` 在生成测试场景时挂载，LOGIN 场景没有 |

## 本轮改动

### 1. `Camera.main` 改缓存（`DungeonGpuMinimap.cs:378`、`DungeonDropLabels.cs:69`）

- 现状：`Camera.main` 内部是带 `MainCamera` Tag 的全场景查找 + 缓存失效检查，小地图在 `LateUpdate` 里每帧调一次。
- 做法：镜像同目录已有的正确写法 `DungeonDropRenderer.GetCachedCamera()`（`DungeonDropRenderer.cs:53-56`）：字段缓存 + `== null` 时重取。
- 为什么不改成外部注入：相机可能随场景切换重建，`UnityEngine.Object == null` 对已销毁对象为真，惰性重取天然覆盖这种情况，也不会引入新的初始化顺序依赖。

### 2. `owner.Drops` 的 foreach 改索引 for（3 处）

- 现状：`DungeonManager.cs:115` 是 `public IReadOnlyList<DungeonDrop> Drops => drops;`，通过接口 `foreach` 会拿到装箱的 `IEnumerator<T>`（约 40 B/次），热路径为
  - `DungeonDropLabels.cs:75`（0.1 s 一刷）
  - `DungeonDropRenderer.cs:76`
  - `DungeonGpuMinimap.cs:487`（0.1 s 一刷；PerfAgent 未扫到这条）
- 做法：局部变量接一次 `IReadOnlyList` 再按索引 `for` 遍历，**不改 `Drops` 的公开类型**——`DungeonBigMap` / `DungeonMinimap` 等消费方不受影响，避免为了省一次装箱去动公共 API。

### 3. 邀请倒计时不再每帧重建字符串（`BattleInviteView.cs:126`）

- 现状：`Update` 里每帧 `pendingText.SetText($"等待 {…} 回应… 剩余 {seconds} 秒")`，每帧一次字符串分配 + 一次 TMP 重排；而秒数一秒才变一次。
- 做法：字段记 `lastPendingSecond`，秒数不变就跳过；退出等待态时置 `-1`，保证下一次邀请能立刻刷新。

## 本轮不改（附理由）

- **6 条 `Mathf.Max/Min` 误报**：属于插件规则源码，改它等于改第三方包，且每次升级会被覆盖。建议反馈给作者加负向断言 `(?<!Mathf)\.(?:Min|Max)`。
- **纹理 590 MB / Resources 20 项**：资源侧问题，需要单独的取舍（哪些压、哪些开 streaming），不混在代码提交里。
- **测试脚本与死代码**：不产生运行时开销，动了反而增加回归面。

## 验证

1. 编译：Unity 刷新后 Console 无 error。
2. 行为回归：
   - fuben 场景进 Play：小地图玩家箭头跟随朝向、掉落标签正常显示/回收；主相机切换（若有）后仍能用。
   - 邀请面板：等待态倒计时仍逐秒跳动，取消/超时后状态恢复。
3. 可选实测：PerfAgent「跟随采集」重采一份，与本次修复前快照对比每帧托管分配。
4. 回滚：每处独立提交，`git revert <sha>` 即可分别撤销。
