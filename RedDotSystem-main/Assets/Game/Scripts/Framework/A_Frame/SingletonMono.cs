namespace MieMieFrameWork
{
    using UnityEngine;

    /// <summary>
    /// Mono 单例基类：Instance 为空时不会自动创建，需要自行挂载或用 GetOrAddComponent
    /// </summary>
    public class SingletonMono<T> : MonoBehaviour where T : SingletonMono<T>
    {
        private static readonly object locked = new object();

        public static T Instance { get; private set; }

        /// <summary>是否跨场景常驻</summary>
        protected virtual bool DontDestroyOnLoadEnabled => false;

        protected virtual void Awake()
        {
            lock (locked)
            {
                if (Instance != null)
                {
                    Destroy(gameObject);
                    return;
                }

                Instance = this as T;

                if (DontDestroyOnLoadEnabled)
                {
                    // 提升到根节点，避免子物体 DontDestroyOnLoad 报错
                    if (transform.parent != null)
                        transform.SetParent(null);
                    DontDestroyOnLoad(gameObject);
                }
            }
        }

        protected virtual void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }
    }

    /// <summary>
    /// 组件获取扩展：有就复用，没有就挂一个
    /// </summary>
    public static class ComponentExtension
    {
        public static T GetOrAddComponent<T>(this Component component) where T : Component
        {
            var Result = component.GetComponent<T>();
            if (Result == null)
                Result = component.gameObject.AddComponent<T>();
            return Result;
        }
    }
}
