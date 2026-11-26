using SkiaSharp;
using SnapPocket.Annotation.Models;

namespace SnapPocket.Annotation.Commands;

public sealed class AnnotationSnapshot : IDisposable
{
    public SKBitmap? BaseBitmap { get; private set; }
    public List<AnnotationItem> Items { get; }
    public int NextStepNumber { get; }
    internal long BitmapBytes => BaseBitmap is { } bitmap ? (long)bitmap.RowBytes * bitmap.Height : 0;

    public AnnotationSnapshot(SKBitmap? baseBitmap, IEnumerable<AnnotationItem> items, int nextStepNumber)
    {
        BaseBitmap = baseBitmap?.Copy();
        Items = items.Select(i => i.Clone()).ToList();
        NextStepNumber = nextStepNumber;
    }

    internal SKBitmap? TakeBitmap()
    {
        var bitmap = BaseBitmap;
        BaseBitmap = null;
        return bitmap;
    }

    public void Dispose() => TakeBitmap()?.Dispose();
}

public sealed class UndoRedoStack : IDisposable
{
    private const int MaxHistory = 30;
    private readonly long _bitmapBudget;
    private readonly LinkedList<AnnotationSnapshot> _undo = new();
    private readonly LinkedList<AnnotationSnapshot> _redo = new();
    private long _retainedBitmapBytes;

    public UndoRedoStack() : this(256L * 1024 * 1024) { }

    internal UndoRedoStack(long bitmapBudget) => _bitmapBudget = bitmapBudget;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    internal long RetainedBitmapBytes => _retainedBitmapBytes;

    public void PushUndo(SKBitmap currentBase, IEnumerable<AnnotationItem> currentItems, int nextStep)
        => Push(new AnnotationSnapshot(currentBase, currentItems, nextStep));

    public void PushVectorUndo(IEnumerable<AnnotationItem> currentItems, int nextStep)
        => Push(new AnnotationSnapshot(null, currentItems, nextStep));

    private void Push(AnnotationSnapshot snapshot)
    {
        Clear(_redo);
        Add(_undo, snapshot);
        Trim(_undo, _redo);
    }

    public AnnotationSnapshot? Undo(SKBitmap currentBase, IEnumerable<AnnotationItem> currentItems, int nextStep)
        => Move(_undo, _redo, currentBase, currentItems, nextStep);

    public AnnotationSnapshot? Redo(SKBitmap currentBase, IEnumerable<AnnotationItem> currentItems, int nextStep)
        => Move(_redo, _undo, currentBase, currentItems, nextStep);

    private AnnotationSnapshot? Move(LinkedList<AnnotationSnapshot> from, LinkedList<AnnotationSnapshot> to,
        SKBitmap currentBase, IEnumerable<AnnotationItem> items, int nextStep)
    {
        if (from.Last is not { } node)
            return null;

        var snapshot = node.Value;
        var opposite = new AnnotationSnapshot(snapshot.BaseBitmap is null ? null : currentBase, items, nextStep);
        from.RemoveLast();
        _retainedBitmapBytes -= snapshot.BitmapBytes;
        Add(to, opposite);
        Trim(to, from);
        return snapshot;
    }

    private void Add(LinkedList<AnnotationSnapshot> history, AnnotationSnapshot snapshot)
    {
        history.AddLast(snapshot);
        _retainedBitmapBytes += snapshot.BitmapBytes;
    }

    private void Trim(LinkedList<AnnotationSnapshot> destination, LinkedList<AnnotationSnapshot> other)
    {
        while (destination.Count + other.Count > 1 &&
               (_retainedBitmapBytes > _bitmapBudget || destination.Count + other.Count > MaxHistory))
        {
            // Keep the nearest step even if the image alone exceeds the budget.
            RemoveOldest(other.Count > 0 ? other : destination);
        }
    }

    private void RemoveOldest(LinkedList<AnnotationSnapshot> history)
    {
        var snapshot = history.First!.Value;
        history.RemoveFirst();
        _retainedBitmapBytes -= snapshot.BitmapBytes;
        snapshot.Dispose();
    }

    private void Clear(LinkedList<AnnotationSnapshot> history)
    {
        while (history.First is not null)
            RemoveOldest(history);
    }

    public void Dispose()
    {
        Clear(_undo);
        Clear(_redo);
    }
}
