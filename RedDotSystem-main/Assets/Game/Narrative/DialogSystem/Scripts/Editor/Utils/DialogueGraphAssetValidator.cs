#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Miemie.DialogSystem.Editor
{
    /// <summary>
    /// 对话图跨资产校验工具。
    /// DialogueGraphValidator 只校验单张图内部，这里补"整项目"级别的规则：
    /// graphId 必须全局唯一——运行时按 ID 找图时重复 ID 会静默取到另一张图（症状是剧情串台，
    /// 且靠 Inspector 硬引用时完全察觉不到）。
    /// 纯编辑器工具，不改任何资产，也不参与运行时。
    /// </summary>
    public static class DialogueGraphAssetValidator
    {
        /// <summary>菜单入口：扫描全部对话图并打印重复的 graphId</summary>
        [MenuItem("Tools/对话/校验全部对话图（graphId 唯一性）")]
        private static void ValidateAllGraphIds()
        {
            var byId = new Dictionary<int, List<string>>();
            string[] guids = AssetDatabase.FindAssets("t:DialogueGraph");

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                DialogueGraph graph = AssetDatabase.LoadAssetAtPath<DialogueGraph>(path);
                if (graph == null) continue;

                if (!byId.TryGetValue(graph.ConfigId, out var list))
                {
                    list = new List<string>();
                    byId[graph.ConfigId] = list;
                }
                list.Add(path);
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"=== 对话图 graphId 校验（共 {guids.Length} 张图）===");

            int duplicateCount = 0;
            foreach (KeyValuePair<int, List<string>> pair in byId)
            {
                if (pair.Value.Count <= 1) continue;

                duplicateCount++;
                sb.AppendLine($"[错误] graphId={pair.Key} 被 {pair.Value.Count} 张图使用：");
                foreach (string path in pair.Value)
                    sb.AppendLine($"    {path}");
            }

            sb.AppendLine(duplicateCount == 0
                ? "校验通过：所有 graphId 唯一"
                : $"=== 结束：发现 {duplicateCount} 组重复 graphId ===");

            if (duplicateCount == 0) Debug.Log(sb.ToString());
            else Debug.LogError(sb.ToString());
        }

        /// <summary>
        /// 收集重复的 graphId（供测试/CI 断言使用，不打印）
        /// </summary>
        /// <returns>返回值类型：Dictionary&lt;int, List&lt;string&gt;&gt;，用途：graphId → 使用它的资产路径（仅含重复项）</returns>
        public static Dictionary<int, List<string>> CollectDuplicatedGraphIds()
        {
            var byId = new Dictionary<int, List<string>>();
            foreach (string guid in AssetDatabase.FindAssets("t:DialogueGraph"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                DialogueGraph graph = AssetDatabase.LoadAssetAtPath<DialogueGraph>(path);
                if (graph == null) continue;

                if (!byId.TryGetValue(graph.ConfigId, out var list))
                {
                    list = new List<string>();
                    byId[graph.ConfigId] = list;
                }
                list.Add(path);
            }

            var duplicated = new Dictionary<int, List<string>>();
            foreach (KeyValuePair<int, List<string>> pair in byId)
            {
                if (pair.Value.Count > 1) duplicated[pair.Key] = pair.Value;
            }
            return duplicated;
        }
    }
}
#endif
