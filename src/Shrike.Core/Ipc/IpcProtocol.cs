namespace Shrike.Core.Ipc;

/// <summary>
/// The tiny line protocol spoken over the single-instance named pipe: one <see cref="CaptureAction"/>
/// per connection, as its name. Kept trivial and version-tagged so a future format change can't be
/// misread by an older resident instance.
/// </summary>
public static class IpcProtocol
{
    /// <summary>Named-pipe identity. The <c>.v1</c> suffix guards against protocol drift.</summary>
    public const string PipeName = "Shrike.Ipc.v1";

    /// <summary>Encode an action for the wire.</summary>
    public static string Format(CaptureAction action) => action.ToString();

    /// <summary>Decode a wire line back to an action; false for anything unrecognised.</summary>
    public static bool TryParse(string? line, out CaptureAction action)
    {
        action = default;
        return !string.IsNullOrWhiteSpace(line)
            && Enum.TryParse(line.Trim(), ignoreCase: true, out action)
            && Enum.IsDefined(action);
    }

    /// <summary>
    /// Map process command-line args to the action a launch requests. Unknown/empty args fall back
    /// to <see cref="CaptureAction.ShowOverlay"/> so a bare re-launch simply pops the overlay.
    /// </summary>
    public static CaptureAction ActionFromArgs(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            switch (arg.TrimStart('-', '/').ToLowerInvariant())
            {
                case "region": return CaptureAction.CaptureRegion;
                case "window": return CaptureAction.CaptureWindow;
                case "full": case "fullscreen": return CaptureAction.CaptureFullScreen;
                case "record": return CaptureAction.StartRecording;
                case "recent": return CaptureAction.ShowRecent;
                case "settings": return CaptureAction.ShowSettings;
            }
        }
        return CaptureAction.ShowOverlay;
    }
}
