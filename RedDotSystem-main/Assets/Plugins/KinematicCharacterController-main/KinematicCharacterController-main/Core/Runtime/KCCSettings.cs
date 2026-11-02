using UnityEngine;

namespace KinematicCharacterController
{
    [CreateAssetMenu]
    public class KCCSettings : ScriptableObject
    {
        /// <summary>
        /// 决定系统是否自动模拟。
        /// 若为 true，模拟在 FixedUpdate 中进行
        /// </summary>
        [Tooltip("Determines if the system simulates automatically. If true, the simulation is done on FixedUpdate")]
        public bool AutoSimulation = true;
        /// <summary>
        /// 是否由系统统一处理角色与 PhysicsMover 的插值
        /// </summary>
        [Tooltip("Should interpolation of characters and PhysicsMovers be handled")]
        public bool Interpolate = true;
        /// <summary>
        /// 系统 Motor 列表的初始容量（需要时会自动扩容，但把初始容量设大有助于避免 GC 分配）
        /// </summary>
        [Tooltip("Initial capacity of the system's list of Motors (will resize automatically if needed, but setting a high initial capacity can help preventing GC allocs)")]
        public int MotorsListInitialCapacity = 100;
        /// <summary>
        /// 系统 Mover 列表的初始容量（需要时会自动扩容，但把初始容量设大有助于避免 GC 分配）
        /// </summary>
        [Tooltip("Initial capacity of the system's list of Movers (will resize automatically if needed, but setting a high initial capacity can help preventing GC allocs)")]
        public int MoversListInitialCapacity = 100;
    }
}