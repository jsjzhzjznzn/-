using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 本地世界表现层暂停：栈式分层记账，栈非空即 Time.timeScale=0。
///
/// 边界约定：
/// - "暂停"只作用于本地 3D 世界（动画/物理/粒子/DOTween 默认档/相机混合全部吃缩放时间，自动冻结）；
/// - UI 侧用 unscaled 时间保持运转：Animator→UnscaledTime、DOTween→SetUpdate(true)、
///   计时/协程→unscaledDeltaTime / WaitForSecondsRealtime（BagUIController 加载圆环即此约定）；
/// - 服务器权威系统（背包/任务/聊天/邮件）与服务端进程永远真实时间，不受此影响——
///   将来若做服务器权威战斗，"暂停"应降级为客户端表现冻结+输入锁，服务器照跑。
/// </summary>
public static class PauseManager
{
    /// <summary> 暂停来源分层：一个面板一层，多层叠加时关掉中间层不会误恢复时间 </summary>
    public enum Layer
    {
        Settings,
        Bag,
        Mail,
        Quest,
        Character,
        Achievement,
        Friend,
        Leaderboard,
        Chat,
        Cutscene,
        Other,
    }

    private static readonly Stack<Layer> stack = new Stack<Layer>();
    private static float savedTimeScale = 1f;

    // ---- 顿帧（hit-stop）账本 ----
    // 顿帧和"暂停"都改 Time.timeScale，必须放在同一处结算，否则会互相覆盖：
    // 典型事故是顿帧结束把 timeScale 写回 1，把打开着的暂停面板"解冻"。
    private static float hitStopScale = 1f;
    private static int hitStopCount;

    /// <summary> 是否处于任意一层暂停中 </summary>
    public static bool IsPaused => stack.Count > 0;

    /// <summary> 当前是否有顿帧在进行（供调试/断言用） </summary>
    public static bool IsHitStopping => hitStopCount > 0;

    /// <summary> 暂停状态变化（true=进入暂停）。订阅方据此做输入门禁/指针状态/提示显隐等显式处理 </summary>
    public static event Action<bool> PausedChanged;

    /// <summary> 计入一层暂停（同一层重复 Push 幂等忽略） </summary>
    public static void Push(Layer layer)
    {
        if (stack.Contains(layer)) return;

        // 第一次暂停前记下"正常倍速"，为延迟恢复/慢动作预留语义。
        // 只有在既没暂停、也没顿帧时读取 Time.timeScale 才是可信的正常倍速；
        // 顿帧期间打开面板时若照抄当前值，会把 0.03 当成正常倍速，关面板后世界就永远慢放。
        if (stack.Count == 0 && hitStopCount == 0) savedTimeScale = Time.timeScale;

        stack.Push(layer);
        Apply();
    }

    /// <summary> 移除一层暂停（不管它在栈的哪个位置）；栈空才恢复世界时间 </summary>
    public static void Pop(Layer layer)
    {
        if (!stack.Contains(layer)) return;

        var keep = new List<Layer>(stack); // 迭代顺序：栈顶→栈底
        keep.Remove(layer);
        stack.Clear();
        for (int i = keep.Count - 1; i >= 0; i--) stack.Push(keep[i]); // 逆序压回，保持原栈序
        Apply();
    }

    public static bool Contains(Layer layer) => stack.Contains(layer);

    /// <summary>
    /// 计入一次顿帧（命中停顿）：多段同时命中时取最小倍速，引用计数归零才解除。
    /// 暂停期间只记账不改 timeScale——暂停的 0 优先，关掉暂停后若仍在顿帧会回到顿帧倍速。
    /// </summary>
    public static void PushHitStop(float scale)
    {
        hitStopCount++;
        hitStopScale = Mathf.Min(hitStopScale, Mathf.Clamp(scale, 0f, 1f));
        Apply();
    }

    /// <summary> 解除一次顿帧；计数归零才把倍速还原 </summary>
    public static void PopHitStop()
    {
        hitStopCount = Mathf.Max(0, hitStopCount - 1);
        if (hitStopCount == 0) hitStopScale = 1f;
        Apply();
    }

    private static void Apply()
    {
        bool paused = stack.Count > 0;
        // 优先顺序：暂停(0) > 顿帧(接近 0) > 正常倍速
        Time.timeScale = paused
            ? 0f
            : (hitStopCount > 0 ? hitStopScale : savedTimeScale);
        PausedChanged?.Invoke(paused);
    }

#if UNITY_EDITOR
    // 关闭 Domain Reload 时静态字段会跨 Play 会话残留，进 Play 前复位
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetState()
    {
        stack.Clear();
        savedTimeScale = 1f;
        hitStopScale = 1f;
        hitStopCount = 0;
        PausedChanged = null;
        Time.timeScale = 1f;
    }
#endif
}
