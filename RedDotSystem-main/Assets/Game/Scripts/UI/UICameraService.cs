using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// UI 相机服务（全项目 UI 从 ScreenSpaceOverlay 迁到 ScreenSpaceCamera 的基础设施）
///
/// 单一 UI 相机策略（2026-10-21 收敛，用户定稿"运行时只需要一个 UI 相机"）：
/// 唯一在渲染的 UI 相机是框架注入的 SkierUICamera（SkierFramework.UIManager 自建、DDOL、常驻）；
/// 本服务只做三件事：①把画布绑到这台相机（含运行时程序化新建的画布）；②把它注册进各 Base 相机的
/// Camera Stack（URP Overlay 必须注册才会渲染）；③每帧同步正交尺寸。
///
/// 兜底相机（UICamera）：仅在"框架尚未初始化"的场景（LOGIN/111/gas 这类直进 Play 的场景）临时创建，
/// 一旦框架相机注入就**销毁**（旧实现只禁用，会在层级里留下第二台相机——2026-10-21 用户反馈的问题）。
///
/// 为什么正交 + orthographicSize=Screen.height/2：1 canvas unit 恒等于 1 屏幕像素——
/// CanvasScaler（ConstantPixelSize / ScaleWithScreenSize）在 overlay 下的全部布局语义零变化迁移。
///
/// 渲染语义：UI 相机是 URP Overlay，在主相机的渲染 pass 内叠加（Tuanjie 的 URP 14.2-t1 分叉不支持两台
/// Base 相机顺序叠加，用 Overlay 绕开），Clear=Depth 只清深度不清颜色，UI 不进主相机后处理。
///
/// 回退：Enabled=false 停止一切自动转换（场景资产里已序列化的画布仍保持 camera space）。
/// </summary>
public static class UICameraService
{
    /// <summary>兜底 UI 相机对象名（仅框架未初始化时临时存在；场景资产里已不再放同名相机）</summary>
    public const string UICameraObjectName = "UICamera";

    private static Camera uiCamera;
    private static Camera externalCamera;
    private static Camera fallbackCamera;
    private static int uiLayer = -1;

    /// <summary>总开关：false 时不再做任何自动转换/掩码清理（运行时兜底可关）</summary>
    public static bool Enabled = true;

    /// <summary>
    /// 由 SkierFramework.UIManager 注入的 UI 相机（框架自建、常驻，运行时唯一 UI 相机）。
    /// 注入时清掉全部自建兜底相机，此后本服务不再创建任何相机，只做画布重绑与相机栈注册。
    /// </summary>
    public static void SetExternalCamera(Camera camera)
    {
        if (camera == null)
        {
            Debug.LogWarning("[UICameraService] SetExternalCamera 收到 null，已忽略");
            return;
        }

        externalCamera = camera;
        uiCamera = camera;

        // 框架相机到位 → 兜底相机全部退场（是"销毁"不是"禁用"：层级里不留第二台 UI 相机）
        int removed = DestroyFallbackCameras(camera);
        fallbackCamera = null;
        Debug.Log($"[UICameraService] 框架 UI 相机注入 {camera.name}（清理兜底相机 {removed} 台）");

        ConfigureCamera(camera);
        SweepAll();
    }

    /// <summary>当前 UI 相机（框架注入的优先；没有则临时兜底相机）</summary>
    public static Camera UICamera => externalCamera != null
        ? externalCamera
        : (fallbackCamera != null ? fallbackCamera : CreateFallbackCamera());

    /// <summary>UI 层号（懒取）</summary>
    public static int UiLayer => uiLayer >= 0 ? uiLayer : uiLayer = LayerMask.NameToLayer("UI");

