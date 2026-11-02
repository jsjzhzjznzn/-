using System;
using System.Collections.Generic;
using UnityEngine;

namespace KinematicCharacterController
{
    // 角色与"非运动学刚体"的交互模型：
    //   None            —— 不与动态刚体做速度交换（只挡路，不推）
    //   Kinematic       —— 角色以无限大质量推动刚体：它走它的，箱子被它撞开，自己速度几乎不变
    //   SimulatedDynamic—— 用 SimulatedCharacterMass 参与动量分配：角色也会被箱子减速/顶开
    public enum RigidbodyInteractionType
    {
        None,
        Kinematic,
        SimulatedDynamic
    }

    // 台阶处理强度：
    //   None     —— 完全不管台阶（性能最好，但台阶上地面判定会错）
    //   Standard —— 标准检测：从命中点上方往下打一条独立 sweep，能识别常见台阶
    //   Extra    —— 额外允许踩上"比胶囊半径还窄"的台阶（多一次 sweep，更贵）
    public enum StepHandlingMethod
    {
        None,
        Standard,
        Extra
    }

    // 一次移动解算中，速度投影所处的状态。它决定"连续撞到两个面"时怎么处理：
    //   Initial              —— 还没撞过东西
    //   AfterFirstHit        —— 撞过一次，再撞就可能是凹角（crease），需要判断能否沿交线滑
    //   FoundBlockingCrease  —— 已确认卡在凹角，速度被约束到交线方向
    //   FoundBlockingCorner  —— 彻底被夹角卡死，速度清零（防止在墙角里抖动/穿模）
    public enum MovementSweepState
    {
        Initial,
        AfterFirstHit,
        FoundBlockingCrease,
        FoundBlockingCorner,
    }

    /// <summary>
    /// 表示与模拟相关的角色 Motor 的完整状态。
    /// 用于保存状态或回退到过去的状态。
    /// </summary>
    [Serializable]
    // 一次模拟的完整快照：位置/速度/接地/附着刚体全在里面。
    // 有了它就能做"存一帧、回滚、网络重放"，而不需要重新跑一遍物理。
    public struct KinematicCharacterMotorState
    {
        // 逻辑真值位姿（就是当时的 TransientPosition/Rotation，不是 Transform）。
        public Vector3 Position;
        public Quaternion Rotation;
        // Controller 写进去的速度（不含平台携带的那部分）。
        public Vector3 BaseVelocity;

        // 是否处在"强制离地"状态，以及该状态还要持续多久（秒）。
        public bool MustUnground;
        public float MustUngroundTime;
        // 上次移动扫掠中是否碰到过可站立的面。下一 tick 用它决定地面探测距离要不要加长。
        public bool LastMovementIterationFoundAnyGround;
        // 接地报告（轻量版，不带 Collider 引用，因此可以安全序列化）。
        public CharacterTransientGroundingReport GroundingStatus;

        // 当前附着的刚体（站在移动平台上时就是它）。
        public Rigidbody AttachedRigidbody;
        // 由该刚体带给角色的速度（跟着平台走的那份）。
        public Vector3 AttachedRigidbodyVelocity;
    }

    // 一条"角色被推到别处"的记录：解重叠时产生，事后用于报告与刚体交互。
    /// <summary>
    /// 描述角色胶囊体与另一个碰撞体之间的重叠。
    /// </summary>
    public struct OverlapResult
    {
        // 把角色推出去的方向（已经过 GetObstructionNormal 处理，是"阻挡法线"而不是原始命中法线）。
        public Vector3 Normal;
        // 与之重叠的那个碰撞体。
        public Collider Collider;

        public OverlapResult(Vector3 normal, Collider collider)
        {
            Normal = normal;
            Collider = collider;
        }
    }

    // 完整接地信息（比 Transient 版多两个字段：命中的碰撞体与命中点）。
    // Transient 版是"跨 tick 保留的轻量版"，这个版是"本 tick 的详细版"。
    // 两者互相 CopyFrom，目的都是避免每次重新探测。
    /// <summary>
    /// 包含 Motor 接地状态的全部信息。
    /// </summary>
    public struct CharacterGroundingReport
    {
        // 向下探测碰到东西了 —— 注意它不代表"能站"（踩到 80° 的陡坡也是 true）。
        public bool FoundAnyGround;
        // 碰到的东西可以站：坡度在 MaxStableSlopeAngle 内，且通过边缘/台阶/特殊规则判定。
        public bool IsStableOnGround;
        // 能站，但被特殊规则否决了"吸附"（悬崖边缘、坡面突变）。
        // 此时 IsStableOnGround 仍为 true —— 逻辑上算站在地上，只是身体不往下贴。
        public bool SnappingPrevented;
        // 实际命中的表面法线。
        public Vector3 GroundNormal;
        // 命中点"内侧"（朝角色移动方向一侧）再打一条竖直射线得到的法线，用于识别边缘。
        // 没探到时等于 GroundNormal。
        public Vector3 InnerGroundNormal;
        // 命中点"外侧"（背向移动方向一侧）的法线。内外两侧稳定性不同 → 判定为悬崖边。
        public Vector3 OuterGroundNormal;

        // 踩到的碰撞体。找移动平台/可交互刚体就靠它（GroundCollider.attachedRigidbody）。
        public Collider GroundCollider;
        // 命中点的世界坐标。
        public Vector3 GroundPoint;

        public void CopyFrom(CharacterTransientGroundingReport transientGroundingReport)
        {
            FoundAnyGround      = transientGroundingReport.FoundAnyGround;
            IsStableOnGround    = transientGroundingReport.IsStableOnGround;
            SnappingPrevented   = transientGroundingReport.SnappingPrevented;
            GroundNormal        = transientGroundingReport.GroundNormal;
            InnerGroundNormal   = transientGroundingReport.InnerGroundNormal;
            OuterGroundNormal   = transientGroundingReport.OuterGroundNormal;

            GroundCollider = null;
            GroundPoint = Vector3.zero;
        }
    }

    // 轻量接地信息：只保留"是否着地/法线"这些跨 tick 需要的结论，
    // 不带 Collider 引用（引用会阻止 GC，也不适合做状态快照）。
    /// <summary>
    /// 包含与模拟相关的 Motor 接地状态信息。
    /// </summary>
    public struct CharacterTransientGroundingReport
    {
        // 以下六个字段与 CharacterGroundingReport 同名同义，只是不再携带 Collider/GroundPoint。
        // 去掉引用是为了让结构体可以安全地做快照、序列化、跨 tick 保存。
        public bool FoundAnyGround;
        public bool IsStableOnGround;
        public bool SnappingPrevented;
        public Vector3 GroundNormal;
        public Vector3 InnerGroundNormal;
        public Vector3 OuterGroundNormal;

        public void CopyFrom(CharacterGroundingReport groundingReport)
        {
            FoundAnyGround      = groundingReport.FoundAnyGround;
            IsStableOnGround    = groundingReport.IsStableOnGround;
            SnappingPrevented   = groundingReport.SnappingPrevented;
            GroundNormal        = groundingReport.GroundNormal;
            InnerGroundNormal   = groundingReport.InnerGroundNormal;
            OuterGroundNormal   = groundingReport.OuterGroundNormal;
        }
    }

    // 一次命中的"稳定性评估"结果。它是 KCC 判定"能不能站、能不能走"的唯一依据，
    // 分四层填充：基础坡度判定 → 边缘检测 → 台阶检测 → 交给你的 Controller 否决。
    // 注意这些字段都是 ref 传出去的，OnGroundHit / ProcessHitStabilityReport 里可以改写。
    /// <summary>
    /// 包含一次命中稳定性评估的全部信息。
    /// </summary>
    public struct HitStabilityReport
    {
        // 最终结论：这一脚踩得稳不稳。所有上层逻辑只读这一个字段。
        public bool IsStable;

        // 内侧辅助探测：是否探到、探到的法线。
        public bool FoundInnerNormal;
        public Vector3 InnerNormal;
        // 外侧辅助探测：同上。
        public bool FoundOuterNormal;
        public Vector3 OuterNormal;

        // 这次命中其实是可以踩上去的台阶（抬脚 MaxStepHeight 内有落脚点）。
        public bool ValidStepDetected;
        // 被判定为台阶的那个碰撞体。InternalCharacterMove 用它精确定位落点。
        public Collider SteppedCollider;

        // 是否位于悬崖边缘（内外两侧一个能站、一个不能站）。
        public bool LedgeDetected;
        // 角色此刻站在"空的那一侧"（再往前就掉下去）。
        public bool IsOnEmptySideOfLedge;
        // 命中点到边缘的水平距离。
        public float DistanceFromLedge;
        // 速度方向是否指向空侧（决定"是走过去掉下去"还是"只是蹭到边"）。
        public bool IsMovingTowardsEmptySideOfLedge;
        // 边缘处"能站的那一侧"的地面法线。
        public Vector3 LedgeGroundNormal;
        // 沿边缘走向的方向（右手方向），用于构造朝向。
        public Vector3 LedgeRightDirection;
        // 从边缘指向空侧的水平方向。速度投影到它上面就能判断"掉的趋势有多强"。
        public Vector3 LedgeFacingDirection;
    }

    // 移动过程中撞到刚体时先记下来，等移动结束后统一结算（避免边算边改刚体状态）。
    // 结算在 ProcessVelocityForRigidbodyHits 里完成。
    /// <summary>
    /// 包含移动阶段中命中的刚体信息，以便之后统一处理
    /// </summary>
    public struct RigidbodyProjectionHit
    {
        // 被撞到的刚体（箱子、平台、另一个角色……）。
        public Rigidbody Rigidbody;
        // 命中点世界坐标。施加冲力时用它做 AddForceAtPosition 的作用点，
        // 打在边缘上会产生旋转，更真实。
        public Vector3 HitPoint;
        // 处理后的阻挡法线（可能已被转成"沿地面方向"），结算动量时用它而不是原始 hit.normal。
        public Vector3 EffectiveHitNormal;
        // 撞击发生那一瞬间角色的速度，用于计算相对冲击速度。
        public Vector3 HitVelocity;
        // 这次命中是否可站立（决定角色是"被挡住"还是"踩上去"）。
        public bool StableOnHit;
    }

    /// <summary>
    /// 负责角色碰撞与移动解算的组件
    /// </summary>
    [RequireComponent(typeof(CapsuleCollider))]
    // ─────────────────────────────────────────────────────────────
    // KCC 的"引擎"部分：只管怎么动，不管想怎么动。
    //
    // 每 tick 由 KinematicCharacterSystem 调用两次：
    //   UpdatePhase1  探测（地面、重叠、平台速度）—— 不产生位移，但会因"被平台携带"而挪动
    //   UpdatePhase2  解算（旋转、速度、位移、刚体反作用）—— 真正决定走到哪
    //
    // 内部只有三种数据：
    //   位姿  _transientPosition / _transientRotation（逻辑真值，Transform 只是它的投影）
    //   速度  BaseVelocity（你的 Controller 每 tick 重写）+ _attachedRigidbodyVelocity（平台给的）
    //   状态  GroundingStatus / _overlaps / _rigidbodyProjectionHits
    //
    // 它本身不是 PhysX 的 body，也不会被物理力推动；所有碰撞都是"查询"出来的。
    // ─────────────────────────────────────────────────────────────
    public class KinematicCharacterMotor : MonoBehaviour
    {
#pragma warning disable 0414
        /// <summary>
        /// 该 Motor 的胶囊碰撞体。
        /// </summary>
        [Header("Components")]
        [ReadOnlyKCC]
        public CapsuleCollider Capsule;

        
        /// <summary>
        /// 角色胶囊体的半径。
        /// </summary>
        [Header("Capsule Settings")]
        [SerializeField]
        [Tooltip("Radius of the Character Capsule")]
        private float CapsuleRadius = 0.5f;
        
        /// <summary>
        /// 角色胶囊体的高度。
        /// </summary>
        [SerializeField]
        [Tooltip("Height of the Character Capsule")]
        private float CapsuleHeight = 2f;
        
        /// <summary>
        /// 角色胶囊体中心的局部 y 坐标。
        /// </summary>
        [SerializeField]
        [Tooltip("Height of the Character Capsule")]
        private float CapsuleYOffset = 1f;
        
        /// <summary>
        /// 角色胶囊体的物理材质。
        /// </summary>
        [SerializeField]
        [Tooltip("Physics material of the Character Capsule (Does not affect character movement. Only affects things colliding with it)")]
#pragma warning disable 0649
        private PhysicMaterial CapsulePhysicsMaterial;
#pragma warning restore 0649


        /// <summary>
        /// 增大地面探测范围，以便在极高速度下也能吸附到地面。
        /// </summary>
        [Header("Grounding settings")]
        [Tooltip("Increases the range of ground detection, to allow snapping to ground at very high speeds")]
        public float GroundDetectionExtraDistance;
        
        /// <summary>
        /// 角色能够稳定站立的最高坡度角。
        /// </summary>
        [Range(0f, 89f)]
        [Tooltip("Maximum slope angle on which the character can be stable")]
        public float MaxStableSlopeAngle = 60f;
        
        /// <summary>
        /// 角色在哪些层上可以被判定为稳定。
        /// </summary>
        [Tooltip("Which layers can the character be considered stable on")]
        public LayerMask StableGroundLayers = -1;
        
        /// <summary>
        /// 检测到离散碰撞时通知 Character Controller。
        /// </summary>
        [Tooltip("Notifies the Character Controller when discrete collisions are detected")]
        public bool DiscreteCollisionEvents;


        /// <summary>
        /// 正确检测台阶上的接地状态，但有性能开销。
        /// </summary>
        [Header("Step settings")]
        [Tooltip("Handles properly detecting grounding status on steps, but has a performance cost.")]
        public StepHandlingMethod StepHandling = StepHandlingMethod.Standard;
        
        /// <summary>
        /// 角色可以攀爬的台阶的最大高度。
        /// </summary>
        [Tooltip("Maximum height of a step which the character can climb")]
        public float MaxStepHeight = 0.5f;
        
        /// <summary>
        /// 即使当前不稳定，角色是否也能踏上障碍物？
        /// </summary>
        [Tooltip("Can the character step up obstacles even if it is not currently stable?")]
        public bool AllowSteppingWithoutStableGrounding;
        
        /// <summary>
        /// 角色可以踩上的台阶的最小长度（用于 Extra 台阶模式）。
        /// 用它让角色能踩上比自身半径更小的台阶。
        /// </summary>
        [Tooltip("Minimum length of a step that the character can step on (used in Extra stepping method). " +
                 "Use this to let the character step on steps that are smaller that its radius")]
        public float MinRequiredStepDepth = 0.1f;


        /// <summary>
        /// 正确检测边缘信息与接地状态，但有性能开销。
        /// </summary>
        [Header("Ledge settings")]
        [Tooltip("Handles properly detecting ledge information and grounding status, but has a performance cost.")]
        public bool LedgeAndDenivelationHandling = true;
        
        /// <summary>
        /// 角色站在边缘上仍能保持稳定时，离胶囊体中轴的最大距离。
        /// </summary>
        [Tooltip("The distance from the capsule central axis at which the character can stand on a ledge and still be stable")]
        public float MaxStableDistanceFromLedge = 0.5f;
        
        /// <summary>
        /// 速度超过一定值时，阻止向地面吸附。
        /// </summary>
        [Tooltip("Prevents snapping to ground on ledges beyond a certain velocity")]
        public float MaxVelocityForLedgeSnap;
        
