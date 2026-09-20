using Compositor.Model;
using Compositor.Rendering;
using SkiaSharp;

namespace Compositor.Editing;

public sealed partial class EditorSession
{
    private const int TextPadding = 4;

    public static IReadOnlyList<string> FontFamilies { get; } = SKFontManager.Default.FontFamilies.Distinct().OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

    private static SKFont FontFor(TextStyle style)
    {
        var typeface = SKFontManager.Default.MatchFamily(style.FontFamily, new SKFontStyle(
            style.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
            style.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright)) ?? SKTypeface.Default;
        return new SKFont(typeface, (float)Math.Clamp(style.Size, 1, 4000)) { Subpixel = true, Edging = SKFontEdging.Antialias, Hinting = SKFontHinting.None };
    }

    /// <summary>Draws text into a bitmap just large enough to hold it.</summary>
    public static SKBitmap RenderText(TextStyle style)
    {
        using var font = FontFor(style);
        using var paint = new SKPaint { Color = new SKColor(style.Color), IsAntialias = true };
        var lines = (style.Text.Length == 0 ? " " : style.Text).Replace("\r", "").Split('\n');
        var metrics = font.Metrics;
        var lineHeight = font.Spacing * (float)Math.Clamp(style.LineSpacing, 0.5, 4);
        var widths = lines.Select(line => font.MeasureText(line)).ToArray();
        var width = (int)Math.Ceiling(Math.Max(1, widths.Max())) + TextPadding * 2;
        var height = (int)Math.Ceiling(lineHeight * (lines.Length - 1) + (metrics.Descent - metrics.Ascent)) + TextPadding * 2;
        var bitmap = Pixels.NewColor(Math.Min(width, Document.MaxSide), Math.Min(height, Document.MaxSide));
        using var canvas = new SKCanvas(bitmap);
        for (var i = 0; i < lines.Length; i++)
        {
            var x = style.Alignment switch
            {
                TextAlignment.Center => (bitmap.Width - widths[i]) / 2,
                TextAlignment.Right => bitmap.Width - TextPadding - widths[i],
                _ => TextPadding
            };
            canvas.DrawText(lines[i], x, TextPadding - metrics.Ascent + i * lineHeight, SKTextAlign.Left, font, paint);
        }
        return bitmap;
    }

    /// <summary>Adds a live text layer with its top-left corner at a document point. Leaves the edit open when <paramref name="commit"/> is false.</summary>
    public Layer AddText(SKPoint at, TextStyle style, bool commit = true)
    {
        var pixels = RenderText(style);
        var firstLine = style.Text.Split('\n')[0].Trim();
        var layer = Layer.Raster(firstLine.Length == 0 ? document.UniqueName("Text") : firstLine.Length > 24 ? firstLine[..24] + "…" : firstLine,
            pixels, Math.Round(at.X) - TextPadding, Math.Round(at.Y) - TextPadding);
        layer.Text = style;
        Begin("Text");
        document.InsertAboveActive(layer);
        if (commit) Commit();
        EditingMask = false;
        Invalidate(AffectedArea(layer));
        LayersChanged?.Invoke();
        return layer;
    }

    /// <summary>Re-renders a text layer with new settings, keeping its position, rotation and any scaling. Call inside Begin/Commit.</summary>
    public void SetText(Layer layer, TextStyle style)
    {
        if (layer.Pixels == null) return;
        var before = AffectedArea(layer);
        var t = layer.Transform;
        double scaleX = t.Width / layer.Pixels.Width, scaleY = t.Height / layer.Pixels.Height;
        var pixels = RenderText(style);
        var previous = layer.Pixels;
        layer.Text = style;
        // Anchor the edge the alignment reads from, so typing grows the text the way the eye expects.
        double width = pixels.Width * scaleX, height = pixels.Height * scaleY;
        var x = style.Alignment switch { TextAlignment.Center => t.X + (t.Width - width) / 2, TextAlignment.Right => t.X + t.Width - width, _ => t.X };
        layer.Transform = t with { X = x, Width = width, Height = height, Distort = null };
        ReplaceLivePixels(layer, pixels);
        var firstLine = style.Text.Split('\n')[0].Trim();
        if (firstLine.Length > 0) layer.Name = firstLine.Length > 24 ? firstLine[..24] + "…" : firstLine;
        if (pendingBefore != null && !ReferenceEquals(previous, pendingBefore.Find(layer.Id)?.Pixels)) { Pixels.Invalidate(previous); previous.Dispose(); }
        Invalidate(Geometry.Union(before, AffectedArea(layer)));
    }

    /// <summary>After scaling, text is redrawn at the matching font size instead of being stretched.</summary>
    private void RescaleText(Layer layer)
    {
        if (layer.Text is not { } style || layer.Pixels is not { } pixels) return;
        var factor = layer.Transform.Height / pixels.Height;
        if (Math.Abs(factor - 1) < 0.01 || layer.Transform.Distort != null) return;
        var resized = style with { Size = Math.Clamp(style.Size * factor, 1, 4000) };
        var center = layer.Transform.Center;
        var rendered = RenderText(resized);
        layer.Text = resized;
        layer.Transform = layer.Transform with { X = center.X - rendered.Width / 2.0, Y = center.Y - rendered.Height / 2.0, Width = rendered.Width, Height = rendered.Height };
        ReplaceLivePixels(layer, rendered);
    }
}
