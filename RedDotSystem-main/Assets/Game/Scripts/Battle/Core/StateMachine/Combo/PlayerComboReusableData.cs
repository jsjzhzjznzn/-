using MieMieFrameWork.M_InputSystem;
using UnityEngine;

/// <summary>
/// 连击状态间共享的数据容器
/// 存放需要在多个连击状态间传递的字段（当前连招、攻击段数、输入缓冲、冷却开关、判定信息等）
/// 由 PlayerComboStateMachine 创建并持有，各连击状态通过状态机访问
/// </summary>
public class PlayerComboReusableData
{
    // ==================== 攻击判定相关 ====================

    /// <summary>攻击判定用的相机参考（未配置时回退 Camera.main）</summary>
    public Transform cameraTransform { get; set; }

    /// <summary>攻击判定检测方向</summary>
    public Vector3 detectionDir { get; set; }

    /// <summary>攻击判定检测起点</summary>
    public Vector3 detectionOrigin { get; set; }

    // ==================== 当前连招数据 ====================

    /// <summary>当前激活的连招容器（轻攻击/重攻击/处决等）</summary>
    public ComboContainerData currentCombo { get; set; }

    /// <summary>当前执行的技能配置（含招式与冷却；轻击时保持 null）</summary>
    public SkillDefinition currentSkill { get; set; }

    // ==================== 连招下标 ====================

    /// <summary>当前攻击段数（comboDates 下标，0=第一段），变化时通知订阅方（动画/UI）</summary>
    public BindableProperty<int> currentIndex { get; set; } = new BindableProperty<int>();

    // ==================== 连击流程开关 ====================

    /// <summary>是否处于可输入的攻击窗口（相当于攻击的预备/收招阶段）</summary>
    public bool canInput { get; set; }

    /// <summary>冷却结束开关：动画播够最短时间后可发动下一段（相当于攻击冷却）</summary>
    public bool canATK { get; set; }

    /// <summary>
    /// 一次性消费攻击缓冲：按键按下时由 CharacterInputSystem 统一写入框架 InputBuffer3D，
    /// 这里只负责"在窗口里查一次并清空"，过期判定与时长的配置都交给缓冲模块
    /// （默认 0.4s，覆盖"按键 → linkCancelTime 检查点"的常见间隔）
    /// </summary>
    public bool ConsumeATKInput()
    {
        var buffer = InputBuffer3D.Instance;
        return buffer != null && buffer.ConsumeBuffer(E_InputType.Attack);
    }

    /// <summary>是否允许连下一段连招</summary>
    public bool canLink { get; set; }

    /// <summary>是否可被移动打断</summary>
    public bool canMoveInterrupt { get; set; }

    /// <summary>是否可触发 QTE</summary>
    public bool canQTE { get; set; }
}
