using UnityEngine;

/// <summary>
/// 打击感全局参数（唯一配置入口）。
///
/// 为什么单独做 SO：顿帧时长/震屏力度/吸附距离/闪白时长这些值会反复调，
/// 散落在各脚本常量里每次都要重编译；放资产里策划/美术也能直接改。
/// 运行时由 <see cref="HitFeelService"/> 用 Resources 兜底加载
/// （<c>Assets/Game/Resources/Battle/HitFeelProfile.asset</c>）。
/// </summary>
[CreateAssetMenu(menuName = "Battle/Hit Feel Profile", fileName = "HitFeelProfile")]
public class HitFeelProfile : ScriptableObject
{
    [Header("顿帧")]
    [Tooltip("≤ 这个时长走局部冻结（只暂停双方动画，世界照常走）；超过则走全局 timeScale")]
    public float localHitStopMax = 0.09f;

    [Tooltip("全局顿帧时的时间倍速（越小停得越死）")]
    [Range(0.01f, 0.5f)] public float globalHitStopScale = 0.05f;

    [Tooltip("BOSS 打中玩家时的顿帧时长（秒）")]
    public float enemyHitStopSeconds = 0.07f;

    [Tooltip("BOSS 打中玩家时的全局倍速")]
    [Range(0.01f, 0.5f)] public float enemyHitStopScale = 0.06f;

    [Header("震屏（Cinemachine Impulse 力度）")]
    [Tooltip("招式没配 shakeForce 时的轻击兜底")]
    public float lightHitShake = 0.18f;

    [Tooltip("招式没配 shakeForce 时的重击兜底")]
    public float heavyHitShake = 0.55f;

    [Tooltip("BOSS 打中玩家时的震屏")]
    public float enemyHitShake = 0.32f;

    [Header("命中特效")]
    public string lightHitVfxKey = "enemy_hit";
    public string heavyHitVfxKey = "enemy_hit";
    [Min(0f)] public float lightVfxScale = 0.75f;
    [Min(0f)] public float heavyVfxScale = 1.15f;

    [Header("命中音效（3D，在受击点播放）")]
    public AudioClip[] lightImpactClips;
    public AudioClip[] heavyImpactClips;
    [Range(0f, 1f)] public float lightImpactVolume = 0.7f;
    [Range(0f, 1f)] public float heavyImpactVolume = 0.95f;

    [Header("粘刀")]
    [Tooltip("打击帧发现目标只差一点点时，最多把攻方拉近这么多米")]
    [Min(0f)] public float stickMaxDistance = 0.35f;

    [Tooltip("超出这个夹角（度）不吸附，避免隔着侧后方拉人")]
    [Range(1f, 180f)] public float stickMaxAngle = 70f;

    [Header("受击反馈（BOSS）")]
    [Tooltip("受击闪白时长（秒）")]
    [Min(0.01f)] public float flashSeconds = 0.12f;

    [Tooltip("受击抖动幅度（米）")]
    [Min(0f)] public float jitterAmplitude = 0.035f;

    [Tooltip("受击抖动时长（秒）")]
    [Min(0.01f)] public float jitterSeconds = 0.12f;

    [Tooltip("普通命中击退距离（米），0 = 不击退")]
    [Min(0f)] public float knockbackLight = 0.06f;

    [Tooltip("重击/暴击击退距离（米）")]
    [Min(0f)] public float knockbackHeavy = 0.18f;

    /// <summary>重击判定门槛：伤害达到这个值算重击（与 EnemyProfile.heavyHitDamage 同口径）</summary>
    [Min(0f)] public float heavyDamageThreshold = 15f;

    [Header("命中停顿")]
    [Tooltip("玩家命中敌人时是否顿帧。关掉 = 打怪不暂停（怪一直挂在受击状态里连续挨打）")]
    public bool hitStopOnPlayerHit = false;

    [Header("弹刀（拼刀成功）")]
    [Tooltip("双方冻结时长（秒）")]
    public float deflectHitStopSeconds = 0.2f;

    [Tooltip("弹刀时的全局倍速（越小停得越死）")]
    [Range(0.01f, 0.5f)] public float deflectHitStopScale = 0.02f;

    public float deflectShake = 0.9f;

    [Tooltip("弹刀时相机 FOV 收紧（度；正数=推近）")]
    public float deflectFovKick = 3f;

    [Tooltip("弹刀把玩家推开的距离（米）")]
    public float deflectKnockbackPlayer = 0.25f;

    [Tooltip("弹刀把 BOSS 推开的距离（米）")]
    public float deflectKnockbackEnemy = 0.45f;

    [Tooltip("弹刀后 BOSS 的硬直时长（秒）")]
    public float deflectStaggerSeconds = 0.6f;

    [Tooltip("两次弹刀的最短间隔（秒），防止连弹")]
    public float deflectCooldown = 1.2f;

    [Tooltip("弹刀金属音（工程里现成的 IMPT-blocks-1~10）")]
    public AudioClip[] deflectClips;
    [Range(0f, 1f)] public float deflectVolume = 1f;

    [Header("极限闪避（无敌窗拦截伤害）")]
    public float dodgeSlowSeconds = 0.35f;
    [Range(0.01f, 0.9f)] public float dodgeSlowScale = 0.25f;
    public float dodgeFovKick = 4f;
    [Range(0f, 1f)] public float dodgeFlashAlpha = 0.22f;

    [Header("击杀")]
    public float killSlowSeconds = 0.5f;
    [Range(0.01f, 0.9f)] public float killSlowScale = 0.15f;
    public float killFovKick = 6f;
    [Range(0f, 1f)] public float killFlashAlpha = 0.3f;

    [Header("全屏闪光（UI 叠层，不依赖后处理）")]
    [Tooltip("普通命中不闪，只有弹刀/极限闪避/击杀用")]
    public Color deflectFlashColor = new Color(1f, 0.85f, 0.25f, 1f);
    public Color dodgeFlashColor = new Color(0.85f, 0.95f, 1f, 1f);
    public Color killFlashColor = new Color(1f, 0.95f, 0.8f, 1f);
    public float deflectFlashSeconds = 0.16f;
    public float dodgeFlashSeconds = 0.14f;
    public float killFlashSeconds = 0.22f;
}
