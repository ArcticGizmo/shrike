using Shrike.Core.Annotations;
using Shrike.Core.Recording;

namespace Shrike.Tests;

public class CanvasEffectTests
{
    // --- annotation JSON round-trip ------------------------------------------------------------------------

    [Fact]
    public void AnnotationJson_round_trips_every_type()
    {
        var items = new Annotation[]
        {
            new RectAnnotation(1, 2, 3, 4, Filled: true) { Color = "#112233", StrokeWidth = 5, Rotation = 12 },
            new EllipseAnnotation(5, 6, 7, 8),
            new LineAnnotation(1, 1, 9, 9, Arrow: true),
            new FreehandAnnotation([new PointD(1, 2), new PointD(3, 4), new PointD(5, 6)]),
            new HighlightAnnotation(10, 11, 12, 13),
            new RedactionAnnotation(20, 21, 22, 23),
            new TextAnnotation(30, 31, "hello", 24),
            new StepBadgeAnnotation(40, 41, 7),
        };

        var back = AnnotationJson.FromDtos(AnnotationJson.ToDtos(items));

        Assert.Equal(8, back.Count);
        var rect = Assert.IsType<RectAnnotation>(back[0]);
        Assert.True(rect.Filled);
        Assert.Equal("#112233", rect.Color);
        Assert.Equal(5, rect.StrokeWidth);
        Assert.Equal(12, rect.Rotation);
        Assert.True(Assert.IsType<LineAnnotation>(back[2]).Arrow);
        Assert.Equal(3, Assert.IsType<FreehandAnnotation>(back[3]).Points.Count);
        Assert.Equal("hello", Assert.IsType<TextAnnotation>(back[6]).Text);
        Assert.Equal(7, Assert.IsType<StepBadgeAnnotation>(back[7]).Number);
    }

    [Fact]
    public void AnnotationJson_drops_unknown_items()
        => Assert.Empty(AnnotationJson.FromDtos([new AnnotationDto { T = "no-such-type" }]));

    // --- clip-edit v2 canvas persistence -------------------------------------------------------------------

    [Fact]
    public void ClipEdit_round_trips_a_canvas_effect_with_its_drawing()
    {
        var canvas = new CanvasEffect(0, 2000, 0, 0, CanvasSpace.Screen)
        {
            Annotations = [new RectAnnotation(10, 10, 40, 30), new TextAnnotation(5, 5, "note")],
        };
        var back = ClipEdit.FromJson(new ClipEdit(new EffectTrack([canvas])).ToJson());

        var c = Assert.Single(back.Effects.OfKind<CanvasEffect>());
        Assert.Equal(CanvasSpace.Screen, c.Space);
        Assert.Equal(2, c.Annotations.Count);
        Assert.Equal("note", Assert.IsType<TextAnnotation>(c.Annotations[1]).Text);
    }

    // --- canvas compositor ---------------------------------------------------------------------------------

    [Fact]
    public void CanvasCompositor_blits_the_sprite_at_the_frame_opacity()
    {
        const int w = 4, h = 4;
        // A fully-opaque red sprite (premultiplied BGRA — for full alpha premul == straight).
        var sprite = RedSprite(w, h);

        // Frame 0: opacity 1 → red; frame 1: opacity 0 → untouched.
        var comp = new CanvasCompositor(sprite, w, h,
            [LayerTransform.Identity, LayerTransform.Identity with { Opacity = 0 }]);

        var f0 = new byte[w * h * 4];
        comp.Compose(f0, w, h, 0);
        Assert.Equal(255, f0[2]); // R
        Assert.Equal(0, f0[0]);   // B

        var f1 = new byte[w * h * 4];
        comp.Compose(f1, w, h, 1);
        Assert.Equal(0, f1[2]);   // opacity 0 → nothing drawn
    }

