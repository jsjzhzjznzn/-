using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 服务端房间裁判 / 房间状态（挂在与 NetworkManager 同物体的常驻联机对象上）。
///
/// 移植自 compete 项目，NGO 2.x -> 1.13 无需改动（NamedMessage API 保持一致）。
/// 职责：
///   1. 客户端之间互不可见，只有服务端能汇总"谁选了谁、谁准备好了"；
///   2. 状态放在 DDOL 联机对象上，切战斗场景时随 NetworkManager 一起存活；
///   3. 双方就绪后由服务端发起场景切换，战斗场景的 BattleSpawnManager 从这里取快照。
///
/// 注意：不要用会自动创建空物体的单例基类 —— 本组件依赖同物体上的 NetworkManager，
/// 忘挂时宁可报错提示，也不要悄悄生成一个无 NetworkManager 的空单例。
/// </summary>
[DisallowMultipleComponent]
public class RoomState : MonoBehaviour
{
    /// <summary>客户端"准备"命名消息名；payload = int charId</summary>
    public const string MsgSetReady = "RoomState.SetReady";
    /// <summary>客户端"取消准备"命名消息名；无 payload</summary>
    public const string MsgCancelReady = "RoomState.CancelReady";
    /// <summary>客户端上报"我选了英雄"命名消息名；payload = int charId（点选瞬间就发，不等准备）</summary>
    public const string MsgSelectChar = "RoomState.SelectChar";
    /// <summary>服务端转发"对手选了英雄"命名消息名；payload = int charId（-1 = 清空）</summary>
    public const string MsgPeerSelectChar = "RoomState.PeerSelectChar";
    /// <summary>客户端打开房间界面时拉取对手当前选择；无 payload</summary>
    public const string MsgQueryPeerChar = "RoomState.QueryPeerChar";
    /// <summary>服务端推送"对手的准备状态"命名消息；payload = byte（1=已准备 0=未准备）</summary>
    public const string MsgPeerReady = "RoomState.PeerReady";
    /// <summary>客户端上报"我的账号玩家ID"命名消息；payload = int（NGO 的 clientId 与账号 ID 没有天然映射）</summary>
    public const string MsgHello = "RoomState.Hello";
    /// <summary>服务端推送"对手的账号玩家ID"命名消息；payload = int（0=未知）</summary>
    public const string MsgPeerInfo = "RoomState.PeerInfo";
    /// <summary>客户端上报"我当前角色的装备快照"命名消息；payload = string（EquipBattleSnapshot 的 JSON）</summary>
    public const string MsgEquipSnapshot = "RoomState.EquipSnapshot";

    /// <summary>唯一实例：由 Awake 在挂载的物体上赋值；没挂载时为 null（调用处需判空）</summary>
    public static RoomState Instance { get; private set; }

    /// <summary>
    /// 是否正在跳转战斗场景（LoadScene 已发起）。
    /// 房间界面关闭时据此决定要不要销毁联机对象：跳转战斗时保留，其余关闭一律销毁。
    /// </summary>
    public bool IsLoadingBattleScene => _sceneLoading;

    /// <summary>某玩家英雄选择变化（clientId=选择归属者，charId=-1 表示未选/清空）。</summary>
    public event Action<ulong, int> OnPeerCharIdChanged;

    /// <summary>某玩家准备状态变化（clientId=状态归属者，isReady=是否已准备）。</summary>
    public event Action<ulong, bool> OnPeerReadyChanged;

    /// <summary>玩家账号身份（playerId）变化：房间界面据此刷新对手昵称。</summary>
    public event Action OnPlayerInfoChanged;

