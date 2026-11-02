using Animancer;
using Cinemachine;
using Cysharp.Threading.Tasks;
using SkierFramework;
using System.Linq;
using System.Threading;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering.Universal;


/// <summary>
/// 角色主控脚本，继承移动基类
/// 负责：输入驱动、动画播放、连接状态机
/// </summary>
public partial class Player : CharacterMoveControllerBase
{
    [SerializeField] public string currentMovementState;   // 调试用：当前移动状态名
     [SerializeField] public string currentComboState;

    [SerializeField] private PlayerSO playerSO;            // 角色数据资产（Inspector 拖入）

    /// <summary>角色数据资产（连击状态机取连招配置用）</summary>
    public PlayerSO PlayerSO => playerSO;

    /// <summary>行为状态（眩晕/沉默/无敌计数；Awake 兜底挂载）</summary>
    public CharacterState CharacterState { get; private set; }

    /// <summary>技能容器（冷却/充能/公共冷却；Awake 兜底挂载）</summary>
    public SkillComponent SkillComponent { get; private set; }

    /// <summary>技能键对应的技能（PlayerSO.skillSlots[0]；未配置返回 null）</summary>
    public SkillDefinition DefaultSkill =>
        playerSO != null && playerSO.skillSlots != null && playerSO.skillSlots.Count > 0
            ? playerSO.skillSlots[0]
            : null;

    /// <summary>装备属性应用器（装备加成按实例来源写进 AttributeComponent，重复应用不叠加）</summary>
    private EquipmentAttrApplier equipmentApplier;
    

    // 角色音效组件缓存（懒获取：首次访问时 GetComponent 并缓存，避免状态机频繁调用重复查找）
    private ActorAudioComponent actorAudio;
    /// <summary>角色音效组件（播放 3D 空间音效用；未挂载返回 null）</summary>
    public ActorAudioComponent ActorAudio
    {
        get
        {
            // 本对象可能已被销毁（切场景/退出战斗后的残留状态机回调会访问到这里）：
            // Unity 的 == 能判"已销毁"，先挡掉，避免走进 GetComponent 抛 MissingReferenceException
            if (this == null) return null;
            if (actorAudio == null)
            {
                actorAudio = GetComponent<ActorAudioComponent>();
            }
            return actorAudio;
        }
    }
    [SerializeField] public PlayerCameraUtility playerCameraUtility;

    [Header("视角参考")]
    [SerializeField] private Camera viewCamera;            // 主相机（带CinemachineBrain），Inspector拖入

    /// <summary>相机 Transform（相机相对移动转换用）；未配置时回退 Camera.main</summary>
    public Transform CameraTransform =>
        viewCamera != null ? viewCamera.transform
        : Camera.main != null ? Camera.main.transform : null;

    // [Header("武器骨骼（攻击特效挂点）")]
    // [SerializeField] private Transform weaponBone;
    //
    // /// <summary>
    // /// 武器骨骼 Transform（VFX 挂点）。优先用 Inspector 配置，未配置则在首次访问时
    // /// 沿 transform.Find 查找 "Bip001/Anbi_Weapon_02"，结果缓存复用。
    // /// </summary>
    // public Transform WeaponBone
    // {
    //     get
    //     {
    //         if (weaponBone == null) weaponBone = transform.Find("Bip001/Anbi_Weapon_02");
    //         return weaponBone;
    //     }
    // }

    private PlayerMovementStateMachine stateMachine;
    private PlayerComboStateMachine comboStateMachine;

    /// <summary>死亡标记：死亡后完全不接受任何反应</summary>
    private bool _isDead;

    /// <summary>是否已死亡（供外部查询）</summary>
    public bool IsDead => _isDead;

    /// <summary>移动状态机（连击状态等需要联动移动状态时访问）</summary>
    public PlayerMovementStateMachine MovementStateMachine => stateMachine;

    /// <summary>连击状态机（调试组件读取当前连招/判定数据用）</summary>
    public PlayerComboStateMachine ComboStateMachine => comboStateMachine;

    // ================================================================
    // 网络动画状态同步（拥有者写入，远程端回放动画）
    // ================================================================

