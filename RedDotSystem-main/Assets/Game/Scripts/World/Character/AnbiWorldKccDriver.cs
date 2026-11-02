using KinematicCharacterController;
using UnityEngine;

/// <summary>
/// 安比大世界的 KCC 位移驱动（独立脚本，不改动 Player 与状态机）。
///
/// 背景：角色位移原本由 Player.OnAnimatorMove 把动画根运动交给 CharacterController.Move。
/// 接入 KCC 后：
///   · Player 的位移能力在预制体上被清零（moveMult / characterGravity / whatIsGround），
///     状态机、连招、闪避、技能、音频、血量全部保持原样；
///   · 本脚本在 LateUpdate 采样 Animator.deltaPosition（根运动），
///     在 KCC 的物理 tick 里换算成速度交给 KinematicCharacterMotor；
///   · 碰撞、台阶、坡度、重力、贴地由 KCC 负责。
/// </summary>
[RequireComponent(typeof(KinematicCharacterMotor))]
public class AnbiWorldKccDriver : MonoBehaviour, ICharacterController
{
    [Header("KCC 重力")]
    [Tooltip("离地时每秒叠加的重力速度")]
    [SerializeField] private float gravity = 20f;

    [Tooltip("着地时的贴地加速度：只在有根运动时施加，避免静止时被斜坡法线推着滑行")]
    [SerializeField] private float groundedStickSpeed = 18f;

    private KinematicCharacterMotor motor;
    private Animator animator;
    private Player player;

    /// <summary>本帧之前累积的根运动位移（世界空间，米）</summary>
    private Vector3 pendingRootMotion;

    private void Awake()
    {
        motor = GetComponent<KinematicCharacterMotor>();
        // 不挂这一行，Motor 第一个 tick 会空引用
        motor.CharacterController = this;
        animator = GetComponent<Animator>();
        player = GetComponent<Player>();
    }

    private void LateUpdate()
    {
        if (animator == null) return;

        // OnAnimatorMove（Player）在本帧动画更新时读取同一份 deltaPosition 并乘 0 交给 CharacterController，
        // 这里原样再取一次，由 KCC 真正执行位移。LateUpdate 采样才能拿到本帧的动画位移。
        pendingRootMotion += animator.deltaPosition;
    }

    /// <summary>清掉尚未消费的根运动（存档恢复 / 传送后调用，避免落位后又被推一段）</summary>
    public void ResetRootMotion() => pendingRootMotion = Vector3.zero;

    // ───────────────────────── ICharacterController ─────────────────────────

    public void UpdateVelocity(ref Vector3 currentVelocity, float deltaTime)
    {
        Vector3 up = motor.CharacterUp;
        Vector3 rootVelocity = deltaTime > 0f ? pendingRootMotion / deltaTime : Vector3.zero;
        pendingRootMotion = Vector3.zero;

        // 待机动画里也会有毫米级的根运动残留；小于 5cm/s 视为静止，避免角色被推着慢慢漂
        if (rootVelocity.magnitude < 0.05f) rootVelocity = Vector3.zero;

        if (motor.GroundingStatus.IsStableOnGround)
        {
            // 着地：根运动投影到地面切线；贴地分量只在真的在走时施加且乘以 dt。
            // 静止时若仍给一个固定向下的速度，KCC 会把它投影到斜坡法线上变成水平推力，
            // 角色会在台阶/曲面交界处自己转圈滑行（实测 2m/s 绕圈）。
            Vector3 normal = motor.GroundingStatus.GroundNormal;
            Vector3 planar = Vector3.ProjectOnPlane(rootVelocity, normal);
            float stick = rootVelocity.sqrMagnitude > 0.04f ? groundedStickSpeed * deltaTime : 0f;
            currentVelocity = planar + normal * (-stick);
        }
        else
        {
            // 离地：保留 KCC 已累积的竖直速度并继续加重力，水平方向只走根运动
            float vertical = Vector3.Dot(currentVelocity, up) - gravity * deltaTime;
            Vector3 planar = Vector3.ProjectOnPlane(rootVelocity, up);
            currentVelocity = planar + up * vertical;
        }
    }

    public void UpdateRotation(ref Quaternion currentRotation, float deltaTime)
    {
        // KCC 的插值每帧都会用 TransientRotation 覆盖 Transform，状态机直接写 Transform 的朝向会被抹掉；
        // 这里从状态机的共享数据取「相机相对移动方向」，在 KCC 的 tick 内完成同样的平滑转向。
        if (player == null) player = GetComponent<Player>();
        if (player == null || player.MovementStateMachine == null) return;

        PlayerStateReusableData data = player.MovementStateMachine.reusableData;
        Vector3 direction = data.worldMovement;
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f) return;

        Quaternion target = Quaternion.LookRotation(direction, motor.CharacterUp);
        float rotationTime = data.rotationTime;
        float t = rotationTime <= 0f ? 1f : 1f - Mathf.Exp(-deltaTime / rotationTime);
        currentRotation = Quaternion.Slerp(currentRotation, target, t);
    }

    public void BeforeCharacterUpdate(float deltaTime)
    {
    }

    public void PostGroundingUpdate(float deltaTime)
    {
    }

    public void AfterCharacterUpdate(float deltaTime)
    {
    }

    /// <summary>
    /// KCC 默认只忽略自己的 Capsule，会把自己身上的 CharacterController 隐式胶囊、
    /// 武器 HitCollider/WallCollider 当成障碍；这里过滤掉自身子树的全部碰撞体。
    /// </summary>
    public bool IsColliderValidForCollisions(Collider coll)
    {
        if (coll == null) return false;
        return !coll.transform.IsChildOf(motor.Transform);
    }

    public void OnGroundHit(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint,
        ref HitStabilityReport hitStabilityReport)
    {
    }

    public void OnMovementHit(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint,
        ref HitStabilityReport hitStabilityReport)
    {
    }

    public void ProcessHitStabilityReport(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint,
        Vector3 atCharacterPosition, Quaternion atCharacterRotation, ref HitStabilityReport hitStabilityReport)
    {
    }

    public void OnDiscreteCollisionDetected(Collider hitCollider)
    {
    }
}
