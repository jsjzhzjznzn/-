using System.Threading.Tasks;
using Common.Network;
using UnityEngine;

/// <summary>
/// 场景存档控制器基类（每个需要存档的场景挂一个子类）
///
/// 生命周期：
///   Start → 拉取存档（2 秒超时）→ 应用位置/相机/对话 → 之后才允许上报
///   事件点（对话节点变更、打开设置、返回主页、退出、失焦、场景卸载）→ 上报最新快照
///
/// 子类只负责三件事：玩家根节点、相机的读/写、对话进度的读/恢复。
/// 位置只在事件点写库（用户定案），不做定时写库。
/// </summary>
public abstract class SceneSaveController : MonoBehaviour
{
    [Header("存档")]
    [Tooltip("场景名（服务端白名单：dashijie；留空由子类填默认值）")]
    [SerializeField] protected string sceneName = string.Empty;

    [Tooltip("玩家根节点（留空则运行时由子类自动查找）")]
    [SerializeField] protected Transform playerRoot;

    [Tooltip("拉取存档的超时（秒）；超时按首次进入处理，不阻塞游戏")]
    [SerializeField] protected float restoreTimeoutSeconds = 2f;

    [Tooltip("恢复时打印详细日志（排障用）")]
    [SerializeField] protected bool verboseLog = true;

    /// <summary>存档是否已应用完成（完成前不允许上报，避免把默认位姿写回服务端）</summary>
    protected bool Restored { get; private set; }

    /// <summary>是否正在进行恢复（恢复期间子类产生的节点变更不应触发上报）</summary>
    protected bool Restoring { get; private set; }

    /// <summary>最近一次应用的存档（子类排障/测试用）</summary>
    public SceneSaveSnapshot AppliedSnapshot { get; private set; }

    /// <summary>是否已经完成恢复、可以正常上报（测试/UI 判断用）</summary>
    public bool IsReady => Restored && !Restoring;

    /// <summary>采集当前快照（测试/排障用；不上报、不改状态）</summary>
    public SceneSaveSnapshot CaptureSnapshot()
    {
        if (playerRoot == null) playerRoot = ResolvePlayerRoot();
        return playerRoot != null ? BuildSnapshot() : null;
    }

    // ──────────────────────────────
    // 生命周期
    // ──────────────────────────────

    protected virtual void Start()
    {
        SceneSaveService.EnsureExists();

        if (playerRoot == null) playerRoot = ResolvePlayerRoot();

        PauseManager.PausedChanged += OnPausedChanged;

        _ = RestoreAsync();
    }

    protected virtual void OnDisable()
    {
        // 场景卸载（返回主页 / 切其他场景）也是事件点：尽量把最后状态发出去
        SaveNow();
    }

    protected virtual void OnDestroy()
    {
        PauseManager.PausedChanged -= OnPausedChanged;
    }

    private void OnApplicationQuit()
    {
        SaveNow();
    }

    private void OnApplicationPause(bool paused)
    {
        if (paused) SaveNow();
    }

    private void OnPausedChanged(bool paused)
    {
        // 打开设置 = 玩家此刻位置/对话状态的语义锚点，强制存一次
        if (paused) SaveNow();
    }

    // ──────────────────────────────
    // 恢复
    // ──────────────────────────────

    protected async Task RestoreAsync()
    {
        Restoring = true;

        SceneSaveService service = SceneSaveService.Instance;
        if (service == null) return;   // 单例没起来（理论上不会），走默认

        SceneSaveSnapshot snapshot = await service.PullAsync(sceneName, restoreTimeoutSeconds);
        if (!isActiveAndEnabled) return;   // 拉取期间场景已被卸载

        if (playerRoot == null) playerRoot = ResolvePlayerRoot();

        if (snapshot != null && playerRoot != null)
        {
            ApplySnapshot(snapshot);
            AppliedSnapshot = snapshot;
            if (verboseLog)
            {
                Debug.Log($"[SceneSave] 已恢复场景存档：{sceneName} appliedPos={playerRoot.position} " +
                          $"dialogue={snapshot.DialogueActive}/{snapshot.DialogueGraph}#{snapshot.DialogueNodeId}");
            }
        }
        else if (verboseLog)
        {
            Debug.Log($"[SceneSave] 无可用存档，按场景默认开局：{sceneName}");
        }

        Restoring = false;
        Restored = true;
    }

