using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Composa.Editing;
using SkiaSharp;

namespace Composa.App.Tests;

/// <summary>Tool toggles that follow the person rather than the document.</summary>
public class SettingsTests
{
    [Fact]
    public void View_options_and_move_tool_toggles_survive_the_settings_file()
    {
        var settings = new Settings
        {
            ShowTransformControls = false, AutoSelect = false, ShowPixelGrid = false,
            View = new ViewOptions { ShowRulers = true, ShowGrid = true, Snap = false, SnapToGrid = true, LockGuides = true }
        };
        var loaded = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal((false, false, false), (loaded.ShowTransformControls, loaded.AutoSelect, loaded.ShowPixelGrid));
        Assert.Equal(settings.View, loaded.View);
        // A settings file from before these were remembered keeps the defaults.
        var old = JsonSerializer.Deserialize<Settings>("""{ "JpegQuality": 80 }""")!;
        Assert.Equal((true, true, true), (old.ShowTransformControls, old.AutoSelect, old.View.Snap));
    }

    [AvaloniaFact]
    public void Toggling_view_options_and_transform_controls_is_remembered_and_seeds_the_first_document()
    {
        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        window.Settings.View = new ViewOptions { ShowRulers = true, SnapToLayers = false };
        window.Settings.AutoSelect = false;
        window.AddSession(EditorSession.NewCanvas(200, 100, SKColors.White));
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.Session!.View.ShowRulers);
        Assert.False(window.Session.View.SnapToLayers);
        Assert.True(window.Canvas.AutoSelect); // The canvas was built before the test changed the settings; startup reads them.

        window.KeyPressQwerty(PhysicalKey.R, RawInputModifiers.Control);      // Rulers off
        window.KeyPressQwerty(PhysicalKey.Quote, RawInputModifiers.Control);  // Grid on
        window.KeyPressQwerty(PhysicalKey.H, RawInputModifiers.Control);      // Transform controls off
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.Settings.View.ShowRulers);
        Assert.True(window.Settings.View.ShowGrid);
        Assert.False(window.Settings.ShowTransformControls);
        Assert.False(window.Canvas.ShowTransformControls);
    }
}
