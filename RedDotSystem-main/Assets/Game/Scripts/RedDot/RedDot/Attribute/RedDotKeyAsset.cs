using System;
using System.Collections.Generic;
using UnityEngine;

namespace MH
{
    // [CreateAssetMenu(fileName = "RedDotKeyAsset", menuName = "RedDot/RedDotKeyAsset")]

    /// <summary>
    /// 红点Key配置资产（"红点名册"）：定义系统里有哪些红点（唯一Id + 中文描述）。
    /// 通过编辑器窗口 RedDotKeyWindow 维护，并据此生成 ERedDotKeyType 枚举代码。
    /// 数据字段全部 ReadOnly，防止在 Inspector 里绕过编辑窗口误改。
    /// </summary>
    public class RedDotKeyAsset : ScriptableObject
    {
        // 所有红点Key的列表（编辑器窗口编辑的数据本体）
        [SerializeField] private List<RedDotKeyData> m_AllRedDotList = new List<RedDotKeyData>();
        // Id → 数据 的字典缓存（惰性构建，列表变化时由 OnValidate 清空重建）
        private Dictionary<int, RedDotKeyData> m_AllRedDotDic;

        /// <summary>
        /// Id → 红点数据 的只读字典（惰性构建 + 缓存）：
        /// 第一次访问时才把列表转成字典（加速按 Id 查找，如编辑器窗口根据 Id 显示中文描述），之后直接复用
        /// </summary>
        public IReadOnlyDictionary<int, RedDotKeyData> AllRedDotDic
        {
            get
            {
                if (m_AllRedDotDic == null)
                {
                    var dict = new Dictionary<int, RedDotKeyData>();
                    foreach (var data in m_AllRedDotList)
                    {
                        dict.Add(data.Id, data);
                    }

                    m_AllRedDotDic = dict;
                }

                return m_AllRedDotDic;
            }
        }

        /// <summary>Inspector 数据变化时清空字典缓存，下次访问时按新列表重建</summary>
        private void OnValidate()
        {
            m_AllRedDotDic = null; // 强制重建字典
        }
    }

    /// <summary>单条红点Key定义：唯一Id + 中文描述（编辑器界面显示用）</summary>
    [Serializable]
    public class RedDotKeyData
    {
        // ReadOnly：Inspector 只读，只能通过 RedDotKeyWindow 编辑（保证 Id 查重等校验逻辑生效）
        [SerializeField, ReadOnly] private int id = 1;
        [SerializeField, ReadOnly] private string des;

        public int Id
        {
            get => id;
            internal set => id = value;
        }

        public string Des
        {
            get => des;
            internal set => des = value;
        }

        // 改为公共的无参构造函数
        public RedDotKeyData()
        {
        }

        public RedDotKeyData(int id)
        {
            Id = id;
        }

        public RedDotKeyData(int id, string des)
        {
            Id = id;
            Des = des;
        }

        /// <summary>修改描述（仅编辑器窗口内部使用）</summary>
        internal void ChangeDes(string des)
        {
            Des = des;
        }
    }
}
