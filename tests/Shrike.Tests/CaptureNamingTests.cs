using Shrike.Core.Capture;

namespace Shrike.Tests;

public class CaptureNamingTests
{
    private static readonly DateTimeOffset When = new(2026, 8, 12, 14, 39, 5, TimeSpan.Zero);

    [Fact]
    public void Default_template_expands_to_timestamped_stem()
    {
        Assert.Equal("shrike-20260812-143905", CaptureNaming.Expand(CaptureNaming.DefaultTemplate, When));
    }

    [Fact]
    public void Literal_text_outside_braces_is_preserved()
    {
        Assert.Equal("shot_2026 done", CaptureNaming.Expand("shot_{yyyy} done", When));
    }

    [Fact]
    public void Empty_template_falls_back_to_default()
    {
        Assert.Equal("shrike-20260812-143905", CaptureNaming.Expand("  ", When));
    }

    [Fact]
    public void Invalid_filename_characters_are_replaced()
    {
        var result = CaptureNaming.Expand("a/b:c", When);
        Assert.DoesNotContain('/', result);
        Assert.DoesNotContain(':', result);
    }
}
