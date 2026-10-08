using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Composa.Editing;
using Composa.Filters;
using Composa.Model;

namespace Composa.App.Controls;

/// <summary>
/// The document's history, oldest first: every state it can go back or forward to, named after the step that led to
/// it. A click goes to a state; pressing and dragging along the list scrubs through the states with the canvas
/// following, as a slider field is dragged. The states Redo would bring back are dimmed until an edit drops them, and
/// the state in the file carries a disk. The list never takes the keyboard: Ctrl+Z and Ctrl+Shift+Z step through it,
/// and the canvas keeps its arrow keys.
/// </summary>
public sealed class HistoryPanel : UserControl
{
    public const double RowHeight = 24;

    private EditorSession? session;
    private readonly StackPanel rows = new() { Background = Brushes.Transparent };
    private readonly ScrollViewer scroll;
    private readonly TextBlock dropped = new() { Text = Loc.T("Older steps are no longer kept"), Foreground = Palette.Secondary, FontSize = 11, Margin = new Thickness(10, 0, 10, 4), IsVisible = false };
    private bool scrubbing;
    private int? pendingIndex;
    private bool pendingPosted;

    /// <summary>A click or scrub asks for a state by its index in <see cref="History.Steps"/>; the window decides whether now is a time to move.</summary>
    public event Action<int>? GoToRequested;

