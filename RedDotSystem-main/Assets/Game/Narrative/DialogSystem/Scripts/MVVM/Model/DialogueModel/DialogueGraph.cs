using System.Collections.Generic;
using MiMieMVVM;
using Sirenix.OdinInspector;
using UnityEngine;

namespace Miemie.DialogSystem
{
    /// <summary>
    /// 对话图
    /// 节点内嵌于本资产 一张图一个 SO
    /// </summary>
    [CreateAssetMenu(fileName = "New Dialogue Graph", menuName = "Dialog System/Dialogue Graph")]
    public class DialogueGraph : ScriptableObject,IModelConfig
    {
        #region 字段
        /// <summary> 对话图ID </summary>
        [SerializeField]
        private int graphId;

        /// <summary> 对话图名称 </summary>
        [SerializeField]
        private string graphName;

        /// <summary> 开始节点ID </summary>
        [SerializeField]
        private int startNodeId;

        /// <summary> 节点列表 </summary>
        [SerializeField]
        private List<DialogueNodeData> nodeList = new();

        /// <summary> 图变量声明 </summary>
        [SerializeField, HideInInspector]
        private List<DialogueVariableData> variableList = new();

        /// <summary> 节点ID索引缓存（懒构建；AddNode/RemoveNode 时失效，FindNode O(1) 用） </summary>
        private Dictionary<int, DialogueNodeData> nodeIndexCache;
        #endregion

        #region 属性
        public int ConfigId => graphId;
        public string Name => graphName;
        public int StartNodeId => startNodeId;
        public DialogueNodeData StartNode => FindNode(startNodeId);
        public List<DialogueNodeData> NodeList => nodeList;
        public List<DialogueVariableData> VariableList => variableList;
        #endregion

        #region 方法
        /// <summary>
        /// 按 ID 查找节点（字典索引 O(1)，索引懒构建、增删节点时失效重建）
        /// </summary>
        public DialogueNodeData FindNode(int nodeId)
        {
            if (nodeId == 0 || nodeList == null)
                return null;

            EnsureNodeIndex();
            return nodeIndexCache.TryGetValue(nodeId, out DialogueNodeData node) ? node : null;
        }

        /// <summary>
        /// 懒构建节点ID索引（重复ID保留先出现的，与原线性扫描语义一致）
        /// </summary>
        private void EnsureNodeIndex()
        {
            if (nodeIndexCache != null)
                return;

            var dict = new Dictionary<int, DialogueNodeData>(nodeList.Count);
            foreach (var node in nodeList)
            {
                if (node == null) continue;
                if (!dict.ContainsKey(node.ConfigId))
                    dict[node.ConfigId] = node;
            }
            nodeIndexCache = dict;
        }

        /// <summary>
        /// 添加节点
        /// </summary>
        public void AddNode(DialogueNodeData node)
        {
            if (nodeList is null)
                nodeList = new List<DialogueNodeData>();
            nodeList.Add(node);
            nodeIndexCache = null;
        }

        /// <summary>
        /// 删除节点
        /// </summary>
        public void RemoveNode(DialogueNodeData node)
        {
            if (nodeList is null)
            {
                Debug.LogError("Node list is null, please add node first");
                return;
            }
            nodeList.Remove(node);
            nodeIndexCache = null;
        }

        /// <summary>
        /// 查找黑板变量
        /// </summary>
        public DialogueVariableData FindBlackBoardVariable(string variableName)
        {
            if (string.IsNullOrEmpty(variableName) || variableList == null)
                return null;

            foreach (var item in variableList)
            {
                if (item != null && item.name == variableName)
                    return item;
            }

            return null;
        }

#if UNITY_EDITOR
        /// <summary>
        /// 设置开始节点
        /// </summary>
        public void SetStartNodeInEditorWindow(DialogueNodeData node)
        {
            startNodeId = node?.ConfigId ?? 0;
        }
#endif
        #endregion
    }
}
