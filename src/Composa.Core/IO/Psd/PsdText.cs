using System.Globalization;
using System.Text;
using Composa.Editing;
using Composa.Model;
using Composa.Text;
using SkiaSharp;

namespace Composa.IO.Psd;

/// <summary>
/// Reads a Photoshop 6 type layer (<c>TySh</c>) into this editor's text model, from the Type Tool Object Setting in
/// Adobe's Photoshop File Formats Specification: a version, a 2×3 transform, a text descriptor and a warp descriptor.
/// The engine dictionary inside <c>EngineData</c> supplies the font, size, color, tracking, leading and alignment.
/// Anything the model cannot hold (vertical text, shear, uneven scale) stays a raster, and the report says so.
/// </summary>
internal static class PsdText
{
    /// <summary>What the block held, before it is drawn: the style, where its anchor lands and how it is turned.</summary>
    public sealed record Source(TextStyle Style, List<string> Notes, SKPoint DocumentAnchor, double Rotation, bool FlipY, bool AnchorIsFrame);

    public const string RasterizedNote = "Editable Photoshop text becomes pixels and can't be retyped.";
    public const string FirstStyleNote = "Only the first text style was kept.";
    public const string WarpNote = "The Photoshop text warp was omitted.";
    public const string FauxNote = "Faux bold or faux italic was omitted.";
    public const string JustifyNote = "Full justification was imported as left alignment.";

    /// <summary>The type block read into a style, or null when it holds something this text model cannot carry.</summary>
    public static Source? Parse(Dictionary<string, byte[]> extra)
    {
        if (!(extra.TryGetValue("TySh", out var data) || extra.TryGetValue("tySh", out data)) || data.Length > 8_000_000) return null;
        try
        {
            var cursor = new PsdCursor(data);
            if (cursor.U16() != 1) return null;
            double xx = cursor.F64(), xy = cursor.F64(), yx = cursor.F64(), yy = cursor.F64(), tx = cursor.F64(), ty = cursor.F64();
            if (!new[] { xx, xy, yx, yy, tx, ty }.All(double.IsFinite)) return null;
            if (cursor.U16() != 50) return null;
            var text = PsdDescriptor.ReadVersioned(ref cursor);
            if (text == null) return null;
            if (PsdDescriptor.Enumeration(text, "Ornt") == "Vrtc") return null;
            if (Placement.From(xx, xy, yx, yy, tx, ty) is not { } placed) return null;

            var notes = new List<string>();
            if (cursor.Remaining >= 2 && cursor.U16() == 1 && PsdDescriptor.ReadVersioned(ref cursor) is { } warp
                && PsdDescriptor.Enumeration(warp, "warpStyle") is { } warpStyle && warpStyle is not ("warpNone" or "none"))
                notes.Add(WarpNote);

            var engine = PsdDescriptor.Data(text, "EngineData") is { } raw ? EngineData.Parse(raw) : null;
            var content = Cleaned(PsdDescriptor.Text(text, "Txt ") ?? PsdDescriptor.Text(text, "Txt"))
                ?? Cleaned(EngineData.String(EngineData.Walk(engine, "EngineDict", "Editor", "Text")));
            if (string.IsNullOrEmpty(content) || content.Length > TextStyle.MaxLength) return null;

            var style = new TextStyle { Text = content, Size = Math.Clamp(12 * placed.PixelScale, 1, 2000) };
            if (engine != null) style = ApplyStyle(style, engine, placed.PixelScale, notes);

            var anchor = new SKPoint((float)tx, (float)ty);
            var anchorIsFrame = false;
            if (Rect(text, "bounds") is { } bounds && Rect(text, "boundingBox") is { } glyphs
                && bounds.Width > glyphs.Width + 4 && bounds.Height > glyphs.Height + 4 && bounds.Width > 1 && bounds.Height > 1)
            {
                // A paragraph frame: the box, with this editor's padding around it, anchored at its top-left corner.
                style = style with
                {
                    BoxWidth = bounds.Width * placed.PixelScale + TextLayout.Padding * 2,
                    BoxHeight = bounds.Height * placed.PixelScale + TextLayout.Padding * 2
                };
                anchor = placed.Map(bounds.Left, bounds.Top);
                anchorIsFrame = true;
            }
            return new Source(style.Clamped(), notes, anchor, placed.Rotation, placed.FlipY, anchorIsFrame);
        }
        catch (PsdException) { return null; }
    }

