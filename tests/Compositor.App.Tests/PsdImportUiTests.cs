using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Compositor.Core.Tests;
using Compositor.Rendering;
using SkiaSharp;

namespace Compositor.App.Tests;

public class PsdImportUiTests
{
    private static SKBitmap Solid(int width, int height, SKColor color)
    {
        var bitmap = Pixels.NewColor(width, height);
        bitmap.Erase(color);
        return bitmap;
    }

    private static async Task Pump(Func<bool> until)
    {
        for (var i = 0; i < 400 && !until(); i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Opening_a_photoshop_file_reports_its_conversions_and_cancel_applies_nothing()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        var writer = new PsdWriter { Width = 300, Height = 200 };
        writer.Layers.Add(new PsdWriterLayer { Name = "Background", Image = Solid(300, 200, new SKColor(0xF2, 0xE8, 0xD5)) });
        writer.Layers.Add(new PsdWriterLayer { Name = "Headline", Image = Solid(180, 40, new SKColor(0x20, 0x30, 0x50)), Left = 30, Top = 30 }.With("TySh", new byte[16]));
        writer.Layers.Add(new PsdWriterLayer { Name = "Badge", Image = Solid(60, 60, new SKColor(0xD0, 0x40, 0x30)), Left = 200, Top = 100, Blend = "diss" }.With("lfx2", new byte[16]));
        var path = Path.Combine(Path.GetTempPath(), "compositor-psd-" + Guid.NewGuid().ToString("N") + ".psd");
        File.WriteAllBytes(path, writer.Build());
        try
        {
            var opening = window.OpenPaths([path]);
            await Pump(() => window.OwnedWindows.Count > 0);
            var dialog = Assert.Single(window.OwnedWindows);
            Assert.Equal("Open " + Path.GetFileName(path) + "?", dialog.Title);
            var text = string.Join("\n", dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Assert.Contains("Headline", text);
            Assert.Contains("retyped", text);
            Assert.Contains("diss", text);
            Assert.Contains("effects", text);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Directory.CreateDirectory(WindowTests.Shots);
            dialog.CaptureRenderedFrame()?.Save(Path.Combine(WindowTests.Shots, "40-psd-conversions.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);

            // Cancel: nothing opens.
            dialog.Close(false);
            await Pump(() => opening.IsCompleted);
            Assert.Null(window.Session);

            // Import: the file becomes a document with its layers, folders and report honoured.
            opening = window.OpenPaths([path]);
            await Pump(() => window.OwnedWindows.Count > 0);
            Assert.Single(window.OwnedWindows).Close(true);
            await Pump(() => opening.IsCompleted);
            var session = window.Session!;
            Assert.Equal(Path.GetFileNameWithoutExtension(path), session.Title);
            Assert.Equal(["Background", "Headline", "Badge"], session.Document.Layers.Select(l => l.Name));
            Assert.Equal((300, 200), (session.Document.Width, session.Document.Height));
            Capture(window, "41-psd-opened");

            // Placed into that document, a second copy arrives inside a folder named after the file.
            opening = window.PlacePaths([path], new SKPoint(150, 100));
            await Pump(() => window.OwnedWindows.Count > 0);
            Assert.Single(window.OwnedWindows).Close(true);
            await Pump(() => opening.IsCompleted);
            var folder = session.ActiveLayer!;
            Assert.True(folder.IsGroup);
            Assert.Equal(Path.GetFileNameWithoutExtension(path), folder.Name);
            Assert.Equal(3, folder.Children.Count);
            Assert.Equal("Import Photoshop File", session.History.UndoName);
        }
        finally { File.Delete(path); }
    }

    private static void Capture(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Directory.CreateDirectory(WindowTests.Shots);
        window.CaptureRenderedFrame()?.Save(Path.Combine(WindowTests.Shots, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}
