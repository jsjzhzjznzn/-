#if UNITY_EDITOR
using UnityEditor;

namespace SkierFrameworkUIMigration
{
    /// <summary>阿福面板 → Assets/Game/UI/Agent/AgentView.prefab（入口 AgentEntryButton → HomeView.agentButton）</summary>
    public static class AgentViewBuilder
    {
        [MenuItem("Tools/UI/构建 AgentView 预制体")]
        public static void Build()
        {
            PanelMigrator.Migrate(new PanelMigrator.Options
            {
                panelPath = "agent/AgentPanel",
                prefabPath = "Assets/Game/UI/Agent/AgentView.prefab",
                viewType = typeof(AgentView),
                entryName = "AgentEntryButton",
                entryField = "agentButton",
                canvasToDelete = "agent",
            });
        }
    }
}
#endif
