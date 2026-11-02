using System.Collections.Generic;
using System.Text;

using Common.Network;

using Cysharp.Threading.Tasks;

using SkierFramework;

using TMPro;

using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 成就界面（框架面板）
/// 布局：深色 HUD 面板——顶部标题+汇总、右上关闭键；左列按系列分组的虚拟列表；
/// 右列成就详情（条件/奖励/领取）；底部状态栏；支持"全部领取"。
/// 数据全部来自 AchievementManager（服务端权威快照），本类只做渲染与交互转发。
/// </summary>
public class AchievementView : UIView
{
    // ──────────────────────────────
    // 序列化引用（预制体构建器注入）
    // ──────────────────────────────

    [Header("面板")]
    [ControlBinding]
    public Button closeButton;

    [Header("顶部")]
    [ControlBinding]
    public TMP_Text titleText;

    [ControlBinding]
    public Button claimAllButton;

    [ControlBinding("ClaimAllButton/Label")]
    public TMP_Text claimAllText;

    [Header("左列：成就列表")]
    [ControlBinding]
    public ScrollRect achievementScroll;

    [ControlBinding("AchievementScroll/Viewport/Content")]
    public RectTransform listContent;

    [ControlBinding("AchievementScroll/Viewport/Content/RowTemplate")]
    public RectTransform rowTemplate;

    [Header("右列：成就详情")]
    [ControlBinding]
    public TMP_Text detailTitle;

    [ControlBinding]
    public TMP_Text detailMeta;

    [ControlBinding]
    public TMP_Text detailDesc;

    [ControlBinding]
    public TMP_Text detailConditions;

    [ControlBinding]
    public TMP_Text detailReward;

    [ControlBinding]
    public Button claimButton;

    [ControlBinding("ClaimButton/Label")]
    public TMP_Text claimButtonText;

    [Header("底部提示")]
    [ControlBinding]
    public TMP_Text statusText;

    [Header("空态/加载")]
    [ControlBinding]
    public TMP_Text emptyText;

    [Header("视觉参数")]

    /// <summary>成就行高</summary>
    public float rowHeight = 64f;

    /// <summary>系列段头行高</summary>
    public float headerHeight = 28f;

    /// <summary>列表行宽（滚动区宽 - 滚动条与留白）</summary>
    public float rowWidth = 360f;

    /// <summary>普通行底色</summary>
    public Color rowColor = new(0.180f, 0.200f, 0.239f, 1f);

    /// <summary>选中行底色</summary>
    public Color rowSelectedColor = new(0.227f, 0.259f, 0.314f, 1f);

    // ──────────────────────────────
    // 私有字段
    // ──────────────────────────────

    private VirtualScrollList achievementVirtual;

    private sealed class RowEntry
    {
        public bool IsHeader;
        public string Series;
        public int AchievementId;
    }

    private readonly List<RowEntry> visibleRows = new();
    private int selectedAchievementId;

    private static readonly Color TextMain = new(0.878f, 0.878f, 0.878f, 1f);
    private static readonly Color TextDim = new(0.541f, 0.561f, 0.588f, 1f);
    private static readonly Color Accent = new(0.498f, 0.698f, 0.851f, 1f);
    private static readonly Color Gold = new(0.910f, 0.769f, 0.416f, 1f);

    // ──────────────────────────────
    // 生命周期
    // ──────────────────────────────

    private void Awake()
    {
        if (rowTemplate != null) rowTemplate.gameObject.SetActive(false);
    }

    public override void OnInit(UIControlData uIControlData, UIViewController controller)
    {
        base.OnInit(uIControlData, controller);

        if (AchievementManager.Instance != null)
        {
            AchievementManager.Instance.AchievementsUpdated += RefreshAll;
            AchievementManager.Instance.StatusChanged += SetStatus;
        }

        SetupAchievementList();
    }

    public override void OnAddListener()
    {
        if (closeButton != null) closeButton.onClick.AddListener(OnCloseClicked);
        if (claimButton != null) claimButton.onClick.AddListener(OnClaimClicked);
        if (claimAllButton != null) claimAllButton.onClick.AddListener(OnClaimAllClicked);
    }

    public override void OnRemoveListener()
    {
        if (closeButton != null) closeButton.onClick.RemoveListener(OnCloseClicked);
        if (claimButton != null) claimButton.onClick.RemoveListener(OnClaimClicked);
        if (claimAllButton != null) claimAllButton.onClick.RemoveListener(OnClaimAllClicked);
    }

