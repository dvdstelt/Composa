using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Compositor.App.Controls;
using Compositor.App.Dialogs;
using Compositor.Editing;
using Compositor.Filters;
using Compositor.Model;
using SkiaSharp;

namespace Compositor.App.Tests;

/// <summary>UI bugs found in review, each pinned by the interaction that exposed it.</summary>
public class UiRegressionTests
{
    private static MainWindow Open(out EditorSession session, int width = 800, int height = 600)
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        session = EditorSession.NewCanvas(width, height, SKColors.White);
        window.AddSession(session);
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static Point At(MainWindow window, float x, float y) => window.Canvas.TranslatePoint(window.Canvas.ToScreen(new SKPoint(x, y)), window)!.Value;

    [AvaloniaFact]
    public void Editing_existing_text_keeps_the_change()
    {
        var window = Open(out var session);
        var layer = session.AddText(new SKPoint(100, 100), new TextStyle { Text = "Hello", Size = 60 });
        window.SelectTool(Tool.Text);
        Dispatcher.UIThread.RunJobs();
        var at = At(window, 140, 140);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(session.IsEditingText);
        window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
        window.KeyTextInput("Goodbye");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.False(session.IsEditingText);
        Assert.Equal("Goodbye", session.Document.Find(layer.Id)!.Text!.Text);
        Assert.Equal("Edit Text", session.History.UndoName);
    }

