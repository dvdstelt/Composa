using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Compositor.Filters;
using SkiaSharp;

namespace Compositor.App.Dialogs;

/// <summary>Editors for every adjustment and filter. Each reports changes live so the canvas can preview them.</summary>
public static class AdjustmentDialogs
{
    private static readonly string[] Channels = ["RGB", "Red", "Green", "Blue"];

    /// <summary>Shows the editor for an adjustment. <paramref name="changed"/> runs (debounced) on every edit; returns the accepted settings or null.</summary>
    public static async Task<Adjustment?> Edit(Window owner, Adjustment initial, Action<Adjustment> changed, Histogram? histogram, SKColor foreground, SKColor background)
    {
        var current = initial;
        var preview = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) => { timer.Stop(); changed(preview ? current : Identity(initial)); };
        void Update(Adjustment value) { current = value; timer.Stop(); timer.Start(); }

        var body = initial switch
        {
            LevelsAdjustment levels => LevelsEditor(levels, histogram, Update),
            CurvesAdjustment curves => CurvesEditor(curves, histogram, Update),
            HueSaturationAdjustment hue => HueEditor(hue, Update),
            ExposureAdjustment exposure => Sliders(
                ("Exposure", exposure.Exposure, -5, 5, 0.01, "0.00", v => Update(exposure = exposure with { Exposure = v })),
                ("Offset", exposure.Offset, -0.5, 0.5, 0.001, "0.000", v => Update(exposure = exposure with { Offset = v })),
                ("Gamma", exposure.Gamma, 0.1, 5, 0.01, "0.00", v => Update(exposure = exposure with { Gamma = v }))),
            BrightnessContrastAdjustment bc => Sliders(
                ("Brightness", bc.Brightness, -100, 100, 1, "0", v => Update(bc = bc with { Brightness = v })),
                ("Contrast", bc.Contrast, -100, 100, 1, "0", v => Update(bc = bc with { Contrast = v }))),
            GrainAdjustment grain => Sliders(
                ("Amount", grain.Amount, 0, 100, 1, "0", v => Update(grain = grain with { Amount = v })),
                ("Size", grain.Size, 0.5, 20, 0.1, "0.0", v => Update(grain = grain with { Size = v })),
                ("Roughness", grain.Roughness, 0, 100, 1, "0", v => Update(grain = grain with { Roughness = v }))),
            GradientMapAdjustment map => GradientMapEditor(owner, map, foreground, background, Update),
            BlackAndWhiteAdjustment bw => BlackAndWhiteEditor(bw, Update),
            ColorBalanceAdjustment balance => ColorBalanceEditor(balance, Update),
            _ => Ui.Label("This adjustment has no settings.", Palette.Secondary)
        };
        var previewBox = Ui.Check("Preview", true, v => { preview = v; timer.Stop(); timer.Start(); });
        var dialog = new DialogWindow(initial.DisplayName, Ui.Column(12, body, previewBox));
        dialog.Opened += (_, _) => changed(current);
        var accepted = await dialog.Ask(owner);
        timer.Stop();
        return accepted ? current : null;
    }

    private static Adjustment Identity(Adjustment like) => Adjustment.Create(like.Kind) switch
    {
        GrainAdjustment grain => grain with { Amount = 0 },
        GradientMapAdjustment => new BrightnessContrastAdjustment(),
        InvertAdjustment => new BrightnessContrastAdjustment(),
        BlackAndWhiteAdjustment => new BrightnessContrastAdjustment(),
        var other => other
    };

    private static Control Sliders(params (string Label, double Value, double Min, double Max, double Step, string Format, Action<double> Changed)[] rows)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var r in rows) panel.Children.Add(Ui.SliderRow(r.Label, r.Value, r.Min, r.Max, r.Changed, r.Step, r.Format, 240, 80).Row);
        return panel;
    }

    private static Control LevelsEditor(LevelsAdjustment levels, Histogram? histogram, Action<Adjustment> update)
    {
        var channel = 0;
        var graph = new HistogramView { Histogram = histogram, Width = 360, Height = 110 };
        var rows = new StackPanel { Spacing = 8 };
        void Build()
        {
            rows.Children.Clear();
            var range = levels.Ranges[channel];
            void Set(LevelsRange next) { range = next; update(levels = levels.WithRange(channel, next)); }
            rows.Children.Add(Ui.SliderRow("Input black", range.InputBlack, 0, 253, v => Set(range with { InputBlack = Math.Min(v, range.InputWhite - 2) }), 1, "0", 220, 100).Row);
            rows.Children.Add(Ui.SliderRow("Midtones", range.Gamma, 0.1, 5, v => Set(range with { Gamma = v }), 0.01, "0.00", 220, 100).Row);
            rows.Children.Add(Ui.SliderRow("Input white", range.InputWhite, 2, 255, v => Set(range with { InputWhite = Math.Max(v, range.InputBlack + 2) }), 1, "0", 220, 100).Row);
            rows.Children.Add(Ui.SliderRow("Output black", range.OutputBlack, 0, 255, v => Set(range with { OutputBlack = v }), 1, "0", 220, 100).Row);
            rows.Children.Add(Ui.SliderRow("Output white", range.OutputWhite, 0, 255, v => Set(range with { OutputWhite = v }), 1, "0", 220, 100).Row);
        }
        var picker = Ui.Combo(Channels, "RGB", c => c, c => { channel = Array.IndexOf(Channels, c); graph.Channel = channel == 0 ? 3 : channel - 1; Build(); });
        var auto = Ui.TextButton("Auto", () =>
        {
            if (histogram == null) return;
            update(levels = LevelsAdjustment.Auto(histogram));
            Build();
        });
        var reset = Ui.TextButton("Reset", () => { update(levels = new LevelsAdjustment()); Build(); });
        Build();
        return Ui.Column(10, Ui.Row(10, Ui.Label("Channel", Palette.Secondary), picker, auto, reset), graph, rows);
    }

    private static Control CurvesEditor(CurvesAdjustment curves, Histogram? histogram, Action<Adjustment> update)
    {
        var editor = new CurveEditor { Width = 300, Height = 300, Curves = curves, Histogram = histogram };
        editor.Changed += value => update(curves = value);
        var picker = Ui.Combo(Channels, "RGB", c => c, c => editor.Channel = Array.IndexOf(Channels, c));
        var reset = Ui.TextButton("Reset", () => { editor.Curves = curves = new CurvesAdjustment(); update(curves); });
        return Ui.Column(10, Ui.Row(10, Ui.Label("Channel", Palette.Secondary), picker, reset), editor,
            Ui.Label("Click to add a point, drag to move it, drag it off the graph to remove it.", Palette.Secondary));
    }

    private static Control HueEditor(HueSaturationAdjustment hue, Action<Adjustment> update)
    {
        var range = HueRange.Master;
        var rows = new StackPanel { Spacing = 8 };
        void Build()
        {
            rows.Children.Clear();
            var shift = hue.Shifts[(int)range];
            void Set(HslShift next) { shift = next; update(hue = hue.WithShift(range, next)); }
            var colorizing = hue.Colorize && range == HueRange.Master;
            rows.Children.Add(Ui.SliderRow("Hue", shift.Hue, colorizing ? 0 : -180, colorizing ? 360 : 180, v => Set(shift with { Hue = v }), 1, "0", 240, 80).Row);
            rows.Children.Add(Ui.SliderRow("Saturation", shift.Saturation, -100, 100, v => Set(shift with { Saturation = v }), 1, "0", 240, 80).Row);
            rows.Children.Add(Ui.SliderRow("Lightness", shift.Lightness, -100, 100, v => Set(shift with { Lightness = v }), 1, "0", 240, 80).Row);
        }
        var picker = Ui.Combo(Enum.GetValues<HueRange>(), HueRange.Master, r => r.ToString(), r => { range = r; Build(); });
        var colorize = Ui.Check("Colorize", hue.Colorize, v =>
        {
            var master = hue.Shifts[0];
            hue = (hue with { Colorize = v }).WithShift(HueRange.Master, master with { Hue = v ? Math.Max(0, master.Hue) : Math.Min(180, master.Hue) });
            update(hue);
            Build();
        });
        Build();
        return Ui.Column(10, Ui.Row(10, Ui.Label("Range", Palette.Secondary), picker, colorize), rows);
    }

    private static readonly string[] ColorFamilies = ["Reds", "Yellows", "Greens", "Cyans", "Blues", "Magentas"];

    /// <summary>Each slider says how bright that family of colors becomes, as Photoshop's do; the tint colors the gray.</summary>
    private static Control BlackAndWhiteEditor(BlackAndWhiteAdjustment bw, Action<Adjustment> update)
    {
        var weights = new StackPanel { Spacing = 8 };
        for (var i = 0; i < ColorFamilies.Length; i++)
        {
            var index = i;
            weights.Children.Add(Ui.SliderRow(ColorFamilies[i], bw.Weights[i], BlackAndWhiteAdjustment.MinWeight, BlackAndWhiteAdjustment.MaxWeight,
                v => update(bw = bw.WithWeight(index, v)), 1, "0", 240, 80).Row);
        }
        var tintRows = new StackPanel { Spacing = 8, IsVisible = bw.Tint, Margin = new Thickness(0, 4, 0, 0) };
        tintRows.Children.Add(Ui.SliderRow("Hue", bw.TintHue, 0, 360, v => update(bw = bw with { TintHue = v }), 1, "0", 240, 80).Row);
        tintRows.Children.Add(Ui.SliderRow("Saturation", bw.TintSaturation, 0, 100, v => update(bw = bw with { TintSaturation = v }), 1, "0", 240, 80).Row);
        var tint = Ui.Check("Tint", bw.Tint, v => { tintRows.IsVisible = v; update(bw = bw with { Tint = v }); });
        ToolTip.SetTip(tint, "Color the result while keeping its tones, for a sepia or a cyanotype");
        var reset = Ui.TextButton("Reset", () =>
        {
            update(bw = new BlackAndWhiteAdjustment { Tint = bw.Tint, TintHue = bw.TintHue, TintSaturation = bw.TintSaturation });
            for (var i = 0; i < ColorFamilies.Length; i++) ((Slider)((StackPanel)weights.Children[i]).Children[1]).Value = bw.Weights[i];
        });
        return Ui.Column(10, weights, Ui.Row(12, tint, reset), tintRows);
    }

    private static readonly string[] BalanceRanges = ["Shadows", "Midtones", "Highlights"];
    private static readonly string[] BalancePairs = ["Cyan / Red", "Magenta / Green", "Yellow / Blue"];

    /// <summary>Three shifts each for the shadows, midtones and highlights, with Preserve Luminosity.</summary>
    private static Control ColorBalanceEditor(ColorBalanceAdjustment balance, Action<Adjustment> update)
    {
        var panel = new StackPanel { Spacing = 6 };
        for (var range = 0; range < 3; range++)
        {
            panel.Children.Add(Ui.Label(BalanceRanges[range], Palette.Secondary, weight: FontWeight.SemiBold));
            for (var channel = 0; channel < 3; channel++)
            {
                var (r, c) = (range, channel);
                panel.Children.Add(Ui.SliderRow(BalancePairs[channel], balance.Shift(range, channel), ColorBalanceAdjustment.MinShift, ColorBalanceAdjustment.MaxShift,
                    v => update(balance = balance.WithShift(r, c, v)), 1, "0", 220, 110).Row);
            }
        }
        var preserve = Ui.Check("Preserve Luminosity", balance.PreserveLuminosity, v => update(balance = balance with { PreserveLuminosity = v }));
        ToolTip.SetTip(preserve, "Put each pixel's brightness back afterwards, so only the color moves");
        preserve.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(preserve);
        return panel;
    }

    private static Control GradientMapEditor(Window owner, GradientMapAdjustment map, SKColor foreground, SKColor background, Action<Adjustment> update)
    {
        var bar = new Border { Height = 28, Width = 320, CornerRadius = new CornerRadius(4) };
        void Paint()
        {
            var dark = new SKColor(map.Reversed ? map.Highlights : map.Shadows).ToAvalonia();
            var light = new SKColor(map.Reversed ? map.Shadows : map.Highlights).ToAvalonia();
            bar.Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(dark, 0), new GradientStop(light, 1) }
            };
        }
        Button Swatch(string label, Func<uint> get, Action<uint> set)
        {
            var button = new Button { Content = label };
            button.Click += async (_, _) =>
            {
                if (await Prompts.Color((Window)TopLevel.GetTopLevel(button)!, label, new SKColor(get())) is not { } picked) return;
                set((uint)picked);
                Paint();
                update(map);
            };
            return button;
        }
        var shadows = Swatch("Shadows…", () => map.Shadows, v => map = map with { Shadows = v });
        var highlights = Swatch("Highlights…", () => map.Highlights, v => map = map with { Highlights = v });
        var useColors = Ui.TextButton("Use Foreground/Background", () =>
        {
            map = map with { Shadows = (uint)foreground, Highlights = (uint)background };
            Paint();
            update(map);
        });
        var reversed = Ui.Check("Reverse", map.Reversed, v => { map = map with { Reversed = v }; Paint(); update(map); });
        Paint();
        return Ui.Column(10, bar, Ui.Row(8, shadows, highlights, useColors), reversed);
    }

    /// <summary>The editor for a Filter menu command.</summary>
    public static async Task<FilterSettings?> EditFilter(Window owner, FilterSettings initial, Action<FilterSettings> changed)
    {
        var current = initial;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) => { timer.Stop(); changed(current); };
        void Update(FilterSettings value) { current = value; timer.Stop(); timer.Start(); }
        var panel = new StackPanel { Spacing = 8 };
        void Slider(string label, double value, double min, double max, Func<double, FilterSettings> apply, double step = 1, string format = "0") =>
            panel.Children.Add(Ui.SliderRow(label, value, min, max, v => Update(apply(v)), step, format, 240, 90).Row);
        switch (initial.Kind)
        {
            case FilterKind.GaussianBlur:
                Slider("Radius", initial.Radius, 0.1, 250, v => current with { Radius = v }, 0.1, "0.0");
                break;
            case FilterKind.MotionBlur:
                Slider("Angle", initial.Angle, -90, 90, v => current with { Angle = v });
                Slider("Distance", initial.Radius, 1, 500, v => current with { Radius = v });
                break;
            case FilterKind.Sharpen:
                Slider("Amount", initial.Amount, 0, 200, v => current with { Amount = v });
                Slider("Radius", initial.Radius, 0.5, 20, v => current with { Radius = v }, 0.1, "0.0");
                break;
            case FilterKind.AddNoise:
                Slider("Amount", initial.Amount, 0, 100, v => current with { Amount = v });
                panel.Children.Add(Ui.Check("Monochromatic", initial.Monochrome, v => Update(current with { Monochrome = v })));
                break;
            case FilterKind.LensCorrection:
                Slider("Distortion", initial.Distortion, -100, 100, v => current with { Distortion = v });
                Slider("Vignette", initial.Vignette, -100, 100, v => current with { Vignette = v });
                break;
            case FilterKind.RemoveBackground:
                Slider("Tolerance", initial.Amount, 1, 100, v => current with { Amount = v });
                panel.Children.Add(new TextBlock
                {
                    Text = "Removes the plain backdrop connected to the image's edges. Raise the tolerance to take more.",
                    Foreground = Palette.Secondary, MaxWidth = 380, TextWrapping = TextWrapping.Wrap
                });
                break;
        }
        var dialog = new DialogWindow(FilterSettings.DisplayName(initial.Kind), panel);
        dialog.Opened += (_, _) => changed(current);
        var accepted = await dialog.Ask(owner);
        timer.Stop();
        return accepted ? current : null;
    }
}

