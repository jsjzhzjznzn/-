#if UNITY_EDITOR
using Miemie.DialogSystem;
using MiMieEventBus.Editor;

namespace Miemie.DialogSystem.Editor
{
    /// <summary>
    /// 叙事总线调试注册：把 NarrytiveBus 挂进 MmEventBusWindow（菜单 Tools/MmEventBusWindow）
    /// </summary>
    static class NarrativeEventBusDebugRegistration
    {
        [UnityEditor.InitializeOnLoadMethod]
        static void Register()
        {
            MmEventBusWindow.Register("叙事总线 NarrytiveBus", () => NarrativeEventBus.NarrytiveBus);
        }
    }
}
#endif
