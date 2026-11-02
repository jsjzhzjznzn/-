using System;
using System.Collections;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Cysharp.Threading.Tasks;
using SkierFramework;
using TMPro;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 对战房间（移植自 compete 的 room）：
///   选英雄 -> 本地 3D 预览 + 上报服务端 -> 对手实时看到换人；
///   准备 -> 双方就绪 -> 服务端加载战斗场景（P3 阶段场景名留空，只保持就绪）。
///
/// 未连接网络时保留单机调试路径：可以选人、看预览、切准备状态，只是不同步。
/// </summary>
public class BattleRoomView : UIView
{
    [ControlBinding] public RectTransform panel;
    [ControlBinding] public TMP_Text titleText;
    [ControlBinding] public TMP_Text ipText;
    [ControlBinding] public Button closeButton;

    [ControlBinding] public RectTransform selfCard;
    [ControlBinding] public RawImage selfPreview;
    [ControlBinding] public TMP_Text selfNameText;
    [ControlBinding] public TMP_Text selfPrepareText;
    [ControlBinding] public TMP_Text selfEmptyText;

    [ControlBinding] public RectTransform enemyCard;
    [ControlBinding] public RawImage enemyPreview;
    [ControlBinding] public TMP_Text enemyNameText;
    [ControlBinding] public TMP_Text enemyPlayerText;
    [ControlBinding] public TMP_Text enemyPrepareText;
    [ControlBinding] public TMP_Text enemyEmptyText;

    [ControlBinding] public Button inviteButton;

    [ControlBinding] public Button anbiButton;
    [ControlBinding] public Button ellenButton;
    [ControlBinding] public Button readyButton;
    [ControlBinding] public TMP_Text readyButtonLabel;
    [ControlBinding] public Button startButton;
    [ControlBinding] public TMP_Text startButtonLabel;
    [ControlBinding] public Button exitButton;
    [ControlBinding] public TMP_Text statusText;

    [ControlBinding] public CanvasGroup toastGroup;
    [ControlBinding] public TMP_Text toastText;

    private int _selectedCharId = -1;
    private bool _isReady;
    private int _enemyCharId = -1;
    private bool _enemyReady;
    private Coroutine _waitForConnectRoutine;
    private Coroutine _toastRoutine;

    private static readonly Color HeroNormalColor = new Color(0.996f, 0.988f, 0.965f, 1f);
    private static readonly Color HeroSelectedColor = new Color(0.925f, 0.835f, 0.647f, 1f);
    private static readonly Color GoldNormal = new Color(0.788f, 0.647f, 0.392f, 0.45f);
    private static readonly Color GoldSelected = new Color(0.788f, 0.647f, 0.392f, 1f);

    private NetworkManager Nm => BattleNetworkLauncher.NetworkManager;
    private bool IsOnline => Nm != null && Nm.IsConnectedClient;

    public override void OnInit(UIControlData uIControlData, UIViewController controller)
    {
        base.OnInit(uIControlData, controller);
        if (toastGroup != null)
        {
            toastGroup.alpha = 0f;
            toastGroup.blocksRaycasts = false;
            toastGroup.interactable = false;
        }
    }

    public override void OnAddListener()
    {
        base.OnAddListener();
        if (anbiButton != null) anbiButton.onClick.AddListener(OnClickAnbi);
        if (ellenButton != null) ellenButton.onClick.AddListener(OnClickEllen);
        if (readyButton != null) readyButton.onClick.AddListener(OnClickReady);
        if (startButton != null) startButton.onClick.AddListener(OnClickStart);
        if (exitButton != null) exitButton.onClick.AddListener(OnClickExit);
        if (closeButton != null) closeButton.onClick.AddListener(OnClickExit);
        if (inviteButton != null) inviteButton.onClick.AddListener(OnClickInvite);

        if (RoomState.Instance != null)
        {
            RoomState.Instance.OnPeerCharIdChanged += OnPeerCharIdChanged;
            RoomState.Instance.OnPeerReadyChanged += OnPeerReadyChanged;
            RoomState.Instance.OnPlayerInfoChanged += RefreshEnemyPlayerName;
        }

        if (Nm != null)
            Nm.OnClientDisconnectCallback += OnClientDisconnected;
    }

