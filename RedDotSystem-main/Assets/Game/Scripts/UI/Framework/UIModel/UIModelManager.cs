using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace SkierFramework
{
    public class UIModelManager : MieMieFrameWork.SingletonService<UIModelManager>
    {
        private Stack<Camera> m_CameraPool = new Stack<Camera>();
        private Stack<int> m_IndexPool = new Stack<int>();
        private int m_PoolCount = 0;
        private Camera m_UICamera;

        private Transform m_UIModelRoot;
        private Light m_UIModelLight;

        /// <summary>
        /// 预览点击命中时用来做屏幕坐标换算。框架启动后 UICamera 由 UIManager 持有，
        /// 这里惰性解析，避免 UIModelManager 初始化早于 UIManager 时拿到 null。
        /// </summary>
        public Camera UICamera
        {
            get
            {
                if (m_UICamera == null)
                {
                    var manager = UnityEngine.Object.FindObjectOfType<UIManager>();
                    if (manager != null) m_UICamera = manager.UICamera;
                }
                return m_UICamera;
            }
        }

        public Light UILight => m_UIModelLight;

        public override void OnInitialize()
        {
            base.OnInitialize();

            //m_UICamera = GameObject.Find("UICamera").GetOrAddComponent<Camera>();

            m_UIModelRoot = new GameObject("UIModelRoot").transform;
            m_UIModelRoot.SetParentEx(null);
            GameObject.DontDestroyOnLoad(m_UIModelRoot);

            m_UIModelLight = new GameObject("UIModelLight").GetOrAddComponent<Light>();
            m_UIModelLight.transform.SetParentEx(m_UIModelRoot);

            m_UIModelLight.transform.localEulerAngles = new Vector3(36, 180, 0);
            m_UIModelLight.type = LightType.Directional;
            m_UIModelLight.cullingMask = 1 << Layer.UI | 1 << Layer.UIRenderToTarget;
            m_UIModelLight.shadows = LightShadows.None;

            m_UIModelLight.gameObject.SetActive(false);
        }

        /// <summary>
        /// 加载一个模型到一张RawImage上
        /// </summary>
        public async UniTask LoadModelToRawImage(string path, RawImage rawImage, bool canDrag = true, Vector3 offset = default,
            Quaternion rot = default, Vector3 scale = default, bool isOrth = true, float orthSizeOrFOV = 1, 
            float cameraDistance = 20f, Action<UIRenderToTexture, GameObject> callback = null)
        {
            if (rawImage == null)
            {
                Debug.LogError("RawImage Is Null!");
                return;
            }

            rawImage.enabled = false;
            var go = await ResourceManager.Instance.InstantiateAsync(path);
            UnLoadModelByRawImage(rawImage);
            rawImage.enabled = true;
            LoadModelToRawImage(go, rawImage, canDrag, offset, rot, scale, isOrth, orthSizeOrFOV, cameraDistance, callback);
        }

        public void LoadModelToRawImage(GameObject go, RawImage rawImage, bool canDrag, Vector3 offset = default,
            Quaternion rot = default, Vector3 scale = default, bool isOrth = true, float orthSizeOrFOV = 1, 
            float cameraDistance = 20f, Action<UIRenderToTexture, GameObject> callback = null)
        {
            if (go != null)
            {
                UIRenderToTexture renderToTexture = rawImage.GetOrAddComponent<UIRenderToTexture>();
                Vector3 pos;
                int index = 0;
                if (renderToTexture.Targets == null || renderToTexture.Targets.Count == 0)
                {
                    index = m_IndexPool.Count > 0 ? m_IndexPool.Pop() : m_PoolCount++;
                }
                else
                {
                    index = renderToTexture.Index;
                }

                pos = new Vector3(100 * index, -10000, 0); 
                renderToTexture.Init(pos, isOrth, orthSizeOrFOV, index, cameraDistance);
                UpdateLight();

                go.SetLayerRecursively(Layer.UIRenderToTarget);
                go.transform.SetParent(m_UIModelRoot);
                go.transform.localPosition = pos + offset;
                go.transform.localScale = scale == default ? Vector3.one : scale;
                go.transform.rotation = rot;
                renderToTexture.AddTarget(go, canDrag);
                callback?.Invoke(renderToTexture, go);
            }
        }

        /// <summary>
        /// 卸载单个
        /// </summary>
        public void UnLoadModelByRawImage(RawImage rawImage, GameObject go)
        {
            if (rawImage != null && go != null)
            {
                UIRenderToTexture renderToTexture = rawImage.GetComponent<UIRenderToTexture>();
                int id = go.GetInstanceID();
                if (renderToTexture != null && renderToTexture.Targets != null && renderToTexture.Targets.ContainsKey(id))
                {
                    renderToTexture.Targets.Remove(id);
                    ResourceManager.Instance.Recycle(go);
                    if (renderToTexture.Targets.Count == 0)
                    {
                        m_IndexPool.Push(renderToTexture.Index);
                        renderToTexture.ResetTarget();
                    }
                    UpdateLight();
                }
            }
        }

        /// <summary>
        /// 卸载所有
        /// </summary>
        public void UnLoadModelByRawImage(RawImage rawImage, bool recycleGo = true)
        {
            if (rawImage != null)
            {
                UIRenderToTexture renderToTexture = rawImage.GetComponent<UIRenderToTexture>();
                if (renderToTexture != null && renderToTexture.Targets != null && renderToTexture.Targets.Count > 0)
                {
                    if (recycleGo)
                    {
                        foreach (var target in renderToTexture.Targets.Values)
                        {
                            if (target.gameObject == null) continue;
                            // 预览模型可能是业务侧自己 Instantiate 的（不走 ResourceManager 池）：
                            // 对这类实例直接 Destroy，避免 Recycle 打出“不是本模块实例化”的 error。
                            if (ResourceManager.Instance.IsManagedInstance(target.gameObject))
                                ResourceManager.Instance.Recycle(target.gameObject);
                            else
                                UnityEngine.Object.Destroy(target.gameObject);
                        }
                    }
                    m_IndexPool.Push(renderToTexture.Index);
                    renderToTexture.ResetTarget();
                    UpdateLight();
                }
            }
        }

        private void UpdateLight()
        {
            // 目标工程的角色使用"主光"（URP Lit / YSA Toon 都读 GetMainLight），
            // 这里额外生成的平行光不会成为主光，反而会在预览里叠一层附加光。
            // 预览统一使用场景主光（zhuye 的 Directional Light），因此常灯保持关闭。
            if (m_UIModelLight != null && m_UIModelLight.gameObject.activeSelf)
                m_UIModelLight.gameObject.SetActive(false);
        }

        public Camera SpawnCamera()
        {
            if (m_CameraPool.Count > 0)
            {
                Camera camera = m_CameraPool.Pop();
                camera.gameObject.SetActive(true);
                return camera;
            }
            else
            {
                GameObject go = new GameObject("CameraRTT");
                go.transform.SetParentEx(m_UIModelRoot);
                Camera camera = go.GetOrAddComponent<Camera>();
                camera.fieldOfView = 30;
                camera.allowHDR = false;
                camera.backgroundColor = Color.clear;
                camera.useOcclusionCulling = false;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.cullingMask = 1 << Layer.UIRenderToTarget;
                camera.farClipPlane = 30;
                camera.orthographicSize = 1;

                return camera;
            }
        }

        public void RecyleCamera(Camera camera)
        {
            if (camera != null)
            {
                camera.targetTexture = null;
                camera.gameObject.SetActive(false);
                m_CameraPool.Push(camera);
            }
        }
    }
}
