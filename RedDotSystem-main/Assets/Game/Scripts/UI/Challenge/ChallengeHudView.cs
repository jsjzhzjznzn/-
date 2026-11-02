using System;
using Cysharp.Threading.Tasks;
using SkierFramework;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 击杀竞速挑战 HUD。
/// 所有界面元素都来自 Assets/Game/UI/Challenge/ChallengeHudView.prefab，
/// 由 UIManager 打开/关闭，控件通过 ControlBinding 绑定；本类不再创建任何 UGUI 元素。
/// </summary>
public class ChallengeHudView : UIView
{
    [ControlBinding] public TMP_Text timerText;
    [ControlBinding] public Image monsterHpFill;
    [ControlBinding] public TMP_Text monsterHpText;
    [ControlBinding] public TMP_Text hintText;

    [ControlBinding] public GameObject resultPanel;
    [ControlBinding] public TMP_Text resultTitle;
    [ControlBinding] public TMP_Text resultTime;
    [ControlBinding] public TMP_Text resultRank;
    [ControlBinding] public Button retryButton;
    [ControlBinding] public Button homeButton;

    /// <summary>右上角常驻退出按钮（预制体 ExitButton，由 ChallengeHudPrefabBuilder 生成）</summary>
    [ControlBinding] public Button exitButton;

    private bool leaving;

    /// <summary>退出确认窗口（秒）：挑战是限时玩法，误触退出会作废进度，所以要点两次</summary>
    private const float ExitConfirmSeconds = 2f;

    /// <summary>第一次点退出的时刻（-1 = 未进入确认态）</summary>
    private float exitArmedAt = -1f;

    /// <summary>战斗提示文案（光标收回到战斗状态时恢复显示）</summary>
    private const string PlayHint = "左键攻击 · 右键闪避    Esc 释放鼠标    击杀怪物，用时越短排名越高";

    /// <summary>光标是否已释放给 UI（挑战用鼠标视角，锁定光标时 HUD 按钮收不到点击）</summary>
    private bool cursorReleased;

    /// <summary>
    /// Esc 释放/收回光标（与副本场景 DungeonManager 的做法一致）：
    /// 玩家在 Player.Start 里把光标锁死并隐藏，不释放就点不到退出按钮与结算按钮。
    /// </summary>
    private void Update()
    {
        if (!Input.GetKeyDown(KeyCode.Escape)) return;

        SetCursorReleased(!cursorReleased);
        if (cursorReleased)
            SetHint("光标已释放：点击「退出」返回主页，按 Esc 回到战斗");
        else if (resultPanel == null || !resultPanel.activeSelf)
            SetHint(PlayHint);
    }

    private void SetCursorReleased(bool released)
    {
        cursorReleased = released;
        Cursor.lockState = released ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = released;
    }

    public override void OnOpen(object userData)
    {
        base.OnOpen(userData);
        if (resultPanel != null) resultPanel.SetActive(false);
        if (resultRank != null) resultRank.text = string.Empty;
        // 光标锁由 Player.Start 负责，这里只重置自己的状态（重启挑战时可能残留上一次的释放态）
        cursorReleased = false;
        SetHint(PlayHint);
    }

    public override void OnClose()
    {
        // 离开挑战场景时把光标还给玩家，否则主页/设置等界面都点不动
        SetCursorReleased(true);
        base.OnClose();
    }

    public override void OnAddListener()
    {
        if (retryButton != null) retryButton.onClick.AddListener(OnRetryClicked);
        if (homeButton != null) homeButton.onClick.AddListener(OnHomeClicked);
        if (exitButton != null) exitButton.onClick.AddListener(OnExitClicked);
    }

    public override void OnRemoveListener()
    {
        if (retryButton != null) retryButton.onClick.RemoveListener(OnRetryClicked);
        if (homeButton != null) homeButton.onClick.RemoveListener(OnHomeClicked);
        if (exitButton != null) exitButton.onClick.RemoveListener(OnExitClicked);
    }

    public void SetTimer(long elapsedMilliseconds)
    {
        if (timerText != null)
            timerText.text = FormatTime(elapsedMilliseconds);
    }

