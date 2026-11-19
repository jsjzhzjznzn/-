#if UNITY_EDITOR
// 测试脚手架：只服务编辑器内的战斗测试（BattleTestBuilder），不随正式包编译
using UnityEngine;

/// <summary>
/// P4 测试木桩：可选自动攻击附近玩家（用于验证受击硬直/闪避无敌），
/// 并提供重置血量的公开方法，方便重复 E2E。
/// </summary>
public class BattleTestDummy : MonoBehaviour
{
    public Player target;
    public bool autoAttack;
    public float attackInterval = 1.5f;
    public float attackRange = 2.5f;
    public float attackDamage = 8f;

    private float _timer;
    private HealthModel _health;

    /// <summary>目标血量缓存（目标可能被替换，按 InstanceID 失效）</summary>
    private HealthModel _targetHealth;
    private int _targetHealthOwnerId;

    private void Awake()
    {
        _health = GetComponent<HealthModel>();
    }

    private void Update()
    {
        if (!autoAttack || target == null) return;

        _timer += Time.deltaTime;
        if (_timer < attackInterval) return;
        _timer = 0f;

        if (Vector3.Distance(transform.position, target.transform.position) > attackRange) return;

        var targetHealth = ResolveTargetHealth();
        if (targetHealth == null || !targetHealth.IsAlive) return;

        // 无判定几何的来源：极小半径整球 + 只打一个目标，走同一套命中管线
        var context = new AttackContext
        {
            attacker = gameObject,
            faction = CombatFaction.Enemy,
            origin = target.transform.position,
            forward = transform.forward,
            radius = 0.05f,
            arc = 360f,
            layerMask = ~0,
            delivery = AttackDelivery.Scripted,
            requireLineOfSight = false,
            maxTargets = 1,
            flatDamage = attackDamage,
            sourceId = 0,
        };

        CombatPipeline.Resolve(context, _combatBuffer);
    }

    /// <summary>命中管线用的缓冲（复用避免每次出手分配）</summary>
    private readonly Collider[] _combatBuffer = new Collider[8];

    private HealthModel ResolveTargetHealth()
    {
        int targetId = target.GetInstanceID();
        if (_targetHealth == null || _targetHealthOwnerId != targetId)
        {
            _targetHealth = target.GetComponent<HealthModel>();
            _targetHealthOwnerId = targetId;
        }
        return _targetHealth;
    }

    public void ResetDummy(float maxHP = 300f)
    {
        if (_health == null) _health = GetComponent<HealthModel>();
        if (_health != null) _health.ResetHealth(maxHP, maxHP);
    }
}
#endif
