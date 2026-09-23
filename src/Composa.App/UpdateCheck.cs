using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Composa.App;

/// <summary>Where this build came from, which decides whether it should talk about updates at all.</summary>
public enum UpdateChannel
{
    /// <summary>Downloaded from the releases page: nothing else will tell the user a new version exists.</summary>
    GitHub,
    /// <summary>Installed by apt, dnf or similar. The package manager owns updates and this must stay quiet.</summary>
    Managed
}

public sealed record ReleaseInfo(string Tag, string Url, bool PreRelease);

/// <summary>The network half, kept behind an interface so the rules below can be tested without it.</summary>
public interface IReleaseSource
{
    Task<ReleaseInfo?> Latest(CancellationToken cancel);
}

public sealed class GitHubReleaseSource : IReleaseSource
{
    private const string Endpoint = "https://api.github.com/repos/dvdstelt/Composa/releases/latest";

    private sealed record Payload(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("prerelease")] bool PreRelease);

    public async Task<ReleaseInfo?> Latest(CancellationToken cancel)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub refuses a request with no User-Agent. Nothing identifying is sent: no version, no
        // machine details, no identifier of any kind.
        client.DefaultRequestHeaders.Add("User-Agent", "Composa");
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        var payload = await client.GetFromJsonAsync<Payload>(Endpoint, cancel);
        return payload?.TagName is { Length: > 0 } tag && payload.HtmlUrl is { Length: > 0 } url
            ? new ReleaseInfo(tag, url, payload.PreRelease)
            : null;
    }
}

public enum UpdateOutcome { Available, UpToDate, Skipped, Disabled, TooSoon, Failed }

public sealed record UpdateResult(UpdateOutcome Outcome, ReleaseVersion Version = default, string Url = "")
{
    public bool ShouldNotify => Outcome == UpdateOutcome.Available;
}

/// <summary>
/// Reports that a newer version exists. It never downloads or installs anything: it reads one
/// release from the GitHub API and, at most, offers to open the release page in a browser.
/// </summary>
/// <param name="runningVersion">
/// What to compare against, defaulting to this build's own version. Tests pass it explicitly:
/// otherwise every comparison would depend on whether the current commit happens to be tagged, and
/// the same test would exercise a stable version the day of a release and a pre-release the day
/// after.
/// </param>
public sealed class UpdateCheck(IReleaseSource source, Settings settings, Func<DateTime>? now = null, string? runningVersion = null)
{
    private readonly Func<DateTime> now = now ?? (() => DateTime.UtcNow);
    private readonly string runningVersion = runningVersion ?? AppInfo.Version;

    /// <summary>How long an automatic check waits before asking again. A manual check ignores it.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>
    /// Set at build time by the packaging scripts. A .deb or .rpm is built as "managed", because
    /// telling someone to sidestep their package manager is worse than saying nothing.
    /// </summary>
    public static UpdateChannel Channel { get; } =
        typeof(UpdateCheck).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateChannel")?.Value?.Equals("managed", StringComparison.OrdinalIgnoreCase) == true
            ? UpdateChannel.Managed
            : UpdateChannel.GitHub;

    /// <summary>A packager can switch the check off without patching code.</summary>
    public static bool DisabledByEnvironment =>
        Environment.GetEnvironmentVariable("COMPOSA_DISABLE_UPDATE_CHECK") is { Length: > 0 } value &&
        value != "0" && !value.Equals("false", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the check runs by itself at launch. A manual check is always allowed.</summary>
    public bool RunsAutomatically =>
        Channel == UpdateChannel.GitHub && settings.CheckForUpdates && !DisabledByEnvironment;

    public async Task<UpdateResult> Run(bool manual, CancellationToken cancel = default)
    {
        if (!manual)
        {
            if (!RunsAutomatically) return new UpdateResult(UpdateOutcome.Disabled);
            if (settings.LastUpdateCheck is { } last && now() - last < Interval)
                return new UpdateResult(UpdateOutcome.TooSoon);
        }

        // The attempt is recorded before anything can go wrong with it, so that a failure waits its
        // turn like a success does. Otherwise someone offline, or rate limited by GitHub, would
        // send another request on every single launch.
        settings.LastUpdateCheck = now();
        settings.Save();

        ReleaseInfo? latest;
        try { latest = await source.Latest(cancel); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or NotSupportedException or System.Text.Json.JsonException)
        {
            // An automatic check says nothing at all; a manual one owes the person an answer.
            return new UpdateResult(UpdateOutcome.Failed);
        }

        if (latest is null || !ReleaseVersion.TryParse(latest.Tag, out var available))
            return new UpdateResult(UpdateOutcome.Failed);
        if (!ReleaseVersion.TryParse(this.runningVersion, out var running))
            return new UpdateResult(UpdateOutcome.Failed);

        // Someone on a stable build is not offered a pre-release: running 0.3.0 should never be
        // told that 0.4.0-beta.1 is "available". Someone already on a pre-release does get them.
        if (available.IsPreRelease && !running.IsPreRelease) return new UpdateResult(UpdateOutcome.UpToDate);
        if (available.CompareTo(running) <= 0) return new UpdateResult(UpdateOutcome.UpToDate);

        // A skip applies to that version only, so the next one is still announced.
        if (!manual && settings.SkippedVersion == available.ToString())
            return new UpdateResult(UpdateOutcome.Skipped, available, latest.Url);

        return new UpdateResult(UpdateOutcome.Available, available, latest.Url);
    }
}