        /// <summary>
        /// 角色在仍能保持吸附地面的前提下，可承受的最大向下坡度角变化。
        /// </summary>
        [Tooltip("The maximum downward slope angle change that the character can be subjected to and still be snapping to the ground")]
        [Range(1f, 180f)]
        public float MaxStableDenivelationAngle = 180f;


        /// <summary>
        /// 正确处理被 PhysicsMover 或动态刚体推动、站在其上的情况，同时处理推动动态刚体。
        /// </summary>
        [Header("Rigidbody interaction settings")]
        [Tooltip("Handles properly being pushed by and standing on PhysicsMovers or dynamic rigidbodies. Also handles pushing dynamic rigidbodies")]
        public bool InteractiveRigidbodyHandling = true;
        
        /// <summary>
        /// 角色如何与非运动学刚体交互。
        /// 「Kinematic」模式表示角色以无限大的力推动刚体（与运动学刚体相同）。
        /// 「SimulatedDynamic」以一个模拟出的质量值推动刚体。
        /// </summary>
        [Tooltip("How the character interacts with non-kinematic rigidbodies. " +
                 "\"Kinematic\" mode means the character pushes the rigidbodies with infinite force (as a kinematic body would). " +
                 "\"SimulatedDynamic\" pushes the rigidbodies with a simulated mass value.")]
        public RigidbodyInteractionType RigidbodyInteractionType;
        
        /// <summary>
        /// 用于推动刚体的质量
        /// </summary>
        [Tooltip("Mass used for pushing bodies")]
        public float SimulatedCharacterMass = 1f;
        
        /// <summary>
        /// 决定角色从平台上脱离时是否保留平台速度。
        /// </summary>
        [Tooltip("Determines if the character preserves moving platform velocities when de-grounding from them")]
        public bool PreserveAttachedRigidbodyMomentum = true;

        /// <summary>
        /// 使角色脱离平台所需的速度突变。
        /// </summary>
        [Tooltip("The sudden velocity change required to detach the character.")]
        public float RigidbodySlipVelocity = 5f;

        /// <summary>
        /// 决定角色的移动是否使用平面约束。
        /// </summary>
        [Header("Constraints settings")]
        [Tooltip("Determines if the character's movement uses the planar constraint")]
        public bool HasPlanarConstraint;
        
        /// <summary>
        /// 定义角色移动被约束到的平面（当 HasMovementConstraintPlane 启用时）。
        /// </summary>
        [Tooltip("Defines the plane that the character's movement is constrained on, if HasMovementConstraintPlane is active")]
        public Vector3 PlanarConstraintAxis = Vector3.forward;

        /// <summary>
        /// 每次更新中可以进行多少次移动扫掠。
        /// </summary>
        [Header("Other settings")]
        [Tooltip("How many times can we sweep for movement per update")]
        public int MaxMovementIterations = 5;
        
        /// <summary>
        /// 每次更新中可以进行多少次去重叠检测。
        /// </summary>
        [Tooltip("How many times can we check for de-collision per update")]
        public int MaxDecollisionIterations = 1;
        
        /// <summary>
        /// 在移动扫掠前检测重叠，确保即使已经与几何体相交也能发现所有碰撞。
        /// （有性能开销，但能防止穿透碰撞体）
        /// </summary>
        [Tooltip("Checks for overlaps before casting movement, making sure all collisions are detected even when already intersecting geometry" +
                 " (has a performance cost, but provides safety against tunneling through colliders)")]
        public bool CheckMovementInitialOverlaps = true;
        
        /// <summary>
        /// 超过最大移动迭代次数时将速度清零。
        /// </summary>
        [Tooltip("Sets the velocity to zero if exceed max movement iterations")]
        public bool KillVelocityWhenExceedMaxMovementIterations = true;
        
        /// <summary>
        /// 超过最大移动迭代次数时将剩余位移清零。
        /// </summary>
        [Tooltip("Sets the remaining movement to zero if exceed max movement iterations")]
        public bool KillRemainingMovementWhenExceedMaxMovementIterations = true;

        
        /// <summary>
        /// 包含当前的接地信息。
        /// </summary>
        [NonSerialized]
        public CharacterGroundingReport GroundingStatus;
        
        /// <summary>
        /// 包含上一 tick 的接地信息
        /// </summary>
        [NonSerialized]
        public CharacterTransientGroundingReport LastGroundingStatus;
        
        /// <summary>
        /// 指定角色的移动算法可以检测碰撞的 LayerMask。
        /// 默认使用刚体所在层的碰撞矩阵。
        /// </summary>
        [NonSerialized]
        public LayerMask CollidableLayers = -1;

        
        /// <summary>
        /// 角色 Motor 的 Transform。
        /// </summary>
        public Transform Transform => _transform;
        private Transform _transform;
        /// <summary>
        /// 角色在移动计算中的目标位置。（角色更新阶段始终保持最新）
        /// </summary>
        public Vector3 TransientPosition => _transientPosition;
        private Vector3 _transientPosition;
        /// <summary>
        /// 角色的上方向。（角色更新阶段始终保持最新）
        /// </summary>
        public Vector3 CharacterUp => _characterUp;
        private Vector3 _characterUp;
        /// <summary>
        /// 角色的前方向。（角色更新阶段始终保持最新）
        /// </summary>
        public Vector3 CharacterForward => _characterForward;
        private Vector3 _characterForward;
        /// <summary>
        /// 角色的右方向。（角色更新阶段始终保持最新）
        /// </summary>
        public Vector3 CharacterRight => _characterRight;
        private Vector3 _characterRight;
        /// <summary>
        /// 移动计算开始前角色的位置。
        /// </summary>
        public Vector3 InitialSimulationPosition => _initialSimulationPosition;
        private Vector3 _initialSimulationPosition;
        /// <summary>
        /// 移动计算开始前角色的旋转。
        /// </summary>
        public Quaternion InitialSimulationRotation => _initialSimulationRotation;
        private Quaternion _initialSimulationRotation;
        /// <summary>
        /// 表示需要保持附着的刚体。
        /// </summary>
        public Rigidbody AttachedRigidbody => _attachedRigidbody;
        private Rigidbody _attachedRigidbody;
        /// <summary>
        /// 从角色 Transform 位置指向胶囊体中心的向量。
        /// </summary>
        public Vector3 CharacterTransformToCapsuleCenter => _characterTransformToCapsuleCenter;
        private Vector3 _characterTransformToCapsuleCenter;
        /// <summary>
        /// 从角色 Transform 位置指向胶囊体底部的向量。
        /// </summary>
        public Vector3 CharacterTransformToCapsuleBottom => _characterTransformToCapsuleBottom;
        private Vector3 _characterTransformToCapsuleBottom;
        /// <summary>
        /// 从角色 Transform 位置指向胶囊体顶部的向量。
        /// </summary>
        public Vector3 CharacterTransformToCapsuleTop => _characterTransformToCapsuleTop;
        private Vector3 _characterTransformToCapsuleTop;
        /// <summary>
        /// 从角色 Transform 位置指向胶囊体下半球中心的向量。
        /// </summary>
        public Vector3 CharacterTransformToCapsuleBottomHemi => _characterTransformToCapsuleBottomHemi;
        private Vector3 _characterTransformToCapsuleBottomHemi;
        /// <summary>
        /// 从角色 Transform 位置指向胶囊体上半球中心的向量。
        /// </summary>
        public Vector3 CharacterTransformToCapsuleTopHemi => _characterTransformToCapsuleTopHemi;
        private Vector3 _characterTransformToCapsuleTopHemi;
        /// <summary>
        /// 角色因站在刚体或 PhysicsMover 上而获得的速度。
        /// </summary>
        public Vector3 AttachedRigidbodyVelocity => _attachedRigidbodyVelocity;
        private Vector3 _attachedRigidbodyVelocity;
        /// <summary>
        /// 角色更新过程中目前已检测到的重叠数量。（每次更新开始时重置）
        /// </summary>
        public int OverlapsCount => _overlapsCount;
        private int _overlapsCount;
        /// <summary>
        /// 角色更新过程中目前已检测到的重叠。
        /// </summary>
        public OverlapResult[] Overlaps => _overlaps;
        private OverlapResult[] _overlaps = new OverlapResult[MaxRigidbodyOverlapsCount];

        /// <summary>
        /// 该 Motor 是否正被 PhysicsMover 挤压。（重叠未被解开）
        /// </summary>
        public bool IsBeingCrushed { get; private set; }

        /// <summary>
        /// 该 Motor 被指定的 Controller。
        /// </summary>
        [NonSerialized]
        public ICharacterController CharacterController;
        /// <summary>
        /// Motor 上一次扫掠检测是否找到了地面？
        /// </summary>
        [NonSerialized]
        public bool LastMovementIterationFoundAnyGround;
        /// <summary>
        /// 该 Motor 在 KinematicCharacterSystem 数组中的索引。
        /// 注意：本 fork 从未给它赋值，也没有任何读取方，属于原版遗留字段。
        /// </summary>
        [NonSerialized]
        public int IndexInCharacterSystem;
        /// <summary>
        /// 记录全部模拟开始前的初始位置。
        /// </summary>
        [NonSerialized]
        public Vector3 InitialTickPosition;
        /// <summary>
        /// 记录全部模拟开始前的初始旋转。
        /// </summary>
        [NonSerialized]
        public Quaternion InitialTickRotation;
        /// <summary>
        /// 强制指定"我要附着的刚体"，会覆盖地面探测得出的结果。
        /// 用途：角色正被某个刚体托着、但脚下暂时没探到稳定地面时（例如跳上电梯的瞬间），
        /// 手动锁定它，保证仍能被带着走。用完记得清空，否则会一直跟着旧刚体。
        /// </summary>
        [NonSerialized]
        public Rigidbody AttachedRigidbodyOverride;
        /// <summary>
        /// 角色由直接移动产生的速度。
        /// </summary>
        [NonSerialized]
        public Vector3 BaseVelocity;

        // ── 私有运行时状态 ────────────────────────────────────────────
        // 全是"每 tick 复用"的缓存与标志位。刻意用定长数组而不是 List/新分配，目的是零 GC。

        // 胶囊 sweep / 射线命中的结果缓冲。定长，超出的命中直接丢弃（上限见 MaxHitsBudget）。
        private RaycastHit[] _internalCharacterHits = new RaycastHit[MaxHitsBudget];
        // 重叠查询（OverlapCapsule）的结果缓冲，同样定长复用。
        private Collider[] _internalProbedColliders = new Collider[MaxCollisionBudget];
        // 本 tick 已经施加过力的刚体。防止同一个箱子在一次位移里被反复加速（推箱子会飞出去）。
        private List<Rigidbody> _rigidbodiesPushedThisMove = new List<Rigidbody>(16);
        // 移动过程中记下的刚体命中，等位移算完再统一结算（见 ProcessVelocityForRigidbodyHits）。
        private RigidbodyProjectionHit[] _internalRigidbodyProjectionHits = new RigidbodyProjectionHit[MaxRigidbodyOverlapsCount];
        // 上一 tick 附着的刚体。用来判断"这一 tick 是不是刚脱离平台"，从而决定要不要保留平台速度。
        private Rigidbody _lastAttachedRigidbody;
        // 是否解算移动碰撞。由 SetMovementCollisionsSolvingActivation 控制，false 时移动退化为直接加位移。
        private bool _solveMovementCollisions = true;
        // 是否做地面探测。由 SetGroundSolvingActivation 控制，false 时角色永远被当作"在空中"。
        private bool _solveGrounding = true;
        // MoveCharacter 请求的标记位：业务代码随时置位，Phase1 里消费并清掉。
        private bool _movePositionDirty;
        // MoveCharacter 的目标位置，与上面的标记位配对。
        private Vector3 _movePositionTarget = Vector3.zero;
        // RotateCharacter 请求的标记位：Phase2 里消费。
        private bool _moveRotationDirty;
        // RotateCharacter 的目标旋转，与上面的标记位配对。
        private Quaternion _moveRotationTarget = Quaternion.identity;
        // 死字段：Phase1 里只被置 false，全文件没有任何地方读取它。原版残留。
        private bool _lastSolvedOverlapNormalDirty;
        // 本 tick 已记录的刚体命中数量，即 _internalRigidbodyProjectionHits 的有效长度。
        private int _rigidbodyProjectionHitCount;
        // 当前这次移动是否由"附着刚体（平台）"驱动。为 true 时碰撞过滤会放行脚下这块平台本身，
        // 否则角色会被自己站着的平台判定为撞墙而卡住。
        private bool _isMovingFromAttachedRigidbody;
        // 本 tick 要求强制离地（跳跃、从平台起跳）。由 ForceUnground 置位，Phase1 末尾清掉。
        private bool _mustUnground;
        // 强制离地的剩余时间（秒）。倒计时期间一直算"不在稳定地面上"，用于跳跃后不被立刻吸回地面。
        private float _mustUngroundTimeCounter;
        // 缓存的世界轴。Vector3.up 之类是属性、每次访问都有开销，缓存成字段能省掉每 tick 数十次调用。
        private readonly Vector3 _cachedWorldUp = Vector3.up;
        private readonly Vector3 _cachedWorldForward = Vector3.forward;
        private readonly Vector3 _cachedWorldRight = Vector3.right;
        // 常用零向量，避免到处 new Vector3(0,0,0) 或写 Vector3.zero（属性）。
        private readonly Vector3 _cachedZeroVector = Vector3.zero;

        private Quaternion _transientRotation;
        /// <summary>
        /// 角色在移动计算中的目标旋转。（角色更新阶段始终保持最新）
        /// </summary>
        public Quaternion TransientRotation
        {
            get => _transientRotation;
            private set
            {
                _transientRotation = value;
                _characterUp = _transientRotation * _cachedWorldUp;
                _characterForward = _transientRotation * _cachedWorldForward;
                _characterRight = _transientRotation * _cachedWorldRight;
            }
        }

        /// <summary>
        /// 角色的总速度，包含站在刚体或 PhysicsMover 上产生的速度。
        /// </summary>
        public Vector3 Velocity => BaseVelocity + _attachedRigidbodyVelocity;

        // 警告：除非你确切知道自己在做什么，否则不要改这些常量！
        // 它们不是手感参数，而是一组互相配合的"容差尺度"：改一个往往会连带影响另外几个。

        // 单次查询最多接受多少条命中，超出直接丢弃（防止极端几何下写爆数组）。
        private const int MaxHitsBudget = 16;
        // 重叠查询的结果上限，同上。
        private const int MaxCollisionBudget = 16;
        // 地面探测最多重探几次：第一次直下，第二次沿不稳定坡面的切向继续往下找。
        private const int MaxGroundingSweepIterations = 2;
        // 一次移动最多记录多少个"撞到的刚体"。
        private const int MaxRigidbodyOverlapsCount = 16;
        // 通用安全间距（1cm）。所有解重叠、贴地吸附都会额外留出它，
        // 否则浮点误差会让角色下一 tick 又贴回去，表现为高频抖动。
        private const float CollisionOffset = 0.01f;
        // 沿坡面续探时，每轮最多再往下走多远（防止顺着斜坡一路探到底）。
        private const float GroundProbeReboundDistance = 0.02f;
        // 地面探测的最小距离。角色站在地上时用它——范围小，不易被旁边的斜面/装饰物干扰。
        private const float MinimumGroundProbingDistance = 0.005f;
        // 地面探测起点向上回退的距离（10cm）。让"紧贴脚下的薄地面"也能被扫到；
        // 比通用 sweep 的回退大得多，因为脚底 10cm 内的地面很常见。
        private const float GroundProbingBackstepDistance = 0.1f;
        // 移动 sweep 起点回退的距离（2mm），纯粹用来吃掉浮点误差，贴墙移动时最关键。
        private const float SweepProbingBackstepDistance = 0.002f;
        // 边缘检测的两条辅助射线的起点偏移：竖直抬高 2cm、横向偏移 1mm。
        // 横移 1mm 是为了避开"正好打在棱边上"的退化情况。
        private const float SecondaryProbesVertical = 0.02f;
        private const float SecondaryProbesHorizontal = 0.001f;
        // 小于该速度直接归零，避免角色以肉眼不可见的速度无限滑行（也避免速度永不为零导致逻辑抖动）。
        private const float MinVelocityMagnitude = 0.01f;
        // 台阶检测时向前多探 3cm，确保能碰到台阶的立面而不是擦着过去。
        private const float SteppingForwardDistance = 0.03f;
        // 关闭台阶处理时，"脚下是不是悬崖"用的探测高度（5cm）。
        private const float MinDistanceForLedge = 0.05f;
        // 判定"这是竖直障碍而不是可踩的坡"的阈值：
        // 命中法线与角色上方向的点积绝对值要小于它，才允许尝试踏台阶（否则会顺着墙往上爬）。
        private const float CorrelationForVerticalObstruction = 0.01f;
#pragma warning restore 0414 

