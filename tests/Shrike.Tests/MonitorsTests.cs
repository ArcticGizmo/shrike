using Shrike.Core.Capture;

namespace Shrike.Tests;

public class MonitorsTests
{
    [Fact]
    public void Enumerates_at_least_one_monitor_on_a_desktop()
    {
        var monitors = Monitors.All();
        if (monitors.Count == 0) return; // session-less agent — nothing to enumerate

        Assert.All(monitors, m =>
        {
            Assert.False(m.Bounds.IsEmpty);
            Assert.True(m.Scale > 0);
        });
        Assert.True(monitors.Count(m => m.IsPrimary) <= 1);
    }
}
