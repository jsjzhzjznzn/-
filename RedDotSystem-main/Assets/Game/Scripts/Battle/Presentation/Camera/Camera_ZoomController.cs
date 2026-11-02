using UnityEngine;
using Cinemachine;

/// <summary>
/// 相机滚轮缩放控制器（源工程 camera/镜头/Camera_ZoomController.cs 原样移植）。
///
/// 挂载在同时带 CinemachineVirtualCamera + CinemachineInputProvider 的物体上（thirdcamera）。
/// 每帧从 Provider 的 ZAxis（轴索引 2，绑定 playerinputaction 的 zoom 动作）读取滚轮值，
/// 累加到目标距离后平滑逼近 FramingTransposer 的 m_CameraDistance，实现滚轮拉近 / 拉远。
///
/// 移植说明：类名与字段名、GUID 全部沿用源工程，工程里 thirdcamera.prefab / tiaozhan.scene
/// 已有的序列化引用（defaultDistance 2.8 / lookMin 1 / lookMax 6 / zoomSensitivity 3 / zoomSpeed 7）
/// 因此能自动接上，无需改资产。
/// </summary>
public class Camera_ZoomController : MonoBehaviour
{
    /// <summary>默认（初始）相机距离</summary>
    [Range(1, 8), SerializeField, Header("默认的距离")] private float defaultDistance;

    /// <summary>滚轮拉近时的最小相机距离（钳制下限）</summary>
    [Range(0, 8), SerializeField, Header("最小的距离")] private float lookMinDistance;

    /// <summary>滚轮拉远时的最大相机距离（钳制上限）</summary>
    [Range(1, 8), SerializeField, Header("最大的距离")] private float lookMaxDistance;

    /// <summary>滚轮灵敏度：滚动输入值再乘以此系数，值越大滚一格相机动得越多</summary>
    [SerializeField] private float zoomSensitivity = 1;

    /// <summary>缩放插值速度：每帧向目标距离推进 zoomSpeed * deltaTime 比例，值越大越跟手</summary>
    [SerializeField] private float zoomSpeed = 4;

    /// <summary>外部调速变量（预留，供 SetZoom 外部干预时使用）</summary>
    public float ExternalSpeedVariable = 1;

    /// <summary>FramingTransposer 组件缓存（Body 在 Virtual Camera 子级 Rig 上，需用 GetCinemachineComponent 获取）</summary>
    private CinemachineFramingTransposer CinemachineFramingTransposer;

    /// <summary>输入 Provider 组件缓存（用于读取滚轮 ZAxis 轴输入）</summary>
    private CinemachineInputProvider CinemachineInputProvider;

    /// <summary>当前目标相机距离（受滚轮控制，最终被平滑插值逼近）</summary>
    [SerializeField] public float currentDistance;

    private void Awake()
    {
        // 缓存 Body 组件：通过 CinemachineVirtualCamera 查找其 Rig 上的 FramingTransposer（负责控制相机与目标的距离）
        var vcam = GetComponent<CinemachineVirtualCamera>();
        CinemachineFramingTransposer = vcam != null ? vcam.GetCinemachineComponent<CinemachineFramingTransposer>() : null;
        // 缓存输入 Provider（ZAxis 已绑定滚轮 zoom 动作）
        CinemachineInputProvider = GetComponent<CinemachineInputProvider>();

        // 场景接线缺失时关掉自己，避免逐帧空引用刷屏（源工程未做校验，因为预制体是唯一来源）
        if (CinemachineFramingTransposer == null || CinemachineInputProvider == null)
        {
            Debug.LogWarning("[Camera_ZoomController] 缺少 CinemachineFramingTransposer 或 CinemachineInputProvider，滚轮缩放已停用：" + name, this);
            enabled = false;
            return;
        }

        // 初始目标距离 = 默认距离
        currentDistance = defaultDistance;
    }

    private void Update()
    {
        UpdateInput();
    }

    /// <summary>读取滚轮输入（Provider 轴索引 2 = ZAxis）并应用缩放</summary>
    private void UpdateInput()
    {
        // ZAxis 的值已经过 inputactions 里的 Scale(0.001)+Invert 处理器，这里再乘灵敏度
        float inputZoomValue = CinemachineInputProvider.GetAxisValue(2) * zoomSensitivity;
        UpdateZoom(inputZoomValue);
    }

    /// <summary>根据滚轮输入更新相机距离（目标值钳制 + 平滑插值）</summary>
    private void UpdateZoom(float inputZoomValue)
    {
        // 目标距离累加滚轮增量，并钳制到最小 / 最大范围内
        currentDistance = Mathf.Clamp(currentDistance + inputZoomValue, lookMinDistance, lookMaxDistance);

        // 读取当前实际相机距离
        float realDistance = CinemachineFramingTransposer.m_CameraDistance;

        // 平滑逼近目标距离：每帧向 currentDistance 推进 zoomSpeed * deltaTime 比例
        realDistance = Mathf.Lerp(realDistance, currentDistance, zoomSpeed * Time.deltaTime);

        // 写回实际相机距离
        CinemachineFramingTransposer.m_CameraDistance = realDistance;

        // 已到达目标距离，无需继续处理
        if (realDistance == currentDistance)
        { return; }
    }

    /// <summary>
    /// 外部调用：直接设定目标相机距离与速度（如剧情镜头、技能拉镜等场景使用）
    /// </summary>
    /// <param name="distance">目标相机距离</param>
    /// <param name="speed">外部调速变量</param>
    public void SetZoom(float distance, float speed)
    {
        currentDistance = distance;
        ExternalSpeedVariable = speed;
    }
}
