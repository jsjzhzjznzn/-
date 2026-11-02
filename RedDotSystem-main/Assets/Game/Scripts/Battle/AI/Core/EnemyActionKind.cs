/// <summary>
/// 敌人动作层的动作种类。
/// 只描述"身体在做什么"，不描述"为什么做"——后者是决策层（行为树/内置决策）的职责。
/// </summary>
public enum EnemyActionKind
{
    None = 0,
    Idle = 1,
    Move = 2,
    Strafe = 3,
    Backstep = 4,
    FaceTarget = 5,
    Attack = 6,
    Hurt = 7,
    Stagger = 8,
    Dead = 9,
    PhaseIntro = 10,
    Taunt = 11,
}