    /// <summary>状态起始的服务端时间戳（秒）：远程端据此计算"状态已播放时长"做相位对齐。
    /// 注意：必须声明在所有状态变量之前（NetworkVariable 按声明顺序应用，保证状态回调里读到的已是最新值）</summary>
    private readonly NetworkVariable<float> netStateStartTime =
        new(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    /// <summary>移动状态同步变量（拥有者写入；远程端 OnValueChanged 回放对应状态动画）</summary>
    private readonly NetworkVariable<MovementStateType> netMoveState =
        new(MovementStateType.Idle, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    /// <summary>
    /// 当前技能标识（拥有者写入；远程端据此从 SkillDatabase 还原动画）。
    /// 必须声明在 netComboState 之前：NetworkVariable 按声明顺序应用，
    /// 远程端收到"切到技能状态"时要能读到已经更新的技能 id。
    /// </summary>
    private readonly NetworkVariable<int> netSkillIdHash =
        new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    /// <summary>连击状态同步变量（拥有者写入；远程端据此播放攻击/技能动画）</summary>
    private readonly NetworkVariable<ComboStateType> netComboState =
        new(ComboStateType.Null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    /// <summary>连击段数同步变量（拥有者写入；远程端段数前进时重播新段攻击动画）</summary>
    private readonly NetworkVariable<int> netComboIndex =
        new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    /// <summary>前进攻击标识（拥有者写入；远程端把轻击容器首段切换/还原为前进攻击段）</summary>
    private readonly NetworkVariable<bool> netForwardATK =
        new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    /// <summary>
    /// 本角色座位号（服务端生成时写入：0=第一人，1=第二人；**-1 = 还未分配**）。
    /// 客户端据此选场景里对应的那套相机（Camera/thirdcamera 或 Camera (1)/thirdcamera (1)）。
    /// 单机（未 spawn）不走这个变量，相机固定用座位 0 那套。
    /// </summary>
    public readonly NetworkVariable<int> NetSeatId =
        new(-1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>本角色的英雄编号（服务端生成时写入：安比=0，艾莲=1），HUD 血条面板据此加载头像</summary>
    public readonly NetworkVariable<int> NetCharId =
        new(-1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>是否作为远程镜像端（联网且非拥有者）；单机永远为 false</summary>
    public bool IsRemote => IsSpawned && !IsOwner;

    /// <summary>当前技能标识（远程端播技能动画时从 SkillDatabase 反查配置）</summary>
    public int NetSkillIdHash => netSkillIdHash.Value;

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        // ===== 联机排障日志：确认每个角色归属到谁（定位"客户端无法操作角色"，定位后可删） =====
        ulong localId = NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : 0;
        Debug.Log($"[Player] name={name} Owner={OwnerClientId} Local={localId} IsOwner={IsOwner} IsSpawned={IsSpawned}");

        netMoveState.OnValueChanged += OnMoveStateChanged;
        netComboState.OnValueChanged += OnComboStateChanged;
        netComboIndex.OnValueChanged += OnComboIndexChanged;
        netForwardATK.OnValueChanged += OnForwardATKChanged;

        // 启动相机绑定（使用协程延迟重试，每 0.5 秒查找一次，找到即停）
        StartCameraBinding();

        // 拥有者端等双方角色都实例化后再打开 HUD 血条面板（UIManager 未初始化时 Open 会自动排队）
        if (IsOwner)
        {
            StartCoroutine(OpenBloodPanelWhenReady());
        }
    }

    /// <summary>等待场上已生成的 Player 达到 2 个（我方+敌方都实例化）后打开 bloodpanel
    /// 优化：使用 WaitForSeconds 代替 WaitUntil，避免每帧执行 FindObjectsByType</summary>
    private System.Collections.IEnumerator OpenBloodPanelWhenReady()
    {
        // 等待一小段时间让角色生成
        yield return new WaitForSeconds(0.5f);

        // 每 0.5 秒检查一次，而不是每帧检查
        int spawnedCount = 0;
        while (spawnedCount < 2)
        {
            spawnedCount = 0;
            foreach (var player in FindObjectsOfType<Player>())
            {
                if (player.IsSpawned) spawnedCount++;
            }
            if (spawnedCount < 2)
            {
                yield return new WaitForSeconds(0.5f);
            }
        }
        UIManager.Instance.Open(UIType.bloodpanel).Forget();
    }

    public override void OnNetworkDespawn()
    {
        base.OnNetworkDespawn();
        netMoveState.OnValueChanged -= OnMoveStateChanged;
        netComboState.OnValueChanged -= OnComboStateChanged;
        netComboIndex.OnValueChanged -= OnComboIndexChanged;
        netForwardATK.OnValueChanged -= OnForwardATKChanged;

        // 角色离场（战斗结束/断线清理等）时取消状态机挂起的计时器，防止到点回调访问已销毁组件
        stateMachine?.CancelPendingTimers();

        // 装备加成随离场撤销，避免对象池复用/同场景重生时旧来源残留
        ClearEquipmentSnapshot();

        // 面板常驻 UIRoot(DDOL)，拥有者端角色离场时同步关闭，防止跨场景残留上一局的血条
        if (IsOwner)
        {
            UIManager.Instance.Close(UIType.bloodpanel).Forget();
        }
    }

    protected override void Awake()
    {
        base.Awake();
        stateMachine = new PlayerMovementStateMachine(this, playerSO);
        comboStateMachine = new PlayerComboStateMachine(this);
        // 相机工具类的初始化挪到 TryBindSceneCamera（绑定成功后传入"本座位"的 vcam）
        // 初始状态在 Start 切换：确保所有单例（CharacterInputSystem 等）Awake 已完成，
        // 事件订阅能拿到已初始化的 inputActions

        // Buff 组件兜底挂载：不强制在 Inspector 手动挂，漏挂时自动补（增伤/减伤/持续伤害都依赖它）
        if (GetComponent<BuffComponent>() == null)
            gameObject.AddComponent<BuffComponent>();

        // 属性账本（Buff 的属性加成写 Modifier）+ 行为状态（眩晕/沉默计数）
        if (GetComponent<AttributeComponent>() == null)
            gameObject.AddComponent<AttributeComponent>();
        CharacterState = GetComponent<CharacterState>();
        if (CharacterState == null)
            CharacterState = gameObject.AddComponent<CharacterState>();

        // 技能容器（冷却/充能/公共冷却；没配技能的角色的容器保持空转）
        SkillComponent = GetComponent<SkillComponent>();
        if (SkillComponent == null)
            SkillComponent = gameObject.AddComponent<SkillComponent>();

        // 把角色配置(SO)里的白值属性写进属性账本 / 血量组件
        // （放在最后：上面保证 AttributeComponent 已存在；InitStats 要早于 HealthModel 的网络同步）
        ApplyStatsFromSO();

        // 装备加成：单机按本地缓存直接应用；联机等房主生成英雄时注入快照
        equipmentApplier = new EquipmentAttrApplier(GetComponent<AttributeComponent>());
        ApplyLocalEquipmentIfSinglePlayer();
    }

    /// <summary>
    /// 单机场景按 PlayerSO.characterId 从本地装备缓存应用加成。
    /// 联机（已开 Host/Client）不读本地：装备快照由客户端上报房主、房主在生成英雄时注入，避免各端算出不同数值。
    /// </summary>
    private void ApplyLocalEquipmentIfSinglePlayer()
    {
        bool networked = NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;
        if (networked || IsSpawned) return;

        int charId = playerSO != null ? playerSO.CharacterId : -1;
        if (charId < 0 || EquipmentManager.Instance == null) return;
        ApplyEquipmentSnapshot(EquipmentManager.Instance.BuildBattleSnapshot(charId));
    }

    /// <summary>应用装备快照（联机：房主生成英雄后按座位注入；单机：Awake 里按本地缓存应用）</summary>
    public void ApplyEquipmentSnapshot(EquipBattleSnapshot snapshot)
    {
        equipmentApplier?.Apply(snapshot);
    }

    /// <summary>清空装备加成（离场/复活重置）</summary>
    public void ClearEquipmentSnapshot()
    {
        equipmentApplier?.Clear();
    }

    /// <summary>把 PlayerSO.statsData 的基础属性写进 AttributeComponent(攻击/防御) 与 HealthModel(最大血量)。
    /// 没配 statsData 时不覆盖，保持组件上的默认值。</summary>
    private void ApplyStatsFromSO()
    {
        var stats = playerSO != null ? playerSO.statsData : null;
        if (stats == null) return;

        var attr = GetComponent<AttributeComponent>();
        if (attr != null)
        {
            attr.SetBase(AttrType.Attack, stats.baseAttack);
            attr.SetBase(AttrType.Defense, stats.baseDefense);
        }

        var health = GetComponent<HealthModel>();
        if (health != null)
            health.InitStats(stats.baseMaxHP);
    }

    protected override void Start()
    {
        base.Start();
        // 联网时状态由 NetworkVariable 同步驱动：拥有者端在此做初始切换（写入网络），
        // 远程端跳过（OnNetworkSpawn 时已收到拥有者当前状态并回放）；单机走本地初始切换
        if (!IsSpawned || IsOwner)
        {
            stateMachine.SwitchState(stateMachine.idlingState);   // 初始进入待机
            comboStateMachine.SwitchState(comboStateMachine.NullState);   // 连击初始进入空状态
        }

        // 游戏运行时锁定并隐藏鼠标光标（用鼠标视角）；非拥有者端不抢光标
        if (!IsSpawned || IsOwner)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        // 单机（未启动网络）：OnNetworkSpawn 不会触发，这里直接绑定座位0的相机（Camera/thirdcamera）
        if (!IsSpawned)
        {
            StartCameraBinding();
        }
    }

    // ============ 联机排障日志：拥有者端每秒采样一次输入（定位"客户端无法操作角色"，定位后可删） ============
    private float _inputProbeNextLogTime;

    /// <summary>
    /// 只有 IsOwner 的 Update 会跑到这里：每秒打一次输入采样。
    /// 判断方法：客户端若收不到这行 = 客户端不是 Owner(Update 提前 return)；收到了但输入恒为 0 = 输入系统没生效。
    /// </summary>
    private void LogInputProbeOncePerSecond()
    {
        if (Time.unscaledTime < _inputProbeNextLogTime) return;
        _inputProbeNextLogTime = Time.unscaledTime + 1f;

    }

    protected override void Update()
    {
        // 非拥有者端不做本地模拟：状态机/输入只驱动自己的角色
        if (IsSpawned && !IsOwner) return;

        LogInputProbeOncePerSecond();         // 联机排障：客户端跑不到这里 = IsOwner=false

        base.Update();                        // 地面检测 + 重力 + 竖直速度
        stateMachine?.Update();               // 状态机Tick
        comboStateMachine?.Update();          // 连击状态机Tick
        HandleDodgeInput();                   // 右键开无敌窗口（带冷却）
        //currentMovementState = stateMachine.CurrentState?.GetType().Name;   // 同步调试显示
    }

    // ================================================================
    // 相机绑定（场景预置两套相机：第一人 Camera/thirdcamera，第二人 Camera (1)/thirdcamera (1)）
    // ================================================================

    private const string SeatCameraBaseName = "Camera";      // 主相机基名（渲染，带 CinemachineBrain）
    private const string SeatVCamBaseName = "thirdcamera";   // vcam 基名（跟随）
    private const float CameraRetryInterval = 0.5f;          // 相机查找重试间隔（秒）

    private bool _cameraAssigned; // 本机相机关联完成（成功后停止重试）
    private bool _warnedNoCamera; // 找不到相机警告只打一次
    private Coroutine _cameraBindCoroutine; // 相机绑定协程引用

    /// <summary>
    /// 启动相机绑定（在 OnNetworkSpawn 中调用，替代 Update 中的每帧轮询）
    /// 使用协程延迟重试，每 0.5 秒查找一次，找到即停
    /// </summary>
    private void StartCameraBinding()
    {
        if (_cameraAssigned) return;
        if (_cameraBindCoroutine != null) return; // 已在重试中

        _cameraBindCoroutine = StartCoroutine(CameraBindCoroutine());
    }

    /// <summary>
    /// 相机绑定协程：每 0.5 秒重试一次，找到相机后停止
    /// 相比每帧轮询，减少 90% 以上的 FindObjectsByType 调用
    /// </summary>
    private System.Collections.IEnumerator CameraBindCoroutine()
    {
        while (!_cameraAssigned)
        {
            TryBindSceneCamera();
            if (!_cameraAssigned)
            {
                yield return new WaitForSeconds(CameraRetryInterval);
            }
        }
        _cameraBindCoroutine = null;
    }

    /// <summary>
    /// 按本角色座位号选场景里预置的那套相机（不生成相机）：
    ///   座位 0 → Camera / thirdcamera
    ///   座位 1 → Camera (1) / thirdcamera (1)
    /// 把 vcam 的 Follow/LookAt 绑到本角色，主相机赋给 viewCamera，
    /// 并停用本机其它 Camera/vcam，避免一屏叠多个画面。
    /// 优化：合并 FindObjectsByType 调用，一次性查找所有需要的对象
    /// </summary>
    private void TryBindSceneCamera()
    {
        if (_cameraAssigned) return;

        // 只给【本机拥有(IsOwner)】的角色绑相机：
        //   联机时服务端上会同时存在双方角色的副本，若两边都跑绑定，
        //   后绑的那个会执行"停用本机其它相机"，把先绑那套（主机的）关掉 → 主机没画面/晃不了视角。
        //   （单机未 spawn 时 IsOwner 恒为 false，这里不会拦截。）
        if (IsSpawned && !IsOwner) return;

        // 座位解析：
        //   单机           → 固定用座位 0 那套（Camera/thirdcamera）
        //   联机           → 等 BattleSpawnManager 写入座位号（-1 = 还没分到，本次跳过、下次重试）
        // 目的：联机时不会在座位号还没同步到的情况下，误绑成座位 0 的那套相机
        int seat;
        if (!IsSpawned)
        {
            seat = 0;
        }
        else
        {
            seat = NetSeatId.Value;
            if (seat < 0) return;
        }

        string vcamName = seat == 0 ? SeatVCamBaseName : $"{SeatVCamBaseName} ({seat})";
        string camName = seat == 0 ? SeatCameraBaseName : $"{SeatCameraBaseName} ({seat})";

        // 绑定成功前把 UICamera 挂进场景所有主相机的 Camera Stack（幂等），
        // 保证无论哪台座位相机在渲染，UI 层都可见
        EnsureUICameraInStacks();

        // 一次性查找所有需要的对象（合并 4 次 FindObjectsByType 为 1 次）
        // 注意：要用基类 CinemachineVirtualCameraBase —— CinemachineFreeLook 不是 CinemachineVirtualCamera，
        // 直接按 CinemachineVirtualCamera 查只会查到 FreeLook 内部的 Middle/Bottom/TopRig 三个子机位。
        var allVCams = FindObjectsByType<CinemachineVirtualCameraBase>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var allCams = FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        // 优先用场景里的座位相机表（BattleCameraRig，序列化配置，不依赖物体名字）；
        // 没有相机表（如 BattleTest.scene）时回退到按名字匹配。
        CinemachineVirtualCameraBase vcam = null;
        Camera mainCam = null;
        if (BattleCameraRig.Instance == null
            || !BattleCameraRig.Instance.TryGetSeat(seat, out mainCam, out vcam))
        {
            vcam = allVCams.FirstOrDefault(v => v.gameObject.name == vcamName);
            mainCam = allCams.FirstOrDefault(c => c.gameObject.name == camName);
        }
        var vcamGo = vcam != null ? vcam.gameObject : null;

        if (vcam == null || mainCam == null)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!_warnedNoCamera)
            {
                _warnedNoCamera = true;
                // 排障：复用已查找的结果，不再额外调用 FindObjectsByType
                string vcams = string.Join(", ", allVCams.Select(v => $"\"{v.gameObject.name}\"(active={v.gameObject.activeSelf})"));
                string cams = string.Join(", ", allCams.Select(c => $"\"{c.gameObject.name}\"(active={c.gameObject.activeSelf})"));
                Debug.LogWarning($"[{name}] 没找到座位{seat}的相机({camName}/{vcamName})，将每 {CameraRetryInterval} 秒重试。\n" +
                                 $"运行时场景里的 vcam: [{vcams}]\n" +
                                 $"运行时场景里的 Camera: [{cams}]", this);
            }
#endif
            return; // 下次重试再试
        }

        // 确保本座位那套相机是启用状态（防止场景里初始是关的）
        vcamGo.SetActive(true);
        mainCam.gameObject.SetActive(true);

        vcam.Follow = transform;   // vcam 跟随本角色
        vcam.LookAt = transform;   // vcam 看向本角色
        viewCamera = mainCam;      // Player.viewCamera(主相机) = 本座位的 Camera

        // 相机工具类绑定【本座位】的 vcam（多人时不能全场景找第一台，否则两个玩家抢同一台 POV）
        // FreeLook 等复合虚拟相机不支持 CinemachinePOV 回正，这里只对普通 VirtualCamera 生效
        playerCameraUtility?.Init(vcam as CinemachineVirtualCamera);

        // 一屏只留自己那套相机：停用本机其它主相机和 vcam
        // （FreeLook 的 Middle/Bottom/TopRig 也是 VirtualCameraBase，属于选中 vcam 的子物体，不能停用）
        foreach (var v in allVCams)
            if (v != vcam && !v.transform.IsChildOf(vcam.transform)) v.gameObject.SetActive(false);
        var uiCamera = ResolveUICamera();
        foreach (var c in allCams)
        {
            // UICamera 是 Overlay 相机（DDOL），不是场景渲染相机，绝不能被停用。
            // 除了 UIManager 注入的相机，还要认 UICameraService 的兜底相机：
            // 挑战/战斗场景直接 Play 时 UIManager.UICamera 可能还没初始化，只比较引用会把 UI 相机一起停掉。
            if (c == null || c == uiCamera) continue;
            if (c.name == UICameraService.UICameraObjectName) continue;
            if (c.GetComponent<UICameraService.UICameraRig>() != null) continue;
            if (c != mainCam) c.gameObject.SetActive(false);
        }

        _cameraAssigned = true;
           Debug.Log($"[{name}] 座位{seat}: 主相机={camName} vcam={vcamName} 已关联本角色", this);
    }

    /// <summary>
    /// 取当前可用的 UI 相机：优先框架注入的，其次 UICameraService 的场景/兜底相机。
    /// 挑战场景直接 Play 时框架不一定已经初始化，必须有第二条路，否则 UI 会整层不渲染。
    /// </summary>
    private static Camera ResolveUICamera()
    {
        Camera camera = UIManager.Instance != null ? UIManager.Instance.UICamera : null;
        if (camera == null) camera = UICameraService.UICamera;
        if (camera != null && !camera.gameObject.activeSelf)
            camera.gameObject.SetActive(true);
        return camera;
    }

    /// <summary>
    /// 把 UICamera(Overlay) 挂进场景里所有主相机的 Camera Stack（幂等，可重复调用）。
    /// UIManager 初始化时只挂了当时那台主相机；战斗场景的座位相机不挂的话，整个 UI 层都不会被渲染。
    /// </summary>
    private void EnsureUICameraInStacks()
    {
        var uiCamera = ResolveUICamera();
        if (uiCamera == null) return;
        foreach (var cam in FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (cam == uiCamera) continue;
            var camData = cam.GetComponent<UniversalAdditionalCameraData>();
            // 只有 Base 相机才有 cameraStack；Overlay 相机访问该属性会报错并返回 null
            if (camData != null
                && camData.renderType == CameraRenderType.Base
                && camData.cameraStack != null
                && !camData.cameraStack.Contains(uiCamera))
            {
                camData.cameraStack.Add(uiCamera);
            }
        }
    }

    // ================================================================
    // 受击处理
    // ================================================================

    private void OnEnable()
    {
        // 受到伤害 → 进受击硬直（HealthModel.TakeDamage 派发 E_OnDamage）
        EventCenter.MainInstance.AddListener<DamageData>(E_EventType.E_OnDamage, this, OnDamageTaken);
        // 无敌窗口内被打中 → 触发闪避（HealthModel.TakeDamage 派发 E_DamageBlocked）
        EventCenter.MainInstance.AddListener<DamageData>(E_EventType.E_DamageBlocked, this, OnDamageBlocked);
        // 死亡事件（HealthModel 派发 E_OnDeath）
        EventCenter.MainInstance.AddListener<DeathData>(E_EventType.E_OnDeath, this, OnDeath);
    }

    private void OnDisable()
    {
        EventCenter.MainInstance.UnregisterTarget(this);

        // 停止相机绑定协程（避免销毁后残留协程访问已销毁组件）
        if (_cameraBindCoroutine != null)
        {
            StopCoroutine(_cameraBindCoroutine);
            _cameraBindCoroutine = null;
        }

        // 释放命中顿帧的 CancellationTokenSource（避免内存泄漏）
        if (hitStopCts != null)
        {
            hitStopCts.Cancel();
            hitStopCts.Dispose();
            hitStopCts = null;
        }

        // 角色被禁用/销毁（切场景、退出战斗、反序列化等）时状态机不会走正常 Exit，
        // 主动取消挂起的计时器（与 OnNetworkDespawn 互为兜底，幂等可重复调用）
        // 但死亡状态下不取消（避免销毁过程中的空引用）
        if (!_isDead)
        {
            stateMachine?.CancelPendingTimers();
        }
    }

    private void OnDamageTaken(DamageData data)
    {
        if (IsSpawned && !IsOwner) return;   // 血量只在拥有者端结算，事件也只在拥有者端派发
        if (_isDead) return;  // 死亡后不接受受击
        if (data.target == gameObject && !data.isDoT)   // DoT（灼烧类）不进受击硬直
        {
            TakeHit();
        }
    }

    // ================================================================
    // 死亡处理
    // ================================================================

    /// <summary>死亡事件处理</summary>
    private void OnDeath(DeathData data)
    {
        if (data.target != gameObject) return;  // 只处理自己的死亡
        if (_isDead) return;                     // 防止重复死亡

        Die();
    }

    /// <summary>
    /// 死亡入口：任何状态下血量归零都能直接跳转到死亡状态
    /// 顺序：先切连击→Null（复位），再切移动→Dead（播放死亡动画）
    /// </summary>
    public virtual void Die()
    {
        if (_isDead) return;  // 防止重复死亡
        _isDead = true;

        // 状态机为空时也标记死亡，防止后续逻辑继续执行
        if (comboStateMachine == null || stateMachine == null) return;

        // 强制打断当前状态，切换到死亡状态
        // StateMachine.SwitchState 会先调用当前状态的 Exit()，再进入 deadState
        comboStateMachine.SwitchState(comboStateMachine.NullState);
        stateMachine.SwitchState(stateMachine.deadState);
    }

    // ================================================================
    // 状态同步写入（状态机 SwitchState 钩子 / 连击辅助类调用；只允许拥有者写）
    // ================================================================

    /// <summary>同步移动状态（拥有者写入；单机/远程端为空操作）</summary>
    public void SyncMoveState(MovementStateType type)
    {
        if (!IsSpawned || !IsOwner) return;
        netMoveState.Value = type;
        netStateStartTime.Value = (float)NetworkManager.ServerTime.Time;   // 状态起始时间戳（相位对齐用）
    }

    /// <summary>同步连击状态（拥有者写入；单机/远程端为空操作）</summary>
    public void SyncComboState(ComboStateType type)
    {
        if (!IsSpawned || !IsOwner) return;
        netComboState.Value = type;
        netStateStartTime.Value = (float)NetworkManager.ServerTime.Time;   // 状态起始时间戳（相位对齐用）
    }

    /// <summary>同步连击段数（拥有者写入；单机/远程端为空操作）</summary>
    public void SyncComboIndex(int index)
    {
        if (!IsSpawned || !IsOwner) return;
        netComboIndex.Value = index;
        netStateStartTime.Value = (float)NetworkManager.ServerTime.Time;   // 状态起始时间戳（相位对齐用）
    }

    /// <summary>同步当前技能标识（拥有者写入；远程端据此还原技能动画；单机/远程端为空操作）</summary>
    public void SyncSkillId(int skillIdHash)
    {
        if (!IsSpawned || !IsOwner) return;
        netSkillIdHash.Value = skillIdHash;
    }

    /// <summary>同步前进攻击标识（拥有者写入；单机/远程端为空操作）</summary>
    public void SyncForwardATK(bool isForward)
    {
        if (!IsSpawned || !IsOwner) return;
        netForwardATK.Value = isForward;
    }

    // ================================================================
    // 状态同步回放（远程端：收到状态变化 → 切换对应状态播动画）
    // ================================================================

    private void OnMoveStateChanged(MovementStateType previous, MovementStateType current)
    {
        if (IsOwner) return;   // 拥有者端状态本来就是本地切换的，不需要回放
        if (stateMachine == null) return;

        MarkPhaseOffsetPending();
        switch (current)
        {
            case MovementStateType.Idle: stateMachine.SwitchState(stateMachine.idlingState); break;
            case MovementStateType.Walking: stateMachine.SwitchState(stateMachine.walkingState); break;
            case MovementStateType.WalkStop: stateMachine.SwitchState(stateMachine.walkStopState); break;
            case MovementStateType.Dashing: stateMachine.SwitchState(stateMachine.dashingState); break;
            case MovementStateType.Dodge: stateMachine.SwitchState(stateMachine.dodgeState); break;
            case MovementStateType.Hurt: stateMachine.SwitchState(stateMachine.hurtState); break;
            case MovementStateType.Null: stateMachine.SwitchState(stateMachine.playerMovementNullState); break;
            case MovementStateType.Dead: stateMachine.SwitchState(stateMachine.deadState); break;
        }
        ClearPhaseOffsetPending();
    }

    private void OnComboStateChanged(ComboStateType previous, ComboStateType current)
    {
        if (IsOwner) return;
        if (comboStateMachine == null) return;

        MarkPhaseOffsetPending();
        switch (current)
        {
            case ComboStateType.Null: comboStateMachine.SwitchState(comboStateMachine.NullState); break;
            case ComboStateType.Attacking: comboStateMachine.SwitchState(comboStateMachine.ATKIngState); break;
            case ComboStateType.Skill: comboStateMachine.SwitchState(comboStateMachine.SkillState); break;
        }
        ClearPhaseOffsetPending();
    }

    private void OnComboIndexChanged(int previous, int current)
    {
        if (IsOwner) return;
        if (comboStateMachine == null) return;

        comboStateMachine.ReusableData.currentIndex.Value = current;

        // 段数前进 = 拥有者切到下一段：重进攻击状态播放新段动画（当前正处于攻击段且非收尾时）
        if (current > previous && comboStateMachine.CurrentState is PlayerATKIngState { IsRecovery: false })
        {
            MarkPhaseOffsetPending();
            comboStateMachine.SwitchState(comboStateMachine.ATKIngState);
            ClearPhaseOffsetPending();
        }
    }

    private void OnForwardATKChanged(bool previous, bool current)
    {
        if (IsOwner) return;

        // 使用运行时副本（避免修改共享的 ScriptableObject）
        var lightCombo = comboStateMachine?.characterCombo?.LightCombo;
        if (lightCombo == null) return;
        if (current) lightCombo.SwitchForwardATK();
        else lightCombo.ResetComboDates();
    }

    // ================================================================
    // 远程端动画相位对齐（状态变化 → 按已播放时长定位起点，而非从头重放）
    // ================================================================

    /// <summary>相位缓冲：防网络抖动导致远程端超前于拥有者，预留一点"慢半拍"的余量</summary>
    private const float RemotePhaseBuffer = 0.05f;

    /// <summary>本次网络状态切换是否带相位偏移（进入状态播放第一个动画时消费）</summary>
    private bool phaseOffsetPending;

    /// <summary>状态回调触发时标记：下一次播放动画按时间戳偏移起播</summary>
    private void MarkPhaseOffsetPending()
    {
        phaseOffsetPending = IsRemote;
    }

    private void ClearPhaseOffsetPending()
    {
        phaseOffsetPending = false;
    }

    /// <summary>起播时按时间戳对齐：已播放时长 - 缓冲，钳制到片段长度内；消费后自动清除</summary>
    public void ApplyRemotePhaseOffset(Animancer.AnimancerState state)
    {
        if (!phaseOffsetPending || state == null) return;
        phaseOffsetPending = false;

        float elapsed = Mathf.Max(0f, (float)NetworkManager.ServerTime.Time - netStateStartTime.Value);
        if (elapsed <= RemotePhaseBuffer) return;
        state.Time = Mathf.Min(elapsed - RemotePhaseBuffer, Mathf.Max(0f, state.Length));
    }

    /// <summary>
    /// 受击入口：打断当前动作进入受击硬直。
    /// 顺序敏感：先切连击→空状态（复位连招 + 清除攻击动画残留回调），
    /// 再切移动→受击（覆盖空状态 Enter 恢复的移动，硬直期间锁定输入）。
    /// </summary>
    public virtual void TakeHit()
    {
        if (_isDead) return;  // 死亡后不接受受击
        if (comboStateMachine == null || stateMachine == null) return;

        comboStateMachine.SwitchState(comboStateMachine.NullState);
        stateMachine.SwitchState(stateMachine.hurtState);
    }

    // ================================================================
    // 闪避（右键：开无敌窗口，窗口内受伤才触发闪避）
    // ================================================================

    private float dodgeCooldownRemain;

    /// <summary>右键开启的无敌窗口时长（秒，读 PlayerSO 配置，未配置用默认值）</summary>
    private float DodgeInvincibleWindow => PlayerSO?.movementData?.dodgeData?.invincibleWindow ?? 0.3f;

    /// <summary>闪避冷却时长（秒，读 PlayerSO 配置，未配置用默认值）</summary>
    private float DodgeCooldown => PlayerSO?.movementData?.dodgeData?.cooldown ?? 1.5f;

    /// <summary>
    /// 右键闪避：按下右键立刻进入闪避状态并播放闪避动画，同时在服务器开一段无敌窗口；
    /// 窗口内受到的伤害被拦下（E_DamageBlocked），窗口外的伤害照常结算并打断闪避。
    /// 限制：冷却已好 + 非受击/非闪避中 + 存活。
    /// </summary>
    private void HandleDodgeInput()
    {
        // 每帧都要取一次（读取即清除），否则冷却中/受击中按下的那一下会留到之后误触发
        bool dodgePressed = CharacterInputSystem.MainInstance.ConsumeDefensePressed();

        if (_isDead) return;  // 死亡后不接受闪避
        if (dodgeCooldownRemain > 0f)
            dodgeCooldownRemain -= Time.deltaTime;

        if (!dodgePressed) return;

        if (CharacterState != null && CharacterState.IsStun) return;   // 眩晕中不能起手闪避
        if (dodgeCooldownRemain > 0f) return;
        if (stateMachine == null) return;
        if (stateMachine.CurrentState == stateMachine.hurtState) return;
        if (stateMachine.CurrentState == stateMachine.dodgeState) return;

        var health = GetComponent<HealthModel>();
        if (health == null || !health.IsAlive) return;

        // 先开无敌窗口（联网时在服务器开，服务器另有冷却限流），再立刻进入闪避状态：
        // 右键本身就是"闪避"，动画必须马上播；窗口内被打中则由 HealthModel 拦下伤害
        // （E_DamageBlocked 只做表现补充，见 OnDamageBlocked）。
        health.RequestInvincible(DodgeInvincibleWindow, DodgeCooldown);
        dodgeCooldownRemain = DodgeCooldown;   // 本地冷却只做按键手感反馈
        EnterDodgeState();
    }

    /// <summary>无敌窗口内被打中：伤害已拦下，切入闪避状态（DoT 无视无敌直接扣血，不会走到这里）</summary>
    private void OnDamageBlocked(DamageData data)
    {
        if (IsSpawned && !IsOwner) return;   // 非拥有者端不参与本地状态切换
        if (data.target != gameObject) return;
        if (!data.canTriggerDodge) return;   // Buff 无敌只拦伤害，不触发翻滚
        EnterDodgeState();
    }

    /// <summary>
    /// 闪避入口：打断当前动作进入闪避。
    /// 顺序敏感：先切连击→空状态（复位连招 + 清除攻击动画残留回调），
    /// 再切移动→闪避（覆盖空状态 Enter 恢复的移动，闪避期间锁定输入 + 无敌）。
    /// </summary>
    private void EnterDodgeState()
    {
        if (stateMachine == null || comboStateMachine == null) return;
        if (stateMachine.CurrentState == stateMachine.hurtState) return;
        if (stateMachine.CurrentState == stateMachine.dodgeState) return;

        comboStateMachine.SwitchState(comboStateMachine.NullState);
        stateMachine.SwitchState(stateMachine.dodgeState);
    }

    // ================================================================
    // 动画播放（统一入口，方便以后改Transition、加Fade）
    // ================================================================

    public void PlayAnimation(AnimationClip clip)
    {
        // 角色已销毁（切场景/退出战斗/反序列化）时 Animancer 组件已释放，Unity 的 == 判空能捕获，直接跳过
        if (characterAnimancer == null) return;
        ApplyRemotePhaseOffset(characterAnimancer.Play(clip));
    }

    public void PlayAnimation(AnimationClip clip, float fadeDuration)
    {
        if (characterAnimancer == null) return;
        ApplyRemotePhaseOffset(characterAnimancer.Play(clip, fadeDuration));
    }

    // ================================================================
    // 输入响应（状态机会调用这些，暂时暴露）
    // ================================================================

    public float CurrentMoveSpeed => CharacterInputSystem.MainInstance.Movement.magnitude;
    public bool IsMoving => CurrentMoveSpeed > 0.1f;
   public bool IsSprintHeld => CharacterInputSystem.MainInstance.dashHeld;
    public bool IsSprintPressed => CharacterInputSystem.MainInstance.dashPressed;
    public bool PressedAttack => CharacterInputSystem.MainInstance.Attack;
    public bool PressedSkill => CharacterInputSystem.MainInstance.Skill;

    // ================================================================
    // 打击感辅助（命中顿帧）
    // ================================================================

    private CancellationTokenSource hitStopCts;

    /// <summary>
    /// 命中顿帧：把时间倍速压到接近 0 制造打击停顿感，realTime 后解除。
    /// 用实时等待（ignoreTimeScale），顿帧期间动画/Update 全部停住也不会影响恢复计时。
    ///
    /// 倍速由 `PauseManager` 统一结算（暂停 0 &gt; 顿帧 0.03 &gt; 正常）：
    /// 顿帧期间打开暂停面板，暂停优先；关掉面板后若顿帧还没结束会回到顿帧倍速，
    /// 顿帧结束才恢复 1 —— 不会再出现"顿帧恢复把暂停面板解冻"的老问题。
    /// </summary>
    public void HitStop(float realSeconds)
    {
        HitStop(realSeconds, HitStopTimeScale);
    }

    /// <summary>
    /// 带倍速的顿帧：轻击用接近 0.1 的倍速（只"顿一下"），重击才压到 0.03~0.05。
    /// 倍速仍由 PauseManager 统一结算，暂停优先于顿帧。
    /// </summary>
    public void HitStop(float realSeconds, float timeScale)
    {
        // 新顿帧接管：取消旧顿帧并先退还它的计数，避免计数泄漏导致世界永久慢放
        if (hitStopCts != null)
        {
            var previous = hitStopCts;
            hitStopCts = null;
            previous.Cancel();
            previous.Dispose();
            PauseManager.PopHitStop();
        }

        var cts = new CancellationTokenSource();
        hitStopCts = cts;
        PauseManager.PushHitStop(Mathf.Clamp(timeScale, 0.01f, 1f));
        _ = HitStopAsync(realSeconds, cts);
    }

    /// <summary>顿帧期间的时间倍速（越低停顿越明显）</summary>
    private const float HitStopTimeScale = 0.03f;

    private async UniTaskVoid HitStopAsync(float realSeconds, CancellationTokenSource cts)
    {
        try
        {
            await UniTask.WaitForSeconds(realSeconds, ignoreTimeScale: true, cancellationToken: cts.Token);
        }
        catch (System.OperationCanceledException)
        {
            // 被新顿帧接管：计数已在 HitStop() 里退还，这里直接退出
            return;
        }

        // 只有"自然结束"才退计数
        if (cts.IsCancellationRequested) return;

        PauseManager.PopHitStop();
        if (hitStopCts == cts) hitStopCts = null;
    }
}