        // 注册到全局系统。第一个 Motor 出现时会把 KinematicCharacterSystem 创建出来，
        // 之后所有角色由它统一按 FixedUpdate 推进（保证平台/角色的时序一致）。
        private void OnEnable()
        {
            KinematicCharacterSystem.EnsureCreation();
            KinematicCharacterSystem.RegisterCharacterMotor(this);
        }

        // 只注销，不销毁系统（系统带 DontDestroyOnLoad，跨场景常驻）。
        private void OnDisable()
        {
            KinematicCharacterSystem.UnregisterCharacterMotor(this);
        }

        private void Reset()
        {
            ValidateData();
        }

        private void OnValidate()
        {
            ValidateData();
        }

        [ContextMenu("Remove Component")]
        private void HandleRemoveComponent()
        {
            var tmpCapsule = gameObject.GetComponent<CapsuleCollider>();
            DestroyImmediate(this);
            DestroyImmediate(tmpCapsule);
        }

        // 把 Inspector 里的参数"翻译"成运行时状态。Reset/OnValidate/Awake 都会调它，
        // 所以这里的赋值带有强制性质：手改胶囊尺寸会在下次校验时被覆盖回来。
        //
        // 两个必须知道的约束：
        //   · Capsule.direction 被强制为 1（Y 轴朝上），横向胶囊不被支持；
        //   · transform.localScale 被强制为 1，因为所有 sweep 都假定半径/高度是真实世界尺寸。
        /// <summary>
        /// 校验所有必需的值。
        /// </summary>
        private void ValidateData()
        {
            Capsule = GetComponent<CapsuleCollider>();
            CapsuleRadius = Mathf.Clamp(CapsuleRadius, 0f, CapsuleHeight * 0.5f);
            Capsule.direction = 1;
            Capsule.sharedMaterial = CapsulePhysicsMaterial;
            SetCapsuleDimensions(CapsuleRadius, CapsuleHeight, CapsuleYOffset);

            MaxStepHeight = Mathf.Clamp(MaxStepHeight, 0f, Mathf.Infinity);
            MinRequiredStepDepth = Mathf.Clamp(MinRequiredStepDepth, 0f, CapsuleRadius);
            MaxStableDistanceFromLedge = Mathf.Clamp(MaxStableDistanceFromLedge, 0f, CapsuleRadius);

            transform.localScale = Vector3.one;

#if UNITY_EDITOR
            Capsule.hideFlags = HideFlags.NotEditable;
            if (!Mathf.Approximately(transform.lossyScale.x, 1f) ||
                !Mathf.Approximately(transform.lossyScale.y, 1f) ||
                !Mathf.Approximately(transform.lossyScale.z, 1f))
            {
                Debug.LogError("Character's lossy scale is not (1,1,1). This is not allowed." +
                               " Make sure the character's transform and all of its parents have a (1,1,1) scale.",
                                gameObject);
            }
#endif
        }

        // 开关角色胶囊体的"存在感"：关闭后它变成 Trigger，别人的射线/碰撞不再被它挡住。
        // 注意内部仍会用自己的 sweep 做碰撞，所以角色不会掉下去。
        /// <summary>
        /// 设置胶囊碰撞体是否检测碰撞。
        /// </summary>
        public void SetCapsuleCollisionsActivation(bool collisionsActive)
        {
            Capsule.isTrigger = !collisionsActive;
        }

        // 关掉后 InternalCharacterMove 退化成"直接加位移"，不做任何碰撞解算。
        // 用于过场、传送、或自己接管碰撞的场合；关掉时角色可以穿墙。
        /// <summary>
        /// 设置 Motor 在移动（或被移动到）时是否解算碰撞。
        /// </summary>
        public void SetMovementCollisionsSolvingActivation(bool movementCollisionsSolvingActive)
        {
            _solveMovementCollisions = movementCollisionsSolvingActive;
        }

        // 关掉后不做地面探测，IsStableOnGround 永远为 false，角色会一直处于"空中"分支。
        /// <summary>
        /// 设置是否对所有命中都评估接地。
        /// </summary>
        public void SetGroundSolvingActivation(bool stabilitySolvingActive)
        {
            _solveGrounding = stabilitySolvingActive;
        }

        // 瞬移。注意它同时写 Transform、_transientPosition、_initialSimulationPosition 三处，
        // 保证"下一 tick 的差分基准"与"当前位置"一致，不会算出一个巨大的假速度。
        //
        // bypassInterpolation = true 时还会覆盖 InitialTickPosition，
        // 让插值起点立刻跟上，避免瞬移后画面从旧位置"飞"过来。
        /// <summary>
        /// 直接设置角色的位置。
        /// </summary>
        public void SetPosition(Vector3 position, bool bypassInterpolation = true)
        {
            _transform.position = position;
            _initialSimulationPosition = position;
            _transientPosition = position;

            if (bypassInterpolation)
            {
                InitialTickPosition = position;
            }
        }

        /// <summary>
        /// 直接设置角色的旋转。
        /// </summary>
        public void SetRotation(Quaternion rotation, bool bypassInterpolation = true)
        {
            _transform.rotation = rotation;
            _initialSimulationRotation = rotation;
            TransientRotation = rotation;

            if (bypassInterpolation)
            {
                InitialTickRotation = rotation;
            }
        }

        /// <summary>
        /// 直接设置角色的位置与旋转。
        /// </summary>
        public void SetPositionAndRotation(Vector3 position, Quaternion rotation, bool bypassInterpolation = true)
        {
            _transform.SetPositionAndRotation(position, rotation);
            _initialSimulationPosition = position;
            _initialSimulationRotation = rotation;
            _transientPosition = position;
            TransientRotation = rotation;

            if (bypassInterpolation)
            {
                InitialTickPosition = position;
                InitialTickRotation = rotation;
            }
        }

        // 请求"在下一个 Phase1 里移动到 toPosition"。
        // 它不是立即移动，而是转成速度交给 InternalCharacterMove 走一次完整解算，
        // 所以会正确地被墙挡住、被台阶抬升、被平台带动。
        // 与 SetPosition 的区别：SetPosition 是穿透式瞬移，这个是"合法移动"。
        /// <summary>
        /// 移动角色位置，会考虑所有移动碰撞解算。
        /// 真正的移动在下次调用 Motor 更新时执行。
        /// </summary>
        public void MoveCharacter(Vector3 toPosition)
        {
            _movePositionDirty = true;
            _movePositionTarget = toPosition;
        }

        /// <summary>
        /// 移动角色旋转。真正的旋转在下次调用 Motor 更新时执行。
        /// </summary>
        public void RotateCharacter(Quaternion toRotation)
        {
            _moveRotationDirty = true;
            _moveRotationTarget = toRotation;
        }

        // 状态快照（存档/回滚/网络同步用）。注意它取的是 _transientPosition，
        // 而不是 Transform.position —— 后者可能是插值中的画面位置。
        /// <summary>
        /// 返回与模拟相关的 Motor 的全部状态信息。
        /// </summary>
        public KinematicCharacterMotorState GetState()
        {
            var state = new KinematicCharacterMotorState
            {
                Position = _transientPosition,
                Rotation = _transientRotation,
                BaseVelocity = BaseVelocity,
                AttachedRigidbodyVelocity = _attachedRigidbodyVelocity,
                MustUnground = _mustUnground,
                MustUngroundTime = _mustUngroundTimeCounter,
                LastMovementIterationFoundAnyGround = LastMovementIterationFoundAnyGround
            };

            state.GroundingStatus.CopyFrom(GroundingStatus);
            state.AttachedRigidbody = _attachedRigidbody;

            return state;
        }

        /// <summary>
        /// 立即应用一份 Motor 状态。
        /// </summary>
        public void ApplyState(KinematicCharacterMotorState state, bool bypassInterpolation = true)
        {
            SetPositionAndRotation(state.Position, state.Rotation, bypassInterpolation);

            BaseVelocity = state.BaseVelocity;
            _attachedRigidbodyVelocity = state.AttachedRigidbodyVelocity;

            _mustUnground = state.MustUnground;
            _mustUngroundTimeCounter = state.MustUngroundTime;
            LastMovementIterationFoundAnyGround = state.LastMovementIterationFoundAnyGround;
            GroundingStatus.CopyFrom(state.GroundingStatus);
            _attachedRigidbody = state.AttachedRigidbody;
        }

        // 调整胶囊尺寸，并缓存五个"从角色原点到胶囊各部位"的偏移向量。
        // 后面所有查询都用这些缓存值算球心，避免每 tick 重复计算。
        //
        // 为什么 height 要至少 radius*2：Unity 的 CapsuleCollider 在 height < 2r 时几何会退化，
        // 两端半球重叠会导致 sweep 结果不稳定。
        /// <summary>
        /// 调整胶囊体尺寸，同时缓存重要的胶囊体尺寸数据。
        /// </summary>
        public void SetCapsuleDimensions(float radius, float height, float yOffset)
        {
            // 安全措施：防止出现非法的胶囊体几何。
            height = Mathf.Max(height, radius * 2f + 0.01f);

            CapsuleRadius = radius;
            CapsuleHeight = height;
            CapsuleYOffset = yOffset;

            Capsule.radius = CapsuleRadius;
            Capsule.height = Mathf.Clamp(CapsuleHeight, CapsuleRadius * 2f, CapsuleHeight);
            var center = new Vector3(0f, CapsuleYOffset, 0f);
            Capsule.center = center;

            _characterTransformToCapsuleCenter = center;
            _characterTransformToCapsuleBottom = center + -_cachedWorldUp * (Capsule.height * 0.5f);
            _characterTransformToCapsuleTop = Capsule.center + _cachedWorldUp * (Capsule.height * 0.5f);
            _characterTransformToCapsuleBottomHemi = Capsule.center + -_cachedWorldUp * (Capsule.height * 0.5f) + _cachedWorldUp * Capsule.radius;
            _characterTransformToCapsuleTopHemi = Capsule.center + _cachedWorldUp * (Capsule.height * 0.5f) + -_cachedWorldUp * Capsule.radius;
        }

        // Awake 里做三件事：缓存 Transform、校验数据、按当前 Layer 构建碰撞掩码。
        //
        // CollidableLayers 的构建方式值得注意：它读的是项目的 Layer 碰撞矩阵，
        // 即"角色所在层能撞到哪些层"，而不是简单的一个 LayerMask 字段。
        private void Awake()
        {
            _transform = transform;
            ValidateData();

            _transientPosition = _transform.position;
            TransientRotation = _transform.rotation;

            // 构建 CollidableLayers 掩码
            CollidableLayers = 0;
            for (int i = 0; i < 32; i++)
            {
                if (!Physics.GetIgnoreLayerCollision(gameObject.layer, i))
                    CollidableLayers |= 1 << i;
            }

            SetCapsuleDimensions(CapsuleRadius, CapsuleHeight, CapsuleYOffset);
        }

