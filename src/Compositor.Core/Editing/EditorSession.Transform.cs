using Compositor.Model;
using SkiaSharp;

namespace Compositor.Editing;

public enum TransformHandle { None, Move, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left, Rotate }

/// <summary>
/// An interactive move/scale/rotate of the selected layers. Every change is computed from the transforms captured
/// when the drag started, so rounding never accumulates.
/// </summary>
public sealed class TransformEdit
{
    private readonly EditorSession session;
    private readonly List<(Layer Layer, LayerTransform Start)> layers;

    /// <summary>The frame being manipulated: an unrotated rectangle plus a rotation about its center.</summary>
    public SKRect StartFrame { get; }
    public double StartRotation { get; }
    public SKRect Frame { get; private set; }
    public double Rotation { get; private set; }
    public IReadOnlyList<Layer> Layers => layers.Select(l => l.Layer).ToList();

    internal TransformEdit(EditorSession session, List<Layer> targets)
    {
        this.session = session;
        layers = targets.Select(l => (l, l.Transform)).ToList();
        if (layers.Count == 1)
        {
            var t = layers[0].Start;
            StartFrame = SKRect.Create((float)t.X, (float)t.Y, (float)t.Width, (float)t.Height);
            StartRotation = t.Rotation;
        }
        else
        {
            var bounds = SKRect.Empty;
            foreach (var (layer, _) in layers) bounds = bounds.IsEmpty ? layer.Bounds : SKRect.Union(bounds, layer.Bounds);
            StartFrame = bounds;
        }
        Frame = StartFrame;
        Rotation = StartRotation;
    }

    /// <summary>The frame's corners in document space: top-left, top-right, bottom-right, bottom-left.</summary>
    public SKPoint[] Corners()
    {
        var rotate = SKMatrix.CreateRotationDegrees((float)Rotation, Frame.MidX, Frame.MidY);
        return [rotate.MapPoint(Frame.Left, Frame.Top), rotate.MapPoint(Frame.Right, Frame.Top), rotate.MapPoint(Frame.Right, Frame.Bottom), rotate.MapPoint(Frame.Left, Frame.Bottom)];
    }

    public void Set(SKRect frame, double rotation)
    {
        if (session.Transform != this) return; // Already committed or cancelled.
        var before = Area();
        Frame = frame;
        Rotation = rotation;
        Update();
        session.Invalidate(Geometry.Union(before, Area()));
    }

    public void MoveBy(float dx, float dy) => Set(SKRect.Create(StartFrame.Left + dx, StartFrame.Top + dy, StartFrame.Width, StartFrame.Height), Rotation);

    /// <summary>Drags a resize handle to a document point. Shift keeps proportions unless <paramref name="free"/>; Alt scales about the center.</summary>
    public void Resize(TransformHandle handle, SKPoint point, bool free, bool fromCenter)
    {
        // Work in the frame's own unrotated space.
        var unrotate = SKMatrix.CreateRotationDegrees((float)-StartRotation, StartFrame.MidX, StartFrame.MidY);
        var p = unrotate.MapPoint(point);
        float left = StartFrame.Left, top = StartFrame.Top, right = StartFrame.Right, bottom = StartFrame.Bottom;
        var movesLeft = handle is TransformHandle.TopLeft or TransformHandle.Left or TransformHandle.BottomLeft;
        var movesRight = handle is TransformHandle.TopRight or TransformHandle.Right or TransformHandle.BottomRight;
        var movesTop = handle is TransformHandle.TopLeft or TransformHandle.Top or TransformHandle.TopRight;
        var movesBottom = handle is TransformHandle.BottomLeft or TransformHandle.Bottom or TransformHandle.BottomRight;
        if (movesLeft) left = p.X;
        if (movesRight) right = p.X;
        if (movesTop) top = p.Y;
        if (movesBottom) bottom = p.Y;

        if (!free && StartFrame.Width > 0 && StartFrame.Height > 0)
        {
            var aspect = StartFrame.Width / StartFrame.Height;
            float w = right - left, h = bottom - top;
            var corner = (movesLeft || movesRight) && (movesTop || movesBottom);
            if (corner)
            {
                if (Math.Abs(w) / aspect > Math.Abs(h)) h = Math.Abs(w) / aspect * Math.Sign(h == 0 ? 1 : h);
                else w = Math.Abs(h) * aspect * Math.Sign(w == 0 ? 1 : w);
                if (movesLeft) left = right - w; else right = left + w;
                if (movesTop) top = bottom - h; else bottom = top + h;
            }
            else if (movesLeft || movesRight)
            {
                h = Math.Abs(w) / aspect;
                top = StartFrame.MidY - h / 2; bottom = StartFrame.MidY + h / 2;
            }
            else
            {
                w = Math.Abs(h) * aspect;
                left = StartFrame.MidX - w / 2; right = StartFrame.MidX + w / 2;
            }
        }
        if (fromCenter)
        {
            float cx = StartFrame.MidX, cy = StartFrame.MidY;
            var halfW = movesLeft ? cx - left : movesRight ? right - cx : (right - left) / 2;
            var halfH = movesTop ? cy - top : movesBottom ? bottom - cy : (bottom - top) / 2;
            left = cx - halfW; right = cx + halfW; top = cy - halfH; bottom = cy + halfH;
        }
        if (Math.Abs(right - left) < 1) right = left + 1;
        if (Math.Abs(bottom - top) < 1) bottom = top + 1;

        // Resizing moves the center; keep the opposite edge fixed on screen by rotating the new center back.
        var rotate = SKMatrix.CreateRotationDegrees((float)StartRotation, StartFrame.MidX, StartFrame.MidY);
        var center = rotate.MapPoint((left + right) / 2, (top + bottom) / 2);
        float width = right - left, height = bottom - top;
        Set(new SKRect(center.X - width / 2, center.Y - height / 2, center.X + width / 2, center.Y + height / 2), StartRotation);
    }

