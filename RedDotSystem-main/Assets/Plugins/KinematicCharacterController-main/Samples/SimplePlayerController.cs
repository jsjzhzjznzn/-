using UnityEngine;
using KinematicCharacterController;

/// <summary>
/// 最小可跑的玩家 Controller：WASD 相对相机移动、重力、空格跳跃、转身平滑。
/// 它是 ICharacterController 的一份最小实现，用来补上官方示例被裁掉的那一层。
///
/// 组装方式：
///   Player（根节点，Scale 必须是 1,1,1）
///     ├─ CapsuleCollider        Motor 的 [RequireComponent] 会要求它
///     ├─ KinematicCharacterMotor（Capsule Radius/Height、MaxStableSlopeAngle 在 Inspector 调）
///     └─ SimplePlayerController （本脚本，CameraTransform 拖主相机；留空则用 Camera.main）
///
/// 注意：不要给这个根节点挂 Rigidbody —— Motor 靠直接写 Transform 移动。
/// </summary>
[RequireComponent(typeof(KinematicCharacterMotor))]
public class SimplePlayerController : MonoBehaviour, ICharacterController
{
    [Header("引用")]
    [Tooltip("用于把输入转成相机相对方向，留空则用 Camera.main")]
    public Transform CameraTransform;

    [Header("地面")]
    public float MoveSpeed = 6f;
    public float GroundAcceleration = 60f;
    [Tooltip("贴地力：朝地面压的加速度，防止上坡/下坡时被弹飞或腾空")]
    public float StickToGroundForce = 20f;

    [Header("空中")]
    public float AirAcceleration = 20f;
    public float Gravity = 25f;

    [Header("跳跃")]
    public float JumpSpeed = 8f;

    [Header("转身")]
    [Tooltip("越大转得越快，8~15 之间比较自然")]
    public float RotationSharpness = 12f;

    private KinematicCharacterMotor _motor;
    private Camera _mainCamera;

    // 意图：每帧在 Update 里采集，在 FixedUpdate 的 UpdateVelocity 里消费
    private Vector3 _moveInput;        // 世界空间、已归一化（长度 0~1）
    private bool _jumpRequested;

    private void Awake()
    {
        _motor = GetComponent<KinematicCharacterMotor>();
        // 关键：不挂这一行，Motor 第一个 tick 就会空引用
        _motor.CharacterController = this;

        // Camera.main 是带 Tag 的全场景查找，缓存一次；对象销毁后自动判空，Update 里再取一次
        _mainCamera = Camera.main;
    }

    private void Update()
    {
        // 输入必须在 Update 采、在 UpdateVelocity 用：
        // UpdateVelocity 跑在 FixedUpdate 里，一帧可能跑 0 次或 2 次，直接读 Input 会漏键或重复
        // CameraTransform 留空时用缓存的主相机；主相机被销毁重建（切场景）时缓存会变成 null，这里补取一次
        if (!CameraTransform && _mainCamera == null) _mainCamera = Camera.main;
        Transform cam = CameraTransform ? CameraTransform : (_mainCamera ? _mainCamera.transform : null);

        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");

        if (cam)
        {
            Vector3 forward = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized;
            Vector3 right   = Vector3.ProjectOnPlane(cam.right,   Vector3.up).normalized;
            _moveInput = forward * v + right * h;
        }
        else
        {
            _moveInput = new Vector3(h, 0f, v);
        }

        // 斜向输入不加速
        if (_moveInput.sqrMagnitude > 1f) _moveInput.Normalize();

        if (Input.GetButtonDown("Jump")) _jumpRequested = true;
    }

    // ───────────────────────── ICharacterController ─────────────────────────

    public void BeforeCharacterUpdate(float deltaTime)
    {
        // 本 tick 尚未做任何事。适合推进状态机、清理每帧标志
    }

    public void PostGroundingUpdate(float deltaTime)
    {
        // 地面探测已完成：落地/离地事件可以在这里派发（音效、动画重置）
    }

