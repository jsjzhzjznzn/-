using System.Collections;
using Common.Network;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 新手引导系统（服务端存档 + 逐步讲解）
///
/// 架构（三层）：
/// - 视觉：GuideMask.shader 全屏遮罩挖洞（SDF 圆角矩形 + 金色呼吸描边），信息步全屏均匀暗罩；
/// - 输入：GuideMaskGraphic.Raycast 洞内不命中（点击穿透给下层真按钮），
///         讲解步 blockAll=true 时洞内也拦截（只高亮，不让玩家误开面板）；
///         洞矩形与 shader 用同一份数据，视觉与命中像素级一致；
/// - 步骤：GuideSteps 数据表驱动，本类做状态机（目标解析带超时兜底，引导永不硬卡死）。
///
/// 进度：服务端 player_guide_progress（3601 拉 / 3603 推）存"下一步该播哪一步"，
/// 每进入一步就覆盖式上报；本机 PlayerPrefs 只做断网兜底。退出后回来从同一步继续。
/// 触发：zhuye 场景 + 已登录 + 未完成；无记录时只有 ID > GuideSteps.GuideCutoffPlayerId 的新号自动开场。
/// 重看：设置面板调 ReplayFromStart()；编辑器 F9 或 ForceStart() 可无视条件重跑（演示/E2E 用）。
///
/// 画布：每轮引导现场构建、结束即销毁（sort=6000 在阿福悬浮球 5900 之上；
/// ConstantPixelSize 照 FriendCanvas 惯例 1单位=1px；ScreenSpaceCamera 经 UICameraService.ConvertCanvas 接 UI 相机）。
/// </summary>
public class GuideManager : MonoBehaviour
{
    // ──────────────────────────────
    // 单例自举（照 UiFx / VfxManager 先例：运行时自举 + DontDestroyOnLoad）
    // ──────────────────────────────

