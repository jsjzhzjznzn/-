using System;
using System.Collections.Generic;

using Animancer;

using SkierFramework;

using Unity.Profiling;
using UnityEngine;

/// <summary>
/// 敌人 AI 门面（唯一对外入口）：
///   感知 → 决策（内置决策 / 行为树）→ 意图 → 动作层 → Animancer
///
/// 设计要点（与《敌人AI架构改进方案》一致）：
///  - 决策层只发 Intent（Request），不直接 Play 动画；
///  - 动作层（EnemyActionController）独占动画、判定帧、位移与打断；
///  - 自身内置一套"效用式"决策（P1），后续接 Behavior Designer 时把
///    EnableBuiltInDecision 关掉、由树调 Request() 即可，动作层零改动。
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(EnemyActionController))]
public class EnemyBrain : MonoBehaviour
{
    /// <summary>§12 指标：单只怪的每帧耗时在 Profiler 里看这个标记</summary>
    private static readonly ProfilerMarker UpdateMarker = new ProfilerMarker("EnemyAI");

    [Header("数据")]
    public EnemyProfile profile;

    [Header("目标")]
    public Transform target;

    [Header("决策")]
    /// <summary>true = 用内置决策（P1）；接行为树后置 false，避免两套决策打架</summary>
    public bool enableBuiltInDecision = true;

    /// <summary>决策节流（秒）：行为树 tick 一致的量级</summary>
    public float decisionInterval = 0.15f;

    [Header("调试")]
    public bool logActions;

    // ──────────────────────────────
    // 运行时
    // ──────────────────────────────

    private AnimancerComponent animancer;
    private HealthModel health;
    private EnemyActionController controller;

    private bool dead;
    private float groundY;
    private float nextPerceptionTime;
    private float nextDecisionTime;
    private float nextAttackAllowedTime;
    private float reactionReadyTime;
    private float lastDamageTime = -99f;
    private float poiseCurrent;
    private bool poiseBroken;
    private int phase = 1;

    /// <summary>阶段变了但动作层正忙：挂起演出，等下一次空闲</summary>
    private bool pendingPhaseIntro;
    private string lastAttackId = string.Empty;

    private readonly Dictionary<string, float> attackCooldowns = new Dictionary<string, float>();
    private readonly Collider[] hitBuffer = new Collider[16];
    private int playerMask;
    private int environmentMask;

    /// <summary>位移碰撞探测：半径/高度/skin（r 略小于模型，避免卡缝）</summary>
    private const float MoveProbeRadius = 0.35f;
    private const float MoveProbeHeight = 0.9f;
    private const float MoveProbeSkin = 0.02f;

    /// <summary>调试计数：位移调用次数与滑动次数（排查贴墙龟速用，可随时删）</summary>
    /// <summary>贴墙滑动方向（+1/-1，0=未贴墙）。一旦选定就保持，避免每帧重选导致左右摆动互相抵消。</summary>
    private float wallSide;

    /// <summary>被障碍挡住后的绕行窗口：这段时间决策层只走环绕（沿墙侧移），不再硬往墙里撞</summary>
    private float blockedUntil;
    private int blockedSide = 1;

    /// <summary>选定侧连续被堵多少帧后换边</summary>
    private int wallSideBlockedFrames;

    private const int WallSideSwitchFrames = 6;

    // 感知缓存（0.1s 刷新）
    private float targetDistance = 999f;
    private float targetAngle = 180f;
    private bool hasLineOfSight;

    /// <summary>动作完成回调（行为树的 WaitActionEnd 订阅它，而不是轮询）</summary>
    public event Action<EnemyActionKind> ActionFinished;

    // ── 对外只读状态（给玩家自动索敌/测试用）──
    public AnimancerComponent Animancer => animancer;
    public EnemyProfile Profile => profile;
    public Transform Target => target;
    public bool IsDead => dead;
    public bool IsAttacking => controller != null && controller.CurrentKind == EnemyActionKind.Attack;
    public bool IsStaggered => controller != null && controller.CurrentKind == EnemyActionKind.Stagger;
    public bool IsPoiseBroken => poiseBroken;
    public float PoiseNormalized => profile != null && profile.maxPoise > 0f ? Mathf.Clamp01(poiseCurrent / profile.maxPoise) : 0f;
    public float TargetDistance => targetDistance;
    public bool HasLineOfSight => hasLineOfSight;
    public int Phase => phase;

