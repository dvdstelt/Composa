using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;

namespace Composa.App.Dialogs;

/// <summary>A modal window with a body and OK/Cancel. Enter accepts, Escape cancels.</summary>
public class DialogWindow : Window
{
    private readonly Button ok;
    protected readonly StackPanel Buttons;

    public DialogWindow(string title, Control body, string okText = "OK", bool cancellable = true)
    {
        Title = Loc.T(title);
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ok = Ui.TextButton(okText, Accept, accent: true);
        ok.IsDefault = true;
        Buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        if (cancellable)
        {
            var cancel = Ui.TextButton("Cancel", Reject);
            cancel.IsCancel = true;
            Buttons.Children.Add(cancel);
        }
        Buttons.Children.Add(ok);
        Content = new StackPanel { Margin = new Thickness(20), Children = { body, Buttons } };
    }

    public bool CanAccept { set => ok.IsEnabled = value; }

    public async Task<bool> Ask(Window owner) => await ShowDialog<bool?>(owner) == true;

    /// <summary>OK and Enter. A panel shown without <see cref="Ask"/> overrides these to act instead of closing with a result.</summary>
    protected virtual void Accept() => Close(true);
    /// <summary>Cancel and Escape.</summary>
    protected virtual void Reject() => Close(false);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (e.Key == Key.Escape) { Reject(); e.Handled = true; }
    }
}

public static class Prompts
{
    public static Task<bool> Confirm(Window owner, string title, string message, string okText = "OK") =>
        new DialogWindow(title, new TextBlock { Text = message, MaxWidth = 420, TextWrapping = TextWrapping.Wrap }, okText).Ask(owner);

    public static Task Alert(Window owner, string title, string message) =>
        new DialogWindow(title, new TextBlock { Text = message, MaxWidth = 460, TextWrapping = TextWrapping.Wrap }, cancellable: false).Ask(owner);

    /// <summary>Save / Don't Save / Cancel. Returns null for cancel.</summary>
    public static async Task<bool?> SaveChanges(Window owner, string name)
    {
        var dialog = new DialogWindow("Unsaved Changes", new TextBlock { Text = Loc.Format("Save changes to \"{0}\" before closing?", name), MaxWidth = 420, TextWrapping = TextWrapping.Wrap }, "Save");
        bool? result = null;
        var discard = Ui.TextButton("Don't Save", () => { result = false; dialog.Close(false); });
        ((StackPanel)((StackPanel)dialog.Content!).Children[1]).Children.Insert(0, discard);
        if (await dialog.Ask(owner)) return true;
        return result;
    }

    public static async Task<double?> Number(Window owner, string title, string label, double value, double min, double max, string unit = "px")
    {
        var result = value;
        var box = Ui.Number(value, min, max, v => result = v, width: 120);
        var dialog = new DialogWindow(title, Ui.Row(10, Ui.Scrub(Ui.Label(Loc.T(label)), box), box, Ui.Label(unit, Palette.Secondary)));
        dialog.Opened += (_, _) => box.Focus();
        return await dialog.Ask(owner) ? result : null;
    }

    /// <param name="preview">Called with the working color as it changes while the dialog is open, so what it colors can follow along. The caller puts the original back on Cancel.</param>
    public static async Task<SKColor?> Color(Window owner, string title, SKColor initial, Action<SKColor>? preview = null)
    {
        // Saturation across and brightness up in the square, hue on the strip, as Photoshop lays it out. The control's
        // default puts brightness on the strip, where a black starting color leaves it at zero and every click in the
        // square stays black.
        var view = new ColorView
        {
            Color = initial.ToAvalonia(), IsAlphaEnabled = false, IsAlphaVisible = false, IsColorPaletteVisible = true,
            IsColorModelVisible = true, IsHexInputVisible = true, Width = 360, ColorSpectrumComponents = ColorSpectrumComponents.SaturationValue
        };
        if (preview != null) view.ColorChanged += (_, e) => preview(e.NewColor.ToSkia().WithAlpha(255));
        var swatches = Ui.Row(0,
            new Border { Width = 60, Height = 28, Background = new SolidColorBrush(initial.ToAvalonia()) },
            new Border { Width = 60, Height = 28, [!Border.BackgroundProperty] = view.GetObservable(ColorView.ColorProperty).Select(c => (IBrush)new SolidColorBrush(c)).ToBinding() });
        var body = Ui.Column(10, view, Ui.Row(10, Ui.Label(Loc.T("Before / After"), Palette.Secondary), swatches));
        return await new DialogWindow(title, body).Ask(owner) ? view.Color.ToSkia().WithAlpha(255) : null;
    }

