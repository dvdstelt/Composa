using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Compositor.App.Dialogs;
using Compositor.Filters;
using SkiaSharp;

namespace Compositor.App.Tests;

public class DialogTests
{
    private static void Capture(Window owner, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var dialog = owner.OwnedWindows.Last();
        Directory.CreateDirectory(WindowTests.Shots);
        dialog.CaptureRenderedFrame()?.Save(Path.Combine(WindowTests.Shots, name + ".png"));
        dialog.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Dialogs_render()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        using var gradient = new SKBitmap(new SKImageInfo(256, 64, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (var x = 0; x < 256; x++) for (var y = 0; y < 64; y++) gradient.SetPixel(x, y, new SKColor((byte)x, (byte)(x / 2 + y), (byte)(255 - x)));
        var histogram = Histogram.Of(gradient);

        _ = AdjustmentDialogs.Edit(window, new LevelsAdjustment(), _ => { }, histogram, SKColors.Black, SKColors.White);
        Capture(window, "10-levels");
        _ = AdjustmentDialogs.Edit(window, new CurvesAdjustment().WithChannel(0, [new(0, 0), new(90, 140), new(255, 255)]), _ => { }, histogram, SKColors.Black, SKColors.White);
        Capture(window, "11-curves");
        _ = AdjustmentDialogs.Edit(window, new HueSaturationAdjustment(), _ => { }, histogram, SKColors.Black, SKColors.White);
        Capture(window, "12-hue-saturation");
        _ = AdjustmentDialogs.Edit(window, new GradientMapAdjustment(), _ => { }, histogram, SKColors.Black, SKColors.White);
        Capture(window, "13-gradient-map");
        _ = AdjustmentDialogs.EditFilter(window, new FilterSettings { Kind = FilterKind.MotionBlur }, _ => { });
        Capture(window, "14-motion-blur");
        _ = CanvasDialogs.NewCanvas(window, SKColors.White);
        Capture(window, "15-new-canvas");
        _ = CanvasDialogs.CanvasSize(window, 1920, 1080);
        Capture(window, "16-canvas-size");
        _ = CanvasDialogs.ImageSize(window, 1920, 1080, 72);
        Capture(window, "17-image-size");
        _ = Prompts.Color(window, "Foreground Color", new SKColor(0x20, 0xC0, 0xFF));
        Capture(window, "18-color");
    }
}

public class TextToolTests
{
    [AvaloniaFact]
    public void Clicking_with_the_text_tool_opens_the_editor_and_previews_on_the_canvas()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        var session = Compositor.Editing.EditorSession.NewCanvas(900, 500, new SKColor(0x1E, 0x22, 0x2E));
        window.AddSession(session);
        session.Foreground = new SKColor(0xFF, 0xC8, 0x57);
        window.SelectTool(Compositor.Editing.Tool.Text);
        Dispatcher.UIThread.RunJobs();
        var view = window.Canvas;
        var at = view.TranslatePoint(view.ToScreen(new SKPoint(80, 120)), window)!.Value;
        window.MouseDown(at, Avalonia.Input.MouseButton.Left);
        window.MouseUp(at, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        var dialog = Assert.Single(window.OwnedWindows);
        var box = Assert.IsType<TextBox>(dialog.FocusManager?.GetFocusedElement());
        box.Text = "Compositor\nfor Linux";
        Thread.Sleep(60);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Directory.CreateDirectory(WindowTests.Shots);
        dialog.CaptureRenderedFrame()?.Save(Path.Combine(WindowTests.Shots, "19-text-dialog.png"));
        dialog.Close(true);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.CaptureRenderedFrame()?.Save(Path.Combine(WindowTests.Shots, "20-text-layer.png"));

        var layer = Assert.Single(session.Document.Layers, l => l.Text != null);
        Assert.Equal("Compositor\nfor Linux", layer.Text!.Text);
        Assert.Equal("Text", session.History.UndoName);
        Assert.Equal(Compositor.Editing.Tool.Move, session.Tool);
        session.Undo();
        Assert.Single(session.Document.Layers);
    }
}
