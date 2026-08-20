using NAudio.CoreAudioApi;
using Shrike.Core.Audio;

namespace Shrike.Audio;

/// <summary>
/// WASAPI implementation of <see cref="IAudioDeviceCatalog"/> via <c>MMDeviceEnumerator</c>. Backs the
/// mic-check device picker (capture endpoints) and tells system-sound loopback which render endpoint to tap.
/// Enumeration is done on demand — nothing here runs at tray start.
/// </summary>
public sealed class NAudioDeviceCatalog : IAudioDeviceCatalog
{
    public IReadOnlyList<AudioDevice> InputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var defaultId = DefaultId(enumerator, DataFlow.Capture);

        var devices = new List<AudioDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device)
                devices.Add(new AudioDevice(device.ID, device.FriendlyName, device.ID == defaultId));
        }

        // Default first so the picker pre-selects it.
        devices.Sort((a, b) => b.IsDefault.CompareTo(a.IsDefault));
        return devices;
    }

    public AudioDevice? DefaultInput() => Default(DataFlow.Capture);

    public AudioDevice? DefaultOutput() => Default(DataFlow.Render);

    private static AudioDevice? Default(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia)) return null;
        using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
        return new AudioDevice(device.ID, device.FriendlyName, true);
    }

    private static string? DefaultId(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        if (!enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia)) return null;
        using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
        return device.ID;
    }
}
