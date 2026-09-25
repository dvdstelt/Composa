using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Composa.App;

/// <summary>Small builders that keep code-built layouts readable.</summary>
public static class Ui
{
    public static TextBlock Label(string text, IBrush? brush = null, double? size = null, FontWeight weight = FontWeight.Normal)
    {
        var block = new TextBlock { Text = text, Foreground = brush ?? Palette.Foreground, FontWeight = weight };
        if (size is { } s) block.FontSize = s;
        return block;
    }

    public static StackPanel Row(double spacing, params Control[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.AddRange(children);
        return panel;
    }

    public static StackPanel Column(double spacing, params Control[] children)
    {
        var panel = new StackPanel { Spacing = spacing };
        panel.Children.AddRange(children);
        return panel;
    }

    public static Button IconButton(Icons.Icon icon, string tip, Action click, double size = 16)
    {
        var button = new Button { Content = Icons.Create(icon, size), Classes = { "flat" } };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    public static Button TextButton(string text, Action click, bool accent = false)
    {
        var button = new Button { Content = text, MinWidth = 72, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (accent) button.Classes.Add("accent");
        button.Click += (_, _) => click();
        return button;
    }

    public static CheckBox Check(string text, bool value, Action<bool> changed)
    {
        var box = new CheckBox { Content = text, IsChecked = value };
        box.IsCheckedChanged += (_, _) => changed(box.IsChecked == true);
        return box;
    }

    public static ComboBox Combo<T>(IEnumerable<T> items, T selected, Func<T, string> label, Action<T> changed, double width = 130)
    {
        var list = items.ToList();
        var combo = new ComboBox { ItemsSource = list.Select(label).ToList(), SelectedIndex = list.IndexOf(selected), Width = width };
        combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) changed(list[combo.SelectedIndex]); };
        return combo;
    }

    public static NumericUpDown Number(double value, double min, double max, Action<double> changed, double step = 1, string format = "0", double width = 72)
    {
        var box = new NumericUpDown
        {
            Value = (decimal)Math.Clamp(value, min, max), Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)step, FormatString = format,
            Width = width, ClipValueToMinMax = true, ShowButtonSpinner = false
        };
        box.ValueChanged += (_, e) => { if (e.NewValue is { } v) changed((double)v); };
        return box;
    }

    /// <summary>How much slower a scrubbed value moves under the pointer while Alt is held.</summary>
    private const double ScrubFineScale = 0.1;
    /// <summary>Pixels a press must travel before a scrub starts, so a click leaves the value alone.</summary>
    private const double ScrubThreshold = 3;

    /// <summary>
    /// Makes a label a drag target for the number field beside it, as in Photoshop: drag left or right to change the
    /// value one whole unit per pixel, Alt for ten times finer. Typing in the field still takes decimals.
    /// </summary>
    public static T Scrub<T>(T label, NumericUpDown box) where T : Control
    {
        label.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast);
        // Only what a control draws is hit; a label without a background would take the pointer on its glyphs alone.
        if (label is TextBlock { Background: null } text) text.Background = Brushes.Transparent;
        ToolTip.SetTip(label, "Drag to change the value (Alt: finer)");
        ToolTip.SetShowDelay(label, 450);
        double pressX = 0, lastX = 0, start = 0, travel = 0;
        bool pressed = false, dragging = false;
        label.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(label).Properties.IsLeftButtonPressed) return;
            ToolTip.SetIsOpen(label, false);
            pressX = lastX = e.GetPosition(label).X;
            start = (double)(box.Value ?? 0);
            travel = 0;
            pressed = true;
            dragging = false;
            e.Pointer.Capture(label);
            e.Handled = true;
        };
        label.PointerMoved += (_, e) =>
        {
            if (!pressed || !e.GetCurrentPoint(label).Properties.IsLeftButtonPressed) return;
            var x = e.GetPosition(label).X;
            if (!dragging)
            {
                if (Math.Abs(x - pressX) < ScrubThreshold) return;
                dragging = true;
                lastX = pressX;
            }
            travel += (x - lastX) * (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt) ? ScrubFineScale : 1);
            lastX = x;
            // Whole numbers, as Photoshop scrubs: the field's own step and format decide what typing accepts.
            var value = Math.Clamp((decimal)Math.Round(start + travel), box.Minimum, box.Maximum);
            if (box.Value != value) box.Value = value;
            e.Handled = true;
        };
        void End(Avalonia.Input.IPointer pointer)
        {
            if (!pressed) return;
            pressed = dragging = false;
            if (Equals(pointer.Captured, label)) pointer.Capture(null);
        }
        label.PointerReleased += (_, e) => { var was = pressed; End(e.Pointer); e.Handled = was; };
        label.PointerCaptureLost += (_, e) => End(e.Pointer);
        return label;
    }

    /// <summary>A Krita-style field whose fill is the slider: drag to change, Alt-drag for fine steps, double-click to type. See <see cref="Controls.SliderField"/>.</summary>
    /// <param name="track">The colors along the box when the value is a color or moves one (see <see cref="Controls.SliderTracks"/>).</param>
    /// <param name="reset">The value the field's Reset button puts back; null leaves the button out.</param>
    public static Controls.SliderField SliderField(string label, double value, double min, double max, Action<double> changed, double step = 1, string format = "0", double width = 120,
        IReadOnlyList<Avalonia.Media.Color>? track = null, double? reset = null)
    {
        var field = new Controls.SliderField(label, value, min, max, step, format) { Width = width, Track = track, Reset = reset };
        field.Changed += changed;
        return field;
    }

    public static Border Separator(bool vertical = true) => vertical
        ? new Border { Width = 1, Background = Palette.Divider, Margin = new Thickness(4, 6) }
        : new Border { Height = 1, Background = Palette.Divider };

    /// <summary>Copies Skia pixels into an Avalonia bitmap, reduced to fit <paramref name="maxSide"/>.</summary>
    public static Avalonia.Media.Imaging.Bitmap ToAvaloniaBitmap(SkiaSharp.SKBitmap source, int maxSide)
    {
        var scale = Math.Min(1, Math.Min((double)maxSide / source.Width, (double)maxSide / source.Height));
        int w = Math.Max(1, (int)Math.Round(source.Width * scale)), h = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var small = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(w, h, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul));
        using (var canvas = new SkiaSharp.SKCanvas(small))
        using (var image = SkiaSharp.SKImage.FromPixels(source.PeekPixels()))
            canvas.DrawImage(image, new SkiaSharp.SKRect(0, 0, w, h), new SkiaSharp.SKSamplingOptions(SkiaSharp.SKFilterMode.Linear, SkiaSharp.SKMipmapMode.Linear));
        return new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul, small.GetPixels(),
            new PixelSize(w, h), new Vector(96, 96), small.RowBytes);
    }

    public static Color ToAvalonia(this SkiaSharp.SKColor c) => Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue);
    public static SkiaSharp.SKColor ToSkia(this Color c) => new(c.R, c.G, c.B, c.A);
}
