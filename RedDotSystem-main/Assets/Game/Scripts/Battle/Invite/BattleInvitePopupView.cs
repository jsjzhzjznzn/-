using Common.Network;

using Cysharp.Threading.Tasks;
using DG.Tweening;
using SkierFramework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 收到对战邀请的右下角浮卡（三角洲式）
///
/// 表现约定：
///   - 位置固定屏幕右下角，不铺全屏遮罩，卡片外的点击照常落到下层界面；
///   - 进场/退场交给框架 UIViewAnim（UIViewController 在 Open/Close 里 await），这里只处理内容；
///   - 倒计时用 DOTween 驱动：文字与进度条同一条补间，到点自动拒绝并关闭，玩家拒绝则立刻关闭。
///
/// 生命周期：BattleInviteManager 收到推送 → Open(UIType.BattleInvitePopupView, notify)
/// 关闭路径：同意成功 / 拒绝 / 超时 / 邀请失效（服务端推送）/ 手动关闭
/// </summary>
public class BattleInvitePopupView : UIView
{
    [ControlBinding] public RectTransform panel;
    [ControlBinding] public TMP_Text titleText;
    [ControlBinding] public TMP_Text fromNameText;
    [ControlBinding] public TMP_Text messageText;
    [ControlBinding] public TMP_Text countdownText;
    /// <summary>倒计时进度条填充（左对齐），按剩余比例缩 scaleX</summary>
    [ControlBinding] public RectTransform countdownFill;
    [ControlBinding] public Button acceptButton;
    [ControlBinding] public TMP_Text acceptLabel;
    [ControlBinding] public Button declineButton;
    [ControlBinding] public TMP_Text declineLabel;
    [ControlBinding] public TMP_Text statusText;

    /// <summary>倒计时补间（unscaled，暂停游戏也不停表）</summary>
    private Tweener countdownTween;

    private float totalSeconds;
    private float remainingSeconds;

    /// <summary>上次写进 TMP 的整秒数（秒数不变就不 SetText，避免每帧触发 TMP 重排）</summary>
    private int lastShownSeconds = -1;

    /// <summary>是否已点过同意（点过后等回执，不再继续倒计时）</summary>
    private bool responding;

    /// <summary>弹窗是否已进入"已结束"状态（防超时与回执双触发）</summary>
    private bool finished;

    public override void OnAddListener()
    {
        base.OnAddListener();
        if (acceptButton != null) acceptButton.onClick.AddListener(OnClickAccept);
        if (declineButton != null) declineButton.onClick.AddListener(OnClickDecline);

        if (BattleInviteManager.Instance != null)
            BattleInviteManager.Instance.Notice += OnManagerNotice;
    }

    public override void OnRemoveListener()
    {
        if (acceptButton != null) acceptButton.onClick.RemoveListener(OnClickAccept);
        if (declineButton != null) declineButton.onClick.RemoveListener(OnClickDecline);

        if (BattleInviteManager.Instance != null)
            BattleInviteManager.Instance.Notice -= OnManagerNotice;

        base.OnRemoveListener();
    }

    public override void OnOpen(object userData)
    {
        base.OnOpen(userData);

        finished = false;
        responding = false;
        SetButtonsInteractable(true);
        SetStatus(string.Empty);

        BattleInviteNotify invite = userData as BattleInviteNotify;
        if (invite == null)
        {
            // 没有数据（被误开）：直接关掉，避免留下一个空卡片
            UIManager.Instance.Close(UIType.BattleInvitePopupView).Forget();
            return;
        }

        if (titleText != null) titleText.SetText("对战邀请");
        if (fromNameText != null)
            fromNameText.SetText(string.IsNullOrEmpty(invite.FromPlayerName)
                ? $"玩家{invite.FromPlayerId}"
                : invite.FromPlayerName);
        if (messageText != null) messageText.SetText("邀请你进行 1v1 对战");

        StartCountdown(ResolveRemainingSeconds(invite.ExpireAtMs));
    }

