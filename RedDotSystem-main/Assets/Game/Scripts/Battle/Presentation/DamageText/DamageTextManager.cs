using System.Collections.Generic;
using TMPro;
using UnityEngine;
using SkierFramework;

/// <summary>
/// 飘字反馈类别：决定颜色、字号、合并键与是否显示文案
/// </summary>
public enum DamageTextKind
{
    Normal = 0,     // 普通伤害
    Critical = 1,   // 暴击伤害
    Dot = 2,        // 持续伤害（Buff 灼烧/中毒 tick）
    Dodge = 3,      // 闪避/无敌拦下（显示 MISS）
    Heal = 4        // 治疗
}

/// <summary>
/// 飘字样式：每个类别配一套
/// </summary>
[System.Serializable]
public class DamageTextStyle
{
    public Color color = Color.white;
    public int fontSize = 56;
    public float shake = 0f;          // 入场抖动幅度（度），0 表示不抖
    public float scaleMul = 1f;       // 整体缩放乘数
    public float outlineWidth = 0.12f;
    public string label;              // 显示文案（ASCII，<=6 字符）；空表示显示数字
    public float mergeWindow = 0f;    // 该类别的合并窗口（秒）；0 表示用全局值
}

/// <summary>
/// 伤害飘字管理器（单例，DontDestroyOnLoad）
///
/// 【职责】
///   订阅 E_OnDamage / E_DamageBlocked，把反馈写进一个定长结构体数组；每帧推进动画并把结果交给
///   DamageTextRenderer 一次性实例化绘制。
///
/// 【为什么不是一条飘字一个 UGUI 对象】
///   一条一个 TextMeshProUGUI 时，任何一条改文本都会触发整层 Canvas 重建，
///   1000 条规模下重建与激活开销会主导帧时间。这里一条飘字只是一个数据槽 +
///   一个 GPU 实例，运行期不建不删 GameObject、不分配托管内存。
///
/// 【为什么动画不用 DOTween / 协程】
///   每条一个 tween 意味着每帧一份调度与托管对象。这里所有动画都是 t 的纯函数，
///   在 Update 里算完直接写实例矩阵。
///
/// 【数字不走字符串】
///   数值按 4bit/位打包进 int，着色器负责取字形与字距，不产生任何 string。
///
/// 【合并】
///   同一目标（同类别）在 mergeWindow 内连续命中会累加到同一条并重新弹入，连击读起来是
///   「一个不断跳大的数字」，同时把同屏条数压下来。合并键带类别，伤害不会吞掉治疗与闪避。
/// </summary>
public class DamageTextManager : global::Singleton<DamageTextManager>
{
    private struct FloatingText
    {
        public Vector2 origin;      // 屏幕像素坐标
        public float age;
        public float rotation;      // 初始倾斜（度）
        public float fontSize;
        public float shake;         // 抖动幅度（度）
        public float scaleMul;
        public Color color;
        public int value;
        public int packed;          // 4bit/位，低位是最右边一位
        public int digitCount;
        public TextGlyph glyph;     // 文案（空表示显示数字）
        public int targetId;        // 目标实例 ID，0 表示不参与合并
        public DamageTextKind kind;
        public float mergeWindow;   // 生成时快照的合并窗口
    }

    private struct HeadCache
    {
        public GameObject target;
        public Renderer renderer;
        public Vector3 offset;      // 头顶相对目标根节点的偏移
        public int lastUsedFrame;
    }

    private const int DropSlotCount = 8;      // 每个目标的落点槽位数
    private const int HeadCacheLimit = 64;    // 头顶点缓存条目上限

    [Header("字体（不填则用 TMP 默认字体）")]
    [SerializeField] private TMP_FontAsset fontAsset;

