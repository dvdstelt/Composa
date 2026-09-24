using Avalonia;
using Avalonia.Input;
using Composa.Editing;
using Composa.Model;
using Composa.Painting;
using Composa.Selections;
using SkiaSharp;

namespace Composa.App.Controls;

public sealed partial class CanvasView
{
    private enum Drag { None, Pan, Marquee, MoveSelection, MovePixels, Lasso, Crop, Stroke, Gradient, Shape, Transform, Eyedropper, ZoomScrub, TextBox, TextSelect, TextResize, Guide }

    private Drag drag;
    private MouseButton dragButton;
    private bool recapture;

    /// <summary>True while the pointer is dragging out an edit; commands wait until it finishes.</summary>
    public bool IsDragging => drag is not (Drag.None or Drag.Pan) && !(drag == Drag.Lasso && session?.LassoKind == LassoKind.Polygonal);
    private Point pressScreen, cursorScreen;
    private SKPoint pressDocument, currentDocument;
    private bool cursorInside;
    private SelectionMode dragMode;
    private KeyModifiers dragModifiers;
    /// <summary>A Ctrl-drag with another tool is moving layers: Photoshop's temporary Move tool.</summary>
    private bool temporaryMove;
    /// <summary>Ctrl is held over the canvas with another tool, so the cursor promises a move.</summary>
    private bool controlHover;
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
    // A drawn gradient stays adjustable (drag either end) until Enter, Escape, or another action settles it.
    private SKPoint gradientFrom, gradientTo;
    private bool gradientPending, gradientMovesStart;

    /// <summary>The gradient is still open only while the session's edit is; any other command will have committed it.</summary>
    private bool HasPendingGradient => gradientPending && session is { IsInteracting: true } && gradientLayer != null && gradientOriginal != null;

    private void SettleGradient(bool keep)
    {
        if (HasPendingGradient) { if (keep) session!.Commit(); else session!.Cancel(); }
        gradientPending = false;
        gradientOriginal = null;
        gradientLayer = null;
        InvalidateVisual();
    }

    public bool HasCrop => cropRect != null;
    public SKRect? CropRect => cropRect;

    private bool IsBrushTool => session?.Tool is Tool.Brush or Tool.SpotHealing or Tool.CloneStamp or Tool.Smear;

