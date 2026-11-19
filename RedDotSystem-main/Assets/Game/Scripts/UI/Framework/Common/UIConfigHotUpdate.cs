namespace SkierFramework
{
    using System;

    using Common.Network;

    using Cysharp.Threading.Tasks;

    using UnityEngine;

    using YooAsset;

    /// <summary>
    /// UIConfig 热更的开关与触发。
    ///
    /// 开关（PlayerPrefs UIConfig_UseHostPlayMode，默认关）：
    ///   开 → ui 包走 HostPlay，从 {HotContentRoot}/ui 增量下载（含 UIConfig.json 本体）
    ///   关 → 与改造前完全一致（编辑器模拟 / 真机只读包内资源）
    ///
    /// 触发：登录成功后检查一次 ui 包更新；有新版本时由 UIManager.ReloadUIConfig 换表并重建界面。
    /// 换表会拆掉全部界面（含常驻 UI），所以只放在登录成功这一刻，不提供运行时手动刷新入口。
    /// </summary>
    public static class UIConfigHotUpdate
    {
        /// <summary>是否启用 host 下载（1 = 开；缺省 0 = 关）</summary>
        public const string PreferenceKey = "UIConfig_UseHostPlayMode";

        /// <summary>热更内容服务根地址（与活动包同一台 8899 静态服务）</summary>
        public const string DefaultHotContentRoot = "http://127.0.0.1:8899";

        /// <summary>发布到别的机器时在启动前改写它（与 LuaActivityHost 同款约定）</summary>
        public static string HotContentRootOverride;

        public static bool Enabled
        {
            get => PlayerPrefs.GetInt(PreferenceKey, 0) == 1;
            set
            {
                PlayerPrefs.SetInt(PreferenceKey, value ? 1 : 0);
                PlayerPrefs.Save();
            }
        }

        private static string HotContentRoot =>
            string.IsNullOrEmpty(HotContentRootOverride) ? DefaultHotContentRoot : HotContentRootOverride;

        /// <summary>
        /// 在资源包初始化之前落实运行模式。
        /// 用 SubsystemRegistration 而不是 BeforeSceneLoad：必须早于 ModuleHub 启动时对 ui 包的初始化，
        /// 否则会出现「开关开着但包已按 OfflinePlay 初始化完」的竞态。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ApplyRunMode()
        {
            if (!Enabled) return;

            YooAssetService.ForceRunMode = YooAssetRunMode.HostPlay;
            YooAssetService.RemoteServiceFactory = packageName =>
                packageName == YooAssetService.DefaultPackageName
                    ? YooRemoteService.ForPackage(packageName, HotContentRoot)
                    : null;

            GameLog.Info($"[UIConfig热更] 已启用 HostPlay：{YooAssetService.DefaultPackageName} 包从 {HotContentRoot}/{YooAssetService.DefaultPackageName} 增量下载");
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoSubscribe()
        {
            if (!Enabled) return;
            SubscribeAsync().Forget();
            StartupCheckAsync().Forget();
        }

        /// <summary>
        /// 启动期检查热更：进登录界面前先弹下载界面，有更新时下载完再继续。
        /// 与登录后的 <see cref="CheckAsync"/> 是同一份检查逻辑，区别只是这里带进度界面。
        /// </summary>
        private static async UniTask StartupCheckAsync()
        {
            try
            {
                await UniTask.WaitUntil(() => YooAssetService.Instance != null && YooAssetService.Instance.IsInitialized);
                await ResourceUpdateScreen.RunAsync(async report =>
                {
                    return await YooAssetService.Instance.UpdatePackageAsync(
                        YooAssetService.DefaultPackageName,
                        progress => report(progress, null),
                        default);
                });
            }
            catch (Exception exception)
            {
                GameLog.Warn($"[UIConfig热更] 启动期检查失败（不影响进入登录）：{exception.Message}");
            }
        }

        private static async UniTask SubscribeAsync()
        {
            await UniTask.WaitUntil(() => ClientNetworkManager.Instance != null);
            ClientNetworkManager.Instance.LoginResponseReceived += OnLoginResponse;
        }

        private static void OnLoginResponse(LoginResponse response)
        {
            if (response == null || !response.Success) return;
            CheckAsync().Forget();
        }

        /// <summary>
        /// 检查 ui 包更新；有新版本时 UIManager 会自动重载注册表。
        /// 失败不打断登录流程，只记日志。
        /// </summary>
        public static async UniTask<bool> CheckAsync()
        {
            try
            {
                UIManager manager = UIManager.Instance;
                if (manager == null)
                {
                    GameLog.Warn("[UIConfig热更] UIManager 尚未就绪，跳过本次检查");
                    return false;
                }

                bool updated = await manager.CheckUIUpdateAsync();
                GameLog.Info(updated
                    ? "[UIConfig热更] ui 包已更新，UIConfig 注册表已重载"
                    : "[UIConfig热更] ui 包无新版本，沿用当前注册表");
                return updated;
            }
            catch (Exception exception)
            {
                GameLog.Warn($"[UIConfig热更] 检查更新失败（不影响登录）：{exception.Message}");
                return false;
            }
        }
    }
}