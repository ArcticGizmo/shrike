using Shrike.Core.Recording;

namespace Shrike.Tests;

public class OneEuroFilterTests
{
    // A ramp with alternating +/- jitter on top, sampled at 60 Hz.
    private static (double[] t, double[] raw) JitterySignal(int count = 120, double noise = 6.0)
    {
        var t = new double[count];
        var raw = new double[count];
        for (var i = 0; i < count; i++)
        {
            t[i] = i / 60.0;
            raw[i] = i + (i % 2 == 0 ? noise : -noise);
        }
        return (t, raw);
    }

    private static double TotalVariation(IReadOnlyList<double> v)
    {
        var tv = 0.0;
        for (var i = 1; i < v.Count; i++) tv += Math.Abs(v[i] - v[i - 1]);
        return tv;
    }

    [Fact]
    public void First_sample_passes_through()
    {
        var f = new OneEuroFilter(minCutoff: 1.0, beta: 0.0);
        Assert.Equal(42.0, f.Filter(42.0, 0.0));
    }

    [Fact]
    public void Filtering_reduces_jitter()
    {
        var (t, raw) = JitterySignal();
        var f = new OneEuroFilter(minCutoff: 1.0, beta: 0.0);
        var outp = new double[raw.Length];
        for (var i = 0; i < raw.Length; i++) outp[i] = f.Filter(raw[i], t[i]);

        // The filtered path should wiggle far less than the raw one.
        Assert.True(TotalVariation(outp) < TotalVariation(raw) * 0.5,
            $"expected smoothing; raw TV={TotalVariation(raw):F1}, filtered TV={TotalVariation(outp):F1}");
    }

    [Fact]
    public void Lower_min_cutoff_smooths_more()
    {
        var (t, raw) = JitterySignal();

        double Tv(double minCutoff)
        {
            var f = new OneEuroFilter(minCutoff, beta: 0.0);
            var outp = new double[raw.Length];
            for (var i = 0; i < raw.Length; i++) outp[i] = f.Filter(raw[i], t[i]);
            return TotalVariation(outp);
        }

        // Monotonic control: a lower cutoff must smooth harder (less total variation).
        Assert.True(Tv(0.5) < Tv(2.0));
        Assert.True(Tv(2.0) < Tv(8.0));
    }
}
