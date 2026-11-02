using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MH
{
    /*
     * 表现层：根据红点是否显示或显示数，自定义红点的表现方式
     * 工作流：Start 时向逻辑层订阅本红点Key的变化回调 → 节点数值变化时自动回调 SetDotState 刷新 → OnDestroy 注销
     * 本脚本只负责"怎么显示"，完全不关心红点树和业务逻辑（数据驱动 UI）
     */

    /// <summary>
    /// UGUI红点物体脚本
    /// 挂载到需要显示红点的 UI 上（如按钮），配置好红点Key后自动订阅刷新，无需手动调用任何刷新接口
    /// </summary>
    public class RedDotItem : MonoBehaviour
    {
        // 红点圆点物体：通过控制它的显隐来"点亮/熄灭"红点
        [Header("红点父节点")] [SerializeField] public GameObject m_DotObj;

        // 数字文本：数值红点时显示具体数量（99 封顶，超过显示 99+）
        [Header("红点数文本")] [SerializeField] private TextMeshProUGUI m_DotCountText;
        // 本 UI 绑定的红点Key：订阅这个Key对应节点的变化
        [Header("红点所属键")] [SerializeField] private ERedDotKeyType m_RedDotType;
        // 本 UI 想表现的类型：与节点的"当前最高优先级类型"做匹配
        [Header("红点类型")] [SerializeField] private RedDotFlags m_Priority;
        // true = 精确匹配（节点当前类型必须完全等于 m_Priority 才显示）；false = 模糊匹配（类型有交集即显示）
        [Header("是否精确匹配")] [SerializeField] private bool m_ExactMatch = true;

        /// <summary>是否已完成订阅（防止 Bind 与 Start 重复订阅）</summary>
        private bool m_Subscribed;

        /// <summary>订阅红点变化：把 SetDotState 注册为该Key节点的观察者，注册成功后立即回调一次同步初始状态</summary>
        private void Start()
        {
            TrySubscribe();
        }

        /// <summary>
        /// 尝试订阅（幂等）：Key 未绑定（None）或红点系统未初始化时跳过
        /// </summary>
        private void TrySubscribe()
        {
            if (m_Subscribed) return;
            if (m_RedDotType == ERedDotKeyType.None) return;  // 模板未 Bind，等运行时绑定后再订
            if (RedDotSingleton.Instance == null)
            {
                Debug.LogWarning($"RedDotItem: RedDotSingleton 未初始化，无法注册 {m_RedDotType}");
                return;
            }
            RedDotSingleton.Instance.SetRedDotNodeCallBack(m_RedDotType, SetDotState);
            m_Subscribed = true;
        }

        /// <summary>
        /// 运行时改绑红点Key（好友列表条目是运行时实例化的，Inspector 配不了动态Key）
        /// 在 Instantiate 当帧调用即可（先于 Start 的订阅生效）；若此前已订阅旧Key会先退订再订新Key
        /// </summary>
        /// <param name="key">参数名：key，类型：ERedDotKeyType，用途：要订阅的红点Key（须已存在对应节点）</param>
        public void Bind(ERedDotKeyType key)
        {
            if (key == m_RedDotType)
            {
                TrySubscribe();
                return;
            }
            if (m_Subscribed && RedDotSingleton.Instance != null)
            {
                RedDotSingleton.Instance.RemoveRedDotNodeCallBack(m_RedDotType, SetDotState);
                m_Subscribed = false;
            }
            m_RedDotType = key;
            TrySubscribe();
        }

        /// <summary>红点状态刷新（观察者回调）：根据节点当前最高优先级类型与计数，决定红点显隐和数字文本</summary>
        public void SetDotState(RedDotNode node)
        {
            // 获取当前最高优先级的红点类型
            RedDotFlags highestPriority = (RedDotFlags)node.rdPriority;
            int count = node.GetCount(highestPriority);  // 缓存结果，避免重复调用
            bool shouldShow;
            if (m_ExactMatch)
            {
                // 精确匹配：节点当前类型必须与本 UI 配置的类型完全相等才显示
                shouldShow = count > 0 && highestPriority == m_Priority;
            }
            else
            {
                // 模糊匹配：位与有交集即可显示（父级汇总按钮常用，例如"只要有任何提示就亮"）
                shouldShow = count > 0 && (highestPriority & m_Priority) != 0;
            }

            // 控制红点圆点显隐
            m_DotObj.gameObject.SetActive(shouldShow);
            if (shouldShow && m_DotCountText)
            {
                // 数量超上限显示 99+；只有数值红点类型才显示数字文本
                m_DotCountText.SetText(count >= 100 ? "99+" : count.ToString());
                m_DotCountText.gameObject.SetActive(highestPriority == RedDotFlags.Number);
            }
        }

        /// <summary>销毁时注销观察者回调，防止红点节点持有已销毁的 UI 造成空引用/内存泄漏</summary>
        private void OnDestroy()
        {
            m_DotObj = null;
            m_DotCountText = null;
            // 防止单例已销毁时访问导致空引用
            if (RedDotSingleton.Instance != null)
            {
                RedDotSingleton.Instance.RemoveRedDotNodeCallBack(m_RedDotType, SetDotState);
            }
        }
    }
}