/// <summary>A filled histogram for one channel (0–2 RGB, 3 luminance).</summary>
public sealed class HistogramView : Control
{
    private int channel = 3;
    public Histogram? Histogram { get; set; }
    public int Channel { get => channel; set { channel = value; InvalidateVisual(); } }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#1C1C1C")), bounds);
        if (Histogram == null) return;
        Draw(context, Histogram.Channel(channel), bounds, channel switch { 0 => Color.Parse("#E05555"), 1 => Color.Parse("#55C26A"), 2 => Color.Parse("#5590E0"), _ => Color.Parse("#B8B8B8") });
    }

    internal static void Draw(DrawingContext context, int[] bins, Rect bounds, Color color)
    {
        // The tallest few bins are clipped so one spike (a flat background) doesn't flatten everything else.
        var sorted = bins.OrderByDescending(v => v).ToArray();
        double peak = Math.Max(1, sorted[Math.Min(3, sorted.Length - 1)] * 1.1);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(bounds.Left, bounds.Bottom), true);
            for (var i = 0; i < 256; i++)
                g.LineTo(new Point(bounds.Left + i / 255.0 * bounds.Width, bounds.Bottom - Math.Min(1, bins[i] / peak) * bounds.Height));
            g.LineTo(new Point(bounds.Right, bounds.Bottom));
            g.EndFigure(true);
        }
        context.DrawGeometry(new SolidColorBrush(color, 0.75), null, geometry);
    }
}