    private static IObservable<TResult> Select<T, TResult>(this IObservable<T> source, Func<T, TResult> map) => new MapObservable<T, TResult>(source, map);

    private sealed class MapObservable<T, TResult>(IObservable<T> source, Func<T, TResult> map) : IObservable<TResult>
    {
        public IDisposable Subscribe(IObserver<TResult> observer) => source.Subscribe(new Observer(observer, map));

        private sealed class Observer(IObserver<TResult> inner, Func<T, TResult> map) : IObserver<T>
        {
            public void OnCompleted() => inner.OnCompleted();
            public void OnError(Exception error) => inner.OnError(error);
            public void OnNext(T value) => inner.OnNext(map(value));
        }
    }
}

public sealed record NewCanvasResult(int Width, int Height, SKColor? Background);

public static class CanvasDialogs
{
    /// <summary>
    /// Preset sizes, as the macOS app groups them: resolutions, screens and social formats, plus two print sizes. The
    /// first entry is Custom; the combo shows the one the typed size matches.
    /// </summary>
    public static readonly (string Name, int W, int H)[] Presets =
    [
        ("Custom", 0, 0),
        ("4K", 3840, 2160), ("1440p", 2560, 1440), ("1080p", 1920, 1080),
        ("iPhone 18 Pro", 1206, 2622), ("iPhone 18 Pro Max", 1320, 2868), ("MacBook Pro 14\"", 3024, 1964), ("MacBook Pro 16\"", 3456, 2234), ("Studio Display", 5120, 2880),
        ("Instagram Square", 1080, 1080), ("Instagram Portrait", 1080, 1350), ("Instagram Story", 1080, 1920), ("YouTube Thumb", 1080, 608),
        ("A4 at 300 ppi", 2480, 3508), ("Photo 6000 × 4000", 6000, 4000)
    ];

    private static string PresetLabel((string Name, int W, int H) preset) => preset.W == 0 || preset.Name.Contains('×') ? preset.Name : $"{preset.Name}  {preset.W} × {preset.H}";

    /// <summary>
    /// With the size of the image on the clipboard, the dialog opens on a Clipboard preset of that size, listed first,
    /// so what is pasted next fills the new canvas exactly; every other preset is still a choice away. An image too
    /// large for a canvas offers no preset.
    /// </summary>
    public static async Task<NewCanvasResult?> NewCanvas(Window owner, SKColor backgroundColor, (int W, int H)? clipboardSize = null)
    {
        if (clipboardSize is { } clip && !DocumentLimits.FitsSurface(clip.W, clip.H)) clipboardSize = null;
        var presets = clipboardSize is { } size ? Presets.Prepend(("Clipboard", size.W, size.H)).ToArray() : Presets;
        var custom = Array.FindIndex(presets, p => p.W == 0);
        int width = clipboardSize?.W ?? 1920, height = clipboardSize?.H ?? 1080;
        var fill = 0;
        var widthBox = Ui.Number(width, 1, DocumentLimits.MaxSide, v => width = (int)v, width: 120);
        var heightBox = Ui.Number(height, 1, DocumentLimits.MaxSide, v => height = (int)v, width: 120);
        var applying = false;
        ComboBox preset = null!;
        preset = Ui.Combo(presets, presets.FirstOrDefault(p => p.W == width && p.H == height, presets[custom]), PresetLabel, p =>
        {
            if (p.W == 0 || applying) return;
            applying = true;
            widthBox.Value = p.W; heightBox.Value = p.H;
            applying = false;
        }, 220);
        // A typed size shows the preset it matches, or Custom.
        void FollowSize(object? _, NumericUpDownValueChangedEventArgs __)
        {
            if (applying) return;
            applying = true;
            var match = Array.FindIndex(presets, p => p.W == width && p.H == height);
            preset.SelectedIndex = match >= 0 ? match : custom;
            applying = false;
        }
        widthBox.ValueChanged += FollowSize;
        heightBox.ValueChanged += FollowSize;
        var background = Ui.Combo(new[] { "Transparent", "White", "Background color" }, "Transparent", Loc.T, s => fill = s == "White" ? 1 : s == "Transparent" ? 0 : 2, 220);
        var grid = Form(("Preset", preset), ("Width", Ui.Row(6, widthBox, Ui.Label(Loc.T("px"), Palette.Secondary))), ("Height", Ui.Row(6, heightBox, Ui.Label(Loc.T("px"), Palette.Secondary))), ("Background", background));
        if (!await new DialogWindow("New Canvas", grid, "Create").Ask(owner)) return null;
        return new NewCanvasResult(width, height, fill == 0 ? null : fill == 1 ? SKColors.White : backgroundColor);
    }