    public void SetMonsterHp(float current, float max)
    {
        float safeMax = Mathf.Max(1f, max);
        float safeCurrent = Mathf.Clamp(current, 0f, safeMax);
        float ratio = safeCurrent / safeMax;

        if (monsterHpFill != null)
        {
            monsterHpFill.fillAmount = ratio;

            // 兜底：uGUI 的 Image 在 sprite == null 时走 Graphic 的默认整块四边形，
            // OnPopulateMesh 直接 return，fillAmount 会被完全忽略（血条永远是满的）。
            // 预制体一旦漏配 sprite（或换皮重建时丢了），这里用锚点宽度表现比例。
            if (monsterHpFill.sprite == null)
            {
                RectTransform rect = monsterHpFill.rectTransform;
                rect.anchorMin = new Vector2(0f, 0f);
                rect.anchorMax = new Vector2(ratio, 1f);
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
            }
        }

        if (monsterHpText != null)
            monsterHpText.text = $"怪物 HP {Mathf.CeilToInt(safeCurrent)} / {Mathf.CeilToInt(safeMax)}";
    }

    public void SetHint(string text)
    {
        if (hintText != null) hintText.text = text;
    }

    public void HideResult()
    {
        if (resultPanel != null) resultPanel.SetActive(false);
    }

    public void ShowResult(bool success, long elapsedMilliseconds)
    {
        if (resultPanel != null) resultPanel.SetActive(true);

        // 结算面板要点击「重试 / 返回主页」：战斗结束后立刻把光标还给玩家
        SetCursorReleased(true);
        SetHint("挑战结束：可点击下方按钮");

        if (resultTitle != null)
        {
            resultTitle.text = success ? "挑战成功" : "挑战失败";
            resultTitle.color = success
                ? new Color(1f, 0.82f, 0.25f)
                : new Color(1f, 0.35f, 0.35f);
        }

        if (resultTime != null)
        {
            resultTime.text = success
                ? $"击杀用时  {FormatTime(elapsedMilliseconds)}"
                : "你被怪物击败了";
        }

        if (resultRank != null)
            resultRank.text = success ? "正在结算…" : "再试一次，缩短击杀时间";
    }

    public void SetRankText(string text)
    {
        if (resultRank != null) resultRank.text = text;
    }

    /// <summary>
    /// 退出按钮：两次点击确认。第一次只提示，2 秒内再点才真正离场（回主页）；
    /// 超时后重新计时，不会因为很久以前点过一次就误退。
    /// </summary>
    private void OnExitClicked()
    {
        if (leaving) return;

        if (exitArmedAt > 0f && Time.unscaledTime - exitArmedAt <= ExitConfirmSeconds)
        {
            exitArmedAt = -1f;
            CloseAndLoadAsync("zhuye").Forget();
            return;
        }

        exitArmedAt = Time.unscaledTime;
        SetHint("再点一次「退出」返回主页（本次挑战进度不保留）");
    }

    private void OnRetryClicked() => CloseAndLoadAsync("tiaozhan").Forget();

    private void OnHomeClicked() => CloseAndLoadAsync("zhuye").Forget();

    /// <summary>毫秒 → mm:ss.mmm，挑战计时统一格式（HUD 计时与结算面板共用）</summary>
    private static string FormatTime(long elapsedMilliseconds)
    {
        TimeSpan t = TimeSpan.FromMilliseconds(Math.Max(0L, elapsedMilliseconds));
        return $"{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    private async UniTaskVoid CloseAndLoadAsync(string sceneName)
    {
        if (leaving) return;
        leaving = true;

        SetCursorReleased(true);

        if (UIManager.Instance.IsOpen(UIType.ChallengeHudView))
            await UIManager.Instance.Close(UIType.ChallengeHudView);

        Time.timeScale = 1f;
        string tip = sceneName == "zhuye" ? "正在返回主页…" : "正在进入挑战…";
        Debug.Log("[挑战] " + tip);
        await SceneLoader.LoadAsync(sceneName);
    }
}
