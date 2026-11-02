using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 技能容器（挂在角色根上，与 HealthModel / BuffComponent / AttributeComponent 并列）。
///
/// 职责：
///   1. 按技能标识维护冷却记录（充能次数、下一层恢复时刻）
///   2. 维护公共冷却分组（GCD）
///   3. 释放校验：存活 → 眩晕/沉默 → 技能配置 → 公共冷却 → 自身充能
///   4. 服务端权威：联网时只有房主扣次数/登记冷却/推进恢复；客户端只做乐观预检与显示
///   5. 网络同步：NetworkList&lt;SkillNetState&gt; 传剩余秒数，客户端重建只读镜像
///
/// 时间口径：全部用 Time.time（受 timeScale 影响）——暂停时停表、顿帧时与动画同步变慢。
/// 冷却缩减读 AttributeComponent.CooldownRate，在开始冷却那一刻快照。
///
/// 与 Buff 的接口：释放前读 CharacterState.IsStun / IsSilence；
/// 冷却状态属于角色，不挂在按钮或界面上。
/// </summary>
public class SkillComponent : NetworkBehaviour
{
    /// <summary>冷却缩减上限（0.8 = 最多缩短 80%，防止叠满后冷却变成 0）</summary>
    private const float MaxCooldownReduction = 0.8f;

    /// <summary>技能冷却记录（按 skillIdHash 索引）</summary>
    private readonly Dictionary<int, SkillCooldownRecord> _records = new Dictionary<int, SkillCooldownRecord>();

    /// <summary>公共冷却分组结束时刻（按 cooldownGroupIdHash 索引，Time.time 口径）</summary>
    private readonly Dictionary<int, float> _groupEndAt = new Dictionary<int, float>();

    /// <summary>网络同步列表（服务端写、全员可读）；运行时创建，OnDestroy 显式 Dispose</summary>
    private NetworkList<SkillNetState> _netSkills;
    private bool _netSkillsDisposed;

    private HealthModel _health;
    private CharacterState _characterState;
    private AttributeComponent _attributes;

    private HealthModel Health => _health != null ? _health : _health = GetComponent<HealthModel>();
    private CharacterState State => _characterState != null ? _characterState : _characterState = GetComponent<CharacterState>();
    private AttributeComponent Attr => _attributes != null ? _attributes : _attributes = GetComponent<AttributeComponent>();

    /// <summary>
    /// 延迟到运行时创建：NetworkList 构造会立刻分配 Persistent NativeList，
    /// 写在字段初始化里会在编辑模式重载程序集时泄漏（编辑器重载不调用 OnDestroy）。
    /// </summary>
    private void Awake()
    {
        if (_netSkills == null)
            _netSkills = new NetworkList<SkillNetState>();
    }

    public override void OnNetworkSpawn()
    {
        _netSkills.OnListChanged += OnNetSkillsChanged;

        if (IsServer)
            SyncNetSkills();          // 初次全量推送
        else
            RebuildFromNet();         // 迟到加入：首次同步不走 OnListChanged
    }

    public override void OnNetworkDespawn()
    {
        _netSkills.OnListChanged -= OnNetSkillsChanged;
    }

    public override void OnDestroy()
    {
        if (_netSkills != null && !_netSkillsDisposed)
        {
            _netSkillsDisposed = true;
            _netSkills.OnListChanged -= OnNetSkillsChanged;
            _netSkills.Dispose();
        }

        if (Application.isPlaying)
            base.OnDestroy();
    }

    private void Update()
    {
        // 客户端：只做镜像计时/显示，不推进充能、不做释放校验之外的写入
        if (IsSpawned && !IsServer)
        {
            TickClientMirror();
            return;
        }

        // 死亡清空：冷却属于战斗状态，死亡后全部重置
        var health = Health;
        if (health != null && !health.IsAlive)
        {
            if (_records.Count > 0 || _groupEndAt.Count > 0) ResetAll();
            return;
        }

        float now = Time.time;
        bool dirty = false;
        foreach (var rec in _records.Values)
        {
            int before = rec.charges;
            if (RecoverRecord(rec, now)) dirty = true;
            if (rec.charges > before) DispatchCooldownEnd(rec.def);
        }

        if (dirty) SyncNetSkills();
    }

