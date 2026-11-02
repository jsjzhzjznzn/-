using System.Collections.Generic;
using Animancer;
using UnityEngine;

/// <summary>
/// 连击战斗逻辑辅助类
/// 参考旧版 CharacterComboBase 重构，适配本项目 Animancer + ComboContainerData + PlayerComboReusableData 架构
///
/// 职责：选招（轻/重/闪避）→ 连击推进（换段动画）→ 动画事件回调（可输入/冷却/锁连击）
///       → 伤害触发入口（距离角度判定、帧暂停、QTE 检查）
///
/// 说明：音效池、伤害事件、敌人系统、相机震动在本项目尚未接入，相关位置以 TODO 占位，接入后补实现
/// </summary>
public class CharacterCombo
{
    private readonly Player player;
    private readonly PlayerComboReusableData reusableData;
    private readonly PlayerComboSOData comboData;

    /// <summary>运行时副本：避免修改共享的 ScriptableObject 导致多人数据污染</summary>
    private ComboContainerData runtimeLightCombo;

    public CharacterCombo(Player player, PlayerComboReusableData reusableData, PlayerComboSOData comboData)
    {
        this.player = player;
        this.reusableData = reusableData;
        this.comboData = comboData;

        reusableData.cameraTransform = player.CameraTransform;
    }

    // ==================== 初始化 ====================

    /// <summary>初始化所有连招容器（缓存首段用于闪避后还原），角色初始化时调用一次</summary>
    public void Init()
    {
        // 创建运行时副本，避免修改共享的 ScriptableObject
        if (comboData?.lightCombo != null)
        {
            runtimeLightCombo = comboData.lightCombo.CreateRuntimeClone();
        }
        // comboData?.heavyCombo?.Init();      // heavyCombo 未配置（PlayerComboSOData 中注释保留）
        // comboData?.executeCombo?.Init();    // executeCombo 未配置（PlayerComboSOData 中注释保留）
    }

    /// <summary>技能招式数据（技能状态用）</summary>
    public ComboData SkillCombo => comboData?.skillCombo;

    /// <summary>轻击连招容器（运行时副本，可安全修改）</summary>
    public ComboContainerData LightCombo => runtimeLightCombo;

    // ==================== 输入门控 ====================

    /// <summary>是否允许发动基础连招：处于可输入窗口且当前未在执行其他招式</summary>
    public virtual bool CanBaseComboInput()
    {
        if (!reusableData.canInput) return false;
        return true;
    }

    // ==================== 选招 / 进入连击 ====================

    /// <summary>轻攻击连招</summary>
    public virtual void LightComboInput()
    {
        if (runtimeLightCombo == null) return;

        // 换到轻击容器时重置连击信息
        if (reusableData.currentCombo != runtimeLightCombo || reusableData.currentCombo == null)
        {
            reusableData.currentCombo = runtimeLightCombo;
            ReSetComboInfo();
        }

        // 确保前进攻击后首段恢复正常普攻
        reusableData.currentCombo.ResetComboDates();
        player.SyncForwardATK(false);   // 同步给网络：远程端把轻击首段还原为普攻
        ExecuteBaseCombo();
    }

    /// <summary>重攻击连招（heavyCombo 未配置，PlayerComboSOData 中注释保留）</summary>
    /*public virtual void HeavyComboInput()
    {
        if (comboData?.heavyCombo == null) return;

        if (reusableData.currentCombo != comboData.heavyCombo || reusableData.currentCombo == null)
        {
            reusableData.currentCombo = comboData.heavyCombo;
            ReSetComboInfo();
        }

        ExecuteBaseCombo();
    }*/

    /// <summary>前进攻击：临时把首段替换为前进攻击段再执行</summary>
    public virtual void ForwardCombo()
    {
        if (runtimeLightCombo == null) return;

        if (reusableData.currentCombo != runtimeLightCombo || reusableData.currentCombo == null)
        {
            reusableData.currentCombo = runtimeLightCombo;
        }

        reusableData.currentCombo.SwitchForwardATK();
        player.SyncForwardATK(true);   // 同步给网络：远程端把轻击首段切换为前进攻击段
        ReSetComboInfo();
        ExecuteBaseCombo();
    }

    /// <summary>进入攻击流程：关闭输入窗口（等攻击段播完再开）</summary>
    protected virtual void ExecuteBaseCombo()
    {
        if (reusableData.currentCombo == null) return;

        reusableData.canInput = false;
    }

