using System.Collections.Generic;

using Common.Network;

using Cysharp.Threading.Tasks;

using Miemie.DialogSystem;
using Miemie.DialogSystem.Quest;

using SkierFramework;

using TMPro;

using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 任务界面（鸣潮风三栏：左侧竖排导航 / 中间分组任务列表 / 右侧任务详情）。
///
/// 数据全部来自 <see cref="MmQuestRuntime"/>（服务端权威镜像），本类只做渲染与操作转发：
/// - 列表：主线 / 支线分组 → 任务卡片（选中展开目标行）；
/// - 详情：任务名、当前目标胶囊、描述、目标清单、奖励格、主按钮（接取 / 追踪 / 提交）；
/// - 主按钮语义随任务状态切换，追踪只对进行中的任务生效。
///
/// 列表用普通实例化行而不是虚拟列表：任务数是个位数，虚拟化的池化换绑反而更容易出绑定错。
/// </summary>
public class QuestView : UIView
{
    // ──────────────────────────────
    // 序列化引用（编辑器搭建脚本注入）
    // ──────────────────────────────

    [Header("面板")]
    [ControlBinding] public Button closeButton;
    [ControlBinding] public Button backdropButton;
    [ControlBinding] public TMP_Text titleText;
    [ControlBinding] public TMP_Text statusText;

    [Header("左侧导航")]
    [ControlBinding] public Button navQuestButton;
    [ControlBinding] public Button navAchievementButton;
    [ControlBinding] public Button navMapButton;
    [ControlBinding] public Button navCharacterButton;
    [ControlBinding] public Button navSettingsButton;

    [Header("中间列表")]
    [ControlBinding] public ScrollRect questScroll;
    [ControlBinding("QuestScroll/Viewport/Content")] public RectTransform listContent;

    /// <summary>任务卡片模板（构建器生成后置为未激活，运行时克隆）</summary>
    [ControlBinding("QuestScroll/Viewport/Content/CardTemplate")] public RectTransform cardTemplate;

    /// <summary>卡片内的目标行模板（未激活，运行时克隆到卡片的 GoalRoot 下）</summary>
    [ControlBinding("QuestScroll/Viewport/Content/CardTemplate/GoalRoot/GoalTemplate")] public RectTransform goalTemplate;

    /// <summary>分组标题条模板（未激活）</summary>
    [ControlBinding("QuestScroll/Viewport/Content/SectionTemplate")] public RectTransform sectionTemplate;

    [Header("右侧详情")]
    [ControlBinding] public TMP_Text detailTitle;
    [ControlBinding("DetailGoalChip/Label")] public TMP_Text detailGoalChip;
    [ControlBinding] public TMP_Text detailRegion;
    [ControlBinding] public TMP_Text detailDesc;
    [ControlBinding] public TMP_Text detailGoals;

    [ControlBinding] public GameObject rewardSection;
    [ControlBinding("RewardSection/RewardContent")] public RectTransform rewardContent;

    /// <summary>奖励格模板（未激活）</summary>
    [ControlBinding("RewardSection/RewardContent/CellTemplate")] public RectTransform rewardCellTemplate;

    [ControlBinding] public Button primaryButton;
    [ControlBinding("PrimaryButton/Label")] public TMP_Text primaryLabel;

    // ──────────────────────────────
    // 视觉参数（与构建器保持一致）
    // ──────────────────────────────

    /// <summary>单一强调金</summary>
    private static readonly Color Gold = new(0.851f, 0.737f, 0.486f, 1f);
    private static readonly Color TextMain = new(0.94f, 0.94f, 0.94f, 1f);
    private static readonly Color TextDim = new(0.72f, 0.72f, 0.72f, 1f);
    private static readonly Color TextFaint = new(0.45f, 0.46f, 0.48f, 1f);

    // ──────────────────────────────
    // 私有状态
    // ──────────────────────────────

    /// <summary>当前选中的任务（0 = 未选中）</summary>
    private int selectedQuestId;

    /// <summary>实例化出来的行（重刷时统一销毁）</summary>
    private readonly List<GameObject> spawnedRows = new();