    /// <summary>
    /// 阶段攻速倍率：每进一个阶段 +phaseAttackSpeedBonus（文档 §8 P1 #11「HP50% 后攻速 +10%」）。
    /// 同时作用于**动画播放速度**与**招式冷却**，否则只快一半、观感是断的。
    /// </summary>
    public float AttackSpeedMultiplier
    {
        get
        {
            float bonus = profile != null ? Mathf.Max(0f, profile.phaseAttackSpeedBonus) : 0f;
            return 1f + Mathf.Max(0, phase - 1) * bonus;
        }
    }
    public string LastAttackId => lastAttackId;
    public int AttackCount { get; private set; }

    // ────────────── 文档 §12 验收指标（可量化，不靠感觉） ──────────────

    /// <summary>每 30 秒打一条指标汇总</summary>
    [Header("指标（文档 §12）")]
    [Tooltip("每 30 秒输出一条指标汇总：出手率 / 命中率 / 被打断率")]
    public bool logMetrics;

    /// <summary>完成的出手次数（含被打断的那部分）</summary>
    public int AttacksFinished { get; private set; }

    /// <summary>判定帧总数 / 实际命中次数，差值就是挥空（用于算玩家闪避成功率）</summary>
    public int HitFramesResolved { get; private set; }
    public int HitsLanded { get; private set; }

    /// <summary>出手被硬直打断（破防） / 被受击打断</summary>
    public int InterruptedByStagger { get; private set; }
    public int InterruptedByHurt { get; private set; }

    /// <summary>破韧次数</summary>
    public int PoiseBreakCount { get; private set; }

    /// <summary>挑衅次数（§8 P2 #15 观测用）</summary>
    public int TauntCount { get; private set; }

    private float nextTauntTime;

    private float metricsStartTime;
    private float nextMetricsTime;
    private const float MetricsInterval = 30f;

    /// <summary>出手次数（进入前摇即 +1；AttackCount 是"完整打完"的次数，指标口径分开）</summary>
    public int AttacksStarted { get; private set; }
    public EnemyActionKind CurrentAction => controller != null ? controller.CurrentKind : EnemyActionKind.None;

    /// <summary>动作层引用（行为树任务用）</summary>
    public EnemyActionController Controller => controller;

    /// <summary>当前攻击动作（弹刀判定用；不在攻击中为 null）</summary>
    public AttackAction CurrentAttack => controller != null ? controller.Current as AttackAction : null;

    /// <summary>
    /// 弹刀：强制打断当前动作并进入短硬直（无视打断矩阵）。
    /// 普通受击要遵守"前摇霸体"；弹刀是拼刀成功，必须能把判定中的招式打断 —— 所以先 ForceStop 再起 Stagger。
    /// </summary>
    public void ForceStagger(float seconds = -1f)
    {
        if (dead || controller == null) return;
        controller.ForceStop();
        controller.TryStart(new StaggerAction(this, false, seconds));
    }

    /// <summary>现在可以出手吗（全局节奏就绪 + 未死亡）</summary>
    public bool CanAttackNow => !dead && profile != null && Time.time >= nextAttackAllowedTime;

    // ──────────────────────────────
    // 生命周期
    // ──────────────────────────────

    private void Awake()
    {
        controller = GetComponent<EnemyActionController>();
        animancer = GetComponent<AnimancerComponent>();
        health = GetComponent<HealthModel>();

        int enemyLayer = LayerMask.NameToLayer("enemy");
        if (enemyLayer >= 0) gameObject.SetLayerRecursively(enemyLayer);

        playerMask = LayerMask.GetMask("player");
        environmentMask = LayerMask.GetMask("environment");

        // 位移完全由动作层控制：Goblin 片段带根位移，开启会把模型一路顶上天（已踩过）
        if (animancer != null && animancer.Animator != null) animancer.Animator.applyRootMotion = false;

        groundY = transform.position.y;

        if (profile != null)
        {
            poiseCurrent = profile.maxPoise;
            for (int i = 0; i < profile.attacks.Length; i++) profile.attacks[i]?.Validate();
        }

        // 目标绑定必须在 Awake 完成：BehaviorTree 的 Start 可能早于本组件的 Start，
        // 那时若 target 还是 null，树的第一个 tick 会直接走 Idle 分支并"完成"。
        if (target == null) BindDefaultTarget();
    }

    private void BindDefaultTarget()
    {
        Player player = FindObjectOfType<Player>();
        if (player != null) target = player.transform;
    }

    private void OnEnable()
    {
        EventCenter.MainInstance.AddListener<DamageData>(E_EventType.E_OnDamage, this, OnDamaged);
        EventCenter.MainInstance.AddListener<DeathData>(E_EventType.E_OnDeath, this, OnDead);
    }

    private void OnDisable()
    {
        EventCenter.MainInstance.UnregisterTarget(this);
    }

