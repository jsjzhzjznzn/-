#if UNITY_EDITOR
using UnityEditor;

namespace SkierFrameworkUIMigration
{
    /// <summary>邮件面板 → Assets/Game/UI/Mail/MailView.prefab（入口 MailEntryButton → HomeView.mailButton）</summary>
    public static class MailViewBuilder
    {
        [MenuItem("Tools/UI/构建 MailView 预制体")]
        public static void Build()
        {
            PanelMigrator.Migrate(new PanelMigrator.Options
            {
                panelPath = "youjian/MailPanel",
                prefabPath = "Assets/Game/UI/Mail/MailView.prefab",
                viewType = typeof(MailView),
                entryName = "MailEntryButton",
                entryField = "mailButton",
                canvasToDelete = "youjian",
            });
        }
    }
}
#endif
