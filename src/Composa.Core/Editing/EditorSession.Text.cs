using Composa.Model;
using Composa.Rendering;
using Composa.Text;
using SkiaSharp;

namespace Composa.Editing;

public sealed partial class EditorSession
{
    private Layer? textLayer;
    private TextStyle? textOriginal;
    private bool textIsNew;

    public static IReadOnlyList<string> FontFamilies { get; } = SKFontManager.Default.FontFamilies.Distinct().OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The text being typed on the canvas, or null when none is open.</summary>
    public TextEditor? TextEdit { get; private set; }
    /// <summary>The layer the open text edit is writing to.</summary>
    public Layer? TextEditLayer => TextEdit != null ? textLayer : null;
    public bool IsEditingText => TextEdit != null;

    /// <summary>Draws text into a bitmap just large enough to hold it (or exactly its paragraph box).</summary>
    public static SKBitmap RenderText(TextStyle style) => new TextLayout(style).Render();

    /// <summary>Adds a live text layer with its text starting at a document point. Leaves the edit open when <paramref name="commit"/> is false.</summary>
    public Layer AddText(SKPoint at, TextStyle style, bool commit = true)
    {
        style = style.Clamped();
        var pixels = RenderText(style);
        var layer = Layer.Raster(style.LayerName(), pixels, Math.Round(at.X) - TextLayout.Padding, Math.Round(at.Y) - TextLayout.Padding);
        layer.Text = style;
        Begin("Text");
        document.InsertAboveActive(layer);
        if (commit) Commit();
        EditingMask = false;
        Invalidate(AffectedArea(layer));
        LayersChanged?.Invoke();
        return layer;
    }

    /// <summary>
    /// Re-renders a text layer with new settings, keeping its scale, rotation and flips. Point text grows from the edge
    /// its alignment reads from; a paragraph box keeps its top-left corner. Call inside Begin/Commit.
    /// </summary>
    public void SetText(Layer layer, TextStyle style)
    {
        if (layer.Pixels == null) return;
        style = style.Clamped();
        var before = AffectedArea(layer);
        var t = layer.Transform;
        var old = layer.Pixels;
        double scaleX = t.Width / old.Width, scaleY = t.Height / old.Height;
        var unit = style.IsBox ? 0f : style.Alignment switch { TextAlignment.Center => 0.5f, TextAlignment.Right => 1f, _ => 0f };
        var anchorBefore = layer.Matrix.MapPoint(unit * old.Width, 0);
        var pixels = RenderText(style);
        layer.Text = style;
        layer.Pixels = pixels;
        layer.Transform = t with { Width = pixels.Width * scaleX, Height = pixels.Height * scaleY, Distort = null };
        var anchorAfter = layer.Matrix.MapPoint(unit * pixels.Width, 0);
        var moved = layer.Transform with { X = layer.Transform.X + anchorBefore.X - anchorAfter.X, Y = layer.Transform.Y + anchorBefore.Y - anchorAfter.Y };
        // Freshly drawn glyphs are only sharp when they sit on the pixel grid at their own size.
        if (moved.Rotation == 0 && Math.Abs(moved.Width - pixels.Width) < 0.01 && Math.Abs(moved.Height - pixels.Height) < 0.01)
            moved = moved with { X = Math.Round(moved.X), Y = Math.Round(moved.Y), Width = pixels.Width, Height = pixels.Height };
        layer.Transform = moved;
        if (layer.Mask is { } mask && (mask.Width != pixels.Width || mask.Height != pixels.Height)) layer.Mask = Resample(mask, pixels.Width, pixels.Height);
        layer.Name = style.LayerName();
        if (pendingBefore != null && !ReferenceEquals(old, pendingBefore.Find(layer.Id)?.Pixels)) { Pixels.Invalidate(old); old.Dispose(); }
        Invalidate(Geometry.Union(before, AffectedArea(layer)));
    }

    /// <summary>After scaling, text is redrawn at the matching font size instead of being stretched.</summary>
    private void RescaleText(Layer layer)
    {
        if (layer.Text is not { } style || layer.Pixels is not { } pixels) return;
        double factorX = layer.Transform.Width / pixels.Width, factorY = layer.Transform.Height / pixels.Height;
        if (Math.Abs(factorY - 1) < 0.01 && (!style.IsBox || Math.Abs(factorX - 1) < 0.01) || layer.Transform.Distort != null) return;
        var resized = style.IsBox ? style.Scaled(factorX, factorY) : style.Scaled(factorY);
        var center = layer.Transform.Center;
        var rendered = RenderText(resized);
        layer.Text = resized;
        layer.Transform = layer.Transform with { X = center.X - rendered.Width / 2.0, Y = center.Y - rendered.Height / 2.0, Width = rendered.Width, Height = rendered.Height };
        ReplaceLivePixels(layer, rendered);
    }