    // ──────────────────────────────
    // 自举与场景钩子
    // ──────────────────────────────

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        // 注意：域重载（脚本改动/编辑器重编译）会让本类静态引用变脏，而 DDOL 上的兜底相机对象还活着。
        // 所以这里只清"引用"，真正的唯一性靠 EnsureUICamera → CreateFallbackCamera 的按名字幂等复用。
        fallbackCamera = null;
        uiCamera = null;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        EnsureUICamera();
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!Enabled) return;
        EnsureUICamera();
        SweepAll();
    }

    // ──────────────────────────────
    // UI 相机获取/创建
    // ──────────────────────────────

    /// <summary>确保 UI 相机存在：框架注入的优先，否则临时兜底相机（直进 Play 的场景也能跑）。</summary>
    public static Camera EnsureUICamera()
    {
        if (externalCamera != null)
        {
            if (!externalCamera.gameObject.activeSelf) externalCamera.gameObject.SetActive(true);
            ConfigureCamera(externalCamera);
            return externalCamera;
        }

        if (fallbackCamera != null)
        {
            if (!fallbackCamera.enabled) fallbackCamera.enabled = true;
            if (!fallbackCamera.gameObject.activeSelf) fallbackCamera.gameObject.SetActive(true);
            return fallbackCamera;
        }

        return CreateFallbackCamera();
    }

    /// <summary>
    /// 临时兜底 UI 相机（DontDestroyOnLoad）：只在框架 UIManager 尚未初始化的场景里存在，
    /// 框架相机一注入就被销毁（见 SetExternalCamera）。
    /// 按名字幂等：域重载/重复自举导致静态引用丢失时复用已存在的兜底相机，绝不建出第二台。
    /// </summary>
    private static Camera CreateFallbackCamera()
    {
        Camera[] existing = FindFallbackCameras(null);
        if (existing.Length > 0)
        {
            Camera reuse = existing[0];
            for (int i = 1; i < existing.Length; i++)
            {
                DestroyCameraAndPrune(existing[i]);
            }
            fallbackCamera = reuse;
            uiCamera = reuse;
            ConfigureCamera(reuse);
            if (existing.Length > 1) Debug.Log($"[UICameraService] 复用兜底 UI 相机并清理重复项 {existing.Length - 1} 台");
            return reuse;
        }

        GameObject holder = new(UICameraObjectName);
        Camera camera = holder.AddComponent<Camera>();
        UnityEngine.Object.DontDestroyOnLoad(holder);
        fallbackCamera = camera;
        uiCamera = camera;
        ConfigureCamera(camera);
        Debug.Log("[UICameraService] 创建兜底 UI 相机（本场景框架 UI 尚未初始化）");
        return camera;
    }

    /// <summary>找出所有自建兜底相机（按对象名 + 本服务打的 UICameraRig 标记；except 用于排除框架相机）。</summary>
    private static Camera[] FindFallbackCameras(Camera except)
    {
        var found = new System.Collections.Generic.List<Camera>();
        foreach (Camera cam in UnityEngine.Object.FindObjectsOfType<Camera>(true))
        {
            if (cam == null || cam == except) continue;
            if (cam.gameObject.name != UICameraObjectName) continue;
            if (cam.GetComponent<UICameraRig>() == null) continue;   // 不是本服务建的就不动
            found.Add(cam);
        }
        return found.ToArray();
    }

    /// <summary>销毁除 except 之外的全部自建兜底相机，返回销毁数量。</summary>
    private static int DestroyFallbackCameras(Camera except)
    {
        Camera[] cameras = FindFallbackCameras(except);
        foreach (Camera cam in cameras) DestroyCameraAndPrune(cam);
        return cameras.Length;
    }

    /// <summary>销毁相机并从所有 Base 相机的 Camera Stack 里摘掉它（防 URP 警告"overlay no longer exists"）。</summary>
    private static void DestroyCameraAndPrune(Camera camera)
    {
        if (camera == null) return;

        foreach (Camera other in UnityEngine.Object.FindObjectsOfType<Camera>(true))
        {
            if (other == null || other == camera) continue;
            UniversalAdditionalCameraData data = other.GetUniversalAdditionalCameraData();
            if (data == null || data.cameraStack == null) continue;
            while (data.cameraStack.Remove(camera)) { }
        }

        Debug.Log($"[UICameraService] 销毁 UI 相机对象 {camera.name}");
        UnityEngine.Object.Destroy(camera.gameObject);
    }

    /// <summary>相机参数收敛（运行时创建与框架注入的相机都过一遍，防漏项；编辑器资产改造也走这里）。</summary>
    public static void ConfigureCamera(Camera camera)
    {
        if (camera == null) return;

        camera.orthographic = true;
        camera.orthographicSize = Screen.height * 0.5f;   // 1 canvas unit = 1 屏幕像素
        camera.clearFlags = CameraClearFlags.Depth;       // Overlay 相机忽略清屏标志（不主清，只叠加）
        camera.cullingMask = 1 << UiLayer;                 // 只渲染 UI 层
        camera.depth = 100f;                                // Overlay 用 stack 顺序排序，留着无害
        camera.nearClipPlane = 0.3f;
        camera.farClipPlane = 2000f;
        camera.transform.position = new Vector3(0f, 0f, -10f);   // planeDistance=10 → UI 平面在 z=0
        camera.transform.rotation = Quaternion.identity;
        camera.tag = "Untagged";                           // 绝不抢 Camera.main（掉落标签/飘字都靠它）
        camera.GetUniversalAdditionalCameraData().renderType = CameraRenderType.Overlay;
        if (camera.GetComponent<UICameraRig>() == null) camera.gameObject.AddComponent<UICameraRig>();
        camera.enabled = true;
    }

    /// <summary>把 UI 相机挂进某个 Base 相机的 stack（URP Overlay 必须注册才渲染；幂等，先清理无效项）。</summary>
    public static void RegisterToBaseCamera(Camera baseCamera, Camera uiCam)
    {
        if (baseCamera == null || uiCam == null || baseCamera == uiCam) return;
        // 渲染到 RenderTexture 的相机（UIModelManager 的 CameraRTT 角色预览）不能挂 UI 相机：
        // URP 会把 Overlay 栈一起渲进这张 RT，整帧 alpha 被写成 1，
        // 模型预览在 UI 上就变成一块不透光的黑底（等 1 秒兜底扫描后才出现）。
        if (baseCamera.targetTexture != null) return;
        UniversalAdditionalCameraData data = baseCamera.GetUniversalAdditionalCameraData();
        if (data == null || data.renderType != CameraRenderType.Base) return;
        if (data.cameraStack == null) return;

        // 已销毁相机留在栈里 → URP 每次跑 UpdateCameraStack 都会打 "overlay no longer exists" 警告，自己先清掉
        int removed = data.cameraStack.RemoveAll(cam => cam == null);
        if (removed > 0)
        {
            Debug.Log($"[UICameraService] {baseCamera.name}: 清理 {removed} 个已销毁的相机栈项");
        }
        if (data.cameraStack.Contains(uiCam)) return;
        data.cameraStack.Add(uiCam);
    }

    // ──────────────────────────────
    // 扫描与转换
    // ──────────────────────────────

    /// <summary>全量扫描：overlay 画布转 camera space + 世界相机重绑 + 掩码清理 + UI 相机入栈（场景加载时与每秒兜底各跑一次）。</summary>
    public static void SweepAll()
    {
        if (!Enabled) return;
        Camera camera = EnsureUICamera();
        if (camera == null) return;

        // 1) 画布转换与重绑（含 inactive；世界空间画布跳过）
        Canvas[] canvases = UnityEngine.Object.FindObjectsOfType<Canvas>(true);
        foreach (Canvas canvas in canvases)
        {
            if (canvas == null) continue;
            if (!canvas.gameObject.scene.IsValid()) continue;                      // prefab 资产里的不算
            if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)                // 只转 overlay；WorldSpace 刻意留在 3D 世界里
            {
                ConvertCanvas(canvas, camera);
            }
            else if (canvas.renderMode == RenderMode.ScreenSpaceCamera && canvas.worldCamera != camera)
            {
                // 场景切换重建绑定：跨场景存活的画布（DontDestroyOnLoad 的 UiFxCanvas/阿福画布等）
                // 还挂着上一场景已销毁的相机，不重绑就集体失明
                canvas.worldCamera = camera;
            }
        }

        // 2) 其他相机：排除 UI 层（防双渲染/深度错乱）+ 清理无效栈项 + UI 相机注册
        int uiMask = 1 << UiLayer;
        foreach (Camera other in UnityEngine.Object.FindObjectsOfType<Camera>(true))
        {
            if (other == null || other == camera) continue;
            if ((other.cullingMask & uiMask) != 0) other.cullingMask &= ~uiMask;

            if (other.targetTexture != null)
            {
                // 预览用的 RTT 相机：把之前误挂进来的 UI 相机摘掉，否则 RT 整帧 alpha=1，UI 里显示成黑底
                UniversalAdditionalCameraData rttData = other.GetUniversalAdditionalCameraData();
                if (rttData != null && rttData.cameraStack != null && rttData.cameraStack.Remove(camera))
                {
                    Debug.Log($"[UICameraService] {other.name}: 摘除 UI 相机栈项（RTT 预览相机不叠 UI）");
                }
                continue;
            }

            RegisterToBaseCamera(other, camera);
        }
    }

    /// <summary>
    /// 单画布转换的方法（场景资产/程序化画布/兜底扫描共用）
    /// </summary>
    public static void ConvertCanvas(Canvas canvas, Camera camera)
    {
        if (canvas == null) return;
        canvas.worldCamera = camera;
        canvas.planeDistance = 10f;
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        SetLayerRecursive(canvas.transform, UiLayer);
    }

    /// <summary>整棵子树设层（画布下所有 Graphic 的 GameObject 都必须是 UI 层，UI 相机的 cullingMask 才看得见）。</summary>
    public static void SetLayerRecursive(Transform target, int layer)
    {
        if (target == null) return;
        target.gameObject.layer = layer;
        for (int i = 0; i < target.childCount; i++)
        {
            SetLayerRecursive(target.GetChild(i), layer);
        }
    }

    // ──────────────────────────────
    // 相机小跟班（挂在 UI 相机上）
    // ──────────────────────────────

    /// <summary>
    /// UI 相机跟班：每帧同步正交尺寸（分辨率/窗口变化即时生效）+ 每秒兜底扫描
    /// （程序化新建画布、动态出现的相机、框架自建的活动面板画布，晚于 sceneLoaded 钩子的都能被捞回来）
    /// </summary>
    public sealed class UICameraRig : MonoBehaviour
    {
        private Camera owner;
        private float nextSweep;

        private void Awake()
        {
            owner = GetComponent<Camera>();
            // AddComponent 幂等保护（Configure 对同一相机重复调用时）
            if (GetComponents<UICameraRig>().Length > 1) Destroy(this);
        }

        private void LateUpdate()
        {
            if (owner == null) return;

            // 兜底相机在框架相机注入后就让位（正常情况下已被销毁，这里是保险）
            if (externalCamera != null && owner != externalCamera)
            {
                owner.enabled = false;
                return;
            }

            float targetSize = Screen.height * 0.5f;
            if (Mathf.Abs(owner.orthographicSize - targetSize) > 0.01f)
            {
                owner.orthographicSize = targetSize;
            }

            if (Enabled && Time.unscaledTime >= nextSweep)
            {
                nextSweep = Time.unscaledTime + 1f;
                SweepAll();
            }
        }
    }
}
