namespace Shrike.Core.Recording;

/// <summary>One keyframe on an animation channel: a value at a local time (ms from the effect's start).</summary>
public readonly record struct Keyframe(long AtMs, double Value);

/// <summary>The resolved transform of a canvas layer at one frame — a translate (source px), uniform scale,
/// rotation (degrees, about the frame centre) and opacity. Identity leaves the sprite untouched.</summary>
public readonly record struct LayerTransform(double Dx, double Dy, double Scale, double RotationDeg, double Opacity)
{
    public static LayerTransform Identity { get; } = new(0, 0, 1, 0, 1);

    /// <summary>True when only opacity may differ from identity — lets the compositor take the cheap straight
    /// blit instead of a full affine resample.</summary>
    public bool IsIdentityGeometry =>
        Dx == 0 && Dy == 0 && Math.Abs(Scale - 1) < 1e-9 && RotationDeg == 0;
}

/// <summary>
/// A canvas layer's animation — five keyframe channels (translate x/y, scale, rotation, opacity) in the
/// layer's <b>local</b> time (0..duration), so the animation rides the effect when it's dragged on the lane.
/// An empty channel holds its default (0 / 1), so <see cref="Identity"/> (all empty) reproduces a static
/// layer exactly — animation is purely additive over the static case. Pure and headless-tested; the same
/// <see cref="SampleAt"/> feeds both the export compositor and the live preview, so they stay WYSIWYG.
/// </summary>
public sealed record CanvasAnimation(
    IReadOnlyList<Keyframe> X,
    IReadOnlyList<Keyframe> Y,
    IReadOnlyList<Keyframe> Scale,
    IReadOnlyList<Keyframe> Rotation,
    IReadOnlyList<Keyframe> Opacity)
{
    public static CanvasAnimation Identity { get; } =
        new([], [], [], [], []);

    public bool IsEmpty =>
        X.Count == 0 && Y.Count == 0 && Scale.Count == 0 && Rotation.Count == 0 && Opacity.Count == 0;

    /// <summary>The transform at local time <paramref name="localMs"/> — each channel sampled (held past its
    /// ends, smoothstepped between keys).</summary>
    public LayerTransform SampleAt(long localMs) => new(
        Sample(X, localMs, 0),
        Sample(Y, localMs, 0),
        Sample(Scale, localMs, 1),
        Sample(Rotation, localMs, 0),
        Math.Clamp(Sample(Opacity, localMs, 1), 0, 1));

    // Sample a sorted keyframe channel: the endpoints hold; between two keys, a smoothstep-eased lerp.
    private static double Sample(IReadOnlyList<Keyframe> ks, long t, double dflt)
    {
        if (ks.Count == 0) return dflt;
        if (t <= ks[0].AtMs) return ks[0].Value;
        if (t >= ks[^1].AtMs) return ks[^1].Value;
        for (var i = 1; i < ks.Count; i++)
        {
            if (t > ks[i].AtMs) continue;
            var a = ks[i - 1];
            var b = ks[i];
            var span = Math.Max(1, b.AtMs - a.AtMs);
            var u = (t - a.AtMs) / (double)span;
            u = u * u * (3 - 2 * u); // smoothstep
            return a.Value + (b.Value - a.Value) * u;
        }
        return ks[^1].Value;
    }
}

/// <summary>Canned canvas animations that populate the keyframe channels — the authoring entry point until a
/// per-key editor lands. Each is expressed in the same model, so a preset is just a starting point the user
/// (or a future editor) can refine.</summary>
public enum CanvasAnimationKind
{
    None,
    Fade,       // fade in and out
    SlideLeft,  // enter from the left
    SlideRight, // enter from the right
    SlideUp,    // enter from below
    Pop,        // scale + fade in
}

public static class CanvasAnimationPresets
{
    /// <summary>Build a preset for a layer of length <paramref name="durationMs"/> over a
    /// <paramref name="frameW"/>×<paramref name="frameH"/> frame (slide distances scale with the frame).</summary>
    public static CanvasAnimation Build(CanvasAnimationKind kind, long durationMs, int frameW, int frameH)
    {
        var ramp = Math.Clamp(durationMs / 4, 120, 500);       // enter/leave time
        var outStart = Math.Max(ramp, durationMs - ramp);
        switch (kind)
        {
            case CanvasAnimationKind.Fade:
                return CanvasAnimation.Identity with
                { Opacity = [new(0, 0), new(ramp, 1), new(outStart, 1), new(durationMs, 0)] };
            case CanvasAnimationKind.SlideLeft:
                return CanvasAnimation.Identity with
                { X = [new(0, -frameW * 0.35), new(ramp, 0)], Opacity = [new(0, 0), new(ramp, 1)] };
            case CanvasAnimationKind.SlideRight:
                return CanvasAnimation.Identity with
                { X = [new(0, frameW * 0.35), new(ramp, 0)], Opacity = [new(0, 0), new(ramp, 1)] };
            case CanvasAnimationKind.SlideUp:
                return CanvasAnimation.Identity with
                { Y = [new(0, frameH * 0.35), new(ramp, 0)], Opacity = [new(0, 0), new(ramp, 1)] };
            case CanvasAnimationKind.Pop:
                return CanvasAnimation.Identity with
                {
                    Scale = [new(0, 0.6), new(ramp, 1.06), new(ramp + 120, 1.0)],
                    Opacity = [new(0, 0), new(ramp, 1)],
                };
            default:
                return CanvasAnimation.Identity;
        }
    }
}
