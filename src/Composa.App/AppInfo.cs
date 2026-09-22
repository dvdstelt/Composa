using System.Reflection;

namespace Composa.App;

/// <summary>
/// What this build calls itself. The version comes from MinVer by way of the informational version
/// attribute, so it is derived from the git tag and never hand-edited; the About dialog and the
/// update check both read it here so there is one answer rather than two.
/// </summary>
public static class AppInfo
{
    public const string Name = "Composa";

    /// <summary>The release URL a user is sent to when a newer version exists.</summary>
    public const string ReleasesUrl = "https://github.com/dvdstelt/Composa/releases";

    /// <summary>
    /// The version without build metadata: MinVer writes <c>0.2.0+&lt;sha&gt;</c>, and the commit hash is
    /// noise in a dialog and would break a comparison against a release tag.
    /// </summary>
    public static string Version { get; } = Read();

    private static string Read()
    {
        var informational = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational)) return "0.0.0";
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
