using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Composa.Editing;
using Composa.Model;
using Composa.Text;
using SkiaSharp;

namespace Composa.App.Controls;

/// <summary>The Type tool: typing straight onto the canvas, with the caret, selection and box handles drawn over the layer.</summary>
public sealed partial class CanvasView
{
    private TransformHandle textHandle;
    private (double Width, double Height) textBoxStart;
    private LayerTransform? textResizeStart;

    /// <summary>Raised when text editing starts or ends, so the options bar can swap its buttons.</summary>
    public event Action? TextEditingChanged;

    /// <summary>A document point in the coordinates of a text layer's pixels, which are also its layout's.</summary>
    private static SKPoint LocalTextPoint(Layer layer, SKPoint document) =>
        layer.Matrix.TryInvert(out var inverse) ? inverse.MapPoint(document) : document;

    private static bool InsideLayer(Layer layer, SKPoint document)
    {
        if (layer.Pixels == null) return false;
        var local = LocalTextPoint(layer, document);
        return local.X >= 0 && local.Y >= 0 && local.X <= layer.Pixels.Width && local.Y <= layer.Pixels.Height;
    }

    private SKPoint[]? TextBoxCorners()
    {
        if (session?.TextEditLayer is not { Pixels: { } pixels } layer) return null;
        return layer.Transform.Corners(pixels.Width, pixels.Height);
    }

    /// <summary>A press with the Type tool: a handle resizes the box, a click in the text places the caret, elsewhere starts new text.</summary>
    private void BeginTextPress(bool shift, int clicks)
    {
        if (session == null) return;
        if (session.TextEdit is { } editor && session.TextEditLayer is { Pixels: { } pixels } editing)
        {
            textHandle = TextBoxCorners() is { } corners ? HitFrame(corners, pressScreen, allowRotate: false) : TransformHandle.None;
            if (textHandle is not (TransformHandle.None or TransformHandle.Move))
            {
                var layout = editor.Layout;
                textBoxStart = (editor.Style.BoxWidth ?? layout.Width, editor.Style.BoxHeight ?? layout.Height);
                textResizeStart = editing.Transform;
                drag = Drag.TextResize;
                return;
            }
            if (InsideLayer(editing, pressDocument))
            {
                var local = LocalTextPoint(editing, pressDocument);
                if (clicks >= 2) editor.SelectWordAt(editor.Layout.IndexAt(local));
                else editor.ClickAt(local, shift);
                drag = Drag.TextSelect;
                return;
            }
            session.FinishText();
            TextEditingChanged?.Invoke();
        }
        if (session.TextLayerAt(pressDocument) is { } layer && session.EditText(layer) is { } opened)
        {
            opened.ClickAt(LocalTextPoint(layer, pressDocument), shift);
            drag = Drag.TextSelect;
            TextEditingChanged?.Invoke();
            return;
        }
        drag = Drag.TextBox;
    }

    private void DragTextSelection()
    {
        if (session?.TextEdit is not { } editor || session.TextEditLayer is not { } layer) return;
        editor.MoveTo(editor.Layout.IndexAt(LocalTextPoint(layer, currentDocument)), select: true);
    }

    /// <summary>Drags one edge or corner of the box; the opposite edge stays where it is.</summary>
    private void DragTextBox()
    {
        if (session?.TextEdit == null || session.TextEditLayer is not { Pixels: { } pixels } layer || textResizeStart is not { } start) return;
        // Work in the box's own unrotated space, in layout pixels.
        var unrotate = SKMatrix.CreateRotationDegrees((float)-start.Rotation, start.Center.X, start.Center.Y);
        var from = unrotate.MapPoint(pressDocument);
        var to = unrotate.MapPoint(currentDocument);
        double scaleX = start.Width / textBoxStart.Width, scaleY = start.Height / textBoxStart.Height;
        double dx = (to.X - from.X) / Math.Max(1e-6, scaleX), dy = (to.Y - from.Y) / Math.Max(1e-6, scaleY);
        if (start.FlipHorizontal) dx = -dx;
        if (start.FlipVertical) dy = -dy;
        var movesLeft = textHandle is TransformHandle.TopLeft or TransformHandle.Left or TransformHandle.BottomLeft;
        var movesRight = textHandle is TransformHandle.TopRight or TransformHandle.Right or TransformHandle.BottomRight;
        var movesTop = textHandle is TransformHandle.TopLeft or TransformHandle.Top or TransformHandle.TopRight;
        var movesBottom = textHandle is TransformHandle.BottomLeft or TransformHandle.Bottom or TransformHandle.BottomRight;
        var width = textBoxStart.Width + (movesRight ? dx : movesLeft ? -dx : 0);
        var height = textBoxStart.Height + (movesBottom ? dy : movesTop ? -dy : 0);
        session.SetTextBox(Math.Max(TextStyle.MinBox, width), Math.Max(TextStyle.MinBox, height), movesLeft ? 1 : 0, movesTop ? 1 : 0);
        _ = pixels;
        _ = layer;
    }

