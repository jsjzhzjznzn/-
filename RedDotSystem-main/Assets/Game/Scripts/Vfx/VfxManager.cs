using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 特效播放句柄（Play 返回值）
/// 只承载身份（key + 实例Id），不持引用——失效句柄 Stop 是安全空操作，
/// 这样调用方可以随手存、跨帧存，不需要担心实例已被自动回收
/// </summary>
public readonly struct VfxHandle
{
    /// <summary>注册键（诊断/打印用）</summary>
    public string Key { get; }

    /// <summary>实例编号（0=无效句柄；每次播放自增，永不复用）</summary>
    public int Id { get; }

    /// <summary>是否指向一个已播放的特效（可能已自然回收——用 IsValid 后仍要容忍 Stop 空操作）</summary>
    public bool IsValid => Id != 0;

    public VfxHandle(string key, int id)
    {
        Key = key;
        Id = id;
    }
}

/// <summary>
/// 特效系统核心（注册表 + 对象池 + 生命周期管理）
///
/// 职责与边界：
/// - 世界空间 3D 粒子特效的按 key 播放（Play / PlayAttached）、停止（Stop / StopAll）、
///   自动回收（粒子播完 or 达到时长覆盖）与生命周期事件（VfxStarted / VfxFinished）；
/// - 池化：预热 + 按需增长 + 永不销毁（特效实例是高频短命对象，回收重用避免 GC 与 Instantiate 抖动）；
/// - 引用计数式清理：每个活跃实例只有一条账（唯一 Id），自然回收 / Stop / 场景切换释放各走同一条
///   Release 路径，杜绝二次回收与泄漏；场景卸载时 StopAll 把残留特效一并收干净；
/// - 时间语义：默认走缩放时间（timeScale=0 时随 PauseManager 冻结），条目标 unscaled 则照播照回收
///   （对齐项目暂停分层约定：世界冻结、UI 反馈照常）；
/// - UI 反馈特效不归本类管（见 UiFx：程序化 Image 粒子，天然活在 overlay canvas 里）。
///
/// 素材来源约定：prefab 由卡通特效库（TJGeneratorLibEffects）下载，材质为 Particles/Standard Unlit
/// （无光照粒子 shader，URP 兼容）；换素材/改风格只动注册表资产，不动代码。
/// </summary>
public class VfxManager : MonoBehaviour
{
    // ──────────────────────────────
    // 单例实现（照 BagManager / ChatManager 先例：运行时自举，无需手动挂载）
    // ──────────────────────────────

