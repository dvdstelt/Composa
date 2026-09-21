using System.Text.Json;

namespace Compositor.App;

/// <summary>Preferences remembered between launches, stored under the XDG config directory.</summary>
public sealed class Settings
{
    public List<string> RecentFiles { get; set; } = [];
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;
    public bool Maximized { get; set; }
    public int JpegQuality { get; set; } = 90;
    public bool ShowPixelGrid { get; set; } = true;
    /// <summary>Rebound shortcuts by command id: a gesture string, or empty for none. Missing entries keep the default.</summary>
    public Dictionary<string, string> Shortcuts { get; set; } = [];

    private static string FilePath
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(root, "compositor", "settings.json");
        }
    }

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