    [Header("样式")]
    [SerializeField] private DamageTextStyle normalStyle =
        new DamageTextStyle { color = new Color(1f, 0.94f, 0.76f), fontSize = 56, scaleMul = 1f, outlineWidth = 0.12f };
    [SerializeField] private DamageTextStyle criticalStyle =
        new DamageTextStyle { color = new Color(1f, 0.61f, 0.10f), fontSize = 76, shake = 3f, scaleMul = 1.25f, outlineWidth = 0.26f };
    [SerializeField] private DamageTextStyle dotStyle =
        new DamageTextStyle { color = new Color(0.82f, 0.55f, 0.95f), fontSize = 46, scaleMul = 0.9f, outlineWidth = 0.2f };
    [SerializeField] private DamageTextStyle dodgeStyle =
        new DamageTextStyle { color = new Color(0.72f, 0.86f, 1f), fontSize = 40, scaleMul = 1f, outlineWidth = 0.22f, label = "MISS" };
    [SerializeField] private DamageTextStyle healStyle =
        new DamageTextStyle { color = new Color(0.45f, 0.95f, 0.55f), fontSize = 52, scaleMul = 1f, outlineWidth = 0.18f, label = "+" };

    [Header("动效")]
    [SerializeField, Min(0.05f)] private float duration = 0.9f;
    [SerializeField, Min(0f)] private float floatHeight = 86f;
    [SerializeField, Min(0f)] private float settleDrop = 12f;
    [SerializeField, Min(0f)] private float riseTime = 0.55f;
    [SerializeField, Min(0.01f)] private float popTime = 0.14f;
    [SerializeField, Min(0f)] private float scaleFrom = 0.55f;
    [SerializeField, Range(0f, 1f)] private float fadeStart = 0.45f;
    [SerializeField, Min(0f)] private float shakeTime = 0.18f;

    [Header("随机")]
    [SerializeField, Min(0f)] private float randomOffsetRange = 20f;     // 同目标落点的最大水平散开（像素）
    [SerializeField, Min(0f)] private float randomVerticalRange = 10f;   // 垂直偏移（像素）
    [SerializeField, Min(0f)] private float tiltRange = 8f;              // 倾斜（度）

    [Header("位置")]
    [SerializeField, Min(0f)] private float headOffsetY = 1.4f;

    [Header("合并与限流")]
    [SerializeField, Min(0.01f)] private float mergeWindow = 0.25f;
    [SerializeField, Min(0.01f)] private float mergeWindowHighLoad = 0.45f;
    [SerializeField, Min(0)] private int highLoadCount = 700;
    [SerializeField, Min(1)] private int maxSpawnPerFrame = 32;
    [SerializeField, Min(16)] private int capacity = 1024;

    [Header("可见性")]
    [Tooltip("联机时只飘本机角色相关的伤害（受击者或攻击者为本机角色）")]
    [SerializeField] private bool onlyShowLocalPlayerInvolved = true;

    [Header("描边")]
    [SerializeField] private Color outlineColor = new Color(0f, 0f, 0f, 0.8f);

    private FloatingText[] _texts;
    private DamageTextDrawData[] _drawBuffer;
    private int _count;
    private int _spawnBudget;

    // 头顶点缓存：按目标缓存渲染器与头顶偏移，避免每击一次 GetComponentInChildren
    private readonly Dictionary<int, HeadCache> _headCache = new Dictionary<int, HeadCache>();
    private readonly List<int> _headStale = new List<int>();

    // 落点槽位：按目标分配固定方向，目标从飘字表里完全消失后释放
    private readonly Dictionary<int, bool[]> _slotUsage = new Dictionary<int, bool[]>();

    private DamageTextRenderer _renderer;
    private Camera _cachedCamera;
    private bool _localFilterWarned;

    protected override void Awake()
    {
        base.Awake();
        int cap = Mathf.Max(16, capacity);
        _texts = new FloatingText[cap];
        _drawBuffer = new DamageTextDrawData[cap];
        EnsureReady();
    }

    private void OnEnable()
    {
        EventCenter.MainInstance.AddListener<DamageData>(E_EventType.E_OnDamage, this, OnDamage);
        EventCenter.MainInstance.AddListener<DamageData>(E_EventType.E_DamageBlocked, this, OnBlocked);
    }