    public override void OnOpen(object userData)
    {
        base.OnOpen(userData);

        SetStatus(string.Empty);
        RefreshAll();
        AchievementManager.Instance?.RequestAchievements();
    }

    private void OnDestroy()
    {
        if (AchievementManager.Instance == null) return;
        AchievementManager.Instance.AchievementsUpdated -= RefreshAll;
        AchievementManager.Instance.StatusChanged -= SetStatus;
    }

    private void OnCloseClicked()
    {
        UIManager.Instance.Close(UIType.AchievementView).Forget();
    }

    // ──────────────────────────────
    // 虚拟列表
    // ──────────────────────────────

    private void SetupAchievementList()
    {
        if (achievementScroll == null || listContent == null || rowTemplate == null) return;

        achievementVirtual = achievementScroll.GetComponent<VirtualScrollList>();
        if (achievementVirtual == null)
        {
            achievementVirtual = achievementScroll.gameObject.AddComponent<VirtualScrollList>();
        }
        achievementVirtual.Configure(8f, 8f, 4f);
        achievementVirtual.GetItemCount = () => visibleRows.Count;
        achievementVirtual.GetItemHeight = index =>
            visibleRows.Count > index && visibleRows[index].IsHeader ? headerHeight : rowHeight;
        achievementVirtual.CreateItem = _ =>
        {
            RectTransform row = Instantiate(rowTemplate, listContent);
            row.gameObject.SetActive(true);
            return row;
        };
        achievementVirtual.BindItem = BindAchievementRow;
    }

    private void BindAchievementRow(int index, RectTransform row)
    {
        AchievementManager manager = AchievementManager.Instance;
        if (manager == null || index < 0 || index >= visibleRows.Count) return;
        RowEntry entry = visibleRows[index];

        row.sizeDelta = new Vector2(rowWidth, entry.IsHeader ? headerHeight : rowHeight);
        row.anchoredPosition = new Vector2(0f, row.anchoredPosition.y);

        Image background = row.GetComponent<Image>();
        Button button = row.GetComponent<Button>();
        TMP_Text name = row.Find("NameText")?.GetComponent<TMP_Text>();
        TMP_Text progress = row.Find("ProgressText")?.GetComponent<TMP_Text>();
        TMP_Text state = row.Find("StateText")?.GetComponent<TMP_Text>();
        TMP_Text points = row.Find("PointsText")?.GetComponent<TMP_Text>();
        Image badge = row.Find("Badge")?.GetComponent<Image>();
        TMP_Text badgeText = row.Find("Badge/BadgeText")?.GetComponent<TMP_Text>();
        RectTransform separator = row.Find("Separator") as RectTransform;

        if (progress != null) progress.gameObject.SetActive(!entry.IsHeader);
        if (state != null) state.gameObject.SetActive(!entry.IsHeader);
        if (points != null) points.gameObject.SetActive(!entry.IsHeader);
        if (badge != null) badge.gameObject.SetActive(!entry.IsHeader);
        if (separator != null) separator.gameObject.SetActive(!entry.IsHeader);

        if (entry.IsHeader)
        {
            if (background != null) background.color = Color.clear;
            if (button != null) button.enabled = false;
            if (name != null)
            {
                name.fontSize = 15;
                name.color = TextDim;
                name.SetText($"<b>{entry.Series}</b>");
            }
            return;
        }

        AchievementCfgInfo config = manager.GetConfig(entry.AchievementId);
        if (config == null) return;

        int stateValue = manager.StateOf(config.AchievementId);
        bool lockedHidden = config.Hidden && stateValue < 2;
        if (background != null)
        {
            background.color = config.AchievementId == selectedAchievementId ? rowSelectedColor : rowColor;
        }
        if (button != null)
        {
            button.enabled = !lockedHidden;
            int targetId = config.AchievementId;
            button.onClick.RemoveAllListeners();
            if (!lockedHidden) button.onClick.AddListener(() => SelectAchievement(targetId));
        }
        if (name != null)
        {
            name.fontSize = 16;
            name.color = lockedHidden ? TextDim : TextMain;
            name.SetText(lockedHidden ? "？？？" : config.Name);
        }
        if (progress != null)
        {
            progress.color = TextDim;
            int target = manager.TargetOf(config.AchievementId);
            progress.SetText($"{manager.DisplayProgress(config.AchievementId)}/{target}");
        }
        if (state != null)
        {
            state.color = stateValue == 2 ? Accent : stateValue == 3 ? TextDim : TextMain;
            state.SetText(StateLabel(stateValue));
        }
        if (points != null)
        {
            points.color = Gold;
            points.SetText($"+{config.Points}");
        }
        if (badge != null)
        {
            badge.color = RarityColor(config.Rarity);
        }
        if (badgeText != null)
        {
            badgeText.SetText(lockedHidden ? "?" : FirstChar(config.Name));
        }
    }

