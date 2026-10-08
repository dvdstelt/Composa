using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Composa.Filters;
using SkiaSharp;

namespace Composa.App.Dialogs;

/// <summary>Editors for every adjustment and filter. Each reports changes live so the canvas can preview them.</summary>
public static class AdjustmentDialogs
{
    private static readonly string[] Channels = ["RGB", "Red", "Green", "Blue"];

    /// <summary>
    /// Shows the editor for an adjustment. <paramref name="changed"/> runs (debounced) on every edit; returns the accepted
    /// settings or null. <paramref name="picture"/> is what the adjustment is applied to, for the editors that show it
    /// (Color Lookup draws each look on it); <paramref name="pickFile"/> asks for a file when an editor loads one.
    /// </summary>
    public static async Task<Adjustment?> Edit(Window owner, Adjustment initial, Action<Adjustment> changed, Histogram? histogram, SKColor foreground, SKColor background,
        Func<SKBitmap?>? picture = null, Func<Task<string?>>? pickFile = null)
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
            // Reset puts a slider back to the value a new adjustment starts with, not to what the dialog opened with.
            ExposureAdjustment exposure => Sliders(
                ("Exposure", exposure.Exposure, -5, 5, 0.01, "0.00", v => Update(exposure = exposure with { Exposure = v }), new ExposureAdjustment().Exposure),
                ("Offset", exposure.Offset, -0.5, 0.5, 0.001, "0.000", v => Update(exposure = exposure with { Offset = v }), new ExposureAdjustment().Offset),
                ("Gamma", exposure.Gamma, 0.1, 5, 0.01, "0.00", v => Update(exposure = exposure with { Gamma = v }), new ExposureAdjustment().Gamma)),
            BrightnessContrastAdjustment bc => Sliders(
                ("Brightness", bc.Brightness, -100, 100, 1, "0", v => Update(bc = bc with { Brightness = v }), new BrightnessContrastAdjustment().Brightness),
                ("Contrast", bc.Contrast, -100, 100, 1, "0", v => Update(bc = bc with { Contrast = v }), new BrightnessContrastAdjustment().Contrast)),
            GrainAdjustment grain => Sliders(
                ("Amount", grain.Amount, 0, 100, 1, "0", v => Update(grain = grain with { Amount = v }), new GrainAdjustment().Amount),
                ("Size", grain.Size, 0.5, 20, 0.1, "0.0", v => Update(grain = grain with { Size = v }), new GrainAdjustment().Size),
                ("Roughness", grain.Roughness, 0, 100, 1, "0", v => Update(grain = grain with { Roughness = v }), new GrainAdjustment().Roughness)),
            GaussianBlurAdjustment blur => Sliders(
                ("Radius", blur.Radius, GaussianBlurAdjustment.MinRadius, GaussianBlurAdjustment.MaxRadius, 0.1, "0.0", v => Update(blur = blur with { Radius = v }), new GaussianBlurAdjustment().Radius)),
            MotionBlurAdjustment motion => Ui.Column(8,
                Ui.AngleField("Angle", motion.Angle, -180, 180, v => Update(motion = motion with { Angle = v }), FieldWidth, Controls.AngleDialStyle.Line, new MotionBlurAdjustment().Angle),
                Ui.SliderField("Distance", motion.Distance, MotionBlurAdjustment.MinDistance, 500, v => Update(motion = motion with { Distance = v }), 1, "0", FieldWidth, reset: new MotionBlurAdjustment().Distance)),
            AddNoiseAdjustment noise => NoiseEditor(noise, Update),
            GradientMapAdjustment map => GradientMapEditor(owner, map, foreground, background, Update),
            BlackAndWhiteAdjustment bw => BlackAndWhiteEditor(bw, Update),
            ColorBalanceAdjustment balance => ColorBalanceEditor(balance, Update),
            ColorLookupAdjustment lookup => ColorLookupEditor(owner, lookup, picture, pickFile, Update),
            _ => Ui.Label(Loc.T("This adjustment has no settings."), Palette.Secondary)
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
        GaussianBlurAdjustment blur => blur with { Radius = 0 },
        MotionBlurAdjustment motion => motion with { Distance = 0 },
        AddNoiseAdjustment noise => noise with { Amount = 0 },
        GradientMapAdjustment => new BrightnessContrastAdjustment(),
        InvertAdjustment => new BrightnessContrastAdjustment(),
        BlackAndWhiteAdjustment => new BrightnessContrastAdjustment(),
        var other => other
    };

    /// <summary>One width for every field in these dialogs, so their right edges line up whatever the labels are.</summary>
    private const double FieldWidth = 330;

    private static Control Sliders(params (string Label, double Value, double Min, double Max, double Step, string Format, Action<double> Changed, double Reset)[] rows)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var r in rows) panel.Children.Add(Ui.SliderField(r.Label, r.Value, r.Min, r.Max, r.Changed, r.Step, r.Format, FieldWidth, reset: r.Reset));
        return panel;
    }

    private static readonly string[] NoiseDistributions = ["Uniform", "Gaussian"];

    private static Control NoiseEditor(AddNoiseAdjustment noise, Action<Adjustment> update)
    {
        var amount = Ui.SliderField("Amount", noise.Amount, AddNoiseAdjustment.MinAmount, 100, v => update(noise = noise with { Amount = v }), 0.1, "0.0", FieldWidth, reset: new AddNoiseAdjustment().Amount);
        var distribution = Ui.Combo(NoiseDistributions, noise.Gaussian ? "Gaussian" : "Uniform", d => Loc.T(d), d => update(noise = noise with { Gaussian = d == "Gaussian" }), 120);
        var mono = Ui.Check("Monochromatic", noise.Monochromatic, v => update(noise = noise with { Monochromatic = v }));
        return Ui.Column(10, amount, Ui.Row(10, Ui.Label(Loc.T("Distribution"), Palette.Secondary), distribution), mono);
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
            rows.Children.Add(Ui.SliderField("Input black", range.InputBlack, 0, 253, v => Set(range with { InputBlack = Math.Min(v, range.InputWhite - 2) }), 1, "0", FieldWidth));
            rows.Children.Add(Ui.SliderField("Midtones", range.Gamma, 0.1, 5, v => Set(range with { Gamma = v }), 0.01, "0.00", FieldWidth));
            rows.Children.Add(Ui.SliderField("Input white", range.InputWhite, 2, 255, v => Set(range with { InputWhite = Math.Max(v, range.InputBlack + 2) }), 1, "0", FieldWidth));
            rows.Children.Add(Ui.SliderField("Output black", range.OutputBlack, 0, 255, v => Set(range with { OutputBlack = v }), 1, "0", FieldWidth));
            rows.Children.Add(Ui.SliderField("Output white", range.OutputWhite, 0, 255, v => Set(range with { OutputWhite = v }), 1, "0", FieldWidth));
        }
        var picker = Ui.Combo(Channels, "RGB", c => Loc.T(c), c => { channel = Array.IndexOf(Channels, c); graph.Channel = channel == 0 ? 3 : channel - 1; Build(); });
        var auto = Ui.TextButton("Auto", () =>
        {
            if (histogram == null) return;
            update(levels = LevelsAdjustment.Auto(histogram));
            Build();
        });
        var reset = Ui.TextButton("Reset", () => { update(levels = new LevelsAdjustment()); Build(); });
        Build();
        return Ui.Column(10, Ui.Row(10, Ui.Label(Loc.T("Channel"), Palette.Secondary), picker, auto, reset), graph, rows);
    }

    private static Control CurvesEditor(CurvesAdjustment curves, Histogram? histogram, Action<Adjustment> update)
    {
        var editor = new CurveEditor { Width = 300, Height = 300, Curves = curves, Histogram = histogram };
        editor.Changed += value => update(curves = value);
        var picker = Ui.Combo(Channels, "RGB", c => Loc.T(c), c => editor.Channel = Array.IndexOf(Channels, c));
        var reset = Ui.TextButton("Reset", () => { editor.Curves = curves = new CurvesAdjustment(); update(curves); });
        return Ui.Column(10, Ui.Row(10, Ui.Label(Loc.T("Channel"), Palette.Secondary), picker, reset), editor,
            Ui.Label(Loc.T("Click to add a point, drag to move it, drag it off the graph to remove it."), Palette.Secondary));
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
            // The hue track is the hue circle centred on the range's own color (red for Master), so the color under the value is
            // what that range becomes; colorizing picks an absolute hue, so its track runs red to red. Saturation runs from gray to
            // the range's color, or to the tint being picked. Reset is no shift; colorizing at zero is Photoshop's starting tint.
            var rangeHue = Math.Max(0, (int)range - 1) * 60.0;
            Controls.SliderField saturation = null!;
            var hueField = Ui.SliderField("Hue", shift.Hue, colorizing ? 0 : -180, colorizing ? 360 : 180, v =>
            {
                Set(shift with { Hue = v });
                if (colorizing) saturation.Track = Controls.SliderTracks.Saturation(v);
            }, 1, "0", FieldWidth, Controls.SliderTracks.Spectrum(colorizing ? 180 : rangeHue), reset: 0);
            saturation = Ui.SliderField("Saturation", shift.Saturation, -100, 100, v => Set(shift with { Saturation = v }), 1, "0", FieldWidth,
                Controls.SliderTracks.Saturation(colorizing ? shift.Hue : rangeHue), reset: 0);
            rows.Children.Add(hueField);
            rows.Children.Add(saturation);
            rows.Children.Add(Ui.SliderField("Lightness", shift.Lightness, -100, 100, v => Set(shift with { Lightness = v }), 1, "0", FieldWidth, Controls.SliderTracks.Lightness, reset: 0));
        }
        var picker = Ui.Combo(Enum.GetValues<HueRange>(), HueRange.Master, r => Loc.T(r.ToString()), r => { range = r; Build(); });
        var colorize = Ui.Check("Colorize", hue.Colorize, v =>
        {
            var master = hue.Shifts[0];
            hue = (hue with { Colorize = v }).WithShift(HueRange.Master, master with { Hue = v ? Math.Max(0, master.Hue) : Math.Min(180, master.Hue) });
            update(hue);
            Build();
        });
        Build();
        return Ui.Column(10, Ui.Row(10, Ui.Label(Loc.T("Range"), Palette.Secondary), picker, colorize), rows);
    }

    private static readonly string[] ColorFamilies = ["Reds", "Yellows", "Greens", "Cyans", "Blues", "Magentas"];

    /// <summary>Each slider says how bright that family of colors becomes, as Photoshop's do; the tint colors the gray.</summary>
    private static Control BlackAndWhiteEditor(BlackAndWhiteAdjustment bw, Action<Adjustment> update)
    {
        var defaults = new BlackAndWhiteAdjustment();
        var weights = new StackPanel { Spacing = 8 };
        for (var i = 0; i < ColorFamilies.Length; i++)
        {
            var index = i;
            // Each track runs dark to light in the family's own hue, 60 degrees apart from red round to magenta.
            weights.Children.Add(Ui.SliderField(ColorFamilies[i], bw.Weights[i], BlackAndWhiteAdjustment.MinWeight, BlackAndWhiteAdjustment.MaxWeight,
                v => update(bw = bw.WithWeight(index, v)), 1, "0", FieldWidth, Controls.SliderTracks.Luminance(i * 60), reset: defaults.Weights[i]));
        }
        var tintRows = new StackPanel { Spacing = 8, IsVisible = bw.Tint, Margin = new Thickness(0, 4, 0, 0) };
        Controls.SliderField tintSaturation = null!;
        tintRows.Children.Add(Ui.SliderField("Hue", bw.TintHue, 0, 360, v =>
        {
            update(bw = bw with { TintHue = v });
            tintSaturation.Track = Controls.SliderTracks.Saturation(v);
        }, 1, "0", FieldWidth, Controls.SliderTracks.Spectrum(180), reset: defaults.TintHue));
        tintSaturation = Ui.SliderField("Saturation", bw.TintSaturation, 0, 100, v => update(bw = bw with { TintSaturation = v }), 1, "0", FieldWidth,
            Controls.SliderTracks.Saturation(bw.TintHue), reset: defaults.TintSaturation);
        tintRows.Children.Add(tintSaturation);
        var tint = Ui.Check("Tint", bw.Tint, v => { tintRows.IsVisible = v; update(bw = bw with { Tint = v }); });
        ToolTip.SetTip(tint, Loc.T("Color the result while keeping its tones, for a sepia or a cyanotype"));
        var reset = Ui.TextButton("Reset", () =>
        {
            update(bw = new BlackAndWhiteAdjustment { Tint = bw.Tint, TintHue = bw.TintHue, TintSaturation = bw.TintSaturation });
            for (var i = 0; i < ColorFamilies.Length; i++) ((Controls.SliderField)weights.Children[i]).Value = bw.Weights[i];
        });
        return Ui.Column(10, weights, Ui.Row(12, tint, reset), tintRows);
    }

    private static readonly string[] BalanceRanges = ["Shadows", "Midtones", "Highlights"];
    private static readonly string[] BalancePairs = ["Cyan / Red", "Magenta / Green", "Yellow / Blue"];
    private static readonly IReadOnlyList<Color>[] BalanceTracks = [Controls.SliderTracks.CyanRed, Controls.SliderTracks.MagentaGreen, Controls.SliderTracks.YellowBlue];

    /// <summary>Three shifts each for the shadows, midtones and highlights, with Preserve Luminosity.</summary>
    private static Control ColorBalanceEditor(ColorBalanceAdjustment balance, Action<Adjustment> update)
    {
        var panel = new StackPanel { Spacing = 6 };
        for (var range = 0; range < 3; range++)
        {
            panel.Children.Add(Ui.Label(Loc.T(BalanceRanges[range]), Palette.Secondary, weight: FontWeight.SemiBold));
            for (var channel = 0; channel < 3; channel++)
            {
                var (r, c) = (range, channel);
                panel.Children.Add(Ui.SliderField(BalancePairs[channel], balance.Shift(range, channel), ColorBalanceAdjustment.MinShift, ColorBalanceAdjustment.MaxShift,
                    v => update(balance = balance.WithShift(r, c, v)), 1, "0", FieldWidth, BalanceTracks[channel], reset: 0));
            }
        }
        var preserve = Ui.Check("Preserve Luminosity", balance.PreserveLuminosity, v => update(balance = balance with { PreserveLuminosity = v }));
        ToolTip.SetTip(preserve, Loc.T("Put each pixel's brightness back afterwards, so only the color moves"));
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
            var button = new Button { Content = Loc.T(label) };
            button.Click += async (_, _) =>
            {
                if (await Prompts.Color((Window)TopLevel.GetTopLevel(button)!, Loc.T(label), new SKColor(get())) is not { } picked) return;
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

    /// <summary>The side of a look's thumbnail, and the width of the loaded file's name beside the Load button.</summary>
    private const int LookThumb = 72;

    /// <summary>
    /// Color Lookup: every bundled look drawn on the picture being edited, so each tile shows what it does, a Load
    /// File… button for a .cube or .3dl of your own (shown as a tile of its own once loaded), and Amount.
    /// </summary>
    private static Control ColorLookupEditor(Window owner, ColorLookupAdjustment lookup, Func<SKBitmap?>? picture, Func<Task<string?>>? pickFile, Action<Adjustment> update)
    {
        // The sample lives as long as the dialog: Load File… draws a new tile on it, so it must not go with this call.
        var sample = LookSample(picture?.Invoke());
        var tiles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        tiles.DetachedFromVisualTree += (_, _) => sample.Dispose();
        var chosen = new List<(Border Frame, Func<bool> Selected)>();

        void Refresh()
        {
            foreach (var (frame, selected) in chosen) frame.BorderBrush = selected() ? Palette.Accent : Brushes.Transparent;
        }

        Border Tile(string name, string display, ColorLattice lattice, Func<bool> selected, string tip)
        {
            using var shown = sample.Copy();
            new ColorLookupAdjustment { Lattice = lattice }.Apply(shown);
            var image = new Image { Source = Ui.ToAvaloniaBitmap(shown, LookThumb), Width = LookThumb, Height = LookThumb * sample.Height / sample.Width, Stretch = Stretch.Uniform };
            var label = Ui.Label(display, size: 11);
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.MaxWidth = LookThumb;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            var frame = new Border
            {
                Child = Ui.Column(4, image, label), Padding = new Thickness(3), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4),
                Cursor = new Cursor(StandardCursorType.Hand), Background = Brushes.Transparent, Tag = name
            };
            ToolTip.SetTip(frame, tip);
            frame.PointerPressed += (_, _) => { update(lookup = lookup with { Lattice = lattice, Source = name }); Refresh(); };
            chosen.Add((frame, selected));
            return frame;
        }

        foreach (var name in Looks.Names)
            tiles.Children.Add(Tile(name, Loc.T(name), Looks.Find(name)!, () => lookup.Lattice?.Id == Looks.Find(name)!.Id, Loc.T(Looks.Describe(name))));

        // A file's tile comes and goes with the file: loading another replaces it.
        var fileName = Ui.Label("", Palette.Secondary, 12);
        fileName.TextTrimming = TextTrimming.CharacterEllipsis;
        fileName.VerticalAlignment = VerticalAlignment.Center;
        Border? fileTile = null;
        void ShowFile(string name, ColorLattice lattice)
        {
            if (fileTile != null) { tiles.Children.Remove(fileTile); chosen.RemoveAll(c => c.Frame == fileTile); }
            fileTile = Tile(name, name, lattice, () => lookup.Lattice?.Id == lattice.Id, lattice.Title.Length > 0 ? lattice.Title : name);
            tiles.Children.Add(fileTile);
            fileName.Text = name;
        }
        if (lookup.Lattice is { } own && Looks.Names.All(name => Looks.Find(name)!.Id != own.Id)) ShowFile(lookup.Source, own);

        var load = Ui.TextButton("Load File…", async () =>
        {
            if (pickFile == null || await pickFile() is not { } path) return;
            ColorLattice lattice;
            try { lattice = ColorLattice.Load(path); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                await Prompts.Alert(owner, Loc.T("Color Lookup"), Loc.Format("{0} could not be read. {1}", Path.GetFileName(path), error.Message));
                return;
            }
            var name = Path.GetFileName(path);
            ShowFile(name, lattice);
            update(lookup = lookup with { Lattice = lattice, Source = name });
            Refresh();
        });
        var amount = Ui.SliderField("Amount", lookup.Amount, 0, 100, v => update(lookup = lookup with { Amount = v }), 1, "0", FieldWidth, reset: new ColorLookupAdjustment().Amount);
        Refresh();
        return Ui.Column(10, tiles, Ui.Row(8, load, fileName), amount);
    }

    /// <summary>
    /// What the look tiles are drawn on: the picture, reduced to thumbnail size, or a ramp of hues over lightness when
    /// there is no picture in color (a mask being edited, or nothing yet).
    /// </summary>
    private static SKBitmap LookSample(SKBitmap? picture)
    {
        if (picture is { ColorType: SKColorType.Rgba8888 })
        {
            var scale = Math.Min(1, Math.Min((double)LookThumb / picture.Width, (double)LookThumb / picture.Height));
            int w = Math.Max(1, (int)Math.Round(picture.Width * scale)), h = Math.Max(1, (int)Math.Round(picture.Height * scale));
            var small = Composa.Rendering.Pixels.NewColor(w, h);
            using (var canvas = new SKCanvas(small))
            using (var image = SKImage.FromPixels(picture.PeekPixels()))
                canvas.DrawImage(image, new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            return small;
        }
        var ramp = Composa.Rendering.Pixels.NewColor(LookThumb, LookThumb * 2 / 3);
        for (var y = 0; y < ramp.Height; y++)
            for (var x = 0; x < ramp.Width; x++)
                ramp.SetPixel(x, y, SKColor.FromHsv(x * 360f / ramp.Width, 70, 100 - y * 60f / ramp.Height));
        return ramp;
    }

    /// <summary>The editor for a Filter menu command.</summary>
    /// <param name="canDetect">Remove Background: whether the Detect choice is offered. Editing a mask, only the plain backdrop makes sense.</param>
    public static async Task<FilterSettings?> EditFilter(Window owner, FilterSettings initial, Action<FilterSettings> changed, bool canDetect = true)
    {
        var current = initial;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) => { timer.Stop(); changed(current); };
        void Update(FilterSettings value) { current = value; timer.Stop(); timer.Start(); }
        var panel = new StackPanel { Spacing = 8 };
        // A filter opens with its defaults, so Reset puts a slider back to the value it opened with.
        void Slider(string label, double value, double min, double max, Func<double, FilterSettings> apply, double step = 1, string format = "0") =>
            panel.Children.Add(Ui.SliderField(label, value, min, max, v => Update(apply(v)), step, format, FieldWidth, reset: value));
        switch (initial.Kind)
        {
            case FilterKind.GaussianBlur:
                Slider("Radius", initial.Radius, 0.1, 250, v => current with { Radius = v }, 0.1, "0.0");
                break;
            case FilterKind.MotionBlur:
                // The blur runs along a line, so its dial is a line rather than a light. It turns the full circle as Photoshop's field
                // allows, although 200 blurs the same as 20, because a dial that turns half way feels stuck.
                panel.Children.Add(Ui.AngleField("Angle", initial.Angle, -180, 180, v => Update(current with { Angle = v }), FieldWidth, Controls.AngleDialStyle.Line, initial.Angle));
                Slider("Distance", initial.Radius, 1, 500, v => current with { Radius = v });
                break;
            case FilterKind.Sharpen:
                Slider("Amount", initial.Amount, 0, 200, v => current with { Amount = v });
                Slider("Radius", initial.Radius, 0.5, 20, v => current with { Radius = v }, 0.1, "0.0");
                break;
            case FilterKind.AddNoise:
                Slider("Amount", initial.Amount, 0, 100, v => current with { Amount = v });
                panel.Children.Add(Ui.Row(10, Ui.Label(Loc.T("Distribution"), Palette.Secondary),
                    Ui.Combo(NoiseDistributions, initial.Gaussian ? "Gaussian" : "Uniform", d => Loc.T(d), d => Update(current with { Gaussian = d == "Gaussian" }), 120)));
                panel.Children.Add(Ui.Check("Monochromatic", initial.Monochrome, v => Update(current with { Monochrome = v })));
                break;
            case FilterKind.Vignette:
                var swatch = new Border { Width = 44, Height = 24, CornerRadius = new CornerRadius(3), BorderBrush = Brushes.White, BorderThickness = new Thickness(1), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
                void PaintSwatch() => swatch.Background = new SolidColorBrush(new SKColor(current.VignetteColor).ToAvalonia());
                PaintSwatch();
                ToolTip.SetTip(swatch, Loc.T("Choose the vignette color"));
                swatch.PointerPressed += async (_, _) =>
                {
                    if (await Prompts.Color(owner, Loc.T("Vignette Color"), new SKColor(current.VignetteColor)) is not { } picked) return;
                    Update(current with { VignetteColor = (uint)picked | 0xFF000000 });
                    PaintSwatch();
                };
                var colorLabel = Ui.Label(Loc.T("Color"), Palette.Secondary);
                colorLabel.Width = 90;
                panel.Children.Add(Ui.Row(8, colorLabel, swatch));
                Slider("Amount", initial.VignetteAmount, 0, 100, v => current with { VignetteAmount = v });
                Slider("Midpoint", initial.VignetteMidpoint, 0, 100, v => current with { VignetteMidpoint = v });
                Slider("Roundness", initial.VignetteRoundness, -100, 100, v => current with { VignetteRoundness = v });
                Slider("Feather", initial.VignetteFeather, 0, 100, v => current with { VignetteFeather = v });
                Slider("Highlights", initial.VignetteHighlights, 0, 100, v => current with { VignetteHighlights = v });
                panel.Children.Add(new TextBlock
                {
                    Text = Loc.T("Blends the color into the edges while keeping the center. On an empty layer it paints across the whole canvas."),
                    Foreground = Palette.Secondary, MaxWidth = 380, TextWrapping = TextWrapping.Wrap
                });
                break;
            case FilterKind.BloomGlow:
                Slider("Amount", initial.BloomAmount, 0, 100, v => current with { BloomAmount = v });
                Slider("Radius", initial.BloomRadius, 1, 150, v => current with { BloomRadius = v });
                break;
            case FilterKind.TonalContrast:
                Slider("Amount", initial.TonalAmount, 0, 100, v => current with { TonalAmount = v });
                Slider("Shadows", initial.TonalShadows, -100, 100, v => current with { TonalShadows = v });
                Slider("Midtones", initial.TonalMidtones, -100, 100, v => current with { TonalMidtones = v });
                Slider("Highlights", initial.TonalHighlights, -100, 100, v => current with { TonalHighlights = v });
                Slider("Radius", initial.TonalRadius, 1, 100, v => current with { TonalRadius = v });
                break;
            case FilterKind.LensCorrection:
                Slider("Remove Distortion", initial.Distortion, -100, 100, v => current with { Distortion = v });
                panel.Children.Add(new TextBlock
                {
                    Text = Loc.T("Positive straightens lines that bow outward (barrel); negative, lines that bow inward (pincushion)."),
                    Foreground = Palette.Secondary, MaxWidth = 380, TextWrapping = TextWrapping.Wrap
                });
                break;
            case FilterKind.Painterly:
                panel.Children.Add(Ui.Row(10, Ui.Label(Loc.T("Style"), Palette.Secondary),
                    Ui.Combo(Enum.GetValues<PainterlyStyle>(), initial.Painterly.Style, v => Loc.T(PainterlySettings.DisplayName(v)), v => Update(current with { Painterly = current.Painterly with { Style = v } }), 140)));
                Slider("Brush Size", initial.Painterly.BrushSize, 0, 200, v => current with { Painterly = current.Painterly with { BrushSize = v } });
                Slider("Passes", initial.Painterly.Passes, 1, 4, v => current with { Painterly = current.Painterly with { Passes = (int)Math.Round(v) } });
                Slider("Detail", initial.Painterly.Detail, 0, 100, v => current with { Painterly = current.Painterly with { Detail = v } });
                panel.Children.Add(new TextBlock
                {
                    Text = Loc.T("Repaints the layer in brush strokes that follow the picture's edges, the largest brush first and each smaller one only where the picture still differs. A brush size of 0 fits the brush to the picture. Gaps between strokes stay transparent."),
                    Foreground = Palette.Secondary, MaxWidth = 380, TextWrapping = TextWrapping.Wrap
                });
                break;
            case FilterKind.Dither:
            {
                // The controls follow the style and the colors, so the section is rebuilt when a menu changes them;
                // the pixel shape row stays and is only enabled while the pixels are chunky, so a slider drag never
                // rebuilds the slider under the pointer.
                var section = new StackPanel { Spacing = 8 };
                panel.Children.Add(section);
                var styles = DitherSettings.Groups.SelectMany(g => g).ToList();
                var defaults = new DitherSettings();
                Control? shapeRow = null;
                void Set(Func<DitherSettings, DitherSettings> change)
                {
                    Update(current with { Dither = change(current.Dither) });
                    if (shapeRow != null) shapeRow.IsEnabled = current.Dither.PixelSize > 1;
                }
                void Rebuild()
                {
                    var d = current.Dither;
                    section.Children.Clear();
                    shapeRow = null;
                    void DitherSlider(string label, double value, double min, double max, Func<double, DitherSettings, DitherSettings> apply, double reset) =>
                        section.Children.Add(Ui.SliderField(label, value, min, max, v => Set(dd => apply(v, dd)), 1, "0", FieldWidth, reset: reset));
                    Control Swatch(string title, Func<DitherSettings, uint> get, Func<uint, DitherSettings, DitherSettings> apply)
                    {
                        var swatch = new Border { Width = 44, Height = 24, CornerRadius = new CornerRadius(3), BorderBrush = Brushes.White, BorderThickness = new Thickness(1), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
                        void Paint() => swatch.Background = new SolidColorBrush(new SKColor(get(current.Dither)).ToAvalonia());
                        Paint();
                        ToolTip.SetTip(swatch, Loc.Format("Choose the {0} color", Loc.T(title)));
                        swatch.PointerPressed += async (_, _) =>
                        {
                            // The picker shows on the layer as it goes; Cancel puts the color back.
                            var before = current.Dither;
                            var picked = await Prompts.Color(owner, Loc.T(title), new SKColor(get(before)), color => { Set(dd => apply((uint)color | 0xFF000000, dd)); Paint(); });
                            Set(_ => picked is { } color ? apply((uint)color | 0xFF000000, before) : before);
                            Paint();
                        };
                        return swatch;
                    }

                    section.Children.Add(Ui.Row(10, Ui.Label(Loc.T("Style"), Palette.Secondary),
                        Ui.Combo(styles, d.Style, v => Loc.T(DitherSettings.DisplayName(v)), v => { Set(dd => dd with { Style = v }); Rebuild(); }, 200)));
                    if (d.Style != DitherStyle.Ascii) DitherSlider("Pixel Size", d.PixelSize, DitherSettings.MinPixelSize, DitherSettings.MaxPixelSize, (v, dd) => dd with { PixelSize = (int)Math.Round(v) }, defaults.PixelSize);
                    else DitherSlider("Text Size", d.TextSize, DitherSettings.MinTextSize, DitherSettings.MaxTextSize, (v, dd) => dd with { TextSize = (int)Math.Round(v) }, defaults.TextSize);
                    if (d.IsHalftone)
                    {
                        DitherSlider("Cell Size", d.CellSize, DitherSettings.MinCellSize, DitherSettings.MaxCellSize, (v, dd) => dd with { CellSize = (int)Math.Round(v) }, defaults.CellSize);
                        section.Children.Add(Ui.AngleField("Angle", d.Angle, -90, 90, v => Set(dd => dd with { Angle = v }), FieldWidth, Controls.AngleDialStyle.Line, defaults.Angle));
                    }
                    if (d.Style == DitherStyle.Ascii)
                    {
                        var characters = new TextBox { Text = d.Characters, Width = FieldWidth - 90, FontFamily = new FontFamily("monospace") };
                        ToolTip.SetTip(characters, Loc.T("The characters to draw with, in any order: each spot gets the one whose ink best matches its tone"));
                        // The property rather than TextChanged, which the box raises only after it has been through the input loop.
                        characters.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Set(dd => dd with { Characters = characters.Text ?? "" }); };
                        section.Children.Add(Ui.Row(10, Ui.Label(Loc.T("Characters"), Palette.Secondary), characters));
                    }
                    if (d.HasTones) DitherSlider("Tones", d.Levels, DitherSettings.MinLevels, DitherSettings.MaxLevels, (v, dd) => dd with { Levels = (int)Math.Round(v) }, defaults.Levels);
                    if (d.Diffuses) DitherSlider("Diffusion", d.Diffusion, 0, 100, (v, dd) => dd with { Diffusion = v }, defaults.Diffusion);
                    DitherSlider("Density", d.Density, -100, 100, (v, dd) => dd with { Density = v }, defaults.Density);
                    DitherSlider("Contrast", d.Contrast, -100, 100, (v, dd) => dd with { Contrast = v }, defaults.Contrast);
                    var colorsRow = new List<Control>
                    {
                        Ui.Label(Loc.T("Colors"), Palette.Secondary),
                        Ui.Combo(Enum.GetValues<DitherColors>(), d.Colors, v => Loc.T(DitherSettings.DisplayName(v)), v => { Set(dd => dd with { Colors = v }); Rebuild(); }, 140)
                    };
                    if (d.Colors == DitherColors.TwoColors)
                    {
                        colorsRow.Add(Ui.Label(Loc.T("Dark"), Palette.Secondary));
                        colorsRow.Add(Swatch("Dark", dd => dd.Dark, (c, dd) => dd with { Dark = c }));
                        colorsRow.Add(Ui.Label(Loc.T("Light"), Palette.Secondary));
                        colorsRow.Add(Swatch("Light", dd => dd.Light, (c, dd) => dd with { Light = c }));
                    }
                    section.Children.Add(Ui.Row(10, colorsRow.ToArray()));
                    if (d.Style != DitherStyle.Ascii)
                    {
                        shapeRow = Ui.Row(10, Ui.Label(Loc.T("Pixel Shape"), Palette.Secondary),
                            Ui.Combo(Enum.GetValues<DitherPixelShape>(), d.PixelShape, v => Loc.T(DitherSettings.DisplayName(v)), v => Set(dd => dd with { PixelShape = v }), 140));
                        ToolTip.SetTip(shapeRow, Loc.T("Draw each chunky pixel as a solid square, or as a round dot like a dot-matrix screen"));
                        shapeRow.IsEnabled = d.PixelSize > 1;
                        section.Children.Add(shapeRow);
                    }
                    if (d.DrawsMarks)
                    {
                        var lightOnDark = Ui.Check("Light on Dark", d.LightOnDark, v => Set(dd => dd with { LightOnDark = v }));
                        ToolTip.SetTip(lightOnDark, Loc.T("Draw the marks for the light tones on the dark color, like a glowing screen"));
                        section.Children.Add(lightOnDark);
                    }
                }
                Rebuild();
                panel.Children.Add(new TextBlock
                {
                    Text = Loc.T("Turns the layer into dithered pixels. Diffusion and Bayer styles quantize to a number of tones; halftone shapes, Mac patterns and ASCII draw marks that cover as much of each cell as the tone calls for. Pixel Size makes chunky pixels. Density adds or removes ink before dithering."),
                    Foreground = Palette.Secondary, MaxWidth = 380, TextWrapping = TextWrapping.Wrap
                });
                break;
            }
            case FilterKind.RemoveBackground:
            {
                var detects = new[] { Composa.Vision.SubjectDetect.Any, Composa.Vision.SubjectDetect.Person, Composa.Vision.SubjectDetect.Backdrop };
                var tolerance = Ui.SliderField("Tolerance", initial.Amount, 1, 100, v => Update(current with { Amount = v }), 1, "0", FieldWidth, reset: initial.Amount);
                var note = new TextBlock { Foreground = Palette.Secondary, MaxWidth = 380, TextWrapping = TextWrapping.Wrap };
                void Describe()
                {
                    var plain = Composa.Vision.SubjectFinder.Resolve(current.Detect) == Composa.Vision.SubjectDetect.Backdrop;
                    tolerance.IsEnabled = plain;
                    note.Text = plain
                        ? Loc.T("Removes the plain backdrop connected to the layer's edges by making it transparent. Raise the tolerance to take more.")
                        : Loc.T("A model run on this machine finds the subject and hides everything else behind a layer mask, so a wrong edge can be painted back.");
                    if (!plain && Composa.Vision.SubjectFinder.FallbackReason(current.Detect) is { } reason) note.Text = Loc.T(reason);
                }
                var detect = Ui.Combo(detects, initial.Detect, v => Loc.T(Composa.Vision.SubjectFinder.DisplayName(v)), v => { Update(current with { Detect = v }); Describe(); }, 160);
                detect.IsEnabled = canDetect;
                ToolTip.SetTip(detect, Loc.T(canDetect ? "A model for any subject or for a person, or the plain backdrop touching the layer's edges" : "A mask is edited by erasing, so only the plain backdrop applies"));
                panel.Children.Add(Ui.Row(10, Ui.Label(Loc.T("Detect"), Palette.Secondary), detect));
                panel.Children.Add(tolerance);
                panel.Children.Add(note);
                Describe();
                break;
            }
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