    private void Start()
    {
        // Awake 已绑过；这里只兜底"运行时才生成玩家"的场景（联机/延迟生成）
        if (target == null) BindDefaultTarget();

        metricsStartTime = Time.time;
        nextMetricsTime = Time.time + MetricsInterval;
        ScheduleNextTaunt();

        reactionReadyTime = Time.time + (profile != null ? profile.reactionDelay : 0.18f);
        nextAttackAllowedTime = Time.time + 0.8f;   // 给玩家一点准备时间（与旧版一致）
        PlayLocomotion(profile != null ? profile.idleClip : null);
    }

    private void Update()
    {
        using (UpdateMarker.Auto())
        {
            TickBrain();
            LogMetricsIfDue();
        }
    }

    /// <summary>每帧主体（放在 Update 里由 ProfilerMarker 包住，便于按 §12 量单只怪耗时）</summary>
    private void TickBrain()
    {
        // 贴地：任何位移/动画漂移都在下一帧拉回
        Vector3 pos = transform.position;
        if (!Mathf.Approximately(pos.y, groundY))
            transform.position = new Vector3(pos.x, groundY, pos.z);

        if (dead) return;

        UpdatePerception();
        RecoverPoise();
        UpdatePhase();
        TryPlayPendingPhaseIntro();

        if (enableBuiltInDecision) TickBuiltInDecision();
    }

    // ──────────────────────────────
    // 感知
    // ──────────────────────────────

    private void UpdatePerception()
    {
        if (Time.time < nextPerceptionTime) return;
        nextPerceptionTime = Time.time + 0.1f;

        if (target == null)
        {
            targetDistance = 999f;
            hasLineOfSight = false;
            return;
        }

        Vector3 to = target.position - transform.position;
        to.y = 0f;
        targetDistance = to.magnitude;
        targetAngle = to.sqrMagnitude > 0.0001f ? Vector3.Angle(transform.forward, to) : 0f;

        if (targetDistance > (profile != null ? profile.sightRange : 25f) || targetAngle > (profile != null ? profile.sightAngle : 180f) * 0.5f)
        {
            hasLineOfSight = false;
            return;
        }

        // 只对 environment 层做遮挡检测（别被玩家/掉落物挡住视线）
        Vector3 eye = transform.position + Vector3.up * 1.4f;
        Vector3 chest = target.position + Vector3.up * 1.0f;
        hasLineOfSight = !Physics.Linecast(eye, chest, environmentMask, QueryTriggerInteraction.Ignore);
    }

    // ──────────────────────────────
    // 内置决策（P1）：后续可整体替换为行为树
    // ──────────────────────────────

    private void TickBuiltInDecision()
    {
        if (profile == null || controller == null) return;
        if (!controller.IsIdle) return;                      // 动作层在忙：不抢
        if (Time.time < reactionReadyTime) return;           // 反应延迟
        if (Time.time < nextDecisionTime) return;
        nextDecisionTime = Time.time + Mathf.Max(0.05f, decisionInterval);

        if (target == null)
        {
            PlayLocomotion(profile.idleClip);
            return;
        }

        if (targetDistance > profile.sightRange)
        {
            PlayLocomotion(profile.idleClip);
            return;
        }

        // 0) 待机破绽（§8 P2 #15）：按档案的 15~25s 间隔、且目标已被拉开距离时挑衅一次，
        //    **无霸体**，任何攻击都能打断。贴身缠斗时不插，否则会打断攻击节奏、看起来像发呆。
        if (CanTaunt)
        {
            if (controller.TryStart(new TauntAction(this)))
            {
                ScheduleNextTaunt();
                return;
            }
        }

        // 0.5) 刚被障碍挡住：优先沿墙侧移（StrafeAction 是承诺动作，不会每帧翻方向）
        if (Time.time < blockedUntil)
        {
            if (controller.TryStart(new StrafeAction(this, blockedSide)))
                return;
        }

        // 1) 出手：距离够 + 有视线 + 全局节奏就绪
        bool inAttackWindow = targetDistance <= profile.attackRange + 0.6f;
        if (inAttackWindow && hasLineOfSight && Time.time >= nextAttackAllowedTime)
        {
            AttackDef def = profile.PickAttack(targetDistance, phase, attackCooldowns, lastAttackId, out _);
            if (def != null)
            {
                if (controller.TryStart(new AttackAction(this, def)))
                {
                    lastAttackId = def.id;
                    attackCooldowns[def.id] = Time.time + def.cooldown / AttackSpeedMultiplier;
                    return;
                }
            }
        }

        // 2) 贴太近：有概率后撤，制造"拉开—突进"的节奏
        if (targetDistance < profile.stopDistance * 0.75f && UnityEngine.Random.value < profile.backstepChance)
        {
            if (controller.TryStart(new BackstepAction(this))) return;
        }

        // 3) 中距离：有概率环绕（不直勾勾贴脸）
        bool strafeBand = targetDistance > profile.stopDistance * 0.9f && targetDistance < profile.strafeRadius + 1.2f;
        if (strafeBand && UnityEngine.Random.value < profile.strafeChance)
        {
            int dir = UnityEngine.Random.value < 0.5f ? -1 : 1;
            if (controller.TryStart(new StrafeAction(this, dir))) return;
        }

        // 4) 追击
        if (targetDistance > profile.stopDistance)
        {
            if (controller.TryStart(new MoveAction(this))) return;
        }

        // 5) 站桩
        PlayLocomotion(profile.idleClip);
    }

