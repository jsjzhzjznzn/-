#if UNITY_EDITOR
namespace GAS.Editor
{
    using GAS.General;
    using UnityEditor;

    public class AbilityTimelineEditorConfig
    {
        public int FrameUnitWidth = 10;
        public const int StandardFrameUnitWidth = 1;
        public const int MaxFrameUnitLevel= 20;
        public const float MinTimerShaftFrameDrawStep = 5;
        public int DefaultFrameRate => GASTimer.FrameRate;

        private const string PreviewScenePrefKey = "GAS.TimelineAbilityEditor.PreviewScenePath";

        /// <summary>
        /// 时间轴编辑器的技能预览场景路径(可通过 EditorPrefs 持久化覆盖)。
        /// 为空或场景不存在时, "预览场景"按钮会给出警告并留在当前场景。
        /// </summary>
        public string PreviewScenePath
        {
            get => EditorPrefs.GetString(PreviewScenePrefKey, DefaultPreviewScenePath);
            set => EditorPrefs.SetString(PreviewScenePrefKey, value);
        }

        /// <summary>默认预览场景: 本项目的 GAS 测试场景(带技能预览用角色/Animator)。</summary>
        public const string DefaultPreviewScenePath = "Assets/Scenes/gas.scene";
    }
}
#endif
