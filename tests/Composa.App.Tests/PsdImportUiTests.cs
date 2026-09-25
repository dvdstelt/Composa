using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.Core.Tests;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App.Tests;

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
        var path = Path.Combine(Path.GetTempPath(), "composa-psd-" + Guid.NewGuid().ToString("N") + ".psd");
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
            Screenshots.Save(dialog, "40-psd-conversions");

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
            Screenshots.Save(window, "41-psd-opened");

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
        finally { TempFiles.Delete(path); }
    }

    /// <summary>Opened, an SVG becomes a document at the size it declares; placed, it is drawn to fit the canvas.</summary>
    [AvaloniaFact]
    public async Task An_svg_opens_at_its_declared_size_and_is_placed_fitted_to_the_canvas()
    {
        if (!Composa.IO.ImageMagick.IsAvailable) return; // SVG goes through ImageMagick, like HEIC and TIFF.
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        var path = Path.Combine(Path.GetTempPath(), "composa-" + Guid.NewGuid().ToString("N") + ".svg");
        File.WriteAllText(path, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"50\"><circle cx=\"50\" cy=\"25\" r=\"20\" fill=\"#ff0000\"/></svg>");
        try
        {
            var opening = window.OpenPaths([path]);
            await Pump(() => opening.IsCompleted);
            var session = window.Session!;
            Assert.Equal((100, 50), (session.Document.Width, session.Document.Height));
            Assert.Equal(Path.GetFileNameWithoutExtension(path), Assert.Single(session.Document.Layers).Name);

            var placing = window.PlacePaths([path], null);
            await Pump(() => placing.IsCompleted);
            var placed = session.ActiveLayer!;
            Assert.Equal(2, session.Document.Layers.Count);
            Assert.Equal((100d, 50d), (placed.Transform.Width, placed.Transform.Height));
            Assert.Equal((100, 50), (placed.Pixels!.Width, placed.Pixels.Height));
            Assert.Equal("Add Image", session.History.UndoName);

            // On a larger canvas the placed copy is drawn at the fitted size, not enlarged from 100 x 50 pixels.
            var large = Composa.Editing.EditorSession.NewCanvas(800, 600, SKColors.White);
            window.AddSession(large);
            placing = window.PlacePaths([path], new SKPoint(400, 300));
            await Pump(() => placing.IsCompleted);
            var fitted = large.ActiveLayer!;
            Assert.Equal((800, 400), (fitted.Pixels!.Width, fitted.Pixels.Height));
            Assert.Equal((0d, 100d, 800d, 400d), (fitted.Transform.X, fitted.Transform.Y, fitted.Transform.Width, fitted.Transform.Height));
        }
        finally { TempFiles.Delete(path); }
    }
}
