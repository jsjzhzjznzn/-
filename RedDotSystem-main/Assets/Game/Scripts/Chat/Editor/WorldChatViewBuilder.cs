#if UNITY_EDITOR
using UnityEditor;

namespace SkierFrameworkUIMigration
{
    /// <summary>世界聊天 → Assets/Game/UI/Chat/WorldChatView.prefab（入口 WorldEntryButton → HomeView.worldButton）</summary>
    public static class WorldChatViewBuilder
    {
        [MenuItem("Tools/UI/构建 WorldChatView 预制体")]
        public static void Build()
        {
            PanelMigrator.Migrate(new PanelMigrator.Options
            {
                panelPath = "shijie/WorldPanel",
                prefabPath = "Assets/Game/UI/Chat/WorldChatView.prefab",
                viewType = typeof(WorldChatView),
                entryName = "WorldEntryButton",
                entryField = "worldButton",
                canvasToDelete = "shijie",
            });
        }
    }
}
#endif
