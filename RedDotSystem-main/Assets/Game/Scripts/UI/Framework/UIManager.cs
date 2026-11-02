using DG.Tweening;
using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace SkierFramework
{
    public enum UIBlackType
    {
        None,       // 无黑边，全适应
        Height,     // 保持高度填满，两边黑边
        Width,      // 保持宽度填满, 上下黑边
        AutoBlack,  // 自动黑边(选中左右或上下黑边最少的一方)
    }

    public struct UIJumpData
    {
        public string curUIId;
        public object curUserData;
        public string nextUIId;
        public object nextUserData;
    }

    public class UIManager : MieMieFrameWork.SingletonBehaviour<UIManager>
    {
        /// <summary>
        /// 需要修改分辨率 根据实际情况
        /// </summary>
        public int width = 1920;
        public int height = 1080;
        public UIBlackType uiBlackType = UIBlackType.AutoBlack;

        private Transform _root;
        private Camera _worldCamera;
        private Camera _uiCamera;
        /// <summary>
        /// 屏幕渐变遮罩
        /// </summary>
        private CanvasGroup _blackMask;
        private CanvasGroup _backgroundMask;
        private Tweener _fadeTweener;
        /// <summary>
        /// 黑边
        /// </summary>
        private RectTransform[] _blacks = new RectTransform[2];

        // 注册表 key 统一为字符串：C# UI = UIType 枚举名；纯 Lua 热更 UI = json 里任意不重复字符串
        private Dictionary<string, UIViewController> _viewControllers;
        private Dictionary<UILayer, UILayerLogic> _layers;
        private HashSet<string> _openViews;
        private HashSet<string> _residentViews;
        private List<UIJumpData> _uiJumpDatas;
        private bool _isConfigInit;
        /// <summary>
        /// 初始化进行中（YooAsset 初始化 + 配置加载是异步的）
        /// </summary>
        private bool _isInitializing;
        /// <summary>
        /// 注册表重建进行中（热更生效流程）：期间 IsReady 为 false，Open/Close 会等待而不是另起一套初始化
        /// </summary>
        private bool _isReloading;
        /// <summary>
        /// UI 配置是否已真正解析完成（区别于 _isConfigInit 发起即置 true）
        /// </summary>
        private bool _isConfigLoaded;

        public EventSystem EventSystem { get; private set; }
        public EventController<UIEvent> Event { get; private set; }
        public Camera UICamera => _uiCamera;

        // Debug用
        public List<UIJumpData> UIJumpDatas => _uiJumpDatas;

        /// <summary>
        /// 打开/加载 UI 的分段耗时日志开关（排查卡顿用，默认关闭；打开后每次 Open 打一条 [UITiming]）
        /// </summary>
        public static bool LogOpenTiming;

        public void Initialize()
        {
            if (_viewControllers != null) return;

            _layers = new Dictionary<UILayer, UILayerLogic>();
            _viewControllers = new Dictionary<string, UIViewController>();
            _openViews = new HashSet<string>();
            _residentViews = new HashSet<string>();
            _uiJumpDatas = new List<UIJumpData>();
            Event = new EventController<UIEvent>();

            // UI 相机由框架自建并常驻（DDOL）。场景里原有的 UICamera 由 UICameraService 让位/禁用，
            // 避免两套 UI 相机同时渲染导致层级/后处理冲突。
            var root = GameObject.Find("SkierUIRoot");
            if (root == null)
            {
                root = new GameObject("SkierUIRoot");
            }
            root.layer = Layer.UI;
            GameObject.DontDestroyOnLoad(root);
            _root = root.transform;

            var cameraGo = GameObject.Find("SkierUICamera");
            if (cameraGo == null)
            {
                cameraGo = new GameObject("SkierUICamera");
                cameraGo.transform.SetParent(_root);
            }
            _uiCamera = cameraGo.GetOrAddComponent<Camera>();
            _uiCamera.cullingMask = 1 << Layer.UI;
            _uiCamera.orthographic = true;
            _uiCamera.clearFlags = CameraClearFlags.Depth;
            _uiCamera.orthographicSize = Screen.height * 0.5f;
            _uiCamera.transform.position = new Vector3(0f, 0f, -10f);
            _uiCamera.transform.rotation = Quaternion.identity;
            // URP 管线下必须设为 Overlay，并注册到当前 Base 相机的 Camera Stack
            var cameraData = _uiCamera.GetOrAddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            cameraData.renderType = UnityEngine.Rendering.Universal.CameraRenderType.Overlay;
            UICameraService.SetExternalCamera(_uiCamera);

            _worldCamera = Camera.main;
            EnsureCameraStacks();
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;

            EventSystem = EventSystem.current;
            if (EventSystem == null)
            {
                var esGo = new GameObject("EventSystem_UI");
                EventSystem = esGo.AddComponent<EventSystem>();
                esGo.AddComponent<StandaloneInputModule>();
                GameObject.DontDestroyOnLoad(esGo);
            }

            var layers = Enum.GetValues(typeof(UILayer));
            foreach (UILayer layer in layers)
            {
                bool is3d = layer == UILayer.SceneLayer;
                Canvas layerCanvas = UIExtension.CreateLayerCanvas(layer, is3d, _root, is3d ? _worldCamera : _uiCamera, width, height);
                UILayerLogic uILayerLogic = new UILayerLogic(layer, layerCanvas);
                _layers.Add(layer, uILayerLogic);
            }
            _blackMask = UIExtension.CreateBlackMask(_layers[UILayer.BlackMaskLayer].canvas.transform);
            _backgroundMask = UIExtension.CreateBlackMask(_layers[UILayer.BackgroundLayer].canvas.transform);

            // ui 包新清单生效（HostPlay 更新完成）→ 重读 UIConfig，重建全部 UI 与 Lua 模块缓存
            YooAssetService.Instance.OnPackageUpdated -= OnYooPackageUpdated;
            YooAssetService.Instance.OnPackageUpdated += OnYooPackageUpdated;

            AdoptAllCanvases();
        }

        private void OnYooPackageUpdated(string packageName)
        {
            if (packageName != YooAssetService.DefaultPackageName)
                return;
            if (!IsReady || _isReloading)
                return;

            ReloadUIConfig().Forget();
        }

        /// <summary>
        /// 主动检查 ui 包更新：请求新版本 → 加载清单 → 下载差异文件 → 触发 ReloadUIConfig。
        /// 适合挂在“检查更新”按钮或登录完成后的启动流程里。
        /// </summary>
        public async UniTask<bool> CheckUIUpdateAsync(CancellationToken cancellationToken = default)
        {
            await EnsureInitializedAsync(cancellationToken);
            if (!YooAssetService.Instance.IsInitialized)
                return false;

            return await YooAssetService.Instance.UpdatePackageAsync(
                YooAssetService.DefaultPackageName, cancellationToken);
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            EnsureCameraStacks();
            AdoptAllCanvases();
        }

        /// <summary>
        /// 把框架 UI 相机注册到所有 Base 相机（URP Overlay Stack），并让场景里的旧 UICamera 让位。
        /// 可重复调用（场景切换、战斗相机切换后都要重跑）。
        /// </summary>
        public void EnsureCameraStacks()
        {
            if (_uiCamera == null) return;
            if (!_uiCamera.gameObject.activeSelf) _uiCamera.gameObject.SetActive(true);

            int uiMask = 1 << Layer.UI;
            foreach (var cam in UnityEngine.Object.FindObjectsOfType<Camera>(true))
            {
                if (cam == _uiCamera) continue;

                // 历史遗留的同名 UICamera（旧场景资产里的第二台 UI 相机）让位；正常情况下它已被 UICameraService 销毁
                if (cam.name == UICameraService.UICameraObjectName)
                {
                    cam.enabled = false;
                    continue;
                }

                var data = cam.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
                if (data != null && data.renderType == UnityEngine.Rendering.Universal.CameraRenderType.Base)
                {
                    cam.cullingMask &= ~uiMask;
                    // 统一走服务里的注册（会先清掉已销毁的栈项，防 URP 打 "overlay no longer exists" 警告）
                    UICameraService.RegisterToBaseCamera(cam, _uiCamera);
                }
            }
        }

        /// <summary>把一个画布接管到框架 UI 相机/UI 层（程序化画布与旧场景画布共用）。</summary>
        public void AdoptCanvas(Canvas canvas)
        {
            if (canvas == null || _uiCamera == null) return;
            if (canvas.renderMode == RenderMode.WorldSpace) return;

            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = _uiCamera;
            canvas.planeDistance = 10f;
            canvas.gameObject.SetLayerRecursively(Layer.UI);
        }

        /// <summary>全场景扫描并接管画布（overlay 与旧的 camera space 都重绑到框架 UI 相机）。</summary>
        public void AdoptAllCanvases()
        {
            if (_uiCamera == null) return;
            foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>(true))
            {
                if (canvas.renderMode == RenderMode.WorldSpace) continue;
                if (canvas.renderMode == RenderMode.ScreenSpaceCamera && canvas.worldCamera == _uiCamera) continue;
                AdoptCanvas(canvas);
            }
        }

        /// <summary>
        /// 创建或者调整黑边，需间隔触发，由于有些设备屏幕是可以转动，是动态的
        /// </summary>
        private void ChangeOrCreateBlack()
        {
            if (_layers == null) return;
            var parent = _layers[UILayer.BackgroundLayer].canvas.transform as RectTransform;
            var uIBlackType = GetUIBlackType();
            switch (uIBlackType)
            {
                case UIBlackType.Height:
                    // 高度适配时的左右黑边
                    var rect = _blacks[0];
                    if (rect == null)
                    {
                        _blacks[0] = rect = UIExtension.CreateBlackMask(parent, 1, "right").transform as RectTransform;
                    }
                    else if (Mathf.Abs(rect.anchoredPosition.x * 2 + parent.rect.width - width) < 1)
                    {
                        return;
                    }
                    rect.pivot = new Vector2(0, 0.5f);
                    rect.anchorMin = new Vector2(1, 0);
                    rect.anchorMax = new Vector2(1, 1);
                    rect.sizeDelta = new Vector2(Mathf.Abs(width - parent.rect.width), 0);
                    rect.anchoredPosition = new Vector2((width - parent.rect.width) / 2, 0);

                    rect = _blacks[1];
                    if (rect == null)
                    {
                        _blacks[1] = rect = UIExtension.CreateBlackMask(parent, 1, "left").transform as RectTransform;
                    }
                    rect.pivot = new Vector2(1, 0.5f);
                    rect.anchorMin = new Vector2(0, 0);
                    rect.anchorMax = new Vector2(0, 1);
                    rect.sizeDelta = new Vector2(Mathf.Abs(width - parent.rect.width), 0);
                    rect.anchoredPosition = new Vector2(-(width - parent.rect.width) / 2, 0);
                    break;
                case UIBlackType.Width:
                    // 宽度适配时的上下黑边
                    rect = _blacks[0];
                    if (rect == null)
                    {
                        _blacks[0] = rect = UIExtension.CreateBlackMask(parent, 1, "top").transform as RectTransform;
                    }
                    else if (Mathf.Abs(rect.anchoredPosition.y * 2 + parent.rect.height - height) < 1)
                    {
                        return;
                    }
                    rect.pivot = new Vector2(0.5f, 0);
                    rect.anchorMin = new Vector2(0, 1);
                    rect.anchorMax = new Vector2(1, 1);
                    rect.sizeDelta = new Vector2(0, Mathf.Abs(height - parent.rect.height));
                    rect.anchoredPosition = new Vector2(0, (height - parent.rect.height) / 2);

                    rect = _blacks[1];
                    if (rect == null)
                    {
                        _blacks[1] = rect = UIExtension.CreateBlackMask(parent, 1, "bottom").transform as RectTransform;
                    }
                    rect.pivot = new Vector2(0.5f, 1);
                    rect.anchorMin = new Vector2(0, 0);
                    rect.anchorMax = new Vector2(1, 0);
                    rect.sizeDelta = new Vector2(0, Mathf.Abs(height - parent.rect.height));
                    rect.anchoredPosition = new Vector2(0, -(height - parent.rect.height) / 2);
                    break;
                default:
                    break;
            }
        }

        public UIBlackType GetUIBlackType()
        {
            var uIBlackType = uiBlackType;
            if (uIBlackType == UIBlackType.AutoBlack)
            {
                var parent = _layers[UILayer.BackgroundLayer].canvas.transform as RectTransform;
                float widthDis = Mathf.Abs(width - parent.rect.width);
                float heightDis = Mathf.Abs(height - parent.rect.height);

                if (widthDis < 1 && heightDis < 1)
                    uIBlackType = UIBlackType.None;
                else if (widthDis > heightDis)
                    uIBlackType = UIBlackType.Height;
                else
                    uIBlackType = UIBlackType.Width;
            }
            return uIBlackType;
        }

        public Rect GetSafeArea()
        {
            Rect rect = Screen.safeArea;
            if (uiBlackType == UIBlackType.Width)
            {
                var parent = _layers[UILayer.BackgroundLayer].canvas.transform as RectTransform;
                float blackArea = Mathf.Abs(height - parent.rect.height) / 2;
                rect.yMin = Mathf.Max(0, rect.yMin - blackArea);
                rect.yMax = Mathf.Min(rect.yMax + blackArea, Screen.height);
            }
            else if (uiBlackType == UIBlackType.Height)
            {
                var parent = _layers[UILayer.BackgroundLayer].canvas.transform as RectTransform;
                float blackArea = Mathf.Abs(width - parent.rect.width) / 2;
                rect.xMin = Mathf.Max(0, rect.xMin - blackArea);
                rect.xMax = Mathf.Min(rect.xMax + blackArea, Screen.width);
            }
            return rect;
        }

        public void EnableBackgroundMask(bool enable)
        {
            _backgroundMask.alpha = enable ? 1 : 0;
        }

        public async UniTask InitUIConfig()
        {
            // 重复调用自动跳过
            if (_isConfigInit) return;
            _isConfigInit = true;

            // 初始化需要加载所有UI的配置
            var list = await UIConfig.GetAllConfigs();
            var configs = BuildControllers(list);
            foreach (var kv in configs)
            {
                if (_viewControllers.ContainsKey(kv.Key))
                {
                    Debug.LogErrorFormat("存在相同的uiType:{0}， 请检查UIConfig是否重复！", kv.Key);
                    continue;
                }
                _viewControllers.Add(kv.Key, kv.Value);
            }
        }

        /// <summary>
        /// 由配置列表构建注册表（纯构建，不写入 _viewControllers），供热更时重建
        /// </summary>
        private Dictionary<string, UIViewController> BuildControllers(List<UIConfig> configs)
        {
            var result = new Dictionary<string, UIViewController>();
            if (configs == null) return result;
            if (_layers == null)
            {
                Debug.LogError("[UIManager] BuildControllers 在 Initialize() 之前被调用，_layers 为空！");
                return result;
            }

            foreach (var cfg in configs)
            {
                if (result.ContainsKey(cfg.uiType))
                {
                    Debug.LogErrorFormat("存在相同的uiType:{0}， 请检查UIConfig是否重复！", cfg.uiType);
                    continue;
                }
                if (!_layers.TryGetValue(cfg.uiLayer, out var layer))
                {
                    Debug.LogErrorFormat("uiLayer 不存在：{0}（{1}）", cfg.uiLayer, cfg.uiType);
                    continue;
                }

                result.Add(cfg.uiType, new UIViewController
                {
                    uiPath = cfg.path,
                    uiId = cfg.uiType,
                    uiLayer = layer,
                    uiViewType = cfg.viewType,
                    isWindow = cfg.isWindow,
                    luaModuleName = cfg.luaModuleName,
                });
            }
            return result;
        }

        /// <summary>
        /// 热更后重读 UIConfig.json 并重建注册表。
        /// 前提：YooAsset 已激活新清单（否则读到的还是旧 json）。
        /// 流程：解析新表（失败保旧表）→ 关闭全部界面 → 彻底释放实例与句柄 → 换表 → 清 Lua 模块缓存。
        /// 注意：期间 IsReady 为 false，并发 Open 会等待本次重建结束；常驻 UI 也会被拆掉重建。
        /// </summary>
        public async UniTask ReloadUIConfig()
        {
            await EnsureInitializedAsync(DestroyToken);
            if (_layers == null || _isReloading) return;

            _isReloading = true;
            _isConfigLoaded = false;
            try
            {
                var configs = BuildControllers(await UIConfig.GetAllConfigs());
                if (configs.Count == 0)
                {
                    // 解析失败/空表：保留旧注册表，不能让 UI 系统直接空掉
                    Debug.LogError("[UIManager] ReloadUIConfig：新配置为空或解析失败，沿用当前注册表");
                    _isConfigLoaded = _viewControllers != null && _viewControllers.Count > 0;
                    return;
                }

                await CloseAllCoreAsync(null, closeResidentView: true);
                await ReleaseAllCoreAsync(includeResident: true);

                _viewControllers = configs;
                _openViews.Clear();
                _uiJumpDatas.Clear();
                UILuaView.ClearAllModuleCache();

                _isConfigLoaded = true;
                Debug.LogFormat("[UIManager] UI 注册表已重建，共 {0} 项", configs.Count);
            }
            finally
            {
                _isReloading = false;
            }
        }

        /// <summary>
        /// C# 枚举 UI 转注册表字符串键；UIType.Max 是“无 UI”哨兵，转成 null
        /// </summary>
        public static string ToUIId(UIType type)
        {
            return type == UIType.Max ? null : type.ToString();
        }

        /// <summary>
        /// 注册常驻UI
        /// </summary>
        public void AddResidentUI(string uiId)
        {
            if (string.IsNullOrEmpty(uiId)) return;
            _residentViews.Add(uiId);
        }

        /// <summary>
        /// 注册常驻UI（C# UI 转发）
        /// </summary>
        public void AddResidentUI(UIType type)
        {
            AddResidentUI(ToUIId(type));
        }

        /// <summary>
        /// 是否初始化完成（YooAsset + UI 配置均就绪）
        /// </summary>
        private bool IsReady => _viewControllers != null && _isConfigLoaded;

        /// <summary>
        /// UIManager 生命周期令牌（DDOL 单例销毁时取消）
        /// </summary>
        private CancellationToken DestroyToken => this.GetCancellationTokenOnDestroy();

        /// <summary>
        /// 确保初始化完成：未就绪则等待；首个调用方触发 YooAsset + UI 配置初始化。
        /// </summary>
        private async UniTask EnsureInitializedAsync(CancellationToken cancellationToken)
        {
            if (IsReady) return;

            if (_isInitializing || _isReloading)
            {
                // 已有初始化/重建流程在进行：等待其结束（成功或失败都退出，避免死等）
                await UniTask.WaitUntil(() => IsReady || (!_isInitializing && !_isReloading), cancellationToken: cancellationToken);
                return;
            }

            _isInitializing = true;
            var initWatch = LogOpenTiming ? System.Diagnostics.Stopwatch.StartNew() : null;
            try
            {
                // 1) Resources 适配版无需异步初始化（活动热更仍走项目现有 YooAsset/LuaActivityHost）
                await ResourceManager.Instance.InitializeAsync(cancellationToken);
                long msResource = initWatch != null ? initWatch.ElapsedMilliseconds : 0;

                // 2) UI 根节点/分层/相机
                Initialize();
                long msLayers = initWatch != null ? initWatch.ElapsedMilliseconds : 0;

                // 3) 加载 UI 配置
                await InitUIConfig();
                _isConfigLoaded = true;

                if (initWatch != null)
                {
                    Debug.Log($"[UITiming] init: resource={msResource}ms layers={msLayers - msResource}ms config={initWatch.ElapsedMilliseconds - msLayers}ms total={initWatch.ElapsedMilliseconds}ms");
                }
            }
            finally
            {
                _isInitializing = false;
            }
        }

        /// <summary>
        /// 开启UI（uiId 字符串：C# UI 传 UIType 枚举名，纯 Lua 热更 UI 传 UIConfig.json 里配置的字符串）
        /// </summary>
        public async UniTask Open(string uiId, object userData = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(uiId)) return;
            cancellationToken = cancellationToken == default ? DestroyToken : cancellationToken;
            await EnsureInitializedAsync(cancellationToken);
            if (!IsReady) return;

            if (!_viewControllers.ContainsKey(uiId))
            {
                Debug.LogErrorFormat("未配置uiType:{0}， 请检查UIConfig.cs！", uiId);
                return;
            }

            _openViews.Add(uiId);
            await _viewControllers[uiId].Open(userData);
        }

        /// <summary>
        /// 开启UI（C# UI 转发）
        /// </summary>
        public async UniTask Open(UIType type, object userData = null, CancellationToken cancellationToken = default)
        {
            await Open(ToUIId(type), userData, cancellationToken);
        }

        /// <summary>
        /// 关闭UI
        /// </summary>
        public async UniTask Close(string uiId, bool isJump = false, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(uiId)) return;
            cancellationToken = cancellationToken == default ? DestroyToken : cancellationToken;
            await EnsureInitializedAsync(cancellationToken);
            if (!IsReady) return;

            if (!_viewControllers.ContainsKey(uiId))
            {
                Debug.LogErrorFormat("未配置uiType:{0}， 请检查UIConfig.cs！", uiId);
                return;
            }

            _openViews.Remove(uiId);
            await _viewControllers[uiId].Close(isJump);
        }

        /// <summary>
        /// 关闭UI（C# UI 转发）
        /// </summary>
        public async UniTask Close(UIType type, bool isJump = false, CancellationToken cancellationToken = default)
        {
            await Close(ToUIId(type), isJump, cancellationToken);
        }

        /// <summary>
        /// UI跳转 
        /// 解决想要有依次打开1->2->3->2，并在关闭2时依次是2->3->2->1的恢复情况
        /// 跳转问题不该由底层的UI遮挡问题来实现，属于两套逻辑
        /// UI遮挡问题的目的：解决底下看不见的UI的重复渲染，不管理其他业务。
        /// 
        /// 逻辑：从 curUI 跳转到 nextUI，如果nextUI被关闭则重新打开curUI
        /// </summary>
        public async UniTask JumpUI(string curUIId, object curUserData, string nextUIId, object nextUserData, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(curUIId) || string.IsNullOrEmpty(nextUIId)) return;
            cancellationToken = cancellationToken == default ? DestroyToken : cancellationToken;
            await EnsureInitializedAsync(cancellationToken);
            if (!IsReady) return;

            // 记录跳转关系（须在 await 关闭前加入，保证 nextUI 关闭时 OnUIClose 能取到）
            _uiJumpDatas.Add(new UIJumpData
            {
                curUIId = curUIId,
                curUserData = curUserData,
                nextUIId = nextUIId,
                nextUserData = nextUserData
            });

            if (IsOpen(curUIId))
            {
                int order = _viewControllers[curUIId].order;
                // 由于存在异步，所以必须等他先开启完毕后，再关闭
                await Open(nextUIId, nextUserData, cancellationToken);
                if (order == _viewControllers[curUIId].order)
                {
                    await Close(curUIId, isJump: true, cancellationToken);
                }
            }
            else
            {
                Debug.LogError($"跳转UI从 {curUIId} 跳转到 {nextUIId}时，{curUIId}并没有被开启！");
            }
        }

        /// <summary>
        /// UI跳转（C# UI 转发）
        /// </summary>
        public async UniTask JumpUI(UIType curUIType, object curUserData, UIType nextUIType, object nextUserData, CancellationToken cancellationToken = default)
        {
            await JumpUI(ToUIId(curUIType), curUserData, ToUIId(nextUIType), nextUserData, cancellationToken);
        }

        /// <summary>
        /// UI关闭时回调，把跳转之前的UI重新还原。
        /// </summary>
        public void OnUIClose(string uiId)
        {
            if (_uiJumpDatas.Count == 0) return;

            for (int i = _uiJumpDatas.Count - 1; i >= 0; i--)
            {
                if (_uiJumpDatas[i].nextUIId == uiId)
                {
                    Open(_uiJumpDatas[i].curUIId, _uiJumpDatas[i].curUserData).Forget();
                    _uiJumpDatas.RemoveAt(i);
                    break;
                }
            }
        }

        /// <summary>
        /// UI打开失败时回调（如资源加载失败），清理打开标记保证 IsOpen() 与真实状态一致
        /// </summary>
        public void OnUIOpenFailed(string uiId)
        {
            _openViews.Remove(uiId);
        }

        public void Preload(string uiId)
        {
            PreloadInternalAsync(uiId).Forget();
        }

        private async UniTaskVoid PreloadInternalAsync(string uiId)
        {
            if (string.IsNullOrEmpty(uiId)) return;
            await EnsureInitializedAsync(DestroyToken);
            if (!IsReady) return;

            if (!_viewControllers.TryGetValue(uiId, out var controller))
            {
                Debug.LogErrorFormat("未配置uiType:{0}， 请检查UIConfig.cs！", uiId);
                return;
            }
            await controller.Load();
        }

        /// <summary>
        /// 预加载（C# UI 转发）
        /// </summary>
        public void Preload(UIType type)
        {
            Preload(ToUIId(type));
        }

        public void PreloadAll()
        {
            PreloadAllInternalAsync().Forget();
        }

        private async UniTaskVoid PreloadAllInternalAsync()
        {
            await EnsureInitializedAsync(DestroyToken);
            if (!IsReady) return;

            foreach (var controller in _viewControllers.Values)
            {
                ResourceManager.Instance.LoadAssetAsync<GameObject>(controller.uiPath).Forget();
            }
        }

        public bool IsOpen(string uiId)
        {
            if (!IsReady)
            {
                // 查询类接口在初始化完成前被调用是正常时序（入口按钮/常驻脚本会先问），
                // 用 Warning 而不是 Error：Error 会触发编辑器的 Error Pause 把游戏暂停（2026-10-21 踩过）
                Debug.LogWarning("[UIManager] 尚未初始化完成，无法查询！");
                return false;
            }
            return !string.IsNullOrEmpty(uiId) && _openViews.Contains(uiId);
        }

        /// <summary>
        /// 是否打开（C# UI 转发）
        /// </summary>
        public bool IsOpen(UIType type)
        {
            return IsOpen(ToUIId(type));
        }

        /// <summary>
        /// UI建议都用事件进行交互，最好不使用该接口
        /// </summary>
        public T GetView<T>(string uiId) where T : UIView
        {
            if (!IsReady)
            {
                Debug.LogWarning("[UIManager] 尚未初始化完成，无法获取视图！");
                return null;
            }

            if (string.IsNullOrEmpty(uiId) || !_viewControllers.ContainsKey(uiId))
            {
                Debug.LogErrorFormat("未配置uiType:{0}， 请检查UIConfig.cs！", uiId);
                return null;
            }

            return _viewControllers[uiId].uiView as T;
        }

        /// <summary>
        /// UI建议都用事件进行交互，最好不使用该接口（C# UI 转发）
        /// </summary>
        public T GetView<T>(UIType type) where T : UIView
        {
            return GetView<T>(ToUIId(type));
        }

        /// <summary>
        /// 获得已经打开的UI，没开返回空
        /// </summary>
        public UIView GetOpenedView(string uiId)
        {
            if (!IsReady)
            {
                Debug.LogWarning("[UIManager] 尚未初始化完成，无法获取视图！");
                return null;
            }

            if (!string.IsNullOrEmpty(uiId) && _viewControllers.TryGetValue(uiId, out var viewController))
            {
                if (viewController.uiView != null && viewController.isOpen)
                {
                    return viewController.uiView;
                }
            }
            return null;
        }

        /// <summary>
        /// 获得已经打开的UI，没开返回空（C# UI 转发）
        /// </summary>
        public UIView GetOpenedView(UIType type)
        {
            return GetOpenedView(ToUIId(type));
        }

        /// <summary>
        /// 关闭所有UI（常驻UI可选保留）。ignoreId 为 null/空 表示不忽略任何 UI
        /// </summary>
        public void CloseAll(string ignoreId = null, bool closeResidentView = false)
        {
            CloseAllAsync(ignoreId, closeResidentView).Forget();
        }

        /// <summary>
        /// 关闭所有UI并等待关闭动画全部播完（热更生效流程需要在换注册表前等到这里）
        /// </summary>
        public async UniTask CloseAllAsync(string ignoreId = null, bool closeResidentView = false)
        {
            await EnsureInitializedAsync(DestroyToken);
            if (!IsReady) return;
            await CloseAllCoreAsync(ignoreId, closeResidentView);
        }

        /// <summary>
        /// 关闭主体：不含初始化等待，供热更流程在 _isReloading 期间直接调用
        /// </summary>
        private async UniTask CloseAllCoreAsync(string ignoreId, bool closeResidentView)
        {
            _uiJumpDatas.Clear();
            var list = ListPool<string>.Get();

            foreach (var uiId in _openViews)
            {
                if (!string.IsNullOrEmpty(ignoreId) && ignoreId == uiId) continue;

                if (closeResidentView || !_residentViews.Contains(uiId))
                {
                    list.Add(uiId);
                }
            }

            var closers = new List<UniTask>(list.Count);
            foreach (var uiId in list)
            {
                if (_viewControllers.TryGetValue(uiId, out var controller))
                {
                    _openViews.Remove(uiId);
                    closers.Add(controller.Close());
                }
            }
            ListPool<string>.Release(list);

            // 并发播放关闭动画，但要等它们全部结束再往下走
            await UniTask.WhenAll(closers);
        }

        /// <summary>
        /// 关闭所有UI（C# UI 转发，忽略指定枚举类型）
        /// </summary>
        public void CloseAll(UIType ignoreType, bool closeResidentView = false)
        {
            CloseAll(ToUIId(ignoreType), closeResidentView);
        }

        public void ReleaseAll()
        {
            ReleaseAllAsync().Forget();
        }

        public async UniTask ReleaseAllAsync()
        {
            await EnsureInitializedAsync(DestroyToken);
            if (!IsReady) return;
            await ReleaseAllCoreAsync(includeResident: false);
        }

        /// <summary>
        /// 释放全部 UI（含常驻 UI），用于退出登录这种"整会话重置"场景：
        /// ReleaseAll 会刻意跳过常驻主页，退出登录时主页会一直浮在登录场景上。
        /// </summary>
        public async UniTask ReleaseAllIncludingResidentAsync()
        {
            await EnsureInitializedAsync(DestroyToken);
            if (!IsReady) return;
            await ReleaseAllCoreAsync(includeResident: true);
            _residentViews.Clear();
        }

        private UniTask ReleaseAllCoreAsync(bool includeResident)
        {
            _uiJumpDatas.Clear();
            foreach (var controller in _viewControllers.Values)
            {
                if (!includeResident && _residentViews.Contains(controller.uiId)) continue;

                _openViews.Remove(controller.uiId);
                controller.FullRelease();
            }
            return UniTask.CompletedTask;
        }

        /// <summary>
        /// 关闭指定层级的所有UI（仅隐藏，实例保留复用；常驻UI可选保留）
        /// </summary>
        public void CloseAllInLayer(UILayer layer, bool closeResidentView = false)
        {
            CloseAllInLayerInternalAsync(layer, closeResidentView).Forget();
        }

        private async UniTaskVoid CloseAllInLayerInternalAsync(UILayer layer, bool closeResidentView)
        {
            await EnsureInitializedAsync(DestroyToken);
            if (!IsReady) return;

            _uiJumpDatas.Clear();
            var list = ListPool<string>.Get();

            foreach (var uiId in _openViews)
            {
                var controller = _viewControllers[uiId];
                if (controller.uiLayer == null || controller.uiLayer.layer != layer) continue;

                if (closeResidentView || !_residentViews.Contains(uiId))
                {
                    list.Add(uiId);
                }
            }
            foreach (var uiId in list)
            {
                if (_viewControllers.TryGetValue(uiId, out var controller))
                {
                    _openViews.Remove(uiId);
                    controller.Close().Forget();
                }
            }
            ListPool<string>.Release(list);
        }

        /// <summary>
        /// 彻底清除指定层级的所有UI（回收实例并卸载预制体资源，常驻UI除外）
        /// </summary>
        public void ReleaseAllInLayer(UILayer layer)
        {
            ReleaseAllInLayerInternalAsync(layer).Forget();
        }

        private async UniTaskVoid ReleaseAllInLayerInternalAsync(UILayer layer)
        {
            await EnsureInitializedAsync(DestroyToken);
            if (!IsReady) return;

            _uiJumpDatas.Clear();
            foreach (var controller in _viewControllers.Values)
            {
                if (controller.uiLayer == null || controller.uiLayer.layer != layer) continue;
                if (_residentViews.Contains(controller.uiId)) continue;

                _openViews.Remove(controller.uiId);
                controller.FullRelease();
            }
        }

        public void FadeIn(float duration = 0.5f, TweenCallback callback = null)
        {
            if (_fadeTweener != null && _fadeTweener.IsPlaying())
                _fadeTweener.Complete();
            _fadeTweener = _blackMask.DOFade(1.0f, duration);
            _fadeTweener.onComplete = callback;
        }

        public void FadeOut(float duration = 0.5f, TweenCallback callback = null)
        {
            if (_fadeTweener != null && _fadeTweener.IsPlaying())
                _fadeTweener.Complete();
            _fadeTweener = _blackMask.DOFade(0.0f, duration);
            _fadeTweener.onComplete = callback;
        }

        public void FadeInOut(float duration = 1.0f, TweenCallback callback = null)
        {
            if (_fadeTweener != null && _fadeTweener.IsPlaying())
                _fadeTweener.Complete();
            _fadeTweener = _blackMask.DOFade(1.0f, duration * 0.5f);
            _fadeTweener.onComplete += () =>
            {
                _fadeTweener = _blackMask.DOFade(0.0f, duration * 0.5f);
                _fadeTweener.onComplete = callback;
            };
        }

        public void Cancel()
        {
            if (!IsReady)
            {
                return;
            }

            if (_layers.TryGetValue(UILayer.NormalLayer, out var layer) && layer.openedViews.Count > 0)
            {
                var viewController = layer.openedViews[layer.openedViews.Count - 1];
                if (viewController.uiView != null)
                {
                    viewController.uiView.OnCancel();
                }
            }
        }

        public Canvas GetLayerCanvas(UILayer layer)
        {
            // 初始化（YooAsset 异步）完成前 _layers 为 null，调用方需容忍返回 null 后惰性重试
            if (_layers == null) return null;
            if (_layers.TryGetValue(layer, out var layerLogic))
                return layerLogic.canvas;
            return null;
        }
    }
}
