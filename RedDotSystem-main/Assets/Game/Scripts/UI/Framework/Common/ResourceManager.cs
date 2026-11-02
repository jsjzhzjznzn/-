using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using YooAsset;

namespace SkierFramework
{
    /// <summary>
    /// 资源管理器（基于 YooAsset）
    /// - path 统一为 YooAsset location：完整资源路径（可带扩展名），如 "Assets/Game/HOTS/UI/Prefabs/Mainman.prefab"
    /// - 保留引用计数 / 对象池 / 常驻资源 / 缓存语义
    /// - 缓存句柄（AssetHandle）与缓存资产同生共死，卸载时统一 Release
    /// </summary>
    public class ResourceManager : MieMieFrameWork.SingletonService<ResourceManager>
    {
        /// <summary>
        /// 已加载资源路径对应的资源缓存
        /// </summary>
        private Dictionary<string, UnityEngine.Object> _assetCaches = new Dictionary<string, UnityEngine.Object>();
        /// <summary>
        /// 已加载资源路径对应的缓存句柄（与 _assetCaches 严格同步）
        /// </summary>
        private Dictionary<string, AssetHandle> _assetHandles = new Dictionary<string, AssetHandle>();
        /// <summary>
        /// 常驻内存中的资源路径哈希集
        /// </summary>
        private HashSet<string> _residentAssetsHashSet = new HashSet<string>();
        /// <summary>
        /// 调用清除时使用
        /// </summary>
        private HashSet<string> _clearAssetsSet = new HashSet<string>();
        /// <summary>
        /// 资源的引用个数
        /// </summary>
        private Dictionary<string, int> _loadedAssetInstanceCountDic = new Dictionary<string, int>();
        /// <summary>
        /// 已实例化对象对应的Key
        /// key: entityId
        /// value: path
        /// </summary>
        private Dictionary<int, string> _objectInstanceIdKeyDic = new Dictionary<int, string>();
        /// <summary>
        /// instancePool
        /// </summary>
        private InstancePool _instancePool;
        /// <summary>
        /// 真实加载中的资源数量
        /// </summary>
        private int _processingLoadCount = 0;

        public bool IsProcessLoading
        {
            get => _processingLoadCount > 0;
        }

        public override void OnInitialize()
        {
            base.OnInitialize();
            _instancePool = new InstancePool();
        }

        #region 初始化/清除
        /// <summary>
        /// 初始化 YooAsset（防重入）
        /// </summary>
        public async UniTask InitializeAsync(CancellationToken cancellationToken = default)
        {
            await YooAssetService.Instance.InitializeAsync(cancellationToken: cancellationToken);
        }

        /// <summary>
        /// 清除所有常驻资源之外的资源
        /// </summary>
        public async UniTask CleanupAsync(CancellationToken cancellationToken = default)
        {
            await UniTask.WaitUntil(() => !IsProcessLoading, cancellationToken: cancellationToken);
            Cleanup();
        }

        /// <summary>
        /// 清除所有常驻资源之外的资源
        /// </summary>
        public void Cleanup()
        {
            foreach (var item in _assetCaches)
            {
                if (!_residentAssetsHashSet.Contains(item.Key))
                {
                    _clearAssetsSet.Add(item.Key);
                }
            }
            foreach (var key in _clearAssetsSet)
            {
                if (_spriteCache.TryGetValue(key, out SpriteAtlas spriteAtlas))
                {
                    spriteAtlas.Cleanup();
                    _spriteCache.Remove(key);
                }

                ReleaseHandle(key);
                _assetCaches.Remove(key);
                _loadedAssetInstanceCountDic.Remove(key);
                _instancePool.Clear(key);
            }
            _clearAssetsSet.Clear();
            YooAssetService.Instance.UnloadUnusedAssets();
        }

        /// <summary>
        /// 增加常驻资源
        /// </summary>
        public void AddResidentAsset(string key)
        {
            _residentAssetsHashSet.Add(NormalizeToLocation(key));
        }
        #endregion