    public override void OnClose()
    {
        StopCountdown();

        // 通知中枢：当前卡片结束，可以展示下一条排队中的邀请
        BattleInviteManager.Instance?.OnPopupClosed();
        base.OnClose();
    }

    // ──────────────────────────────
    // 倒计时（DOTween）
    // ──────────────────────────────

    private void StartCountdown(float seconds)
    {
        StopCountdown();

        totalSeconds = Mathf.Max(1f, seconds);
        remainingSeconds = totalSeconds;
        lastShownSeconds = -1;
        RefreshCountdown();

        // 一条补间同时驱动文字与进度条：避免两套计时器跑偏
        countdownTween = DOTween
            .To(() => remainingSeconds, value =>
            {
                remainingSeconds = value;
                RefreshCountdown();
            }, 0f, totalSeconds)
            .SetEase(Ease.Linear)
            .SetUpdate(true)
            .SetTarget(this)
            .OnComplete(OnCountdownElapsed);
    }

    private void StopCountdown()
    {
        if (countdownTween != null && countdownTween.IsActive())
            countdownTween.Kill();
        countdownTween = null;
    }

    private void RefreshCountdown()
    {
        if (countdownText != null)
        {
            int seconds = Mathf.Max(0, Mathf.CeilToInt(remainingSeconds));
            if (seconds != lastShownSeconds)
            {
                lastShownSeconds = seconds;
                countdownText.SetText($"剩余 {seconds} 秒");
            }
        }

        if (countdownFill != null)
        {
            float ratio = totalSeconds > 0f ? Mathf.Clamp01(remainingSeconds / totalSeconds) : 0f;
            countdownFill.localScale = new Vector3(ratio, 1f, 1f);
        }
    }

    private void OnCountdownElapsed()
    {
        countdownTween = null;
        if (finished || responding) return;

        // 本地到点先收起；服务端同时会推"已过期"，两边幂等
        finished = true;
        SetButtonsInteractable(false);
        SetStatus("邀请已过期");
        BattleInviteManager.Instance?.Decline("已超时");
    }

    // ──────────────────────────────
    // 交互
    // ──────────────────────────────

    private void OnClickAccept()
    {
        if (finished || responding) return;

        if (BattleInviteManager.Instance == null)
        {
            SetStatus("邀请服务未就绪");
            return;
        }

        responding = true;
        SetButtonsInteractable(false);
        SetStatus("正在进入房间…");
        countdownTween?.Pause();
        BattleInviteManager.Instance.Accept();
    }

    private void OnClickDecline()
    {
        if (finished || responding) return;

        finished = true;
        StopCountdown();
        SetButtonsInteractable(false);
        SetStatus("已拒绝");
        BattleInviteManager.Instance?.Decline();
    }

    private void OnManagerNotice(string message)
    {
        if (!responding || finished) return;
        if (string.IsNullOrEmpty(message)) return;

        // 同意失败（地址无效/连接失败）：恢复按钮与倒计时，让玩家还能拒绝
        responding = false;
        SetButtonsInteractable(true);
        SetStatus(message);
        countdownTween?.Play();
    }

    // ──────────────────────────────
    // 工具
    // ──────────────────────────────

    /// <summary>
    /// 由服务端过期时间折算剩余秒数
    ///
    /// 允许一定偏差：客户端与服务器时钟不同步时，宁可给一个"看起来正常"的 60s，
    /// 也不要显示负数或几小时的荒唐倒计时（真正的到期判定在服务端）。
    /// </summary>
    private static float ResolveRemainingSeconds(long expireAtMs)
    {
        if (expireAtMs <= 0) return 60f;

        long nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        double seconds = (expireAtMs - nowMs) / 1000.0;
        if (seconds <= 0 || seconds > 70) return 60f;
        return (float)seconds;
    }

    private void SetStatus(string message)
    {
        if (statusText != null) statusText.SetText(message ?? string.Empty);
    }

    private void SetButtonsInteractable(bool interactable)
    {
        if (acceptButton != null) acceptButton.interactable = interactable;
        if (declineButton != null) declineButton.interactable = interactable;
    }
}
