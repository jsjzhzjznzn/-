namespace MieMieFrameWork
{
    using Cysharp.Threading.Tasks;
    using UnityEngine;

    /// <summary>
    /// 框架自举：从 Resources 加载 FrameRoot 预制体（与 BattleNetworkLauncher 加载 BattleNetworking 的方式一致）
    /// 场景里手摆了 FrameRoot 时直接复用，不重复创建
    /// </summary>
    public static class ModuleHubBootstrap
    {
        /// <summary>FrameRoot 预制体在 Resources 下的相对路径</summary>
        public const string FrameRootLocation = "Framework/FrameRoot";

        private static bool booting;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoBoot()
        {
            EnsureAsync().Forget();
        }

        /// <summary>
        /// 确保框架根节点存在并等待启动完成，可重复调用
        /// </summary>
        public static async UniTask<ModuleHub> EnsureAsync()
        {
            var Existing = Object.FindFirstObjectByType<ModuleHub>();
            if (Existing != null)
            {
                await Existing.ReadyAsync();
                return Existing;
            }

            if (booting)
            {
                // 并发保护：等第一个调用把根节点建出来
                await UniTask.WaitUntil(() => Object.FindFirstObjectByType<ModuleHub>() != null);
                return await EnsureAsync();
            }

            booting = true;
            try
            {
                var Prefab = Resources.Load<GameObject>(FrameRootLocation);
                if (Prefab == null)
                {
                    FrameLog.Error("[ModuleHub] 找不到 Resources/" + FrameRootLocation + ".prefab，框架未启动", "Module");
                    return null;
                }

                var Root = Object.Instantiate(Prefab);
                Root.name = "FrameRoot";
                Object.DontDestroyOnLoad(Root);

                var Hub = Root.GetComponent<ModuleHub>();
                if (Hub == null)
                    Hub = Root.AddComponent<ModuleHub>();

                await Hub.ReadyAsync();
                return Hub;
            }
            finally
            {
                booting = false;
            }
        }
    }
}
