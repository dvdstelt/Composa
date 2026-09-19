using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Compositor.App.Controls;
using Compositor.Editing;
using SkiaSharp;

namespace Compositor.App;

public sealed partial class MainWindow : Window
{
    private readonly List<EditorSession> sessions = [];
    private EditorSession? session;
    private readonly CanvasView canvas = new();
    private readonly LayersPanel layers = new() { Width = 296 };
    private readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly Border optionsHost = new() { Height = 40, Background = Palette.Panel, Padding = new Thickness(12, 0) };
    private readonly Dictionary<Tool, ToggleButton> toolButtons = [];
    private readonly TextBlock zoomText = new() { Width = 56, Foreground = Palette.Secondary };
    private readonly TextBlock sizeText = new() { Foreground = Palette.Secondary };
    private readonly TextBlock hintText = new() { Foreground = Palette.Secondary, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border foregroundSwatch = new() { Width = 26, Height = 26, BorderBrush = Brushes.White, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(3) };
    private readonly Border backgroundSwatch = new() { Width = 26, Height = 26, BorderBrush = Brushes.White, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(3) };
    private readonly Panel welcome;
    private Action? refreshOptions;
    private readonly Settings settings = Settings.Load();
    private readonly Recovery? recovery = Settings.Persist ? new Recovery() : null;
    private string? problem;

    public MainWindow()
    {
        Title = "Compositor";
        Width = Math.Clamp(settings.WindowWidth, 800, 10000);
        Height = Math.Clamp(settings.WindowHeight, 520, 10000);
        if (settings.Maximized) WindowState = WindowState.Maximized;
        canvas.ShowPixelGrid = settings.ShowPixelGrid;
        jpegQuality = Math.Clamp(settings.JpegQuality, 1, 100);
        MinWidth = 800;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://compositor/Assets/icon.png")));

        welcome = BuildWelcome();
        var canvasHost = new Panel { Children = { canvas, welcome } };
        var center = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto") };
        center.Children.Add(BuildToolRail());
        AddAt(center, Ui.Separator(), 1).Margin = new Thickness(0);
        AddAt(center, canvasHost, 2);
        AddAt(center, Ui.Separator(), 3).Margin = new Thickness(0);
        AddAt(center, layers, 4);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto,Auto") };
        root.Children.Add(BuildMenu());
        AddRow(root, BuildTabBar(), 1);
        AddRow(root, optionsHost, 2);
        AddRow(root, Ui.Separator(false), 3);
        AddRow(root, center, 4);
        AddRow(root, Ui.Separator(false), 5);
        AddRow(root, BuildStatusBar(), 6);
        Content = root;

        canvas.ViewChanged += UpdateStatus;
        canvas.Problem += message => { problem = message; UpdateStatus(); };
        canvas.ToolStateChanged += () => { refreshOptions?.Invoke(); UpdateColors(); };
        canvas.TextRequested += (at, existing) => _ = EditText(at, existing);
        layers.EditTextRequested += layer => _ = EditText(default, layer);
        layers.EditAdjustmentRequested += layer => _ = EditAdjustmentLayer(layer, isNew: false);
        layers.NewAdjustmentRequested += kind => _ = NewAdjustmentLayer(kind);

        AddHandler(KeyDownEvent, OnWindowKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) => { if (!SwallowAlt(e)) canvas.HandleKeyUp(e); }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);
        Closing += OnClosing;

        SetSession(null);

        if (recovery != null)
        {
            var autosave = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
            autosave.Tick += (_, _) => recovery.Save(sessions);
            autosave.Start();
            Opened += (_, _) => _ = OfferRecovery();
        }
    }

    private async Task OfferRecovery()
    {
        var abandoned = recovery!.FindAbandoned();
        if (abandoned.Count == 0) return;
        var names = string.Join("\n", abandoned.Select(e => $"• {e.Title} (autosaved {e.SavedAt:g})"));
        var recover = await Dialogs.Prompts.Confirm(this, "Recover Unsaved Work",
            $"Compositor did not close normally last time. These documents had unsaved changes:\n\n{names}\n\nRecover them? Choosing Cancel discards the autosaved copies.", "Recover");
        foreach (var entry in abandoned)
        {
            if (recover)
            {
                try
                {
                    var restored = new EditorSession(Compositor.IO.ProjectFile.Load(entry.ProjectPath)) { SuggestedName = entry.Title + " (recovered)" };
                    restored.MarkModified();
                    AddSession(restored);
                }
                catch (Exception error)
                {
                    await Dialogs.Prompts.Alert(this, "Couldn't recover " + entry.Title, error.Message + "\n\nThe autosaved copy was kept at " + entry.ProjectPath);
                    continue;
                }
            }
            recovery.Discard(entry);
        }
    }

