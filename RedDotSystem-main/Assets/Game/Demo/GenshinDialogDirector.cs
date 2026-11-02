using System.Collections;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Miemie.DialogSystem;
using SkierFramework;
using UnityEngine;

/// <summary>
/// 对话场景总导演：串联 交互检测 → 对话框架 → 相机切换 → 交互提示。
/// 对话开始时把玩家与 NPC 平滑转向面对面；对话中锁移动输入、藏提示；
/// 结束后回玩法相机并恢复输入。
/// </summary>
public class GenshinDialogDirector : MonoBehaviour
{
    [SerializeField] private DialogueRunner runner;
    [SerializeField] private DialoguePlayerController playerController;
    [SerializeField] private GenshinInteractionZone interactionZone;
    [SerializeField] private GenshinCameraDirector cameraDirector;
    [SerializeField] private GameObject interactionHint;

    [Header("面对面转向")]
    [SerializeField] private float faceEachOtherDuration = 0.3f;

    [Tooltip("玩法态是否锁定并隐藏鼠标指针。本场景需要随时点 HUD 设置按钮，默认不锁（POV 仍用鼠标增量）。")]
    [SerializeField] private bool lockCursorInGameplay = false;

    public bool InDialogue { get; private set; }

    /// <summary>测试钩子：当前是否正在等“面对面转向”结束</summary>
    public bool IsFacing { get; private set; }

    private Coroutine faceRoutine;

    private void Start()
    {
        if (interactionZone != null)
        {
            interactionZone.RangeChanged += _ => RefreshHint();
            interactionZone.InteractRequested += TryStartDialogue;
        }

        if (runner != null && runner.ViewModel != null)
        {
            runner.ViewModel.NodeChanged += OnNodeChanged;
            runner.ViewModel.DialogEnded += OnDialogEnded;
        }

        // 指针状态派生：对话中/暂停中释放，玩法态按开关决定
        PauseManager.PausedChanged += OnPauseChanged;
        UpdateCursorState();

        RefreshHint();
    }

    private void OnDestroy()
    {
        PauseManager.PausedChanged -= OnPauseChanged;

        if (runner != null && runner.ViewModel != null)
        {
            runner.ViewModel.NodeChanged -= OnNodeChanged;
            runner.ViewModel.DialogEnded -= OnDialogEnded;
        }
    }

    private void OnPauseChanged(bool paused)
    {
        UpdateCursorState();
        RefreshHint();
    }

    /// <summary>指针状态派生：对话中或暂停中释放；玩法态由 lockCursorInGameplay 决定</summary>
    private void UpdateCursorState()
    {
        bool free = InDialogue || PauseManager.IsPaused || !lockCursorInGameplay;
        Cursor.lockState = free ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = free;
    }

    /// <summary>
    /// 从存档恢复对话（场景存档用）
    /// 与 TryStartDialogue 的区别：不看交互距离、不播 0.3s 面对面动画、直接跳到指定节点——
    /// 恢复发生在进场景的第一时间，任何过渡都会让镜头/站位在玩家眼前"动一下"。
    /// </summary>
    /// <param name="nodeId">存档里的对话节点 ConfigId</param>
    /// <param name="savedVariablesJson">存档变量快照（可为 null）</param>
    /// <param name="firedEventKeys">已发布过事件的选项 Key（抑制重复推进任务）</param>
    public void RestoreDialogue(int nodeId, string savedVariablesJson = null,
        IEnumerable<string> firedEventKeys = null)
    {
        if (InDialogue || runner == null) return;

        Transform player = playerController != null ? playerController.transform : null;
        Transform npc = interactionZone != null ? interactionZone.transform : null;

        InDialogue = true;
        if (playerController != null) playerController.InputLocked = true;

        FaceEachOtherImmediate(player, npc);

        UpdateCursorState();
        RefreshHint();

        if (cameraDirector != null) cameraDirector.EnterDialogue(player, npc);

        RestoreDialogueAsync(nodeId, savedVariablesJson, firedEventKeys).Forget();
    }

    private async UniTaskVoid RestoreDialogueAsync(int nodeId, string savedVariablesJson,
        IEnumerable<string> firedEventKeys)
    {
        // 先开面板再跳节点：否则恢复时第一句的 NodeChanged 没有 View 接收
        await OpenDialogueUiAsync();
        runner.PlayGraphFrom(runner.DialogueGraph, nodeId, savedVariablesJson, firedEventKeys);
    }