    private void FinishTextBoxDrag(bool moved)
    {
        if (session == null) return;
        if (moved) session.BeginText(MarqueeRect(false, false));
        else session.BeginText(pressDocument);
        Focus();
        TextEditingChanged?.Invoke();
    }

    /// <summary>Starts typing in an existing text layer (from the Layers panel or the Edit Text command).</summary>
    public void EditText(Layer layer)
    {
        if (session?.EditText(layer) is not { } editor) return;
        editor.MoveToDocumentEdge(end: true, select: false);
        Focus();
        InvalidateVisual();
        TextEditingChanged?.Invoke();
    }

    /// <summary>
    /// Keys while text is being typed. Editing keys are acted on here. A key that may produce a character is left
    /// unhandled on purpose: the X11 backend only sends text input for a key press nobody handled, so marking it
    /// handled would swallow the letter. The window keeps tool shortcuts off while text is open instead.
    /// </summary>
    private bool HandleTextKey(TextEditor editor, KeyEventArgs e)
    {
        if (session == null) return false;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var step = shift ? 10 : 1;
        switch (e.Key)
        {
            case Key.Escape:
                if (drag != Drag.None) { drag = Drag.None; InvalidateVisual(); return true; }
                session.CancelText();
                TextEditingChanged?.Invoke();
                return true;
            case Key.Enter or Key.Return when control:
                session.FinishText();
                TextEditingChanged?.Invoke();
                return true;
            case Key.Enter or Key.Return: editor.Insert("\n"); return true;
            case Key.Tab: editor.Insert("\t"); return true;
            case Key.Back: editor.Backspace(word: control); return true;
            case Key.Delete: editor.Delete(word: control); return true;
            // Alt with the arrows sets spacing, as in Photoshop: left and right the tracking, up and down the leading.
            case Key.Left when alt: session.ChangeTextStyle(s => s with { Tracking = s.Tracking - step }); return true;
            case Key.Right when alt: session.ChangeTextStyle(s => s with { Tracking = s.Tracking + step }); return true;
            case Key.Up when alt: session.ChangeTextStyle(s => s with { Leading = Math.Max(1, s.LineHeight - step) }); return true;
            case Key.Down when alt: session.ChangeTextStyle(s => s with { Leading = s.LineHeight + step }); return true;
            case Key.Left: editor.MoveHorizontal(-1, shift, control); return true;
            case Key.Right: editor.MoveHorizontal(1, shift, control); return true;
            case Key.Up: editor.MoveVertical(-1, shift); return true;
            case Key.Down: editor.MoveVertical(1, shift); return true;
            case Key.Home: if (control) editor.MoveToDocumentEdge(false, shift); else editor.MoveToLineEdge(false, shift); return true;
            case Key.End: if (control) editor.MoveToDocumentEdge(true, shift); else editor.MoveToLineEdge(true, shift); return true;
            case Key.A when control: editor.SelectAll(); return true;
            case Key.Z when control && shift: editor.Redo(); return true;
            case Key.Z when control: editor.Undo(); return true;
            case Key.Y when control: editor.Redo(); return true;
            case Key.C when control: _ = CopyText(editor, cut: false); return true;
            case Key.X when control: _ = CopyText(editor, cut: true); return true;
            case Key.V when control: _ = PasteText(editor); return true;
        }
        // Ctrl and Alt combinations that mean nothing here are swallowed rather than reaching the menus. Everything
        // else (letters, digits, space, punctuation, dead keys) is left for the text input that follows it.
        return control || alt;
    }