    /// <summary>被行为树调用：按当前距离/阶段/冷却/权重挑一个招式 id（空串=没有可用招式）</summary>
    public string PickAttackId()
    {
        if (profile == null) return string.Empty;
        AttackDef def = profile.PickAttack(HorizontalDistanceToTarget(), phase, attackCooldowns, lastAttackId, out _);
        return def != null ? def.id : string.Empty;
    }

    /// <summary>被行为树调用：发一个意图，交给动作层仲裁</summary>
    public EnemyIntentResult Request(EnemyIntent intent)
    {
        if (dead || profile == null || controller == null) return EnemyIntentResult.Reject();

        switch (intent.Type)
        {
            case EnemyIntentType.Idle:
                PlayLocomotion(profile.idleClip);
                return EnemyIntentResult.Accept(false);

            case EnemyIntentType.MoveTo:
                return controller.TryStart(new MoveAction(this)) ? EnemyIntentResult.Accept(true) : EnemyIntentResult.Reject();

            case EnemyIntentType.Strafe:
                return controller.TryStart(new StrafeAction(this, intent.Direction != 0
                        ? (int)Mathf.Sign(intent.Direction)
                        : (UnityEngine.Random.value < 0.5f ? -1 : 1)))
                    ? EnemyIntentResult.Accept(true) : EnemyIntentResult.Reject();

            case EnemyIntentType.FaceTarget:
                return controller.TryStart(new FaceTargetAction(this)) ? EnemyIntentResult.Accept(true) : EnemyIntentResult.Reject();

            case EnemyIntentType.Backstep:
                return controller.TryStart(new BackstepAction(this)) ? EnemyIntentResult.Accept(true) : EnemyIntentResult.Reject();

            case EnemyIntentType.Attack:
            {
                AttackDef def = profile.FindAttack(intent.AttackId);
                if (def == null || def.clip == null) return EnemyIntentResult.Reject();
                if (Time.time < nextAttackAllowedTime) return EnemyIntentResult.Reject();
                if (!controller.TryStart(new AttackAction(this, def))) return EnemyIntentResult.Reject();
                lastAttackId = def.id;
                attackCooldowns[def.id] = Time.time + def.cooldown / AttackSpeedMultiplier;
                return EnemyIntentResult.Accept(true);
            }
        }
        return EnemyIntentResult.Reject();
    }

    // ──────────────────────────────
    // 受击 / 死亡 / 韧性
    // ──────────────────────────────

    private void OnDamaged(DamageData data)
    {
        if (dead || data.target != gameObject || data.isDoT || profile == null) return;

        lastDamageTime = Time.time;
        poiseCurrent -= data.amount * profile.poisePerDamage;

        if (poiseCurrent <= 0f)
        {
            poiseCurrent = 0f;
            poiseBroken = true;
            PoiseBreakCount++;
            if (controller.TryStart(new StaggerAction(this, true)))
            {
                if (logActions) Debug.Log($"[AI] {name} 破防硬直（伤害 {data.amount:F1}）", this);
            }
            return;
        }

        bool isHeavy = data.isCritical || data.amount >= profile.heavyHitDamage;

        // 轻击不总是能打断前摇：BOSS 挥到一半时挨轻击，只有 lightHitInterruptChance 的概率吃硬直，
        // 其余情况伤害与削韧照常结算、但这一刀继续挥完（否则玩家平A可以把 BOSS 永远按在半招里）。
        if (!isHeavy && ShouldSwingThroughLightHit())
        {
            if (logActions) Debug.Log($"[AI] {name} 轻击未打断前摇（伤害 {data.amount:F1}，韧性 {poiseCurrent:F0}）", this);
            return;
        }

        AnimationClip clip;
        float seconds;
        float speed;

        if (isHeavy)
        {
            // 重击：按命中方向选正面/背面受击
            clip = PickHeavyHitClip(data);
            seconds = profile.heavyHitSeconds > 0f ? profile.heavyHitSeconds : profile.hurtStaggerSeconds;
            speed = profile.heavyHitSpeed;
        }
        else
        {
            // 轻击：短受击片段（Hit_Shake），时长够到下一刀，连打就一直在受击里
            clip = profile.flinchClip != null ? profile.flinchClip : profile.hurtClip;
            seconds = profile.flinchSeconds > 0f ? profile.flinchSeconds : profile.hurtStaggerSeconds;
            speed = 1f;
        }

        // 已经在受击：连打只延长计时、不重播片段（同一段受击动画一直播）
        if (controller.Current is HurtAction currentHurt)
        {
            bool clipChanged = isHeavy && currentHurt.Clip != clip;
            if (!clipChanged)
            {
                currentHurt.Refresh();
                if (logActions) Debug.Log($"[AI] {name} 受击延长（伤害 {data.amount:F1}，韧性 {poiseCurrent:F0}）", this);
                return;
            }

            // 轻击受击中挨了重击：换成重击片段（这是真正的状态升级，允许重播一次）
            controller.ForceStop();
        }

        if (controller.TryStart(new HurtAction(this, clip, seconds, speed)))
        {
            if (logActions) Debug.Log($"[AI] {name} 进入受击（伤害 {data.amount:F1}，重击={isHeavy}，韧性 {poiseCurrent:F0}）", this);
        }
    }