    public static async Task<(int Width, int Height, Anchor Anchor)?> CanvasSize(Window owner, int currentWidth, int currentHeight)
    {
        int width = currentWidth, height = currentHeight;
        var anchor = Anchor.Center;
        var relative = false;
        var widthBox = Ui.Number(width, -DocumentLimits.MaxSide, DocumentLimits.MaxSide, v => width = (int)v, width: 120);
        var heightBox = Ui.Number(height, -DocumentLimits.MaxSide, DocumentLimits.MaxSide, v => height = (int)v, width: 120);
        var relativeBox = Ui.Check("Relative", false, v =>
        {
            relative = v;
            widthBox.Value = v ? 0 : currentWidth;
            heightBox.Value = v ? 0 : currentHeight;
        });
        var anchors = new UniformGridPanel(3);
        var buttons = new List<ToggleButton>();
        foreach (var value in Enum.GetValues<Anchor>())
        {
            var button = new ToggleButton { Width = 26, Height = 26, IsChecked = value == anchor, Margin = new Thickness(1) };
            button.Click += (_, _) =>
            {
                anchor = value;
                foreach (var other in buttons) other.IsChecked = other == button;
            };
            buttons.Add(button);
            anchors.Children.Add(button);
        }
        var body = Ui.Column(12,
            Ui.Label($"Current size: {currentWidth} × {currentHeight} px", Palette.Secondary),
            Form(("Width", Ui.Row(6, widthBox, Ui.Label(Loc.T("px"), Palette.Secondary))), ("Height", Ui.Row(6, heightBox, Ui.Label(Loc.T("px"), Palette.Secondary))), ("", relativeBox), ("Anchor", anchors)));
        if (!await new DialogWindow("Canvas Size", body).Ask(owner)) return null;
        if (relative) { width += currentWidth; height += currentHeight; }
        return (Math.Clamp(width, 1, DocumentLimits.MaxSide), Math.Clamp(height, 1, DocumentLimits.MaxSide), anchor);
    }

    public static readonly ResampleMode[] ResampleModes = [ResampleMode.Automatic, ResampleMode.Nearest, ResampleMode.Enhance];

    public static string ResampleName(ResampleMode mode) => mode switch
    {
        ResampleMode.Nearest => "Nearest Neighbor",
        ResampleMode.Enhance => "Enhance",
        _ => "Automatic"
    };

    /// <param name="resample">The last Resample choice, which the dialog opens on; Enhance applies only when enlarging and the model is available.</param>
    public static async Task<(int Width, int Height, double Resolution, ResampleMode Resample)?> ImageSize(Window owner, int currentWidth, int currentHeight, double resolution, ResampleMode resample = ResampleMode.Automatic)
    {
        int width = currentWidth, height = currentHeight;
        var constrain = true;
        var syncing = false;
        NumericUpDown widthBox = null!, heightBox = null!;
        var note = new TextBlock { Foreground = Palette.Secondary, MaxWidth = 380, TextWrapping = TextWrapping.Wrap };
        var mode = resample;
        void Describe()
        {
            var enlarging = width > currentWidth || height > currentHeight;
            note.Text = mode switch
            {
                ResampleMode.Nearest => "Every pixel becomes a hard-edged block, which keeps pixel art crisp.",
                ResampleMode.Enhance when !Composa.Vision.UpscaleModels.IsAvailable => Composa.Vision.UpscaleModels.UnavailableReason + " The picture is resampled as Automatic does.",
                ResampleMode.Enhance when !enlarging => "Enhance only applies when enlarging; at this size the picture is resampled as Automatic does.",
                ResampleMode.Enhance => "A model run on this machine enlarges each photo layer four times, inventing fine detail that was not there, then fits it to the new size. It takes about a second per 65,000 pixels of each layer; text and shapes are redrawn instead, and masks are resampled.",
                _ => "Smooth when shrinking, sharp cubic when enlarging."
            };
        }
        widthBox = Ui.Number(width, 1, DocumentLimits.MaxSide, v =>
        {
            width = (int)v;
            if (constrain && !syncing) { syncing = true; heightBox.Value = Math.Max(1, (int)Math.Round(v * currentHeight / currentWidth)); syncing = false; }
            Describe();
        }, width: 120);
        heightBox = Ui.Number(height, 1, DocumentLimits.MaxSide, v =>
        {
            height = (int)v;
            if (constrain && !syncing) { syncing = true; widthBox.Value = Math.Max(1, (int)Math.Round(v * currentWidth / currentHeight)); syncing = false; }
            Describe();
        }, width: 120);
        var resolutionBox = Ui.Number(resolution, 1, 9600, v => resolution = v, width: 120);
        var modes = Ui.Combo(ResampleModes, mode, ResampleName, v => { mode = v; Describe(); }, 160);
        ToolTip.SetTip(modes, Loc.T("How the pixels are resampled: Automatic for most pictures, Nearest Neighbor for pixel art, Enhance to invent detail with a model while enlarging"));
        Describe();
        var body = Ui.Column(12,
            Ui.Label($"Current size: {currentWidth} × {currentHeight} px", Palette.Secondary),
            Form(("Width", Ui.Row(6, widthBox, Ui.Label(Loc.T("px"), Palette.Secondary))), ("Height", Ui.Row(6, heightBox, Ui.Label(Loc.T("px"), Palette.Secondary))),
                ("", Ui.Check("Constrain proportions", true, v => constrain = v)), ("Resolution", Ui.Row(6, resolutionBox, Ui.Label(Loc.T("pixels/inch"), Palette.Secondary))),
                ("Resample", modes)),
            note);
        if (!await new DialogWindow("Image Size", body).Ask(owner)) return null;
        return (width, height, resolution, mode);
    }

