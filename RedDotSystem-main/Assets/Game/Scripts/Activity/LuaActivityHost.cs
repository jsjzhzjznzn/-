using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using XLua;
using YooAsset;

/// <summary>
/// Lua 活动宿主（与 ActivityShell 同挂 ActivitySystem 物体）：
///   1. YooAsset 活动包生命周期：EditorSimulate（开发秒迭代）/ HostPlay（联机真下载）双模式；
///   2. 活动自建一台独立 LuaEnv，require 走 YooAsset（热更包 = 真·热更），不挂接/不销毁全局共享 VM；
///   3. 每次开面板用这台独立 VM（旧 VM 释放失败时保留复用并告警——绝不影响 Bag/Chat 共用的 VM）；
///   4. 面板画布与热更 prefab 实例化（统一挂 ActivityCanvas 下，拆解整树回收）。
/// 资源包与场景无关（DDOL），跨场景打开活动不重复初始化。
/// </summary>
public class LuaActivityHost : MonoBehaviour
{
    public static LuaActivityHost Instance { get; private set; }

    /// <summary>活动包名（与 ActivityPackagePublisher.PackageName 一致）</summary>
    public const string PackageName = "activity";

    /// <summary>热更层入口模块（Assets/Content/HotContent/lua/activity_main.lua.txt → 地址 activity_main.lua）</summary>
    public const string EntryModule = "activity_main";

    /// <summary>
    /// 资源模式：true = 联机 HostPlay（从 8899 真下载，演示热更）；false = 编辑器模拟（免构建秒迭代）
    /// 编辑器菜单 Tools/活动热更/切换资源模式 可改（PlayerPrefs 持久化）
    /// </summary>
    public static bool UseHostPlayMode = true;

    private const string ModePrefsKey = "Activity_UseHostPlayMode";

    /// <summary>上一次成功使用的热更内容版本（版本变化时清缓存重下）</summary>
    private const string LastContentVersionKey = "Activity_LastContentVersion";

    /// <summary>热更内容远端根地址（默认本机联调 8899；换 CDN / 故障演练时覆盖它）</summary>
    public static string HotContentUrlOverride;
    private static string HotContentUrl => string.IsNullOrEmpty(HotContentUrlOverride)
        ? "http://127.0.0.1:8899"
        : HotContentUrlOverride;

    private ResourcePackage package;
    private readonly HashSet<string> requireMissCache = new HashSet<string>();
    private Canvas panelCanvas;

    /// <summary>框架壳（ActivityView）提供的落点；非空时内容挂在框架画布里，不再自建画布</summary>
    private RectTransform contentRoot;

    /// <summary>框架落点下的内容容器（每次换落点重建，TearDown 只清它的子物体）</summary>
    private Transform contentHost;

    /// <summary>当前入口模块与它的 on_update 帧回调（面板打开期间宿主每帧泵给 Lua：倒计时/动画驱动）</summary>
    private LuaTable entryModule;
    private LuaFunction entryUpdate;

    /// <summary>活动宿主自己独立的 LuaEnv（不与 Bag/Chat 等共用全局 VM，创建/销毁由宿主自理）</summary>
    private LuaEnv activityEnv;

    /// <summary>交给 Lua 的活动上下文表（留待 TearDown 统一 Dispose，防泄漏）</summary>
    private LuaTable contextTable;

    /// <summary>Sprite 等常驻资源句柄：宿主记账、TearDown 统一 Release，引用计数净零</summary>
    private readonly Dictionary<string, AssetHandle> residentSpriteHandles = new Dictionary<string, AssetHandle>();
    private readonly Dictionary<string, AssetHandle> residentAudioHandles = new Dictionary<string, AssetHandle>();

    /// <summary>是否有"被推迟的释放"待执行（TearDown 只登记，真正销毁 LuaEnv 放到下一帧）</summary>
    private bool teardownPending;
    private int teardownFrame = -1;

    private void Awake()
    {
        if (Instance != null)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        UseHostPlayMode = PlayerPrefs.GetInt(ModePrefsKey, 1) == 1;
        DontDestroyOnLoad(gameObject);
    }