    // ==================== 重置连击信息 ====================

    /// <summary>重置连击信息：回到第一段、打开输入、允许连击、冷却就绪。
    /// 注意：不触碰输入缓冲——缓冲是带时间戳的输入事实，由消费方一次性取走，复位与输入解耦</summary>
    public virtual void ReSetComboInfo()
    {
        reusableData.currentIndex.Value = 0;
        reusableData.canInput = true;
        reusableData.canLink = true;
        reusableData.canMoveInterrupt = false;
        reusableData.canATK = true;

        // 新一套连招：清掉索敌锚点，下一刀重新选目标（连招内部则保持锁定）
        _autoAimTarget = null;
        _autoAimTargetHealth = null;
        _aimTweenActive = false;
    }

    // ==================== 动画事件回调（动画时间轴触发） ====================

    /// <summary>事件：禁止继续连击（例如被格挡/断连）</summary>
    public void DisConnectCombo()
    {
        reusableData.canLink = false;
    }

    /// <summary>事件：允许被移动打断</summary>
    public void CanMoveInterrupt()
    {
        reusableData.canMoveInterrupt = true;
    }

    /// <summary>事件：打开可输入窗口（预备/收招阶段）</summary>
    public void CanInput()
    {
        reusableData.canInput = true;
    }

    /// <summary>事件：冷却结束，允许发动下一段攻击</summary>
    public void CanATK()
    {
        reusableData.canATK = true;
    }

    /// <summary>事件：播放连击通用音效</summary>
    public void PlayComboFX()
    {
        var data = reusableData.currentCombo?.GetComboData(reusableData.currentIndex.Value);
        if (data == null) return;
        // 通用音效类型：查 SoundData 表播放（未配置则为 Null，PlayByStyle 内部会忽略）
        player.ActorAudio?.PlayByStyle(data.universalSound);
    }

    // ==================== 伤害触发（打击帧回调） ====================

    /// <summary>本段已结算过的受击单位（同一段/同一次判定内防重复扣血，按受击单位 GameObject 去重）</summary>
    private readonly HashSet<GameObject> _hitTargets = new();

    /// <summary>预分配的命中检测缓冲（OverlapSphereNonAlloc 用，避免每次打击帧 GC 分配；敌人多时调大）</summary>
    private readonly Collider[] _detectBuffer = new Collider[16];

    /// <summary>自动索敌命中缓冲（复用，避免 OverlapSphere 每次起手 GC）</summary>
    private readonly Collider[] _enemyBuffer = new Collider[16];

    /// <summary>敌人层掩码缓存（懒加载 LayerMask.GetMask("enemy")，只取一次）</summary>
    private int enemyLayerMask;
    private bool enemyLayerMaskCached;

    /// <summary>打击帧动画事件入口：根据当前招式类型走不同伤害逻辑</summary>
    public void ATK()
    {
        AttackTrigger();
    }

    protected virtual void AttackTrigger()
    {
        var combo = reusableData.currentCombo;
        if (combo == null) return;

        int index = reusableData.currentIndex.Value;
        var data = combo.GetComboData(index);
        if (data == null) return;

        AttackTrigger(data);
    }

    /// <summary>技能打击帧入口：与轻击共用同一套判定/结算/打击感，只换招式数据</summary>
    public void SkillATK(ComboData data)
    {
        if (data == null) return;
        AttackTrigger(data);
    }

