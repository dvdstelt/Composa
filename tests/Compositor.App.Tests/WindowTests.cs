using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Compositor.Editing;
using Compositor.Filters;
using Compositor.Painting;
using SkiaSharp;

namespace Compositor.App.Tests;

public class WindowTests
{
    internal static readonly string Shots = Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/screenshots");

    private static void Capture(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame();
        Directory.CreateDirectory(Shots);
        frame?.Save(Path.Combine(Shots, name + ".png"));
    }

    private static MainWindow Open()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        return window;
    }

    private static EditorSession Sample(MainWindow window)
    {
        var session = EditorSession.NewCanvas(900, 600, SKColors.White);
        window.AddSession(session);
        var photo = Rendering.Pixels.NewColor(500, 320);
        using (var canvas = new SKCanvas(photo))
        {
            using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(500, 320), [new SKColor(0xFF, 0x7A, 0x3D), new SKColor(0x4B, 0x2A, 0x9E)], SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawPaint(paint);
            using var sun = new SKPaint { Color = new SKColor(0xFF, 0xE0, 0x80), IsAntialias = true };
            canvas.DrawCircle(360, 120, 60, sun);
        }
        session.AddImageLayer("Sunset", photo);
        return session;
    }

    [AvaloniaFact]
    public void Welcome_screen_renders()
    {
        var window = Open();
        Capture(window, "01-welcome");
        Assert.Null(window.Session);
    }

    [AvaloniaFact]
    public void Editing_session_renders_with_layers_selection_and_tools()
    {
        var window = Open();
        var session = Sample(window);
        Capture(window, "02-move-tool");

        session.SelectEllipse(new SKRect(250, 150, 650, 450));
        session.AddAdjustmentLayer(new HueSaturationAdjustment().WithShift(HueRange.Master, new HslShift(120, 20, 0)));
        session.Deselect();
        window.SelectTool(Tool.Brush);
        session.AddBlankLayer();
        session.Foreground = new SKColor(0x20, 0xC0, 0xFF);
        session.Brush = new BrushSettings { Size = 46, Hardness = 0.4 };
        session.BeginStroke(new SKPoint(120, 480), out _);
        for (var i = 0; i <= 40; i++) session.ContinueStroke(new SKPoint(120 + i * 16, 480 + MathF.Sin(i / 4f) * 60));
        session.EndStroke();
        Capture(window, "03-brush-and-adjustment");

        window.SelectTool(Tool.Marquee);
        session.SelectRect(new SKRect(80, 60, 420, 300));
        Capture(window, "04-selection");

        session.Deselect();
        session.SelectLayer(session.Document.Layers[1].Id);
        window.SelectTool(Tool.Move);
        var edit = session.BeginTransform()!;
        edit.RotateTo(new SKPoint(900, 300), new SKPoint(880, 420), snap: false);
        session.CommitTransform();
        Capture(window, "05-rotated");
        Assert.True(session.CanUndo);
    }

    [AvaloniaFact]
    public void Pointer_input_paints_on_the_canvas()
    {
        var window = Open();
        var session = EditorSession.NewCanvas(400, 300, SKColors.White);
        window.AddSession(session);
        window.SelectTool(Tool.Brush);
        Dispatcher.UIThread.RunJobs();
        var view = window.Canvas;
        var center = view.TranslatePoint(new Point(view.Bounds.Width / 2, view.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseMove(center + new Vector(60, 10));
        window.MouseUp(center + new Vector(60, 10), MouseButton.Left);
        Capture(window, "06-pointer-stroke");
        Assert.True(session.CanUndo);
        Assert.Equal("Brush", session.History.UndoName);
        var painted = session.Composite().GetPixel(200, 150);
        Assert.True(painted.Red < 40, $"expected black paint at the center, found {painted}");

        window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        Assert.False(session.CanUndo);
        window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.None);
        Assert.Equal(Tool.Marquee, session.Tool);
    }
}