    /// <summary>全局唯一实例（战斗/玩法代码的特效入口；可能为 null—— Awake 之前的极早期调用要容忍）</summary>
    public static VfxManager Instance { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance != null) return;
        GameObject holder = new GameObject("VfxManager");
        holder.AddComponent<VfxManager>();
        DontDestroyOnLoad(holder);
    }

    // ──────────────────────────────
    // 生命周期事件（主线程触发；订阅方负责注销，随实例销毁自然失效）
    // ──────────────────────────────

    /// <summary>特效开始播放（Play 成功即触发）</summary>
    public event Action<VfxHandle> VfxStarted;

    /// <summary>特效结束（自然播完 / 达到时长 / 被 Stop / 场景释放，四种路径都触发且只触发一次）</summary>
    public event Action<VfxHandle> VfxFinished;

    // ──────────────────────────────
    // 配置
    // ──────────────────────────────

    /// <summary>注册表资产（运行时自举建不出序列化引用，Awake 用 Resources 兜底加载）</summary>
    [SerializeField] private VfxCatalog catalog;

    // ──────────────────────────────
    // 运行时状态
    // ──────────────────────────────

    /// <summary>每个注册 key 一条池（懒建：首次播放才建）</summary>
    private readonly Dictionary<string, EntryPool> pools = new(StringComparer.Ordinal);

    /// <summary>活跃实例账本（引用计数的事实来源；Id → 记录）</summary>
    private readonly List<ActiveVfx> active = new();

    /// <summary>实例编号发号器（只增不减，句柄永不复用 → 失效句柄天然安全）</summary>
    private int nextInstanceId;

    /// <summary>最近播放记录（诊断窗口展示；固定容量环形覆盖）</summary>
    private readonly List<PlayRecord> recentPlays = new();
    private const int RecentCapacity = 16;

    /// <summary>单 key 池：模板 + 空闲栈 + 租借计数</summary>
    private sealed class EntryPool
    {
        public VfxCatalog.Entry Entry;
        public Vector3 TemplateScale;   // prefab 根自带的缩放是美术设计的一部分，应用注册表缩放时要保住
        public readonly Stack<GameObject> Idle = new();
        public int Rented;
        public int TotalCreated;
    }

    /// <summary>活跃实例记录（每实例唯一一条，回收即移除）</summary>
    private sealed class ActiveVfx
    {
        public int Id;
        public string Key;
        public EntryPool Pool;
        public GameObject Instance;
        public ParticleSystem[] Systems;
        public bool Attached;          // PlayAttached 播放（目标销毁即回收）；Play 播放=false
        public Transform Follow;        // PlayAttached 的跟随目标
        public float ReleaseAt;         // 时长覆盖的到期时刻（无覆盖=Infinity）
        public float StartedAt;
        public bool Unscaled;
    }

    /// <summary>诊断快照（编辑器窗口只读展示，避免直接暴露内部可变状态）</summary>
    public readonly struct VfxSnapshot
    {
        public readonly string Key;
        public readonly int Id;
        public readonly float ElapsedSeconds;
        public readonly float RemainingSeconds;   // Infinity=无时长覆盖
        public readonly string FollowName;        // null=非跟随
        public VfxSnapshot(string key, int id, float elapsed, float remaining, string followName)
        {
            Key = key; Id = id; ElapsedSeconds = elapsed; RemainingSeconds = remaining; FollowName = followName;
        }
    }

    /// <summary>池状态快照（诊断窗口用）</summary>
    public readonly struct VfxPoolStats
    {
        public readonly string Key;
        public readonly int Prewarm, Rented, Pooled, TotalCreated;
        public readonly float DurationOverride;
        public readonly bool Unscaled;
        public VfxPoolStats(string key, int prewarm, int rented, int pooled, int created, float duration, bool unscaled)
        {
            Key = key; Prewarm = prewarm; Rented = rented; Pooled = pooled; TotalCreated = created;
            DurationOverride = duration; Unscaled = unscaled;
        }
    }

    /// <summary>一次播放的记录（诊断窗口展示最近 N 次播放）</summary>
    public readonly struct PlayRecord
    {
        public readonly string Key;
        public readonly float UnscaledTime;
        public readonly Vector3 Position;
        public readonly bool Attached;
        public PlayRecord(string key, float time, Vector3 position, bool attached)
        {
            Key = key; UnscaledTime = time; Position = position; Attached = attached;
        }
    }

    // ──────────────────────────────
    // Unity 生命周期
    // ──────────────────────────────

    private void Awake()
    {
        Instance = this;
        if (catalog == null) catalog = Resources.Load<VfxCatalog>("VfxCatalog");
        if (catalog == null)
        {
            Debug.LogWarning("[特效] 找不到注册表 Assets/Game/Resources/VfxCatalog.asset，Play 全部为空操作");
        }
        else
        {
            // 预热：高频短命特效（命中/受击）开 Play 就有实例可用，首次播放零 Instantiate
            foreach (VfxCatalog.Entry entry in catalog.AllEntries)
            {
                if (entry?.prefab == null || string.IsNullOrEmpty(entry.key)) continue;
                GetOrCreatePool(entry);   // 内部按 prewarm 建足空闲实例
            }
        }
        // 场景卸载清场：残留的世界特效跨场景播放是穿帮（fuben 打死怪的消散跟到 zhuye）
        SceneManager.sceneUnloaded += OnSceneUnloaded;
    }

    private void OnDestroy()
    {
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
        StopAll();
        foreach (EntryPool pool in pools.Values)
        {
            while (pool.Idle.Count > 0) Destroy(pool.Idle.Pop());
        }
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        if (active.Count == 0) return;
        float scaledNow = Time.time;
        float unscaledNow = Time.unscaledTime;
        for (int i = active.Count - 1; i >= 0; i--)
        {
            ActiveVfx record = active[i];

            // 跟随目标没了 → 立即回收（敌人死亡自毁、面板关闭等情况）
            if (record.Attached && record.Follow == null)
            {
                Release(record, i);
                continue;
            }

            // 时长覆盖到期（循环型特效的唯一自动回收路径）
            if (record.ReleaseAt <= (record.Unscaled ? unscaledNow : scaledNow))
            {
                Release(record, i);
                continue;
            }

            // 粒子自然播完 → 回收（至少留 0.1s，防止 Clear 后首帧 IsAlive 的边界误判）
            float elapsed = (record.Unscaled ? unscaledNow : scaledNow) - record.StartedAt;
            if (elapsed > 0.1f && !AnySystemAlive(record))
            {
                Release(record, i);
            }
        }
    }

    // ──────────────────────────────
    // 播放 API
    // ──────────────────────────────

    /// <summary>
    /// 在世界位置播放特效的方法
    /// </summary>
    /// <param name="key">参数名：key，类型：string，用途：注册键（VfxKeys 常量）</param>
    /// <param name="position">参数名：position，类型：Vector3，用途：世界坐标</param>
    /// <param name="rotation">参数名：rotation，类型：Quaternion，用途：朝向（注册表可再叠加统一偏移）</param>
    /// <param name="scaleMultiplier">参数名：scaleMultiplier，类型：float，用途：在注册表缩放之上再乘的倍率（默认1）</param>
    public VfxHandle Play(string key, Vector3 position, Quaternion rotation = default, float scaleMultiplier = 1f)
    {
        if (!TryRent(key, out EntryPool pool, out GameObject instance, out string failReason)) return default;
        instance.transform.SetParent(transform, false);
        instance.transform.SetPositionAndRotation(
            position + rotation * pool.Entry.positionOffset,   // 可见中心补偿（随播放朝向旋转）
            rotation * Quaternion.Euler(pool.Entry.rotationOffsetEuler));
        instance.transform.localScale = pool.TemplateScale * pool.Entry.scale * scaleMultiplier;
        return Activate(pool, instance, null, position);
    }

    /// <summary>
    /// 播放并附着跟随目标的方法（挂在目标身上跟着走；目标销毁自动回收）
    /// </summary>
    /// <param name="key">参数名：key，类型：string，用途：注册键（VfxKeys 常量）</param>
    /// <param name="parent">参数名：parent，类型：Transform，用途：跟随目标（父物体）</param>
    /// <param name="localPosition">参数名：localPosition，类型：Vector3，用途：相对目标的局部位置</param>
    /// <param name="localRotation">参数名：localRotation，类型：Quaternion，用途：相对目标的局部朝向</param>
    /// <param name="scaleMultiplier">参数名：scaleMultiplier，类型：float，用途：缩放倍率（默认1）</param>
    public VfxHandle PlayAttached(string key, Transform parent, Vector3 localPosition = default, Quaternion localRotation = default, float scaleMultiplier = 1f)
    {
        if (!TryRent(key, out EntryPool pool, out GameObject instance, out string failReason)) return default;
        instance.transform.SetParent(parent, false);
        instance.transform.localPosition = localPosition + localRotation * pool.Entry.positionOffset;
        instance.transform.localRotation = localRotation * Quaternion.Euler(pool.Entry.rotationOffsetEuler);
        instance.transform.localScale = pool.TemplateScale * pool.Entry.scale * scaleMultiplier;
        return Activate(pool, instance, parent, parent != null ? parent.position : Vector3.zero);
    }

    /// <summary>停掉一个句柄指向的特效（失效句柄=安全空操作）</summary>
    public void Stop(VfxHandle handle)
    {
        if (!handle.IsValid) return;
        for (int i = 0; i < active.Count; i++)
        {
            if (active[i].Id == handle.Id)
            {
                Release(active[i], i);
                return;
            }
        }
    }

    /// <summary>停掉某个 key 的全部活跃实例</summary>
    public void StopAll(string key)
    {
        for (int i = active.Count - 1; i >= 0; i--)
        {
            if (string.Equals(active[i].Key, key, StringComparison.Ordinal)) Release(active[i], i);
        }
    }

    /// <summary>停掉全部活跃实例（场景切换清场 / 诊断窗口手动收池用）</summary>
    public void StopAll()
    {
        for (int i = active.Count - 1; i >= 0; i--)
        {
            Release(active[i], i);
        }
    }

    // ──────────────────────────────
    // 查询（断言与诊断用）
    // ──────────────────────────────

    /// <summary>某 key 当前活跃实例数（E2E 断言的口径：Play 后=1，回收后=0）</summary>
    public int GetActiveCount(string key)
    {
        int count = 0;
        for (int i = 0; i < active.Count; i++)
        {
            if (string.Equals(active[i].Key, key, StringComparison.Ordinal)) count++;
        }
        return count;
    }

    /// <summary>活跃实例总数</summary>
    public int ActiveTotal => active.Count;

    /// <summary>注册表是否就绪（Play 前提；诊断窗口展示）</summary>
    public bool IsReady => catalog != null;

    /// <summary>按 key 查注册条目（诊断窗口与接线层校验用）</summary>
    public bool TryGetEntry(string key, out VfxCatalog.Entry entry)
    {
        entry = null;
        return catalog != null && catalog.TryGet(key, out entry);
    }

    /// <summary>收集活跃实例快照（诊断窗口；输出列表由调用方提供复用）</summary>
    public void CollectActive(List<VfxSnapshot> output)
    {
        output.Clear();
        float scaledNow = Time.time;
        float unscaledNow = Time.unscaledTime;
        foreach (ActiveVfx record in active)
        {
            float now = record.Unscaled ? unscaledNow : scaledNow;
            float remaining = float.IsPositiveInfinity(record.ReleaseAt)
                ? float.PositiveInfinity
                : Mathf.Max(0f, record.ReleaseAt - now);
            output.Add(new VfxSnapshot(record.Key, record.Id, now - record.StartedAt, remaining,
                record.Attached && record.Follow != null ? record.Follow.name : null));
        }
    }

    /// <summary>收集池状态快照（诊断窗口）</summary>
    public void CollectPoolStats(List<VfxPoolStats> output)
    {
        output.Clear();
        foreach (EntryPool pool in pools.Values)
        {
            output.Add(new VfxPoolStats(pool.Entry.key, pool.Entry.prewarm, pool.Rented, pool.Idle.Count,
                pool.TotalCreated, pool.Entry.durationOverride, pool.Entry.unscaled));
        }
    }

    /// <summary>最近播放记录（诊断窗口；内层 (key, unscaledTime, position, attached)）</summary>
    public IReadOnlyList<PlayRecord> RecentPlays => recentPlays;

    // ──────────────────────────────
    // 内部：租借 / 激活 / 回收
    // ──────────────────────────────

    private bool TryRent(string key, out EntryPool pool, out GameObject instance, out string failReason)
    {
        pool = null;
        instance = null;
        failReason = null;
        if (catalog == null) { failReason = "注册表未加载"; return false; }
        if (!catalog.TryGet(key, out VfxCatalog.Entry entry) || entry.prefab == null)
        {
            Debug.LogWarning($"[特效] 未注册的 key：{key}（注册表里没有或没拖 prefab）");
            failReason = "key 未注册";
            return false;
        }
        pool = GetOrCreatePool(entry);
        instance = pool.Idle.Count > 0 ? pool.Idle.Pop() : Instantiate(entry.prefab, transform);
        if (instance != null) pool.TotalCreated = Mathf.Max(pool.TotalCreated, pool.Rented + 1 + pool.Idle.Count);
        return true;
    }

    private EntryPool GetOrCreatePool(VfxCatalog.Entry entry)
    {
        if (!pools.TryGetValue(entry.key, out EntryPool pool))
        {
            pool = new EntryPool { Entry = entry, TemplateScale = entry.prefab.transform.localScale };
            pools.Add(entry.key, pool);
            for (int i = 0; i < Mathf.Max(0, entry.prewarm); i++)
            {
                GameObject prewarmed = Instantiate(entry.prefab, transform);
                prewarmed.SetActive(false);
                pool.Idle.Push(prewarmed);
            }
            pool.TotalCreated = pool.Idle.Count;
        }
        return pool;
    }

    private VfxHandle Activate(EntryPool pool, GameObject instance, Transform follow, Vector3 recordPosition)
    {
        instance.SetActive(true);

        // 重置粒子：清旧粒子 + 全系统重播（池化实例上一轮的残留必须归零；必须先激活再 Play）
        ParticleSystem[] systems = instance.GetComponentsInChildren<ParticleSystem>(true);
        foreach (ParticleSystem ps in systems) ps.Clear(true);
        foreach (ParticleSystem ps in systems)
        {
            ParticleSystem.MainModule main = ps.main;
            main.useUnscaledTime = pool.Entry.unscaled;   // Tuanjie 版 API：true=用 unscaled 时间（暂停中照播）
            ps.Play();
        }

        float now = pool.Entry.unscaled ? Time.unscaledTime : Time.time;
        ActiveVfx record = new()
        {
            Id = ++nextInstanceId,
            Key = pool.Entry.key,
            Pool = pool,
            Instance = instance,
            Systems = systems,
            Attached = follow != null,
            Follow = follow,
            StartedAt = now,
            Unscaled = pool.Entry.unscaled,
            ReleaseAt = pool.Entry.durationOverride > 0f ? now + pool.Entry.durationOverride : float.PositiveInfinity,
        };
        active.Add(record);
        pool.Rented++;

        recentPlays.Add(new PlayRecord(record.Key, Time.unscaledTime, recordPosition, follow != null));
        if (recentPlays.Count > RecentCapacity) recentPlays.RemoveAt(0);

        VfxHandle handle = new(record.Key, record.Id);
        VfxStarted?.Invoke(handle);
        return handle;
    }

    /// <summary>唯一回收路径：出账 → 归还父级/隐藏 → 回池 → 记账 → 事件（幂等由调用方保证：出账后才走到这里）</summary>
    private void Release(ActiveVfx record, int activeIndex)
    {
        active.RemoveAt(activeIndex);
        record.Pool.Rented--;
        if (record.Instance != null)
        {
            record.Instance.transform.SetParent(transform, false);
            record.Instance.SetActive(false);
            record.Pool.Idle.Push(record.Instance);
        }
        VfxFinished?.Invoke(new VfxHandle(record.Key, record.Id));
    }

    private static bool AnySystemAlive(ActiveVfx record)
    {
        ParticleSystem[] systems = record.Systems;
        for (int i = 0; i < systems.Length; i++)
        {
            if (systems[i] != null && systems[i].IsAlive(true)) return true;
        }
        return false;
    }

    private void OnSceneUnloaded(Scene scene)
    {
        StopAll();
        // 场景切换时池中实例保留（DontDestroyOnLoad 根下，下个场景直接复用）
    }
}
