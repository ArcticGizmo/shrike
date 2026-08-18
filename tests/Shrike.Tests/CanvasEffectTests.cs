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
    public void CanvasCompositor_blits_the_sprite_scaled_by_the_frame_envelope()
    {
        const int w = 4, h = 4;
        // A fully-opaque red sprite (premultiplied BGRA — for full alpha premul == straight).
        var sprite = new byte[w * h * 4];
        for (var i = 0; i < sprite.Length; i += 4) { sprite[i] = 0; sprite[i + 1] = 0; sprite[i + 2] = 255; sprite[i + 3] = 255; }

        // Frame 0: alpha 1 → red; frame 1: alpha 0 → untouched.
        var comp = new CanvasCompositor(sprite, w, h, [1.0, 0.0]);

        var f0 = new byte[w * h * 4];
        comp.Compose(f0, w, h, 0);
        Assert.Equal(255, f0[2]); // R
        Assert.Equal(0, f0[0]);   // B

        var f1 = new byte[w * h * 4];
        comp.Compose(f1, w, h, 1);
        Assert.Equal(0, f1[2]);   // alpha 0 → nothing drawn
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
