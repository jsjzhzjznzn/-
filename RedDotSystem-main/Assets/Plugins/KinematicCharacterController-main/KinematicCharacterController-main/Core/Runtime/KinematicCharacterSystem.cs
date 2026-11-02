using System.Collections.Generic;
using UnityEngine;

namespace KinematicCharacterController
{
    /// <summary>
    /// KCC 的总调度中心：管理所有 KinematicCharacterMotor 与 PhysicsMover 的模拟。
    ///
    /// 设计要点：角色不是各跑各的 Update，而是被一个全局单例按固定顺序批量推进。
    /// 只有这样才能保证"平台先动、角色后解算"这类跨对象时序正确，
    /// 也才能让插值、物理提交这些操作只有一处实现。
    ///
    /// 一帧的完整流水（AutoSimulation = true、Interpolate = true 时）：
    ///
    ///   FixedUpdate
    ///     ├─ PreSimulationInterpolationUpdate  记录插值起点 InitialTick*，并把 Transform 拉到瞬态位姿
    ///     ├─ Simulate
    ///     │    ├─ foreach Mover : VelocityUpdate        算本 tick 的目标位姿 → 反推出 Velocity
    ///     │    ├─ foreach Motor : UpdatePhase1          地面探测 / 解初始重叠 / 采样附着刚体速度（不产生位移）
    ///     │    ├─ foreach Mover : 提交位姿到 Transform 和 Rigidbody  ← 卡在 Phase1 与 Phase2 之间
    ///     │    └─ foreach Motor : UpdatePhase2          按 BaseVelocity 解算位移与旋转，结果写进 Transient*
    ///     └─ PostSimulationInterpolationUpdate 把 Transform 拉回起点，留给 LateUpdate 慢慢补出来
    ///
    ///   LateUpdate
    ///     └─ CustomInterpolationUpdate         InitialTick* → Transient* 之间插值，抹平 FixedUpdate 的台阶感
    ///
    /// 注意：整套逻辑依赖"两份位姿"——Transient（模拟真值）与 Transform（显示值）。
    /// 任何一步把两者写乱，表现就是抖动、穿模、角色和平台不同步。
    /// </summary>
    // 先于普通脚本执行，保证角色位姿在本帧其他逻辑读取之前就已经算好
    [DefaultExecutionOrder(-100)]
    public class KinematicCharacterSystem : MonoBehaviour
    {
        // 单例。不用 Instance 属性对外暴露，是为了强制调用方走 EnsureCreation()，避免空引用
        private static KinematicCharacterSystem _instance;

        // 静态列表：所有实例共享，Motor/Mover 在 OnEnable 时自行注册（见各自的 OnEnable）
        public static List<KinematicCharacterMotor> CharacterMotors = new List<KinematicCharacterMotor>();
        public static List<PhysicsMover> PhysicsMovers = new List<PhysicsMover>();

        // 本 tick 的模拟起点时间与步长，供 LateUpdate 计算插值系数 t
        // 初值 -1 是有意的"从未模拟过"哨兵值，此时插值系数会被 Clamp01 压到 0
        private static float _lastCustomInterpolationStartTime = -1f;
        private static float _lastCustomInterpolationDeltaTime = -1f;

        // 运行时配置（自动模拟 / 是否插值 / 列表初始容量）
        // 由 EnsureCreation 现场 new 出来；若想改配置，必须在创建之后再赋值
        public static KCCSettings Settings;

        /// <summary>
        /// 如果尚不存在，则创建一个 KinematicCharacterSystem 实例。
        /// 不需要手动调用：第一个 Motor/Mover 在 OnEnable 里会触发它。
        /// </summary>
        public static void EnsureCreation()
        {
            if (_instance != null) return;

            // 挂在一个自建的空 GameObject 上；AddComponent 会同步触发 Awake，_instance 在那里被赋值为 this
            var systemGameObject = new GameObject("KinematicCharacterSystem");
            _instance = systemGameObject.AddComponent<KinematicCharacterSystem>();

            // 隐藏且禁止在 Inspector 里编辑，防止误删/误改让整套系统瘫痪
            systemGameObject.hideFlags = HideFlags.NotEditable;
            _instance.hideFlags = HideFlags.NotEditable;

            // 用代码创建默认配置，而不是从 Resources 读取：这个 fork 没有附带预设资产
            Settings = ScriptableObject.CreateInstance<KCCSettings>();

            // 跨场景常驻，避免切换场景后注册表被清空

            DontDestroyOnLoad(systemGameObject);
        }

