---
name: test-from-real-login
description: >-
  本工程任何功能测试、联调、验收都必须从 LOGIN 场景进入 Play，用真实账号在登录界面完成真实登录
  （默认 test/123456，PlayerId=1）连接本机 8888 服务端；确认 LoginResponse 成功、MyPlayerId 正确、
  场景切到 zhuye 后才允许开始验证。禁止直接播 zhuye、伪造登录、mock 网络或用演示脚本绕过登录界面。
  Use whenever testing, verifying, or debugging gameplay features end-to-end in this project.
---

# 从 LOGIN 真实登录再测试

## 何时必须用

- 任何 Play Mode 功能验证、UI 验收、联调、回归、截图取证。
- 只要结论涉及「这个功能能不能用」，就必须先完成本流程。
- 纯 Editor 静态检查（编译、资源引用）不需要登录；但不得用它冒充功能验证。

## 铁律

1. **入口**：从 `Assets/Game/Scenes/LOGIN.scene` 进入 Play，不允许在 `zhuye` 或业务场景直接 Play。
2. **真实账号**：通过登录界面的账号/密码输入框，走 `LoginSceneController.OnLoginClicked` →
   TCP `127.0.0.1:8888` → 服务端 `accounts` 表校验的完整链路。
3. **唯一成功判据**（全部满足）：
   - `ClientNetworkManager.Instance.MyPlayerId` 等于预期账号的 PlayerId；
   - `SceneManager.GetActiveScene().name == "zhuye"`；
   - `UIManager.Instance.IsOpen(UIType.LoginView) == false`；
   - 登录过程中状态文本出现过「登录成功！玩家ID:N」。
4. **失败就停**：连不上、账号占用、DB 错误都如实报告，不能用 mock / 假回执 / 改 `MyPlayerId` 继续。
5. **单账号单会话**：同一账号不能同时被 TestClient、第二个 Play 实例或另一台机器占用，否则会被拒绝或顶号。
6. **报告口径**：先写「从 LOGIN 用账号 X 登录，PlayerId=N」，再写验证了什么；没走登录就不许说「功能验证通过」。

## 账号

| 用途 | 账号 | 密码 | PlayerId |
| --- | --- | --- | --- |
| 默认联调号 | `test` | `123456` | 1 |
| 专用 E2E 号 | 见 `Docs/Documents/成就系统设计方案.md` 等文档登记（如 `ach_e2e_1026`=57、`gacha_v2_a`=55、`signe2e1`=49） | `123456` | 文档为准 |

- 优先 `test`；**用户指定了账号时以用户给的为准**（密码也从用户处获取），不要擅自换回 `test`。
- 不要随手注册垃圾账号污染 `accounts` 表；确有需要时用 `e2e_<用途>_<MMdd>`，并在报告里写明。
- 忘记密码 / 账号不存在时问用户，不要猜。

## 标准流程（2026-10-27 实测）

### 0. 预检

```text
tuanjie_status
```

- 编辑器必须在线；`playMode == playing` 时先 `manage_editor stop`。
- 服务端端口：

```powershell
Test-NetConnection 127.0.0.1 -Port 8888 -InformationLevel Quiet
```

- `False`：进入 Play 时 `CompeteServerAutoStart` 会自动拉起 `../competeserves/run_server.bat`
  （`dotnet run` 首次编译要几秒到几十秒）；也可以提前手动运行该 bat。
  服务端没就绪时点登录会提示连接失败，等端口起来再点一次即可（仍是真实登录）。
- 登录依赖 MySQL 的 `accounts` 表。服务端在但登录报 DB 错误时，看 `../competeserves/server_err.log`
  和弹出的服务端窗口；MySQL 没起就停下来报告，不要改代码绕过。

### 1. 本次强制走账号密码（可选但推荐）

自动登录令牌会绕过输入框。进入 Play 前把开关关掉：

```csharp
int old = PlayerPrefs.GetInt("Login_AutoLogin", 0);
PlayerPrefs.SetInt("Login_AutoLogin", 0);
PlayerPrefs.Save();
return "autoLogin_was=" + old;
```

记下 `old`，测完可用 `PlayerPrefs.SetInt("Login_AutoLogin", old)` 恢复（可选）。
专门测试「自动登录」功能时跳过本步。

