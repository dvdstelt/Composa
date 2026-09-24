using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Composa.Editing;
using Composa.Filters;
using Composa.Painting;
using SkiaSharp;

namespace Composa.App.Tests;

public class WindowTests
{
    internal static readonly string Shots = Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/screenshots");

    private static void Capture(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame();
        Directory.CreateDirectory(Shots);
        frame?.Save(Path.Combine(Shots, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
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

public class KeyboardTests
{
    [AvaloniaFact]
    public void A_bare_alt_press_does_not_steal_focus_for_the_menu()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        window.AddSession(EditorSession.NewCanvas(200, 200, SKColors.White));
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.AltLeft, RawInputModifiers.Alt);
        window.KeyReleaseQwerty(PhysicalKey.AltLeft, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.B, RawInputModifiers.None);
        Assert.Equal(Tool.Brush, window.Session!.Tool);
        Assert.False(window.FocusManager?.GetFocusedElement() is MenuItem);
    }

    [AvaloniaFact]
    public void Tool_and_command_shortcuts_work()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        var session = EditorSession.NewCanvas(200, 200, SKColors.White);
        window.AddSession(session);
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
        Assert.NotNull(session.Selection);
        window.KeyPressQwerty(PhysicalKey.I, RawInputModifiers.Control);
        Assert.Equal("Invert", session.History.UndoName);
        window.KeyPressQwerty(PhysicalKey.D, RawInputModifiers.Control);
        Assert.Null(session.Selection);
        window.KeyPressQwerty(PhysicalKey.N, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.Equal(2, session.Document.Layers.Count);
        window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.Control);
        Assert.Single(session.Document.Layers);
        window.KeyPressQwerty(PhysicalKey.E, RawInputModifiers.None);
        Assert.True(session.EraserMode);
        window.KeyPressQwerty(PhysicalKey.BracketRight, RawInputModifiers.None);
        Assert.Equal(45, session.Brush.Size);
        window.KeyPressQwerty(PhysicalKey.Digit5, RawInputModifiers.None);
        Assert.Equal(0.5, session.Brush.Opacity);
    }
}

public class LayersPanelTests
{
    [AvaloniaFact]
    public void Complex_layer_stack_renders()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        var session = EditorSession.NewCanvas(800, 600, new SKColor(0x30, 0x34, 0x40));
        window.AddSession(session);
        session.ShapeKind = Model.ShapeKind.Ellipse;
        session.Foreground = new SKColor(0xFF, 0x8A, 0x3D);
        var sun = session.AddShape(new SKRect(250, 120, 550, 420))!;
        session.ShapeKind = Model.ShapeKind.RoundedRectangle;
        session.Foreground = new SKColor(0x3D, 0x9B, 0xFF);
        var stripe = session.AddShape(new SKRect(100, 240, 700, 330))!;
        session.ToggleClippingMask(stripe);
        session.SelectLayer(sun.Id);
        session.SelectLayer(stripe.Id, extend: true);
        session.GroupSelectedLayers();
        var folder = session.ActiveLayer!;
        session.AddMask(folder);
        session.EditingMask = true;
        session.Foreground = SKColors.Black;
        session.GradientToTransparent = false;
        session.Begin("Gradient");
        session.DrawGradient(folder, folder.Mask!, new SKPoint(0, 300), new SKPoint(800, 300));
        session.Commit();
        session.EditingMask = false;
        var curves = session.AddAdjustmentLayer(new CurvesAdjustment().WithChannel(0, [new(0, 20), new(128, 170), new(255, 255)]));
        var hidden = session.AddBlankLayer();
        session.SetVisible(hidden, false);
        session.SelectLayer(curves.Id);
        window.SelectTool(Tool.Crop);
        Dispatcher.UIThread.RunJobs();
        var view = window.Canvas;
        var from = view.TranslatePoint(view.ToScreen(new SKPoint(100, 80)), window)!.Value;
        var to = view.TranslatePoint(view.ToScreen(new SKPoint(640, 500)), window)!.Value;
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
        Assert.True(view.HasCrop);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Directory.CreateDirectory(WindowTests.Shots);
        window.CaptureRenderedFrame()?.Save(Path.Combine(WindowTests.Shots, "07-layers-and-crop.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Equal(540, session.Document.Width);
        Assert.Equal(420, session.Document.Height);
    }
}

public class ZoomTests
{
    private static void Save(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Directory.CreateDirectory(WindowTests.Shots);
        window.CaptureRenderedFrame()?.Save(Path.Combine(WindowTests.Shots, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    [AvaloniaFact]
    public void Large_documents_render_zoomed_out_and_zoomed_in()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        var session = EditorSession.NewCanvas(4800, 3200, SKColors.White);
        window.AddSession(session);
        var photo = Rendering.Pixels.NewColor(4000, 2600);
        using (var canvas = new SKCanvas(photo))
        {
            using var shader = SKShader.CreateSweepGradient(new SKPoint(2000, 1300), [SKColors.OrangeRed, SKColors.Gold, SKColors.MediumSeaGreen, SKColors.RoyalBlue, SKColors.OrangeRed]);
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawPaint(paint);
            using var line = new SKPaint { Color = SKColors.Black, StrokeWidth = 3, IsAntialias = true, Style = SKPaintStyle.Stroke };
            for (var r = 100; r < 1300; r += 40) canvas.DrawCircle(2000, 1300, r, line);
        }
        session.AddImageLayer("Rings", photo, fit: false);
        session.AddAdjustmentLayer(new CurvesAdjustment().WithChannel(0, [new(0, 0), new(110, 150), new(255, 255)]));
        session.SelectEllipse(new SKRect(1500, 900, 3300, 2300));
        Save(window, "08-large-zoomed-out");
        Assert.True(window.Canvas.Zoom < 0.5);

        // Painting while zoomed out updates just the dirty part of the on-screen render.
        session.Deselect();
        session.AddBlankLayer();
        window.SelectTool(Tool.Brush);
        session.Foreground = SKColors.White;
        session.Brush = new BrushSettings { Size = 260, Hardness = 0.7 };
        session.BeginStroke(new SKPoint(600, 600), out _);
        for (var i = 0; i <= 30; i++) { session.ContinueStroke(new SKPoint(600 + i * 120, 600 + MathF.Sin(i / 3f) * 300)); Dispatcher.UIThread.RunJobs(); }
        session.EndStroke();
        Save(window, "09-large-painted");

        window.Canvas.ZoomTo(12, new Point(window.Canvas.Bounds.Width / 2, window.Canvas.Bounds.Height / 2));
        Save(window, "09b-zoomed-in-grid");
        Assert.Equal(12, window.Canvas.Zoom);
    }

    /// <summary>A Magic Wand outline with tens of thousands of edges is drawn from a screen-resolution trace when zoomed out, and in full at 1:1 and above.</summary>
    [AvaloniaFact]
    public void Complex_selections_render_zoomed_out_and_in()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        var session = EditorSession.NewCanvas(3000, 2000, SKColors.White);
        window.AddSession(session);
        var mask = Rendering.Pixels.NewMask(3000, 2000);
        var span = mask.GetPixelSpan();
        for (var y = 400; y < 1600; y++) for (var x = 600; x < 2400; x++) if (((x / 8) + (y / 8)) % 2 == 0) span[y * mask.RowBytes + x] = 255;
        Rendering.Pixels.Invalidate(mask);
        session.Select(mask, Selections.SelectionMode.Replace);
        using (var outline = Selections.SelectionMask.Outline(session.Selection!)) Assert.True(outline.PointCount > 20_000);
        Save(window, "08b-complex-selection-zoomed-out");
        Assert.True(window.Canvas.Zoom < 0.5);
        window.Canvas.ZoomTo(2, new Point(window.Canvas.Bounds.Width / 2, window.Canvas.Bounds.Height / 2));
        Save(window, "08c-complex-selection-zoomed-in");
        Assert.Equal(2, window.Canvas.Zoom);
    }
}
