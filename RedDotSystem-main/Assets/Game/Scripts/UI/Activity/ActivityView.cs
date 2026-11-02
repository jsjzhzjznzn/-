using Cysharp.Threading.Tasks;
using SkierFramework;
using UnityEngine;

/// <summary>
/// 活动面板的框架壳（NormalLayer 普通面板）。
/// 面板内容仍由热更包（ActivityPanel.prefab + activity_main.lua）现场构建，本视图只负责"在框架里有个落点"：
/// 层级排序、非窗口互斥（打开时隐藏主页）、关闭/释放语义全部交给 UIManager，
/// LuaActivityHost 不再自己建带 sortingOrder 的画布。
/// </summary>
public class ActivityView : UIView
{
    /// <summary>热更内容落点（预制体里的全屏空节点，由 UIControlData 按字段名绑定）</summary>
    [ControlBinding] public RectTransform contentRoot;

    public override void OnOpen(object userData)
    {
        base.OnOpen(userData);

        if (ActivityShell.Instance == null)
        {
            Debug.LogError("[活动] ActivityShell 未自举，活动面板无法打开");
            CloseSelfNextFrameAsync().Forget();
            return;
        }

        ActivityShell.Instance.AttachView(this);
        ActivityShell.Instance.OpenActivity();
    }

    public override void OnClose()
    {
        if (ActivityShell.Instance != null)
            ActivityShell.Instance.OnViewClosed(this);
        base.OnClose();
    }

    /// <summary>
    /// 异常态的自我关闭：不能在本帧 OnOpen 栈里直接 Close（框架 TrueOpen 尚未走完，会在关闭后又执行 OnResume）。
    /// </summary>
    private async UniTaskVoid CloseSelfNextFrameAsync()
    {
        await UniTask.NextFrame();
        if (UIManager.Instance != null)
            await UIManager.Instance.Close(UIType.ActivityView);
    }
}
