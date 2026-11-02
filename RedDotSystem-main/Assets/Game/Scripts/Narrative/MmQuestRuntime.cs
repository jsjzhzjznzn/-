using System;
using System.Collections.Generic;
using System.Reflection;

using Common.Network;

using Miemie.DialogSystem.Quest;

using MH;

using UnityEngine;

/// <summary>
/// 叙事任务运行时（客户端唯一任务入口，运行时自举，场景零接线）。
///
/// 职责分三层：
/// 1) 宿主：进 Play 前创建一个常驻物体，挂 Mm 框架的 QuestManager 与 MmQuestServerSync
///    （桥负责 2701/2705/2706/2708/2711 与整表镜像），任务配置从 Resources/Quest 读取；
/// 2) 镜像门面：UI/HUD 只读这里（分类列表、状态、进度、当前目标、追踪任务），不直接碰 Mm 框架；
/// 3) 红点：可接取 + 可提交数量写入 Key16（Number 类型才会显示数字）。
///
/// 权威在服务端：本类不做任何规则判断，任何本地改动都会被下一份 2710 快照覆盖。
/// </summary>
public sealed class MmQuestRuntime : MonoBehaviour
{
    /// <summary>任务配置在 Resources 下的目录（QuestData 资产）</summary>
    public const string QuestResourceFolder = "Quest";

    /// <summary>全局唯一实例</summary>
    public static MmQuestRuntime Instance { get; private set; }

    /// <summary>任务数据变化（快照镜像 / 接取 / 提交 / 放弃 / 推送）</summary>
    public event Action QuestsUpdated;

    /// <summary>服务端提示推送（提交成功后的提示文案）</summary>
    public event Action<NarrativeQuestToastNotify> ToastReceived;

    /// <summary>Mm 框架的任务管理器（镜像本体）</summary>
    public QuestManager Mirror { get; private set; }

    /// <summary>服务端桥（2701 等收发与镜像落盘）</summary>
    public MmQuestServerSync Sync { get; private set; }

    /// <summary>当前追踪的任务（0 = 未追踪）</summary>
    public int TrackedQuestId { get; private set; }

    /// <summary>运行时自举：场景加载前创建（与 QuestManager/BagManager 相同的自举模式）</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance != null) return;

