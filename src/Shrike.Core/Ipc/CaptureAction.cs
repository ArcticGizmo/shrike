namespace Shrike.Core.Ipc;

/// <summary>
/// A user-facing capture intent. A second launch of Shrike doesn't cold-start — it forwards one of
/// these to the resident instance over the single-instance pipe (<see cref="IpcProtocol"/>), which
/// then performs it on the current desktop. Only <see cref="ShowOverlay"/> is wired in M0; the rest
/// are the surface the later milestones fill in.
/// </summary>
public enum CaptureAction
{
    /// <summary>Show the region-capture overlay (the M0 default and stand-in for region capture).</summary>
    ShowOverlay,
    CaptureRegion,
    CaptureWindow,
    CaptureFullScreen,
    StartRecording,
    ShowRecent,
    ShowSettings,
}