/// <summary>An editable tone curve.</summary>
public sealed class CurveEditor : Control
{
    private CurvesAdjustment curves = new();
    private int channel;
    private int dragging = -1;

    public event Action<CurvesAdjustment>? Changed;
    public Histogram? Histogram { get; set; }
    public CurvesAdjustment Curves { get => curves; set { curves = value; InvalidateVisual(); } }
    public int Channel { get => channel; set { channel = value; dragging = -1; InvalidateVisual(); } }

    public CurveEditor() => ClipToBounds = true;

    private Point ToScreen(CurvePoint p) => new(p.X / 255 * Bounds.Width, (1 - p.Y / 255) * Bounds.Height);
    private CurvePoint ToCurve(Point p) => new(Math.Clamp(p.X / Bounds.Width * 255, 0, 255), Math.Clamp((1 - p.Y / Bounds.Height) * 255, 0, 255));

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#1C1C1C")), bounds);
        if (Histogram != null) HistogramView.Draw(context, Histogram.Channel(channel == 0 ? 3 : channel - 1), bounds, Color.Parse("#555555"));
        var grid = new Pen(new SolidColorBrush(Color.Parse("#3A3A3A")));
        for (var i = 1; i < 4; i++)
        {
            context.DrawLine(grid, new Point(bounds.Width * i / 4, 0), new Point(bounds.Width * i / 4, bounds.Height));
            context.DrawLine(grid, new Point(0, bounds.Height * i / 4), new Point(bounds.Width, bounds.Height * i / 4));
        }
        context.DrawLine(grid, new Point(0, bounds.Height), new Point(bounds.Width, 0));
        var color = channel switch { 1 => Color.Parse("#FF6B6B"), 2 => Color.Parse("#6BDB7F"), 3 => Color.Parse("#6BA5FF"), _ => Colors.White };
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(ToScreen(new CurvePoint(0, curves.Value(0, channel))), false);
            for (var x = 1; x <= 255; x++) g.LineTo(ToScreen(new CurvePoint(x, curves.Value(x, channel))));
            g.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(new SolidColorBrush(color), 1.5), geometry);
        var points = curves.Channels[channel];
        for (var i = 0; i < points.Length; i++)
        {
            var p = ToScreen(points[i]);
            context.DrawEllipse(i == dragging ? new SolidColorBrush(color) : Brushes.Black, new Pen(new SolidColorBrush(color), 1.5), p, 4, 4);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var position = e.GetPosition(this);
        var points = curves.Channels[channel].ToList();
        dragging = points.FindIndex(p => Math.Abs(ToScreen(p).X - position.X) < 9 && Math.Abs(ToScreen(p).Y - position.Y) < 9);
        if (dragging < 0 && points.Count < 16)
        {
            var added = ToCurve(position);
            // No room for another point between neighbours this close together.
            if (points.Any(p => Math.Abs(p.X - added.X) < 4)) { e.Pointer.Capture(this); return; }
            points.Add(added);
            points.Sort((a, b) => a.X.CompareTo(b.X));
            dragging = points.IndexOf(added);
            Set(points);
        }
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (dragging < 0) return;
        var position = e.GetPosition(this);
        var points = curves.Channels[channel].ToList();
        if (dragging >= points.Count) { dragging = -1; return; }
        var interior = dragging > 0 && dragging < points.Count - 1;
        if (interior && (position.Y < -30 || position.Y > Bounds.Height + 30 || position.X < -30 || position.X > Bounds.Width + 30))
        {
            points.RemoveAt(dragging);
            dragging = -1;
            Set(points);
            return;
        }
        var moved = ToCurve(position);
        // End points only move vertically; interior points stay between their neighbours.
        var x = points[dragging].X;
        if (interior)
        {
            double low = points[dragging - 1].X + 2, high = points[dragging + 1].X - 2;
            if (low <= high) x = Math.Clamp(moved.X, low, high); // Squeezed between close neighbours, it only moves vertically.
        }
        points[dragging] = new CurvePoint(x, moved.Y);
        Set(points);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        dragging = -1;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    private void Set(List<CurvePoint> points)
    {
        curves = curves.WithChannel(channel, points);
        InvalidateVisual();
        Changed?.Invoke(curves);
    }
}
