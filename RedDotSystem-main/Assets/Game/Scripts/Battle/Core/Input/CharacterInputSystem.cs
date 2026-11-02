using MieMieFrameWork.M_InputSystem;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 角色输入系统（单例）
/// 封装 playerinputaction.inputactions 生成的强类型类 Playerinputaction，
/// 统一对角色脚本/状态机暴露输入状态
/// </summary>
public class CharacterInputSystem : MieMieFrameWork.SingletonBehaviour<CharacterInputSystem>
{
    public Playerinputaction inputActions;

    protected override void Awake()
    {
        base.Awake();
        if (inputActions == null)
            inputActions = new Playerinputaction();
    }

    private void OnEnable()
    {
        inputActions?.Enable();
        SubscribeBufferFeed();
    }

    private void OnDisable()
    {
        UnsubscribeBufferFeed();
        inputActions?.Disable();
    }

    // ==================== 输入缓冲写入（全局唯一入口） ====================
    // 按下即入缓冲，不看状态窗口：窗口关死期间的按键也不会凭空消失，
    // 具体能不能执行由消费方（连招检查点 / 待机轮询）判断

    private void SubscribeBufferFeed()
    {
        if (inputActions == null)
            return;

        inputActions.player.attack.started += OnAttackStartedFeed;
        inputActions.player.defense.started += OnDefenseStartedFeed;
        inputActions.player.dash.started += OnDashStartedFeed;
        inputActions.player.skill.started += OnSkillStartedFeed;
    }

    private void UnsubscribeBufferFeed()
    {
        if (inputActions == null)
            return;

        inputActions.player.attack.started -= OnAttackStartedFeed;
        inputActions.player.defense.started -= OnDefenseStartedFeed;
        inputActions.player.dash.started -= OnDashStartedFeed;
        inputActions.player.skill.started -= OnSkillStartedFeed;
    }

    private void OnAttackStartedFeed(InputAction.CallbackContext context)
    {
        if (PlayerInputGate.IsBlocked) return;
        PushBuffer(E_InputType.Attack);
    }

    private void OnDefenseStartedFeed(InputAction.CallbackContext context)
    {
        // 门禁拦截时右键既不进缓冲也不置按下标记，避免面板关闭后补放闪避
        if (PlayerInputGate.IsBlocked) return;

        PushBuffer(E_InputType.HeavyAttack);
        defensePressed = true;   // 右键按下沿：给闪避用（triggered 只有一帧，快速点击会漏）
    }

    private void OnDashStartedFeed(InputAction.CallbackContext context)
    {
        if (PlayerInputGate.IsBlocked) return;
        PushBuffer(E_InputType.Dash);
    }

    private void OnSkillStartedFeed(InputAction.CallbackContext context)
    {
        if (PlayerInputGate.IsBlocked) return;
        PushBuffer(E_InputType.Skill);
    }

    /// <summary>右键"按下过"标记（由 started 回调置位）</summary>
    private bool defensePressed;

    /// <summary>
    /// 取出并清除"右键按下过"标记。读取即消费：每帧调一次，按下的那一帧一定拿得到，
    /// 不像 <see cref="HeavyAttack"/>（triggered）那样会被一帧内的按下+抬起吞掉。
    /// </summary>
    public bool ConsumeDefensePressed()
    {
        // 门禁期间按键不生效，同时清掉之前残留的按下标记
        if (PlayerInputGate.IsBlocked)
        {
            defensePressed = false;
            return false;
        }

        bool pressed = defensePressed;
        defensePressed = false;
        return pressed;
    }

    /// <summary>
    /// 写入框架输入缓冲；ModuleHub 尚未启动时静默跳过（连招退化为只吃实时输入，不崩）
    /// </summary>
    private static void PushBuffer(E_InputType inputType)
    {
        // 暂停（timeScale=0）期间不写入：缓冲按 deltaTime 递减，暂停时不流逝，
        // 若照写会一直留到解除暂停后自动出手。顿帧是 0.03 不是 0，照常写入
        if (Time.timeScale <= 0f)
            return;

        var buffer = InputBuffer3D.Instance;
        if (buffer == null)
            return;

        buffer.BufferInput(inputType);
    }

    // ==================== 输入封装 ====================
    // 所有属性先看门禁：面板打开/世界暂停/对话锁定时统一返回空输入，
    // 由 PlayerInputGate 一处记账，避免每个消费方各写一遍 IsPaused 判断。

    // 移动
    public Vector2 Movement => PlayerInputGate.IsBlocked ? Vector2.zero : inputActions.player.move.ReadValue<Vector2>();
    public Vector2 Look     => PlayerInputGate.IsBlocked ? Vector2.zero : inputActions.player.look.ReadValue<Vector2>();

    // 冲刺（Shift）
    public bool dashHeld => !PlayerInputGate.IsBlocked && inputActions.player.dash.phase == InputActionPhase.Performed;
    public bool dashPressed => !PlayerInputGate.IsBlocked && inputActions.player.dash.triggered;

    // 轻攻击（鼠标左键）
    public bool Attack          => !PlayerInputGate.IsBlocked && inputActions.player.attack.triggered;
    public bool Attack_Continue => !PlayerInputGate.IsBlocked && inputActions.player.attack.phase == InputActionPhase.Performed;

    // 防御（鼠标右键）
    public bool HeavyAttack          => !PlayerInputGate.IsBlocked && inputActions.player.defense.triggered;
    public bool HeavyAttack_Continue => !PlayerInputGate.IsBlocked && inputActions.player.defense.phase == InputActionPhase.Performed;

    // 技能（E）
    public bool Skill          => !PlayerInputGate.IsBlocked && inputActions.player.skill.triggered;
    public bool Skill_Continue => !PlayerInputGate.IsBlocked && inputActions.player.skill.phase == InputActionPhase.Performed;
}
