using UnityEngine;

/// <summary>
/// 玩法全局配置（管线第 2 步"全局规则"）：一次攻击结算期间不变的规则值。
/// 由 <see cref="CombatRunRulesSource"/> 提供；场景里没有规则源时用 <see cref="Default"/>。
///
/// 默认值等于改造前的实际行为：关闭友军伤害、遮挡判定由招式单独开启。
/// </summary>
public struct CombatRunRules
{
    /// <summary>是否允许打到友军（默认关闭）</summary>
    public bool friendlyFire;

    /// <summary>是否允许做遮挡判定（招式自己声明要不要遮挡，这里是否决开关）</summary>
    public bool lineOfSightEnabled;

    /// <summary>遮挡判定使用的层（只打 environment，不被角色/道具挡住）</summary>
    public int environmentMask;

    /// <summary>默认规则</summary>
    public static CombatRunRules Default => new CombatRunRules
    {
        friendlyFire = false,
        lineOfSightEnabled = true,
        environmentMask = CombatLayerCache.EnvironmentMask,
    };
}

/// <summary>
/// 层掩码缓存：LayerMask.GetMask 要按名字解析，不能在每次打击帧里反复调。
/// 层表在运行期不变，缓存一次即可。
/// </summary>
public static class CombatLayerCache
{
    private static int _environmentMask;
    private static bool _environmentCached;

    /// <summary>environment 层掩码（未配置该层时返回 0 = 不做遮挡判定）</summary>
    public static int EnvironmentMask
    {
        get
        {
            if (!_environmentCached)
            {
                _environmentMask = LayerMask.GetMask("environment");
                _environmentCached = true;
            }
            return _environmentMask;
        }
    }
}