        /// <summary>
        /// 获取已存在的 KinematicCharacterSystem 实例（可能为 null，调用前应先 EnsureCreation）。
        /// </summary>
        /// <returns></returns>
        public static KinematicCharacterSystem GetInstance()
        {
            return _instance;
        }

        /// <summary>
        /// 设置角色 Motor 列表的容量，避免添加角色时产生扩容分配。
        /// 注意：只改 Capacity（预分配内存），不改变 Count，也不会清空已有元素。
        /// </summary>
        /// <param name="capacity"></param>
        public static void SetCharacterMotorsCapacity(int capacity)
        {
            // 容量不能小于当前元素数，否则 List 会抛异常
            if (capacity < CharacterMotors.Count)
            {
                capacity = CharacterMotors.Count;
            }
            CharacterMotors.Capacity = capacity;
        }

        /// <summary>
        /// 将一个 KinematicCharacterMotor 注册到系统中，之后才会被 Simulate 推进。
        /// 由 Motor.OnEnable 调用。这里不做去重检查，重复注册会让同一角色每帧被模拟两次。
        /// </summary>
        public static void RegisterCharacterMotor(KinematicCharacterMotor motor)
        {
            CharacterMotors.Add(motor);
        }

        /// <summary>
        /// 从系统中注销一个 KinematicCharacterMotor（由 Motor.OnDisable 调用）。
        /// 用 Remove 是 O(n) 线性查找；角色数量在千级以内无需优化。
        /// </summary>
        public static void UnregisterCharacterMotor(KinematicCharacterMotor motor)
        {
            CharacterMotors.Remove(motor);
        }

        /// <summary>
        /// 设置物理 Mover 列表的容量，避免添加 Mover 时产生扩容分配。
        /// </summary>
        /// <param name="capacity"></param>
        public static void SetPhysicsMoversCapacity(int capacity)
        {
            if (capacity < PhysicsMovers.Count)
            {
                capacity = PhysicsMovers.Count;
            }
            PhysicsMovers.Capacity = capacity;
        }

        /// <summary>
        /// 将一个 PhysicsMover 注册到系统中。
        /// </summary>
        public static void RegisterPhysicsMover(PhysicsMover mover)
        {
            PhysicsMovers.Add(mover);

            // 关键：必须关掉刚体自带的插值。
            // KCC 用 CustomInterpolationUpdate 自己插值 Transform，若再让 PhysX 插值 Rigidbody，
            // 两套插值叠加会把平台的位置算成"两帧之间又插了一次"，表现为角色在平台上滑步/抖动。
            mover.Rigidbody.interpolation = RigidbodyInterpolation.None;
        }

        /// <summary>
        /// 从系统中注销一个 PhysicsMover。
        /// </summary>
        public static void UnregisterPhysicsMover(PhysicsMover mover)
        {
            PhysicsMovers.Remove(mover);
        }

        // 防止脚本重新编译（域重载）时残留指向旧实例的静态引用，
        // 也避免场景里出现两个 KinematicCharacterSystem 同时推进模拟
        private void OnDisable()
        {
            Destroy(gameObject);
        }

        private void Awake()
        {
            // 与 EnsureCreation 里的 AddComponent 配合：那一刻就把 _instance 指向新建的组件
            _instance = this;
        }

        private void FixedUpdate()
        {
            // 手动驱动模式：由业务代码自己调用 Simulate（例如回放、暂停、或自定义时间步）
            if (!Settings.AutoSimulation) return;

            // FixedUpdate 中的 Time.deltaTime 就等于 Time.fixedDeltaTime，两种写法等价；
            // 但不能换成 Time.unscaledDeltaTime，那会破坏慢动作/暂停时的物理一致性
            float deltaTime = Time.deltaTime;

            // 记录插值起点，必须在 Simulate 之前，否则起点已是模拟后的位姿，插值等于没做
            if (Settings.Interpolate)
            {
                PreSimulationInterpolationUpdate();
            }

            Simulate(deltaTime, CharacterMotors, PhysicsMovers);

            // 记录本 tick 起始时间，并把显示位姿拉回起点，供 LateUpdate 逐帧插值
            if (Settings.Interpolate)
            {
                PostSimulationInterpolationUpdate(deltaTime);
            }
        }

