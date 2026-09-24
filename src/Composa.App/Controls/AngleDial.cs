using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Composa.App.Controls;

/// <summary>
/// Photoshop's angle dial: a circle with a hand that points where the value points, in degrees counterclockwise from the right.
/// Press or drag anywhere on it to turn the hand to the pointer; Shift snaps to 15 degree steps; the wheel and the arrow keys turn it
/// by one degree. Setting <see cref="Value"/> from code redraws without raising <see cref="Changed"/>.
/// </summary>
public sealed class AngleDial : Control
{
    public const double DefaultSize = 40;
    public const double SnapDegrees = 15;

    private static readonly IBrush FaceBrush = new SolidColorBrush(Color.Parse("#1F1F1F"));
    private static readonly IPen RimPen = new Pen(new SolidColorBrush(Color.Parse("#454545")));
    private static readonly IPen RimHoverPen = new Pen(new SolidColorBrush(Color.Parse("#6A6A6A")));
    private static readonly IPen RimFocusPen = new Pen(Palette.Accent);
    private static readonly IPen HandPen = new Pen(Palette.Foreground, 2, lineCap: PenLineCap.Round);
    private static readonly IPen TickPen = new Pen(new SolidColorBrush(Color.Parse("#3A3A3A")));

    private readonly double min, max;
    private double value;
    private bool hovered, pressed;

    /// <summary>Raised for every angle the user sets, already normalised into the dial's range.</summary>
    public event Action<double>? Changed;

    public AngleDial(double value, double min = -180, double max = 180)
    {
        this.min = min; this.max = max;
        this.value = Normalise(value);
        Width = Height = DefaultSize;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, "Drag to turn the hand · Shift snaps to 15° · Arrow keys or scroll wheel turn by 1°");
        ToolTip.SetShowDelay(this, 450);
    }

    public double Value
    {
        get => value;
        set
        {
            var normalised = Normalise(value);
            if (normalised == this.value) return;
            this.value = normalised;
            InvalidateVisual();
        }
    }

    /// <summary>Brings any angle into the dial's range, so 190 on a -180 to 180 dial is -170 and straight left reads 180, never -180.</summary>
    public double Normalise(double degrees)
    {
        if (!double.IsFinite(degrees)) return max;
        if (max - min < 360) return Math.Clamp(degrees, min, max);
        var wrapped = max - ((max - degrees) % 360 + 360) % 360;
        return Math.Round(wrapped, 6);
    }

    private void SetFromUser(double degrees)
    {
        var normalised = Normalise(degrees);
        if (normalised == value) return;
        value = normalised;
        InvalidateVisual();
        Changed?.Invoke(value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsFocusedProperty || change.Property == IsEnabledProperty) InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = size / 2 - 1;
        using (context.PushOpacity(IsEnabled ? 1 : 0.45))
        {
            context.DrawEllipse(FaceBrush, pressed || IsFocused ? RimFocusPen : hovered ? RimHoverPen : RimPen, centre, radius, radius);
            for (var tick = 0; tick < 360; tick += 90)
            {
                var (dx, dy) = Direction(tick);
                context.DrawLine(TickPen, centre + new Vector(dx * (radius - 4), dy * (radius - 4)), centre + new Vector(dx * (radius - 1), dy * (radius - 1)));
            }
            var (hx, hy) = Direction(value);
            context.DrawLine(HandPen, centre, centre + new Vector(hx * (radius - 3), hy * (radius - 3)));
            context.DrawEllipse(Palette.Foreground, null, centre, 2, 2);
        }
    }

    /// <summary>The unit vector on screen for an angle counterclockwise from the right, with y growing downward.</summary>
    private static (double X, double Y) Direction(double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return (Math.Cos(radians), -Math.Sin(radians));
    }

    private double AngleAt(Point position, bool snap)
    {
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var dx = position.X - centre.X;
        var dy = centre.Y - position.Y;
        if (dx == 0 && dy == 0) return value;
        var degrees = Math.Atan2(dy, dx) * 180 / Math.PI;
        return Math.Round(snap ? Math.Round(degrees / SnapDegrees) * SnapDegrees : degrees);
    }

    protected override void OnPointerEntered(PointerEventArgs e) { base.OnPointerEntered(e); hovered = true; InvalidateVisual(); }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); hovered = false; InvalidateVisual(); }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        ToolTip.SetIsOpen(this, false);
        Focus();
        pressed = true;
        e.Pointer.Capture(this);
        SetFromUser(AngleAt(e.GetPosition(this), e.KeyModifiers.HasFlag(KeyModifiers.Shift)));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!pressed || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        SetFromUser(AngleAt(e.GetPosition(this), e.KeyModifiers.HasFlag(KeyModifiers.Shift)));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!pressed) return;
        pressed = false;
        if (Equals(e.Pointer.Captured, this)) e.Pointer.Capture(null);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (pressed) { pressed = false; InvalidateVisual(); }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var delta = e.Delta.Y != 0 ? e.Delta.Y : -e.Delta.X;
        if (delta == 0) return;
        SetFromUser(value + (delta > 0 ? 1 : -1));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? SnapDegrees : 1;
        switch (e.Key)
        {
            case Key.Up or Key.Right: SetFromUser(value + step); e.Handled = true; break;
            case Key.Down or Key.Left: SetFromUser(value - step); e.Handled = true; break;
        }
    }
}
