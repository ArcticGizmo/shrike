using Shrike.Core.Startup;

namespace Shrike.Tests;

public class StartupBudgetTests
{
    [Fact]
    public void Marks_are_recorded_and_snapshotted()
    {
        var budget = StartupBudget.Start();
        budget.Mark(StartupMarks.TrayReady);
        budget.Mark(StartupMarks.OverlayShown);

        var snapshot = budget.Snapshot();

        Assert.True(snapshot.ContainsKey(StartupMarks.TrayReady));
        Assert.True(snapshot.ContainsKey(StartupMarks.OverlayShown));
        Assert.All(snapshot.Values, ms => Assert.True(ms >= 0));
    }

    [Fact]
    public void ToJson_contains_recorded_marks()
    {
        var budget = StartupBudget.Start();
        budget.Mark(StartupMarks.TrayReady);

        var json = budget.ToJson();

        Assert.Contains(StartupMarks.TrayReady, json);
    }

    [Fact]
    public void Evaluate_flags_a_mark_over_budget()
    {
        var marks = new Dictionary<string, double>
        {
            [StartupMarks.TrayReady] = 120,
            [StartupMarks.OverlayShown] = 350,
        };
        var thresholds = new[]
        {
            new BudgetThreshold(StartupMarks.TrayReady, 400),
            new BudgetThreshold(StartupMarks.OverlayShown, 100),
        };

        var results = BudgetEvaluator.Evaluate(marks, thresholds);

        var tray = results.Single(r => r.Mark == StartupMarks.TrayReady);
        var overlay = results.Single(r => r.Mark == StartupMarks.OverlayShown);

        Assert.True(tray.Measured);
        Assert.True(tray.WithinBudget);
        Assert.True(overlay.Measured);
        Assert.False(overlay.WithinBudget);
    }

    [Fact]
    public void Evaluate_reports_a_missing_mark_as_unmeasured()
    {
        var marks = new Dictionary<string, double> { [StartupMarks.TrayReady] = 50 };

        var results = BudgetEvaluator.Evaluate(marks, BudgetEvaluator.ProvisionalTargets);

        var overlay = results.Single(r => r.Mark == StartupMarks.OverlayShown);
        Assert.False(overlay.Measured);
        Assert.False(overlay.WithinBudget);
        Assert.Null(overlay.ActualMs);
    }

    [Fact]
    public void Evaluate_preserves_threshold_order()
    {
        var results = BudgetEvaluator.Evaluate(new Dictionary<string, double>(), BudgetEvaluator.ProvisionalTargets);
        Assert.Equal(
            BudgetEvaluator.ProvisionalTargets.Select(t => t.Mark),
            results.Select(r => r.Mark));
    }
}