        private void LateUpdate()
        {
            // 每个渲染帧都跑，频率高于 FixedUpdate（默认 50Hz），负责把两 tick 之间平滑补出来
            if (Settings.Interpolate)
            {
                CustomInterpolationUpdate();
            }
        }

        /// <summary>
        /// 记录 KinematicCharacterMotor 与 PhysicsMover 的插值起点。
        ///
        /// 此时 Transient* 还是"上一次模拟的结果"，把它同时存进 InitialTick* 并写入 Transform，
        /// 相当于给本 tick 画好插值线段的左端点；Simulate 之后 Transient* 就是右端点。
        /// </summary>
        private static void PreSimulationInterpolationUpdate()
        {
            // 保存模拟前的位姿，并把 Transform 放到瞬态位姿（让物理与渲染从同一处出发）
            foreach (KinematicCharacterMotor motor in CharacterMotors)
            {
                motor.InitialTickPosition = motor.TransientPosition;
                motor.InitialTickRotation = motor.TransientRotation;

                motor.Transform.SetPositionAndRotation(motor.TransientPosition, motor.TransientRotation);
            }

            foreach (PhysicsMover mover in PhysicsMovers)
            {
                mover.InitialTickPosition = mover.TransientPosition;
                mover.InitialTickRotation = mover.TransientRotation;

                // Mover 是物理对象，Transform 与 Rigidbody 两处都得摆到位，否则 PhysX 仍在用旧位姿做碰撞
                mover.Transform.SetPositionAndRotation(mover.TransientPosition, mover.TransientRotation);
                mover.Rigidbody.position = mover.TransientPosition;
                mover.Rigidbody.rotation = mover.TransientRotation;
            }
        }

        /// <summary>
        /// 推进角色和/或 Mover 的模拟。调用顺序就是这个函数存在的全部理由，不能随意调换：
        ///
        /// 1. Mover.VelocityUpdate      先知道平台这帧要去哪（只算，不提交）
        /// 2. Motor.UpdatePhase1        角色在平台"旧位置"上做地面探测；发现站在 Mover 上后，
        ///                              直接读 mover.Velocity 并当场把自己搬运过去（不是存着等 Phase2）
        /// 3. 提交 Mover 位姿           平台真正挪到新位置
        /// 4. Motor.UpdatePhase2        在平台新位置上解算自身输入带来的位移（携带已在步 2 完成，不重复）
        ///
        /// 这样角色既不会被移动的平台撞开，又能跟着平台走。
        /// </summary>
        private static void Simulate(float deltaTime, List<KinematicCharacterMotor> motors, List<PhysicsMover> movers)
        {
            // 更新 PhysicsMover 速度（内部调 MoverController.UpdateMovement 得到目标位姿，再差分出 Velocity）
            foreach (PhysicsMover mover in movers)
            {
                mover.VelocityUpdate(deltaTime);
            }

            // 角色控制器更新阶段 1：解初始重叠 → 在平台旧位置上探测地面 → 若站在刚体上，
            // 取其速度并立刻 InternalCharacterMove 一段（角色被平台"扛着走"就发生在这里）
            foreach (KinematicCharacterMotor motor in motors)
            {
                motor.UpdatePhase1(deltaTime);
            }

            // 模拟 PhysicsMover 位移：把上一步算出的目标位姿真正提交（放在两阶段之间是刻意的）
            foreach (PhysicsMover mover in movers)
            {
                mover.Transform.SetPositionAndRotation(mover.TransientPosition, mover.TransientRotation);
                mover.Rigidbody.position = mover.TransientPosition;
                mover.Rigidbody.rotation = mover.TransientRotation;
            }

            // 角色控制器更新阶段 2 与移动：解旋转、解位移、处理刚体反作用、平面约束
            foreach (KinematicCharacterMotor motor in motors)
            {
                motor.UpdatePhase2(deltaTime);

                // 模拟结果最终落到 Transform（若开启插值，稍后 PostSimulation 会把它拉回起点再补间）
                motor.Transform.SetPositionAndRotation(motor.TransientPosition, motor.TransientRotation);
            }
        }

