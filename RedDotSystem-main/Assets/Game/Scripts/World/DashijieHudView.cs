using Cysharp.Threading.Tasks;
using Common.Network;
using Miemie.DialogSystem.Quest;
using SkierFramework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 大世界 HUD（dashijie 场景常驻）：任务追踪卡 + 右上功能热键 + 底部角色血条 + 数字键换人。
///
/// 面板一律复用已注册的业务视图，HUD 只负责热键、世界暂停记账、鼠标指针与内容显隐。
/// 暂停时序：打开面板等 Open 完成后再 Push 暂停层（面板开场动画吃缩放时间）；
/// 关闭面板先 Pop 暂停层再等 Close 动画（否则 0 倍速下动画永远播不完）。
/// </summary>
public class DashijieHudView : UIView
{
    [ControlBinding] public CanvasGroup contentGroup;

    [ControlBinding] public GameObject questTrackerRoot;
    [ControlBinding] public TMP_Text questTitleText;
    [ControlBinding] public TMP_Text questGoalText;
    [ControlBinding] public TMP_Text questProgressText;
    /// <summary>任务卡按钮与卡片是同一节点（整卡可点）</summary>
    [ControlBinding("questTrackerRoot")] public Button questTrackerButton;

    [ControlBinding] public Button bagHotkeyButton;
    [ControlBinding] public Button questHotkeyButton;
    [ControlBinding] public Button characterHotkeyButton;
    [ControlBinding] public Button achievementHotkeyButton;
    [ControlBinding] public Button mailHotkeyButton;
    [ControlBinding] public Button friendHotkeyButton;
    [ControlBinding] public Button leaderboardHotkeyButton;
    [ControlBinding] public Button worldChatHotkeyButton;
    [ControlBinding] public Button settingsHotkeyButton;
    [ControlBinding] public Button mapHotkeyButton;

    /// <summary>小地图根节点（常驻，随 contentGroup 一起隐藏）</summary>
    [ControlBinding] public GameObject minimapRoot;
    /// <summary>小地图窗口：RawImage + MinimapClip 材质，底图靠 uvRect 采样</summary>
    [ControlBinding] public RawImage minimapMap;
    /// <summary>玩家朝向箭头（绕 Z 旋转）</summary>
    [ControlBinding] public RectTransform minimapArrow;
    /// <summary>任务目标标记（追踪中任务的当前目标 NPC；不可见时整节点关闭）</summary>
    [ControlBinding] public RectTransform questMarker;
    /// <summary>小地图点击入口（挂在外圈底衬上），点它打开大地图</summary>
    [ControlBinding("MinimapBackdrop")] public Button minimapButton;

    [ControlBinding] public TMP_Text playerNameText;
    [ControlBinding] public TMP_Text playerLevelText;
    [ControlBinding] public Image hpFill;
    [ControlBinding] public TMP_Text hpText;
    [ControlBinding] public TMP_Text switchHintText;

    [ControlBinding] public Button chatBubbleButton;

    /// <summary>一个热键项：按钮 + 键盘 + 目标面板 + 暂停层（Settings 由面板自己管暂停，不进这张表）</summary>
    private sealed class Hotkey
    {
        public Button Button;
        public KeyCode Key;
        public UIType Panel;
        public PauseManager.Layer Layer;
    }

    /// <summary>HUD 可能打开的全部面板（用于外部打开时也能隐藏/恢复内容）</summary>
    private static readonly UIType[] ManagedPanels =
    {
        UIType.BagView,
        UIType.QuestView,
        UIType.CharacterView,
        UIType.AchievementView,
        UIType.MailView,
        UIType.FriendView,
        UIType.LeaderboardView,
        UIType.WorldChatView,
        UIType.SettingsView,
        UIType.DashijieMapView,
    };

    /// <summary>数字键最多支持到 4 人队（1–4）</summary>
    private const int MaxSwitchKeys = 4;

    /// <summary>小地图窗口边长（px，与 builder 的 MinimapSize 一致）</summary>
    private const float MinimapWindowPx = 240f;

    /// <summary>小地图缩放：k = PixelsPerMeter × zoom = 4 × 0.55 = 2.2 px/m → 窗口显示约 109 m</summary>
    private const float MinimapZoom = 0.55f;

    /// <summary>面板状态轮询间隔（秒，非缩放时间）：面板用自身按钮关闭时兜底对齐暂停栈</summary>
    private const float PanelPollInterval = 0.1f;

