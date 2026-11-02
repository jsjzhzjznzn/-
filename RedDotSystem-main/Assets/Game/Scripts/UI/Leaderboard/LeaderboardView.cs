using System.Collections.Generic;
using Common.Network;
using Cysharp.Threading.Tasks;
using SkierFramework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 排行榜面板（SkierFramework 版）
/// 从 zhuye 场景的 LeaderboardPanel 抽出，逻辑沿用原 ChallengeUIController 的榜单部分：
/// 打开/刷新时拉 Top100，VirtualScrollList 只渲染视口内的行，自己那行名字加“（我）”。
/// </summary>
public class LeaderboardView : UIView
{
    [ControlBinding("TitleText")] public TMP_Text titleText;
    [ControlBinding("CloseButton")] public Button closeButton;
    [ControlBinding("RefreshButton")] public Button refreshButton;
    [ControlBinding("LeaderboardList")] public ScrollRect leaderboardScroll;
    [ControlBinding("LeaderboardList/Viewport/Content")] public RectTransform listContent;
    [ControlBinding("LeaderboardList/Viewport/Content/RowTemplate")] public GameObject rowTemplate;
    [ControlBinding("MyRankText")] public TMP_Text myRankText;
    [ControlBinding("LBStatusText")] public TMP_Text leaderboardStatusText;

    private const int ChallengeId = 1;
    private const int LeaderboardTopN = 100;
    private const float RowHeight = 36f;
    private const float RowWidth = 456f;

    private readonly List<LeaderboardEntry> entries = new();
    private VirtualScrollList leaderboardVirtual;

    public override void OnInit(UIControlData uIControlData, UIViewController controller)
    {
        base.OnInit(uIControlData, controller);
        if (rowTemplate != null) rowTemplate.SetActive(false);
    }

    public override void OnAddListener()
    {
        base.OnAddListener();
        if (closeButton != null) closeButton.onClick.AddListener(OnCloseClicked);
        if (refreshButton != null) refreshButton.onClick.AddListener(RequestLeaderboard);

        SetupLeaderboardList();

        if (ClientNetworkManager.Instance != null)
            ClientNetworkManager.Instance.LeaderboardResponseReceived += OnLeaderboardResponse;
    }

    public override void OnRemoveListener()
    {
        if (closeButton != null) closeButton.onClick.RemoveListener(OnCloseClicked);
        if (refreshButton != null) refreshButton.onClick.RemoveListener(RequestLeaderboard);

        if (ClientNetworkManager.Instance != null)
            ClientNetworkManager.Instance.LeaderboardResponseReceived -= OnLeaderboardResponse;

        base.OnRemoveListener();
    }

    public override void OnOpen(object userData)
    {
        base.OnOpen(userData);
        SetLeaderboardStatus(entries.Count == 0 ? "榜单加载中…" : string.Empty);
        RequestLeaderboard();
    }

    private void OnCloseClicked()
    {
        UIManager.Instance.Close(UIType.LeaderboardView).Forget();
    }

    private void SetupLeaderboardList()
    {
        if (leaderboardScroll == null || listContent == null || rowTemplate == null) return;

        leaderboardVirtual = leaderboardScroll.GetComponent<VirtualScrollList>();
        if (leaderboardVirtual == null)
            leaderboardVirtual = leaderboardScroll.gameObject.AddComponent<VirtualScrollList>();

        leaderboardVirtual.Configure(8f, 8f, 4f);
        leaderboardVirtual.GetItemCount = () => entries.Count;
        leaderboardVirtual.GetItemHeight = _ => RowHeight;
        leaderboardVirtual.CreateItem = _ =>
        {
            RectTransform row = Instantiate(rowTemplate, listContent).GetComponent<RectTransform>();
            row.gameObject.SetActive(true);
            row.gameObject.AddComponent<LeaderboardRowRefs>().Resolve(row);
            return row;
        };
        leaderboardVirtual.BindItem = BindLeaderboardRow;
    }

    private sealed class LeaderboardRowRefs : MonoBehaviour
    {
        public TMP_Text Rank;
        public TMP_Text Name;
        public TMP_Text Score;

        public void Resolve(RectTransform row)
        {
            Rank = row.Find("RankText")?.GetComponent<TMP_Text>();
            Name = row.Find("NameText")?.GetComponent<TMP_Text>();
            Score = row.Find("ScoreText")?.GetComponent<TMP_Text>();
        }
    }

    private static LeaderboardRowRefs GetRowRefs(RectTransform row)
    {
        LeaderboardRowRefs refs = row.GetComponent<LeaderboardRowRefs>();
        if (refs == null)
        {
            refs = row.gameObject.AddComponent<LeaderboardRowRefs>();
            refs.Resolve(row);
        }
        return refs;
    }

    private void BindLeaderboardRow(int index, RectTransform row)
    {
        LeaderboardEntry entry = entries[index];
        row.sizeDelta = new Vector2(RowWidth, RowHeight);
        row.anchoredPosition = new Vector2(0f, row.anchoredPosition.y);

        int myId = ClientNetworkManager.Instance != null ? ClientNetworkManager.Instance.MyPlayerId : 0;
        string name = entry.Name;
        if (myId != 0 && entry.PlayerId == myId) name += "（我）";

        LeaderboardRowRefs refs = GetRowRefs(row);
        if (refs.Rank != null) refs.Rank.SetText($"{entry.Rank}.");
        if (refs.Name != null) refs.Name.SetText(name);
        if (refs.Score != null) refs.Score.SetText(entry.Score.ToString());
    }

    private void RequestLeaderboard()
    {
        if (ClientNetworkManager.Instance == null) return;
        SetLeaderboardStatus(entries.Count == 0 ? "榜单加载中…" : string.Empty);
        _ = ClientNetworkManager.Instance.SendLeaderboardRequest(ChallengeId, LeaderboardTopN);
    }

    private void OnLeaderboardResponse(LeaderboardResponse response)
    {
        if (!response.Success)
        {
            SetLeaderboardStatus(response.Message);
            return;
        }

        entries.Clear();
        entries.AddRange(response.Entries);
        leaderboardVirtual?.RefreshData();
        leaderboardVirtual?.ScrollToTop();

        SetLeaderboardStatus(entries.Count == 0 ? "还没有人上榜，快来挑战！" : string.Empty);

        if (myRankText != null)
        {
            myRankText.SetText(response.MyRank > 0
                ? $"我的排名：第 {response.MyRank} 名 · 最高 {response.MyScore} 分"
                : "我的排名：未上榜 · 快去挑战吧！");
        }
    }

    private void SetLeaderboardStatus(string content)
    {
        if (leaderboardStatusText != null) leaderboardStatusText.SetText(content);
    }
}