        #region 实例化和回收对象
        /// <summary>
        /// 实例化资源（异步；已加载过则同帧返回）
        /// </summary>
        public async UniTask<GameObject> InstantiateAsync(string path, bool active = true)
        {
            if (string.IsNullOrEmpty(path)) return null;

            string location = NormalizeToLocation(path);
            if (!_assetCaches.ContainsKey(location))
            {
                var asset = await LoadAssetAsyncInternalAsync<GameObject>(location);
                if (asset == null)
                {
                    Debug.LogErrorFormat("[InstantiateAsync] 加载失败：{0}！", path);
                    return null;
                }
            }
            return InternalInstantiate(location, active);
        }

        /// <summary>
        /// 判断对象是否由本模块实例化（决定销毁时走回收计数还是直接 Destroy）
        /// </summary>
        public bool IsManagedInstance(UnityEngine.GameObject instanceObject)
        {
            return instanceObject != null && _objectInstanceIdKeyDic.ContainsKey(instanceObject.GetInstanceID());
        }

        public void Recycle(UnityEngine.GameObject instanceObject, bool forceDestroy = false)
        {
            if (instanceObject == null)
            {
                return;
            }

            int id = instanceObject.GetInstanceID();
            if (_objectInstanceIdKeyDic.TryGetValue(id, out string path))
            {
                _instancePool.Recycle(path, instanceObject, forceDestroy);
                if (_loadedAssetInstanceCountDic.TryGetValue(path, out int count))
                {
                    _loadedAssetInstanceCountDic[path] = count - 1;
                }
                _objectInstanceIdKeyDic.Remove(id);
            }
            else
            {
                Debug.LogErrorFormat("此模块不回收不是从这个模块实例化出去的对象：{0}", instanceObject.name);
                GameObject.Destroy(instanceObject);
            }
        }

        /// <summary>
        /// 实例化
        /// </summary>
        private GameObject InternalInstantiate(string location, bool active = true)
        {
            GameObject result = _instancePool.Get(location);
            GameObject invokeResult = null;

            if (result == null)
            {
                if (_assetCaches.TryGetValue(location, out UnityEngine.Object asset) && asset != null)
                {
                    invokeResult = GameObject.Instantiate(asset as GameObject);
                }
            }
            else
            {
                invokeResult = result;
            }

            if (invokeResult != null)
            {
                _instancePool.InitInst(invokeResult, active);
                _objectInstanceIdKeyDic[invokeResult.GetInstanceID()] = location;
                if (_loadedAssetInstanceCountDic.TryGetValue(location, out int count))
                {
                    _loadedAssetInstanceCountDic[location] = count + 1;
                }
                else
                {
                    _loadedAssetInstanceCountDic[location] = 1;
                }
            }
            return invokeResult;
        }
        #endregion

        #region 资源加载/卸载   
        /// <summary>
        /// 加载资源（异步；已加载则走缓存同帧返回）
        /// </summary>
        public async UniTask<T> LoadAssetAsync<T>(string path) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(path)) return null;