    /// <summary>
    /// 轻击是否"打不动"当前出手。只在攻击的前摇/判定帧生效，后摇一律可打断。
    /// 返回 true = 这一刀继续挥完（不切受击）。
    /// </summary>
    private bool ShouldSwingThroughLightHit()
    {
        if (profile == null || controller == null) return false;
        if (!(controller.Current is AttackAction attack)) return false;
        if (attack.NormalizedTime >= attack.RecoveryStart) return false;
        return UnityEngine.Random.value >= profile.lightHitInterruptChance;
    }

    /// <summary>重击片段：从正面打 → 正面受击，从背后打 → 背面受击</summary>
    private AnimationClip PickHeavyHitClip(DamageData data)
    {
        if (profile == null) return null;
        if (profile.heavyHitFrontClip == null && profile.heavyHitBackClip == null) return profile.hurtClip;

        Vector3 from = data.source != null ? data.source.transform.position : transform.position - transform.forward;
        Vector3 dir = from - transform.position;
        dir.y = 0f;
        float angle = dir.sqrMagnitude > 0.0001f ? Vector3.Angle(transform.forward, dir) : 0f;

        AnimationClip clip = angle <= 90f ? profile.heavyHitFrontClip : profile.heavyHitBackClip;
        return clip != null ? clip : profile.hurtClip;
    }

    private void OnDead(DeathData data)
    {
        if (data.target != gameObject || dead) return;
        dead = true;
        controller?.TryStart(new DeadAction(this));
        if (logActions) Debug.Log($"[AI] {name} 死亡", this);
    }

    private void RecoverPoise()
    {
        if (profile == null || poiseBroken) return;
        if (poiseCurrent >= profile.maxPoise) return;
        if (Time.time - lastDamageTime < profile.poiseRecoverDelay) return;

        float perSecond = profile.maxPoise / Mathf.Max(0.5f, profile.poiseRecoverSeconds);
        poiseCurrent = Mathf.Min(profile.maxPoise, poiseCurrent + perSecond * Time.deltaTime);
    }

    private void UpdatePhase()
    {
        if (profile == null || health == null || profile.phaseHpPercents == null || profile.phaseHpPercents.Length <= 1) return;

        float max = health.MaxHP.Value;
        if (max <= 0f) return;
        int next = profile.PhaseFor(health.CurrentHP.Value / max * 100f);
        if (next == phase)
            return;

        phase = next;
        if (logActions) Debug.Log($"[AI] {name} 进入阶段 {phase}", this);

        // 阶段演出不能打断手上的动作，但也不能丢——挂起，等当前动作结束立刻播
        pendingPhaseIntro = true;
    }

    /// <summary>
    /// 阶段演出：优先抢占位移类动作（走/环绕/后撤）立刻播；
    /// 攻击/受击/硬直/死亡不抢，等它们结束后再播（避免把一次出手截断成半招）。
    /// </summary>
    private void TryPlayPendingPhaseIntro()
    {
        if (!pendingPhaseIntro || controller == null)
            return;

        if (!controller.IsIdle)
        {
            EnemyActionKind current = controller.CurrentKind;
            bool locomotion = current == EnemyActionKind.Move || current == EnemyActionKind.Strafe
                              || current == EnemyActionKind.Backstep || current == EnemyActionKind.FaceTarget
                              || current == EnemyActionKind.Idle || current == EnemyActionKind.None;
            if (!locomotion)
                return;

            controller.ForceStop();
        }

        pendingPhaseIntro = false;
        controller.TryStart(new PhaseIntroAction(this));
    }

