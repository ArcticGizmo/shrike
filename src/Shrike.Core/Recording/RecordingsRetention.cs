namespace Shrike.Core.Recording;

/// <summary>How many working recordings to keep before evicting the oldest. Any one bound tripping evicts a
/// recording (the newest is always kept). Recordings are working sources — kept long enough to edit / re-export,
/// then reclaimed so the folder can't grow without bound.</summary>
public sealed record RecordingRetention(int MaxCount, long MaxBytes, TimeSpan MaxAge)
{
    public static RecordingRetention Default { get; } =
        new(MaxCount: 20, MaxBytes: 2L * 1024 * 1024 * 1024, MaxAge: TimeSpan.FromDays(14));
}

/// <summary>A recording file the retention pass reasons about (path + size + last-write time).</summary>
public readonly record struct RecordingFile(string Path, long SizeBytes, DateTimeOffset LastWriteUtc);

/// <summary>
/// Keeps the recordings working folder bounded. <see cref="ToEvict"/> is the pure, testable decision —
/// newest-first, the most recent recording always survives (it may be open in the editor), and everything
/// past the count / total-size / age bound is evicted. <see cref="Sweep"/> applies it to a real folder,
/// deleting each recording together with its sidecars (<c>*.track.json</c> and the <c>*.mic.wav</c> /
/// <c>*.sys.wav</c> audio) and clearing orphaned sidecars.
/// </summary>
public static class RecordingsRetention
{
    /// <summary>Decide which recordings to evict under <paramref name="policy"/> as of <paramref name="nowUtc"/>.</summary>
    public static IReadOnlyList<RecordingFile> ToEvict(
        IReadOnlyList<RecordingFile> files, RecordingRetention policy, DateTimeOffset nowUtc)
    {
        var ordered = files.OrderByDescending(f => f.LastWriteUtc).ToList();
        var evict = new List<RecordingFile>();
        long cumulative = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            var f = ordered[i];
            cumulative += f.SizeBytes;
            if (i == 0) continue; // always keep the most recent — it may be open in the editor
            if (i >= policy.MaxCount || cumulative > policy.MaxBytes || nowUtc - f.LastWriteUtc > policy.MaxAge)
                evict.Add(f);
        }
        return evict;
    }

    /// <summary>Apply <see cref="ToEvict"/> to <paramref name="directory"/>: delete evicted recordings + their
    /// sidecars, then remove any orphaned sidecar (a track with no recording). Best-effort; returns the number
    /// of recordings deleted. A file locked by the editor is skipped and retried on a later sweep.</summary>
    public static int Sweep(string directory, RecordingRetention policy, DateTimeOffset nowUtc)
    {
        if (!Directory.Exists(directory)) return 0;

        var files = new List<RecordingFile>();
        foreach (var mp4 in Directory.EnumerateFiles(directory, "*.mp4"))
        {
            try
            {
                var info = new FileInfo(mp4);
                files.Add(new RecordingFile(mp4, info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
            }
            catch { /* unreadable — skip it */ }
        }

        var deleted = 0;
        foreach (var f in ToEvict(files, policy, nowUtc))
        {
            if (TryDelete(f.Path)) deleted++;
            TryDelete(AppStorage.SidecarFor(f.Path)); // the track sidecar shares the recording's fate
            foreach (var suffix in AppStorage.AudioSidecarSuffixes) // audio sidecars go with it too
                TryDelete(Path.ChangeExtension(f.Path, suffix));
        }

        // Clear orphaned sidecars (a sidecar whose recording is gone).
        string[] suffixes = [".track.json", .. AppStorage.AudioSidecarSuffixes];
        foreach (var suffix in suffixes)
            foreach (var sidecar in Directory.EnumerateFiles(directory, "*" + suffix))
            {
                var name = Path.GetFileName(sidecar);
                if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                var mp4 = Path.Combine(directory, name[..^suffix.Length] + ".mp4");
                if (!File.Exists(mp4)) TryDelete(sidecar);
            }

        return deleted;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) { File.Delete(path); return true; }
        }
        catch { /* locked / permissions — a later sweep retries */ }
        return false;
    }
}