    /// <summary>开始对话入口：交互键走此路，E2E 测试也直接调用</summary>
    public void TryStartDialogue()
    {
        if (InDialogue || runner == null) return;
        if (PauseManager.IsPaused) return;                 // 暂停中不开新对话（进行中的对话会随世界一起冻结）
        if (interactionZone == null || !interactionZone.PlayerInRange) return;

        InDialogue = true;
        if (playerController != null) playerController.InputLocked = true;

        // 两人平滑转向面对面
        if (faceRoutine != null) StopCoroutine(faceRoutine);
        faceRoutine = StartCoroutine(FaceEachOtherRoutine());

        UpdateCursorState();
        RefreshHint();

        if (cameraDirector != null)
            cameraDirector.EnterDialogue(
                playerController != null ? playerController.transform : null,
                interactionZone != null ? interactionZone.transform : null);

        StartDialogueAsync().Forget();
    }

    private async UniTaskVoid StartDialogueAsync()
    {
        await OpenDialogueUiAsync();
        runner.StartDialog();
    }

    /// <summary>
    /// 打开对话面板（框架注册视图）。UIManager 不存在时降级为只跑逻辑（无 UI），不阻断对话。
    /// </summary>
    private async UniTask OpenDialogueUiAsync()
    {
        if (UIManager.Instance == null)
        {
            Debug.LogWarning("[Dialogue] UIManager 不存在，对话面板不会显示");
            return;
        }

        await UIManager.Instance.Open(UIType.DialogueView, runner);
    }

    /// <summary>对话开始时把玩家与 NPC 平滑转到彼此相对</summary>
    private IEnumerator FaceEachOtherRoutine()
    {
        Transform player = playerController != null ? playerController.transform : null;
        Transform npc = interactionZone != null ? interactionZone.transform : null;
        if (player == null || npc == null) yield break;

        Vector3 toNpc = npc.position - player.position;
        Vector3 toPlayer = player.position - npc.position;
        toNpc.y = 0f;
        toPlayer.y = 0f;
        if (toNpc.sqrMagnitude < 0.001f || toPlayer.sqrMagnitude < 0.001f) yield break;

        Quaternion playerTarget = Quaternion.LookRotation(toNpc);
        Quaternion npcTarget = Quaternion.LookRotation(toPlayer);
        Quaternion playerFrom = player.rotation;
        Quaternion npcFrom = npc.rotation;

        IsFacing = true;
        float t = 0f;
        while (t < 1f)
        {
            t = Mathf.Min(1f, t + Time.deltaTime / Mathf.Max(faceEachOtherDuration, 0.01f));
            float k = Mathf.SmoothStep(0f, 1f, t);
            // InputLocked 期间控制器不再驱动转向，这里安全接管
            player.rotation = Quaternion.Slerp(playerFrom, playerTarget, k);
            npc.rotation = Quaternion.Slerp(npcFrom, npcTarget, k);
            yield return null;
        }
        IsFacing = false;
        faceRoutine = null;
    }

    /// <summary>立即面对面（恢复存档用；不播过渡，避免恢复瞬间镜头对不上）</summary>
    private void FaceEachOtherImmediate(Transform player, Transform npc)
    {
        if (player == null || npc == null) return;

        Vector3 toNpc = npc.position - player.position;
        toNpc.y = 0f;
        if (toNpc.sqrMagnitude < 0.001f) return;

        player.rotation = Quaternion.LookRotation(toNpc);
        npc.rotation = Quaternion.LookRotation(-toNpc);
    }

    private void OnNodeChanged(DialogueNodeData node)    {
        // 说话人切换 → 对话机位正反打
        if (cameraDirector != null) cameraDirector.SwitchSpeaker(node.SpeakType);
    }

    private void OnDialogEnded()
    {
        InDialogue = false;

        if (cameraDirector != null) cameraDirector.ExitDialogue();
        if (playerController != null) playerController.InputLocked = false;

        UpdateCursorState();
        RefreshHint();

        CloseDialogueUiAfterFadeAsync().Forget();
    }

    /// <summary>
    /// 等面板淡出播完再关（框架 OnClose 会 Kill 补间，立刻关会看不到淡出）。
    /// 若这段时间玩家又触发了新对话，则不关（面板复用）。
    /// </summary>
    private async UniTaskVoid CloseDialogueUiAfterFadeAsync()
    {
        const float fadeSeconds = 0.3f;
        await UniTask.Delay(System.TimeSpan.FromSeconds(fadeSeconds), ignoreTimeScale: true);

        if (InDialogue) return;
        if (UIManager.Instance == null) return;
        if (!UIManager.Instance.IsOpen(UIType.DialogueView)) return;

        await UIManager.Instance.Close(UIType.DialogueView);
    }

    private void RefreshHint()
    {
        if (interactionHint == null) return;

        bool show = !InDialogue
                    && !PauseManager.IsPaused
                    && interactionZone != null
                    && interactionZone.PlayerInRange;

        interactionHint.SetActive(show);
    }
}
