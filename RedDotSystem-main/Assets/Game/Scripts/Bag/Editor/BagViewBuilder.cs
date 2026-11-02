#if UNITY_EDITOR
using UnityEditor;

namespace SkierFrameworkUIMigration
{
    /// <summary>背包 → Assets/Game/UI/Bag/BagView.prefab（入口已在 HomeView.bagButton，本面板无入口）</summary>
    public static class BagViewBuilder
    {
        [MenuItem("Tools/UI/构建 BagView 预制体")]
        public static void Build()
        {
            PanelMigrator.Migrate(new PanelMigrator.Options
            {
                panelPath = "beibao/BagPanel",
                prefabPath = "Assets/Game/UI/Bag/BagView.prefab",
                viewType = typeof(BagView),
                entryName = null,
                entryField = null,
                canvasToDelete = "beibao",
            });
        }
    }
}
#endif
