using System;
using System.Collections.Generic;

using Common.Network;

using UnityEngine;

/// <summary>
/// 装备数据层（客户端缓存 + 协议收发）
/// 服务端权威：配置、实例、穿戴关系全部来自服务端，客户端只缓存与分发，不做归属/槽位判断。
/// 三层缓存与协议同构：configs = 这一类装备怎么描述；instances = 我有哪些件；
/// slots = 角色 × 槽位 → 实例（"穿在哪"的唯一真相）。
/// UI（CharacterView / 背包"已装备"标注）只订阅 Changed 渲染，不直接碰网络（与 BagManager 同款分层）。
/// </summary>
public class EquipmentManager : MonoBehaviour
{
    // ──────────────────────────────
    // 单例
    // ──────────────────────────────

    /// <summary>全局唯一实例（UI 层访问装备数据的入口）</summary>
    public static EquipmentManager Instance { get; private set; }

    /// <summary>运行时自举：进入 Play 自动创建，无需手动挂载</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance != null) return;
        GameObject holder = new GameObject("EquipmentManager");
        holder.AddComponent<EquipmentManager>();
        DontDestroyOnLoad(holder);
    }

    private void Awake()
    {
        if (Instance != null)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    // ──────────────────────────────
    // 事件
    // ──────────────────────────────

    /// <summary>装备数据变化（列表到达 / 穿戴成功，UI 重渲染）</summary>
    public event Action Changed;

    /// <summary>提示文案（穿戴结果/失败原因，UI 状态栏显示）</summary>
    public event Action<string> StatusChanged;

    /// <summary>穿戴请求结算（成功与否 + 文案）：角色页据此在成功后收回二级页</summary>
    public event Action<bool, string> EquipResult;

    // ──────────────────────────────
    // 缓存
    // ──────────────────────────────

    /// <summary>装备配置：itemId → 配置（槽位/武器类型/可用角色/属性 JSON）</summary>
    private readonly Dictionary<int, EquipConfigInfo> configs = new();

    /// <summary>装备实例：instanceId → 实例（含"穿在谁身上"）</summary>
    private readonly Dictionary<long, EquipInstanceInfo> instances = new();

    /// <summary>穿戴关系：charId × slotType → instanceId</summary>
    private readonly Dictionary<(int CharId, int SlotType), long> slots = new();

    /// <summary>解析后的属性加成缓存（itemId → 加成列表；配置里 attr_json 不变，解析一次即可）</summary>
    private readonly Dictionary<int, List<EquipAttrEntry>> attrCache = new();

    /// <summary>是否已拿到过一次列表（UI 可据此显示空态/加载态）</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>是否正在请求列表</summary>
    public bool IsLoading { get; private set; }

    /// <summary>当前在途的穿戴请求发出时刻（0 = 无在途；超时 5s 视为失败可重试）</summary>
    private float equipSentAt;

    /// <summary>穿戴请求超时（秒）：超时只解除在途标记并提示，不改本地数据</summary>
    private const float EquipTimeoutSeconds = 5f;

    /// <summary>武器槽位类型（本期唯一槽位，与服务端 EquipmentRepository.SlotWeapon 同源）</summary>
    public const int SlotWeapon = 1;

    // ──────────────────────────────
    // 生命周期
    // ──────────────────────────────

    private void Start()
    {
        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net == null) return;

        net.EquipmentListResponseReceived += OnEquipmentListResponse;
        net.EquipResponseReceived += OnEquipResponse;
        net.LoginResponseReceived += OnLoginResponse;
        net.ConnectionBroken += OnConnectionBroken;

        // 组件可能在登录回执之后才激活（例如直接从主页进 Play 的手动联调）
        if (net.MyPlayerId > 0) RequestEquipment();
    }

    private void OnDestroy()
    {
        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net == null) return;

        net.EquipmentListResponseReceived -= OnEquipmentListResponse;
        net.EquipResponseReceived -= OnEquipResponse;
        net.LoginResponseReceived -= OnLoginResponse;
        net.ConnectionBroken -= OnConnectionBroken;
    }

    private void Update()
    {
        if (equipSentAt <= 0f) return;
        if (Time.unscaledTime - equipSentAt < EquipTimeoutSeconds) return;

        equipSentAt = 0f;
        StatusChanged?.Invoke("装备操作超时，请重试");
    }

    // ──────────────────────────────
    // 查询（UI 只读这些）
    // ──────────────────────────────

    /// <summary>取装备配置（不是装备返回 null）</summary>
    public EquipConfigInfo GetConfig(int itemId)
        => configs.TryGetValue(itemId, out EquipConfigInfo config) ? config : null;

    /// <summary>取某个实例（已丢弃/不存在返回 null）</summary>
    public EquipInstanceInfo GetInstance(long instanceId)
        => instances.TryGetValue(instanceId, out EquipInstanceInfo instance) ? instance : null;

    /// <summary>取角色某槽位上的实例（空槽返回 null）</summary>
    public EquipInstanceInfo GetEquipped(int charId, int slotType)
    {
        if (!slots.TryGetValue((charId, slotType), out long instanceId) || instanceId <= 0) return null;
        return GetInstance(instanceId);
    }

    /// <summary>某实例当前穿在哪个角色身上（-1 = 未装备）</summary>
    public int GetEquippedChar(long instanceId)
    {
        EquipInstanceInfo instance = GetInstance(instanceId);
        return instance != null ? instance.EquippedChar : -1;
    }

    /// <summary>
    /// 全部武器实例（武器选择列表的数据源）。
    /// 排序：稀有度降序 → 攻击力降序 → 实例ID升序（稳定，同一件不会跳位）。
    /// </summary>
    public List<EquipInstanceInfo> GetWeaponInstances()
    {
        var list = new List<EquipInstanceInfo>();
        foreach (EquipInstanceInfo instance in instances.Values)
        {
            EquipConfigInfo config = GetConfig(instance.ItemId);
            if (config != null && config.SlotType == SlotWeapon) list.Add(instance);
        }

        list.Sort((left, right) =>
        {
            int rarityCompare = GetRarity(right.ItemId).CompareTo(GetRarity(left.ItemId));
            if (rarityCompare != 0) return rarityCompare;

            float leftAttack = GetAttrValue(left.ItemId, AttrType.Attack);
            float rightAttack = GetAttrValue(right.ItemId, AttrType.Attack);
            int attackCompare = rightAttack.CompareTo(leftAttack);
            if (attackCompare != 0) return attackCompare;

            return left.InstanceId.CompareTo(right.InstanceId);
        });
        return list;
    }

    /// <summary>解析某件装备的属性加成（配置缺 attr_json 或解析失败返回空列表）</summary>
    public List<EquipAttrEntry> GetAttrEntries(int itemId)
    {
        if (attrCache.TryGetValue(itemId, out List<EquipAttrEntry> cached)) return cached;

        var parsed = new List<EquipAttrEntry>();
        EquipConfigInfo config = GetConfig(itemId);
        if (config != null && !string.IsNullOrEmpty(config.AttrJson))
        {
            try
            {
                EquipAttrList wrapper = JsonUtility.FromJson<EquipAttrList>("{\"items\":" + config.AttrJson + "}");
                if (wrapper?.items != null)
                {
                    foreach (EquipAttrEntry entry in wrapper.items)
                    {
                        if (!Enum.IsDefined(typeof(AttrType), entry.type)) continue;   // 未知枚举值跳过（服务端可能先加）
                        parsed.Add(entry);
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Equipment] 属性配置解析失败 itemId={itemId} json={config.AttrJson}: {exception.Message}");
            }
        }

        attrCache[itemId] = parsed;
        return parsed;
    }

    /// <summary>
    /// 计算某角色当前的装备加成合计（不含基础白值；属性预览由 UI 把基础值与这里相加显示）。
    /// candidateInstanceId 传非 0 时按"把这一件换上"预演（与当前已装备的其他槽叠加，本槽位以候选为准）。
    /// </summary>
    public EquipAttrTotals GetEquipAttrBonus(int charId, long candidateInstanceId = 0)
    {
        var totals = new EquipAttrTotals();
        var counted = new HashSet<long>();

        foreach (KeyValuePair<(int CharId, int SlotType), long> slot in slots)
        {
            if (slot.Key.CharId != charId) continue;
            if (candidateInstanceId > 0 && slot.Key.SlotType == SlotWeapon) continue;   // 本槽位由候选替换
            if (!counted.Add(slot.Value)) continue;
            AddAttrs(ref totals, slot.Value);
        }

        if (candidateInstanceId > 0 && counted.Add(candidateInstanceId)) AddAttrs(ref totals, candidateInstanceId);
        return totals;
    }

    /// <summary>把某实例的加成累加进总计</summary>
    private void AddAttrs(ref EquipAttrTotals totals, long instanceId)
    {
        EquipInstanceInfo instance = GetInstance(instanceId);
        if (instance == null) return;
        foreach (EquipAttrEntry entry in GetAttrEntries(instance.ItemId))
        {
            switch ((AttrType)entry.type)
            {
                case AttrType.Attack: totals.Attack += entry.value; break;
                case AttrType.Defense: totals.Defense += entry.value; break;
                case AttrType.DamageUp: totals.DamageUp += entry.value; break;
                case AttrType.DamageDown: totals.DamageDown += entry.value; break;
                case AttrType.MoveSpeed: totals.MoveSpeed += entry.value; break;
            }
        }
    }

    /// <summary>取某件装备某个属性的加成值（列表排序与对比文案用；没有该属性返回 0）</summary>
    public float GetAttrValue(int itemId, AttrType type)
    {
        foreach (EquipAttrEntry entry in GetAttrEntries(itemId))
        {
            if (entry.type == (int)type) return entry.value;
        }
        return 0f;
    }

    /// <summary>取物品稀有度（配置未加载时按 1 处理，保证排序稳定）</summary>
    private int GetRarity(int itemId)
    {
        ItemConfigInfo item = BagManager.Instance != null ? BagManager.Instance.GetConfig(itemId) : null;
        return item != null ? item.Rarity : 1;
    }

    /// <summary>
    /// 生成某角色当前的战斗快照（只含已穿戴装备与它们的加成）。
    /// 单机挑战直接用它应用属性；联机由客户端把它发给房主。
    /// </summary>
    public EquipBattleSnapshot BuildBattleSnapshot(int charId)
    {
        var snapshot = new EquipBattleSnapshot { charId = charId };
        foreach (KeyValuePair<(int CharId, int SlotType), long> slot in slots)
        {
            if (slot.Key.CharId != charId) continue;
            EquipInstanceInfo instance = GetInstance(slot.Value);
            if (instance == null) continue;

            var item = new EquipBattleItem
            {
                instanceId = instance.InstanceId,
                itemId = instance.ItemId,
                enhanceLevel = instance.EnhanceLevel,
            };
            foreach (EquipAttrEntry entry in GetAttrEntries(instance.ItemId))
            {
                item.attrs.Add(new EquipAttrEntry { type = entry.type, value = entry.value });
            }
            snapshot.items.Add(item);
        }
        return snapshot;
    }

    // ──────────────────────────────
    // 协议
    // ──────────────────────────────

    /// <summary>
    /// 确保数据可用：已加载直接返回，否则拉一次 3501。
    /// 打开角色页前调用；登录后也会主动拉一次（背包"已装备"标注依赖它）。
    /// </summary>
    public void EnsureLoaded()
    {
        if (!IsLoaded) RequestEquipment();

        // 名称/图标/稀有度来自背包的物品配置表：没拉过就顺手拉一次（复用同一份数据，不新增协议）
        if (BagManager.Instance != null && BagManager.Instance.Capacity == 0) BagManager.Instance.RequestBag();
    }

    /// <summary>请求装备列表（配置 + 实例 + 槽位）</summary>
    public void RequestEquipment()
    {
        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net == null || !net.IsConnected || net.MyPlayerId <= 0) return;
        if (IsLoading) return;

        IsLoading = true;
        Changed?.Invoke();
        _ = SendListAsync(net);
    }

    private async System.Threading.Tasks.Task SendListAsync(ClientNetworkManager net)
    {
        try
        {
            await net.SendEquipmentListRequest();
        }
        catch (Exception exception)
        {
            IsLoading = false;
            StatusChanged?.Invoke($"装备数据读取失败：{exception.Message}");
            Changed?.Invoke();
        }
    }

    /// <summary>穿戴（同角色同槽重复穿同一件由服务端判定幂等，客户端不拦）</summary>
    public void Equip(int charId, int slotType, long instanceId)
    {
        if (equipSentAt > 0f) return;   // 单在途：避免连点产生多份请求
        ClientNetworkManager net = ClientNetworkManager.Instance;
        if (net == null || !net.IsConnected) return;

        equipSentAt = Time.unscaledTime;
        _ = SendEquipAsync(net, charId, slotType, instanceId);
    }

    /// <summary>卸下某槽（等价于穿 0 号实例）</summary>
    public void Unequip(int charId, int slotType)
    {
        Equip(charId, slotType, 0);
    }

    private async System.Threading.Tasks.Task SendEquipAsync(ClientNetworkManager net, int charId, int slotType, long instanceId)
    {
        try
        {
            await net.SendEquip(charId, slotType, instanceId);
        }
        catch (Exception exception)
        {
            equipSentAt = 0f;
            StatusChanged?.Invoke($"装备操作失败：{exception.Message}");
        }
    }

    // ──────────────────────────────
    // 响应处理
    // ──────────────────────────────

    private void OnEquipmentListResponse(EquipmentListResponse response)
    {
        IsLoading = false;

        if (!response.Success)
        {
            StatusChanged?.Invoke(response.Message);
            Changed?.Invoke();
            return;
        }

        ApplyConfigs(response.Configs);
        ApplyInstances(response.Instances);
        ApplySlots(response.Slots);
        IsLoaded = true;
        Changed?.Invoke();
    }

    private void OnEquipResponse(EquipResponse response)
    {
        equipSentAt = 0f;

        if (response.Success)
        {
            // 回执带全量槽位与实例（服务端权威），失败路径也可能带，只应用成功态
            ApplyInstances(response.Instances);
            ApplySlots(response.Slots);
            if (!string.IsNullOrEmpty(response.Message) && !response.Unchanged) StatusChanged?.Invoke(response.Message);

            // 背包里的"已装备"标注跟着刷新（设计约定：装备变更后静默拉一次背包）
            BagManager.Instance?.RequestBag();
            Changed?.Invoke();
            EquipResult?.Invoke(true, response.Message);
            return;
        }

        if (!string.IsNullOrEmpty(response.Message)) StatusChanged?.Invoke(response.Message);
        Changed?.Invoke();
        EquipResult?.Invoke(false, response.Message);
    }

    private void ApplyConfigs(IEnumerable<EquipConfigInfo> source)
    {
        configs.Clear();
        attrCache.Clear();
        foreach (EquipConfigInfo config in source) configs[config.ItemId] = config;
    }

    private void ApplyInstances(IEnumerable<EquipInstanceInfo> source)
    {
        instances.Clear();
        foreach (EquipInstanceInfo instance in source) instances[instance.InstanceId] = instance;
    }

    private void ApplySlots(IEnumerable<EquipSlotInfo> source)
    {
        slots.Clear();
        foreach (EquipSlotInfo slot in source)
        {
            if (slot.InstanceId <= 0) continue;
            slots[(slot.CharId, slot.SlotType)] = slot.InstanceId;
        }
    }

    /// <summary>登录成功：清空上一账号的装备缓存（防串号），并主动拉一次（背包"已装备"标注不依赖打开角色页）</summary>
    private void OnLoginResponse(LoginResponse response)
    {
        if (!response.Success) return;
        ClearCache();
        Changed?.Invoke();
        RequestEquipment();
    }

    /// <summary>断线：保留缓存用于展示，标记为未加载——下次 EnsureLoaded 会重新拉取</summary>
    private void OnConnectionBroken()
    {
        IsLoaded = false;
        IsLoading = false;
        equipSentAt = 0f;
        Changed?.Invoke();
    }

    private void ClearCache()
    {
        configs.Clear();
        instances.Clear();
        slots.Clear();
        attrCache.Clear();
        IsLoaded = false;
        IsLoading = false;
        equipSentAt = 0f;
    }
}

/// <summary>装备加成合计（只含装备部分，不含角色基础白值）</summary>
public struct EquipAttrTotals
{
    /// <summary>攻击力加成（白值）</summary>
    public float Attack;

    /// <summary>防御力加成（白值）</summary>
    public float Defense;

    /// <summary>增伤系数</summary>
    public float DamageUp;

    /// <summary>减伤系数</summary>
    public float DamageDown;

    /// <summary>移速系数</summary>
    public float MoveSpeed;
}