    /// <summary>主按钮当前语义</summary>
    private enum PrimaryAction
    {
        None,
        Accept,
        Submit,
        Track,
        Untrack,
        Submitted,
    }

    private PrimaryAction primaryAction = PrimaryAction.None;

    // ──────────────────────────────
    // 框架生命周期
    // ──────────────────────────────

    private void Awake()
    {
        if (cardTemplate != null) cardTemplate.gameObject.SetActive(false);
        if (goalTemplate != null) goalTemplate.gameObject.SetActive(false);
        if (sectionTemplate != null) sectionTemplate.gameObject.SetActive(false);
        if (rewardCellTemplate != null) rewardCellTemplate.gameObject.SetActive(false);
    }

    public override void OnInit(UIControlData uIControlData, UIViewController controller)
    {
        base.OnInit(uIControlData, controller);

        if (MmQuestRuntime.Instance != null)
            MmQuestRuntime.Instance.QuestsUpdated += RefreshAll;

        // 奖励格图标来自背包物品配置：背包未拉时 iconKey 为空，格子只剩空底框
        if (BagManager.Instance != null)
            BagManager.Instance.BagUpdated += OnBagUpdated;

        // 切场景（进副本/大世界/对话等）必须收起本页：全屏底板留着会盖住新场景，
        // 而且面板实例常驻 DDOL，不收起还会一直显示旧场景算出来的内容（NPC/区域文案）
        UnityEngine.SceneManagement.SceneManager.activeSceneChanged += OnActiveSceneChanged;
    }

    /// <summary>切场景时自动收起任务面板</summary>
    private void OnActiveSceneChanged(UnityEngine.SceneManagement.Scene previous,
                                      UnityEngine.SceneManagement.Scene next)
    {
        if (this == null || !gameObject.activeInHierarchy) return;
        if (UIManager.Instance == null || !UIManager.Instance.IsOpen(UIType.QuestView)) return;
        UIManager.Instance.Close(UIType.QuestView).Forget();
    }

    /// <summary>背包配置到达后补刷奖励格图标</summary>
    private void OnBagUpdated() => RefreshAll();

    public override void OnAddListener()
    {
        if (closeButton != null) closeButton.onClick.AddListener(OnCloseClicked);
        if (backdropButton != null) backdropButton.onClick.AddListener(OnCloseClicked);
        if (primaryButton != null) primaryButton.onClick.AddListener(OnPrimaryClicked);

        if (navAchievementButton != null) navAchievementButton.onClick.AddListener(OnNavUnavailable);
        if (navMapButton != null) navMapButton.onClick.AddListener(OnNavUnavailable);
        if (navCharacterButton != null) navCharacterButton.onClick.AddListener(OnNavUnavailable);
        if (navSettingsButton != null) navSettingsButton.onClick.AddListener(OnNavUnavailable);
    }

    public override void OnRemoveListener()
    {
        if (closeButton != null) closeButton.onClick.RemoveAllListeners();
        if (backdropButton != null) backdropButton.onClick.RemoveAllListeners();
        if (primaryButton != null) primaryButton.onClick.RemoveAllListeners();

        if (navAchievementButton != null) navAchievementButton.onClick.RemoveAllListeners();
        if (navMapButton != null) navMapButton.onClick.RemoveAllListeners();
        if (navCharacterButton != null) navCharacterButton.onClick.RemoveAllListeners();
        if (navSettingsButton != null) navSettingsButton.onClick.RemoveAllListeners();
    }

    public override void OnOpen(object userData)
    {
        base.OnOpen(userData);

        if (statusText != null) statusText.SetText(string.Empty);
        RefreshAll();

        // 打开即拉最新快照（服务端权威：面板展示的必须是刚拉到的状态）
        MmQuestRuntime.Instance?.RequestSync();

        // 奖励图标依赖背包物品配置，没拉到就补一次
        if (BagManager.Instance != null && BagManager.Instance.AllConfigs.Count == 0)
            BagManager.Instance.RequestBag();
    }

    public override void OnCancel()
    {
        OnCloseClicked();
    }

