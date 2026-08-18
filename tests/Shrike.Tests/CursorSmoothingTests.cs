using Shrike.Core.Recording;
using Shrike.Core.Settings;

namespace Shrike.Tests;

public class CursorSmoothingTests
{
    // ---- Smoothness ↔ 1€ params mapping ----

    [Fact]
    public void FromSmoothness_clamps_out_of_range()
    {
        // Below 0 behaves like 0; above 1 behaves like 1 — no throw, no runaway params.
        Assert.Equal(CursorSmoothing.FromSmoothness(0.0), CursorSmoothing.FromSmoothness(-0.5));
        Assert.Equal(CursorSmoothing.FromSmoothness(1.0), CursorSmoothing.FromSmoothness(2.0));
    }

    [Fact]
    public void Higher_smoothness_lowers_both_params_monotonically()
    {
        // "Smoother" means a lower min-cutoff (heavier low-pass) and a lower beta (less loosening on speed).
        CursorSmoothing? prev = null;
        for (var s = 0.0; s <= 1.0001; s += 0.1)
        {
            var cur = CursorSmoothing.FromSmoothness(s);
            if (prev is { } p)
            {
                Assert.True(cur.MinCutoff < p.MinCutoff, $"MinCutoff not decreasing at s={s:0.0}");
                Assert.True(cur.Beta < p.Beta, $"Beta not decreasing at s={s:0.0}");
            }
            prev = cur;
        }
    }

    [Fact]
    public void DefaultSmoothness_reproduces_the_shipped_default_look()
    {
        // The single knob is anchored so the default position lands on the shipped (0.8, 0.35) params,
        // keeping export behaviour unchanged for anyone who never touches the slider.
        var d = CursorSmoothing.FromSmoothness(CursorSmoothing.DefaultSmoothness);
        Assert.Equal(CursorSmoothing.Default.MinCutoff, d.MinCutoff, tolerance: 0.01);
        Assert.Equal(CursorSmoothing.Default.Beta, d.Beta, tolerance: 0.01);
    }

    [Fact]
    public void Smoothness_round_trips_through_FromSmoothness()
    {
        // The inverse (used to seed the slider from a persisted/Default value) recovers the knob position.
        for (var s = 0.0; s <= 1.0001; s += 0.05)
        {
            var back = CursorSmoothing.FromSmoothness(s).Smoothness;
            Assert.Equal(Math.Clamp(s, 0.0, 1.0), back, precision: 4);
        }
    }

    [Fact]
    public void Default_reports_its_knob_position()
    {
        Assert.Equal(CursorSmoothing.DefaultSmoothness, CursorSmoothing.Default.Smoothness, precision: 2);
    }

    // ---- persistence clamping ----

    [Fact]
    public void Settings_sanitise_clamps_cursor_tuning()
    {
        var wild = AppSettings.Default with { CursorSmoothness = 5.0, CursorSize = 9.0 };
        var s = wild.Sanitised();
        Assert.Equal(1.0, s.CursorSmoothness);
        Assert.Equal(2.0, s.CursorSize);

        var low = AppSettings.Default with { CursorSmoothness = -1.0, CursorSize = 0.1 };
        var t = low.Sanitised();
        Assert.Equal(0.0, t.CursorSmoothness);
        Assert.Equal(0.5, t.CursorSize);
    }
}
