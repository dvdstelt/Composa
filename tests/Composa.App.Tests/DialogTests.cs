using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.App.Controls;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Filters;
using SkiaSharp;

namespace Composa.App.Tests;

public class DialogTests
{
    private static void Capture(Window owner, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var dialog = owner.OwnedWindows.Last();
        Directory.CreateDirectory(WindowTests.Shots);
        dialog.CaptureRenderedFrame()?.Save(Path.Combine(WindowTests.Shots, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
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
        _ = AdjustmentDialogs.Edit(window, new BlackAndWhiteAdjustment { Tint = true }, _ => { }, histogram, SKColors.Black, SKColors.White);
        Capture(window, "27-black-white");
        _ = AdjustmentDialogs.Edit(window, new ColorBalanceAdjustment(), _ => { }, histogram, SKColors.Black, SKColors.White);
        Capture(window, "28-color-balance");
    }

    [AvaloniaFact]
    public void Raw_develop_dialog_previews_the_frame_and_returns_the_settings()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        var samples = new ushort[320 * 200 * 3];
        for (var y = 0; y < 200; y++) for (var x = 0; x < 320; x++)
        {
            var i = (y * 320 + x) * 3;
            samples[i] = (ushort)(x * 204); samples[i + 1] = (ushort)(y * 327); samples[i + 2] = (ushort)(65535 - x * 204);
        }
        var raw = new Composa.IO.RawImage(320, 200, samples);
        var task = RawDevelopDialog.Show(window, "IMG_0001.CR2", raw);
        Dispatcher.UIThread.RunJobs();
        var dialog = window.OwnedWindows.Last();
        // The preview develops on a worker; give it time to land.
        for (var i = 0; i < 50 && dialog.GetVisualDescendants().OfType<Image>().First().Source == null; i++) { Thread.Sleep(20); Dispatcher.UIThread.RunJobs(); }
        var image = dialog.GetVisualDescendants().OfType<Image>().First();
        Assert.NotNull(image.Source);
        var exposure = dialog.GetVisualDescendants().OfType<SliderField>().First();
        exposure.Value = 1.5;
        Dispatcher.UIThread.RunJobs();
        Capture(window, "29-raw-develop");
        Assert.Null(task.Result); // Capture closes the dialog, which counts as Cancel.
    }
}

public class JpegDialogTests
{
    [AvaloniaFact]
    public async Task Jpeg_dialog_previews_and_reports_the_size()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        using var picture = new SKBitmap(new SKImageInfo(640, 420, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(picture))
        {
            using var shader = SKShader.CreateSweepGradient(new SKPoint(320, 210), [SKColors.Crimson, SKColors.Gold, SKColors.Teal, SKColors.Crimson]);
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawPaint(paint);
        }
        var task = CanvasDialogs.JpegQuality(window, 60, picture);
        Dispatcher.UIThread.RunJobs();
        var dialog = Assert.Single(window.OwnedWindows);
        for (var i = 0; i < 40; i++) { await Task.Delay(25); Dispatcher.UIThread.RunJobs(); }
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        dialog.CaptureRenderedFrame()?.Save(Path.Combine(WindowTests.Shots, "21-jpeg-export.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        dialog.Close(true);
        Assert.Equal(60, await task);
    }
}

public class CameraRawDialogTests
{
    [AvaloniaFact]
    public void The_panel_lists_every_group_and_ok_commits_the_grade_as_one_step()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        var session = EditorSession.NewCanvas(200, 120, SKColors.White);
        window.AddSession(session);
        var photo = Rendering.Pixels.NewColor(160, 100);
        photo.Erase(new SKColor(128, 110, 100));
        session.AddImageLayer("Photo", photo);
        Dispatcher.UIThread.RunJobs();
        var original = session.ActiveLayer!.Pixels!;
        CameraRawSettings? result = null;
        var task = CameraRawDialog.Show(window, new CameraRawSettings { Exposure = 0.5 }, original, s => result = s, () => session.ActiveLayer?.Pixels);
        Dispatcher.UIThread.RunJobs();
        var dialog = Assert.Single(window.OwnedWindows);
        Assert.Equal("Camera Raw Filter", dialog.Title);
        var groups = dialog.GetVisualDescendants().OfType<Expander>().ToList();
        Assert.Equal(9, groups.Count);
        var headers = groups.Select(g => ((Grid)g.Header!).Children.OfType<TextBlock>().Single().Text).ToList();
        Assert.Equal(["Light", "Color", "Effects", "Curve", "Color Mixer", "Color Grading", "Detail", "Optics", "Calibration"], headers);
        Assert.True(groups[0].IsExpanded && groups[1].IsExpanded && !groups[2].IsExpanded);
        // The Light group's eye is shown because Exposure is set; clicking it renders without the group.
        var eye = ((Grid)groups[0].Header!).Children.OfType<Button>().Single();
        Assert.True(eye.IsVisible);
        Assert.False(((Grid)groups[2].Header!).Children.OfType<Button>().Single().IsVisible);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Directory.CreateDirectory(WindowTests.Shots);
        dialog.CaptureRenderedFrame()?.Save(Path.Combine(WindowTests.Shots, "30-camera-raw.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        eye.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        dialog.Close(true);
        Dispatcher.UIThread.RunJobs();
        var grade = task.Result;
        Assert.NotNull(grade);
        Assert.Equal(0, grade!.Exposure);                                   // Hidden on OK, the group is absent from the grade.
        Assert.True(grade.IsIdentity);

        // Through the menu, the grade previews live and commits as one undo step named after the filter.
        session.ApplyFilter(new FilterSettings { Kind = FilterKind.CameraRaw, CameraRaw = new CameraRawSettings { Exposure = 1 } });
        Assert.Equal("Camera Raw Filter", session.History.UndoName);
        Assert.True(session.ActiveLayer!.Pixels!.GetPixel(10, 10).Red > 160);
        _ = result;
    }
}
