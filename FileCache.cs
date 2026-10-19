using System.Collections.Concurrent;
using System.Runtime;

namespace ImageViewer;

/// <summary>
/// In-memory cache of raw image files (still compressed, so it is compact). The folder is
/// preloaded in the background around the current image, which hides the latency of slow
/// drives (network, Google Drive). Entries expire 10 minutes after their last use.
/// </summary>
sealed class FileCache : IDisposable
{
    const long Budget = 512L * 1024 * 1024;
    const long MaxFileSize = 128L * 1024 * 1024;
    static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    sealed class Item(byte[] data)
    {
        public byte[] Data { get; } = data;
        public long LastAccess = Environment.TickCount64;
        public void Touch() => Interlocked.Exchange(ref LastAccess, Environment.TickCount64);
    }

    readonly ConcurrentDictionary<string, Item> _items = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> _inflight = new(StringComparer.OrdinalIgnoreCase);
    readonly object _sizeLock = new();
    readonly Timer _sweepTimer;
    long _total;
    CancellationTokenSource? _preloadCts;

    public FileCache() => _sweepTimer = new Timer(_ => Sweep(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

    /// <summary>Returns the file's bytes, from the cache or from disk (concurrent requests share one read).</summary>
    public Task<byte[]> GetAsync(string path)
    {
        if (_items.TryGetValue(path, out var item))
        {
            item.Touch();
            return Task.FromResult(item.Data);
        }
        var lazy = _inflight.GetOrAdd(path, p => new Lazy<Task<byte[]>>(() => Task.Run(async () =>
        {
            try
            {
                var data = await File.ReadAllBytesAsync(p);
                Add(p, data);
                return data;
            }
            finally
            {
                _inflight.TryRemove(p, out _);
            }
        })));
        return lazy.Value;
    }

    public byte[] Get(string path) => GetAsync(path).GetAwaiter().GetResult();

    public void Remove(string path)
    {
        if (_items.TryRemove(path, out var item))
            lock (_sizeLock) _total -= item.Data.Length;
    }

    /// <summary>
    /// Reads the folder's files into the cache, nearest to <paramref name="index"/> first
    /// (next, previous, next+1, ...). Restarting it with a new index just reorders the work.
    /// Stops when the budget is full instead of evicting images closer to the current one.
    /// </summary>
    public void Preload(IReadOnlyList<string> files, int index)
    {
        _preloadCts?.Cancel();
        if (files.Count < 2 || index < 0) return;
        var cts = _preloadCts = new CancellationTokenSource();
        var order = new List<string>(files.Count);
        for (int d = 1; d < files.Count && order.Count < files.Count - 1; d++)
        {
            order.Add(files[(index + d) % files.Count]);
            if (order.Count < files.Count - 1) order.Add(files[((index - d) % files.Count + files.Count) % files.Count]);
        }

        Task.Run(async () =>
        {
            foreach (var path in order.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (cts.IsCancellationRequested) return;
                if (_items.TryGetValue(path, out _)) continue;
                lock (_sizeLock) if (_total >= Budget * 9 / 10) return;
                try { await GetAsync(path); }
                catch { /* unreadable: shown as an error when opened */ }
            }
        });
    }

    void Add(string path, byte[] data)
    {
        if (data.Length > MaxFileSize) return;
        var item = new Item(data);
        lock (_sizeLock)
        {
            if (_items.TryGetValue(path, out var old)) _total -= old.Data.Length;
            _items[path] = item;
            _total += data.Length;

            // Over budget: drop the least recently used files.
            while (_total > Budget)
            {
                var oldest = _items.Where(kv => kv.Key != path).OrderBy(kv => kv.Value.LastAccess).FirstOrDefault();
                if (oldest.Key == null || !_items.TryRemove(oldest.Key, out var removed)) break;
                _total -= removed.Data.Length;
            }
        }
    }

    void Sweep()
    {
        long cutoff = Environment.TickCount64 - (long)Ttl.TotalMilliseconds;
        long freed = 0;
        lock (_sizeLock)
        {
            foreach (var kv in _items)
            {
                if (kv.Value.LastAccess < cutoff && _items.TryRemove(kv.Key, out var removed))
                {
                    _total -= removed.Data.Length;
                    freed += removed.Data.Length;
                }
            }
        }
        // The buffers live on the large object heap; give the memory back to Windows promptly.
        if (freed > 32 * 1024 * 1024)
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
        }
    }

    public void Dispose()
    {
        _preloadCts?.Cancel();
        _sweepTimer.Dispose();
    }
}
