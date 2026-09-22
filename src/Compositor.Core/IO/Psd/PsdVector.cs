using Compositor.Model;
using Compositor.Rendering;
using SkiaSharp;

namespace Compositor.IO.Psd;

/// <summary>
/// Turns Photoshop vector layers into what this editor has: a fill rectangle or ellipse becomes a live shape, any
/// other path is drawn once into pixels. Reads the <c>vogk</c>, <c>vmsk</c>/<c>vsms</c>, <c>SoCo</c> and <c>vstk</c>
/// blocks described in Adobe's Photoshop File Formats Specification.
/// </summary>
internal static class PsdVector
{
    public sealed record Live(ShapeStyle Style, SKRectI Bounds, List<string> Notes);
    public sealed record Raster(SKBitmap Image, SKRectI Bounds);

    /// <summary>The layer's fill color from its <c>SoCo</c> block, when it is a solid color layer.</summary>
    public static uint? FillColor(Dictionary<string, byte[]> extra) =>
        extra.TryGetValue("SoCo", out var soco) ? PsdDescriptor.Color(PsdDescriptor.Child(PsdDescriptor.ReadVersioned(soco), "Clr ")) : null;

    private static (bool Fill, bool Stroke, uint? StrokeColor, double StrokeWidth) StrokeSettings(Dictionary<string, byte[]> extra, bool hasFill)
    {
        if (!extra.TryGetValue("vstk", out var vstk)) return (hasFill, false, null, 1);
        var settings = PsdDescriptor.ReadVersioned(vstk);
        var fill = PsdDescriptor.Flag(settings, "fillEnabled") ?? hasFill;
        var stroke = PsdDescriptor.Flag(settings, "strokeEnabled") ?? false;
        var color = PsdDescriptor.Color(PsdDescriptor.Child(PsdDescriptor.Child(settings, "strokeStyleContent"), "Clr "));
        var width = PsdDescriptor.Number(settings, "strokeStyleLineWidth") ?? 1;
        return (fill, stroke, color, width);
    }

    /// <summary>A live rectangle, rounded rectangle or ellipse when the layer is one filled with a solid color.</summary>
    public static Live? LiveShape(Dictionary<string, byte[]> extra, SKSizeI canvas, long remainingPixels)
    {
        if (FillColor(extra) is not { } fill) return null;
        var (fillEnabled, strokeEnabled, _, _) = StrokeSettings(extra, true);
        if (!fillEnabled) return null;
        var origin = Origination(extra) ?? SharpRectangle(extra, canvas);
        if (origin == null) return null;
        var (kind, box, radius, notes) = origin.Value;
        // Path points are 8.24 fixed-point fractions of the canvas, so a corner drawn at 20 may read back as 19.99999.
        int left = (int)Math.Round(box.Left), top = (int)Math.Round(box.Top), right = (int)Math.Round(box.Right), bottom = (int)Math.Round(box.Bottom);
        var size = PixelSize(new SKRect(left, top, right, bottom), remainingPixels);
        if (size == null) return null;
        var bounds = new SKRectI(left, top, left + size.Value.Width, top + size.Value.Height);
        if (strokeEnabled) notes.Insert(0, "The Photoshop stroke isn't supported on shape layers and was omitted.");
        var style = new ShapeStyle(radius > 0 && kind == ShapeKind.Rectangle ? ShapeKind.RoundedRectangle : kind, fill, radius);
        return new Live(style, bounds, notes);
    }

