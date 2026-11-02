using UnityEngine;

/// <summary>
/// 原神demo·玩家控制器：CharacterController 第三人称移动
/// WASD 相对相机方向移动 + 平滑转向 + 重力；动画用 float 参数 Speed 驱动 Idle/RunFwd
/// 对话期间由 Director 置 InputLocked（保留重力）
/// </summary>
public class GenshinPlayerController : MonoBehaviour
{
    [SerializeField] private float moveSpeed = 3.5f;
    [SerializeField] private float turnSpeed = 10f;
    [SerializeField] private float animBlendSpeed = 8f;
    [SerializeField] private float gravity = -9.8f;

    private CharacterController cc;
    private Animator animator;
    private Transform cam;
    private float verticalVel;
    private float animSpeed;
    private bool hasSpeedParam;

    /// <summary> 对话等系统锁定移动输入（重力仍然生效） </summary>
    public bool InputLocked { get; set; }

    /// <summary> 测试钩子：非空时用它替代键盘输入（E2E 驱动用） </summary>
    public Vector2? TestMoveInput { get; set; }

    private void Awake()
    {
        cc = GetComponent<CharacterController>();
        animator = GetComponent<Animator>();
        hasSpeedParam = animator != null &&
            System.Array.Exists(animator.parameters, p => p.name == "Speed");
    }

    private void OnEnable()
    {
        // 相机由 CinemachineBrain 驱动，延迟到 OnEnable 再取主相机
        if (Camera.main != null) cam = Camera.main.transform;
    }

    private void Update()
    {
        Vector2 input = InputLocked ? Vector2.zero : ReadInput();
        Vector3 move = Vector3.zero;

        if (input.sqrMagnitude > 0.01f)
        {
            Vector3 fwd = cam != null
                ? Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized
                : Vector3.forward;
            Vector3 right = cam != null
                ? Vector3.ProjectOnPlane(cam.right, Vector3.up).normalized
                : Vector3.right;
            move = (fwd * input.y + right * input.x).normalized * moveSpeed;

            Quaternion targetRot = Quaternion.LookRotation(move);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, turnSpeed * Time.deltaTime);
        }

        // 贴地时给微小下压，保证 isGrounded 稳定
        if (cc.isGrounded) verticalVel = -2f;
        else verticalVel += gravity * Time.deltaTime;
        move.y = verticalVel;

        cc.Move(move * Time.deltaTime);

        float targetSpeed = input.sqrMagnitude > 0.01f ? 1f : 0f;
        animSpeed = Mathf.MoveTowards(animSpeed, targetSpeed, animBlendSpeed * Time.deltaTime);
        if (animator != null && hasSpeedParam) animator.SetFloat("Speed", animSpeed);
    }

    private Vector2 ReadInput()
    {
        if (TestMoveInput.HasValue) return TestMoveInput.Value;
        return new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
    }
}