    public void UpdateVelocity(ref Vector3 currentVelocity, float deltaTime)
    {
        if (_motor.GroundingStatus.IsStableOnGround)
        {
            Vector3 n = _motor.GroundingStatus.GroundNormal;

            // 1) 把现有速度转到地面切向上，避免沿坡走时"往下插"进地面
            currentVelocity = _motor.GetDirectionTangentToSurface(currentVelocity, n) * currentVelocity.magnitude;

            // 2) 目标速度：把输入方向按地面法线贴合到坡面上（这样上坡不会变慢太多）
            Vector3 inputRight = Vector3.Cross(_moveInput, _motor.CharacterUp);
            Vector3 targetDir  = Vector3.Cross(n, inputRight).normalized;
            Vector3 targetVel  = targetDir * (MoveSpeed * _moveInput.magnitude);

            // 3) 水平速度向目标靠拢
            Vector3 planar = Vector3.ProjectOnPlane(currentVelocity, n);
            planar = Vector3.MoveTowards(planar, targetVel, GroundAcceleration * deltaTime);

            // 4) 贴地：垫一点朝地面的分量，保持地面判定稳定
            currentVelocity = planar + n * (-StickToGroundForce * deltaTime);

            // 5) 起跳：给一个向上的冲量，并临时解除贴地，否则下一 tick 会被吸回去
            if (_jumpRequested)
            {
                currentVelocity += _motor.CharacterUp * JumpSpeed;
                _motor.ForceUnground();
            }
        }
        else
        {
            // 空中：水平方向仍可微调，竖直方向只受重力
            Vector3 up = _motor.CharacterUp;
            Vector3 planar = Vector3.ProjectOnPlane(currentVelocity, up);
            planar = Vector3.MoveTowards(planar, _moveInput * MoveSpeed, AirAcceleration * deltaTime);

            float vertical = Vector3.Dot(currentVelocity, up) - Gravity * deltaTime;
            currentVelocity = planar + up * vertical;
        }
    }

    public void UpdateRotation(ref Quaternion currentRotation, float deltaTime)
    {
        // 只朝移动方向转；没有输入就保持当前朝向
        if (_moveInput.sqrMagnitude > 0.0001f)
        {
            // 用角色当前的上方向构造目标旋转，这样在斜坡上也不会歪
            Quaternion target = Quaternion.LookRotation(_moveInput, currentRotation * Vector3.up);
            float t = 1f - Mathf.Exp(-RotationSharpness * deltaTime);   // 与帧率无关的平滑系数
            currentRotation = Quaternion.Slerp(currentRotation, target, t);
        }
    }

    public void AfterCharacterUpdate(float deltaTime)
    {
        // 本 tick 结束：清掉一次性意图，避免长按持续触发
        // （真实项目里这里更适合做跳跃缓冲，把输入保留几帧）
        _jumpRequested = false;
    }

    public bool IsColliderValidForCollisions(Collider coll)
    {
        // 调用极频繁（每次 sweep/overlap 都会问），保持廉价、不要 GetComponent
        // 常见用途：排除其他角色、排除触发器、排除某个 Layer
        return true;
    }

    public void OnGroundHit(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint, ref HitStabilityReport hitStabilityReport)
    {
        // 想在"踩到的地面"层面做文章时用，例如冰面打滑：
        // if (hitCollider.CompareTag("Ice")) hitStabilityReport.IsStable = false;
    }

    public void OnMovementHit(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint, ref HitStabilityReport hitStabilityReport)
    {
        // 移动中撞到东西。适合做撞墙音效、贴墙滑行的额外处理
    }

    public void ProcessHitStabilityReport(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint,
        Vector3 atCharacterPosition, Quaternion atCharacterRotation, ref HitStabilityReport hitStabilityReport)
    {
        // 最后一次否决稳定性的机会：把某类表面强制判为"站不住"
    }

    public void OnDiscreteCollisionDetected(Collider hitCollider)
    {
        // 不是 sweep 撞的，而是检测到已经嵌进去了（卡墙/卡地面）。
        // 想兜底就把角色往外推，这里先留空
    }
}
