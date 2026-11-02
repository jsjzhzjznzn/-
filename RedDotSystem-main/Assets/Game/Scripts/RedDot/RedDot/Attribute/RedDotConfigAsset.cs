using System;
using System.Collections.Generic;
using UnityEngine;

namespace MH
{
    // [CreateAssetMenu(fileName = "RedDotConfigAsset", menuName = "RedDot/RedDotConfigAsset")]

    /// <summary>
    /// 红点关系配置资产（"红点家谱"）：定义每个红点的父节点列表和类型位标志。
    /// RedDotSingleton 启动时读取它构建内存中的红点树；
    /// 通过编辑器窗口 RedDotConfigWindow 维护（自带循环引用检测），Inspector 中只读展示。
    /// </summary>
    public class RedDotConfigAsset : ScriptableObject
    {
        // 所有红点的配置条目（编辑器窗口编辑的数据本体）
        [SerializeField] private List<RedDotConfig> m_AllRedDotConfigList = new List<RedDotConfig>();

        /// <summary>所有红点配置条目（只读暴露）。建树时遍历两遍：先建节点、再挂父子关系</summary>
        public IReadOnlyList<RedDotConfig> AllRedDotConfigList => m_AllRedDotConfigList;
    }

    /// <summary>单条红点关系配置：这个红点是谁、它的父节点有哪些、它支持哪些红点类型</summary>
    [Serializable]
    public class RedDotConfig
    {
        // 本条目对应的红点Key（对应 ERedDotKeyType 里的某一项）
        public ERedDotKeyType key;
        // 父节点列表：支持多父节点（一处变化，多个入口同时亮）
        public List<ERedDotKeyType> parentList = new List<ERedDotKeyType>();
        // 该节点支持的红点类型位标志（编辑器里勾选的 普通/数值/新/提示/重要 组合成的整数）
        public int flags; // RedDotFlags 位标志
    }

}
