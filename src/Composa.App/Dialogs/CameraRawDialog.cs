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
        // Reset puts a slider back to what a fresh grade has, read from the defaults with the same getter that reads the value.
        var defaults = new CameraRawSettings();
        Controls.SliderField Slider(string label, Func<CameraRawSettings, double> get, double min, double max, Func<CameraRawSettings, double, CameraRawSettings> set, double step = 1, string format = "0", string? tip = null, IReadOnlyList<Color>? track = null)
        {
            var field = Ui.SliderField(label, get(current), min, max, v => Update(set(current, v)), step, format, FieldWidth, track, get(defaults));
            // The field's own tip explains its gestures; what the slider adjusts goes above that.
            if (tip != null) ToolTip.SetTip(field, Ui.Column(6, Ui.Label(tip), (Control)ToolTip.GetTip(field)!));
            return field;
        }
        Control Column(params Control[] rows) => Ui.Column(6, rows);
        Control Heading(string text) { var label = Ui.Label(text, Palette.Secondary); label.Margin = new Thickness(0, 4, 0, 0); return label; }

        // Light.
        Group(CameraRawGroup.Light, "Light", Column(
            Slider("Exposure", s => s.Exposure, -5, 5, (s, v) => s with { Exposure = v }, 0.05, "0.00", "Brightens or darkens the whole picture, in stops of light"),
            Slider("Contrast", s => s.Contrast, -100, 100, (s, v) => s with { Contrast = v }, tip: "Makes light and dark tones more or less different, mostly around the middle"),
            Slider("Highlights", s => s.Highlights, -100, 100, (s, v) => s with { Highlights = v }),
            Slider("Shadows", s => s.Shadows, -100, 100, (s, v) => s with { Shadows = v }),
            Slider("Whites", s => s.Whites, -100, 100, (s, v) => s with { Whites = v }, tip: "Sets the brightest point"),
            Slider("Blacks", s => s.Blacks, -100, 100, (s, v) => s with { Blacks = v }, tip: "Sets the darkest point")), open: true);

        // Color, with Auto white balance from the layer's average.
        var temperature = Slider("Temperature", s => s.Temperature, -100, 100, (s, v) => s with { Temperature = v, WhiteBalance = CameraRawWhiteBalance.Custom }, tip: "Shifts the picture from blue to yellow", track: Controls.SliderTracks.Temperature);
        var tint = Slider("Tint", s => s.Tint, -100, 100, (s, v) => s with { Tint = v, WhiteBalance = CameraRawWhiteBalance.Custom }, tip: "Shifts the picture from green to magenta", track: Controls.SliderTracks.Tint);
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
            Slider("Vibrance", s => s.Vibrance, -100, 100, (s, v) => s with { Vibrance = v }, tip: "Strengthens quiet colors more than strong ones, and protects skin tones", track: Controls.SliderTracks.Chroma),
            Slider("Saturation", s => s.Saturation, -100, 100, (s, v) => s with { Saturation = v }, track: Controls.SliderTracks.Chroma)), open: true);

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
            Slider("Texture", s => s.Texture, -100, 100, (s, v) => s with { Texture = v }, tip: "Adds or softens small detail"),
            Slider("Clarity", s => s.Clarity, -100, 100, (s, v) => s with { Clarity = v }, tip: "Adds or softens contrast along broader shapes"),
            Slider("Dehaze", s => s.Dehaze, -100, 100, (s, v) => s with { Dehaze = v }, tip: "Clears haze when raised, adds it when lowered"),
            Heading("Glow"),
            Slider("Glow", s => s.Glow, 0, 100, (s, v) => s with { Glow = v }, tip: "Spreads a glow from the bright areas"),
            StyleRow("Style", Ui.Combo(glowStyles, initial.GlowStyle, v => StyleName(v), v => Update(current with { GlowStyle = v }), 140)),
            Slider("Range", s => s.GlowRange, -100, 100, (s, v) => s with { GlowRange = v }, tip: "How bright an area must be to glow; idle until Glow is raised"),
            Slider("Spread", s => s.GlowSpread, -100, 100, (s, v) => s with { GlowSpread = v }, tip: "How far the glow reaches; idle until Glow is raised"),
            Slider("Warmth", s => s.GlowWarmth, -100, 100, (s, v) => s with { GlowWarmth = v }, tip: "Cool to warm; Halation stays red", track: Controls.SliderTracks.Temperature),
            Heading("Vignette"),
            Slider("Amount", s => s.VignetteAmount, -100, 100, (s, v) => s with { VignetteAmount = v }, tip: "Darkens or lightens the edges; the center does not change"),
            StyleRow("Style", Ui.Combo(vignetteStyles, initial.VignetteStyle, v => StyleName(v), v => Update(current with { VignetteStyle = v }), 140)),
            Slider("Midpoint", s => s.VignetteMidpoint, 0, 100, (s, v) => s with { VignetteMidpoint = v }),
            Slider("Roundness", s => s.VignetteRoundness, -100, 100, (s, v) => s with { VignetteRoundness = v }),
            Slider("Feather", s => s.VignetteFeather, 0, 100, (s, v) => s with { VignetteFeather = v }),
            Slider("Highlights", s => s.VignetteHighlights, 0, 100, (s, v) => s with { VignetteHighlights = v }, tip: "Protects bright edges while the vignette darkens (Highlight Priority)"),
            Heading("Grain"),
            Slider("Amount", s => s.GrainAmount, 0, 100, (s, v) => s with { GrainAmount = v }),
            Slider("Size", s => s.GrainSize, 0, 100, (s, v) => s with { GrainSize = v }),
            Slider("Roughness", s => s.GrainRoughness, 0, 100, (s, v) => s with { GrainRoughness = v })));

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
            Slider("Highlights", s => s.Curve.Highlights, -100, 100, (s, v) => s with { Curve = s.Curve with { Highlights = v } }),
            Slider("Lights", s => s.Curve.Lights, -100, 100, (s, v) => s with { Curve = s.Curve with { Lights = v } }),
            Slider("Darks", s => s.Curve.Darks, -100, 100, (s, v) => s with { Curve = s.Curve with { Darks = v } }),
            Slider("Shadows", s => s.Curve.Shadows, -100, 100, (s, v) => s with { Curve = s.Curve with { Shadows = v } }),
            Slider("Refine Saturation", s => s.Curve.RefineSaturation, -100, 100, (s, v) => s with { Curve = s.Curve with { RefineSaturation = v } }, tip: "How much the curve also changes saturation"),
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
                // The track shows the family's neighbours, its gray to color, or its dark to light, whichever the tab adjusts.
                var centre = CameraRawMixer.Centers[family];
                var track = t switch { 0 => Controls.SliderTracks.Hue(centre), 1 => Controls.SliderTracks.Saturation(centre), _ => Controls.SliderTracks.Luminance(centre) };
                mixerRows.Children.Add(Slider(CameraRawMixer.Names[family], s => s.Mixer.Get(t, f), -100, 100, (s, v) => s with { Mixer = s.Mixer.With(t, f, v) }, track: track));
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
            wheelRows.Add(Heading(CameraRawGrading.Names[i]));
            // Saturation runs from gray to the wheel's hue and follows the Hue slider as it turns.
            Controls.SliderField saturation = null!;
            var hue = Slider("Hue", s => s.Grading.Wheels[index].Hue, 0, 360, (s, v) => s with { Grading = s.Grading.WithWheel(index, s.Grading.Wheels[index] with { Hue = v }) }, tip: "Around the color wheel", track: Controls.SliderTracks.Spectrum(180));
            hue.Changed += v => saturation.Track = Controls.SliderTracks.Saturation(v);
            saturation = Slider("Saturation", s => s.Grading.Wheels[index].Saturation, 0, 100, (s, v) => s with { Grading = s.Grading.WithWheel(index, s.Grading.Wheels[index] with { Saturation = v }) }, tip: "How strongly the tint takes; 0 leaves this wheel off", track: Controls.SliderTracks.Saturation(initial.Grading.Wheels[i].Hue));
            wheelRows.Add(hue);
            wheelRows.Add(saturation);
            wheelRows.Add(Slider("Luminance", s => s.Grading.Wheels[index].Luminance, -100, 100, (s, v) => s with { Grading = s.Grading.WithWheel(index, s.Grading.Wheels[index] with { Luminance = v }) }, track: Controls.SliderTracks.Lightness));
        }
        wheelRows.Add(Heading("Overlap"));
        wheelRows.Add(Slider("Blending", s => s.Grading.Blending, 0, 100, (s, v) => s with { Grading = s.Grading with { Blending = v } }, tip: "How much the three tonal wheels overlap"));
        wheelRows.Add(Slider("Balance", s => s.Grading.Balance, -100, 100, (s, v) => s with { Grading = s.Grading with { Balance = v } }, tip: "Negative favors the shadows, positive the highlights"));
        Group(CameraRawGroup.Grading, "Color Grading", Column(wheelRows.ToArray()));

        // Detail.
        Group(CameraRawGroup.Detail, "Detail", Column(
            Heading("Sharpening"),
            Slider("Amount", s => s.Detail.SharpenAmount, 0, 150, (s, v) => s with { Detail = s.Detail with { SharpenAmount = v } }),
            Slider("Radius", s => s.Detail.SharpenRadius, 0, 100, (s, v) => s with { Detail = s.Detail with { SharpenRadius = v } }),
            Slider("Detail", s => s.Detail.SharpenDetail, 0, 100, (s, v) => s with { Detail = s.Detail with { SharpenDetail = v } }),
            Slider("Masking", s => s.Detail.SharpenMasking, 0, 100, (s, v) => s with { Detail = s.Detail with { SharpenMasking = v } }, tip: "Keeps sharpening to the edges"),
            Heading("Noise Reduction"),
            Slider("Luminance", s => s.Detail.NoiseLuminance, 0, 100, (s, v) => s with { Detail = s.Detail with { NoiseLuminance = v } }),
            Slider("Detail", s => s.Detail.NoiseLuminanceDetail, 0, 100, (s, v) => s with { Detail = s.Detail with { NoiseLuminanceDetail = v } }),
            Slider("Contrast", s => s.Detail.NoiseLuminanceContrast, 0, 100, (s, v) => s with { Detail = s.Detail with { NoiseLuminanceContrast = v } }),
            Slider("Color", s => s.Detail.NoiseColor, 0, 100, (s, v) => s with { Detail = s.Detail with { NoiseColor = v } }),
            Slider("Detail", s => s.Detail.NoiseColorDetail, 0, 100, (s, v) => s with { Detail = s.Detail with { NoiseColorDetail = v } }),
            Slider("Smoothness", s => s.Detail.NoiseColorSmoothness, 0, 100, (s, v) => s with { Detail = s.Detail with { NoiseColorSmoothness = v } })));

        // Optics.
        var o = initial.Optics;
        Group(CameraRawGroup.Optics, "Optics", Column(
            Ui.Check("Remove Chromatic Aberration", o.RemoveChromaticAberration, v => Update(current with { Optics = current.Optics with { RemoveChromaticAberration = v } })),
            Ui.Check("Enable Lens Profile Corrections", o.EnableLensProfile, v => Update(current with { Optics = current.Optics with { EnableLensProfile = v } })),
            Slider("Distortion", s => s.Optics.ProfileDistortion, 0, 100, (s, v) => s with { Optics = s.Optics with { ProfileDistortion = v } }, tip: "Profile strength; a rendered layer carries no lens data, so this scales a generic correction"),
            Slider("Vignetting", s => s.Optics.ProfileVignetting, 0, 100, (s, v) => s with { Optics = s.Optics with { ProfileVignetting = v } }),
            Heading("Manual"),
            Slider("Distortion", s => s.Optics.Distortion, -100, 100, (s, v) => s with { Optics = s.Optics with { Distortion = v } }, tip: "Positive straightens lines that bow outward, negative lines that bow inward"),
            Heading("Defringe"),
            Slider("Purple Amount", s => s.Optics.PurpleAmount, 0, 100, (s, v) => s with { Optics = s.Optics with { PurpleAmount = v } }),
            Slider("Purple Hue Low", s => s.Optics.PurpleHueLow, 0, 360, (s, v) => s with { Optics = s.Optics with { PurpleHueLow = v } }),
            Slider("Purple Hue High", s => s.Optics.PurpleHueHigh, 0, 360, (s, v) => s with { Optics = s.Optics with { PurpleHueHigh = v } }),
            Slider("Green Amount", s => s.Optics.GreenAmount, 0, 100, (s, v) => s with { Optics = s.Optics with { GreenAmount = v } }),
            Slider("Green Hue Low", s => s.Optics.GreenHueLow, 0, 360, (s, v) => s with { Optics = s.Optics with { GreenHueLow = v } }),
            Slider("Green Hue High", s => s.Optics.GreenHueHigh, 0, 360, (s, v) => s with { Optics = s.Optics with { GreenHueHigh = v } }),
            Heading("Vignette"),
            Slider("Amount", s => s.Optics.VignetteAmount, -100, 100, (s, v) => s with { Optics = s.Optics with { VignetteAmount = v } }, tip: "Brightens the corners to counter lens falloff"),
            Slider("Midpoint", s => s.Optics.VignetteMidpoint, 0, 100, (s, v) => s with { Optics = s.Optics with { VignetteMidpoint = v } })));

        // Calibration.
        var c = initial.Calibration;
        var processes = Enumerable.Range(1, 6).ToArray();
        var processSummary = new TextBlock { Text = CameraRawCalibration.ProcessSummary(c.Process), Foreground = Palette.Secondary, TextWrapping = TextWrapping.Wrap, MaxWidth = 310, FontSize = 11 };
        Group(CameraRawGroup.Calibration, "Calibration", Column(
            StyleRow("Process", Ui.Combo(processes, c.Process, p => $"Version {p}", p => { Update(current with { Calibration = current.Calibration with { Process = p } }); processSummary.Text = CameraRawCalibration.ProcessSummary(p); }, 130)),
            processSummary,
            Slider("Shadow Tint", s => s.Calibration.ShadowTint, -100, 100, (s, v) => s with { Calibration = s.Calibration with { ShadowTint = v } }, track: Controls.SliderTracks.Tint, tip: "Green to magenta in the shadows"),
            Heading("Red Primary"),
            Slider("Hue", s => s.Calibration.RedHue, -100, 100, (s, v) => s with { Calibration = s.Calibration with { RedHue = v } }, track: Controls.SliderTracks.Hue(0)),
            Slider("Saturation", s => s.Calibration.RedSaturation, -100, 100, (s, v) => s with { Calibration = s.Calibration with { RedSaturation = v } }, track: Controls.SliderTracks.Saturation(0)),
            Heading("Green Primary"),
            Slider("Hue", s => s.Calibration.GreenHue, -100, 100, (s, v) => s with { Calibration = s.Calibration with { GreenHue = v } }, track: Controls.SliderTracks.Hue(120)),
            Slider("Saturation", s => s.Calibration.GreenSaturation, -100, 100, (s, v) => s with { Calibration = s.Calibration with { GreenSaturation = v } }, track: Controls.SliderTracks.Saturation(120)),
            Heading("Blue Primary"),
            Slider("Hue", s => s.Calibration.BlueHue, -100, 100, (s, v) => s with { Calibration = s.Calibration with { BlueHue = v } }, track: Controls.SliderTracks.Hue(240)),
            Slider("Saturation", s => s.Calibration.BlueSaturation, -100, 100, (s, v) => s with { Calibration = s.Calibration with { BlueSaturation = v } }, track: Controls.SliderTracks.Saturation(240))));

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