    // ──────────────────────────────
    // 预热（登录后后台把资源包拉到"可同步加载"）
    // ──────────────────────────────

    /// <summary>资源包初始化是否在途（预热与点入口并发进来的互斥标记）</summary>
    private bool packageInitInFlight;

    /// <summary>是否已完成 YooAsset 包初始化（只做一次：重复初始化会重建包内部状态）</summary>
    private bool initDone;

    /// <summary>
    /// 资源包是否**完全就绪**（初始化 + 版本 + manifest + 增量下载全过）。
    /// 为什么不信 YooAsset 的 InitializeStatus：版本/manifest/下载失败后它依然是 Succeeded，
    /// 据此判断会让后续每次点击都秒回"就绪"却加载不到资源 —— 就是"点了不显示、点多少次都一样"的根因。
    /// </summary>
    private bool packageReady;

    /// <summary>预热协程句柄（重复调用无副作用）</summary>
    private Coroutine prewarmRoutine;

    /// <summary>
    /// 登录后后台预热资源包：只做 YooAsset 侧（初始化 → 版本 → manifest → 增量下载），**不建 LuaEnv、不建 UI**。
    /// 目的：首次点活动入口几乎零等待——此前首次点击要现场跑完整条下载链路，界面上又没反馈，
    /// 用户会以为按钮失灵而反复点。
    /// </summary>
    public void PrewarmPackage()
    {
        if (packageReady)
        {
            Debug.Log("[活动] 资源包已就绪，无需预热");
            return;
        }
        if (prewarmRoutine != null) return;
        prewarmRoutine = StartCoroutine(PrewarmRoutine());
    }

    private IEnumerator PrewarmRoutine()
    {
        float started = Time.realtimeSinceStartup;
        bool ready = false;
        yield return EnsurePackageReadyRoutine(value => ready = value);
        Debug.Log($"[活动] 资源包预热{(ready ? "成功" : "失败")}，耗时 {Time.realtimeSinceStartup - started:F2}s");
        prewarmRoutine = null;
    }

    // ──────────────────────────────
    // 资源包就绪（协程，Shell 的开面板流程里 yield）
    // ──────────────────────────────

    /// <summary>
    /// 确保资源包就绪（**可重入安全**）：并发调用时后到者等前一次结束再读状态。
    /// 为什么必须做：登录会自动预热，用户紧接着点入口就会并发进来，
    /// 而 YooAsset 不允许对同一个包并发初始化（会报错/流程半死）。
    /// </summary>
    public IEnumerator EnsurePackageReadyRoutine(Action<bool> onDone)
    {
        // 并发保护：登录预热与本轮点击可能同时进来（YooAsset 不允许对同一个包并发初始化）
        while (packageInitInFlight) yield return null;
        if (packageReady)
        {
            onDone?.Invoke(true);
            yield break;
        }

        // 走到这里说明"要么没人跑、要么上一次没跑成"——本次自己跑：
        // 初始化已完成时会跳过初始化，只重跑"版本 → manifest → 下载"这一段
        packageInitInFlight = true;
        try
        {
            yield return EnsurePackageReadyInternal(onDone);
        }
        finally
        {
            packageInitInFlight = false;
        }
    }