    // ==================== 对外 API ====================

    /// <summary>只查询，不提交：返回 Ok 表示现在可以释放</summary>
    public SkillCastResult CanCast(SkillDefinition def) => Evaluate(def, commit: false);

    /// <summary>
    /// 提交释放（服务端/单机调用）。客户端请走 <see cref="RequestCast"/>，
    /// 这里在客户端只做校验、不会写冷却。
    /// </summary>
    public SkillCastResult TryCast(SkillDefinition def)
    {
        if (IsSpawned && !IsServer) return Evaluate(def, commit: false);
        return Evaluate(def, commit: true);
    }

    /// <summary>
    /// 释放请求入口（输入层调用）：
    ///   单机/房主 → 本地校验并提交；
    ///   客户端   → 本地预检通过后发 ServerRpc，并乐观播放表现，最终以服务端状态为准。
    /// </summary>
    public SkillCastResult RequestCast(SkillDefinition def)
    {
        if (def == null) return SkillCastResult.NotConfigured;

        if (!IsSpawned || IsServer)
            return TryCast(def);

        var pre = Evaluate(def, commit: false);
        if (pre != SkillCastResult.Ok) return pre;

        RequestCastServerRpc(def.skillIdHash);
        DispatchCast(def);   // 乐观表现：动画立即播；冷却显示随后由服务端同步校正
        return SkillCastResult.Ok;
    }

    /// <summary>剩余冷却秒数（0 = 已就绪；未知技能返回 0）</summary>
    public float GetRemaining(int skillIdHash)
    {
        if (!_records.TryGetValue(skillIdHash, out var rec)) return 0f;
        if (rec.charges > 0 || rec.nextRecoverAt <= 0f) return 0f;
        return Mathf.Max(0f, rec.nextRecoverAt - Time.time);
    }

    /// <summary>当前充能次数（-1 = 这个技能还没有记录）</summary>
    public int GetCharges(int skillIdHash)
        => _records.TryGetValue(skillIdHash, out var rec) ? rec.charges : -1;

    /// <summary>清空全部冷却与分组（死亡/复活/场景切换）</summary>
    public void ResetAll()
    {
        if (IsSpawned && !IsServer) return;
        if (_records.Count == 0 && _groupEndAt.Count == 0) return;

        _records.Clear();
        _groupEndAt.Clear();
        SyncNetSkills();
    }

    // ==================== 校验与提交 ====================

    /// <summary>
    /// 释放校验的唯一实现。commit = false 只读（允许顺带补齐恢复次数），
    /// commit = true 时依次扣充能、登记下一次恢复、登记公共冷却并同步网络。
    /// </summary>
    private SkillCastResult Evaluate(SkillDefinition def, bool commit)
    {
        if (def == null) return SkillCastResult.NotConfigured;

        var health = Health;
        if (health != null && !health.IsAlive) return SkillCastResult.Dead;

        var state = State;
        if (state != null && state.IsStun) return SkillCastResult.Stunned;
        if (state != null && state.IsSilence) return SkillCastResult.Silenced;

        float now = Time.time;
        var rec = EnsureRecord(def);
        RecoverRecord(rec, now);   // 查询前先把已到时间的充能补上

        int groupHash = def.cooldownGroupIdHash;
        if (groupHash != 0 && _groupEndAt.TryGetValue(groupHash, out float groupEnd))
        {
            if (groupEnd > now) return SkillCastResult.GroupCooldown;
            _groupEndAt.Remove(groupHash);
        }

        if (rec.charges <= 0) return SkillCastResult.OnCooldown;
        if (!commit) return SkillCastResult.Ok;

        // ---- 提交 ----
        rec.charges--;
        float recover = EffectiveRecover(def);
        if (rec.charges < def.maxCharges && rec.nextRecoverAt <= 0f)
            rec.nextRecoverAt = now + recover;

        if (groupHash != 0 && def.gcdSeconds > 0f)
            _groupEndAt[groupHash] = now + def.gcdSeconds;

        SyncNetSkills();
        DispatchCast(def);
        return SkillCastResult.Ok;
    }

