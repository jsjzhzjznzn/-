using Cinemachine;
using Miemie.DialogSystem;
using UnityEngine;

/// <summary>
/// 对话相机导演：玩法第三人称 ↔ 对话正反打。
///
/// 对话机位不靠手摆，而是按“玩家/NPC 两个人形”现场计算——
///   NPC 正打：站在玩家肩后偏右，看向 NPC 头部（画面带入玩家肩）
///   玩家反打：站在 NPC 肩后偏左，看向玩家头部
/// 两人在对话开始时会互相转向面对面，所以两种机位都稳定。
///
/// 取景距离按两人实际间距自适应：贴得太近时（玩家走到 NPC 身上）沿用固定 0.55m 后拉
/// 会把镜头顶进说话者的头发/身体里（画面整片发黑只剩剪影），所以近距离时把机位再往后拉。
/// 说话人切换用硬切（Cut），进出对话用 0.35s 缓动（与原神式镜头节奏一致）。
/// </summary>
public class GenshinCameraDirector : MonoBehaviour
{
    [Header("机位")]
    [SerializeField] private CinemachineVirtualCamera playCam;
    [SerializeField] private CinemachineVirtualCamera npcCam;
    [SerializeField] private CinemachineVirtualCamera playerCam;

    [Header("对话取景")]
    [Tooltip("头部高度（相对角色根节点）")]
    [SerializeField] private float headHeight = 1.55f;
    [Tooltip("机位相对说话者后退的基础距离")]
    [SerializeField] private float shoulderBack = 0.55f;
    [Tooltip("机位相对说话者侧移的基础距离（正反打朝相反方向，形成过肩构图）")]
    [SerializeField] private float shoulderSide = 0.45f;
    [Tooltip("机位相对头部再抬高多少")]
    [SerializeField] private float shoulderUp = 0.12f;
    [Tooltip("这个间距以内开始额外后拉机位（米）")]
    [SerializeField] private float comfortableDistance = 1.6f;
    [SerializeField] private float dialogueFov = 40f;

    [Header("混合")]
    [SerializeField] private float enterExitBlend = 0.35f;

    private CinemachineBrain brain;

    private void Awake()
    {
        Camera main = Camera.main;
        brain = main != null ? main.GetComponent<CinemachineBrain>() : null;

        SetActiveCam(playCam != null && playCam.gameObject.activeSelf);
    }

    /// <summary>进入对话：先拍 NPC（开场第一句通常是 NPC），机位按两人位置现场计算</summary>
    public void EnterDialogue(Transform player, Transform npc)
    {
        BuildDialogueShots(player, npc);
        SetBlend(CinemachineBlendDefinition.Style.EaseInOut, enterExitBlend);

        if (playCam != null) playCam.gameObject.SetActive(false);
        if (playerCam != null) playerCam.gameObject.SetActive(false);
        if (npcCam != null) npcCam.gameObject.SetActive(true);
    }

    /// <summary>说话人切换：正反打（硬切，避免同一段对话里镜头来回滑）</summary>
    public void SwitchSpeaker(SpeakEnums speakType)
    {
        switch (speakType)
        {
            case SpeakEnums.NPC:
                SetBlend(CinemachineBlendDefinition.Style.Cut, 0f);
                if (npcCam != null) npcCam.gameObject.SetActive(true);
                if (playerCam != null) playerCam.gameObject.SetActive(false);
                break;

            case SpeakEnums.Player:
                SetBlend(CinemachineBlendDefinition.Style.Cut, 0f);
                if (npcCam != null) npcCam.gameObject.SetActive(false);
                if (playerCam != null) playerCam.gameObject.SetActive(true);
                break;

            // Node（旁白类）保持当前机位
        }
    }

    /// <summary>退出对话：回到玩法相机</summary>
    public void ExitDialogue()
    {
        SetBlend(CinemachineBlendDefinition.Style.EaseInOut, enterExitBlend);

        if (npcCam != null) npcCam.gameObject.SetActive(false);
        if (playerCam != null) playerCam.gameObject.SetActive(false);
        if (playCam != null) playCam.gameObject.SetActive(true);
    }

    /// <summary>按两人位置计算两台对话机位（写入 transform，清空 Follow/LookAt 以免 Body/Aim 再插一手）</summary>
    private void BuildDialogueShots(Transform player, Transform npc)
    {
        if (player == null || npc == null) return;

        Vector3 playerHead = player.position + Vector3.up * headHeight;
        Vector3 npcHead = npc.position + Vector3.up * headHeight;

        Vector3 d = npcHead - playerHead;
        d.y = 0f;
        if (d.sqrMagnitude < 0.0001f) d = player.forward;
        d.Normalize();

        Vector3 right = Vector3.Cross(Vector3.up, d).normalized;

        // 间距越近，机位越要往后拉，避免镜头贴进说话者的头发/身体
        float distance = Vector3.Distance(
            new Vector3(playerHead.x, 0f, playerHead.z),
            new Vector3(npcHead.x, 0f, npcHead.z));
        float extra = Mathf.Max(0f, comfortableDistance - distance);
        float back = shoulderBack + extra;
        float side = shoulderSide + extra * 0.35f;

        // 正打：站在玩家肩后（-d）偏右，看向 NPC 头部
        Place(npcCam, playerHead - d * back + right * side + Vector3.up * shoulderUp, npcHead);

        // 反打：站在 NPC 肩后（+d）偏左，看向玩家头部
        Place(playerCam, npcHead + d * back - right * side + Vector3.up * shoulderUp, playerHead);
    }

    private void Place(CinemachineVirtualCamera cam, Vector3 position, Vector3 lookAt)
    {
        if (cam == null) return;

        cam.Follow = null;
        cam.LookAt = null;

        Vector3 forward = lookAt - position;
        if (forward.sqrMagnitude < 0.0001f) forward = transform.forward;

        cam.transform.SetPositionAndRotation(position, Quaternion.LookRotation(forward.normalized, Vector3.up));
        cam.m_Lens.FieldOfView = dialogueFov;
    }

    private void SetBlend(CinemachineBlendDefinition.Style style, float duration)
    {
        if (brain == null) return;
        brain.m_DefaultBlend = new CinemachineBlendDefinition(style, duration);
    }

    private void SetActiveCam(bool gameplay)
    {
        if (playCam != null) playCam.gameObject.SetActive(gameplay);
        if (npcCam != null) npcCam.gameObject.SetActive(!gameplay);
        if (playerCam != null) playerCam.gameObject.SetActive(false);
    }
}
