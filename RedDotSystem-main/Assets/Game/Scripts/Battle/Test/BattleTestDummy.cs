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

        var targetHealth = target.GetComponent<HealthModel>();
        if (targetHealth == null || !targetHealth.IsAlive) return;

        targetHealth.RequestDamage(new DamageRequest
        {
            damage = attackDamage,
            isCritical = false,
            sourceId = 0,
            isDoT = false,
        }, gameObject);
    }

    public void ResetDummy(float maxHP = 300f)
    {
        if (_health == null) _health = GetComponent<HealthModel>();
        if (_health != null) _health.ResetHealth(maxHP, maxHP);
    }
}