        // ══════════════════════════════════════════════════════════
        // 阶段 1：探测。此时平台还停在"旧位置"，本函数只读取世界、不改世界。
        //
        // 顺序（每一步都依赖前一步的结果，不能换）：
        //   1. 清空临时缓冲、取 Transform 作为本 tick 起点
        //   2. 处理 MoveCharacter 请求（会真的走一次位移解算）
        //   3. 解开初始重叠（卡在墙里时先推出来）
        //   4. 地面探测 + 吸附（ProbeGround）
        //   5. 处理附着刚体（平台携带、旋转、脱离动量）
        //
        // 关键：第 5 步里的 InternalCharacterMove 是本函数唯一会改 _transientPosition 的地方，
        // 它负责"被平台扛着走"。角色的自主移动在 Phase2，不在这里。
        // ══════════════════════════════════════════════════════════
        /// <summary>
        /// 更新阶段 1 应在 PhysicsMover 计算完速度之后、
        /// 但还未模拟其目标位置/旋转之前调用。它负责：
        /// - 初始化本次更新的所有值
        /// - 处理 MovePosition 调用
        /// - 解开初始碰撞重叠
        /// - 地面探测
        /// - 检测可能的可交互刚体
        /// </summary>
        public void UpdatePhase1(float deltaTime)
        {
            // NaN 传播安全中止
            if (float.IsNaN(BaseVelocity.x) || float.IsNaN(BaseVelocity.y) || float.IsNaN(BaseVelocity.z))
                BaseVelocity = Vector3.zero;
            
            if (float.IsNaN(_attachedRigidbodyVelocity.x) || float.IsNaN(_attachedRigidbodyVelocity.y) || float.IsNaN(_attachedRigidbodyVelocity.z))
                _attachedRigidbodyVelocity = Vector3.zero;
            

#if UNITY_EDITOR
            if (!Mathf.Approximately(_transform.lossyScale.x, 1f) ||
                !Mathf.Approximately(_transform.lossyScale.y, 1f) ||
                !Mathf.Approximately(_transform.lossyScale.z, 1f))
            {
                Debug.LogError("Character's lossy scale is not (1,1,1). This is not allowed." +
                               " Make sure the character's transform and all of its parents have a (1,1,1) scale.",
                                gameObject);
            }
#endif

            _rigidbodiesPushedThisMove.Clear();

            // 更新前
            CharacterController.BeforeCharacterUpdate(deltaTime);

            _transientPosition = _transform.position;
            TransientRotation = _transform.rotation;
            _initialSimulationPosition = _transientPosition;
            _initialSimulationRotation = _transientRotation;
            _rigidbodyProjectionHitCount = 0;
            _overlapsCount = 0;
            _lastSolvedOverlapNormalDirty = false;

            #region Handle Move Position
            if (_movePositionDirty)
            {
                if (_solveMovementCollisions)
                {
                    Vector3 tmpVelocity = GetVelocityFromMovement(_movePositionTarget - _transientPosition, deltaTime);
                    if (InternalCharacterMove(ref tmpVelocity, deltaTime))
                    {
                        if (InteractiveRigidbodyHandling)
                        {
                            ProcessVelocityForRigidbodyHits(ref tmpVelocity, deltaTime);
                        }
                    }
                }
                else
                {
                    _transientPosition = _movePositionTarget;
                }

                _movePositionDirty = false;
            }
            #endregion

            LastGroundingStatus.CopyFrom(GroundingStatus);
            GroundingStatus = new CharacterGroundingReport { GroundNormal = _characterUp };

            if (_solveMovementCollisions)
            {
                #region Resolve initial overlaps
                int iterationsMade = 0;
                bool overlapSolved = false;
                while (iterationsMade < MaxDecollisionIterations && !overlapSolved)
                {
                    int nbOverlaps = CharacterCollisionsOverlap(_transientPosition, _transientRotation, _internalProbedColliders);

                    if (nbOverlaps > 0)
                    {
                        // 解开与动态刚体或 PhysicsMover 之外的重叠
                        for (int i = 0; i < nbOverlaps; i++)
                        {
                            if (GetInteractiveRigidbody(_internalProbedColliders[i]) != null) continue;
                            
                            // 处理重叠
                            var overlappedTransform = _internalProbedColliders[i].GetComponent<Transform>();
                            
                            if (!Physics.ComputePenetration(Capsule, _transientPosition, _transientRotation,
                                    _internalProbedColliders[i], overlappedTransform.position, overlappedTransform.rotation,
                                    out Vector3 resolutionDirection, out float resolutionDistance)) continue;
                            
                            // 沿阻挡方向推出
                            var mockReport = new HitStabilityReport { IsStable = IsStableOnNormal(resolutionDirection) };
                            
                            resolutionDirection = GetObstructionNormal(resolutionDirection, mockReport.IsStable);

                            // 解开重叠
                            Vector3 resolutionMovement = resolutionDirection * (resolutionDistance + CollisionOffset);
                            _transientPosition += resolutionMovement;

                            // 记录重叠
                            if (_overlapsCount < _overlaps.Length)
                            {
                                _overlaps[_overlapsCount] = new OverlapResult(resolutionDirection, _internalProbedColliders[i]);
                                _overlapsCount++;
                            }

                            break;
                        }
                    }
                    else
                    {
                        overlapSolved = true;
                    }

                    iterationsMade++;
                }
                #endregion
            }

            #region Ground Probing and Snapping
            // 处理脱离地面
            if (_solveGrounding)
            {
                if (MustUnground())
                {
                    _transientPosition += _characterUp * (MinimumGroundProbingDistance * 1.5f);
                }
                else
                {
                    // 选择合适的地面探测距离
                    float selectedGroundProbingDistance = MinimumGroundProbingDistance;
                    if (!LastGroundingStatus.SnappingPrevented &&
                        (LastGroundingStatus.IsStableOnGround || LastMovementIterationFoundAnyGround))
                    {
                        selectedGroundProbingDistance = StepHandling != StepHandlingMethod.None ?
                            Mathf.Max(CapsuleRadius, MaxStepHeight) : CapsuleRadius;

                        selectedGroundProbingDistance += GroundDetectionExtraDistance;
                    }

                    ProbeGround(ref _transientPosition, _transientRotation, selectedGroundProbingDistance, ref GroundingStatus);

                    if (!LastGroundingStatus.IsStableOnGround && GroundingStatus.IsStableOnGround)
                    {
                        // 处理稳定落地
                        BaseVelocity = Vector3.ProjectOnPlane(BaseVelocity, CharacterUp);
                        BaseVelocity = GetDirectionTangentToSurface(BaseVelocity, GroundingStatus.GroundNormal) * BaseVelocity.magnitude;
                    }
                }
            }

            LastMovementIterationFoundAnyGround = false;

            if (_mustUngroundTimeCounter > 0f)
                _mustUngroundTimeCounter -= deltaTime;
            
            _mustUnground = false;
            #endregion

            if (_solveGrounding) CharacterController.PostGroundingUpdate(deltaTime);

            if (InteractiveRigidbodyHandling)
            {
#region Interactive Rigidbody Handling

                _lastAttachedRigidbody = _attachedRigidbody;
                if (AttachedRigidbodyOverride)
                {
                    _attachedRigidbody = AttachedRigidbodyOverride;
                }
                else
                {
                    // 从接地信息中检测可交互刚体
                    if (GroundingStatus.IsStableOnGround && GroundingStatus.GroundCollider.attachedRigidbody)
                    {
                        Rigidbody interactiveRigidbody = GetInteractiveRigidbody(GroundingStatus.GroundCollider);
                        if (interactiveRigidbody)
                            _attachedRigidbody = interactiveRigidbody;
                    }
                    else
                        _attachedRigidbody = null;
                }

                Vector3 tmpVelocityFromCurrentAttachedRigidbody = Vector3.zero;
                Vector3 tmpAngularVelocityFromCurrentAttachedRigidbody = Vector3.zero;
                if (_attachedRigidbody)
                {
                    GetVelocityFromRigidbodyMovement(_attachedRigidbody, _transientPosition, deltaTime,
                        out tmpVelocityFromCurrentAttachedRigidbody,
                        out tmpAngularVelocityFromCurrentAttachedRigidbody);

                    var currMagnitude = tmpVelocityFromCurrentAttachedRigidbody.magnitude;
                    var lastMagnitude = _attachedRigidbodyVelocity.magnitude;
                    
                    if (_lastAttachedRigidbody && currMagnitude < lastMagnitude && lastMagnitude - currMagnitude > RigidbodySlipVelocity)
                    {
                        ForceUnground(0.1f);
                        _attachedRigidbody = null;
                    }
                }

                // 从附着的刚体上脱离时保持动量
                if (PreserveAttachedRigidbodyMomentum && _lastAttachedRigidbody != null &&
                    _attachedRigidbody != _lastAttachedRigidbody)
                {
                    BaseVelocity += _attachedRigidbodyVelocity;
                    BaseVelocity -= tmpVelocityFromCurrentAttachedRigidbody;
                }

                // 处理来自附着刚体的附加速度
                _attachedRigidbodyVelocity = _cachedZeroVector;
                if (_attachedRigidbody)
                {
                    _attachedRigidbodyVelocity = tmpVelocityFromCurrentAttachedRigidbody;

                    // 附着刚体带来的旋转
                    Vector3 newForward = Vector3.ProjectOnPlane
                    (Quaternion.Euler(tmpAngularVelocityFromCurrentAttachedRigidbody * (Mathf.Rad2Deg * deltaTime))
                     * _characterForward, _characterUp).normalized;

                    TransientRotation = Quaternion.LookRotation(newForward, _characterUp);
                }

                // 落到附着的刚体上时抵消水平速度
                if (GroundingStatus.GroundCollider
                    && GroundingStatus.GroundCollider.attachedRigidbody
                    && GroundingStatus.GroundCollider.attachedRigidbody == _attachedRigidbody
                    && _attachedRigidbody != null
                    && _lastAttachedRigidbody == null)
                {
                    BaseVelocity -= Vector3.ProjectOnPlane(_attachedRigidbodyVelocity, _characterUp);
                }

                // 来自附着刚体的移动
                if (_attachedRigidbodyVelocity.sqrMagnitude > 0f)
                {
                    _isMovingFromAttachedRigidbody = true;

                    if (_solveMovementCollisions)
                    {
                        // 按刚体速度执行移动
                        InternalCharacterMove(ref _attachedRigidbodyVelocity, deltaTime);
                    }
                    else
                    {
                        _transientPosition += _attachedRigidbodyVelocity * deltaTime;
                    }

                    _isMovingFromAttachedRigidbody = false;
                }
                #endregion
            }
        }

        // ══════════════════════════════════════════════════════════
        // 阶段 2：解算。此时平台已经提交到新位置，世界是"本 tick 的最终形态"。
        //
        // 顺序：
        //   1. 解旋转（问 Controller）
        //   2. 处理 RotateCharacter 请求
        //   3. 被平台顶起来时的上抬 + 旋转/平台造成的重叠解脱
        //   4. 问 Controller 要速度（UpdateVelocity）
        //   5. 用 InternalCharacterMove 走完自主位移
        //   6. 结算撞到的刚体（推箱子/被推）
        //   7. 平面约束 → 离散碰撞事件 → AfterCharacterUpdate
        //
        // 注意第 4 步是"整段覆盖"：Controller 拿到的 ref 就是 BaseVelocity，
        // 它写什么，角色这一 tick 就走什么样的路。
        // ══════════════════════════════════════════════════════════
        /// <summary>
        /// 更新阶段 2 应在 PhysicsMover 模拟完其目标位置/旋转之后调用。
        /// 结束时，TransientPosition/Rotation 会更新为本次移动结束时 Motor 应处的位置。
        /// 它负责：
        /// - 解算旋转
        /// - 处理 MoveRotation 调用
        /// - 解开附着刚体可能造成的重叠
        /// - 解算速度
        /// - 应用平面约束
        /// </summary>
        public void UpdatePhase2(float deltaTime)
        {
            // 处理旋转
            CharacterController.UpdateRotation(ref _transientRotation, deltaTime);
            TransientRotation = _transientRotation;

            // 处理移动旋转
            if (_moveRotationDirty)
            {
                TransientRotation = _moveRotationTarget;
                _moveRotationDirty = false;
            }

            if (_solveMovementCollisions && InteractiveRigidbodyHandling)
            {
                if (InteractiveRigidbodyHandling)
                {
                    #region Solve potential attached rigidbody overlap
                    if (_attachedRigidbody)
                    {
                        float upwardsOffset = Capsule.radius;

                        if (CharacterGroundSweep(_transientPosition + _characterUp * upwardsOffset, _transientRotation,
                                -_characterUp, upwardsOffset, out RaycastHit closestHit))
                        {
                            if (closestHit.collider.attachedRigidbody == _attachedRigidbody && IsStableOnNormal(closestHit.normal))
                            {
                                float distanceMovedUp = upwardsOffset - closestHit.distance;
                                _transientPosition = _transientPosition + _characterUp * distanceMovedUp + _characterUp * CollisionOffset;
                            }
                        }
                    }
                    #endregion
                }

                if (InteractiveRigidbodyHandling)
                {
                    #region Resolve overlaps that could've been caused by rotation or physics movers simulation pushing the character
                    int iterationsMade = 0;
                    bool overlapSolved = false;
                    while (iterationsMade < MaxDecollisionIterations && !overlapSolved)
                    {
                        int nbOverlaps = CharacterCollisionsOverlap(_transientPosition, _transientRotation, _internalProbedColliders);
                        if (nbOverlaps > 0)
                        {
                            bool penetrationFailed = false;
                            for (int i = 0; i < nbOverlaps; i++)
                            {
                                // Process overlap
                                var overlappedTransform = _internalProbedColliders[i].GetComponent<Transform>();
                                if (Physics.ComputePenetration(
                                        Capsule, _transientPosition, _transientRotation, _internalProbedColliders[i],
                                        overlappedTransform.position, overlappedTransform.rotation,
                                        out Vector3 resolutionDirection, out float resolutionDistance))
                                {
                                    // Resolve along obstruction direction
                                    var mockReport = new HitStabilityReport 
                                        { IsStable = IsStableOnNormal(resolutionDirection) };
                                    
                                    resolutionDirection = GetObstructionNormal(resolutionDirection, mockReport.IsStable);

                                    // Solve overlap
                                    Vector3 resolutionMovement = resolutionDirection * (resolutionDistance + CollisionOffset);
                                    _transientPosition += resolutionMovement;

                                    // If interactiveRigidbody, register as rigidbody hit for velocity
                                    if (InteractiveRigidbodyHandling)
                                    {
                                        Rigidbody probedRigidbody = GetInteractiveRigidbody(_internalProbedColliders[i]);
                                        if (probedRigidbody != null)
                                        {
                                            var tmpReport = new HitStabilityReport
                                                { IsStable = IsStableOnNormal(resolutionDirection) };
                                            
                                            if (tmpReport.IsStable) LastMovementIterationFoundAnyGround = true;
                                            
                                            if (probedRigidbody != _attachedRigidbody)
                                            {
                                                Vector3 estimatedCollisionPoint = _transientPosition;

                                                StoreRigidbodyHit(probedRigidbody, Velocity, estimatedCollisionPoint,
                                                    resolutionDirection, tmpReport);
                                            }
                                        }
                                    }

                                    // Remember overlaps
                                    if (_overlapsCount < _overlaps.Length)
                                    {
                                        _overlaps[_overlapsCount] = new OverlapResult(resolutionDirection, _internalProbedColliders[i]);
                                        _overlapsCount++;
                                    }

                                    break;
                                }

                                penetrationFailed = true;
                            }

                            if (penetrationFailed) overlapSolved = true;
                        }
                        else
                        {
                            overlapSolved = true;
                        }

                        iterationsMade++;
                    }

                    IsBeingCrushed = !overlapSolved;
                    #endregion
                }
            }

            // Handle velocity
            CharacterController.UpdateVelocity(ref BaseVelocity, deltaTime);

            if (BaseVelocity.magnitude < MinVelocityMagnitude)
                BaseVelocity = Vector3.zero;

            #region Calculate Character movement from base velocity   
            // Perform the move from base velocity
            if (BaseVelocity.sqrMagnitude > 0f)
            {
                if (_solveMovementCollisions)
                {
                    InternalCharacterMove(ref BaseVelocity, deltaTime);
                }
                else
                {
                    _transientPosition += BaseVelocity * deltaTime;
                }
            }

            // Process rigidbody hits/overlaps to affect velocity
            if (InteractiveRigidbodyHandling)
                ProcessVelocityForRigidbodyHits(ref BaseVelocity, deltaTime);
            #endregion

            // Handle planar constraint
            if (HasPlanarConstraint)
            {
                _transientPosition = _initialSimulationPosition + Vector3.ProjectOnPlane
                    (_transientPosition - _initialSimulationPosition, PlanarConstraintAxis.normalized);
            }

            // Discrete collision detection
            if (DiscreteCollisionEvents)
            {
                int nbOverlaps = CharacterCollisionsOverlap(_transientPosition, _transientRotation,
                    _internalProbedColliders, CollisionOffset * 2f);
                
                for (int i = 0; i < nbOverlaps; i++)
                    CharacterController.OnDiscreteCollisionDetected(_internalProbedColliders[i]);
            }

            CharacterController.AfterCharacterUpdate(deltaTime);
        }

        // 最基础的一票：法线与角色上方向夹角 <= MaxStableSlopeAngle。
        // 很多地方直接用它做快速判断，完整的四层判定在 EvaluateHitStability。
        /// <summary>
        /// Determines if motor can be considered stable on given slope normal.
        /// </summary>
        private bool IsStableOnNormal(Vector3 normal)
        {
            return Vector3.Angle(_characterUp, normal) <= MaxStableSlopeAngle;
        }

        // 在"坡度够缓"之上再排除两类不该站稳的情况：
        //
        //   1) 站在悬崖边：如果正朝"空的那一侧"移动，且速度超过 MaxVelocityForLedgeSnap，
        //      就允许掉下去（否则角色会像被粘在地面上一样走出悬崖再突然下落）；
        //      如果离边缘太远（超过 MaxStableDistanceFromLedge），也不稳定。
        //
        //   2) 坡面突变：相邻两段地面法线夹角超过 MaxStableDenivelationAngle 时，
        //      判定为"地面骤然断开"，不吸附。这个参数是防止从缓坡直接接到陡坡时角色被"拉"下去。
        /// <summary>
        /// Determines if motor can be considered stable on given slope normal.
        /// </summary>
        private bool IsStableWithSpecialCases(ref HitStabilityReport stabilityReport, Vector3 velocity)
        {
            if (!LedgeAndDenivelationHandling) return true;
            
            if (stabilityReport.LedgeDetected)
            {
                if (stabilityReport.IsMovingTowardsEmptySideOfLedge)
                {
                    // Max snap vel
                    Vector3 velocityOnLedgeNormal = Vector3.Project(velocity, stabilityReport.LedgeFacingDirection);
                    if (velocityOnLedgeNormal.magnitude >= MaxVelocityForLedgeSnap)
                    {
                        return false;
                    }
                }

                // Distance from ledge
                if (stabilityReport.IsOnEmptySideOfLedge && stabilityReport.DistanceFromLedge > MaxStableDistanceFromLedge)
                {
                    return false;
                }
            }

            // "Launching" off of slopes of a certain denivelation angle
            if (LastGroundingStatus.FoundAnyGround
                && stabilityReport.InnerNormal.sqrMagnitude != 0f
                && stabilityReport.OuterNormal.sqrMagnitude != 0f)
            {
                float denivelationAngle = Vector3.Angle(stabilityReport.InnerNormal, stabilityReport.OuterNormal);
                if (denivelationAngle > MaxStableDenivelationAngle)
                {
                    return false;
                }

                denivelationAngle = Vector3.Angle(LastGroundingStatus.InnerGroundNormal, stabilityReport.OuterNormal);
                if (denivelationAngle > MaxStableDenivelationAngle)
                {
                    return false;
                }
            }

            return true;
        }

