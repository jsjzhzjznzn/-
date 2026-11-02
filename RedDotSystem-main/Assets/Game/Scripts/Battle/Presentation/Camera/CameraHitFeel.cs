using Cinemachine;

using UnityEngine;

/// <summary>
/// 相机震屏（打击感）——运行时自举单例，内部用 Cinemachine Impulse 发射端。
///
/// 为什么用 Impulse：`tiaozhan.scene` 的 `thirdcamera` 上本来就挂着
/// `CinemachineImpulseListener`（源工程留下的），只缺发射端；接上就能直接震，
/// 不用自己改相机位置（也不会和 Follow/LookAt 的阻尼打架）。
///
/// 同时它替代了场景里那个**脚本已丢失**的 `CameraShakeManager`
/// （guid e9b2e1a67c3727547baf4610f7848221 在工程里已经找不到）。
///
/// 力度约定（与 HitFeelProfile 一致）：轻击 ~0.18、重击 ~0.55、弹刀 ~0.85。
/// 振幅按"离相机越远越大"补偿（文献第 8 条：远景震感会被距离吃掉）。
/// </summary>
[DisallowMultipleComponent]
public class CameraHitFeel : MonoBehaviour
{
    public static CameraHitFeel Instance { get; private set; }

    [SerializeField, Tooltip("每米距离增加的力度比例（0.06 = 10m 外多 60%，上限 80%）")]
    private float distanceBoost = 0.06f;

    [SerializeField, Tooltip("力度 → 相机位移（米）的换算系数：0.08 表示 force=0.55 约偏移 4.4cm")]
    private float velocityScale = 0.08f;

    private CinemachineImpulseSource source;

    /// <summary>调试/E2E：累计震屏次数（命中一次 +1）</summary>
    public int ShakeCount { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance != null) return;
        GameObject holder = new GameObject("CameraHitFeel");
        holder.AddComponent<CameraHitFeel>();
        DontDestroyOnLoad(holder);
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        source = GetComponent<CinemachineImpulseSource>();
        if (source == null) source = gameObject.AddComponent<CinemachineImpulseSource>();

        // 运行时 AddComponent 不会走 Reset()，这里显式给一套可用默认值
        CinemachineImpulseDefinition definition = source.m_ImpulseDefinition;
        if (definition == null)
        {
            definition = new CinemachineImpulseDefinition();
            source.m_ImpulseDefinition = definition;
        }

        definition.m_ImpulseChannel = 1;
        definition.m_ImpulseDuration = 0.16f;
        definition.m_ImpulseShape = CinemachineImpulseDefinition.ImpulseShapes.Bump;

        // 注意不能选 Legacy：Legacy 依赖 m_RawSignal（默认空）+ TimeEnvelope（默认 0），
        // 运行时 AddComponent 出来的定义会直接 CreateEvent 返回 null —— 表现就是"调了没反应"。
        definition.m_ImpulseType = CinemachineImpulseDefinition.ImpulseTypes.Uniform;
        definition.m_DissipationDistance = 100f;
        source.m_DefaultVelocity = Vector3.down;

        // 顿帧期间也想让镜头抖起来：Impulse 用实时时间推进，不被 timeScale 拖慢
        CinemachineImpulseManager.Instance.IgnoreTimeScale = true;
    }

    /// <summary>静态入口（命中点、力度、攻击方向）</summary>
    public static void ShakeAt(Vector3 position, float force, Vector3 direction)
    {
        CameraHitFeel instance = Instance;
        if (instance == null || force <= 0.0001f) return;
        instance.DoShake(position, force, direction);
    }

    /// <summary>兼容旧调用名（CharacterCombo 里原来注释掉的就是 CameraShake(force)）</summary>
    public void CameraShake(float force) => DoShake(transform.position, force, Vector3.down);

    /// <summary>
    /// FOV 冲击：ZZZ 式"命中瞬间镜头收紧一下"。
    /// delta 是原始 FOV 偏移：**负值 = 视野变窄 = 推近**（Unity 里 FOV 越小越"近"）。
    /// unscaled 播放，顿帧期间也照常走。
    /// </summary>
    public static void PunchFov(float delta, float seconds)
    {
        CameraHitFeel instance = Instance;
        if (instance == null || Mathf.Abs(delta) < 0.01f || seconds <= 0f) return;
        instance.DoPunchFov(delta, seconds);
    }

    private int fovVersion;
    private CinemachineVirtualCamera fovVcam;
    private float fovBase;
    private bool fovBaseValid;

    private void DoPunchFov(float delta, float seconds)
    {
        CinemachineBrain brain = Camera.main != null ? Camera.main.GetComponent<CinemachineBrain>() : null;
        var vcam = brain != null ? brain.ActiveVirtualCamera as CinemachineVirtualCamera : null;
        if (vcam == null) return;

        // 基准 FOV 只在"没有冲击在播"时读取：连击高频触发时不会把上一次的收缩当成基准（否则会越打越近）
        if (!fovBaseValid || fovVcam != vcam)
        {
            fovVcam = vcam;
            fovBase = vcam.m_Lens.FieldOfView;
            fovBaseValid = true;
        }

        int version = ++fovVersion;
        StartCoroutine(PunchFovRoutine(vcam, delta, seconds, version));
    }

    private System.Collections.IEnumerator PunchFovRoutine(CinemachineVirtualCamera vcam, float delta, float seconds, int version)
    {
        float elapsed = 0f;
        const float AttackPortion = 0.3f;

        while (elapsed < seconds)
        {
            // 被更新的冲击接管：直接退出，别把旧 FOV 写回去
            if (version != fovVersion) yield break;

            elapsed += Time.unscaledDeltaTime;
            float k = Mathf.Clamp01(elapsed / seconds);
            float curve = k < AttackPortion
                ? Mathf.SmoothStep(0f, 1f, k / AttackPortion)
                : 1f - Mathf.SmoothStep(0f, 1f, (k - AttackPortion) / (1f - AttackPortion));

            vcam.m_Lens.FieldOfView = fovBase + delta * curve;
            yield return null;
        }

        if (version == fovVersion)
        {
            vcam.m_Lens.FieldOfView = fovBase;
            fovBaseValid = false;
        }
    }

    private void DoShake(Vector3 position, float force, Vector3 direction)
    {
        if (source == null) return;

        // 距离补偿：远景震动容易被忽略
        Camera cam = Camera.main;
        float distance = cam != null ? Vector3.Distance(cam.transform.position, position) : 0f;
        float distanceScale = 1f + Mathf.Clamp(distance * Mathf.Max(0f, distanceBoost), 0f, 0.8f);

        // 方向：以攻击方向为主 + 一点上抛 + 少量随机，避免每次抖动一模一样
        Vector3 dir = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.down;
        float magnitude = force * distanceScale * Mathf.Max(0f, velocityScale);
        Vector3 velocity = (dir * 0.75f + Vector3.up * 0.25f + Random.insideUnitSphere * 0.15f) * magnitude;

        source.GenerateImpulseAtPositionWithVelocity(position, velocity);
        ShakeCount++;
    }
}
