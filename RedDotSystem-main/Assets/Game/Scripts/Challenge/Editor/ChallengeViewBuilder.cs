#if UNITY_EDITOR
using UnityEditor;

namespace SkierFrameworkUIMigration
{
    /// <summary>挑战面板 → Assets/Game/UI/Challenge/ChallengeView.prefab（入口 ChallengeEntryButton → HomeView.challengeButton）</summary>
    public static class ChallengeViewBuilder
    {
        [MenuItem("Tools/UI/构建 ChallengeView 预制体")]
        public static void Build()
        {
            PanelMigrator.Migrate(new PanelMigrator.Options
            {
                panelPath = "FriendCanvas/FriendUIRoot/ChallengePanel",
                prefabPath = "Assets/Game/UI/Challenge/ChallengeView.prefab",
                viewType = typeof(ChallengeView),
                entryName = "ChallengeEntryButton",
                entryField = "challengeButton",
                canvasToDelete = "FriendCanvas",
            });
        }
    }
}
#endif
