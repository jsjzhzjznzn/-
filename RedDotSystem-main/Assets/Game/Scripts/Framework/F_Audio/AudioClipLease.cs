namespace MieMieFrameWork
{
    using System;
    using System.Threading;
    using Cysharp.Threading.Tasks;
    using SkierFramework;
    using UnityEngine;
    using YooAsset;

    /// <summary>
    /// 单次音频请求持有的资源句柄（本项目后端 = SkierFramework.YooAssetService）
    /// 一个请求一个句柄，Dispose 即释放，不走 ResourceManager 的全局引用计数
    /// </summary>
    internal sealed class AudioClipLease : IDisposable
    {
        /// <summary>资源 location（完整资源路径）</summary>
        private readonly string location;

        /// <summary>本次加载独占的原生句柄</summary>
        private AssetHandle assetHandle;

        /// <summary>是否已经释放</summary>
        private bool disposed;

        /// <summary>接收 location，不在构造期间加载资源</summary>
        public AudioClipLease(string location)
        {
            this.location = location;
        }

        /// <summary>
        /// 同步加载音频；成功返回片段，失败释放本次句柄并抛出异常
        /// </summary>
        public AudioClip Load()
        {
            CheckLoadEntry();
            try
            {
                assetHandle = RequireHandle(YooAssetService.Instance.LoadAssetSync<AudioClip>(location));
                return GetClip();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>
        /// 异步加载音频；取消等待或加载失败时释放本次句柄
        /// </summary>
        public async UniTask<AudioClip> LoadAsync(CancellationToken cancellationToken)
        {
            CheckLoadEntry();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                assetHandle = RequireHandle(YooAssetService.Instance.LoadAssetAsync<AudioClip>(location));
                await UniTask.WaitUntil(() => disposed || assetHandle.IsDone, cancellationToken: cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (disposed)
                    throw new ObjectDisposedException(nameof(AudioClipLease));
                return GetClip();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>句柄为空 = 资源包未就绪或 location 不合法，立刻带地址报错</summary>
        private AssetHandle RequireHandle(AssetHandle handle)
        {
            if (handle == null)
                throw new InvalidOperationException($"[AudioClipLease] 资源包未就绪或地址不合法：{location}");
            return handle;
        }

        /// <summary>校验请求只加载一次，不允许复用已释放的持有记录</summary>
        private void CheckLoadEntry()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(AudioClipLease));
            if (assetHandle != null)
                throw new InvalidOperationException($"音频资源持有记录不能重复加载 {location}");
        }

        /// <summary>检查原生加载状态，返回片段或带地址的失败异常</summary>
        private AudioClip GetClip()
        {
            if (assetHandle.Status == EOperationStatus.Succeeded && assetHandle.AssetObject is AudioClip Clip)
                return Clip;
            throw new InvalidOperationException($"音频加载失败 地址 {location} 错误 {assetHandle.Error}");
        }

        /// <summary>释放本次资源持有，重复释放不影响其他请求</summary>
        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            if (assetHandle != null && assetHandle.IsValid)
                assetHandle.Release();
            assetHandle = null;
        }
    }
}
