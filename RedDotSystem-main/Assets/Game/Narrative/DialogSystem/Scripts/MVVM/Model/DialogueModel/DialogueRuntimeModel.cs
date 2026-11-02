using System.Collections.Generic;
using MiMieMVVM;

namespace Miemie.DialogSystem
{
    /// <summary>
    /// 对话进行中的运行时状态
    /// </summary>
    public class DialogueRuntimeModel : IModelState
    {
        /// <summary> 当前跑在哪张图上 </summary>
        public DialogueGraph Graph { get; private set; }

        /// <summary> 变量黑板（当前值）</summary>
        public DialogueVariablesBlackBoard Variables { get; } = new();

        /// <summary> 当前节点 </summary>
        public DialogueNodeData CurrentNode { get; private set; }

        /// <summary> 可用选项缓存 </summary>
        public List<DialogueTransLineData> AvailableChoiceList { get; } = new();

        /// <summary>
        /// 绑定对话图
        /// savedVariablesJson：场景存档里的变量快照（应用顺序在「默认值之后、事实播种之前」，
        /// 保证背包等实时事实永远覆盖过期存档值——见存档方案 D4）
        /// </summary>
        public void BindGraph(DialogueGraph graph, string savedVariablesJson = null)
        {
            this.Graph = graph;
            // graph?.VariableList(黑板变量列表) 在配置阶段已填充
            Variables.InitDefaultVariables(graph?.VariableList);
            // 恢复存档变量（仅图声明过的同类型变量生效）
            Variables.ApplyJson(savedVariablesJson);
            // 播种全局事实（背包等玩法状态；图未声明/类型不符自动跳过，无绑定器时为无操作）
            DialogueFacts.SeedInto(Variables, graph?.VariableList);
            CurrentNode = null;
            AvailableChoiceList.Clear();
        }

        /// <summary>
        /// 设置当前节点
        /// </summary>
        public void SetCurrentNode(DialogueNodeData node) => CurrentNode = node;

        /// <summary>
        /// 清空运行时
        /// </summary>
        public void Clear()
        {
            Graph = null;
            CurrentNode = null;
            AvailableChoiceList.Clear();
        }
    }
}
