namespace SkierFramework
{
    using System;
    using System.Collections.Generic;

    using UnityEngine;

    using UnityEngine.U2D;

    using YooAsset;

    /// <summary>
    /// 响应 Unity 的图集请求并从 ui 包加载 SpriteAtlas。
    ///
    /// 图集打包进 ui 包后，Sprite 加载时 Unity 会通过 SpriteAtlasManager.atlasRequested
    /// 索要所属图集；无人监听时 Unity 退回加载原始贴图，图集合批随之失效。
    /// 本类在自举时注册回调，按图集名（与 AddressByFileName 一致）从 ui 包同步加载并缓存句柄。
    /// </summary>
    public static class SpriteAtlasHotUpdateLoader
    {
        private static readonly Dictionary<string, SpriteAtlas> LoadedAtlases = new Dictionary<string, SpriteAtlas>(32);
        private static readonly List<AssetHandle> LoadHandles = new List<AssetHandle>(32);
        private static bool _registered;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            if (_registered) return;
            _registered = true;
            SpriteAtlasManager.atlasRequested += RequestAtlas;
            SpriteAtlasManager.atlasRegistered += OnAtlasRegistered;
        }

        private static void RequestAtlas(string atlasName, Action<SpriteAtlas> callback)
        {
            if (LoadedAtlases.TryGetValue(atlasName, out SpriteAtlas cached))
            {
                callback?.Invoke(cached);
                return;
            }

            SpriteAtlas atlas = LoadAtlas(atlasName);
            if (atlas == null)
            {
                // 未打进包里的图集（例如只存在于安装包的资产）交回 null，让 Unity 回退原图。
                callback?.Invoke(null);
                return;
            }

            LoadedAtlases[atlasName] = atlas;
            callback?.Invoke(atlas);
        }

        private static SpriteAtlas LoadAtlas(string atlasName)
        {
            YooAssetService service = YooAssetService.Instance;
            if (service == null || service.IsInitialized == false)
            {
                GameLog.Warn($"[图集] 资源后端未就绪，跳过图集 {atlasName}");
                return null;
            }

            AssetHandle handle = service.LoadAssetSync<SpriteAtlas>(atlasName);
            if (handle == null || handle.Status != EOperationStatus.Succeeded)
            {
                GameLog.Warn($"[图集] 加载失败 {atlasName}：{(handle == null ? "句柄为空" : handle.Error)}");
                handle?.Release();
                return null;
            }

            LoadHandles.Add(handle);
            return handle.AssetObject as SpriteAtlas;
        }

        private static void OnAtlasRegistered(SpriteAtlas atlas)
        {
            if (atlas == null) return;
            LoadedAtlases[atlas.name] = atlas;
        }

        /// <summary>释放全部图集句柄（关闭运行时由框架调用）。</summary>
        public static void ReleaseAll()
        {
            foreach (AssetHandle handle in LoadHandles)
            {
                handle.Release();
            }
            LoadHandles.Clear();
            LoadedAtlases.Clear();
        }
    }
}
