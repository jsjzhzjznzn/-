using System;
using System.Collections.Generic;

using Common.Network;

using UnityEngine;

/// <summary>
/// 背包数据层（客户端缓存 + 协议收发）
/// 服务端权威：槽位快照与物品配置都由服务端下发，客户端只缓存与分发，不做任何规则判断
/// 两份缓存：slots = 定长 Capacity 的槽位快照（下标即槽位号，空槽为 null）；
///           configs = 物品静态配置表（随列表响应下发一次，供 UI 查名字/图标/堆叠上限）
/// UI（BagUIController）只订阅 BagUpdated 渲染，不直接碰网络（与 ChatManager 同款分层）
/// </summary>
public class BagManager : MonoBehaviour
{
    // ──────────────────────────────
    // 单例实现
    // ──────────────────────────────

    /// <summary>全局唯一实例（UI 层访问背包数据的入口）</summary>
    public static BagManager Instance { get; private set; }

    /// <summary>
    /// 运行时自举
    /// 进入 Play 模式自动创建，无需手动挂载（与 ClientNetworkManager / ChatManager 相同模式）
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance != null) return;
        GameObject holder = new GameObject("BagManager");
        holder.AddComponent<BagManager>();
        DontDestroyOnLoad(holder);
    }

    // ──────────────────────────────
    // 事件（主线程触发，UI 可安全订阅）
    // ──────────────────────────────

    /// <summary>背包数据变化（列表到达 / 任意操作成功后触发，UI 重渲染）</summary>
    public event Action BagUpdated;

    /// <summary>提示文案（操作成功/失败的 message，UI 显示在状态栏）</summary>
    public event Action<string> StatusChanged;

    // ──────────────────────────────
    // 私有字段
    // ──────────────────────────────

    /// <summary>物品静态配置：itemId → 配置（名字/类型/稀有度/图标键/描述/堆叠上限）</summary>
    private readonly Dictionary<int, ItemConfigInfo> configs = new();

    /// <summary>物品配置的稳定顺序列表（"加物品"按钮按序循环发放用；Dictionary 顺序不保证稳定）</summary>
    private readonly List<ItemConfigInfo> configOrder = new();

    /// <summary>槽位快照：长度 = 武器区容量 + 道具区容量，下标即槽位号，空槽为 null</summary>
    private BagSlotInfo[] slots = new BagSlotInfo[0];

    /// <summary>是否正在等待列表响应（UI 据此显示加载指示器）</summary>
    private bool loading;

    // ──────────────────────────────
    // 分区常量（与服务端 BagRepository 同源，抄错就对不齐）
    // ──────────────────────────────

    /// <summary>道具区最小容量（20 行 × 6 列）：一件道具都没有时也要有格子可看</summary>
    public const int ItemCapacityFloor = 120;

    /// <summary>道具区扩容步长（10 行 × 6 列）</summary>
    public const int ItemCapacityStep = 60;

    // ──────────────────────────────
    // 对外只读状态
    // ──────────────────────────────

    /// <summary>武器区容量（服务端下发；未加载过为 0）。道具区无固定上限，见 ItemCapacity。</summary>
    public int Capacity { get; private set; }

    /// <summary>道具区当前容量（按快照最高槽位推导，协议不变）</summary>
    public int ItemCapacity { get; private set; } = ItemCapacityFloor;

    /// <summary>槽位总数（武器区 + 道具区）</summary>
    public int SlotCount => Capacity + ItemCapacity;

    /// <summary>是否正在加载（打开面板拉列表期间为 true）</summary>
    public bool IsLoading => loading;

    /// <summary>武器区已占用格数</summary>
    public int UsedWeaponCount { get; private set; }

    /// <summary>道具区已占用格数</summary>
    public int UsedItemCount { get; private set; }

    /// <summary>已占用格数（两区合计）</summary>
    public int UsedSlotCount => UsedWeaponCount + UsedItemCount;

    /// <summary>该槽位是否属于武器区（武器区 [0, Capacity)）</summary>
    public bool IsWeaponSlot(int index) => index >= 0 && index < Capacity;

    /// <summary>物品配置（按 itemId 升序，供"加物品"循环发放）</summary>
    public IReadOnlyList<ItemConfigInfo> AllConfigs => configOrder;

    /// <summary>
    /// 取槽位的方法（越界或空槽返回 null，UI 直接判空即可）
    /// </summary>
    /// <param name="index">参数名：index，类型：int，用途：槽位下标</param>
    /// <returns>返回值类型：BagSlotInfo，用途：槽位快照（空槽为 null）</returns>
    public BagSlotInfo GetSlot(int index) => index >= 0 && index < slots.Length ? slots[index] : null;

    /// <summary>
    /// 取物品配置的方法
    /// </summary>
    /// <param name="itemId">参数名：itemId，类型：int，用途：物品ID</param>
    /// <returns>返回值类型：ItemConfigInfo，用途：配置（未加载到为 null）</returns>
    public ItemConfigInfo GetConfig(int itemId)
        => configs.TryGetValue(itemId, out ItemConfigInfo config) ? config : null;

    /// <summary>某槽位是否有物品（UI 判断能否使用/丢弃/拖拽）</summary>
    /// <param name="index">参数名：index，类型：int，用途：槽位下标</param>
    /// <returns>返回值类型：bool，用途：槽位是否非空</returns>
    public bool HasItem(int index) => GetSlot(index)?.Count > 0;

    // ──────────────────────────────
    // Unity 生命周期
    // ──────────────────────────────

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

    private void Start()
    {
        // ClientNetworkManager 的自举同样发生在 BeforeSceneLoad，Start 时必已存在
        if (ClientNetworkManager.Instance == null) return;
        ClientNetworkManager.Instance.BagListResponseReceived += OnBagListResponse;
        ClientNetworkManager.Instance.BagUseResponseReceived += OnBagUseResponse;
        ClientNetworkManager.Instance.BagDiscardResponseReceived += OnBagDiscardResponse;
        ClientNetworkManager.Instance.BagTidyResponseReceived += OnBagTidyResponse;
        ClientNetworkManager.Instance.BagSwapResponseReceived += OnBagSwapResponse;
        ClientNetworkManager.Instance.BagGrantResponseReceived += OnBagGrantResponse;
        ClientNetworkManager.Instance.GachaDrawResponseReceived += OnGachaDrawResponse;
        ClientNetworkManager.Instance.SignInClaimResponseReceived += OnSignInClaimResponse;
        ClientNetworkManager.Instance.LoginResponseReceived += OnLoginResponse;
    }

    private void OnDestroy()
    {
        if (ClientNetworkManager.Instance == null) return;
        ClientNetworkManager.Instance.BagListResponseReceived -= OnBagListResponse;
        ClientNetworkManager.Instance.BagUseResponseReceived -= OnBagUseResponse;
        ClientNetworkManager.Instance.BagDiscardResponseReceived -= OnBagDiscardResponse;
        ClientNetworkManager.Instance.BagTidyResponseReceived -= OnBagTidyResponse;
        ClientNetworkManager.Instance.BagSwapResponseReceived -= OnBagSwapResponse;
        ClientNetworkManager.Instance.BagGrantResponseReceived -= OnBagGrantResponse;
        ClientNetworkManager.Instance.GachaDrawResponseReceived -= OnGachaDrawResponse;
        ClientNetworkManager.Instance.SignInClaimResponseReceived -= OnSignInClaimResponse;
        ClientNetworkManager.Instance.LoginResponseReceived -= OnLoginResponse;
    }

    // ──────────────────────────────
    // 请求（UI 调这些，不直接碰网络）
    // ──────────────────────────────

    /// <summary>请求背包列表（打开面板时调用；物品配置表随本次响应一起下发）</summary>
    public void RequestBag()
    {
        if (ClientNetworkManager.Instance == null) return;
        loading = true;
        BagUpdated?.Invoke();   // 先刷一次让 UI 立刻显示加载指示器
        _ = ClientNetworkManager.Instance.SendBagListRequest();
    }

    /// <summary>使用物品（数量默认 1；校验与扣减都在服务端）</summary>
    /// <param name="slotIndex">参数名：slotIndex，类型：int，用途：目标槽位下标</param>
    /// <param name="count">参数名：count，类型：int，用途：使用数量</param>
    public void UseItem(int slotIndex, int count = 1)
    {
        if (!HasItem(slotIndex) || ClientNetworkManager.Instance == null) return;
        _ = ClientNetworkManager.Instance.SendBagUse(slotIndex, count);
    }

    /// <summary>丢弃物品（数量默认 1；丢光则该槽清空）</summary>
    /// <param name="slotIndex">参数名：slotIndex，类型：int，用途：目标槽位下标</param>
    /// <param name="count">参数名：count，类型：int，用途：丢弃数量</param>
    public void DiscardItem(int slotIndex, int count = 1)
    {
        if (!HasItem(slotIndex) || ClientNetworkManager.Instance == null) return;
        _ = ClientNetworkManager.Instance.SendBagDiscard(slotIndex, count);
    }

    /// <summary>整理背包（同类合并 + 排序，规则在服务端）</summary>
    public void Tidy()
    {
        if (ClientNetworkManager.Instance == null) return;
        _ = ClientNetworkManager.Instance.SendBagTidy();
    }

    /// <summary>槽位换位（拖拽换位的服务端落地）</summary>
    /// <param name="fromSlot">参数名：fromSlot，类型：int，用途：起点槽位下标</param>
    /// <param name="toSlot">参数名：toSlot，类型：int，用途：目标槽位下标</param>
    public void SwapSlots(int fromSlot, int toSlot)
    {
        if (fromSlot == toSlot || ClientNetworkManager.Instance == null) return;
        _ = ClientNetworkManager.Instance.SendBagSwap(fromSlot, toSlot);
    }

    /// <summary>发放物品（测试发放，本期物品唯一来源）</summary>
    /// <param name="itemId">参数名：itemId，类型：int，用途：物品ID</param>
    /// <param name="count">参数名：count，类型：int，用途：发放数量</param>
    public void Grant(int itemId, int count)
    {
        if (itemId <= 0 || count <= 0 || ClientNetworkManager.Instance == null) return;
        _ = ClientNetworkManager.Instance.SendBagGrant(itemId, count);
    }

    // ──────────────────────────────
    // 响应处理
    // ──────────────────────────────

    /// <summary>列表响应：整表替换（容量 + 配置表 + 槽位快照），失败只提示不清缓存</summary>
    private void OnBagListResponse(BagListResponse response)
    {
        loading = false;

        if (!response.Success)
        {
            StatusChanged?.Invoke(response.Message);
            BagUpdated?.Invoke();
            return;
        }

        Capacity = Math.Max(1, response.Capacity);

        configs.Clear();
        configOrder.Clear();
        foreach (ItemConfigInfo config in response.Configs)
        {
            configs[config.ItemId] = config;
            configOrder.Add(config);
        }
        configOrder.Sort((left, right) => left.ItemId.CompareTo(right.ItemId));

        ApplySlots(response.Slots);
        BagUpdated?.Invoke();
    }

    private void OnBagUseResponse(BagUseResponse response)
        => ApplyOperationResult(response.Success, response.Message, response.Slots);

    private void OnBagDiscardResponse(BagDiscardResponse response)
        => ApplyOperationResult(response.Success, response.Message, response.Slots);

    private void OnBagTidyResponse(BagTidyResponse response)
        => ApplyOperationResult(response.Success, response.Message, response.Slots);

    private void OnBagSwapResponse(BagSwapResponse response)
        => ApplyOperationResult(response.Success, response.Message, response.Slots);

    private void OnBagGrantResponse(BagGrantResponse response)
        => ApplyOperationResult(response.Success, response.Message, response.Slots);

    /// <summary>
    /// 唤取响应处理：槽位快照整表应用 + 涉及物品配置并入本地表（唤取可不经背包面板直接发起，
    /// 配置不全物品在背包里会显示不出来）。特例修复：背包从未拉过列表时 Capacity==0 会摊不平快照
    /// （已知历史坑），此处按服务端定长容量补齐后再应用——只影响本响应路径，不动既有方法。
    /// </summary>
    private void OnGachaDrawResponse(GachaDrawResponse response)
    {
        if (response.Success)
        {
            foreach (ItemConfigInfo config in response.Configs)
            {
                if (!configs.ContainsKey(config.ItemId))
                {
                    configs[config.ItemId] = config;
                    configOrder.Add(config);
                }
            }
            if (configOrder.Count > 0) configOrder.Sort((left, right) => left.ItemId.CompareTo(right.ItemId));
            if (Capacity == 0) Capacity = 500;   // 与服务端 BagRepository.BagCapacity 同源（定长容量）
        }
        ApplyOperationResult(response.Success, response.Message, response.Slots);
    }

    /// <summary>按物品ID统计持有总量（唤取界面显示媒介余量用；服务端权威，本地缓存读）。
    /// 注意：slots 里空槽是 null 引用（BagSlotInfo 是 protobuf 类，new 数组 ≠ 空槽），必须判空。</summary>
    public int GetItemCount(int itemId)
    {
        if (slots == null) return 0;
        int total = 0;
        foreach (BagSlotInfo slot in slots)
        {
            if (slot != null && slot.ItemId == itemId) total += slot.Count;
        }
        return total;
    }

    /// <summary>
    /// 签到回执的背包快照应用（复刻 OnGachaDrawResponse 口径：合并配置 + Capacity 补齐 + 槽位整表替换）。
    /// 签到奖励的媒介余量/物品到账提示依赖这条链路——只订阅不主动拉。
    /// </summary>
    private void OnSignInClaimResponse(SignInClaimResponse response)
    {
        if (response.Success)
        {
            foreach (ItemConfigInfo config in response.Configs)
            {
                if (!configs.ContainsKey(config.ItemId))
                {
                    configs[config.ItemId] = config;
                    configOrder.Add(config);
                }
            }
            if (configOrder.Count > 0) configOrder.Sort((left, right) => left.ItemId.CompareTo(right.ItemId));
            if (Capacity == 0) Capacity = 500;   // 与服务端 BagRepository.BagCapacity 同源（定长容量）
        }
        ApplyOperationResult(response.Success, response.Message, response.Slots);
    }

    /// <summary>
    /// 操作响应统一处理：成功则用服务端返回的槽位快照整表替换（服务端权威，不做本地预测），
    /// 失败只提示不动作；两种情况都广播状态文案
    /// </summary>
    private void ApplyOperationResult(bool success, string message, IEnumerable<BagSlotInfo> snapshot)
    {
        if (success) ApplySlots(snapshot);
        if (!string.IsNullOrEmpty(message)) StatusChanged?.Invoke(message);
        if (success) BagUpdated?.Invoke();
    }

    /// <summary>
    /// 把服务端槽位快照摊平成数组（只收非空槽，负索引防御性忽略）。
    /// 数组长度按「武器区容量 + 道具区容量」算：道具区容量取 max(基数, 快照最高道具槽 - 武器区容量 + 1 向上取整到步长)，
    /// 与服务端 ItemCapacityFor 同一公式，因此协议里不需要新增字段。
    /// </summary>
    private void ApplySlots(IEnumerable<BagSlotInfo> snapshot)
    {
        var incoming = new List<BagSlotInfo>();
        int maxIndex = -1;
        foreach (BagSlotInfo slot in snapshot)
        {
            if (slot == null || slot.Count <= 0 || slot.SlotIndex < 0) continue;
            incoming.Add(slot);
            if (slot.SlotIndex > maxIndex) maxIndex = slot.SlotIndex;
        }

        ItemCapacity = ItemCapacityFor(maxIndex);
        var fresh = new BagSlotInfo[Capacity + ItemCapacity];
        int weapons = 0, items = 0;
        foreach (BagSlotInfo slot in incoming)
        {
            if (slot.SlotIndex >= fresh.Length) continue;   // 防御：超出本次推导长度（理论上不会）
            fresh[slot.SlotIndex] = slot;
            // 计数按槽位区间走，保证与页签里看到的格子一致（历史数据把道具放在武器区时，
            // 它会先显示在武器页，按一次「整理」后按类型归位到道具区）
            if (slot.SlotIndex < Capacity) weapons++;
            else items++;
        }
        slots = fresh;
        UsedWeaponCount = weapons;
        UsedItemCount = items;
    }

    /// <summary>道具区容量推导（与服务端 BagRepository.ItemCapacityFor 同一公式）。</summary>
    private int ItemCapacityFor(int maxSlotIndex)
    {
        if (Capacity <= 0 || maxSlotIndex < Capacity) return ItemCapacityFloor;
        int used = maxSlotIndex - Capacity + 1;
        int rounded = ((used + ItemCapacityStep - 1) / ItemCapacityStep) * ItemCapacityStep;
        return Math.Max(ItemCapacityFloor, rounded);
    }

    /// <summary>登录成功：清空上一账号的背包缓存（防串号，与 ChatManager 同款处理）</summary>
    private void OnLoginResponse(LoginResponse response)
    {
        if (!response.Success) return;
        configs.Clear();
        configOrder.Clear();
        slots = new BagSlotInfo[0];
        Capacity = 0;
        ItemCapacity = ItemCapacityFloor;
        UsedWeaponCount = 0;
        UsedItemCount = 0;
        loading = false;
        BagUpdated?.Invoke();
    }
}
