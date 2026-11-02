using Cinemachine;
using KinematicCharacterController;
using UnityEngine;

/// <summary>
/// dashijie 场景存档：位置/朝向 + 第三人称相机（POV 双轴 + 滚轮距离）。
///
/// 大世界不接对话：快照里的对话节点/变量/已发布事件字段不再写入，也不再从存档里读取。
/// 恢复语义：位置与相机直接落位（不做插值），首次进入从场景默认出生点开始。
/// </summary>
public class DashijieSceneSaveController : SceneSaveController
{
    [Header("dashijie 场景引用")]
    [SerializeField] private Player player;
    [SerializeField] private CinemachineVirtualCamera playCam;
    [SerializeField] private Camera_ZoomController cameraZoom;

    protected override void Start()
    {
        if (string.IsNullOrEmpty(sceneName)) sceneName = "dashijie";

        base.Start();
    }

    /// <summary>切角色后由 <see cref="DashijiePlayerSwitcher"/> 通知，存档改用新角色与新相机目标</summary>
    public void BindPlayer(Player newPlayer)
    {
        player = newPlayer;
        playerRoot = newPlayer != null ? newPlayer.transform : null;
    }

    protected override Transform ResolvePlayerRoot()
    {
        // 世界内切角色会销毁旧 Player 实例，缓存失效时重新找一次
        if (player == null) player = FindObjectOfType<Player>();
        return player != null ? player.transform : null;
    }

    protected override void ApplyPosition(Vector3 position, Quaternion rotation)
    {
        if (player == null) player = FindObjectOfType<Player>();
        if (player == null) return;

        // KCC 角色：走 Motor 落位（TransientPosition 才是真值），并清掉驱动里未消费的根运动，
        // 否则恢复后会被上一帧的动画位移再推一下
        KinematicCharacterMotor motor = player.GetComponent<KinematicCharacterMotor>();
        if (motor != null)
        {
            motor.SetPositionAndRotation(position, rotation, true);
            player.GetComponent<AnbiWorldKccDriver>()?.ResetRootMotion();
            return;
        }

        // CharacterController 直接改 Transform 会被下一帧 Move 拉回：先禁用再落位
        CharacterController controller = player.GetComponent<CharacterController>();
        if (controller != null) controller.enabled = false;

        player.transform.SetPositionAndRotation(position, rotation);

        if (controller != null) controller.enabled = true;
    }

    protected override void CaptureCamera(out float yaw, out float pitch, out float distance)
    {
        yaw = 0f;
        pitch = 0f;
        distance = 0f;

        if (playCam == null) return;

        CinemachinePOV pov = playCam.GetCinemachineComponent<CinemachinePOV>();
        if (pov != null)
        {
            yaw = pov.m_HorizontalAxis.Value;
            pitch = pov.m_VerticalAxis.Value;
        }

        if (cameraZoom != null) distance = cameraZoom.currentDistance;
    }

    protected override void ApplyCamera(float yaw, float pitch, float distance)
    {
        if (playCam == null) return;

        CinemachinePOV pov = playCam.GetCinemachineComponent<CinemachinePOV>();
        if (pov != null)
        {
            pov.m_HorizontalAxis.Value = yaw;
            pov.m_VerticalAxis.Value = pitch;
        }

        if (cameraZoom != null && distance > 0f)
        {
            cameraZoom.SetZoom(distance, cameraZoom.ExternalSpeedVariable);

            // 直接落位：恢复时相机不该从默认距离慢慢推过去
            CinemachineFramingTransposer transposer =
                playCam.GetCinemachineComponent<CinemachineFramingTransposer>();
            if (transposer != null) transposer.m_CameraDistance = distance;
        }
    }
}
