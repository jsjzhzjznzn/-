# 移植说明（Unity 2022.3 / 团结引擎 1.9.3）

本仓库中的 `com.ethan.act-action-editor` 原版面向 **Unity 6（最低 6000.0，验证基线 6000.3.6f1）**，
本目录是把它落到 **Unity 2022.3.62t11（团结引擎 1.9.3）** 工程里的本地适配副本。

## 改了什么

| 项 | 原版 | 本副本 |
| --- | --- | --- |
| `package.json` → `unity` | `6000.0` | `2022.3` |
| `package.json` → `description` | Unity 6 表述 | 标注为移植版 |
| `.github/`、`Releases~/` | 保留 | 未拷贝（与运行时无关） |

代码、资源、`.meta` GUID 均未改动。原版 Editor/Runtime 代码没有使用 Unity 6 专属 API
（无 `Awaitable`、`FindFirstObjectByType`、`EntityId`、RenderGraph、UI Toolkit 等），
也没有任何 `#if UNITY_6000_0_OR_NEWER` 版本分支，因此清单放开后可直接编译。

## 已验证

- 编辑器导入 + 脚本编译：`Ethan.ActionEditor.Runtime`、`Ethan.ActionEditor.Editor` 及两个测试程序集全部生成，
  仅有 2 条 `CS0414` 未使用字段警告（原版自带）。
- 窗口：`Tools > ACT Action Editor > Open Editor` 正常打开，时间轴、轨道分组（Presentation / Mechanics）、
  工具栏、`Action properties`、`Validation` 面板均渲染正常，Console 无异常。
- 首次打开自动创建的默认资产：`Assets/ActionEditor/Skills/NewAction.asset`。

## 已知限制

- **URP 集成未启用**：`Integrations/URP` 的 versionDefine 要求 URP `17.0.0`（Unity 6 配套），
  本工程是 URP `14.2.0-t1`，因此该程序集被自动排除（不报错，但没有 URP 专属能力）。
- **AI Navigation 集成未启用**：要求 `com.unity.ai.navigation >= 2.0.0`，本工程是 `1.1.6`，同样被排除。
- 动画预览、Time 轴手感、资产序列化在 2022.3 上的行为**未逐项回归**，只做了编译与窗口冒烟验证。
- 这是社区移植，不受上游支持；上游明确声明 Unity 2022 未验证。

## 运行时注意事项

- `Packages/` 下是嵌入式（embedded）包，`Packages/packages-lock.json` 会记录 `file:com.ethan.act-action-editor`，
  两者都要入库，否则克隆工程后包会缺失。
- 本机 Codely 桥（1.0.87）把心跳写到 `Temp/.com-unity-codely.json`，而 DSH 的 tuanjie 插件从工程根目录读取；
  工程根目录的 `.com-unity-codely.json` 是本机调试用的兼容副本，已加入 `.gitignore`，不要提交。

## 回滚

```bash
git checkout main          # 回到移植前的基线分支
# 或
git revert <port-commit>   # 只撤掉移植提交
```
