namespace Game.Bootstrap
{
    using Cysharp.Threading.Tasks;
    using Game.Achievement;
    using Game.Bag;
    using Game.Battle.Invite;
    using Game.Chat;
    using Game.Guide;
    using Game.Narrative.Quest;
    using Game.Save;
    using Game.Equipment;
    using Game.Mail;
    using Game.Network;
    using MieMieFrameWork;
    using MieMieFrameWork.Business;
    using UnityEngine;

    /// <summary>
    /// 业务组合根
    /// 框架（ModuleHub）就绪后按固定顺序创建各业务模块，并把它们按公共接口注册到 GameHub
    /// 本类位于 Assembly-CSharp：模块程序集不能反向引用它，因此这里可以同时看见框架与全部模块
    /// </summary>
    public static class GameBootstrap
    {
        /// <summary>业务服务是否已完成组装</summary>
        public static bool IsReady { get; private set; }

        private static UniTaskCompletionSource readySource;

        /// <summary>场景加载前自举，与其它常驻单例相同</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoBoot()
        {
            BootAsync().Forget();
        }

        /// <summary>
        /// 等待业务服务组装完成，可重复调用
        /// </summary>
        public static UniTask ReadyAsync()
        {
            if (IsReady)
                return UniTask.CompletedTask;

            readySource ??= new UniTaskCompletionSource();
            return readySource.Task;
        }

        private static async UniTask BootAsync()
        {
            ModuleHub hub = await ModuleHubBootstrap.EnsureAsync();
            if (hub == null)
            {
                GameLog.Error("[Boot] [GameBootstrap] 框架未启动，业务模块不组装");
                return;
            }

            await hub.ReadyAsync();

            // 模块宿主随各自单例自举，与组合根的启动先后顺序不固定
            await UniTask.WaitUntil(() => ClientNetworkManager.Instance != null);
            await UniTask.WaitUntil(() => BagManager.Instance != null);
            await UniTask.WaitUntil(() => MailManager.Instance != null);
            await UniTask.WaitUntil(() => AchievementManager.Instance != null);
            await UniTask.WaitUntil(() => EquipmentManager.Instance != null);
            await UniTask.WaitUntil(() => ChatManager.Instance != null);
            await UniTask.WaitUntil(() => WorldChatManager.Instance != null);
            await UniTask.WaitUntil(() => GuideManager.Instance != null);
            await UniTask.WaitUntil(() => MmQuestRuntime.Instance != null);
            await UniTask.WaitUntil(() => BattleInviteManager.Instance != null);

            RegisterModules();

            IsReady = true;
            readySource?.TrySetResult();
        }

        /// <summary>
        /// 按依赖顺序注册各模块公共服务
        /// 各模块仍由自身单例自举并持有生命周期，这里只登记公共接口
        /// </summary>
        private static void RegisterModules()
        {
            ClientNetworkManager network = ClientNetworkManager.Instance;
            if (network != null && network.Network != null)
                GameHub.Register<INetworkService>(network.Network);
            else
                GameLog.Error("[Boot] [GameBootstrap] 网络管理器未就绪，INetworkService 未注册");

            if (BagManager.Instance != null)
                GameHub.Register<IBagService>(BagManager.Instance);
            else
                GameLog.Error("[Boot] [GameBootstrap] 背包管理器未就绪，IBagService 未注册");

            if (MailManager.Instance != null)
                GameHub.Register<IMailService>(MailManager.Instance);
            else
                GameLog.Error("[Boot] [GameBootstrap] 邮件管理器未就绪，IMailService 未注册");

            if (AchievementManager.Instance != null)
                GameHub.Register<IAchievementService>(AchievementManager.Instance);
            else
                GameLog.Error("[Boot] [GameBootstrap] 成就管理器未就绪，IAchievementService 未注册");

            if (EquipmentManager.Instance != null)
                GameHub.Register<IEquipmentService>(EquipmentManager.Instance);
            else
                GameLog.Error("[Boot] [GameBootstrap] 装备管理器未就绪，IEquipmentService 未注册");

            if (ChatManager.Instance != null)
                GameHub.Register<IChatService>(ChatManager.Instance);
            else
                GameLog.Error("[Boot] [GameBootstrap] 聊天管理器未就绪，IChatService 未注册");

            if (WorldChatManager.Instance != null)
                GameHub.Register<IWorldChatService>(WorldChatManager.Instance);
            else
                GameLog.Error("[Boot] [GameBootstrap] 世界频道管理器未就绪，IWorldChatService 未注册");

            if (GuideManager.Instance != null)
                GameHub.Register<IGuideService>(GuideManager.Instance);
            else
                GameLog.Error("[Boot] [GameBootstrap] 引导管理器未就绪，IGuideService 未注册");

            if (MmQuestRuntime.Instance != null)
                GameHub.Register<IQuestService>(MmQuestRuntime.Instance);
            else
                GameLog.Error("[Boot] [GameBootstrap] 任务运行时未就绪，IQuestService 未注册");

            if (BattleInviteManager.Instance != null)
                GameHub.Register<IBattleInviteService>(BattleInviteManager.Instance);
            else
                GameLog.Error("[Boot] [GameBootstrap] 对战邀请管理器未就绪，IBattleInviteService 未注册");

            // 存档服务常驻但只在大世界等场景使用，这里主动创建并登记
            SceneSaveService saveService = SceneSaveService.EnsureExists();
            GameHub.Register<ISceneSaveService>(saveService);
        }
    }
}
