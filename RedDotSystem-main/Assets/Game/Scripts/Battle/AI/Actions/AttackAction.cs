using Animancer;
using UnityEngine;

/// <summary>
/// 一次攻击：前摇（可转向/前冲/霸体）→ 判定帧（扇形结算）→ 后摇（可被打断）。
///
/// 完成判定用"动画时长 / 播放速度"的计时器，不依赖 Animancer 的 Events.OnEnd——
/// 旧 ChallengeMonster 在攻击/受击/死亡三处互相覆盖 OnEnd，是状态卡死的根因。
///
/// 片段裁剪：只播 [startNormalized, endNormalized] 这一段。片段尾部的收招/回正属于纯等待，
/// 砍掉后用下一动作的淡入做交叉淡出，单招从 3.5~4.7s 压到 0.85~2.8s。
/// 判定帧仍然以原始片段的归一化时间为准，动作内部换算成本地进度做窗口判断。
///
/// 连段：进入时按 followUps 掷一次目标，进度到达 atNormalized 时取消剩余后摇、
/// 同帧把下一招交给控制器（Next），不回到决策层。
/// </summary>
public sealed class AttackAction : EnemyAction
{
    /// <summary>单次最多续接几段（防止 followUps 互指造成无限连）</summary>
    private const int MaxComboDepth = 3;

    private readonly AttackDef def;
    private readonly int comboDepth;

    private AnimancerState state;
    private float elapsed;
    private float duration;
    private float advanced;
    private bool[] resolved;

    /// <summary>判定帧在裁剪窗口内的本地进度（0~1）</summary>
    private float[] localHitFrames;

    /// <summary>后摇起点在裁剪窗口内的本地进度（0~1）</summary>
    private float localRecoveryStart = 0.7f;

    /// <summary>本招掷中的续接目标招式（null = 不连段）</summary>
    private AttackDef chainedDef;

    /// <summary>续接触发点（本地进度）</summary>
    private float chainPoint = 1f;

    public AttackAction(EnemyBrain brain, AttackDef def, int comboDepth = 0) : base(brain)
    {
        this.def = def;
        this.comboDepth = comboDepth;
    }

    public AttackDef Def => def;
    public override EnemyActionKind Kind => EnemyActionKind.Attack;
    public override bool IsFinished { get; protected set; }

    public override float NormalizedTime => duration > 0.0001f ? Mathf.Clamp01(elapsed / duration) : 1f;

    /// <summary>后摇起点（本地进度）：此前的出手不允许被位移/走位类意图抢占</summary>
    public float RecoveryStart => localRecoveryStart;

    /// <summary>本次攻击已触发的命中帧数量（测试断言用）</summary>
    public int HitCount { get; private set; }

    /// <summary>本次攻击是连段的第几段（0 = 决策层发起的起手招）</summary>
    public int ComboDepth => comboDepth;

    public override void Enter()
    {
        float clipLength = def != null && def.clip != null ? def.clip.length : 0.8f;
        // 阶段攻速：动画与判定帧一起加速（判定帧是归一化的，天然跟随）
        float speed = def != null ? Mathf.Max(0.05f, def.speed * brain.AttackSpeedMultiplier) : 1f;

        float start = def != null ? Mathf.Clamp01(def.startNormalized) : 0f;
        float end = def != null ? Mathf.Clamp01(def.endNormalized) : 1f;
        if (end - start < 0.1f) end = Mathf.Min(1f, start + 0.1f);
        float span = Mathf.Max(0.05f, end - start);

        duration = Mathf.Max(0.1f, clipLength * span / speed);
        elapsed = 0f;
        advanced = 0f;
        HitCount = 0;

        int hitCount = def != null && def.hitFrames != null ? def.hitFrames.Length : 0;
        localHitFrames = new float[hitCount];
        resolved = new bool[hitCount];
        for (int i = 0; i < hitCount; i++)
            localHitFrames[i] = Mathf.Clamp01((def.hitFrames[i] - start) / span);
        localRecoveryStart = Mathf.Clamp01(((def != null ? def.recoveryStart : 0.7f) - start) / span);

        if (def == null) return;

        brain.NotifyAttackStarted();
        brain.RegisterAttackUse(def);
        PickChainTarget();

        state = brain.PlayClip(def.clip, def.fadeIn);
        if (state != null)
        {
            state.Speed = speed;
            if (start > 0f) state.NormalizedTime = start;   // 跳过裁掉的起手段
            for (int i = 0; i < hitCount; i++)
            {
                float clipTime = def.hitFrames[i];
                if (clipTime < start || clipTime > end) continue;
                int index = i;
                state.Events.Add(Mathf.Clamp01(clipTime), () => OnHitFrame(index));
            }
        }

        // 起手预兆（可选）：给玩家"读招"的视觉线索
        if (!string.IsNullOrEmpty(def.telegraphVfxKey))
            VfxManager.Instance?.Play(def.telegraphVfxKey, brain.transform.position + Vector3.up * 1.2f);
    }

