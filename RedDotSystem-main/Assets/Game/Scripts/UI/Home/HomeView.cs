using Cysharp.Threading.Tasks;
using SkierFramework;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 主界面入口栏（HomeView 预制体）。
/// 只做三件事：入口按钮跳转、红点（按钮上的 RedDotItem 自带订阅）、常驻不销毁。
/// 业务面板一律走 UIManager.Open，不在本类里碰任何面板对象。
/// </summary>
public class HomeView : UIView
{
    [ControlBinding] public Button friendButton;
    [ControlBinding] public Button leaderboardButton;
    [ControlBinding] public Button questButton;
    [ControlBinding] public Button achievementButton;
    [ControlBinding] public Button mailButton;
    [ControlBinding] public Button worldButton;
    [ControlBinding] public Button challengeButton;
    [ControlBinding] public Button agentButton;
    [ControlBinding] public Button battleButton;
    [ControlBinding] public Button bagButton;
    [ControlBinding] public Button activityButton;
    [ControlBinding] public Button characterButton;
    [ControlBinding] public Button settingsButton;
    /// <summary>右下角「开始冒险」主按钮（进入大世界场景 dashijie）</summary>
    [ControlBinding] public Button mainCtaButton;
    [ControlBinding] public TextMeshProUGUI playerInfoText;
    /// <summary>左上玩家名片（整枚胶囊可点，打开共鸣者影像簿）</summary>
    [ControlBinding] public Button playerInfoButton;

    public override void OnOpen(object userData)
    {
        base.OnOpen(userData);
        // 主页打开即拉起阿福悬浮球（未登录/编辑器直接播主场景也能看到；登录后保持全场景常驻）
        AgentFloatBall.NotifyHomeOpened();
        RefreshPlayerInfo();
    }

    /// <summary>左上角玩家信息（目前只有 ID 可用，昵称接口接入后在这里补）</summary>
    private void RefreshPlayerInfo()
    {
        if (playerInfoText == null) return;
        int playerId = ClientNetworkManager.Instance != null ? ClientNetworkManager.Instance.MyPlayerId : 0;
        playerInfoText.text = playerId > 0 ? $"冒险者 · ID {playerId}" : "未登录";
    }

    public override void OnAddListener()
    {
        if (friendButton != null) friendButton.onClick.AddListener(OpenFriend);
        if (leaderboardButton != null) leaderboardButton.onClick.AddListener(OpenLeaderboard);
        if (questButton != null) questButton.onClick.AddListener(OpenQuest);
        if (achievementButton != null) achievementButton.onClick.AddListener(OpenAchievement);
        if (mailButton != null) mailButton.onClick.AddListener(OpenMail);
        if (worldButton != null) worldButton.onClick.AddListener(OpenWorldChat);
        if (challengeButton != null) challengeButton.onClick.AddListener(OpenChallenge);
        if (agentButton != null) agentButton.onClick.AddListener(OpenAgent);
        if (battleButton != null) battleButton.onClick.AddListener(OpenBattleLobby);
        if (bagButton != null) bagButton.onClick.AddListener(OpenBag);
        if (activityButton != null) activityButton.onClick.AddListener(OpenActivity);
        if (characterButton != null) characterButton.onClick.AddListener(OpenCharacter);
        if (settingsButton != null) settingsButton.onClick.AddListener(OpenSettings);
        if (mainCtaButton != null) mainCtaButton.onClick.AddListener(EnterWorldScene);
        if (playerInfoButton != null) playerInfoButton.onClick.AddListener(OpenProfile);
    }

