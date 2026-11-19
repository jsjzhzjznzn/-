using UnityEngine;

/// <summary>
/// 大世界远景距离剔除（技术选型评估里的 P1）。
///
/// 依据 Docs/Documents/大世界技术选型评估(dashijie).md：
///   - 城市包围盒 3.9km，天际线远景与眼前建筑同精度提交，视锥外 500m 外的集群照样进管线；
///   - 超过 350m 的建筑不再投影（阴影距离只有 50m，无实际影响），切换处由雾效掩藏；
///   - 用 CullingGroup 分带，而不是每帧在托管侧遍历比较距离。
///
/// 做法：把城市实例下所有渲染器的世界包围球喂给 CullingGroup，按到参考点（玩家/相机）的距离分两带，
/// 远带直接把 renderer.enabled 关掉；分带判定在原生侧完成，托管侧只在状态变化时收到回调。
/// </summary>
public sealed class DashijieFarCull : MonoBehaviour
{
    [Header("剔除范围")]
    /// <summary>待剔除的根（城市实例）；留空则按下面的名字在本场景里找</summary>
    [SerializeField] private Transform cullRoot;

    /// <summary>留空时自动查找的城市实例名（与场景里的实例名一致）</summary>
    [SerializeField] private string cullRootName = "未命名 (1)";

    [Header("距离分带")]
    /// <summary>
    /// 距离参考点；留空则用主相机。
    /// 刻意优先用相机而不是 Player：切换世界角色时 Player 实例会被销毁重建，
    /// 而 CullingGroup 的距离参考点一旦指向被销毁的 Transform 就不再更新（分带会卡死）。
    /// </summary>
    [SerializeField] private Transform distanceReference;

    /// <summary>远带半径（米）：超出即关闭渲染。默认 350，与评估文档一致</summary>
    [SerializeField] private float farRadius = 350f;

    /// <summary>近带强制常显的最大个数（防止某个包围球异常巨大导致整块不剔）；0 = 不限制</summary>
    [SerializeField] private int neverCullBelowIndex = 0;

    private CullingGroup group;
    private Renderer[] renderers;

    /// <summary>
    /// 每个索引是否由本组件关掉：只恢复自己关的，避免把场景里原本就禁用的渲染器重新打开
    /// </summary>
    private bool[] culledByUs;

    /// <summary>当前被远带关掉的渲染器数量（排障/验收用）</summary>
    public int CulledCount { get; private set; }

    /// <summary>参与分带的渲染器总数（排障/验收用）</summary>
    public int TrackedCount => renderers != null ? renderers.Length : 0;

    public float FarRadius => farRadius;

    private void Awake()
    {
        if (cullRoot == null && !string.IsNullOrEmpty(cullRootName))
        {
            GameObject found = GameObject.Find(cullRootName);
            if (found != null) cullRoot = found.transform;
        }
        if (distanceReference == null)
        {
            Camera main = Camera.main;
            if (main != null) distanceReference = main.transform;
        }
        if (distanceReference == null)
        {
            Player player = FindObjectOfType<Player>();
            distanceReference = player != null ? player.transform : null;
        }
    }

    private void OnEnable()
    {
        Build();
    }

    private void OnDisable()
    {
        DisposeGroup();
    }

    private void OnDestroy()
    {
        DisposeGroup();
    }

    /// <summary>收集渲染器并建立分带（城市是静态的，包围球只算一次）</summary>
    public void Build()
    {
        DisposeGroup();

        if (cullRoot == null)
        {
            GameLog.Warn("[大世界剔除] 未找到城市实例，距离剔除未生效");
            return;
        }

        renderers = cullRoot.GetComponentsInChildren<Renderer>(includeInactive: true);
        if (renderers == null || renderers.Length == 0)
        {
            GameLog.Warn("[大世界剔除] 城市实例下没有渲染器");
            return;
        }
        culledByUs = new bool[renderers.Length];

        var spheres = new BoundingSphere[renderers.Length];
        for (int i = 0; i < renderers.Length; i++)
        {
            Bounds bounds = renderers[i].bounds;
            // 半径取 0：CullingGroup 用「参考点到包围球的最近点」算距离，
            // 而城市只有 240 个巨型网格（平均包围半径 113m，实测出生点最近点全部落在 350m 内），
            // 带上真实半径会导致一个都剔不掉。评估文档的口径就是按「包围盒中心距离」分带，故用中心点。
            spheres[i] = new BoundingSphere(bounds.center, 0f);
        }

        group = new CullingGroup();

        // CullingGroup 必须给一个 targetCamera 才会进入每帧求值（只设包围球与参考点不会触发回调），
        // 缺了它表现就是"挂上了、跟踪数正确、但永远剔除 0 个"。
        Camera evalCamera = Camera.main;
        if (evalCamera == null && distanceReference != null) evalCamera = distanceReference.GetComponentInParent<Camera>();
        if (evalCamera != null) group.targetCamera = evalCamera;
        else GameLog.Warn("[大世界剔除] 场景里没有主相机，CullingGroup 不会被求值");

        if (distanceReference != null) group.SetDistanceReferencePoint(distanceReference);
        else if (evalCamera != null) group.SetDistanceReferencePoint(evalCamera.transform);
        group.SetBoundingSpheres(spheres);
        group.SetBoundingDistances(new[] { Mathf.Max(1f, farRadius) });
        group.onStateChanged += OnStateChanged;

        GameLog.Info($"[大世界剔除] 已启用：跟踪 {renderers.Length} 个渲染器，远带 {farRadius:0}m，参考点={(distanceReference != null ? distanceReference.name : "<主相机>")}");
    }

    /// <summary>运行期更换距离参考点（参考点被销毁时调用，重建分带）</summary>
    public void SetDistanceReference(Transform reference)
    {
        distanceReference = reference;
        if (group == null) return;
        if (reference != null) group.SetDistanceReferencePoint(reference);
        else Build();
    }

    private void OnStateChanged(CullingGroupEvent sphere)
    {
        if (renderers == null) return;
        int index = sphere.index;
        if (index < 0 || index >= renderers.Length) return;
        if (neverCullBelowIndex > 0 && index < neverCullBelowIndex) return;

        Renderer target = renderers[index];
        if (target == null) return;

        // currentDistance = 0 表示落在第一带（远带半径之内）；>=1 表示已经超出
        bool visible = sphere.currentDistance == 0;
        if (visible)
        {
            // 只恢复本组件关掉的；原本就禁用的渲染器保持禁用
            if (!culledByUs[index]) return;
            culledByUs[index] = false;
            target.enabled = true;
            CulledCount--;
            return;
        }

        // 已经关着的（本组件关的，或场景里本来就禁用的）不再动它，也不计入剔除数
        if (!target.enabled || culledByUs[index]) return;
        culledByUs[index] = true;
        target.enabled = false;
        CulledCount++;
    }

    private void DisposeGroup()
    {
        if (group == null) return;
        group.onStateChanged -= OnStateChanged;
        group.Dispose();
        group = null;

        // 销毁分带时把本组件关掉的渲染器恢复，避免下次 Build 后残留不可见
        if (renderers != null && culledByUs != null)
        {
            for (int i = 0; i < renderers.Length && i < culledByUs.Length; i++)
            {
                if (!culledByUs[i]) continue;
                culledByUs[i] = false;
                if (renderers[i] != null) renderers[i].enabled = true;
            }
        }

        renderers = null;
        culledByUs = null;
    }
}