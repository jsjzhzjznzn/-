using System;
using System.Collections.Generic;
using UnityEngine;

namespace MH
{
    /// <summary>
    /// 红点类型位标志。
    /// 一个红点节点可同时携带多种类型；位越靠上优先级越高：Default(最低) → Number → New → Tips → Important(最高)。
    /// 节点的 rdPriority = 所有生效类型按位或后取"最高位"，UI 据此决定显示哪种红点样式。
    /// </summary>
    [Flags]
    public enum RedDotFlags
    {
        None = 0,
        Default = 1 << 0,  // 普通红点
        Number = 1 << 1,   // 数值红点
        New = 1 << 2,      // 新红点
        Tips = 1 << 3,     // 提示红点
        Important = 1 << 4  // 重要红点
    }

    /// <summary>
    /// 红点节点 —— 整个系统的核心数据结构（纯C#类，不挂载物体，由 RedDotSingleton 启动时创建于内存中）。
    /// 三大职责：
    /// 1. 存数据：各类型计数(rdCounts)、总计数(rdCount)、当前最高优先级(rdPriority)；
    /// 2. 传变化：叶子节点被业务 Set 后，沿 parents 链逐层向上汇总（父节点数值永远等于所有子节点之和）；
    /// 3. 发通知：数据变化时通过 countChangeFunc 委托（观察者模式）广播给所有订阅的 UI。
    /// </summary>
    public class RedDotNode
    {
        /// <summary>本节点对应的红点Key（我是谁）</summary>
        public ERedDotKeyType rdType { get; set; }

        /// <summary>各类型的红点计数，如 { Number: 3 }。叶子节点由业务写入；父节点为所有子节点按类型求和的结果</summary>
        public Dictionary<RedDotFlags, int> rdCounts = new Dictionary<RedDotFlags, int>();

        /// <summary>红点总计数（所有类型之和）。每次变化时同步维护，查询时 O(1) 直接读取</summary>
        public int rdCount { get; private set; }

        /// <summary>当前最高优先级类型（所有生效类型按位或后取最高位），UI 据此选择显示样式</summary>
        public int rdPriority { get; private set; }

        /// <summary>父节点列表。支持多父节点：一个红点变化可让多个入口同时亮起（如"属性点"同时挂在"角色信息"和"提升"下）</summary>
        public List<RedDotNode> parents = new List<RedDotNode>();

        /// <summary>观察者回调（多播委托）：本节点数据变化时被 Invoke。UI 层（RedDotItem）通过它订阅本节点，实现自动刷新</summary>
        public RedDotSingleton.OnRdCountChange countChangeFunc;

        /// <summary>子节点字典（父→子引用）。同时用于判断节点身份：Count 大于 0 为父节点，等于 0 为叶子节点</summary>
        public Dictionary<ERedDotKeyType, RedDotNode> rdChildrenDic = new Dictionary<ERedDotKeyType, RedDotNode>();

        // 汇总计算用的临时字典：成员级复用 + 交换引用，避免每次刷新 new 字典产生 GC
        private Dictionary<RedDotFlags, int> _tempCounts = new Dictionary<RedDotFlags, int>();

        /// <summary>
        /// 使用位运算获取最高优先级（最高位）。O(1) 且零分配，不循环比较、不查表。
        /// 原理：把最高位以下的所有位填成 1，得到全 1 掩码；全 1 掩码 +1 进位到最高位的下一位，再右移一位即还原出最高位。
        /// 例：0b0110 → 位扩展得 0b0111 → +1 得 0b1000 → 右移一位得 0b0100（即原最高位）
        /// </summary>
        private static int GetHighestBit(int value)
        {
            if (value == 0) return 0;
            // 位扩展：将最高位以下的所有位都设为1
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            // 取最高位：(全1 + 1) / 2 = 最高位
            return (value + 1) >> 1;
        }

        /// <summary>
        /// 【父节点核心算法】汇总所有子节点的红点数据（带向上传播的完整版）。
        /// 结果有变化时：更新自身数据 → 通知 UI → 递归传播给父节点；
        /// 结果无变化时：直接返回（剪枝），避免无效刷新扩散到整条父链。
        /// </summary>
        private void CheckRedDotCount()
        {
            // 复用临时字典而不是每次创建新的
            _tempCounts.Clear();
            int combinedFlags = 0;  // 使用位运算合并所有标志
            int totalCount = 0;

            // 遍历每个子节点的每条类型计数，按类型分别累加
            foreach (RedDotNode node in rdChildrenDic.Values)
            {
                foreach (var pair in node.rdCounts)
                {
                    if (pair.Value > 0)
                    {
                        _tempCounts.TryGetValue(pair.Key, out int existing);
                        _tempCounts[pair.Key] = existing + pair.Value;
                        totalCount += pair.Value;
                        combinedFlags |= (int)pair.Key;  // 位或合并
                    }
                }
            }

            int maxPriority = GetHighestBit(combinedFlags);  // 位运算取最高位

            // 变更检测剪枝：新旧数据完全一致（字典内容、总数、优先级都相同）时，不更新、不回调、不传播
            bool needUpdate = !DictionaryEquals(rdCounts, _tempCounts) || rdCount != totalCount || rdPriority != maxPriority;
            if (needUpdate)
            {
                // 交换字典引用，避免重新分配
                var temp = rdCounts;
                rdCounts = _tempCounts;
                _tempCounts = temp;

                rdCount = totalCount;
                rdPriority = maxPriority;
                NotifyRedDotCountChange();

                // 只有当本节点有变化时才传播给父节点
                foreach (var parent in parents)
                {
                    parent.CheckRedDotCount();
                }
            }
        }