    private void OnDisable()
    {
        EventCenter.MainInstance.UnregisterTarget(this);
        _count = 0;
        _headCache.Clear();
        _slotUsage.Clear();
    }

    private void EnsureReady()
    {
        fontAsset = fontAsset != null ? fontAsset : TMP_Settings.defaultFontAsset;

        if (_renderer == null) _renderer = GetComponent<DamageTextRenderer>();
        if (_renderer == null) _renderer = gameObject.AddComponent<DamageTextRenderer>();
        _renderer.Initialize(fontAsset, normalStyle != null ? normalStyle.outlineWidth : 0.12f, outlineColor);
    }

    private void Update()
    {
        _spawnBudget = Mathf.Max(1, maxSpawnPerFrame);

        if (_count <= 0)
        {
            if (_slotUsage.Count > 0) _slotUsage.Clear();
            return;
        }

        float life = Mathf.Max(0.05f, duration);
        float dt = Time.deltaTime;
        int drawCount = 0;

        for (int i = 0; i < _count; i++)
        {
            _texts[i].age += dt;
            if (_texts[i].age >= life)
            {
                RemoveAt(i);
                i--;
                continue;
            }
            _drawBuffer[drawCount++] = BuildDrawData(_texts[i], life);
        }

        ReleaseSlotsIfIdle();

        if (drawCount <= 0) return;

        Camera camera = ResolveWorldCamera();
        if (camera == null) return;
        _renderer.Draw(_drawBuffer, drawCount, camera);
    }

    // ==================== 每帧动画 ====================

    private DamageTextDrawData BuildDrawData(FloatingText text, float life)
    {
        float t = Mathf.Clamp01(text.age / life);

        // 上浮：先快后慢
        float riseT = Mathf.Clamp01(t / Mathf.Max(0.0001f, riseTime));
        float rise = (1f - (1f - riseT) * (1f - riseT)) * floatHeight;

        // 尾部微坠
        float fallT = Mathf.Clamp01((t - riseTime) / Mathf.Max(0.0001f, 1f - riseTime));
        float fall = settleDrop * fallT * fallT;

        // 弹入：过冲回弹
        float popT = Mathf.Clamp01(t / Mathf.Max(0.0001f, popTime));
        float scale = Mathf.LerpUnclamped(scaleFrom, 1f, EaseOutBack(popT)) * text.scaleMul;

        // 渐隐
        float alpha = t <= fadeStart ? 1f : 1f - (t - fadeStart) / Mathf.Max(0.0001f, 1f - fadeStart);

        float rotation = text.rotation;
        if (text.shake > 0f && t < shakeTime)
            rotation += Mathf.Sin(t * 90f + text.rotation) * text.shake * (1f - t / shakeTime);

        Color color = text.color;
        color.a *= Mathf.Clamp01(alpha);

        var data = new DamageTextDrawData
        {
            origin = new Vector2(text.origin.x, text.origin.y + rise - fall),
            rotation = rotation,
            scale = scale,
            fontSize = text.fontSize,
            packed = text.packed,
            digitCount = text.digitCount,
            glyph = text.glyph,
            color = color
        };
        return data;
    }