    private static T AddAt<T>(Grid grid, T control, int column) where T : Control
    {
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
        return control;
    }

    private static void AddRow(Grid grid, Control control, int row)
    {
        Grid.SetRow(control, row);
        grid.Children.Add(control);
    }

    public EditorSession? Session => session;
    public CanvasView Canvas => canvas;

    // ---- Sessions and tabs --------------------------------------------------------------------------------------

    public void AddSession(EditorSession added)
    {
        sessions.Add(added);
        added.HistoryChanged += RebuildTabs;
        added.Problem += message => { if (added == session) ShowProblem(message); };
        added.LayersChanged += () => { if (added == session) OnSessionLayersChanged(); };
        SetSession(added);
    }

    private void SetSession(EditorSession? next)
    {
        if (session != null && session != next)
        {
            canvas.CancelInteraction();
            if (session.IsPreviewing) session.CancelPreview();
        }
        var tool = session?.Tool ?? Tool.Move;
        session = next;
        if (session != null) CarryToolState(session, tool);
        canvas.Session = session;
        layers.Session = session;
        welcome.IsVisible = session == null;
        RebuildTabs();
        RebuildOptions();
        UpdateColors();
        UpdateStatus();
        if (session != null) canvas.Focus();
    }

    private EditorSession? lastToolSource;

    /// <summary>Tool choice, colors and brush settings follow the user from tab to tab.</summary>
    private void CarryToolState(EditorSession target, Tool tool)
    {
        if (lastToolSource is { } from && from != target)
        {
            target.Foreground = from.Foreground; target.Background = from.Background; target.Brush = from.Brush;
            target.EraserMode = from.EraserMode; target.SmearMode = from.SmearMode; target.MarqueeKind = from.MarqueeKind; target.LassoKind = from.LassoKind;
            target.Feather = from.Feather; target.WandTolerance = from.WandTolerance; target.WandContiguous = from.WandContiguous;
            target.SampleAllLayers = from.SampleAllLayers; target.CloneAligned = from.CloneAligned; target.ShapeKind = from.ShapeKind;
            target.ShapeCornerRadius = from.ShapeCornerRadius; target.GradientRadial = from.GradientRadial; target.GradientToTransparent = from.GradientToTransparent; target.TextDefaults = from.TextDefaults;
            target.Tool = tool;
        }
        lastToolSource = target;
        foreach (var (key, button) in toolButtons) button.IsChecked = key == target.Tool;
    }