### 2. 打开 LOGIN 场景（编辑模式）

```text
manage_scene load --path Assets/Game/Scenes/LOGIN.scene
manage_editor get_state
```

- 当前场景有未保存改动时 `load` 会报错；先让用户决定保存 / 放弃，不要盲目保存。
- 确认 `activeScenePath == Assets/Game/Scenes/LOGIN.scene`。

### 3. 进入 Play

```text
manage_editor play
```

### 4. 等登录面板就绪

登录面板由 UI 框架从 YooAsset `ui` 包异步加载，通常 1–3 秒。轮询（每次间隔 1–2 秒，最多约 20 秒）：

```csharp
var c = UnityEngine.Object.FindObjectOfType<LoginSceneController>();
var ui = SkierFramework.UIManager.Instance;
bool open = ui != null && ui.IsOpen(SkierFramework.UIType.LoginView);
string status = c != null && c.statusText != null ? c.statusText.text : "";
return "scene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name
     + " loginOpen=" + open
     + " controller=" + (c != null)
     + " status=" + status;
```

等到 `scene=LOGIN loginOpen=True controller=True`。

- 如果直接看到 `scene=zhuye playerId>0`：说明自动登录令牌生效了。这仍是真实账号会话，但没走输入框；
  要么在报告里注明「令牌自动登录」，要么按第 1 步重来。
- 20 秒还没打开：`read_console get` 看 `[LoginSceneBootstrap]` / YooAsset 报错，停下来报告；
  不要手动实例化 `LoginPanel.prefab` 冒充。

### 5. 计算输入框 / 按钮的屏幕坐标

登录 Canvas 是 **ScreenSpaceCamera**，直接 `GetWorldCorners` 得到的是世界坐标（会看到负数），
必须用 `RectTransformUtility.WorldToScreenPoint`：

```csharp
var c = UnityEngine.Object.FindObjectOfType<LoginSceneController>();
var canvas = c.usernameInput.GetComponentInParent<UnityEngine.Canvas>();
System.Func<UnityEngine.RectTransform, UnityEngine.Vector2> center = rt =>
{
    var cs = new UnityEngine.Vector3[4];
    rt.GetWorldCorners(cs);
    return UnityEngine.RectTransformUtility.WorldToScreenPoint(canvas.worldCamera, (cs[0] + cs[2]) / 2f);
};
var a = center(c.usernameInput.GetComponent<UnityEngine.RectTransform>());
var p = center(c.passwordInput.GetComponent<UnityEngine.RectTransform>());
var b = center(c.loginButton.GetComponent<UnityEngine.RectTransform>());
return "account=" + (int)a.x + "," + (int)a.y
     + " password=" + (int)p.x + "," + (int)p.y
     + " button=" + (int)b.x + "," + (int)b.y;
```

坐标是游戏画面的屏幕坐标（左下角原点），会随 Game 视图分辨率变化，**不要照抄上一次的数字**。
1920×1080 时约是 `account=540,630 password=540,514 button=540,366`，仅用于对照。

### 6. 清空并输入账号密码

先清空，避免「记住账号」预填导致追加：

```csharp
var c = UnityEngine.Object.FindObjectOfType<LoginSceneController>();
c.usernameInput.text = "";
c.passwordInput.text = "";
return "cleared";
```

然后点输入框 + 真实键入：

```text
manage_input mouse_click --x <accountX> --y <accountY>
manage_input type_text --text test
manage_input mouse_click --x <passwordX> --y <passwordY>
manage_input type_text --text 123456
```

核对（密码只看长度）：

```csharp
var c = UnityEngine.Object.FindObjectOfType<LoginSceneController>();
return "account='" + c.usernameInput.text + "' pwdLen=" + c.passwordInput.text.Length
     + " selected=" + UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject?.name;
```

`type_text` 报 “needs a focused … field” 时，说明点击没选中；fallback：

```csharp
UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(
    UnityEngine.Object.FindObjectOfType<LoginSceneController>().usernameInput.gameObject);
return "selected";
```

仍不生效就停下来报告，**不要**改用 `SendLogin` 直接发协议。

### 7. 点登录并等待回执

```text
manage_input mouse_click --x <buttonX> --y <buttonY>
```

然后轮询（1–2 秒一次，最多 20–30 秒）：

