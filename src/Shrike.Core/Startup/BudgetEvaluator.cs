namespace Shrike.Core.Startup;

/// <summary>A single snappy-load target: a named mark must land at or under <see cref="LimitMs"/>.</summary>
public sealed record BudgetThreshold(string Mark, double LimitMs);

/// <summary>The outcome of comparing one measured mark against its threshold.</summary>
public sealed record BudgetResult(string Mark, double? ActualMs, double LimitMs)
{
    /// <summary>False when the mark was never recorded (e.g. the overlay was not shown this run).</summary>
    public bool Measured => ActualMs.HasValue;

    /// <summary>True only when a value was measured and it is within budget.</summary>
    public bool WithinBudget => ActualMs is { } actual && actual <= LimitMs;
}

/// <summary>
/// Pure comparison of recorded marks against thresholds. Decision #3 from review: in M0 these
/// thresholds are <b>measured, not hard-enforced</b> — the CI hook reports them but does not fail
/// the build. The numbers are tuned to a real baseline once the app sees daily use.
/// </summary>
public static class BudgetEvaluator
{
    /// <summary>Provisional M0 targets. Placeholders until a real baseline is measured.</summary>
    public static readonly IReadOnlyList<BudgetThreshold> ProvisionalTargets =
    [
        new BudgetThreshold(StartupMarks.TrayReady, 400),
        new BudgetThreshold(StartupMarks.OverlayShown, 100),
    ];

    /// <summary>Compare each threshold against the recorded marks, preserving threshold order.</summary>
    public static IReadOnlyList<BudgetResult> Evaluate(
        IReadOnlyDictionary<string, double> marks,
        IReadOnlyList<BudgetThreshold> thresholds)
        => thresholds
            .Select(t => new BudgetResult(
                t.Mark,
                marks.TryGetValue(t.Mark, out var actual) ? actual : null,
                t.LimitMs))
            .ToList();
}
