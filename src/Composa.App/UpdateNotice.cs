using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;

namespace Composa.App;

/// <summary>
/// The strip that appears when a newer version exists. Deliberately not a dialog: nothing is
/// urgent about an update, and interrupting someone's work to say so would be rude.
/// </summary>
/// <remarks>
/// It goes through four states. Available offers Download where there is a file for this install,
/// the release notes, and skipping the version. Downloading shows how far it got, with a thin bar
/// along the bottom and Cancel. Ready offers Show in Folder, Install where the download has an
/// installer, and for a Linux package the terminal command to copy. Failed says why and offers to
/// try again. The strip only raises events; the window does the work.
/// </remarks>
public sealed class UpdateNotice : Border
{
    public enum Phase { Hidden, Available, Downloading, Ready, Failed }

    private readonly TextBlock message = Ui.Label("", Brushes.White);
    private readonly StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button dismiss;
    private readonly Grid commandRow = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 6, 0, 0), IsVisible = false };
    private readonly SelectableTextBlock command = new() { Foreground = Brushes.White, FontFamily = new FontFamily("monospace"), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button copy;
    private readonly Border progress = new() { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, Background = new SolidColorBrush(Colors.White, 0.25), IsVisible = false };
    // The strip is the accent color, so the bar that fills it is white: an accent bar would vanish into it.
    private readonly Border progressFill = new() { Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    private double fraction;

    public UpdateNotice()
    {
        IsVisible = false;
        Background = Palette.Accent;

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        message.VerticalAlignment = VerticalAlignment.Center;
        message.TextTrimming = TextTrimming.CharacterEllipsis;
        row.Children.Add(message);
        buttons.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(buttons, 1);
        row.Children.Add(buttons);
        // Built here rather than through Ui.IconButton, which leaves the icon its default muted
        // foreground: readable on a dark panel, nearly invisible on this one.
        dismiss = new Button { Content = Icons.Create(Icons.Close, 11, Brushes.White), Classes = { "flat" }, Margin = new Thickness(8, 0, 0, 0) };
        ToolTip.SetTip(dismiss, Loc.T("Dismiss"));
        dismiss.Click += (_, _) => Hide();
        Grid.SetColumn(dismiss, 2);
        row.Children.Add(dismiss);

        var hint = Ui.Label(Loc.T("Or in a terminal:"), Brushes.White);
        hint.Margin = new Thickness(0, 0, 8, 0);
        commandRow.Children.Add(hint);
        Grid.SetColumn(command, 1);
        commandRow.Children.Add(command);
        copy = Ui.TextButton("Copy", () => _ = CopyCommand());
        copy.Margin = new Thickness(8, 0, 0, 0);
        ToolTip.SetTip(copy, Loc.T("Copy the command, for a software centre that will not install a downloaded package"));
        Grid.SetColumn(copy, 2);
        commandRow.Children.Add(copy);

        progress.Child = progressFill;
        progress.SizeChanged += (_, _) => LayOutProgress();
        Child = new Panel
        {
            Children =
            {
                new StackPanel { Margin = new Thickness(12, 6), Children = { row, commandRow } },
                progress,
            }
        };
    }

    public Phase State { get; private set; } = Phase.Hidden;

    /// <summary>Opens the release page in a browser.</summary>
    public event Action? OpenReleasePage;

    /// <summary>The user does not want to hear about this particular version again.</summary>
    public event Action? Skip;

    /// <summary>Download pressed, or Retry after a failure.</summary>
    public event Action? Download;

    public event Action? CancelDownload;

    public event Action? ShowInFolder;

    public event Action? Install;

    /// <summary>A newer version exists. <paramref name="canDownload"/> is whether a file for this install can be fetched.</summary>
    public void Show(ReleaseVersion version, bool canDownload)
    {
        Enter(Phase.Available, Loc.Format("Composa {0} is available. You are running {1}.", version, AppInfo.Version));
        if (canDownload) AddButton("Download", () => Download?.Invoke(), Loc.T("Download it into your Downloads folder, checked against the release's checksums"));
        AddButton("Release notes", () => OpenReleasePage?.Invoke());
        AddButton("Skip this version", () => { Skip?.Invoke(); Hide(); });
    }

    public void ShowProgress(ReleaseVersion version, DownloadProgress received)
    {
        if (State != Phase.Downloading)
        {
            Enter(Phase.Downloading, "");
            AddButton("Cancel", () => CancelDownload?.Invoke());
            // A download is not something to dismiss and forget: it is cancelled or it finishes.
            dismiss.IsVisible = false;
            progress.IsVisible = true;
        }
        message.Text = received.Total is > 0 and var total
            ? Loc.Format("Downloading Composa {0}… {1} of {2} MB", version, Megabytes(received.Received), Megabytes(total))
            : Loc.Format("Downloading Composa {0}… {1} MB", version, Megabytes(received.Received));
        fraction = received.Total is > 0 and var whole ? Math.Clamp((double)received.Received / whole, 0, 1) : 0;
        LayOutProgress();
    }

    /// <param name="installHint">What Install does, when the download has an installer; null leaves Install out.</param>
    /// <param name="terminalCommand">The command that installs the package from a terminal, for a .deb or .rpm.</param>
    public void ShowReady(ReleaseVersion version, string path, string? installHint, string? terminalCommand)
    {
        // The folder by its own name, which is how the file manager shows it; the tooltip has the whole path.
        Enter(Phase.Ready, Loc.Format("Composa {0} is ready in {1}: {2}", version, Path.GetFileName(Path.GetDirectoryName(path)), Path.GetFileName(path)));
        ToolTip.SetTip(message, path);
        AddButton("Show in Folder", () => ShowInFolder?.Invoke());
        if (installHint != null) AddButton("Install", () => Install?.Invoke(), installHint);
        if (terminalCommand != null)
        {
            command.Text = terminalCommand;
            copy.Content = Loc.T("Copy");
            commandRow.IsVisible = true;
        }
    }

    public void ShowFailed(string problem)
    {
        Enter(Phase.Failed, problem);
        AddButton("Retry", () => Download?.Invoke());
        AddButton("Release notes", () => OpenReleasePage?.Invoke());
    }

    public void Hide()
    {
        State = Phase.Hidden;
        IsVisible = false;
    }

    /// <summary>The label of every button showing, for tests.</summary>
    public IReadOnlyList<string> Buttons => buttons.Children.OfType<Button>().Select(b => b.Content as string ?? "").ToList();

    public string Message => message.Text ?? "";

    public string? Command => commandRow.IsVisible ? command.Text : null;

    /// <summary>Presses a button by its label, as a click would.</summary>
    public void Press(string label) =>
        buttons.Children.OfType<Button>().Single(b => b.Content as string == label)
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    private void Enter(Phase phase, string text)
    {
        State = phase;
        message.Text = text;
        ToolTip.SetTip(message, text); // A long reason or path is cut off in the strip, but not here.
        buttons.Children.Clear();
        dismiss.IsVisible = true;
        commandRow.IsVisible = false;
        progress.IsVisible = false;
        fraction = 0;
        IsVisible = true;
    }

    private void AddButton(string label, Action click, string? tip = null)
    {
        var button = Ui.TextButton(label, click);
        if (tip != null) ToolTip.SetTip(button, tip);
        buttons.Children.Add(button);
    }

    private void LayOutProgress() => progressFill.Width = progress.Bounds.Width * fraction;

    private async Task CopyCommand()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard || command.Text is not { } text) return;
        await clipboard.SetTextAsync(text);
        copy.Content = Loc.T("Copied");
    }

    /// <summary>Megabytes as GitHub counts them on the release page, a million bytes, to one decimal.</summary>
    public static string Megabytes(long bytes) => (bytes / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Opens a URL with whatever the desktop uses for the job. A failure here is not worth reporting.</summary>
    public static void OpenInBrowser(string url)
    {
        try { using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or System.IO.IOException or InvalidOperationException) { }
    }
}
