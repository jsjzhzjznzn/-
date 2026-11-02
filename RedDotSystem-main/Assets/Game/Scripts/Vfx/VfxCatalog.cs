using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 特效注册表（ScriptableObject 配置资产）
/// key → prefab + 池参数 + 表现参数；系统运行时按 key 查表租借实例，调用方零资产引用
/// 资产位置约定：Assets/Game/Resources/VfxCatalog.asset（VfxManager 通过 Resources.Load 兜底加载）
/// </summary>
[CreateAssetMenu(menuName = "RedDot/Vfx Catalog", fileName = "VfxCatalog")]
public class VfxCatalog : ScriptableObject
{
    /// <summary>单条特效注册</summary>
    [Serializable]
    public class Entry
    {
        [Tooltip("注册键（用 VfxKeys 常量，勿写魔法字符串）")]
        public string key;

        [Tooltip("特效 prefab（粒子系统根）")]
        public GameObject prefab;

        [Tooltip("预热实例数（进 Play 即建好，首次播放零 Instantiate 开销；池会按需继续增长，不销毁）")]
        [Min(0)] public int prewarm = 1;

        [Tooltip("时长覆盖（秒）。0=按粒子自然时长自动回收；循环型特效必须填写，否则只能靠 Stop 回收")]
        [Min(0)] public float durationOverride;

        [Tooltip("unscaled：暂停（timeScale=0）时也照播照回收——世界表现特效关、纯 UI 反馈类开")]
        public bool unscaled;

        [Tooltip("统一缩放倍率（prefab 原始尺寸与场景需求不一致时调这里，不动 prefab）")]
        [Min(0.01f)] public float scale = 1f;

        [Tooltip("统一朝向偏移（欧拉角）：库 prefab 的默认朝向与调用方约定不一致时，在这里一次性校正")]
        public Vector3 rotationOffsetEuler = Vector3.zero;

        [Tooltip("统一位置偏移（局部空间，随播放朝向旋转）：库 prefab 的可见中心不在原点上时（如弧光质心挂在原点左侧），在这里补偿到居中")]
        public Vector3 positionOffset = Vector3.zero;

        [Tooltip("编辑器备注（这个效果用在哪）")]
        public string comment;
    }

    [Tooltip("全部注册条目（key 不可重复，重复时前者生效）")]
    [SerializeField] private List<Entry> entries = new();

    /// <summary>条目数（诊断窗口用）</summary>
    public int Count => entries.Count;

    /// <summary>按 key 查条目（条目数是个位数到十几，线性扫描足够且免维护缓存失效）</summary>
    public bool TryGet(string key, out Entry entry)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            if (string.Equals(entries[i].key, key, StringComparison.Ordinal))
            {
                entry = entries[i];
                return true;
            }
        }
        entry = null;
        return false;
    }

    /// <summary>遍历全部条目（诊断窗口用；不要修改返回集合的内容）</summary>
    public IReadOnlyList<Entry> AllEntries => entries;

    /// <summary>查重复 key（编辑器窗口的校验提示用）</summary>
    public bool HasDuplicateKeys(out string duplicated)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < entries.Count; i++)
        {
            if (!string.IsNullOrEmpty(entries[i].key) && !seen.Add(entries[i].key))
            {
                duplicated = entries[i].key;
                return true;
            }
        }
        duplicated = null;
        return false;
    }
}