    /// <summary>
    /// 资源包初始化主流程：
    /// HostPlay = 初始化 → 拉远端版本 → 载 manifest → 增量下载（热更核心链路，日志即证据）
    /// EditorSimulate = 直接对 AssetDatabase 模拟（免构建，开发迭代用）
    /// </summary>
    private IEnumerator EnsurePackageReadyInternal(Action<bool> onDone)
    {
        if (YooAssets.IsInitialized == false) YooAssets.Initialize();
        if (package == null && !YooAssets.TryGetPackage(PackageName, out package))
            package = YooAssets.CreatePackage(PackageName);

        // ① 已完全就绪 → 秒回（就绪状态见 packageReady 的注释：不能拿 YooAsset 的 InitializeStatus 当就绪）
        if (packageReady)
        {
            onDone?.Invoke(true);
            yield break;
        }

        InitializePackageOperation initOp = null;
        if (!initDone && UseHostPlayMode)
        {
            HostPlayModeOptions options = new HostPlayModeOptions
            {
                BuiltinFileSystemParameters = null,   // 纯热更包：无首包内置（FileSystemHost 跳过 null）
                CacheFileSystemParameters = FileSystemParameters.CreateDefaultSandboxFileSystemParameters(new HotContentRemoteService()),
            };
            options.CacheFileSystemParameters.AddParameter(EFileSystemParameter.DownloadMaxConcurrency, 5);
            options.CacheFileSystemParameters.AddParameter(EFileSystemParameter.DownloadMaxRequestPerFrame, 1);
            initOp = package.InitializePackageAsync(options);
        }
        else if (!initDone)
        {
#if UNITY_EDITOR
            // 模拟构建：扫收集器生成虚拟清单，AssetDatabase 直读（改 HotContent 立即生效）
            PackageBuildResult simulate = EditorSimulateBuildInvoker.Build(PackageName, (int)EBundleType.VirtualAssetBundle);
            EditorSimulateModeOptions options = new EditorSimulateModeOptions
            {
                EditorFileSystemParameters = FileSystemParameters.CreateDefaultEditorFileSystemParameters(simulate.PackageRootDirectory),
            };
            initOp = package.InitializePackageAsync(options);
#else
            Debug.LogError("[活动] EditorSimulate 模式仅编辑器可用，运行时请用 HostPlay");
            onDone?.Invoke(false);
            yield break;
#endif
        }
        if (!initDone)
        {
            yield return initOp;
            if (initOp.Status != EOperationStatus.Succeeded)
            {
                Debug.LogWarning($"[活动] 资源包初始化失败：{initOp.Error}（YooAsset 的包实例此时已不可用，编辑器里请重启 Play 再试）");
                onDone?.Invoke(false);
                yield break;
            }
            initDone = true;
        }

        // 版本 → manifest：HostPlay 与编辑器模拟都必须走（模拟模式漏这一步，LoadAssetSync 会报 Active package manifest not found）
        RequestPackageVersionOperation verOp = package.RequestPackageVersionAsync();
        yield return verOp;
        if (verOp.Status != EOperationStatus.Succeeded)
        {
            Debug.LogWarning($"[活动] 版本请求失败：{verOp.Error}");
            onDone?.Invoke(false);
            yield break;
        }
        Debug.Log($"[活动] 资源版本 = {verOp.PackageVersion}" + (UseHostPlayMode ? "（远端）" : "（编辑器模拟）"));

        // 远端版本变化时清空本地缓存再下载：沙箱缓存的校验逻辑会漏掉同名 bundle 的内容变化，
        // 不清理会出现「提示资源已是最新，但跑的还是旧内容」。
        if (UseHostPlayMode)
        {
            string lastVersion = PlayerPrefs.GetString(LastContentVersionKey, string.Empty);
            if (lastVersion != verOp.PackageVersion)
            {
                Debug.Log($"[活动热更] 版本变化 {lastVersion} -> {verOp.PackageVersion}，清空本地缓存后重新下载");
                yield return package.ClearCacheAsync(new ClearCacheOptions(ClearCacheMethods.ClearAllBundleFiles));
                yield return package.ClearCacheAsync(new ClearCacheOptions(ClearCacheMethods.ClearAllManifestFiles));
                PlayerPrefs.SetString(LastContentVersionKey, verOp.PackageVersion);
                PlayerPrefs.Save();
            }
        }

        LoadPackageManifestOperation manifestOp = package.LoadPackageManifestAsync(new LoadPackageManifestOptions(verOp.PackageVersion, 60));
        yield return manifestOp;
        if (manifestOp.Status != EOperationStatus.Succeeded)
        {
            Debug.LogWarning($"[活动] manifest 加载失败：{manifestOp.Error}");
            onDone?.Invoke(false);
            yield break;
        }

        if (UseHostPlayMode)
        {
            // 增量下载（鸣潮式"发现新版本→下载→生效"的完整闭环）
            ResourceDownloaderOperation downloader = package.CreateResourceDownloader(new ResourceDownloaderOptions(10, 3));
            if (downloader.TotalDownloadCount > 0)
            {
                Debug.Log($"[活动热更] 发现 {downloader.TotalDownloadCount} 个待更新文件（{downloader.TotalDownloadBytes} 字节），开始增量下载");
                downloader.StartDownload();
                yield return downloader;
                if (downloader.Status != EOperationStatus.Succeeded)
                {
                    Debug.LogWarning("[活动] 增量下载失败");
                    onDone?.Invoke(false);
                    yield break;
                }
                Debug.Log($"[活动热更] 下载完成 {downloader.CurrentDownloadCount}/{downloader.TotalDownloadCount}");
            }
            else
            {
                Debug.Log("[活动] 资源已是最新，无需下载");
            }
        }

        // ③ 全部通过才记账"就绪"：失败只算没就绪，下次点击会重跑"版本→manifest→下载"这一段
        packageReady = true;
        onDone?.Invoke(true);
    }

