using SkiaSharp;

namespace Composa.Model;

/// <summary>
/// Snapshot-based undo. Snapshots share bitmaps with the live document, so an entry only costs the pixels an
/// edit actually replaced. The oldest entries go once the distinct bitmaps held exceed the memory budget.
/// </summary>
public sealed class History
{
    private readonly List<(string Name, Document State)> undo = [];
    private readonly List<(string Name, Document State)> redo = [];

    private int sinceCollect;

    public long MemoryBudget { get; set; } = 3L * 1024 * 1024 * 1024;
    public int MaxEntries { get; set; } = 100;

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public string UndoName => undo.Count > 0 ? undo[^1].Name : "";
    public string RedoName => redo.Count > 0 ? redo[^1].Name : "";
    public int Count => undo.Count;

    /// <summary>Records the state as it was before an edit named <paramref name="name"/>.</summary>
    public void Push(string name, Document before)
    {
        undo.Add((name, before));
        redo.Clear();
        Trim();
    }

    public Document? Undo(Document current)
    {
        if (undo.Count == 0) return null;
        var entry = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        redo.Add((entry.Name, current));
        return entry.State;
    }

    public Document? Redo(Document current)
    {
        if (redo.Count == 0) return null;
        var entry = redo[^1];
        redo.RemoveAt(redo.Count - 1);
        undo.Add((entry.Name, current));
        return entry.State;
    }

    /// <summary>Folds the newest entry into the one before it, so two consecutive edits undo as one step named <paramref name="name"/>.</summary>
    public void MergeLast(string name)
    {
        if (undo.Count < 2) return;
        var before = undo[^2].State;
        undo.RemoveRange(undo.Count - 2, 2);
        undo.Add((name, before));
    }

    /// <summary>Drops the newest undo entry without applying it, for edits that turned out to change nothing.</summary>
    public void DiscardLast()
    {
        if (undo.Count > 0) undo.RemoveAt(undo.Count - 1);
    }

    public void Clear()
    {
        undo.Clear();
        redo.Clear();
    }

    private void Trim()
    {
        var dropped = false;
        while (undo.Count > MaxEntries) { undo.RemoveAt(0); dropped = true; }
        while (undo.Count > 1 && HeldBytes() > MemoryBudget) { undo.RemoveAt(0); dropped = true; }
        // Bitmaps live in native memory the garbage collector cannot see, so it gets a nudge when history lets go of
        // some; they cannot simply be disposed here because newer snapshots may still share them.
        if (dropped && ++sinceCollect >= 8) { sinceCollect = 0; GC.Collect(2, GCCollectionMode.Optimized, blocking: false); }
    }

    private long HeldBytes()
    {
        var bitmaps = new HashSet<SKBitmap>(ReferenceEqualityComparer.Instance);
        foreach (var (_, state) in undo) state.CollectBitmaps(bitmaps);
        return bitmaps.Sum(b => (long)b.ByteCount);
    }
}
