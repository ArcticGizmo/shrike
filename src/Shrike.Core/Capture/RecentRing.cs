namespace Shrike.Core.Capture;

/// <summary>One entry in the <see cref="RecentRing"/>: the full capture plus a small preview and an
/// identity handle for the UI surfaces to key off.</summary>
public sealed class RecentCapture
{
    public Guid Id { get; }

    /// <summary>The full-resolution capture, re-copyable / re-openable as taken.</summary>
    public CapturedImage Image { get; }

    /// <summary>A downscaled preview for the tray flyout and editor strip.</summary>
    public CapturedImage Thumbnail { get; }

    public DateTimeOffset CapturedAt => Image.CapturedAt;

    /// <summary>Memory footprint counted against the ring's byte cap (full image only; the thumbnail is tiny).</summary>
    public long Bytes => Image.Bgra.LongLength;

    internal RecentCapture(Guid id, CapturedImage image, CapturedImage thumbnail)
    {
        Id = id;
        Image = image;
        Thumbnail = thumbnail;
    }
}

/// <summary>
/// A bounded, in-memory list of the last few captures — the recent-captures ring (design §7 / G2).
/// Newest-first, capped by both count and total bytes, and cleared on quit (there is no disk spill in
/// v1). Pure state: headless-testable, no UI or toolkit dependency. Not thread-safe — the App drives
/// it from the UI thread.
/// </summary>
public sealed class RecentRing
{
    public const int DefaultMaxCount = 10;
    public const long DefaultMaxBytes = 512L * 1024 * 1024; // 512 MB ceiling before count usually binds

    private readonly List<RecentCapture> _items = []; // index 0 == newest
    private readonly int _thumbnailSize;

    public int MaxCount { get; }
    public long MaxBytes { get; }

    /// <summary>Raised after any mutation (add/remove/clear) so surfaces can refresh.</summary>
    public event Action? Changed;

    public RecentRing(int maxCount = DefaultMaxCount, long maxBytes = DefaultMaxBytes, int thumbnailSize = 160)
    {
        if (maxCount <= 0) throw new ArgumentOutOfRangeException(nameof(maxCount));
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        MaxCount = maxCount;
        MaxBytes = maxBytes;
        _thumbnailSize = thumbnailSize;
    }

    /// <summary>Newest-first snapshot of the ring's contents.</summary>
    public IReadOnlyList<RecentCapture> Items => _items;

    public int Count => _items.Count;

    public long TotalBytes
    {
        get
        {
            long total = 0;
            foreach (var item in _items) total += item.Bytes;
            return total;
        }
    }

    /// <summary>Push a capture onto the front of the ring, build its thumbnail, then evict to the caps.</summary>
    public RecentCapture Add(CapturedImage image)
    {
        var entry = new RecentCapture(Guid.NewGuid(), image, Thumbnail.Downscale(image, _thumbnailSize));
        _items.Insert(0, entry);
        EvictToCaps();
        Changed?.Invoke();
        return entry;
    }

    public bool Remove(RecentCapture item)
    {
        var removed = _items.Remove(item);
        if (removed) Changed?.Invoke();
        return removed;
    }

    public bool Remove(Guid id)
    {
        var index = _items.FindIndex(i => i.Id == id);
        if (index < 0) return false;
        _items.RemoveAt(index);
        Changed?.Invoke();
        return true;
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        Changed?.Invoke();
    }

    // Drop oldest entries until both caps are satisfied. The newest entry is always kept, even if it
    // alone exceeds the byte cap — evicting what the user just captured would be worse than the overage.
    private void EvictToCaps()
    {
        while (_items.Count > MaxCount)
            _items.RemoveAt(_items.Count - 1);

        while (_items.Count > 1 && TotalBytes > MaxBytes)
            _items.RemoveAt(_items.Count - 1);
    }
}
