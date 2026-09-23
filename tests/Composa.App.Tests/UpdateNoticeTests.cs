using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace Composa.App.Tests;

public class UpdateNoticeTests
{
    private static (Window Window, UpdateNotice Notice) Open()
    {
        var notice = new UpdateNotice();
        var window = new Window { Width = 900, Height = 120, Content = notice };
        window.Show();
        return (window, notice);
    }

    [AvaloniaFact]
    public void The_notice_stays_out_of_the_way_until_there_is_something_to_say()
    {
        var (_, notice) = Open();
        Assert.False(notice.IsVisible);
    }

    [AvaloniaFact]
    public void Showing_a_version_renders_the_strip()
    {
        var (window, notice) = Open();
        notice.Show(new ReleaseVersion(9, 9, 9, ""));
        Assert.True(notice.IsVisible);

        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame();
        Directory.CreateDirectory(WindowTests.Shots);
        frame?.Save(Path.Combine(WindowTests.Shots, "20-update-notice.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        Assert.NotNull(frame);
    }

    [AvaloniaFact]
    public void Dismissing_hides_it_without_skipping_the_version()
    {
        var (_, notice) = Open();
        var skipped = false;
        notice.Skip += () => skipped = true;

        notice.Show(new ReleaseVersion(9, 9, 9, ""));
        notice.Hide();

        Assert.False(notice.IsVisible);
        Assert.False(skipped); // Dismissing is "not now", not "never tell me again".
    }

    [AvaloniaFact]
    public void Skipping_raises_the_event_and_hides_it()
    {
        var (_, notice) = Open();
        var skipped = 0;
        notice.Skip += () => skipped++;

        notice.Show(new ReleaseVersion(9, 9, 9, ""));
        var skip = notice.GetVisualDescendants().OfType<Button>()
            .First(b => (b.Content as string) == "Skip this version");
        // Ui.TextButton subscribes to Click rather than binding a Command, so the event is what
        // has to be raised; executing a Command here would silently do nothing.
        skip.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, skipped);
        Assert.False(notice.IsVisible);
    }
}