    [Header("服务端配置")]
    [Tooltip("对战人数（双方=2），全部准备才开局")]
    public int RequiredPlayerCount = 2;
    [Tooltip("英雄总数，服务端用它校验客户端上报的 CharId")]
    public int HeroCount = 2;
    [Tooltip("开始战斗后加载的场景名，需加入 Build Settings。留空 = P4 前不切场景，只保持就绪状态")]
    public string BattleSceneName = "";
    [Tooltip("双方就绪后是否自动加载战斗场景（关掉后改由房主点开始游戏触发）")]
    public bool LoadBattleSceneWhenReady = false;

    [Tooltip("true = 所有人准备后还要房主点开始游戏才进战斗；false = 所有人准备后自动进")]
    public bool RequireHostStart = true;

    /// <summary>某玩家在房间里的状态（只存服务端字典里，不发给任何人）</summary>
    public struct PlayerState
    {
        public bool IsReady;
        public int CharId;
        /// <summary>账号玩家ID（由客户端 Hello 上报；0=未知）</summary>
        public int PlayerId;
        /// <summary>该玩家上报的装备快照 JSON（EquipBattleSnapshot；空 = 无装备加成）</summary>
        public string EquipJson;
    }

    /// <summary>战斗场景生成用：按座位排序的玩家快照（仅服务端调用）</summary>
    public struct BattlePlayerInfo
    {
        public ulong ClientId;
        public int Seat;
        public int CharId;
        /// <summary>装备快照 JSON（生成英雄后注入 AttributeComponent；空 = 无装备加成）</summary>
        public string EquipJson;
    }

    private readonly Dictionary<ulong, PlayerState> _players = new Dictionary<ulong, PlayerState>();
    private readonly Dictionary<ulong, int> _seats = new Dictionary<ulong, int>();
    private bool _sceneLoading;
    private bool _handlersRegistered;
    private bool _clientHandlersRegistered;
    private NetworkManager _nm;

    /// <summary>客机记录的"对手账号ID"（由服务端 PeerInfo 推送）</summary>
    private int _peerPlayerId;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;

        _nm = GetComponent<NetworkManager>();
        if (_nm == null) _nm = NetworkManager.Singleton;
        if (_nm == null)
        {
            Debug.LogError("[RoomState] 必须挂在带 NetworkManager 的物体上（联机对象根节点）");
            return;
        }

