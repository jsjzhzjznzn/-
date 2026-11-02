using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 技能配置总表（ScriptableObject，放在 Resources/SkillDatabase.asset）。
///
/// 用途：按 skillIdHash 反查 SkillDefinition —— 联网时 RPC 只传 id（不能传 SO 引用），
/// 服务端/客户端拿到 id 后用本表还原配置。与 <see cref="BuffDatabase"/> 同构。
///
/// skillIdHash 走 Animator.StringToHash（确定性哈希，跨端/跨进程稳定），可安全用于网络。
/// </summary>
[CreateAssetMenu(fileName = "SkillDatabase", menuName = "Create/Skill/SkillDatabase")]
public class SkillDatabase : ScriptableObject
{
    [SerializeField, Header("全部技能配置（服务端/客户端按 id 反查用）")]
    private List<SkillDefinition> _skills = new List<SkillDefinition>();

    private Dictionary<int, SkillDefinition> _map;

    private const string ResourcePath = "SkillDatabase";

    private static SkillDatabase _instance;

    /// <summary>单例（从 Resources 加载；未配置返回 null）</summary>
    public static SkillDatabase Instance
    {
        get
        {
            if (_instance == null) _instance = Resources.Load<SkillDatabase>(ResourcePath);
            return _instance;
        }
    }

    /// <summary>按 skillIdHash 反查配置（未配置总表或查不到返回 null）</summary>
    public static SkillDefinition ResolveSkill(int skillIdHash)
    {
        var db = Instance;
        if (db == null) return null;
        db.EnsureMap();
        return db._map.TryGetValue(skillIdHash, out var def) ? def : null;
    }

    private void EnsureMap()
    {
        if (_map != null) return;
        _map = new Dictionary<int, SkillDefinition>();
        for (int i = 0; i < _skills.Count; i++)
        {
            var def = _skills[i];
            if (def == null) continue;
            _map[def.skillIdHash] = def;
        }
    }

    /// <summary>编辑器改表后重建映射（静态缓存，运行时构建一次）</summary>
    private void OnValidate() => _map = null;
}
