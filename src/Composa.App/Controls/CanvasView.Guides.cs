using Avalonia;
using Avalonia.Input;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Controls;

/// <summary>Rulers along the top and left, guides dragged out of them, and the layout grid.</summary>
public sealed partial class CanvasView
{
    /// <summary>The rulers' thickness in control units.</summary>
    public const double RulerThickness = 18;
    private const double GuideHitDistance = 5;
    private static readonly SKColor GuideColor = new(0, 255, 255, 230);

    // A guide being created or moved; the document is changed only when the drag ends.
    private Guid? guideId;
    private GuideAxis guideAxis;
    private double guidePosition, guideOriginal;

    private bool Rulers => session?.View.ShowRulers == true;
    private double RulerInset => Rulers ? RulerThickness : 0;

    /// <summary>The View menu changed what is shown; rulers turning on push the picture out from under them.</summary>
    public void ViewOptionsChanged(bool rulersWereShown)
    {
        if (Rulers && !rulersWereShown) origin += new Vector(RulerThickness, RulerThickness);
        else if (!Rulers && rulersWereShown) origin -= new Vector(RulerThickness, RulerThickness);
        ClampOrigin();
        InvalidateVisual();
    }

    /// <summary>Which ruler a control point lies on, if any: the top strip makes horizontal guides, the left one vertical.</summary>
    private GuideAxis? RulerAt(Point point)
    {
        if (!Rulers) return null;
        if (point.Y < RulerThickness && point.X >= RulerThickness) return GuideAxis.Horizontal;
        if (point.X < RulerThickness && point.Y >= RulerThickness) return GuideAxis.Vertical;
        return null;
    }

    private bool OverRuler(Point point) => Rulers && (point.X < RulerThickness || point.Y < RulerThickness);

    /// <summary>The guides as shown, with a drag in progress standing in for the guide it moves.</summary>
    private IEnumerable<Guide> DisplayedGuides()
    {
        if (session == null) yield break;
        foreach (var guide in session.Guides)
        {
            if (drag == Drag.Guide && guideId == guide.Id) yield return guide with { Position = guidePosition };
            else yield return guide;
        }
        if (drag == Drag.Guide && guideId == null) yield return new Guide(Guid.Empty, guideAxis, guidePosition);
    }

    private Guide? HitGuide(Point point)
    {
        if (session == null || !session.View.ShowGuides || !session.CanEditGuides) return null;
        Guide? best = null;
        var bestDistance = GuideHitDistance;
        foreach (var guide in session.Guides)
        {
            var distance = guide.Axis == GuideAxis.Vertical ? Math.Abs(ToScreen(new SKPoint((float)guide.Position, 0)).X - point.X) : Math.Abs(ToScreen(new SKPoint(0, (float)guide.Position)).Y - point.Y);
            if (distance <= bestDistance) { bestDistance = distance; best = guide; }
        }
        return best;
    }

    private double GuidePositionAt(SKPoint document) => guideAxis == GuideAxis.Vertical ? document.X : document.Y;

    private bool BeginGuideDrag(Point point)
    {
        if (session == null || !session.CanEditGuides) return false;
        if (RulerAt(point) is { } axis)
        {
            guideId = null;
            guideAxis = axis;
            guidePosition = Snapped(GuidePositionAt(pressDocument), null);
            drag = Drag.Guide;
            return true;
        }
        if (session.Tool == Tool.Move && HitGuide(point) is { } guide)
        {
            guideId = guide.Id;
            guideAxis = guide.Axis;
            guidePosition = guideOriginal = guide.Position;
            drag = Drag.Guide;
            return true;
        }
        return false;
    }

    private double Snapped(double position, Guid? excluding) => session == null ? position : session.SnapGuidePosition(guideAxis, position, excluding, 6 / UnitsPerPixel);

    private void DragGuide()
    {
        if (session == null) return;
        guidePosition = Snapped(GuidePositionAt(currentDocument), guideId);
        if (guideId == null && !session.View.ShowGuides) session.View = session.View with { ShowGuides = true };
        Cursor = new Cursor(guideAxis == GuideAxis.Vertical ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth);
    }

    /// <summary>Released on a ruler: a new guide is dropped, an existing one deleted. Elsewhere the guide lands where it is.</summary>
    private void FinishGuideDrag(Point releasePoint)
    {
        if (session == null) return;
        if (OverRuler(releasePoint))
        {
            if (guideId is { } doomed) session.RemoveGuide(doomed);
        }
        else if (guideId is { } id)
        {
            if (guidePosition != guideOriginal) session.MoveGuide(id, Math.Round(guidePosition * 2) / 2);
        }
        else session.AddGuide(guideAxis, Math.Round(guidePosition * 2) / 2);
        guideId = null;
        UpdateCursor();
    }

    // ---- Drawing --------------------------------------------------------------------------------------------------

