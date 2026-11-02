using Common.Network;
using Cysharp.Threading.Tasks;
using SkierFramework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 挑战面板控制器（P2 后只负责挑战玩法；排行榜已迁到 LeaderboardView / SkierFramework）
/// 点“开始挑战”→ 5 秒内狂点挑战区 → 倒计时结束自动上报成绩。
/// </summary>
public class ChallengeView : UIView
{
    [Header("挑战面板")]
    [ControlBinding]
    public Button closeButton;

    [Header("挑战区")]
    [ControlBinding]
    public Button startButton;

    [ControlBinding("StartButton/Label")]
    public TMP_Text startLabel;

    [ControlBinding]
    public Button clickZone;

    [ControlBinding("ClickZone/Label")]
    public TMP_Text clickLabel;

    [ControlBinding]
    public TMP_Text countdownText;

    [ControlBinding]
    public TMP_Text statusText;

    private const int ChallengeId = 1;
    private const float ChallengeDuration = 5f;

    private bool challenging;
    private float remainTime;
    private int clickCount;

    /// <summary>绑定完成后的一次性初始化：订阅网络回调（实例常驻，只做一次）</summary>
    public override void OnInit(UIControlData uIControlData, UIViewController controller)
    {
        base.OnInit(uIControlData, controller);

        SetClickZoneEnabled(false);
        SetStatus("点\"开始挑战\"，5 秒内狂点挑战区！");

        if (ClientNetworkManager.Instance != null)
            ClientNetworkManager.Instance.ScoreSubmitResponseReceived += OnScoreSubmitResponse;
    }

    /// <summary>每次打开重新接线（关闭时框架走 OnRemoveListener 摘掉）</summary>
    public override void OnAddListener()
    {
        if (closeButton != null) closeButton.onClick.AddListener(OnCloseClicked);
        if (startButton != null) startButton.onClick.AddListener(OnStartClicked);
        if (clickZone != null) clickZone.onClick.AddListener(OnClickZoneClicked);
    }

    public override void OnRemoveListener()
    {
        if (closeButton != null) closeButton.onClick.RemoveListener(OnCloseClicked);
        if (startButton != null) startButton.onClick.RemoveListener(OnStartClicked);
        if (clickZone != null) clickZone.onClick.RemoveListener(OnClickZoneClicked);
    }

    public override void OnOpen(object userData)
    {
        base.OnOpen(userData);
        ResetChallenge();
    }

    private void OnDestroy()
    {
        if (ClientNetworkManager.Instance != null)
            ClientNetworkManager.Instance.ScoreSubmitResponseReceived -= OnScoreSubmitResponse;
    }

    private void Update()
    {
        if (!challenging) return;

        remainTime -= Time.deltaTime;
        if (countdownText != null)
        {
            // 不要改成 TMP 的零分配 SetText("剩余 {0:F1}s", v) 重载——它不支持 F1 语法
            countdownText.SetText($"剩余 {remainTime:F1}s");
        }

        if (remainTime <= 0f) EndChallenge();
    }

    private void OnCloseClicked()
    {
        UIManager.Instance.Close(nameof(ChallengeView)).Forget();
    }

    private void OnStartClicked()
    {
        if (challenging) return;
        challenging = true;
        remainTime = ChallengeDuration;
        clickCount = 0;
        if (startLabel != null) startLabel.SetText("挑战中…");
        if (startButton != null) startButton.interactable = false;
        SetClickZoneEnabled(true);
        SetStatus("冲！");
    }

    private void OnClickZoneClicked()
    {
        if (!challenging) return;
        clickCount++;
        if (clickLabel != null) clickLabel.SetText(clickCount.ToString());
    }

    private void ResetChallenge()
    {
        challenging = false;
        clickCount = 0;
        if (countdownText != null) countdownText.SetText($"限时 {ChallengeDuration:F0}s");
        if (clickLabel != null) clickLabel.SetText("0");
        if (startLabel != null) startLabel.SetText("开始挑战");
        if (startButton != null) startButton.interactable = true;
        SetClickZoneEnabled(false);
    }

    private void EndChallenge()
    {
        challenging = false;
        remainTime = 0f;
        if (countdownText != null) countdownText.SetText("时间到！");
        SetClickZoneEnabled(false);
        if (startLabel != null) startLabel.SetText("开始挑战");
        if (startButton != null) startButton.interactable = true;

        SetStatus($"本次成绩 {clickCount} 分，上报中…");
        _ = ClientNetworkManager.Instance?.SendScoreSubmit(ChallengeId, clickCount);
    }

    private void SetClickZoneEnabled(bool enabled)
    {
        if (clickZone != null) clickZone.interactable = enabled;
        if (clickLabel != null) clickLabel.SetText(challenging ? clickCount.ToString() : "0");
    }

    private void SetStatus(string content)
    {
        if (statusText != null) statusText.SetText(content);
    }

    private void OnScoreSubmitResponse(ScoreSubmitResponse response)
    {
        if (!response.Success)
        {
            SetStatus($"上报失败：{response.Message}");
            return;
        }

        string rankPart = response.MyRank > 0 ? $"，当前第 {response.MyRank} 名" : string.Empty;
        SetStatus($"本次 {clickCount} 分 · {response.Message}{rankPart}");
    }
}
