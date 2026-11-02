using UnityEngine;

namespace KinematicCharacterController
{
    public interface IMoverController
    {
        /// <summary>
        /// 由你来告诉 PhysicsMover 它现在应该处于的位姿时调用
        /// </summary>
        void UpdateMovement(out Vector3 goalPosition, out Quaternion goalRotation, float deltaTime);
    }
}