    /// <summary>Picks the JPEG quality while showing what the compression does to the picture and how large the file gets.</summary>
    public static async Task<int?> JpegQuality(Window owner, int initial, SKBitmap flattened)
    {
        var quality = initial;
        var preview = new Image { Width = 520, Height = 340, Stretch = Stretch.Uniform };
        var info = Ui.Label(Loc.T("Measuring…"), Palette.Secondary);
        var generation = 0;
        var closed = false;
        Task inFlight = Task.CompletedTask;
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };

        async void Refresh()
        {
            timer.Stop();
            var mine = ++generation;
            var q = quality;
            info.Text = Loc.T("Measuring…");
            // Encoding a large photo takes a moment, so it runs off the UI thread; a newer request supersedes this one.
            var work = Task.Run(() =>
            {
                var encoded = Composa.IO.ImageFiles.Encode(flattened, Composa.IO.ExportFormat.Jpeg, q);
                using var decoded = SKBitmap.Decode(encoded);
                return (encoded.LongLength, decoded == null ? null : Ui.ToAvaloniaBitmap(decoded, 1040));
            });
            inFlight = Task.WhenAll(inFlight, work);
            var (bytes, shown) = await work;
            if (closed || mine != generation) return;
            if (shown != null) preview.Source = shown;
            info.Text = $"{flattened.Width} × {flattened.Height} px · {(bytes >= 1024 * 1024 ? $"{bytes / 1048576.0:0.0} MB" : $"{bytes / 1024.0:0} KB")}";
        }

        timer.Tick += (_, _) => Refresh();
        var row = Ui.SliderField("Quality", initial, 1, 100, v => { quality = (int)v; timer.Stop(); timer.Start(); }, width: 380);
        var frame = new Border { Child = preview, Background = new SolidColorBrush(Color.Parse("#1A1A1A")), Padding = new Thickness(1) };
        var dialog = new DialogWindow("Export JPEG", Ui.Column(12, frame, row, info), "Export…");
        dialog.Opened += (_, _) => Refresh();
        var accepted = await dialog.Ask(owner);
        closed = true;
        timer.Stop();
        // The caller disposes the flattened bitmap next; no encode may still be reading it.
        try { await inFlight; } catch (Exception) { /* A failed preview encode does not block the export itself. */ }
        return accepted ? quality : null;
    }

    /// <summary>
    /// Labelled rows. The label of a number field, or of a row that starts with one, drags the field's
    /// value. Every label is a word the application chose, so the translation happens here once rather
    /// than at each call.
    /// </summary>
    public static Grid Form(params (string Label, Control Field)[] rows)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,12,*") };
        for (var i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = Ui.Label(Loc.T(rows[i].Label), Palette.Secondary);
            if ((rows[i].Field as NumericUpDown ?? (rows[i].Field as Panel)?.Children.FirstOrDefault() as NumericUpDown) is { } number) Ui.Scrub(label, number);
            label.Margin = new Thickness(0, 6);
            Grid.SetRow(label, i);
            grid.Children.Add(label);
            Grid.SetRow(rows[i].Field, i);
            Grid.SetColumn(rows[i].Field, 2);
            rows[i].Field.Margin = new Thickness(0, 4);
            grid.Children.Add(rows[i].Field);
        }
        return grid;
    }

    private sealed class UniformGridPanel : Avalonia.Controls.Primitives.UniformGrid
    {
        public UniformGridPanel(int columns)
        {
            Columns = columns;
            HorizontalAlignment = HorizontalAlignment.Left;
        }
    }
}