    /// <summary>按给定招式结算一次打击帧（轻击/技能共用；命中去重按本次调用独立）</summary>
    private void AttackTrigger(ComboData data)
    {
        // 粘刀（P1）：打击帧前先补一小段吸附位移，差一点点能到的刀不再空挥
        TryStickToTarget(data);

        // 弹刀（P2）：BOSS 正处于判定窗口且这一刀能碰到 → 拼刀成功，本次不结算普通伤害
        if (TryDeflect(data)) return;

        // 命中判定：收集本段打击范围内的目标（结果写入 _detectBuffer，返回有效数量）
        int hitCount = AttackDetection(data);

        if (hitCount == 0) return;

        // 本次打击帧独立结算（同一目标多 Collider / 多次命中只扣一次血）
        _hitTargets.Clear();

        bool anyHit = false;
        // 打击感只取本次打击帧的第一个有效目标做一次（多目标叠加顿帧会让画面卡死）
        HealthModel firstHitHealth = null;
        Collider firstHitCollider = null;
        bool anyCritical = false;
        for (int i = 0; i < hitCount; i++)
        {
            var target = _detectBuffer[i].GetComponentInParent<HealthModel>();
            if (target == null) continue;                       // 没有受击组件（地形/装饰）跳过

            // 检查目标是否已死亡
            var targetPlayer = target.GetComponent<Player>();
            if (targetPlayer != null && targetPlayer.IsDead) continue;  // 死亡目标不接受伤害

            if (!_hitTargets.Add(target.gameObject)) continue;  // 同一受击单位只结算一次

            // 出手伤害在攻方这一刻算定（攻击力 = 基础+Buff 账本的最终值 × 倍率 × 增伤 × 暴击），
            // 之后攻方属性再变也不回溯影响本击（出手快照）。
            // 逐个目标独立算：每次结算独立掷暴击骰，与拆分前一致。
            var outgoing = DamageCalculator.CalculateOutgoing(
                player.gameObject, data.damageMultiplier, data.critRate, data.critMultiplier);

            // 受击方只做自己这一侧的防御/减伤结算
            target.RequestDamage(new DamageRequest
            {
                damage = outgoing.finalDamage,
                isCritical = outgoing.isCritical,
                sourceId = player.IsSpawned ? player.NetworkObjectId : 0UL,
                isDoT = false,
            }, player.gameObject);

            // 命中附带 Buff：本次伤害先结算完再挂状态（挂的增伤/减伤从下一击生效，不影响本次）
            // 走 RequestAddBuff：联网时转发到服务端挂（Buff 服务端权威），单机本地挂
            if (data.hitBuff != null)
            {
                var targetBuff = target.GetComponent<BuffComponent>();
                targetBuff?.RequestAddBuff(data.hitBuff, player.gameObject);
            }

            // 命中事件：吸血/叠层/击杀回怒类效果订阅（HitData 已定义，此前无人派发）
            EventCenter.MainInstance.Dispatch(E_EventType.E_Attack, new HitData
            {
                attacker = player.gameObject,
                target = target.gameObject,
                hitPoint = _detectBuffer[i].transform.position,
            });
            anyHit = true;

            if (firstHitHealth == null)
            {
                firstHitHealth = target;
                firstHitCollider = _detectBuffer[i];
            }
            if (outgoing.isCritical) anyCritical = true;
        }

        // 打击感：顿帧（局部/全局）+ 相机震屏 + 命中特效 + 命中音（统一入口）
        if (anyHit && firstHitHealth != null)
        {
            bool isHeavy = data.pauseFrameTime >= 0.1f || data.shakeForce >= 0.5f;

            HitFeelService.PlayerHitEnemy(player, firstHitHealth, firstHitCollider,
                data.pauseFrameTime, data.shakeForce, isHeavy, anyCritical);
        }
    }

    /// <summary>
    /// 粘刀（P1）：目标只差一点点在判定球外时，把攻方沿朝向拉近一小段（≤ stickMaxDistance）。
    /// 用 CharacterController.Move，自带碰撞，不会穿墙；只补"差一点点"的空挥，不做隔空拉人。
    /// </summary>
    private void TryStickToTarget(ComboData data)
    {
        if (player == null || data == null) return;

        HitFeelProfile feel = HitFeelService.Profile;
        float maxPull = feel != null ? feel.stickMaxDistance : 0.35f;
        if (maxPull <= 0f) return;

        var controller = player.GetComponent<CharacterController>();
        if (controller == null || !controller.enabled) return;

        // 与判定同口径的层：优先本段配置，未配置则 player+enemy
        int layerMask = data.enemyLayer.value != 0 ? data.enemyLayer : LayerMask.GetMask("player", "enemy");
        float reach = data.comboOffset + data.attackDistance;
        int count = Physics.OverlapSphereNonAlloc(
            player.transform.position, reach + maxPull, _enemyBuffer, layerMask);
        if (count <= 0) return;

        float maxAngle = feel != null ? feel.stickMaxAngle : 70f;
        Vector3 fwd = player.transform.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-6f) return;
        fwd.Normalize();

        Collider best = null;
        float bestGap = float.MaxValue;
        Vector3 bestDir = Vector3.zero;

