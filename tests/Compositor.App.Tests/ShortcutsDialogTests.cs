using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Compositor.Editing;
using SkiaSharp;

namespace Compositor.App.Tests;

public class ShortcutsDialogTests
{
    [AvaloniaFact]
    public void A_tool_key_can_be_rebound_and_the_new_key_works()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        var session = EditorSession.NewCanvas(200, 200, SKColors.White);
        window.AddSession(session);
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.F1, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        var dialog = Assert.Single(window.OwnedWindows);
        Assert.Equal("Keyboard Shortcuts", dialog.Title);
        var search = dialog.GetVisualDescendants().OfType<TextBox>().First();
        search.Text = "Hand tool";
        Dispatcher.UIThread.RunJobs();
        var recorder = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "H");
        var point = recorder.TranslatePoint(new Point(recorder.Bounds.Width / 2, recorder.Bounds.Height / 2), dialog)!.Value;
        dialog.MouseDown(point, MouseButton.Left);
        dialog.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Press keys…", recorder.Content);
        dialog.KeyPressQwerty(PhysicalKey.K, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("K", recorder.Content);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Directory.CreateDirectory(WindowTests.Shots);
        dialog.CaptureRenderedFrame()?.Save(Path.Combine(WindowTests.Shots, "26-keyboard-shortcuts.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        dialog.Close(true);
        Dispatcher.UIThread.RunJobs();

        window.KeyPressQwerty(PhysicalKey.K, RawInputModifiers.None);
        Assert.Equal(Tool.Hand, session.Tool);
        window.KeyPressQwerty(PhysicalKey.V, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.H, RawInputModifiers.None);
        Assert.Equal(Tool.Move, session.Tool); // The old key no longer picks the Hand.
    }
}
