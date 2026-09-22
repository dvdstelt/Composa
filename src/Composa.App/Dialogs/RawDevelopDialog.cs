using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Composa.IO;

namespace Composa.App.Dialogs;

/// <summary>
/// A RAW file holds more range than a layer can, so the choice of what to keep is made here rather than assumed. The
/// preview develops at screen size while the sliders move; the import then develops the full frame once.
/// </summary>
public static class RawDevelopDialog
{
    private const int PreviewWidth = 560, PreviewHeight = 340;

    /// <summary>Shows the frame with live controls; returns the chosen settings, or null when the import was cancelled.</summary>
    public static async Task<RawDevelopSettings?> Show(Window owner, string fileName, RawImage raw)
    {
        var settings = new RawDevelopSettings();
        var image = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var frame = new Border
        {
            Width = PreviewWidth, Height = PreviewHeight, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.Parse("#1C1C1C")),
            Child = image, ClipToBounds = true
        };
        var info = Ui.Label($"{raw.Width} × {raw.Height} pixels, developed at 16 bits per channel", Palette.Secondary);

        // Each slider move develops a preview on a worker; only the newest request is shown, older ones are dropped.
        var revision = 0;
        var rendering = false;
        var pending = false;
        var preview = Math.Max(PreviewWidth, PreviewHeight);
        async void Render()
        {
            if (rendering) { pending = true; return; }
            rendering = true;
            do
            {
                pending = false;
                var wanted = ++revision;
                var chosen = settings;
                var bitmap = await Task.Run(() => raw.Develop(chosen, preview));
                if (wanted == revision) image.Source = Ui.ToAvaloniaBitmap(bitmap, preview);
                bitmap.Dispose();
            } while (pending);
            rendering = false;
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) => { timer.Stop(); Render(); };
        void Update(RawDevelopSettings next) { settings = next; timer.Stop(); timer.Start(); }

        var exposure = Ui.SliderRow("Exposure (EV)", settings.Exposure, -3, 3, v => Update(settings with { Exposure = v }), 0.05, "+0.00;-0.00;0.00", 300, 90);
        var temperature = Ui.SliderRow("Temperature", settings.Temperature, -100, 100, v => Update(settings with { Temperature = v }), 1, "0", 300, 90);
        var tint = Ui.SliderRow("Tint", settings.Tint, -100, 100, v => Update(settings with { Tint = v }), 1, "0", 300, 90);
        var reset = Ui.TextButton("Reset", () =>
        {
            Update(new RawDevelopSettings());
            exposure.Set(0); temperature.Set(0); tint.Set(0);
        });
        var note = Ui.Label("Cooler to warmer, and green to magenta, away from the camera's own white balance.", Palette.Secondary);
        note.TextWrapping = TextWrapping.Wrap;
        note.MaxWidth = PreviewWidth;

        var body = Ui.Column(12, frame, info, exposure.Row, temperature.Row, tint.Row, Ui.Row(12, reset, note));
        var dialog = new DialogWindow($"Develop {fileName}", body, "Import");
        dialog.Opened += (_, _) => Render();
        var accepted = await dialog.Ask(owner);
        timer.Stop();
        revision++;
        return accepted ? settings : null;
    }
}
