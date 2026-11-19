using UnityEngine;

/// <summary>
/// 场景级全局战斗规则（管线第 2 步"玩法全局配置"）。
///
/// 场景里挂一个本组件即可覆盖默认规则；不挂则全部使用默认值。
/// 默认值等于改造前的实际行为：关闭友军伤害、遮挡判定由招式自己决定。
/// </summary>
[DisallowMultipleComponent]
public class CombatRunRulesSource : MonoBehaviour
{
    [SerializeField, Tooltip("是否允许打到友军（默认关闭）")]
    private bool _friendlyFire;

    [SerializeField, Tooltip("是否允许对招式启用遮挡判定（具体招式还要自己开启）")]
    private bool _lineOfSightEnabled = true;

    /// <summary>当前生效的规则</summary>
    public CombatRunRules Rules => new CombatRunRules
    {
        friendlyFire = _friendlyFire,
        lineOfSightEnabled = _lineOfSightEnabled,
        environmentMask = CombatLayerCache.EnvironmentMask,
    };

    // ============ 管线读取用的缓存（场景切换时组件重建，缓存随之失效） ============

    /// <summary>缓存的规则源；null + <see cref="absentCached"/> = 本场景确实没有规则源</summary>
    private static CombatRunRulesSource _instance;
    private static bool absentCached;

    /// <summary>取当前规则：有本组件用它的值，否则用默认值</summary>
    public static CombatRunRules Current
    {
        get
        {
            if (_instance != null) return _instance.Rules;
            if (absentCached) return CombatRunRules.Default;

            _instance = FindObjectOfType<CombatRunRulesSource>();
            if (_instance == null) absentCached = true;
            return _instance != null ? _instance.Rules : CombatRunRules.Default;
        }
    }

    private void OnDestroy()
    {
        if (_instance == this)
        {
            _instance = null;
            absentCached = false;   // 下次结算重新查找（场景里可能换了规则源）
        }
    }
}