        // ══════════════════════════════════════════════════════════
        // 地面探测 + 吸附。这是"角色能稳稳贴地"的核心，逻辑比看起来复杂：
        //
        // 它不是一个 sweep，而是一个最多 MaxGroundingSweepIterations 次的小循环：
        //   · 向下 sweep，命中的法线如果稳定 → 吸附（把 probingPos 拉到离地 CollisionOffset 的位置）
        //   · 如果命中的是"不稳定"的面（比如陡坡边缘）→ 不吸附，而是把方向改成
        //     "沿该面切向继续往下探"，再探一次，看坡下面有没有可站的平台
        //
        // 这就是角色能站在台阶边缘、能沿斜坡往下"贴"着走的原因。
        // 副作用：探测距离会随状态变化（平地探得浅，站稳后才探得深）。
        // ══════════════════════════════════════════════════════════
        /// <summary>
        /// Probes for valid ground and modifies the input transientPosition if ground snapping occurs.
        /// </summary>
        private void ProbeGround(ref Vector3 probingPos, Quaternion atRotation, float probingDist, ref CharacterGroundingReport groundingReport)
        {
            if (probingDist < MinimumGroundProbingDistance)
                probingDist = MinimumGroundProbingDistance;

            int groundSweepsMade = 0;
            bool groundSweepingIsOver = false;
            Vector3 groundSweepPosition = probingPos;
            Vector3 groundSweepDirection = atRotation * -_cachedWorldUp;
            float groundProbeDistanceRemaining = probingDist;
            while (groundProbeDistanceRemaining > 0 && groundSweepsMade <= MaxGroundingSweepIterations && !groundSweepingIsOver)
            {
                // Sweep for ground detection
                if (CharacterGroundSweep(groundSweepPosition, atRotation, groundSweepDirection, groundProbeDistanceRemaining, out RaycastHit groundSweepHit))
                {
                    Vector3 targetPosition = groundSweepPosition + groundSweepDirection * groundSweepHit.distance;
                    var groundHitStabilityReport = new HitStabilityReport();
                    
                    EvaluateHitStability(groundSweepHit.collider, groundSweepHit.normal, groundSweepHit.point,
                        targetPosition, _transientRotation, BaseVelocity, ref groundHitStabilityReport);

                    groundingReport.FoundAnyGround      = true;
                    groundingReport.GroundNormal        = groundSweepHit.normal;
                    groundingReport.InnerGroundNormal   = groundHitStabilityReport.InnerNormal;
                    groundingReport.OuterGroundNormal   = groundHitStabilityReport.OuterNormal;
                    groundingReport.GroundCollider      = groundSweepHit.collider;
                    groundingReport.GroundPoint         = groundSweepHit.point;
                    groundingReport.SnappingPrevented   = false;

                    // Found stable ground
                    if (groundHitStabilityReport.IsStable)
                    {
                        // Find all scenarios where ground snapping should be canceled
                        groundingReport.SnappingPrevented = !IsStableWithSpecialCases(ref groundHitStabilityReport, BaseVelocity);

                        groundingReport.IsStableOnGround = true;

                        // Ground snapping
                        if (!groundingReport.SnappingPrevented)
                            probingPos = groundSweepPosition + groundSweepDirection * (groundSweepHit.distance - CollisionOffset);

                        CharacterController.OnGroundHit(
                            groundSweepHit.collider,
                            groundSweepHit.normal,
                            groundSweepHit.point,
                            ref groundHitStabilityReport);
                        
                        groundSweepingIsOver = true;
                    }
                    else
                    {
                        // Calculate movement from this iteration and advance position
                        Vector3 sweepMovement = groundSweepDirection * groundSweepHit.distance + atRotation * 
                            _cachedWorldUp * Mathf.Max(CollisionOffset, groundSweepHit.distance);
                        
                        groundSweepPosition += sweepMovement;

                        // Set remaining distance
                        groundProbeDistanceRemaining = Mathf.Min(GroundProbeReboundDistance,
                            Mathf.Max(groundProbeDistanceRemaining - sweepMovement.magnitude, 0f));

                        // Reorient direction
                        groundSweepDirection = Vector3.ProjectOnPlane(groundSweepDirection, groundSweepHit.normal).normalized;
                    }
                }
                else
                {
                    groundSweepingIsOver = true;
                }

                groundSweepsMade++;
            }
        }

        // 强制"离地"一小段时间（默认 0.1s）。跳跃时必须调用它，
        // 否则下一 tick 的地面吸附会立刻把角色拽回地面，跳不起来。
        //
        // 内部是"计时器 + 布尔"双保险：布尔管本 tick，计时器管后续几 tick。
        /// <summary>
        /// Forces the character to unground itself on its next grounding update
        /// </summary>
        public void ForceUnground(float time = 0.1f)
        {
            _mustUnground = true;
            _mustUngroundTimeCounter = time;
        }

        public bool MustUnground()
        {
            return _mustUnground || _mustUngroundTimeCounter > 0f;
        }

        // 把一个方向"压"到指定表面的切平面上，同时保持它在角色视角下的前后左右关系不乱。
        // 用法：在斜坡上走时，把水平速度转到坡面上（否则会贴着坡面往下插）。
        /// <summary>
        /// Returns the direction adjusted to be tangent to a specified surface normal relatively to the character's up direction.
        /// 用于在坡面上重新定向一个方向，且不产生横向偏移。
        /// </summary>
        public Vector3 GetDirectionTangentToSurface(Vector3 direction, Vector3 surfaceNormal)
        {
            Vector3 directionRight = Vector3.Cross(direction, _characterUp);
            return Vector3.Cross(surfaceNormal, directionRight).normalized;
        }

        // ══════════════════════════════════════════════════════════
        // 【核心】位移解算器 —— 2494 行里最重要的一个函数。
        //
        // 输入：transientVelocity（BaseVelocity 或平台携带速度）+ deltaTime
        // 输出：_transientPosition 前进到最终位置；transientVelocity 被投影成"撞完之后剩下的速度"
        // 返回：false 表示"迭代次数用尽仍未走完"（可能卡在复杂几何里）
        //
        // 三段结构：
        //   A. 预投影：把上一次残留的重叠法线先应用到速度上（否则一开局就插在墙里）
        //   B. 主循环：sweep 出一段 → 走完这一段 → 投影速度 → 用剩下的速度继续 sweep
        //   C. 收尾：剩余位移直接加上（此时说明前方已无障碍）
        //
        // 两个不变式（抓住这两点，循环就不难看懂）：
        //   · remainingMovementMagnitude 是"当前方向上的剩余长度"，单调不增；
        //   · 每次投影只会减小或改变速度方向，所以循环一定收敛，迭代上限只是兜底。
        //
        // 为什么不用 CharacterController.Move：这里每一步命中都可见，
        // 台阶、凹角、坡度、刚体反作用都需要命中法线和稳定性报告。
        // ══════════════════════════════════════════════════════════
        /// <summary>
        /// 在考虑所有物理模拟、台阶处理与影响该 Motor 的速度投影规则的前提下，
        /// 按给定位移移动角色位置。
        /// </summary>
        /// <returns> 若位移无法被完全解算，返回 false </returns>
        private bool InternalCharacterMove(ref Vector3 transientVelocity, float deltaTime)
        {
            if (deltaTime <= 0f)
                return false;

            bool wasCompleted                   = true;
            Vector3 remainingMovementDirection  = transientVelocity.normalized;
            float remainingMovementMagnitude    = transientVelocity.magnitude * deltaTime;
            int sweepsMade                      = 0;
            bool hitSomethingThisSweepIteration = true;
            Vector3 tmpMovedPosition            = _transientPosition;
            bool previousHitIsStable            = false;
            Vector3 previousVelocity            = _cachedZeroVector;
            Vector3 previousObstructionNormal   = _cachedZeroVector;
            var sweepState                      = MovementSweepState.Initial;

            // 在扫掠之前，先按当前重叠投影这次移动
            for (int i = 0; i < _overlapsCount; i++)
            {
                Vector3 overlapNormal = _overlaps[i].Normal;
                if (Vector3.Dot(remainingMovementDirection, overlapNormal) > 0f) continue;
                
                bool stableOnHit = IsStableOnNormal(overlapNormal) && !MustUnground();
                Vector3 velocityBeforeProjection = transientVelocity;
                Vector3 obstructionNormal = GetObstructionNormal(overlapNormal, stableOnHit);

                InternalHandleVelocityProjection(
                    stableOnHit, obstructionNormal,
                    ref sweepState, previousHitIsStable, previousVelocity, previousObstructionNormal,
                    ref transientVelocity, ref remainingMovementMagnitude, ref remainingMovementDirection);

                previousHitIsStable         = stableOnHit;
                previousVelocity            = velocityBeforeProjection;
                previousObstructionNormal   = obstructionNormal;
            }

            // 扫掠期望的位移以检测碰撞
            while (remainingMovementMagnitude > 0f && sweepsMade <= MaxMovementIterations && hitSomethingThisSweepIteration)
            {
                bool foundClosestHit                = false;
                Vector3 closestSweepHitPoint        = default;
                Vector3 closestSweepHitNormal       = default;
                float closestSweepHitDistance       = 0f;
                Collider closestSweepHitCollider    = null;

                if (CheckMovementInitialOverlaps)
                {
                    int numOverlaps = CharacterCollisionsOverlap(tmpMovedPosition, _transientRotation, _internalProbedColliders);
                    if (numOverlaps > 0)
                    {
                        closestSweepHitDistance = 0f;

                        float mostObstructingOverlapNormalDotProduct = 2f;

                        for (int i = 0; i < numOverlaps; i++)
                        {
                            Collider tmpCollider = _internalProbedColliders[i];

                            if (!Physics.ComputePenetration(Capsule, tmpMovedPosition, _transientRotation,
                                    tmpCollider, tmpCollider.transform.position, tmpCollider.transform.rotation,
                                    out Vector3 resolutionDirection, out float resolutionDistance))
                                continue;
                            
                            float dotProduct = Vector3.Dot(remainingMovementDirection, resolutionDirection);
                            if (dotProduct < 0f && dotProduct < mostObstructingOverlapNormalDotProduct)
                            {
                                mostObstructingOverlapNormalDotProduct = dotProduct;

                                closestSweepHitNormal   = resolutionDirection;
                                closestSweepHitCollider = tmpCollider;
                                closestSweepHitPoint    = tmpMovedPosition
                                                          + _transientRotation * CharacterTransformToCapsuleCenter
                                                          + resolutionDirection * resolutionDistance;

                                foundClosestHit = true;
                            }
                        }
                    }
                }

                if (!foundClosestHit && CharacterCollisionsSweep(tmpMovedPosition, _transientRotation,
                        remainingMovementDirection, remainingMovementMagnitude + CollisionOffset,
                        out RaycastHit closestSweepHit, _internalCharacterHits) > 0)
                {
                    closestSweepHitNormal       = closestSweepHit.normal;
                    closestSweepHitDistance     = closestSweepHit.distance;
                    closestSweepHitCollider     = closestSweepHit.collider;
                    closestSweepHitPoint        = closestSweepHit.point;

                    foundClosestHit = true;
                }

                if (foundClosestHit)
                {
                    // 计算本次迭代产生的位移
                    Vector3 sweepMovement = remainingMovementDirection * 
                                            Mathf.Max(0f, closestSweepHitDistance - CollisionOffset);
                    tmpMovedPosition += sweepMovement;
                    remainingMovementMagnitude -= sweepMovement.magnitude;

                    // 评估该命中是否稳定
                    var moveHitStabilityReport = new HitStabilityReport();
                    EvaluateHitStability(closestSweepHitCollider, closestSweepHitNormal, closestSweepHitPoint,
                        tmpMovedPosition, _transientRotation, transientVelocity, ref moveHitStabilityReport);

                    // 处理踏上高于胶囊体底部半径的台阶点
                    bool foundValidStepHit = false;
                    if (_solveGrounding && StepHandling != StepHandlingMethod.None && moveHitStabilityReport.ValidStepDetected)
                    {
                        float obstructionCorrelation = Mathf.Abs(Vector3.Dot(closestSweepHitNormal, _characterUp));
                        if (obstructionCorrelation <= CorrelationForVerticalObstruction)
                        {
                            Vector3 stepForwardDirection = Vector3.ProjectOnPlane(-closestSweepHitNormal, _characterUp).normalized;
                            Vector3 stepCastStartPoint = tmpMovedPosition + stepForwardDirection
                                                         * SteppingForwardDistance + _characterUp * MaxStepHeight;

                            // 从台阶高度顶部向下投射
                            int nbStepHits = CharacterCollisionsSweep(stepCastStartPoint, _transientRotation,
                                -_characterUp, MaxStepHeight, out RaycastHit _, _internalCharacterHits, 0f, true); 

                            // 检测是否命中对应被踩上的碰撞体
                            for (int i = 0; i < nbStepHits; i++)
                            {
                                if (_internalCharacterHits[i].collider != moveHitStabilityReport.SteppedCollider)
                                    continue;
                                
                                Vector3 endStepPosition = stepCastStartPoint + -_characterUp *
                                    (_internalCharacterHits[i].distance - CollisionOffset);
                                
                                tmpMovedPosition     = endStepPosition;
                                foundValidStepHit    = true;

                                // 在台阶处把速度投影到地面法线上
                                transientVelocity = Vector3.ProjectOnPlane(transientVelocity, CharacterUp);
                                remainingMovementDirection = transientVelocity.normalized;

                                break;
                            }
                        }
                    }

                    // 处理移动解算
                    if (!foundValidStepHit)
                    {
                        Vector3 obstructionNormal = 
                            GetObstructionNormal(closestSweepHitNormal, moveHitStabilityReport.IsStable);

                        // 移动命中回调
                        CharacterController.OnMovementHit(closestSweepHitCollider, closestSweepHitNormal,
                            closestSweepHitPoint, ref moveHitStabilityReport);

                        // 记录刚体命中
                        if (InteractiveRigidbodyHandling && closestSweepHitCollider.attachedRigidbody)
                        {
                            StoreRigidbodyHit(closestSweepHitCollider.attachedRigidbody, transientVelocity,
                                closestSweepHitPoint, obstructionNormal, moveHitStabilityReport);
                        }

                        bool stableOnHit = moveHitStabilityReport.IsStable && !MustUnground();
                        Vector3 velocityBeforeProj = transientVelocity;

                        // 为下一次迭代投影速度
                        InternalHandleVelocityProjection(stableOnHit,
                            obstructionNormal, ref sweepState,
                            previousHitIsStable, previousVelocity, previousObstructionNormal, 
                            ref transientVelocity, ref remainingMovementMagnitude, ref remainingMovementDirection);

                        previousHitIsStable         = stableOnHit;
                        previousVelocity            = velocityBeforeProj;
                        previousObstructionNormal   = obstructionNormal;
                    }
                }
                // 如果什么也没命中……
                else
                    hitSomethingThisSweepIteration = false;

                
                // 超过允许的最大扫掠次数的安全措施
                sweepsMade++;
                if (sweepsMade <= MaxMovementIterations) continue;
                
                if (KillRemainingMovementWhenExceedMaxMovementIterations)
                    remainingMovementMagnitude = 0f;

                if (KillVelocityWhenExceedMaxMovementIterations)
                    transientVelocity = _cachedZeroVector;
                
                wasCompleted = false;
            }

            // 用剩余位移移动位置
            tmpMovedPosition += remainingMovementDirection * remainingMovementMagnitude;
            _transientPosition = tmpMovedPosition;

            return wasCompleted;
        }

