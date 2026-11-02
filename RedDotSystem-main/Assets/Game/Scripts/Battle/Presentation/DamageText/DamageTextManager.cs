using TMPro;
using UnityEngine;
using SkierFramework;

/// <summary>
/// 飘字样式：普通 / 暴击各配一套
/// </summary>
[System.Serializable]
public class DamageTextStyle
{
    public Color color = Color.white;
    public int fontSize = 56;
    public float shake = 0f;          // 入场抖动幅度（度），0 表示不抖
    public float scaleMul = 1f;       // 整体缩放乘数
    public float outlineWidth = 0.12f;
}

/// <summary>
/// 伤害飘字管理器（单例，DontDestroyOnLoad）
///
/// 【职责】
///   订阅 E_OnDamage，把伤害写进一个定长结构体数组；每帧推进动画并把结果交给
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
///   同一目标在 mergeWindow 内连续命中会累加到同一条并重新弹入，连击读起来是
///   「一个不断跳大的数字」，同时把同屏条数压下来。
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
        public int targetId;        // GetInstanceID，0 表示不参与合并
    }

    [Header("字体（不填则用 TMP 默认字体）")]
    [SerializeField] private TMP_FontAsset fontAsset;

    [Header("样式")]
    [SerializeField] private DamageTextStyle normalStyle =
        new DamageTextStyle { color = new Color(1f, 0.94f, 0.76f), fontSize = 56, scaleMul = 1f, outlineWidth = 0.12f };
    [SerializeField] private DamageTextStyle criticalStyle =
        new DamageTextStyle { color = new Color(1f, 0.61f, 0.10f), fontSize = 76, shake = 3f, scaleMul = 1.25f, outlineWidth = 0.26f };

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
    [SerializeField, Min(0f)] private float randomOffsetRange = 20f;     // 水平偏移（像素）
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

    [Header("描边")]
    [SerializeField] private Color outlineColor = new Color(0f, 0f, 0f, 0.8f);

    private FloatingText[] _texts;
    private DamageTextDrawData[] _drawBuffer;
    private int _count;
    private int _spawnBudget;

    private DamageTextRenderer _renderer;
    private Camera _cachedCamera;

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
    }

    private void OnDisable()
    {
        EventCenter.MainInstance.UnregisterTarget(this);
        _count = 0;
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

        if (_count <= 0) return;

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
        if (data.target == null) return;

        Camera camera = ResolveWorldCamera();
        if (camera == null) return;

        Vector3 screen = camera.WorldToScreenPoint(ResolveHeadPoint(data.target));
        if (screen.z < 0f) return;   // 目标在相机背后

        screen.x += Random.Range(-randomOffsetRange, randomOffsetRange);
        screen.y += Random.Range(-randomVerticalRange, randomVerticalRange);

        // 体型大的 BOSS 上半身也可能投影到视野外，夹进屏幕内保证一定看得见
        const float margin = 48f;
        screen.x = Mathf.Clamp(screen.x, margin, Screen.width - margin);
        screen.y = Mathf.Clamp(screen.y, margin, Screen.height - margin);

        DamageTextStyle style = data.isCritical ? criticalStyle : normalStyle;
        int amount = Mathf.Max(1, Mathf.RoundToInt(data.amount));
        Spawn(amount, screen, style, data.target.GetInstanceID());
    }

    /// <summary>
    /// 对外接口：手动飘一条数字（screenPos 为屏幕像素坐标）。
    /// 回血、格挡、经验等后续飘字复用这个入口。
    /// </summary>
    public void Show(int amount, Vector2 screenPos, DamageTextStyle style)
    {
        Spawn(amount, screenPos, style, 0);
    }

    private void Spawn(int amount, Vector2 origin, DamageTextStyle style, int targetId)
    {
        if (style == null || _texts == null) return;

        float window = _count >= highLoadCount ? mergeWindowHighLoad : mergeWindow;

        int mergeIndex = FindMergeIndex(targetId, window);
        if (mergeIndex >= 0)
        {
            MergeInto(mergeIndex, amount);
            return;
        }

        // 本帧新建额度用尽：能合并就合并，否则丢弃（避免瞬时爆发卡帧）
        if (_spawnBudget <= 0)
        {
            mergeIndex = FindMergeIndex(targetId, float.MaxValue);
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

        int packed, digits;
        PackDigits(amount, out packed, out digits);

        _texts[index] = new FloatingText
        {
            origin = origin,
            age = 0f,
            rotation = targetId != 0 ? Random.Range(-tiltRange, tiltRange) : 0f,
            fontSize = style.fontSize,
            shake = style.shake,
            scaleMul = style.scaleMul,
            color = style.color,
            value = amount,
            packed = packed,
            digitCount = digits,
            targetId = targetId
        };
    }

    private int FindMergeIndex(int targetId, float window)
    {
        if (targetId == 0) return -1;
        for (int i = 0; i < _count; i++)
        {
            if (_texts[i].targetId != targetId) continue;
            if (_texts[i].age > window) continue;
            return i;
        }
        return -1;
    }

    private void MergeInto(int index, int amount)
    {
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

    // ==================== 位置 ====================

    /// <summary>受击者上半身：优先渲染器包围盒上半段，取不到退固定偏移</summary>
    private Vector3 ResolveHeadPoint(GameObject target)
    {
        Renderer renderer = target.GetComponentInChildren<Renderer>();
        if (renderer != null)
        {
            // 用上半身而不是包围盒顶：BOSS 体型大时顶部会投影到屏幕之外
            Bounds bounds = renderer.bounds;
            return new Vector3(bounds.center.x, bounds.center.y + bounds.extents.y * 0.35f, bounds.center.z);
        }
        return target.transform.position + Vector3.up * headOffsetY;
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

动态Mesh合并：最实用，1个Draw Call搞定所有文字

GPU Instancing：性能更好，适合大量重复几何体

对象池+循环：简单有效，适合固定数量

ECS/DOTS：终极方案，但复杂度高

分层渲染：根据重要性使用不同方案*/