    /// <summary>Any other vector layer drawn into pixels: the path filled and, when Photoshop drew one, stroked.</summary>
    public static Raster? Rasterized(Dictionary<string, byte[]> extra, SKSizeI canvas, long remainingPixels)
    {
        if (!(extra.TryGetValue("vmsk", out var mask) || extra.TryGetValue("vsms", out mask))) return null;
        using var path = Path(mask, canvas);
        if (path == null) return null;
        var fill = FillColor(extra);
        var (fillEnabled, strokeEnabled, strokeColor, strokeWidth) = StrokeSettings(extra, fill != null);
        fillEnabled &= fill != null;
        strokeEnabled &= strokeColor != null;
        if (!fillEnabled && !strokeEnabled) return null;
        if (!double.IsFinite(strokeWidth) || strokeWidth < 0 || strokeWidth > PsdReader.MaxSide) throw PsdException.TooLarge();
        var box = path.TightBounds;
        if (strokeEnabled) box.Inflate((float)Math.Ceiling(strokeWidth / 2 + 1), (float)Math.Ceiling(strokeWidth / 2 + 1));
        box = SKRect.Create(MathF.Floor(box.Left), MathF.Floor(box.Top), MathF.Ceiling(box.Right) - MathF.Floor(box.Left), MathF.Ceiling(box.Bottom) - MathF.Floor(box.Top));
        var size = PixelSize(box, remainingPixels);
        if (size == null) return null;
        var image = Pixels.NewColor(size.Value.Width, size.Value.Height);
        using (var surface = new SKCanvas(image))
        {
            surface.Translate(-box.Left, -box.Top);
            using var paint = new SKPaint { IsAntialias = true };
            if (fillEnabled)
            {
                paint.Style = SKPaintStyle.Fill;
                paint.Color = new SKColor(fill!.Value);
                surface.DrawPath(path, paint);
            }
            if (strokeEnabled)
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = (float)strokeWidth;
                paint.StrokeJoin = SKStrokeJoin.Miter;
                paint.StrokeMiter = 10;
                paint.StrokeCap = SKStrokeCap.Butt;
                paint.Color = new SKColor(strokeColor!.Value);
                surface.DrawPath(path, paint);
            }
        }
        Pixels.Invalidate(image);
        return new Raster(image, new SKRectI((int)box.Left, (int)box.Top, (int)box.Left + size.Value.Width, (int)box.Top + size.Value.Height));
    }

    /// <summary>Refuses sizes past the 30,000 px side and the remaining pixel budget; null for an empty box.</summary>
    private static SKSizeI? PixelSize(SKRect box, long remainingPixels)
    {
        if (!float.IsFinite(box.Left) || !float.IsFinite(box.Top) || !float.IsFinite(box.Width) || !float.IsFinite(box.Height)) return null;
        if (Math.Abs(box.Width) > PsdReader.MaxSide || Math.Abs(box.Height) > PsdReader.MaxSide) throw PsdException.TooLarge();
        if (box.Width < 1 || box.Height < 1) return null;
        int width = Math.Max(1, (int)Math.Round(box.Width)), height = Math.Max(1, (int)Math.Round(box.Height));
        if ((long)width * height > Math.Max(0, remainingPixels)) throw PsdException.TooLarge();
        return new SKSizeI(width, height);
    }

    /// <summary>Photoshop's own record of what the shape tool drew: 1 a rectangle, 2 a rounded rectangle, 5 an ellipse.</summary>
    private static (ShapeKind Kind, SKRect Box, double Radius, List<string> Notes)? Origination(Dictionary<string, byte[]> extra)
    {
        if (!extra.TryGetValue("vogk", out var vogk) || vogk.Length < 8) return null;
        // Two version numbers (1 and 16) lead the descriptor.
        var items = PsdDescriptor.TryRead(vogk.AsSpan(8));
        if (PsdDescriptor.List(items, "keyDescriptorList")?.OfType<Dictionary<string, object?>>().FirstOrDefault() is not { } shape) return null;
        var kind = PsdDescriptor.Number(shape, "keyOriginType") switch { 1 or 2 => ShapeKind.Rectangle, 5 => ShapeKind.Ellipse, _ => (ShapeKind?)null };
        if (kind == null) return null;
        var bbox = PsdDescriptor.Child(shape, "keyOriginShapeBBox");
        if (PsdDescriptor.Number(bbox, "Left") is not { } left || PsdDescriptor.Number(bbox, "Top ") is not { } top
            || PsdDescriptor.Number(bbox, "Rght") is not { } right || PsdDescriptor.Number(bbox, "Btom") is not { } bottom) return null;
        var box = new SKRect((float)left, (float)top, (float)right, (float)bottom);
        if (box.Width < 1 || box.Height < 1) return null;
        double radius = 0;
        var notes = new List<string>();
        if (kind == ShapeKind.Rectangle && PsdDescriptor.Child(shape, "keyOriginRRectRadii") is { } radii)
        {
            var corners = new[] { "topLeft", "topRight", "bottomRight", "bottomLeft" }.Select(c => PsdDescriptor.Number(radii, c)).ToArray();
            if (corners.All(c => c != null))
            {
                double low = corners.Min(c => c!.Value), high = corners.Max(c => c!.Value);
                if (high - low > 0.5) notes.Add("The rounded rectangle's corners differ; the largest radius was used for all four.");
                radius = Math.Max(0, high);
            }
        }
        return (kind.Value, box, radius, notes);
    }

    /// <summary>A path of four sharp, axis-aligned corners is a rectangle even without an origination record.</summary>
    private static (ShapeKind Kind, SKRect Box, double Radius, List<string> Notes)? SharpRectangle(Dictionary<string, byte[]> extra, SKSizeI canvas)
    {
        if (!(extra.TryGetValue("vmsk", out var mask) || extra.TryGetValue("vsms", out mask))) return null;
        var anchors = new List<SKPoint>();
        var subpaths = 0;
        foreach (var (type, body) in Records(mask))
        {
            if (type is 0 or 3) { if (++subpaths > 1) return null; continue; }
            if (type is not (1 or 2 or 4 or 5)) continue;
            var (incoming, anchor, outgoing) = Knot(body, canvas);
            if (Distance(incoming, anchor) > 0.5f || Distance(outgoing, anchor) > 0.5f) return null;
            anchors.Add(anchor);
        }
        if (anchors.Count != 4) return null;
        for (var i = 0; i < 4; i++)
        {
            SKPoint a = anchors[i], b = anchors[(i + 1) % 4];
            if (Math.Abs(a.X - b.X) > 0.5f && Math.Abs(a.Y - b.Y) > 0.5f) return null;
        }
        var box = new SKRect(anchors.Min(p => p.X), anchors.Min(p => p.Y), anchors.Max(p => p.X), anchors.Max(p => p.Y));
        return box.Width < 1 || box.Height < 1 ? null : (ShapeKind.Rectangle, box, 0, []);
    }

    /// <summary>The path records: 26 bytes each after an 8-byte header, knots as three 8.24 fixed-point points scaled to the canvas.</summary>
    private static IEnumerable<(int Type, byte[] Body)> Records(byte[] data)
    {
        for (var offset = 8; offset + 26 <= data.Length; offset += 26)
        {
            var type = (short)(data[offset] << 8 | data[offset + 1]);
            yield return (type, data[(offset + 2)..(offset + 26)]);
        }
    }

    private static (SKPoint In, SKPoint Anchor, SKPoint Out) Knot(byte[] body, SKSizeI canvas)
    {
        var cursor = new PsdCursor(body);
        SKPoint Point(ref PsdCursor c)
        {
            var y = c.I32() / (double)0x1000000;
            var x = c.I32() / (double)0x1000000;
            return new SKPoint((float)(x * canvas.Width), (float)(y * canvas.Height));
        }
        var incoming = Point(ref cursor);
        var anchor = Point(ref cursor);
        var outgoing = Point(ref cursor);
        return (incoming, anchor, outgoing);
    }

    private static float Distance(SKPoint a, SKPoint b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    public static SKPath? Path(byte[] data, SKSizeI canvas)
    {
        if (data.Length < 8 || canvas.Width <= 0 || canvas.Height <= 0) return null;
        var path = new SKPath();
        var closed = true;
        var first = true;
        SKPoint previousOut = default, firstIn = default, firstAnchor = default;
        void Finish(SKPath p)
        {
            if (first) return;
            if (closed) { p.CubicTo(previousOut, firstIn, firstAnchor); p.Close(); }
            first = true;
        }
        foreach (var (type, body) in Records(data))
        {
            switch (type)
            {
                case 0 or 3:
                    Finish(path);
                    closed = type == 0;
                    break;
                case 1 or 2 or 4 or 5:
                    var (incoming, anchor, outgoing) = Knot(body, canvas);
                    if (first) { path.MoveTo(anchor); firstIn = incoming; firstAnchor = anchor; first = false; }
                    else path.CubicTo(previousOut, incoming, anchor);
                    previousOut = outgoing;
                    break;
            }
        }
        Finish(path);
        if (path.IsEmpty) { path.Dispose(); return null; }
        return path;
    }
}