    /// <summary>挑衅开始：计数 + 排下一次（调用方负责排程，这里只记数）</summary>
    public void NotifyTaunt()
    {
        TauntCount++;
    }

    /// <summary>行为树用：挑衅间隔已到、且动作层空闲（可挑衅）</summary>
    public bool CanTaunt => !dead && profile != null && controller != null
                            && Time.time >= nextTauntTime && controller.IsIdle
                            && targetDistance > profile.attackRange * Mathf.Max(1f, profile.tauntMinDistanceFactor);

    /// <summary>行为树用：正处在"被障碍挡住"的绕行窗口内</summary>
    public bool IsMoveBlocked => Time.time < blockedUntil;

    /// <summary>绕行方向（+1 / -1），与 NotifyMoveBlocked 选定的一致</summary>
    public int BlockedSide => blockedSide;

    /// <summary>是否已经大致面向目标（度）：贴到停止距离后靠它决定"原地转身"何时结束</summary>
    public bool IsFacingTarget(float maxAngleDeg)
    {
        if (target == null) return true;
        Vector3 to = target.position - transform.position;
        to.y = 0f;
        if (to.sqrMagnitude < 0.0001f) return true;
        return Vector3.Angle(transform.forward, to) <= maxAngleDeg;
    }

    /// <summary>行为树用：请求一次挑衅（无霸体的破绽窗口），并排下一次间隔</summary>
    public bool TryTaunt()
    {
        if (!CanTaunt) return false;
        if (!controller.TryStart(new TauntAction(this))) return false;
        ScheduleNextTaunt();
        return true;
    }

    /// <summary>按档案的随机间隔安排下一次挑衅</summary>
    private void ScheduleNextTaunt()
    {
        float min = profile != null ? Mathf.Max(1f, profile.tauntIntervalMin) : 15f;
        float max = profile != null ? Mathf.Max(min, profile.tauntIntervalMax) : 25f;
        nextTauntTime = Time.time + UnityEngine.Random.Range(min, max);
    }

    /// <summary>动作层报告"走不动了"（贴墙/缝隙卡住）：开一段绕行窗口，让决策层改走环绕</summary>
    public void NotifyMoveBlocked()
    {
        blockedUntil = Time.time + 2f;
        blockedSide = UnityEngine.Random.value < 0.5f ? -1 : 1;

        if (logActions) Debug.Log($"[AI] {name} 被障碍挡住，绕行 side={blockedSide}", this);
    }

    /// <summary>
    /// §12 指标汇总：每 30 秒一条，口径与文档验收表一致。
    /// 出手率 = AttacksStarted / 分钟；打断率 = 被打断 / 完成出手；挥空率 = 1 - 命中/判定帧。
    /// </summary>
    private void LogMetricsIfDue()
    {
        if (!logMetrics || Time.time < nextMetricsTime)
            return;

        nextMetricsTime = Time.time + MetricsInterval;

        float minutes = Mathf.Max(0.01f, (Time.time - metricsStartTime) / 60f);
        float perMinute = AttacksStarted / minutes;
        float interruptRate = AttacksFinished > 0 ? (float)(InterruptedByStagger + InterruptedByHurt) / AttacksFinished : 0f;
        float missRate = HitFramesResolved > 0 ? 1f - (float)HitsLanded / HitFramesResolved : 0f;

        Debug.Log(
            $"[AI-METRIC] {name} 出手={AttacksStarted}({perMinute:F1}/min) 完成={AttacksFinished} " +
            $"打断(硬直)={InterruptedByStagger} 打断(受击)={InterruptedByHurt} 打断率={interruptRate:P0} " +
            $"判定帧={HitFramesResolved} 命中={HitsLanded} 挥空率={missRate:P0} 破韧={PoiseBreakCount} 阶段={phase}",
            this);
    }

    /// <summary>破防硬直结束：回满韧性并给一段免疫回复延迟（防连锁破防）</summary>
    public void OnStaggerEnd()
    {
        poiseBroken = false;
        poiseCurrent = profile != null ? profile.maxPoise : 0f;
        lastDamageTime = Time.time;
    }

    // ──────────────────────────────
    // 动作层回调
    // ──────────────────────────────

    /// <summary>攻击进入前摇时由动作层调用（"出手率"指标口径）</summary>
    public void NotifyAttackStarted()
    {
        AttacksStarted++;
    }

    public void OnActionFinished(EnemyActionKind kind)    {
        switch (kind)
        {
            case EnemyActionKind.Attack:
            {
                AttacksFinished++;
                AttackCount++;
                float jitter = 1f;
                if (profile != null)
                    jitter = UnityEngine.Random.Range(profile.intervalJitterMin, profile.intervalJitterMax);
                nextAttackAllowedTime = Time.time + (profile != null ? profile.globalCooldown : 0.6f) * jitter;
                break;
            }
        }

        ActionFinished?.Invoke(kind);
    }

