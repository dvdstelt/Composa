using Compositor.Model;
using SkiaSharp;

namespace Compositor.Editing;

public sealed partial class EditorSession
{
    /// <summary>What the View menu shows and snaps to.</summary>
    public ViewOptions View { get; set; } = new();

    public IReadOnlyList<Guide> Guides => document.Guides;
    public bool CanClearGuides => document.Guides.Count > 0;
    public bool CanEditGuides => !View.LockGuides;

    /// <summary>Adds a guide at a document position (X for vertical, Y for horizontal). Returns null while guides are locked.</summary>
    public Guide? AddGuide(GuideAxis axis, double position)
    {
        if (!CanEditGuides || !double.IsFinite(position) || document.Guides.Count >= 1000) return null;
        var guide = new Guide(Guid.NewGuid(), axis, position);
        Apply("New Guide", () => document.Guides.Add(guide));
        if (!View.ShowGuides) View = View with { ShowGuides = true };
        return guide;
    }

    public void MoveGuide(Guid id, double position)
    {
        var index = document.Guides.FindIndex(g => g.Id == id);
        if (!CanEditGuides || index < 0 || !double.IsFinite(position) || document.Guides[index].Position == position) return;
        Apply("Move Guide", () => document.Guides[index] = document.Guides[index] with { Position = position });
    }

    public void RemoveGuide(Guid id)
    {
        if (!CanEditGuides || document.Guides.All(g => g.Id != id)) return;
        Apply("Delete Guide", () => document.Guides.RemoveAll(g => g.Id == id));
    }

    /// <summary>Clear Guides works even while guides are locked, as in Photoshop.</summary>
    public void ClearGuides()
    {
        if (!CanClearGuides) return;
        Apply("Clear Guides", () => document.Guides.Clear());
    }

    /// <summary>
    /// Alignment lines a move or crop may snap to, according to View > Snap and Snap To: the canvas edges (and
    /// centers), other visible layers' bounds, the grid and the guides. Empty when snapping is off.
    /// </summary>
    public (List<float> Xs, List<float> Ys) SnapTargets(IReadOnlySet<Guid>? moving = null, bool includeCenters = true)
    {
        var xs = new List<float>();
        var ys = new List<float>();
        if (!View.Snap) return (xs, ys);
        if (View.SnapToDocumentBounds)
        {
            xs.AddRange([0, document.Width]);
            ys.AddRange([0, document.Height]);
            if (includeCenters) { xs.Add(document.Width / 2f); ys.Add(document.Height / 2f); }
        }
        if (View.SnapToLayers)
        {
            foreach (var other in document.AllLayers().Where(l => l.Pixels != null && (moving == null || !moving.Contains(l.Id)) && document.IsEffectivelyVisible(l)).Take(60))
            {
                var b = other.Bounds;
                xs.AddRange([MathF.Round(b.Left), MathF.Round(b.Right)]);
                ys.AddRange([MathF.Round(b.Top), MathF.Round(b.Bottom)]);
                if (includeCenters) { xs.Add(MathF.Round(b.MidX)); ys.Add(MathF.Round(b.MidY)); }
            }
        }
        // Hidden extras do not snap, matching Photoshop.
        if (View.SnapToGrid && View.ShowGrid)
        {
            xs.AddRange(LayoutGrid.Lines(document.Width).Select(v => (float)v));
            ys.AddRange(LayoutGrid.Lines(document.Height).Select(v => (float)v));
        }
        if (View.SnapToGuides && View.ShowGuides)
        {
            foreach (var guide in document.Guides)
                (guide.Axis == GuideAxis.Vertical ? xs : ys).Add((float)guide.Position);
        }
        return (xs, ys);
    }

    /// <summary>A guide being placed snaps to the grid, other guides, the canvas and layer edges within a tolerance.</summary>
    public double SnapGuidePosition(GuideAxis axis, double position, Guid? excluding, double tolerance)
    {
        if (!View.Snap) return position;
        var (xs, ys) = SnapTargets(null, includeCenters: true);
        var targets = axis == GuideAxis.Vertical ? xs : ys;
        if (excluding is { } id && document.Guides.FirstOrDefault(g => g.Id == id) is { } own) targets.Remove((float)own.Position);
        var best = position;
        var bestDistance = tolerance;
        foreach (var target in targets)
        {
            var distance = Math.Abs(target - position);
            if (distance <= bestDistance) { bestDistance = distance; best = target; }
        }
        return best;
    }

    /// <summary>Pulls a move onto the snap targets; returns the adjusted offsets and the positions snapped to.</summary>
    public (float Dx, float Dy, float? SnapX, float? SnapY) SnapMove(SKRect box, IReadOnlySet<Guid> moving, float dx, float dy, float tolerance)
    {
        var (xs, ys) = SnapTargets(moving, includeCenters: true);
        float bestX = tolerance, bestY = tolerance, addX = 0, addY = 0;
        float? snapX = null, snapY = null;
        foreach (var edge in new[] { box.Left, box.MidX, box.Right })
        foreach (var target in xs)
        {
            var d = Math.Abs(edge + dx - target);
            if (d < bestX) { bestX = d; addX = target - (edge + dx); snapX = target; }
        }
        foreach (var edge in new[] { box.Top, box.MidY, box.Bottom })
        foreach (var target in ys)
        {
            var d = Math.Abs(edge + dy - target);
            if (d < bestY) { bestY = d; addY = target - (edge + dy); snapY = target; }
        }
        return (dx + addX, dy + addY, snapX, snapY);
    }

    /// <summary>Snaps one crop edge to the targets (without centers), or leaves it alone.</summary>
    public float SnapCropEdge(float value, bool horizontal, float tolerance)
    {
        var (xs, ys) = SnapTargets(null, includeCenters: false);
        var best = value;
        var bestDistance = tolerance;
        foreach (var target in horizontal ? xs : ys)
        {
            var distance = Math.Abs(target - value);
            if (distance < bestDistance) { bestDistance = distance; best = target; }
        }
        return best;
    }
}
