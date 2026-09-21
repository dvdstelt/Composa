namespace Compositor.Model;

public enum GuideAxis { Horizontal, Vertical }

/// <summary>A user-placed alignment line. Horizontal guides sit at a document Y; vertical ones at a document X.</summary>
public sealed record Guide(Guid Id, GuideAxis Axis, double Position)
{
    public Guide Offset(double dx, double dy) => this with { Position = Position + (Axis == GuideAxis.Vertical ? dx : dy) };

    public Guide Scaled(double sx, double sy) => this with { Position = Position * (Axis == GuideAxis.Vertical ? sx : sy) };

    /// <summary>Mirrors the guide when it runs across the flip, so it stays on the same content.</summary>
    public Guide Mirrored(bool horizontally, double center) =>
        (horizontally && Axis == GuideAxis.Vertical) || (!horizontally && Axis == GuideAxis.Horizontal) ? this with { Position = 2 * center - Position } : this;

    /// <summary>The guide after a quarter turn of the canvas: axes swap, positions follow the pixels.</summary>
    public Guide Turned(bool clockwise, int width, int height) => Axis == GuideAxis.Vertical
        ? this with { Axis = GuideAxis.Horizontal, Position = clockwise ? Position : width - Position }
        : this with { Axis = GuideAxis.Vertical, Position = clockwise ? height - Position : Position };

    public bool IsValid => double.IsFinite(Position) && Math.Abs(Position) <= 1_000_000;
}

/// <summary>Non-printing layout grid: a major line every 64 px, eight subdivisions (every 8 px).</summary>
public static class LayoutGrid
{
    public const double Spacing = 64;
    public const int Subdivisions = 8;
    public static double Step => Spacing / Subdivisions;

    /// <summary>Every grid line along a document edge, including subdivisions, in whole pixels.</summary>
    public static IEnumerable<double> Lines(double length)
    {
        if (!(length >= 0)) yield break;
        for (var value = 0.0; value <= length + 0.001; value += Step) yield return Math.Round(value);
    }

    public static bool IsMajor(double value) => Math.Abs(Math.Round(value) % Spacing) < 0.001;
}