    private Hotkey[] hotkeys;

    private DashijiePlayerSwitcher switcher;
    private Player boundPlayer;
    private HealthModel boundHealth;

    /// <summary>小地图底图（世界契约 + 贴图句柄）</summary>
    private DashijieMinimap minimap;

    /// <summary>任务标记数据源（场景里的任务导演；首次刷新时按需查找）</summary>
    private WorldQuestDirector questDirector;

    /// <summary>当前由 HUD 记录在案的面板（null = 没有面板）</summary>
    private UIType? activePanel;

    /// <summary>当前面板的暂停层（Settings 为 null——它自己 Push/Pop）</summary>
    private PauseManager.Layer? activeLayer;

    /// <summary>当前面板的暂停是否由 HUD 计入（SettingsView 自己管理，这里为 false）</summary>
    private bool ownsPause;

    /// <summary>开合串行化：面板动画期间不接受新的开合请求，防连点并发</summary>
    private bool busy;

    private float nextPanelPollTime;

    // ──────────────────────────────
    // 生命周期
    // ──────────────────────────────

    public override void OnOpen(object userData)
    {
        base.OnOpen(userData);

        switcher = userData as DashijiePlayerSwitcher;
        if (switcher == null) switcher = FindObjectOfType<DashijiePlayerSwitcher>();
        if (switcher != null) switcher.PlayerChanged += OnPlayerChanged;

        BindPlayer(switcher != null ? switcher.CurrentPlayer : FindObjectOfType<Player>());

        PauseManager.PausedChanged += OnPausedChanged;
        // 打开 HUD 时世界可能已经被暂停（例如直接热重载进场景），补一次门禁
        if (PauseManager.IsPaused) PlayerInputGate.Push(PlayerInputGate.Source.WorldPause);

        if (MmQuestRuntime.Instance != null) MmQuestRuntime.Instance.QuestsUpdated += RefreshQuestTracker;
        RefreshQuestTracker();
        RefreshSwitchHint();
        LoadMinimap();
        activePanel = null;
        activeLayer = null;
        ownsPause = false;
        busy = false;

        SetContentVisible(true);
        SetCursorReleased(false);
    }

    public override void OnClose()
    {
        // 先清账再走基类：暂停中关 HUD 必须把 timeScale 恢复，否则会被带去下一个场景
        RestorePauseAndGate();
        PlayerInputGate.Pop(PlayerInputGate.Source.WorldPause);

        PauseManager.PausedChanged -= OnPausedChanged;
        if (MmQuestRuntime.Instance != null) MmQuestRuntime.Instance.QuestsUpdated -= RefreshQuestTracker;
        if (switcher != null) switcher.PlayerChanged -= OnPlayerChanged;

        UnbindHealth();
        ReleaseMinimap();

        // 离开大世界（回主页等）时把光标还给 UI
        SetCursorReleased(true);

        base.OnClose();
    }

    public override void OnAddListener()
    {
        hotkeys = new[]
        {
            new Hotkey { Button = bagHotkeyButton, Key = KeyCode.B, Panel = UIType.BagView, Layer = PauseManager.Layer.Bag },
            new Hotkey { Button = questHotkeyButton, Key = KeyCode.Y, Panel = UIType.QuestView, Layer = PauseManager.Layer.Quest },
            new Hotkey { Button = characterHotkeyButton, Key = KeyCode.C, Panel = UIType.CharacterView, Layer = PauseManager.Layer.Character },
            new Hotkey { Button = achievementHotkeyButton, Key = KeyCode.V, Panel = UIType.AchievementView, Layer = PauseManager.Layer.Achievement },
            new Hotkey { Button = mailHotkeyButton, Key = KeyCode.N, Panel = UIType.MailView, Layer = PauseManager.Layer.Mail },
            new Hotkey { Button = friendHotkeyButton, Key = KeyCode.G, Panel = UIType.FriendView, Layer = PauseManager.Layer.Friend },
            new Hotkey { Button = leaderboardHotkeyButton, Key = KeyCode.L, Panel = UIType.LeaderboardView, Layer = PauseManager.Layer.Leaderboard },
            new Hotkey { Button = worldChatHotkeyButton, Key = KeyCode.T, Panel = UIType.WorldChatView, Layer = PauseManager.Layer.Chat },
            new Hotkey { Button = mapHotkeyButton, Key = KeyCode.M, Panel = UIType.DashijieMapView, Layer = PauseManager.Layer.Other },
        };

        foreach (Hotkey hotkey in hotkeys)
        {
            Hotkey captured = hotkey;
            if (captured.Button != null)
                captured.Button.onClick.AddListener(() => OnHotkeyRequested(captured));
        }

        if (settingsHotkeyButton != null) settingsHotkeyButton.onClick.AddListener(OnSettingsRequested);
        if (questTrackerButton != null) questTrackerButton.onClick.AddListener(OnQuestTrackerClicked);
        if (chatBubbleButton != null) chatBubbleButton.onClick.AddListener(OnChatBubbleClicked);
        if (minimapButton != null) minimapButton.onClick.AddListener(OnMinimapClicked);
    }