    public override void OnRemoveListener()
    {
        if (anbiButton != null) anbiButton.onClick.RemoveListener(OnClickAnbi);
        if (ellenButton != null) ellenButton.onClick.RemoveListener(OnClickEllen);
        if (readyButton != null) readyButton.onClick.RemoveListener(OnClickReady);
        if (startButton != null) startButton.onClick.RemoveListener(OnClickStart);
        if (exitButton != null) exitButton.onClick.RemoveListener(OnClickExit);
        if (closeButton != null) closeButton.onClick.RemoveListener(OnClickExit);
        if (inviteButton != null) inviteButton.onClick.RemoveListener(OnClickInvite);

        if (RoomState.Instance != null)
        {
            RoomState.Instance.OnPeerCharIdChanged -= OnPeerCharIdChanged;
            RoomState.Instance.OnPeerReadyChanged -= OnPeerReadyChanged;
            RoomState.Instance.OnPlayerInfoChanged -= RefreshEnemyPlayerName;
        }

        if (Nm != null)
            Nm.OnClientDisconnectCallback -= OnClientDisconnected;

        if (_waitForConnectRoutine != null)
        {
            StopCoroutine(_waitForConnectRoutine);
            _waitForConnectRoutine = null;
        }
        if (_toastRoutine != null)
        {
            StopCoroutine(_toastRoutine);
            _toastRoutine = null;
        }

        base.OnRemoveListener();
    }

    public override void OnOpen(object userData)
    {
        base.OnOpen(userData);

        // 全屏房间会压住左上角阿福悬浮球，进入房间时临时隐藏
        AgentFloatBall.NotifyBattleRoomOpen(true);

        _selectedCharId = -1;
        _isReady = false;
        _enemyCharId = -1;
        _enemyReady = false;

        UnloadPreview(selfPreview);
        UnloadPreview(enemyPreview);
        if (selfPreview != null) selfPreview.gameObject.SetActive(false);
        if (enemyPreview != null) enemyPreview.gameObject.SetActive(false);
        if (selfEmptyText != null) selfEmptyText.gameObject.SetActive(true);
        if (enemyEmptyText != null) enemyEmptyText.gameObject.SetActive(true);

        UpdateSelfName();
        UpdateEnemyName();
        RefreshEnemyPlayerName();
        UpdateSelfPrepareText();
        UpdateEnemyPrepareText();
        RefreshReadyUI();
        RefreshHeroButtons();
        RefreshStartButton();
        RefreshInviteButton();
        HideToast();

        // 刚发起连接时 IsOnline 还是 false：用 HasSession 区分"正在连房主"和"真的单机"
        bool connecting = !IsOnline && BattleNetworkLauncher.HasSession;

        if (ipText != null)
        {
            if (BattleNetworkLauncher.IsHost)
                ipText.SetText($"房主 IP：{GetLocalIPv4()}:{BattleNetworkLauncher.DefaultPort}");
            else if (IsOnline)
                ipText.SetText("已加入房间");
            else if (connecting)
                ipText.SetText($"正在连接房主 {BattleNetworkLauncher.DefaultAddress}:{BattleNetworkLauncher.DefaultPort} …");
            else
                ipText.SetText("未连接网络 · 单机调试");
        }

        SetStatus(IsOnline
            ? (BattleNetworkLauncher.IsHost ? "你是房主（服务端）" : "已连接房主")
            : (connecting ? "正在连接房主…" : "未连接 · 选人与准备仅在本地生效"));

        RequestPeerChar();

        if (Nm != null && !Nm.IsConnectedClient)
            _waitForConnectRoutine = StartCoroutine(WaitConnectedAndRequestPeer());
    }

    public override void OnClose()
    {
        UnloadPreview(selfPreview);
        UnloadPreview(enemyPreview);

        bool battleTransition = RoomState.Instance != null && RoomState.Instance.IsLoadingBattleScene;
        if (!battleTransition)
        {
            BattleNetworkLauncher.ShutdownAndDestroy();
            BattleLobbyView.NotifySessionClosed();
        }

        AgentFloatBall.NotifyBattleRoomOpen(false);

        base.OnClose();
    }

