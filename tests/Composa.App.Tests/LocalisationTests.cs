using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Composa.Editing;
using Composa.Model;
using SkiaSharp;
using Xunit;

namespace Composa.App.Tests;

/// <summary>
/// The rules the localisation rests on: English is the identity and the fallback, a translation only
/// ever changes the words drawn on screen, and a name nothing translates reads as itself rather than
/// as nothing. Every case puts the language back, because the language is process-wide state that the
/// rest of the suite reads English.
/// </summary>
public sealed class LocalisationTests : IDisposable
{
    /// <summary>
    /// The whole window built in Chinese: every menu, panel and status hint the app writes at startup
    /// goes through the language without throwing, and the frame is kept as a screenshot so a layout
    /// that cannot hold the shorter or wider words is visible rather than silent.
    /// </summary>
    [AvaloniaFact]
    public void The_window_builds_in_Chinese()
    {
        Loc.Apply("zh-CN");
        try
        {
            var window = new MainWindow { Width = 1400, Height = 900 };
            window.Show();
            var session = EditorSession.NewCanvas(900, 600, SKColors.White);
            window.AddSession(session);
            Dispatcher.UIThread.RunJobs();
            Assert.True(Screenshots.Save(window, "localised-window-zh-CN"), "the window drew no frame");
            window.Close();
        }
        finally { Loc.Apply(Loc.English); }
    }

    public void Dispose() => Loc.Apply(Loc.English);

    [Fact]
    public void A_name_with_no_translation_reads_as_itself()
    {
        Loc.Apply("zh-CN");
        Assert.Equal("A name no resource carries", Loc.T("A name no resource carries"));
    }

    [Fact]
    public void Chinese_replaces_the_words_a_person_reads()
    {
        Loc.Apply("zh-CN");
        Assert.Equal("新建画布…", Loc.T("New Canvas…"));
        Assert.Equal("图层", Loc.T("Layers"));
        // A menu header carries Avalonia's accelerator marker; the entry is filed under that spelling
        // and brings its own, so the marker never reaches the screen.
        Assert.Equal("文件(&F)", Loc.T("_File"));
    }

    [Fact]
    public void English_puts_everything_back()
    {
        Loc.Apply("zh-CN");
        var han = Loc.T("Undo");
        Loc.Apply(Loc.English);
        Assert.NotEqual(han, Loc.T("Undo"));
        Assert.Equal("Undo", Loc.T("Undo"));
    }

    [Fact]
    public void An_unknown_language_falls_back_to_English()
    {
        Loc.Apply("de");
        Assert.Equal("en", Loc.Culture.TwoLetterISOLanguageName);
        Assert.False(Loc.IsLocalised);
        Assert.Equal("New Canvas…", Loc.T("New Canvas…"));
    }

    [Fact]
    public void A_blend_mode_keeps_its_english_name_for_an_agent()
    {
        // The MCP tool parses what an agent sends against DisplayName, so the model's display name
        // stays English in every language; only what the Layers panel draws goes through Loc.T.
        Loc.Apply("zh-CN");
        Assert.Equal("Multiply", BlendMode.Multiply.DisplayName());
        Assert.Equal("Soft Light", BlendMode.SoftLight.DisplayName());
        Assert.NotEqual("Multiply", Loc.T(BlendMode.Multiply.DisplayName()));
    }

    [Fact]
    public void A_format_fills_its_placeholders_in_the_active_language()
    {
        Loc.Apply("zh-CN");
        var undo = Loc.Format("Undo {0}", Loc.T("Crop"));
        Assert.DoesNotContain("{", undo);
        Assert.Equal("撤销裁切", undo);
        Loc.Apply(Loc.English);
        Assert.Equal("Undo Crop", Loc.Format("Undo {0}", "Crop"));
    }

    [Fact]
    public void Every_offered_language_resolves_to_a_real_culture()
    {
        foreach (var (code, _) in Loc.Available)
        {
            Loc.Apply(code);
            Assert.NotNull(Loc.Culture);
            Assert.False(string.IsNullOrEmpty(Loc.Culture.Name) && code != Loc.SystemLanguage);
        }
    }

}
