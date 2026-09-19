using Avalonia;
using Avalonia.Input;
using Compositor.Editing;
using Compositor.Model;
using Compositor.Painting;
using Compositor.Selections;
using SkiaSharp;

namespace Compositor.App.Controls;

public sealed partial class CanvasView
{
    private enum Drag { None, Pan, Marquee, MoveSelection, MovePixels, Lasso, Crop, Stroke, Gradient, Shape, Transform, Eyedropper, ZoomScrub }

    private Drag drag;
    private MouseButton dragButton;

    /// <summary>True while the pointer is dragging out an edit; commands wait until it finishes.</summary>
    public bool IsDragging => drag is not (Drag.None or Drag.Pan) && !(drag == Drag.Lasso && session?.LassoKind == LassoKind.Polygonal);
    private Point pressScreen, cursorScreen;
    private SKPoint pressDocument, currentDocument;
    private bool cursorInside;
    private SelectionMode dragMode;
    private KeyModifiers dragModifiers;
    private readonly List<SKPoint> polygon = [];
    private SKRect? cropRect;
    private SKRect cropStart;
    private TransformHandle handle;
    private int distortCorner = -1;
    private bool spaceDown;
    private double scrubZoom;
    private readonly List<(SKPoint From, SKPoint To)> guides = [];
    private Layer? gradientLayer;
    private SKBitmap? gradientOriginal;

    public bool HasCrop => cropRect != null;
    public SKRect? CropRect => cropRect;

    private bool IsBrushTool => session?.Tool is Tool.Brush or Tool.SpotHealing or Tool.CloneStamp or Tool.Smear;

    public void ToolChanged()
    {
        CancelInteraction();
        if (session?.Tool != Tool.Crop) cropRect = null;
        UpdateCursor();
        InvalidateVisual();
    }

    public void CancelInteraction()
    {
        if (session == null) { drag = Drag.None; return; }
        switch (drag)
        {
            case Drag.Stroke: session.CancelStroke(); break;
            case Drag.Transform: session.CancelTransform(); break;
            case Drag.MovePixels: session.EndMovePixels(keep: false); break;
            case Drag.Gradient: session.Cancel(); gradientOriginal = null; break;
        }
        drag = Drag.None;
        polygon.Clear();
        guides.Clear();
        InvalidateVisual();
    }

    private void UpdateCursor()
    {
        var type = StandardCursorType.Arrow;
        if (session != null)
        {
            if (spaceDown || drag == Drag.Pan) type = StandardCursorType.Hand;
            else type = session.Tool switch
            {
                Tool.Hand => StandardCursorType.Hand,
                Tool.Marquee or Tool.Lasso or Tool.Wand or Tool.Crop or Tool.Gradient or Tool.Shape or Tool.Eyedropper => StandardCursorType.Cross,
                Tool.Text => StandardCursorType.Ibeam,
                Tool.Brush or Tool.SpotHealing or Tool.CloneStamp or Tool.Smear => StandardCursorType.None,
                Tool.Zoom => StandardCursorType.Cross,
                _ => StandardCursorType.Arrow
            };
        }
        Cursor = new Cursor(type);
    }

    private static SelectionMode ModeFor(KeyModifiers modifiers)
    {
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var alt = modifiers.HasFlag(KeyModifiers.Alt);
        return shift && alt ? SelectionMode.Intersect : shift ? SelectionMode.Add : alt ? SelectionMode.Subtract : SelectionMode.Replace;
    }

    private bool InsideSelection(SKPoint p)
    {
        var selection = session?.Selection;
        int x = (int)p.X, y = (int)p.Y;
        return selection != null && x >= 0 && y >= 0 && x < selection.Width && y < selection.Height && selection.GetPixel(x, y).Alpha >= 128;
    }

    // ---- Pointer ------------------------------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (session == null || drag != Drag.None && !(drag == Drag.Lasso && session.LassoKind == LassoKind.Polygonal)) return;
        var point = e.GetCurrentPoint(this);
        pressScreen = cursorScreen = point.Position;
        pressDocument = currentDocument = ToDocument(point.Position);
        dragButton = point.Properties.IsMiddleButtonPressed ? MouseButton.Middle : point.Properties.IsRightButtonPressed ? MouseButton.Right : MouseButton.Left;
        dragModifiers = e.KeyModifiers;
        e.Pointer.Capture(this);