    // ---------------- 英雄选择 ----------------

    private void OnClickAnbi()
    {
        SelectHero(BattleHeroCatalog.Anbi);
    }

    private void OnClickEllen()
    {
        SelectHero(BattleHeroCatalog.Ellen);
    }

    private void SelectHero(int charId)
    {
        _selectedCharId = charId;
        _isReady = false;
        RefreshReadyUI();
        RefreshStartButton();
        RefreshHeroButtons();
        UpdateSelfName();
        LoadHeroPreview(selfPreview, charId);
        if (selfEmptyText != null) selfEmptyText.gameObject.SetActive(false);
        SubmitSelectChar(charId);
    }

    private void LoadHeroPreview(RawImage target, int charId)
    {
        if (target == null) return;

        // 同一个预览框只允许站一个角色：换人先清掉上一个（否则两次点击会叠两个人）
        UnloadPreview(target);

        var hero = BattleHeroCatalog.Get(charId);
        target.gameObject.SetActive(true);

        // 预览预制体放在 Resources 下（不走 YooAsset：预制体里引用的 FBX 依赖不在收集范围内）
        GameObject prefab = Resources.Load<GameObject>(hero.PreviewPath);
        if (prefab == null)
        {
            Debug.LogError("[BattleRoom] 预览预制体缺失：" + hero.PreviewPath);
            ShowToast("预览资源缺失：" + hero.DisplayName);
            return;
        }
        GameObject model = Instantiate(prefab);

        UIModelManager.Instance.LoadModelToRawImage(
            model,
            target,
            canDrag: true,
            offset: new Vector3(0f, hero.PreviewOffsetY, 0f),
            rot: Quaternion.identity,
            scale: Vector3.one,
            isOrth: true,
            orthSizeOrFOV: hero.PreviewOrthSize
        );
    }

    /// <summary>
    /// 预览模型是我们自己 Instantiate 的（Resources，不走 ResourceManager 池），
    /// 所以要先自己销毁实例，再让 UIModelManager 只清目标表（recycleGo:false 避免"不是本模块实例化"警告与泄漏）。
    /// </summary>
    private static void UnloadPreview(RawImage target)
    {
        if (target == null) return;

        var renderToTexture = target.GetComponent<UIRenderToTexture>();
        if (renderToTexture != null && renderToTexture.Targets != null)
        {
            foreach (var kv in renderToTexture.Targets)
            {
                var go = kv.Value.gameObject;
                if (go != null) UnityEngine.Object.Destroy(go);
            }
        }
        UIModelManager.Instance.UnLoadModelByRawImage(target, false);
    }

    private void SubmitSelectChar(int charId)
    {
        var nm = Nm;
        if (nm == null || !nm.IsConnectedClient)
        {
            ShowToast("单机调试：仅本地预览");
            return;
        }

        if (nm.IsServer)
        {
            if (RoomState.Instance != null)
            {
                RoomState.Instance.ServerSetCharId(nm.LocalClientId, charId);
                // 房主自己也是服务端：装备快照直接写进房间状态，不走网络消息
                RoomState.Instance.ServerSetEquipSnapshot(nm.LocalClientId, charId);
            }
        }
        else
        {
            using var writer = new FastBufferWriter(4, Allocator.Temp);
            writer.WriteValueSafe(charId);
            nm.CustomMessagingManager.SendNamedMessage(RoomState.MsgSelectChar, NetworkManager.ServerClientId, writer);
            RoomState.Instance?.SendEquipSnapshot(charId);
        }
    }

    // ---------------- 对手同步 ----------------

    private void OnPeerCharIdChanged(ulong clientId, int charId)
    {
        var nm = Nm;
        if (nm != null && clientId == nm.LocalClientId) return;
        UpdateEnemyPreview(charId);
        if (charId >= 0)
            ShowToast($"对手选择了 {BattleHeroCatalog.GetDisplayName(charId)}");
    }

