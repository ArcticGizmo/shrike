using Shrike.Core.Ipc;

namespace Shrike.Tests;

public class IpcProtocolTests
{
    [Theory]
    [InlineData(CaptureAction.ShowOverlay)]
    [InlineData(CaptureAction.CaptureRegion)]
    [InlineData(CaptureAction.StartRecording)]
    [InlineData(CaptureAction.ShowSettings)]
    public void Format_then_parse_roundtrips(CaptureAction action)
    {
        Assert.True(IpcProtocol.TryParse(IpcProtocol.Format(action), out var parsed));
        Assert.Equal(action, parsed);
    }

    [Fact]
    public void Parse_is_case_insensitive_and_trims()
    {
        Assert.True(IpcProtocol.TryParse("  showoverlay  ", out var action));
        Assert.Equal(CaptureAction.ShowOverlay, action);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-action")]
    public void Parse_rejects_garbage(string? line)
    {
        Assert.False(IpcProtocol.TryParse(line, out _));
    }

    [Theory]
    [InlineData(new[] { "--region" }, CaptureAction.CaptureRegion)]
    [InlineData(new[] { "/window" }, CaptureAction.CaptureWindow)]
    [InlineData(new[] { "--full" }, CaptureAction.CaptureFullScreen)]
    [InlineData(new[] { "record" }, CaptureAction.StartRecording)]
    [InlineData(new[] { "--settings" }, CaptureAction.ShowSettings)]
    public void ActionFromArgs_maps_known_verbs(string[] args, CaptureAction expected)
    {
        Assert.Equal(expected, IpcProtocol.ActionFromArgs(args));
    }

    [Fact]
    public void ActionFromArgs_defaults_to_show_overlay_for_empty_or_unknown()
    {
        Assert.Equal(CaptureAction.ShowOverlay, IpcProtocol.ActionFromArgs(Array.Empty<string>()));
        Assert.Equal(CaptureAction.ShowOverlay, IpcProtocol.ActionFromArgs(new[] { "--unknown" }));
    }
}
