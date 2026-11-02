namespace CompeteServer.Database
{
    /// <summary>
    /// 玩家级写锁（进程内按玩家ID串行化各仓库的写入口，纵深防御）
    /// 定位：正确性由"单事务 + FOR UPDATE 行锁"保证；本锁让同一玩家的并发写
    /// （本人会话 vs 跨玩家事件线程，如好友同意/邮件通知链）在进入事务前先排队，
    /// 减少行锁等待与死锁回滚。仅单进程有效，多进程部署时退化为纯 DB 行锁兜底。
    /// 约定：一次调用只锁一个玩家、不嵌套获取，因此不存在锁顺序死锁；
    /// 获取超时不阻断业务（照常放行，由 DB 行锁保证正确性）。
    /// </summary>
    public static class PlayerWriteLock
    {
        /// <summary>获取锁的最长等待（毫秒）：正常只等前一笔写事务落库完毕，超时说明存在异常长事务。</summary>
        private const int AcquireTimeoutMs = 10_000;

        /// <summary>按玩家ID登记的控制项；引用计数归零即回收，防止字典随在线玩家无限增长。</summary>
        private sealed class Entry
        {
            public Entry(int playerId) => PlayerId = playerId;

            public int PlayerId { get; }

            public SemaphoreSlim Semaphore { get; } = new(1, 1);

            /// <summary>持有 + 排队等待的本数（只在 Entries 锁内增减）。</summary>
            public int RefCount;
        }

        private static readonly Dictionary<int, Entry> Entries = new();

        /// <summary>空释放器：未持锁（超时放行）时返回，Dispose 无副作用。</summary>
        private static readonly IDisposable NoOp = new Releaser(null);

        /// <summary>获取玩家写锁，配合 using 使用；释放即放行下一本。</summary>
        /// <param name="playerId">参数名：playerId，类型：int，用途：被串行化的玩家ID</param>
        /// <returns>返回值类型：IDisposable，用途：释放句柄（using 结束即解锁）</returns>
        public static IDisposable Acquire(int playerId)
        {
            Entry? entry;
            lock (Entries)
            {
                if (!Entries.TryGetValue(playerId, out entry))
                {
                    entry = new Entry(playerId);
                    Entries[playerId] = entry;
                }
                entry.RefCount++;
            }

            if (!entry.Semaphore.Wait(AcquireTimeoutMs))
            {
                Console.WriteLine($"玩家写锁等待超时 [{playerId}]，本次改由数据库行锁兜底");
                ReleaseEntry(entry);
                return NoOp;
            }
            return new Releaser(entry);
        }

        /// <summary>释放信号量并撤销引用计数；计数归零时从字典回收条目。</summary>
        private static void ReleaseEntry(Entry entry)
        {
            lock (Entries)
            {
                entry.RefCount--;
                if (entry.RefCount == 0) Entries.Remove(entry.PlayerId);
            }
        }

        private sealed class Releaser : IDisposable
        {
            private Entry? entry;   // null = 空释放器（未持锁）

            public Releaser(Entry? entry) => this.entry = entry;

            public void Dispose()
            {
                Entry? current = Interlocked.Exchange(ref entry, null);
                if (current == null) return;
                current.Semaphore.Release();
                ReleaseEntry(current);
            }
        }
    }
}
