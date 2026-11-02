using UnityEngine;

/// <summary>
/// 暂停面板基类：面板激活即 Push 自己的暂停层，失活即 Pop。
/// 多面板叠加时由 PauseManager 记账，最后一个关闭才恢复世界时间。
/// 要求面板默认 inactive，用 SetActive 开关。
/// </summary>
public class PausePanel : MonoBehaviour
{
    [SerializeField] protected PauseManager.Layer layer = PauseManager.Layer.Other;

    private bool pushed;

    protected virtual void OnEnable()
    {
        PauseManager.Push(layer);
        pushed = true;
    }

    protected virtual void OnDisable()
    {
        if (!pushed) return;
        pushed = false;
        PauseManager.Pop(layer);
    }
}
