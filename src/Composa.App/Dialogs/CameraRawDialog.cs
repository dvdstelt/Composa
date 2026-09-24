using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Composa.Filters;
using SkiaSharp;

namespace Composa.App.Dialogs;

/// <summary>
/// The Camera Raw Filter's panel: a histogram of the graded layer, a thumbnail that doubles as the white-balance
/// eyedropper, then the groups Light, Color, Effects, Curve, Color Mixer, Color Grading, Detail, Optics and
/// Calibration, each collapsible and switchable off with an eye without clearing its sliders. Changes preview live
/// on the canvas through <paramref name="changed"/>; OK returns the grade as rendered (a hidden group contributes nothing).
/// </summary>
public static class CameraRawDialog
{
    private const double LabelWidth = 96, FieldWidth = 300;

    /// <param name="original">The layer's pixels before the filter, for the eyedropper and Auto.</param>
    /// <param name="graded">Reads the layer as currently previewed, for the histogram.</param>
    public static async Task<CameraRawSettings?> Show(Window owner, CameraRawSettings initial, SKBitmap original, Action<CameraRawSettings> changed, Func<SKBitmap?> graded)
    {
        var current = initial;
        var hidden = new HashSet<CameraRawGroup>();
        CameraRawSettings Rendered() => hidden.Aggregate(current, (settings, group) => settings.Without(group));
        var histogram = new HistogramView { Width = 300, Height = 90 };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            changed(Rendered());
            if (graded() is { } pixels) { histogram.Histogram = Histogram.Of(pixels); histogram.InvalidateVisual(); }
        };
        var eyes = new Dictionary<CameraRawGroup, Button>();
        void Update(CameraRawSettings value)
        {
            current = value;
            foreach (var (group, eye) in eyes) eye.IsVisible = current.Adjusts(group);
            timer.Stop();
            timer.Start();
        }

        // The thumbnail: click a pixel that should be neutral and Temperature and Tint follow.
        var thumb = Ui.ToAvaloniaBitmap(original, 300);
        var preview = new Image { Source = thumb, Width = thumb.PixelSize.Width, Height = thumb.PixelSize.Height, Stretch = Stretch.Uniform, Cursor = new Cursor(StandardCursorType.Cross) };
        ToolTip.SetTip(preview, "Click a pixel that should be neutral to set the white balance from it");
        var readout = Ui.Label("R —   G —   B —", Palette.Secondary);
        readout.FontSize = 11;
        Action<double>? setTemperature = null, setTint = null;
        ComboBox? balance = null;
        preview.PointerMoved += (_, e) =>
        {
            var p = e.GetPosition(preview);
            var color = CameraRawPixels.StraightColor(original, (int)(p.X / preview.Width * original.Width), (int)(p.Y / preview.Height * original.Height));
            readout.Text = color is { } c ? $"R {Math.Round(c.Red * 255)}   G {Math.Round(c.Green * 255)}   B {Math.Round(c.Blue * 255)}" : "R —   G —   B —";
        };
        preview.PointerPressed += (_, e) =>
        {
            var p = e.GetPosition(preview);
            if (CameraRawPixels.StraightColor(original, (int)(p.X / preview.Width * original.Width), (int)(p.Y / preview.Height * original.Height)) is not { } c) return;
            if (CameraRawSettings.NeutralizeSrgb(c.Red, c.Green, c.Blue) is not { } solved) return;
            Update(current with { Temperature = Math.Clamp(solved.Temperature, -100, 100), Tint = Math.Clamp(solved.Tint, -100, 100), WhiteBalance = CameraRawWhiteBalance.Custom });
            setTemperature?.Invoke(current.Temperature);
            setTint?.Invoke(current.Tint);
            if (balance != null) balance.SelectedIndex = 0;
        };

