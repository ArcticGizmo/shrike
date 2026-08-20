namespace Shrike.App.Views;

/// <summary>The persisted audio-capture choices the recording HUD and mic-check dialog open with, and the
/// shape the settings round-trip through. A plain value so it's trivial to pass around.</summary>
internal readonly record struct MicSetup(bool MicEnabled, string? MicDeviceId, bool SystemSound)
{
    /// <summary>True when anything will be captured — used to show the HUD's armed state.</summary>
    public bool AnyArmed => MicEnabled || SystemSound;
}
