using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Composa.Editing;
using Composa.IO;
using SkiaSharp;

namespace Composa.App.Tests;

/// <summary>Saving writes off the UI thread: the tools stay usable, and only what was on screen when Save was pressed counts as saved.</summary>
public class SavingTests
{
    private static async Task Pump(Func<bool> until)
    {
        for (var i = 0; i < 1000 && !until(); i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Dispatcher.UIThread.RunJobs();
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"composa-save-{Guid.NewGuid():N}{ProjectFile.Extension}");

    private static SKColor SavedPixel(string path) => ProjectFile.Load(path).Layers[0].Pixels!.GetPixel(5, 5);

    [AvaloniaFact]
    public async Task Saving_writes_a_snapshot_in_the_background_and_edits_made_meanwhile_stay_unsaved()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        var session = EditorSession.NewCanvas(1500, 1500, SKColors.White);
        window.AddSession(session);
        var path = TempPath();
        session.FilePath = path;
        session.Fill(SKColors.Red);
        Dispatcher.UIThread.RunJobs();
        try
        {
            window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
            Assert.True(window.IsSaving(session));
            Assert.True(session.IsModified);                                 // Nothing counts as saved until the file is written.
            session.Fill(SKColors.Blue);                                     // Editing goes on while the file writes.
            Assert.Equal("Fill", session.History.UndoName);
            await Pump(() => !window.IsSaving(session));
            Assert.True(File.Exists(path));
            Assert.True(session.IsModified);                                 // The blue fill came after the snapshot.
            Assert.Equal(SKColors.Red, SavedPixel(path));                    // What was on screen when Save was pressed.

            window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
            await Pump(() => !window.IsSaving(session));
            Assert.False(session.IsModified);
            Assert.Equal(SKColors.Blue, SavedPixel(path));
            Assert.Equal(0, Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".tmp-*").Length);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Two quick saves write in order, the second taking what changed since the first, and neither cuts the other's file short.</summary>
    [AvaloniaFact]
    public async Task A_second_save_waits_for_the_first_and_saves_what_changed_since()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        var session = EditorSession.NewCanvas(1500, 1500, SKColors.White);
        window.AddSession(session);
        var path = TempPath();
        session.FilePath = path;
        session.Fill(SKColors.Red);
        Dispatcher.UIThread.RunJobs();
        try
        {
            window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
            session.Fill(SKColors.Green);
            window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
            Assert.True(window.IsSaving(session));
            await Pump(() => !window.IsSaving(session) && !session.IsModified);
            Assert.False(session.IsModified);
            Assert.Equal(SKColors.Green, SavedPixel(path));
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task Closing_the_window_waits_for_a_save_still_writing()
    {
        var window = new MainWindow { Width = 1000, Height = 700 };
        window.Show();
        var session = EditorSession.NewCanvas(1500, 1500, SKColors.White);
        window.AddSession(session);
        var path = TempPath();
        session.FilePath = path;
        session.Fill(SKColors.Red);
        Dispatcher.UIThread.RunJobs();
        try
        {
            window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
            Assert.True(window.IsSaving(session));
            window.Close();
            Assert.True(window.IsVisible);                                   // Not yet: the file is still being written.
            await Pump(() => !window.IsVisible);
            Assert.False(window.IsVisible);                                  // Saved, so nothing was left to ask about.
            Assert.False(session.IsModified);
            Assert.Equal(SKColors.Red, SavedPixel(path));
        }
        finally { File.Delete(path); }
    }
}
