using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Composa.Editing;
using Composa.Model;
using Composa.Selections;
using SkiaSharp;

namespace Composa.App.Tests;

public class SelectionAndShapeBarTests
{
    private readonly MainWindow window;
    private readonly EditorSession session;

    public SelectionAndShapeBarTests()
    {
        window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        session = EditorSession.NewCanvas(600, 400, SKColors.White);
        window.AddSession(session);
        Dispatcher.UIThread.RunJobs();
    }

    private Point At(float x, float y) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(new SKPoint(x, y)), window)!.Value;

    [AvaloniaFact]
    public void Tab_switches_the_magic_tool_to_object_mode_and_a_click_selects_the_object()
    {
        session.AddImageLayer("box", Rendering.Pixels.NewColor(80, 60), new SKPoint(300, 200));
        session.ActiveLayer!.Pixels!.Erase(SKColors.Red);
        session.InvalidateAll();
        session.SampleAllLayers = true;
        window.SelectTool(Tool.Wand);
        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(WandMode.Object, session.WandMode);
        Assert.Equal(Tool.Wand, session.Tool);
        window.MouseDown(At(300, 200), MouseButton.Left);
        window.MouseUp(At(300, 200), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new SKRectI(260, 170, 340, 230), SelectionMask.Bounds(session.Selection!, 128));

        // The bar's Expand button works by its amount field.
        var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
        var expand = buttons.Single(b => b.Content as string == "Expand");
        Assert.True(expand.IsEnabled);
        session.SelectionExpandAmount = 5;
        expand.Command?.Execute(null);
        var point = expand.TranslatePoint(new Point(expand.Bounds.Width / 2, expand.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new SKRectI(255, 165, 345, 235), SelectionMask.Bounds(session.Selection!, 128));

        window.KeyPressQwerty(PhysicalKey.D, RawInputModifiers.Control);
        window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control | RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Select Subject", session.History.UndoName);
        Assert.True(session.Selection!.GetPixel(300, 200).Alpha > 200);
    }

    [AvaloniaFact]
    public void Shift_U_reaches_the_line_shape_and_dragging_draws_one()
    {
        window.SelectTool(Tool.Shape);
        session.ShapeKind = ShapeKind.Ellipse;
        window.KeyPressQwerty(PhysicalKey.U, RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(ShapeKind.Line, session.ShapeKind);
        session.Foreground = SKColors.Blue;
        session.ShapeLineWidth = 8;
        window.MouseDown(At(100, 100), MouseButton.Left);
        window.MouseMove(At(300, 110), RawInputModifiers.LeftMouseButton | RawInputModifiers.Shift);
        window.MouseUp(At(300, 110), MouseButton.Left, RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
        var line = Assert.Single(session.Document.Layers, l => l.Shape?.Kind == ShapeKind.Line);
        Assert.Equal(8, line.Shape!.LineWidth);
        // Shift snapped the slight slope away: a flat line 8 pixels tall.
        Assert.Equal(8, line.Transform.Height);
        TestColor(SKColors.Blue, session.Composite().GetPixel(200, 100));
        Directory.CreateDirectory(WindowTests.Shots);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.CaptureRenderedFrame()?.Save(Path.Combine(WindowTests.Shots, "24-line-shape.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static void TestColor(SKColor expected, SKColor actual) =>
        Assert.True(Math.Abs(expected.Red - actual.Red) < 3 && Math.Abs(expected.Green - actual.Green) < 3 && Math.Abs(expected.Blue - actual.Blue) < 3, $"expected {expected}, found {actual}");
}
