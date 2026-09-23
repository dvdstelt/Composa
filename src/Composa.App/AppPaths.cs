namespace Composa.App;

/// <summary>
/// Where Composa keeps its own files, in each platform's own convention: XDG on Linux, the roaming
/// and local application data folders on Windows, and Application Support and Caches on macOS.
/// Settings and crash-recovery copies both ask here, so a platform is added in one place.
/// </summary>
public static class AppPaths
{
    public enum Platform { Linux, Windows, MacOS }

    /// <summary>The config directory (preferences) and the cache directory (recovery copies) for one platform.</summary>
    public readonly record struct Folders(string Config, string Cache);

    private static readonly Folders current = For(
        OperatingSystem.IsWindows() ? Platform.Windows : OperatingSystem.IsMacOS() ? Platform.MacOS : Platform.Linux,
        Environment.GetEnvironmentVariable, Environment.GetFolderPath);

    /// <summary>Preferences: small, and worth carrying to another machine, so Windows keeps them in the roaming profile.</summary>
    public static string Config => current.Config;

    /// <summary>Things that can be lost without harm, which Windows keeps out of the roaming profile.</summary>
    public static string Cache => current.Cache;

    /// <summary>The folders for a platform, given how to read its environment. Separate from the running OS so every platform can be tested anywhere.</summary>
    public static Folders For(Platform platform, Func<string, string?> variable, Func<Environment.SpecialFolder, string> folder)
    {
        var home = folder(Environment.SpecialFolder.UserProfile);
        switch (platform)
        {
            case Platform.Windows:
                return new(Path.Combine(folder(Environment.SpecialFolder.ApplicationData), AppInfo.Name),
                           Path.Combine(folder(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name));
            case Platform.MacOS:
                return new(Path.Combine(home, "Library", "Application Support", AppInfo.Name),
                           Path.Combine(home, "Library", "Caches", AppInfo.Name));
            default:
                // Lowercase, as every other program in ~/.config is.
                var config = variable("XDG_CONFIG_HOME");
                if (string.IsNullOrEmpty(config)) config = Path.Combine(home, ".config");
                var cache = variable("XDG_CACHE_HOME");
                if (string.IsNullOrEmpty(cache)) cache = Path.Combine(home, ".cache");
                return new(Path.Combine(config, "composa"), Path.Combine(cache, "composa"));
        }
    }
}