    private void OnPeerReadyChanged(ulong clientId, bool isReady)
    {
        var nm = Nm;
        if (nm != null && clientId == nm.LocalClientId) return;
        _enemyReady = isReady;
        UpdateEnemyPrepareText();
        RefreshStartButton();
    }

    private void UpdateEnemyPreview(int charId)
    {
        _enemyCharId = charId;
        if (charId < 0)
        {
            UnloadPreview(enemyPreview);
            if (enemyPreview != null) enemyPreview.gameObject.SetActive(false);
            if (enemyEmptyText != null) enemyEmptyText.gameObject.SetActive(true);
            UpdateEnemyName();
            UpdateEnemyPrepareText();
            return;
        }

        if (enemyEmptyText != null) enemyEmptyText.gameObject.SetActive(false);
        LoadHeroPreview(enemyPreview, charId);
        UpdateEnemyName();
        UpdateEnemyPrepareText();
    }

    private void RequestPeerChar()
    {
        var nm = Nm;
        if (nm == null || !nm.IsConnectedClient) return;

        if (nm.IsServer)
        {
            if (RoomState.Instance != null)
            {
                UpdateEnemyPreview(RoomState.Instance.GetPeerCharId(nm.LocalClientId));
                _enemyReady = RoomState.Instance.GetPeerReady(nm.LocalClientId);
                UpdateEnemyPrepareText();
            }
        }
        else
        {
            using var writer = new FastBufferWriter(0, Allocator.Temp);
            nm.CustomMessagingManager.SendNamedMessage(RoomState.MsgQueryPeerChar, NetworkManager.ServerClientId, writer);
        }
    }

    private IEnumerator WaitConnectedAndRequestPeer()
    {
        float timeout = 15f;
        while (timeout > 0f)
        {
            var nm = Nm;
            if (nm == null) yield break;
            if (nm.IsConnectedClient) break;
            timeout -= Time.unscaledDeltaTime;
            yield return null;
        }
        _waitForConnectRoutine = null;
        if (IsOnline)
        {
            RequestPeerChar();
            SetStatus(BattleNetworkLauncher.IsHost ? "你是房主（服务端）" : "已连接房主");
        }
        else
        {
            SetStatus("连接超时 · 点击退出后重试");
        }
    }

    private void OnClientDisconnected(ulong clientId)
    {
        var nm = Nm;
        if (nm == null || nm.IsServer) return;
        if (clientId != NetworkManager.ServerClientId) return;

        ShowToast("房主已退出");
        BattleNetworkLauncher.ShutdownAndDestroy();
        UIManager.Instance.Close(UIType.BattleRoomView).Forget();
    }

    // ---------------- 准备 / 退出 ----------------

    private void OnClickReady()
    {
        if (_isReady)
        {
            SubmitCancelReady();
            return;
        }

        if (_selectedCharId < 0)
        {
            ShowToast("请先选择角色");
            return;
        }

        var nm = Nm;
        if (nm == null || !nm.IsConnectedClient)
        {
        _isReady = true;
        RefreshReadyUI();
        RefreshStartButton();
        ShowToast("单机调试：已准备（未同步）");
            return;
        }

        if (nm.IsServer)
        {
            if (RoomState.Instance == null)
            {
                ShowToast("联机对象缺少 RoomState 组件");
                return;
            }
            RoomState.Instance.ServerSetReady(nm.LocalClientId, _selectedCharId);
        }
        else
        {
            using var writer = new FastBufferWriter(4, Allocator.Temp);
            writer.WriteValueSafe(_selectedCharId);
            nm.CustomMessagingManager.SendNamedMessage(RoomState.MsgSetReady, NetworkManager.ServerClientId, writer);
        }

        _isReady = true;
        RefreshReadyUI();
        RefreshStartButton();
    }

    private void SubmitCancelReady()
    {
        var nm = Nm;
        if (nm != null && nm.IsConnectedClient)
        {
            if (nm.IsServer)
            {
                if (RoomState.Instance != null)
                    RoomState.Instance.ServerCancelReady(nm.LocalClientId);
            }
            else
            {
                using var writer = new FastBufferWriter(0, Allocator.Temp);
                nm.CustomMessagingManager.SendNamedMessage(RoomState.MsgCancelReady, NetworkManager.ServerClientId, writer);
            }
        }

        _isReady = false;
        RefreshReadyUI();
        RefreshStartButton();
    }

