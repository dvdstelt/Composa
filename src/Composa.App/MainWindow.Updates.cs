using Composa.App.Dialogs;
namespace Composa.App;

/// <summary>
/// Telling the user that a newer version exists. Nothing here downloads or installs anything: the
/// most it does is offer to open the release page in a browser.
/// </summary>
public sealed partial class MainWindow
{
    private UpdateCheck NewUpdateCheck() => new(new GitHubReleaseSource(), settings);

    /// <summary>
    /// Started once the window is up, on a background task, and never awaited. A slow or
    /// unreachable network must not hold up a single frame of startup, and an automatic check that
    /// fails says nothing at all.
    /// </summary>
    private void StartUpdateCheck()
    {
        var check = NewUpdateCheck();
        if (!check.RunsAutomatically) return;

        updateNotice.OpenReleasePage += () => UpdateNotice.OpenInBrowser(pendingUpdateUrl ?? AppInfo.ReleasesUrl);
        updateNotice.Skip += () =>
        {
            settings.SkippedVersion = pendingUpdateVersion?.ToString();
            settings.Save();
        };

        UpdateNotice.RunInBackground(check, result =>
        {
            if (!result.ShouldNotify) return;
            pendingUpdateVersion = result.Version;
            pendingUpdateUrl = result.Url;
            updateNotice.Show(result.Version);
        });
    }

    private ReleaseVersion? pendingUpdateVersion;
    private string? pendingUpdateUrl;

    /// <summary>Help &gt; Check for Updates. Unlike the automatic check, this always reports what happened.</summary>
    private async Task CheckForUpdatesNow()
    {
        if (UpdateCheck.Channel == UpdateChannel.Managed)
        {
            await Prompts.Alert(this, "Check for Updates",
                $"Composa {AppInfo.Version} was installed through your package manager, which is where updates come from. " +
                "Use it to upgrade, rather than downloading a build that it does not know about.");
            return;
        }

        var result = await NewUpdateCheck().Run(manual: true);
        switch (result.Outcome)
        {
            case UpdateOutcome.Available:
                pendingUpdateVersion = result.Version;
                pendingUpdateUrl = result.Url;
                updateNotice.Show(result.Version);
                break;
            case UpdateOutcome.Failed:
                // Someone who asked deserves an answer, even when the answer is that it did not work.
                await Prompts.Alert(this, "Check for Updates",
                    "Could not reach the release page to check for a newer version. Please try again later.");
                break;
            default:
                await Prompts.Alert(this, "Check for Updates", $"Composa {AppInfo.Version} is the latest version.");
                break;
        }
    }
}
