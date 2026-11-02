using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 通用虚拟滚动列表
/// 只实例化"视口内可见 + 上下缓冲"的少量条目，滚动时从对象池取条目换绑数据——
/// 条目物体数与数据量解耦，一万条数据也只存活十几个物体
/// 行高任意（好友条目等高 / 聊天气泡变高都支持），由回调提供
///
/// 用法：挂在 ScrollView 根物体上（与 ScrollRect 同物体），Viewport/Content 按默认命名自动查找；
/// 宿主注入回调后调 RefreshData 渲染，之后滚动自动换绑
/// 约定：条目统一锚定 Content 顶边（anchorMin=anchorMax=(0.5,1)、pivot=(0.5,0.5)），
///       y（垂直位置）由本组件写入，x（左右）由 BindItem 写入
/// </summary>
public class VirtualScrollList : MonoBehaviour
{
    [Header("结构引用（留空自动查找 Viewport/Content）")]
    [SerializeField] private RectTransform viewportRect;

    [SerializeField] private RectTransform contentRect;

    [Header("布局参数（可被 Configure 覆盖）")]
    [SerializeField] private float topPadding = 8f;

    [SerializeField] private float bottomPadding = 8f;

    [SerializeField] private float spacing = 6f;

    /// <summary>视口外上下各多保留的行数（预渲染，滚动更平滑）</summary>
    [SerializeField] private int extraBuffer = 1;

    // ──────────────────────────────
    // 宿主注入的回调
    // ──────────────────────────────

    /// <summary>数据总条数</summary>
    public Func<int> GetItemCount;

    /// <summary>第 index 行的高度（变高列表每次 RefreshData 会被逐行询问，宿主应做缓存）</summary>
    public Func<int, float> GetItemHeight;

    /// <summary>第 index 行属于哪个对象池（如聊天按我方/对方分池）；null=共用单池</summary>
    public Func<int, object> GetPoolKey;

    /// <summary>池空时创建条目（应把条目 Instantiate 到 Content 下，返回其 RectTransform）</summary>
    public Func<int, RectTransform> CreateItem;

    /// <summary>换绑数据（填内容、设尺寸与 x 坐标；y 由本组件随后写入）</summary>
    public Action<int, RectTransform> BindItem;

    /// <summary>回收前清理（可选，如清掉条目→索引的反查表）</summary>
    public Action<RectTransform> RecycleItem;

    /// <summary>单池模式的共用池键</summary>
    private static readonly object SharedPoolKey = new();

    private ScrollRect scrollRect;

    /// <summary>行偏移前缀和：offsets[i]=第 i 行顶边距内容顶的距离，offsets[Count]=累计总高</summary>
    private readonly List<float> offsets = new();

    /// <summary>当前渲染中的 index→(条目, 所属池键)；回收时用存下的键，不回头查数据（数据可能已换）</summary>
    private readonly Dictionary<int, (RectTransform Item, object Key)> active = new();

    /// <summary>对象池：池键→条目堆栈（SetActive(false) 状态存放）</summary>
    private readonly Dictionary<object, Stack<RectTransform>> pools = new();

    /// <summary>回收缓冲（避免遍历字典时修改）</summary>
    private readonly List<int> recycleBuffer = new();

    /// <summary>内容总高度（撑 Content 的 sizeDelta.y）</summary>
    private float contentHeight;

    // ──────────────────────────────
    // 对外查询（测试/调试用）
    // ──────────────────────────────

    /// <summary>当前渲染中的条目数（≈视口可见+缓冲，与数据量无关）</summary>
    public int ActiveCount => active.Count;

    /// <summary>内容总高度</summary>
    public float ContentHeight => contentHeight;

    // ──────────────────────────────
    // Unity 生命周期
    // ──────────────────────────────

    private void Awake()
    {
        scrollRect = GetComponent<ScrollRect>();
        if (viewportRect == null) viewportRect = transform.Find("Viewport") as RectTransform;
        if (contentRect == null && viewportRect != null) contentRect = viewportRect.Find("Content") as RectTransform;
        if (scrollRect != null) scrollRect.onValueChanged.AddListener(HandleScroll);
    }