        /// <summary>广播：把最新数据（本节点自身）通知给所有订阅者（UI 等）</summary>
        private void NotifyRedDotCountChange()
        {
            countChangeFunc?.Invoke(this);
        }

        /// <summary>逐项比较两个字典内容是否完全一致，供上面的变更检测剪枝使用</summary>
        private bool DictionaryEquals(Dictionary<RedDotFlags, int> dict1, Dictionary<RedDotFlags, int> dict2)
        {
            if (dict1.Count != dict2.Count) return false;
            foreach (var pair in dict1)
            {
                if (!dict2.TryGetValue(pair.Key, out int value) || value != pair.Value)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 【业务写入入口】设置叶子节点的红点数（注意：覆盖式写入，不是累加！想加减要先查询当前值）。
        /// 由 RedDotSingleton.Set 调用，仅对叶子节点生效。
        /// </summary>
        public void SetRedDotCount(int count, RedDotFlags priority)
        {
            // 有子节点的父节点，数值只能来自子节点汇总，禁止业务直接写入
            if (rdChildrenDic.Count > 0)
            {
                Debug.LogWarning("不可直接设定根节点的红点数");
                return;
            }

            // count 小于等于 0 视为清除该类型；大于 0 则覆盖记录该类型的计数
            if (count <= 0)
            {
                rdCounts.Remove(priority);
            }
            else
            {
                rdCounts[priority] = count;
            }

            // 重新计算总数和优先级（使用位运算）（叶子节点无子节点，直接基于自身 rdCounts 计算）
            int combinedFlags = 0;
            int totalCount = 0;
            foreach (var pair in rdCounts)
            {
                totalCount += pair.Value;
                combinedFlags |= (int)pair.Key;  // 位或合并
            }

            rdPriority = GetHighestBit(combinedFlags);  // 位运算取最高位
            rdCount = totalCount;  // 同步更新 rdCount
            NotifyRedDotCountChange();  // 先刷新自己的 UI

            // 检查是否在批量模式
            if (RedDotSingleton.Instance != null && RedDotSingleton.Instance.IsBatchMode)
            {
                // 批量模式：只标记脏节点，不立即传播（等 EndBatch 统一按深度顺序刷新）
                RedDotSingleton.Instance.MarkNodeDirty(this);
            }
            else
            {
                // 正常模式：立即向上传播
                foreach (var parent in parents)
                {
                    parent.CheckRedDotCount();
                }
            }
        }

        /// <summary>查询指定类型的红点计数（无记录时返回 0）</summary>
        public int GetCount(RedDotFlags priority)
        {
            return rdCounts.TryGetValue(priority, out int count) ? count : 0;
        }

        /// <summary>查询红点总计数（直接返回已同步的缓存值，避免重复计算）</summary>
        public int GetTotalCount()
        {
            // 直接返回已同步的 rdCount，避免重复计算
            return rdCount;
        }

        /// <summary>
        /// 检查红点计数（不向上传播，用于批量更新模式）。
        /// 由 EndBatch 统一调度：所有脏节点的祖先按"深度从大到小"排序后依次调用本方法，
        /// 保证轮到某个父节点计算时，它下面的子节点都已是最新值，每个节点只重算一次。
        /// </summary>
        public void CheckRedDotCountNoPropagation()
        {
            // 复用临时字典
            _tempCounts.Clear();
            int combinedFlags = 0;
            int totalCount = 0;

            // 汇总逻辑与 CheckRedDotCount 相同
            foreach (RedDotNode node in rdChildrenDic.Values)
            {
                foreach (var pair in node.rdCounts)
                {
                    if (pair.Value > 0)
                    {
                        _tempCounts.TryGetValue(pair.Key, out int existing);
                        _tempCounts[pair.Key] = existing + pair.Value;
                        totalCount += pair.Value;
                        combinedFlags |= (int)pair.Key;
                    }
                }
            }

            int maxPriority = GetHighestBit(combinedFlags);

            bool needUpdate = !DictionaryEquals(rdCounts, _tempCounts) || rdCount != totalCount || rdPriority != maxPriority;
            if (needUpdate)
            {
                var temp = rdCounts;
                rdCounts = _tempCounts;
                _tempCounts = temp;

                rdCount = totalCount;
                rdPriority = maxPriority;
                NotifyRedDotCountChange();
                // 不向上传播
            }
        }
    }
}
