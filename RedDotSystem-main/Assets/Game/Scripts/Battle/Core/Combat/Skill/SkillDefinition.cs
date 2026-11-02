using UnityEngine;

/// <summary>
/// 技能静态配置（ScriptableObject）。
///
/// 职责：只描述"这个技能是什么"——标识、动画/招式、冷却、充能、公共冷却。
/// 运行时状态（剩余充能、恢复时刻、分组剩余时间）放在 SkillCooldownRecord，由 SkillComponent 统一管理。
///
/// 动画、倍率、暴击、打击帧、命中 Buff、判定范围全部复用 <see cref="ComboData"/>：
/// 技能与轻击走同一条伤害管线，差异只在"什么时候开始冷却、能放几次、与谁共享公共冷却"。
///
/// 【命名】项目里已有 <c>SkillData</c>（EventCenter 的事件参数结构体），配置类不能同名。
/// </summary>
[CreateAssetMenu(fileName = "Skill", menuName = "Create/Skill/Skill")]
public class SkillDefinition : ScriptableObject
{
    [SerializeField, Header("技能标识（冷却记录/网络判定按此 id；为空回退资产名）")]
    private string _skillId;

    [SerializeField, Header("显示名称（UI/调试用）")]
    private string _displayName;

    [SerializeField, Header("技能图标（UI 用，可空）")]
    private Sprite _icon;

    [SerializeField, Header("招式配置（动画/倍率/暴击/打击帧/命中 Buff 复用 ComboData）")]
    private ComboData _combo;

    [SerializeField, Header("冷却秒数（<=0 时回落到 ComboData.comboColdTime）")]
    private float _cooldown = 3f;

    [SerializeField, Header("最大充能次数（1 = 普通冷却；>1 = 充能型）")]
    private int _maxCharges = 1;

    [SerializeField, Header("每恢复一层充能的间隔（<=0 时用冷却秒数）")]
    private float _chargeRecoverSeconds;

    [SerializeField, Header("公共冷却组标识（空 = 不参与公共冷却；同组技能应配相同 GCD）")]
    private string _cooldownGroupId;

    [SerializeField, Header("本技能触发的公共冷却秒数（<=0 = 不触发公共冷却）")]
    private float _gcdSeconds;

    #region 只读属性封装（外部只读，禁止修改配置数据）

    /// <summary>技能标识</summary>
    public string skillId => _skillId;

    /// <summary>
    /// 技能标识的预计算哈希（冷却记录键 / 网络传输用）。
    /// 空标识回退资产名，避免多个没填标识的技能哈希到同一个值被当成同一个技能。
    /// </summary>
    public int skillIdHash => Animator.StringToHash(string.IsNullOrEmpty(_skillId) ? name : _skillId);

    /// <summary>显示名称</summary>
    public string displayName => _displayName;

    /// <summary>技能图标（可空）</summary>
    public Sprite icon => _icon;

    /// <summary>招式配置（动画与伤害参数）</summary>
    public ComboData combo => _combo;

    /// <summary>最大充能次数（至少 1）</summary>
    public int maxCharges => Mathf.Max(1, _maxCharges);

    /// <summary>
    /// 冷却秒数：优先本配置；未配置时回落到招式的 comboColdTime
    /// （该字段目前没有其它调用点，正好作为技能的默认冷却来源）。
    /// </summary>
    public float cooldownSeconds =>
        _cooldown > 0f ? _cooldown : (_combo != null ? _combo.comboColdTime : 0f);

    /// <summary>
    /// 每恢复一层充能的间隔：普通冷却（1 次）就是冷却秒数；
    /// 充能型未单独配置时也用冷却秒数。
    /// </summary>
    public float chargeRecoverSeconds =>
        _maxCharges <= 1
            ? cooldownSeconds
            : (_chargeRecoverSeconds > 0f ? _chargeRecoverSeconds : cooldownSeconds);

    /// <summary>公共冷却组哈希（空标识 = 0，表示不参与公共冷却）</summary>
    public int cooldownGroupIdHash =>
        string.IsNullOrEmpty(_cooldownGroupId) ? 0 : Animator.StringToHash(_cooldownGroupId);

    /// <summary>本技能触发的公共冷却秒数（0 = 不触发）</summary>
    public float gcdSeconds => Mathf.Max(0f, _gcdSeconds);

    #endregion

#if UNITY_EDITOR
    /// <summary>编辑器校验：缺标识会回退资产名当键，缺招式则技能没有动画与伤害</summary>
    private void OnValidate()
    {
        if (string.IsNullOrEmpty(_skillId))
            Debug.LogWarning($"[SkillDefinition] \"{name}\" 未填 skillId，将临时用资产名当键；建议显式填写", this);
        if (_combo == null)
            Debug.LogWarning($"[SkillDefinition] \"{name}\" 未配招式（ComboData），释放时只有冷却没有动画与伤害", this);
    }
#endif
}
