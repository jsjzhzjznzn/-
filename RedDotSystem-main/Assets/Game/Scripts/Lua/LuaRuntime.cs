using UnityEngine;

/// <summary>
/// 场景级 Lua 入口：挂到任意 GameObject 上，Start 时 require 指定模块
///
/// 用途：给某个场景挂一段 Lua 逻辑（例如 zhuye 里挂一个 LuaRuntime 指向 "Lua.main"）。
/// 不挂也不影响 —— LuaEnvManager 会在业务代码首次访问时自动创建 LuaEnv。
/// 本组件没有改动任何已有场景，是否使用由业务决定。
/// </summary>
public class LuaRuntime : MonoBehaviour
{
    /// <summary>Lua 模块名（require 的名字）。脚本放在 Assets/Game/Resources/Lua 下</summary>
    [Tooltip("Lua 模块名（require 的名字），脚本放 Assets/Game/Resources/Lua 下，如 Lua.main")]
    public string entry = "Lua.main";

    /// <summary>Start 时自动执行入口脚本</summary>
    [Tooltip("Start 时自动执行入口脚本")]
    public bool runOnStart = true;

    private void Start()
    {
        if (runOnStart) Run();
    }

    /// <summary>执行入口模块（外部/测试也可直接调用）</summary>
    public object Run()
    {
        if (string.IsNullOrEmpty(entry)) return null;
        if (LuaEnvManager.Instance == null)
        {
            Debug.LogError("[LuaRuntime] LuaEnvManager 尚未自举，无法执行 " + entry);
            return null;
        }

        object result = LuaEnvManager.Instance.Require(entry);
        Debug.Log("[LuaRuntime] require '" + entry + "' 完成");
        return result;
    }
}
