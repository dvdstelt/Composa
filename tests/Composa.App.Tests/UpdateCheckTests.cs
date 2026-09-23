using Composa.App;

namespace Composa.App.Tests;

public class ReleaseVersionTests
{
    private static ReleaseVersion V(string text)
    {
        Assert.True(ReleaseVersion.TryParse(text, out var version), $"could not parse {text}");
        return version;
    }

    [Theory]
    [InlineData("1.2.3", 1, 2, 3, "")]
    [InlineData("v1.2.3", 1, 2, 3, "")]
    [InlineData("0.2.0-alpha.0.7", 0, 2, 0, "alpha.0.7")]
    [InlineData("v0.2.0-beta.1+1dea66ea", 0, 2, 0, "beta.1")] // Build metadata is not part of a version.
    public void Tags_parse(string text, int major, int minor, int patch, string pre)
        => Assert.Equal(new ReleaseVersion(major, minor, patch, pre), V(text));

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("nightly")]
    [InlineData("1.2.x")]
    [InlineData("1.2.3-")]
    [InlineData("-1.2.3")]
    public void Nonsense_does_not_parse(string text) => Assert.False(ReleaseVersion.TryParse(text, out _));

    [Fact]
    public void Ordering_follows_semver()
    {
        Assert.True(V("1.0.0").CompareTo(V("0.9.9")) > 0);
        Assert.True(V("0.3.0").CompareTo(V("0.2.9")) > 0);
        Assert.True(V("0.2.1").CompareTo(V("0.2.0")) > 0);
        Assert.Equal(0, V("1.2.3").CompareTo(V("v1.2.3+abc")));

        // A pre-release comes before the release it leads to, which is the case a naive string
        // comparison gets backwards.
        Assert.True(V("1.0.0").CompareTo(V("1.0.0-beta")) > 0);
        Assert.True(V("1.0.0-alpha").CompareTo(V("1.0.0-beta")) < 0);
        Assert.True(V("1.0.0-alpha.1").CompareTo(V("1.0.0-alpha")) > 0);
        Assert.True(V("1.0.0-alpha.2").CompareTo(V("1.0.0-alpha.10")) < 0); // Numeric, not textual.
        Assert.True(V("1.0.0-alpha.beta").CompareTo(V("1.0.0-alpha.2")) > 0); // Text outranks numeric.
    }
}

public class UpdateCheckTests
{
    private sealed class Source(ReleaseInfo? release = null, Exception? throws = null) : IReleaseSource
    {
        public int Calls;
        public Task<ReleaseInfo?> Latest(CancellationToken cancel)
        {
            Calls++;
            if (throws != null) return Task.FromException<ReleaseInfo?>(throws);
            return Task.FromResult(release);
        }
    }

    private static ReleaseInfo Release(string tag, bool pre = false)
        => new(tag, "https://github.com/dvdstelt/Composa/releases/tag/" + tag, pre);

    private static Settings Fresh() => new() { CheckForUpdates = true };

    /// <summary>
    /// What this build believes it is. Derived, never hardcoded: MinVer takes it from the nearest
    /// tag, so it changes with every release and a literal here would rot on the next one.
    /// </summary>
    private static ReleaseVersion Running
    {
        get
        {
            Assert.True(ReleaseVersion.TryParse(AppInfo.Version, out var running), $"AppInfo.Version is not a version: {AppInfo.Version}");
            return running;
        }
    }

    /// <summary>A version comfortably ahead of whatever this build is, whatever it is.</summary>
    private static string Newer(string? pre = null)
        => $"v{Running.Major + 1}.0.0" + (pre is null ? "" : "-" + pre);

    [Fact]
    public async Task A_newer_release_is_offered()
    {
        var result = await new UpdateCheck(new Source(Release(Newer())), Fresh()).Run(manual: false);
        Assert.Equal(UpdateOutcome.Available, result.Outcome);
        Assert.True(result.ShouldNotify);
        Assert.Equal($"{Running.Major + 1}.0.0", result.Version.ToString());
    }

    [Fact]
    public async Task The_same_or_an_older_release_is_not()
    {
        var same = await new UpdateCheck(new Source(Release("v" + Running)), Fresh()).Run(manual: false);
        Assert.Equal(UpdateOutcome.UpToDate, same.Outcome);

        var older = await new UpdateCheck(new Source(Release($"v{Running.Major}.0.0-alpha.1")), Fresh()).Run(manual: false);
        Assert.Equal(UpdateOutcome.UpToDate, older.Outcome);
    }

