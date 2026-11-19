using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MH
{
    /// <summary>
    /// 红点系统运行时诊断窗口（只读）。
    /// 用途：线上/联调时怀疑"角标与实际不符"，先在这里跑一次一致性校验，
    /// 不一致的节点就是传播断点；再看整棵树的数值分布。
    /// 窗口只调用 RedDotSingleton 的只读接口，不修改任何红点状态。
    /// </summary>
    public class RedDotDebugWindow : EditorWindow
    {
        private List<string> _mismatches = new List<string>();
        private bool _lastResult = true;
        private bool _hasChecked;
        private Vector2 _scroll;

        [MenuItem("Tools/红点/运行时诊断窗口")]
        private static void Open()
        {
            RedDotDebugWindow window = GetWindow<RedDotDebugWindow>("红点诊断");
            window.minSize = new Vector2(420f, 320f);
        }

        private void OnGUI()
        {
            RedDotSingleton redDot = RedDotSingleton.Instance;
            if (redDot == null)
            {
                EditorGUILayout.HelpBox("红点系统尚未初始化。\n进入 Play 模式后会自动自举；编辑模式下没有运行时数据。", MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField($"节点数 {redDot.AllRedDotNodes.Count}　批量模式 {(redDot.IsBatchMode ? "是" : "否")}", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("校验一致性", GUILayout.Height(24f)))
                {
                    _lastResult = redDot.ValidateConsistency(out _mismatches);
                    _hasChecked = true;
                }

                if (GUILayout.Button("复制树快照", GUILayout.Height(24f)))
                {
                    EditorGUIUtility.systemCopyBuffer = redDot.DumpTree();
                    Debug.Log("[红点] 树快照已复制到剪贴板");
                }
            }

            if (_hasChecked)
            {
                if (_lastResult)
                    EditorGUILayout.HelpBox("校验通过：所有父节点汇总值与子节点之和一致。", MessageType.Info);
                else
                    EditorGUILayout.HelpBox($"发现 {_mismatches.Count} 处不一致：", MessageType.Warning);
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (_hasChecked && !_lastResult)
            {
                foreach (string mismatch in _mismatches)
                    EditorGUILayout.LabelField(mismatch, EditorStyles.wordWrappedLabel);
            }
            else
            {
                foreach (KeyValuePair<ERedDotKeyType, RedDotNode> pair in redDot.AllRedDotNodes)
                {
                    RedDotNode node = pair.Value;
                    EditorGUILayout.LabelField($"{pair.Key}　总计 {node.rdCount}　优先级 {(RedDotFlags)node.rdPriority}　子节点 {node.rdChildrenDic.Count}　父节点 {node.parents.Count}");
                }
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
