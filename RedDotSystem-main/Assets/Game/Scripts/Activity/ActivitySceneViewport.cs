using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace Game.Activity
{
    /// <summary>
    /// 活动用的**场景内 3D 视口**：不建 RT，直接把热更包里的舞台放到当前场景的远端，
    /// 接管主相机渲染它；同时把当前场景里与战斗无关的 UI 画布收起来，关闭时原样恢复。
    /// 与具体玩法无关，只做舞台/相机/UI 的接管与还原。
    /// </summary>
    public class ActivitySceneViewport : MonoBehaviour
    {
        /// <summary>舞台专用层（TagManager 里名为 ActivityStage，渲染与拾取只认这一层）</summary>
        public const int StageLayer = 8;

        /// <summary>舞台远离当前场景世界的锚点，避免与已有碰撞/视觉重叠</summary>
        private static readonly Vector3 SceneAnchor = new Vector3(0f, 3000f, 0f);

        // 照抄 BattleScene 的主相机参数：位置 (0,10,-2.5)、旋转 (80,0,0)、透视 FOV 60
        private static readonly Vector3 CameraLocalPosition = new Vector3(0f, 10f, -2.5f);
        private static readonly Vector3 CameraLocalEuler = new Vector3(80f, 0f, 0f);
        private const float CameraFov = 60f;
        private const float PickDistance = 100f;

        public Camera ViewCamera { get; private set; }
        public GameObject StageRoot { get; private set; }
        public ActivityDragProbe InputProbe { get; private set; }
        public bool IsReady => ViewCamera != null && StageRoot != null;

        private readonly List<Canvas> _hiddenCanvases = new List<Canvas>();
        private readonly List<CameraState> _cameraStates = new List<CameraState>();
        private Transform _hudRoot;
        private GameObject _rootObject;

        private class CameraState
        {
            public Camera camera;
            public bool enabled;
            public CameraClearFlags clearFlags;
            public Color background;
            public int cullingMask;
            public float near;
            public float far;
            public float fov;
            public float depth;
            public Vector3 position;
            public Quaternion rotation;
            public bool urpBaseDisabled;
        }

        /// <summary>
        /// 接管场景：建立舞台、挑选主相机渲染它、收起与 HUD 无关的画布。
        /// hudRoot 为战斗 HUD 所在节点（它在的画布保持可见）。
        /// </summary>
        public void Setup(GameObject stageInstance, Transform hudRoot)
        {
            Dispose();

            if (stageInstance == null)
            {
                Debug.LogError("[ActivitySceneViewport] 舞台为空");
                return;
            }

            _hudRoot = hudRoot;

            if (_rootObject == null) _rootObject = new GameObject("ActivitySceneRoot");
            _rootObject.transform.position = SceneAnchor;

            StageRoot = stageInstance;
            StageRoot.transform.SetParent(_rootObject.transform, false);
            StageRoot.transform.localPosition = Vector3.zero;
            StageRoot.transform.localRotation = Quaternion.identity;
            StageRoot.transform.localScale = Vector3.one;
            SetLayerRecursive(StageRoot);

            ViewCamera = PickMainCamera();
            if (ViewCamera == null)
            {
                Debug.LogError("[ActivitySceneViewport] 场景里没有可用相机");
                return;
            }

            HideOtherCanvases();
            TakeOverCameras();

            // 战斗里的音效由 AudioManager 一次性播放，场景没有 AudioListener 时会每帧刷警告
            if (FindObjectOfType<AudioListener>() == null && ViewCamera != null)
                ViewCamera.gameObject.AddComponent<AudioListener>();
        }

        /// <summary>屏幕坐标 → 与**舞台本地**水平面 y = planeY 的交点（拖拽跟手用）</summary>
        public Vector3 WorldOnPlaneAtScreen(Vector2 screenPoint, float planeY)
        {
            if (!IsReady) return Vector3.zero;
            float worldHeight = (_rootObject != null ? _rootObject.transform.position.y : 0f) + planeY;
            Ray ray = ViewCamera.ScreenPointToRay(new Vector3(screenPoint.x, screenPoint.y, 0f));
            var plane = new Plane(Vector3.up, new Vector3(0f, worldHeight, 0f));
            return plane.Raycast(ray, out float distance) ? ray.GetPoint(distance) : Vector3.zero;
        }

        /// <summary>世界坐标 → 舞台本地坐标（把拖拽落点写回卡片节点的 localPosition 用）</summary>
        public Vector3 WorldToStageLocal(Vector3 world)
        {
            if (_rootObject == null) return world;
            return world - _rootObject.transform.position;
        }

        /// <summary>屏幕坐标 → 命中的舞台对象名（未命中返回空串）</summary>
        public string PickNameAtScreen(Vector2 screenPoint)
        {
            if (!IsReady) return string.Empty;
            Ray ray = ViewCamera.ScreenPointToRay(new Vector3(screenPoint.x, screenPoint.y, 0f));
            RaycastHit[] hits = Physics.RaycastAll(ray, PickDistance, 1 << StageLayer);
            if (hits.Length == 0) return string.Empty;

            RaycastHit nearest = hits[0];
            for (int i = 1; i < hits.Length; i++)
                if (hits[i].distance < nearest.distance) nearest = hits[i];

            return nearest.collider != null ? nearest.collider.name : string.Empty;
        }

        /// <summary>从舞台里的模板节点克隆一张卡（模板保持未激活）</summary>
        public Transform SpawnCard(string templateName, float x, float y, float z, string name)
        {
            if (StageRoot == null) return null;

            Transform template = StageRoot.transform.Find(templateName);
            if (template == null)
            {
                Debug.LogWarning("[ActivitySceneViewport] 舞台缺少模板节点：" + templateName);
                return null;
            }

            Transform clone = Instantiate(template, StageRoot.transform);
            clone.gameObject.SetActive(true);
            clone.name = name;
            clone.localPosition = new Vector3(x, y, z);
            clone.localRotation = template.localRotation;
            clone.localScale = template.localScale;
            return clone;
        }

        /// <summary>
        /// 在**HUD 画布**下挂全屏输入探针，Lua 每帧取事件做拖拽。
        /// 活动面板自己的画布在战斗期间会被收起，探针必须挂在保持可见的 HUD 画布上才收得到指针事件。
        /// </summary>
        public ActivityDragProbe EnsureInputProbe()
        {
            if (InputProbe != null) return InputProbe;
            if (_hudRoot == null) return null;

            Canvas canvas = _hudRoot.GetComponent<Canvas>();
            if (canvas == null) return null;
            if (canvas.GetComponent<GraphicRaycaster>() == null) canvas.gameObject.AddComponent<GraphicRaycaster>();

            var go = new GameObject("InputArea", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(_hudRoot, false);
            rect.SetAsFirstSibling(); // 排在按钮之前，HUD 按钮仍优先接收点击
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            if (go.GetComponent<CanvasRenderer>() == null) go.AddComponent<CanvasRenderer>();

            var image = go.AddComponent<Image>();
            image.color = new Color(0f, 0f, 0f, 0.001f);
            image.raycastTarget = true;

            InputProbe = go.AddComponent<ActivityDragProbe>();
            return InputProbe;
        }

        /// <summary>还原相机与 UI（关闭战斗时调用）</summary>
        public void Dispose()
        {
            RestoreCameras();

            foreach (Canvas canvas in _hiddenCanvases)
                if (canvas != null) canvas.enabled = true;
            _hiddenCanvases.Clear();

            if (StageRoot != null) Destroy(StageRoot);
            StageRoot = null;

            if (_rootObject != null) Destroy(_rootObject);
            _rootObject = null;

            ViewCamera = null;
            _hudRoot = null;
            InputProbe = null;
        }

        private void OnDestroy()
        {
            Dispose();
        }

        private Camera PickMainCamera()
        {
            Camera main = Camera.main;
            if (main != null) return main;

            Camera[] cameras = FindObjectsOfType<Camera>(true);
            foreach (Camera camera in cameras)
                if (camera.targetTexture == null) return camera;
            return null;
        }

        private void TakeOverCameras()
        {
            _cameraStates.Clear();

            Camera[] cameras = FindObjectsOfType<Camera>(true);
            foreach (Camera camera in cameras)
            {
                var state = new CameraState
                {
                    camera = camera,
                    enabled = camera.enabled,
                    clearFlags = camera.clearFlags,
                    background = camera.backgroundColor,
                    cullingMask = camera.cullingMask,
                    near = camera.nearClipPlane,
                    far = camera.farClipPlane,
                    fov = camera.fieldOfView,
                    depth = camera.depth,
                    position = camera.transform.position,
                    rotation = camera.transform.rotation,
                };
                _cameraStates.Add(state);

                bool isMain = camera == ViewCamera;
                bool isOverlay = false;
                var urpData = camera.GetComponent<UniversalAdditionalCameraData>();
                if (urpData != null && urpData.renderType == CameraRenderType.Overlay) isOverlay = true;

                if (isMain)
                {
                    // 接管主相机：只渲染舞台层，UI 由 Overlay 相机栈继续叠在上面
                    camera.transform.position = _rootObject.transform.position + CameraLocalPosition;
                    camera.transform.rotation = _rootObject.transform.rotation * Quaternion.Euler(CameraLocalEuler);
                    camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.backgroundColor = new Color(0.05f, 0.06f, 0.08f, 1f);
                    camera.cullingMask = 1 << StageLayer;
                    camera.nearClipPlane = 0.3f;
                    camera.farClipPlane = 100f;
                    camera.fieldOfView = CameraFov;
                }
                else if (!isOverlay)
                {
                    // 其它 Base 相机（含 Cinemachine 驱动的）一律停掉，避免抢输出
                    camera.enabled = false;
                    state.urpBaseDisabled = true;
                }
            }
        }

        private void RestoreCameras()
        {
            foreach (CameraState state in _cameraStates)
            {
                Camera camera = state.camera;
                if (camera == null) continue;

                camera.transform.position = state.position;
                camera.transform.rotation = state.rotation;
                camera.clearFlags = state.clearFlags;
                camera.backgroundColor = state.background;
                camera.cullingMask = state.cullingMask;
                camera.nearClipPlane = state.near;
                camera.farClipPlane = state.far;
                camera.fieldOfView = state.fov;
                camera.depth = state.depth;
                camera.enabled = state.enabled;
            }
            _cameraStates.Clear();
        }

        private void HideOtherCanvases()
        {
            _hiddenCanvases.Clear();

            Canvas[] canvases = FindObjectsOfType<Canvas>(true);
            foreach (Canvas canvas in canvases)
            {
                if (canvas == null || !canvas.enabled) continue;
                if (_hudRoot != null && (_hudRoot.IsChildOf(canvas.transform) || canvas.transform == _hudRoot)) continue;

                canvas.enabled = false;
                _hiddenCanvases.Add(canvas);
            }
        }

        private static void SetLayerRecursive(GameObject root)
        {
            var stack = new Stack<Transform>();
            stack.Push(root.transform);
            while (stack.Count > 0)
            {
                Transform current = stack.Pop();
                current.gameObject.layer = StageLayer;
                for (int i = 0; i < current.childCount; i++) stack.Push(current.GetChild(i));
            }
        }
    }
}
