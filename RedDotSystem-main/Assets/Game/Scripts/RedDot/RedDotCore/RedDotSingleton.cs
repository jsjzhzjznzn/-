using System;
using System.Collections.Generic;
using UnityEngine;

namespace MH
{
    /// <summary>
    /// 红点系统唯一入口（场景单例，需挂在常驻物体上并在 Inspector 拖入 RedDotConfigAsset 配置）。
    /// 职责：
    /// 1. 启动时根据配置资产（key + parentList）在内存中构建整棵红点树；
    /// 2. 对外提供运行时 API：Set（业务唯一写入口）、查询、回调注册/注销；
    /// 3. 提供批量更新模式（BeginBatch/EndBatch），优化大量红点同时变化时的刷新效率。
    /// </summary>
    public class RedDotSingleton : MonoBehaviour
    {
        private static RedDotSingleton _instance;
        /// <summary>全局唯一实例。系统未初始化时为 null，外部调用前需判空</summary>
        public static RedDotSingleton Instance => _instance;

        /// <summary>节点数据变化时的回调签名：参数为发生变化的节点自身（可直接读取最新数据）</summary>
        public delegate void OnRdCountChange(RedDotNode node);

        /// <summary>整棵红点树：红点Key → 节点。系统内所有节点都在这里查找</summary>
        public Dictionary<ERedDotKeyType, RedDotNode> AllRedDotNodes = new Dictionary<ERedDotKeyType, RedDotNode>();

        /// <summary>红点关系配置资产（Inspector 拖入），决定红点树的形状；启动时据此建树</summary>
        public RedDotConfigAsset _config;

        // 批量更新模式
        // 是否处于批量模式：批量模式中 Set 只登记脏节点，不立即向上传播
        private bool _isBatchMode = false;
        // 批量模式中被 Set 过的"脏节点"集合，EndBatch 时统一刷新它们的祖先链
        private HashSet<RedDotNode> _dirtyNodes = new HashSet<RedDotNode>();

        /// <summary>初始化：单例保护 → 常驻不销毁 → 读取配置建树</summary>
        public void Awake()
        {
            // 单例保护：如果已存在实例则销毁新创建的
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);  // 常驻场景，切场景不销毁

            // todo 根据自己项目的资源管理进行加载就好 这里演示直接Inspector拖动了
            // _config = Resources.Load<RedDotConfigAsset>(nameof(RedDotConfigAsset));
            InitRedDotTreeNode();
        }

