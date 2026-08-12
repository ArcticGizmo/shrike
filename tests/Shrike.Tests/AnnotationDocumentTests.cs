using Shrike.Core.Annotations;

namespace Shrike.Tests;

public class AnnotationDocumentTests
{
    [Fact]
    public void Add_appends_in_order()
    {
        var doc = new AnnotationDocument();
        doc.Add(new RectAnnotation(0, 0, 10, 10));
        doc.Add(new EllipseAnnotation(1, 1, 5, 5));

        Assert.Equal(2, doc.Items.Count);
        Assert.IsType<RectAnnotation>(doc.Items[0]);
        Assert.IsType<EllipseAnnotation>(doc.Items[1]);
    }

    [Fact]
    public void Undo_and_redo_walk_history()
    {
        var doc = new AnnotationDocument();
        doc.Add(new RectAnnotation(0, 0, 10, 10));
        doc.Add(new RectAnnotation(0, 0, 20, 20));

        Assert.True(doc.CanUndo);
        doc.Undo();
        Assert.Single(doc.Items);

        Assert.True(doc.CanRedo);
        doc.Redo();
        Assert.Equal(2, doc.Items.Count);
    }

    [Fact]
    public void Adding_after_undo_clears_redo()
    {
        var doc = new AnnotationDocument();
        doc.Add(new RectAnnotation(0, 0, 10, 10));
        doc.Undo();
        Assert.True(doc.CanRedo);

        doc.Add(new EllipseAnnotation(0, 0, 5, 5));
        Assert.False(doc.CanRedo);
        Assert.Single(doc.Items);
    }

    [Fact]
    public void Changed_fires_on_mutations()
    {
        var doc = new AnnotationDocument();
        var count = 0;
        doc.Changed += () => count++;

        doc.Add(new RectAnnotation(0, 0, 1, 1));
        doc.Undo();
        doc.Redo();

        Assert.Equal(3, count);
    }

    [Fact]
    public void RemoveAt_is_undoable()
    {
        var doc = new AnnotationDocument();
        doc.Add(new RectAnnotation(0, 0, 10, 10));
        doc.Add(new EllipseAnnotation(0, 0, 5, 5));

        doc.RemoveAt(0);
        Assert.Single(doc.Items);
        Assert.IsType<EllipseAnnotation>(doc.Items[0]);

        doc.Undo();
        Assert.Equal(2, doc.Items.Count);
        Assert.IsType<RectAnnotation>(doc.Items[0]);
    }

    [Fact]
    public void Interactive_move_collapses_to_one_undo()
    {
        var doc = new AnnotationDocument();
        doc.Add(new RectAnnotation(0, 0, 10, 10));

        // Simulate a drag: one checkpoint, then several live frames.
        doc.BeginInteractive();
        doc.ReplaceLive(0, new RectAnnotation(2, 0, 10, 10));
        doc.ReplaceLive(0, new RectAnnotation(5, 0, 10, 10));
        doc.ReplaceLive(0, new RectAnnotation(9, 0, 10, 10));

        Assert.Equal(9, ((RectAnnotation)doc.Items[0]).X);

        doc.Undo(); // a single undo returns to the pre-drag position
        Assert.Equal(0, ((RectAnnotation)doc.Items[0]).X);
    }

    [Fact]
    public void RedactionRects_only_returns_redactions()
    {
        var doc = new AnnotationDocument();
        doc.Add(new RectAnnotation(0, 0, 10, 10));
        doc.Add(new RedactionAnnotation(5, 6, 20, 30));

        var rects = doc.RedactionRects().ToList();
        Assert.Single(rects);
        Assert.Equal(new Shrike.Core.Capture.PixelBounds(5, 6, 20, 30), rects[0]);
    }
}