    // ──────────────────────────────
    // 渲染
    // ──────────────────────────────

    private void RefreshAll()
    {
        AchievementManager manager = AchievementManager.Instance;
        if (manager == null) return;

        RebuildVisibleRows(manager);

        AchievementSummary summary = manager.Summary;
        if (titleText != null && summary != null)
        {
            titleText.SetText(
                $"成就　<size=15><color=#8A8F96>成就点 {summary.Points}/{summary.TotalPoints}" +
                $" · 已解锁 {summary.Unlocked}/{summary.Total} · 可领取 {summary.Claimable}</color></size>");
        }

        if (claimAllButton != null)
        {
            bool canClaim = manager.ClaimableCount > 0;
            claimAllButton.interactable = canClaim;
            if (claimAllText != null)
            {
                claimAllText.SetText(canClaim ? $"全部领取（{manager.ClaimableCount}）" : "全部领取");
            }
        }

        if (emptyText != null)
        {
            if (manager.IsLoading && !manager.IsLoaded)
            {
                emptyText.gameObject.SetActive(true);
                emptyText.SetText("读取中…");
            }
            else if (visibleRows.Count == 0)
            {
                emptyText.gameObject.SetActive(true);
                emptyText.SetText("暂无成就");
            }
            else
            {
                emptyText.gameObject.SetActive(false);
            }
        }

        achievementVirtual?.RefreshData();
        RefreshDetail(manager);
    }

    private void RebuildVisibleRows(AchievementManager manager)
    {
        visibleRows.Clear();

        // 按服务端下发的系列顺序分组；组内：可领取 → 进行中 → 已领取
        var groups = new Dictionary<string, List<AchievementCfgInfo>>();
        var seriesOrder = new List<string>();
        foreach (AchievementCfgInfo config in manager.Configs)
        {
            if (config == null) continue;
            string series = string.IsNullOrEmpty(config.Series) ? "其他" : config.Series;
            if (!groups.TryGetValue(series, out List<AchievementCfgInfo> list))
            {
                list = new List<AchievementCfgInfo>();
                groups[series] = list;
                seriesOrder.Add(series);
            }
            list.Add(config);
        }

        foreach (string series in seriesOrder)
        {
            List<AchievementCfgInfo> list = groups[series];
            list.Sort((left, right) =>
            {
                int rankLeft = RankOf(manager, left.AchievementId);
                int rankRight = RankOf(manager, right.AchievementId);
                if (rankLeft != rankRight) return rankLeft.CompareTo(rankRight);
                if (left.Sort != right.Sort) return left.Sort.CompareTo(right.Sort);
                return left.AchievementId.CompareTo(right.AchievementId);
            });

            visibleRows.Add(new RowEntry { IsHeader = true, Series = series });
            foreach (AchievementCfgInfo config in list)
            {
                visibleRows.Add(new RowEntry { AchievementId = config.AchievementId });
            }
        }

        if (selectedAchievementId == 0 || manager.GetConfig(selectedAchievementId) == null)
        {
            foreach (RowEntry entry in visibleRows)
            {
                if (entry.IsHeader) continue;
                AchievementCfgInfo config = manager.GetConfig(entry.AchievementId);
                if (config == null) continue;
                if (config.Hidden && manager.StateOf(config.AchievementId) < 2) continue;
                selectedAchievementId = config.AchievementId;
                break;
            }
        }
    }

    private static int RankOf(AchievementManager manager, int achievementId)
    {
        return manager.StateOf(achievementId) switch
        {
            2 => 0,   // 可领取：最该处理
            1 => 1,   // 进行中
            3 => 3,   // 已领取
            _ => 2,   // 未激活/锁定
        };
    }

