namespace Shrike.Core.Audio;

/// <summary>An audio endpoint the user can pick — a microphone (capture) or, for system-sound loopback,
/// the active render device. <see cref="Id"/> is the stable WASAPI endpoint id used to re-select a
/// remembered device across sessions; <see cref="Name"/> is the friendly label shown in the mic-check UI.</summary>
public readonly record struct AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// Enumerates audio endpoints for the mic-check gate — capture devices for the mic, and the default render
/// device that system-sound loopback taps. UI-free abstraction so device selection is testable with a fake
/// catalogue; the NAudio (<c>MMDeviceEnumerator</c>) implementation lives in the adapter.
/// </summary>
public interface IAudioDeviceCatalog
{
    /// <summary>All active capture (microphone) endpoints, default first where the platform reports one.</summary>
    IReadOnlyList<AudioDevice> InputDevices();

    /// <summary>The system default capture endpoint, or null when none is present.</summary>
    AudioDevice? DefaultInput();

    /// <summary>The system default render endpoint — what a loopback source captures. Null when none.</summary>
    AudioDevice? DefaultOutput();
}
