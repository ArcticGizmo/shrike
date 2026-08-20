using Shrike.Core.Imaging;

namespace Shrike.Core.Settings;

/// <summary>How a reused window (editor) behaves when it's parked on another virtual desktop.</summary>
public enum DesktopBehaviour
{
    /// <summary>Bring the window to the desktop you're looking at (never switches you away). The default.</summary>
    FollowMe,
    /// <summary>Leave the existing window where it is and open a fresh one on the current desktop.</summary>
    NewWindowHere,
}

/// <summary>
/// Everything the user can configure, in one serialisable bag. It's a plain record with default values on
/// every member, so a settings file that predates a new field — or is missing one — still loads with a
/// sensible default rather than a zero. Hotkeys live as text (round-tripped through
/// <see cref="Shrike.Core.Hotkeys.Hotkey"/>); an empty/absent hotkey means "unbound". UI-free so it stays
/// in <c>Shrike.Core</c> with tests.
/// </summary>
public sealed record AppSettings
{
    /// <summary>Opens the capture chooser. Empty = unbound.</summary>
    public string CaptureHotkey { get; init; } = "Alt+Shift+Q";

    public DesktopBehaviour DesktopBehaviour { get; init; } = DesktopBehaviour.FollowMe;

    /// <summary>Max captures kept in the recent ring.</summary>
    public int RingSize { get; init; } = 10;

    /// <summary>Total-bytes cap for the recent ring.</summary>
    public long RingByteCap { get; init; } = 512L * 1024 * 1024;

    /// <summary>Remembered save folder; null = ask / use the OS default each time.</summary>
    public string? DefaultSaveDirectory { get; init; }

    public ImageFormatKind DefaultImageFormat { get; init; } = ImageFormatKind.Png;

    /// <summary>Self-timer delay (seconds) before a screenshot fires — a "capture in N" so you can arrange
    /// transient UI first. 0 = off; only 3/5/10 are offered. Remembered across captures and sessions.
    /// Applies to screenshots only; recording has its own 3-2-1 countdown.</summary>
    public int CaptureDelaySeconds { get; init; } = 0;

    /// <summary>Draw the cursor into recordings.</summary>
    public bool CursorInRecording { get; init; } = true;

    // ---- Audio capture (recording): opt-in, off by default so no recording silently opens the mic. ----

    /// <summary>Capture the microphone into recordings. Off by default.</summary>
    public bool MicEnabled { get; init; } = false;

    /// <summary>Remembered microphone endpoint id; null = the system default input device.</summary>
    public string? MicDeviceId { get; init; }

    /// <summary>Also capture system sound (WASAPI loopback on the default render device). Off by default.</summary>
    public bool SystemSoundEnabled { get; init; } = false;

    /// <summary>Show a glowing "spotlight" under the mouse (visible on screen and in the recording). Off by default.</summary>
    public bool SpotlightCursorEnabled { get; init; } = false;

    /// <summary>Spotlight glow colour, as a hex string.</summary>
    public string SpotlightColor { get; init; } = "#FFD24A";

    /// <summary>Spotlight opacity at its core, 0..1.</summary>
    public double SpotlightOpacity { get; init; } = 0.30;

    /// <summary>Spotlight radius in screen pixels.</summary>
    public int SpotlightRadius { get; init; } = 30;

    /// <summary>Launch Shrike at login. Opt-in — off by default (a locked review decision).</summary>
    public bool Autostart { get; init; } = false;

    /// <summary>Show the "what's new" changelog popup on the first launch after an update. On by default.</summary>
    public bool ShowChangelogOnUpdate { get; init; } = true;

    /// <summary>The app version that last ran here — drives which changelog entries are "new". Null = fresh install.</summary>
    public string? LastSeenVersion { get; init; }

    // ---- Smooth cursor (experimental): the editor's tuning, persisted so it carries across sessions. ----

    /// <summary>Synthetic-cursor smoothing strength, 0..1 (higher = smoother). Maps to the 1€ params via
    /// <see cref="Shrike.Core.Recording.CursorSmoothing.FromSmoothness"/>. Default reproduces the shipped look.</summary>
    public double CursorSmoothness { get; init; } = Recording.CursorSmoothing.DefaultSmoothness;

    /// <summary>Relative synthetic-cursor size (1 = the resolution-scaled default; 0.5 = half, 2 = double).</summary>
    public double CursorSize { get; init; } = 1.0;

    /// <summary>Draw the expanding ring on each click.</summary>
    public bool CursorRippleEnabled { get; init; } = true;

    // ---- Captions (transcription): the model is an opt-in, in-app download, not bundled. ----

    /// <summary>Remembered transcription model id (e.g. <c>base.en</c>); null = none chosen yet, so the
    /// first "Generate captions" prompts the user to pick + download one.</summary>
    public string? CaptionModelId { get; init; }

    public static AppSettings Default { get; } = new();

    /// <summary>Clamp any out-of-range values a hand-edited or corrupt file might carry.</summary>
    public AppSettings Sanitised() => this with
    {
        RingSize = Math.Clamp(RingSize, 1, 100),
        RingByteCap = Math.Clamp(RingByteCap, 16L * 1024 * 1024, 4096L * 1024 * 1024),
        SpotlightOpacity = Math.Clamp(SpotlightOpacity, 0.1, 1.0),
        SpotlightRadius = Math.Clamp(SpotlightRadius, 12, 160),
        SpotlightColor = string.IsNullOrWhiteSpace(SpotlightColor) ? "#FFD24A" : SpotlightColor,
        CursorSmoothness = Math.Clamp(CursorSmoothness, 0.0, 1.0),
        CursorSize = Math.Clamp(CursorSize, 0.5, 2.0),
        // Only the offered delays are valid; anything else (hand-edited, or a future value) falls back to off.
        CaptureDelaySeconds = CaptureDelaySeconds is 3 or 5 or 10 ? CaptureDelaySeconds : 0,
    };
}