    private void OnClickExit()
    {
        UIManager.Instance.Close(UIType.BattleRoomView).Forget();
    }

    // ---------------- 邀请好友 ----------------

    /// <summary>
    /// 房间内邀请：没有会话就地建房（单机调试路径），是房主就打开邀请面板
    /// </summary>
    private async void OnClickInvite()
    {
        if (BattleInviteManager.Instance == null)
        {
            ShowToast("邀请服务未就绪");
            return;
        }

        string error = await BattleInviteManager.Instance.EnsureHostRoomAsync();
        if (!string.IsNullOrEmpty(error))
        {
            ShowToast(error);
            return;
        }

        RefreshInviteButton();
        UIManager.Instance.Open(UIType.BattleInviteView).Forget();
    }

    /// <summary>邀请按钮只对房主（或还没建房的人）可见，客机看不到</summary>
    private void RefreshInviteButton()
    {
        if (inviteButton == null) return;
        bool visible = !BattleNetworkLauncher.HasSession || BattleNetworkLauncher.IsHost;
        inviteButton.gameObject.SetActive(visible);
    }

    /// <summary>
    /// 刷新对手昵称
    ///
    /// NGO 只有 clientId，账号ID靠 RoomState.Hello/PeerInfo 同步，昵称再从邀请结果里带过来
    /// （BattleInviteManager.KnownPlayerNames）。手动填 IP 进房时没有昵称，退化为"玩家{id}"。
    /// </summary>
    private void RefreshEnemyPlayerName()
    {
        if (enemyPlayerText == null) return;

        var nm = Nm;
        int peerPlayerId = RoomState.Instance != null && nm != null
            ? RoomState.Instance.GetPeerPlayerId(nm.LocalClientId)
            : 0;

        if (peerPlayerId <= 0)
        {
            enemyPlayerText.SetText("等待好友加入…");
            return;
        }

        string name = null;
        if (BattleInviteManager.Instance != null)
            BattleInviteManager.Instance.KnownPlayerNames.TryGetValue(peerPlayerId, out name);

        enemyPlayerText.SetText(string.IsNullOrEmpty(name) ? $"玩家{peerPlayerId}" : name);
    }

    // ---------------- 文案刷新 ----------------

    private void RefreshReadyUI()
    {
        if (readyButtonLabel != null)
            readyButtonLabel.SetText(_isReady ? "取消准备" : "准备");
        UpdateSelfPrepareText();
    }

    /// <summary>
    /// 房主专属"开始游戏"：只有房主看得到，双方都准备后才可点。
    /// 客户端不显示（等服务端 NetworkSceneManager 把大家带进战斗场景）。
    /// </summary>
    private void RefreshStartButton()
    {
        if (startButton == null) return;

        bool isHost = Nm != null && Nm.IsServer;
        startButton.gameObject.SetActive(isHost);
        if (!isHost) return;

        bool allReady = RoomState.Instance != null && RoomState.Instance.AllReady;
        bool loading = RoomState.Instance != null && RoomState.Instance.IsLoadingBattleScene;
        startButton.interactable = allReady && !loading;
        if (startButtonLabel != null)
            startButtonLabel.SetText(loading ? "正在进入…" : (allReady ? "开始游戏" : "等待准备"));
    }

    private void OnClickStart()
    {
        if (RoomState.Instance == null)
        {
            ShowToast("联机对象缺少 RoomState 组件");
            return;
        }
        if (!RoomState.Instance.AllReady)
        {
            ShowToast("还有玩家未准备");
            return;
        }
        if (!RoomState.Instance.ServerStartBattle())
            ShowToast("开始失败：检查战斗场景是否加入 Build Settings");
        else
            SetStatus("正在进入战斗场景…");
    }

    private void RefreshHeroButtons()
    {
        SetHeroButtonColor(anbiButton, _selectedCharId == BattleHeroCatalog.Anbi);
        SetHeroButtonColor(ellenButton, _selectedCharId == BattleHeroCatalog.Ellen);
    }

