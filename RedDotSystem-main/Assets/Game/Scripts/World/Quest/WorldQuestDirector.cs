using Common.Network;

using UnityEngine;

/// <summary>
/// 大世界任务编排：把叙事任务接到大世界玩法上（挂在 DashijieSceneBootstrap 同一物体）。
///
/// 只做三件事：
/// 1) 任务提交提示 → 通用提示条；
/// 2) 地图标记数据源：追踪中任务的当前目标 NPC 世界坐标（小地图/大地图共用，保证两处口径一致）；
/// 3) NPC 注册表兜底查询（NPC 自己 OnEnable 会登记，这里只在需要时按 npcKey 取）。
/// 任务规则一律在服务端，本类不做任何进度判断。
/// </summary>
public class WorldQuestDirector : MonoBehaviour
{
    private MmQuestRuntime runtime;

    private void OnEnable()
    {
        runtime = MmQuestRuntime.Instance;
        if (runtime == null)
        {
            // 自举在 BeforeSceneLoad，正常已就绪；极端时序下延迟一帧再取
            StartCoroutine(SubscribeNextFrame());
            return;
        }
        runtime.ToastReceived += OnToastReceived;
    }

    private System.Collections.IEnumerator SubscribeNextFrame()
    {
        yield return null;
        runtime = MmQuestRuntime.Instance;
        if (runtime == null) yield break;
        runtime.ToastReceived += OnToastReceived;
    }

    private void OnDisable()
    {
        if (runtime != null) runtime.ToastReceived -= OnToastReceived;
        runtime = null;
    }

    private void OnToastReceived(NarrativeQuestToastNotify notify)
    {
        if (notify == null) return;
        string title = string.IsNullOrEmpty(notify.Title) ? "任务" : notify.Title;
        WorldPrompt.Toast($"{title} · {notify.Message}");
    }

    // ──────────────────────────────
    // 地图标记
    // ──────────────────────────────

    /// <summary>
    /// 取当前追踪任务的目标 NPC 位置（给小地图/大地图标记用）。
    /// 可见条件：有追踪任务 + 任务执行中 + 还有未达标目标 + 该任务配了 npcKey + 场景里找得到这个 NPC。
    /// 目标达标或任务提交后自然不满足，标记随即消失。
    /// </summary>
    /// <param name="position">参数名：position，类型：Vector3，用途：标记的世界坐标</param>
    /// <param name="npcKey">参数名：npcKey，类型：string，用途：目标 NPC 标识（调试与图标分型用）</param>
    /// <returns>返回值类型：bool，用途：是否应显示标记</returns>
    public bool TryGetTrackedMarker(out Vector3 position, out string npcKey)
    {
        position = Vector3.zero;
        npcKey = null;

        MmQuestRuntime quests = MmQuestRuntime.Instance;
        if (quests == null) return false;

        int tracked = quests.ResolveTrackedQuest();
        if (tracked <= 0) return false;

        // 可接取也标记：任务还没接时玩家同样需要"找到这个人"（只做标记，不改变接取规则）
        Miemie.DialogSystem.Quest.EQuestState state = quests.StateOf(tracked);
        if (state != Miemie.DialogSystem.Quest.EQuestState.执行中
            && state != Miemie.DialogSystem.Quest.EQuestState.可用) return false;

        if (quests.CurrentGoalIndex(tracked) < 0) return false;   // 目标已全部达标，等玩家提交

        string key = quests.ConfigOf(tracked)?.NpcKey;
        WorldDialogueNpc npc = WorldDialogueNpc.Find(key);
        if (npc == null) return false;

        npcKey = key;
        position = npc.transform.position;
        return true;
    }
}
