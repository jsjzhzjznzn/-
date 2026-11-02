using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using YooAsset;

namespace SkierFramework
{
    /// <summary>
    /// YooAsset 运行模式
    /// </summary>
    public enum YooAssetRunMode
    {
        /// <summary>编辑器模拟（走 AssetDatabase，无需打 AB 包）</summary>
        EditorSimulate,
        /// <summary>离线包（bundle 内置在 StreamingAssets）</summary>
        OfflinePlay,
        /// <summary>联机模式（远端版本 + 增量下载）</summary>
        HostPlay,
    }

    /// <summary>
    /// YooAsset 初始化解耦层：UI 框架只依赖本类。
    /// 包划分：ui（UI 预制体/UIConfig/Lua/物品图标）、battle（角色/动作/音效/VFX/配置资产）；
    /// activity 包由 LuaActivityHost 自行管理，不在这里创建。
    /// </summary>
    public class YooAssetService : MieMieFrameWork.SingletonService<YooAssetService>
    {
        /// <summary>默认包名（未命中路由的资源都归它管）</summary>
        public const string DefaultPackageName = "ui";

        /// <summary>需要初始化的包列表（需与 BundleCollectorSetting 中的包名一致）</summary>
        public static readonly string[] PackageNames =
        {
            "ui",
            // "battle" 在 P4 导入 Assets/Game/Art/Battle 时加入（空包会让 EditorSimulateBuild 失败）
        };

        /// <summary>
        /// 路径前缀 → 包名 路由表（最长前缀优先；未命中回退默认包）。
        /// </summary>
        public static readonly (string prefix, string package)[] LocationRoutes =
        {
            ("Assets/Game/UI", "ui"),
            ("Assets/Content/LuaScripts", "ui"),
            ("Assets/Game/Resources/ui", "ui"),
            ("Assets/Game/Art/Battle", "battle"),
        };

        private readonly Dictionary<string, ResourcePackage> _packages = new Dictionary<string, ResourcePackage>();

        /// <summary>默认包（初始化完成后才非空）</summary>
        public ResourcePackage Package => GetPackage(DefaultPackageName);

        /// <summary>全部包的只读视图</summary>
        public IReadOnlyDictionary<string, ResourcePackage> Packages => _packages;

        /// <summary>当前运行模式</summary>
        public YooAssetRunMode RunMode { get; private set; }

        /// <summary>所有包是否初始化完成</summary>
        public bool IsInitialized { get; private set; }

        /// <summary>最近一次失败原因（成功时为 null）</summary>
        public string LastError { get; private set; }

        /// <summary>外部在初始化前赋值可强制指定运行模式</summary>
        public static YooAssetRunMode? ForceRunMode;

        /// <summary>HostPlay 模式按包名提供远端服务；为 null 时回退 OfflinePlay</summary>
        public static Func<string, IRemoteService> RemoteServiceFactory;

        /// <summary>某个包的新清单激活成功（热重载入口：UIConfig / Lua 模块）</summary>
        public event Action<string> OnPackageUpdated;

        private bool _initializing;
        private bool _initFailed;

        private static YooAssetRunMode GetDefaultRunMode()
        {
#if UNITY_EDITOR
            return YooAssetRunMode.EditorSimulate;
#else
            return YooAssetRunMode.OfflinePlay;
#endif
        }

        public ResourcePackage GetPackage(string packageName)
        {
            if (string.IsNullOrEmpty(packageName)) return null;
            _packages.TryGetValue(packageName, out var pkg);
            return pkg;
        }