    private void OnEnable()
    {
        // 重新显示（如聊天区切页回来）时刷新：隐藏期间数据可能已换
        UpdateView();
    }

    private void OnDestroy()
    {
        if (scrollRect != null) scrollRect.onValueChanged.RemoveListener(HandleScroll);
    }

    private void HandleScroll(Vector2 _) => UpdateView();

    // ──────────────────────────────
    // 公开接口
    // ──────────────────────────────

    /// <summary>配置布局参数（等价 Inspector 序列化值，代码接入时调用）</summary>
    /// <param name="top">参数名：top，类型：float，用途：内容顶部留白</param>
    /// <param name="bottom">参数名：bottom，类型：float，用途：内容底部留白</param>
    /// <param name="itemSpacing">参数名：itemSpacing，类型：float，用途：相邻行间距</param>
    public void Configure(float top, float bottom, float itemSpacing)
    {
        topPadding = top;
        bottomPadding = bottom;
        spacing = itemSpacing;
    }

    /// <summary>
    /// 数据变化的方法
    /// 重算全部行偏移与内容总高，回收所有渲染中的条目，再按当前滚动位置重建可视区
    /// （全量回收最简单可靠：可见的十几个条目重绑代价可忽略）
    /// </summary>
    public void RefreshData()
    {
        if (contentRect == null) return;

        offsets.Clear();
        offsets.Add(topPadding);
        int count = GetItemCount?.Invoke() ?? 0;
        for (int i = 0; i < count; i++)
        {
            float height = GetItemHeight != null ? Mathf.Max(1f, GetItemHeight(i)) : 20f;
            offsets.Add(offsets[i] + height + spacing);
        }
        contentHeight = count > 0 ? offsets[count] - spacing + bottomPadding : 0f;
        contentRect.sizeDelta = new Vector2(0f, contentHeight);

        // 关键：内容尺寸刚变化，ScrollRect 内部缓存的 bounds 还是旧值——
        // 不强制收敛布局的话，紧随其后的 verticalNormalizedPosition 赋值全部空操作（踩过）
        Canvas.ForceUpdateCanvases();

        RecycleAll();
        UpdateView();
    }

    /// <summary>视口尺寸变化（如输入框增高把消息区压矮）：可见行数变了，重建可视区</summary>
    public void RefreshView() => UpdateView();

    /// <summary>滚动到顶部</summary>
    public void ScrollToTop()
    {
        if (scrollRect != null)
        {
            Canvas.ForceUpdateCanvases();   // 先让 bounds 跟上内容尺寸，否则赋值无效
            scrollRect.verticalNormalizedPosition = 1f;
        }
        UpdateView();
    }

    /// <summary>滚动到底部（聊天新消息）</summary>
    public void ScrollToBottom()
    {
        if (scrollRect != null)
        {
            Canvas.ForceUpdateCanvases();   // 先让 bounds 跟上内容尺寸，否则赋值无效
            scrollRect.verticalNormalizedPosition = 0f;
        }
        UpdateView();
    }

    // ──────────────────────────────
    // 可视区重建
    // ──────────────────────────────

    /// <summary>视口世界角点缓冲（避免每次滚动分配）</summary>
    private static readonly Vector3[] corners = new Vector3[4];