    private async Task CopyText(TextEditor editor, bool cut)
    {
        if (!editor.HasSelection) return;
        var text = editor.SelectedText;
        if (cut) editor.Insert("");
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            try { await clipboard.SetTextAsync(text); }
            catch { /* The desktop clipboard is a convenience; the edit itself has already happened. */ }
        }
    }

    private async Task PasteText(TextEditor editor)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        string? text = null;
        try { text = await clipboard.TryGetTextAsync(); }
        catch { /* Nothing usable on the clipboard. */ }
        if (!string.IsNullOrEmpty(text) && session?.TextEdit == editor) editor.Insert(text);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (session?.TextEdit is not { } editor || string.IsNullOrEmpty(e.Text)) return;
        var text = new string(e.Text.Where(c => c >= ' ' || c == '\n').ToArray());
        if (text.Length == 0) return;
        editor.Insert(text);
        e.Handled = true;
    }

    private void UpdateTextCursor(Point position)
    {
        if (controlHover) return; // Ctrl is held: the move cursor is showing what a drag would do.
        var type = StandardCursorType.Ibeam;
        if (session?.TextEdit != null && TextBoxCorners() is { } corners)
            type = HitFrame(corners, position, allowRotate: false) switch
            {
                TransformHandle.TopLeft or TransformHandle.BottomRight => StandardCursorType.TopLeftCorner,
                TransformHandle.TopRight or TransformHandle.BottomLeft => StandardCursorType.TopRightCorner,
                TransformHandle.Top or TransformHandle.Bottom => StandardCursorType.SizeNorthSouth,
                TransformHandle.Left or TransformHandle.Right => StandardCursorType.SizeWestEast,
                _ => StandardCursorType.Ibeam
            };
        Cursor = new Cursor(type);
    }

    /// <summary>The box, its handles, the selection and the caret over the text being typed, plus a new box being dragged out.</summary>
    private void CaptureTextOverlay(List<Action<SKCanvas>> steps, SKMatrix view, float hair)
    {
        if (session == null) return;
        if (drag == Drag.TextBox)
        {
            var rect = view.MapRect(MarqueeRect(false, false));
            steps.Add(canvas =>
            {
                using var line = new SKPaint { Color = Accent, StrokeWidth = hair, Style = SKPaintStyle.Stroke };
                canvas.DrawRect(rect, line);
            });
        }
        if (session.TextEdit is not { } editor || session.TextEditLayer is not { Pixels: { } pixels } layer) return;
        var toView = layer.Matrix.PostConcat(view);
        var corners = layer.Transform.Corners(pixels.Width, pixels.Height).Select(p => view.MapPoint(p)).ToArray();
        var layout = editor.Layout;
        var selection = editor.HasSelection ? layout.SelectionRects(editor.SelectionStart, editor.SelectionEnd) : [];
        var caret = editor.HasSelection || antsPhase % 8 >= 4 ? null : (ValueTuple<float, float, float>?)layout.CaretAt(editor.Caret);
        // Text that overflowed a paragraph box is clipped, and so is its caret.
        if (caret is { Item2: var caretTop } && layout.Style.IsBox && caretTop > layout.Height - TextLayout.Padding) caret = null;
        var overflows = layout.Overflows;
        steps.Add(canvas =>
        {
            using var path = new SKPath();
            path.AddPoly(corners, close: true);
            using var line = new SKPaint { Color = Accent, StrokeWidth = hair, Style = SKPaintStyle.Stroke, IsAntialias = true };
            canvas.DrawPath(path, line);
            DrawHandles(canvas, corners, hair);
            if (overflows)
            {
                // Text that doesn't fit is marked by a plus in the bottom-right handle, as in Photoshop.
                using var dark = new SKPaint { Color = SKColors.Black, StrokeWidth = hair * 1.5f, IsAntialias = true };
                var p = corners[2];
                canvas.DrawLine(p.X - 2.5f, p.Y, p.X + 2.5f, p.Y, dark);
                canvas.DrawLine(p.X, p.Y - 2.5f, p.X, p.Y + 2.5f, dark);
            }
            if (selection.Count > 0)
            {
                using var fill = new SKPaint { Color = Accent.WithAlpha(90), IsAntialias = true };
                foreach (var rect in selection)
                {
                    using var quad = new SKPath();
                    quad.AddPoly([toView.MapPoint(rect.Left, rect.Top), toView.MapPoint(rect.Right, rect.Top), toView.MapPoint(rect.Right, rect.Bottom), toView.MapPoint(rect.Left, rect.Bottom)], close: true);
                    canvas.DrawPath(quad, fill);
                }
            }
            if (caret is { } c)
            {
                var (x, top, bottom) = c;
                using var pen = new SKPaint { Color = Accent, StrokeWidth = Math.Max(hair * 1.5f, 1.5f), IsAntialias = true };
                using var halo = new SKPaint { Color = SKColors.White.WithAlpha(160), StrokeWidth = Math.Max(hair * 3.5f, 3.5f), IsAntialias = true };
                canvas.DrawLine(toView.MapPoint(x, top), toView.MapPoint(x, bottom), halo);
                canvas.DrawLine(toView.MapPoint(x, top), toView.MapPoint(x, bottom), pen);
            }
        });
    }
}