    public void OnActionSwitched(EnemyActionKind from, EnemyActionKind to)
    {
        // §12：出手是否被硬直/受击打断（轻击打断率的分母来自 AttacksFinished）
        if (from == EnemyActionKind.Attack)
        {
            if (to == EnemyActionKind.Stagger) InterruptedByStagger++;
            else if (to == EnemyActionKind.Hurt) InterruptedByHurt++;
        }

        if (logActions) Debug.Log($"[AI] {name} {from} → {to}（dist {targetDistance:F2}）", this);
    }

    // ──────────────────────────────
    // 动作层用的工具
    // ──────────────────────────────

    public AnimancerState PlayClip(AnimationClip clip, float fade)
    {
        if (animancer == null || clip == null) return null;
        AnimancerState state = animancer.Play(clip, Mathf.Max(0f, fade));
        if (state != null) state.Events.Clear();
        return state;
    }

    /// <summary>循环片段（idle/run）已导入为 Loop；同一片段不重复 Play，避免每帧重启</summary>
    public void PlayLocomotion(AnimationClip clip)
    {
        if (animancer == null || clip == null) return;
        if (animancer.IsPlayingClip(clip)) return;
        PlayClip(clip, 0.2f);
    }

    /// <summary>
    /// 按指定倍速播循环片段（走位用）：同一片段只改 Speed、不重播，
    /// 倍速 = 实际移速 / 片段设计移速，用来消除脚底打滑。
    /// </summary>
    public AnimancerState PlayLocomotionScaled(AnimationClip clip, float speed, float fade = 0.2f)
    {
        if (animancer == null || clip == null) return null;

        AnimancerState current = animancer.States.Current;
        if (current != null && current.Clip == clip)
        {
            current.Speed = speed;
            return current;
        }

        AnimancerState state = PlayClip(clip, fade);
        if (state != null) state.Speed = speed;
        return state;
    }

    /// <summary>招式冷却与"上一招"的统一登记点（决策层与连段共用）</summary>
    public void RegisterAttackUse(AttackDef def)
    {
        if (def == null || profile == null) return;
        lastAttackId = def.id;
        attackCooldowns[def.id] = Time.time + def.cooldown / AttackSpeedMultiplier;
    }

    public float HorizontalDistanceToTarget()
    {
        if (target == null) return 999f;
        Vector3 to = target.position - transform.position;
        to.y = 0f;
        return to.magnitude;
    }

    public Vector3 HorizontalDirectionToTarget()
    {
        if (target == null) return transform.forward;
        Vector3 to = target.position - transform.position;
        to.y = 0f;
        return to.sqrMagnitude > 0.0001f ? to.normalized : transform.forward;
    }

    /// <summary>沿朝向位移（贴地；起手前冲也走碰撞，避免冲进墙里）</summary>
    public void MoveForward(float distance)
    {
        if (Mathf.Abs(distance) < 0.00001f) return;

        Vector3 direction = transform.forward;
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f) return;

