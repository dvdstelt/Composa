using System.Text;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Text;

/// <summary>
/// Lays a <see cref="TextStyle"/> out in layer pixels: which characters make each line, where every character
/// boundary sits, and how big the bitmap is. Point text is as wide as its longest line; paragraph text wraps at
/// word boundaries inside its box. The same layout draws the pixels and places the caret, so what is edited is
/// exactly what is rendered.
/// </summary>
public sealed class TextLayout
{
    /// <summary>The gap between the text and the edge of its bitmap, in layer pixels.</summary>
    public const float Padding = 12;

    public sealed class Line
    {
        /// <summary>Character range, end exclusive. A hard line break's newline is not part of any line.</summary>
        public int Start { get; init; }
        public int End { get; init; }
        /// <summary>Where the line starts, after alignment.</summary>
        public float X { get; init; }
        public float Baseline { get; init; }
        /// <summary>The x offset of each character boundary from <see cref="X"/>: <c>Positions[k]</c> is where character <c>Start + k</c> begins, and the last entry is where the line ends.</summary>
        public float[] Positions { get; init; } = [];
        /// <summary>The width without trailing spaces, which alignment ignores.</summary>
        public float VisibleWidth { get; init; }
        public int Length => End - Start;
    }

    public TextStyle Style { get; }
    public string Text { get; }
    public int Width { get; }
    public int Height { get; }
    public float LineHeight { get; }
    public float Ascent { get; }
    public float Descent { get; }
    public IReadOnlyList<Line> Lines { get; }
    /// <summary>Paragraph text that does not fit its box; the lines beyond it are laid out but clipped.</summary>
    public bool Overflows { get; }

    private readonly SKTypeface typeface;

    public static SKTypeface TypefaceFor(TextStyle style) =>
        SKFontManager.Default.MatchFamily(style.FontFamily, new SKFontStyle(
            style.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
            style.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright)) ?? SKTypeface.Default;

    private SKFont MakeFont() => new(typeface, (float)Math.Clamp(Style.Size, 1, 4000)) { Subpixel = true, Edging = SKFontEdging.Antialias, Hinting = SKFontHinting.None };

    public TextLayout(TextStyle style)
    {
        Style = style = style.Clamped();
        Text = style.Text.Replace("\r", "");
        typeface = TypefaceFor(style);
        using var font = MakeFont();
        var metrics = font.Metrics;
        Ascent = -metrics.Ascent;
        Descent = metrics.Descent;
        LineHeight = (float)style.LineHeight;
        var tracking = (float)style.Tracking;
        var advances = Advances(font, Text, tracking);

        var available = style.IsBox ? (float)Math.Max(1, style.BoxWidth!.Value - 2 * Padding) : float.PositiveInfinity;
        var ranges = new List<(int Start, int End)>();
        var paragraphStart = 0;
        for (var i = 0; i <= Text.Length; i++)
        {
            if (i < Text.Length && Text[i] != '\n') continue;
            Wrap(paragraphStart, i, advances, available, ranges);
            paragraphStart = i + 1;
        }

        float contentWidth = 0;
        var visible = new float[ranges.Count];
        for (var i = 0; i < ranges.Count; i++)
        {
            var (start, end) = ranges[i];
            var last = end;
            while (last > start && Text[last - 1] == ' ') last--;
            visible[i] = Sum(advances, start, last);
            contentWidth = Math.Max(contentWidth, visible[i]);
        }
        if (style.IsBox)
        {
            Width = (int)Math.Round(style.BoxWidth!.Value);
            Height = (int)Math.Round(style.BoxHeight!.Value);
            contentWidth = available;
        }
        else
        {
            // A caret's worth of width so an empty line still has somewhere to type.
            Width = (int)Math.Max(TextStyle.MinBox, Math.Ceiling(contentWidth + Padding * 2 + style.Size * 0.1));
            var contentHeight = Math.Max(ranges.Count * LineHeight, Ascent + Descent);
            Height = (int)Math.Max(TextStyle.MinBox, Math.Ceiling(contentHeight + Padding * 2));
        }
        Width = Math.Min(Width, Document.MaxSide);
        Height = Math.Min(Height, Document.MaxSide);

        var lines = new List<Line>(ranges.Count);
        for (var i = 0; i < ranges.Count; i++)
        {
            var (start, end) = ranges[i];
            var positions = new float[end - start + 1];
            for (var k = 1; k <= end - start; k++) positions[k] = positions[k - 1] + advances[start + k - 1];
            var x = Padding + style.Alignment switch
            {
                TextAlignment.Center => (contentWidth - visible[i]) / 2,
                TextAlignment.Right => contentWidth - visible[i],
                _ => 0
            };
            lines.Add(new Line { Start = start, End = end, X = x, Baseline = Padding + Ascent + i * LineHeight, Positions = positions, VisibleWidth = visible[i] });
        }
        Lines = lines;
        Overflows = style.IsBox && lines.Count > 0 && lines[^1].Baseline + Descent > Height - Padding + 0.5f;
    }