        if (point.Properties.IsMiddleButtonPressed || spaceDown || (session.Tool == Tool.Hand && point.Properties.IsLeftButtonPressed))
        {
            drag = Drag.Pan;
            UpdateCursor();
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        switch (session.Tool)
        {
            case Tool.Move:
                if (session.CanMovePixels && InsideSelection(pressDocument) && session.BeginMovePixels(duplicate: alt)) drag = Drag.MovePixels;
                else BeginMove(control, e.ClickCount);
                break;
            case Tool.Marquee:
                dragMode = ModeFor(e.KeyModifiers & ~KeyModifiers.Control);
                if (control && InsideSelection(pressDocument) && session.BeginMovePixels(duplicate: alt)) { drag = Drag.MovePixels; break; }
                drag = dragMode == SelectionMode.Replace && InsideSelection(pressDocument) ? Drag.MoveSelection : Drag.Marquee;
                break;
            case Tool.Lasso:
                dragMode = polygon.Count == 0 ? ModeFor(e.KeyModifiers) : dragMode;
                if (session.LassoKind == LassoKind.Freehand)
                {
                    if (dragMode == SelectionMode.Replace && InsideSelection(pressDocument)) { drag = Drag.MoveSelection; break; }
                    polygon.Clear();
                    polygon.Add(pressDocument);
                    drag = Drag.Lasso;
                }
                else
                {
                    drag = Drag.Lasso;
                    var closes = polygon.Count > 2 && Distance(ToScreen(polygon[0]), point.Position) < 8;
                    if (e.ClickCount >= 2 || closes) FinishPolygon();
                    else polygon.Add(pressDocument);
                }
                break;
            case Tool.Wand:
                session.SelectWand((int)Math.Floor(pressDocument.X), (int)Math.Floor(pressDocument.Y), ModeFor(e.KeyModifiers));
                break;
            case Tool.Crop:
                handle = cropRect is { } crop ? HitFrame(Corners(crop), point.Position, allowRotate: false) : TransformHandle.None;
                if (handle == TransformHandle.None) { cropRect = null; handle = TransformHandle.BottomRight; cropStart = SKRect.Create(Snap(pressDocument.X), Snap(pressDocument.Y), 0, 0); }
                else cropStart = cropRect!.Value;
                drag = Drag.Crop;
                break;
            case Tool.Brush or Tool.SpotHealing or Tool.CloneStamp or Tool.Smear:
                if (alt && session.Tool == Tool.CloneStamp) { session.SetCloneSource(pressDocument); InvalidateVisual(); break; }
                if (alt && session.Tool == Tool.Brush) { PickColor(background: false); drag = Drag.Eyedropper; break; }
                if (session.BeginStroke(pressDocument, out var problem, lineFromLast: shift && session.LastStrokeEnd != null)) drag = Drag.Stroke;
                else if (problem != null) Problem?.Invoke(problem);
                break;
            case Tool.Gradient:
                if (session.EditableLayer is not { } target) { Problem?.Invoke("Select a pixel layer or a mask to draw a gradient on."); break; }
                session.Begin("Gradient");
                if (!session.IsEditingMask) session.EnsureCoversCanvas(target);
                gradientLayer = target;
                gradientOriginal = session.IsEditingMask ? target.Mask : target.Pixels;
                drag = Drag.Gradient;
                break;
            case Tool.Shape:
                drag = Drag.Shape;
                break;
            case Tool.Text:
                e.Pointer.Capture(null);
                TextRequested?.Invoke(pressDocument, LayerAt(pressDocument) is { Text: not null } hit ? hit : null);
                break;
            case Tool.Eyedropper:
                PickColor(alt);
                drag = Drag.Eyedropper;
                break;
            case Tool.Zoom:
                drag = Drag.ZoomScrub;
                scrubZoom = zoom;
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        var delta = position - cursorScreen;
        cursorScreen = position;
        cursorInside = true;
        if (session == null) return;
        currentDocument = ToDocument(position);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        switch (drag)
        {
            case Drag.Pan: PanBy(delta); break;
            case Drag.Stroke:
                foreach (var p in e.GetIntermediatePoints(this))
                    session.ContinueStroke(ToDocument(p.Position), e.Pointer.Type == PointerType.Pen ? p.Properties.Pressure : 1);
                break;
            case Drag.Lasso when session.LassoKind == LassoKind.Freehand:
                if (polygon.Count == 0 || Distance(ToScreen(polygon[^1]), position) >= 2) polygon.Add(currentDocument);
                break;
            case Drag.Crop: DragCrop(shift, alt); break;
            case Drag.MovePixels:
                float mx = currentDocument.X - pressDocument.X, my = currentDocument.Y - pressDocument.Y;
                if (shift) { if (Math.Abs(mx) > Math.Abs(my)) my = 0; else mx = 0; }
                session.MovePixelsBy((int)Math.Round(mx), (int)Math.Round(my));
                break;
            case Drag.Transform: DragTransform(shift, alt, e.KeyModifiers.HasFlag(KeyModifiers.Control)); break;
            case Drag.Gradient:
                if (gradientLayer != null && gradientOriginal != null) session.DrawGradient(gradientLayer, gradientOriginal, pressDocument, ConstrainAngle(currentDocument, shift));
                break;
            case Drag.Eyedropper: PickColor(alt && session.Tool == Tool.Eyedropper); break;
            case Drag.ZoomScrub:
                if (Math.Abs(position.X - pressScreen.X) > 4) ZoomTo(scrubZoom * Math.Pow(2, (position.X - pressScreen.X) / 120), pressScreen);
                break;
            case Drag.None when session.Tool == Tool.Move:
                UpdateMoveCursor(position);
                break;
        }
        dragModifiers = e.KeyModifiers;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        // Only the button that started a drag ends it; a stray right or middle click mid-stroke changes nothing.
        if (session == null || (drag != Drag.None && e.InitialPressMouseButton != dragButton)) return;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var moved = Distance(pressScreen, cursorScreen) > 2;
        var finished = drag;
        if (!(drag == Drag.Lasso && session.LassoKind == LassoKind.Polygonal)) drag = Drag.None;
        e.Pointer.Capture(null);

        switch (finished)
        {
            case Drag.Pan: UpdateCursor(); break;
            case Drag.Stroke: session.EndStroke(); break;
            case Drag.Marquee:
                if (!moved) { if (dragMode == SelectionMode.Replace) session.Deselect(); break; }
                var rect = MarqueeRect(shift && dragMode != SelectionMode.Add || shift && alt, false);
                if (session.MarqueeKind == MarqueeKind.Ellipse) session.SelectEllipse(rect, dragMode); else session.SelectRect(rect, dragMode);
                break;
            case Drag.MoveSelection:
                if (moved) session.MoveSelection((int)Math.Round(currentDocument.X - pressDocument.X), (int)Math.Round(currentDocument.Y - pressDocument.Y));
                else session.Deselect();
                break;
            case Drag.Lasso when session.LassoKind == LassoKind.Freehand:
                if (polygon.Count > 2) session.SelectPolygon(polygon.ToList(), dragMode);
                else if (dragMode == SelectionMode.Replace) session.Deselect();
                polygon.Clear();
                break;
            case Drag.Crop:
                if (cropRect is { } crop && (crop.Width < 1 || crop.Height < 1)) cropRect = null;
                ToolStateChanged?.Invoke();
                break;
            case Drag.MovePixels: session.EndMovePixels(keep: moved); break;
            case Drag.Transform:
                guides.Clear();
                if (moved) session.CommitTransform(); else session.CancelTransform();
                break;
            case Drag.Gradient:
                if (moved) session.Commit(); else session.Cancel();
                gradientOriginal = null;
                gradientLayer = null;
                break;
            case Drag.Shape:
                if (moved) session.AddShape(MarqueeRect(shift, alt));
                break;
            case Drag.ZoomScrub:
                if (!moved) ZoomTo(alt ? zoom / 1.5 : zoom * 1.5, pressScreen);
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        // Losing the pointer mid-drag (the window lost focus, a popup opened) must not leave an edit hanging open.
        if (IsDragging || drag == Drag.Pan) { CancelInteraction(); UpdateCursor(); }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        cursorInside = false;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (session == null) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            ZoomTo(zoom * Math.Pow(1.2, e.Delta.Y), e.GetPosition(this));
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) PanBy(new Vector(e.Delta.Y * 60, 0));
        else PanBy(new Vector(e.Delta.X * 60, e.Delta.Y * 60));
        currentDocument = ToDocument(e.GetPosition(this));
        e.Handled = true;
    }

    private void PickColor(bool background)
    {
        if (session == null) return;
        int x = (int)Math.Floor(currentDocument.X), y = (int)Math.Floor(currentDocument.Y);
        if (x < 0 || y < 0 || x >= session.Document.Width || y >= session.Document.Height) return;
        var color = session.Composite().GetPixel(x, y);
        if (color.Alpha == 0) return;
        if (background) session.Background = color.WithAlpha(255); else session.Foreground = color.WithAlpha(255);
        ToolStateChanged?.Invoke();
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static float Snap(float value) => MathF.Round(value);

    private static SKPoint ConstrainAngle(SKPoint from, SKPoint to, bool constrain)
    {
        if (!constrain) return to;
        float dx = to.X - from.X, dy = to.Y - from.Y;
        var angle = MathF.Round(MathF.Atan2(dy, dx) / (MathF.PI / 4)) * (MathF.PI / 4);
        var length = MathF.Sqrt(dx * dx + dy * dy);
        return new SKPoint(from.X + MathF.Cos(angle) * length, from.Y + MathF.Sin(angle) * length);
    }

    private SKPoint ConstrainAngle(SKPoint to, bool constrain) => ConstrainAngle(pressDocument, to, constrain);

    /// <summary>The rectangle dragged from the press point, optionally square and/or grown from its center.</summary>
    private SKRect MarqueeRect(bool square, bool fromCenter)
    {
        float dx = Snap(currentDocument.X) - Snap(pressDocument.X), dy = Snap(currentDocument.Y) - Snap(pressDocument.Y);
        if (square)
        {
            var side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            dx = side * (dx < 0 ? -1 : 1);
            dy = side * (dy < 0 ? -1 : 1);
        }
        float x = Snap(pressDocument.X), y = Snap(pressDocument.Y);
        var rect = fromCenter ? new SKRect(x - dx, y - dy, x + dx, y + dy) : new SKRect(x, y, x + dx, y + dy);
        return rect.Standardized;
    }

    // ---- Crop ---------------------------------------------------------------------------------------------------

    private void DragCrop(bool shift, bool alt)
    {
        if (session == null) return;
        var start = cropStart;
        float left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;
        float px = SnapToCanvas(Snap(currentDocument.X), session.Document.Width), py = SnapToCanvas(Snap(currentDocument.Y), session.Document.Height);
        if (handle == TransformHandle.Move)
        {
            float dx = Snap(currentDocument.X - pressDocument.X), dy = Snap(currentDocument.Y - pressDocument.Y);
            cropRect = SKRect.Create(start.Left + dx, start.Top + dy, start.Width, start.Height);
            return;
        }
        if (handle is TransformHandle.TopLeft or TransformHandle.Left or TransformHandle.BottomLeft) left = px;
        if (handle is TransformHandle.TopRight or TransformHandle.Right or TransformHandle.BottomRight) right = px;
        if (handle is TransformHandle.TopLeft or TransformHandle.Top or TransformHandle.TopRight) top = py;
        if (handle is TransformHandle.BottomLeft or TransformHandle.Bottom or TransformHandle.BottomRight) bottom = py;
        if (shift && start.Width > 0 && start.Height > 0)
        {
            var aspect = start.Width / start.Height;
            if (handle is TransformHandle.Left or TransformHandle.Right) bottom = top + Math.Abs(right - left) / aspect;
            else right = left + Math.Abs(bottom - top) * aspect * (right < left ? -1 : 1);
        }
        else if (shift)
        {
            var side = Math.Max(Math.Abs(right - left), Math.Abs(bottom - top));
            right = left + side * (right < left ? -1 : 1);
            bottom = top + side * (bottom < top ? -1 : 1);
        }
        if (alt)
        {
            // Symmetric cropping: the opposite edge mirrors the dragged one.
            float cx = start.Width > 0 ? start.MidX : start.Left, cy = start.Height > 0 ? start.MidY : start.Top;
            if (left != start.Left) right = 2 * cx - left; else if (right != start.Right) left = 2 * cx - right;
            if (top != start.Top) bottom = 2 * cy - top; else if (bottom != start.Bottom) top = 2 * cy - bottom;
        }
        cropRect = new SKRect(left, top, right, bottom).Standardized;
    }

    private float SnapToCanvas(float value, int extent)
    {
        var threshold = (float)(8 / UnitsPerPixel);
        if (Math.Abs(value) < threshold) return 0;
        if (Math.Abs(value - extent) < threshold) return extent;
        if (Math.Abs(value - extent / 2f) < threshold) return extent / 2f;
        return value;
    }

    public void ApplyCrop()
    {
        if (session == null || cropRect is not { } crop) return;
        var rect = new SKRectI((int)Math.Round(crop.Left), (int)Math.Round(crop.Top), (int)Math.Round(crop.Right), (int)Math.Round(crop.Bottom));
        cropRect = null;
        if (rect.Width >= 1 && rect.Height >= 1) session.Crop(rect);
        Fit();
        ToolStateChanged?.Invoke();
    }

    public void CancelCrop()
    {
        cropRect = null;
        InvalidateVisual();
        ToolStateChanged?.Invoke();
    }

    // ---- Move / transform ---------------------------------------------------------------------------------------

    private static SKPoint[] Corners(SKRect r) => [new(r.Left, r.Top), new(r.Right, r.Top), new(r.Right, r.Bottom), new(r.Left, r.Bottom)];

    /// <summary>The transform frame for the current selection, as document-space corners, or null.</summary>
    private SKPoint[]? CurrentFrame()
    {
        if (session == null) return null;
        if (session.Transform is { } edit) return edit.Corners();
        var targets = session.TransformTargets();
        if (targets.Count == 0) return null;
        if (targets.Count == 1) return new TransformFrame(targets[0].Transform).Corners();
        var bounds = SKRect.Empty;
        foreach (var layer in targets) bounds = bounds.IsEmpty ? layer.Bounds : SKRect.Union(bounds, layer.Bounds);
        return Corners(bounds);
    }

    private readonly record struct TransformFrame(LayerTransform Transform)
    {
        public SKPoint[] Corners()
        {
            var t = Transform;
            var rotate = SKMatrix.CreateRotationDegrees((float)t.Rotation, t.Center.X, t.Center.Y);
            float l = (float)t.X, tp = (float)t.Y, r = (float)(t.X + t.Width), b = (float)(t.Y + t.Height);
            return [rotate.MapPoint(l, tp), rotate.MapPoint(r, tp), rotate.MapPoint(r, b), rotate.MapPoint(l, b)];
        }
    }

    /// <summary>Which handle of a frame (document-space corners) is under a screen point.</summary>
    private TransformHandle HitFrame(SKPoint[] corners, Point screen, bool allowRotate)
    {
        var points = corners.Select(ToScreen).ToArray();
        Point Mid(int a, int b) => new((points[a].X + points[b].X) / 2, (points[a].Y + points[b].Y) / 2);
        (Point P, TransformHandle H)[] handles =
        [
            (points[0], TransformHandle.TopLeft), (points[1], TransformHandle.TopRight), (points[2], TransformHandle.BottomRight), (points[3], TransformHandle.BottomLeft),
            (Mid(0, 1), TransformHandle.Top), (Mid(1, 2), TransformHandle.Right), (Mid(2, 3), TransformHandle.Bottom), (Mid(3, 0), TransformHandle.Left)
        ];
        foreach (var (p, h) in handles) if (Distance(p, screen) <= 7) return h;
        var inside = Contains(points, screen);
        if (inside) return TransformHandle.Move;
        if (allowRotate) foreach (var p in points) if (Distance(p, screen) <= 26) return TransformHandle.Rotate;
        return TransformHandle.None;
    }

    private static bool Contains(Point[] polygon, Point p)
    {
        var inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            if (polygon[i].Y > p.Y != polygon[j].Y > p.Y && p.X < (polygon[j].X - polygon[i].X) * (p.Y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) + polygon[i].X)
                inside = !inside;
        return inside;
    }

    private void UpdateMoveCursor(Point position)
    {
        var type = StandardCursorType.Arrow;
        if (ShowTransformControls && CurrentFrame() is { } frame)
            type = HitFrame(frame, position, allowRotate: true) switch
            {
                TransformHandle.TopLeft or TransformHandle.BottomRight => StandardCursorType.TopLeftCorner,
                TransformHandle.TopRight or TransformHandle.BottomLeft => StandardCursorType.TopRightCorner,
                TransformHandle.Top or TransformHandle.Bottom => StandardCursorType.SizeNorthSouth,
                TransformHandle.Left or TransformHandle.Right => StandardCursorType.SizeWestEast,
                TransformHandle.Rotate => StandardCursorType.Cross,
                TransformHandle.Move => StandardCursorType.SizeAll,
                _ => StandardCursorType.Arrow
            };
        Cursor = new Cursor(type);
    }

    /// <summary>The topmost visible layer with a non-transparent pixel under a document point.</summary>
    private Layer? LayerAt(SKPoint p)
    {
        if (session == null) return null;
        foreach (var layer in session.Document.AllLayers().Reverse())
        {
            if (layer.Pixels == null || !session.Document.IsEffectivelyVisible(layer) || !layer.Matrix.TryInvert(out var inverse)) continue;
            var local = inverse.MapPoint(p);
            int x = (int)Math.Floor(local.X), y = (int)Math.Floor(local.Y);
            if (x < 0 || y < 0 || x >= layer.Pixels.Width || y >= layer.Pixels.Height) continue;
            if (layer.Pixels.GetPixel(x, y).Alpha > 12) return layer;
        }
        return null;
    }

    private void BeginMove(bool control, int clicks)
    {
        if (session == null) return;
        handle = ShowTransformControls && CurrentFrame() is { } frame ? HitFrame(frame, pressScreen, allowRotate: true) : TransformHandle.None;
        distortCorner = -1;
        var onHandle = handle is not (TransformHandle.None or TransformHandle.Move);
        if (!onHandle)
        {
            // Clicking pixels of another layer selects it (Ctrl-click or double-click always; otherwise only when the
            // click misses the current selection's frame entirely).
            var hit = LayerAt(pressDocument);
            if (hit != null && !session.Document.SelectedLayerIds.Contains(hit.Id) && (control || clicks >= 2 || handle == TransformHandle.None))
                session.SelectLayer(hit.Id, extend: dragModifiers.HasFlag(KeyModifiers.Shift));
            handle = TransformHandle.Move;
        }
        else if (control && handle is TransformHandle.TopLeft or TransformHandle.TopRight or TransformHandle.BottomRight or TransformHandle.BottomLeft)
        {
            distortCorner = handle switch { TransformHandle.TopLeft => 0, TransformHandle.TopRight => 1, TransformHandle.BottomRight => 2, _ => 3 };
        }
        if (session.BeginTransform(handle == TransformHandle.Move ? "Move" : handle == TransformHandle.Rotate ? "Rotate" : distortCorner >= 0 ? "Distort" : "Scale") == null)
        {
            Problem?.Invoke("Select a layer with pixels to move.");
            return;
        }
        drag = Drag.Transform;
    }

    private void DragTransform(bool shift, bool alt, bool control)
    {
        if (session?.Transform is not { } edit) return;
        guides.Clear();
        switch (handle)
        {
            case TransformHandle.Move:
                float dx = currentDocument.X - pressDocument.X, dy = currentDocument.Y - pressDocument.Y;
                if (shift) { if (Math.Abs(dx) > Math.Abs(dy)) dy = 0; else dx = 0; }
                dx = MathF.Round(dx); dy = MathF.Round(dy);
                if (!control) SnapMove(edit, ref dx, ref dy);
                edit.MoveBy(dx, dy);
                break;
            case TransformHandle.Rotate:
                edit.RotateTo(pressDocument, currentDocument, shift);
                break;
            default:
                if (distortCorner >= 0) edit.DistortCorner(distortCorner, currentDocument);
                else edit.Resize(handle, currentDocument, free: shift, fromCenter: alt);
                break;
        }
        ToolStateChanged?.Invoke();
    }

    /// <summary>Pulls a move onto the canvas's and other layers' edges and centers, recording the guides to draw.</summary>
    private void SnapMove(TransformEdit edit, ref float dx, ref float dy)
    {
        if (session == null) return;
        var threshold = (float)(6 / UnitsPerPixel);
        var rotate = SKMatrix.CreateRotationDegrees((float)edit.StartRotation, edit.StartFrame.MidX, edit.StartFrame.MidY);
        var box = rotate.MapRect(edit.StartFrame);
        var document = session.Document;
        var xs = new List<float> { 0, document.Width / 2f, document.Width };
        var ys = new List<float> { 0, document.Height / 2f, document.Height };
        var moving = edit.Layers.Select(l => l.Id).ToHashSet();
        foreach (var other in document.AllLayers().Where(l => l.Pixels != null && !moving.Contains(l.Id) && document.IsEffectivelyVisible(l)).Take(40))
        {
            var b = other.Bounds;
            xs.AddRange([b.Left, b.MidX, b.Right]);
            ys.AddRange([b.Top, b.MidY, b.Bottom]);
        }
        float bestX = threshold, bestY = threshold, snapX = float.NaN, snapY = float.NaN, addX = 0, addY = 0;
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
        dx += addX; dy += addY;
        if (!float.IsNaN(snapX)) guides.Add((new SKPoint(snapX, -100000), new SKPoint(snapX, 100000)));
        if (!float.IsNaN(snapY)) guides.Add((new SKPoint(-100000, snapY), new SKPoint(100000, snapY)));
    }

    // ---- Keyboard -----------------------------------------------------------------------------------------------

    private void FinishPolygon()
    {
        if (session == null) return;
        if (polygon.Count > 2) session.SelectPolygon(polygon.ToList(), dragMode);
        polygon.Clear();
        drag = Drag.None;
    }

    /// <summary>Tool keys that depend on what the canvas is doing. Returns true when the key was used.</summary>
    public bool HandleKeyDown(KeyEventArgs e)
    {
        if (session == null) return false;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        // Mid-drag only Escape (cancel) and Space (pan) mean anything; everything else waits for the drag to end.
        if (IsDragging && e.Key is not (Key.Escape or Key.Space)) return true;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return false;
        switch (e.Key)
        {
            case Key.Space:
                if (!spaceDown) { spaceDown = true; UpdateCursor(); }
                return true;
            case Key.Escape:
                if (drag != Drag.None || polygon.Count > 0) { CancelInteraction(); return true; }
                if (cropRect != null) { CancelCrop(); return true; }
                return false;
            case Key.Enter:
                if (polygon.Count > 0) { FinishPolygon(); InvalidateVisual(); return true; }
                if (cropRect != null) { ApplyCrop(); return true; }
                return false;
            case Key.Back or Key.Delete when polygon.Count > 0:
                polygon.RemoveAt(polygon.Count - 1);
                if (polygon.Count == 0) drag = Drag.None;
                InvalidateVisual();
                return true;
            case Key.OemOpenBrackets or Key.OemCloseBrackets when IsBrushTool:
                var grow = e.Key == Key.OemCloseBrackets;
                if (shift) session.Brush = session.Brush with { Hardness = Math.Clamp(session.Brush.Hardness + (grow ? 0.25 : -0.25), 0, 1) };
                else session.Brush = session.Brush with { Size = NextBrushSize(session.Brush.Size, grow) };
                ToolStateChanged?.Invoke();
                InvalidateVisual();
                return true;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                var step = shift ? 10 : 1;
                int dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0, dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
                if (session.Tool == Tool.Move) session.Nudge(dx, dy);
                else if (session.Tool is Tool.Marquee or Tool.Lasso or Tool.Wand && session.Selection != null) session.MoveSelection(dx, dy);
                else return false;
                return true;
        }
        if (e.Key is >= Key.D0 and <= Key.D9 && !shift)
        {
            var value = e.Key == Key.D0 ? 1.0 : (e.Key - Key.D0) / 10.0;
            if (IsBrushTool) session.Brush = session.Brush with { Opacity = value };
            else if (session.Tool == Tool.Gradient) session.GradientOpacity = value;
            else if (session.Tool == Tool.Move && session.ActiveLayer is { } layer)
            {
                session.Apply("Opacity", () => session.SetOpacity(layer, value));
                session.NotifyLayersChanged();
            }
            else return false;
            ToolStateChanged?.Invoke();
            return true;
        }
        return false;
    }

    public void HandleKeyUp(KeyEventArgs e)
    {
        if (e.Key != Key.Space || !spaceDown) return;
        spaceDown = false;
        UpdateCursor();
    }

    private static double NextBrushSize(double size, bool grow)
    {
        var step = size < 10 ? 1 : size < 50 ? 5 : size < 100 ? 10 : size < 300 ? 25 : 50;
        return Math.Clamp(grow ? size + step : size - (size <= 10 ? 1 : size <= 50 ? 5 : size <= 100 ? 10 : size <= 300 ? 25 : 50), 1, 2500);
    }
}