    /// <summary>
    /// 按当前滚动位置重建可视区的方法
    /// 视口上下沿用世界角点换算成"距内容顶的深度"（正数向下）——不依赖 Content 的锚点/轴心约定，
    /// 顶锚内容向下滚时 anchoredPosition 为正值，直接读它很容易把符号搞反（踩过）
    /// 视口外的条目回池，视口内缺失的条目出池换绑；隐藏状态下跳过（OnEnable 会补）
    /// </summary>
    private void UpdateView()
    {
        if (viewportRect == null || contentRect == null || GetItemCount == null) return;
        if (!isActiveAndEnabled) return;

        int count = GetItemCount();
        if (count == 0)
        {
            RecycleAll();
            contentRect.sizeDelta = Vector2.zero;
            return;
        }

        // 数据已就位但还没跑过 RefreshData（宿主先填数据、面板后激活时 OnEnable 先到）：
        // 偏移表为空直接换绑会越界——自愈改为整表重建
        if (offsets.Count != count + 1)
        {
            RefreshData();
            return;
        }

        viewportRect.GetWorldCorners(corners);
        float scrollTop = Mathf.Max(0f, -contentRect.InverseTransformPoint(corners[1]).y);
        float scrollBottom = Mathf.Min(-contentRect.InverseTransformPoint(corners[3]).y, contentHeight);

        int first = Mathf.Max(0, FindFirstVisible(scrollTop) - extraBuffer);
        int last = Mathf.Min(count - 1, FindLastVisible(scrollBottom) + extraBuffer);

        // 回收区间外的
        recycleBuffer.Clear();
        foreach (KeyValuePair<int, (RectTransform Item, object Key)> pair in active)
        {
            if (pair.Key < first || pair.Key > last) recycleBuffer.Add(pair.Key);
        }
        foreach (int index in recycleBuffer)
        {
            Recycle(index);
        }

        // 补齐区间内缺失的
        for (int i = first; i <= last; i++)
        {
            if (!active.ContainsKey(i)) TakeOrCreate(i);
        }
    }

    /// <summary>二分查找：第一个底边超过视口顶的行（可见首行）</summary>
    private int FindFirstVisible(float scrollTop)
    {
        int count = offsets.Count - 1;
        int lo = 0, hi = count - 1, result = count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (offsets[mid + 1] - spacing > scrollTop)
            {
                result = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }
        return result;
    }

    /// <summary>二分查找：最后一个顶边低于视口底的行（可见末行）</summary>
    private int FindLastVisible(float scrollBottom)
    {
        int count = offsets.Count - 1;
        int lo = 0, hi = count - 1, result = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (offsets[mid] < scrollBottom)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return result;
    }

    /// <summary>从池里取（或新建）条目并换绑的方法</summary>
    private void TakeOrCreate(int index)
    {
        object key = GetPoolKey != null ? GetPoolKey(index) : SharedPoolKey;
        if (!pools.TryGetValue(key, out Stack<RectTransform> pool))
        {
            pool = new Stack<RectTransform>();
            pools[key] = pool;
        }

        RectTransform item = pool.Count > 0 ? pool.Pop() : CreateItem?.Invoke(index);
        if (item == null) return;

        // 条目锚定约定：Content 顶边中心锚 + 中心轴心（每次设置，幂等）
        item.anchorMin = new Vector2(0.5f, 1f);
        item.anchorMax = new Vector2(0.5f, 1f);
        item.pivot = new Vector2(0.5f, 0.5f);

        active[index] = (item, key);
        BindItem?.Invoke(index, item);

        float height = offsets[index + 1] - offsets[index] - spacing;
        item.anchoredPosition = new Vector2(item.anchoredPosition.x, -(offsets[index] + height * 0.5f));
        item.gameObject.SetActive(true);
    }

    /// <summary>回收条目回池的方法（用创建时记录的池键，不依赖当前数据）</summary>
    private void Recycle(int index)
    {
        if (!active.TryGetValue(index, out (RectTransform Item, object Key) entry)) return;
        active.Remove(index);

        RecycleItem?.Invoke(entry.Item);
        entry.Item.gameObject.SetActive(false);

        if (!pools.TryGetValue(entry.Key, out Stack<RectTransform> pool))
        {
            pool = new Stack<RectTransform>();
            pools[entry.Key] = pool;
        }
        pool.Push(entry.Item);
    }

    /// <summary>回收全部渲染中的条目</summary>
    private void RecycleAll()
    {
        recycleBuffer.Clear();
        recycleBuffer.AddRange(active.Keys);
        foreach (int index in recycleBuffer)
        {
            Recycle(index);
        }
    }
}
