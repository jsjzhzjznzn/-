using System;
using Cinemachine;
using UnityEngine;

/// <summary>
/// 大世界角色切换：数字键 1–4 对应 <see cref="BattleHeroCatalog"/> 的 charId（下标 = 键号 - 1）。
///
/// 换人流程：在旧角色原位置/朝向上实例化目标角色预制体 → 立刻停用旧角色（避免两个 Player 同时吃输入）
/// → 销毁旧角色 → 把 thirdcamera 的 Follow/LookAt 重新指向新角色。
/// 相机对象本身不销毁：thirdcamera 是场景里唯一的主玩法机位，换人只换跟随目标。
/// </summary>
public class DashijiePlayerSwitcher : MonoBehaviour
{
    /// <summary>跟随虚拟相机物体名（玩家预制体与 thirdcamera.prefab 同名约定）</summary>
    private const string ThirdCameraName = "thirdcamera";

    [Header("世界角色预制体：下标 = charId（0 安比 / 1 艾莲）")]
    [SerializeField] private GameObject[] heroPrefabs = Array.Empty<GameObject>();

    [Tooltip("当前操控的角色（场景预置的 安比dan 实例；留空则运行时自动查找）")]
    [SerializeField] private Player currentPlayer;

    /// <summary>当前操控的角色（切换后指向新实例）</summary>
    public Player CurrentPlayer => currentPlayer;

    /// <summary>当前角色编号（BattleHeroCatalog 下标）</summary>
    public int CurrentCharId { get; private set; }

    /// <summary>可用角色数量（= 世界预制体数量）</summary>
    public int Count => heroPrefabs != null ? heroPrefabs.Length : 0;

    /// <summary>换人完成（参数为新角色；用于血条/存档重新绑定）</summary>
    public event Action<Player> PlayerChanged;

    private void Awake()
    {
        if (currentPlayer == null) currentPlayer = FindObjectOfType<Player>();

        CurrentCharId = ResolveCharId(currentPlayer);

        // 场景加载后先绑一次，避免玩家 Start 完成前相机跟丢
        BindCamera(currentPlayer);
    }

    /// <summary>
    /// 切换到指定角色。越界 / 空预制体 / 已经是当前角色时返回 false（不重播动画、不重建实例）。
    /// </summary>
    public bool SwitchTo(int charId)
    {
        if (charId < 0 || charId >= Count) return false;
        if (heroPrefabs[charId] == null) return false;
        if (currentPlayer != null && CurrentCharId == charId) return false;

        Transform previous = currentPlayer != null ? currentPlayer.transform : null;
        Vector3 position = previous != null ? previous.position : transform.position;
        Quaternion rotation = previous != null ? previous.rotation : Quaternion.identity;

        // 旧角色先停用再销毁：Destroy 要等到帧末，停用能保证这一帧不会两个 Player 一起响应输入
        if (currentPlayer != null) currentPlayer.gameObject.SetActive(false);

        GameObject instance = Instantiate(heroPrefabs[charId], position, rotation);
        instance.name = heroPrefabs[charId].name;

        Player next = instance.GetComponent<Player>();
        currentPlayer = next;
        CurrentCharId = charId;

        if (previous != null) Destroy(previous.gameObject);

        BindCamera(next);
        PlayerChanged?.Invoke(next);

        Debug.Log($"[Dashijie] 切换角色 charId={charId} → {instance.name} pos={position}", this);
        return true;
    }

    /// <summary>把 thirdcamera 的跟随/看向目标换成指定角色（相机物体保持不换）</summary>
    private void BindCamera(Player player)
    {
        if (player == null) return;

        CinemachineVirtualCamera vcam = FindThirdCamera();
        if (vcam == null) return;

        vcam.Follow = player.transform;
        vcam.LookAt = player.transform;
    }

    /// <summary>按名查找场景里的 thirdcamera（只找激活对象；参数相机不参与）</summary>
    private static CinemachineVirtualCamera FindThirdCamera()
    {
        foreach (CinemachineVirtualCamera vcam in FindObjectsOfType<CinemachineVirtualCamera>())
        {
            if (vcam.gameObject.name == ThirdCameraName) return vcam;
        }
        return null;
    }

    /// <summary>角色编号取自玩家预制体上的 PlayerSO（安比=0 / 艾莲=1）</summary>
    private static int ResolveCharId(Player player)
    {
        if (player == null || player.PlayerSO == null) return 0;
        return player.PlayerSO.CharacterId;
    }
}