    private static float EaseOutBack(float x)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        float p = x - 1f;
        return 1f + c3 * p * p * p + c1 * p * p;
    }

    // ==================== 事件入口 ====================

    private void OnDamage(DamageData data)
    {
        DamageTextKind kind = data.isCritical ? DamageTextKind.Critical
            : data.isDoT ? DamageTextKind.Dot
            : DamageTextKind.Normal;
        ShowDamage(data.target, data.source, data.amount, kind);
    }

    private void OnBlocked(DamageData data)
    {
        ShowDodge(data.target);
    }

    /// <summary>对外接口：在目标头顶飘一条伤害（amount 为实际扣血量）</summary>
    public void ShowDamage(GameObject target, GameObject source, float amount, DamageTextKind kind)
    {
        if (!CanShow(target, source)) return;

        Camera camera = ResolveWorldCamera();
        if (camera == null) return;

        Vector3 world = ResolveHeadPoint(target);
        Vector3 screen = camera.WorldToScreenPoint(world);
        if (screen.z < 0f) return;   // 目标在相机背后

        screen.y += Random.Range(-randomVerticalRange, randomVerticalRange);

        int amountValue = Mathf.Max(1, Mathf.RoundToInt(amount));
        Spawn(amountValue, screen, StyleOf(kind), target, kind);
    }

    /// <summary>对外接口：目标闪避/无敌拦下伤害，飘示文案（无文案时显示数字 0）</summary>
    public void ShowDodge(GameObject target)
    {
        if (!CanShow(target, null)) return;

        Camera camera = ResolveWorldCamera();
        if (camera == null) return;

        Vector3 screen = camera.WorldToScreenPoint(ResolveHeadPoint(target));
        if (screen.z < 0f) return;

        screen.y += Random.Range(-randomVerticalRange, randomVerticalRange);
        Spawn(0, screen, StyleOf(DamageTextKind.Dodge), target, DamageTextKind.Dodge);
    }

    /// <summary>对外接口：目标回血，飘出治疗数字（由回血效果在结算后调用）</summary>
    public void ShowHeal(GameObject target, float amount)
    {
        if (!CanShow(target, null)) return;

        Camera camera = ResolveWorldCamera();
        if (camera == null) return;

        Vector3 screen = camera.WorldToScreenPoint(ResolveHeadPoint(target));
        if (screen.z < 0f) return;

        screen.y += Random.Range(-randomVerticalRange, randomVerticalRange);
        int amountValue = Mathf.Max(1, Mathf.RoundToInt(amount));
        Spawn(amountValue, screen, StyleOf(DamageTextKind.Heal), target, DamageTextKind.Heal);
    }

    /// <summary>
    /// 对外接口：手动飘一条数字（screenPos 为屏幕像素坐标）。
    /// </summary>
    public void Show(int amount, Vector2 screenPos, DamageTextStyle style)
    {
        Spawn(amount, screenPos, style, null, DamageTextKind.Normal);
    }

    private DamageTextStyle StyleOf(DamageTextKind kind)
    {
        switch (kind)
        {
            case DamageTextKind.Critical: return criticalStyle != null ? criticalStyle : normalStyle;
            case DamageTextKind.Dot: return dotStyle != null ? dotStyle : normalStyle;
            case DamageTextKind.Dodge: return dodgeStyle != null ? dodgeStyle : normalStyle;
            case DamageTextKind.Heal: return healStyle != null ? healStyle : normalStyle;
            default: return normalStyle;
        }
    }

    /// <summary>
    /// 联机可见性：只飘「本机角色参与」的战斗——本机角色打人、或本机角色挨打。
    /// 本机操控的角色只有一个（本机拥有者端；单机未 spawn 时 IsRemote 恒为 false）。
    /// 出手方与受击方都不是本机角色时（别人之间的互相伤害）不打飘字，避免同一场战斗被两端各飘一次。
    /// 识别不到本机角色时退化为不过滤（演示/测试场景）。
    /// </summary>
    private bool CanShow(GameObject target, GameObject source)
    {
        if (target == null) return false;
        if (!onlyShowLocalPlayerInvolved) return true;

        Player[] players = FindObjectsOfType<Player>();
        if (players == null || players.Length == 0) return true;

        Player localPlayer = null;
        for (int i = 0; i < players.Length; i++)
        {
            if (!IsControlledByMe(players[i])) continue;
            localPlayer = players[i];
            break;
        }

        if (localPlayer == null)
        {
            if (!_localFilterWarned)
            {
                _localFilterWarned = true;
                GameLog.Warn("[DamageText] 场景里有玩家但没有本机角色，本机过滤退化为全部飘字");
            }
            return true;
        }

        // 只有「打人/挨打的是本机角色」才飘字；非本机角色之间的伤害不重复飘
        GameObject localRoot = localPlayer.gameObject;
        bool targetIsLocal = target == localRoot || target.transform.IsChildOf(localPlayer.transform);
        bool sourceIsLocal = source != null
            && (source == localRoot || source.transform.IsChildOf(localPlayer.transform));
        return targetIsLocal || sourceIsLocal;
    }

    /// <summary>该角色是否为本机操控（联网时远端镜像指向别人的座位）</summary>
    private static bool IsControlledByMe(Player player)
    {
        return player != null && !player.IsRemote;
    }

    private void Spawn(int amount, Vector2 origin, DamageTextStyle style, GameObject target, DamageTextKind kind)
    {
        if (style == null || _texts == null) return;

        int targetId = target != null ? target.GetInstanceID() : 0;
        float window = style.mergeWindow > 0f
            ? style.mergeWindow
            : (_count >= highLoadCount ? mergeWindowHighLoad : this.mergeWindow);

        int mergeIndex = FindMergeIndex(targetId, kind, window);
        if (mergeIndex >= 0)
        {
            MergeInto(mergeIndex, amount);
            return;
        }

        // 本帧新建额度用尽：能合并就合并，否则丢弃（避免瞬时爆发卡帧）
        if (_spawnBudget <= 0)
        {
            mergeIndex = FindMergeIndex(targetId, kind, float.MaxValue);
            if (mergeIndex >= 0) MergeInto(mergeIndex, amount);
            return;
        }

        int index;
        if (_count >= _texts.Length)
        {
            index = FindOldestIndex();   // 容量满：顶替最老的一条
        }
        else
        {
            index = _count;
            _count++;
        }

        _spawnBudget--;

        if (targetId != 0)
        {
            int slot = AcquireSlot(targetId);
            float spacing = randomOffsetRange * 0.6f;
            origin.x += (slot - (DropSlotCount - 1) * 0.5f) * spacing
                      + Random.Range(-spacing * 0.2f, spacing * 0.2f);

            // 倾斜按槽位固定，避免同目标的多条数字互相压住
            float angle = (slot - (DropSlotCount - 1) * 0.5f) / ((DropSlotCount - 1) * 0.5f) * tiltRange;
            _texts[index].rotation = angle + Random.Range(-1f, 1f);
        }
        else
        {
            origin.x += Random.Range(-randomOffsetRange, randomOffsetRange);
            _texts[index].rotation = 0f;
        }

        // 大号 BOSS 上半身也可能投影到视野外，夹进屏幕内保证一定看得见
        const float margin = 48f;
        origin.x = Mathf.Clamp(origin.x, margin, Screen.width - margin);
        origin.y = Mathf.Clamp(origin.y, margin, Screen.height - margin);

        TextGlyph glyph = DamageTextRenderer.DecodeLabel(style.label);
        int packed = 0;
        int digits = 0;
        if (glyph.IsEmpty) PackDigits(amount, out packed, out digits);

        _texts[index] = new FloatingText
        {
            origin = origin,
            age = 0f,
            fontSize = style.fontSize,
            shake = style.shake,
            scaleMul = style.scaleMul,
            color = style.color,
            value = amount,
            packed = packed,
            digitCount = digits,
            glyph = glyph,
            targetId = targetId,
            kind = kind,
            mergeWindow = window
        };
    }

    private int FindMergeIndex(int targetId, DamageTextKind kind, float window)
    {
        if (targetId == 0) return -1;
        for (int i = 0; i < _count; i++)
        {
            if (_texts[i].targetId != targetId) continue;
            if (_texts[i].kind != kind) continue;
            if (_texts[i].age > window) continue;
            return i;
        }
        return -1;
    }

    private void MergeInto(int index, int amount)
    {
        // 文案类（MISS 等）的数值没有意义，合并只做重新弹入
        if (!_texts[index].glyph.IsEmpty)
        {
            _texts[index].age = 0f;
            return;
        }

        int value = _texts[index].value + amount;
        int packed, digits;
        PackDigits(value, out packed, out digits);

        _texts[index].value = value;
        _texts[index].packed = packed;
        _texts[index].digitCount = digits;
        _texts[index].age = 0f;   // 重新弹入一次
    }

    private int FindOldestIndex()
    {
        int oldest = 0;
        float maxAge = _texts[0].age;
        for (int i = 1; i < _count; i++)
        {
            if (_texts[i].age > maxAge)
            {
                maxAge = _texts[i].age;
                oldest = i;
            }
        }
        return oldest;
    }

    /// <summary>swap-back 回收：末尾覆盖被删位，O(1) 且活跃数据连续</summary>
    private void RemoveAt(int index)
    {
        _count--;
        if (index != _count) _texts[index] = _texts[_count];
    }

    /// <summary>数值按 4bit/位打包（低位是最右边一位），最多 6 位</summary>
    private static void PackDigits(int value, out int packed, out int count)
    {
        packed = 0;
        count = 0;
        if (value <= 0)
        {
            count = 1;
            return;
        }

        while (value > 0 && count < 6)
        {
            packed |= (value % 10) << (count * 4);
            count++;
            value /= 10;
        }
    }

    // ==================== 落点槽位 ====================

    private int AcquireSlot(int targetId)
    {
        if (!_slotUsage.TryGetValue(targetId, out bool[] used))
        {
            used = new bool[DropSlotCount];
            _slotUsage[targetId] = used;
        }

        float bestDist = float.MaxValue;
        int best = 0;
        for (int i = 0; i < DropSlotCount; i++)
        {
            // 找离中心最近的空闲槽：方向可见、又优先向中间靠
            float dist = Mathf.Abs(i - (DropSlotCount - 1) * 0.5f);
            if (used[i] || dist >= bestDist) continue;
            bestDist = dist;
            best = i;
        }
        used[best] = true;
        return best;
    }

    /// <summary>目标不再有活跃飘字时释放其槽位（每帧最多清一次全表）</summary>
    private void ReleaseSlotsIfIdle()
    {
        if (_slotUsage.Count == 0) return;

        foreach (var pair in _slotUsage)
        {
            bool alive = false;
            for (int i = 0; i < _count; i++)
            {
                if (_texts[i].targetId != pair.Key) continue;
                alive = true;
                break;
            }
            if (!alive) _headStale.Add(pair.Key);
        }

        for (int i = 0; i < _headStale.Count; i++) _slotUsage.Remove(_headStale[i]);
        _headStale.Clear();
    }

    // ==================== 位置 ====================

    /// <summary>
    /// 受击者上半身：优先渲染器包围盒上半段，取不到退固定偏移。
    /// 渲染器与头顶偏移按目标缓存（首次命中时查一次子树），目标销毁后条目被清理。
    /// </summary>
    private Vector3 ResolveHeadPoint(GameObject target)
    {
        int id = target.GetInstanceID();
        int frame = Time.frameCount;
        if (_headCache.TryGetValue(id, out HeadCache cached) && cached.target != null)
        {
            cached.lastUsedFrame = frame;
            _headCache[id] = cached;

            if (cached.renderer != null)
            {
                Vector3 center = cached.renderer.bounds.center;
                return new Vector3(center.x, center.y, center.z) + cached.offset;
            }
            return target.transform.position + cached.offset;
        }

        Renderer renderer = target.GetComponentInChildren<Renderer>();
        Vector3 head;
        if (renderer != null)
        {
            // 用上半身而不是包围盒顶：BOSS 体型大时顶部会投影到屏幕之外
            Bounds bounds = renderer.bounds;
            head = new Vector3(bounds.center.x, bounds.center.y + bounds.extents.y * 0.35f, bounds.center.z);
        }
        else
        {
            head = target.transform.position + Vector3.up * headOffsetY;
        }

        StoreHeadCache(id, target, renderer, head - target.transform.position);
        return head;
    }

    private void StoreHeadCache(int id, GameObject target, Renderer renderer, Vector3 offset)
    {
        if (_headCache.Count >= HeadCacheLimit && !_headCache.ContainsKey(id))
        {
            // 条目上限：淘汰最久没被使用的
            int evictKey = 0;
            int oldestFrame = int.MaxValue;
            foreach (var pair in _headCache)
            {
                if (pair.Value.lastUsedFrame >= oldestFrame) continue;
                oldestFrame = pair.Value.lastUsedFrame;
                evictKey = pair.Key;
            }
            if (evictKey != 0) _headCache.Remove(evictKey);
        }

        _headCache[id] = new HeadCache
        {
            target = target,
            renderer = renderer,
            offset = offset,
            lastUsedFrame = Time.frameCount
        };
    }

    /// <summary>
    /// 渲染世界的相机：优先 Camera.main（战斗里两套座位相机都打 MainCamera 标签，
    /// 绑座后停用另一套），取不到再退「任意启用的 Game 相机并排除 UI 相机」。
    /// </summary>
    private Camera ResolveWorldCamera()
    {
        if (_cachedCamera != null && _cachedCamera.isActiveAndEnabled) return _cachedCamera;

        Camera uiCamera = UIManager.Instance != null ? UIManager.Instance.UICamera : null;

        Camera main = Camera.main;
        if (main != null && main != uiCamera && main.isActiveAndEnabled)
        {
            _cachedCamera = main;
            return _cachedCamera;
        }

        foreach (var cam in Camera.allCameras)
        {
            if (cam == null || cam == uiCamera || !cam.isActiveAndEnabled) continue;
            if (cam.cameraType != CameraType.Game) continue;
            _cachedCamera = cam;
            return _cachedCamera;
        }
        _cachedCamera = null;
        return null;
    }
}