    public HistoryPanel()
    {
        scroll = new ScrollViewer { Content = rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var panel = new DockPanel();
        DockPanel.SetDock(dropped, Dock.Top);
        panel.Children.Add(dropped);
        panel.Children.Add(scroll);
        Content = panel;

        // The pointer is captured by the list itself, which outlives the rows it rebuilds as the states change under a scrub.
        rows.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(rows).Properties.IsLeftButtonPressed || IndexAt(e.GetPosition(rows).Y) is not { } index) return;
            scrubbing = true;
            e.Pointer.Capture(rows);
            Request(index);
            e.Handled = true;
        };
        rows.PointerMoved += (_, e) =>
        {
            if (scrubbing && IndexAt(e.GetPosition(rows).Y) is { } index) Request(index);
        };
        rows.PointerReleased += (_, e) =>
        {
            if (!scrubbing) return;
            scrubbing = false;
            e.Pointer.Capture(null);
        };
        rows.PointerCaptureLost += (_, _) => scrubbing = false;
    }

    public EditorSession? Session
    {
        get => session;
        set
        {
            if (session == value) return;
            if (session != null) session.HistoryChanged -= Rebuild;
            session = value;
            if (session != null) session.HistoryChanged += Rebuild;
            Rebuild();
        }
    }

    /// <summary>The row under a point of the list, the first or last one beyond its ends, so a scrub past them stops at the ends.</summary>
    private int? IndexAt(double y)
    {
        if (rows.Children.Count == 0) return null;
        return Math.Clamp((int)Math.Floor(y / RowHeight), 0, rows.Children.Count - 1);
    }

    /// <summary>
    /// Asks for a state once the input and the frame in hand are dealt with, so a fast scrub over a large document
    /// moves once per frame to where the pointer is by then, instead of through every row it crossed.
    /// </summary>
    private void Request(int index)
    {
        pendingIndex = index;
        if (pendingPosted) return;
        pendingPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            pendingPosted = false;
            if (pendingIndex is not { } target) return;
            pendingIndex = null;
            if (session != null && target != session.History.CurrentIndex) GoToRequested?.Invoke(target);
        }, DispatcherPriority.Background);
    }

    private void Rebuild()
    {
        rows.Children.Clear();
        dropped.IsVisible = session?.History.EarlierStepsDropped == true;
        if (session == null) return;
        var steps = session.History.Steps;
        var current = session.History.CurrentIndex;
        Control? currentRow = null;
        for (var i = 0; i < steps.Count; i++)
        {
            var row = Row(steps[i], i == 0, i == current, i > current, session.IsSavedState(steps[i].Id));
            rows.Children.Add(row);
            if (i == current) currentRow = row;
        }
        // A new step lands at the bottom, so the list follows it down.
        if (currentRow != null) Dispatcher.UIThread.Post(() => currentRow.BringIntoView(), DispatcherPriority.Loaded);
    }

    private static Control Row(History.Step step, bool first, bool current, bool ahead, bool saved)
    {
        var brush = ahead ? Palette.Secondary : Palette.Foreground;
        var name = new TextBlock
        {
            Text = Loc.T(step.Name), Foreground = brush, FontStyle = ahead ? FontStyle.Italic : FontStyle.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 0, 0)
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(Icons.Create(first ? Icons.Document : IconFor(step.Name), 14, ahead ? Palette.Secondary : null));
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        if (saved)
        {
            var disk = Icons.Create(Icons.Disk, 12, Palette.Secondary);
            // The icon itself ignores the pointer, so the tip sits on a box around it.
            var mark = new Border { Background = Brushes.Transparent, Child = disk, Margin = new Thickness(6, 0, 0, 0) };
            ToolTip.SetTip(mark, Loc.T("This is the state in the file"));
            Grid.SetColumn(mark, 2);
            grid.Children.Add(mark);
        }
        var row = new Border
        {
            Height = RowHeight, Padding = new Thickness(12, 0, 10, 0), Child = grid, Cursor = new Cursor(StandardCursorType.Hand),
            Background = current ? Palette.Selected : Brushes.Transparent, Tag = step
        };
        if (!current)
        {
            row.PointerEntered += (_, _) => row.Background = Palette.Hover;
            row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        }
        return row;
    }

    private static readonly HashSet<string> AdjustmentNames = Enum.GetValues<AdjustmentKind>().Select(k => Adjustment.Create(k).DisplayName).Append("Auto Levels").ToHashSet();
    private static readonly HashSet<string> FilterNames = Enum.GetValues<FilterKind>().Select(FilterSettings.DisplayName).ToHashSet();
    private static readonly string[] EffectNames = Enum.GetValues<LayerEffectKind>().Select(LayerEffects.DisplayName).ToArray();

    /// <summary>
    /// The icon for a step, from its name. The names are the ones the Edit menu shows after Undo, so they already say
    /// what kind of step each is; a name nothing here knows is a layer step.
    /// </summary>
    public static Icons.Icon IconFor(string name) => name switch
    {
        "Brush" or "Brush Strokes" => Icons.Brush,
        "Eraser" => Icons.Eraser,
        "Clone Stamp" => Icons.Stamp,
        "Spot Healing Brush" or "Content-Aware Fill" => Icons.Heal,
        "Liquify" => Icons.Liquify,
        "Blur" or "Fill" => Icons.Drop,
        "Smudge" => Icons.Smudge,
        "Dodge" => Icons.Dodge,
        "Burn" => Icons.Burn,
        "Gradient" => Icons.Gradient,
        "Rectangle" => Icons.Rectangle,
        "Rounded Rectangle" => Icons.RoundedRectangle,
        "Ellipse" => Icons.Ellipse,
        "Line" => Icons.Line,
        "Rectangular Marquee" => Icons.Marquee,
        "Elliptical Marquee" => Icons.MarqueeEllipse,
        "Lasso" => Icons.Lasso,
        "Magic Wand" => Icons.Wand,
        "Object Selection" or "Select Subject" => Icons.ObjectSelect,
        "Move Selection Pixels" or "Duplicate Selection" => Icons.Move,
        "Deselect" or "Color Range" => Icons.Marquee,
        "Crop" or "Trim" or "Canvas Size" or "Image Size" or "Reveal All" or "Enhance Resolution" => Icons.Crop,
        "Move" or "Nudge" or "Scale" or "Rotate" or "Distort" or "Transform" => Icons.Move,
        _ when name.StartsWith("Select", StringComparison.Ordinal) || name.EndsWith("Selection", StringComparison.Ordinal) => Icons.Marquee,
        _ when name.Contains("Text", StringComparison.Ordinal) => Icons.Text,
        _ when name.Contains("Guide", StringComparison.Ordinal) => Icons.Ruler,
        _ when name.Contains("Mask", StringComparison.Ordinal) => Icons.Mask,
        _ when name.StartsWith("Rotate Canvas", StringComparison.Ordinal) || name.StartsWith("Flip Canvas", StringComparison.Ordinal) => Icons.Crop,
        _ when name.StartsWith("Rotate Layer", StringComparison.Ordinal) || name.StartsWith("Flip Layer", StringComparison.Ordinal) => Icons.Move,
        // Blurs and noise are adjustment layers too, but as a step of their own name they are always the filter: a new
        // adjustment layer is "New Adjustment Layer".
        _ when FilterNames.Contains(name) || EffectNames.Any(effect => name.EndsWith(effect, StringComparison.Ordinal)) => Icons.Effects,
        _ when name.Contains("Adjustment", StringComparison.Ordinal) || AdjustmentNames.Contains(name) => Icons.Adjust,
        _ => Icons.Layers
    };
}
