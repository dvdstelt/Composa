using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Compositor.Editing;
using Compositor.Selections;
using SkiaSharp;

namespace Compositor.App.Tests;

/// <summary>Every canvas tool driven through real pointer and key events.</summary>
public class ToolInputTests
{
    private readonly MainWindow window;
    private readonly EditorSession session;

    public ToolInputTests()
    {
        window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        session = EditorSession.NewCanvas(600, 400, SKColors.White);
        window.AddSession(session);
        Dispatcher.UIThread.RunJobs();
    }

    private Point At(float x, float y) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(new SKPoint(x, y)), window)!.Value;

    private void Drag(SKPoint from, SKPoint to, RawInputModifiers modifiers = RawInputModifiers.None, int steps = 6)
    {
        window.MouseDown(At(from.X, from.Y), MouseButton.Left, modifiers);
        for (var i = 1; i <= steps; i++)
            window.MouseMove(At(from.X + (to.X - from.X) * i / steps, from.Y + (to.Y - from.Y) * i / steps), modifiers | RawInputModifiers.LeftMouseButton);
        window.MouseUp(At(to.X, to.Y), MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    private void Click(float x, float y, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.MouseDown(At(x, y), MouseButton.Left, modifiers);
        window.MouseUp(At(x, y), MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Marquee_selects_adds_subtracts_and_moves()
    {
        window.SelectTool(Tool.Marquee);
        Drag(new SKPoint(100, 100), new SKPoint(200, 180));
        Assert.Equal(new SKRectI(100, 100, 200, 180), SelectionMask.Bounds(session.Selection!));
        Drag(new SKPoint(300, 100), new SKPoint(350, 150), RawInputModifiers.Shift);
        Assert.Equal(new SKRectI(100, 100, 350, 180), SelectionMask.Bounds(session.Selection!));
        Drag(new SKPoint(290, 90), new SKPoint(360, 160), RawInputModifiers.Alt);
        Assert.Equal(new SKRectI(100, 100, 200, 180), SelectionMask.Bounds(session.Selection!));
        Drag(new SKPoint(150, 150), new SKPoint(170, 160)); // Inside: moves the outline.
        Assert.Equal(new SKRectI(120, 110, 220, 190), SelectionMask.Bounds(session.Selection!));
        Click(500, 350);
        Assert.Null(session.Selection);
    }

    [AvaloniaFact]
    public void Lasso_wand_and_polygon_select()
    {
        window.SelectTool(Tool.Lasso);
        window.MouseDown(At(50, 50), MouseButton.Left);
        foreach (var p in new[] { new SKPoint(150, 50), new SKPoint(150, 150), new SKPoint(50, 150) }) window.MouseMove(At(p.X, p.Y), RawInputModifiers.LeftMouseButton);
        window.MouseUp(At(50, 150), MouseButton.Left);
        Assert.NotNull(session.Selection);
        Assert.Equal(255, session.Selection!.GetPixel(100, 100).Alpha);

        session.LassoKind = LassoKind.Polygonal;
        window.SelectTool(Tool.Lasso);
        Click(300, 50); Click(400, 50); Click(400, 150);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(255, session.Selection!.GetPixel(380, 70).Alpha);
        Assert.Equal(0, session.Selection!.GetPixel(100, 100).Alpha);

        session.Deselect();
        window.SelectTool(Tool.Wand);
        Click(10, 10);
        Assert.Equal(new SKRectI(0, 0, 600, 400), SelectionMask.Bounds(session.Selection!, 128));
    }

    [AvaloniaFact]
    public void Gradient_shape_eyedropper_and_fill_keys()
    {
        session.Foreground = SKColors.Red;
        session.Background = SKColors.Blue;
        window.SelectTool(Tool.Gradient);
        Drag(new SKPoint(0, 200), new SKPoint(600, 200));
        Assert.True(session.IsInteracting); // Still adjustable.
        Assert.True(session.Composite().GetPixel(300, 200).Red is > 100 and < 160);
        Drag(new SKPoint(600, 200), new SKPoint(300, 200)); // Pull the end in: everything right of it is now pure blue.
        Assert.True(session.Composite().GetPixel(320, 200).Blue > 240);
        window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control); // Undo takes the open gradient back.
        Assert.False(session.CanUndo);
        Assert.Equal(255, session.Composite().GetPixel(20, 200).Green);
        Drag(new SKPoint(0, 200), new SKPoint(600, 200));
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Assert.False(session.IsInteracting);
        Assert.Equal("Gradient", session.History.UndoName);
        Assert.True(session.Composite().GetPixel(20, 200).Red > 200);
        Assert.True(session.Composite().GetPixel(580, 200).Blue > 200);

        window.SelectTool(Tool.Shape);
        session.Foreground = SKColors.Lime;
        Drag(new SKPoint(100, 100), new SKPoint(200, 160));
        var shape = Assert.Single(session.Document.Layers, l => l.Shape != null);
        Assert.Equal((100d, 100d, 100d, 60d), (shape.Transform.X, shape.Transform.Y, shape.Transform.Width, shape.Transform.Height));

        window.SelectTool(Tool.Eyedropper);
        Click(150, 130);
        Assert.Equal(SKColors.Lime, session.Foreground);

        session.SelectLayer(session.Document.Layers[0].Id);
        window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.Control);
        Assert.Equal("Fill", session.History.UndoName);
        TestColor(SKColors.Blue, session.Document.Layers[0].Pixels!.GetPixel(5, 5));
    }

    [AvaloniaFact]
    public void Move_tool_drags_scales_with_handles_and_nudges()
    {
        var box = Rendering.Pixels.NewColor(100, 100);
        box.Erase(SKColors.Red);
        var layer = session.AddImageLayer("box", box, new SKPoint(300, 200));
        window.SelectTool(Tool.Move);
        Dispatcher.UIThread.RunJobs();
        Drag(new SKPoint(300, 200), new SKPoint(340, 230));
        Assert.Equal((290d, 180d), (layer.Transform.X, layer.Transform.Y));
        Assert.Equal("Move", session.History.UndoName);

        Drag(new SKPoint(390, 280), new SKPoint(440, 330)); // Bottom-right handle, proportional.
        Assert.Equal((150d, 150d), (Math.Round(layer.Transform.Width), Math.Round(layer.Transform.Height)));
        Assert.Equal(290, layer.Transform.X);

        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.Shift);
        Assert.Equal(300, layer.Transform.X);

        // A key pressed mid-drag must not start another edit.
        window.MouseDown(At(350, 250), MouseButton.Left);
        window.MouseMove(At(360, 250), RawInputModifiers.LeftMouseButton);
        window.KeyPressQwerty(PhysicalKey.D, RawInputModifiers.Control);
        window.KeyPressQwerty(PhysicalKey.B, RawInputModifiers.None);
        window.MouseMove(At(380, 250), RawInputModifiers.LeftMouseButton);
        window.MouseUp(At(380, 250), MouseButton.Left);
        Assert.Equal(Tool.Move, session.Tool);
        Assert.Equal(330, layer.Transform.X);
        session.Undo();
        Assert.Equal(300, session.Document.Find(layer.Id)!.Transform.X);
    }

    [AvaloniaFact]
    public void Ctrl_drag_moves_the_layer_with_any_tool_and_leaves_the_other_ctrl_drags_alone()
    {
        var box = Rendering.Pixels.NewColor(100, 100);
        box.Erase(SKColors.Red);
        var layer = session.AddImageLayer("box", box, new SKPoint(300, 200));
        var blue = Rendering.Pixels.NewColor(40, 40);
        blue.Erase(SKColors.Blue);
        var other = session.AddImageLayer("other", blue, new SKPoint(500, 100));
        session.SelectLayer(layer.Id);
        window.SelectTool(Tool.Brush);
        session.Brush = session.Brush with { Size = 10, Hardness = 1 };
        Dispatcher.UIThread.RunJobs();

        // Pressing Ctrl over the canvas promises a move with the four-way arrow at once, before the pointer moves;
        // letting go brings the brush cursor back.
        window.MouseMove(At(300, 200), RawInputModifiers.None);
        Assert.Equal("None", window.Canvas.Cursor?.ToString());
        window.KeyPressQwerty(PhysicalKey.ControlLeft, RawInputModifiers.None);
        Assert.Equal("SizeAll", window.Canvas.Cursor?.ToString());
        window.KeyReleaseQwerty(PhysicalKey.ControlLeft, RawInputModifiers.Control);
        Assert.Equal("None", window.Canvas.Cursor?.ToString());
        window.MouseMove(At(300, 200), RawInputModifiers.Control);
        Assert.Equal("SizeAll", window.Canvas.Cursor?.ToString());
        window.MouseMove(At(302, 200), RawInputModifiers.None);
        Assert.Equal("None", window.Canvas.Cursor?.ToString());

        // With the Brush: the layer moves, nothing is painted, and the tool stays the Brush.
        Drag(new SKPoint(300, 200), new SKPoint(340, 230), RawInputModifiers.Control);
        Assert.Equal((290d, 180d), (layer.Transform.X, layer.Transform.Y));
        Assert.Equal("Move", session.History.UndoName);
        Assert.Equal(Tool.Brush, session.Tool);
        Assert.Equal(SKColors.Red, session.Composite().GetPixel(320, 220));

        // A Ctrl-press on another layer's pixels, away from the current frame, moves that layer instead.
        Drag(new SKPoint(500, 100), new SKPoint(510, 120), RawInputModifiers.Control);
        Assert.Equal((490d, 100d), (other.Transform.X, other.Transform.Y));
        Assert.Equal((290d, 180d), (layer.Transform.X, layer.Transform.Y));
        Assert.Equal(other.Id, session.ActiveLayer!.Id);

        // The Marquee's own Ctrl-drag inside a selection still moves the selected pixels rather than the layer.
        session.SelectLayer(layer.Id);
        window.SelectTool(Tool.Marquee);
        Dispatcher.UIThread.RunJobs();
        Drag(new SKPoint(300, 190), new SKPoint(340, 230));
        Drag(new SKPoint(320, 210), new SKPoint(320, 260), RawInputModifiers.Control);
        Assert.Equal(SKColors.Red, session.Composite().GetPixel(320, 270));  // The pixels moved down...
        Assert.Equal(SKColors.White, session.Composite().GetPixel(320, 200)); // ...leaving the white background behind.
        Assert.Equal((490d, 100d), (other.Transform.X, other.Transform.Y)); // No layer was moved.

        // Without a selection, a Ctrl-drag moves the layer again, snapping like an ordinary move does.
        session.ClearSelection();
        session.SelectLayer(other.Id);
        Drag(new SKPoint(510, 120), new SKPoint(23, 120), RawInputModifiers.Control);
        Assert.Equal(0, other.Transform.X);                                  // 3 px short, snapped onto the canvas edge.

        // The Type tool shows the move cursor too, instead of its I-beam, for as long as Ctrl is down.
        window.SelectTool(Tool.Text);
        window.MouseMove(At(200, 300), RawInputModifiers.None);
        Assert.Equal("Ibeam", window.Canvas.Cursor?.ToString());
        window.KeyPressQwerty(PhysicalKey.ControlLeft, RawInputModifiers.None);
        window.MouseMove(At(202, 300), RawInputModifiers.Control);
        Assert.Equal("SizeAll", window.Canvas.Cursor?.ToString());
        window.KeyReleaseQwerty(PhysicalKey.ControlLeft, RawInputModifiers.Control);
        window.MouseMove(At(204, 300), RawInputModifiers.None);
        Assert.Equal("Ibeam", window.Canvas.Cursor?.ToString());
    }

    [AvaloniaFact]
    public void Zoom_pan_and_escape()
    {
        window.SelectTool(Tool.Zoom);
        var before = window.Canvas.Zoom;
        Click(300, 200);
        Assert.True(window.Canvas.Zoom > before);
        Click(300, 200, RawInputModifiers.Alt);
        Assert.Equal(before, window.Canvas.Zoom, 3);

        window.SelectTool(Tool.Hand);
        var origin = At(0, 0);
        Drag(new SKPoint(300, 200), new SKPoint(350, 240));
        Assert.NotEqual(origin, At(0, 0));

        window.SelectTool(Tool.Brush);
        window.MouseDown(At(100, 100), MouseButton.Left);
        window.MouseMove(At(200, 100), RawInputModifiers.LeftMouseButton);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.MouseUp(At(200, 100), MouseButton.Left);
        Assert.False(session.CanUndo);
        Assert.False(session.IsInteracting);
    }

    private static void TestColor(SKColor expected, SKColor actual) =>
        Assert.True(Math.Abs(expected.Red - actual.Red) < 3 && Math.Abs(expected.Green - actual.Green) < 3 && Math.Abs(expected.Blue - actual.Blue) < 3, $"expected {expected}, found {actual}");
}
