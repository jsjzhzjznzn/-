# 项目协作约定（RedDotSystem-main）

## 引擎与工程

- **整个项目根目录 = `D:\tuanjieproject\frined`**，当前工作区 `RedDotSystem-main` 只是客户端。
  同级的 `competeserves`（服务端）、`AgentServer`（守岸人 Node/TS）、`TestClient`、`protobuf`、`Config`
  都是本项目代码；改服务端 / 协议 / 联调工具时直接改这些目录，不要另找仓库。
- 团结引擎 1.9.3（= Unity 2022.3.62t11），URP 14.2.0-t1，AI Navigation 1.1.6。
- 已嵌入式安装 ACT 动作编辑器 `Packages/com.ethan.act-action-editor`（从 YCombatEditor 移植，适配 2022.3）。

## 版本控制（重要）

- 需要还原点时，先在 `main` 上提交基线。
- **改完直接在 `main` 上提交并合并，不要再询问是否提交 / 是否合并。**
  需要试验性改动时可以开分支，但完成后自动快进合并回 `main`，并删除临时分支。
- **不要自动 `git push`**，推送远端由用户自己决定。
- 不要提交 `Library/`、`Temp/`、`Logs/`、`Preview/`（用户本地截图目录）、根目录 `.com-unity-codely.json`；
  `.gitignore` 已覆盖这些，新增忽略规则时保持 ASCII 注释（该文件是 GBK 编码）。
- `Packages/com.ethan.act-action-editor/` 与 `Packages/packages-lock.json` **必须入库**：嵌入式包依赖 lock 里的 `file:` 记录，缺了克隆后无法还原。

## 编辑器操作

- 编辑器已经打开时**不要**用 batchmode 启动第二个实例（会报 `HandleProjectAlreadyOpenInAnotherInstance` 并崩溃）；优先用 `tuanjie_*` 桥命令。
- 桥心跳兼容：Codely 桥 1.0.87 把心跳写到 `Temp/.com-unity-codely.json`，DSH 的 tuanjie 插件从**工程根目录**读取；
  根目录那份 `.com-unity-codely.json` 是本机兼容副本，已加入 `.gitignore`，不要提交。
- 改完 `Packages/` 下嵌入式包的脚本后，触发 `AssetDatabase.Refresh`（或让编辑器窗口获得焦点）以重新编译。
- 批量模式日志：`Logs/port-compile.log`；编辑器日志：`%LOCALAPPDATA%\Tuanjie\Editor\Editor.log`。

## Codely 技能接入 DSH

- `.dsh/skills/` 已经接进 DSH 两组技能，它们会自动出现在会话技能目录里，按名字用 `skill` 工具加载：
  - Codely 的项目技能：`.codely-cli/extensions/TJGenerators/skills`、`TMPChineseFont`；
  - 本工程自有技能：`.codebuddy/skills/` 里的 `skier-ui-create` / `skier-ui-edit` / `skier-ui-yooasset`
    以及测试规范 `test-from-real-login`。
- 这些 SKILL.md 是照着 Codely / Unity MCP 的工具名写的，在 DSH 里要按下面的映射换成真实工具：
  - `activate_skill("x")` → `skill` 工具，`name = "x"`
  - `execute_custom_tool("gen_xxx", {...})` → `tuanjie_command`，`command = "custom.gen_xxx"`（工程里已注册 74 个 `custom.*`）
  - `unity_editor` / `unity_package` / `unity_refresh` → `tuanjie_command` 的 `manage_editor` / `manage_package` / `manage_editor(refresh)`
  - `task(subagent_name="x")` → `subagent` 工具
  - Codely 的 `<bg_task_done>` 通知 DSH 没有：生成类任务在提交后用对应的 `custom.query_*_status` 查一次即可，**不要轮询**
- UI 三件套里的 Unity MCP 工具名同样要换（已实测可用）：
  - `manage_prefabs get_info(path)` → `tuanjie_command manage_asset get_info`（返回 `guid` + `components`，够核对登记）
  - `manage_prefabs open_prefab_stage` / `save_prefab_stage` / `close_prefab_stage` → DSH 没有预制体阶段命令，
    用 `tuanjie_eval` 跑 `PrefabUtility.LoadPrefabContents` / `SaveAsPrefabAsset`（skill 里的「方式 B」可原样执行），
    走这条路务必像 skill 里那样手动调 `CorrectComponents()` / `CheckSubUIs()`；要在界面上看效果就用
    `execute_menu_item` 打开 `Tools/UI管理`
  - `manage_gameobject create` / `manage_components add` → `tuanjie_command manage_gameobject` 的 `create` / `add_component`
  - `refresh_unity` → `manage_editor` 的 `refresh`（有 C# 改动时 `request_compile` + `wait_for_compile`）
  - `read_console` → `tuanjie_command read_console get`
  - `execute_code` → `tuanjie_eval`
- TJGenerators 的两个技能（`generate_3d_model`、`generate_animated_character`）frontmatter 里 description 含裸冒号
  （`scratch: ...`）会让严格 YAML 解析失败，已在 `.dsh/skills/` 内用折叠块标量（`>-`）重写为副本；
  `TMPChineseFont` 因为不是 kebab-case，重命名为 `tmp-chinese-font`。三个 `skier-ui-*` 的 frontmatter 本身合规
  （kebab-case + 全角冒号），直接建 junction，不改写内容。
- `.dsh/skills/` 不入库（来源是被忽略的 `.codely-cli/`）；需要重建时运行
  `powershell -NoProfile -ExecutionPolicy Bypass -File .dsh/relink-skills.ps1`（脚本可重复执行，已存在的会跳过）。

## 测试与联调

- 任何功能验证都必须从 `LOGIN` 场景进入 Play，用真实账号走登录界面登录（默认 `test` / `123456`，PlayerId=1），
  连本机 8888 服务端；看到 `LoginResponse` 成功、`MyPlayerId` 正确、场景切到 `zhuye` 后才算登录成功。
- 不允许直接播 `zhuye` / 业务场景、伪造登录回执、mock 网络，或直接调 `SendLogin` 绕过登录界面。
  完整步骤、判定与失败处理见技能 `test-from-real-login`。

## 报告习惯

- 默认用中文回答。
- 只报告真正跑过的验证（编译 / 窗口打开 / Play Mode / Player 构建），不要把「编译通过」说成「功能验证通过」。