    private static void SetHeroButtonColor(Button button, bool selected)
    {
        if (button == null) return;

        if (button.targetGraphic is Image image)
            image.color = selected ? HeroSelectedColor : HeroNormalColor;

        Transform border = button.transform.Find("Border");
        if (border != null && border.TryGetComponent(out Image borderImage))
            borderImage.color = selected ? GoldSelected : GoldNormal;

        Transform mark = button.transform.Find("SelectedMark");
        if (mark != null) mark.gameObject.SetActive(selected);
    }

    /// <summary>准备状态胶囊：文字与父节点底图一起显隐</summary>
    private static void SetPrepareVisible(TMP_Text text, bool visible)
    {
        if (text == null) return;
        text.gameObject.SetActive(visible);

        Transform badge = text.transform.parent;
        if (badge != null && badge.name == "PrepareBadge")
            badge.gameObject.SetActive(visible);
    }

    private void UpdateSelfName()
    {
        if (selfNameText != null)
            selfNameText.SetText(_selectedCharId >= 0 ? BattleHeroCatalog.GetDisplayName(_selectedCharId) : "未选择");
    }

    private void UpdateEnemyName()
    {
        if (enemyNameText != null)
            enemyNameText.SetText(_enemyCharId >= 0 ? BattleHeroCatalog.GetDisplayName(_enemyCharId) : "未选择");
    }

    private void UpdateSelfPrepareText()
    {
        if (selfPrepareText == null) return;
        bool hasChar = _selectedCharId >= 0;
        SetPrepareVisible(selfPrepareText, hasChar);
        if (hasChar)
            selfPrepareText.SetText(_isReady ? "已准备" : "未准备");
    }

    private void UpdateEnemyPrepareText()
    {
        if (enemyPrepareText == null) return;
        bool hasChar = _enemyCharId >= 0;
        SetPrepareVisible(enemyPrepareText, hasChar);
        if (hasChar)
            enemyPrepareText.SetText(_enemyReady ? "已准备" : "未准备");
    }

    private void SetStatus(string content)
    {
        if (statusText != null) statusText.SetText(content ?? string.Empty);
    }

    // ---------------- 飘字 ----------------

    private void ShowToast(string message)
    {
        if (toastText == null || toastGroup == null)
        {
            SetStatus(message);
            return;
        }

        toastText.SetText(message);
        if (_toastRoutine != null) StopCoroutine(_toastRoutine);
        _toastRoutine = StartCoroutine(ToastRoutine());
    }

    private IEnumerator ToastRoutine()
    {
        toastGroup.alpha = 1f;
        yield return new WaitForSecondsRealtime(1.6f);

        const float fade = 0.3f;
        float t = 0f;
        while (t < fade)
        {
            t += Time.unscaledDeltaTime;
            toastGroup.alpha = 1f - Mathf.Clamp01(t / fade);
            yield return null;
        }

        toastGroup.alpha = 0f;
        _toastRoutine = null;
    }

    private void HideToast()
    {
        if (_toastRoutine != null)
        {
            StopCoroutine(_toastRoutine);
            _toastRoutine = null;
        }
        if (toastGroup != null) toastGroup.alpha = 0f;
    }

    // ---------------- 本机 IP ----------------

    private static string GetLocalIPv4()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 80);
            if (socket.LocalEndPoint is IPEndPoint endPoint && endPoint.Address != null)
            {
                string ip = endPoint.Address.ToString();
                if (!string.IsNullOrEmpty(ip)) return ip;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[BattleRoom] 默认路由探测 IP 失败：{e.Message}");
        }

        string fallback = null;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                    byte[] b = ua.Address.GetAddressBytes();
                    bool isPrivate = b[0] == 10
                        || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                        || (b[0] == 192 && b[1] == 168);
                    if (isPrivate) return ua.Address.ToString();
                    if (fallback == null) fallback = ua.Address.ToString();
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[BattleRoom] 遍历网卡取 IP 失败：{e.Message}");
        }
        return fallback ?? "127.0.0.1";
    }
}