    /// <summary>Guides across the whole view and the layout grid over the document, under the tool overlays.</summary>
    private void CaptureGuidesAndGrid(List<Action<SKCanvas>> steps, SKMatrix view, float hair)
    {
        if (session == null) return;
        var document = session.Document;
        var size = new SKSize((float)Bounds.Width, (float)Bounds.Height);
        if (session.View.ShowGrid)
        {
            var units = (float)UnitsPerPixel;
            var subdivisions = LayoutGrid.Step * units >= 4;
            var xs = LayoutGrid.Lines(document.Width).Select(v => (float)v).ToArray();
            var ys = LayoutGrid.Lines(document.Height).Select(v => (float)v).ToArray();
            float width = document.Width, height = document.Height;
            steps.Add(canvas =>
            {
                using var minor = new SKPaint { Color = new SKColor(140, 140, 140, 70), StrokeWidth = hair, PathEffect = SKPathEffect.CreateDash([hair, hair * 2], 0) };
                using var major = new SKPaint { Color = new SKColor(180, 180, 180, 115), StrokeWidth = hair };
                foreach (var x in xs)
                {
                    var isMajor = LayoutGrid.IsMajor(x);
                    if (!isMajor && !subdivisions) continue;
                    canvas.DrawLine(view.MapPoint(x, 0), view.MapPoint(x, height), isMajor ? major : minor);
                }
                foreach (var y in ys)
                {
                    var isMajor = LayoutGrid.IsMajor(y);
                    if (!isMajor && !subdivisions) continue;
                    canvas.DrawLine(view.MapPoint(0, y), view.MapPoint(width, y), isMajor ? major : minor);
                }
            });
        }
        if (session.View.ShowGuides || drag == Drag.Guide)
        {
            var lines = DisplayedGuides().Select(g => g.Axis == GuideAxis.Vertical
                ? (view.MapPoint((float)g.Position, 0).X, true)
                : (view.MapPoint(0, (float)g.Position).Y, false)).ToArray();
            if (lines.Length > 0)
                steps.Add(canvas =>
                {
                    using var paint = new SKPaint { Color = GuideColor, StrokeWidth = hair };
                    foreach (var (at, vertical) in lines)
                    {
                        if (vertical) canvas.DrawLine(at, 0, at, size.Height, paint);
                        else canvas.DrawLine(0, at, size.Width, at, paint);
                    }
                });
        }
    }

    /// <summary>Numbered ticks about 70 points apart, using 1-2-5 steps in document pixels.</summary>
    internal static double RulerStep(double unitsPerPixel)
    {
        var target = 70 / Math.Max(unitsPerPixel, 1e-4);
        foreach (var nice in new double[] { 1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 2500, 5000, 10000, 20000, 25000 })
            if (nice >= target) return nice;
        return 50000;
    }

    /// <summary>The rulers, drawn last so they sit over everything, in control units.</summary>
    private Action<SKCanvas>? CaptureRulers(float scaling)
    {
        if (session == null || !Rulers) return null;
        var thickness = (float)RulerThickness;
        var width = (float)Bounds.Width;
        var height = (float)Bounds.Height;
        var units = UnitsPerPixel;
        var step = RulerStep(units);
        var minor = step / 10;
        var hair = 1f / scaling;
        var startX = ToDocument(new Point(thickness, 0)).X;
        var endX = ToDocument(new Point(width, 0)).X;
        var startY = ToDocument(new Point(0, thickness)).Y;
        var endY = ToDocument(new Point(0, height)).Y;
        var originX = origin.X;
        var originY = origin.Y;
        return canvas =>
        {
            using var background = new SKPaint { Color = new SKColor(0x30, 0x30, 0x30) };
            using var tick = new SKPaint { Color = new SKColor(0xA0, 0xA0, 0xA0), StrokeWidth = hair };
            using var edge = new SKPaint { Color = new SKColor(0x14, 0x14, 0x14), StrokeWidth = hair };
            using var font = new SKFont(SKTypeface.Default, 9) { Edging = SKFontEdging.SubpixelAntialias };
            using var label = new SKPaint { Color = new SKColor(0xC8, 0xC8, 0xC8), IsAntialias = true };
            canvas.DrawRect(0, 0, width, thickness, background);
            canvas.DrawRect(0, 0, thickness, height, background);
            canvas.DrawLine(0, thickness - hair / 2, width, thickness - hair / 2, edge);
            canvas.DrawLine(thickness - hair / 2, 0, thickness - hair / 2, height, edge);
            // Horizontal ruler.
            for (var value = Math.Floor(Math.Min(startX, endX) / minor) * minor; value <= Math.Max(startX, endX); value += minor)
            {
                var x = (float)(originX + value * units);
                if (x < thickness) continue;
                var remainder = Math.Abs(value % step);
                var isMajor = remainder < 1e-6 || Math.Abs(remainder - step) < 1e-6;
                var isMid = !isMajor && Math.Abs(value % (step / 2)) < 1e-6;
                var length = isMajor ? 8f : isMid ? 5f : 3f;
                canvas.DrawLine(x, thickness - length, x, thickness, tick);
                if (isMajor) canvas.DrawText(((long)Math.Round(value)).ToString(), x + 2, 9, SKTextAlign.Left, font, label);
            }
            // Vertical ruler, its labels turned to read downward.
            for (var value = Math.Floor(Math.Min(startY, endY) / minor) * minor; value <= Math.Max(startY, endY); value += minor)
            {
                var y = (float)(originY + value * units);
                if (y < thickness) continue;
                var remainder = Math.Abs(value % step);
                var isMajor = remainder < 1e-6 || Math.Abs(remainder - step) < 1e-6;
                var isMid = !isMajor && Math.Abs(value % (step / 2)) < 1e-6;
                var length = isMajor ? 8f : isMid ? 5f : 3f;
                canvas.DrawLine(thickness - length, y, thickness, y, tick);
                if (isMajor)
                {
                    canvas.Save();
                    canvas.Translate(9, y + 2);
                    canvas.RotateDegrees(90);
                    canvas.DrawText(((long)Math.Round(value)).ToString(), 0, 0, SKTextAlign.Left, font, label);
                    canvas.Restore();
                }
            }
            canvas.DrawRect(0, 0, thickness, thickness, background);
            canvas.DrawLine(4, thickness - 4, thickness - 4, 4, tick);
        };
    }
}
