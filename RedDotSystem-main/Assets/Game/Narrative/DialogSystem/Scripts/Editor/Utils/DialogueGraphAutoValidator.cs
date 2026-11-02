#if UNITY_EDITOR
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Miemie.DialogSystem.Editor
{
    /// <summary>
    /// 对话图自动校验：保存资产时触发（AssetModificationProcessor），
    /// 违规集中输出到 Console（点击行可定位到图资产），无需手动点按钮；
    /// [提示] 级信息不参与自动告警（合法结局节点会误报刷屏）；
    /// 另提供全图批量校验菜单
    /// </summary>
    public class DialogueGraphAutoValidator : AssetModificationProcessor
    {
        [MenuItem("Tools/MmGraphValidator")]
        static void ValidateAllGraphs()
        {
            var guids = AssetDatabase.FindAssets("t:DialogueGraph");
            int broken = 0;
            var sb = new StringBuilder();
            sb.AppendLine($"=== 对话图批量校验（{guids.Length} 张）===");

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var graph = AssetDatabase.LoadAssetAtPath<DialogueGraph>(path);
                var issues = DialogueGraphValidator.CollectIssues(graph);

                // 全是[提示]级（如合法结局节点"无出口"）视为通过，不算"有问题"
                bool hasRealIssue = false;
                foreach (var issue in issues)
                {
                    if (!issue.StartsWith("[提示]")) { hasRealIssue = true; break; }
                }
                if (!hasRealIssue) continue;

                broken++;
                sb.AppendLine($"◆ {path}");
                foreach (var issue in issues)
                    sb.AppendLine($"    {issue}");
            }

            if (broken == 0)
                Debug.Log($"[批量校验] {guids.Length} 张对话图全部通过");
            else
                Debug.LogWarning($"[批量校验] {broken}/{guids.Length} 张图有问题：\n{sb}");
        }

        /// <summary>
        /// 保存资产自动校验（只看本次保存的 .asset 中的 DialogueGraph）
        /// </summary>
        static string[] OnWillSaveAssets(string[] paths)
        {
            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".asset"))
                    continue;

                var graph = AssetDatabase.LoadAssetAtPath<DialogueGraph>(path);
                if (graph == null)
                    continue;

                var issues = DialogueGraphValidator.CollectIssues(graph);
                var sb = new StringBuilder();
                foreach (var issue in issues)
                {
                    if (issue.StartsWith("[提示]")) continue;
                    sb.AppendLine(issue);
                }

                if (sb.Length > 0)
                    Debug.LogWarning($"[对话图校验] 保存 {path} 时发现问题：\n{sb}", graph);
            }

            return paths;
        }
    }
}
#endif
