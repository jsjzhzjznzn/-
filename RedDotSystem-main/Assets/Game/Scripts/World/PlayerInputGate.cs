using System.Collections.Generic;

/// <summary>
/// 角色输入门禁：按来源记账，任一来源占位时 <see cref="CharacterInputSystem"/> 对角色返回空输入。
///
/// 与 PauseManager 的区别：暂停只冻结时间（动画/物理），Update 仍在跑、输入沿仍会打到状态机；
/// 门禁在输入层直接断开，用于"世界暂停/面板打开/对话"这类不该让角色出招的场景。
/// </summary>
public static class PlayerInputGate
{
    /// <summary>占用来源（WorldPause = 大世界面板暂停；Dialogue = 对话锁定，本轮预留给对话接入时使用）</summary>
    public enum Source
    {
        WorldPause,
        Dialogue,
    }

    private static readonly HashSet<Source> blockers = new();

    /// <summary>当前是否有任一来源占位（角色输入应返回空值）</summary>
    public static bool IsBlocked => blockers.Count > 0;

    /// <summary>计入一个占用来源（重复 Push 幂等）</summary>
    public static void Push(Source source) => blockers.Add(source);

    /// <summary>移除一个占用来源（未占用时无副作用）</summary>
    public static void Pop(Source source) => blockers.Remove(source);

    /// <summary>清空所有占用来源（场景卸载兜底，避免把锁带进下一个场景）</summary>
    public static void Clear() => blockers.Clear();

#if UNITY_EDITOR
    // 关闭 Domain Reload 时静态字段会跨 Play 会话残留，进 Play 前复位
    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetState() => blockers.Clear();
#endif
}
