using SkiaSharp;

namespace Compositor.Model;

/// <summary>
/// Places a layer's source pixels on the document without resampling them: the source rectangle is scaled to
/// <see cref="Width"/> x <see cref="Height"/>, flipped, optionally distorted corner by corner, rotated around
/// its center and moved to <see cref="X"/>, <see cref="Y"/>.
/// </summary>
public sealed record LayerTransform
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    /// <summary>Clockwise, in degrees.</summary>
    public double Rotation { get; init; }
    public bool FlipHorizontal { get; init; }
    public bool FlipVertical { get; init; }
    /// <summary>Offsets of the four corners (top-left, top-right, bottom-right, bottom-left) in unrotated local space.</summary>
    public float[]? Distort { get; init; }

    public static LayerTransform Identity(int width, int height) => new() { Width = width, Height = height };

    public SKPoint Center => new((float)(X + Width / 2), (float)(Y + Height / 2));

    public bool IsPureTranslation(int sourceWidth, int sourceHeight) =>
        Rotation == 0 && !FlipHorizontal && !FlipVertical && Distort == null
        && Width == sourceWidth && Height == sourceHeight && X == Math.Round(X) && Y == Math.Round(Y);

    /// <summary>Maps source pixel coordinates to document coordinates.</summary>
    public SKMatrix Matrix(int sourceWidth, int sourceHeight)
    {
        var sx = (float)(Width / Math.Max(1, sourceWidth));
        var sy = (float)(Height / Math.Max(1, sourceHeight));
        var m = SKMatrix.CreateScale(sx, sy);
        if (FlipHorizontal) m = m.PostConcat(SKMatrix.CreateScale(-1, 1, (float)Width / 2, 0));
        if (FlipVertical) m = m.PostConcat(SKMatrix.CreateScale(1, -1, 0, (float)Height / 2));
        if (Distort is { Length: 8 } d)
        {
            float w = (float)Width, h = (float)Height;
            var quad = new[]
            {
                new SKPoint(d[0], d[1]), new SKPoint(w + d[2], d[3]),
                new SKPoint(w + d[4], h + d[5]), new SKPoint(d[6], h + d[7])
            };
            m = m.PostConcat(Geometry.RectToQuad(w, h, quad));
        }
        m = m.PostConcat(SKMatrix.CreateTranslation((float)X, (float)Y));
        if (Rotation != 0) m = m.PostConcat(SKMatrix.CreateRotationDegrees((float)Rotation, Center.X, Center.Y));
        return m;
    }

    /// <summary>The four document-space corners: top-left, top-right, bottom-right, bottom-left.</summary>
    public SKPoint[] Corners(int sourceWidth, int sourceHeight)
    {
        var m = Matrix(sourceWidth, sourceHeight);
        return
        [
            m.MapPoint(0, 0), m.MapPoint(sourceWidth, 0),
            m.MapPoint(sourceWidth, sourceHeight), m.MapPoint(0, sourceHeight)
        ];
    }

    public SKRect Bounds(int sourceWidth, int sourceHeight)
    {
        var c = Corners(sourceWidth, sourceHeight);
        float l = c.Min(p => p.X), t = c.Min(p => p.Y), r = c.Max(p => p.X), b = c.Max(p => p.Y);
        return new SKRect(l, t, r, b);
    }

    public LayerTransform Translated(double dx, double dy) => this with { X = X + dx, Y = Y + dy };
}

public static class Geometry
{
    /// <summary>The perspective matrix carrying the rectangle (0,0,w,h) onto <paramref name="quad"/> (TL, TR, BR, BL).</summary>
    public static SKMatrix RectToQuad(float w, float h, SKPoint[] quad)
    {
        // Heckbert's square-to-quad mapping, composed with the rect-to-unit-square scale.
        double x0 = quad[0].X, y0 = quad[0].Y, x1 = quad[1].X, y1 = quad[1].Y;
        double x2 = quad[2].X, y2 = quad[2].Y, x3 = quad[3].X, y3 = quad[3].Y;
        double dx1 = x1 - x2, dx2 = x3 - x2, dx3 = x0 - x1 + x2 - x3;
        double dy1 = y1 - y2, dy2 = y3 - y2, dy3 = y0 - y1 + y2 - y3;
        double a, b, c, d, e, f, g, hh;
        if (Math.Abs(dx3) < 1e-9 && Math.Abs(dy3) < 1e-9)
        {
            a = x1 - x0; b = x2 - x1; c = x0; d = y1 - y0; e = y2 - y1; f = y0; g = 0; hh = 0;
        }
        else
        {
            var det = dx1 * dy2 - dx2 * dy1;
            if (Math.Abs(det) < 1e-12) return SKMatrix.Identity;
            g = (dx3 * dy2 - dx2 * dy3) / det;
            hh = (dx1 * dy3 - dx3 * dy1) / det;
            a = x1 - x0 + g * x1; b = x3 - x0 + hh * x3; c = x0;
            d = y1 - y0 + g * y1; e = y3 - y0 + hh * y3; f = y0;
        }
        var unit = new SKMatrix((float)a, (float)b, (float)c, (float)d, (float)e, (float)f, (float)g, (float)hh, 1);
        return SKMatrix.CreateScale(1 / w, 1 / h).PostConcat(unit);
    }

    /// <summary>A quad a perspective can take: no corner pulled past its neighbours, so it does not fold over itself.</summary>
    public static bool IsConvex(SKPoint[] quad)
    {
        if (quad.Length != 4) return false;
        var sign = 0;
        for (var i = 0; i < 4; i++)
        {
            SKPoint a = quad[i], b = quad[(i + 1) % 4], c = quad[(i + 2) % 4];
            var cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (Math.Abs(cross) < 1e-6) continue;
            var current = cross > 0 ? 1 : -1;
            if (sign == 0) sign = current;
            else if (sign != current) return false;
        }
        return sign != 0;
    }

    /// <summary>The affine map taking three points onto three others, or null when the source triangle has no area.</summary>
    public static SKMatrix? Affine(SKPoint s0, SKPoint s1, SKPoint s2, SKPoint d0, SKPoint d1, SKPoint d2)
    {
        double ux = s1.X - s0.X, uy = s1.Y - s0.Y, vx = s2.X - s0.X, vy = s2.Y - s0.Y;
        double det = ux * vy - vx * uy;
        if (Math.Abs(det) < 1e-9) return null;
        double px = d1.X - d0.X, py = d1.Y - d0.Y, qx = d2.X - d0.X, qy = d2.Y - d0.Y;
        double a = (px * vy - qx * uy) / det, c = (qx * ux - px * vx) / det;
        double b = (py * vy - qy * uy) / det, d = (qy * ux - py * vx) / det;
        return new SKMatrix((float)a, (float)c, (float)(d0.X - (a * s0.X + c * s0.Y)), (float)b, (float)d, (float)(d0.Y - (b * s0.X + d * s0.Y)), 0, 0, 1);
    }

    public static SKRectI RoundOut(SKRect r) =>
        new((int)Math.Floor(r.Left), (int)Math.Floor(r.Top), (int)Math.Ceiling(r.Right), (int)Math.Ceiling(r.Bottom));

    public static SKRectI Intersect(SKRectI a, SKRectI b)
    {
        var r = new SKRectI(Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top), Math.Min(a.Right, b.Right), Math.Min(a.Bottom, b.Bottom));
        return r.Width <= 0 || r.Height <= 0 ? SKRectI.Empty : r;
    }

    public static SKRectI Union(SKRectI a, SKRectI b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        return new SKRectI(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
    }
}