        // 把"撞到的面法线"转换成"实际用来阻挡移动的法线"。
        //
        // 为什么要转换：角色贴着地面走时撞到墙，如果直接用法线投影，
        // 速度的竖直分量会被保留，导致角色沿着墙面往上"爬"一点再掉下来。
        // 这里把法线绕角色上方向旋转，使其与地面法线垂直，投影后就只剩水平分量的削减。
        //
        // 命中稳定（可站立）时不做转换——此时就应该沿真实表面重新定向。
        /// <summary>
        /// 根据当前接地状态，获取用于移动阻挡的有效法线。
        /// </summary>
        private Vector3 GetObstructionNormal(Vector3 hitNormal, bool stableOnHit)
        {
            // 查找命中/阻挡/偏移法线
            Vector3 obstructionNormal = hitNormal;
            if (GroundingStatus.IsStableOnGround && !MustUnground() && !stableOnHit)
            {
                Vector3 obstructionLeftAlongGround = 
                    Vector3.Cross(GroundingStatus.GroundNormal, obstructionNormal).normalized;
                
                obstructionNormal = Vector3.Cross(obstructionLeftAlongGround, _characterUp).normalized;
            }

            // 处理平行法线叉乘结果为 0 的情况
            if (obstructionNormal.sqrMagnitude == 0f)
                obstructionNormal = hitNormal;

            return obstructionNormal;
        }

        /// <summary>
        /// 记录一次刚体命中，以便稍后处理。
        /// </summary>
        private void StoreRigidbodyHit(Rigidbody hitRigidbody, Vector3 hitVelocity, Vector3 hitPoint, Vector3 obstructionNormal, HitStabilityReport hitStabilityReport)
        {
            if (_rigidbodyProjectionHitCount >= _internalRigidbodyProjectionHits.Length) return;

            if (hitRigidbody.GetComponent<KinematicCharacterMotor>()) return;
            
            var rph = new RigidbodyProjectionHit
            {
                Rigidbody           = hitRigidbody,
                HitPoint            = hitPoint,
                EffectiveHitNormal  = obstructionNormal,
                HitVelocity         = hitVelocity,
                StableOnHit         = hitStabilityReport.IsStable
            };

            _internalRigidbodyProjectionHits[_rigidbodyProjectionHitCount] = rph;
            _rigidbodyProjectionHitCount++;
        }

        public void SetTransientPosition(Vector3 newPos)
        {
            _transientPosition = newPos;
        }

        // 移动状态机：决定"撞到第二个面"时是滑走、被卡住、还是清零。
        //
        //   Initial → 第一次撞：直接投影到阻挡面上
        //   AfterFirstHit → 第二次撞：先用 EvaluateCrease 判断是不是凹角，
        //                   是凹角则把速度约束到交线（能沿墙角滑下去），
        //                   且若角色本来站在地上，直接判定为"夹角"并清零（防止卡墙角抖动）
        //   FoundBlockingCrease → 第三次撞：清零
        //   FoundBlockingCorner → 已经清零，后续命中全部忽略
        //
        // 末尾按"投影后速度 / 投影前速度"的比例缩减剩余位移——因为速度变慢了，
        // 本 tick 还能走的距离也要同比变短。这是很多手写控制器会漏掉的一步。
        /// <summary>
        /// 检测到命中时处理移动投影。
        /// </summary>
        private void InternalHandleVelocityProjection(bool stableOnHit, Vector3 obstructionNormal,
            ref MovementSweepState sweepState, bool previousHitIsStable, Vector3 previousVelocity, Vector3 previousObstructionNormal,
            ref Vector3 transientVelocity, ref float remainingMovementMagnitude, ref Vector3 remainingMovementDirection)
        {
            if (transientVelocity.sqrMagnitude <= 0f)
                return;

            Vector3 velocityBeforeProjection = transientVelocity;

            if (stableOnHit)
            {
                LastMovementIterationFoundAnyGround = true;
                HandleVelocityProjection(ref transientVelocity, obstructionNormal, true);
            }
            else
            {
                switch (sweepState)
                {
                    default:
                    // 处理投影
                    case MovementSweepState.Initial:
                        HandleVelocityProjection(ref transientVelocity, obstructionNormal, false);
                        sweepState = MovementSweepState.AfterFirstHit;
                        break;
                    
                    // 阻挡性交线（crease）处理
                    case MovementSweepState.AfterFirstHit:
                        EvaluateCrease(transientVelocity, previousVelocity, obstructionNormal, previousObstructionNormal,
                            false, previousHitIsStable, GroundingStatus.IsStableOnGround && !MustUnground(),
                            out bool foundCrease, out Vector3 creaseDirection);

                        if (foundCrease)
                        {
                            if (GroundingStatus.IsStableOnGround && !MustUnground())
                            {
                                transientVelocity = _cachedZeroVector;
                                sweepState = MovementSweepState.FoundBlockingCorner;
                            }
                            else
                            {
                                transientVelocity = Vector3.Project(transientVelocity, creaseDirection);
                                sweepState = MovementSweepState.FoundBlockingCrease;
                            }
                        }
                        else
                            HandleVelocityProjection(ref transientVelocity, obstructionNormal, false);
                        
                        break;

                    // 阻挡性夹角（corner）处理
                    case MovementSweepState.FoundBlockingCrease:
                        transientVelocity = _cachedZeroVector;
                        sweepState = MovementSweepState.FoundBlockingCorner;
                        break;
                    
                    case MovementSweepState.FoundBlockingCorner:
                        break;
                }
            }

            float newVelocityFactor = transientVelocity.magnitude / velocityBeforeProjection.magnitude;
            remainingMovementMagnitude *= newVelocityFactor;
            remainingMovementDirection = transientVelocity.normalized;
        }

        // 凹角（crease）判定：两面墙相交时，速度是否可以沿着交线滑动。
        //
        // 做法：把两个法线都投影到"与交线垂直的平面"上变成二维，
        // 再看当前速度方向是否同时被两个面挡住（即速度指向两面夹角的内部）。
        // 如果是，则允许速度沿交线方向保留；否则不算凹角，按普通投影处理。
        //
        // 这一步存在的原因：连续投影到两个面上会把速度压成零，
        // 角色沿墙角斜着走时就会"粘住"。允许沿交线滑是贴墙移动手感的关键。
        private static void EvaluateCrease(Vector3 currentCharacterVelocity, Vector3 previousCharacterVelocity,
            Vector3 currentHitNormal, Vector3 previousHitNormal, bool currentHitIsStable, bool previousHitIsStable,
            bool characterIsStable, out bool isValidCrease, out Vector3 creaseDirection)
        {
            isValidCrease = false;
            creaseDirection = default;

            if (characterIsStable && currentHitIsStable && previousHitIsStable) return;
            
            Vector3 tmpBlockingCreaseDirection = Vector3.Cross(currentHitNormal, previousHitNormal).normalized;
            float dotPlanes = Vector3.Dot(currentHitNormal, previousHitNormal);
            bool isVelocityConstrainedByCrease = false;

            // 两个平面相同时跳过计算
            if (dotPlanes < 0.999f)
            {
                // TODO: 这一整段能不能更简单？（用 2D 投影之类的方法）
                Vector3 normalAOnCreasePlane = Vector3.ProjectOnPlane(currentHitNormal, tmpBlockingCreaseDirection).normalized;
                Vector3 normalBOnCreasePlane = Vector3.ProjectOnPlane(previousHitNormal, tmpBlockingCreaseDirection).normalized;
                float dotPlanesOnCreasePlane = Vector3.Dot(normalAOnCreasePlane, normalBOnCreasePlane);

                Vector3 enteringVelocityDirectionOnCreasePlane = Vector3.ProjectOnPlane(previousCharacterVelocity, tmpBlockingCreaseDirection).normalized;

                if (dotPlanesOnCreasePlane <= Vector3.Dot(-enteringVelocityDirectionOnCreasePlane, normalAOnCreasePlane) + 0.001f &&
                    dotPlanesOnCreasePlane <= Vector3.Dot(-enteringVelocityDirectionOnCreasePlane, normalBOnCreasePlane) + 0.001f)
                {
                    isVelocityConstrainedByCrease = true;
                }
            }

            if (!isVelocityConstrainedByCrease) return;
            
            // 翻转交线方向，使其代表我们速度真正会被投影到的方向
            if (Vector3.Dot(tmpBlockingCreaseDirection, currentCharacterVelocity) < 0f)
                tmpBlockingCreaseDirection = -tmpBlockingCreaseDirection;

            isValidCrease = true;
            creaseDirection = tmpBlockingCreaseDirection;
        }

        // 碰撞响应的策略点（virtual，可被继承覆盖）。
        //
        // 分四种情形，本质都是"把速度重新定向到允许的方向，并尽量保持大小"：
        //   · 站在地上 + 撞到可站立面（斜坡）：转到该面的切向，不损失速度 → 能顺畅上下坡
        //   · 站在地上 + 撞到墙：先转到"沿地面方向"的阻挡面，再投影掉法线分量 → 沿墙滑
        //   · 在空中 + 撞到可站立面：先投影掉竖直分量，再贴合表面 → 落地不弹
        //   · 在空中 + 撞到墙：直接投影 → 普通滑墙
        //
        // 想实现"贴墙摩擦/冰面/磁力吸附"就覆盖这个函数。
        /// <summary>
        /// 允许你改写速度在阻挡物上的投影方式。
        /// </summary>
        protected virtual void HandleVelocityProjection(ref Vector3 velocity, Vector3 obstructionNormal, bool stableOnHit)
        {
            if (GroundingStatus.IsStableOnGround && !MustUnground())
            {
                // 稳定坡面：直接重新定向移动，不产生损失
                if (stableOnHit)
                    velocity = GetDirectionTangentToSurface(velocity, obstructionNormal) * velocity.magnitude;
                // 阻挡命中：沿接地平面把移动投影到阻挡物上
                else
                {
                    Vector3 obstructionRightAlongGround = Vector3.Cross(obstructionNormal, GroundingStatus.GroundNormal).normalized;
                    Vector3 obstructionUpAlongGround = Vector3.Cross(obstructionRightAlongGround, obstructionNormal).normalized;
                    velocity = GetDirectionTangentToSurface(velocity, obstructionUpAlongGround) * velocity.magnitude;
                    velocity = Vector3.ProjectOnPlane(velocity, obstructionNormal);
                }
            }
            else
            {
                if (stableOnHit)
                {
                    // 处理稳定落地
                    velocity = Vector3.ProjectOnPlane(velocity, CharacterUp);
                    velocity = GetDirectionTangentToSurface(velocity, obstructionNormal) * velocity.magnitude;
                }
                // 处理一般阻挡
                else
                    velocity = Vector3.ProjectOnPlane(velocity, obstructionNormal);
            }
        }

        // 预留钩子：SimulatedDynamic 模式下，你可以在这里根据命中信息进一步修改速度。
        // 基类实现为空，官方示例也没用，属于留给使用者的扩展点。
        /// <summary>
        /// 允许你改写命中刚体被推动/交互的方式。
        /// 如果这种交互会影响角色速度，需要修改的是 ProcessedVelocity。
        /// </summary>
        protected virtual void HandleSimulatedRigidbodyInteraction(ref Vector3 processedVelocity, RigidbodyProjectionHit hit, float deltaTime)
        {
            
        }

        // 结算本 tick 撞到的所有刚体（每 tick 只会处理一次，防止重复施力）。
        //
        // 核心是"质量比"模型：角色质量 /（角色质量 + 刚体质量），
        // 这个比例决定动量怎么在两个物体间分配。三个特例：
        //   · 命中非动态刚体（kinematic 且不是角色）→ 比例为 0，角色单方面被挡
        //   · RigidbodyInteractionType == Kinematic → 比例为 1，角色不减速，箱子被推开
        //   · 命中另一个角色 → 双方按各自的 SimulatedCharacterMass 互相推（但见下）
        //
        // 注意：命中角色时写的是对方的 BaseVelocity，而对方本 tick 的 Phase2 可能已经跑完，
        // 所以角色互推在本版本实际不生效（StoreRigidbodyHit 里还会把带 Motor 的刚体过滤掉）。
        /// <summary>
        /// 把刚体命中纳入速度计算。
        /// </summary>
        private void ProcessVelocityForRigidbodyHits(ref Vector3 processedVelocity, float deltaTime)
        {
            for (int i = 0; i < _rigidbodyProjectionHitCount; i++)
            {
                RigidbodyProjectionHit bodyHit = _internalRigidbodyProjectionHits[i];

                if (!bodyHit.Rigidbody || _rigidbodiesPushedThisMove.Contains(bodyHit.Rigidbody)) continue;

                if (_internalRigidbodyProjectionHits[i].Rigidbody == _attachedRigidbody) continue;
                
                // 记住我们命中了这个刚体
                _rigidbodiesPushedThisMove.Add(bodyHit.Rigidbody);

                float characterMass         = SimulatedCharacterMass;
                Vector3 characterVelocity   = bodyHit.HitVelocity;

                var hitCharacterMotor       = bodyHit.Rigidbody.GetComponent<KinematicCharacterMotor>();
                bool hitBodyIsCharacter     = hitCharacterMotor != null;
                bool hitBodyIsDynamic       = !bodyHit.Rigidbody.isKinematic;
                float hitBodyMass           = bodyHit.Rigidbody.mass;
                Vector3 hitBodyVelocity     = bodyHit.Rigidbody.velocity;
                
                if (hitBodyIsCharacter)
                {
                    hitBodyMass             = hitCharacterMotor.SimulatedCharacterMass;
                    hitBodyVelocity         = hitCharacterMotor.BaseVelocity;
                }
                else if (!hitBodyIsDynamic)
                {
                    var physicsMover = bodyHit.Rigidbody.GetComponent<PhysicsMover>();
                    
                    if (physicsMover)
                        hitBodyVelocity = physicsMover.Velocity;
                }

                // 计算角色质量占总质量的比例
                float characterToBodyMassRatio;
                if (characterMass + hitBodyMass > 0f)
                {
                    characterToBodyMassRatio = characterMass / (characterMass + hitBodyMass);
                }
                else
                {
                    characterToBodyMassRatio = 0.5f;
                }

                // 命中的是非动态刚体
                if (!hitBodyIsDynamic)
                {
                    characterToBodyMassRatio = 0f;
                }
                // 模拟运动学刚体的交互
                else if (RigidbodyInteractionType == RigidbodyInteractionType.Kinematic && !hitBodyIsCharacter)
                {
                    characterToBodyMassRatio = 1f;
                }

                ComputeCollisionResolutionForHitBody(bodyHit.EffectiveHitNormal,
                    characterVelocity, hitBodyVelocity, characterToBodyMassRatio,
                    out Vector3 velocityChangeOnCharacter, out Vector3 velocityChangeOnBody);

                processedVelocity += velocityChangeOnCharacter;

                if (hitBodyIsCharacter)
                {
                    hitCharacterMotor.BaseVelocity += velocityChangeOnCharacter;
                }
                else if (hitBodyIsDynamic)
                {
                    bodyHit.Rigidbody.AddForceAtPosition(velocityChangeOnBody, bodyHit.HitPoint, ForceMode.VelocityChange);
                }

                if (RigidbodyInteractionType == RigidbodyInteractionType.SimulatedDynamic)
                    HandleSimulatedRigidbodyInteraction(ref processedVelocity, bodyHit, deltaTime);
            }
        }

