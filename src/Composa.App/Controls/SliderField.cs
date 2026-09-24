using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Composa.App.Controls;

/// <summary>
/// A numeric field whose fill is the slider, as in Krita and Affinity Photo: drag anywhere on it to change the value, hold Alt while
/// dragging for ten times finer steps, double-click to type an exact value, and step by one with the scroll wheel or the arrow keys.
/// Label and value are drawn inside the box, so a row of them stays on one centre line. Setting <see cref="Value"/> from code
/// redraws without raising <see cref="Changed"/>; only the user's input does.
/// </summary>
public sealed class SliderField : Control
{
    public const double DefaultHeight = 24;
    /// <summary>Pointer travel before a press counts as a drag, so the first click of a double-click leaves the value alone.</summary>
    public const double DragThreshold = 3;
    /// <summary>How much slower the value moves under the pointer while Alt is held.</summary>
    public const double FineScale = 0.1;

    private static readonly IBrush FieldBrush = new SolidColorBrush(Color.Parse("#1F1F1F"));
    private static readonly IBrush FillBrush = new SolidColorBrush(Color.Parse("#2E4A6B"));
    private static readonly IPen BorderPen = new Pen(new SolidColorBrush(Color.Parse("#454545")));
    private static readonly IPen FocusPen = new Pen(Palette.Accent);
    private static readonly IPen HoverPen = new Pen(new SolidColorBrush(Color.Parse("#6A6A6A")));

    private readonly TextBox editor;
    private readonly string label;
    private readonly double min, max, step;
    private readonly string format;
    private double value;
    private bool hovered, pressed, dragging, editing, lastPressDragged;
    private Point pressAt;
    private double dragStart, dragTravel, lastX;

    /// <summary>Raised for every value the user sets, already rounded to <paramref name="step"/> and clamped.</summary>
    public event Action<double>? Changed;

