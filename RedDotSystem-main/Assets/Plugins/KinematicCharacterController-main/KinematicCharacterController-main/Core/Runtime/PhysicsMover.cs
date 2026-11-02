using System;
using UnityEngine;

namespace KinematicCharacterController
{
    /// <summary>
    /// PhysicsMover 的完整状态快照。
    ///
    /// 用于存档、回滚（rollback）、网络同步：把"这个平台此刻在哪、多快"打包成值类型，
    /// 之后可以原样 ApplyState 回去。它只描述模拟相关的量，不含 Transform 层级等表现数据。
    /// </summary>
    [Serializable]
    public struct PhysicsMoverState
    {
        public Vector3 Position;
        public Quaternion Rotation;
        /// <summary>由位姿差分得到的速度（不是物理引擎给的）</summary>
        public Vector3 Velocity;
        /// <summary>由位姿差分得到的角速度（弧度/秒）</summary>
        public Vector3 AngularVelocity;
    }

    /// <summary>
    /// 可被角色"站上去并跟着走"的运动学刚体组件。
    ///
    /// 名字叫 Mover 而不是 Platform，因为它不限于平台：电梯、旋转门、行驶的船、机械臂都行，
    /// 只要"位姿由你指定、且要能载着角色移动"。
    ///
    /// 它解决的问题：直接改 transform 的物体在 PhysX 里没有速度，角色站上去不会被带走；
    /// 用 velocity 驱动又会被求解器干扰。于是这里规定——**位姿由你给（绝对可控），速度由位姿反推**。
    ///
    /// 时序由 KinematicCharacterSystem.Simulate 驱动，四拍不能乱：
    ///   ① VelocityUpdate      算出本 tick 目标位姿与 Velocity（只改数据，不动 Transform）
    ///   ② 角色 Phase1         角色在平台"旧位置"上探测地面，读到 Velocity 并把自己搬运同样距离
    ///   ③ 提交位姿（系统代码） 平台真正移到新位置，让后续碰撞查询看到新世界
    ///   ④ 角色 Phase2         角色在新位置上解算自身输入带来的位移
    ///
    /// 组装：
    ///   Platform（Scale 必须 1,1,1）
    ///     ├─ Rigidbody      本组件自动设为 isKinematic、关插值
    ///     ├─ Collider       可以是多个（一个 body 承载多个 shape）
    ///     ├─ PhysicsMover
    ///     └─ 你的 IMoverController 实现（在 Awake 里把自己赋给 MoverController）
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class PhysicsMover : MonoBehaviour
    {
        /// <summary>
        /// Mover 的 Rigidbody（由 ValidateData 自动获取，不需要手填）。
        ///
        /// 为什么非要有它：角色要靠 collider.attachedRigidbody 找到"可携带物体"，
        /// 只有 Collider 没有 Rigidbody 的话 attachedRigidbody 是 null，角色就不会被带走。
        /// </summary>
        [ReadOnlyKCC]
        public Rigidbody Rigidbody;

        /// <summary>
        /// 决定平台位姿如何"提交"给物理世界：
        /// - true  ：用 Rigidbody.MovePosition/MoveRotation，PhysX 视其为运动学物体，能推开动态刚体；
        /// - false ：直接写 position/rotation，纯搬运，不参与物理求解（适合纯装饰平台）。
        /// </summary>
        public bool MoveWithPhysics = true;

        /// <summary>
        /// 位姿目标提供者（你的脚本实现 IMoverController）。
        /// 标注了 [NonSerialized]，所以 Inspector 里看不到这个槽位——
        /// 必须在你的 Awake/Start 里代码赋值：GetComponent&lt;PhysicsMover&gt;().MoverController = this;
        /// </summary>
        [NonSerialized]
        public IMoverController MoverController;

        /// <summary>
        /// 上一渲染帧插值后的位置快照，用来算"这一渲染帧平台挪了多少"。
        /// </summary>
        [NonSerialized]
        public Vector3 LatestInterpolationPosition;
        /// <summary>
        /// 上一渲染帧插值后的旋转快照。
        /// </summary>
        [NonSerialized]
        public Quaternion LatestInterpolationRotation;

        /// <summary>
        /// 本渲染帧相对上一渲染帧的位移增量（PositionDeltaFromInterpolation = 当前插值位姿 - 上一帧插值位姿）。
        ///
        /// 原版 KCC 的示例 Controller 用它做视觉补偿：角色物理位姿只在 FixedUpdate 更新，
        /// 而平台在 LateUpdate 每帧都在被插值移动，两者之间的差就是"看起来脚底在打滑"的量。
        ///
        /// 注意：本 fork 只保留 Core，没有任何代码读取这个字段（示例 Controller 被裁掉了）。
        /// 也就是说它现在是"写好放着"的预留接口，等你自己的 Controller 来消费。
        /// </summary>
        [NonSerialized]
        public Vector3 PositionDeltaFromInterpolation;
        /// <summary>
        /// 本渲染帧相对上一渲染帧的旋转增量，形式为 Inverse(上一帧) * 当前帧。
        /// 用法：transform.rotation = RotationDeltaFromInterpolation * transform.rotation;
        /// 同样地，本 fork 里还没有消费方。
        /// </summary>
        [NonSerialized]
        public Quaternion RotationDeltaFromInterpolation;

        /// <summary>
        /// 在 KinematicCharacterSystem.PhysicsMovers 列表中的索引。
        /// 注意：本 fork 从未给它赋值，也无人读取，属于原版遗留的字段。
        /// </summary>
        public int IndexInCharacterSystem { get; set; }

        /// <summary>
        /// 本 tick 的速度（世界单位/秒），由 VelocityUpdate 从位姿差分反推。
        ///
        /// 这是给角色用的：角色在 Phase1 里读这个值，把自己搬运 velocity * deltaTime，
        /// 从而"站在平台上跟着走"。因为它在平台真正移动之前就算好了，所以不会滞后一帧。
        /// </summary>
        public Vector3 Velocity { get; protected set; }

        /// <summary>
        /// 本 tick 的角速度（弧度/秒），同样由位姿差分反推。
        /// 角色站在旋转平台上时，靠它算出"脚下那一点"的线速度，从而绕着平台中心转。
        /// </summary>
        public Vector3 AngularVelocity { get; protected set; }

        /// <summary>
        /// 插值线段的左端点：本 tick 开始前的位姿。由 KinematicCharacterSystem 在
        /// PreSimulationInterpolationUpdate 里写入，LateUpdate 时与 Transient* 做 Lerp。
        /// </summary>
        public Vector3 InitialTickPosition { get; set; }
        /// <summary>
        /// 插值线段的左端点（旋转）。
        /// </summary>
        public Quaternion InitialTickRotation { get; set; }

        /// <summary>
        /// 缓存的 Transform 引用，避免每 tick 多次访问 Component.transform（原生调用）。
        /// </summary>
        public Transform Transform { get; private set; }

        /// <summary>
        /// 本 tick 开始时的位姿（用于和 TransientPosition 差分出 Velocity）。
        /// 与 InitialTickPosition 不同：后者是给渲染插值用的，这个是给速度计算用的。
        /// </summary>
        public Vector3 InitialSimulationPosition { get; private set; }
        /// <summary>
        /// 本 tick 开始时的旋转（用于差分出 AngularVelocity）。
        /// </summary>
        public Quaternion InitialSimulationRotation { get; private set; }

        private Vector3 _internalTransientPosition;
        /// <summary>
        /// Mover 的逻辑位姿（模拟真值）——本 tick 结束后平台应该在的位置。
        ///
        /// 与 Transform 的区别：这里是"算出来的"，Transform 是"画出来的"。
        /// 只有 VelocityUpdate 通过 out 参数能写它（因为 setter 是 private）。
        /// </summary>
        public Vector3 TransientPosition
        {
            get
            {
                return _internalTransientPosition;
            }
            private set
            {
                _internalTransientPosition = value;
            }
        }

        private Quaternion _internalTransientRotation;
        /// <summary>
        /// Mover 的逻辑旋转（模拟真值）。
        /// </summary>
        public Quaternion TransientRotation
        {
            get
            {
                return _internalTransientRotation;
            }
            private set
            {
                _internalTransientRotation = value;
            }
        }


        // 编辑器里首次添加组件时触发
        private void Reset()
        {
            ValidateData();
        }

        // Inspector 里改任意值、或脚本重编译时触发（运行中也可能触发）
        private void OnValidate()
        {
            ValidateData();
        }

        /// <summary>
        /// 校验并强制修正 Rigidbody 的配置。
        ///
        /// 这些设置不是"建议"，而是这套系统能正常工作的前提，所以每次编辑都会被覆盖：
        /// 你手动改成非 kinematic 或打开插值，保存后就会被打回原样。
        /// </summary>
        public void ValidateData()
        {
            // 引用由组件自己接管，所以字段是 [ReadOnlyKCC]，不允许在 Inspector 里换对象
            Rigidbody = gameObject.GetComponent<Rigidbody>();

            // 质心归零：让旋转围绕物体原点，符合"平台"的直觉，
            // 否则角色站在偏心的平台上，旋转时会被算出一个奇怪的分速度
            Rigidbody.centerOfMass = Vector3.zero;

            // 解除角速度上限：旋转平台常常需要每秒几百度的转速，默认 7rad/s 会把它卡住
            Rigidbody.maxAngularVelocity = Mathf.Infinity;

            // 解除去穿透速度上限：平台把角色/箱子挤开时不会被限速，避免"卡住"
            Rigidbody.maxDepenetrationVelocity = Mathf.Infinity;

            // 关键：运动学。位姿由 MovePosition 决定，物理力不会推它
            Rigidbody.isKinematic = true;

            // 关键：关掉 PhysX 插值。KCC 用 CustomInterpolationUpdate 自己插值 Transform，
            // 两套插值叠加会导致平台位置被插两次，角色站上去滑步/抖动
            Rigidbody.interpolation = RigidbodyInterpolation.None;
        }

        private void OnEnable()
        {
            // 第一个 Mover（或 Motor）出现时，顺手把全局系统创建出来
            KinematicCharacterSystem.EnsureCreation();
            KinematicCharacterSystem.RegisterPhysicsMover(this);
        }

        private void OnDisable()
        {
            // 只注销，不销毁系统（系统由 DontDestroyOnLoad 常驻）
            KinematicCharacterSystem.UnregisterPhysicsMover(this);
        }

        private void Awake()
        {
            Transform = this.transform;
            ValidateData();

            // 初始化四套位姿，保证第一 tick 之前所有字段都是一致的：
            // Rigidbody.position 是 PhysX 真值；Transform.position 在 Awake 阶段与之一致
            TransientPosition = Rigidbody.position;
            TransientRotation = Rigidbody.rotation;
            InitialSimulationPosition = Rigidbody.position;
            InitialSimulationRotation = Rigidbody.rotation;
            LatestInterpolationPosition = Transform.position;
            LatestInterpolationRotation = Transform.rotation;
        }

        /// <summary>
        /// 直接设置 Mover 的位置（瞬移，不产生速度）。
        ///
        /// 注意它同时写了四处：Transform（渲染/查询）、Rigidbody（PhysX 真值）、
        /// InitialSimulationPosition（速度差分基准）、TransientPosition（逻辑真值）。
        /// 少写一处就会有一帧的不一致：比如漏了 InitialSimulationPosition，
        /// 下一 tick 会算出一个巨大的假速度，把站在上面的角色弹飞。
        /// </summary>
        public void SetPosition(Vector3 position)
        {
            Transform.position = position;
            Rigidbody.position = position;
            InitialSimulationPosition = position;
            TransientPosition = position;
        }

        /// <summary>
        /// 直接设置 Mover 的旋转（瞬移，不产生角速度）。
        /// </summary>
        public void SetRotation(Quaternion rotation)
        {
            Transform.rotation = rotation;
            Rigidbody.rotation = rotation;
            InitialSimulationRotation = rotation;
            TransientRotation = rotation;
        }

        /// <summary>
        /// 直接设置 Mover 的位置与旋转。
        /// </summary>
        public void SetPositionAndRotation(Vector3 position, Quaternion rotation)
        {
            Transform.SetPositionAndRotation(position, rotation);
            Rigidbody.position = position;
            Rigidbody.rotation = rotation;
            InitialSimulationPosition = position;
            InitialSimulationRotation = rotation;
            TransientPosition = position;
            TransientRotation = rotation;
        }

        /// <summary>
        /// 返回与模拟相关的 Mover 状态信息（存档 / 回滚用）。
        /// 只包含逻辑真值，不包含 Transform 层级等表现数据。
        /// </summary>
        public PhysicsMoverState GetState()
        {
            PhysicsMoverState state = new PhysicsMoverState
            {
                Position = TransientPosition,
                Rotation = TransientRotation,
                Velocity = Velocity,
                AngularVelocity = AngularVelocity
            };

            return state;
        }

        /// <summary>
        /// 立即应用一份 Mover 状态（读档 / 回滚用）。
        /// 内部走 SetPositionAndRotation，因此不会产生"假速度"。
        /// </summary>
        public void ApplyState(PhysicsMoverState state)
        {
            SetPositionAndRotation(state.Position, state.Rotation);
            Velocity = state.Velocity;
            AngularVelocity = state.AngularVelocity;
        }

        /// <summary>
        /// 根据 deltaTime 与目标位姿缓存速度值。由 KinematicCharacterSystem 在每个 tick 的
        /// 最开头调用（早于所有角色的 Phase1）。
        ///
        /// 这里算出的 Velocity 是"本 tick 将要发生的位移速度"——注意是**预测性**的：
        /// 平台此刻还没动，但角色马上要按这个速度被搬运，所以两者才能严丝合缝地同步。
        ///
        /// 全程不写 Transform / Rigidbody.position：真正提交在系统的第三拍。
        /// </summary>
        public void VelocityUpdate(float deltaTime)
        {
            // 记录本 tick 的起点，用于差分
            InitialSimulationPosition = TransientPosition;
            InitialSimulationRotation = TransientRotation;

            // 由你（IMoverController）决定目标位姿。
            // 这里用 out 直接写私有字段，绕过了 private setter
            MoverController.UpdateMovement(out _internalTransientPosition, out _internalTransientRotation, deltaTime);

            if (deltaTime > 0f)
            {
                // 线性速度 = 位移 / 时间
                Velocity = (TransientPosition - InitialSimulationPosition) / deltaTime;

                // 角速度：先求旋转增量 q_delta = q_new * q_old⁻¹，再转成欧拉角，最后换算成弧度/秒。
                //
                // 已知坑：eulerAngles 的取值范围是 0~360，例如目标 -10° 会返回 350°，
                // 于是角速度的符号会翻，转速一大（每 tick 超过 180°）就会算错方向。
                // 对小角度旋转（每秒几十度）这个近似完全够用，快速自转的平台需要换成
                // Quaternion.ToAngleAxis 来计算轴角。
                Quaternion rotationFromCurrentToGoal = TransientRotation * (Quaternion.Inverse(InitialSimulationRotation));
                AngularVelocity = (Mathf.Deg2Rad * rotationFromCurrentToGoal.eulerAngles) / deltaTime;
            }
        }
    }
}