    /// <summary>HostPlay 模式的远端地址查询（本机联调固定 8899）</summary>
    private class HotContentRemoteService : IRemoteService
    {
        public IReadOnlyList<string> GetRemoteUrls(string fileName)
        {
            return new List<string> { $"{HotContentUrl}/{fileName}" };
        }
    }

    // ──────────────────────────────
    // Lua 环境重建与入口启动
    // ──────────────────────────────

    /// <summary>
    /// 准备 Lua 环境：为活动新建一台**独立** LuaEnv（require 走 YooAsset），每次开面板从零构建，
    /// 全局状态绝不跨活动残留；旧 VM 释放失败时保留复用并告警。
    /// 关键：绝不触碰 LuaEnvManager 的全局共享 VM——面板开关不再清光 Bag/Chat 等业务的 Lua 状态。
    /// </summary>
    public void PrepareLuaEnvironment()
    {
        // 上一轮的延迟释放先落地：本方法在协程里被调用（不在 Lua 调用栈内），此刻销毁是安全的
        CompletePendingTeardown();
        ActivityUi.BeginSession();       // 新会话：放开点击、清空上一轮的回调登记

        // activityEnv 非空说明上一台 VM 释放失败被保留（xLua 释放被 C# 回调拒绝）：复用而非并存两台
        if (activityEnv == null)
        {
            activityEnv = new LuaEnv();
            activityEnv.AddLoader(ActivityLuaLoader);   // require 走 YooAsset 热更包 → 回退本地 Resources
        }
        else
        {
            Debug.LogWarning("[活动] 上一台 LuaEnv 释放失败（Lua 侧仍有 C# 回调），复用旧虚拟机——本面板可能运行旧内容");
        }
    }

    /// <summary>
    /// 启动热更层：require 入口模块并调用 M.launch(ctx)。
    /// 返回是否成功（失败时 Shell 不置"已打开"态）。
    /// </summary>
    public bool LaunchActivityModule()
    {
        if (activityEnv == null)
        {
            Debug.LogError("[活动] 活动 LuaEnv 未就绪");
            return false;
        }

        LuaFunction launch = null;
        try
        {
            object module = RequireOnActivityEnv(EntryModule);
            LuaTable entry = module as LuaTable;
            if (entry == null)
            {
                Debug.LogError($"[活动] 入口模块 {EntryModule} 未返回 table（返回值：{module}）");
                return false;
            }

            launch = entry.Get<LuaFunction>("launch");
            if (launch == null)
            {
                Debug.LogError($"[活动] 入口模块缺少 launch(ctx) 函数");
                return false;
            }

            entryModule = entry;
            entryUpdate = entry.Get<LuaFunction>("on_update");   // 可选：定义了才每帧泵
            contextTable = BuildContextTable();
            launch.Call(contextTable);
            Debug.Log($"[活动] 热更层已启动（{EntryModule}，内容版本 {ActivityShell.Instance?.CurrentContentVersion}）");
            return true;
        }
        catch (Exception exception)
        {
            Debug.LogError($"[活动] 启动热更层失败：{exception}");
            TearDown();     // 半截会话整套拆掉（VM 释放仍推迟到 Lua 栈外），下次打开重新来过
            return false;
        }
        finally
        {
            launch?.Dispose();   // launch 已同步执行完，成功/失败都释放本地句柄
        }
    }

