using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Shrike.Core.Startup;

/// <summary>
/// Well-known startup mark names. Kept as string keys so the JSON diagnostics output is stable
/// and the budget test can assert against them by name.
/// </summary>
public static class StartupMarks
{
    /// <summary>Tray icon is up and the app is idle-ready to service a hotkey.</summary>
    public const string TrayReady = "tray_ready_ms";

    /// <summary>The capture overlay window has been shown for the first time.</summary>
    public const string OverlayShown = "overlay_shown_ms";
}

/// <summary>
/// Records elapsed-time marks from a single monotonic clock started as early as possible in
/// <c>Main</c>. This is the timing side of the snappy-load gate; evaluation against thresholds is
/// a separate pure step (<see cref="BudgetEvaluator"/>) so it can be unit-tested without a clock.
/// </summary>
public sealed class StartupBudget
{
    private readonly Stopwatch _clock;
    private readonly ConcurrentDictionary<string, double> _marks = new();

    private StartupBudget(Stopwatch clock) => _clock = clock;

    /// <summary>Start the budget clock now. Call this on the very first line of <c>Main</c>.</summary>
    public static StartupBudget Start() => new(Stopwatch.StartNew());

    /// <summary>Record the elapsed milliseconds since start under <paramref name="name"/>.</summary>
    public void Mark(string name) => _marks[name] = _clock.Elapsed.TotalMilliseconds;

    /// <summary>Elapsed milliseconds since the clock started (without recording a mark).</summary>
    public double ElapsedMs => _clock.Elapsed.TotalMilliseconds;

    /// <summary>An immutable snapshot of the marks recorded so far.</summary>
    public IReadOnlyDictionary<string, double> Snapshot() => new Dictionary<string, double>(_marks);

    /// <summary>Serialise the marks to pretty JSON for the <c>measure-startup</c> diagnostic.</summary>
    public string ToJson()
        => JsonSerializer.Serialize(
            new Dictionary<string, double>(_marks),
            new JsonSerializerOptions { WriteIndented = true });
}