    private void OnDestroy()
    {
        UnityEngine.SceneManagement.SceneManager.activeSceneChanged -= OnActiveSceneChanged;

        if (MmQuestRuntime.Instance != null)
            MmQuestRuntime.Instance.QuestsUpdated -= RefreshAll;
        if (BagManager.Instance != null)
            BagManager.Instance.BagUpdated -= OnBagUpdated;
    }

    // ──────────────────────────────
    // 渲染
    // ──────────────────────────────

    private void RefreshAll()
    {
        MmQuestRuntime quests = MmQuestRuntime.Instance;
        if (quests == null) return;

        if (titleText != null) titleText.SetText("任务");

        RebuildList(quests);
        RefreshDetail(quests);
    }

    /// <summary>重建列表：分组标题条 + 任务卡片；选中项不存在时退回第一个任务</summary>
    private void RebuildList(MmQuestRuntime quests)
    {
        if (listContent == null || cardTemplate == null) return;

        foreach (GameObject row in spawnedRows) Destroy(row);
        spawnedRows.Clear();

        // 选中项失效（任务被提交/配置变更）时按"可提交 → 执行中 → 可用"顺序退到第一个任务
        if (quests.ConfigOf(selectedQuestId) == null) selectedQuestId = PickDefaultQuest(quests);

        AddSection(quests, EQuestCategory.主线任务, "主线任务", "MAIN");
        AddSection(quests, EQuestCategory.支线任务, "支线任务", "SIDE");

        // 因为垂直布局要等一帧才排版，重刷后强制刷新一次画布
        Canvas.ForceUpdateCanvases();
        if (questScroll != null) questScroll.verticalNormalizedPosition = 1f;
    }

    private void AddSection(MmQuestRuntime quests, EQuestCategory category, string title, string watermark)
    {
        List<QuestData> list = quests.QuestsOf(category);
        if (list.Count == 0) return;

        if (sectionTemplate != null)
        {
            RectTransform header = Instantiate(sectionTemplate, listContent);
            header.gameObject.SetActive(true);
            header.name = $"Section_{category}";
            spawnedRows.Add(header.gameObject);

            TMP_Text label = header.Find("Label")?.GetComponent<TMP_Text>();
            if (label != null) label.SetText(title);
            TMP_Text mark = header.Find("Watermark")?.GetComponent<TMP_Text>();
            if (mark != null) mark.SetText(watermark);
        }

        // 段内排序：可提交 → 执行中 → 可用 → 未激活 → 已提交
        list.Sort((left, right) =>
        {
            int rankLeft = RankOf(quests, left.QuestId);
            int rankRight = RankOf(quests, right.QuestId);
            return rankLeft != rankRight ? rankLeft.CompareTo(rankRight) : left.QuestId.CompareTo(right.QuestId);
        });

        foreach (QuestData quest in list) AddCard(quests, quest);
    }

    private static int RankOf(MmQuestRuntime quests, int questId)
    {
        if (quests.IsSubmittable(questId)) return 0;
        return quests.StateOf(questId) switch
        {
            EQuestState.执行中 => 1,
            EQuestState.可用 => 2,
            EQuestState.未激活 => 3,
            _ => 4,
        };
    }

    private void AddCard(MmQuestRuntime quests, QuestData quest)
    {
        if (cardTemplate == null) return;

        int questId = quest.QuestId;
        bool isSelected = questId == selectedQuestId;
        bool isExpanded = isSelected;

        RectTransform card = Instantiate(cardTemplate, listContent);
        card.gameObject.SetActive(true);
        card.name = $"Card_{questId}";
        spawnedRows.Add(card.gameObject);

        Image background = card.GetComponent<Image>();
        if (background != null)
            background.color = isSelected ? new Color(0.13f, 0.13f, 0.14f, 0.98f) : new Color(0.09f, 0.09f, 0.10f, 0.92f);

        // 选中态金色下划线（参考图用"底部金色发光线"表达当前项）
        Transform underline = card.Find("Underline");
        if (underline != null) underline.gameObject.SetActive(isSelected);

        Button button = card.GetComponent<Button>();
        if (button != null)
        {
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() => SelectQuest(questId));
        }

