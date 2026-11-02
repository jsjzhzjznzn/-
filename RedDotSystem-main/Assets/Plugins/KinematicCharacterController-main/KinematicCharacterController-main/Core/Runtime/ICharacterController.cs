using UnityEngine;

namespace KinematicCharacterController
{
    public interface ICharacterController
    {
        /// <summary>
        /// Motor 想知道它当前的旋转应该是多少时调用
        /// </summary>
        void UpdateRotation(ref Quaternion currentRotation, float deltaTime);
        /// <summary>
        /// Motor 想知道它当前的速度应该是多少时调用
        /// </summary>
        void UpdateVelocity(ref Vector3 currentVelocity, float deltaTime);
        /// <summary>
        /// 在 Motor 做任何事情之前调用
        /// </summary>
        void BeforeCharacterUpdate(float deltaTime);
        /// <summary>
        /// Motor 完成地面探测后调用，但早于 PhysicsMover / 速度等处理
        /// </summary>
        void PostGroundingUpdate(float deltaTime);
        /// <summary>
        /// Motor 完成本 tick 的全部更新后调用
        /// </summary>
        void AfterCharacterUpdate(float deltaTime);
        /// <summary>
        /// Motor 想知道某个碰撞体是否可以与之碰撞时调用（或者是否直接穿过它）
        /// </summary>
        bool IsColliderValidForCollisions(Collider coll);
        /// <summary>
        /// Motor 的地面探测命中地面时调用
        /// </summary>
        void OnGroundHit(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint, ref HitStabilityReport hitStabilityReport);
        /// <summary>
        /// Motor 的移动逻辑命中碰撞体时调用
        /// </summary>
        void OnMovementHit(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint, ref HitStabilityReport hitStabilityReport);
        /// <summary>
        /// 每次移动命中后调用，让你有机会按自己的需要修改 HitStabilityReport
        /// </summary>
        void ProcessHitStabilityReport(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint, Vector3 atCharacterPosition, Quaternion atCharacterRotation, ref HitStabilityReport hitStabilityReport);
        /// <summary>
        /// 角色检测到离散碰撞时调用（即不是由移动时的 capsuleCasts 产生的碰撞）
        /// </summary>
        void OnDiscreteCollisionDetected(Collider hitCollider);
    }
}