    /// <summary>The advance of every character (a surrogate pair's second half advances nothing), tracking included.</summary>
    private static float[] Advances(SKFont font, string text, float tracking)
    {
        var advances = new float[text.Length];
        if (text.Length == 0) return advances;
        var glyphs = font.GetGlyphs(text);
        var widths = font.GetGlyphWidths(glyphs);
        var glyph = 0;
        for (var i = 0; i < text.Length && glyph < widths.Length; i++)
        {
            advances[i] = widths[glyph++] + tracking;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
        }
        return advances;
    }

    private static float Sum(float[] advances, int start, int end)
    {
        float total = 0;
        for (var i = start; i < end; i++) total += advances[i];
        return total;
    }

    /// <summary>Greedy word wrap of one paragraph; a word wider than the box breaks between characters.</summary>
    private void Wrap(int start, int end, float[] advances, float available, List<(int, int)> lines)
    {
        if (start == end) { lines.Add((start, end)); return; }
        var position = start;
        while (position < end)
        {
            float width = 0;
            int k = position, lastSpace = -1;
            for (; k < end; k++)
            {
                if (width + advances[k] > available && k > position) break;
                width += advances[k];
                if (Text[k] == ' ') lastSpace = k;
            }
            var lineEnd = k;
            if (k < end)
            {
                // Spaces at the break stay on this line (they take no visible width); mid-word, go back to the last
                // space so the next line starts with a letter.
                if (Text[k] == ' ') { while (lineEnd < end && Text[lineEnd] == ' ') lineEnd++; }
                else if (lastSpace >= position) lineEnd = lastSpace + 1;
            }
            lines.Add((position, lineEnd));
            position = lineEnd;
        }
    }

    // ---- Drawing --------------------------------------------------------------------------------------------------

    /// <summary>Draws the text into a fresh bitmap of exactly the layout's size.</summary>
    public SKBitmap Render()
    {
        var bitmap = Pixels.NewColor(Width, Height);
        using var canvas = new SKCanvas(bitmap);
        Draw(canvas);
        return bitmap;
    }

    public void Draw(SKCanvas canvas)
    {
        using var font = MakeFont();
        using var paint = new SKPaint { Color = new SKColor(Style.Color), IsAntialias = true };
        canvas.Save();
        if (Style.IsBox) canvas.ClipRect(new SKRect(0, 0, Width, Height));
        foreach (var line in Lines)
        {
            if (line.Length == 0) continue;
            var segment = Text.Substring(line.Start, line.Length);
            var glyphs = font.GetGlyphs(segment);
            if (glyphs.Length == 0) continue;
            var positions = new SKPoint[glyphs.Length];
            var glyph = 0;
            for (var k = 0; k < segment.Length && glyph < glyphs.Length; k++)
            {
                positions[glyph++] = new SKPoint(line.X + line.Positions[k], line.Baseline);
                if (char.IsHighSurrogate(segment[k]) && k + 1 < segment.Length && char.IsLowSurrogate(segment[k + 1])) k++;
            }
            using var builder = new SKTextBlobBuilder();
            var run = builder.AllocatePositionedRun(font, glyphs.Length);
            run.SetGlyphs(glyphs);
            run.SetPositions(positions);
            using var blob = builder.Build();
            if (blob != null) canvas.DrawText(blob, 0, 0, paint);
        }
        canvas.Restore();
    }