            string location = NormalizeToLocation(path);
            var asset = await LoadAssetAsyncInternalAsync<T>(location);
            if (asset == null)
            {
                Debug.LogErrorFormat("[LoadAssetAsync] {0} 加载失败！", path);
            }
            return asset;
        }

        /// <summary>
        /// 加载并缓存资源（缓存命中同步返回；句柄与缓存同生共死）
        /// </summary>
        private async UniTask<T> LoadAssetAsyncInternalAsync<T>(string location) where T : UnityEngine.Object
        {
            if (_assetCaches.TryGetValue(location, out UnityEngine.Object cached))
            {
                return cached as T;
            }

            if (!YooAssetService.Instance.IsInitialized)
            {
                Debug.LogErrorFormat("[LoadAssetAsync] {0} 加载失败：YooAsset 尚未初始化！", location);
                return null;
            }

            _processingLoadCount++;
            try
            {
                var handle = YooAssetService.Instance.LoadAssetAsync<T>(location);
                if (handle == null)
                {
                    Debug.LogErrorFormat("[LoadAssetAsync] {0} 加载失败：资源包未初始化或 location 不合法！", location);
                    return null;
                }
                await handle.ToUniTask();
                if (handle.Status == EOperationStatus.Succeeded && handle.AssetObject != null)
                {
                    if (_assetCaches.TryGetValue(location, out UnityEngine.Object other))
                    {
                        // 并发加载：另一请求先完成并已缓存，本句柄立即自释放
                        if (handle.IsValid) handle.Release();
                        return other as T;
                    }
                    _assetCaches[location] = handle.AssetObject;
                    _assetHandles[location] = handle;
                    return handle.AssetObject as T;
                }

                Debug.LogErrorFormat("[LoadAssetAsync] {0} 加载失败！{1}", location, handle.Error);
                if (handle.IsValid) handle.Release();
                return null;
            }
            finally
            {
                _processingLoadCount--;
            }
        }

        /// <summary>
        /// 释放缓存句柄（与 _assetCaches 同步移除）
        /// </summary>
        private void ReleaseHandle(string location)
        {
            if (_assetHandles.TryGetValue(location, out AssetHandle handle))
            {
                if (handle.IsValid)
                {
                    handle.Release();
                }
                _assetHandles.Remove(location);
            }
        }

        /// <summary>
        /// 直接卸载资源
        /// </summary>
        public void UnLoadAsset(string path)
        {
            string location = NormalizeToLocation(path);

            //判断卸载是否是一个常驻资源
            if (_residentAssetsHashSet.Contains(location))
            {
                Debug.LogErrorFormat("[UnLoadAsset] 禁止卸载常驻资源：{0} ！", path);
                return;
            }

            if (_assetCaches.ContainsKey(location))
            {
                Debug.Log(string.Format("[UnLoadAsset] 卸载资源：{0} ！", path));

                if (_spriteCache.TryGetValue(location, out SpriteAtlas spriteAtlas))
                {
                    spriteAtlas.Cleanup();
                    _spriteCache.Remove(location);
                }
                ReleaseHandle(location);
                _assetCaches.Remove(location);
                _loadedAssetInstanceCountDic.Remove(location);
                YooAssetService.Instance.TryUnloadUnusedAsset(location);
            }
            else
            {
                Debug.LogErrorFormat("[UnLoadAsset] 卸载未加载资源：{0} ！", path);
            }
        }

        /// <summary>
        /// 释放资源引用，当引用数为0时 自动卸载
        /// </summary>
        public void ReleaseRef(string path)
        {
            string location = NormalizeToLocation(path);
            if (_loadedAssetInstanceCountDic.TryGetValue(location, out int count))
            {
                if (count <= 0)
                {
                    Debug.LogErrorFormat("[ReleaseRef] 引用计数已为 0，疑似重复释放：{0}！", path);
                    return;
                }
                _loadedAssetInstanceCountDic[location] = --count;
                if (count == 0)
                {
                    UnLoadAsset(location);
                }
            }
        }
        #endregion

        #region 预加载/缓存
        public async UniTask<T> PreLoadAssetAsync<T>(string path) where T : UnityEngine.Object
        {
            return await LoadAssetAsync<T>(path);
        }

        public bool TryGetAsset<T>(string path, out T target) where T : UnityEngine.Object
        {
            target = null;
            string location = NormalizeToLocation(path);
            if (_assetCaches.TryGetValue(location, out UnityEngine.Object cached))
            {
                target = cached as T;
                return target != null;
            }

            if (!YooAssetService.Instance.IsInitialized)
            {
                return false;
            }

            var handle = YooAssetService.Instance.LoadAssetSync<T>(location);
            if (handle.Status == EOperationStatus.Succeeded && handle.AssetObject != null)
            {
                _assetCaches[location] = handle.AssetObject;
                _assetHandles[location] = handle;
                if (!_loadedAssetInstanceCountDic.ContainsKey(location))
                {
                    _loadedAssetInstanceCountDic.Add(location, 1);
                }
                target = handle.AssetObject as T;
                return target != null;
            }

            if (handle.IsValid) handle.Release();
            return false;
        }
        #endregion

        #region 图片加载
        /// <summary>
        /// SpriteAtlas.GetSprite()会clone一份，不会重复使用 因此需要缓存
        /// </summary>
        private class SpriteAtlas
        {
            public UnityEngine.U2D.SpriteAtlas spriteAtlas;
            private Dictionary<string, Sprite> _spriteCache = new Dictionary<string, Sprite>();

            public Sprite Get(string name)
            {
                if (!_spriteCache.TryGetValue(name, out Sprite sprite))
                {
                    sprite = spriteAtlas.GetSprite(name);
                    _spriteCache.Add(name, sprite);
                }
                return sprite;
            }

            public void Cleanup()
            {
                foreach (var sprite in _spriteCache.Values)
                {
                    GameObject.Destroy(sprite);
                }
                _spriteCache.Clear();
            }
        }
        private Dictionary<string, SpriteAtlas> _spriteCache = new Dictionary<string, SpriteAtlas>();
        public async UniTask<UnityEngine.Sprite> LoadSpriteAsync(string atlasPath, string spriteName)
        {
            if (string.IsNullOrEmpty(atlasPath) || string.IsNullOrEmpty(spriteName))
            {
                Debug.LogErrorFormat("[LoadSpriteAsync] error：atlasPath = {0}, spriteName = {1}！", atlasPath, spriteName);
                return null;
            }

            string location = NormalizeToLocation(atlasPath);
            SpriteAtlas atlas = null;
            if (_spriteCache.TryGetValue(location, out atlas))
            {
                return atlas.Get(spriteName);
            }

            var obj = await LoadAssetAsync<UnityEngine.U2D.SpriteAtlas>(location);
            if (obj == null)
            {
                Debug.LogErrorFormat("[LoadSpriteAsync] load failed：atlasPath = {0}！", atlasPath);
                return null;
            }
            if (_spriteCache.TryGetValue(location, out atlas))
            {
                return atlas.Get(spriteName);
            }
            atlas = new SpriteAtlas { spriteAtlas = obj };
            _spriteCache.Add(location, atlas);
            return atlas.Get(spriteName);
        }
        #endregion

        #region 场景加载
        /// <summary>
        /// 已加载场景句柄（key 为归一化 location，卸载场景时统一归还引用计数）
        /// </summary>
        private Dictionary<string, YooAsset.SceneHandle> _sceneHandles = new Dictionary<string, YooAsset.SceneHandle>();

        public async UniTask LoadSceneAsync(string name, LoadSceneMode loadMode = LoadSceneMode.Single)
        {
            if (!YooAssetService.Instance.IsInitialized)
            {
                return;
            }

            string location = NormalizeToLocation(name);
            if (loadMode == LoadSceneMode.Single)
            {
                // 单场景模式：旧场景即将被 Unity 替换卸载，一并归还已跟踪句柄
                ReleaseAllSceneHandles();
            }
            var handle = YooAssetService.Instance.LoadSceneAsync(location, loadMode);
            await handle.ToUniTask();
            if (handle.IsValid && handle.Status == EOperationStatus.Succeeded)
            {
                if (_sceneHandles.TryGetValue(location, out YooAsset.SceneHandle oldHandle) && oldHandle != handle)
                {
                    oldHandle.Release();
                }
                _sceneHandles[location] = handle;
            }
            else
            {
                Debug.LogErrorFormat("[LoadSceneAsync] 场景加载失败：{0}！{1}", location, handle.Error);
                if (handle.IsValid) handle.Release();
            }
        }

        /// <summary>
        /// 释放指定场景的句柄（场景不再需要时主动归还 YooAsset 引用计数）
        /// </summary>
        public void ReleaseSceneHandle(Scene scene)
        {
            ReleaseSceneHandleByName(scene.name);
        }

        public async UniTask UnloadSceneAsync(Scene scene)
        {
            string sceneName = scene.name;
            AsyncOperation op = SceneManager.UnloadSceneAsync(scene);
            if (op != null)
            {
                await op.ToUniTask();
            }
            ReleaseSceneHandleByName(sceneName);
        }

        private void ReleaseAllSceneHandles()
        {
            if (_sceneHandles.Count == 0) return;
            foreach (var handle in _sceneHandles.Values)
            {
                if (handle != null && handle.IsValid)
                {
                    handle.Release();
                }
            }
            _sceneHandles.Clear();
        }

        private void ReleaseSceneHandleByName(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return;
            foreach (var pair in _sceneHandles)
            {
                if (pair.Value != null && pair.Value.IsValid && pair.Value.SceneName == sceneName)
                {
                    pair.Value.Release();
                    _sceneHandles.Remove(pair.Key);
                    return;
                }
            }
        }
        #endregion

        #region Text读取
        public async UniTask<string> ReadTextStringAsync(string path)
        {
            var obj = await LoadAssetAsync<UnityEngine.TextAsset>(path);
            if (obj == null)
            {
                Debug.LogErrorFormat("[ReadTextStreamAsync] load failed：path = {0}！", path);
                return string.Empty;
            }

            return obj.text;
        }

        public async UniTask<byte[]> ReadTextBytesAsync(string path)
        {
            var obj = await LoadAssetAsync<UnityEngine.TextAsset>(path);
            if (obj == null)
            {
                Debug.LogErrorFormat("[ReadTextStreamAsync] load failed：path = {0}！", path);
                return null;
            }

            byte[] bytes = obj.bytes;
            UnLoadAsset(NormalizeToLocation(path));
            return bytes;
        }

        public byte[] ReadTextBytes(string path)
        {
            if (!YooAssetService.Instance.IsInitialized)
            {
                return null;
            }

            string location = NormalizeToLocation(path);
            var handle = YooAssetService.Instance.LoadAssetSync<TextAsset>(location);
            if (handle.Status == EOperationStatus.Succeeded && handle.AssetObject != null)
            {
                var text = handle.AssetObject as TextAsset;
                byte[] result = text.bytes;
                handle.Release();
                return result;
            }

            if (handle.IsValid) handle.Release();
            Debug.LogErrorFormat("[ReadTextStreamAsync] load failed：path = {0}！", path);
            return null;
        }
        #endregion

        #region Debug
        public void PrintState()
        {
            foreach (var item in _loadedAssetInstanceCountDic)
            {
                Debug.LogFormat("Asset Key: {0}, Count: {1}", item.Key, item.Value);
            }
        }
        #endregion

        #region 路径归一化
        /// <summary>
        /// 统一转成 YooAsset location：
        /// "Assets/Game/HOTS/UI/Prefabs/Mainman.prefab" → "Assets/Game/HOTS/UI/Prefabs/Mainman"
        /// 兼容旧适配版的 "Assets/Game/Resources/xxx" 前缀；其余相对路径原样保留（Addressable 模式下可用）
        /// </summary>
        private static string NormalizeToLocation(string path)
        {
            path = path.Trim().Replace((char)92, '/');
            if (!path.StartsWith("Assets/") && !path.StartsWith("Packages/"))
            {
                // 兼容旧 Resources 相对写法：GameUI/x → Assets/Game/UI/x
                path = "Assets/" + path;
            }
            const string resPrefix = "Assets/Game/Resources/";
            if (path.StartsWith(resPrefix))
            {
                path = path.Substring(resPrefix.Length);
            }
            int extIndex = path.LastIndexOf('.');
            int slashIndex = path.LastIndexOf('/');
            if (extIndex > slashIndex)
            {
                path = path.Substring(0, extIndex);
            }
            return path;
        }
        #endregion
    }
}
