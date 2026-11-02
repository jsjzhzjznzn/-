using Animancer;
using SkierFramework;
using UnityEngine;

/// <summary>
/// 挑战场景的最小怪物控制（替代源工程那套 AIPlayer + Behavior Designer，那套的
/// AIPlayerSO 数据在一次误回退中丢了，留空无法驱动）。
///
/// 只做挑战玩法需要的四件事：
///   1. 站在原地朝玩家，超出攻击距离就走过去
///   2. 间隔若干秒出手，命中帧给玩家造成伤害（走 HealthModel，和玩家打它完全同一条链路）
///   3. 被玩家打中时播受击动画（订阅 E_OnDamage），播完回到待机
///   4. 血量归零播死亡动画，不再行动
///
/// 动画走 Animancer（怪物预制体本身只有 Animator 控制器为空，这里直接播 FBX 片段）。
/// </summary>
public class ChallengeMonster : MonoBehaviour
{
    [Header("目标")]
    public Transform target;

    [Header("数值")]
    public float moveSpeed = 1.6f;
    public float attackRange = 2.0f;
    public float attackInterval = 2.0f;
    public float attackDamage = 10f;
    [Range(0f, 1f)] public float attackHitTime = 0.35f;

    [Header("动画片段（怪兽.fbx 内的 Goblin 片段）")]
    public AnimationClip idleClip;
    public AnimationClip runClip;
    public AnimationClip attackClip;
    public AnimationClip hurtClip;
    public AnimationClip deathClip;

    private AnimancerComponent animancer;
    private HealthModel health;
    private float nextAttackTime;
    private bool dead;
    private bool busy;          // 攻击/受击中（不移动、不重复出手）
    private AnimancerState currentState;
    private AnimationClip loopClip;   // 当前循环播放的片段（null = 非循环）

    /// <summary>脚下的地面高度：怪物的 Y 永远钉在这里（防止根位移把模型顶上天）</summary>
    private float groundY;

    private void Awake()
    {
        // 玩家攻击判定按 player / enemy 层过滤，怪物必须在 enemy 层才打得到
        int enemy = LayerMask.NameToLayer("enemy");
        if (enemy >= 0) gameObject.SetLayerRecursively(enemy);

        animancer = GetComponent<AnimancerComponent>();
        health = GetComponent<HealthModel>();

        // 关掉动画根位移：Goblin 的片段带 root 位移，applyRootMotion=true 时每帧都会被应用到
        // transform 上（长时间累积 → 怪物一路飘到天上）。位移完全由本脚本按 XZ 控制。
        if (animancer != null && animancer.Animator != null)
            animancer.Animator.applyRootMotion = false;

        groundY = transform.position.y;
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
        if (target == null)
        {
            var player = FindObjectOfType<Player>();
            if (player != null) target = player.transform;
        }
        if (health == null) health = GetComponent<HealthModel>();

        nextAttackTime = Time.time + 1f;   // 给玩家一点准备时间
        Play(idleClip, true);
    }

    private void Update()
    {
        // Y 永远钉在开局地面高度：即使动画/根位移把 transform 拉起来，下一帧也会被拉回地面
        Vector3 pos = transform.position;
        if (!Mathf.Approximately(pos.y, groundY))
            transform.position = new Vector3(pos.x, groundY, pos.z);

        if (dead || target == null) return;

        Vector3 to = target.position - transform.position;
        to.y = 0f;
        float distance = to.magnitude;

        // 始终面向玩家
        if (to.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(to.normalized), 8f * Time.deltaTime);

        if (busy) return;

        if (distance > attackRange)
        {
            Vector3 next = transform.position + to.normalized * moveSpeed * Time.deltaTime;
            transform.position = new Vector3(next.x, groundY, next.z);
            Play(runClip != null ? runClip : idleClip, true);
            return;
        }

        Play(idleClip, true);
        if (Time.time >= nextAttackTime)
            Attack();
    }

    /// <summary>出手：播攻击动画，命中帧对玩家结算伤害（同一段只结算一次）</summary>
    private void Attack()
    {
        nextAttackTime = Time.time + attackInterval;
        busy = true;

        var state = Play(attackClip, false);
        if (state == null)
        {
            // 没配攻击动画也要能打：直接结算
            DealDamage();
            busy = false;
            return;
        }

        state.Events.Clear();
        // Animancer 的事件时间是「归一化时间」(0~1)，不是秒
        float hitTime = Mathf.Clamp01(attackHitTime);
        bool damaged = false;
        state.Events.Add(hitTime, () => { if (!damaged) { damaged = true; DealDamage(); } });
        state.Events.OnEnd = () => { state.Events.OnEnd = null; busy = false; Play(idleClip, true); };
    }

    private void DealDamage()
    {
        if (dead || target == null) return;

        var targetHealth = target.GetComponentInParent<HealthModel>();
        if (targetHealth == null || !targetHealth.IsAlive) return;

        targetHealth.RequestDamage(new DamageRequest
        {
            damage = attackDamage,
            isCritical = false,
            sourceId = 0,
            isDoT = false,
        }, gameObject);
    }

    /// <summary>被玩家打中：播受击动画（打断攻击），播完回待机</summary>
    private void OnDamaged(DamageData data)
    {
        if (dead || data.target != gameObject || data.isDoT) return;
        if (hurtClip == null) return;

        busy = true;
        var state = Play(hurtClip, false);
        if (state == null) { busy = false; return; }

        state.Events.Clear();
        state.Events.OnEnd = () => { state.Events.OnEnd = null; busy = false; Play(idleClip, true); };
    }

    private void OnDead(DeathData data)
    {
        if (data.target != gameObject || dead) return;
        dead = true;
        busy = true;
        Play(deathClip, false);
    }

    /// <summary>播放片段；loop=true 时用"播完重播"实现循环（片段本身没开 Loop，不改导入设置）。没配片段返回 null</summary>
    private AnimancerState Play(AnimationClip clip, bool loop)
    {
        if (animancer == null || clip == null) return null;
        if (loop && currentState != null && currentState.Clip == clip) return currentState;

        var state = animancer.Play(clip, 0.2f);
        state.Events.Clear();
        currentState = state;
        loopClip = loop ? clip : null;
        if (loop) state.Events.OnEnd = RestartLoop;
        return state;
    }

    /// <summary>循环片段播到末尾：从头重播并重新挂回调</summary>
    private void RestartLoop()
    {
        if (dead || animancer == null || loopClip == null || !isActiveAndEnabled) return;

        var state = animancer.Play(loopClip, 0f);
        state.Time = 0f;
        state.Events.Clear();
        state.Events.OnEnd = RestartLoop;
        currentState = state;
    }
}