        /// <summary>
        /// 启动 KinematicCharacterMotor 与 PhysicsMover 的插值：记录本 tick 起点时间，
        /// 并把显示位姿倒回 InitialTick*，让 LateUpdate 有东西可"追"。
        /// </summary>
        private static void PostSimulationInterpolationUpdate(float deltaTime)
        {
            _lastCustomInterpolationStartTime = Time.time;
            _lastCustomInterpolationDeltaTime = deltaTime;

            // 把插值根对象还原到初始位姿（视觉回到 tick 起点，物理真值仍在 Transient* 里）
            foreach (KinematicCharacterMotor motor in CharacterMotors)
            {
                motor.Transform.SetPositionAndRotation(motor.InitialTickPosition, motor.InitialTickRotation);
            }

            foreach (PhysicsMover mover in PhysicsMovers)
            {
                if (mover.MoveWithPhysics)
                {
                    // 参与物理的 Mover：不能直接把 Rigidbody.position 拨回去，那会被当成瞬移，
                    // 丢掉物理速度、还可能穿透角色。正确做法是先同步、再倒带，
                    // 最后用 MovePosition/MoveRotation 交出终点，由 PhysX 自行按"起点→终点"计算速度。
                    Physics.SyncTransforms();
                    mover.Rigidbody.position = mover.InitialTickPosition;
                    mover.Rigidbody.rotation = mover.InitialTickRotation;
                    Physics.SyncTransforms();

                    mover.Rigidbody.MovePosition(mover.TransientPosition);
                    mover.Rigidbody.MoveRotation(mover.TransientRotation);
                }
                else
                {
                    // 纯运动学 Mover：不参与物理求解，直接写终点即可
                    mover.Rigidbody.position = mover.TransientPosition;
                    mover.Rigidbody.rotation = mover.TransientRotation;
                }
            }
        }

        /// <summary>
        /// 处理逐帧插值：把 Transform 放在 InitialTick* 与 Transient* 之间，按真实时间取比例。
        /// 用 Time.time 而不是自己累加 deltaTime，是为了自动吸收掉帧间隔的抖动。
        /// </summary>
        private static void CustomInterpolationUpdate()
        {
            // 距本 tick 起点已过去多久 / 一个 tick 有多长，夹到 [0,1]
            // 若从未模拟过（起点时间为 -1），这里会得到负值并被压到 0，角色停在原地而不是乱飞
            float interpolationFactor = Mathf.Clamp01((Time.time - _lastCustomInterpolationStartTime) / _lastCustomInterpolationDeltaTime);

            // 处理角色的插值
            foreach (KinematicCharacterMotor motor in CharacterMotors)
            {
                motor.Transform.SetPositionAndRotation(
                    Vector3.Lerp(motor.InitialTickPosition, motor.TransientPosition, interpolationFactor),
                    Quaternion.Slerp(motor.InitialTickRotation, motor.TransientRotation, interpolationFactor));
            }

            // 处理 PhysicsMover 的插值
            foreach (PhysicsMover mover in PhysicsMovers)
            {
                mover.Transform.SetPositionAndRotation(
                    Vector3.Lerp(mover.InitialTickPosition, mover.TransientPosition, interpolationFactor),
                    Quaternion.Slerp(mover.InitialTickRotation, mover.TransientRotation, interpolationFactor));

                // 记录"本渲染帧相对上一渲染帧"的位移增量。原版 KCC 用它给站在平台上的角色做视觉补偿：
                // 角色物理位置只在 FixedUpdate 更新，而平台在 LateUpdate 被插值移动，
                // 两者差的这一点靠这个增量补上，否则角色看起来会在平台上漂。
                // 注意：本 fork 只保留了 Core 包，没有任何代码读取这两个增量（示例 Controller 已被裁掉），
                // 所以这里的计算目前是"写好放着"，接入自己的 Controller 时才用得上。
                Vector3 newPos = mover.Transform.position;
                Quaternion newRot = mover.Transform.rotation;
                mover.PositionDeltaFromInterpolation = newPos - mover.LatestInterpolationPosition;
                mover.RotationDeltaFromInterpolation = Quaternion.Inverse(mover.LatestInterpolationRotation) * newRot;
                mover.LatestInterpolationPosition = newPos;
                mover.LatestInterpolationRotation = newRot;
            }
        }
    }
}
