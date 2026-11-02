namespace MieMieFrameWork
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using Cysharp.Threading.Tasks;
    using UnityEngine;

    /// <summary>
    /// 组合根：负责把散落的管理器 构造 → 按优先级排序 → 依次 Init → 逆序释放
    /// Inspector 配置见 ModuleHub.Config.cs，注册见 ModuleHub.Register.cs，自举见 ModuleHub.Bootstrap.cs
    /// </summary>
    public partial class ModuleHub : SingletonMono<ModuleHub>
    {
        protected override bool DontDestroyOnLoadEnabled => true;

        /// <summary>管理器实例字典，键为运行时具体类型</summary>
        private readonly Dictionary<Type, IManagerBase> managerDict = new Dictionary<Type, IManagerBase>();

        /// <summary>管理器顺序表，按优先级排好序，Init 与 Dispose 都按它走</summary>
        private readonly List<IManagerBase> managerOrderList = new List<IManagerBase>();

        /// <summary>启动完成信号，失败时带异常</summary>
        private UniTaskCompletionSource readySource;

        private bool isReady;
        private bool bootStarted;

        /// <summary>当前启动阶段（只前进不后退）</summary>
        private BootPhase currentPhase = BootPhase.Resource;

        /// <summary>等待各启动阶段就绪的订阅者</summary>
        private readonly List<(BootPhase Phase, UniTaskCompletionSource Source)> phaseWaiters
            = new List<(BootPhase, UniTaskCompletionSource)>();

        /// <summary>是否已完成启动</summary>
        public bool IsReady => isReady;

        /// <summary>当前启动阶段</summary>
        public BootPhase CurrentPhase => currentPhase;

        #region Unity 生命周期

        protected override void Awake()
        {
            base.Awake();
            if (Instance != this)
                return;

            BootAsync(this.GetCancellationTokenOnDestroy()).Forget();
        }

        protected override void OnDestroy()
        {
            try
            {
                if (Instance == this)
                    CleanupFramework();
            }
            finally
            {
                base.OnDestroy();
            }
        }

        #endregion

        #region 启动

        /// <summary>
        /// 等待启动完成；启动失败时抛出当初的异常（业务侧必须 try/catch）
        /// </summary>
        public async UniTask ReadyAsync()
        {
            if (isReady)
                return;

            readySource ??= new UniTaskCompletionSource();
            await readySource.Task;
        }

        /// <summary>
        /// 等待某个启动阶段就绪：阶段已过则立即返回，不会触发任何初始化。
        /// 注意：本方法只做「观测」，不改变各系统自身的懒初始化时机。
        /// </summary>
        public UniTask WaitPhaseAsync(BootPhase phase, CancellationToken cancellationToken = default)
        {
            if (currentPhase >= phase)
                return UniTask.CompletedTask;

            var Source = new UniTaskCompletionSource();
            phaseWaiters.Add((phase, Source));
            return Source.Task.AttachExternalCancellation(cancellationToken);
        }

        /// <summary>推进启动阶段并唤醒所有不晚于该阶段的等待者</summary>
        private void AdvancePhase(BootPhase phase)
        {
            currentPhase = phase;

            for (int Index = phaseWaiters.Count - 1; Index >= 0; Index--)
            {
                if (phaseWaiters[Index].Phase > phase)
                    continue;

                phaseWaiters[Index].Source.TrySetResult();
                phaseWaiters.RemoveAt(Index);
            }
        }

        /// <summary>启动失败：唤醒所有阶段等待者，避免业务侧死等</summary>
        private void FailPhaseWaiters(Exception exception)
        {
            for (int Index = 0; Index < phaseWaiters.Count; Index++)
                phaseWaiters[Index].Source.TrySetException(exception);

            phaseWaiters.Clear();
        }

        /// <summary>
        /// 启动流水线：资源就绪 → 注册 → 初始化 → 置位 IsReady
        /// </summary>
        private async UniTask BootAsync(System.Threading.CancellationToken cancellationToken)
        {
            if (bootStarted)
                return;
            bootStarted = true;
            readySource = new UniTaskCompletionSource();

            try
            {
                await BootResourceAsync(cancellationToken);
                AdvancePhase(BootPhase.Resource);

                RegisterAllManagers();
                await InitManagersAsync(cancellationToken);
                AdvancePhase(BootPhase.Managers);

                isReady = true;
                readySource.TrySetResult();
                AdvancePhase(BootPhase.Ready);
                FrameLog.Info("框架启动完成", "Module");
            }
            catch (Exception Exception)
            {
                isReady = false;
                readySource.TrySetException(Exception);
                FailPhaseWaiters(Exception);
                FrameLog.Error("框架启动失败：" + Exception, "Module");
            }
        }

        /// <summary>
        /// 初始化已注册的管理器，优先级只决定本阶段内部顺序。
        /// 实现 IAsyncManagerBase 的管理器只走 InitAsync，Init() 不参与（避免同步/异步两条路径重复初始化）。
        /// </summary>
        internal async UniTask InitManagersAsync(CancellationToken cancellationToken)
        {
            foreach (var Manager in managerOrderList)
            {
                try
                {
                    FrameLog.SetContext("InitializingManager", Manager.GetType().FullName);
                    FrameLog.Info($"初始化管理器 {Manager.GetType().FullName}", "Module");

                    if (Manager is IAsyncManagerBase AsyncManager)
                        await AsyncManager.InitAsync(cancellationToken);
                    else
                        Manager.Init();
                }
                catch (Exception Exception)
                {
                    var ManagerType = Manager.GetType();
                    throw new InvalidOperationException($"[ModuleHub] 管理器 {ManagerType.FullName} 初始化失败", Exception);
                }
            }
        }

        /// <summary>
        /// 取管理器：键必须是运行时具体类型，找不到直接抛（不做延迟创建）
        /// </summary>
        public T GetManager<T>() where T : IManagerBase
        {
            if (managerDict.TryGetValue(typeof(T), out var Manager))
            {
                if (Manager is T TypedManager)
                    return TypedManager;

                throw new Exception($"管理器 {typeof(T).Name} 类型不匹配");
            }

            throw new Exception($"管理器 {typeof(T).Name} 不存在");
        }

        /// <summary>
        /// 尝试取管理器：可选模块用，不抛异常
        /// </summary>
        public bool TryGetManager<T>(out T Manager) where T : IManagerBase
        {
            if (managerDict.TryGetValue(typeof(T), out var Found) && Found is T TypedManager)
            {
                Manager = TypedManager;
                return true;
            }

            Manager = default;
            return false;
        }

        #endregion

        #region 释放

        /// <summary>
        /// 逆序释放：先起的后关，单个失败不打断其他管理器，最后汇总抛出
        /// </summary>
        private void CleanupFramework()
        {
            var FailureList = new List<Exception>();

            for (int Index = managerOrderList.Count - 1; Index >= 0; Index--)
            {
                var Manager = managerOrderList[Index];
                if (Manager is IDisposable DisposableManager)
                {
                    try
                    {
                        DisposableManager.Dispose();
                    }
                    catch (Exception Exception)
                    {
                        FailureList.Add(new InvalidOperationException($"[ModuleHub] 管理器 {Manager.GetType().FullName} 释放失败", Exception));
                    }
                }
            }

            managerDict.Clear();
            managerOrderList.Clear();
            isReady = false;

            if (FailureList.Count > 0)
                Debug.LogException(new AggregateException("[ModuleHub] 框架清理失败", FailureList));
        }

        #endregion

        #region 管理器契约

        public interface IManagerBase
        {
            void Init();
        }

        /// <summary>
        /// 异步模块契约：需要 await 才能就绪的管理器（资源装载、配置解析等）实现它。
        /// ModuleHub 对这些管理器只调用 InitAsync，Init() 不参与初始化。
        /// </summary>
        public interface IAsyncManagerBase : IManagerBase
        {
            UniTask InitAsync(CancellationToken cancellationToken);
        }

        /// <summary>
        /// 启动阶段（只前进）：Resource → Managers → Ready。
        /// 用 WaitPhaseAsync 观测，不会触发任何系统的初始化。
        /// </summary>
        public enum BootPhase
        {
            Resource = 0,
            Managers = 1,
            Ready = 2,
        }

        [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
        public sealed class ManagerAttribute : Attribute
        {
            public int Priority { get; }

            public ManagerAttribute(int priority = 0)
            {
                Priority = priority;
            }
        }

        #endregion
    }
}