    /// <summary>
    /// 应用完整快照（位置 → 相机 → 对话；子类可覆盖追加逻辑）
    /// </summary>
    protected virtual void ApplySnapshot(SceneSaveSnapshot snapshot)
    {
        Vector3 position = new(snapshot.PosX, snapshot.PosY, snapshot.PosZ);
        Quaternion rotation = Quaternion.Euler(0f, snapshot.RotY, 0f);
        ApplyPosition(position, rotation);

        ApplyCamera(snapshot.CamYaw, snapshot.CamPitch, snapshot.CamDistance);

        AfterApplyPosition(position);
        RestoreDialogue(snapshot);
    }

    // ──────────────────────────────
    // 上报
    // ──────────────────────────────

    /// <summary>
    /// 立即采集并上报当前快照（事件点调用；恢复完成前为空操作）
    /// </summary>
    public void SaveNow()
    {
        if (!Restored || Restoring) return;

        SceneSaveService service = SceneSaveService.Instance;
        if (service == null) return;

        if (playerRoot == null) playerRoot = ResolvePlayerRoot();
        if (playerRoot == null) return;

        service.QueuePush(BuildSnapshot());
    }

    private SceneSaveSnapshot BuildSnapshot()
    {
        Vector3 position = playerRoot.position;
        float rotY = playerRoot.eulerAngles.y;
        if (rotY > 180f) rotY -= 360f;

        SceneSaveSnapshot snapshot = new()
        {
            SceneName = sceneName,
            PosX = position.x,
            PosY = position.y,
            PosZ = position.z,
            RotY = rotY,
        };

        CaptureCamera(out float camYaw, out float camPitch, out float camDistance);
        snapshot.CamYaw = camYaw;
        snapshot.CamPitch = camPitch;
        snapshot.CamDistance = camDistance;

        CaptureDialogue(out bool dialogueActive, out string dialogueGraph, out int dialogueNodeId);
        snapshot.DialogueActive = dialogueActive;
        snapshot.DialogueGraph = dialogueGraph ?? string.Empty;
        snapshot.DialogueNodeId = dialogueNodeId;
        snapshot.DialogueVarsJson = CaptureVariablesJson() ?? string.Empty;
        snapshot.DialogueFiredJson = CaptureFiredEventsJson() ?? string.Empty;

        return snapshot;
    }

    // ──────────────────────────────
    // 子类扩展点
    // ──────────────────────────────

    /// <summary>查找玩家根节点（场景首次加载时调用）</summary>
    protected abstract Transform ResolvePlayerRoot();

    /// <summary>应用位置/朝向（KCC 要走 Motor，CharacterController 要先禁用再启用）</summary>
    protected abstract void ApplyPosition(Vector3 position, Quaternion rotation);

    /// <summary>读取相机状态（yaw/pitch 语义由场景定义；distance 无缩放概念时填 0）</summary>
    protected abstract void CaptureCamera(out float yaw, out float pitch, out float distance);

    /// <summary>应用相机状态</summary>
    protected abstract void ApplyCamera(float yaw, float pitch, float distance);

    /// <summary>位置应用后的补充处理（例如相机目标 warp，避免恢复后相机滑移）</summary>
    protected virtual void AfterApplyPosition(Vector3 position)
    {
    }

    /// <summary>读取对话进度（无对话的场景返回 active=false / 空图 / 0）</summary>
    protected virtual void CaptureDialogue(out bool active, out string graphName, out int nodeId)
    {
        active = false;
        graphName = string.Empty;
        nodeId = 0;
    }

    /// <summary>恢复对话进度（无对话的场景不实现）</summary>
    protected virtual void RestoreDialogue(SceneSaveSnapshot snapshot)
    {
    }

    /// <summary>对话变量快照 JSON（无对话返回 null）</summary>
    protected virtual string CaptureVariablesJson()
    {
        return null;
    }

    /// <summary>已发布过事件的选项 Key JSON 数组（无对话返回 null）</summary>
    protected virtual string CaptureFiredEventsJson()
    {
        return null;
    }
}
