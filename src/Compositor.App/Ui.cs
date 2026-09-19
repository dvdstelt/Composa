using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Compositor.App;

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

    /// <summary>A labelled slider with a numeric readout. Returns the row and a setter that updates it silently.</summary>
    public static (Control Row, Action<double> Set) SliderRow(string label, double value, double min, double max, Action<double> changed,
        double step = 1, string format = "0", double sliderWidth = 140, double labelWidth = double.NaN)
    {
        var silent = false;
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, Width = sliderWidth, SmallChange = step, LargeChange = step * 10, VerticalAlignment = VerticalAlignment.Center };
        var readout = new TextBlock { Text = value.ToString(format), Width = 38, TextAlignment = TextAlignment.Right, Foreground = Palette.Secondary };
        slider.ValueChanged += (_, e) =>
        {
            var v = Math.Round(e.NewValue / step) * step;
            readout.Text = v.ToString(format);
            if (!silent) changed(v);
        };
        var title = Label(label);
        if (!double.IsNaN(labelWidth)) title.Width = labelWidth;
        return (Row(8, title, slider, readout), v => { silent = true; slider.Value = v; silent = false; });
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
