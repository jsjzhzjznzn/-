/// <summary>
/// 一次技能释放请求的校验结果。
/// 只有 <see cref="Ok"/> 表示"通过校验、已提交、已开始冷却"；
/// 其余值都表示本次没有释放，也没有消耗充能或进入冷却。
/// </summary>
public enum SkillCastResult
{
    /// <summary>可以释放，且已提交</summary>
    Ok,

    /// <summary>没有配置这个技能（技能表查不到 / 传入了空配置 / 玩家没有技能槽）</summary>
    NotConfigured,

    /// <summary>已死亡</summary>
    Dead,

    /// <summary>被眩晕（禁止行动）</summary>
    Stunned,

    /// <summary>被沉默（禁止释放技能）</summary>
    Silenced,

    /// <summary>技能自身还在冷却 / 没有可用充能</summary>
    OnCooldown,

    /// <summary>公共冷却（GCD）未结束</summary>
    GroupCooldown,
}
