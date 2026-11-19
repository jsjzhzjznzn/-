namespace MieMieFrameWork.Business
{
    using System;
    using System.Collections.Generic;
    using UnityEngine;

    /// <summary>
    /// 业务服务标记接口
    /// 各模块公共接口继承它后才能注册到 GameHub
    /// </summary>
    public interface IGameService
    {
    }

    /// <summary>
    /// 游戏业务服务注册表
    /// 跨模块只通过接口定位服务，避免直接依赖实现类
    /// </summary>
    public static class GameHub
    {
        /// <summary>服务字典，键为公共服务接口类型</summary>
        private static readonly Dictionary<Type, IGameService> serviceDict = new Dictionary<Type, IGameService>();

        public static int Count => serviceDict.Count;

        /// <summary>
        /// 注册模块公共服务；同接口重复注册会覆盖
        /// </summary>
        /// <typeparam name="TService">模块公共服务接口</typeparam>
        public static void Register<TService>(TService service)
            where TService : class, IGameService
        {
            Type serviceType = typeof(TService);
            if (!serviceType.IsInterface)
                throw new InvalidOperationException($"服务注册键 {serviceType.FullName} 必须是接口");

            if (serviceType == typeof(IGameService))
                throw new InvalidOperationException("不能直接使用 IGameService 作为服务注册键");

            if (service == null)
                throw new ArgumentNullException(nameof(service));

            serviceDict[serviceType] = service;
        }

        /// <summary>
        /// 获取模块服务；未注册时返回 null，调用方应视为组装错误
        /// </summary>
        public static TService Get<TService>()
            where TService : class, IGameService
        {
            return serviceDict.TryGetValue(typeof(TService), out IGameService service) ? service as TService : null;
        }

        /// <summary>
        /// 尝试获取可选模块服务
        /// </summary>
        public static bool TryGet<TService>(out TService service)
            where TService : class, IGameService
        {
            service = Get<TService>();
            return service != null;
        }

        /// <summary>
        /// 注销指定模块服务
        /// </summary>
        public static void Unregister<TService>()
            where TService : class, IGameService
        {
            serviceDict.Remove(typeof(TService));
        }

        /// <summary>
        /// 清空全部业务服务；框架销毁时调用，普通场景切换不清空
        /// </summary>
        public static void Clear()
        {
            serviceDict.Clear();
        }

        /// <summary>
        /// 兼容关闭域重载的编辑器配置：关闭域重载时静态字典不会重新初始化
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRuntimeServices()
        {
            Clear();
        }
    }
}