    private void RebuildTabs()
    {
        tabs.Children.Clear();
        foreach (var item in sessions)
        {
            var label = Ui.Label(item.Title + (item.IsModified ? " •" : ""), item == session ? Palette.Foreground : Palette.Secondary);
            var close = new Button { Classes = { "flat" }, Padding = new Thickness(3), Content = Icons.Create(Icons.Close, 10), VerticalAlignment = VerticalAlignment.Center };
            close.Click += (_, e) => { _ = CloseSession(item); e.Handled = true; };
            var tab = new Border
            {
                Child = Ui.Row(8, label, close), Padding = new Thickness(12, 4, 6, 4), CornerRadius = new CornerRadius(6),
                Background = item == session ? Palette.PanelRaised : Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand)
            };
            tab.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(tab).Properties.IsMiddleButtonPressed) _ = CloseSession(item);
                else if (item != session) SetSession(item);
            };
            tabs.Children.Add(tab);
        }
        Title = session == null ? "Compositor" : $"{session.Title}{(session.IsModified ? " •" : "")} - Compositor";
    }

    private async Task<bool> CloseSession(EditorSession item)
    {
        if (item.IsModified)
        {
            if (item != session) SetSession(item);
            var answer = await Dialogs.Prompts.SaveChanges(this, item.Title);
            if (answer == null) return false;
            if (answer == true && !await Save(item, saveAs: false)) return false;
        }
        var index = sessions.IndexOf(item);
        sessions.Remove(item);
        recovery?.Forget(item);
        item.HistoryChanged -= RebuildTabs;
        if (lastToolSource == item) lastToolSource = null;
        if (item == session) SetSession(sessions.Count == 0 ? null : sessions[Math.Clamp(index, 0, sessions.Count - 1)]);
        else RebuildTabs();
        GC.Collect();
        return true;
    }

    private bool closingConfirmed;

    private void RememberWindow()
    {
        settings.Maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal) { settings.WindowWidth = Width; settings.WindowHeight = Height; }
        settings.ShowPixelGrid = canvas.ShowPixelGrid;
        settings.JpegQuality = jpegQuality;
        settings.Save();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        RememberWindow();
        if (closingConfirmed || sessions.All(s => !s.IsModified)) return;
        e.Cancel = true;
        foreach (var item in sessions.Where(s => s.IsModified).ToList())
            if (!await CloseSession(item)) return;
        closingConfirmed = true;
        Close();
    }

    // ---- Chrome -------------------------------------------------------------------------------------------------

    private Control BuildTabBar()
    {
        var newButton = Ui.IconButton(Icons.Plus, "New canvas (Ctrl+N)", () => _ = NewCanvas());
        var scroll = new ScrollViewer { Content = tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var zoomControls = Ui.Row(2,
            Ui.TextButton("Fit", () => canvas.Fit()), Ui.TextButton("100%", () => canvas.ZoomTo(1)),
            Ui.IconButton(Icons.ZoomOut, "Zoom out (Ctrl+-)", canvas.ZoomOut), Ui.IconButton(Icons.ZoomIn, "Zoom in (Ctrl++)", canvas.ZoomIn));
        foreach (var button in zoomControls.Children.OfType<Button>()) { button.MinWidth = 0; button.Classes.Add("flat"); }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Background = Palette.Window, Margin = new Thickness(6, 2) };
        grid.Children.Add(newButton);
        AddAt(grid, scroll, 1).Margin = new Thickness(6, 0);
        AddAt(grid, zoomControls, 2);
        return grid;
    }

    private static readonly (Tool Tool, Icons.Icon Icon, string Tip)[] ToolList =
    [
        (Tool.Move, Icons.Move, "Move / Transform (V)"), (Tool.Marquee, Icons.Marquee, "Marquee (M) · press again for Ellipse"),
        (Tool.Lasso, Icons.Lasso, "Lasso (L) · press again for Polygonal"), (Tool.Wand, Icons.Wand, "Magic Wand (W)"), (Tool.Crop, Icons.Crop, "Crop (C)"),
        (Tool.Brush, Icons.Brush, "Brush (B) · Eraser (E)"), (Tool.SpotHealing, Icons.Heal, "Spot Healing Brush (J)"),
        (Tool.CloneStamp, Icons.Stamp, "Clone Stamp (S) · Alt-click sets the source"), (Tool.Smear, Icons.Drop, "Smear: Liquify, Blur, Smudge, Dodge, Burn (R)"),
        (Tool.Gradient, Icons.Gradient, "Gradient (G)"), (Tool.Shape, Icons.Shape, "Shape (U) · Shift+U switches shape"),
        (Tool.Text, Icons.Text, "Text (T) · click to add, click text to edit it"), (Tool.Eyedropper, Icons.Eyedropper, "Eyedropper (I)"), (Tool.Hand, Icons.Hand, "Hand (H) · hold Space with any tool"), (Tool.Zoom, Icons.Zoom, "Zoom (Z)")
    ];

    private Control BuildToolRail()
    {
        var rail = new StackPanel { Spacing = 2, Margin = new Thickness(0, 8, 0, 8), HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var (tool, icon, tip) in ToolList)
        {
            var button = new ToggleButton { Classes = { "tool" }, Content = Icons.Create(icon, 19) };
            ToolTip.SetTip(button, tip);
            button.Click += (_, _) => SelectTool(tool);
            toolButtons[tool] = button;
            rail.Children.Add(button);
        }

        foregroundSwatch.Cursor = backgroundSwatch.Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(foregroundSwatch, "Foreground color");
        ToolTip.SetTip(backgroundSwatch, "Background color");
        foregroundSwatch.PointerPressed += (_, _) => _ = PickColor(foreground: true);
        backgroundSwatch.PointerPressed += (_, _) => _ = PickColor(foreground: false);
        backgroundSwatch.Margin = new Thickness(14, 14, 0, 0);
        foregroundSwatch.HorizontalAlignment = backgroundSwatch.HorizontalAlignment = HorizontalAlignment.Left;
        foregroundSwatch.VerticalAlignment = backgroundSwatch.VerticalAlignment = VerticalAlignment.Top;
        var swatches = new Panel { Width = 42, Height = 42, Margin = new Thickness(0, 8, 0, 0), Children = { backgroundSwatch, foregroundSwatch } };
        rail.Children.Add(swatches);
        var swap = Ui.IconButton(Icons.Swap, "Swap colors (X) · D resets to black and white", () => { session?.SwapColors(); UpdateColors(); }, 14);
        swap.HorizontalAlignment = HorizontalAlignment.Center;
        rail.Children.Add(swap);

        return new ScrollViewer
        {
            Content = rail, Width = 56, Background = Palette.Panel, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    private Control BuildStatusBar()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), Height = 28, Background = Palette.Panel };
        zoomText.Margin = new Thickness(14, 0, 8, 0);
        sizeText.Margin = new Thickness(0, 0, 24, 0);
        hintText.HorizontalAlignment = HorizontalAlignment.Right;
        hintText.Margin = new Thickness(0, 0, 14, 0);
        grid.Children.Add(zoomText);
        AddAt(grid, sizeText, 1);
        AddAt(grid, hintText, 2);
        foreach (var text in new[] { zoomText, sizeText, hintText }) text.FontSize = 11.5;
        return grid;
    }

    private Panel BuildWelcome()
    {
        var title = Ui.Label("Compositor", size: 26, weight: FontWeight.SemiBold);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        var subtitle = Ui.Label("Create a canvas, open a project or image, or drop files here.", Palette.Secondary);
        subtitle.HorizontalAlignment = HorizontalAlignment.Center;
        var buttons = Ui.Row(10, Ui.TextButton("New Canvas…", () => _ = NewCanvas(), accent: true), Ui.TextButton("Open…", () => _ = Open()));
        buttons.HorizontalAlignment = HorizontalAlignment.Center;
        var box = Ui.Column(14, title, subtitle, buttons);
        var recent = settings.RecentFiles.Where(p => File.Exists(p) || Directory.Exists(p)).Take(6).ToList();
        if (recent.Count > 0)
        {
            var heading = Ui.Label("Recent", Palette.Secondary);
            heading.HorizontalAlignment = HorizontalAlignment.Center;
            heading.Margin = new Thickness(0, 18, 0, 0);
            box.Children.Add(heading);
            foreach (var path in recent)
            {
                var link = new Button { Classes = { "flat" }, Content = Ui.Label(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)), Palette.Accent), HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(8, 3) };
                ToolTip.SetTip(link, path);
                link.Click += (_, _) => OpenPaths([path]);
                box.Children.Add(link);
            }
        }
        box.VerticalAlignment = VerticalAlignment.Center;
        return new Panel { Children = { box } };
    }

    private void UpdateColors()
    {
        if (session == null) return;
        foregroundSwatch.Background = new SolidColorBrush(session.Foreground.ToAvalonia());
        backgroundSwatch.Background = new SolidColorBrush(session.Background.ToAvalonia());
    }

    private async Task PickColor(bool foreground)
    {
        if (session == null) return;
        var picked = await Dialogs.Prompts.Color(this, foreground ? "Foreground Color" : "Background Color", foreground ? session.Foreground : session.Background);
        if (picked is not { } color) return;
        if (foreground) session.Foreground = color; else session.Background = color;
        UpdateColors();
    }

    public void SelectTool(Tool tool)
    {
        if (session == null) { foreach (var button in toolButtons.Values) button.IsChecked = false; return; }
        session.Tool = tool;
        problem = null;
        foreach (var (key, button) in toolButtons) button.IsChecked = key == tool;
        toolButtons[Tool.Marquee].Content = Icons.Create(session.MarqueeKind == MarqueeKind.Ellipse ? Icons.MarqueeEllipse : Icons.Marquee, 19);
        toolButtons[Tool.Lasso].Content = Icons.Create(session.LassoKind == LassoKind.Polygonal ? Icons.PolygonLasso : Icons.Lasso, 19);
        toolButtons[Tool.Brush].Content = Icons.Create(session.EraserMode ? Icons.Eraser : Icons.Brush, 19);
        canvas.ToolChanged();
        RebuildOptions();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (session == null)
        {
            zoomText.Text = "";
            sizeText.Text = "";
            hintText.Text = "Ready when you are";
            return;
        }
        zoomText.Text = canvas.Zoom >= 0.1 ? $"{canvas.Zoom * 100:0.#}%" : $"{canvas.Zoom * 100:0.##}%";
        sizeText.Text = $"{session.Document.Width} × {session.Document.Height} px · {session.Document.Resolution:0.#} ppi · sRGB";
        hintText.Text = problem ?? Hint(session);
        hintText.Foreground = problem != null ? new SolidColorBrush(Color.Parse("#FFB454")) : Palette.Secondary;
    }

    private static string Hint(EditorSession s) => s.Tool switch
    {
        Tool.Move => "Drag to move · Handles resize (Shift free, Alt from center) · Outside a corner rotates · Ctrl-drag a corner distorts · Ctrl-click picks a layer · 1–0 opacity",
        Tool.Marquee => "Drag to select · Shift add · Alt subtract · Shift+Alt intersect · Drag inside to move · Delete clears · Ctrl+D deselect",
        Tool.Lasso => s.LassoKind == LassoKind.Freehand ? "Drag to select · Shift add · Alt subtract · Drag inside to move" : "Click corners · Click the start, double-click or Enter to close · Backspace removes a corner · Escape cancels",
        Tool.Wand => "Click to select similar colors · Shift add · Alt subtract",
        Tool.Crop => "Drag to crop · Shift keeps proportions · Alt symmetric · Enter applies · Escape cancels",
        Tool.Brush => (s.EraserMode ? "Drag to erase" : "Drag to paint · Alt-click picks a color") + " · Shift-click draws a line · [ ] size · { } hardness · 1–0 opacity",
        Tool.SpotHealing => "Drag over blemishes to heal · [ ] size",
        Tool.CloneStamp => "Alt-click sets the source · Drag to clone · [ ] size · 1–0 opacity",
        Tool.Smear => "Drag to " + (s.SmearMode == SmearMode.Liquify ? "push pixels" : s.SmearMode.ToString().ToLowerInvariant()) + " · [ ] size · 1–0 strength",
        Tool.Gradient => "Drag to draw from foreground to " + (s.GradientToTransparent ? "transparent" : "background") + " · Shift snaps to 45°",
        Tool.Shape => "Drag to draw a shape on a new layer · Shift square · Alt from center",
        Tool.Text => "Click to add text · Click existing text to edit it · Scale it with the Move tool and it stays sharp",
        Tool.Eyedropper => "Click to pick the foreground color · Alt-click for the background",
        Tool.Hand => "Drag to pan · Ctrl+wheel zooms",
        _ => "Click to zoom in · Alt-click to zoom out · Drag right or left to zoom smoothly"
    };

    private bool reportingFailure;

    /// <summary>Tells the user about an unexpected error, once at a time, after putting the editor back into a sane state.</summary>
    public void ReportFailure(Exception error)
    {
        try { canvas.CancelInteraction(); }
        catch (Exception secondary) { Console.Error.WriteLine(secondary); }
        if (reportingFailure) return;
        reportingFailure = true;
        _ = Show();

        async Task Show()
        {
            try
            {
                await Dialogs.Prompts.Alert(this, "Something went wrong",
                    $"{error.GetType().Name}: {error.Message}\n\nThe last action may not have completed. Your document is still open; saving a copy now (File > Save As) is a good idea.");
            }
            finally { reportingFailure = false; }
        }
    }

    public void ShowProblem(string message)
    {
        problem = message;
        UpdateStatus();
    }
}