        // Awake 就订阅，保证 StartHost 之前已经在监听，房主自己的连接回调也不会漏
        _nm.OnClientConnectedCallback += OnClientConnected;
        _nm.OnClientDisconnectCallback += OnClientDisconnected;
        _nm.OnServerStarted += OnServerStarted;
    }

    private void Start()
    {
        if (_nm == null) _nm = NetworkManager.Singleton;
        if (_nm != null && _nm.IsServer)
        {
            RegisterMessageHandlers();
            RegisterExistingPlayers();
        }
        else if (_nm != null && _nm.IsClient && _nm.IsConnectedClient)
        {
            RegisterClientHandlers();
            SendHello();
        }
    }

    private void OnDestroy()
    {
        if (_nm != null)
        {
            _nm.OnClientConnectedCallback -= OnClientConnected;
            _nm.OnClientDisconnectCallback -= OnClientDisconnected;
            _nm.OnServerStarted -= OnServerStarted;
        }
        if (Instance == this) Instance = null;
    }

    private bool IsServerNow()
    {
        return _nm != null && _nm.IsServer;
    }

    // ---------------- 玩家进出 ----------------

    private void OnServerStarted()
    {
        RegisterMessageHandlers();
        RegisterExistingPlayers();
    }

    private void RegisterExistingPlayers()
    {
        if (!IsServerNow()) return;
        var ids = new List<ulong>(_nm.ConnectedClients.Keys);
        foreach (var clientId in ids)
            TryRegisterPlayer(clientId);
    }

    private void OnClientConnected(ulong clientId)
    {
        if (IsServerNow())
        {
            TryRegisterPlayer(clientId);
            return;
        }
        RegisterClientHandlers();
        SendHello();
    }

    private void TryRegisterPlayer(ulong clientId)
    {
        if (_players.ContainsKey(clientId)) return;

        if (_players.Count >= RequiredPlayerCount)
        {
            Debug.LogWarning($"[RoomState] 房间已满({RequiredPlayerCount}人)，{clientId} 按旁观处理");
            return;
        }

        _players[clientId] = new PlayerState
        {
            IsReady = false,
            CharId = -1,
            // 房主的账号ID客户端自己就知道；客机等它的 Hello 上来再补
            PlayerId = clientId == NetworkManager.ServerClientId ? ResolveLocalPlayerId() : 0,
        };
        _seats[clientId] = GetNextFreeSeat();
        Debug.Log($"[RoomState] 玩家 {clientId} 进入房间，座位 {_seats[clientId]}");

        // 新人进房时，把其他已选英雄的玩家状态推给它
        foreach (var kv in _players)
        {
            if (kv.Key == clientId || kv.Value.CharId < 0) continue;
            if (kv.Key == NetworkManager.ServerClientId) continue;
            using (var writer = new FastBufferWriter(4, Allocator.Temp))
            {
                writer.WriteValueSafe(kv.Value.CharId);
                _nm.CustomMessagingManager.SendNamedMessage(MsgPeerSelectChar, clientId, writer);
            }
            using (var readyWriter = new FastBufferWriter(1, Allocator.Temp))
            {
                readyWriter.WriteValueSafe(kv.Value.IsReady ? (byte)1 : (byte)0);
                _nm.CustomMessagingManager.SendNamedMessage(MsgPeerReady, clientId, readyWriter);
            }
            SendPeerInfoTo(clientId, kv.Value.PlayerId);
            break;
        }

        OnPlayerInfoChanged?.Invoke();
    }

    private void RegisterMessageHandlers()
    {
        if (_handlersRegistered || _nm == null || _nm.CustomMessagingManager == null) return;
        _handlersRegistered = true;
        _nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgSetReady, OnMsgSetReady);
        _nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgCancelReady, OnMsgCancelReady);
        _nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgSelectChar, OnMsgSelectChar);
        _nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgQueryPeerChar, OnMsgQueryPeerChar);
        _nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgPeerSelectChar, OnMsgPeerSelectChar);
        _nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgPeerReady, OnMsgPeerReady);
        _nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgHello, OnMsgHello);
        _nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgPeerInfo, OnMsgPeerInfo);
        _nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgEquipSnapshot, OnMsgEquipSnapshot);
    }

    private void RegisterClientHandlers()
    {
        if (_clientHandlersRegistered || _nm == null || _nm.CustomMessagingManager == null) return;
        _clientHandlersRegistered = true;
        _nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgPeerSelectChar, OnMsgPeerSelectChar);
        _nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgPeerReady, OnMsgPeerReady);
        _nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgPeerInfo, OnMsgPeerInfo);
    }

    private void OnClientDisconnected(ulong clientId)
    {
        if (!IsServerNow()) return;
        _players.Remove(clientId);
        _seats.Remove(clientId);
        Debug.Log($"[RoomState] 玩家 {clientId} 离开房间");
    }

    private int GetNextFreeSeat()
    {
        for (int s = 0; s < RequiredPlayerCount; s++)
        {
            if (!_seats.ContainsValue(s)) return s;
        }
        return _seats.Count;
    }

    // ---------------- 命名消息入口（客户端上报） ----------------

    private void OnMsgSetReady(ulong senderClientId, FastBufferReader reader)
    {
        if (!IsServerNow()) return;
        if (!TryReadCharId(senderClientId, reader, out int charId)) return;
        ServerSetReady(senderClientId, charId);
    }

    private void OnMsgCancelReady(ulong senderClientId, FastBufferReader reader)
    {
        if (!IsServerNow()) return;
        ServerCancelReady(senderClientId);
    }

    private void OnMsgSelectChar(ulong senderClientId, FastBufferReader reader)
    {
        if (!IsServerNow()) return;
        if (!TryReadCharId(senderClientId, reader, out int charId)) return;
        ServerSetCharId(senderClientId, charId);
    }

    private void OnMsgQueryPeerChar(ulong senderClientId, FastBufferReader reader)
    {
        if (!IsServerNow()) return;
        int peerChar = GetPeerCharId(senderClientId);
        using (var writer = new FastBufferWriter(4, Allocator.Temp))
        {
            writer.WriteValueSafe(peerChar);
            _nm.CustomMessagingManager.SendNamedMessage(MsgPeerSelectChar, senderClientId, writer);
        }
        using (var readyWriter = new FastBufferWriter(1, Allocator.Temp))
        {
            readyWriter.WriteValueSafe(GetPeerReady(senderClientId) ? (byte)1 : (byte)0);
            _nm.CustomMessagingManager.SendNamedMessage(MsgPeerReady, senderClientId, readyWriter);
        }
    }

    private void OnMsgPeerSelectChar(ulong senderClientId, FastBufferReader reader)
    {
        if (IsServerNow()) return;
        if (!TryReadCharId(senderClientId, reader, out int charId)) return;
        OnPeerCharIdChanged?.Invoke(ulong.MaxValue, charId);
    }

    private void OnMsgPeerReady(ulong senderClientId, FastBufferReader reader)
    {
        if (IsServerNow()) return;
        byte flag = 0;
        try
        {
            reader.ReadValueSafe(out flag);
        }
        catch (Exception)
        {
            Debug.LogWarning($"[RoomState] {senderClientId} 的 PeerReady 消息格式不对，忽略");
            return;
        }
        OnPeerReadyChanged?.Invoke(ulong.MaxValue, flag != 0);
    }

    // ---------------- 账号身份（邀请进房后显示昵称用） ----------------

    /// <summary>客机收到"对手的账号玩家ID"</summary>
    private void OnMsgPeerInfo(ulong senderClientId, FastBufferReader reader)
    {
        if (IsServerNow()) return;
        int playerId = 0;
        try
        {
            reader.ReadValueSafe(out playerId);
        }
        catch (Exception)
        {
            Debug.LogWarning($"[RoomState] {senderClientId} 的 PeerInfo 消息格式不对，忽略");
            return;
        }
        _peerPlayerId = playerId;
        OnPlayerInfoChanged?.Invoke();
    }

    /// <summary>服务端收到客机的身份上报</summary>
    private void OnMsgHello(ulong senderClientId, FastBufferReader reader)
    {
        if (!IsServerNow()) return;

        int playerId = 0;
        try
        {
            reader.ReadValueSafe(out playerId);
        }
        catch (Exception)
        {
            Debug.LogWarning($"[RoomState] {senderClientId} 的 Hello 消息格式不对，忽略");
            return;
        }

        if (!_players.TryGetValue(senderClientId, out PlayerState state)) return;
        state.PlayerId = playerId;
        _players[senderClientId] = state;
        Debug.Log($"[RoomState] 玩家 {senderClientId} 身份上报 playerId={playerId}");

        // 把新上报的身份转给其他人（对手），它据此把 clientId 换成昵称
        foreach (var kv in _players)
        {
            if (kv.Key == senderClientId) continue;
            SendPeerInfoTo(kv.Key, playerId);
        }
        OnPlayerInfoChanged?.Invoke();
    }

    /// <summary>客机连上后上报自己的账号ID</summary>
    private void SendHello()
    {
        var nm = _nm;
        if (nm == null || !nm.IsConnectedClient || nm.IsServer) return;
        if (nm.CustomMessagingManager == null) return;

        int playerId = ResolveLocalPlayerId();
        using var writer = new FastBufferWriter(4, Allocator.Temp);
        writer.WriteValueSafe(playerId);
        nm.CustomMessagingManager.SendNamedMessage(MsgHello, NetworkManager.ServerClientId, writer);
        Debug.Log($"[RoomState] 已上报身份 playerId={playerId}");
    }

    /// <summary>
    /// 服务端收到客机的装备快照。快照只描述"该玩家当前角色这一套装备"，
    /// 生成英雄时按座位注入（客户端换角色/换装后需重新上报）。
    /// </summary>
    private void OnMsgEquipSnapshot(ulong senderClientId, FastBufferReader reader)
    {
        if (!IsServerNow()) return;

        string json;
        try
        {
            reader.ReadValueSafe(out json, false);
        }
        catch (Exception)
        {
            Debug.LogWarning($"[RoomState] {senderClientId} 的装备快照格式不对，忽略");
            return;
        }

        if (!_players.TryGetValue(senderClientId, out PlayerState state)) return;
        state.EquipJson = json;
        _players[senderClientId] = state;
        Debug.Log($"[RoomState] 玩家 {senderClientId} 上报装备快照 长度={(json?.Length ?? 0)}");
    }

    /// <summary>
    /// 客户端上报自己的装备快照（进房间、选角色、换装后调用）。
    /// 数据来自本地 EquipmentManager 缓存，房主只负责收下并在生成英雄时应用。
    /// </summary>
    /// <param name="charId">当前选择的角色ID（快照按该角色构建）</param>
    public void SendEquipSnapshot(int charId)
    {
        var nm = _nm;
        if (nm == null || !nm.IsConnectedClient || nm.IsServer) return;
        if (nm.CustomMessagingManager == null) return;
        if (EquipmentManager.Instance == null) return;

        EquipBattleSnapshot snapshot = EquipmentManager.Instance.BuildBattleSnapshot(charId);
        string json = JsonUtility.ToJson(snapshot);

        using var writer = new FastBufferWriter(json.Length + 8, Allocator.Temp);
        writer.WriteValueSafe(json, false);
        nm.CustomMessagingManager.SendNamedMessage(MsgEquipSnapshot, NetworkManager.ServerClientId, writer);
        Debug.Log($"[RoomState] 已上报装备快照 角色{charId} 装备{snapshot.items.Count}件");
    }

    private void SendPeerInfoTo(ulong clientId, int playerId)
    {
        if (!IsServerNow() || _nm == null) return;
        using var writer = new FastBufferWriter(4, Allocator.Temp);
        writer.WriteValueSafe(playerId);
        _nm.CustomMessagingManager.SendNamedMessage(MsgPeerInfo, clientId, writer);
    }

    /// <summary>
    /// 取某玩家对手的账号ID（0=还没上报/没有对手）
    /// 服务端从房间字典取；客机用 MsgPeerInfo 推来的值
    /// </summary>
    public int GetPeerPlayerId(ulong clientId)
    {
        if (IsServerNow())
        {
            foreach (var kv in _players)
            {
                if (kv.Key != clientId) return kv.Value.PlayerId;
            }
            return 0;
        }
        return _peerPlayerId;
    }

    private static int ResolveLocalPlayerId()
    {
        return ClientNetworkManager.Instance != null ? ClientNetworkManager.Instance.MyPlayerId : 0;
    }

    private bool TryReadCharId(ulong senderClientId, FastBufferReader reader, out int charId)
    {
        charId = -1;
        try
        {
            reader.ReadValueSafe(out charId);
            return true;
        }
        catch (Exception)
        {
            Debug.LogWarning($"[RoomState] {senderClientId} 的消息格式不对，忽略");
            return false;
        }
    }

    // ---------------- 服务端状态修改 ----------------

    /// <summary>服务端记录英雄选择并实时转发给对手；换人会让准备状态复位。</summary>
    public void ServerSetCharId(ulong clientId, int charId)
    {
        if (!IsServerNow()) return;
        if (!_players.ContainsKey(clientId))
        {
            Debug.LogWarning($"[RoomState] {clientId} 不在房间状态里（可能是旁观者），忽略选英雄");
            return;
        }
        if (charId < 0 || charId >= HeroCount)
        {
            Debug.LogWarning($"[RoomState] {clientId} 上报非法英雄 CharId={charId}");
            return;
        }

        var state = _players[clientId];
        if (state.CharId == charId) return;

        bool wasReady = state.IsReady;
        // 换角色只复位准备态：装备快照按"该玩家上报的那一套"原样保留（客户端换角色后会重新上报）
        _players[clientId] = new PlayerState { IsReady = false, CharId = charId, EquipJson = state.EquipJson };
        Debug.Log($"[RoomState] 玩家 {clientId} 选择英雄 {charId}");
        if (wasReady)
        {
            Debug.Log($"[RoomState] 玩家 {clientId} 换人，准备状态已复位");
            ServerBroadcastPeerReady(clientId, false);
        }

        foreach (var kv in _players)
        {
            if (kv.Key == clientId) continue;
            if (kv.Key == NetworkManager.ServerClientId) continue;
            using var writer = new FastBufferWriter(4, Allocator.Temp);
            writer.WriteValueSafe(charId);
            _nm.CustomMessagingManager.SendNamedMessage(MsgPeerSelectChar, kv.Key, writer);
        }

        OnPeerCharIdChanged?.Invoke(clientId, charId);
    }

    /// <summary>
    /// 服务端直接写入某玩家的装备快照（房主自己选角色时用，省一次网络往返）。
    /// </summary>
    /// <param name="clientId">玩家连接ID</param>
    /// <param name="charId">当前角色ID（快照按该角色构建）</param>
    public void ServerSetEquipSnapshot(ulong clientId, int charId)
    {
        if (!IsServerNow()) return;
        if (!_players.TryGetValue(clientId, out PlayerState state)) return;
        if (EquipmentManager.Instance == null) return;

        state.EquipJson = JsonUtility.ToJson(EquipmentManager.Instance.BuildBattleSnapshot(charId));
        _players[clientId] = state;
        Debug.Log($"[RoomState] 服务端记录装备快照 client={clientId} 角色{charId}");
    }

    /// <summary>取某玩家对手当前选的英雄（-1 = 对手没选/没有对手）；仅服务端。</summary>
    public int GetPeerCharId(ulong clientId)
    {
        if (!IsServerNow()) return -1;
        foreach (var kv in _players)
        {
            if (kv.Key != clientId) return kv.Value.CharId;
        }
        return -1;
    }

    /// <summary>取某玩家对手当前是否已准备；仅服务端。</summary>
    public bool GetPeerReady(ulong clientId)
    {
        if (!IsServerNow()) return false;
        foreach (var kv in _players)
        {
            if (kv.Key != clientId) return kv.Value.CharId >= 0 && kv.Value.IsReady;
        }
        return false;
    }

    private void ServerBroadcastPeerReady(ulong clientId, bool isReady)
    {
        foreach (var kv in _players)
        {
            if (kv.Key == clientId) continue;
            if (kv.Key == NetworkManager.ServerClientId) continue;
            using var writer = new FastBufferWriter(1, Allocator.Temp);
            writer.WriteValueSafe(isReady ? (byte)1 : (byte)0);
            _nm.CustomMessagingManager.SendNamedMessage(MsgPeerReady, kv.Key, writer);
        }
        OnPeerReadyChanged?.Invoke(clientId, isReady);
    }

    public void ServerSetReady(ulong clientId, int charId)
    {
        if (!IsServerNow()) return;
        if (!_players.ContainsKey(clientId))
        {
            Debug.LogWarning($"[RoomState] {clientId} 不在房间状态里（可能是旁观者），忽略准备");
            return;
        }
        if (charId < 0 || charId >= HeroCount)
        {
            Debug.LogWarning($"[RoomState] {clientId} 上报非法英雄 CharId={charId}");
            return;
        }

        _players[clientId] = new PlayerState { IsReady = true, CharId = charId };
        Debug.Log($"[RoomState] 玩家 {clientId} 已准备，英雄 {charId}");
        ServerBroadcastPeerReady(clientId, true);
        CheckStartGame();
    }

    public void ServerCancelReady(ulong clientId)
    {
        if (!IsServerNow()) return;
        if (!_players.TryGetValue(clientId, out var state)) return;
        if (!state.IsReady) return;

        _players[clientId] = new PlayerState { IsReady = false, CharId = state.CharId };
        Debug.Log($"[RoomState] 玩家 {clientId} 取消准备");
        ServerBroadcastPeerReady(clientId, false);
    }

    /// <summary>是否所有座位都到齐且都点了准备（房主"开始游戏"的启用条件）</summary>
    public bool AllReady
    {
        get
        {
            if (_players.Count != RequiredPlayerCount) return false;
            foreach (var kv in _players)
            {
                if (!kv.Value.IsReady) return false;
            }
            return true;
        }
    }

    /// <summary>房主手动开始：所有玩家准备好后由房主调用，服务端切到战斗场景（NetworkSceneManager 会同步所有客户端）</summary>
    public bool ServerStartBattle()
    {
        if (!IsServerNow())
        {
            Debug.LogWarning("[RoomState] 只有房主（服务端）能开始战斗");
            return false;
        }
        if (_sceneLoading) return false;
        if (!AllReady)
        {
            Debug.LogWarning("[RoomState] 还有玩家未准备，无法开始");
            return false;
        }
        return LoadBattleScene();
    }

    private void CheckStartGame()
    {
        // 房主手动开局模式下，所有人准备后不再自动切场景，等房主点“开始游戏”
        if (RequireHostStart) return;
        if (_sceneLoading) return;
        if (!AllReady) return;
        if (!LoadBattleSceneWhenReady) return;
        LoadBattleScene();
    }

    private bool LoadBattleScene()
    {
        if (string.IsNullOrEmpty(BattleSceneName))
        {
            Debug.LogError("[RoomState] BattleSceneName 为空：先把战斗场景名（如 multiplecompete）填上并加进 Build Settings");
            return false;
        }

        _sceneLoading = true;

        // 注意：这里必须走 NGO 的 NetworkSceneManager，不能用 SceneLoader / Unity SceneManager。
        // 多人场景切换由服务端裁决、客户端跟随，还要同步 NetworkObject；换成原生加载会导致多端场景不同步。
        var status = _nm.SceneManager.LoadScene(BattleSceneName, LoadSceneMode.Single);
        if (status != SceneEventProgressStatus.Started)
        {
            Debug.LogError($"[RoomState] 加载战斗场景失败({status})：确认场景已加进 Build Settings 且名字正确");
            _sceneLoading = false;
            return false;
        }

        var keys = new List<ulong>(_players.Keys);
        foreach (var k in keys)
        {
            var p = _players[k];
            _players[k] = new PlayerState { IsReady = false, CharId = p.CharId, EquipJson = p.EquipJson };
        }
        Debug.Log("[RoomState] 房主开始游戏，正在加载战斗场景 " + BattleSceneName);
        return true;
    }

    /// <summary>供战斗场景的 BattleSpawnManager 取快照：按座位升序。</summary>
    public List<BattlePlayerInfo> GetBattlePlayers()
    {
        var list = new List<BattlePlayerInfo>(_players.Count);
        foreach (var kv in _players)
        {
            int seat = _seats.TryGetValue(kv.Key, out var s) ? s : 0;
            list.Add(new BattlePlayerInfo
            {
                ClientId = kv.Key,
                Seat = seat,
                CharId = kv.Value.CharId,
                EquipJson = kv.Value.EquipJson
            });
        }
        list.Sort((a, b) => a.Seat.CompareTo(b.Seat));
        return list;
    }
}