    [AvaloniaFact]
    public void A_crop_rectangle_does_not_follow_you_to_another_tab()
    {
        var window = Open(out var first);
        window.SelectTool(Tool.Crop);
        Dispatcher.UIThread.RunJobs();
        window.MouseDown(At(window, 100, 100), MouseButton.Left);
        window.MouseMove(At(window, 300, 250), RawInputModifiers.LeftMouseButton);
        window.MouseUp(At(window, 300, 250), MouseButton.Left);
        Assert.True(window.Canvas.HasCrop);
        var second = EditorSession.NewCanvas(800, 600, SKColors.White);
        window.AddSession(second);
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.Canvas.HasCrop);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Equal(800, second.Document.Width);
        Assert.Equal(800, first.Document.Width);
    }

    [AvaloniaFact]
    public void Clicking_another_row_while_renaming_commits_the_name_without_duplicating_rows()
    {
        var window = Open(out var session);
        session.AddBlankLayer();
        session.AddBlankLayer();
        Dispatcher.UIThread.RunJobs();
        var panel = window.GetVisualDescendants().OfType<LayersPanel>().Single();
        List<Border> Rows() => panel.GetVisualDescendants().OfType<Border>().Where(b => b.Tag is Layer).ToList();
        var renamed = session.ActiveLayer!;
        panel.BeginRename();
        Dispatcher.UIThread.RunJobs();
        Assert.IsType<TextBox>(window.FocusManager?.GetFocusedElement()).Text = "Clouds";
        var other = Rows()[2];
        var point = other.TranslatePoint(new Point(150, other.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, Rows().Count);
        Assert.Equal("Clouds", session.Document.Find(renamed.Id)!.Name);
    }

    [AvaloniaFact]
    public void Thumbnail_clicks_choose_between_the_mask_and_the_pixels()
    {
        var window = Open(out var session);
        session.AddMask(session.ActiveLayer!);
        session.EditingMask = false;
        session.NotifyLayersChanged();
        Dispatcher.UIThread.RunJobs();
        var panel = window.GetVisualDescendants().OfType<LayersPanel>().Single();
        List<Avalonia.Controls.Image> Thumbs() => panel.GetVisualDescendants().OfType<Avalonia.Controls.Image>().ToList();
        void Click(Visual target)
        {
            var point = target.TranslatePoint(new Point(10, 10), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }
        Click(Thumbs()[1]);
        Assert.True(session.IsEditingMask);
        Click(Thumbs()[0]);
        Assert.False(session.IsEditingMask);
    }

    [AvaloniaFact]
    public void A_stray_right_click_does_not_cancel_a_brush_stroke()
    {
        var window = Open(out var session);
        window.SelectTool(Tool.Brush);
        Dispatcher.UIThread.RunJobs();
        window.MouseDown(At(window, 100, 100), MouseButton.Left);
        window.MouseMove(At(window, 200, 100), RawInputModifiers.LeftMouseButton);
        window.MouseDown(At(window, 200, 100), MouseButton.Right, RawInputModifiers.LeftMouseButton);
        window.MouseUp(At(window, 200, 100), MouseButton.Right, RawInputModifiers.LeftMouseButton);
        window.MouseMove(At(window, 300, 100), RawInputModifiers.LeftMouseButton);
        window.MouseUp(At(window, 300, 100), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Brush", session.History.UndoName);
        Assert.True(session.Composite().GetPixel(290, 100).Red < 60);
    }

    [AvaloniaFact]
    public void Shift_dragging_a_left_crop_handle_keeps_the_right_edge_in_place()
    {
        var window = Open(out _);
        window.SelectTool(Tool.Crop);
        Dispatcher.UIThread.RunJobs();
        window.MouseDown(At(window, 200, 200), MouseButton.Left);
        window.MouseMove(At(window, 600, 400), RawInputModifiers.LeftMouseButton);
        window.MouseUp(At(window, 600, 400), MouseButton.Left);
        window.MouseDown(At(window, 200, 200), MouseButton.Left, RawInputModifiers.Shift);
        window.MouseMove(At(window, 300, 300), RawInputModifiers.LeftMouseButton | RawInputModifiers.Shift);
        window.MouseUp(At(window, 300, 300), MouseButton.Left, RawInputModifiers.Shift);
        var crop = window.Canvas.CropRect!.Value;
        Assert.Equal(600, crop.Right);
        Assert.Equal(400, crop.Bottom);
        Assert.Equal(2, crop.Width / crop.Height, 2);
    }

    [AvaloniaFact]
    public void The_curve_editor_survives_points_that_are_almost_touching()
    {
        var window = Open(out _);
        var curves = new CurvesAdjustment().WithChannel(0, [new(0, 0), new(100, 100), new(103, 110), new(255, 255)]);
        var editor = new CurveEditor { Width = 300, Height = 300, Curves = curves };
        var host = new Window { Content = editor, Width = 320, Height = 320 };
        host.Show(window);
        Dispatcher.UIThread.RunJobs();
        var between = editor.TranslatePoint(new Point(101.5 / 255 * 300, 150), host)!.Value;
        host.MouseDown(between, MouseButton.Left);
        host.MouseMove(between + new Vector(3, 5), RawInputModifiers.LeftMouseButton);
        host.MouseUp(between + new Vector(3, 5), MouseButton.Left);
        Assert.Equal(4, editor.Curves.Channels[0].Length);
        host.Close();
    }

    [AvaloniaFact]
    public void The_zoomed_out_view_stays_sharp_at_any_pan_position()
    {
        var window = Open(out var session, 1600, 1200);
        var stripes = Rendering.Pixels.NewColor(1600, 1200);
        using (var canvas = new SKCanvas(stripes))
        using (var black = new SKPaint { Color = SKColors.Black })
        {
            canvas.Clear(SKColors.White);
            for (var x = 0; x < 1600; x += 4) canvas.DrawRect(x, 0, 2, 1200, black);
        }
        session.AddImageLayer("stripes", stripes, fit: false);
        window.Canvas.ZoomTo(0.5);
        window.SelectTool(Tool.Hand);
        foreach (var nudge in new[] { 0.0, 0.3, 0.5, 0.8 })
        {
            // Pan by a fraction of a pixel through the wheel path is not possible, so zoom about shifting anchors instead.
            window.Canvas.ZoomTo(0.5, new Point(300 + nudge, 300 + nudge));
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var frame = window.CaptureRenderedFrame()!;
            using var stream = new MemoryStream();
            frame.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            using var shot = SKBitmap.Decode(stream.ToArray());
            var center = window.Canvas.TranslatePoint(new Point(window.Canvas.Bounds.Width / 2, window.Canvas.Bounds.Height / 2), window)!.Value;
            int darkest = 255, lightest = 0;
            for (var x = (int)center.X - 20; x < (int)center.X + 20; x++)
            {
                var value = shot.GetPixel(x, (int)center.Y).Red;
                darkest = Math.Min(darkest, value);
                lightest = Math.Max(lightest, value);
            }
            // 2 px stripes at 50% are 1 px stripes on screen: full contrast only when the render sits on the pixel grid.
            Assert.True(lightest - darkest > 200, $"contrast {darkest}..{lightest} at nudge {nudge}");
        }
    }
}