    public SliderField(string label, double value, double min, double max, double step = 1, string format = "0")
    {
        this.label = label;
        this.min = min; this.max = max; this.step = step; this.format = format;
        this.value = Snap(value);
        Height = DefaultHeight;
        Width = 120;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.SizeWestEast);
        VerticalAlignment = VerticalAlignment.Center;
        editor = new TextBox
        {
            IsVisible = false, TextAlignment = TextAlignment.Right, MinHeight = 0, Padding = new Thickness(6, 0), FontSize = 12.5,
            VerticalContentAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(4)
        };
        editor.KeyDown += OnEditorKeyDown;
        editor.LostFocus += (_, _) => { if (editing) CommitEdit(); };
        VisualChildren.Add(editor);
        LogicalChildren.Add(editor);
        ToolTip.SetTip(this, BuildTip());
        ToolTip.SetShowDelay(this, 450);
    }

    public string Label => label;
    public double Minimum => min;
    public double Maximum => max;
    public double Step => step;
    public bool IsEditing => editing;

    /// <summary>The current value. Setting it from code updates the drawing and any open editor without raising <see cref="Changed"/>.</summary>
    public double Value
    {
        get => value;
        set
        {
            var snapped = Snap(value);
            if (snapped == this.value) return;
            this.value = snapped;
            if (editing) editor.Text = Text;
            InvalidateVisual();
        }
    }

    private string Text => value.ToString(format, System.Globalization.CultureInfo.CurrentCulture);

    private double Snap(double v) => Math.Clamp(Math.Round(v / step) * step, min, max);

    private void SetFromUser(double v)
    {
        var snapped = Snap(v);
        if (snapped == value) return;
        value = snapped;
        InvalidateVisual();
        Changed?.Invoke(value);
    }

    /// <summary>Moves the value by whole steps, as the wheel and the arrow keys do.</summary>
    public void Nudge(int steps) => SetFromUser(value + steps * step);

    private Control BuildTip()
    {
        var unit = step.ToString(format == "0" && step != Math.Floor(step) ? "0.##" : format, System.Globalization.CultureInfo.CurrentCulture);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"), ColumnSpacing = 10, RowSpacing = 2 };
        void Row(int row, string what, string how)
        {
            var w = new TextBlock { Text = what, Foreground = Palette.Secondary };
            var h = new TextBlock { Text = how };
            Grid.SetRow(w, row); Grid.SetColumn(w, 0); Grid.SetRow(h, row); Grid.SetColumn(h, 1);
            grid.Children.Add(w); grid.Children.Add(h);
        }
        Row(0, "Change", "Drag left or right");
        Row(1, "Fine steps", "Hold Alt while dragging");
        Row(2, "Exact value", "Double-click to type");
        Row(3, $"Step by {unit}", "Arrow keys or scroll wheel");
        return grid;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        editor.Measure(availableSize);
        return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : 0, double.IsFinite(availableSize.Height) ? availableSize.Height : DefaultHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        editor.Arrange(new Rect(finalSize));
        return finalSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsFocusedProperty || change.Property == IsEnabledProperty) InvalidateVisual();
    }

    // ---- Drawing -----------------------------------------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        var radius = 4.0;
        var outline = bounds.Deflate(0.5);
        context.DrawRectangle(FieldBrush, null, outline, radius, radius);
        if (!editing)
        {
            var fraction = max > min ? (value - min) / (max - min) : 0;
            var fillWidth = Math.Round(bounds.Width * Math.Clamp(fraction, 0, 1));
            if (fillWidth > 0)
                using (context.PushClip(new RoundedRect(outline, radius)))
                    context.DrawRectangle(FillBrush, null, new Rect(0, 0, fillWidth, bounds.Height));
            var fontSize = TextElement.GetFontSize(this);
            var typeface = new Typeface(TextElement.GetFontFamily(this));
            var opacity = IsEnabled ? 1.0 : 0.45;
            using (context.PushOpacity(opacity))
            {
                var name = new FormattedText(label, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, Palette.Secondary);
                var number = new FormattedText(Text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, Palette.Foreground);
                context.DrawText(name, new Point(8, (bounds.Height - name.Height) / 2));
                context.DrawText(number, new Point(bounds.Width - 8 - number.Width, (bounds.Height - number.Height) / 2));
            }
        }
        var pen = dragging || editing || IsFocused ? FocusPen : hovered ? HoverPen : BorderPen;
        context.DrawRectangle(null, pen, outline, radius, radius);
    }

    protected override void OnPointerEntered(PointerEventArgs e) { base.OnPointerEntered(e); hovered = true; InvalidateVisual(); }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); hovered = false; InvalidateVisual(); }

    // ---- Pointer -----------------------------------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (editing || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // Avalonia counts a press soon after another at the same spot as a double-click even when the first one dragged;
        // two quick drags in a row must stay drags, so only a press after a plain click opens the editor.
        if (e.ClickCount >= 2 && !lastPressDragged) { BeginEdit(); e.Handled = true; return; }
        ToolTip.SetIsOpen(this, false);
        Focus();
        pressAt = e.GetPosition(this);
        lastX = pressAt.X;
        dragStart = value;
        dragTravel = 0;
        pressed = true;
        dragging = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (editing || !pressed || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var position = e.GetPosition(this);
        if (!dragging)
        {
            if (Math.Abs(position.X - pressAt.X) < DragThreshold) return;
            dragging = true;
            lastX = pressAt.X;
            InvalidateVisual();
        }
        var unitsPerPixel = (max - min) / Math.Max(1, Bounds.Width);
        var scale = e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? FineScale : 1;
        dragTravel += (position.X - lastX) * unitsPerPixel * scale;
        lastX = position.X;
        SetFromUser(dragStart + dragTravel);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!pressed) return;
        EndPress();
        if (Equals(e.Pointer.Captured, this)) e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndPress();
    }

    private void EndPress()
    {
        if (!pressed) return;
        pressed = false;
        lastPressDragged = dragging;
        if (dragging) { dragging = false; InvalidateVisual(); }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (editing) return;
        var delta = e.Delta.Y != 0 ? e.Delta.Y : -e.Delta.X;
        if (delta == 0) return;
        Nudge(delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    // ---- Keyboard ----------------------------------------------------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (editing || e.Handled) return;
        switch (e.Key)
        {
            case Key.Up or Key.Right: Nudge(1); e.Handled = true; break;
            case Key.Down or Key.Left: Nudge(-1); e.Handled = true; break;
            case Key.Home: SetFromUser(min); e.Handled = true; break;
            case Key.End: SetFromUser(max); e.Handled = true; break;
            case Key.Enter or Key.F2: BeginEdit(); e.Handled = true; break;
        }
    }

    // ---- Typing ------------------------------------------------------------------------------------------------------

    /// <summary>Shows the text editor over the field with the value selected, as a double-click does.</summary>
    public void BeginEdit()
    {
        if (editing) return;
        editing = true;
        ToolTip.SetIsOpen(this, false);
        editor.Text = Text;
        editor.IsVisible = true;
        editor.Focus();
        editor.SelectAll();
        InvalidateVisual();
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitEdit(); e.Handled = true; }
        else if (e.Key == Key.Escape) { CancelEdit(); e.Handled = true; }
    }

    private void CommitEdit()
    {
        if (!editing) return;
        var typed = double.TryParse(editor.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var parsed)
            || double.TryParse(editor.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsed);
        EndEdit();
        if (typed) SetFromUser(parsed);
    }

    private void CancelEdit()
    {
        if (!editing) return;
        EndEdit();
    }

    private void EndEdit()
    {
        editing = false;
        editor.IsVisible = false;
        if (editor.IsFocused) Focus();
        InvalidateVisual();
    }
}
