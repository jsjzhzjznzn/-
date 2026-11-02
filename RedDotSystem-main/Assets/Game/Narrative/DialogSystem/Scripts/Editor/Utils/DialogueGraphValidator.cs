#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Miemie.DialogSystem.Editor
{
    /// <summary>
    /// 对话图校验工具
    /// CollectIssues 返回规则问题列表（保存自动校验/批量校验共用同一套规则）；
    /// Validate 保持工具栏按钮行为（打印到 Console）
    /// </summary>
    public static class DialogueGraphValidator
    {
        /// <summary>
        /// 收集全部规则问题（空列表 = 校验通过）
        /// </summary>
        public static List<string> CollectIssues(DialogueGraph graph)
        {
            var issues = new List<string>();
            if (graph == null)
            {
                issues.Add("[错误] 图对象为空");
                return issues;
            }

            if (graph.StartNode == null)
                issues.Add("[错误] startNode 未设置");

            if (graph.NodeList == null || graph.NodeList.Count == 0)
            {
                issues.Add("[警告] nodeList 为空");
                return issues;
            }

            var inGraph = new HashSet<int>();
            foreach (var node in graph.NodeList)
            {
                if (node == null)
                {
                    issues.Add("[错误] nodeList 中有空引用");
                    continue;
                }
                inGraph.Add(node.ConfigId);
            }

            if (graph.StartNode != null && !inGraph.Contains(graph.StartNodeId))
                issues.Add("[警告] startNode 不在 nodeList 中");

            foreach (var node in graph.NodeList)
            {
                if (node == null)
                    continue;
                ValidateNode(graph, node, inGraph, issues);
            }

            return issues;
        }

        /// <summary>
        /// 校验并打印（编辑器按钮入口）
        /// </summary>
        public static void Validate(DialogueGraph graph)
        {
            if (graph == null)
            {
                Debug.LogWarning("未选中任何 DialogueGraph");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"=== 校验 {graph.name} ===");

            var issues = CollectIssues(graph);
            if (issues.Count == 0)
                sb.AppendLine("校验通过，无问题");
            else
                foreach (var issue in issues)
                    sb.AppendLine(issue);

            sb.AppendLine("=== 结束 ===");
            Debug.Log(sb.ToString());
        }

        static void ValidateNode(DialogueGraph graph, DialogueNodeData node, HashSet<int> inGraph, List<string> issues)
        {
            if (node.IsOptionNode)
            {
                if (node.ChoiceList == null || node.ChoiceList.Count == 0)
                    issues.Add($"[错误] 选项节点 [{node.ConfigId}] choiceList 为空");
                else
                    ValidateChoices(node, inGraph, issues);
            }
            else
            {
                int nextId = node.NextTransition?.toNodeId ?? 0;
                if (nextId == 0)
                    issues.Add($"[提示] 节点 [{node.ConfigId}] 无出口（可能是结局）");
                else if (!inGraph.Contains(nextId))
                    issues.Add($"[警告] [{node.ConfigId}] → {nextId} 不在本图 nodeList");
            }
        }

        static void ValidateChoices(DialogueNodeData node, HashSet<int> inGraph, List<string> issues)
        {
            foreach (var choice in node.ChoiceList)
            {
                if (choice == null || choice.toNodeId == 0)
                    issues.Add($"[错误] [{node.ConfigId}] 选项「{choice?.labelText}」无 toNodeId");
                else if (!inGraph.Contains(choice.toNodeId))
                    issues.Add($"[警告] 选项 → {choice.toNodeId} 不在本图 nodeList");
            }
        }
    }
}
#endif