    public void RotateTo(SKPoint start, SKPoint point, bool snap)
    {
        var center = new SKPoint(StartFrame.MidX, StartFrame.MidY);
        var a0 = Math.Atan2(start.Y - center.Y, start.X - center.X);
        var a1 = Math.Atan2(point.Y - center.Y, point.X - center.X);
        var rotation = StartRotation + (a1 - a0) * 180 / Math.PI;
        if (snap) rotation = Math.Round(rotation / 15) * 15;
        Set(Frame, Normalize(rotation));
    }

    private static double Normalize(double degrees)
    {
        degrees %= 360;
        if (degrees > 180) degrees -= 360;
        if (degrees <= -180) degrees += 360;
        return Math.Round(degrees, 2);
    }

    private SKRectI Area()
    {
        var area = SKRectI.Empty;
        foreach (var (layer, _) in layers) area = Geometry.Union(area, session.AffectedArea(layer));
        return area;
    }

    private void Update()
    {
        // A negative width or height means the frame was dragged through itself: a flip.
        bool flipX = Frame.Width < 0, flipY = Frame.Height < 0;
        var frame = Frame.Standardized;
        if (layers.Count == 1)
        {
            var (layer, start) = layers[0];
            layer.Transform = start with
            {
                X = frame.Left, Y = frame.Top, Width = frame.Width, Height = frame.Height, Rotation = Rotation,
                FlipHorizontal = start.FlipHorizontal ^ flipX, FlipVertical = start.FlipVertical ^ flipY,
                Distort = start.Distort == null ? null : ScaleDistort(start.Distort, frame.Width / start.Width, frame.Height / start.Height)
            };
            return;
        }
        double sx = frame.Width / Math.Max(1e-6, StartFrame.Width), sy = frame.Height / Math.Max(1e-6, StartFrame.Height);
        var delta = Rotation - StartRotation;
        var spin = SKMatrix.CreateRotationDegrees((float)delta, frame.MidX, frame.MidY);
        foreach (var (layer, start) in layers)
        {
            // Each layer's center follows the group frame; its own box scales and turns with it.
            var c = start.Center;
            var nx = (flipX ? StartFrame.Right - c.X : c.X - StartFrame.Left) * sx + frame.Left;
            var ny = (flipY ? StartFrame.Bottom - c.Y : c.Y - StartFrame.Top) * sy + frame.Top;
            var center = spin.MapPoint((float)nx, (float)ny);
            double w = start.Width * sx, h = start.Height * sy;
            layer.Transform = start with
            {
                X = center.X - w / 2, Y = center.Y - h / 2, Width = w, Height = h, Rotation = Normalize(start.Rotation * (flipX ^ flipY ? -1 : 1) + delta),
                FlipHorizontal = start.FlipHorizontal ^ flipX, FlipVertical = start.FlipVertical ^ flipY,
                Distort = start.Distort == null ? null : ScaleDistort(start.Distort, sx, sy)
            };
        }
    }

