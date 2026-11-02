namespace MieMieFrameWork
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// 按优先级注册运行时管理器：只入表，不初始化
    /// </summary>
    public partial class ModuleHub
    {
        /// <summary>
        /// 构造全部管理器并按优先级排序入表。构造顺序 ≠ 初始化顺序
        /// </summary>
        internal void RegisterAllManagers()
        {
            var ManagerList = new List<IManagerBase>();

            // MonoBehaviour 相关：帧驱动排第一，后面的管理器都要依赖它
            var FrameDriver = this.GetOrAddComponent<MonoManager>();
            ManagerList.Add(FrameDriver);

            // 纯 C# 相关组件
            if (audioManagerConfig == null)
                throw new InvalidOperationException("[ModuleHub] FrameRoot 未配置 AudioManagerConfig");
            ManagerList.Add(new AudioManager(audioManagerConfig, transform, FrameDriver));

            // 输入缓冲：挂在框架根节点上，由 MonoManager 每帧驱动
            ManagerList.Add(this.GetOrAddComponent<MieMieFrameWork.M_InputSystem.InputBuffer3D>());

            foreach (var Manager in ManagerList.OrderBy(GetManagerPriority))
            {
                var ManagerType = Manager.GetType();
                if (managerDict.ContainsKey(ManagerType))
                    throw new InvalidOperationException($"[ModuleHub] 重复管理器类型 {ManagerType.FullName}");

                managerDict.Add(ManagerType, Manager);
                managerOrderList.Add(Manager);
            }
        }

        /// <summary>
        /// 读取 ManagerAttribute 上的优先级，没标就是 0（排最前）
        /// </summary>
        private static int GetManagerPriority(IManagerBase manager)
        {
            var ManagerType = manager.GetType();
            var Attribute = (ManagerAttribute)System.Attribute.GetCustomAttribute(ManagerType, typeof(ManagerAttribute));
            return Attribute?.Priority ?? 0;
        }
    }
}