    public override void OnRemoveListener()
    {
        if (friendButton != null) friendButton.onClick.RemoveListener(OpenFriend);
        if (leaderboardButton != null) leaderboardButton.onClick.RemoveListener(OpenLeaderboard);
        if (questButton != null) questButton.onClick.RemoveListener(OpenQuest);
        if (achievementButton != null) achievementButton.onClick.RemoveListener(OpenAchievement);
        if (mailButton != null) mailButton.onClick.RemoveListener(OpenMail);
        if (worldButton != null) worldButton.onClick.RemoveListener(OpenWorldChat);
        if (challengeButton != null) challengeButton.onClick.RemoveListener(OpenChallenge);
        if (agentButton != null) agentButton.onClick.RemoveListener(OpenAgent);
        if (battleButton != null) battleButton.onClick.RemoveListener(OpenBattleLobby);
        if (bagButton != null) bagButton.onClick.RemoveListener(OpenBag);
        if (activityButton != null) activityButton.onClick.RemoveListener(OpenActivity);
        if (characterButton != null) characterButton.onClick.RemoveListener(OpenCharacter);
        if (settingsButton != null) settingsButton.onClick.RemoveListener(OpenSettings);
        if (mainCtaButton != null) mainCtaButton.onClick.RemoveListener(EnterWorldScene);
    }

    private void OpenFriend() => UIManager.Instance.Open(UIType.FriendView).Forget();

    private void OpenLeaderboard() => UIManager.Instance.Open(UIType.LeaderboardView).Forget();

    private void OpenQuest() => UIManager.Instance.Open(UIType.QuestView).Forget();

    private void OpenAchievement() => UIManager.Instance.Open(UIType.AchievementView).Forget();

    private void OpenMail() => UIManager.Instance.Open(UIType.MailView).Forget();

    private void OpenWorldChat() => UIManager.Instance.Open(UIType.WorldChatView).Forget();

    /// <summary>挑战直接进入击杀竞速场景，不再打开旧的点击挑战面板。</summary>
    private void OpenChallenge()
    {
        if (SceneManager.GetActiveScene().name == "tiaozhan") return;
        EnterChallengeAsync().Forget();
    }

    private async UniTaskVoid EnterChallengeAsync()
    {
        // 先起加载遮罩（它一进入就是全屏最上层），再关主页：遮罩不会等关闭动画，点击即有反馈
        UniTask loading = SceneLoader.LoadAsync("tiaozhan");

        if (UIManager.Instance.IsOpen(UIType.HomeView))
            UIManager.Instance.Close(UIType.HomeView).Forget();

        await loading;
    }

    private void OpenAgent() => UIManager.Instance.Open(UIType.AgentView).Forget();

    private void OpenBattleLobby() => UIManager.Instance.Open(UIType.BattleLobbyView).Forget();

    /// <summary>开始冒险：进入大世界场景（dashijie），走统一加载界面（真实进度条）</summary>
    private void EnterWorldScene()
    {
        if (SceneManager.GetActiveScene().name == "dashijie") return;
        EnterWorldAsync().Forget();
    }

    private async UniTaskVoid EnterWorldAsync()
    {
        // 点击「开始冒险」立刻出加载界面，再切场景（主页关闭动画在遮罩下面跑，不阻塞加载）
        UniTask loading = SceneLoader.LoadAsync("dashijie");

        // HomeView 常驻，会跨场景保留；进场景前先关掉，避免主页入口盖在大世界 HUD 上
        if (UIManager.Instance.IsOpen(UIType.HomeView))
            UIManager.Instance.Close(UIType.HomeView).Forget();

        await loading;
    }

    private void OpenBag() => UIManager.Instance.Open(UIType.BagView).Forget();

    /// <summary>活动：登记在框架里的普通面板（NormalLayer），内容仍由热更层现场构建</summary>
    private void OpenActivity() => UIManager.Instance.Open(UIType.ActivityView).Forget();

    /// <summary>角色页：全屏展示，与背包互斥（先关背包再打开，避免两层同时挡住主页）</summary>
    private void OpenCharacter()
    {
        if (UIManager.Instance.IsOpen(UIType.BagView))
        {
            OpenCharacterAsync().Forget();
            return;
        }
        UIManager.Instance.Open(UIType.CharacterView).Forget();
    }

    private async UniTaskVoid OpenCharacterAsync()
    {
        await UIManager.Instance.Close(UIType.BagView);
        await UIManager.Instance.Open(UIType.CharacterView);
    }

    private void OpenSettings() => UIManager.Instance.Open(UIType.SettingsView).Forget();

    /// <summary>玩家名片：打开共鸣者影像簿</summary>
    private void OpenProfile() => UIManager.Instance.Open(UIType.ProfileView).Forget();
}
