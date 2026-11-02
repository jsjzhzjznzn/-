namespace MieMieFrameWork
{
    using System;
    using UnityEngine;

    /// <summary>
    /// 旧纯 C# 单例基类：无初始化钩子、无状态查询。保留仅为兼容，新代码请用 <see cref="SingletonService{T}"/>。
    /// </summary>
    [Obsolete("[框架收敛] 请改用 SingletonService<T>：语义显式（OnInitialize 钩子 + IsCreated），本类仅保留兼容")]
    public class Singleton<T> where T : Singleton<T>, new()
    {
        private static readonly object locked = new object();

        private static T instance;

        public static T Instance
        {
            get
            {
                lock (locked)
                {
                    if (instance == null)
                        instance = new T();
                    return instance;
                }
            }
        }
    }

    /// <summary>
    /// 纯 C# 服务单例（不需要 MonoBehaviour 的管理器用它）。
    ///
    /// 与旧版差异只有两点，都是补齐而非改变：
    ///   ① 构造后调用 <see cref="OnInitialize"/>，子类可在此建内部状态（旧 SkierFramework.Singleton 已有此行为）；
    ///   ② 提供 <see cref="IsCreated"/>，业务可在"尚未创建"时避免实例化。
    ///
    /// 语义：**访问 Instance 即创建**。需要"不创建也能判断"的场景请用 IsCreated。
    /// </summary>
    public abstract class SingletonService<T> where T : SingletonService<T>, new()
    {
        private static readonly object locked = new object();

        private static T instance;

        private static bool created;

        protected SingletonService()
        {
            // 与 SkierFramework.Singleton 保持一致：构造即调用初始化钩子。
            // 注意：此时子类字段初始化器尚未执行，OnInitialize 内只应赋自身字段，不要读子类的字段初始值。
            OnInitialize();
        }

        /// <summary>全局唯一实例（首次访问时创建）</summary>
        public static T Instance
        {
            get
            {
                lock (locked)
                {
                    if (instance == null)
                    {
                        instance = new T();
                        created = true;
                    }

                    return instance;
                }
            }
        }

        /// <summary>是否已经创建（不会触发创建）</summary>
        public static bool IsCreated
        {
            get
            {
                lock (locked)
                    return created;
            }
        }

        /// <summary>构造后的初始化钩子：子类覆写以建立内部状态</summary>
        public virtual void OnInitialize()
        {
        }
    }

    /// <summary>
    /// MonoBehaviour 单例基类，**两个易错语义必须显式声明**（这正是旧项目里几套基类互相打架的根源）：
    ///   · AutoCreate：取不到实例时是否自动 new 一个（默认 true）
    ///   · Persistent：是否跨场景常驻 DontDestroyOnLoad（默认 true）
    ///
    /// 关闭方式（子类静态构造函数里赋值，不要用 new 隐藏）：
    /// <code>
    /// static RoomState() { AutoCreate = false; }   // 找不到就返回 null，让调用方立刻暴露问题
    /// </code>
    ///
    /// 重复实例的处理：**只销毁组件，不销毁 GameObject**——挂在网络物体上时，销毁整个物体会连带 NetworkObject。
    /// </summary>
    public abstract class SingletonBehaviour<T> : MonoBehaviour where T : SingletonBehaviour<T>
    {
        /// <summary>取不到实例时是否自动新建。子类在静态构造函数里改为 false 即可关闭</summary>
        protected static bool AutoCreate = true;

        /// <summary>是否跨场景常驻</summary>
        protected static bool Persistent = true;

        private static T instance;

        /// <summary>全局唯一实例；AutoCreate 关闭且未挂载时返回 null</summary>
        public static T Instance
        {
            get
            {
                if (instance != null)
                    return instance;

                instance = FindAnyObjectByType<T>();
                if (instance != null)
                    return instance;

                if (!AutoCreate)
                {
                    Debug.LogError($"[Singleton] {typeof(T).Name} 未挂载，且已关闭自动创建");
                    return null;
                }

                GameObject holder = new GameObject(typeof(T).Name);
                instance = holder.AddComponent<T>();   // Awake 内完成注册与常驻
                return instance;
            }
        }

        /// <summary>旧 Battle 单例基类的属性名，保留为别名（避免 25 处调用点改动）</summary>
        public static T MainInstance => Instance;

        protected virtual void Awake()
        {
            if (instance != null && instance != this)
            {
                // 只移除重复组件，不销毁整个 GameObject
                Destroy(this);
                return;
            }

            instance = this as T;

            if (!Persistent)
                return;

            // 提升到根节点：DontDestroyOnLoad 对子物体无效
            if (transform.parent != null)
                transform.SetParent(null);

            DontDestroyOnLoad(gameObject);
        }

        protected virtual void OnDestroy()
        {
            if (instance == this)
                instance = null;
        }
    }
}