    public override void OnRemoveListener()
    {
        if (hotkeys != null)
        {
            foreach (Hotkey hotkey in hotkeys)
                if (hotkey.Button != null) hotkey.Button.onClick.RemoveAllListeners();
        }

        if (settingsHotkeyButton != null) settingsHotkeyButton.onClick.RemoveAllListeners();
        if (questTrackerButton != null) questTrackerButton.onClick.RemoveAllListeners();
        if (chatBubbleButton != null) chatBubbleButton.onClick.RemoveAllListeners();
        if (minimapButton != null) minimapButton.onClick.RemoveAllListeners();
    }

    // ──────────────────────────────
    // 热键与开关
    // ──────────────────────────────

    private void Update()
    {
        SyncPanelState();
        RefreshMinimap();

        if (busy) return;

        // Esc：面板打开时关面板；没有面板时开设置
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            if (activePanel != null) CloseActivePanelAsync().Forget();
            else OpenPanelAsync(UIType.SettingsView, null).Forget();
            return;
        }

        if (activePanel != null)
        {
            // 同一按键再按 = 关闭自己；其它键不响应，避免叠两层全屏面板
            foreach (Hotkey hotkey in hotkeys)
            {
                if (activePanel == hotkey.Panel && Input.GetKeyDown(hotkey.Key))
                {
                    CloseActivePanelAsync().Forget();
                    break;
                }
            }
            return;
        }

        foreach (Hotkey hotkey in hotkeys)
        {
            if (Input.GetKeyDown(hotkey.Key))
            {
                OpenPanelAsync(hotkey.Panel, hotkey.Layer).Forget();
                return;
            }
        }