        /// <summary>销毁时清空静态引用，防止外部访问到已销毁的"假单例"</summary>
        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }

        /// <summary>
        /// 根据配置资产构建红点树，分两步：
        /// 第一步：把配置中出现过的所有 Key 创建为节点（先全部建好，第二步挂链时才找得到对象）；
        /// 第二步：按 parentList 双向挂链：子节点记父（parents），父节点记子（rdChildrenDic）。
        /// </summary>
        public void InitRedDotTreeNode()
        {
            // 配置空检查
            if (_config == null)
            {
                Debug.LogError("RedDotSingleton: _config 未配置，请在Inspector中设置!");
                return;
            }

            // 预分配字典容量
            AllRedDotNodes = new Dictionary<ERedDotKeyType, RedDotNode>(_config.AllRedDotConfigList.Count);

            // 创建所有节点
            foreach (var config in _config.AllRedDotConfigList)
            {
                if (!AllRedDotNodes.ContainsKey(config.key))
                {
                    var node = new RedDotNode { rdType = config.key };  // 纯C#节点，只存在于内存
                    AllRedDotNodes.Add(config.key, node);
                }
            }

            // 建立父子关系
            foreach (var config in _config.AllRedDotConfigList)
            {
                var node = AllRedDotNodes[config.key];
                foreach (var parentKey in config.parentList)
                {
                    if (AllRedDotNodes.TryGetValue(parentKey, out var parentNode))
                    {
                        node.parents.Add(parentNode);                    // 子 → 父（用于向上传播）
                        parentNode.rdChildrenDic[config.key] = node;     // 父 → 子（用于汇总计算）
                    }
                }
            }
        }

        /// <summary>注销指定红点Key的变化回调。UI 销毁时必须调用，防止红点节点持有已销毁物体</summary>
        public void RemoveRedDotNodeCallBack(ERedDotKeyType key, OnRdCountChange callBack)
        {
            if (AllRedDotNodes.TryGetValue(key, out var node))
            {
                node.countChangeFunc -= callBack;
            }
            else
            {
                Debug.LogError($"未找到红点节点: {key}");
            }
        }

        /// <summary>
        /// 注册指定红点Key的变化回调（观察者订阅）。
        /// 注意：注册成功后会立即用当前数据回调一次，方便新出现的 UI 直接同步初始状态。
        /// </summary>
        public void SetRedDotNodeCallBack(ERedDotKeyType key, OnRdCountChange callBack)
        {
            if (AllRedDotNodes.TryGetValue(key, out var node))
            {
                node.countChangeFunc += callBack;
                callBack?.Invoke(node);  // 立即回调一次，同步初始红点状态
            }
            else
            {
                Debug.LogError($"未找到红点节点: {key}");
            }
        }

        /// <summary>
        /// 【业务唯一写入口】设置某个红点的数量和类型（覆盖式，不是累加）。
        /// 例：Set(Key8, 5, Number) 表示系统邮件有 5 封；Set(Key8, 0, Number) 表示清除。
        /// 只对叶子节点生效；变化会自动沿父链向上传播刷新，无需关心父级 UI。
        /// </summary>
        public void Set(ERedDotKeyType key, int rdCount = 1, RedDotFlags redDotFlags = RedDotFlags.Default)
        {
            if (AllRedDotNodes.TryGetValue(key, out var node))
            {
                node.SetRedDotCount(Math.Max(0, rdCount), redDotFlags);  // 负数钳制为 0
            }
            else
            {
                Debug.LogError($"未找到红点节点: {key}");
            }
        }

        /// <summary>查询指定红点Key、指定类型的红点计数（节点不存在或无记录返回 0）</summary>
        public int GetRedDotInfoCount(ERedDotKeyType key, RedDotFlags redDotFlags)
        {
            if (AllRedDotNodes.TryGetValue(key, out var node))
            {
                return node.GetCount(redDotFlags);
            }
            return 0;
        }

        /// <summary>查询指定红点Key的概要信息：(总计数, 当前最高优先级)。节点不存在返回 (0, 0)</summary>
        public (int count, int priority) GetRedDotInfo(ERedDotKeyType key)
        {
            if (AllRedDotNodes.TryGetValue(key, out var node))
            {
                return (node.rdCount, node.rdPriority);
            }
            return (0, 0);
        }

        // ──────────────────────────────
        // 动态子节点（运行时增删）
        // ──────────────────────────────

        /// <summary>
        /// 运行时创建动态叶子节点并挂到父节点下（已存在则直接返回，幂等）
        /// 适用"数量不固定的红点"：如每个好友一条未读——静态枚举配置不可能枚举所有好友
        /// Key 约定取远离配置段的区间（本项目约定 100000 + 好友ID），避免与 KeyAsset 里的 Id 冲突
        /// </summary>
        /// <param name="key">参数名：key，类型：ERedDotKeyType，用途：动态节点Key（如 100005）</param>
        /// <param name="parentKey">参数名：parentKey，类型：ERedDotKeyType，用途：要挂到的父节点Key（须已由配置建好）</param>
        /// <returns>返回值类型：RedDotNode，用途：新建或已存在的节点</returns>
        public RedDotNode CreateChildNode(ERedDotKeyType key, ERedDotKeyType parentKey)
        {
            if (!AllRedDotNodes.TryGetValue(key, out RedDotNode node))
            {
                node = new RedDotNode { rdType = key };
                AllRedDotNodes.Add(key, node);
            }

            if (AllRedDotNodes.TryGetValue(parentKey, out RedDotNode parent) &&
                !parent.rdChildrenDic.ContainsKey(key))
            {
                node.parents.Add(parent);                 // 子 → 父（向上传播用）
                parent.rdChildrenDic[key] = node;         // 父 → 子（汇总计算用）
            }
            return node;
        }

        /// <summary>
        /// 移除动态子节点：先清空其全部计数（触发父链重算、父节点数值同步扣减），再摘链出表
        /// 用于换账号登录：清掉上一个账号的动态红点，防止旧未读串到新账号
        /// </summary>
        /// <param name="key">参数名：key，类型：ERedDotKeyType，用途：要移除的动态节点Key</param>
        /// <param name="parentKey">参数名：parentKey，类型：ERedDotKeyType，用途：它挂着的父节点Key</param>
        public void RemoveChildNode(ERedDotKeyType key, ERedDotKeyType parentKey)
        {
            if (!AllRedDotNodes.TryGetValue(key, out RedDotNode node)) return;
            if (!AllRedDotNodes.TryGetValue(parentKey, out RedDotNode parent)) return;

            // 先经公开 API 清空所有类型计数：父链会立即重算（角标同步熄灭）
            List<RedDotFlags> flags = new List<RedDotFlags>(node.rdCounts.Keys);
            foreach (RedDotFlags flag in flags)
            {
                node.SetRedDotCount(0, flag);
            }

            node.parents.Remove(parent);
            parent.rdChildrenDic.Remove(key);
            AllRedDotNodes.Remove(key);
        }

        #region 批量更新API

        /// <summary>
        /// 开始批量更新模式，在此模式下 Set 调用不会立即向上传播
        /// 适用场景：一次要下发大量红点数据（如登录拉取全部红点），避免父节点被反复重算
        /// </summary>
        public void BeginBatch()
        {
            _isBatchMode = true;
            _dirtyNodes.Clear();
        }

        /// <summary>
        /// 结束批量更新并应用所有变更。流程：
        /// 1. 收集所有脏节点的全部祖先（HashSet 去重，多个脏点共享的祖先只刷新一次）；
        /// 2. 按节点深度从大到小排序（离叶子近的祖先先算，保证算父节点时其子节点数据已最新）；
        /// 3. 依次重算，每个节点只计算一次。
        /// </summary>
        public void EndBatch()
        {
            _isBatchMode = false;

            if (_dirtyNodes.Count == 0) return;

            // 收集所有需要更新的父节点
            var allDirtyParents = new HashSet<RedDotNode>();
            foreach (var node in _dirtyNodes)
            {
                CollectDirtyParents(node, allDirtyParents);
            }

            // 使用缓存避免重复计算深度，使用 List.Sort 原地排序避免LINQ分配
            var depthCache = new Dictionary<RedDotNode, int>();
            var sortedNodes = new List<RedDotNode>(allDirtyParents);
            // 深度降序：深度大 = 离根远 = 离叶子近的祖先先刷新
            sortedNodes.Sort((a, b) => GetNodeDepth(b, depthCache).CompareTo(GetNodeDepth(a, depthCache)));

            foreach (var node in sortedNodes)
            {
                node.CheckRedDotCountNoPropagation();  // 只重算自己，不向上传播
            }

            _dirtyNodes.Clear();
        }

        /// <summary>
        /// 是否处于批量更新模式
        /// </summary>
        public bool IsBatchMode => _isBatchMode;

        /// <summary>
        /// 内部方法：将节点标记为脏（仅在批量模式下使用）
        /// </summary>
        internal void MarkNodeDirty(RedDotNode node)
        {
            if (_isBatchMode)
            {
                _dirtyNodes.Add(node);
            }
        }

        /// <summary>递归收集节点的所有祖先（父、祖父……），HashSet 自动去重并剪断已走过的递归分支</summary>
        private void CollectDirtyParents(RedDotNode node, HashSet<RedDotNode> result)
        {
            foreach (var parent in node.parents)
            {
                if (result.Add(parent))  // Add 返回 false 说明该祖先已收集过，不再深入
                {
                    CollectDirtyParents(parent, result);
                }
            }
        }

        /// <summary>
        /// 计算节点深度：无父节点（根）为 0，其余 = 所有父节点深度的最大值 + 1。
        /// cache 缓存已算过的结果，避免重复递归。
        /// </summary>
        private int GetNodeDepth(RedDotNode node, Dictionary<RedDotNode, int> cache = null)
        {
            // 检查缓存
            if (cache != null && cache.TryGetValue(node, out int cached))
                return cached;

            if (node.parents.Count == 0)
            {
                cache?.Add(node, 0);
                return 0;
            }

            int maxDepth = 0;
            foreach (var parent in node.parents)
            {
                maxDepth = Math.Max(maxDepth, GetNodeDepth(parent, cache) + 1);
            }

            cache?.Add(node, maxDepth);
            return maxDepth;
        }

        #endregion

        // ──────────────────────────────
        // 诊断（排查"父节点汇总与子节点不一致"类问题用）
        // ──────────────────────────────

        /// <summary>
        /// 导出整棵红点树的节点状态（Key/计数/各类型计数/子节点计数/父链），并手工重算每个父节点应有值做对照。
        /// 用法：怀疑陈旧汇总时调用本方法，diff "count=" 与 "recount=" 两列——不一致的节点就是传播断点。
        /// </summary>
        /// <returns>返回值类型：string，用途：多行诊断文本</returns>
        public string DumpTree()
        {
            System.Text.StringBuilder sb = new();
            sb.AppendLine($"[RedDotDump] nodes={AllRedDotNodes.Count} batchMode={_isBatchMode} dirty={_dirtyNodes.Count}");
            foreach (var pair in AllRedDotNodes)
            {
                RedDotNode node = pair.Value;

                // 手工重算父节点应有值（与 CheckRedDotCountNoPropagation 同算法）对照缓存值
                int recount = 0;
                foreach (var child in node.rdChildrenDic.Values)
                {
                    foreach (var kv in child.rdCounts) recount += kv.Value;
                }

                sb.Append($"  key={pair.Key} count={node.rdCount} recount={recount} priority={(RedDotFlags)node.rdPriority} counts=[");
                foreach (var kv in node.rdCounts) sb.Append($"{kv.Key}:{kv.Value} ");
                sb.Append("] children=[");
                foreach (var child in node.rdChildrenDic) sb.Append($"{child.Key}({child.Value.rdCount}) ");
                sb.Append("] parents=[");
                foreach (var parent in node.parents) sb.Append($"{parent.rdType} ");
                sb.AppendLine("]");
            }
            return sb.ToString();
        }
    }
}