        transform.position = MoveWithCollision(direction.normalized, distance);
    }

    /// <summary>朝目标位移（带碰撞约束：撞墙沿墙滑，正对墙左右绕）</summary>
    public void MoveTowardsTarget(float speed, float deltaTime)
    {
        Vector3 dir = HorizontalDirectionToTarget();
        transform.position = MoveWithCollision(dir, speed * deltaTime);
    }

    /// <summary>沿任意水平方向位移</summary>
    public void MoveAlong(Vector3 direction, float distance)
    {
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f) return;
        transform.position = MoveWithCollision(direction.normalized, distance);
    }

    /// <summary>
    /// 带碰撞的水平位移。
    ///
    /// 背景：敌人身上没有 CharacterController / Rigidbody，位移是直接改 transform.position，
    /// 而 transform 直移**不参与碰撞求解**——会直接穿墙（实测两点间 LOS=false，敌人却走直线穿了过去）。
    /// 这里用球形探测补上约束：撞墙→沿碰撞面切向滑；正对墙→依次试 ±60° 绕行；三面都堵→本帧不动。
    /// 用 environment 层，与视线检测同一口径。
    /// </summary>
    private Vector3 MoveWithCollision(Vector3 direction, float distance)
    {
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f || distance <= 0f)
            return ClampToGround(transform.position);

        direction.Normalize();
        Vector3 current = transform.position;

        // 1) 直奔目标（目的地可站立才算通）
        Vector3 step = direction * distance;
        if (CanStand(current + step))
        {
            wallSide = 0f;                 // 路通了，忘掉贴墙方向
            return ClampToGround(current + step);
        }

        // 2) 贴墙滑动。方向必须**粘住**：每帧重算切向会让左右交替，位移互相抵消（实测净位移≈0）。
        Vector3 slide = Vector3.Cross(Vector3.up, direction);
        if (slide.sqrMagnitude < 0.0001f)
            return ClampToGround(current);

        slide.Normalize();

        if (wallSide == 0f)
        {
            // 第一次撞墙：选一条能站的一侧，之后**一直沿这侧走**，直到路通或该侧被堵
            if (CanStand(current + slide * distance))
                wallSide = 1f;
            else if (CanStand(current - slide * distance))
                wallSide = -1f;
        }

        if (wallSide != 0f)
        {
            Vector3 side = current + slide * (wallSide * distance);
            if (CanStand(side))
            {
                wallSideBlockedFrames = 0;
                return ClampToGround(side);
            }

            // 这一侧被堵：连续几帧都走不动就换边
            wallSideBlockedFrames++;
            if (wallSideBlockedFrames >= WallSideSwitchFrames)
            {
                wallSide = -wallSide;
                wallSideBlockedFrames = 0;
            }
        }

        // 3) 半步也走不动才算堵死
        Vector3 half = current + direction * (distance * 0.4f);
        if (CanStand(half))
            return ClampToGround(half);

        return ClampToGround(current);
    }
    /// <summary>该点能不能站（环境层没有实体占据这块胶囊底面积）</summary>
    private bool CanStand(Vector3 position)
    {
        Vector3 center = position + Vector3.up * MoveProbeHeight;
        return !Physics.CheckSphere(center, MoveProbeRadius, environmentMask, QueryTriggerInteraction.Ignore);
    }

    /// <summary>平滑转向目标（度/秒），smoothstep 起步快、到位稳</summary>
    public void FaceTargetSmooth(float deltaTime, float turnSpeedDeg)
    {
        if (target == null) return;
        FaceDirectionSmooth(HorizontalDirectionToTarget(), deltaTime, turnSpeedDeg);
    }

    public void FaceDirectionSmooth(Vector3 direction, float deltaTime, float turnSpeedDeg)
    {
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f) return;

        Quaternion want = Quaternion.LookRotation(direction.normalized, Vector3.up);
        float maxDegrees = Mathf.Max(30f, turnSpeedDeg) * Mathf.Max(0f, deltaTime);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, want, maxDegrees);
    }

    private Vector3 ClampToGround(Vector3 pos)
    {
        pos.y = groundY;
        return pos;
    }

    /// <summary>
    /// 命中判定：在命中帧用扇形几何判定（照玩家 CharacterCombo.AttackDetection 的口径）。
    /// 关键收益：玩家闪到侧后方/拉开距离能真的躲开，AI 也会"挥空"。
    /// </summary>
    public void ResolveAttackHit(AttackDef def)
    {
        if (def == null || dead) return;

        Vector3 origin = transform.position + Vector3.up * 0.9f + transform.forward * (def.range * 0.5f);
        HitFramesResolved++;
        int count = Physics.OverlapSphereNonAlloc(origin, def.range * 0.5f, hitBuffer, playerMask, QueryTriggerInteraction.Ignore);

        for (int i = 0; i < count; i++)
        {
            Collider hit = hitBuffer[i];
            if (hit == null) continue;
            if (hit.transform.IsChildOf(transform)) continue;

            Vector3 to = hit.transform.position - transform.position;
            to.y = 0f;
            if (to.sqrMagnitude > 0.0001f && Vector3.Angle(transform.forward, to) > def.arc * 0.5f) continue;

            HealthModel targetHealth = hit.GetComponentInParent<HealthModel>();
            if (targetHealth == null || !targetHealth.IsAlive) continue;

            targetHealth.RequestDamage(new DamageRequest
            {
                damage = def.damage,
                isCritical = false,
                sourceId = 0,
                isDoT = false,
            }, gameObject);

            HitsLanded++;

            // 打击感统一入口：全局顿帧 + 震屏 + 受击特效/音效
            // （受击方的闪白/抖动/击退由 HitReaction 订阅 E_OnDamage 自己表现）
            Player player = hit.GetComponentInParent<Player>();
            HitFeelService.EnemyHitPlayer(this, player, hit.transform.position + Vector3.up * 0.7f,
                def.damage >= 15f);
        }
    }

    /// <summary>把目标改成指定 Transform（行为树/场景注入用）</summary>
    public void BindTarget(Transform newTarget, bool resetReaction = true)
    {
        target = newTarget;
        if (resetReaction) reactionReadyTime = Time.time + (profile != null ? profile.reactionDelay : 0.18f);
    }
}