        // 玩法态：数字键 1–4 换角色（候选数量由场景里的世界预制体决定）
        for (int i = 0; i < MaxSwitchKeys; i++)
        {
            if (Input.GetKeyDown(KeyCode.Alpha1 + i) || Input.GetKeyDown(KeyCode.Keypad1 + i))
            {
                TrySwitchCharacter(i);
                return;
            }
        }
    }

    private void OnHotkeyRequested(Hotkey hotkey)
    {
        if (busy) return;
        if (activePanel == hotkey.Panel) CloseActivePanelAsync().Forget();
        else if (activePanel == null) OpenPanelAsync(hotkey.Panel, hotkey.Layer).Forget();
    }

    private void OnSettingsRequested()
    {
        if (busy) return;
        if (activePanel == UIType.SettingsView) CloseActivePanelAsync().Forget();
        else if (activePanel == null) OpenPanelAsync(UIType.SettingsView, null).Forget();
    }

    private void OnQuestTrackerClicked()
    {
        if (busy || activePanel != null) return;
        OpenPanelAsync(UIType.QuestView, PauseManager.Layer.Quest).Forget();
    }

    private void OnChatBubbleClicked()
    {
        if (busy || activePanel != null) return;
        OpenPanelAsync(UIType.WorldChatView, PauseManager.Layer.Chat).Forget();
    }

    private void OnMinimapClicked()
    {
        if (busy || activePanel != null) return;
        OpenPanelAsync(UIType.DashijieMapView, PauseManager.Layer.Other).Forget();
    }

    private async UniTaskVoid OpenPanelAsync(UIType type, PauseManager.Layer? layer)
    {
        if (busy || UIManager.Instance == null) return;
        busy = true;

        try
        {
            SetContentVisible(false);
            SetCursorReleased(true);

            if (type == UIType.SettingsView)
            {
                // 设置面板自己 Push/Pop 暂停层；这里只负责传参
                await UIManager.Instance.Open(type, new SettingsViewArgs { pauseWorld = true, showReturnHome = true });
            }
            else
            {
                await UIManager.Instance.Open(type);
                if (layer.HasValue) PauseManager.Push(layer.Value);
            }

            activePanel = type;
            activeLayer = layer;
            ownsPause = layer.HasValue;
        }
        finally
        {
            busy = false;
        }
    }

    private async UniTaskVoid CloseActivePanelAsync()
    {
        if (busy || activePanel == null || UIManager.Instance == null) return;
        busy = true;

        UIType type = activePanel.Value;
        PauseManager.Layer? layer = ownsPause ? activeLayer : null;

        try
        {
            // 关闭动画吃缩放时间：先恢复时间，再等面板播完关闭动画
            if (layer.HasValue) PauseManager.Pop(layer.Value);

            await UIManager.Instance.Close(type);

            activePanel = null;
            activeLayer = null;
            ownsPause = false;

            SetContentVisible(true);
            SetCursorReleased(false);
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>
    /// 每 0.1 秒对齐一次面板状态：
    /// 面板被自身关闭按钮关掉时 Pop 暂停层；面板被外部打开时隐藏 HUD 内容。
    /// </summary>
    private void SyncPanelState()
    {
        if (UIManager.Instance == null) return;
        if (Time.unscaledTime < nextPanelPollTime) return;
        nextPanelPollTime = Time.unscaledTime + PanelPollInterval;

        if (activePanel == null)
        {
            UIType? external = FindOpenPanel();
            if (external == null) return;

            activePanel = external;
            activeLayer = null;
            ownsPause = false;
            SetContentVisible(false);
            SetCursorReleased(true);
            return;
        }

        if (UIManager.Instance.IsOpen(activePanel.Value)) return;

        if (ownsPause && activeLayer.HasValue) PauseManager.Pop(activeLayer.Value);

        activePanel = null;
        activeLayer = null;
        ownsPause = false;

        SetContentVisible(true);
        SetCursorReleased(false);
    }

    private static UIType? FindOpenPanel()
    {
        foreach (UIType type in ManagedPanels)
        {
            if (UIManager.Instance.IsOpen(type)) return type;
        }
        return null;
    }

    // ──────────────────────────────
    // 暂停 / 指针 / 显隐
    // ──────────────────────────────

    /// <summary>暂停状态 → 角色输入门禁（暂停只冻结时间，不拦输入沿）</summary>
    private void OnPausedChanged(bool paused)
    {
        if (paused) PlayerInputGate.Push(PlayerInputGate.Source.WorldPause);
        else PlayerInputGate.Pop(PlayerInputGate.Source.WorldPause);
    }

    /// <summary>把 HUD 记账的暂停层全部弹出（幂等；场景卸载/关 HUD 兜底）</summary>
    private void RestorePauseAndGate()
    {
        if (ownsPause && activeLayer.HasValue) PauseManager.Pop(activeLayer.Value);

        activePanel = null;
        activeLayer = null;
        ownsPause = false;

        // 面板可能被 HUD 之外的路径打开过，逐个兜底，避免暂停栈泄漏到下一个场景
        foreach (UIType type in ManagedPanels)
        {
            if (type == UIType.SettingsView) continue;
            PauseManager.Pop(LayerOf(type));
        }
    }

    private static PauseManager.Layer LayerOf(UIType type) => type switch
    {
        UIType.BagView => PauseManager.Layer.Bag,
        UIType.QuestView => PauseManager.Layer.Quest,
        UIType.CharacterView => PauseManager.Layer.Character,
        UIType.AchievementView => PauseManager.Layer.Achievement,
        UIType.MailView => PauseManager.Layer.Mail,
        UIType.FriendView => PauseManager.Layer.Friend,
        UIType.LeaderboardView => PauseManager.Layer.Leaderboard,
        UIType.WorldChatView => PauseManager.Layer.Chat,
        UIType.DashijieMapView => PauseManager.Layer.Other,
        _ => PauseManager.Layer.Other,
    };

    private void SetContentVisible(bool visible)
    {
        if (contentGroup != null)
        {
            contentGroup.alpha = visible ? 1f : 0f;
            contentGroup.blocksRaycasts = visible;
            contentGroup.interactable = visible;
        }
    }

    private void SetCursorReleased(bool released)
    {
        Cursor.lockState = released ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = released;
    }

    // ──────────────────────────────
    // 角色与血条
    // ──────────────────────────────

    private void OnPlayerChanged(Player player)
    {
        BindPlayer(player);
        RefreshSwitchHint();
    }

    private void BindPlayer(Player player)
    {
        UnbindHealth();

        boundPlayer = player;
        if (player == null)
        {
            RefreshPlayerText(0);
            SetHP(0f, 1f);
            return;
        }

        boundHealth = player.GetComponent<HealthModel>();
        if (boundHealth != null)
        {
            boundHealth.CurrentHP.OnValueChanged += OnHealthChanged;
            boundHealth.MaxHP.OnValueChanged += OnHealthChanged;
        }

        RefreshPlayerText(ResolveCharId(player));
        RefreshHP();
    }

    private void UnbindHealth()
    {
        if (boundHealth != null)
        {
            boundHealth.CurrentHP.OnValueChanged -= OnHealthChanged;
            boundHealth.MaxHP.OnValueChanged -= OnHealthChanged;
        }

        boundHealth = null;
        boundPlayer = null;
    }

    private void OnHealthChanged(float previous, float current) => RefreshHP();

    private void RefreshHP()
    {
        if (boundHealth == null)
        {
            SetHP(0f, 1f);
            return;
        }
        SetHP(boundHealth.CurrentHP.Value, boundHealth.MaxHP.Value);
    }

    private void SetHP(float current, float max)
    {
        float safeMax = Mathf.Max(1f, max);
        float safeCurrent = Mathf.Clamp(current, 0f, safeMax);
        float ratio = safeMax > 0f ? safeCurrent / safeMax : 0f;

        if (hpFill != null)
        {
            hpFill.fillAmount = ratio;

            // 兜底：uGUI 的 Image 在没有 sprite 时会忽略 fillAmount（血条永远满格）
            if (hpFill.sprite == null)
            {
                RectTransform rect = hpFill.rectTransform;
                rect.anchorMin = new Vector2(0f, 0f);
                rect.anchorMax = new Vector2(ratio, 1f);
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
            }
        }

        if (hpText != null)
            hpText.text = $"{Mathf.CeilToInt(safeCurrent)}/{Mathf.CeilToInt(safeMax)}";
    }

    private void RefreshPlayerText(int charId)
    {
        if (playerNameText != null) playerNameText.text = BattleHeroCatalog.GetDisplayName(charId);

        // 大世界没有等级养成数据源，按方案固定展示 Lv.1
        if (playerLevelText != null) playerLevelText.text = "Lv.1";
    }

    private static int ResolveCharId(Player player)
    {
        if (player == null || player.PlayerSO == null) return 0;
        return player.PlayerSO.CharacterId;
    }

    private void TrySwitchCharacter(int charId)
    {
        if (switcher == null) return;
        switcher.SwitchTo(charId);
    }

    /// <summary>底部提示：当前可切换的角色与数字键（按 BattleHeroCatalog 动态拼接）</summary>
    private void RefreshSwitchHint()
    {
        if (switchHintText == null) return;

        int count = Mathf.Min(MaxSwitchKeys, BattleHeroCatalog.Count);
        string hint = string.Empty;
        for (int i = 0; i < count; i++)
        {
            if (i > 0) hint += " · ";
            hint += $"{i + 1} {BattleHeroCatalog.GetDisplayName(i)}";
        }

        switchHintText.text = hint;
    }

    // ──────────────────────────────
    // 小地图
    // ──────────────────────────────

    private void LoadMinimap()
    {
        minimap = new DashijieMinimap();
        if (!minimap.Load())
        {
            Debug.LogWarning($"[DashijieHudView] 小地图底图缺失：Resources/{DashijieMinimap.ResourcePath}（先跑 Tools/地图/烘焙大世界小地图）");
            minimap = null;
            return;
        }

        if (minimapMap != null) minimapMap.texture = minimap.Texture;
    }

    private void ReleaseMinimap()
    {
        if (minimapMap != null) minimapMap.texture = null;
        minimap?.Destroy();
        minimap = null;
    }

    /// <summary>
    /// 每帧只改两个值：窗口的 uvRect（底图相对玩家的偏移）与箭头角度。
    /// 用 uvRect 而不是移动 1536×1536 的 quad：窗口自己就带裁剪材质，不需要 Mask/子物体裁剪。
    /// </summary>
    private void RefreshMinimap()
    {
        if (minimap == null || minimap.Texture == null || minimapMap == null) return;
        if (boundPlayer == null) return;

        Vector3 position = boundPlayer.transform.position;
        Vector2 mapPixel = DashijieMinimap.WorldToMap(new Vector2(position.x, position.z));

        // 窗口覆盖的 UV 尺寸 = 窗口像素 / (PixelsPerMeter × zoom × WorldSize)
        float uvSize = MinimapWindowPx / (DashijieMinimap.PixelsPerMeter * MinimapZoom * DashijieMinimap.WorldSize);
        float uvX = mapPixel.x / DashijieMinimap.TextureSize - uvSize * 0.5f;
        float uvY = mapPixel.y / DashijieMinimap.TextureSize - uvSize * 0.5f;
        minimapMap.uvRect = new Rect(uvX, uvY, uvSize, uvSize);

        Transform cameraTransform = boundPlayer.CameraTransform;
        float yaw = cameraTransform != null ? cameraTransform.eulerAngles.y : boundPlayer.transform.eulerAngles.y;
        if (minimapArrow != null) minimapArrow.localEulerAngles = new Vector3(0f, 0f, -yaw);

        RefreshQuestMarker(mapPixel);
    }

    /// <summary>
    /// 小地图任务标记：把追踪任务目标 NPC 的世界坐标换算到窗口内偏移。
    /// 超出窗口半径时沿方向贴边（保证"还在这边"的信息不丢），任务达标/提交后由导演返回不可见。
    /// </summary>
    private void RefreshQuestMarker(Vector2 playerPixel)
    {
        if (questMarker == null) return;

        if (questDirector == null) questDirector = FindObjectOfType<WorldQuestDirector>();

        Vector3 markerWorld = Vector3.zero;
        bool show = questDirector != null && questDirector.TryGetTrackedMarker(out markerWorld, out _);

        if (questMarker.gameObject.activeSelf != show) questMarker.gameObject.SetActive(show);
        if (!show) return;

        Vector2 markerPixel = DashijieMinimap.WorldToMap(new Vector2(markerWorld.x, markerWorld.z));
        Vector2 offset = (markerPixel - playerPixel) * (DashijieMinimap.PixelsPerMeter * MinimapZoom);

        float radius = MinimapWindowPx * 0.5f - 12f;
        if (offset.magnitude > radius) offset = offset.normalized * radius;

        questMarker.anchoredPosition = offset;
    }

    // ──────────────────────────────
    // 任务追踪
    // ──────────────────────────────

    private void RefreshQuestTracker()
    {
        if (questTrackerRoot == null) return;

        MmQuestRuntime quests = MmQuestRuntime.Instance;
        int trackedId = quests != null ? quests.ResolveTrackedQuest() : 0;
        QuestData tracked = trackedId > 0 ? quests.ConfigOf(trackedId) : null;

        if (tracked == null)
        {
            questTrackerRoot.SetActive(true);
            if (questTitleText != null) questTitleText.text = "暂无追踪任务";
            if (questGoalText != null) questGoalText.text = string.Empty;
            if (questProgressText != null) questProgressText.text = string.Empty;
            return;
        }

        questTrackerRoot.SetActive(true);
        if (questTitleText != null) questTitleText.text = tracked.Title;

        // 目标已完成 → 提示提交；否则显示当前目标与进度
        QuestGoal goal = quests.CurrentGoal(tracked.QuestId);
        if (goal == null)
        {
            if (questGoalText != null) questGoalText.text = quests.StateOf(tracked.QuestId) == EQuestState.执行中
                ? "目标已完成，可提交"
                : "已完成";
            if (questProgressText != null) questProgressText.text = string.Empty;
            return;
        }

        int index = quests.CurrentGoalIndex(tracked.QuestId);
        int need = MmQuestRuntime.Need(goal);
        if (questGoalText != null) questGoalText.text = GoalText(goal);
        if (questProgressText != null)
            questProgressText.text = need > 1 ? $"{quests.GoalProgressOf(tracked.QuestId, index)}/{need}" : string.Empty;
    }

    /// <summary>目标文案（与任务面板同口径；只在 HUD 这一处精简复述）</summary>
    private static string GoalText(QuestGoal goal)
    {
        if (goal == null) return string.Empty;
        if (!string.IsNullOrEmpty(goal.description)) return goal.description;

        return goal.type switch
        {
            EQuestGoalType.对话 => goal.dialogueGraph != null ? $"与{goal.dialogueGraph.name}对话" : "与目标人物对话",
            EQuestGoalType.击杀 => $"击败 {goal.targetKey}",
            EQuestGoalType.收集 => $"收集 {goal.targetKey}",
            EQuestGoalType.到达 => $"前往 {goal.targetKey}",
            _ => "未知目标",
        };
    }
}