        GameObject holder = new GameObject("MmQuestRuntime");
        holder.AddComponent<MmQuestRuntime>();
        DontDestroyOnLoad(holder);
    }

    private void Awake()
    {
        if (Instance != null)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        BuildMirror();
    }

    private void OnDestroy()
    {
        if (Sync != null)
        {
            Sync.SnapshotApplied -= OnSnapshotApplied;
            Sync.ToastReceived -= OnToastReceived;
        }
        if (Instance == this) Instance = null;
    }

    // ──────────────────────────────
    // 宿主组装
    // ──────────────────────────────

    /// <summary>
    /// 组装 Mm 任务本体：先建未激活的子物体，两个组件都挂好再激活。
    /// 原因：QuestManager.OnEnable 靠"同物体有没有 MmQuestServerSync"判断是否走服务端权威模式，
    /// 若在激活状态下先加 QuestManager，它会先订阅本地事件总线，造成本地镜像被本地推进。
    /// </summary>
    private void BuildMirror()
    {
        GameObject core = new("MmQuestCore");
        core.transform.SetParent(transform, false);
        core.SetActive(false);

        Mirror = core.AddComponent<QuestManager>();
        QuestData[] configs = Resources.LoadAll<QuestData>(QuestResourceFolder);
        if (configs == null || configs.Length == 0)
        {
            Debug.LogWarning($"[MmQuestRuntime] Resources/{QuestResourceFolder} 下没有 QuestData 配置，任务面板将是空的");
        }
        SetQuestList(Mirror, configs ?? Array.Empty<QuestData>());

        Sync = core.AddComponent<MmQuestServerSync>();
        Sync.SnapshotApplied += OnSnapshotApplied;
        Sync.ToastReceived += OnToastReceived;

        core.SetActive(true);
    }

    /// <summary>把配置写进 QuestManager 的私有序列化字段（框架没有运行时注入入口）</summary>
    private static void SetQuestList(QuestManager manager, QuestData[] configs)
    {
        FieldInfo field = typeof(QuestManager).GetField("questList", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null)
        {
            Debug.LogError("[MmQuestRuntime] QuestManager.questList 字段不存在，任务配置无法注入");
            return;
        }
        field.SetValue(manager, new List<QuestData>(configs));
    }

    private void OnSnapshotApplied()
    {
        // 追踪任务失效（被提交/放弃/配置消失）时清空，交给 UI 决定默认追踪
        if (TrackedQuestId > 0 && Mirror != null && Mirror.GetState(TrackedQuestId) != EQuestState.执行中)
            TrackedQuestId = 0;

        RefreshRedDot();
        QuestsUpdated?.Invoke();
    }

    private void OnToastReceived(NarrativeQuestToastNotify notify)
    {
        // 提交会发放奖励 → 背包数据过期，静默重拉一次（面板未开时无感）
        BagManager.Instance?.RequestBag();
        ToastReceived?.Invoke(notify);
    }

    // ──────────────────────────────
    // 只读门面（UI / HUD / 地图标记共用）
    // ──────────────────────────────

    /// <summary>全部任务配置（按任务ID升序）</summary>
    public IReadOnlyList<QuestData> AllQuests => Mirror != null ? Mirror.RegisteredQuests : Array.Empty<QuestData>();

    /// <summary>某分类下的任务（按任务ID升序）</summary>
    public List<QuestData> QuestsOf(EQuestCategory category)
    {
        var list = new List<QuestData>();
        foreach (QuestData quest in AllQuests)
        {
            if (quest != null && quest.Category == category) list.Add(quest);
        }
        list.Sort((left, right) => left.QuestId.CompareTo(right.QuestId));
        return list;
    }

    /// <summary>任务状态（未注册返回 未激活）</summary>
    public EQuestState StateOf(int questId) => Mirror != null ? Mirror.GetState(questId) : EQuestState.未激活;

    /// <summary>任务目标数量</summary>
    public int GoalCountOf(int questId) => Mirror != null ? Mirror.GetGoalCount(questId) : 0;

    /// <summary>第 index 个目标的进度</summary>
    public int GoalProgressOf(int questId, int index) => Mirror != null ? Mirror.GetGoalProgress(questId, index) : 0;

    /// <summary>
    /// 当前目标索引：第一个未达标的目标（全部达标返回 -1）。
    /// 任务展示（HUD 任务卡、地图标记、详情"当前目标"胶囊）都以它为准。
    /// </summary>
    public int CurrentGoalIndex(int questId)
    {
        QuestData quest = ConfigOf(questId);
        if (quest == null) return -1;

        IReadOnlyList<QuestGoal> goals = quest.GetGoals();
        for (int i = 0; i < goals.Count; i++)
        {
            if (GoalProgressOf(questId, i) < Need(goals[i])) return i;
        }
        return -1;
    }

    /// <summary>当前目标（全部达标返回 null）</summary>
    public QuestGoal CurrentGoal(int questId)
    {
        int index = CurrentGoalIndex(questId);
        return index < 0 ? null : ConfigOf(questId)?.GetGoals()[index];
    }

    /// <summary>目标需求数量（≤0 视为 1，与 Mm 框架判定一致）</summary>
    public static int Need(QuestGoal goal) => goal == null ? 1 : (goal.count > 0 ? goal.count : 1);

    /// <summary>取任务配置</summary>
    public QuestData ConfigOf(int questId)
    {
        foreach (QuestData quest in AllQuests)
        {
            if (quest != null && quest.QuestId == questId) return quest;
        }
        return null;
    }

    /// <summary>是否可接取（可用 + 前置任务都已提交；真正的前置校验在服务端）</summary>
    public bool IsAcceptable(int questId) => StateOf(questId) == EQuestState.可用;

    /// <summary>是否目标已全部达标但尚未提交</summary>
    public bool IsSubmittable(int questId)
        => StateOf(questId) == EQuestState.执行中 && CurrentGoalIndex(questId) < 0;

    /// <summary>可接取数量</summary>
    public int AcceptableCount
    {
        get
        {
            int count = 0;
            foreach (QuestData quest in AllQuests)
            {
                if (quest != null && StateOf(quest.QuestId) == EQuestState.可用) count++;
            }
            return count;
        }
    }

    /// <summary>可提交数量</summary>
    public int SubmittableCount
    {
        get
        {
            int count = 0;
            foreach (QuestData quest in AllQuests)
            {
                if (quest != null && IsSubmittable(quest.QuestId)) count++;
            }
            return count;
        }
    }

    // ──────────────────────────────
    // 追踪
    // ──────────────────────────────

    /// <summary>
    /// 追踪指定任务；传 0 取消追踪。
    /// 只允许追踪执行中的任务（无标记可指的任务视为取消追踪）。
    /// </summary>
    public void Track(int questId)
    {
        if (questId > 0 && StateOf(questId) != EQuestState.执行中)
        {
            TrackedQuestId = 0;
        }
        else
        {
            TrackedQuestId = questId;
        }

        RefreshRedDot();
        QuestsUpdated?.Invoke();
    }

    /// <summary>取当前追踪任务：显式追踪优先，否则自动取第一个执行中/可提交的任务</summary>
    public int ResolveTrackedQuest()
    {
        if (TrackedQuestId > 0 && StateOf(TrackedQuestId) == EQuestState.执行中) return TrackedQuestId;

        foreach (QuestData quest in AllQuests)
        {
            if (quest != null && StateOf(quest.QuestId) == EQuestState.执行中) return quest.QuestId;
        }

        foreach (QuestData quest in AllQuests)
        {
            if (quest != null && StateOf(quest.QuestId) == EQuestState.可用) return quest.QuestId;
        }
        return 0;
    }

    // ──────────────────────────────
    // 操作（全部转发服务端，本地只做 UI 提示）
    // ──────────────────────────────

    /// <summary>拉取最新快照（打开面板 / 登录后）</summary>
    public void RequestSync() => Sync?.RequestSync();

    /// <summary>接取任务（前置链由服务端裁决）</summary>
    public void Accept(int questId) => Sync?.AcceptServer(questId);

    /// <summary>提交任务（目标达标由服务端裁决）</summary>
    public void Submit(int questId) => Sync?.SubmitServer(questId);

    /// <summary>放弃任务（状态回到未激活、进度清零）</summary>
    public void Abandon(int questId) => Sync?.AbandonServer(questId);

    // ──────────────────────────────
    // 红点
    // ──────────────────────────────

    /// <summary>待办数（可接取 + 可提交）写入任务入口红点；0 时框架自动熄灭</summary>
    private void RefreshRedDot()
    {
        RedDotSingleton redDot = RedDotSingleton.Instance;
        if (redDot == null) return;

        redDot.Set(RedDotKeys.QuestEntry, AcceptableCount + SubmittableCount, RedDotFlags.Number);
    }
}
