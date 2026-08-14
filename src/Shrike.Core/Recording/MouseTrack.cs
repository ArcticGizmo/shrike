using System.Text.Json;
using System.Text.Json.Serialization;
using Shrike.Core.Capture;

namespace Shrike.Core.Recording;

/// <summary>Which mouse button a <see cref="MouseClick"/> refers to.</summary>
public enum MouseButtonKind { Left, Right, Middle }

/// <summary>A pointer position at a moment in the recording. <see cref="TMs"/> is milliseconds on the
/// recording's own (pause-excluded) timeline; <see cref="X"/>/<see cref="Y"/> are virtual-screen physical pixels.</summary>
public readonly record struct MousePoint(int TMs, int X, int Y);

/// <summary>A button transition at a moment in the recording (same timeline as <see cref="MousePoint"/>).</summary>
public readonly record struct MouseClick(int TMs, MouseButtonKind Button, bool Down);

/// <summary>
/// The recorded pointer path + button events for one recording, captured live so a smoothed synthetic
/// cursor can be composited back over the (cursor-free) frames at export. Positions are in virtual-screen
/// physical pixels; <see cref="Region"/> is the recorded rectangle in the same space, so export can map
/// each point into region-local pixels (that mapping is SC2). Serialises to a compact JSON sidecar written
/// next to the MP4. Immutable and UI-free, so it lives in Core with tests.
/// </summary>
public sealed class MouseTrack
{
    private const int SchemaVersion = 1;

    /// <summary>The recorded rectangle in virtual-screen physical pixels (origin + even-trimmed size).</summary>
    public PixelBounds Region { get; }

    public IReadOnlyList<MousePoint> Points { get; }
    public IReadOnlyList<MouseClick> Clicks { get; }

    public MouseTrack(PixelBounds region, IReadOnlyList<MousePoint> points, IReadOnlyList<MouseClick> clicks)
    {
        Region = region;
        Points = points ?? throw new ArgumentNullException(nameof(points));
        Clicks = clicks ?? throw new ArgumentNullException(nameof(clicks));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serialise to compact JSON. Points/clicks are stored as small int arrays to keep the file lean.</summary>
    public string ToJson()
    {
        var dto = new Dto
        {
            V = SchemaVersion,
            Region = [Region.X, Region.Y, Region.Width, Region.Height],
            Points = Points.Select(p => new[] { p.TMs, p.X, p.Y }).ToArray(),
            Clicks = Clicks.Select(c => new[] { c.TMs, (int)c.Button, c.Down ? 1 : 0 }).ToArray(),
        };
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    /// <summary>Parse a track from JSON produced by <see cref="ToJson"/>. Throws on malformed input.</summary>
    public static MouseTrack FromJson(string json)
    {
        var dto = JsonSerializer.Deserialize<Dto>(json, JsonOptions)
            ?? throw new FormatException("Mouse-track JSON was null.");
        if (dto.Region is not { Length: 4 })
            throw new FormatException("Mouse-track region must be [x, y, w, h].");

        var region = new PixelBounds(dto.Region[0], dto.Region[1], dto.Region[2], dto.Region[3]);
        var points = (dto.Points ?? [])
            .Where(p => p.Length == 3)
            .Select(p => new MousePoint(p[0], p[1], p[2]))
            .ToArray();
        var clicks = (dto.Clicks ?? [])
            .Where(c => c.Length == 3)
            .Select(c => new MouseClick(c[0], (MouseButtonKind)c[1], c[2] != 0))
            .ToArray();

        return new MouseTrack(region, points, clicks);
    }

    /// <summary>Write the track as JSON to <paramref name="path"/> (the <c>*.track.json</c> sidecar).</summary>
    public void Save(string path) => File.WriteAllText(path, ToJson());

    /// <summary>Read a track back from a sidecar written by <see cref="Save"/>.</summary>
    public static MouseTrack Load(string path) => FromJson(File.ReadAllText(path));

    private sealed class Dto
    {
        public int V { get; set; } = SchemaVersion;
        public int[]? Region { get; set; }
        public int[][]? Points { get; set; }
        public int[][]? Clicks { get; set; }
    }
}
