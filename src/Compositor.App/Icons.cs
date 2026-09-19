using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace Compositor.App;

/// <summary>Line icons on a 24-unit grid, drawn with the current foreground.</summary>
public static class Icons
{
    public sealed record Icon(string? Stroke, string? Fill = null, bool Dashed = false);

    public static readonly Icon Move = new(null, "M12 2 L15.5 6 H13 V11 H18 V8.5 L22 12 L18 15.5 V13 H13 V18 H15.5 L12 22 L8.5 18 H11 V13 H6 V15.5 L2 12 L6 8.5 V11 H11 V6 H8.5 Z");
    public static readonly Icon Marquee = new("M4 4 H20 V20 H4 Z", null, true);
    public static readonly Icon MarqueeEllipse = new("M12 4 A8 8 0 1 1 12 20 A8 8 0 1 1 12 4 Z", null, true);
    public static readonly Icon Lasso = new("M12 4 C6 4 3 7 3 10 C3 13 6 15 11 15 C17 15 21 13 21 9.5 C21 6 17 4 12 4 M9 15 C6.5 16 6.5 19 8.5 19 C10 21 8 22.5 5.5 22");
    public static readonly Icon PolygonLasso = new("M4 6 L14 3 L21 10 L15 16 L6 14 Z M6 14 C5 17 8 18 7 22");
    public static readonly Icon Wand = new("M4 20 L13.5 10.5", "M17 2 L18.3 5.7 L22 7 L18.3 8.3 L17 12 L15.7 8.3 L12 7 L15.7 5.7 Z M7 3 L7.7 5.3 L10 6 L7.7 6.7 L7 9 L6.3 6.7 L4 6 L6.3 5.3 Z");
    public static readonly Icon Crop = new("M6.5 2 V17.5 H22 M2 6.5 H17.5 V22");
    public static readonly Icon Brush = new(null, "M20.5 2.5 C21.5 3.5 21.5 4.5 20.5 5.5 L11.5 15.5 L8.5 12.5 L18.5 3.5 C19.2 2.2 19.8 1.8 20.5 2.5 Z M7.5 13.8 L10.2 16.5 C10.5 20 7 21.5 2.5 21 C4.5 19.5 4 17.5 4.8 15.8 C5.4 14.5 6.4 13.9 7.5 13.8 Z");
    public static readonly Icon Eraser = new("M9 20 L3.5 14.5 L13.5 4.5 L20.5 11.5 L12 20 Z M9 20 H21 M8 10 L15 17");
    public static readonly Icon Heal = new("M3.8 14.2 L14.2 3.8 A4.2 4.2 0 0 1 20.2 9.8 L9.8 20.2 A4.2 4.2 0 0 1 3.8 14.2 Z M8.5 9.5 L14.5 15.5 M9.5 8.5 L15.5 14.5", "M11.2 12 A0.8 0.8 0 1 1 12.8 12 A0.8 0.8 0 1 1 11.2 12 Z");
    public static readonly Icon Stamp = new(null, "M9.5 2.5 H14.5 C15.5 5 14 7.5 14 10.5 H19 C20 10.5 20.5 11 20.5 12 V15.5 H3.5 V12 C3.5 11 4 10.5 5 10.5 H10 C10 7.5 8.5 5 9.5 2.5 Z M4 17.5 H20 V21 H4 Z");
    public static readonly Icon Drop = new("M12 3 C12 3 5.5 10.5 5.5 15 A6.5 6.5 0 0 0 18.5 15 C18.5 10.5 12 3 12 3 Z");
    public static readonly Icon Gradient = new("M3.5 3.5 H20.5 V20.5 H3.5 Z", "M3.5 12 H20.5 V20.5 H3.5 Z");
    public static readonly Icon Shape = new("M3 3 H14 V14 H3 Z M21.5 15.5 A6 6 0 1 1 9.5 15.5 A6 6 0 1 1 21.5 15.5 Z");
    public static readonly Icon Text = new("M5 7 V4.5 H19 V7 M12 4.5 V19.5 M9 19.5 H15");
    public static readonly Icon Eyedropper = new("M13.5 8.5 L4.5 17.5 L3 21 L6.5 19.5 L15.5 10.5", "M15.5 3.5 C17 2 19 2 20.5 3.5 C22 5 22 7 20.5 8.5 L18 11 L19 12 L17.5 13.5 L10.5 6.5 L12 5 L13 6 Z");
    public static readonly Icon Hand = new("M8 12 V5.5 A1.5 1.5 0 0 1 11 5.5 V11 V4 A1.5 1.5 0 0 1 14 4 V11 V5 A1.5 1.5 0 0 1 17 5 V11.5 V8 A1.5 1.5 0 0 1 20 8 V15 C20 19 17 22 13 22 C10 22 8.2 20.5 6.5 18 L3.6 13.6 A1.5 1.5 0 0 1 6 11.8 L8 14.5 Z");
    public static readonly Icon Zoom = new("M10 3 A7 7 0 1 1 10 17 A7 7 0 1 1 10 3 Z M15 15 L21 21");
    public static readonly Icon ZoomIn = new("M10 3 A7 7 0 1 1 10 17 A7 7 0 1 1 10 3 Z M15 15 L21 21 M7 10 H13 M10 7 V13");
    public static readonly Icon ZoomOut = new("M10 3 A7 7 0 1 1 10 17 A7 7 0 1 1 10 3 Z M15 15 L21 21 M7 10 H13");
    public static readonly Icon Eye = new("M2 12 C5 6.5 8.5 5 12 5 C15.5 5 19 6.5 22 12 C19 17.5 15.5 19 12 19 C8.5 19 5 17.5 2 12 Z M12 9 A3 3 0 1 1 12 15 A3 3 0 1 1 12 9 Z");
    public static readonly Icon Folder = new("M3 6 H9.5 L11.5 8.5 H21 V19 H3 Z");
    public static readonly Icon Plus = new("M12 5 V19 M5 12 H19");
    public static readonly Icon Close = new("M6 6 L18 18 M18 6 L6 18");
    public static readonly Icon Trash = new("M4 7 H20 M9 7 V4 H15 V7 M6 7 L7 21 H17 L18 7 M10 11 V17 M14 11 V17");
    public static readonly Icon Mask = new("M3 5 H21 V19 H3 Z", "M12 8 A4 4 0 1 1 12 16 A4 4 0 1 1 12 8 Z");
    public static readonly Icon Adjust = new("M12 3 A9 9 0 1 1 12 21 A9 9 0 1 1 12 3 Z", "M12 3 A9 9 0 0 1 12 21 Z");
    public static readonly Icon ChevronRight = new("M9 6 L15 12 L9 18");
    public static readonly Icon ChevronDown = new("M6 9 L12 15 L18 9");
    public static readonly Icon ClipArrow = new("M7 5 V14 H16 M13 10.5 L16.5 14 L13 17.5");
    public static readonly Icon Swap = new("M5 9 C5 6 7 5 10 5 H17 M14 2 L17 5 L14 8 M19 15 C19 18 17 19 14 19 H7 M10 16 L7 19 L10 22");

    public static Control Create(Icon icon, double size = 18, IBrush? brush = null)
    {
        var panel = new Panel { Width = 24, Height = 24 };
        if (icon.Fill != null)
        {
            var fill = new Path { Data = Geometry.Parse(icon.Fill) };
            Bind(fill, Avalonia.Controls.Shapes.Shape.FillProperty, brush);
            panel.Children.Add(fill);
        }
        if (icon.Stroke != null)
        {
            var stroke = new Path { Data = Geometry.Parse(icon.Stroke), StrokeThickness = 1.6, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round };
            if (icon.Dashed) stroke.StrokeDashArray = [2.2, 2.2];
            Bind(stroke, Avalonia.Controls.Shapes.Shape.StrokeProperty, brush);
            panel.Children.Add(stroke);
        }
        return new Viewbox { Width = size, Height = size, Child = panel, IsHitTestVisible = false };
    }

    private static void Bind(Path path, AvaloniaProperty<IBrush?> property, IBrush? brush)
    {
        if (brush != null) path.SetValue(property, brush);
        else path.Bind(property, path.GetResourceObservable("CompositorForeground"));
    }
}
