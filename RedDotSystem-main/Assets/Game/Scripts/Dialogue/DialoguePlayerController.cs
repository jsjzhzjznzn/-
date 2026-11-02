using KinematicCharacterController;
using UnityEngine;

/// <summary>
/// 对话场景玩家控制器：KinematicCharacterController（KCC）的最小实现。
///
/// 只做探索移动：相机相对 WASD、贴地、重力、平滑转身；本场景不做跳跃。
/// 组装：根节点（Scale 必须 1,1,1）上挂 CapsuleCollider + KinematicCharacterMotor + 本脚本，
/// 模型作为子物体（自带 Animator/Avatar）。
/// 对话与暂停时由外部置 <see cref="InputLocked"/>（重力仍然生效，不会浮空）。
/// </summary>
[RequireComponent(typeof(KinematicCharacterMotor))]
public class DialoguePlayerController : MonoBehaviour, ICharacterController
{
    [Header("引用")]
    [Tooltip("把输入转成相机相对方向；留空则用 Camera.main")]
    public Transform CameraTransform;

    [Header("地面")]
    public float MoveSpeed = 3.2f;
    public float GroundAcceleration = 45f;
    [Tooltip("贴地力：朝地面压的加速度，防止上坡/下坡被弹飞或腾空")]
    public float StickToGroundForce = 18f;

    [Header("空中")]
    public float AirAcceleration = 18f;
    public float Gravity = 22f;

    [Header("转身 / 动画")]
    [Tooltip("越大转得越快，8~15 之间比较自然")]
    public float RotationSharpness = 12f;
    [Tooltip("动画 Speed 参数向目标值收敛的速度")]
    public float AnimSpeedDamp = 8f;
    public string AnimSpeedParam = "Speed";

    private KinematicCharacterMotor motor;
    private Animator animator;
    private Camera cachedCamera;
    private bool hasSpeedParam;
    private Vector3 moveInput;
    private float animSpeed;

    /// <summary>对话 / 暂停期间锁输入（重力仍然生效）</summary>
    public bool InputLocked { get; set; }

    /// <summary>测试钩子：非空时用它替代键盘输入（长度 0~1，x=右 y=前）</summary>
    public Vector2? TestMoveInput { get; set; }

    public KinematicCharacterMotor Motor => motor;
    public Vector3 MoveInput => moveInput;
    public float AnimSpeed => animSpeed;

    private void Awake()
    {
        motor = GetComponent<KinematicCharacterMotor>();
        // 不挂这一行，Motor 第一个 tick 就会空引用
        motor.CharacterController = this;

        animator = GetComponentInChildren<Animator>(true);
        hasSpeedParam = animator != null &&
                        System.Array.Exists(animator.parameters, p => p.name == AnimSpeedParam);
    }

    private void Update()
    {
        // 输入必须在 Update 采、在 UpdateVelocity 用：
        // UpdateVelocity 跑在 FixedUpdate 里，一帧可能跑 0 次或 2 次，直接读 Input 会漏键或重复
        Vector2 input = InputLocked ? Vector2.zero : ReadInput();
        Transform cam = ResolveCamera();

        if (cam != null)
        {
            Vector3 forward = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized;
            Vector3 right = Vector3.ProjectOnPlane(cam.right, Vector3.up).normalized;
            moveInput = forward * input.y + right * input.x;
        }
        else
        {
            moveInput = new Vector3(input.x, 0f, input.y);
        }

        if (moveInput.sqrMagnitude > 1f) moveInput.Normalize();

        // 动画只需"有没有在走"，用输入意图而不是真实速度更稳（被墙挡住时不会一直跑）
        float targetSpeed = InputLocked ? 0f : moveInput.magnitude;
        animSpeed = Mathf.MoveTowards(animSpeed, targetSpeed, AnimSpeedDamp * Time.deltaTime);
        if (animator != null && hasSpeedParam) animator.SetFloat(AnimSpeedParam, animSpeed);
    }

    private Vector2 ReadInput()
    {
        if (TestMoveInput.HasValue) return TestMoveInput.Value;
        return new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
    }

    /// <summary>
    /// 每帧用到的相机：显式引用优先，否则缓存 Camera.main。
    /// Camera.main 是带 Tag 的全场景查找，结果失效后只在下次访问时重查，所以缓存对象丢失时再取一次。
    /// </summary>
    private Transform ResolveCamera()
    {
        if (CameraTransform != null) return CameraTransform;
        if (cachedCamera != null) return cachedCamera.transform;

        cachedCamera = Camera.main;
        return cachedCamera != null ? cachedCamera.transform : null;
    }

    // ───────────────────────── ICharacterController ─────────────────────────

    public void BeforeCharacterUpdate(float deltaTime)
    {
        // 本 tick 尚未做任何事
    }

    public void PostGroundingUpdate(float deltaTime)
    {
        // 地面探测已完成（落地音效等可挂这里）
    }

    public void UpdateVelocity(ref Vector3 currentVelocity, float deltaTime)
    {
        if (motor.GroundingStatus.IsStableOnGround)
        {
            Vector3 n = motor.GroundingStatus.GroundNormal;

            // 1) 把现有速度转到地面切向上，避免沿坡走时"往下插"进地面
            currentVelocity = motor.GetDirectionTangentToSurface(currentVelocity, n) * currentVelocity.magnitude;

            // 2) 目标速度：输入方向按地面法线贴到坡面上（上坡不会明显变慢）
            Vector3 inputRight = Vector3.Cross(moveInput, motor.CharacterUp);
            Vector3 targetDir = Vector3.Cross(n, inputRight).normalized;
            Vector3 targetVel = targetDir * (MoveSpeed * moveInput.magnitude);

            // 3) 水平速度向目标靠拢
            Vector3 planar = Vector3.ProjectOnPlane(currentVelocity, n);
            planar = Vector3.MoveTowards(planar, targetVel, GroundAcceleration * deltaTime);

            // 4) 贴地：垫一点朝地面的分量，保持地面判定稳定。
            //    注意：只在"有移动意图"时施加。KCC 在着地后会把速度投影到水平面并保留模长，
            //    静止时这个纯竖直分量会被转成水平速度 → 角色自己缓慢滑行（本项目实测 0.36m/s 自走）。
            currentVelocity = planar;
            if (moveInput.sqrMagnitude > 0.0001f)
                currentVelocity += n * (-StickToGroundForce * deltaTime);
        }
        else
        {
            Vector3 up = motor.CharacterUp;
            Vector3 planar = Vector3.ProjectOnPlane(currentVelocity, up);
            planar = Vector3.MoveTowards(planar, moveInput * MoveSpeed, AirAcceleration * deltaTime);

            float vertical = Vector3.Dot(currentVelocity, up) - Gravity * deltaTime;
            currentVelocity = planar + up * vertical;
        }
    }

    public void UpdateRotation(ref Quaternion currentRotation, float deltaTime)
    {
        // 只朝移动方向转；没有输入就保持当前朝向
        if (moveInput.sqrMagnitude > 0.0001f)
        {
            Quaternion target = Quaternion.LookRotation(moveInput, currentRotation * Vector3.up);
            float t = 1f - Mathf.Exp(-RotationSharpness * deltaTime);
            currentRotation = Quaternion.Slerp(currentRotation, target, t);
        }
    }

    public void AfterCharacterUpdate(float deltaTime)
    {
    }

    public bool IsColliderValidForCollisions(Collider coll)
    {
        return true;
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
