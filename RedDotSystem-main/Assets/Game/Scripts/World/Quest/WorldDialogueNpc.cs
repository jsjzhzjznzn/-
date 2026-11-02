using System.Collections.Generic;

using Cysharp.Threading.Tasks;

using Miemie.DialogSystem;

using SkierFramework;

using UnityEngine;

/// <summary>
/// 大世界可交互 NPC：走近显示「按 F 交谈」，按键后开对话；对话结束由对话系统发布
/// <see cref="NarrativeEventKeys.DialogueTriggered"/>（GraphFinished），桥会上报 2705 推进叙事任务。
///
/// 为什么每次交互都新建一个 DialogueRunner：
/// 框架的 ViewModel 在一次生命周期内只发布一次同一个 eventKey（防场景存档恢复时重复推进），
/// 而 Runner 的 Start(autoStart) 会在场景加载时就播完第一遍——复用同一个 Runner 会导致
/// 之后再也发不出 GraphFinished，任务永远推不动。
/// </summary>
public class WorldDialogueNpc : MonoBehaviour
{
    [Header("身份")]
    [Tooltip("与任务配置 QuestData.npcKey 对应的唯一标识（如 daniya / firefly）")]
    [SerializeField] private string npcKey = "npc";

    [Tooltip("头顶/提示里显示的名字")]
    [SerializeField] private string displayName = "NPC";

    [Header("交互")]
    [SerializeField] private DialogueGraph dialogueGraph;
    [SerializeField] private float interactRange = 3f;
    [SerializeField] private KeyCode interactKey = KeyCode.F;

    /// <summary>唯一标识（任务配置用它找 NPC 的位置）</summary>
    public string NpcKey => npcKey;

    /// <summary>展示名</summary>
    public string DisplayName => displayName;

    /// <summary>场景里全部激活的 NPC（地图标记与任务导演共用）</summary>
    private static readonly List<WorldDialogueNpc> registry = new();

    /// <summary>按 npcKey 找 NPC（找不到返回 null）</summary>
    public static WorldDialogueNpc Find(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        foreach (WorldDialogueNpc npc in registry)
        {
            if (npc != null && npc.npcKey == key) return npc;
        }
        return null;
    }

    /// <summary>当前场景全部 NPC（只读）</summary>
    public static IReadOnlyList<WorldDialogueNpc> All => registry;

    /// <summary>当前是否有一段对话由本 NPC 打开</summary>
    public bool InDialogue { get; private set; }

    private Player player;
    private bool inRange;

    /// <summary>本次对话临时创建的 Runner（对话结束即销毁）</summary>
    private DialogueRunner runner;
    private GameObject runnerHolder;

    /// <summary>对话面板是否已经真正打开（打开是异步的，用它区分"还没开"与"被外部关掉"）</summary>
    private bool panelOpened;

    // ──────────────────────────────
    // 生命周期
    // ──────────────────────────────

    private void OnEnable()
    {
        if (!registry.Contains(this)) registry.Add(this);
    }

    private void OnDisable()
    {
        registry.Remove(this);
        if (inRange)
        {
            inRange = false;
            WorldPrompt.SetInteract(null);
        }
        if (InDialogue) CleanupDialogue();
    }

    private void Update()
    {
        if (InDialogue)
        {
            // 面板被外部关闭（Esc / 切场景 / 别的面板顶掉）时收尾，否则输入门禁会一直卡住
            if (panelOpened && (UIManager.Instance == null || !UIManager.Instance.IsOpen(UIType.DialogueView)))
                CleanupDialogue();
            return;
        }

        if (player == null) player = ResolvePlayer();
        if (player == null) return;

        Vector3 offset = player.transform.position - transform.position;
        offset.y = 0f;
        bool range = offset.sqrMagnitude <= interactRange * interactRange;

        if (range != inRange)
        {
            inRange = range;
            WorldPrompt.SetInteract(range ? $"按 {interactKey} 与 {displayName} 交谈" : null);
        }

        if (!inRange) return;
        if (PauseManager.IsPaused || PlayerInputGate.IsBlocked) return;
        if (!Input.GetKeyDown(interactKey)) return;

        BeginDialogueAsync().Forget();
    }