        // 一维弹性碰撞的简化版：只沿命中法线交换动量，切向不管。
        //
        // 两个输出：角色速度的增量、刚体速度的增量（后者用 AddForce 施加）。
        // 前半段"恢复被投影掉的速度"很关键：否则角色撞箱子时会被自己的投影永久减速，
        // 表现为"推箱子越推越慢"。
        private void ComputeCollisionResolutionForHitBody(Vector3 hitNormal,
            Vector3 characterVelocity, Vector3 bodyVelocity, float characterToBodyMassRatio,
            out Vector3 velocityChangeOnCharacter, out Vector3 velocityChangeOnBody)
        {
            velocityChangeOnCharacter   = default;
            velocityChangeOnBody        = default;

            float bodyToCharacterMassRatio               = 1f - characterToBodyMassRatio;
            float characterVelocityMagnitudeOnHitNormal  = Vector3.Dot(characterVelocity, hitNormal);
            float bodyVelocityMagnitudeOnHitNormal       = Vector3.Dot(bodyVelocity, hitNormal);

            // 如果角色速度原本是迎着阻挡物的，
            // 就恢复移动阶段被投影掉的那部分速度
            if (characterVelocityMagnitudeOnHitNormal < 0f)
            {
                Vector3 restoredCharacterVelocity = hitNormal * characterVelocityMagnitudeOnHitNormal;
                velocityChangeOnCharacter += restoredCharacterVelocity;
            }

            // 在两个刚体上求解冲量速度，
            // 但仅当该刚体的速度会以任何方式对角色产生阻力时
            if (bodyVelocityMagnitudeOnHitNormal > characterVelocityMagnitudeOnHitNormal)
            {
                Vector3 relativeImpactVelocity = hitNormal * (bodyVelocityMagnitudeOnHitNormal - characterVelocityMagnitudeOnHitNormal);
                velocityChangeOnCharacter += relativeImpactVelocity * bodyToCharacterMassRatio;
                velocityChangeOnBody += -relativeImpactVelocity * characterToBodyMassRatio;
            }
        }

        // 碰撞过滤的入口。先排除自身胶囊，再交给规则层（InternalIsColliderValidForCollisions）。
        // 调用频率极高（每次 sweep/overlap/raycast 的每个命中都会问），必须保持廉价。
        /// <summary>
        /// 判断输入的碰撞体是否可以参与碰撞处理。
        /// </summary>
        /// <returns> 碰撞体有效时返回 true </returns>
        private bool CheckIfColliderValidForCollisions(Collider coll)
        {
            // 忽略自身
            if (coll == Capsule)
                return false;

            return InternalIsColliderValidForCollisions(coll);
        }

        /// <summary>
        /// 判断输入的碰撞体是否可以参与碰撞处理。
        /// </summary>
        private bool InternalIsColliderValidForCollisions(Collider coll)
        {
            Rigidbody colliderAttachedRigidbody = coll.attachedRigidbody;
            if (colliderAttachedRigidbody)
            {
                bool isRigidbodyKinematic = colliderAttachedRigidbody.isKinematic;

                // 如果移动来自 AttachedRigidbody，则忽略 AttachedRigidbody
                if (_isMovingFromAttachedRigidbody && (!isRigidbodyKinematic || colliderAttachedRigidbody == _attachedRigidbody))
                {
                    return false;
                }

                // 当 RigidbodyInteractionType 为 kinematic 时，不与动态刚体碰撞
                if (RigidbodyInteractionType == RigidbodyInteractionType.Kinematic && !isRigidbodyKinematic)
                {
                    // 唤醒刚体
                    if (coll.attachedRigidbody)
                        coll.attachedRigidbody.WakeUp();

                    return false;
                }
            }

            // 自定义检查
            bool colliderValid = CharacterController.IsColliderValidForCollisions(coll);
            
            return colliderValid;
        }

        // ══════════════════════════════════════════════════════════
        // 稳定性判定的四层（顺序固定，越后越贵）：
        //   1) 坡度：法线与角色上方向夹角 <= MaxStableSlopeAngle
        //   2) 边缘：从命中点往内外各打一条竖直短线，两侧稳定性不同 → 判定为悬崖边，
        //      再结合"是否朝空侧移动 / 离边缘多远"决定还站不站得住
        //   3) 台阶：不稳定时再试"把脚抬高到 MaxStepHeight 有没有落脚点"
        //   4) Controller：ProcessHitStabilityReport 做最终否决（冰面、特殊表面）
        //
        // 注意第 3 层只对静态几何生效——动态刚体上不能踩台阶（会跟着跑）。
        // 结果写回 ref stabilityReport，调用方只读 IsStable。
        // ══════════════════════════════════════════════════════════
        /// <summary>
        /// 判断在一次给定命中上，Motor 是否被视为稳定。
        /// </summary>
        private void EvaluateHitStability(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint, Vector3 atCharacterPosition,
            Quaternion atCharacterRotation, Vector3 withCharacterVelocity, ref HitStabilityReport stabilityReport)
        {
            if (!_solveGrounding)
            {
                stabilityReport.IsStable = false;
                return;
            }

            Vector3 atCharacterUp       = atCharacterRotation * _cachedWorldUp;
            Vector3 innerHitDirection   = Vector3.ProjectOnPlane(hitNormal, atCharacterUp).normalized;

            stabilityReport.IsStable = IsStableOnNormal(hitNormal);

            stabilityReport.FoundInnerNormal    = false;
            stabilityReport.FoundOuterNormal    = false;
            stabilityReport.InnerNormal         = hitNormal;
            stabilityReport.OuterNormal         = hitNormal;

            // 边缘处理
            if (LedgeAndDenivelationHandling)
            {
                float ledgeCheckHeight = MinDistanceForLedge;
                
                if (StepHandling != StepHandlingMethod.None)
                    ledgeCheckHeight = MaxStepHeight;

                bool isStableLedgeInner = false;
                bool isStableLedgeOuter = false;

                if (CharacterCollisionsRaycast(hitPoint + atCharacterUp * SecondaryProbesVertical + innerHitDirection
                        * SecondaryProbesHorizontal, -atCharacterUp, ledgeCheckHeight + SecondaryProbesVertical,
                        out RaycastHit innerLedgeHit, _internalCharacterHits) > 0)
                {
                    Vector3 innerLedgeNormal            = innerLedgeHit.normal;
                    stabilityReport.InnerNormal         = innerLedgeNormal;
                    stabilityReport.FoundInnerNormal    = true;
                    isStableLedgeInner = IsStableOnNormal(innerLedgeNormal);
                }

                if (CharacterCollisionsRaycast(hitPoint + atCharacterUp * SecondaryProbesVertical + -innerHitDirection
                        * SecondaryProbesHorizontal, -atCharacterUp, ledgeCheckHeight + SecondaryProbesVertical,
                        out RaycastHit outerLedgeHit, _internalCharacterHits) > 0)
                {
                    Vector3 outerLedgeNormal            = outerLedgeHit.normal;
                    stabilityReport.OuterNormal         = outerLedgeNormal;
                    stabilityReport.FoundOuterNormal    = true;
                    isStableLedgeOuter = IsStableOnNormal(outerLedgeNormal);
                }

                stabilityReport.LedgeDetected = isStableLedgeInner != isStableLedgeOuter;
                if (stabilityReport.LedgeDetected)
                {
                    stabilityReport.IsOnEmptySideOfLedge = isStableLedgeOuter && !isStableLedgeInner;
                    stabilityReport.LedgeGroundNormal = isStableLedgeOuter ? stabilityReport.OuterNormal : stabilityReport.InnerNormal;
                    stabilityReport.LedgeRightDirection = Vector3.Cross(hitNormal, stabilityReport.LedgeGroundNormal).normalized;
                    stabilityReport.LedgeFacingDirection = Vector3.ProjectOnPlane(Vector3.Cross(
                        stabilityReport.LedgeGroundNormal, stabilityReport.LedgeRightDirection), CharacterUp).normalized;
                    stabilityReport.DistanceFromLedge = Vector3.ProjectOnPlane(hitPoint - (atCharacterPosition
                        + atCharacterRotation * _characterTransformToCapsuleBottom), atCharacterUp).magnitude;
                    stabilityReport.IsMovingTowardsEmptySideOfLedge = Vector3.Dot
                        (withCharacterVelocity.normalized, stabilityReport.LedgeFacingDirection) > 0f;
                }

                if (stabilityReport.IsStable)
                    stabilityReport.IsStable = IsStableWithSpecialCases(ref stabilityReport, withCharacterVelocity);
            }

            // 台阶处理
            if (StepHandling != StepHandlingMethod.None && !stabilityReport.IsStable)
            {
                // 动态刚体不支持踏台阶
                Rigidbody hitRigidbody = hitCollider.attachedRigidbody;
                if (!(hitRigidbody && !hitRigidbody.isKinematic))
                {
                    DetectSteps(atCharacterPosition, atCharacterRotation, hitPoint, innerHitDirection, ref stabilityReport);

                    if (stabilityReport.ValidStepDetected)
                        stabilityReport.IsStable = true;
                }
            }

            CharacterController.ProcessHitStabilityReport(hitCollider, hitNormal, hitPoint,
                atCharacterPosition, atCharacterRotation, ref stabilityReport);
        }

        // 台阶检测：从"命中点上方 MaxStepHeight"处向下 sweep，看能不能落在一个可站立的面上。
        //
        // Standard 模式只做这一种；Extra 模式还会从"角色正前方 MinRequiredStepDepth 处"再探一次，
        // 从而支持踩上比胶囊半径更窄的台阶（代价是多一次 sweep）。
        //
        // 检测成功后只写 stabilityReport.ValidStepDetected + SteppedCollider，
        // 真正的"抬脚"发生在 InternalCharacterMove 里（用 SteppedCollider 精确定位落点）。
        private void DetectSteps(Vector3 characterPosition, Quaternion characterRotation,
            Vector3 hitPoint, Vector3 innerHitDirection, ref HitStabilityReport stabilityReport)
        {
            Vector3 characterUp = characterRotation * _cachedWorldUp;
            Vector3 verticalCharToHit = Vector3.Project(hitPoint - characterPosition, characterUp);
            Vector3 horizontalCharToHitDirection = Vector3.ProjectOnPlane(hitPoint - characterPosition, characterUp).normalized;
            Vector3 stepCheckStartPos = hitPoint - verticalCharToHit + characterUp * MaxStepHeight + horizontalCharToHitDirection * (CollisionOffset * 3f);

            // 在命中点用胶囊体投射做外侧台阶检测
            int nbStepHits = CharacterCollisionsSweep(stepCheckStartPos, characterRotation, -characterUp,
                MaxStepHeight + CollisionOffset, out RaycastHit _, _internalCharacterHits, 0f, true);

            // 检测命中位置处的重叠与阻挡
            if (CheckStepValidity(nbStepHits, characterPosition, characterRotation, innerHitDirection, stepCheckStartPos, out Collider tmpCollider))
            {
                stabilityReport.ValidStepDetected = true;
                stabilityReport.SteppedCollider = tmpCollider;
            }

            if (StepHandling != StepHandlingMethod.Extra || stabilityReport.ValidStepDetected) return;
            
            // 在命中点用胶囊体投射做最小可达台阶检测
            stepCheckStartPos = characterPosition + characterUp * MaxStepHeight + -innerHitDirection * MinRequiredStepDepth;
            nbStepHits = CharacterCollisionsSweep(stepCheckStartPos, characterRotation, -characterUp,
                MaxStepHeight - CollisionOffset, out RaycastHit _, _internalCharacterHits, 0f, true);

            // 检测命中位置处的重叠与阻挡
            if (!CheckStepValidity(nbStepHits, characterPosition, characterRotation,
                    innerHitDirection, stepCheckStartPos, out tmpCollider)) return;
            
            stabilityReport.ValidStepDetected = true;
            stabilityReport.SteppedCollider = tmpCollider;
        }

        // 校验某个候选台阶是否真的能踩：按"由远及近"尝试每个命中，
        // 依次确认四件事——
        //   1) 站在那个位置不与任何东西重叠；
        //   2) 台阶外缘的坡面可站立；
        //   3) 从当前位置垂直往上抬不会被挡住（有天花板就不行）；
        //   4) 台阶内缘也可站立（或允许无稳定地面时直接放行）。
        //
        // 这是 KCC 里嵌套最深的一段，读不懂可以先跳过：它只影响"能不能上台阶"的边界情况。
        private bool CheckStepValidity(int nbStepHits, Vector3 characterPosition, Quaternion characterRotation,
            Vector3 innerHitDirection, Vector3 stepCheckStartPos, out Collider hitCollider)
        {
            hitCollider = null;
            Vector3 characterUp = characterRotation * Vector3.up;

            // 找出用于踏上台阶的最远有效命中
            while (nbStepHits > 0)
            {
                // 在剩余命中中取最远的那个
                var farthestHit = new RaycastHit();
                float farthestDistance = 0f;
                int farthestIndex = 0;
                
                for (int i = 0; i < nbStepHits; i++)
                {
                    float hitDistance = _internalCharacterHits[i].distance;
                    if (hitDistance < farthestDistance) continue;
                    
                    farthestDistance    = hitDistance;
                    farthestHit         = _internalCharacterHits[i];
                    farthestIndex       = i;
                }

                Vector3 characterPositionAtHit = stepCheckStartPos + -characterUp * (farthestHit.distance - CollisionOffset);

                int atStepOverlaps = CharacterCollisionsOverlap(characterPositionAtHit, characterRotation, _internalProbedColliders);
                if (atStepOverlaps <= 0)
                {
                    // 在台阶位置检测外侧命中的坡面法线稳定性
                    if (CharacterCollisionsRaycast(farthestHit.point + characterUp * 
                            SecondaryProbesVertical + -innerHitDirection * SecondaryProbesHorizontal,
                            -characterUp, MaxStepHeight + SecondaryProbesVertical, out RaycastHit outerSlopeHit,
                            _internalCharacterHits, true) > 0)
                    {
                        if (IsStableOnNormal(outerSlopeHit.normal))
                        {
                            // 向上投射，检测移动到那里是否有阻挡
                            if (CharacterCollisionsSweep(characterPosition, characterRotation, characterUp,
                                    MaxStepHeight - farthestHit.distance, out RaycastHit _, _internalCharacterHits) <= 0)
                            {
                                // 做内侧台阶检测……
                                bool innerStepValid = false;
                                RaycastHit innerStepHit;

                                if (AllowSteppingWithoutStableGrounding)
                                {
                                    innerStepValid = true;
                                }
                                else
                                {
                                    // 在台阶高度处的胶囊体中心
                                    if (CharacterCollisionsRaycast(characterPosition + Vector3.Project
                                                (characterPositionAtHit - characterPosition, characterUp), -characterUp,
                                            MaxStepHeight, out innerStepHit, _internalCharacterHits, true) > 0)
                                    {
                                        if (IsStableOnNormal(innerStepHit.normal))
                                            innerStepValid = true;
                                    }
                                }

                                if (!innerStepValid)
                                {
                                    // 在台阶点的内侧
                                    if (CharacterCollisionsRaycast(farthestHit.point + innerHitDirection
                                            * SecondaryProbesHorizontal, -characterUp, MaxStepHeight, out innerStepHit,
                                            _internalCharacterHits, true) > 0)
                                    {
                                        if (IsStableOnNormal(innerStepHit.normal))
                                            innerStepValid = true;
                                    }
                                }

                                // 台阶的最终校验
                                if (innerStepValid)
                                {
                                    hitCollider = farthestHit.collider;
                                    return true;
                                }
                            }
                        }
                    }
                }

                // 若不是有效台阶则丢弃该命中
                nbStepHits--;
                if (farthestIndex < nbStepHits)
                {
                    _internalCharacterHits[farthestIndex] = _internalCharacterHits[nbStepHits];
                }
            }

            return false;
        }

