using Compositor.Editing;
using SkiaSharp;

namespace Compositor.App.Controls;

public sealed partial class CanvasView
{
    private static readonly SKColor Accent = new(0x3D, 0x9B, 0xFF);

    /// <summary>Captures the tool overlay as a drawing closure over immutable values, safe to run on the render thread.</summary>
    private Action<SKCanvas>? CaptureOverlay(SKMatrix view)
    {
        if (session == null) return null;
        var steps = new List<Action<SKCanvas>>();
        var scaling = (float)Scaling;
        var hair = 1f / scaling;
        var tool = session.Tool;
        var phase = antsPhase;

        // Shapes being dragged out.
        if (drag is Drag.Marquee or Drag.Shape)
        {
            var shift = dragModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift);
            var alt = dragModifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt);
            var rect = drag == Drag.Shape ? MarqueeRect(shift, alt) : MarqueeRect(shift && dragMode != Selections.SelectionMode.Add || shift && alt, false);
            var ellipse = drag == Drag.Marquee ? session.MarqueeKind == MarqueeKind.Ellipse : session.ShapeKind == Model.ShapeKind.Ellipse;
            var radius = drag == Drag.Shape && session.ShapeKind == Model.ShapeKind.RoundedRectangle ? (float)session.ShapeCornerRadius : 0;
            var fill = drag == Drag.Shape ? session.Foreground : (SKColor?)null;
            steps.Add(canvas =>
            {
                using var path = new SKPath();
                if (ellipse) path.AddOval(rect); else if (radius > 0) path.AddRoundRect(rect, radius, radius); else path.AddRect(rect);
                if (fill is { } color)
                {
                    using var mapped = new SKPath();
                    path.Transform(in view, mapped);
                    using var paint = new SKPaint { Color = color, IsAntialias = true };
                    canvas.DrawPath(mapped, paint);
                }
                else DrawAnts(canvas, path, view, phase, scaling);
            });
        }

        if (drag == Drag.MoveSelection && selectionOutline is { } outline)
        {
            var shift = SKMatrix.CreateTranslation(MathF.Round(currentDocument.X - pressDocument.X), MathF.Round(currentDocument.Y - pressDocument.Y));
            var moved = shift.PostConcat(view);
            steps.Add(canvas => DrawAnts(canvas, outline, moved, phase, scaling));
        }

        if (polygon.Count > 0)
        {
            var points = polygon.ToArray();
            var rubberBand = session.LassoKind == LassoKind.Polygonal ? currentDocument : (SKPoint?)null;
            steps.Add(canvas =>
            {
                using var path = new SKPath();
                path.MoveTo(points[0]);
                foreach (var p in points.Skip(1)) path.LineTo(p);
                if (rubberBand is { } end) path.LineTo(end);
                DrawAnts(canvas, path, view, phase, scaling);
            });
        }

        if (drag == Drag.Gradient)
        {
            SKPoint from = view.MapPoint(pressDocument), to = view.MapPoint(ConstrainAngle(currentDocument, dragModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift)));
            steps.Add(canvas =>
            {
                using var dark = new SKPaint { Color = SKColors.Black, StrokeWidth = 3 * hair, IsAntialias = true, Style = SKPaintStyle.Stroke };
                using var light = new SKPaint { Color = SKColors.White, StrokeWidth = hair, IsAntialias = true, Style = SKPaintStyle.Stroke };
                canvas.DrawLine(from, to, dark);
                canvas.DrawLine(from, to, light);
                foreach (var p in new[] { from, to })
                {
                    canvas.DrawCircle(p, 4, dark);
                    canvas.DrawCircle(p, 4, light);
                }
            });
        }

        if (tool == Tool.Crop && cropRect is { } crop)
        {
            var screen = view.MapRect(crop);
            var full = new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height);
            steps.Add(canvas =>
            {
                canvas.Save();
                canvas.ClipRect(screen, SKClipOperation.Difference);
                using (var shade = new SKPaint { Color = new SKColor(0, 0, 0, 150) }) canvas.DrawRect(full, shade);
                canvas.Restore();
                using var line = new SKPaint { Color = SKColors.White, StrokeWidth = hair, Style = SKPaintStyle.Stroke };
                using var third = new SKPaint { Color = new SKColor(255, 255, 255, 90), StrokeWidth = hair, Style = SKPaintStyle.Stroke };
                canvas.DrawRect(screen, line);
                for (var i = 1; i < 3; i++)
                {
                    canvas.DrawLine(screen.Left + screen.Width * i / 3, screen.Top, screen.Left + screen.Width * i / 3, screen.Bottom, third);
                    canvas.DrawLine(screen.Left, screen.Top + screen.Height * i / 3, screen.Right, screen.Top + screen.Height * i / 3, third);
                }
                DrawHandles(canvas, [new(screen.Left, screen.Top), new(screen.Right, screen.Top), new(screen.Right, screen.Bottom), new(screen.Left, screen.Bottom)], hair);
            });
        }

        if (tool == Tool.Move && ShowTransformControls && CurrentFrame() is { } frame)
        {
            var corners = frame.Select(p => view.MapPoint(p)).ToArray();
            steps.Add(canvas =>
            {
                using var path = new SKPath();
                path.AddPoly(corners, close: true);
                using var line = new SKPaint { Color = Accent, StrokeWidth = hair, Style = SKPaintStyle.Stroke, IsAntialias = true };
                canvas.DrawPath(path, line);
                DrawHandles(canvas, corners, hair);
            });
        }

        if (guides.Count > 0)
        {
            var lines = guides.Select(g => (view.MapPoint(g.From), view.MapPoint(g.To))).ToArray();
            steps.Add(canvas =>
            {
                using var paint = new SKPaint { Color = new SKColor(0xFF, 0x3D, 0xC8), StrokeWidth = hair };
                foreach (var (from, to) in lines) canvas.DrawLine(from, to, paint);
            });
        }

        if (tool == Tool.CloneStamp && session.CloneSamplePoint(currentDocument) is { } sample)
        {
            var p = view.MapPoint(sample);
            steps.Add(canvas => DrawCrosshair(canvas, p, hair));
        }

        if (IsBrushTool && cursorInside && !spaceDown && drag is Drag.None or Drag.Stroke)
        {
            var center = view.MapPoint(currentDocument);
            var radius = (float)(session.Brush.Size / 2 * UnitsPerPixel);
            var inner = radius * (float)session.Brush.Hardness;
            steps.Add(canvas =>
            {
                using var dark = new SKPaint { Color = new SKColor(0, 0, 0, 200), StrokeWidth = hair, Style = SKPaintStyle.Stroke, IsAntialias = true };
                using var light = new SKPaint { Color = new SKColor(255, 255, 255, 230), StrokeWidth = hair, Style = SKPaintStyle.Stroke, IsAntialias = true };
                if (radius < 3) { DrawCrosshair(canvas, center, hair); return; }
                canvas.DrawCircle(center, radius + hair, dark);
                canvas.DrawCircle(center, radius, light);
                if (inner > 2 && inner < radius - 2)
                {
                    using var faint = new SKPaint { Color = new SKColor(255, 255, 255, 90), StrokeWidth = hair, Style = SKPaintStyle.Stroke, IsAntialias = true };
                    canvas.DrawCircle(center, inner, faint);
                }
            });
        }

        return steps.Count == 0 ? null : canvas => { foreach (var step in steps) step(canvas); };
    }

    private static void DrawHandles(SKCanvas canvas, SKPoint[] corners, float hair)
    {
        using var fill = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var stroke = new SKPaint { Color = Accent, StrokeWidth = hair * 1.5f, Style = SKPaintStyle.Stroke, IsAntialias = true };
        for (var i = 0; i < 4; i++)
        {
            var next = corners[(i + 1) % 4];
            foreach (var p in new[] { corners[i], new SKPoint((corners[i].X + next.X) / 2, (corners[i].Y + next.Y) / 2) })
            {
                var rect = SKRect.Create(p.X - 3.5f, p.Y - 3.5f, 7, 7);
                canvas.DrawRect(rect, fill);
                canvas.DrawRect(rect, stroke);
            }
        }
    }

    private static void DrawCrosshair(SKCanvas canvas, SKPoint p, float hair)
    {
        using var dark = new SKPaint { Color = SKColors.Black, StrokeWidth = 3 * hair, IsAntialias = true };
        using var light = new SKPaint { Color = SKColors.White, StrokeWidth = hair, IsAntialias = true };
        foreach (var paint in new[] { dark, light })
        {
            canvas.DrawLine(p.X - 7, p.Y, p.X + 7, p.Y, paint);
            canvas.DrawLine(p.X, p.Y - 7, p.X, p.Y + 7, paint);
        }
    }
}