    [Fact]
    public async Task A_stable_build_is_never_offered_a_prerelease()
    {
        Assert.False(Running.IsPreRelease, "this test needs a stable running version");
        var result = await new UpdateCheck(new Source(Release(Newer("beta.1"), pre: true)), Fresh()).Run(manual: false);
        Assert.Equal(UpdateOutcome.UpToDate, result.Outcome);
    }

    [Fact]
    public async Task An_automatic_check_asks_at_most_once_a_day()
    {
        var settings = Fresh();
        var source = new Source(Release(Newer()));
        var clock = DateTime.UtcNow;
        var check = new UpdateCheck(source, settings, () => clock);

        Assert.Equal(UpdateOutcome.Available, (await check.Run(manual: false)).Outcome);
        Assert.Equal(1, source.Calls);

        clock += TimeSpan.FromHours(23);
        Assert.Equal(UpdateOutcome.TooSoon, (await check.Run(manual: false)).Outcome);
        Assert.Equal(1, source.Calls); // No second request.

        // A manual check ignores the interval entirely, and counts as a check: having just asked
        // the server, there is no reason for an automatic check to ask again an hour later.
        Assert.Equal(UpdateOutcome.Available, (await check.Run(manual: true)).Outcome);
        Assert.Equal(2, source.Calls);

        clock += TimeSpan.FromHours(2);
        Assert.Equal(UpdateOutcome.TooSoon, (await check.Run(manual: false)).Outcome);
        Assert.Equal(2, source.Calls);

        clock += TimeSpan.FromHours(23);
        Assert.Equal(UpdateOutcome.Available, (await check.Run(manual: false)).Outcome);
        Assert.Equal(3, source.Calls);
    }

    [Fact]
    public async Task A_skipped_version_stays_quiet_but_a_later_one_does_not()
    {
        var settings = Fresh();
        settings.SkippedVersion = $"{Running.Major + 1}.0.0";

        var skipped = await new UpdateCheck(new Source(Release(Newer())), settings).Run(manual: false);
        Assert.Equal(UpdateOutcome.Skipped, skipped.Outcome);
        Assert.False(skipped.ShouldNotify);

        settings.LastUpdateCheck = null;
        var later = await new UpdateCheck(new Source(Release($"v{Running.Major + 2}.0.0")), settings).Run(manual: false);
        Assert.Equal(UpdateOutcome.Available, later.Outcome);

        // Asking explicitly reports it even if that version was skipped.
        settings.LastUpdateCheck = null;
        var asked = await new UpdateCheck(new Source(Release(Newer())), settings).Run(manual: true);
        Assert.Equal(UpdateOutcome.Available, asked.Outcome);
    }

    [Fact]
    public async Task Turning_it_off_stops_the_automatic_check_only()
    {
        var settings = Fresh();
        settings.CheckForUpdates = false;
        var source = new Source(Release(Newer()));
        var check = new UpdateCheck(source, settings);

        Assert.Equal(UpdateOutcome.Disabled, (await check.Run(manual: false)).Outcome);
        Assert.Equal(0, source.Calls); // Nothing reaches the network.

        Assert.Equal(UpdateOutcome.Available, (await check.Run(manual: true)).Outcome);
    }

    [Fact]
    public async Task A_network_failure_is_reported_rather_than_thrown()
    {
        var source = new Source(throws: new HttpRequestException("no route to host"));
        var result = await new UpdateCheck(source, Fresh()).Run(manual: false);
        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
        Assert.False(result.ShouldNotify);
    }

    [Fact]
    public async Task A_tag_that_makes_no_sense_is_a_failure_not_a_crash()
    {
        var result = await new UpdateCheck(new Source(Release("nightly")), Fresh()).Run(manual: false);
        Assert.Equal(UpdateOutcome.Failed, result.Outcome);
    }

    /// <summary>A developer build is not a package, so it checks; a .deb or .rpm build does not.</summary>
    [Fact]
    public void A_build_with_no_channel_set_checks_by_itself()
    {
        Assert.Equal(UpdateChannel.GitHub, UpdateCheck.Channel);
        Assert.True(new UpdateCheck(new Source(), Fresh()).RunsAutomatically);
    }
}
