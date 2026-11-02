namespace MieMieFrameWork
{
    using System;
    using System.Collections.Generic;
    using UnityEngine;

    /// <summary>
    /// 特效音播放器池（本项目没有通用 PoolManager，音频自带一个最小池）
    /// 语义与源框架 PoolHandle 对齐：租借版本号 + 归还校验，旧租约不能操作复用后的播放器
    /// </summary>
    internal sealed class EffectPlayerPool : IDisposable
    {
        /// <summary>单个池实例</summary>
        private sealed class Entry
        {
            public AudioSource Source;
            public ulong Version;
            public bool InUse;
            public bool Discarded;
        }

        private readonly GameObject prefab;
        private readonly Transform root;
        private readonly int maxCount;
        private readonly List<Entry> entries = new List<Entry>();
        private readonly Dictionary<AudioSource, Entry> entryDict = new Dictionary<AudioSource, Entry>();

        private ulong nextVersion;
        private bool disposed;

        /// <summary>当前借出数量（诊断用）</summary>
        public int ActiveCount
        {
            get
            {
                int Count = 0;
                for (int Index = 0; Index < entries.Count; Index++)
                    if (entries[Index].InUse)
                        Count++;
                return Count;
            }
        }

        public EffectPlayerPool(GameObject prefab, Transform root, int maxCount)
        {
            this.prefab = prefab;
            this.root = root;
            this.maxCount = Mathf.Max(1, maxCount);
        }

        /// <summary>预热：提前实例化并置为非激活</summary>
        public void Prewarm(int count)
        {
            for (int Index = 0; Index < count && entries.Count < maxCount; Index++)
                CreateEntry();
        }

        /// <summary>
        /// 借出一个播放器；池满返回 null（调用方按"请求被拒绝"处理）
        /// </summary>
        public T Get<T>(Transform parent = null, bool activate = false, Action<T> configure = null) where T : Component
        {
            if (disposed)
                return null;

            Entry Found = null;
            for (int Index = 0; Index < entries.Count; Index++)
            {
                if (!entries[Index].InUse && !entries[Index].Discarded)
                {
                    Found = entries[Index];
                    break;
                }
            }

            if (Found == null)
            {
                if (entries.Count >= maxCount)
                    return null;
                Found = CreateEntry();
            }

            Found.InUse = true;
            Found.Version = ++nextVersion;

            if (parent != null && Found.Source.transform.parent != parent)
                Found.Source.transform.SetParent(parent, false);

            var Target = Found.Source as T;
            if (Target == null)
            {
                Found.InUse = false;
                return null;
            }

            Found.Source.gameObject.SetActive(activate);
            configure?.Invoke(Target);
            return Target;
        }

        /// <summary>取当前租借版本，用于后续校验</summary>
        public ulong GetLeaseVersion(Component source)
        {
            var Audio = source as AudioSource;
            if (Audio == null || !entryDict.TryGetValue(Audio, out var Entry))
                return 0;
            return Entry.Version;
        }

        /// <summary>租约是否仍然有效（池里、未被丢弃、仍在借出、版本一致）</summary>
        public bool IsLeaseValid(Component source, ulong leaseVersion)
        {
            if (disposed)
                return false;
            var Audio = source as AudioSource;
            if (Audio == null)
                return false;
            if (!entryDict.TryGetValue(Audio, out var Entry))
                return false;
            return !Entry.Discarded && Entry.InUse && Entry.Version == leaseVersion;
        }

        /// <summary>按租借版本归还，版本不一致直接失败（不误伤新租约）</summary>
        public bool TryRelease(Component source, ulong leaseVersion)
        {
            if (!IsLeaseValid(source, leaseVersion))
                return false;

            var Audio = (AudioSource)source;
            var Entry = entryDict[Audio];
            Entry.InUse = false;
            Entry.Version = ++nextVersion;
            Audio.Stop();
            Audio.clip = null;
            Audio.gameObject.SetActive(false);
            return true;
        }

        /// <summary>归还失败时丢弃该实例，避免脏对象继续被借出</summary>
        public void DiscardAfterFailedRelease(GameObject instance, ulong leaseVersion)
        {
            if (instance == null)
                return;
            var Audio = instance.GetComponent<AudioSource>();
            if (Audio == null || !entryDict.TryGetValue(Audio, out var Entry))
            {
                UnityEngine.Object.Destroy(instance);
                return;
            }

            Entry.Discarded = true;
            Entry.InUse = false;
            entryDict.Remove(Audio);
            entries.Remove(Entry);
            UnityEngine.Object.Destroy(instance);
        }

        private Entry CreateEntry()
        {
            var Instance = UnityEngine.Object.Instantiate(prefab, root);
            Instance.name = prefab.name + "_Effect";
            var Audio = Instance.GetComponent<AudioSource>();
            if (Audio == null)
            {
                UnityEngine.Object.Destroy(Instance);
                throw new InvalidOperationException("[EffectPlayerPool] 特效播放器预制体上没有 AudioSource");
            }

            Instance.SetActive(false);
            var Entry = new Entry { Source = Audio, Version = 0, InUse = false };
            entries.Add(Entry);
            entryDict.Add(Audio, Entry);
            return Entry;
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            for (int Index = 0; Index < entries.Count; Index++)
            {
                var Source = entries[Index].Source;
                if (Source != null)
                    UnityEngine.Object.Destroy(Source.gameObject);
            }
            entries.Clear();
            entryDict.Clear();
        }
    }
}