    private void RefreshDetail(AchievementManager manager)
    {
        AchievementCfgInfo config = manager.GetConfig(selectedAchievementId);
        bool valid = config != null && !(config.Hidden && manager.StateOf(config.AchievementId) < 2);

        if (detailTitle != null)
        {
            detailTitle.color = valid ? TextMain : TextDim;
            detailTitle.SetText(valid ? config.Name : "？？？");
        }
        if (detailMeta != null)
        {
            detailMeta.SetText(valid
                ? $"{config.Series}　·　{RarityName(config.Rarity)}　·　成就点 +{config.Points}"
                : "达成后揭晓");
        }
        if (detailDesc != null)
        {
            detailDesc.SetText(valid ? config.Description : "这是一个隐藏成就，解锁前不显示具体内容。");
        }
        if (detailConditions != null)
        {
            detailConditions.SetText(valid ? ConditionsText(config) : "？？？");
        }
        if (detailReward != null)
        {
            detailReward.SetText(valid ? RewardText(config) : "奖励：？？？");
        }

        bool claimable = valid && manager.StateOf(config.AchievementId) == 2;
        if (claimButton != null)
        {
            claimButton.gameObject.SetActive(true);
            claimButton.interactable = claimable && !manager.IsClaiming(config.AchievementId);
        }
        if (claimButtonText != null)
        {
            int state = valid ? manager.StateOf(config.AchievementId) : 0;
            claimButtonText.SetText(state == 3 ? "已领取" : state == 2 ? "领取奖励" : "尚未解锁");
        }
    }

    private static string ConditionsText(AchievementCfgInfo config)
    {
        if (config.Conditions.Count == 0) return "条件：无";
        var builder = new StringBuilder();
        foreach (AchievementConditionInfo condition in config.Conditions)
        {
            bool met = condition.Current >= condition.Target;
            string mark = met ? "✓" : "○";
            string progress = condition.ConditionType == 5
                ? string.Empty
                : $"　{Mathf.Min(condition.Current, condition.Target)}/{condition.Target}";
            builder.AppendLine($"{mark} {condition.Text}{progress}");
        }
        return builder.ToString().TrimEnd();
    }

    private static string RewardText(AchievementCfgInfo config)
    {
        if (config.Rewards.Count == 0) return "奖励：无";
        var builder = new StringBuilder("奖励：");
        for (int i = 0; i < config.Rewards.Count; i++)
        {
            if (i > 0) builder.Append("、");
            builder.Append($"{config.Rewards[i].Name} x{config.Rewards[i].Count}");
        }
        return builder.ToString();
    }

    // ──────────────────────────────
    // 交互
    // ──────────────────────────────

    private void SelectAchievement(int achievementId)
    {
        selectedAchievementId = achievementId;
        achievementVirtual?.RefreshView();
        AchievementManager manager = AchievementManager.Instance;
        if (manager != null) RefreshDetail(manager);
    }

    private void OnClaimClicked()
    {
        int id = selectedAchievementId;
        if (id <= 0) return;
        AchievementManager.Instance?.Claim(id);
    }

    private void OnClaimAllClicked()
    {
        AchievementManager.Instance?.ClaimAll();
    }

    private void SetStatus(string message)
    {
        if (statusText != null)
        {
            statusText.SetText(message ?? string.Empty);
        }
    }

    // ──────────────────────────────
    // 文案/配色
    // ──────────────────────────────

    private static string StateLabel(int state) => state switch
    {
        2 => "可领取",
        3 => "已领取",
        1 => "进行中",
        _ => "未激活",
    };

    private static string RarityName(int rarity) => rarity switch
    {
        4 => "白金",
        3 => "金",
        2 => "银",
        _ => "铜",
    };

    private static Color RarityColor(int rarity) => rarity switch
    {
        4 => new Color(0.62f, 0.78f, 0.95f, 1f),
        3 => new Color(0.91f, 0.77f, 0.42f, 1f),
        2 => new Color(0.72f, 0.75f, 0.80f, 1f),
        _ => new Color(0.66f, 0.49f, 0.35f, 1f),
    };

    private static string FirstChar(string text)
    {
        if (string.IsNullOrEmpty(text)) return "?";
        return text.Substring(0, 1);
    }
}