    // ---- Editing on the canvas ------------------------------------------------------------------------------------

    /// <summary>The topmost visible text layer whose box holds the point; the gaps between letters count too.</summary>
    public Layer? TextLayerAt(SKPoint point)
    {
        foreach (var layer in document.AllLayers().Reverse())
        {
            if (layer.Text == null || layer.Pixels == null || !document.IsEffectivelyVisible(layer) || !layer.Matrix.TryInvert(out var inverse)) continue;
            var local = inverse.MapPoint(point);
            if (local.X >= 0 && local.Y >= 0 && local.X <= layer.Pixels.Width && local.Y <= layer.Pixels.Height) return layer;
        }
        return null;
    }

    /// <summary>
    /// Starts new point text at a clicked document point: no box of its own, so what is typed decides how big the layer
    /// is. The click lands on the first baseline, as Photoshop's does, so the letters rise from where the pointer was.
    /// </summary>
    public TextEditor BeginText(SKPoint at)
    {
        FinishText();
        var style = TextDefaults with { Text = "", BoxWidth = null, BoxHeight = null, Color = (uint)Foreground | 0xFF000000 };
        var layer = AddText(new SKPoint(at.X, at.Y - new TextLayout(style).Ascent), style, commit: false);
        return OpenTextEditor(layer, isNew: true);
    }

    /// <summary>Starts new paragraph text that wraps inside a dragged-out box.</summary>
    public TextEditor BeginText(SKRect box)
    {
        FinishText();
        var style = (TextDefaults with
        {
            Text = "", Color = (uint)Foreground | 0xFF000000,
            BoxWidth = Math.Max(TextStyle.MinBox, Math.Round(box.Width)), BoxHeight = Math.Max(TextStyle.MinBox, Math.Round(box.Height))
        }).Clamped();
        var layer = AddText(new SKPoint(box.Left + TextLayout.Padding, box.Top + TextLayout.Padding), style, commit: false);
        return OpenTextEditor(layer, isNew: true);
    }

    /// <summary>Opens an existing text layer for typing. Returns null for anything that is not live text.</summary>
    public TextEditor? EditText(Layer layer)
    {
        if (layer.Text == null || layer.Pixels == null || document.Find(layer.Id) != layer) return null;
        if (TextEdit != null && textLayer == layer) return TextEdit;
        FinishText();
        SelectLayer(layer.Id);
        Begin("Edit Text");
        return OpenTextEditor(layer, isNew: false);
    }

    private TextEditor OpenTextEditor(Layer layer, bool isNew)
    {
        textLayer = layer;
        textIsNew = isNew;
        textOriginal = layer.Text;
        EditingMask = false;
        var editor = new TextEditor(layer.Text!);
        editor.Changed += SyncTextLayer;
        TextEdit = editor;
        Tool = Tool.Text;
        LayersChanged?.Invoke();
        return editor;
    }

    private void SyncTextLayer()
    {
        if (TextEdit is not { } editor || textLayer is not { } layer || document.Find(layer.Id) != layer) return;
        if (layer.Text == editor.Style) return;
        SetText(layer, editor.Style);
        TextChanged?.Invoke();
    }

    /// <summary>Raised while typing, after the layer has been redrawn.</summary>
    public event Action? TextChanged;

    /// <summary>
    /// Ends the open text edit, keeping what was typed. New text that is still empty is thrown away, and an edit that
    /// changed nothing leaves no undo step. Returns false when no edit was open.
    /// </summary>
    public bool FinishText()
    {
        if (TextEdit is not { } editor || textLayer is not { } layer) return false;
        var (isNew, original) = (textIsNew, textOriginal);
        CloseTextEditor();
        var style = editor.Style;
        if (isNew && style.Text.Trim().Length == 0) Cancel();
        else if (!isNew && style == original) Cancel();
        else
        {
            if (document.Find(layer.Id) == layer && layer.Text != style) SetText(layer, style);
            Commit();
            TextDefaults = style with { Text = "", BoxWidth = null, BoxHeight = null };
        }
        InvalidateAll();
        LayersChanged?.Invoke();
        return true;
    }

