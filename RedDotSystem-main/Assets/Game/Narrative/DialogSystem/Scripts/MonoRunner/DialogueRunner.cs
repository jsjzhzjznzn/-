using System.Collections.Generic;
using Sirenix.OdinInspector;
using Sirenix.Serialization;
using UnityEngine;

namespace Miemie.DialogSystem
{
    /// <summary>
    /// 对话模块入口
    /// 组装 ViewModel View 并创建跨模块服务（经 Cross 属性对外暴露）
    /// </summary>
    public class DialogueRunner : SerializedMonoBehaviour
    {
        [SerializeField]
        private DialogueGraph dialogueGraph;

        [SerializeField]
        private StandDialogView dialogView;

        [SerializeField]
        private bool autoStart = true;

        [SerializeField, ReadOnly]
        private DialogueNodeData currentNode;

        private DialogueViewModel viewModel;
        private DialogueCrossService crossService;

        public DialogueGraph DialogueGraph => dialogueGraph;
        public DialogueViewModel ViewModel => viewModel;
        public DialogueNodeData CurrentNode => viewModel?.CurrentNode;

        /// <summary>
        /// 对话跨模块服务（构造注入创建，由组合根 CrossModuleInstaller 对外分发）
        /// </summary>
        public IDialogueCrossService Cross => crossService;

        #region 生命周期

        private void Awake()
        {
            // 创建vm对象
            viewModel = new DialogueViewModel();
            viewModel.Initialize();

            // 创建跨模块服务
            crossService = new DialogueCrossService(viewModel);

            // View层绑定VM
            if (dialogView != null)
                dialogView.Bind(viewModel);
        }

        private void Start()
        {
            if (autoStart)
                StartDialog();
        }

        #if UNITY_EDITOR
        private void Update()
        {
            // 仅同步 Inspector 只读显示，运行时没有任何读取方（对外访问器是 CurrentNode 属性）
            currentNode = viewModel?.CurrentNode;
        }
#endif

        private void OnDestroy()
        {
            dialogView?.Unbind();
            viewModel?.Shutdown();
        }

        #endregion

        #region 对话流程

        /// <summary>
        /// 创建后立即关闭自动播放（供运行时动态创建的 Runner 使用）
        /// 必须在 Awake 之前调用，调用方负责"先停用物体→挂组件→调用本方法→再激活"的顺序。
        /// 有了这个入口，调用方不必反射改私有字段（字段改名会让反射静默失效，对话会自己播一遍）。
        /// </summary>
        public void DisableAutoStart()
        {
            autoStart = false;
        }

        /// <summary>
        /// 开始对话
        /// </summary>
        public void StartDialog()
        {
            viewModel?.StartDialog(dialogueGraph);
        }

        /// <summary>
        /// 播放指定对话图
        /// </summary>
        public void PlayGraph(DialogueGraph graph)
        {
            dialogueGraph = graph;
            StartDialog();
        }

        /// <summary>
        /// 从指定节点恢复对话（场景存档用）
        /// 节点失效时由 ViewModel 降级为从起始节点开始，保证对话仍可打开。
        /// </summary>
        /// <param name="graph">对话图（传 null 则用当前已绑定的图）</param>
        /// <param name="nodeId">要恢复到的节点 ConfigId</param>
        /// <param name="savedVariablesJson">存档里的变量快照（可为 null）</param>
        /// <param name="firedEventKeys">本段对话已发布过事件的选项 Key（抑制重复推进任务）</param>
        public void PlayGraphFrom(DialogueGraph graph, int nodeId, string savedVariablesJson = null,
            IEnumerable<string> firedEventKeys = null)
        {
            if (graph != null) dialogueGraph = graph;
            if (dialogueGraph == null)
            {
                Debug.LogError("[Dialogue] PlayGraphFrom 失败：对话图为空");
                return;
            }
            viewModel?.StartDialogFrom(dialogueGraph, nodeId, savedVariablesJson, firedEventKeys);
        }

        /// <summary>当前对话变量快照 JSON（场景存档采集用）</summary>
        public string CurrentVariablesJson => viewModel?.RuntimeModel?.Variables?.ToJson();

        /// <summary>
        /// 继续对话
        /// </summary>
        public void GoNext() => viewModel?.GoNext();

        /// <summary>
        /// 选择选项
        /// </summary>
        public void SelectOption(int index) => viewModel?.SelectOption(index);

        #endregion
    }
}