//  下面的注释  agent 你必须保留，作为 TMP 批处理优化的知识点，方便后续维护和优化

/* ▪ UGUI/TMP 的合批（Batching）有 4 个核心条件，全部满足才能合成一个 draw call：

   1. 相同材质（Material）
   最核心的一条。shader 相同、材质参数相同、必须是同一个材质实例。
   - 陷阱：给某个 UI 元素单独改 text.material（实例化材质）就会断批

   2. 相同纹理（Texture）
   材质里的贴图要一样。TMP 场景中"同字体 = 同一张 SDF 图集"就是这条——不同字体的文字必然不同图集，不能合批。

   3. 渲染顺序连续（相邻）
   合批只对绘制顺序相邻的元素有效。中间如果插了一个不同材质的元素（比如一条飘字中间夹了个 Image），批次就在这里断开：
    [飘字A][飘字B][飘字C][Image][飘字D]  →  A/B/C 一批，D 单独一批
   4. 不互相重叠（不遮挡）
   同一批内元素不能覆盖彼此。如果 B 的 rect 压住了 A，批处理算法会把 A 拆出去（或者把 B 拆开）——因为同批次渲染无法保证正确的遮挡关系。
   - 这就是我们飘字加了随机偏移的意义之一：数字叠在一起不止难看，还会拆批*/

   /*对，完全正确。 这正是 TMP 比 legacy Text 干净的地方：

   - text.color（含 alpha）是顶点颜色——写进顶点数据（每条飘字 4 个顶点各带一份颜色），渲染时 GPU 直接按顶点色插值。改颜色不碰材质、不实例化材质、不触发断批
   - 我们 Show() 里每次 text.color = style.color、DOFade 淡出都是改顶点色 → 不会影响合批
   - 所以不同颜色的飘字（红字、橙字）混在一起照样是同一批*/


   /*对于极端高并发飘字场景：

动态Mesh合并：最实用，1个DrawCall搞定所有文字

GPU Instancing：性能更好，适合大量重复几何体

对象池+循环：简单有效，适合固定数量

ECS/DOTS：终极方案，但复杂度高

分层渲染：根据重要性使用不同方案*/