        for (int i = 0; i < count; i++)
        {
            Collider col = _enemyBuffer[i];
            if (col == null) continue;
            if (col.transform.IsChildOf(player.transform)) continue;

            var health = col.GetComponentInParent<HealthModel>();
            if (health == null || !health.IsAlive) continue;

            Vector3 dir = col.transform.position - player.transform.position;
            dir.y = 0f;
            float dist = dir.magnitude;
            if (dist < 0.0001f) continue;
            dir /= dist;

            if (Vector3.Angle(fwd, dir) > maxAngle) continue;   // 侧后方不吸
            if (IsInputAwayFrom(dir)) continue;                  // 玩家明确想打别的方向，不吸

            float gap = dist - reach;
            if (gap <= 0f || gap > maxPull) continue;            // 已经能打到 / 差太远

            if (gap < bestGap)
            {
                bestGap = gap;
                best = col;
                bestDir = dir;
            }
        }

        if (best == null) return;
        controller.Move(bestDir * bestGap);
    }

    /// <summary>
    /// 弹刀（P2，ZZZ 式拼刀）：我方打击帧落在一个"正在攻击且处于判定窗口"的敌人身上。
    /// 成功时由 HitFeelService 负责顿帧/闪光/音效/双方后退/BOSS 硬直，本次不结算普通伤害。
    /// </summary>
    private bool TryDeflect(ComboData data)
    {
        if (player == null || data == null) return false;

        HitFeelProfile feel = HitFeelService.Profile;
        float cooldown = feel != null ? feel.deflectCooldown : 1.2f;
        if (Time.time < _nextDeflectTime) return false;

        int layerMask = data.enemyLayer.value != 0 ? data.enemyLayer : LayerMask.GetMask("enemy");
        float reach = data.comboOffset + data.attackDistance + 0.8f;
        int count = Physics.OverlapSphereNonAlloc(player.transform.position, reach, _enemyBuffer, layerMask);
        if (count <= 0) return false;

        Vector3 fwd = player.transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-6f) return false;
        fwd.Normalize();

        EnemyBrain best = null;
        Collider bestCollider = null;
        float bestDistance = float.MaxValue;

        for (int i = 0; i < count; i++)
        {
            Collider col = _enemyBuffer[i];
            if (col == null) continue;
            if (col.transform.IsChildOf(player.transform)) continue;

            var brain = col.GetComponentInParent<EnemyBrain>();
            if (brain == null || brain.IsDead || !brain.IsAttacking) continue;
            if (!brain.CurrentAttack.IsInParryWindow(0.12f)) continue;

            Vector3 dir = col.transform.position - player.transform.position;
            dir.y = 0f;
            float dist = dir.magnitude;
            if (dist < 0.0001f) continue;
            if (Vector3.Angle(fwd, dir) > 75f) continue;

            if (dist < bestDistance)
            {
                bestDistance = dist;
                best = brain;
                bestCollider = col;
            }
        }

        if (best == null) return false;

        _nextDeflectTime = Time.time + cooldown;

        Vector3 hitPoint = bestCollider != null
            ? bestCollider.ClosestPoint(player.transform.position + Vector3.up * 1.0f)
            : best.transform.position + Vector3.up * 1.0f;

        HitFeelService.PlayerDeflect(player, best, hitPoint);
        return true;
    }

    /// <summary>
    /// 攻击判定：以角色前方偏移为球心做 OverlapSphere 收集命中目标。
    /// 命中点 = 角色位置 + 面朝方向 * 本段前移量(comboOffset)，半径 = 本段判定距离(attackDistance)，
    /// 按本段敌人层(ComboData.enemyLayer)过滤（未配置回退全层），并排除玩家自身及子物体（武器碰撞等）。
    /// 结果写入 _detectBuffer（原地压缩掉无效项），返回有效命中数量。
    /// </summary>
    protected virtual int AttackDetection(ComboData data)
    {
        if (player == null || player.IsDead) return 0;  // 死亡后不检测攻击

        var playerTransform = player.transform;
        Vector3 origin = playerTransform.position + playerTransform.forward * data.comboOffset;

        // 记录判定信息（供外部/调试读取）
        reusableData.detectionOrigin = origin;
        reusableData.detectionDir = playerTransform.forward;

        // 敌人层：本段 ComboData 里配置的 LayerMask；未配置(0)回退 player+enemy 两层
        // (阵营统一走物理层:玩家角色在 player 层,敌方 AI 在 enemy 层,都纳入可被打的判定范围)
        int layerMask = data.enemyLayer.value != 0 ? data.enemyLayer : LayerMask.GetMask("player", "enemy");

        int count = Physics.OverlapSphereNonAlloc(origin, data.attackDistance, _detectBuffer, layerMask);

        // 原地压缩：跳过玩家自身及子物体（全层过滤时可能把玩家自己打中）
        int valid = 0;
        for (int i = 0; i < count; i++)
        {
            if (_detectBuffer[i] == null) continue;
            if (_detectBuffer[i].transform.IsChildOf(playerTransform)) continue;
            if (valid != i) _detectBuffer[valid] = _detectBuffer[i];
            valid++;
        }
        return valid;
    }

    // ==================== 辅助 ====================

    // ==================== 自动索敌（Phase 1：加权评分 + 目标粘性 + 平滑转向） ====================

    /// <summary>当前锁定的敌人（连招期间保持；死亡/超距/无视线才重选）</summary>
    private Transform _autoAimTarget;

    /// <summary>锁定目标的血量缓存：Tick 里判死，转向不追尸体</summary>
    private HealthModel _autoAimTargetHealth;

    /// <summary>是否正在索敌转向（连招全程：目标存活就一直跟）</summary>
    private bool _aimTweenActive;

    /// <summary>下一次允许弹刀的时间（防连弹）</summary>
    private float _nextDeflectTime;

    /// <summary>调试/E2E：当前索敌目标与是否正在转向</summary>
    public Transform AutoAimTarget => _autoAimTarget;
    public bool IsAutoAiming => _aimTweenActive;

    /// <summary>环境层掩码：视线遮挡只打 environment，别被玩家/掉落物挡住</summary>
    private int environmentMask;
    private bool environmentMaskCached;

    /// <summary>
    /// 攻击起手时选定目标并启动平滑转向（软锁定，仅单机）。
    /// 评分 = 距离权重 + 角度权重 + 附加分（正在攻击我 / 是上一段目标），权重全部在 PlayerComboSOData 可调，
    /// 取最高分者；不再单纯"最近"，背后的怪和挂 Dot 摸过来的怪不会再抢走朝向。
    /// 联网/远程端不生效：朝向由 NetworkTransform 同步，本地硬转会被覆盖并抖动。
    /// </summary>
    public void UpdateAttackLookAtEnemy()
    {
        if (player == null || player.IsSpawned) return;              // 单机限定
        if (comboData == null || !comboData.autoAimEnabled) return;
        if (comboData.autoAimRadius <= 0f) return;

        // 1) 连招内优先保持原目标：不每段重选（目标死亡/超距/无视线才换）
        if (TryKeepCurrentTarget())
        {
            _aimTweenActive = true;
            return;
        }

        int enemyMask = GetEnemyLayerMask();
        if (enemyMask == 0)
        {
            _aimTweenActive = false;
            return;
        }

        var playerTransform = player.transform;
        int count = Physics.OverlapSphereNonAlloc(
            playerTransform.position, comboData.autoAimRadius, _enemyBuffer, enemyMask);
        if (count == _enemyBuffer.Length)
            Debug.LogWarning($"[Combo] {player.name} 自动索敌命中数达 buffer 上限({count}),可能漏检,建议调大 _enemyBuffer", player);

        Transform best = null;
        HealthModel bestHealth = null;
        float bestScore = float.MinValue;
        float maxAngle = Mathf.Max(1f, comboData.autoAimMaxAngle);

        // 水平化 forward：角色在斜坡/俯仰时 3D 夹角与去 y 的 dir 不同平面会偏大
        Vector3 fwd = playerTransform.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward; else fwd.Normalize();

        for (int i = 0; i < count; i++)
        {
            var col = _enemyBuffer[i];
            if (col == null) continue;

            Transform t = col.transform;
            if (t.IsChildOf(playerTransform)) continue;              // 排除自身及子物体

            var health = col.GetComponentInParent<HealthModel>();
            if (health == null || !health.IsAlive) continue;         // 跳过死亡/非战斗单位

            Vector3 dir = t.position - playerTransform.position;
            dir.y = 0f;
            float dist = dir.magnitude;
            if (dist < 0.0001f) continue;
            dir /= dist;

            float angle = Vector3.Angle(fwd, dir);
            if (angle > maxAngle) continue;                          // 前方夹角过滤

            // 玩家有方向输入、且目标是反方向 → 尊重玩家，不吸这个目标
            if (IsInputAwayFrom(dir)) continue;

            // 隔墙不锁：视线只打 environment 层（判定是球形，薄墙可能真打过去）
            if (!HasLineOfSight(t)) continue;

            // 加权评分：近 + 正面 都好才赢，单项极端不再通吃（权重在 comboData 里可调）
            float score = (1f - dist / comboData.autoAimRadius) * comboData.autoAimDistanceWeight
                        + (1f - angle / maxAngle) * comboData.autoAimAngleWeight;

            // 正在攻击我的敌人优先（EnemyBrain 就是现成的"仇恨意向"）
            var brain = col.GetComponentInParent<EnemyBrain>();
            if (brain != null && brain.IsAttacking && brain.Target == playerTransform)
                score += comboData.autoAimAttackingMeBonus;

            // 目标粘性：上一段的目标加分保留，除非别人明显更好
            if (t == _autoAimTarget)
                score += comboData.autoAimStickyBonus;

            if (score > bestScore)
            {
                bestScore = score;
                best = t;
                bestHealth = health;
            }
        }

        _autoAimTarget = best;
        _autoAimTargetHealth = bestHealth;
        _aimTweenActive = best != null;
    }

    /// <summary>
    /// 当前目标还值得继续追吗：存活 / 在搜索半径(+2m 容差)内 / 有视线。
    /// 刻意**不检查夹角、也不看玩家输入**：一旦锁定，连招全程都跟着它转
    /// （否则怪绕到侧后方就会停止转向，连招就不面向怪了）。
    /// </summary>
    private bool TryKeepCurrentTarget()
    {
        if (_autoAimTarget == null || _autoAimTargetHealth == null || !_autoAimTargetHealth.IsAlive) return false;

        Vector3 dir = _autoAimTarget.position - player.transform.position;
        dir.y = 0f;
        float dist = dir.magnitude;
        if (dist < 0.0001f) return false;

        // 允许目标被击退/后撤拉开一点，超出就放弃
        if (dist > comboData.autoAimRadius + 2f) return false;

        if (!HasLineOfSight(_autoAimTarget)) return false;

        return true;
    }

    /// <summary>
    /// 索敌转向（攻击状态每帧调用）：锁定后**整段连招持续跟随**，角速度有上限。
    /// 不再在命中帧前停手、也不在命中后锁死——玩家要的是"连招一直面向怪"。
    /// </summary>
    public void TickAutoAimRotation(float deltaTime)
    {
        if (!_aimTweenActive) return;

        var target = _autoAimTarget;
        var playerTransform = player != null ? player.transform : null;
        if (target == null || playerTransform == null
            || _autoAimTargetHealth == null || !_autoAimTargetHealth.IsAlive)
        {
            _aimTweenActive = false;
            _autoAimTarget = null;
            _autoAimTargetHealth = null;
            return;
        }

        Vector3 lookDir = target.position - playerTransform.position;
        lookDir.y = 0f;
        if (lookDir.sqrMagnitude < 0.0001f) return;

        Quaternion want = Quaternion.LookRotation(lookDir);
        float turnSpeed = comboData != null ? comboData.autoAimTurnSpeed : 720f;

        playerTransform.rotation = turnSpeed > 0f
            ? Quaternion.RotateTowards(playerTransform.rotation, want, turnSpeed * deltaTime)
            : want;
    }


    /// <summary>视线：眼到目标胸口之间没有被 environment 层挡住</summary>
    private bool HasLineOfSight(Transform target)
    {
        if (target == null || player == null) return false;

        if (!environmentMaskCached)
        {
            environmentMask = LayerMask.GetMask("environment");
            environmentMaskCached = true;
        }
        if (environmentMask == 0) return true;

        Vector3 eye = player.transform.position + Vector3.up * 1.4f;
        Vector3 chest = target.position + Vector3.up * 1.0f;
        return !Physics.Linecast(eye, chest, environmentMask, QueryTriggerInteraction.Ignore);
    }

    /// <summary>玩家当前方向输入是否指向目标的反侧（是 → 尊重输入，不吸附）</summary>
    private bool IsInputAwayFrom(Vector3 dirToTarget)
    {
        if (comboData == null) return false;
        if (CharacterInputSystem.MainInstance == null) return false;

        Vector2 move = CharacterInputSystem.MainInstance.Movement;
        if (move.sqrMagnitude < 0.0625f) return false;      // 死区 0.25

        Camera cam = Camera.main;
        if (cam == null) return false;

        Vector3 fwd = cam.transform.forward; fwd.y = 0f;
        Vector3 right = cam.transform.right; right.y = 0f;
        if (fwd.sqrMagnitude < 1e-6f) return false;
        fwd.Normalize();
        right.Normalize();

        Vector3 wish = fwd * move.y + right * move.x;
        if (wish.sqrMagnitude < 1e-6f) return false;
        wish.Normalize();

        return Vector3.Angle(wish, dirToTarget) > Mathf.Max(1f, comboData.autoAimInputRespectAngle);
    }

    /// <summary>敌人层掩码（LayerMask.GetMask("enemy")）；懒加载缓存避免每次起手重复取名</summary>
    private int GetEnemyLayerMask()
    {
        if (!enemyLayerMaskCached)
        {
            enemyLayerMask = LayerMask.GetMask("enemy");
            enemyLayerMaskCached = true;
        }
        return enemyLayerMask;
    }

    /// <summary>可被打断且玩家在移动时，打断攻击（切回移动状态）</summary>
    public void CheckMoveInterrupt()
    {
        if (!reusableData.canMoveInterrupt) return;
        if (!player.IsMoving) return;

        // TODO: 通知状态机切回移动状态（如 idlingState / walkingState）
        reusableData.canMoveInterrupt = false;
    }

    /// <summary>不满足连击条件或角色在冲刺时，重置连击回到第一段</summary>
    public void CheckCanLinkCombo()
    {
        if (!reusableData.canLink || player.IsSprintHeld)
        {
            ReSetComboInfo();
        }
    }

    // ==================== 音效（接入 ActorAudioComponent） ====================

    /// <summary>播放角色语音（攻击段开始时调用）</summary>
    /// <returns>实际播出的片段下标；没播返回 -1（供网络广播）</returns>
    public int PlayCharacterVoice(ComboData data)
    {
        if (data == null) return -1;
        return player.ActorAudio != null ? player.ActorAudio.PlayComboVoice(data) : -1;
    }

    /// <summary>播放武器挥砍音效（攻击段开始时调用）</summary>
    /// <returns>实际播出的片段下标；没播返回 -1（供网络广播）</returns>
    public int PlayWeaponSound(ComboData data)
    {
        if (data == null) return -1;
        return player.ActorAudio != null ? player.ActorAudio.PlayWeaponSound(data) : -1;
    }

    // ==================== 特效（接入目标工程 VfxManager） ====================

    /// <summary>武器骨骼缓存（首次使用时按名字查找，找不到退回右手）</summary>
    private Transform _weaponBone;

    /// <summary>
    /// 播放攻击段挥砍特效（刀光）。挂到角色武器骨骼下，随武器挥动移动。
    /// 攻击段开始时由 PlayerComboState.PlayAttackClip 调用。
    /// 源工程走 VFX_PoolManager + 角色特效表；这里改接目标工程统一的 VfxManager（Resources/VfxCatalog）。
    /// </summary>
    public void PlayAttackVFX(ComboData data)
    {
        if (data == null || player == null) return;

        var bone = ResolveWeaponBone();
        if (bone == null) return;   // 模型换装/骨骼缺失时不刷警告，静默跳过

        VfxManager.Instance?.PlayAttached(VfxKeys.PlayerAttackSlash, bone);
    }

    /// <summary>武器骨骼：安比 → 艾莲 → 右手，按骨骼名兜底查找</summary>
    private Transform ResolveWeaponBone()
    {
        if (_weaponBone != null) return _weaponBone;

        var all = player.GetComponentsInChildren<Transform>(true);
        string[] preferred =
        {
            "Anbi_Weapon_Bone01",   // 安比武器骨骼（挂刀光用）
            "Anbi_Weapon_02",
            "Ellen_Weapon",
            "Bip001 R Hand",
        };
        foreach (var name in preferred)
        {
            foreach (var t in all)
            {
                if (t.name == name)
                {
                    _weaponBone = t;
                    return _weaponBone;
                }
            }
        }
        return null;
    }
}
