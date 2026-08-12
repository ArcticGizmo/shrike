using Shrike.Core.Capture;

namespace Shrike.Core.Annotations;

/// <summary>
/// The ordered, non-destructive list of annotations over a capture, with undo/redo. Annotations are
/// lightweight vectors, so undo/redo is snapshot-based (cheap and obviously correct). Raises
/// <see cref="Changed"/> after any mutation so the editor can re-render.
/// </summary>
public sealed class AnnotationDocument
{
    private List<Annotation> _items = [];
    private readonly Stack<List<Annotation>> _undo = new();
    private readonly Stack<List<Annotation>> _redo = new();

    /// <summary>Annotations in draw order (first drawn is painted first / underneath).</summary>
    public IReadOnlyList<Annotation> Items => _items;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public event Action? Changed;

    /// <summary>Append an annotation (e.g. after the user finishes drawing it).</summary>
    public void Add(Annotation annotation)
    {
        Commit();
        _items.Add(annotation);
        Changed?.Invoke();
    }

    /// <summary>Remove the most recently added annotation.</summary>
    public void RemoveLast()
    {
        if (_items.Count == 0) return;
        Commit();
        _items.RemoveAt(_items.Count - 1);
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        Commit();
        _items = [];
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(_items);
        _items = _undo.Pop();
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(_items);
        _items = _redo.Pop();
        Changed?.Invoke();
    }

    /// <summary>The redaction regions as integer pixel rects, for the destructive export scrub.</summary>
    public IEnumerable<PixelBounds> RedactionRects()
        => _items.OfType<RedactionAnnotation>()
            .Select(r => new PixelBounds(
                (int)Math.Round(r.X), (int)Math.Round(r.Y),
                (int)Math.Round(r.Width), (int)Math.Round(r.Height)));

    private void Commit()
    {
        _undo.Push([.. _items]);
        _redo.Clear();
    }
}
