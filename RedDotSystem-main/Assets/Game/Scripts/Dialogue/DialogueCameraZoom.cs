using Cinemachine;
using UnityEngine;

/// <summary>
/// 第三人称相机的滚轮缩放（对话场景专用）。
///
/// 为什么不复用 tiaozhan 的 Camera_ZoomController：
/// 那个脚本从 <see cref="CinemachineInputProvider"/> 的 ZAxis（Input System 动作引用）取值，
/// 而挑战场景/预制体上的 XYAxis、ZAxis 都是空的 → 取到 0，缩放实际不会生效。
/// 本场景不挂 InputProvider（POV 走 legacy "Mouse X/Y" 轴），所以这里直接读 <see cref="Input.mouseScrollDelta"/>。
///
/// 只改 FramingTransposer 的 m_CameraDistance，不影响 POV / Follow。
/// </summary>
[RequireComponent(typeof(CinemachineVirtualCamera))]
public class DialogueCameraZoom : MonoBehaviour
{
    [Header("距离范围（米）")]
    public float MinDistance = 2.0f;
    public float MaxDistance = 4.5f;

    [Header("手感")]
    [Tooltip("滚一格滚轮改变的距离（米）")]
    public float Sensitivity = 0.45f;
    [Tooltip("平滑速度，越大越跟手")]
    public float Smooth = 12f;

    private CinemachineFramingTransposer framing;
    private float targetDistance;

    private void Awake()
    {
        var vcam = GetComponent<CinemachineVirtualCamera>();
        framing = vcam != null ? vcam.GetCinemachineComponent<CinemachineFramingTransposer>() : null;
        if (framing == null)
        {
            Debug.LogWarning("[DialogueCameraZoom] 缺少 CinemachineFramingTransposer，滚轮缩放已停用：" + name, this);
            enabled = false;
            return;
        }

        MinDistance = Mathf.Min(MinDistance, MaxDistance);
        targetDistance = Mathf.Clamp(framing.m_CameraDistance, MinDistance, MaxDistance);
    }

    private void Update()
    {
        float scroll = Input.mouseScrollDelta.y;
        if (Mathf.Abs(scroll) > 0.01f)
        {
            // 向上滚（正值）拉近，符合常见第三人称习惯
            targetDistance = Mathf.Clamp(targetDistance - scroll * Sensitivity, MinDistance, MaxDistance);
        }

        float t = 1f - Mathf.Exp(-Smooth * Time.unscaledDeltaTime);
        framing.m_CameraDistance = Mathf.Lerp(framing.m_CameraDistance, targetDistance, t);
    }

    /// <summary>测试/剧情镜头用：直接设定目标距离</summary>
    public void SetZoom(float distance)
    {
        targetDistance = Mathf.Clamp(distance, MinDistance, MaxDistance);
    }

    /// <summary>
    /// 立即落位（场景存档恢复用）：跳过平滑，避免恢复瞬间镜头从默认距离滑到存档距离
    /// </summary>
    public void SetZoomImmediate(float distance)
    {
        if (framing == null) return;

        targetDistance = Mathf.Clamp(distance, MinDistance, MaxDistance);
        framing.m_CameraDistance = targetDistance;
    }

    public float TargetDistance => targetDistance;
}