    // ---- Caret geometry -------------------------------------------------------------------------------------------

    /// <summary>The line holding a character index. At a soft wrap the caret belongs to the start of the next line.</summary>
    public int LineOf(int index)
    {
        index = Math.Clamp(index, 0, Text.Length);
        for (var i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            if (index < line.Start) return Math.Max(0, i - 1);
            if (index > line.End) continue;
            if (index == line.End && i + 1 < Lines.Count && Lines[i + 1].Start == line.End) return i + 1;
            return i;
        }
        return Lines.Count - 1;
    }

    /// <summary>Where the caret is drawn before a character index: its x and the top and bottom of the line.</summary>
    public (float X, float Top, float Bottom) CaretAt(int index)
    {
        index = Math.Clamp(index, 0, Text.Length);
        var line = Lines[LineOf(index)];
        var k = Math.Clamp(index - line.Start, 0, line.Positions.Length - 1);
        return (line.X + line.Positions[k], line.Baseline - Ascent, line.Baseline + Descent);
    }

    /// <summary>The character boundary nearest a point in layout pixels.</summary>
    public int IndexAt(SKPoint point)
    {
        if (Lines.Count == 0) return 0;
        var row = (int)Math.Floor((point.Y - Padding) / Math.Max(1e-3f, LineHeight));
        var line = Lines[Math.Clamp(row, 0, Lines.Count - 1)];
        return line.Start + NearestBoundary(line, point.X);
    }

    /// <summary>The index on the line above or below that keeps the caret's x, for the up and down arrows.</summary>
    public int IndexOnAdjacentLine(int index, int direction)
    {
        var current = LineOf(index);
        var target = current + direction;
        if (target < 0) return 0;
        if (target >= Lines.Count) return Text.Length;
        var x = CaretAt(index).X;
        var line = Lines[target];
        return line.Start + NearestBoundary(line, x);
    }

    private static int NearestBoundary(Line line, float x)
    {
        var best = 0;
        var bestDistance = float.MaxValue;
        for (var k = 0; k < line.Positions.Length; k++)
        {
            var distance = Math.Abs(line.X + line.Positions[k] - x);
            if (distance < bestDistance) { bestDistance = distance; best = k; }
        }
        return best;
    }

    /// <summary>The highlighted rectangles for a character range, one per line it touches.</summary>
    public List<SKRect> SelectionRects(int start, int end)
    {
        var rects = new List<SKRect>();
        if (end <= start) return rects;
        start = Math.Clamp(start, 0, Text.Length);
        end = Math.Clamp(end, 0, Text.Length);
        foreach (var line in Lines)
        {
            if (line.End < start || line.Start >= end) { if (!(line.Start == line.End && start <= line.Start && line.Start < end)) continue; }
            var from = Math.Clamp(start, line.Start, line.End);
            var to = Math.Clamp(end, line.Start, line.End);
            var left = line.X + line.Positions[from - line.Start];
            var right = line.X + line.Positions[to - line.Start];
            // A newline taken into the selection shows as a sliver past the line's end.
            if (end > line.End && to == line.End) right += Math.Max(4, Ascent * 0.3f);
            rects.Add(new SKRect(left, line.Baseline - Ascent, right, line.Baseline + Descent));
        }
        return rects;
    }

    /// <summary>The start of the word the index is in (or before it), for Ctrl+Left and double-click.</summary>
    public int WordStart(int index)
    {
        index = Math.Clamp(index, 0, Text.Length);
        while (index > 0 && !char.IsLetterOrDigit(Text[index - 1])) index--;
        while (index > 0 && char.IsLetterOrDigit(Text[index - 1])) index--;
        return index;
    }

    public int WordEnd(int index)
    {
        index = Math.Clamp(index, 0, Text.Length);
        while (index < Text.Length && !char.IsLetterOrDigit(Text[index])) index++;
        while (index < Text.Length && char.IsLetterOrDigit(Text[index])) index++;
        return index;
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        foreach (var line in Lines) builder.Append('[').Append(Text, line.Start, line.Length).Append(']');
        return builder.ToString();
    }
}