    [Fact]
    public void CanvasCompositor_translates_the_sprite_when_animated()
    {
        // A single opaque red pixel at the frame centre; translating +2px in x moves where it lands.
        const int w = 8, h = 8;
        var sprite = new byte[w * h * 4];
        var centre = ((h / 2) * w + (w / 2)) * 4;
        sprite[centre] = 0; sprite[centre + 1] = 0; sprite[centre + 2] = 255; sprite[centre + 3] = 255;

        var comp = new CanvasCompositor(sprite, w, h, [new LayerTransform(2, 0, 1, 0, 1)]);
        var f = new byte[w * h * 4];
        comp.Compose(f, w, h, 0);

        // The red pixel is now 2px to the right of centre.
        var moved = ((h / 2) * w + (w / 2 + 2)) * 4;
        Assert.True(f[moved + 2] > 200, "expected the translated red pixel");
        Assert.Equal(0, f[centre + 2]); // and gone from the original centre
    }

    [Fact]
    public void ResolveCanvasTransforms_is_identity_for_a_static_layer_and_animates_opacity()
    {
        var timeline = new Timeline(2000);
        var stat = new CanvasEffect(0, 2000, 0, 0, CanvasSpace.Content);
        var s = EffectTrack.ResolveCanvasTransforms(stat, timeline, 40, 20)[20]; // mid-span
        Assert.True(s.IsIdentityGeometry);
        Assert.Equal(1.0, s.Opacity, 3);

        // A fade preset ramps opacity from 0 at the edges to 1 mid-span.
        var faded = stat with { Animation = CanvasAnimationPresets.Build(CanvasAnimationKind.Fade, 2000, 1920, 1080) };
        var t = EffectTrack.ResolveCanvasTransforms(faded, timeline, 40, 20);
        Assert.True(t[0].Opacity < t[20].Opacity); // fades in
    }

    [Fact]
    public void CanvasAnimation_samples_hold_and_smoothstep()
    {
        var anim = CanvasAnimation.Identity with { Scale = [new Keyframe(0, 1), new Keyframe(1000, 2)] };
        Assert.Equal(1.0, anim.SampleAt(-100).Scale, 6); // hold before first
        Assert.Equal(2.0, anim.SampleAt(2000).Scale, 6); // hold after last
        Assert.Equal(1.5, anim.SampleAt(500).Scale, 6);  // smoothstep midpoint == linear midpoint
    }

    [Fact]
    public void ClipEdit_round_trips_a_canvas_animation()
    {
        var c = new CanvasEffect(0, 2000, 0, 0, CanvasSpace.Content)
        {
            Annotations = [new RectAnnotation(1, 2, 3, 4)],
            Animation = CanvasAnimation.Identity with { X = [new Keyframe(0, -50), new Keyframe(300, 0)] },
        };
        var back = ClipEdit.FromJson(new ClipEdit(new EffectTrack([c])).ToJson());
        var anim = Assert.Single(back.Effects.OfKind<CanvasEffect>()).Animation;
        Assert.Equal(2, anim.X.Count);
        Assert.Equal(-50, anim.X[0].Value, 6);
        Assert.Equal(300, anim.X[1].AtMs);
    }

    private static byte[] RedSprite(int w, int h)
    {
        var sprite = new byte[w * h * 4];
        for (var i = 0; i < sprite.Length; i += 4) { sprite[i] = 0; sprite[i + 1] = 0; sprite[i + 2] = 255; sprite[i + 3] = 255; }
        return sprite;
    }

    [Fact]
    public void ResolveEnvelope_is_one_across_a_hard_cut_span_and_zero_outside()
    {
        var timeline = new Timeline(2000);
        var canvas = new CanvasEffect(500, 1500, 0, 0, CanvasSpace.Content);
        var env = EffectTrack.ResolveEnvelope(canvas, timeline, frameCount: 40, fps: 20);

        Assert.Equal(0.0, env[4]);   // 200ms — before
        Assert.Equal(1.0, env[20]);  // 1000ms — inside (no ease → full)
        Assert.Equal(0.0, env[34]);  // 1700ms — after
    }
}