        // 求"刚体上某一点"的真实线速度 = 刚体线速度 + 该点绕质心旋转产生的切向速度。
        //
        // 对 kinematic 刚体（含 PhysicsMover）特殊处理：它们的 velocity 恒为 0，
        // 所以改读 PhysicsMover 自己算好的 Velocity / AngularVelocity。
        // —— 这就是角色能被移动平台"带着走"的数据来源。
        //
        // atPoint 传角色当前位置，于是旋转平台上的角色会得到"脚下那一点"的速度，
        // 自然绕着平台中心走，而不是整体平移。
        /// <summary>
        /// 获取刚体上某一点的真实线速度（考虑旋转速度）。
        /// </summary>
        public static void GetVelocityFromRigidbodyMovement(Rigidbody interactiveRigidbody,
            Vector3 atPoint, float deltaTime, out Vector3 linearVelocity, out Vector3 angularVelocity)
        {
            if (deltaTime > 0f)
            {
                linearVelocity  = interactiveRigidbody.velocity;
                angularVelocity = interactiveRigidbody.angularVelocity;
                if (interactiveRigidbody.isKinematic)
                {
                    var physicsMover = interactiveRigidbody.GetComponent<PhysicsMover>();
                    if (physicsMover)
                    {
                        linearVelocity  = physicsMover.Velocity;
                        angularVelocity = physicsMover.AngularVelocity;
                    }
                }

                if (angularVelocity == Vector3.zero) return;
                Vector3 centerOfRotation = interactiveRigidbody.transform.TransformPoint(interactiveRigidbody.centerOfMass);
                Vector3 centerOfRotationToPoint = atPoint - centerOfRotation;
                
                Quaternion rotationFromInteractiveRigidbody = Quaternion.Euler(angularVelocity * (Mathf.Rad2Deg * deltaTime));
                Vector3 finalPointPosition = centerOfRotation + rotationFromInteractiveRigidbody * centerOfRotationToPoint;
                linearVelocity += (finalPointPosition - atPoint) / deltaTime;
            }
            else
            {
                linearVelocity = default;
                angularVelocity = default;
            }
        }

        // 判断一个碰撞体是否"可交互"：它必须挂着 Rigidbody，且要么是 PhysicsMover，
        // 要么是非 kinematic（动态）刚体。
        //
        // 这条规则决定了：静态几何（无 Rigidbody）永远不会被当成"可站上去的移动物体"，
        // 而普通的 kinematic 刚体（不是 PhysicsMover）也不会——因为它没有可信的速度来源。
        /// <summary>
        /// 判断一个碰撞体是否带有可交互的刚体。
        /// </summary>
        private static Rigidbody GetInteractiveRigidbody(Collider onCollider)
        {
            Rigidbody colliderAttachedRigidbody = onCollider.attachedRigidbody;
            if (!colliderAttachedRigidbody) return null;
            
            if (colliderAttachedRigidbody.gameObject.GetComponent<PhysicsMover>())
                return colliderAttachedRigidbody;

            return !colliderAttachedRigidbody.isKinematic ? colliderAttachedRigidbody : null;
        }

        /// <summary>
        /// 计算在给定 deltaTime 内把角色移动到目标位置所需的速度。
        /// 适用于你希望在 UpdateVelocity 回调里用位置而不是速度工作的场景。
        /// </summary>
        public Vector3 GetVelocityForMovePosition(Vector3 fromPosition, Vector3 toPosition, float deltaTime)
        {
            return GetVelocityFromMovement(toPosition - fromPosition, deltaTime);
        }

        public static Vector3 GetVelocityFromMovement(Vector3 movement, float deltaTime)
        {
            if (deltaTime <= 0f)
                return Vector3.zero;

            return movement / deltaTime;
        }

        // 按分量把向量"夹"进某个平面所在的象限：只要某个分量的符号与该平面方向相反就清零。
        // 这是平面约束（2.5D 游戏）用的粗暴但有效的做法：不允许角色往轴的反方向移动。
        /// <summary>
        /// 把一个向量裁剪为被限制在某个平面内。
        /// </summary>
        private void RestrictVectorToPlane(ref Vector3 vector, Vector3 toPlane)
        {
            if (vector.x > 0 != toPlane.x > 0)
                vector.x = 0;
            if (vector.y > 0 != toPlane.y > 0)
                vector.y = 0;
            if (vector.z > 0 != toPlane.z > 0)
                vector.z = 0;
        }

        // 查询原语 1/4：胶囊体重叠检测（内部版，会套用角色自己的碰撞过滤规则）。
        //
        // acceptOnlyStableGroundLayer 为 true 时，只查 StableGroundLayers 里的层，
        // 用于台阶/边缘这类"只要地面"的探测，避免被装饰物干扰。
        // 全程 NonAlloc，结果写进调用方传进来的数组，零 GC。
        /// <summary>
        /// 检测角色胶囊体是否与任何可碰撞物重叠。
        /// </summary>
        /// <returns> 返回重叠数量 </returns>
        private int CharacterCollisionsOverlap(Vector3 position, Quaternion rotation, Collider[] overlappedColliders,
            float inflate = 0f, bool acceptOnlyStableGroundLayer = false)
        {
            int queryLayers = CollidableLayers;
            if (acceptOnlyStableGroundLayer)
                queryLayers = CollidableLayers & StableGroundLayers;

            Vector3 bottom = position + rotation * _characterTransformToCapsuleBottomHemi;
            Vector3 top = position + rotation * _characterTransformToCapsuleTopHemi;
            
            if (inflate != 0f)
            {
                bottom += rotation * Vector3.down * inflate;
                top += rotation * Vector3.up * inflate;
            }

            int nbUnfilteredHits = Physics.OverlapCapsuleNonAlloc(bottom, top, Capsule.radius + inflate,
                overlappedColliders, queryLayers, QueryTriggerInteraction.Ignore);

            // 过滤掉无效的碰撞体
            int nbHits = nbUnfilteredHits;
            for (int i = nbUnfilteredHits - 1; i >= 0; i--)
            {
                if (CheckIfColliderValidForCollisions(overlappedColliders[i])) continue;
                
                nbHits--;
                
                if (i < nbHits)
                    overlappedColliders[i] = overlappedColliders[nbHits];
            }

            return nbHits;
        }

        /// <summary>
        /// 检测角色胶囊体是否与任何物体重叠。
        /// </summary>
        /// <returns> 返回重叠数量 </returns>
        public int CharacterOverlap(Vector3 position, Quaternion rotation, Collider[] overlappedColliders,
            LayerMask layers, QueryTriggerInteraction triggerInteraction, float inflate = 0f)
        {
            Vector3 bottom = position + rotation * _characterTransformToCapsuleBottomHemi;
            Vector3 top = position + rotation * _characterTransformToCapsuleTopHemi;
            if (inflate != 0f)
            {
                bottom += rotation * Vector3.down * inflate;
                top += rotation * Vector3.up * inflate;
            }

            int nbUnfilteredHits = Physics.OverlapCapsuleNonAlloc(bottom, top, Capsule.radius + inflate,
                        overlappedColliders, layers, triggerInteraction);

            // 过滤掉角色自身的胶囊体
            int nbHits = nbUnfilteredHits;
            for (int i = nbUnfilteredHits - 1; i >= 0; i--)
            {
                if (overlappedColliders[i] != Capsule) continue;
                
                nbHits--;
                
                if (i < nbHits)
                    overlappedColliders[i] = overlappedColliders[nbHits];
            }

            return nbHits;
        }

        // 查询原语 2/4：胶囊体 sweep（内部版）。
        //
        // 两个容易忽略的细节：
        //   · 起点会沿反方向回退 SweepProbingBackstepDistance，避免因为浮点误差漏掉"
        //     紧贴身前的面（贴墙时尤其明显）；返回时再把距离减回去；
        //   · 过滤阶段用"尾部交换"删除无效命中（nbHits-- 后把末尾元素挪过来），
        //     所以返回后不要假设数组前 N 个之外还有效。
        /// <summary>
        /// 扫掠胶囊体体积以检测碰撞命中。
        /// </summary>
        /// <returns> 返回命中数量 </returns>
        private int CharacterCollisionsSweep(Vector3 position, Quaternion rotation, Vector3 direction, float distance,
            out RaycastHit closestHit, RaycastHit[] hits, float inflate = 0f, bool acceptOnlyStableGroundLayer = false)
        {
            int queryLayers = CollidableLayers;
            if (acceptOnlyStableGroundLayer)
                queryLayers = CollidableLayers & StableGroundLayers;

            Vector3 bottom = position + rotation * _characterTransformToCapsuleBottomHemi - direction * SweepProbingBackstepDistance;
            Vector3 top = position + rotation * _characterTransformToCapsuleTopHemi - direction * SweepProbingBackstepDistance;
            
            if (inflate != 0f)
            {
                bottom += rotation * Vector3.down * inflate;
                top += rotation * Vector3.up * inflate;
            }

            // 胶囊体投射
            int nbUnfilteredHits = Physics.CapsuleCastNonAlloc(bottom, top, Capsule.radius + inflate, direction, hits,
                    distance + SweepProbingBackstepDistance, queryLayers, QueryTriggerInteraction.Ignore);

            // 命中过滤
            closestHit = new RaycastHit();
            float closestDistance = Mathf.Infinity;
            int nbHits = nbUnfilteredHits;
            for (int i = nbUnfilteredHits - 1; i >= 0; i--)
            {
                hits[i].distance -= SweepProbingBackstepDistance;

                RaycastHit hit = hits[i];
                float hitDistance = hit.distance;

                // 过滤掉无效命中
                if (hitDistance <= 0f || !CheckIfColliderValidForCollisions(hit.collider))
                {
                    nbHits--;
                    
                    if (i < nbHits)
                        hits[i] = hits[nbHits];
                }
                else
                {
                    // 记住最近的有效命中
                    if (hitDistance > closestDistance) continue;
                    
                    closestHit = hit;
                    closestDistance = hitDistance;
                }
            }

            return nbHits;
        }

        /// <summary>
        /// 扫掠胶囊体体积以检测命中。
        /// </summary>
        /// <returns> 返回命中数量 </returns>
        public int CharacterSweep(Vector3 position, Quaternion rotation, Vector3 direction, float distance, out RaycastHit closestHit,
            RaycastHit[] hits, LayerMask layers, QueryTriggerInteraction triggerInteraction, float inflate = 0f)
        {
            closestHit = new RaycastHit();

            Vector3 bottom = position + rotation * _characterTransformToCapsuleBottomHemi;
            Vector3 top = position + rotation * _characterTransformToCapsuleTopHemi;
            
            if (inflate != 0f)
            {
                bottom += rotation * Vector3.down * inflate;
                top += rotation * Vector3.up * inflate;
            }

            // 胶囊体投射
            int nbUnfilteredHits = Physics.CapsuleCastNonAlloc(bottom, top, Capsule.radius + inflate, direction,
                hits, distance, layers, triggerInteraction);

            // 命中过滤
            float closestDistance = Mathf.Infinity;
            int nbHits = nbUnfilteredHits;
            for (int i = nbUnfilteredHits - 1; i >= 0; i--)
            {
                RaycastHit hit = hits[i];

                // 过滤掉角色自身的胶囊体
                if (hit.distance <= 0f || hit.collider == Capsule)
                {
                    nbHits--;
                    
                    if (i < nbHits)
                        hits[i] = hits[nbHits];
                }
                else
                {
                    // 记住最近的有效命中
                    float hitDistance = hit.distance;
                    if (hitDistance > closestDistance) continue;
                    
                    closestHit = hit;
                    closestDistance = hitDistance;
                }
            }

            return nbHits;
        }

        // 查询原语 3/4：专用于地面探测的 sweep。
        //
        // 与通用 sweep 的差别：只查 StableGroundLayers、只接受有效命中、不做尾部交换
        // （直接线性扫描找最近的），并且起点回退 GroundProbingBackstepDistance（0.1，比通用的大），
        // 因为"脚下 10cm 内有一层薄薄的地面"这种情况很常见。
        /// <summary>
        /// 沿角色下方向投射角色体积以检测地面。
        /// </summary>
        /// <returns> 返回命中数量 </returns>
        private bool CharacterGroundSweep(Vector3 position, Quaternion rotation, Vector3 direction, float distance, out RaycastHit closestHit)
        {
            closestHit = new RaycastHit();

            // 胶囊体投射
            int nbUnfilteredHits = Physics.CapsuleCastNonAlloc(
                position + rotation * _characterTransformToCapsuleBottomHemi - direction * GroundProbingBackstepDistance,
                position + rotation * _characterTransformToCapsuleTopHemi - direction * GroundProbingBackstepDistance,
                Capsule.radius, direction, _internalCharacterHits, distance + GroundProbingBackstepDistance,
                CollidableLayers & StableGroundLayers, QueryTriggerInteraction.Ignore);

            // 命中过滤
            bool foundValidHit = false;
            float closestDistance = Mathf.Infinity;
            for (int i = 0; i < nbUnfilteredHits; i++)
            {
                RaycastHit hit = _internalCharacterHits[i];
                float hitDistance = hit.distance;

                // 找出最近的有效命中
                if (hitDistance > 0f && CheckIfColliderValidForCollisions(hit.collider))
                {
                    if (hitDistance > closestDistance) continue;
                    
                    closestHit = hit;
                    closestHit.distance -= GroundProbingBackstepDistance;
                    closestDistance = hitDistance;

                    foundValidHit = true;
                }
            }

            return foundValidHit;
        }

        /// <summary>
        /// 投射射线以检测碰撞命中。
        /// </summary>
        /// <returns> 返回命中数量 </returns>
        private int CharacterCollisionsRaycast(Vector3 position, Vector3 direction, float distance, out RaycastHit closestHit,
            RaycastHit[] hits, bool acceptOnlyStableGroundLayer = false)
        {
            int queryLayers = CollidableLayers;
            if (acceptOnlyStableGroundLayer)
                queryLayers = CollidableLayers & StableGroundLayers;

            // 射线投射
            int nbUnfilteredHits = Physics.RaycastNonAlloc(position, direction, hits, distance,
                queryLayers, QueryTriggerInteraction.Ignore);

            // 命中过滤
            closestHit = new RaycastHit();
            float closestDistance = Mathf.Infinity;
            int nbHits = nbUnfilteredHits;
            for (int i = nbUnfilteredHits - 1; i >= 0; i--)
            {
                RaycastHit hit = hits[i];
                float hitDistance = hit.distance;

                // 过滤掉无效命中
                if (hitDistance <= 0f || !CheckIfColliderValidForCollisions(hit.collider))
                {
                    nbHits--;
                    
                    if (i < nbHits)
                        hits[i] = hits[nbHits];
                }
                else
                {
                    // 记住最近的有效命中
                    if (hitDistance > closestDistance) continue;
                    
                    closestHit = hit;
                    closestDistance = hitDistance;
                }
            }

            return nbHits;
        }
    }
}
