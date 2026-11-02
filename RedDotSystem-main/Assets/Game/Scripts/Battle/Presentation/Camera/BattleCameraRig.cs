using System;
using System.Collections.Generic;
using Cinemachine;
using UnityEngine;

/// <summary>
/// 战斗座位相机表（场景内序列化配置，替代"按物体名字找相机"的字符串硬编码）。
///
/// 为什么要它：战斗场景里每个座位都有一套「主相机 + 跟随虚拟相机」，
/// 源工程靠 GameObject 名字（Camera / Camera (1)、thirdcamera / thirdcamera (1)）匹配，
/// 一旦改名或换场景就静默绑不上；这里改成在场景里显式列出座位 → 相机的映射。
///
/// 用法：挂在战斗场景任意常驻物体上（本工程挂在 manager 物体），把两套相机拖进列表；
/// `Player.TryBindSceneCamera` 优先问它要相机，取不到才回退到旧的名字匹配
/// （这样 BattleTest.scene 这类没有相机表的场景仍可运行）。
/// </summary>
[DisallowMultipleComponent]
public class BattleCameraRig : MonoBehaviour
{
    [Serializable]
    public class SeatCamera
    {
        [Tooltip("座位号：0 = 先入座的房主，1 = 后入座的客户端")]
        public int seat;

        [Tooltip("该座位用于渲染世界的主相机（带 CinemachineBrain / AudioListener）")]
        public Camera worldCamera;

        [Tooltip("该座位跟随角色的虚拟相机")]
        public CinemachineVirtualCameraBase virtualCamera;
    }

    [Header("座位相机表：座位号 → (主相机, 虚拟相机)")]
    [SerializeField] private List<SeatCamera> seats = new List<SeatCamera>();

    /// <summary>场景内唯一实例（战斗场景只有一个；切场景自动失效）</summary>
    public static BattleCameraRig Instance { get; private set; }

    private void Awake()
    {
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>按座位号取相机；配置缺失或引用为空时返回 false，调用方回退到名字匹配</summary>
    public bool TryGetSeat(int seat, out Camera worldCamera, out CinemachineVirtualCameraBase virtualCamera)
    {
        for (int i = 0; i < seats.Count; i++)
        {
            var entry = seats[i];
            if (entry == null || entry.seat != seat) continue;

            worldCamera = entry.worldCamera;
            virtualCamera = entry.virtualCamera;
            return worldCamera != null;
        }

        worldCamera = null;
        virtualCamera = null;
        return false;
    }

#if UNITY_EDITOR
    /// <summary>编辑器里一键把场景中的两套相机填进表（菜单 Tools/Battle/填充座位相机表）</summary>
    [ContextMenu("自动填充座位相机")]
    private void AutoFillInInspector()
    {
        seats.Clear();
        var cams = new List<Camera>();
        var vcams = new List<CinemachineVirtualCameraBase>();
        foreach (var c in FindObjectsOfType<Camera>(true))
            if (c.GetComponent<CinemachineBrain>() != null) cams.Add(c);
        foreach (var v in FindObjectsOfType<CinemachineVirtualCameraBase>(true))
            if (!(v is CinemachineVirtualCamera vcam) || vcam.GetCinemachineComponent<CinemachineFramingTransposer>() != null) vcams.Add(v);

        cams.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        vcams.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        int count = Mathf.Min(cams.Count, vcams.Count);
        for (int i = 0; i < count; i++)
            seats.Add(new SeatCamera { seat = i, worldCamera = cams[i], virtualCamera = vcams[i] });
    }
#endif
}
