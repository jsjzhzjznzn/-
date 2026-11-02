using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// UI 反馈特效系统（程序化 Image 粒子，零外部资产依赖）
///
/// 设计取舍：
/// - 为什么不用 ParticleSystem 做 UI 粒子：世界粒子由主相机渲染、永远垫在 UI 底下
///   （本项目 UI 是 ScreenSpaceCamera + URP 相机栈，UI 相机最后叠加）；用 Image 当粒子
///   天然活在 canvas 里，排序、缩放、裁剪全部跟宿主 canvas 一致，永远可见；
/// - 全局点击迸发：每帧轮询鼠标按下 + EventSystem 射线找被点的 Button——一处代码给项目里
///   所有按钮（含既有面板）统一加反馈，不改任何既有控制器；
/// - 时间语义：全程 unscaledDeltaTime（对齐项目暂停约定：暂停中 UI 反馈照常运转）；
/// - 挂到别的 canvas 的粒子必须 raycastTarget=false——宿主 canvas 有 GraphicRaycaster，
///   短命粒子挡住按钮点击 0.6 秒也是事故。
/// </summary>
public class UiFx : MonoBehaviour
{
    // ──────────────────────────────
    // 单例实现（照 VfxManager / BagManager 先例：运行时自举）
    // ──────────────────────────────