    private static GuideManager instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (instance != null) return;
        GameObject holder = new GameObject("GuideManager");
        holder.AddComponent<GuideManager>();
        DontDestroyOnLoad(holder);
    }

    /// <summary>调试/测试入口：强制重跑引导（无视 ID 分界线与已完成标记）</summary>
    public static void ForceStart()
    {
        if (instance == null) return;
        if (instance.running) return;
        if (SceneManager.GetActiveScene().name != GuideSteps.ZhuyeSceneName)
        {
            Debug.LogWarning("[引导] ForceStart 仅在 zhuye 场景有效");
            return;
        }
        instance.SaveProgress(0, false);
        instance.StartGuide(0);
    }

    /// <summary>设置面板入口：从第一步重看（进度重置为 0 并覆盖服务端）</summary>
    public static void ReplayFromStart()
    {
        if (instance == null) return;
        if (instance.running) return;
        if (SceneManager.GetActiveScene().name != GuideSteps.ZhuyeSceneName)
        {
            Debug.LogWarning("[引导] 重看引导仅在 zhuye 场景有效");
            return;
        }
        instance.progressLoaded = true;
        instance.SaveProgress(0, false);
        instance.StartGuide(0);
    }

    // ──────────────────────────────
    // 配置常量
    // ──────────────────────────────

    private const string LocalCachePrefix = "guide_cache_";
    private const float AutoStartDelay = 0.3f;      // 进度就绪后延迟开场（等入口布局落地）
    private const float ProgressTimeout = 3f;        // 拉取进度超时（会话内请求排队 + MySQL/Mongo 抖动，超时走本地缓存）
    private const float TargetResolveTimeout = 3f;   // 目标解析兜底超时
    private const int CanvasSortOrder = 6000;        // 阿福悬浮球(5900) 之上
    private const float HoleFullDim = -1000f;        // 半尺寸传大负数 = SDF 恒正 = 无洞全遮罩

    private static readonly int HoleRectId = Shader.PropertyToID("_HoleRect");
    private static readonly int HoleRadiusId = Shader.PropertyToID("_HoleRadius");

    // ──────────────────────────────
    // 运行时状态
    // ──────────────────────────────

    private Canvas canvas;
    private GuideMaskGraphic mask;
    private RectTransform maskRect;
    private Material maskMaterial;
    private RectTransform bubbleRoot;
    private TextMeshProUGUI bubbleTitle;
    private TextMeshProUGUI bubbleBody;
    private Button continueButton;
    private TextMeshProUGUI continueLabel;
    private RectTransform arrowRect;
    private bool arrowVisible;
    private Vector2 arrowBasePos;

    private bool running;
    private int currentStepIndex = -1;
    private Coroutine stepRoutine;
    private RectTransform currentTarget;

    /// <summary>本次进入 zhuye 是否已经拉取过进度（避免每帧重拉）</summary>
    private bool progressLoaded;

    /// <summary>拉取/开场评估是否已在进行</summary>
    private bool evaluating;

    /// <summary>进度就绪后的开场时间点（&lt;0 = 未排期）</summary>
    private float autoStartAt = -1f;

    private int savedStep;
    private bool savedCompleted;

    /// <summary>进度是否已经由「协程等待」或「迟到响应」定案（定案后不再被覆盖）</summary>
    private bool pullSettled;

    /// <summary>已订阅进度回执的网络管理器（重登后实例会变）</summary>
    private ClientNetworkManager subscribedNet;

    /// <summary>按钮步推进监听登记（跳过时从真按钮上摘除，防悬挂监听）</summary>
    private Button boundButton;
    private UnityAction boundAction;

    private readonly Vector3[] worldCorners = new Vector3[4];

    // E2E/调试访问口
    public static GuideManager Instance => instance;
    public bool IsRunning => running;
    public int CurrentStepIndex => currentStepIndex;
    public GuideMaskGraphic Mask => mask;
    public RectTransform CurrentTarget => currentTarget;
    public int SavedStep => savedStep;
    public bool SavedCompleted => savedCompleted;
    public GuideStep CurrentStep
    {
        get
        {
            if (!running || currentStepIndex < 0 || currentStepIndex >= GuideSteps.Zhuye.Length)
                return null;
            return GuideSteps.Zhuye[currentStepIndex];
        }
    }

    // ──────────────────────────────
    // Unity 生命周期
    // ──────────────────────────────

    private void Awake()
    {
        instance = this;
    }

    private void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    private void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
        EnsurePullSubscribed();
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (subscribedNet != null)
        {
            subscribedNet.GuidePullResponseReceived -= OnGuidePullResponse;
            subscribedNet = null;
        }
    }

    /// <summary>订阅进度拉取回执（断线重登后 ClientNetworkManager 实例可能变化，取用时重新绑定）</summary>
    private void EnsurePullSubscribed()
    {
        var net = ClientNetworkManager.Instance;
        if (net == null || net == subscribedNet) return;
        if (subscribedNet != null) subscribedNet.GuidePullResponseReceived -= OnGuidePullResponse;
        net.GuidePullResponseReceived += OnGuidePullResponse;
        subscribedNet = net;
    }

    /// <summary>
    /// 迟到响应兜底：拉取排队过久、协程已用本地缓存决策后，服务端答案才到。
    /// 此时若还没开场，直接用服务端的步骤续播（已开场则忽略，避免打断）。
    /// </summary>
    private void OnGuidePullResponse(GuidePullResponse response)
    {
        if (response == null || !response.Success) return;
        if (running) return;
        if (pullSettled) return;

        pullSettled = true;
        savedStep = response.HasRecord ? response.Step : 0;
        savedCompleted = response.HasRecord && response.Completed;
        progressLoaded = true;

        if (savedCompleted) return;
        if (savedStep > 0) StartGuide(savedStep);
    }

    private void Update()
    {
        if (running)
        {
            // 箭头轻微浮动（unscaled：不受暂停语义影响）
            if (arrowVisible && arrowRect != null)
            {
                arrowRect.anchoredPosition = arrowBasePos
                    + Vector2.up * (Mathf.Sin(Time.unscaledTime * 4f) * 3f);
            }
            return;
        }

#if UNITY_EDITOR
        // 编辑器调试：F9 强制重跑引导（老账号也能看效果）
        if (Input.GetKeyDown(KeyCode.F9)
            && SceneManager.GetActiveScene().name == GuideSteps.ZhuyeSceneName)
        {
            ForceStart();
            return;
        }
#endif
        EvaluateAutoStart();
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        progressLoaded = false;
        evaluating = false;
        pullSettled = false;
        autoStartAt = -1f;
        EnsurePullSubscribed();

        if (running && scene.name != GuideSteps.ZhuyeSceneName)
        {
            // 引导中途被切场景（编辑器手动操作等）→ 视为完成（记 done），防止反复打扰
            FinishGuide(true);
        }
    }

    // ──────────────────────────────
    // 自动触发（轮询：同时覆盖"场景加载完成"与"登录完成"两个时序）
    // ──────────────────────────────

    private void EvaluateAutoStart()
    {
        if (SceneManager.GetActiveScene().name != GuideSteps.ZhuyeSceneName)
        {
            autoStartAt = -1f;
            return;
        }

        var net = ClientNetworkManager.Instance;
        if (net == null || net.MyPlayerId <= 0) return;

        if (!progressLoaded)
        {
            if (!evaluating) StartCoroutine(LoadProgressAndEvaluate());
            return;
        }

        if (autoStartAt < 0f) autoStartAt = Time.unscaledTime + AutoStartDelay;
        if (Time.unscaledTime < autoStartAt) return;

        autoStartAt = -1f;
        if (savedCompleted) return;
        if (savedStep > 0) StartGuide(savedStep);
        else if (PlayerIdSafe() > GuideSteps.GuideCutoffPlayerId) StartGuide(0);
    }

    /// <summary>拉取服务端进度（超时/失败走本地缓存），就绪后决定续播还是开场</summary>
    private IEnumerator LoadProgressAndEvaluate()
    {
        evaluating = true;
        EnsurePullSubscribed();

        bool hasRecord = false;
        int step = 0;
        bool completed = false;
        bool fromServer = false;

        var net = ClientNetworkManager.Instance;
        if (net != null && net.MyPlayerId > 0)
        {
            GuidePullResponse answer = null;
            bool answered = false;
            void OnAnswer(GuidePullResponse response)
            {
                answer = response;
                answered = true;
            }

            net.GuidePullResponseReceived += OnAnswer;
            try
            {
                _ = net.SendGuidePullRequest(GuideSteps.GuideKey);
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning($"[引导] 进度拉取发送失败：{exception.Message}");
            }

            float deadline = Time.unscaledTime + ProgressTimeout;
            while (!answered && Time.unscaledTime < deadline) yield return null;
            net.GuidePullResponseReceived -= OnAnswer;

            if (answered && answer != null && answer.Success)
            {
                fromServer = true;
                hasRecord = answer.HasRecord;
                step = answer.Step;
                completed = answer.Completed;
            }
        }

        bool needPushBack = false;
        if (!fromServer)
        {
            // 服务端不可用 → 本机缓存兜底（断网也能续播）
            if (TryReadCache(out int cachedStep, out bool cachedCompleted))
            {
                hasRecord = true;
                step = cachedStep;
                completed = cachedCompleted;
            }
        }
        else if (!hasRecord && TryReadCache(out int localStep, out bool localCompleted) && localStep > 0)
        {
            // 服务端没记录但本地有进度（之前在断网状态走过）→ 用本地续播并回写一次
            hasRecord = true;
            step = localStep;
            completed = localCompleted;
            needPushBack = true;
        }

        savedStep = hasRecord ? step : 0;
        savedCompleted = hasRecord && completed;
        progressLoaded = true;
        // 只有拿到服务端答案才算定案；走缓存时留个口子，迟到的服务端答案还能纠正并续播
        pullSettled = fromServer;
        evaluating = false;

        if (needPushBack) SaveProgress(savedStep, savedCompleted);

        Debug.Log($"[引导] 进度就绪：来源={(fromServer ? "服务端" : "本机缓存")} 有记录={hasRecord} step={savedStep} completed={savedCompleted}");
    }

    // ──────────────────────────────
    // 进度读写
    // ──────────────────────────────

    private int PlayerIdSafe()
    {
        var net = ClientNetworkManager.Instance;
        return net != null ? net.MyPlayerId : 0;
    }

    private string CacheKey => LocalCachePrefix + PlayerIdSafe() + "_" + GuideSteps.GuideKey;

    /// <summary>保存进度：服务端权威 + 本机缓存兜底（步骤下标 = 下一次要播的那一步）</summary>
    private void SaveProgress(int step, bool completed)
    {
        savedStep = step;
        savedCompleted = completed;

        PlayerPrefs.SetString(CacheKey, step + "|" + (completed ? "1" : "0"));
        PlayerPrefs.Save();

        var net = ClientNetworkManager.Instance;
        if (net == null || net.MyPlayerId <= 0) return;
        try
        {
            _ = net.SendGuidePushRequest(GuideSteps.GuideKey, step, completed);
        }
        catch (System.Exception exception)
        {
            Debug.LogWarning($"[引导] 进度上报失败（已写本机缓存）：{exception.Message}");
        }
    }

    private bool TryReadCache(out int step, out bool completed)
    {
        step = 0;
        completed = false;
        string raw = PlayerPrefs.GetString(CacheKey, string.Empty);
        if (string.IsNullOrEmpty(raw)) return false;

        string[] parts = raw.Split('|');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out step)) return false;
        completed = parts[1] == "1";
        return true;
    }

    // ──────────────────────────────
    // 引导开始 / 结束
    // ──────────────────────────────

    private void StartGuide(int fromStep)
    {
        if (running) return;
        if (SceneManager.GetActiveScene().name != GuideSteps.ZhuyeSceneName)
        {
            Debug.LogWarning("[引导] 仅在 zhuye 场景运行");
            return;
        }

        running = true;
        currentStepIndex = fromStep - 1;
        autoStartAt = -1f;
        ActivateEntryCanvases();
        BuildCanvas();
        stepRoutine = StartCoroutine(RunGuide(fromStep));
        Debug.Log($"[引导] 开始（玩家ID {PlayerIdSafe()}，从第 {fromStep} 步）");
    }

    /// <summary>结束引导；跳过与完成同权（都记 done，不再打扰）</summary>
    private void FinishGuide(bool skipped)
    {
        if (!running) return;
        running = false;
        autoStartAt = -1f;

        SaveProgress(GuideSteps.Zhuye.Length, true);

        // 从真按钮上摘除推进监听（按钮是场景资产的一部分，不能留悬挂监听）
        if (boundButton != null)
        {
            if (boundAction != null) boundButton.onClick.RemoveListener(boundAction);
            boundButton = null;
            boundAction = null;
        }

        if (stepRoutine != null)
        {
            StopCoroutine(stepRoutine);
            stepRoutine = null;
        }

        if (maskMaterial != null)
        {
            Destroy(maskMaterial);
            maskMaterial = null;
        }
        if (canvas != null)
        {
            Destroy(canvas.gameObject);
            canvas = null;
        }
        mask = null;
        maskRect = null;
        currentTarget = null;
        arrowVisible = false;
        Debug.Log($"[引导] 结束（{(skipped ? "已跳过" : "已完成")}）");
    }

    /// <summary>引导开始时激活默认 inactive 的入口画布（兼容旧版主页布局；新版 HomeView 常驻，调用无副作用）</summary>
    private void ActivateEntryCanvases()
    {
        foreach (string canvasName in EntryCanvasNames)
        {
            GameObject root = FindRoot(canvasName);
            if (root != null && !root.activeSelf) root.SetActive(true);
        }
    }

    private static readonly string[] EntryCanvasNames = { "beibao", "renwu", "youjian" };

    // ──────────────────────────────
    // 步骤状态机
    // ──────────────────────────────

    private IEnumerator RunGuide(int fromStep)
    {
        int total = GuideSteps.Zhuye.Length;
        for (int i = fromStep; i < total; i++)
        {
            currentStepIndex = i;
            // 进入这一步即记录"下次从这一步继续"——中途退出不会丢进度
            SaveProgress(i, false);

            GuideStep step = GuideSteps.Zhuye[i];
            bool advanced = false;
            if (step.kind == GuideStep.Kind.Info)
            {
                yield return RunInfoStep(step, () => advanced = true);
            }
            else
            {
                yield return RunExplainStep(step, () => advanced = true);
            }
            if (!running) yield break;          // 被终止（跳过/切场景）
            if (!advanced) yield break;         // 异常兜底
            yield return null;                   // 一帧缓冲：让面板开关等本帧事件先落地
        }
        FinishGuide(false);
    }

    /// <summary>信息步：全屏暗罩 + 居中气泡 + 继续按钮</summary>
    private IEnumerator RunInfoStep(GuideStep step, System.Action advance)
    {
        mask.fullDim = true;
        mask.blockAll = true;
        maskMaterial.SetVector(HoleRectId, new Vector4(0f, 0f, HoleFullDim, HoleFullDim));
        arrowVisible = false;
        arrowRect.gameObject.SetActive(false);
        ShowBubble(step.title, step.text, step.continueLabel);
        bubbleRoot.anchoredPosition = Vector2.zero;

        bool clicked = false;
        void OnContinue()
        {
            clicked = true;
            continueButton.onClick.RemoveListener(OnContinue);
            advance();
        }
        continueButton.onClick.AddListener(OnContinue);

        while (running && !clicked) yield return null;
    }

    /// <summary>讲解步：挖洞高亮主页入口（洞内也拦截点击）+ 气泡 + 下一步按钮</summary>
    private IEnumerator RunExplainStep(GuideStep step, System.Action advance)
    {
        // 1) 解析目标（等 HomeView 打开，带超时兜底）
        RectTransform target = null;
        float deadline = Time.unscaledTime + TargetResolveTimeout;
        while (running && Time.unscaledTime < deadline)
        {
            target = ResolveHomeEntry(step.homeField);
            if (target != null && target.gameObject.activeInHierarchy) break;
            target = null;
            yield return new WaitForSecondsRealtime(0.1f);
        }
        if (!running) yield break;
        if (target == null)
        {
            Debug.LogWarning($"[引导] 步骤{currentStepIndex} 目标不可用（HomeView.{step.homeField}），跳过");
            advance();
            yield break;
        }

        // 2) 挖洞 + 气泡（洞内不放行点击）
        currentTarget = target;
        mask.fullDim = false;
        mask.blockAll = true;
        arrowVisible = true;
        arrowRect.gameObject.SetActive(true);

        int ordinal = ExplainOrdinal(currentStepIndex);
        string title = step.title + "  <size=15><color=#8FA0B3>" + ordinal + " / " + GuideSteps.ExplainCount + "</color></size>";
        ShowBubble(title, step.text, step.continueLabel);

        bool clicked = false;
        void OnContinue()
        {
            clicked = true;
            continueButton.onClick.RemoveListener(OnContinue);
            advance();
        }
        continueButton.onClick.AddListener(OnContinue);

        // 3) 每帧洞跟随（布局变动/改分辨率即时生效）
        while (running && !clicked)
        {
            if (target == null)   // 目标被外部销毁（HomeView 关闭等）
            {
                continueButton.onClick.RemoveListener(OnContinue);
                Debug.LogWarning($"[引导] 步骤{currentStepIndex} 目标中途消失，跳过");
                advance();
                yield break;
            }
            UpdateHole(target, 8f);
            UpdateBubblePlacement(target);
            yield return null;
        }
        continueButton.onClick.RemoveListener(OnContinue);
    }

    /// <summary>第 index 步在全部讲解步里的序号（1 起）</summary>
    private static int ExplainOrdinal(int stepIndex)
    {
        int ordinal = 0;
        for (int i = 0; i <= stepIndex && i < GuideSteps.Zhuye.Length; i++)
            if (GuideSteps.Zhuye[i].kind == GuideStep.Kind.Explain) ordinal++;
        return ordinal;
    }

    /// <summary>按 HomeView 的 [ControlBinding] 字段名解析入口按钮（不依赖 transform 路径）</summary>
    private static RectTransform ResolveHomeEntry(string fieldName)
    {
        if (string.IsNullOrEmpty(fieldName)) return null;

        HomeView home = FindObjectOfType<HomeView>();
        if (home == null) return null;

        var field = typeof(HomeView).GetField(fieldName,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (field == null) return null;

        return field.GetValue(home) is Component component ? component.transform as RectTransform : null;
    }

    /// <summary>根画布按名查找（GetRootGameObjects 可找到 inactive 根——GameObject.Find 找不到）</summary>
    private static GameObject FindRoot(string rootName)
    {
        Scene scene = SceneManager.GetActiveScene();
        foreach (GameObject go in scene.GetRootGameObjects())
        {
            if (go.name == rootName) return go;
        }
        return null;
    }

    // ──────────────────────────────
    // 洞与气泡定位
    // ──────────────────────────────

    /// <summary>目标世界四角 → 遮罩本地坐标（写 Graphic 与 shader 同一份洞矩形）</summary>
    private void UpdateHole(RectTransform target, float padding)
    {
        target.GetWorldCorners(worldCorners);
        Vector3 a = maskRect.InverseTransformPoint(worldCorners[0]);
        Vector3 c = maskRect.InverseTransformPoint(worldCorners[2]);
        float x0 = Mathf.Min(a.x, c.x) - padding;
        float x1 = Mathf.Max(a.x, c.x) + padding;
        float y0 = Mathf.Min(a.y, c.y) - padding;
        float y1 = Mathf.Max(a.y, c.y) + padding;
        Rect rect = Rect.MinMaxRect(x0, y0, x1, y1);

        mask.holeLocalRect = rect;
        Vector2 center = rect.center;
        Vector2 half = rect.size * 0.5f;
        // 圆角自动适配小目标（接近胶囊形）
        float radius = Mathf.Min(12f, Mathf.Min(half.x, half.y) * 0.5f);
        maskMaterial.SetVector(HoleRectId, new Vector4(center.x, center.y, half.x, half.y));
        maskMaterial.SetFloat(HoleRadiusId, radius);
    }

    /// <summary>气泡贴洞放置（洞在上半屏 → 气泡在洞下方；否则镜像），箭头从气泡指向洞</summary>
    private void UpdateBubblePlacement(RectTransform target)
    {
        Rect hole = mask.holeLocalRect;
        float canvasHalfW = maskRect.rect.width * 0.5f;
        float canvasHalfH = maskRect.rect.height * 0.5f;
        float bubbleHalfW = bubbleRoot.rect.width * 0.5f;
        float bubbleHalfH = bubbleRoot.rect.height * 0.5f;

        bool holeInUpperHalf = hole.center.y >= 0f;
        float dir = holeInUpperHalf ? -1f : 1f;
        float y = hole.center.y + dir * (hole.height * 0.5f + 36f + bubbleHalfH);
        float x = Mathf.Clamp(hole.center.x, -canvasHalfW + bubbleHalfW + 8f, canvasHalfW - bubbleHalfW - 8f);
        y = Mathf.Clamp(y, -canvasHalfH + bubbleHalfH + 8f, canvasHalfH - bubbleHalfH - 8f);
        bubbleRoot.anchoredPosition = new Vector2(x, y);

        // 箭头贴气泡洞侧边缘、指向洞（x 对齐洞中心，收在气泡宽度内）
        float arrowY = bubbleHalfH + 14f;
        if (dir < 0f)
        {
            arrowBasePos = bubbleRoot.anchoredPosition + Vector2.up * arrowY;
            arrowRect.localEulerAngles = Vector3.zero;             // 气泡在洞下 → 箭头朝上
        }
        else
        {
            arrowBasePos = bubbleRoot.anchoredPosition + Vector2.down * arrowY;
            arrowRect.localEulerAngles = new Vector3(0f, 0f, 180f); // 气泡在洞上 → 箭头朝下
        }
        arrowBasePos.x = Mathf.Clamp(hole.center.x, x - bubbleHalfW + 20f, x + bubbleHalfW - 20f);
    }

    // ──────────────────────────────
    // 引导 UI 构建（程序化，零美术资产依赖——项目惯例）
    // ──────────────────────────────

    private void BuildCanvas()
    {
        if (canvas != null) return;

        GameObject canvasGo = new GameObject("GuideCanvas", typeof(Canvas));
        canvas = canvasGo.GetComponent<Canvas>();
        canvas.sortingOrder = CanvasSortOrder;   // 阿福悬浮球(5900) 之上，全 UI 最顶层
        CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;   // 照 FriendCanvas 惯例：1单位=1px
        canvasGo.AddComponent<GraphicRaycaster>();
        // camera space + UI 相机 + 整树设 UI 层（照 UiFx 先例；Rig 每秒 sweep 兜底重绑）
        UICameraService.ConvertCanvas(canvas, UICameraService.UICamera);

        // ── 全屏遮罩 ──
        GameObject maskGo = new GameObject("Mask", typeof(RectTransform), typeof(GuideMaskGraphic));
        maskGo.transform.SetParent(canvas.transform, false);
        SetStretch(maskGo.GetComponent<RectTransform>());
        mask = maskGo.GetComponent<GuideMaskGraphic>();
        mask.color = Color.white;
        mask.raycastTarget = true;
        maskRect = mask.rectTransform;
        maskMaterial = new Material(Shader.Find("Game/GuideMask"));
        mask.material = maskMaterial;

        // ── 箭头（洞上方指路） ──
        GameObject arrowGo = new GameObject("Arrow", typeof(RectTransform), typeof(Image));
        arrowGo.transform.SetParent(canvas.transform, false);
        arrowRect = arrowGo.GetComponent<RectTransform>();
        arrowRect.sizeDelta = new Vector2(24f, 20f);
        Image arrowImage = arrowGo.GetComponent<Image>();
        arrowImage.sprite = GetTriangleSprite();
        arrowImage.raycastTarget = false;
        arrowImage.color = new Color(0.91f, 0.767f, 0.416f, 1f);   // 同洞边呼吸光金色
        arrowGo.SetActive(false);

        // ── 提示气泡（VerticalLayoutGroup + ContentSizeFitter 自适应高度） ──
        GameObject bubbleGo = new GameObject("Bubble", typeof(RectTransform));
        bubbleGo.transform.SetParent(canvas.transform, false);
        bubbleRoot = bubbleGo.GetComponent<RectTransform>();
        bubbleRoot.anchorMin = bubbleRoot.anchorMax = new Vector2(0.5f, 0.5f);
        bubbleRoot.pivot = new Vector2(0.5f, 0.5f);
        bubbleRoot.sizeDelta = new Vector2(440f, 100f);   // 高度由 fitter 接管

        Image bubbleImage = bubbleGo.AddComponent<Image>();
        bubbleImage.sprite = GetRoundedSprite();
        bubbleImage.type = Image.Type.Sliced;
        bubbleImage.color = new Color32(0x26, 0x2B, 0x33, 0xF5);   // 深色 HUD 底（挑战/排行榜面板同款）
        bubbleImage.raycastTarget = false;

        VerticalLayoutGroup layout = bubbleGo.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(18, 18, 16, 16);
        layout.spacing = 6f;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        // 文本必须 ForceExpand 拉满气泡内宽：childControl 单靠 preferred 会让多行文本塌缩成最长单词宽
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        layout.childAlignment = TextAnchor.UpperLeft;
        ContentSizeFitter fitter = bubbleGo.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        bubbleTitle = CreateText("Title", bubbleRoot, 21f, new Color(0.88f, 0.88f, 0.88f, 1f),
            TextAlignmentOptions.MidlineLeft, true);
        bubbleBody = CreateText("Text", bubbleRoot, 17f, new Color(0.76f, 0.78f, 0.81f, 1f),
            TextAlignmentOptions.MidlineLeft, false);

        // 继续按钮（信息步 / 讲解步都用；ForceExpand 下整行宽）
        GameObject btnGo = new GameObject("ContinueButton", typeof(RectTransform));
        btnGo.transform.SetParent(bubbleRoot, false);
        Image btnImage = btnGo.AddComponent<Image>();
        btnImage.sprite = GetRoundedSprite();
        btnImage.type = Image.Type.Sliced;
        btnImage.color = new Color32(0x07, 0xC1, 0x60, 0xFF);   // 入口按钮同款微信绿
        btnImage.raycastTarget = true;
        continueButton = btnGo.AddComponent<Button>();
        continueButton.targetGraphic = btnImage;
        LayoutElement btnLayout = btnGo.AddComponent<LayoutElement>();
        btnLayout.preferredHeight = 42f;
        continueLabel = CreateText("Label", btnGo.transform, 19f, Color.white,
            TextAlignmentOptions.Center, true);
        SetStretch(continueLabel.rectTransform);

        // ── 跳过链接（全程可见，画布最顶层） ──
        // 放右下角：右上角是入口按钮行，透明命中区会盖住洞内的入口按钮（实测踩过）
        GameObject skipGo = new GameObject("SkipButton", typeof(RectTransform));
        skipGo.transform.SetParent(canvas.transform, false);
        RectTransform skipRect = skipGo.GetComponent<RectTransform>();
        skipRect.anchorMin = skipRect.anchorMax = new Vector2(1f, 0f);
        skipRect.pivot = new Vector2(1f, 0f);                 // 角锚配同角 pivot（项目坑：防半截出屏）
        skipRect.anchoredPosition = new Vector2(-16f, 16f);
        skipRect.sizeDelta = new Vector2(110f, 30f);
        Image skipHit = skipGo.AddComponent<Image>();          // 透明命中区（alpha=0 仍可 raycast）
        skipHit.color = new Color(1f, 1f, 1f, 0f);
        skipHit.raycastTarget = true;
        Button skipButton = skipGo.AddComponent<Button>();
        skipButton.targetGraphic = skipHit;
        TextMeshProUGUI skipLabel = CreateText("Label", skipGo.transform, 19f,
            new Color(1f, 1f, 1f, 0.75f), TextAlignmentOptions.MidlineRight, false);
        skipLabel.text = "跳过引导";
        SetStretch(skipLabel.rectTransform);
        skipButton.onClick.AddListener(() => FinishGuide(true));
    }

    /// <summary>填气泡文案并立即重建布局（后续定位需要即时拿到实际高度）</summary>
    private void ShowBubble(string title, string text, string continueText)
    {
        bubbleTitle.gameObject.SetActive(!string.IsNullOrEmpty(title));
        bubbleTitle.text = title ?? string.Empty;
        bubbleBody.text = text ?? string.Empty;
        bool showButton = !string.IsNullOrEmpty(continueText);
        continueButton.gameObject.SetActive(showButton);
        if (showButton) continueLabel.text = continueText;
        LayoutRebuilder.ForceRebuildLayoutImmediate(bubbleRoot);
    }

    private static TextMeshProUGUI CreateText(string goName, Transform parent, float fontSize,
        Color color, TextAlignmentOptions alignment, bool bold)
    {
        var text = new GameObject(goName, typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
        text.transform.SetParent(parent, false);
        text.fontSize = fontSize;
        text.color = color;
        text.alignment = alignment;
        text.enableWordWrapping = true;
        text.raycastTarget = false;   // 项目铁律：非交互文本必须不挡射线
        if (bold) text.fontStyle = FontStyles.Bold;

        // TMP 默认字体（LiberationSans）没有中文字形，必须借一个能显示中文的字体
        TMP_FontAsset font = ResolveChineseFont();
        if (font != null) text.font = font;
        return text;
    }

    private static TMP_FontAsset cachedChineseFont;

    /// <summary>
    /// 取一个能显示中文的 TMP 字体：优先复用场景里已有 UI 的字体（主页/面板都用 NotoSansSC），
    /// 找不到再退回 TMP 默认字体（会显示方块，但至少不崩）。
    /// 不写死资产路径：字体在 Assets/Codely/Fonts 下，不在 Resources，构建期无法按路径加载。
    /// </summary>
    private static TMP_FontAsset ResolveChineseFont()
    {
        if (cachedChineseFont != null) return cachedChineseFont;

        TextMeshProUGUI[] texts = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>();
        foreach (TextMeshProUGUI candidate in texts)
        {
            if (candidate == null || candidate.font == null) continue;
            if (!candidate.gameObject.scene.IsValid()) continue;   // 只看当前场景里的实例
            if (!candidate.font.HasCharacter('中')) continue;
            cachedChineseFont = candidate.font;
            return cachedChineseFont;
        }
        return null;
    }

    /// <summary>拉伸铺满父级（stretch 锚 + 零偏移 + 中心 pivot）</summary>
    private static void SetStretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        rt.pivot = new Vector2(0.5f, 0.5f);
    }

    // ──────────────────────────────
    // 程序化贴图（静态缓存——BagUIController 同款惯例，判空用 == 防 Unity 假 null）
    // ──────────────────────────────

    private static Sprite roundedSprite;
    private static Sprite triangleSprite;

    /// <summary>9-slice 圆角底（64px、圆角16、边框16：拉伸后直线边保真、四角圆滑）</summary>
    private static Sprite GetRoundedSprite()
    {
        if (roundedSprite != null) return roundedSprite;
        const int size = 64;
        const int radius = 16;
        const int border = 16;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave,
        };
        var pixels = new Color[size * size];
        float half = size * 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = SdRoundBox(new Vector2(x + 0.5f - half, y + 0.5f - half),
                    new Vector2(half, half), radius);
                pixels[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - d));
            }
        }
        tex.SetPixels(pixels);
        tex.Apply(false);
        roundedSprite = Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f),
            100f, 0, SpriteMeshType.FullRect, new Vector4(border, border, border, border));
        return roundedSprite;
    }

    /// <summary>朝上三角箭头（底宽顶尖，非切片）</summary>
    private static Sprite GetTriangleSprite()
    {
        if (triangleSprite != null) return triangleSprite;
        const int w = 32;
        const int h = 28;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave,
        };
        var pixels = new Color[w * h];
        for (int y = 0; y < h; y++)
        {
            float halfWidth = Mathf.Lerp(15.5f, 1.2f, (y + 0.5f) / h);   // 底宽 → 顶尖
            for (int x = 0; x < w; x++)
            {
                float dx = Mathf.Abs(x + 0.5f - w * 0.5f);
                pixels[y * w + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(halfWidth - dx + 0.5f));
            }
        }
        tex.SetPixels(pixels);
        tex.Apply(false);
        triangleSprite = Sprite.Create(tex, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f), 100f);
        return triangleSprite;
    }

    /// <summary>SDF 圆角矩形（与 GuideMask.shader 同式：内负外正）</summary>
    private static float SdRoundBox(Vector2 p, Vector2 halfSize, float radius)
    {
        Vector2 q = new Vector2(Mathf.Abs(p.x) - halfSize.x + radius, Mathf.Abs(p.y) - halfSize.y + radius);
        float length = Mathf.Max(q.x, 0f) * Mathf.Max(q.x, 0f) + Mathf.Max(q.y, 0f) * Mathf.Max(q.y, 0f);
        return Mathf.Sqrt(length) + Mathf.Min(Mathf.Max(q.x, q.y), 0f) - radius;
    }
}