    public void ToolChanged()
    {
        CancelInteraction();
        if (session?.Tool != Tool.Crop) cropRect = null;
        // With a selection, the crop starts at its bounds, as Photoshop's does: C, then Enter, crops to it.
        else if (cropRect == null && session.Selection is { } selection && SelectionMask.Bounds(selection) is { IsEmpty: false } bounds)
            cropRect = ConstrainCrop(new SKRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
        UpdateCursor();
        InvalidateVisual();
    }

    /// <summary>The crop bar's ratio changed: the box, when there is one, is reshaped around its center to match.</summary>
    public void ChangeCropRatio()
    {
        if (cropRect is { } crop) cropRect = ConstrainCrop(crop);
        InvalidateVisual();
        ToolStateChanged?.Invoke();
    }

    /// <summary>Reshapes a box to the chosen ratio about its center, kept to the width or height that still fits the canvas.</summary>
    private SKRect ConstrainCrop(SKRect box)
    {
        if (session?.CropAspect is not { } aspect || box.Width < 1 || box.Height < 1) return box;
        var width = box.Width;
        var height = (float)(width / aspect);
        if (height > session.Document.Height) { height = session.Document.Height; width = (float)(height * aspect); }
        if (width > session.Document.Width) { width = session.Document.Width; height = (float)(width / aspect); }
        var shaped = SKRect.Create(box.MidX - width / 2, box.MidY - height / 2, width, height);
        // Slid back inside the canvas when the reshaping pushed it out.
        var dx = Math.Max(0, -shaped.Left) - Math.Max(0, shaped.Right - session.Document.Width);
        var dy = Math.Max(0, -shaped.Top) - Math.Max(0, shaped.Bottom - session.Document.Height);
        shaped.Offset(dx, dy);
        return shaped;
    }

    public void CancelInteraction()
    {
        if (session == null) { drag = Drag.None; return; }
        switch (drag)
        {
            case Drag.Stroke: session.CancelStroke(); break;
            case Drag.Transform: session.CancelTransform(); break;
            case Drag.MovePixels: session.EndMovePixels(keep: false); break;
            case Drag.Gradient: gradientPending = true; SettleGradient(keep: false); break;
        }
        if (drag != Drag.Gradient) SettleGradient(keep: true);
        // Text being typed stays open: only Escape, Ctrl+Enter or another action ends it.
        drag = Drag.None;
        temporaryMove = false;
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
            else if (temporaryMove || (controlHover && drag == Drag.None)) type = StandardCursorType.SizeAll; // The four-way move arrow says what Ctrl will do.
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

        // A press on a ruler drags a new guide out of it; with the Move tool a press on a guide moves it.
        if (BeginGuideDrag(point.Position)) { InvalidateVisual(); return; }
        if (OverRuler(point.Position)) return;

        // Ctrl-drag moves the current layers whatever tool is chosen, as Photoshop's temporary Move tool does. Two
        // Ctrl presses already mean something and keep it: the Marquee's Ctrl-drag inside a selection moves its
        // pixels, and Ctrl on a crop frame's handles adjusts the crop without snapping.
        var movingPixels = session.Tool == Tool.Marquee && session.CanMovePixels && InsideSelection(pressDocument);
        var adjustingCrop = session.Tool == Tool.Crop && cropRect is { } cropFrame && HitFrame(Corners(cropFrame), point.Position, allowRotate: false) != TransformHandle.None;
        if (control && session.Tool != Tool.Move && !movingPixels && !adjustingCrop) BeginTemporaryMove();
        else switch (session.Tool)
        {
            case Tool.Move:
                // A double-click on live text opens it for typing, without switching to the Type tool first.
                if (e.ClickCount >= 2 && BeginLiveTextEdit(shift)) break;
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
                if (session.WandMode == WandMode.Object) session.SelectObject((int)Math.Floor(pressDocument.X), (int)Math.Floor(pressDocument.Y), ModeFor(e.KeyModifiers));
                else session.SelectWand((int)Math.Floor(pressDocument.X), (int)Math.Floor(pressDocument.Y), ModeFor(e.KeyModifiers));
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
                session.ViewZoom = UnitsPerPixel;
                if (session.BeginStroke(pressDocument, out var problem, lineFromLast: shift && session.LastStrokeEnd != null)) drag = Drag.Stroke;
                else if (problem != null) Problem?.Invoke(problem);
                break;
            case Tool.Gradient:
                if (HasPendingGradient)
                {
                    var nearStart = Distance(ToScreen(gradientFrom), point.Position) <= 10;
                    if (nearStart || Distance(ToScreen(gradientTo), point.Position) <= 10)
                    {
                        gradientMovesStart = nearStart;
                        drag = Drag.Gradient;
                        break;
                    }
                }
                SettleGradient(keep: true);
                if (session.EditableLayer is not { } target) { Problem?.Invoke("Select a pixel layer or a mask to draw a gradient on."); break; }
                gradientMovesStart = false;
                gradientFrom = gradientTo = pressDocument;
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
                BeginTextPress(shift, e.ClickCount);
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
        PointerAt?.Invoke(new SKPointI((int)Math.Floor(currentDocument.X), (int)Math.Floor(currentDocument.Y)));
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        switch (drag)
        {
            case Drag.Pan: PanBy(delta); break;
            case Drag.Stroke:
                session.ViewZoom = UnitsPerPixel;
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
                if (gradientLayer == null || gradientOriginal == null) break;
                if (gradientMovesStart) gradientFrom = ConstrainAngle(gradientTo, currentDocument, shift);
                else gradientTo = ConstrainAngle(gradientFrom, currentDocument, shift);
                session.DrawGradient(gradientLayer, gradientOriginal, gradientFrom, gradientTo);
                break;
            case Drag.Eyedropper: PickColor(alt && session.Tool == Tool.Eyedropper); break;
            case Drag.ZoomScrub:
                if (Math.Abs(position.X - pressScreen.X) > 4) ZoomTo(scrubZoom * Math.Pow(2, (position.X - pressScreen.X) / 120), pressScreen);
                break;
            case Drag.TextSelect: DragTextSelection(); break;
            case Drag.TextResize: DragTextBox(); break;
            case Drag.Guide: DragGuide(); break;
            case Drag.None when session.Tool == Tool.Move:
                UpdateMoveCursor(position);
                break;
            case Drag.None when session.Tool == Tool.Text:
                UpdateTextCursor(position);
                break;
        }
        if (drag == Drag.None) SetControlHover(e.KeyModifiers.HasFlag(KeyModifiers.Control));
        dragModifiers = e.KeyModifiers;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        // Only the button that started a drag ends it; a stray right or middle click mid-stroke changes nothing.
        if (session == null) return;
        if (drag != Drag.None && e.InitialPressMouseButton != dragButton)
        {
            // Avalonia drops pointer capture on any button-up; the drag takes it back instead of being cancelled.
            recapture = true;
            return;
        }
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var moved = Distance(pressScreen, cursorScreen) > 2;
        var finished = drag;
        if (!(drag == Drag.Lasso && session.LassoKind == LassoKind.Polygonal)) drag = Drag.None;
        var wasTemporaryMove = temporaryMove;
        temporaryMove = false;
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
                // Released: the gradient stays open for adjustment. A click that drew nothing is dropped.
                if (moved || gradientPending) gradientPending = true;
                else { gradientPending = true; SettleGradient(keep: false); }
                ToolStateChanged?.Invoke();
                break;
            case Drag.Shape:
                if (!moved) break;
                if (session.ShapeKind == ShapeKind.Line) session.AddLine(pressDocument, ConstrainAngle(pressDocument, currentDocument, shift));
                else session.AddShape(MarqueeRect(shift, alt));
                break;
            case Drag.ZoomScrub:
                if (!moved) ZoomTo(alt ? zoom / 1.5 : zoom * 1.5, pressScreen);
                break;
            case Drag.TextBox: FinishTextBoxDrag(moved); break;
            case Drag.Guide: FinishGuideDrag(e.GetPosition(this)); break;
        }
        if (wasTemporaryMove) { controlHover = e.KeyModifiers.HasFlag(KeyModifiers.Control); UpdateCursor(); }
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (recapture)
        {
            recapture = false;
            e.Pointer.Capture(this);
            return;
        }
        // Losing the pointer mid-drag (the window lost focus, a popup opened) must not leave an edit hanging open.
        if (IsDragging || drag == Drag.Pan) { CancelInteraction(); UpdateCursor(); }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        cursorInside = false;
        PointerAt?.Invoke(null);
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
        float px = SnapToCanvas(Snap(currentDocument.X), horizontal: true), py = SnapToCanvas(Snap(currentDocument.Y), horizontal: false);
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
        // The bar's ratio always holds; without one, Shift keeps the box's proportions (a fresh box becomes square).
        var ratio = session.CropAspect ?? (shift ? start.Width > 0 && start.Height > 0 ? start.Width / (double)start.Height : 1 : null);
        if (ratio is { } aspect)
        {
            var movesLeft = handle is TransformHandle.TopLeft or TransformHandle.Left or TransformHandle.BottomLeft;
            var movesRight = handle is TransformHandle.TopRight or TransformHandle.Right or TransformHandle.BottomRight;
            var movesTop = handle is TransformHandle.TopLeft or TransformHandle.Top or TransformHandle.TopRight;
            var movesBottom = handle is TransformHandle.BottomLeft or TransformHandle.Bottom or TransformHandle.BottomRight;
            // The edge opposite the handle stays put; the pointer's edge sets the size along its own axis.
            float anchorX = movesLeft ? start.Right : start.Left, anchorY = movesTop ? start.Bottom : start.Top;
            float draggedX = movesLeft ? left : right, draggedY = movesTop ? top : bottom;
            float w = Math.Abs(draggedX - anchorX), h = Math.Abs(draggedY - anchorY);
            if ((movesLeft || movesRight) && !movesTop && !movesBottom)
            {
                h = (float)(w / aspect);
                left = draggedX >= anchorX ? anchorX : anchorX - w; right = left + w;
                top = start.MidY - h / 2; bottom = top + h;
            }
            else if ((movesTop || movesBottom) && !movesLeft && !movesRight)
            {
                w = (float)(h * aspect);
                top = draggedY >= anchorY ? anchorY : anchorY - h; bottom = top + h;
                left = start.MidX - w / 2; right = left + w;
            }
            else
            {
                if (w / aspect > h) h = (float)(w / aspect); else w = (float)(h * aspect);
                left = draggedX >= anchorX ? anchorX : anchorX - w; right = left + w;
                top = draggedY >= anchorY ? anchorY : anchorY - h; bottom = top + h;
            }
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

    /// <summary>Crop edges snap to the View > Snap To targets (without centers); Ctrl bypasses snapping.</summary>
    private float SnapToCanvas(float value, bool horizontal)
    {
        if (session == null || !session.View.Snap || dragModifiers.HasFlag(KeyModifiers.Control)) return value;
        return session.SnapCropEdge(value, horizontal, (float)(8 / UnitsPerPixel));
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
        if (targets.Count == 1)
        {
            // A distorted layer's handles sit on its corners, wherever they were dragged to.
            var target = targets[0];
            if (target.Pixels != null && target.Transform.Distort != null) return target.Transform.Corners(target.Pixels.Width, target.Pixels.Height);
            return new TransformFrame(target.Transform).Corners();
        }
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
        if (RulerAt(position) is { } axis) type = axis == GuideAxis.Vertical ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth;
        else if (ShowTransformControls && CurrentFrame() is { } frame && HitFrame(frame, position, allowRotate: true) is var hit && hit is not (TransformHandle.None or TransformHandle.Move))
            type = hit switch
            {
                TransformHandle.TopLeft or TransformHandle.BottomRight => StandardCursorType.TopLeftCorner,
                TransformHandle.TopRight or TransformHandle.BottomLeft => StandardCursorType.TopRightCorner,
                TransformHandle.Top or TransformHandle.Bottom => StandardCursorType.SizeNorthSouth,
                TransformHandle.Left or TransformHandle.Right => StandardCursorType.SizeWestEast,
                TransformHandle.Rotate => StandardCursorType.Cross,
                _ => StandardCursorType.Arrow
            };
        else if (HitGuide(position) is { } guide) type = guide.Axis == GuideAxis.Vertical ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth;
        else if (ShowTransformControls && CurrentFrame() is { } inside && HitFrame(inside, position, allowRotate: true) == TransformHandle.Move) type = StandardCursorType.SizeAll;
        Cursor = new Cursor(type);
    }

    /// <summary>The topmost visible layer with a non-transparent pixel under a document point.</summary>
    /// <summary>Whether the one layer being transformed already has a corner pulled out of place.</summary>
    private bool IsDistorted() => session?.TransformTargets() is [{ Pixels: not null, Transform.Distort: not null }];

    /// <summary>Whether <paramref name="layer"/> is painted above <paramref name="other"/>; layers are stored bottom to top.</summary>
    private bool IsAbove(Layer layer, Layer? other)
    {
        if (session == null || other == null) return true;
        var order = session.Document.AllLayers().ToList();
        return order.IndexOf(layer) > order.IndexOf(other);
    }

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

    /// <summary>
    /// The window reports every key press and release, so the cursor changes the moment Ctrl goes down or up rather
    /// than waiting for the pointer to move. The Ctrl key's own events carry the modifier state from before the key
    /// changed, so the key itself decides.
    /// </summary>
    public void ModifierKeyChanged(Key key, KeyModifiers modifiers, bool down)
    {
        if (drag != Drag.None) return;
        var control = key is Key.LeftCtrl or Key.RightCtrl ? down : modifiers.HasFlag(KeyModifiers.Control);
        SetControlHover(control);
    }

    private void SetControlHover(bool control)
    {
        var hover = control && cursorInside && session != null && session.Tool != Tool.Move;
        if (hover == controlHover) return;
        controlHover = hover;
        UpdateCursor();
        InvalidateVisual(); // The brush outline comes and goes with the move cursor.
    }

    /// <summary>
    /// Ctrl-drag with any other tool moves the current layers without switching tools. Like a plain Move-tool drag, a
    /// press on another layer's pixels that misses the current frame moves that layer instead; handles are not offered.
    /// </summary>
    private void BeginTemporaryMove()
    {
        if (session == null) return;
        handle = TransformHandle.Move;
        distortCorner = -1;
        var frame = CurrentFrame();
        var hit = LayerAt(pressDocument);
        if (hit != null && !session.Document.SelectedLayerIds.Contains(hit.Id) && (frame == null || HitFrame(frame, pressScreen, allowRotate: false) == TransformHandle.None))
            session.SelectLayer(hit.Id, extend: false);
        if (session.BeginTransform("Move") == null)
        {
            Problem?.Invoke("Select a layer with pixels to move.");
            return;
        }
        temporaryMove = true;
        drag = Drag.Transform;
        UpdateCursor();
    }

    private void BeginMove(bool control, int clicks)
    {
        if (session == null) return;
        handle = ShowTransformControls && CurrentFrame() is { } frame ? HitFrame(frame, pressScreen, allowRotate: true) : TransformHandle.None;
        distortCorner = -1;
        var onHandle = handle is not (TransformHandle.None or TransformHandle.Move);
        if (!onHandle)
        {
            // Clicking pixels of another layer selects it: Ctrl-click or double-click always; with Auto Select, also when
            // the click misses the current frame or lands on a layer stacked above the active one. A selected background
            // that covers the canvas contains every press, and keeping it would hide the layer painted on top.
            var hit = LayerAt(pressDocument);
            if (hit != null && !session.Document.SelectedLayerIds.Contains(hit.Id)
                && (control || clicks >= 2 || (AutoSelect && (handle == TransformHandle.None || IsAbove(hit, session.ActiveLayer)))))
                session.SelectLayer(hit.Id, extend: dragModifiers.HasFlag(KeyModifiers.Shift));
            handle = TransformHandle.Move;
        }
        else if ((control || IsDistorted()) && handle is TransformHandle.TopLeft or TransformHandle.TopRight or TransformHandle.BottomRight or TransformHandle.BottomLeft)
        {
            // Ctrl-dragging a corner distorts, as in Photoshop; once a layer is distorted its corners keep distorting.
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
                // Ctrl is what started a temporary move, so it cannot also mean "no snapping" there.
                if (!control || temporaryMove) SnapMove(edit, ref dx, ref dy);
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

    /// <summary>Pulls a move onto the View > Snap To targets (canvas, layers, grid, guides), recording the lines to draw.</summary>
    private void SnapMove(TransformEdit edit, ref float dx, ref float dy)
    {
        if (session == null || !session.View.Snap) return;
        var threshold = (float)(6 / UnitsPerPixel);
        var rotate = SKMatrix.CreateRotationDegrees((float)edit.StartRotation, edit.StartFrame.MidX, edit.StartFrame.MidY);
        var box = rotate.MapRect(edit.StartFrame);
        var moving = edit.Layers.Select(l => l.Id).ToHashSet();
        var (snappedX, snappedY, snapX, snapY) = session.SnapMove(box, moving, dx, dy, threshold);
        dx = snappedX; dy = snappedY;
        if (snapX is { } x) guides.Add((new SKPoint(x, -100000), new SKPoint(x, 100000)));
        if (snapY is { } y) guides.Add((new SKPoint(-100000, y), new SKPoint(100000, y)));
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
        if (session.TextEdit is { } editor) return HandleTextKey(editor, e);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (HasPendingGradient && drag == Drag.None)
        {
            // Undo while a gradient is still adjustable takes the gradient back, as it would once applied.
            if (e.Key == Key.Z && e.KeyModifiers == KeyModifiers.Control) { SettleGradient(keep: false); return true; }
            if (e.Key == Key.Escape) { SettleGradient(keep: false); return true; }
            if (e.Key == Key.Enter) { SettleGradient(keep: true); return true; }
        }
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