    /// <summary>构建交给 Lua 的活动上下文（纯数据，Lua 只读）</summary>
    private LuaTable BuildContextTable()
    {
        ActivityShell shell = ActivityShell.Instance;
        LuaTable ctx = activityEnv.NewTable();
        ctx.Set("activity_id", shell != null ? shell.CurrentActivityId : 0);
        ctx.Set("banner_id", shell != null ? shell.CurrentBannerId : 0);
        ctx.Set("title", shell?.CurrentTitle ?? string.Empty);
        ctx.Set("subtitle", shell?.CurrentSubtitle ?? string.Empty);
        ctx.Set("content_version", shell?.CurrentContentVersion ?? string.Empty);
        ctx.Set("start_ms", shell?.CurrentStartAtMs ?? 0L);
        ctx.Set("end_ms", shell?.CurrentEndAtMs ?? 0L);
        ctx.Set("server_now_ms", shell?.ServerNowMs ?? 0L);
        return ctx;
    }

    /// <summary>
    /// YooAsset 脚本加载器：require("xxx") → 包内地址 "xxx.lua"
    /// （约定：热更 Lua 文件命名 xxx.lua.txt，AddressByFileName 收集后地址 = xxx.lua）
    /// 未命中返回 null 回退到 xLua 内置 Resources 规则（Lua.main 等本地脚本不受影响）
    /// </summary>
    private byte[] YooAssetLuaLoader(string moduleName)
    {
        if (string.IsNullOrEmpty(moduleName)) return null;
        if (package == null || package.InitializeStatus != EOperationStatus.Succeeded) return null;
        if (requireMissCache.Contains(moduleName)) return null;

        AssetHandle handle = package.LoadAssetSync<TextAsset>(moduleName + ".lua");
        if (handle == null || handle.Status != EOperationStatus.Succeeded)
        {
            handle?.Release();
            requireMissCache.Add(moduleName);
            return null;
        }

        byte[] bytes = handle.GetAssetObject<TextAsset>().bytes;
        handle.Release();
        return bytes;
    }

    /// <summary>独立 VM 的加载器包装：先问 YooAsset 热更包，未命中回退本地 Resources（Lua.main 等不受影响）</summary>
    private byte[] ActivityLuaLoader(ref string filepath)
    {
        byte[] data = YooAssetLuaLoader(filepath);
        if (data != null) return data;

        TextAsset asset = Resources.Load<TextAsset>(filepath.Replace('.', '/') + ".lua");
        return asset != null ? asset.bytes : null;
    }

    /// <summary>在活动独立 VM 里 require 模块（入口模块名为编译期常量，无注入面）</summary>
    private object RequireOnActivityEnv(string module)
    {
        object[] ret = activityEnv.DoString("return require('" + module + "')", "require:" + module);
        return (ret != null && ret.Length > 0) ? ret[0] : null;
    }