    /// <summary>取/建记录（同一 skillId 的多份资产以已挂记录为准，并提示配置冲突）</summary>
    private SkillCooldownRecord EnsureRecord(SkillDefinition def)
    {
        if (_records.TryGetValue(def.skillIdHash, out var rec))
        {
            if (rec.def != def)
                Debug.LogWarning($"[Skill] 同一 skillId 传入了不同资产（现有 {rec.def.name} / 传入 {def.name}），将沿用现有配置", this);

            if (rec.charges > rec.def.maxCharges) rec.charges = rec.def.maxCharges;
            return rec;
        }

        rec = new SkillCooldownRecord(def);
        _records[def.skillIdHash] = rec;
        return rec;
    }

    /// <summary>
    /// 推进单条记录的充能恢复：按 nextRecoverAt + step 逐层补齐，
    /// 掉帧/长帧后也能一次补多层的正确数量；满充能时清零恢复时刻。
    /// </summary>
    private bool RecoverRecord(SkillCooldownRecord rec, float now)
    {
        if (rec.def == null) return false;

        if (rec.charges >= rec.def.maxCharges)
        {
            rec.nextRecoverAt = 0f;
            return false;
        }

        float step = EffectiveRecover(rec.def);
        if (rec.nextRecoverAt <= 0f)
        {
            rec.nextRecoverAt = now + step;   // 自愈：记录缺恢复时刻时补一个完整间隔
            return false;
        }

        bool changed = false;
        while (rec.charges < rec.def.maxCharges && now >= rec.nextRecoverAt)
        {
            rec.charges++;
            rec.nextRecoverAt += step;
            changed = true;
        }

        if (rec.charges >= rec.def.maxCharges) rec.nextRecoverAt = 0f;
        return changed;
    }

    /// <summary>单层恢复间隔（读冷却缩减；下限 0.01 秒防止 0 冷却造成退化）</summary>
    private float EffectiveRecover(SkillDefinition def)
    {
        float cdr = Attr != null ? Mathf.Clamp(Attr.GetValue(AttrType.CooldownRate), 0f, MaxCooldownReduction) : 0f;
        return Mathf.Max(0.01f, def.chargeRecoverSeconds * (1f - cdr));
    }

    // ==================== 网络同步 ====================

    private void SyncNetSkills()
    {
        if (!IsSpawned || !IsServer) return;

        float now = Time.time;

        // 反向：网络列表里已不存在的记录移除
        for (int i = _netSkills.Count - 1; i >= 0; i--)
        {
            var state = _netSkills[i];
            bool alive = state.kind == (byte)SkillCooldownKind.Group
                ? _groupEndAt.TryGetValue(state.idHash, out float end) && end > now
                : _records.ContainsKey(state.idHash);

            if (!alive) _netSkills.RemoveAt(i);
        }

        // 技能记录
        foreach (var rec in _records.Values)
        {
            Upsert(new SkillNetState(
                (byte)SkillCooldownKind.Skill,
                rec.skillIdHash,
                rec.charges,
                Remaining(rec, now)));
        }

        // 公共冷却分组（只同步还没结束的）
        foreach (var kv in _groupEndAt)
        {
            if (kv.Value <= now) continue;
            Upsert(new SkillNetState((byte)SkillCooldownKind.Group, kv.Key, 0, kv.Value - now));
        }
    }

    private void Upsert(SkillNetState state)
    {
        for (int i = 0; i < _netSkills.Count; i++)
        {
            var cur = _netSkills[i];
            if (cur.kind == state.kind && cur.idHash == state.idHash)
            {
                _netSkills[i] = state;
                return;
            }
        }
        _netSkills.Add(state);
    }

