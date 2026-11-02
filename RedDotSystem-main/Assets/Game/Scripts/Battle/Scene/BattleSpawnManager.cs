using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 战斗场景生成角色（源工程 combat 场景的 BattleSpawnManager 移植版）。
///
/// 与源脚本的差异只有一个：这里的 RoomState 指目标工程的全局 RoomState
/// （Assets/Game/Scripts/Battle/Network/RoomState.cs），而不是 Compete 命名空间里的原样副本。
/// 房间/座位/英雄编号的数据结构保持一致（ClientId / Seat / CharId）。
///
/// 挂载要求：
///   1. 本脚本所在物体要带 NetworkObject，作为"场景内放置的 NetworkObject"，
///      这样服务端加载完战斗场景时 OnNetworkSpawn 才会被触发
///   2. 英雄预制体必须带 NetworkObject，并注册进 NetworkManager 的 Network Prefabs List
///   3. 需要位移同步的给英雄挂 NetworkTransform；角色控制脚本里用 IsOwner 判断是否本机操作
/// </summary>
public class BattleSpawnManager : NetworkBehaviour
{
    [Header("英雄预制体：数组下标 = CharId（安比=0，艾莲=1，与选人大厅一致）")]
    public List<GameObject> HeroPrefabList;
    [Header("出生点：数组下标 = 座位（0 先入座=房主，1 后入座）")]
    public List<Transform> SpawnPoints;

    /// <summary>防止重复生成（例如 OnNetworkSpawn 在异常情况下被再次调用）</summary>
    private bool _spawned;

    /// <summary>本次生成统计：成功生成数（排查"只生成一个人物"用）</summary>
    private int _spawnedCount;
    /// <summary>本次生成统计：被跳过数（CharId/座位/预制体空等）</summary>
    private int _skippedCount;

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        if (!IsServer || _spawned) return;
        _spawned = true;

        if (HeroPrefabList == null || HeroPrefabList.Count == 0 || SpawnPoints == null || SpawnPoints.Count == 0)
        {
            Debug.LogError("[BattleSpawn] 没配置英雄预制体或出生点");
            return;
        }
        if (RoomState.Instance == null)
        {
            Debug.LogError("[BattleSpawn] 找不到 RoomState（联机预制体根节点上没挂？）");
            return;
        }

        var players = RoomState.Instance.GetBattlePlayers();
        if (players.Count == 0)
        {
            Debug.LogError("[BattleSpawn] RoomState 里没有玩家数据");
            return;
        }

        _spawnedCount = 0;
        _skippedCount = 0;
        foreach (var p in players)
            SpawnHeroFor(p);

        Debug.Log($"[BattleSpawn] 生成汇总: 房间玩家={players.Count} 成功={_spawnedCount} 跳过={_skippedCount}，" +
                  $"出生点数={SpawnPoints.Count} 英雄预制体数={HeroPrefabList.Count}");
    }

    private void SpawnHeroFor(RoomState.BattlePlayerInfo info)
    {
        if (info.CharId < 0 || info.CharId >= HeroPrefabList.Count)
        {
            _skippedCount++;
            Debug.LogError($"[BattleSpawn] 玩家 {info.ClientId} 的 CharId={info.CharId} 越界（预制体数={HeroPrefabList.Count}），跳过");
            return;
        }
        if (info.Seat < 0 || info.Seat >= SpawnPoints.Count)
        {
            _skippedCount++;
            Debug.LogError($"[BattleSpawn] 玩家 {info.ClientId} 的座位 {info.Seat} 没有对应出生点（只有 {SpawnPoints.Count} 个），跳过");
            return;
        }

        var prefab = HeroPrefabList[info.CharId];
        if (prefab == null)
        {
            _skippedCount++;
            Debug.LogError($"[BattleSpawn] 英雄预制体 CharId={info.CharId} 为空");
            return;
        }

        var spawnPoint = SpawnPoints[info.Seat];
        var hero = Instantiate(prefab, spawnPoint.position, spawnPoint.rotation);
        var netObj = hero.GetComponent<NetworkObject>();
        if (netObj == null)
        {
            _skippedCount++;
            Debug.LogError($"[BattleSpawn] 英雄预制体 {prefab.name} 上没有 NetworkObject");
            Destroy(hero);
            return;
        }

        // 座位/英雄编号必须在 Spawn 之后写：NGO 要求 NetworkObject 已 spawn 才能标脏。
        // 客户端相机绑定带重试，会等座位号同步到之后再绑（NetSeatId 默认 -1 表示未分配）。
        netObj.SpawnWithOwnership(info.ClientId);

        var playerCtrl = hero.GetComponent<Player>();
        if (playerCtrl != null)
        {
            playerCtrl.NetSeatId.Value = info.Seat;
            playerCtrl.NetCharId.Value = info.CharId;

            // 装备加成：只应用"属于该角色"的快照（换角色但客户端还没重新上报时留空，避免张冠李戴）
            if (!string.IsNullOrEmpty(info.EquipJson))
            {
                EquipBattleSnapshot snapshot = JsonUtility.FromJson<EquipBattleSnapshot>(info.EquipJson);
                if (snapshot == null || snapshot.charId == info.CharId)
                {
                    playerCtrl.ApplyEquipmentSnapshot(snapshot);
                }
                else
                {
                    Debug.LogWarning($"[BattleSpawn] 玩家 {info.ClientId} 的装备快照属于角色{snapshot.charId}，与当前角色{info.CharId}不符，跳过");
                }
            }
        }

        _spawnedCount++;
        Debug.Log($"[BattleSpawn] 玩家 {info.ClientId} 座位 {info.Seat} 生成英雄 CharId={info.CharId}, " +
                  $"OwnerClientId={netObj.OwnerClientId}");
    }
}