        /// <summary>按 location 前缀路由到所属包（未初始化返回 null）</summary>
        public ResourcePackage GetPackageByLocation(string location)
        {
            if (!IsInitialized || string.IsNullOrEmpty(location)) return null;

            ResourcePackage best = Package;
            int bestLen = -1;
            foreach (var (prefix, packageName) in LocationRoutes)
            {
                if (location.StartsWith(prefix, StringComparison.Ordinal) && prefix.Length > bestLen)
                {
                    var candidate = GetPackage(packageName);
                    if (candidate != null)
                    {
                        best = candidate;
                        bestLen = prefix.Length;
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// 初始化全部包（防重入）。每个包：初始化文件系统 → 请求版本 → 加载激活清单。
        /// </summary>
        public async UniTask InitializeAsync(YooAssetRunMode? mode = null, CancellationToken cancellationToken = default)
        {
            if (IsInitialized)
            {
                if (!YooAssets.IsInitialized || !YooAssets.TryGetPackage(DefaultPackageName, out _))
                {
                    IsInitialized = false;
                    _packages.Clear();
                }
                else
                {
                    return;
                }
            }

            if (_initializing)
            {
                await UniTask.WaitUntil(() => IsInitialized || _initFailed, cancellationToken: cancellationToken);
                return;
            }

            _initializing = true;
            _initFailed = false;
            RunMode = mode ?? ForceRunMode ?? GetDefaultRunMode();
            LastError = null;

            bool setupOk = false;
            try
            {
                if (!YooAssets.IsInitialized)
                {
                    YooAssets.Initialize();
                }

                foreach (var packageName in PackageNames)
                {
                    if (!YooAssets.TryGetPackage(packageName, out var pkg))
                    {
                        pkg = YooAssets.CreatePackage(packageName);
                    }
                    _packages[packageName] = pkg;
                }
                setupOk = true;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Debug.LogError("[YooAssetService] 初始化异常：" + e);
            }

            if (setupOk)
            {
                bool allOk = true;
                foreach (var kv in _packages)
                {
                    var initOp = kv.Value.InitializePackageAsync(CreateOptions(RunMode, kv.Key));
                    await initOp.ToUniTask(cancellationToken: cancellationToken);
                    if (initOp.Status != EOperationStatus.Succeeded)
                    {
                        LastError = kv.Key + ": " + initOp.Error;
                        Debug.LogError("[YooAssetService] 包 " + kv.Key + " 初始化失败：" + initOp.Error);
                        allOk = false;
                        break;
                    }

                    var versionOp = kv.Value.RequestPackageVersionAsync();
                    await versionOp.ToUniTask(cancellationToken: cancellationToken);
                    if (versionOp.Status != EOperationStatus.Succeeded)
                    {
                        LastError = kv.Key + ": " + versionOp.Error;
                        Debug.LogError("[YooAssetService] 包 " + kv.Key + " 请求版本失败：" + versionOp.Error);
                        allOk = false;
                        break;
                    }

                    var manifestOp = kv.Value.LoadPackageManifestAsync(new LoadPackageManifestOptions(versionOp.PackageVersion, 30));
                    await manifestOp.ToUniTask(cancellationToken: cancellationToken);
                    if (manifestOp.Status != EOperationStatus.Succeeded)
                    {
                        LastError = kv.Key + ": " + manifestOp.Error;
                        Debug.LogError("[YooAssetService] 包 " + kv.Key + " 加载清单失败：" + manifestOp.Error);
                        allOk = false;
                        break;
                    }
                }

                if (allOk)
                {
                    IsInitialized = true;
                    Debug.Log("[YooAssetService] 初始化成功，模式=" + RunMode + "，包数=" + _packages.Count);
                }
            }

            _initializing = false;
            _initFailed = !IsInitialized;
        }

        /// <summary>
        /// 热更：重新请求版本并加载新清单，成功后触发 OnPackageUpdated(packageName)。
        /// </summary>
        public async UniTask<bool> UpdatePackageAsync(string packageName, CancellationToken cancellationToken = default)
        {
            if (!IsInitialized)
            {
                LastError = "YooAsset 尚未初始化";
                return false;
            }
            if (!_packages.TryGetValue(packageName, out var pkg) || pkg == null)
            {
                LastError = "包不存在：" + packageName;
                return false;
            }

            var versionOp = pkg.RequestPackageVersionAsync();
            await versionOp.ToUniTask(cancellationToken: cancellationToken);
            if (versionOp.Status != EOperationStatus.Succeeded)
            {
                LastError = packageName + ": " + versionOp.Error;
                Debug.LogError("[YooAssetService] 更新版本失败：" + LastError);
                return false;
            }

            var manifestOp = pkg.LoadPackageManifestAsync(new LoadPackageManifestOptions(versionOp.PackageVersion, 30));
            await manifestOp.ToUniTask(cancellationToken: cancellationToken);
            if (manifestOp.Status != EOperationStatus.Succeeded)
            {
                LastError = packageName + ": " + manifestOp.Error;
                Debug.LogError("[YooAssetService] 更新清单失败：" + LastError);
                return false;
            }

            Debug.Log("[YooAssetService] 包已更新：" + packageName + " → " + versionOp.PackageVersion);
            OnPackageUpdated?.Invoke(packageName);
            return true;
        }

        private static InitializePackageOptions CreateOptions(YooAssetRunMode mode, string packageName)
        {
            switch (mode)
            {
                case YooAssetRunMode.EditorSimulate:
                {
#if UNITY_EDITOR
                    var result = EditorSimulateBuildInvoker.Build(packageName, (int)EBundleType.VirtualAssetBundle);
                    return new EditorSimulateModeOptions
                    {
                        EditorFileSystemParameters = FileSystemParameters.CreateDefaultEditorFileSystemParameters(result.PackageRootDirectory),
                    };
#else
                    goto case YooAssetRunMode.OfflinePlay;
#endif
                }
                case YooAssetRunMode.OfflinePlay:
                {
                    return new OfflinePlayModeOptions
                    {
                        BuiltinFileSystemParameters = FileSystemParameters.CreateDefaultBuiltinFileSystemParameters(),
                    };
                }
                case YooAssetRunMode.HostPlay:
                {
                    var remote = RemoteServiceFactory?.Invoke(packageName);
                    if (remote == null)
                    {
                        Debug.LogWarning("[YooAssetService] HostPlay 未提供 IRemoteService，回退 OfflinePlay：" + packageName);
                        goto case YooAssetRunMode.OfflinePlay;
                    }
                    var cache = FileSystemParameters.CreateDefaultSandboxFileSystemParameters(remote);
                    cache.AddParameter(EFileSystemParameter.DownloadMaxConcurrency, 5);
                    cache.AddParameter(EFileSystemParameter.DownloadMaxRequestPerFrame, 1);
                    return new HostPlayModeOptions
                    {
                        BuiltinFileSystemParameters = null,
                        CacheFileSystemParameters = cache,
                    };
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }

        public void UnloadUnusedAssets()
        {
            if (!IsInitialized) return;
            foreach (var pkg in _packages.Values)
            {
                pkg.UnloadUnusedAssetsAsync();
            }
        }

        public void TryUnloadUnusedAsset(string location)
        {
            var pkg = GetPackageByLocation(location);
            pkg?.TryUnloadUnusedAsset(location);
        }

        public AssetHandle LoadAssetAsync<T>(string location) where T : UnityEngine.Object
        {
            var pkg = GetPackageByLocation(location);
            if (pkg == null)
            {
                Debug.LogError("[YooAssetService] 未找到资源包：" + location);
                return null;
            }
            return pkg.LoadAssetAsync<T>(location);
        }

        public AssetHandle LoadAssetSync<T>(string location) where T : UnityEngine.Object
        {
            var pkg = GetPackageByLocation(location);
            if (pkg == null)
            {
                Debug.LogError("[YooAssetService] 未找到资源包：" + location);
                return null;
            }
            return pkg.LoadAssetSync<T>(location);
        }

        public YooAsset.SceneHandle LoadSceneAsync(string location, LoadSceneMode mode)
        {
            var pkg = GetPackageByLocation(location);
            if (pkg == null)
            {
                Debug.LogError("[YooAssetService] 未找到资源包：" + location);
                return null;
            }
            return pkg.LoadSceneAsync(location, mode);
        }

        public bool IsLocationValid(string location)
        {
            var pkg = GetPackageByLocation(location);
            return pkg != null && pkg.IsLocationValid(location);
        }
    }
}