    /// <summary>The text drawn as a live layer, placed so its anchor lands where Photoshop had it; null when it would not fit the budget.</summary>
    public static Layer? Place(Source source, string name, ref long remainingPixels)
    {
        var layout = new TextLayout(source.Style);
        if ((long)layout.Width * layout.Height > Math.Max(0, remainingPixels)) return null;
        remainingPixels -= (long)layout.Width * layout.Height;
        var pixels = layout.Render();
        // Point text is anchored on its first baseline at the edge its alignment reads from; a paragraph on its frame's corner.
        SKPoint imageAnchor;
        if (source.AnchorIsFrame) imageAnchor = new SKPoint(TextLayout.Padding, TextLayout.Padding);
        else
        {
            var first = layout.Lines[0];
            var unit = source.Style.Alignment switch { TextAlignment.Center => 0.5f, TextAlignment.Right => 1f, _ => 0f };
            imageAnchor = new SKPoint(first.X + first.VisibleWidth * unit, first.Baseline);
        }
        var layer = Layer.Raster(name, pixels);
        layer.Text = source.Style;
        layer.Transform = Transform(pixels.Width, pixels.Height, imageAnchor, source.DocumentAnchor, source.Rotation, source.FlipY);
        return layer;
    }

    /// <summary>Puts the image so that <paramref name="imageAnchor"/> maps onto <paramref name="documentAnchor"/> under the flip (about the center) and the clockwise rotation (about the center) the layer transform applies.</summary>
    private static LayerTransform Transform(int width, int height, SKPoint imageAnchor, SKPoint documentAnchor, double rotation, bool flipY)
    {
        double localX = imageAnchor.X - width / 2.0, localY = imageAnchor.Y - height / 2.0;
        if (flipY) localY = -localY;
        var radians = rotation * Math.PI / 180;
        double rotatedX = localX * Math.Cos(radians) - localY * Math.Sin(radians), rotatedY = localX * Math.Sin(radians) + localY * Math.Cos(radians);
        double centerX = documentAnchor.X - rotatedX, centerY = documentAnchor.Y - rotatedY;
        return new LayerTransform { X = centerX - width / 2.0, Y = centerY - height / 2.0, Width = width, Height = height, Rotation = Math.Round(rotation, 2), FlipVertical = flipY };
    }

    /// <summary>
    /// The type block's transform as uniform scale, rotation and an optional vertical flip. Shear and uneven scale
    /// return null. Engine sizes are in text-space units that the matrix maps into document pixels.
    /// </summary>
    private sealed record Placement(double PixelScale, double Rotation, bool FlipY, double Xx, double Xy, double Yx, double Yy, double Tx, double Ty)
    {
        public static Placement? From(double xx, double xy, double yx, double yy, double tx, double ty)
        {
            var scaleX = Math.Sqrt(xx * xx + yx * yx);
            if (scaleX <= 1e-6) return null;
            double cos = xx / scaleX, sin = yx / scaleX;
            double localX = cos * xy + sin * yy, localY = -sin * xy + cos * yy;
            var scaleY = Math.Abs(localY);
            if (scaleY <= 1e-6) return null;
            var largest = Math.Max(scaleX, scaleY);
            if (Math.Abs(localX) > 0.02 * largest || Math.Abs(scaleX - scaleY) > 0.02 * largest) return null;
            var flip = localY < 0;
            var sign = flip ? -1.0 : 1.0;
            return new Placement(scaleX, Math.Atan2(sin, cos) * 180 / Math.PI, flip, cos * scaleX, -sin * scaleX * sign, sin * scaleX, cos * scaleX * sign, tx, ty);
        }

        public SKPoint Map(double x, double y) => new((float)(Xx * x + Xy * y + Tx), (float)(Yx * x + Yy * y + Ty));
    }

    private static SKRect? Rect(Dictionary<string, object?> items, string key)
    {
        var rect = PsdDescriptor.Child(items, key);
        if (PsdDescriptor.Number(rect, "Left") is not { } left || PsdDescriptor.Number(rect, "Top ") is not { } top
            || PsdDescriptor.Number(rect, "Rght") is not { } right || PsdDescriptor.Number(rect, "Btom") is not { } bottom) return null;
        return new SKRect((float)left, (float)top, (float)right, (float)bottom);
    }