        TMP_Text name = card.Find("Name")?.GetComponent<TMP_Text>();
        if (name != null)
        {
            name.SetText(quest.Title);
            name.color = TextMain;
        }

        TMP_Text subtitle = card.Find("Subtitle")?.GetComponent<TMP_Text>();
        if (subtitle != null)
        {
            subtitle.SetText(SubtitleOf(quests, quest));
            subtitle.color = quests.IsSubmittable(questId) ? Gold : TextFaint;
        }

        Transform arrow = card.Find("Arrow");
        if (arrow != null) arrow.localEulerAngles = new Vector3(0f, 0f, isExpanded ? 180f : 0f);

        // 目标行只在选中的卡片里展开（参考图同款：展开项列出子目标）
        RectTransform goalRoot = card.Find("GoalRoot") as RectTransform;
        if (goalRoot == null) return;

        foreach (Transform child in goalRoot) Destroy(child.gameObject);
        goalRoot.gameObject.SetActive(isExpanded);
        if (!isExpanded) return;

        IReadOnlyList<QuestGoal> goals = quest.GetGoals();
        for (int i = 0; i < goals.Count; i++) AddGoalRow(quests, questId, goals[i], i, goalRoot);
    }

    private void AddGoalRow(MmQuestRuntime quests, int questId, QuestGoal goal, int index, RectTransform parent)
    {
        if (goalTemplate == null) return;

        RectTransform row = Instantiate(goalTemplate, parent);
        row.gameObject.SetActive(true);
        row.name = $"Goal_{index}";

        int progress = quests.GoalProgressOf(questId, index);
        int need = MmQuestRuntime.Need(goal);
        bool done = progress >= need;
        bool current = index == quests.CurrentGoalIndex(questId);

        // 单选/罗盘：当前目标金色实心，已完成勾选灰化，未开始空心灰环
        Transform bullet = row.Find("Bullet");
        Image bulletImage = bullet != null ? bullet.GetComponent<Image>() : null;
        if (bulletImage != null)
        {
            bulletImage.color = done ? TextFaint : current ? Gold : new Color(0.55f, 0.56f, 0.58f, 1f);
        }

        TMP_Text label = row.Find("Label")?.GetComponent<TMP_Text>();
        if (label != null)
        {
            string mark = done ? "<color=#8A8F96>✓ </color>" : string.Empty;
            string suffix = need > 1 ? $"　<color=#8A8F96>{progress}/{need}</color>" : string.Empty;
            label.SetText($"{mark}{GoalTitle(goal)}{suffix}");
            label.color = done ? TextFaint : current ? TextMain : TextDim;
        }
    }

    /// <summary>右列详情：标题 / 当前目标胶囊 / 区域名 / 描述 / 目标清单 / 奖励 / 主按钮</summary>
    private void RefreshDetail(MmQuestRuntime quests)
    {
        QuestData quest = quests.ConfigOf(selectedQuestId);

        if (quest == null)
        {
            if (detailTitle != null) detailTitle.SetText("请选择任务");
            if (detailGoalChip != null) detailGoalChip.SetText(string.Empty);
            if (detailRegion != null) detailRegion.SetText(string.Empty);
            if (detailDesc != null) detailDesc.SetText(string.Empty);
            if (detailGoals != null) detailGoals.SetText(string.Empty);
            if (rewardSection != null) rewardSection.SetActive(false);
            SetPrimary(PrimaryAction.None, string.Empty);
            return;
        }

        if (detailTitle != null) detailTitle.SetText(quest.Title);

        // 当前目标胶囊：还有未达标目标就展示该目标，否则提示可提交
        QuestGoal currentGoal = quests.CurrentGoal(quest.QuestId);
        if (detailGoalChip != null)
        {
            detailGoalChip.SetText(currentGoal != null
                ? GoalTitle(currentGoal)
                : quests.StateOf(quest.QuestId) == EQuestState.执行中 ? "目标已完成，可提交" : string.Empty);
        }

        WorldDialogueNpc npc = WorldDialogueNpc.Find(quest.NpcKey);
        if (detailRegion != null)
        {
            detailRegion.SetText(npc != null
                ? $"{npc.DisplayName}所在区域"
                : CategoryName(quest.Category));
        }

        if (detailDesc != null) detailDesc.SetText(quest.Description);

        if (detailGoals != null) detailGoals.SetText(BuildGoalList(quests, quest));

        RebuildRewards(quest);

        // 主按钮：可用 → 接取；执行中未达标 → 追踪/取消追踪；执行中达标 → 提交；已提交 → 已完成
        switch (quests.StateOf(quest.QuestId))
        {
            case EQuestState.可用:
                SetPrimary(PrimaryAction.Accept, "接取");
                break;
            case EQuestState.执行中:
                if (quests.CurrentGoalIndex(quest.QuestId) < 0) SetPrimary(PrimaryAction.Submit, "提交");
                else if (quests.TrackedQuestId == quest.QuestId) SetPrimary(PrimaryAction.Untrack, "取消追踪");
                else SetPrimary(PrimaryAction.Track, "追踪");
                break;
            case EQuestState.提交:
                SetPrimary(PrimaryAction.Submitted, "已完成");
                break;
            default:
                SetPrimary(PrimaryAction.None, string.Empty);
                break;
        }
    }

    private void RebuildRewards(QuestData quest)
    {
        if (rewardContent == null || rewardCellTemplate == null) return;

        foreach (Transform child in rewardContent)
        {
            // 模板本身也挂在 RewardContent 下，重刷时不能连它一起销毁
            if (rewardCellTemplate != null && child == rewardCellTemplate) continue;
            Destroy(child.gameObject);
        }

        IReadOnlyList<QuestRewardConfig> rewards = quest.Rewards;
        if (rewardSection != null) rewardSection.SetActive(rewards.Count > 0);

        foreach (QuestRewardConfig reward in rewards)
        {
            if (reward == null || reward.itemId <= 0) continue;

            RectTransform cell = Instantiate(rewardCellTemplate, rewardContent);
            cell.gameObject.SetActive(true);
            cell.name = $"Reward_{reward.itemId}";

            Image icon = cell.Find("Icon")?.GetComponent<Image>();
            if (icon != null)
            {
                icon.sprite = ItemIconLoader.Get(ItemIconKey(reward.itemId));
                icon.enabled = icon.sprite != null;
            }

            TMP_Text count = cell.Find("Count")?.GetComponent<TMP_Text>();
            if (count != null) count.SetText(reward.count > 1 ? reward.count.ToString() : string.Empty);
        }
    }

    /// <summary>物品图标：走背包配置的 iconKey（与背包格子同一套图标），拿不到就留空底框</summary>
    private static string ItemIconKey(int itemId)
    {
        BagManager bag = BagManager.Instance;
        ItemConfigInfo config = bag?.GetConfig(itemId);
        return config?.IconKey;
    }

    /// <summary>目标清单文案（全部目标 + 状态，与服务端进度一一对应）</summary>
    private static string BuildGoalList(MmQuestRuntime quests, QuestData quest)
    {
        System.Text.StringBuilder builder = new();
        IReadOnlyList<QuestGoal> goals = quest.GetGoals();

        for (int i = 0; i < goals.Count; i++)
        {
            int progress = quests.GoalProgressOf(quest.QuestId, i);
            int need = MmQuestRuntime.Need(goals[i]);
            bool done = progress >= need;

            if (i > 0) builder.Append('\n');
            string mark = done ? "✓" : "·";
            string color = done ? "#6E7178" : "#E8E8E8";
            string extra = need > 1 ? $"（{progress}/{need}）" : string.Empty;
            builder.Append($"<color={color}>{mark} {GoalTitle(goals[i])}{extra}</color>");
        }
        return builder.ToString();
    }

    // ──────────────────────────────
    // 交互
    // ──────────────────────────────

    private void SelectQuest(int questId)
    {
        selectedQuestId = questId;
        if (statusText != null) statusText.SetText(string.Empty);
        RefreshAll();
    }

    private void OnPrimaryClicked()
    {
        MmQuestRuntime quests = MmQuestRuntime.Instance;
        if (quests == null || selectedQuestId == 0) return;

        switch (primaryAction)
        {
            case PrimaryAction.Accept:
                quests.Accept(selectedQuestId);
                SetStatus("已发送接取请求");
                break;
            case PrimaryAction.Submit:
                quests.Submit(selectedQuestId);
                SetStatus("已发送提交请求");
                break;
            case PrimaryAction.Track:
                quests.Track(selectedQuestId);
                SetStatus("已开始追踪");
                break;
            case PrimaryAction.Untrack:
                quests.Track(0);
                SetStatus("已取消追踪");
                break;
        }
    }

    private void OnNavUnavailable()
    {
        SetStatus("该页签暂未开放");
    }

    private void OnCloseClicked()
    {
        if (UIManager.Instance == null) return;

        // 面板自己关自己时先把世界暂停还回去：关闭流程与后续操作都吃缩放时间，
        // 不能指望 HUD 每 0.1 秒的轮询兜底（Pop 幂等，未计入时无副作用）
        PauseManager.Pop(PauseManager.Layer.Quest);

        if (Application.isEditor)
            Debug.Log($"[QuestView] 关闭按钮已触发（isOpen={UIManager.Instance.IsOpen(UIType.QuestView)}）", this);

        UIManager.Instance.Close(UIType.QuestView).Forget();
    }

    private void SetStatus(string content)
    {
        if (statusText != null) statusText.SetText(content ?? string.Empty);
    }

    private void SetPrimary(PrimaryAction action, string label)
    {
        primaryAction = action;
        bool visible = action != PrimaryAction.None && action != PrimaryAction.Submitted;

        if (primaryButton != null)
        {
            primaryButton.gameObject.SetActive(visible);
            primaryButton.interactable = action != PrimaryAction.Submitted;
        }
        if (primaryLabel != null) primaryLabel.SetText(label);

        // 可提交时按钮描边点亮（与卡片金色下划线同一套强调色）
        if (primaryButton != null)
        {
            Image image = primaryButton.GetComponent<Image>();
            if (image != null)
                image.color = action == PrimaryAction.Submit
                    ? new Color(0.94f, 0.90f, 0.80f, 1f)
                    : new Color(0.86f, 0.86f, 0.84f, 1f);
        }
    }

    // ──────────────────────────────
    // 文案
    // ──────────────────────────────

    private static int PickDefaultQuest(MmQuestRuntime quests)
    {
        int best = 0;
        int bestRank = int.MaxValue;
        foreach (QuestData quest in quests.AllQuests)
        {
            if (quest == null) continue;
            int rank = RankOf(quests, quest.QuestId);
            if (rank >= bestRank) continue;
            bestRank = rank;
            best = quest.QuestId;
        }
        return best;
    }

    private static string SubtitleOf(MmQuestRuntime quests, QuestData quest)
    {
        string category = CategoryName(quest.Category);
        return quests.StateOf(quest.QuestId) switch
        {
            EQuestState.执行中 => quests.IsSubmittable(quest.QuestId) ? $"{category} · 可提交" : $"{category} · 进行中",
            EQuestState.可用 => $"{category} · 可接取",
            EQuestState.提交 => $"{category} · 已完成",
            EQuestState.失败 => $"{category} · 已失效",
            _ => $"{category} · 未解锁",
        };
    }

    private static string CategoryName(EQuestCategory category)
        => category == EQuestCategory.主线任务 ? "主线任务" : "支线任务";

    /// <summary>目标文案：优先用配置里的描述，没有则按类型生成</summary>
    private static string GoalTitle(QuestGoal goal)
    {
        if (goal == null) return "未知目标";
        if (!string.IsNullOrEmpty(goal.description)) return goal.description;

        return goal.type switch
        {
            EQuestGoalType.对话 => goal.dialogueGraph != null ? $"与{goal.dialogueGraph.name}对话" : "与目标人物对话",
            EQuestGoalType.击杀 => $"击败 {goal.targetKey} ×{MmQuestRuntime.Need(goal)}",
            EQuestGoalType.收集 => $"收集 {goal.targetKey} ×{MmQuestRuntime.Need(goal)}",
            EQuestGoalType.到达 => $"前往 {goal.targetKey}",
            _ => "未知目标",
        };
    }
}
