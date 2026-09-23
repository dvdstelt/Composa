using static Composa.App.AppPaths;

namespace Composa.App.Tests;

public class AppPathsTests
{
    private static string Folder(Environment.SpecialFolder folder) => folder switch
    {
        Environment.SpecialFolder.UserProfile => "/home/ada",
        Environment.SpecialFolder.ApplicationData => @"C:\Users\ada\AppData\Roaming",
        Environment.SpecialFolder.LocalApplicationData => @"C:\Users\ada\AppData\Local",
        _ => throw new ArgumentException($"{folder} is not a folder Composa should use"),
    };

    private static Func<string, string?> Variables(params (string Name, string Value)[] set) => name => set.FirstOrDefault(v => v.Name == name).Value;

    /// <summary>Exactly what every Linux build before this one used, so nobody's preferences go missing on an update.</summary>
    [Fact]
    public void Linux_follows_xdg_and_falls_back_to_the_home_directory()
    {
        Assert.Equal(new Folders(Path.Combine("/home/ada", ".config", "composa"), Path.Combine("/home/ada", ".cache", "composa")),
            For(Platform.Linux, Variables(), Folder));
        Assert.Equal(new Folders(Path.Combine("/xdg/config", "composa"), Path.Combine("/xdg/cache", "composa")),
            For(Platform.Linux, Variables(("XDG_CONFIG_HOME", "/xdg/config"), ("XDG_CACHE_HOME", "/xdg/cache")), Folder));
    }

    /// <summary>Preferences roam with the profile; recovery copies, which can be large and are worthless elsewhere, do not.</summary>
    [Fact]
    public void Windows_keeps_preferences_roaming_and_the_cache_local()
    {
        var folders = For(Platform.Windows, Variables(("XDG_CONFIG_HOME", "/ignored")), Folder);
        Assert.Equal(Path.Combine(@"C:\Users\ada\AppData\Roaming", "Composa"), folders.Config);
        Assert.Equal(Path.Combine(@"C:\Users\ada\AppData\Local", "Composa"), folders.Cache);
    }

    [Fact]
    public void MacOS_uses_application_support_and_caches()
    {
        var folders = For(Platform.MacOS, Variables(("XDG_CONFIG_HOME", "/ignored")), Folder);
        Assert.Equal(Path.Combine("/home/ada", "Library", "Application Support", "Composa"), folders.Config);
        Assert.Equal(Path.Combine("/home/ada", "Library", "Caches", "Composa"), folders.Cache);
    }
}