    /// <summary>
    /// 释放活动独立 VM（补一轮 GC 重试）；两次都失败就保留旧 VM 并告警——
    /// 只影响本活动，绝不触碰全局共享 VM，Bag/Chat 等业务不受牵连。
    /// </summary>
    private void DisposeActivityEnv()
    {
        if (activityEnv == null) return;

        LuaEnv target = activityEnv;
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                target.Dispose();
                activityEnv = null;
                Debug.Log("[活动] 活动 LuaEnv 已释放");
                return;
            }
            catch (Exception e)
            {
                if (attempt == 1)
                {
                    Debug.LogWarning("[活动] 活动 LuaEnv 释放被拒（" + e.Message + "），补一轮 GC 后重试");
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
                else
                {
                    Debug.LogError("[活动] 活动 LuaEnv 释放失败，保留旧虚拟机（仅影响本活动，共享 VM 安全）：" + e);
                }
            }
        }
    }

    // ──────────────────────────────
    // 面板画布与热更资源实例化
    // ──────────────────────────────

    /// <summary>
    /// 由 ActivityView 在打开时注入框架内的落点（传 null = 退回旧的独立画布，供联调/E2E 直调 Shell 用）。
    /// </summary>
    public void SetContentRoot(RectTransform root)
    {
        // 每次开面板都登记一次：旧落点下的容器直接销毁（内容此时已由 TearDown 清空），
        // 否则会留下同名的空 ActivityContent，Find("ActivityContent") 会读到旧的那个。
        if (contentHost != null)
        {
            Destroy(contentHost.gameObject);
            contentHost = null;
        }
        contentRoot = root;
    }

    /// <summary>
    /// 热更内容的父节点：
    ///   ① 框架内（ActivityView 已登记）→ 落点下的 ActivityContent 节点，层级/缩放随框架 NormalLayer；
    ///   ② 框架外（框架未初始化就直调打开）→ 旧的自建覆盖画布，保证联调与 E2E 脚本照常工作。
    /// </summary>
    private Transform GetContentHost()
    {
        if (contentRoot != null)
        {
            if (contentHost == null || !contentHost.IsChildOf(contentRoot))
            {
                GameObject go = new GameObject("ActivityContent", typeof(RectTransform));
                go.layer = LayerMask.NameToLayer("UI");

                RectTransform rect = (RectTransform)go.transform;
                rect.SetParent(contentRoot, false);
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
                rect.localScale = Vector3.one;
                contentHost = rect;
            }
            return contentHost;
        }

        return GetPanelCanvas().transform;
    }

    /// <summary>
    /// 旧兜底画布（仅在框架未初始化时使用）：程序化 overlay 画布，UICameraService 的 sweep 会把它转 camera space 并接相机。
    /// 正常路径（ActivityView 登记在 NormalLayer）不会走到这里，也不再需要手写 sortingOrder。
    /// </summary>
    public Canvas GetPanelCanvas()
    {
        if (panelCanvas != null) return panelCanvas;

        GameObject go = new GameObject("ActivityCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        panelCanvas = go.GetComponent<Canvas>();
        panelCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // 框架外的兜底层：必须压过主页(3030)与常规面板，又不能盖住提示层(6000)与黑幕(7000)。
        panelCanvas.sortingOrder = 5500;

        CanvasScaler scaler = go.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        go.layer = LayerMask.NameToLayer("UI");
        DontDestroyOnLoad(go);
        return panelCanvas;
    }

    /// <summary>从热更包同步实例化 prefab（挂到当前内容容器下；整棵子树统一 UI 层，UI 相机才看得见）</summary>
    public GameObject InstantiateFromPackage(string location)
    {
        AssetHandle handle = package.LoadAssetSync<GameObject>(location);
        if (handle == null || handle.Status != EOperationStatus.Succeeded)
        {
            Debug.LogWarning($"[活动] 热更资源不存在：{location}");
            handle?.Release();
            return null;
        }
        GameObject prefab = handle.GetAssetObject<GameObject>();
        GameObject instance = UnityEngine.Object.Instantiate(prefab, GetContentHost(), false);
        UICameraService.SetLayerRecursive(instance.transform, LayerMask.NameToLayer("UI"));
        handle.Release();
        return instance;
    }

    /// <summary>
    /// 调入口模块（activity_main）里的函数并返回结果，供联调/自动化验证使用。
    /// 只在活动 LuaEnv 存活期间有效；函数不存在或出错返回 null。
    /// </summary>
    public object CallEntry(string functionName, params object[] args)
    {
        if (entryModule == null || string.IsNullOrEmpty(functionName)) return null;

        LuaFunction function = entryModule.Get<LuaFunction>(functionName);
        if (function == null) return null;

        try
        {
            object[] result = function.Call(args);
            return (result != null && result.Length > 0) ? result[0] : null;
        }
        catch (Exception exception)
        {
            Debug.LogError($"[活动] 调 {EntryModule}.{functionName} 失败：{exception}");
            return null;
        }
        finally
        {
            function.Dispose();
        }
    }

    /// <summary>客户端常驻的活动资源根（玩法视觉随客户端走，逻辑与配置仍走热更包）</summary>
    private static readonly string[] ClientResourceRoots =
    {
        "Activity/arkcards/prefabs/",
        "Activity/arkcards/art/",
        "Activity/arkcards/audio/",
        "Activity/",
    };

    /// <summary>先查客户端 Resources（Resources/Activity/...），没有再回落到热更包</summary>
    private static T LoadClientAsset<T>(string location) where T : UnityEngine.Object
    {
        if (string.IsNullOrEmpty(location)) return null;
        for (int i = 0; i < ClientResourceRoots.Length; i++)
        {
            T asset = Resources.Load<T>(ClientResourceRoots[i] + location);
            if (asset != null) return asset;
        }
        return null;
    }

    /// <summary>实例化预制体并**原样返回**：不改父节点、不改层。
    /// 先取客户端 Resources 里的常驻预制体，没有再回落到热更包。
    /// 给需要自己摆位置的玩法用（如活动 3D 舞台），与 InstantiateFromPackage（挂到活动内容层）区分。
    /// </summary>
    public GameObject InstantiateRawFromPackage(string location)
    {
        GameObject clientPrefab = LoadClientAsset<GameObject>(location);
        if (clientPrefab != null) return UnityEngine.Object.Instantiate(clientPrefab);

        AssetHandle handle = package.LoadAssetSync<GameObject>(location);
        if (handle == null || handle.Status != EOperationStatus.Succeeded)
        {
            Debug.LogWarning($"[活动] 热更预制体不存在：{location}");
            handle?.Release();
            return null;
        }

        GameObject prefab = handle.GetAssetObject<GameObject>();
        GameObject instance = UnityEngine.Object.Instantiate(prefab);
        handle.Release();
        return instance;
    }

    /// <summary>同步加载 Sprite：先取客户端常驻资源，再回落热更包（句柄交宿主记账，TearDown 统一 Release，引用计数净零）</summary>
    public Sprite LoadSpriteFromPackage(string location)
    {
        if (string.IsNullOrEmpty(location)) return null;

        Sprite clientSprite = LoadClientAsset<Sprite>(location);
        if (clientSprite != null) return clientSprite;

        // 同一 location 复用已记录句柄：避免每次调用都再 LoadAssetSync 让引用计数净增（旧缺陷）
        if (residentSpriteHandles.TryGetValue(location, out AssetHandle cached) && cached != null)
            return cached.GetAssetObject<Sprite>();

        AssetHandle handle = package.LoadAssetSync<Sprite>(location);
        if (handle == null || handle.Status != EOperationStatus.Succeeded)
        {
            Debug.LogWarning($"[活动] 热更图片不存在：{location}");
            handle?.Release();
            return null;
        }
        Sprite sprite = handle.GetAssetObject<Sprite>();
        residentSpriteHandles[location] = handle;
        return sprite;
    }

    /// <summary>从热更包同步加载文本（活动配置 json）</summary>
    public string LoadTextFromPackage(string location)
    {
        AssetHandle handle = package.LoadAssetSync<TextAsset>(location);
        if (handle == null || handle.Status != EOperationStatus.Succeeded)
        {
            handle?.Release();
            return null;
        }
        string text = handle.GetAssetObject<TextAsset>().text;
        handle.Release();
        return text;
    }

    /// <summary>同步加载音频：先取客户端常驻资源，再回落热更包（热更包资源常驻，活动关闭时统一释放）</summary>
    public AudioClip LoadAudioClipFromPackage(string location)
    {
        if (string.IsNullOrEmpty(location)) return null;

        AudioClip clientClip = LoadClientAsset<AudioClip>(location);
        if (clientClip != null) return clientClip;

        if (residentAudioHandles.TryGetValue(location, out AssetHandle cached) && cached != null)
            return cached.GetAssetObject<AudioClip>();

        AssetHandle handle = package.LoadAssetSync<AudioClip>(location);
        if (handle == null || handle.Status != EOperationStatus.Succeeded)
        {
            Debug.LogWarning($"[活动] 热更音频不存在：{location}");
            handle?.Release();
            return null;
        }

        AudioClip clip = handle.GetAssetObject<AudioClip>();
        residentAudioHandles[location] = handle;
        return clip;
    }

    /// <summary>
    /// 拆面板（**顺序即正确性**，这里是实测原生崩溃的现场）：
    ///   ① 立刻清空内容容器 → 面板马上消失，手感即时；
    ///   ② 立刻摘掉宿主的 Lua 引用（on_update / 入口模块 / 上下文表）并释放记账的图片句柄、禁止按钮点击
    ///      → 停帧泵、引用计数净零、防窗口期误触（这些都是 unref / Release / 标志位，不会碰 lua_State 的生死）；
    ///   ③ **把"释放按钮回调 + LuaEnv.Dispose"推迟到下一帧**。
    ///
    /// 为什么 ③ 必须推迟：本方法通常是在 Lua 回调里被调用的（点"×" → Lua 调 bridge.CloseActivity()）。
    /// 若在 Lua 调用栈内部同步 lua_close，回调返回时 lua_pcall 仍会落在已销毁的 lua_State 上
    /// —— 实测 SIGSEGV（原生崩溃 / 编辑器闪退），日志表现是"LuaEnv 已释放"之后紧接着崩。
    /// </summary>
    public void TearDown()
    {
        // ① 清空内容容器（框架落点下 = ActivityContent；框架外 = 旧画布）——面板立刻消失，手感即时
        ClearContent();

        entryUpdate?.Dispose();          // 立刻停帧泵（unref，安全）
        entryUpdate = null;
        entryModule?.Dispose();
        entryModule = null;
        contextTable?.Dispose();         // 上下文表句柄回收（unref，安全；Lua 侧若自存副本不受影响）
        contextTable = null;

        ReleaseResidentSpriteHandles();  // 图片句柄统一释放：与开面板的净增对冲
        ReleaseResidentAudioHandles();   // 音频句柄同样成对释放

        ActivityUi.EndSession();         // 立刻禁止点击
        requireMissCache.Clear();

        teardownPending = true;          // 真正的释放放到下一帧（等 Lua 调用栈退干净）
        teardownFrame = Time.frameCount;
    }

    /// <summary>清空当前内容容器（只销毁热更层实例化的物体，容器本身留给下次复用）</summary>
    private void ClearContent()
    {
        Transform host = contentHost != null
            ? contentHost
            : (panelCanvas != null ? panelCanvas.transform : null);
        if (host == null) return;

        for (int i = host.childCount - 1; i >= 0; i--)
            Destroy(host.GetChild(i).gameObject);
    }

    /// <summary>释放宿主记账的常驻图片句柄并清空记录（与开面板的引用净增对冲）</summary>
    private void ReleaseResidentSpriteHandles()
    {
        if (residentSpriteHandles.Count == 0) return;
        foreach (AssetHandle handle in residentSpriteHandles.Values)
            handle?.Release();
        residentSpriteHandles.Clear();
    }

    /// <summary>释放常驻音频句柄（与 LoadAudioClipFromPackage 的引用净增对冲）</summary>
    private void ReleaseResidentAudioHandles()
    {
        if (residentAudioHandles.Count == 0) return;
        foreach (AssetHandle handle in residentAudioHandles.Values)
            handle?.Release();
        residentAudioHandles.Clear();
    }

    /// <summary>
    /// 执行被推迟的释放：先放掉 Lua 回调，再销毁 LuaEnv。幂等；
    /// 重开面板前（PrepareLuaEnvironment）会先 Flush 一次，避免与新一轮重建交错。
    /// </summary>
    public void CompletePendingTeardown()
    {
        if (!teardownPending) return;
        teardownPending = false;
        ActivityUi.ReleaseClickCallbacks();     // 必须先放回调，否则 Dispose 会报"仍有 C# 回调"
        DisposeActivityEnv();
    }

    /// <summary>帧泵：面板打开期间把 deltaTime 喂给 Lua 的 on_update（倒计时走表/动画推进）</summary>
    private void Update()
    {
        // 上一帧点了关闭：此时 Lua 调用栈已退干净，可以安全销毁虚拟机
        if (teardownPending && Time.frameCount > teardownFrame) CompletePendingTeardown();

        if (entryUpdate == null) return;
        try
        {
            entryUpdate.Call(Time.deltaTime);
        }
        catch (Exception exception)
        {
            // Lua 帧回调抛错只报一次并摘除，不让异常每帧刷屏
            Debug.LogError($"[活动] on_update 异常，已摘除帧泵：{exception.Message}");
            entryUpdate.Dispose();
            entryUpdate = null;
        }
    }
}
