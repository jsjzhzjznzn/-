#if UNITY_EDITOR
using GAS.Runtime;
using UnityEditor;

namespace GAS.Editor
{
    /// <summary>
    /// 组合根: 把时间轴编辑窗口的打开入口注入到运行时程序集。
    /// 取代旧的 Assembly.Load("com.exhard.exgas.editor") + GetType + GetMethod 反射调用,
    /// 运行时资产按钮与编辑器窗口之间不再有字符串耦合, 重构任何一端都能获得编译期检查。
    /// </summary>
    [InitializeOnLoad]
    internal static class TimelineEditorInjection
    {
        static TimelineEditorInjection()
        {
            TimelineAbilityAssetBase.OnEditTimelineRequested -= OpenTimelineEditor;
            TimelineAbilityAssetBase.OnEditTimelineRequested += OpenTimelineEditor;
        }

        private static void OpenTimelineEditor(TimelineAbilityAssetBase asset)
        {
            AbilityTimelineEditorWindow.ShowWindow(asset);
        }
    }
}
#endif
