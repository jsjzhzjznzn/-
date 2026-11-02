using UnityEngine;

/// <summary>
/// 受击表现（挂在被击中的角色上，BOSS 用）：受击闪白 + 模型抖动 + 击退。
///
/// 为什么订阅 E_OnDamage 而不是由攻方直接调：
///   伤害可能来自玩家连招、BOSS 技能、DoT、后续系统，订阅事件能一次覆盖全部来源，
///   攻方代码不需要知道"受击方怎么表现"。
///
/// 闪白走**材质实例**的 `_HitFlash`（URPGenshinToon 新增的属性），
/// 不污染共享材质资产；曲线在 0.12s 内衰减到 0（前 25% 保持全白，保证受击瞬间看得见）。
/// 抖动只动视觉骨骼（默认 Bip001），不动根节点——根节点归 EnemyBrain 管，
/// 动了会跟 AI 位移/贴地打架，也会影响命中判定位置。
/// 击退复用 EnemyBrain.MoveAlong（带 environment 层碰撞），不会穿墙。
/// </summary>
[DisallowMultipleComponent]
public class HitReaction : MonoBehaviour
{
    private static readonly int HitFlashId = Shader.PropertyToID("_HitFlash");

    [Header("抖动")]
    [Tooltip("抖动用的子物体名（视觉骨骼根）；留空 = 用第一个子物体")]
    public string jitterRootName = "Bip001";

    [Header("闪白")]
    [Tooltip("勾掉可只保留抖动/击退（例如材质没有 _HitFlash 属性时）")]
    public bool enableFlash = true;

    private Renderer[] renderers;

    /// <summary>
    /// 每个 Renderer 的材质实例（Awake 时用 renderer.materials 克隆一份）。
    /// 为什么不用 MaterialPropertyBlock：本项目 URP 开了 SRP Batcher，`_HitFlash` 不在
    /// 材质资产已有的属性表里，MPB 写入对这个属性不生效（实测：MPB 读回 1，画面完全没变；
    /// 改材质实例则正常发白）。BOSS 只有 3 个 Renderer，克隆成本可忽略。
    /// </summary>
    private Material[][] instanceMaterials;

    private Transform jitterRoot;
    private Vector3 jitterBase;

    private EnemyBrain brain;
    private HitFeelProfile profile;

    private float flashUntil;
    private float flashDuration;
    private bool flashApplied;

    private float jitterUntil;
    private float jitterDuration;
    private float jitterAmplitude;
    private bool jitterApplied;

    private void Awake()
    {
        brain = GetComponent<EnemyBrain>();
        renderers = GetComponentsInChildren<Renderer>(true);
        instanceMaterials = new Material[renderers.Length][];
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            instanceMaterials[i] = renderer != null ? renderer.materials : new Material[0];
        }

        Transform root = null;
        if (!string.IsNullOrEmpty(jitterRootName)) root = transform.Find(jitterRootName);
        if (root == null && transform.childCount > 0) root = transform.GetChild(0);

        jitterRoot = root;
        if (jitterRoot != null) jitterBase = jitterRoot.localPosition;
    }

    private void OnEnable()
    {
        EventCenter.MainInstance.AddListener<DamageData>(E_EventType.E_OnDamage, this, OnDamaged);
    }

    private void OnDisable()
    {
        EventCenter.MainInstance.UnregisterTarget(this);
        RestoreImmediate();
    }

    private void OnDamaged(DamageData data)
    {
        if (data.target != gameObject) return;
        if (data.isDoT || data.amount <= 0f) return;

        profile = HitFeelService.Profile;

        float flashSeconds = profile != null ? profile.flashSeconds : 0.12f;
        float jitterSeconds = profile != null ? profile.jitterSeconds : 0.12f;
        float amplitude = profile != null ? profile.jitterAmplitude : 0.035f;

        if (enableFlash)
        {
            flashDuration = Mathf.Max(0.02f, flashSeconds);
            flashUntil = Time.time + flashDuration;
        }

        if (jitterRoot != null && amplitude > 0f)
        {
            jitterDuration = Mathf.Max(0.02f, jitterSeconds);
            jitterAmplitude = amplitude;
            jitterUntil = Time.time + jitterDuration;
        }

        ApplyKnockback(data);
    }

    /// <summary>击退：方向 = 从伤害来源指向自己；重击/暴击用更大的距离</summary>
    private void ApplyKnockback(DamageData data)
    {
        if (brain == null || brain.IsDead || profile == null) return;

        float heavyThreshold = profile.heavyDamageThreshold;
        bool isHeavy = data.isCritical || data.amount >= heavyThreshold;
        float distance = isHeavy ? profile.knockbackHeavy : profile.knockbackLight;
        if (distance <= 0f) return;

        Vector3 away = data.source != null
            ? transform.position - data.source.transform.position
            : -transform.forward;
        away.y = 0f;
        if (away.sqrMagnitude < 0.0001f) return;

        // 霸体（正在攻击）时给一部分位移：既保留"打到了"的推背感，又不至于把判定中的招式顶偏太多。
        float scale = brain.IsAttacking ? 0.55f : 1f;

        // 注意方向：away 已经是"远离攻击者"，要正向移动；
        // 写成 -away 会朝玩家怀里钻，视觉上等于没有击退（这就是之前的 bug）。
        brain.MoveAlong(away.normalized, distance * scale);
    }

    private void Update()
    {
        // 闪白：前 ~25% 保持全白，再线性衰减（受击瞬间就能看见，不会一帧就掉到半白）
        if (enableFlash)
        {
            if (Time.time < flashUntil)
            {
                float remaining = (flashUntil - Time.time) / Mathf.Max(0.0001f, flashDuration);
                float k = Mathf.Clamp01(remaining * 1.35f);
                SetFlash(k);
            }
            else if (flashApplied)
            {
                SetFlash(0f);
            }
        }

        // 抖动：扁平圆盘随机偏移 + 振幅衰减（樱井第 3/4 条：地面左右抖、幅度递减）
        if (jitterRoot != null)
        {
            if (Time.time < jitterUntil)
            {
                float k = Mathf.Clamp01((jitterUntil - Time.time) / Mathf.Max(0.0001f, jitterDuration));
                Vector2 offset = Random.insideUnitCircle * (jitterAmplitude * k);
                jitterRoot.localPosition = jitterBase + new Vector3(offset.x, offset.y, 0f);
                jitterApplied = true;
            }
            else if (jitterApplied)
            {
                jitterRoot.localPosition = jitterBase;
                jitterApplied = false;
            }
        }
    }

    private void SetFlash(float amount)
    {
        flashApplied = amount > 0.001f;
        float value = flashApplied ? amount : 0f;

        if (instanceMaterials == null) return;
        for (int i = 0; i < instanceMaterials.Length; i++)
        {
            Material[] materials = instanceMaterials[i];
            if (materials == null) continue;

            for (int j = 0; j < materials.Length; j++)
            {
                Material material = materials[j];
                if (material != null && material.HasProperty(HitFlashId)) material.SetFloat(HitFlashId, value);
            }
        }
    }

    private void RestoreImmediate()
    {
        SetFlash(0f);
        if (jitterRoot != null) jitterRoot.localPosition = jitterBase;
        flashApplied = false;
        jitterApplied = false;
    }
}