        var groups = new StackPanel { Spacing = 6 };
        Expander Group(CameraRawGroup group, string title, Control body, bool open = false)
        {
            var eye = new Button { Classes = { "flat" }, Padding = new Thickness(4), Content = Icons.Create(Icons.Eye, 13, Palette.Secondary), IsVisible = initial.Adjusts(group) };
            ToolTip.SetTip(eye, "Switch this group off or on without clearing its sliders");
            eye.Click += (_, _) =>
            {
                if (!hidden.Remove(group)) hidden.Add(group);
                eye.Content = Icons.Create(hidden.Contains(group) ? Icons.EyeOff : Icons.Eye, 13, Palette.Secondary);
                timer.Stop();
                timer.Start();
            };
            eyes[group] = eye;
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Width = 288 };
            header.Children.Add(Ui.Label(title, weight: FontWeight.SemiBold));
            Grid.SetColumn(eye, 1);
            header.Children.Add(eye);
            body.Margin = new Thickness(8, 6, 0, 4);
            var expander = new Expander { Header = header, Content = body, IsExpanded = open, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0) };
            groups.Children.Add(expander);
            return expander;
        }
        Controls.SliderField Slider(string label, double value, double min, double max, Func<CameraRawSettings, double, CameraRawSettings> set, double step = 1, string format = "0", string? tip = null)
        {
            var field = Ui.SliderField(label, value, min, max, v => Update(set(current, v)), step, format, FieldWidth);
            // The field's own tip explains its gestures; what the slider adjusts goes above that.
            if (tip != null) ToolTip.SetTip(field, Ui.Column(6, Ui.Label(tip), (Control)ToolTip.GetTip(field)!));
            return field;
        }
        Control Column(params Control[] rows) => Ui.Column(6, rows);
        Control Heading(string text) { var label = Ui.Label(text, Palette.Secondary); label.Margin = new Thickness(0, 4, 0, 0); return label; }

        // Light.
        Group(CameraRawGroup.Light, "Light", Column(
            Slider("Exposure", initial.Exposure, -5, 5, (s, v) => s with { Exposure = v }, 0.05, "0.00", "Brightens or darkens the whole picture, in stops of light"),
            Slider("Contrast", initial.Contrast, -100, 100, (s, v) => s with { Contrast = v }, tip: "Makes light and dark tones more or less different, mostly around the middle"),
            Slider("Highlights", initial.Highlights, -100, 100, (s, v) => s with { Highlights = v }),
            Slider("Shadows", initial.Shadows, -100, 100, (s, v) => s with { Shadows = v }),
            Slider("Whites", initial.Whites, -100, 100, (s, v) => s with { Whites = v }, tip: "Sets the brightest point"),
            Slider("Blacks", initial.Blacks, -100, 100, (s, v) => s with { Blacks = v }, tip: "Sets the darkest point")), open: true);

        // Color, with Auto white balance from the layer's average.
        var temperature = Slider("Temperature", initial.Temperature, -100, 100, (s, v) => s with { Temperature = v, WhiteBalance = CameraRawWhiteBalance.Custom }, tip: "Shifts the picture from blue to yellow");
        var tint = Slider("Tint", initial.Tint, -100, 100, (s, v) => s with { Tint = v, WhiteBalance = CameraRawWhiteBalance.Custom }, tip: "Shifts the picture from green to magenta");
        setTemperature = v => temperature.Value = v;
        setTint = v => tint.Value = v;
        var balanceLabel = Ui.Label("White Balance");
        balanceLabel.Width = LabelWidth;
        balance = Ui.Combo(new[] { "Custom", "Auto" }, initial.WhiteBalance == CameraRawWhiteBalance.Auto ? "Auto" : "Custom", c => c, choice =>
        {
            if (choice != "Auto") { Update(current with { WhiteBalance = CameraRawWhiteBalance.Custom }); return; }
            // Auto balances the average color of the original layer; the sliders show what it chose.
            var solved = CameraRawPixels.AutoBalance(original);
            Update(current with { WhiteBalance = CameraRawWhiteBalance.Auto, Temperature = Math.Clamp(solved?.Temperature ?? 0, -100, 100), Tint = Math.Clamp(solved?.Tint ?? 0, -100, 100) });
            temperature.Value = current.Temperature;
            tint.Value = current.Tint;
        }, 120);
        ToolTip.SetTip(balance, "Auto balances the average color; Custom follows Temperature and Tint. Click the thumbnail to set them from one pixel.");
        Group(CameraRawGroup.Color, "Color", Column(
            Ui.Row(8, balanceLabel, balance), temperature, tint,
            Slider("Vibrance", initial.Vibrance, -100, 100, (s, v) => s with { Vibrance = v }, tip: "Strengthens quiet colors more than strong ones, and protects skin tones"),
            Slider("Saturation", initial.Saturation, -100, 100, (s, v) => s with { Saturation = v })), open: true);

        // Effects.
        var glowStyles = Enum.GetValues<CameraRawGlowStyle>();
        var vignetteStyles = Enum.GetValues<CameraRawVignetteStyle>();
        static string StyleName(Enum style) => style switch
        {
            CameraRawVignetteStyle.HighlightPriority => "Highlight Priority", CameraRawVignetteStyle.ColorPriority => "Color Priority",
            CameraRawVignetteStyle.PaintOverlay => "Paint Overlay", _ => style.ToString()
        };
        Control StyleRow(string label, Control combo) { var text = Ui.Label(label); text.Width = LabelWidth; return Ui.Row(8, text, combo); }
        Group(CameraRawGroup.Effects, "Effects", Column(
            Slider("Texture", initial.Texture, -100, 100, (s, v) => s with { Texture = v }, tip: "Adds or softens small detail"),
            Slider("Clarity", initial.Clarity, -100, 100, (s, v) => s with { Clarity = v }, tip: "Adds or softens contrast along broader shapes"),
            Slider("Dehaze", initial.Dehaze, -100, 100, (s, v) => s with { Dehaze = v }, tip: "Clears haze when raised, adds it when lowered"),
            Heading("Glow"),
            Slider("Glow", initial.Glow, 0, 100, (s, v) => s with { Glow = v }, tip: "Spreads a glow from the bright areas"),
            StyleRow("Style", Ui.Combo(glowStyles, initial.GlowStyle, v => StyleName(v), v => Update(current with { GlowStyle = v }), 140)),
            Slider("Range", initial.GlowRange, -100, 100, (s, v) => s with { GlowRange = v }, tip: "How bright an area must be to glow; idle until Glow is raised"),
            Slider("Spread", initial.GlowSpread, -100, 100, (s, v) => s with { GlowSpread = v }, tip: "How far the glow reaches; idle until Glow is raised"),
            Slider("Warmth", initial.GlowWarmth, -100, 100, (s, v) => s with { GlowWarmth = v }, tip: "Cool to warm; Halation stays red"),
            Heading("Vignette"),
            Slider("Amount", initial.VignetteAmount, -100, 100, (s, v) => s with { VignetteAmount = v }, tip: "Darkens or lightens the edges; the center does not change"),
            StyleRow("Style", Ui.Combo(vignetteStyles, initial.VignetteStyle, v => StyleName(v), v => Update(current with { VignetteStyle = v }), 140)),
            Slider("Midpoint", initial.VignetteMidpoint, 0, 100, (s, v) => s with { VignetteMidpoint = v }),
            Slider("Roundness", initial.VignetteRoundness, -100, 100, (s, v) => s with { VignetteRoundness = v }),
            Slider("Feather", initial.VignetteFeather, 0, 100, (s, v) => s with { VignetteFeather = v }),
            Slider("Highlights", initial.VignetteHighlights, 0, 100, (s, v) => s with { VignetteHighlights = v }, tip: "Protects bright edges while the vignette darkens (Highlight Priority)"),
            Heading("Grain"),
            Slider("Amount", initial.GrainAmount, 0, 100, (s, v) => s with { GrainAmount = v }),
            Slider("Size", initial.GrainSize, 0, 100, (s, v) => s with { GrainSize = v }),
            Slider("Roughness", initial.GrainRoughness, 0, 100, (s, v) => s with { GrainRoughness = v })));

        // Curve: the parametric sliders and a point curve per channel.
        var curveEditor = new CurveEditor { Width = 240, Height = 240 };
        var channels = new[] { "RGB", "Red", "Green", "Blue" };
        CurvesAdjustment ToEditor(CameraRawCurve c)
        {
            var curves = new CurvesAdjustment();
            CurvePoint[][] points = [c.Rgb, c.Red, c.Green, c.Blue];
            for (var i = 0; i < 4; i++) curves = curves.WithChannel(i, points[i].Select(p => new CurvePoint(p.X * 255, p.Y * 255)));
            return curves;
        }
        curveEditor.Curves = ToEditor(initial.Curve);
        curveEditor.Changed += curves =>
        {
            CurvePoint[] Points(int channel) => curves.Channels[channel].Select(p => new CurvePoint(p.X / 255, p.Y / 255)).ToArray();
            Update(current with { Curve = current.Curve with { Rgb = Points(0), Red = Points(1), Green = Points(2), Blue = Points(3) } });
        };
        var channelPicker = Ui.Combo(channels, "RGB", c => c, c => curveEditor.Channel = Array.IndexOf(channels, c), 100);
        var resetCurve = Ui.TextButton("Reset", () => { Update(current with { Curve = new CameraRawCurve() }); curveEditor.Curves = ToEditor(current.Curve); });
        resetCurve.MinWidth = 0;
        Group(CameraRawGroup.Curve, "Curve", Column(
            Heading("Parametric"),
            Slider("Highlights", initial.Curve.Highlights, -100, 100, (s, v) => s with { Curve = s.Curve with { Highlights = v } }),
            Slider("Lights", initial.Curve.Lights, -100, 100, (s, v) => s with { Curve = s.Curve with { Lights = v } }),
            Slider("Darks", initial.Curve.Darks, -100, 100, (s, v) => s with { Curve = s.Curve with { Darks = v } }),
            Slider("Shadows", initial.Curve.Shadows, -100, 100, (s, v) => s with { Curve = s.Curve with { Shadows = v } }),
            Slider("Refine Saturation", initial.Curve.RefineSaturation, -100, 100, (s, v) => s with { Curve = s.Curve with { RefineSaturation = v } }, tip: "How much the curve also changes saturation"),
            Heading("Point"),
            Ui.Row(8, Ui.Label("Channel", Palette.Secondary), channelPicker, resetCurve),
            curveEditor));

        // Color Mixer: one tab of eight families at a time.
        var mixerTabs = new[] { "Hue", "Saturation", "Luminance" };
        var mixerTab = 0;
        var mixerRows = new StackPanel { Spacing = 6 };
        void BuildMixer()
        {
            mixerRows.Children.Clear();
            for (var family = 0; family < 8; family++)
            {
                var (t, f) = (mixerTab, family);
                mixerRows.Children.Add(Slider(CameraRawMixer.Names[family], current.Mixer.Get(t, f), -100, 100, (s, v) => s with { Mixer = s.Mixer.With(t, f, v) }));
            }
        }
        BuildMixer();
        Group(CameraRawGroup.Mixer, "Color Mixer", Column(
            Ui.Row(8, Ui.Label("Adjust", Palette.Secondary), Ui.Combo(mixerTabs, "Hue", t => t, t => { mixerTab = Array.IndexOf(mixerTabs, t); BuildMixer(); }, 130)),
            mixerRows));

        // Color Grading: four wheels as sliders, then blending and balance.
        var wheelRows = new List<Control>();
        for (var i = 0; i < 4; i++)
        {
            var index = i;
            var wheel = initial.Grading.Wheels[i];
            wheelRows.Add(Heading(CameraRawGrading.Names[i]));
            wheelRows.Add(Slider("Hue", wheel.Hue, 0, 360, (s, v) => s with { Grading = s.Grading.WithWheel(index, s.Grading.Wheels[index] with { Hue = v }) }, tip: "Around the color wheel"));
            wheelRows.Add(Slider("Saturation", wheel.Saturation, 0, 100, (s, v) => s with { Grading = s.Grading.WithWheel(index, s.Grading.Wheels[index] with { Saturation = v }) }, tip: "How strongly the tint takes; 0 leaves this wheel off"));
            wheelRows.Add(Slider("Luminance", wheel.Luminance, -100, 100, (s, v) => s with { Grading = s.Grading.WithWheel(index, s.Grading.Wheels[index] with { Luminance = v }) }));
        }
        wheelRows.Add(Heading("Overlap"));
        wheelRows.Add(Slider("Blending", initial.Grading.Blending, 0, 100, (s, v) => s with { Grading = s.Grading with { Blending = v } }, tip: "How much the three tonal wheels overlap"));
        wheelRows.Add(Slider("Balance", initial.Grading.Balance, -100, 100, (s, v) => s with { Grading = s.Grading with { Balance = v } }, tip: "Negative favors the shadows, positive the highlights"));
        Group(CameraRawGroup.Grading, "Color Grading", Column(wheelRows.ToArray()));

        // Detail.
        var d = initial.Detail;
        Group(CameraRawGroup.Detail, "Detail", Column(
            Heading("Sharpening"),
            Slider("Amount", d.SharpenAmount, 0, 150, (s, v) => s with { Detail = s.Detail with { SharpenAmount = v } }),
            Slider("Radius", d.SharpenRadius, 0, 100, (s, v) => s with { Detail = s.Detail with { SharpenRadius = v } }),
            Slider("Detail", d.SharpenDetail, 0, 100, (s, v) => s with { Detail = s.Detail with { SharpenDetail = v } }),
            Slider("Masking", d.SharpenMasking, 0, 100, (s, v) => s with { Detail = s.Detail with { SharpenMasking = v } }, tip: "Keeps sharpening to the edges"),
            Heading("Noise Reduction"),
            Slider("Luminance", d.NoiseLuminance, 0, 100, (s, v) => s with { Detail = s.Detail with { NoiseLuminance = v } }),
            Slider("Detail", d.NoiseLuminanceDetail, 0, 100, (s, v) => s with { Detail = s.Detail with { NoiseLuminanceDetail = v } }),
            Slider("Contrast", d.NoiseLuminanceContrast, 0, 100, (s, v) => s with { Detail = s.Detail with { NoiseLuminanceContrast = v } }),
            Slider("Color", d.NoiseColor, 0, 100, (s, v) => s with { Detail = s.Detail with { NoiseColor = v } }),
            Slider("Detail", d.NoiseColorDetail, 0, 100, (s, v) => s with { Detail = s.Detail with { NoiseColorDetail = v } }),
            Slider("Smoothness", d.NoiseColorSmoothness, 0, 100, (s, v) => s with { Detail = s.Detail with { NoiseColorSmoothness = v } })));

        // Optics.
        var o = initial.Optics;
        Group(CameraRawGroup.Optics, "Optics", Column(
            Ui.Check("Remove Chromatic Aberration", o.RemoveChromaticAberration, v => Update(current with { Optics = current.Optics with { RemoveChromaticAberration = v } })),
            Ui.Check("Enable Lens Profile Corrections", o.EnableLensProfile, v => Update(current with { Optics = current.Optics with { EnableLensProfile = v } })),
            Slider("Distortion", o.ProfileDistortion, 0, 100, (s, v) => s with { Optics = s.Optics with { ProfileDistortion = v } }, tip: "Profile strength; a rendered layer carries no lens data, so this scales a generic correction"),
            Slider("Vignetting", o.ProfileVignetting, 0, 100, (s, v) => s with { Optics = s.Optics with { ProfileVignetting = v } }),
            Heading("Manual"),
            Slider("Distortion", o.Distortion, -100, 100, (s, v) => s with { Optics = s.Optics with { Distortion = v } }, tip: "Positive straightens lines that bow outward, negative lines that bow inward"),
            Heading("Defringe"),
            Slider("Purple Amount", o.PurpleAmount, 0, 100, (s, v) => s with { Optics = s.Optics with { PurpleAmount = v } }),
            Slider("Purple Hue Low", o.PurpleHueLow, 0, 360, (s, v) => s with { Optics = s.Optics with { PurpleHueLow = v } }),
            Slider("Purple Hue High", o.PurpleHueHigh, 0, 360, (s, v) => s with { Optics = s.Optics with { PurpleHueHigh = v } }),
            Slider("Green Amount", o.GreenAmount, 0, 100, (s, v) => s with { Optics = s.Optics with { GreenAmount = v } }),
            Slider("Green Hue Low", o.GreenHueLow, 0, 360, (s, v) => s with { Optics = s.Optics with { GreenHueLow = v } }),
            Slider("Green Hue High", o.GreenHueHigh, 0, 360, (s, v) => s with { Optics = s.Optics with { GreenHueHigh = v } }),
            Heading("Vignette"),
            Slider("Amount", o.VignetteAmount, -100, 100, (s, v) => s with { Optics = s.Optics with { VignetteAmount = v } }, tip: "Brightens the corners to counter lens falloff"),
            Slider("Midpoint", o.VignetteMidpoint, 0, 100, (s, v) => s with { Optics = s.Optics with { VignetteMidpoint = v } })));

        // Calibration.
        var c = initial.Calibration;
        var processes = Enumerable.Range(1, 6).ToArray();
        var processSummary = new TextBlock { Text = CameraRawCalibration.ProcessSummary(c.Process), Foreground = Palette.Secondary, TextWrapping = TextWrapping.Wrap, MaxWidth = 310, FontSize = 11 };
        Group(CameraRawGroup.Calibration, "Calibration", Column(
            StyleRow("Process", Ui.Combo(processes, c.Process, p => $"Version {p}", p => { Update(current with { Calibration = current.Calibration with { Process = p } }); processSummary.Text = CameraRawCalibration.ProcessSummary(p); }, 130)),
            processSummary,
            Slider("Shadow Tint", c.ShadowTint, -100, 100, (s, v) => s with { Calibration = s.Calibration with { ShadowTint = v } }, tip: "Green to magenta in the shadows"),
            Heading("Red Primary"),
            Slider("Hue", c.RedHue, -100, 100, (s, v) => s with { Calibration = s.Calibration with { RedHue = v } }),
            Slider("Saturation", c.RedSaturation, -100, 100, (s, v) => s with { Calibration = s.Calibration with { RedSaturation = v } }),
            Heading("Green Primary"),
            Slider("Hue", c.GreenHue, -100, 100, (s, v) => s with { Calibration = s.Calibration with { GreenHue = v } }),
            Slider("Saturation", c.GreenSaturation, -100, 100, (s, v) => s with { Calibration = s.Calibration with { GreenSaturation = v } }),
            Heading("Blue Primary"),
            Slider("Hue", c.BlueHue, -100, 100, (s, v) => s with { Calibration = s.Calibration with { BlueHue = v } }),
            Slider("Saturation", c.BlueSaturation, -100, 100, (s, v) => s with { Calibration = s.Calibration with { BlueSaturation = v } })));

        var scroll = new ScrollViewer { Content = groups, MaxHeight = 520, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 8, 0) };
        var head = Ui.Column(6, histogram, Ui.Row(0, preview), readout);
        histogram.HorizontalAlignment = preview.HorizontalAlignment = HorizontalAlignment.Left;
        var body = Ui.Column(10, head, Ui.Separator(false), scroll);
        body.Width = 360;
        var dialog = new DialogWindow("Camera Raw Filter", body);
        dialog.Opened += (_, _) => { timer.Stop(); timer.Start(); };
        var accepted = await dialog.Ask(owner);
        timer.Stop();
        return accepted ? Rendered() : null;
    }
}