    /// <summary>当前操控的玩家（换人后 switcher 会换成新实例，这里每次重新取）</summary>
    private static Player ResolvePlayer()
    {
        DashijiePlayerSwitcher switcher = FindObjectOfType<DashijiePlayerSwitcher>();
        if (switcher != null && switcher.CurrentPlayer != null) return switcher.CurrentPlayer;
        return FindObjectOfType<Player>();
    }

    // ──────────────────────────────
    // 对话
    // ──────────────────────────────

    private async UniTaskVoid BeginDialogueAsync()
    {
        if (InDialogue || dialogueGraph == null)
        {
            if (dialogueGraph == null) Debug.LogWarning($"[WorldDialogueNpc] {npcKey} 没有配置对话图");
            return;
        }

        InDialogue = true;
        WorldPrompt.SetInteract(null);

        // 对话期间锁角色输入（暂停不适用：对话要能推进动画与打字机）
        PlayerInputGate.Push(PlayerInputGate.Source.Dialogue);
        SetCursorReleased(true);

        FacePlayer();

        DialogueRunner created = CreateRunner();
        if (created == null)
        {
            CleanupDialogue();
            return;
        }

        created.ViewModel.DialogEnded += OnDialogEnded;

        if (UIManager.Instance != null)
            await UIManager.Instance.Open(UIType.DialogueView, created);

        panelOpened = true;
        if (!InDialogue) return;   // 打开面板期间被外力关闭（例如场景卸载）
        created.PlayGraph(dialogueGraph);
    }

    /// <summary>
    /// 创建一次性 Runner：物体先停用再挂组件（避免 Awake/Start 抢先自动播放），
    /// 关掉 autoStart 后激活并立刻由调用方 PlayGraph。
    /// </summary>
    private DialogueRunner CreateRunner()
    {
        runnerHolder = new GameObject($"[DialogueRunner:{npcKey}]");
        runnerHolder.transform.SetParent(transform, false);
        runnerHolder.SetActive(false);

        runner = runnerHolder.AddComponent<DialogueRunner>();
        dialogueGraphForAutoStartOff(runner);

        runnerHolder.SetActive(true);
        return runner;
    }

    /// <summary>把 autoStart 关掉（框架字段私有，只能反射）</summary>
    private static void dialogueGraphForAutoStartOff(DialogueRunner target)
    {
        var field = typeof(DialogueRunner).GetField("autoStart",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        field?.SetValue(target, false);
    }

    private void OnDialogEnded()
    {
        if (!InDialogue) return;

        // 面板淡出交给框架，这里只做逻辑收尾；下一帧销毁 Runner（销毁会 Shutdown ViewModel）
        CloseDialogueUiAsync().Forget();
    }

    private async UniTaskVoid CloseDialogueUiAsync()
    {
        if (UIManager.Instance != null && UIManager.Instance.IsOpen(UIType.DialogueView))
            await UIManager.Instance.Close(UIType.DialogueView);

        CleanupDialogue();
    }

    /// <summary>收尾：解绑 → 销毁临时 Runner → 放开输入门禁与光标</summary>
    private void CleanupDialogue()
    {
        if (runner != null && runner.ViewModel != null)
            runner.ViewModel.DialogEnded -= OnDialogEnded;

        if (runnerHolder != null) Destroy(runnerHolder);
        runner = null;
        runnerHolder = null;

        InDialogue = false;
        panelOpened = false;
        PlayerInputGate.Pop(PlayerInputGate.Source.Dialogue);
        SetCursorReleased(false);
    }

    /// <summary>转向玩家（只转 NPC：玩家朝向由 KCC 驱动，直接写 rotation 会被下一帧覆盖）</summary>
    private void FacePlayer()
    {
        if (player == null) return;

        Vector3 direction = player.transform.position - transform.position;
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f) return;

        transform.rotation = Quaternion.LookRotation(direction);
    }

    private static void SetCursorReleased(bool released)
    {
        Cursor.lockState = released ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = released;
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.85f, 0.74f, 0.49f, 0.6f);
        Gizmos.DrawWireSphere(transform.position, interactRange);
    }
}