    /// <summary>进入时掷一次续接目标，进度到达时直接换招</summary>
    private void PickChainTarget()
    {
        chainedDef = null;
        chainPoint = 1f;

        if (def == null || def.followUps == null || def.followUps.Length == 0) return;
        if (comboDepth + 1 >= MaxComboDepth) return;

        // 先把候选按权重（chance 即权重）累加，再掷一次，避免"先命中谁看顺序"
        float total = 0f;
        for (int i = 0; i < def.followUps.Length; i++)
        {
            AttackDef candidate = Resolve(def.followUps[i]);
            if (candidate != null) total += def.followUps[i].chance;
        }
        if (total <= 0.0001f) return;

        float roll = Random.value * total;
        float acc = 0f;
        for (int i = 0; i < def.followUps.Length; i++)
        {
            AttackDef candidate = Resolve(def.followUps[i]);
            if (candidate == null) continue;

            acc += def.followUps[i].chance;
            if (roll <= acc)
            {
                chainedDef = candidate;
                chainPoint = Mathf.Clamp01(def.followUps[i].atNormalized);
                return;
            }
        }
    }

    private AttackDef Resolve(AttackDef.ComboLink link)
    {
        if (link == null || string.IsNullOrEmpty(link.attackId) || brain.Profile == null) return null;
        AttackDef candidate = brain.Profile.FindAttack(link.attackId);
        return candidate != null && candidate.clip != null ? candidate : null;
    }

    public override void Tick(float deltaTime)
    {
        if (def == null)
        {
            IsFinished = true;
            return;
        }

        elapsed += deltaTime;
        float norm = NormalizedTime;

        // 前段前冲：贴脸压上，避免"原地挥"
        if (def.advanceDistance > 0f && norm <= def.advanceEnd && advanced < def.advanceDistance)
        {
            float window = Mathf.Max(0.05f, duration * def.advanceEnd);
            float step = def.advanceDistance * (deltaTime / window);
            step = Mathf.Min(step, def.advanceDistance - advanced);
            if (step > 0f)
            {
                brain.MoveForward(step);
                advanced += step;
            }
        }

        // 前摇允许转向（对齐玩家）；判定与后摇锁朝向，玩家可以侧闪躲开
        float firstHit = localHitFrames != null && localHitFrames.Length > 0 ? localHitFrames[0] : 0.35f;
        if (norm < firstHit && brain.Profile != null)
            brain.FaceTargetSmooth(deltaTime, brain.Profile.turnSpeedDeg);

        // 没有动画片段时事件不会触发：用计时器兜底，保证仍能造成伤害
        if (state == null && resolved != null)
        {
            for (int i = 0; i < resolved.Length; i++)
            {
                if (!resolved[i] && norm >= localHitFrames[i]) OnHitFrame(i);
            }
        }

        // 连段：到点取消剩余后摇，把下一招交给控制器
        if (chainedDef != null && norm >= chainPoint)
        {
            Next = new AttackAction(brain, chainedDef, comboDepth + 1);
            IsFinished = true;
            if (brain.logActions)
                Debug.Log($"[AI] {brain.name} 连段 {def.id} → {chainedDef.id}（第 {comboDepth + 2} 段）", brain);
            return;
        }

        if (elapsed >= duration) IsFinished = true;
    }

    private void OnHitFrame(int index)
    {
        if (resolved == null || index < 0 || index >= resolved.Length || resolved[index]) return;
        resolved[index] = true;
        HitCount++;
        brain.ResolveAttackHit(def);
    }

    /// <summary>
    /// 是否处于"判定窗口"（弹刀判定用）：最后一个判定帧 ± padSeconds。
    /// 用秒数换算成归一化再比较，避免不同片段长度导致窗口大小变形。
    /// </summary>
    public bool IsInParryWindow(float padSeconds = 0.1f)
    {
        if (def == null || localHitFrames == null || localHitFrames.Length == 0) return false;

        float first = localHitFrames[0];
        float last = localHitFrames[localHitFrames.Length - 1];
        float pad = duration > 0.0001f ? padSeconds / duration : 0.05f;
        float n = NormalizedTime;

        return n >= first - pad && n <= last + pad;
    }

    /// <summary>
    /// 是否处于"判定帧"（霸体窗口）：只有真正挥出去那一下不吃硬直。
    /// 前摇 / 后摇都允许被打断——玩家打中正在出手的怪，怪要能看到受击反应。
    /// </summary>
    public bool IsInActiveWindow
    {
        get
        {
            if (def == null || localHitFrames == null || localHitFrames.Length == 0) return false;

            float first = localHitFrames[0];
            float last = localHitFrames[localHitFrames.Length - 1];
            float pad = duration > 0.0001f ? 0.05f / duration : 0.02f;
            float n = NormalizedTime;

            return n >= first - pad && n <= last + pad;
        }
    }

    /// <summary>
    /// 打断矩阵：
    ///  - 死亡永远可以打断；
    ///  - 破防硬直可以打断任何阶段；
    ///  - 受击（Hurt）可以打断——**是否真的切受击由决策层判定**：
    ///    轻击只有 lightHitInterruptChance 的概率会被打断，重击/暴击恒打断；
    ///  - 攻击之间互不打断（防止动作层被决策层高频抢占）；
    ///  - 位移/走位类只能在后摇抢占。
    /// </summary>
    public override bool CanBeInterruptedBy(EnemyAction next)
    {
        if (next == null) return false;
        if (next.Kind == EnemyActionKind.Dead) return true;
        if (next.Kind == EnemyActionKind.Stagger) return true;
        if (next.Kind == EnemyActionKind.Attack) return false;
        if (next.Kind == EnemyActionKind.Hurt) return true;
        return NormalizedTime >= localRecoveryStart;
    }

    public override void Exit()
    {
        if (state != null)
        {
            state.Events.Clear();
            state = null;
        }
    }
}