    private void OnNetSkillsChanged(NetworkListEvent<SkillNetState> changeEvent)
    {
        if (IsServer) return;   // host 本地写入也会触发，防重入
        RebuildFromNet();
    }

    /// <summary>客户端整表重建只读镜像（不跑恢复计时，恢复由服务端快照覆盖）</summary>
    private void RebuildFromNet()
    {
        if (!IsSpawned || IsServer) return;

        _records.Clear();
        _groupEndAt.Clear();

        float now = Time.time;
        for (int i = 0; i < _netSkills.Count; i++)
        {
            var state = _netSkills[i];

            if (state.kind == (byte)SkillCooldownKind.Group)
            {
                if (state.remainSeconds > 0f)
                    _groupEndAt[state.idHash] = now + state.remainSeconds;
                continue;
            }

            var def = SkillDatabase.ResolveSkill(state.idHash);
            if (def == null)
            {
                Debug.LogWarning($"[Skill] 客户端无法解析 skillIdHash={state.idHash}，跳过（SkillDatabase 两端不一致？）", this);
                continue;
            }

            var rec = new SkillCooldownRecord(def);
            rec.charges = Mathf.Clamp(state.charges, 0, def.maxCharges);
            rec.nextRecoverAt = state.remainSeconds > 0f ? now + state.remainSeconds : 0f;
            _records[state.idHash] = rec;
        }
    }

    /// <summary>
    /// 客户端镜像计时：只用于让"冷却归零"这一刻能及时派发事件，
    /// 充能次数最终仍以服务端同步为准。
    /// </summary>
    private void TickClientMirror()
    {
        float now = Time.time;
        foreach (var rec in _records.Values)
        {
            if (rec.def == null) continue;

            bool recovered = false;
            while (rec.charges < rec.def.maxCharges && rec.nextRecoverAt > 0f && now >= rec.nextRecoverAt)
            {
                rec.charges++;
                recovered = true;
                rec.nextRecoverAt += Mathf.Max(0.01f, rec.def.chargeRecoverSeconds);
            }

            if (rec.charges >= rec.def.maxCharges) rec.nextRecoverAt = 0f;
            if (recovered) DispatchCooldownEnd(rec.def);
        }
    }

    private static float Remaining(SkillCooldownRecord rec, float now)
        => rec.charges > 0 || rec.nextRecoverAt <= 0f ? 0f : Mathf.Max(0f, rec.nextRecoverAt - now);

    // ==================== 事件 ====================

    private static void DispatchCast(SkillDefinition def)
    {
        var data = new SkillData { skillId = def.skillIdHash, cooldown = def.cooldownSeconds };
        EventCenter.MainInstance.Dispatch(E_EventType.E_SkillCast, data);
        EventCenter.MainInstance.Dispatch(E_EventType.E_SkillCooldownStart, data);
    }

    private static void DispatchCooldownEnd(SkillDefinition def)
    {
        EventCenter.MainInstance.Dispatch(E_EventType.E_SkillCooldownEnd,
            new SkillData { skillId = def.skillIdHash, cooldown = 0f });
    }

    // ==================== RPC ====================

    [ServerRpc]
    private void RequestCastServerRpc(int skillIdHash)
    {
        if (!IsServer) return;

        var def = SkillDatabase.ResolveSkill(skillIdHash);
        if (def == null) return;   // 未配置总表/查不到：丢弃（防伪造 id）

        var result = TryCast(def);
        if (result != SkillCastResult.Ok)
            RejectCastClientRpc(skillIdHash, (byte)result);
    }

    [ClientRpc]
    private void RejectCastClientRpc(int skillIdHash, byte result)
    {
        if (!IsOwner) return;
        Debug.LogWarning($"[Skill] 释放被服务端拒绝 skillId={skillIdHash} reason={(SkillCastResult)result}", this);
    }
}