    private static UiFx instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (instance != null) return;
        GameObject holder = new GameObject("UiFx");
        holder.AddComponent<UiFx>();
        DontDestroyOnLoad(holder);
    }

    // ──────────────────────────────
    // 配置常量
    // ──────────────────────────────

    /// <summary>迸发粒子默认主色（暖金）</summary>
    private static readonly Color DefaultColorA = new(1f, 0.86f, 0.42f, 1f);

    /// <summary>迸发粒子默认副色（白）</summary>
    private static readonly Color DefaultColorB = new(1f, 1f, 1f, 1f);

    // ──────────────────────────────
    // 运行时状态
    // ──────────────────────────────

    private Canvas canvas;                    // 自有顶层 canvas（sortingOrder 最高，纯展示、无 raycaster）
    private readonly Stack<Particle> pool = new();
    private readonly List<Particle> alive = new();

    /// <summary>一个迸发粒子（Image + 运动状态）</summary>
    private sealed class Particle
    {
        public Image Image;
        public Vector2 Velocity;
        public float AngularVelocity;
        public float Life;
        public float MaxLife;
        public float StartSize;
    }

    // ──────────────────────────────
    // Unity 生命周期
    // ──────────────────────────────

    private void Awake()
    {
        instance = this;

        GameObject canvasGo = new("UiFxCanvas", typeof(Canvas));
        canvasGo.transform.SetParent(transform, false);
        canvas = canvasGo.GetComponent<Canvas>();
        canvas.sortingOrder = 500;   // 全 HUD 之上（面板 sort10 / 副本HUD sort50 / 暂停 sort100）
        // 故意不挂 GraphicRaycaster：纯展示层，绝不参与点击
        // UI 迁移：camera space + UI 相机（本画布无静态子物体，动态粒子在 RentParticle 里逐个设层）
        UICameraService.ConvertCanvas(canvas, UICameraService.UICamera);
    }

    private void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    private void Update()
    {
        float dt = Time.unscaledDeltaTime;

        // 粒子推进（unscaled：暂停中照常）
        for (int i = alive.Count - 1; i >= 0; i--)
        {
            Particle p = alive[i];
            if (p.Image == null)   // 宿主 canvas 被销毁（场景切换）→ 粒子随之没了
            {
                alive.RemoveAt(i);
                continue;
            }
            p.Life += dt;
            if (p.Life >= p.MaxLife)
            {
                Despawn(p, i);
                continue;
            }
            // 运动：减速 + 轻重力 + 自转；尾段缩放淡出
            p.Velocity += Vector2.down * (300f * dt);
            p.Image.rectTransform.anchoredPosition += p.Velocity * dt;
            p.Image.rectTransform.Rotate(0f, 0f, p.AngularVelocity * dt);
            float t = p.Life / p.MaxLife;
            float scale = Mathf.Lerp(1f, 0.15f, t * t);
            p.Image.rectTransform.sizeDelta = Vector2.one * (p.StartSize * scale);
            Color c = p.Image.color;
            c.a = t < 0.15f ? t / 0.15f : 1f - Mathf.Pow((t - 0.15f) / 0.85f, 1.6f);
            p.Image.color = c;
        }

        // 全局点击迸发：鼠标左键点在可交互按钮上时，在点击处撒星光 + 按压回弹
        // 锁光标时是战斗输入（副本攻击），不产生 UI 反馈
        if (Input.GetMouseButtonDown(0) && Cursor.lockState != CursorLockMode.Locked)
        {
            HandlePointerDown(Input.mousePosition);
        }
    }

    // ──────────────────────────────
    // 全局点击检测
    // ──────────────────────────────

    /// <summary>
    /// 处理一次指针按下的方法（Update 轮询与 E2E 直调共用同一入口）
    /// 射线取最上层命中，向上找最近的 Button——是可交互按钮才迸发，其他（面板空白/纯展示文本）不反馈
    /// </summary>
    /// <param name="screenPos">参数名：screenPos，类型：Vector2，用途：屏幕坐标（像素）</param>
    public void HandlePointerDown(Vector2 screenPos)
    {
        EventSystem eventSystem = EventSystem.current;
        if (eventSystem == null) return;

        PointerEventData pointerData = new PointerEventData(eventSystem) { position = screenPos };
        List<RaycastResult> results = new();
        eventSystem.RaycastAll(pointerData, results);
        if (results.Count == 0) return;

        // 最上层命中的物体向上找 Button（最多爬 8 层，够覆盖"按钮上的文字/角标"结构）
        Transform current = results[0].gameObject.transform;
        Button button = null;
        for (int depth = 0; depth < 8 && current != null; depth++)
        {
            button = current.GetComponent<Button>();
            if (button != null) break;
            current = current.parent;
        }
        if (button == null || !button.interactable) return;

        BurstAtScreenPoint(screenPos, 9);
        StartCoroutine(PopRoutine(button.transform));
    }

    // ──────────────────────────────
    // 迸发 API（拾取横幅等定制接线用）
    // ──────────────────────────────

    /// <summary>
    /// 在屏幕坐标处迸发星光的方法（粒子落在自有顶层 canvas 上，永远可见）
    /// </summary>
    /// <param name="screenPos">参数名：screenPos，类型：Vector2，用途：屏幕坐标（像素）</param>
    /// <param name="count">参数名：count，类型：int，用途：粒子数量</param>
    public static void BurstAtScreenPoint(Vector2 screenPos, int count)
    {
        if (instance == null) return;
        Vector2 localPoint;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            (RectTransform)instance.canvas.transform, screenPos, CameraFor(instance.canvas), out localPoint);
        instance.SpawnBurst(instance.canvas.transform, localPoint, count, DefaultColorA, DefaultColorB);
    }

    /// <summary>
    /// 在某个 RectTransform 的中心处迸发星光的方法
    /// 粒子直接落到目标所在的 canvas 上——随宿主裁剪/缩放/排序，宿主销毁粒子自然消亡
    /// </summary>
    /// <param name="target">参数名：target，类型：RectTransform，用途：目标元素（在其中心迸发）</param>
    /// <param name="count">参数名：count，类型：int，用途：粒子数量</param>
    public static void BurstAtRect(RectTransform target, int count)
    {
        if (instance == null || target == null) return;
        Canvas host = target.GetComponentInParent<Canvas>();
        if (host == null) return;

        Camera uiCamera = CameraFor(host);
        Vector2 screenPoint = RectTransformUtility.WorldToScreenPoint(uiCamera, target.position);
        Vector2 localPoint;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            (RectTransform)host.transform, screenPoint, uiCamera, out localPoint);
        instance.SpawnBurst(host.transform, localPoint, count, DefaultColorA, DefaultColorB);
    }

    /// <summary>画布对应的 UI 相机（overlay 传 null 是 UGUI 约定；camera space 必须传 worldCamera，否则坐标换算全错）</summary>
    private static Camera CameraFor(Canvas canvas)
    {
        return canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
    }

    /// <summary>当前存活粒子数（E2E 断言用）</summary>
    public static int AliveCount => instance != null ? instance.alive.Count : 0;

    /// <summary>全局点击迸发测试入口（E2E 用：直接以指定屏幕坐标走一遍点击检测）</summary>
    public static void SimulatePointerDown(Vector2 screenPos)
    {
        if (instance != null) instance.HandlePointerDown(screenPos);
    }

    // ──────────────────────────────
    // 内部
    // ──────────────────────────────

    private void SpawnBurst(Transform parent, Vector2 anchoredPos, int count, Color colorA, Color colorB)
    {
        for (int i = 0; i < count; i++)
        {
            Particle p = RentParticle(parent);
            RectTransform rect = p.Image.rectTransform;
            rect.anchoredPosition = anchoredPos;
            float angle = Random.value * Mathf.PI * 2f;
            float speed = Random.Range(140f, 360f);
            p.Velocity = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * speed;
            // 前三分之一粒子给一个向上抬升的初速，迸发形态更像"扬起的星光"
            if (i % 3 == 0) p.Velocity += Vector2.up * Random.Range(60f, 160f);
            p.AngularVelocity = Random.Range(-240f, 240f);
            p.MaxLife = Random.Range(0.35f, 0.65f);
            p.Life = 0f;
            p.StartSize = Random.Range(9f, 18f);
            rect.sizeDelta = Vector2.one * p.StartSize;
            rect.localRotation = Quaternion.Euler(0f, 0f, Random.value * 360f);
            Color c = Color.Lerp(colorA, colorB, Random.value);
            c.a = 0f;
            p.Image.color = c;
            alive.Add(p);
        }
    }

    private Particle RentParticle(Transform parent)
    {
        // 池里可能残留已销毁引用（宿主 canvas 随场景销毁时粒子一起没了）——Unity 的 == 判空天然过滤
        while (pool.Count > 0)
        {
            Particle reused = pool.Pop();
            if (reused.Image != null)
            {
                reused.Image.transform.SetParent(parent, false);
                reused.Image.gameObject.layer = UICameraService.UiLayer;   // 粒子可能落在任何宿主 canvas 上
                reused.Image.gameObject.SetActive(true);
                return reused;
            }
        }

        GameObject go = new("UiFxParticle", typeof(RectTransform), typeof(Image));
        go.layer = UICameraService.UiLayer;   // UI 相机的 cullingMask 只看 UI 层
        RectTransform rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);   // 画布中心锚 + anchoredPosition 定位
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.SetParent(parent, false);
        Image image = go.GetComponent<Image>();
        image.sprite = SparkleSprite;
        image.raycastTarget = false;   // 粒子绝不挡点击（宿主 canvas 可能有 GraphicRaycaster）
        return new Particle { Image = image };
    }

    private void Despawn(Particle p, int aliveIndex)
    {
        alive.RemoveAt(aliveIndex);
        p.Image.gameObject.SetActive(false);
        p.Image.transform.SetParent(canvas.transform, false);   // 回池先挂回自有 canvas，宿主销毁不影响池
        pool.Push(p);
    }

    /// <summary>按钮按压回弹（缩放 0.9 → 1，unscaled 时间，目标中途销毁安全退出）</summary>
    private IEnumerator PopRoutine(Transform target)
    {
        Vector3 original = target.localScale;
        float duration = 0.14f;
        float elapsed = 0f;
        while (elapsed < duration && target != null)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            float k = Mathf.Sin(Mathf.PI * t);   // 0→1→0 的鼓包曲线：按下去弹回来
            target.localScale = original * Mathf.Lerp(1f, 0.88f, k);
            yield return null;
        }
        if (target != null) target.localScale = original;
    }

    // ──────────────────────────────
    // 程序化星光贴图（四角星：两个三角形函数相乘，中心亮四尖锐）
    // ──────────────────────────────

    private static Sprite sparkle;

    private static Sprite SparkleSprite => sparkle == null ? sparkle = CreateSparkle(48) : sparkle;

    private static Sprite CreateSparkle(int size)
    {
        Texture2D texture = new(size, size, TextureFormat.RGBA32, false);
        texture.wrapMode = TextureWrapMode.Clamp;
        float half = size * 0.5f;
        float radius = half - 1f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = Mathf.Abs(x - (half - 0.5f)) / radius;
                float dy = Mathf.Abs(y - (half - 0.5f)) / radius;
                float falloff = (1f - dx) * (1f - dy);   // 中心=1，越靠十字尖越亮的星形
                float alpha = Mathf.Clamp01(falloff * 1.35f - 0.35f);
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }
        texture.Apply();
        return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
    }
}