    /// <summary>True when any layer ended up somewhere other than where it started.</summary>
    public bool HasChanges => layers.Any(l => !Same(l.Layer.Transform, l.Start));

    /// <summary>Record equality compares the distort array by reference, so it is compared separately.</summary>
    public static bool Same(LayerTransform a, LayerTransform b) =>
        a with { Distort = null } == b with { Distort = null }
        && (a.Distort ?? []).AsSpan().SequenceEqual(b.Distort ?? []);

    private static float[] ScaleDistort(float[] distort, double sx, double sy) =>
        distort.Select((v, i) => (float)(v * (i % 2 == 0 ? sx : sy))).ToArray();

    /// <summary>Moves one corner freely (Ctrl-drag), distorting a single layer.</summary>
    public void DistortCorner(int corner, SKPoint point)
    {
        if (layers.Count != 1 || session.Transform != this) return;
        var before = Area();
        var (layer, start) = layers[0];
        var unrotate = SKMatrix.CreateRotationDegrees((float)-start.Rotation, start.Center.X, start.Center.Y);
        var p = unrotate.MapPoint(point);
        var distort = (float[])(start.Distort ?? new float[8]).Clone();
        float baseX = corner is 1 or 2 ? (float)start.Width : 0, baseY = corner is 2 or 3 ? (float)start.Height : 0;
        distort[corner * 2] = p.X - (float)start.X - baseX;
        distort[corner * 2 + 1] = p.Y - (float)start.Y - baseY;
        layer.Transform = start with { Distort = distort.All(v => Math.Abs(v) < 0.01f) ? null : distort };
        session.Invalidate(Geometry.Union(before, Area()));
    }
}

public sealed partial class EditorSession
{
    public TransformEdit? Transform { get; private set; }

    /// <summary>The raster layers a transform applies to: the selection, including everything inside selected folders.</summary>
    public List<Layer> TransformTargets() =>
        SelectedRoots().SelectMany(r => Document.Flatten([r])).Where(l => l.Pixels != null).Distinct().ToList();

    public TransformEdit? BeginTransform(string name = "Transform")
    {
        var targets = TransformTargets();
        if (targets.Count == 0) return null;
        Begin(name);
        return Transform = new TransformEdit(this, targets);
    }

    public void CommitTransform()
    {
        if (Transform == null) return;
        var changed = Transform.HasChanges;
        foreach (var layer in Transform.Layers)
        {
            // Live shapes are redrawn at their new size instead of being stretched.
            if (layer.Shape != null && layer.Pixels != null)
            {
                int w = Math.Max(1, (int)Math.Round(layer.Transform.Width)), h = Math.Max(1, (int)Math.Round(layer.Transform.Height));
                if (w != layer.Pixels.Width || h != layer.Pixels.Height) layer.Pixels = RenderShape(layer.Shape, w, h);
            }
        }
        Transform = null;
        if (changed) Commit(); else Cancel();
        InvalidateAll();
        LayersChanged?.Invoke();
    }

    public void CancelTransform()
    {
        if (Transform == null) return;
        Transform = null;
        Cancel();
    }

    /// <summary>Nudges the selected layers with the arrow keys.</summary>
    public void Nudge(int dx, int dy)
    {
        var targets = TransformTargets();
        if (targets.Count == 0) return;
        Apply("Nudge", () => { foreach (var layer in targets) layer.Transform = layer.Transform.Translated(dx, dy); });
        InvalidateAll();
    }

    /// <summary>Sets exact values from the transform inspector.</summary>
    public void SetTransform(Layer layer, LayerTransform transform)
    {
        if (layer.Pixels == null || TransformEdit.Same(transform, layer.Transform)) return;
        Apply("Transform", () =>
        {
            layer.Transform = transform;
            if (layer.Shape != null)
                layer.Pixels = RenderShape(layer.Shape, Math.Max(1, (int)Math.Round(transform.Width)), Math.Max(1, (int)Math.Round(transform.Height)));
        });
        InvalidateAll();
        LayersChanged?.Invoke();
    }
}