```csharp
var net = ClientNetworkManager.Instance;
var ui = SkierFramework.UIManager.Instance;
var c = UnityEngine.Object.FindObjectOfType<LoginSceneController>();
string status = c != null && c.statusText != null ? c.statusText.text : "";
return "scene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name
     + " connected=" + (net != null && net.IsConnected)
     + " playerId=" + (net != null ? net.MyPlayerId : 0)
     + " loginOpen=" + (ui != null && ui.IsOpen(SkierFramework.UIType.LoginView))
     + " status=" + status;
```

成功：`scene=zhuye connected=True playerId=1 loginOpen=False`。
登录过程中 `status` 会依次出现「连接服务器中... / 登录中... / 登录成功！玩家ID:1」。

- 失败时 `status` 显示「登录失败：原因」，把原文写进报告。
- 切到 `zhuye` 后 `LoginSceneController` 已销毁，`status` 变空是正常的，以 `playerId` + `scene` 为准。
- 按钮点击无效时 fallback（仍走真实按钮逻辑，需在报告里注明）：

```csharp
UnityEngine.Object.FindObjectOfType<LoginSceneController>().loginButton.onClick.Invoke();
return "clicked";
```

### 8. 这时才开始测功能

- 只有第 7 步成功后才能验证业务：UI、红点、背包、邮件、聊天、成就、活动、战斗入口等。
- 涉及另一个账号的用例，按同样流程退出登录或换账号重登，确保缓存清场后观察「防串号」。
- 需要第二端（好友申请、对战邀请等）时用 TestClient 或第二个 Play 实例，
  但**不要**和第二端同时占用同一个账号。

## 明确禁止（会得到假结论）

- 直接播放 `Assets/Game/Scenes/zhuye.scene` 或任何业务场景，然后说「功能正常」。
- 在 `tuanjie_eval` 里调 `ClientNetworkManager.SendLogin` / `SendTokenLogin` 代替登录界面。
- 伪造 `LoginResponse`、替换 / mock `ClientNetworkManager`、直接给 `MyPlayerId` 赋值。
- 改 `PlayerPrefs` 里的令牌、勾选自动登录来「跳过登录」。
- 复用上一次 Play 的登录态（Play 停止后会话就没了）。
- 只截一张 `zhuye` 截图就宣称登录 / 功能验证通过。
- 账号被占用时反复重试顶号（会造成两端互踢）。

## 常见失败与处理

| 现象 | 原因 | 处理 |
| --- | --- | --- |
| 等不到 `loginOpen=True` | YooAsset `ui` 包 / UIConfig 异常，或 UIManager 未初始化 | `read_console get` 看报错；报告，不手动实例化面板 |
| `status=连接服务器中...` 卡住 / 连接失败 | 8888 未监听、服务端还在 `dotnet run` 编译 | 等端口起来再点一次登录；仍失败看 `../competeserves/server_err.log` |
| `登录失败：账号或密码错误` | 账号不存在 / 密码不对 | 核对账号；不要穷举密码；问用户 |
| `登录失败：该账号已经在线` 或收到 Kickout | TestClient / 另一 Play / 另一台机器占着账号 | 关掉另一端，等几秒重登；不要循环互踢 |
| 登录报数据库错误 | MySQL / Mongo 未启动 | 启动对应服务或报告，不要改代码绕过 |
| 坐标是负数 / 0 | 忘了 `WorldToScreenPoint`，或 Game 视图没渲染 | 按第 5 步重算；必要时 `manage_window_bridge focus_window` |
| `type_text` 提示没有聚焦输入框 | 点击没落到输入框（遮挡 / 坐标错） | 重新点；或用 `SetSelectedGameObject` fallback |
| 首次 `tuanjie_eval` 很慢（约 25 秒） | 脚本程序集首次编译 | 等返回，不要连续重发 |

## 报告模板

```text
登录：LOGIN → 账号 test → LoginResponse 成功 → PlayerId=1 → zhuye（loginOpen=false）
验证：<具体功能 / 用例>，通过 / 失败
证据：<截图路径 / console 关键行 / 服务端日志>
未覆盖：<没测到的分支>
```

没有完成「LOGIN + 真实账号 + playerId 正确」这三项，就不要在报告里写「已验证」。