    private static string? Cleaned(string? text)
    {
        if (text == null) return null;
        text = text.TrimStart('﻿', '\0').TrimEnd('\0');
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    // ---- The engine dictionary's style --------------------------------------------------------------------------

    private static TextStyle ApplyStyle(TextStyle style, Dictionary<string, object?> engine, double pixelScale, List<string> notes)
    {
        var runs = EngineData.List(EngineData.Walk(engine, "EngineDict", "StyleRun", "RunArray"));
        var first = runs.Count > 0 ? runs[0] : engine;
        var sheet = EngineData.Walk(first, "StyleSheet", "StyleSheetData") ?? first;
        var points = EngineData.Number(EngineData.Walk(sheet, "FontSize")) ?? 12;
        if (!double.IsFinite(points) || points <= 0) return style;
        style = style with { Size = Math.Clamp(points * pixelScale, 1, 2000) };

        var fonts = EngineData.List(EngineData.Walk(engine, "ResourceDict", "FontSet"));
        var index = (int)Math.Round(EngineData.Number(EngineData.Walk(sheet, "Font")) ?? 0);
        if (index >= 0 && index < fonts.Count && EngineData.String(EngineData.Walk(fonts[index], "Name")) is { Length: > 0 } postScriptName)
        {
            var (family, bold, italic) = Font(postScriptName);
            style = style with { FontFamily = family, Bold = bold, Italic = italic };
            if (!EditorSession.FontFamilies.Contains(family, StringComparer.OrdinalIgnoreCase))
                notes.Add($"The font \"{family}\" isn't installed, so the text is drawn with the default font.");
        }
        var values = EngineData.List(EngineData.Walk(sheet, "FillColor", "Values")).Select(EngineData.Number).OfType<double>().ToList();
        if (values.Count > 0) style = style with { Color = Color(values) };
        var tracking = EngineData.Number(EngineData.Walk(sheet, "Tracking")) ?? 0;
        if (double.IsFinite(tracking)) style = style with { Tracking = Math.Clamp(tracking * style.Size / 1000, -100, 1000) };
        var auto = EngineData.Bool(EngineData.Walk(sheet, "AutoLeading")) ?? true;
        if (!auto && EngineData.Number(EngineData.Walk(sheet, "Leading")) is { } leading && double.IsFinite(leading) && leading > 0)
            style = style with { Leading = Math.Clamp(leading * pixelScale, 0, 5000) };
        if (EngineData.Bool(EngineData.Walk(sheet, "FauxBold")) == true || EngineData.Bool(EngineData.Walk(sheet, "FauxItalic")) == true) notes.Add(FauxNote);
        if (runs.Count > 1 && runs.Skip(1).Any(run => Signature(run) != Signature(first))) notes.Add(FirstStyleNote);

        var paragraphs = EngineData.List(EngineData.Walk(engine, "EngineDict", "ParagraphRun", "RunArray"));
        var justification = EngineData.Number(EngineData.Walk(paragraphs.Count > 0 ? paragraphs[0] : engine, "ParagraphSheet", "Properties", "Justification"));
        switch ((int)Math.Round(justification ?? 0))
        {
            case 0: style = style with { Alignment = TextAlignment.Left }; break;
            case 1: style = style with { Alignment = TextAlignment.Right }; break;
            case 2: style = style with { Alignment = TextAlignment.Center }; break;
            default: style = style with { Alignment = TextAlignment.Left }; notes.Add(JustifyNote); break;
        }
        return style;
    }

    /// <summary>What a run's style amounts to, so runs that differ only in things this model ignores still count as one style.</summary>
    private static string Signature(object? run)
    {
        var sheet = EngineData.Walk(run, "StyleSheet", "StyleSheetData") ?? run;
        var color = string.Join(",", EngineData.List(EngineData.Walk(sheet, "FillColor", "Values")).Select(EngineData.Number).OfType<double>().Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)));
        return string.Join("|", new[] { "Font", "FontSize", "Tracking", "AutoLeading", "Leading", "HorizontalScale", "VerticalScale", "FauxBold", "FauxItalic" }
            .Select(key => EngineData.Walk(sheet, key) switch { double d => d.ToString("0.###", CultureInfo.InvariantCulture), bool b => b.ToString(), _ => "" })) + "|" + color;
    }

    /// <summary>The fill color's channels: alpha, red, green, blue as 0…1 (or 0…255 from older writers), or three channels, or one gray.</summary>
    private static uint Color(List<double> values)
    {
        static byte Unit(double v) => (byte)Math.Round(v > 1 ? Math.Clamp(v, 0, 255) : Math.Clamp(v, 0, 1) * 255);
        var (r, g, b) = values.Count >= 4 ? (values[1], values[2], values[3]) : values.Count == 3 ? (values[0], values[1], values[2]) : (values[0], values[0], values[0]);
        return 0xFF000000u | (uint)Unit(r) << 16 | (uint)Unit(g) << 8 | Unit(b);
    }

    private static readonly string[] BoldWords = ["Bold", "Black", "Heavy", "Semibold", "SemiBold", "Demibold", "DemiBold", "Extrabold", "ExtraBold", "Ultrabold", "UltraBold"];
    private static readonly string[] ItalicWords = ["Italic", "Oblique"];

    /// <summary>
    /// A PostScript font name ("Helvetica-BoldOblique", "ArialMT", "TimesNewRomanPS-BoldMT") as a family plus the two
    /// styles this model has. The family is the part before the hyphen with Adobe's "MT" and "PS" tags removed, and its
    /// run-together words spaced when that names an installed family ("TimesNewRoman" to "Times New Roman").
    /// </summary>
    internal static (string Family, bool Bold, bool Italic) Font(string postScriptName)
    {
        var hyphen = postScriptName.IndexOf('-');
        var family = hyphen > 0 ? postScriptName[..hyphen] : postScriptName;
        var styleName = hyphen > 0 ? postScriptName[(hyphen + 1)..] : "";
        var bold = BoldWords.Any(w => styleName.Contains(w, StringComparison.OrdinalIgnoreCase));
        var italic = ItalicWords.Any(w => styleName.Contains(w, StringComparison.OrdinalIgnoreCase));
        if (hyphen < 0)
        {
            // Without a hyphen the style may be run into the family ("ArialBold").
            bold = BoldWords.Any(w => family.EndsWith(w, StringComparison.Ordinal));
            italic = ItalicWords.Any(w => family.EndsWith(w, StringComparison.Ordinal));
            foreach (var word in BoldWords.Concat(ItalicWords).OrderByDescending(w => w.Length)) if (family.EndsWith(word, StringComparison.Ordinal)) family = family[..^word.Length];
        }
        foreach (var tag in new[] { "PSMT", "MT", "PS" }) if (family.Length > tag.Length + 2 && family.EndsWith(tag, StringComparison.Ordinal)) { family = family[..^tag.Length]; break; }
        var installed = EditorSession.FontFamilies;
        if (!installed.Contains(family, StringComparer.OrdinalIgnoreCase))
        {
            var spaced = Spaced(family);
            if (installed.Contains(spaced, StringComparer.OrdinalIgnoreCase)) family = spaced;
            else family = installed.FirstOrDefault(f => f.Replace(" ", "").Equals(family, StringComparison.OrdinalIgnoreCase)) ?? family;
        }
        else family = installed.First(f => f.Equals(family, StringComparison.OrdinalIgnoreCase));
        return (family, bold, italic);
    }

    private static string Spaced(string name)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && char.IsLower(name[i - 1])) builder.Append(' ');
            builder.Append(name[i]);
        }
        return builder.ToString();
    }

    // ---- Photoshop's text engine dictionary ---------------------------------------------------------------------

    /// <summary>
    /// The engine dictionary is a small PostScript-like text: <c>&lt;&lt; /Key value &gt;&gt;</c> dictionaries, <c>[ ]</c>
    /// arrays, <c>/names</c>, numbers, booleans and <c>( )</c> strings, whose bytes are UTF-16 with a byte order mark or
    /// Latin-1. Values come back as double, bool, string, Dictionary or List.
    /// </summary>
    internal static class EngineData
    {
        public static Dictionary<string, object?>? Parse(byte[] data)
        {
            var start = IndexOf(data, "<<"u8);
            if (start < 0) return null;
            var cursor = new Cursor(data, start);
            return cursor.Value() as Dictionary<string, object?>;
        }

        private static int IndexOf(byte[] data, ReadOnlySpan<byte> token) => data.AsSpan().IndexOf(token);

        public static object? Walk(object? value, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (value is not Dictionary<string, object?> items || !items.TryGetValue(key, out value)) return null;
            }
            return value;
        }

        public static double? Number(object? value) => value is double d ? d : null;
        public static bool? Bool(object? value) => value is bool b ? b : null;
        public static string? String(object? value) => value as string;
        public static List<object?> List(object? value) => value as List<object?> ?? [];

        private sealed class Cursor(byte[] bytes, int index)
        {
            private int index = index;
            private int depth;

            private int? Peek(int ahead = 0) => index + ahead < bytes.Length ? bytes[index + ahead] : null;

            public object? Value()
            {
                SkipWhitespace();
                if (Peek() is not { } head) return null;
                if (head == '<') return Peek(1) == '<' ? Dictionary() : Hex();
                if (head == '[') return Array();
                if (head == '(') return Text();
                if (head == '/') { index++; return Token(); }
                if (head == '-' || head == '+' || head == '.' || head is >= '0' and <= '9') return NumberValue();
                if (TakeWord("true")) return true;
                if (TakeWord("false")) return false;
                if (TakeWord("null")) return "";
                return null;
            }

            private Dictionary<string, object?>? Dictionary()
            {
                if (!Take("<<") || ++depth > 64) return null;
                var items = new Dictionary<string, object?>();
                while (true)
                {
                    SkipWhitespace();
                    if (Peek() is null or '>') break;
                    if (Peek() != '/') return null;
                    index++;
                    var key = Token();
                    var value = Value();
                    if (value == null && Peek() is not '>' and not ']') return null;
                    items[key] = value;
                }
                depth--;
                return Take(">>") ? items : null;
            }

            private List<object?>? Array()
            {
                if (!Take("[") || ++depth > 64) return null;
                var items = new List<object?>();
                while (true)
                {
                    SkipWhitespace();
                    if (Peek() is null or ']') break;
                    var value = Value();
                    if (value == null) return null;
                    items.Add(value);
                }
                depth--;
                return Take("]") ? items : null;
            }

            private object? NumberValue()
            {
                var start = index;
                if (Peek() is '+' or '-') index++;
                while (Peek() is >= '0' and <= '9') index++;
                if (Peek() == '.') { index++; while (Peek() is >= '0' and <= '9') index++; }
                if (Peek() is 'e' or 'E') { index++; if (Peek() is '+' or '-') index++; while (Peek() is >= '0' and <= '9') index++; }
                var text = Encoding.ASCII.GetString(bytes, start, index - start);
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
            }

            private string? Text()
            {
                if (!Take("(")) return null;
                var raw = new List<byte>();
                while (Peek() is { } b)
                {
                    index++;
                    if (b == ')') return Decode(raw);
                    if (b != '\\') { raw.Add((byte)b); continue; }
                    if (Peek() is not { } escaped) return null;
                    index++;
                    switch (escaped)
                    {
                        case 'n': raw.Add(0x0A); break;
                        case 'r': raw.Add(0x0D); break;
                        case 't': raw.Add(0x09); break;
                        case >= '0' and <= '7':
                        {
                            var value = escaped - '0';
                            for (var i = 0; i < 2 && Peek() is >= '0' and <= '7'; i++) value = value * 8 + (bytes[index++] - '0');
                            raw.Add((byte)(value & 0xFF));
                            break;
                        }
                        case '\n' or '\r': break;
                        default: raw.Add((byte)escaped); break;
                    }
                }
                return null;
            }

            private string? Hex()
            {
                if (!Take("<")) return null;
                var nibbles = new List<byte>();
                while (Peek() is { } b && b != '>')
                {
                    index++;
                    var nibble = b switch { >= '0' and <= '9' => b - '0', >= 'a' and <= 'f' => b - 'a' + 10, >= 'A' and <= 'F' => b - 'A' + 10, _ => -1 };
                    if (nibble >= 0) nibbles.Add((byte)nibble);
                }
                if (!Take(">")) return null;
                var raw = new List<byte>();
                for (var i = 0; i + 1 < nibbles.Count; i += 2) raw.Add((byte)(nibbles[i] << 4 | nibbles[i + 1]));
                return Decode(raw);
            }

            private static string Decode(List<byte> raw)
            {
                if (raw.Count >= 2 && raw[0] == 0xFE && raw[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(raw.ToArray(), 2, raw.Count - 2);
                return Encoding.Latin1.GetString(raw.ToArray());
            }

            private string Token()
            {
                var start = index;
                while (Peek() is { } b && !IsDelimiter(b)) index++;
                return Encoding.ASCII.GetString(bytes, start, index - start);
            }

            private static bool IsDelimiter(int b) => b <= 0x20 || b is '/' or '<' or '>' or '[' or ']' or '(' or ')';

            private bool TakeWord(string word)
            {
                if (!Matches(word)) return false;
                var after = index + word.Length;
                if (after < bytes.Length && !IsDelimiter(bytes[after])) return false;
                index = after;
                return true;
            }

            private bool Take(string token)
            {
                if (!Matches(token)) return false;
                index += token.Length;
                return true;
            }

            private bool Matches(string token) => index + token.Length <= bytes.Length && bytes.AsSpan(index, token.Length).SequenceEqual(Encoding.ASCII.GetBytes(token));

            private void SkipWhitespace()
            {
                while (Peek() is { } b && (b <= 0x20 || b == '%'))
                {
                    if (b == '%') while (Peek() is { } next && next != '\n' && next != '\r') index++;
                    else index++;
                }
            }
        }
    }
}