    /// <summary>Ends the open text edit and puts the layer back as it was (removing a new one).</summary>
    public void CancelText()
    {
        if (TextEdit == null) return;
        CloseTextEditor();
        Cancel();
        InvalidateAll();
        LayersChanged?.Invoke();
    }

    private void CloseTextEditor()
    {
        if (TextEdit is { } editor) editor.Changed -= SyncTextLayer;
        TextEdit = null;
        textLayer = null;
        textOriginal = null;
    }

    /// <summary>
    /// The style the Type tool's bar shows and edits: the text being typed, otherwise the active text layer (which
    /// this opens for editing), otherwise the defaults new text will take.
    /// </summary>
    public TextStyle CurrentTextStyle => TextEdit?.Style ?? ActiveLayer?.Text ?? TextDefaults;

    private const string StyleEditName = "Change Text Style";
    /// <summary>The last bar change made to a text layer that was not open for typing, and the revision it left.</summary>
    private (Guid LayerId, int Revision)? styleEdit;

    /// <summary>
    /// Changes a setting in the Type tool's bar: of the text being typed, otherwise of the active text layer, otherwise
    /// of the defaults new text takes. A text layer is restyled in place, without opening it for typing: the bar's
    /// field keeps the keyboard, and the change is only to the style, so nothing selects the text. Every keystroke in
    /// a field is a change of its own, so a run of them on one layer undoes as one step.
    /// </summary>
    public void ChangeTextStyle(Func<TextStyle, TextStyle> change)
    {
        if (TextEdit is { } editor) { editor.ChangeStyle(change); return; }
        if (ActiveLayer is not { Text: { } current } live) { TextDefaults = change(TextDefaults).Clamped() with { Text = "" }; return; }
        var style = change(current).Clamped();
        if (style == current) return;
        Apply(StyleEditName, () => SetText(live, style));
        // Exactly one revision on: the commit above, with no other edit (or undo) between the two changes.
        if (styleEdit is { } last && last.LayerId == live.Id && last.Revision == Revision - 1) History.MergeLast(StyleEditName);
        styleEdit = (live.Id, Revision);
        TextDefaults = style with { Text = "", BoxWidth = null, BoxHeight = null };
        TextChanged?.Invoke();
    }

    /// <summary>
    /// Resizes the open text's box (turning point text into a box of that size first), in layer pixels. The point of
    /// the layer at the anchor fractions (0 to 1 across its pixels) stays where it is, so dragging one edge leaves the
    /// opposite one in place.
    /// </summary>
    public void SetTextBox(double width, double height, double anchorX = 0, double anchorY = 0)
    {
        if (TextEdit is not { } editor || textLayer is not { Pixels: { } old } layer) return;
        var before = layer.Matrix.MapPoint((float)(anchorX * old.Width), (float)(anchorY * old.Height));
        editor.ChangeStyle(s => s with { BoxWidth = Math.Max(TextStyle.MinBox, Math.Round(width)), BoxHeight = Math.Max(TextStyle.MinBox, Math.Round(height)) });
        if (layer.Pixels is not { } pixels || (anchorX == 0 && anchorY == 0)) return;
        var after = layer.Matrix.MapPoint((float)(anchorX * pixels.Width), (float)(anchorY * pixels.Height));
        if (Math.Abs(after.X - before.X) < 1e-3 && Math.Abs(after.Y - before.Y) < 1e-3) return;
        var area = AffectedArea(layer);
        layer.Transform = layer.Transform with { X = layer.Transform.X + before.X - after.X, Y = layer.Transform.Y + before.Y - after.Y };
        Invalidate(Geometry.Union(area, AffectedArea(layer)));
    }

    /// <summary>Paints a text layer's letters in a color, keeping it editable text. Used by Fill with Foreground/Background.</summary>
    public bool RecolorText(Layer layer, SKColor color)
    {
        if (layer.Text is not { } style) return false;
        var tinted = style with { Color = (uint)color | 0xFF000000 };
        if (tinted == style) return true;
        if (TextEdit != null && textLayer == layer) { TextEdit.ChangeStyle(_ => tinted); return true; }
        Apply("Fill Text", () => SetText(layer, tinted));
        LayersChanged?.Invoke();
        return true;
    }
}
