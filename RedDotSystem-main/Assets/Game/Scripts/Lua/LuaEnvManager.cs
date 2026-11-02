using System;

using UnityEngine;

using XLua;

/// <summary>
/// xLua 运行时入口（单例，DontDestroyOnLoad + 懒初始化）
///
/// 职责：
///   1. 持有全进程唯一的 LuaEnv —— xLua 的 Lua 虚拟机同一时刻只允许存在一个实例；
///   2. 提供 DoString / Require / 全局表等常用入口，业务层不直接 new LuaEnv；
///   3. 按官方建议周期性调用 LuaEnv.Tick()（GC 步进 + UnityEngine.Object 失效回收）。
///
/// 脚本加载规则：默认完全沿用 xLua 内置规则
///   require "Lua.main"  →  Resources/Lua/main.lua（即 Assets/Game/Resources/Lua/main.lua.txt）
/// 将来接入 AssetBundle / StreamingAssets 热更时，给 Loader 赋一个委托即可整体替换，
/// 业务代码（require 的名字）不用改。
///
/// 注意：LuaEnv 是懒创建的（第一次访问 Env 才真正 new），不使用 Lua 的场景零开销。
/// </summary>
public class LuaEnvManager : MonoBehaviour
{
    // ──────────────────────────────
    // 单例实现
    // ──────────────────────────────

    /// <summary>全局唯一实例（业务代码访问 Lua 的入口）</summary>
    public static LuaEnvManager Instance { get; private set; }

    /// <summary>
    /// 运行时自举（与 BagManager / ChatManager 相同的模式）
    /// 只创建管理器本体，LuaEnv 在首次访问 Env 时才创建
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance != null) return;
        GameObject holder = new GameObject("LuaEnvManager");
        holder.AddComponent<LuaEnvManager>();
    }

    // ──────────────────────────────
    // 配置
    // ──────────────────────────────

    /// <summary>
    /// 自定义脚本加载器（参数是 require 的模块名，如 "Lua.main"）
    /// 返回 null 表示"本加载器没有这个模块"，会自动回退到 xLua 内置的 Resources 规则
    /// 必须在 LuaEnv 创建前（即首次访问 Env 前）赋值才生效
    /// </summary>
    public Func<string, byte[]> Loader;

    /// <summary>Tick 间隔（秒）。xLua 官方建议 1~2 秒一次，不要每帧调用</summary>
    public float tickInterval = 1f;

    // ──────────────────────────────
    // 私有字段
    // ──────────────────────────────

    /// <summary>底层 Lua 虚拟机（null 表示尚未创建）</summary>
    private LuaEnv env;

    /// <summary>下一次 Tick 的时间点（Time.unscaledTime）</summary>
    private float nextTickTime;

    // ──────────────────────────────
    // 对外属性
    // ──────────────────────────────

    /// <summary>底层 LuaEnv（懒创建；业务代码通常用 Require/DoString 就够了）</summary>
    public LuaEnv Env
    {
        get
        {
            if (env == null)
            {
                env = new LuaEnv();
                if (Loader != null) env.AddLoader(CustomLoader);
                Debug.Log("[LuaEnvManager] LuaEnv 创建完成");
            }
            return env;
        }
    }

    /// <summary>Lua 全局表（_G）</summary>
    public LuaTable Global => Env.Global;

    /// <summary>LuaEnv 是否已创建（不使用 Lua 时为 false）</summary>
    public bool IsInitialized => env != null;

    // ──────────────────────────────
    // 常用入口
    // ──────────────────────────────

    /// <summary>执行一段 Lua 代码，返回 Lua 侧返回值</summary>
    public object[] DoString(string chunk, string chunkName = "chunk")
    {
        return Env.DoString(chunk, chunkName);
    }

    /// <summary>require 一个 Lua 模块并返回它的返回值（模块没写 return 时返回 true）</summary>
    public object Require(string module)
    {
        // 模块名会被拼进 DoString 源码串，若来源为下发数据可注入 Lua → 入口白名单校验
        if (!IsValidModuleName(module))
        {
            Debug.LogError("[LuaEnvManager] 非法 Lua 模块名（白名单校验拒绝）：" + module);
            return null;
        }

        object[] ret = Env.DoString("return require('" + module + "')", "require:" + module);
        return (ret != null && ret.Length > 0) ? ret[0] : null;
    }

    /// <summary>模块名白名单：非空、长度 ≤128、仅允许 [A-Za-z0-9_.]（防字符串拼接注入 DoString）</summary>
    private static bool IsValidModuleName(string module)
    {
        if (string.IsNullOrEmpty(module) || module.Length > 128) return false;
        for (int i = 0; i < module.Length; i++)
        {
            char c = module[i];
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '.'))
                return false;
        }
        return true;
    }

    // ──────────────────────────────
    // 生命周期
    // ──────────────────────────────

    private void Awake()
    {
        if (Instance != null)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Update()
    {
        if (env == null) return;
        if (Time.unscaledTime < nextTickTime) return;
        nextTickTime = Time.unscaledTime + Mathf.Max(0.1f, tickInterval);
        env.Tick();
    }

    private void OnDestroy()
    {
        DisposeEnv();
        if (Instance == this) Instance = null;
    }

    /// <summary>
    /// 销毁 Lua 虚拟机（停止 Play / 退出时自动调用，也可手动调用）
    ///
    /// xLua 的 LuaEnv.Dispose 在"Lua 侧仍持有 C# 回调"时会直接抛异常拒绝释放
    /// （InvalidOperationException: try to dispose a LuaEnv with C# callback!），
    /// 这里补一轮 GC 重试；两次都失败就保留原虚拟机并报错 —— 绝不能把还活着的
    /// Lua 状态丢掉，否则同一个进程里会同时存在两个 Lua 虚拟机。
    /// </summary>
    /// <returns>是否已成功释放</returns>
    public bool DisposeEnv()
    {
        if (env == null) return true;

        LuaEnv target = env;
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                target.Dispose();
                env = null;
                Debug.Log("[LuaEnvManager] LuaEnv 已释放");
                return true;
            }
            catch (Exception e)
            {
                if (attempt == 1)
                {
                    Debug.LogWarning("[LuaEnvManager] LuaEnv 释放被拒（" + e.Message + "），补一轮 GC 后重试");
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
                else
                {
                    Debug.LogError("[LuaEnvManager] LuaEnv 释放失败，保留原虚拟机：" + e);
                }
            }
        }
        return false;
    }

    // ──────────────────────────────
    // 脚本加载
    // ──────────────────────────────

    /// <summary>
    /// 加载器：先问项目注入的 Loader，再回退到 xLua 内置规则
    /// （require "a.b" → Resources.Load&lt;TextAsset&gt;("a/b.lua")）
    /// </summary>
    private byte[] CustomLoader(ref string filepath)
    {
        byte[] data = Loader != null ? Loader(filepath) : null;
        if (data != null) return data;

        TextAsset asset = Resources.Load<TextAsset>(filepath.Replace('.', '/') + ".lua");
        return asset != null ? asset.bytes : null;
    }
}
