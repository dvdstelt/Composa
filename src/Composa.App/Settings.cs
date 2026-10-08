using System.Text.Json;

namespace Composa.App;

/// <summary>Preferences remembered between launches, stored in the platform's config directory (<see cref="AppPaths.Config"/>).</summary>
public sealed class Settings
{
    public List<string> RecentFiles { get; set; } = [];
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;
    public bool Maximized { get; set; }
    public int JpegQuality { get; set; } = 90;
    public bool ShowPixelGrid { get; set; } = true;
    /// <summary>Toggles that belong to the person rather than to a document, kept the way Photoshop keeps its tool options.</summary>
    public bool ShowTransformControls { get; set; } = true;
    public bool AutoSelect { get; set; } = true;
    /// <summary>How the subject is found: by a model, or from the plain backdrop. The Object Selection options bar and the Remove Background dialog set it.</summary>
    public Composa.Vision.SubjectDetect Detect { get; set; } = Composa.Vision.SubjectDetect.Any;
    /// <summary>How Image Size resamples: the last choice made in its dialog.</summary>
    public Composa.Editing.ResampleMode Resample { get; set; } = Composa.Editing.ResampleMode.Automatic;
    public Composa.Editing.ViewOptions View { get; set; } = new();
    /// <summary>The panels under the Layers panel by title: whether each is shown, collapsed to its header, and how tall it is.</summary>
    public Dictionary<string, DockPanelState> Dock { get; set; } = [];
    /// <summary>Rebound shortcuts by command id: a gesture string, or empty for none. Missing entries keep the default.</summary>
    public Dictionary<string, string> Shortcuts { get; set; } = [];

    /// <summary>
    /// The language the interface is drawn in: "auto" follows the operating system, "en" is English,
    /// or a culture code among <see cref="Loc.Available"/>. Names stay English underneath, so this
    /// changes only what is read on screen; a document, a rebound key and an agent's call are untouched.
    /// </summary>
    public string Language { get; set; } = Loc.SystemLanguage;

    /// <summary>Whether the MCP server runs, so an AI agent can drive the editor. Off until someone switches it on.</summary>
    public bool AllowAiControl { get; set; }

    /// <summary>Whether to look for a newer version at launch. The manual check in the Help menu ignores this.</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>When the last automatic check ran, so it happens at most once a day.</summary>
    public DateTime? LastUpdateCheck { get; set; }
    /// <summary>A version the user dismissed. Only that one stays quiet; the next is announced.</summary>
    public string? SkippedVersion { get; set; }

    private static string FilePath => Path.Combine(AppPaths.Config, "settings.json");

    /// <summary>Tests and other hosts switch persistence off so they never touch the user's files.</summary>
    public static bool Persist { get; set; } = true;

    public static Settings Load()
    {
        if (!Persist) return new Settings();
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings() : new Settings(); }
        catch { return new Settings(); } // A damaged settings file only costs the remembered preferences.
    }

    public void Save()
    {
        if (!Persist) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* Preferences are a convenience; failing to store them must not interrupt editing. */ }
    }

    public void AddRecent(string path)
    {
        RecentFiles.Remove(path);
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 12) RecentFiles.RemoveRange(12, RecentFiles.Count - 12);
        Save();
    }
}

/// <summary>How one panel of the side dock was left: shown or not, collapsed to its header or not, and its height when open.</summary>
public sealed record DockPanelState(bool Visible = true, bool Collapsed = false, double Height = 220);
