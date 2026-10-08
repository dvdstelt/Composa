using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Composa.App.Dialogs;
using Composa.Editing;
using Composa.Filters;
using Composa.Vision;
using Composa.IO;
using Composa.IO.Psd;
using Composa.IO.Xcf;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.App;

public sealed partial class MainWindow
{
    private readonly List<Shortcut> commands = [];
    /// <summary>Keys that pick tools or act on the canvas without a menu entry; letters also work with Shift held.</summary>
    private readonly List<Shortcut> toolKeys = [];
    private readonly List<(MenuItem Item, Shortcut Command)> menuItems = [];
    private MenuItem? undoItem, redoItem, mergeItem, clipItem;
    private readonly List<(MenuItem Item, Func<ViewOptions, bool> Checked)> viewToggles = [];
    /// <summary>The Window menu's checkboxes, by the title of the dock section each shows or hides.</summary>
    private readonly List<(MenuItem Item, string Section)> panelToggles = [];
    private Guid? optionsLayer;
    private int jpegQuality = 90;
    private MenuItem? recentMenu;

    private bool HasDocument => session != null;

    private Menu BuildMenu()
    {
        var menu = new Menu { Background = Palette.Window };
        const KeyModifiers ctrl = KeyModifiers.Control, shift = KeyModifiers.Shift, alt = KeyModifiers.Alt;

        MenuItem Top(string header, params object[] items)
        {
            var top = new MenuItem { Header = Loc.T(header) };
            localisedHeaders.Add((top, header));
            foreach (var item in items) top.Items.Add(item);
            top.SubmenuOpened += (_, _) => RefreshMenuState();
            menu.Items.Add(top);
            return top;
        }
        MenuItem Item(string name, Action run, Key key = Key.None, KeyModifiers modifiers = KeyModifiers.None, Func<bool>? enabled = null, bool needsDocument = true, string? id = null)
        {
            var gesture = key == Key.None ? null : new KeyGesture(key, modifiers);
            var guard = needsDocument ? () => HasDocument && (enabled?.Invoke() ?? true) : enabled;
            var command = new Shortcut(id ?? name.TrimEnd('…'), name.TrimEnd('…'), "Menus", gesture, run, guard);
            // The shortcut keeps its English name: that is what a rebound key is stored under and what the
            // shortcuts window lists. Only the header the person reads goes through the language.
            commands.Add(command);
            var item = new MenuItem { Header = Loc.T(name), InputGesture = gesture };
            localisedHeaders.Add((item, name));
            item.Click += (_, _) => Execute(command);
            command.Item = item;
            menuItems.Add((item, command));
            return item;
        }
        MenuItem Sub(string header, params object[] items)
        {
            var sub = new MenuItem { Header = Loc.T(header) };
            localisedHeaders.Add((sub, header));
            foreach (var item in items) sub.Items.Add(item);
            return sub;
        }
        Separator Line() => new();

        Top("_File",
            Item("New Canvas…", () => _ = NewCanvas(), Key.N, ctrl, needsDocument: false),
            Item("Open…", () => _ = Open(), Key.O, ctrl, needsDocument: false),
            recentMenu = Sub("Open Recent"),
            Item("Place Images as Layers…", () => _ = PlaceImages()),
            Line(),
            Item("Save", () => _ = Save(session!, false), Key.S, ctrl),
            Item("Save As…", () => _ = Save(session!, true), Key.S, ctrl | shift),
            Line(),
            Item("Export PNG…", () => _ = Export(ExportFormat.Png), Key.E, ctrl | shift),
            Item("Export JPEG…", () => _ = Export(ExportFormat.Jpeg), Key.S, ctrl | shift | alt),
            Item("Export WebP…", () => _ = Export(ExportFormat.Webp)),
            Item("Export Look as .cube…", () => _ = ExportLook()),
            Line(),
            Item("Close Project", () => _ = CloseSession(session!), Key.W, ctrl),
            Item("Quit", Close, Key.Q, ctrl, needsDocument: false));

        undoItem = Item("Undo", () => session!.Undo(), Key.Z, ctrl, () => session!.CanUndo);
        redoItem = Item("Redo", () => session!.Redo(), Key.Z, ctrl | shift, () => session!.CanRedo);
        commands.Add(new Shortcut("Redo (Ctrl+Y)", "Redo", "Menus", new KeyGesture(Key.Y, ctrl), () => session!.Redo(), () => HasDocument && session!.CanRedo, hidden: true));
        Top("_Edit", undoItem, redoItem, Line(),
            Item("Cut", () => _ = Cut(), Key.X, ctrl, () => session!.CanCopy),
            Item("Copy", () => _ = Copy(merged: false), Key.C, ctrl, () => session!.CanCopy),
            Item("Copy Merged", () => _ = Copy(merged: true), Key.C, ctrl | shift),
            Item("Paste", () => _ = Paste(), Key.V, ctrl),
            Sub("Paste Special",
                Item("Paste in Place", () => _ = Paste(PasteKind.InPlace), Key.V, ctrl | shift),
                Item("Paste Into", () => _ = Paste(PasteKind.Into), Key.V, ctrl | alt | shift, () => session!.Selection != null)),
            Line(),
            Item("Fill with Foreground Color", () => session!.Fill(session.Foreground, "Fill"), Key.Back, alt, () => session!.CanFill),
            Item("Fill with Background Color", () => session!.Fill(session.Background, "Fill"), Key.Back, ctrl, () => session!.CanFill),
            Item("Clear", DeletePressed, Key.Delete),
            Item("Content-Aware Fill", () => Busy(() => session!.ContentAwareFill()), Key.Back, shift, () => session!.Selection != null && session.CanEditPixels && !session.IsEditingMask),
            Line(),
            Item("Keyboard Shortcuts…", () => _ = ShowShortcuts(), needsDocument: false, id: "Keyboard Shortcuts (Edit menu)"));

        Top("_Select",
            Item("All", () => session!.SelectAll(), Key.A, ctrl),
            Item("Deselect", () => session!.Deselect(), Key.D, ctrl, () => session!.Selection != null),
            Item("Inverse", () => session!.InvertSelection(), Key.I, ctrl | shift),
            Line(),
            Item("Layer's Pixels", () => session!.SelectLayerPixels(session.ActiveLayer!), enabled: () => session!.ActiveLayer?.Pixels != null),
            Item("Layer's Mask", () => session!.SelectLayerMask(session.ActiveLayer!), enabled: () => session!.ActiveLayer?.Mask != null),
            Item("Subject", SelectSubject, Key.A, ctrl | alt),
            Item("Color Range…", ShowColorRange, enabled: () => session!.CanSelectColorRange),
            Line(),
            Item("Expand…", () => _ = ModifySelection("Expand Selection", "Expand by", () => session!.SelectionExpandAmount, 500, v => { session!.SelectionExpandAmount = v; session.ExpandSelection(v); }), enabled: () => session!.Selection != null),
            Item("Contract…", () => _ = ModifySelection("Contract Selection", "Contract by", () => session!.SelectionContractAmount, 500, v => { session!.SelectionContractAmount = v; session.ContractSelection(v); }), enabled: () => session!.Selection != null),
            Item("Feather…", () => _ = ModifySelection("Feather Selection", "Feather radius", () => session!.SelectionFeatherAmount, 250, v => { session!.SelectionFeatherAmount = v; session.FeatherSelection(v); }), Key.F6, shift, () => session!.Selection != null));

        Top("_Image",
            Item("Curves…", () => _ = Adjust(AdjustmentKind.Curves), Key.M, ctrl, () => session!.CanEditPixels),
            Item("Levels…", () => _ = Adjust(AdjustmentKind.Levels), Key.L, ctrl, () => session!.CanEditPixels),
            Item("Hue/Saturation…", () => _ = Adjust(AdjustmentKind.HueSaturation), Key.U, ctrl, () => session!.CanEditPixels),
            Item("Brightness/Contrast…", () => _ = Adjust(AdjustmentKind.BrightnessContrast), enabled: () => session!.CanEditPixels),
            Item("Exposure…", () => _ = Adjust(AdjustmentKind.Exposure), enabled: () => session!.CanEditPixels),
            Item("Black & White…", () => _ = Adjust(AdjustmentKind.BlackAndWhite), enabled: () => session!.CanEditPixels),
            Item("Color Balance…", () => _ = Adjust(AdjustmentKind.ColorBalance), enabled: () => session!.CanEditPixels),
            Item("Gradient Map…", () => _ = Adjust(AdjustmentKind.GradientMap), enabled: () => session!.CanEditPixels),
            Item("Color Lookup…", () => _ = Adjust(AdjustmentKind.ColorLookup), enabled: () => session!.CanEditPixels),
            Item("Grain…", () => _ = Adjust(AdjustmentKind.Grain), enabled: () => session!.CanEditPixels),
            Item("Invert", () => session!.Adjust(new InvertAdjustment()), Key.I, ctrl, () => session!.CanEditPixels),
            Item("Auto Levels", AutoLevels, Key.L, ctrl | shift, () => session!.CanEditPixels),
            Line(),
            // Beside the adjustments rather than among the filters: it changes what the layer shows, not how its pixels look.
            Item("Remove Background…", () => _ = Filter(FilterKind.RemoveBackground), enabled: () => session!.CanEditPixels),
            Line(),
            Item("Canvas Size…", () => _ = CanvasSize(), Key.C, ctrl | alt),
            Item("Image Size…", () => _ = ImageSize(), Key.I, ctrl | alt),
            Item("Trim…", () => _ = Trim()),
            Item("Reveal All", () => { if (session!.RevealAll()) canvas.Fit(); }, enabled: () => session!.CanRevealAll),
            Item("Duplicate", () => AddSession(session!.Duplicate())),
            Line(),
            Item("Rotate Canvas 90° Clockwise", () => { session!.RotateCanvas(true); canvas.Fit(); }),
            Item("Rotate Canvas 90° Counterclockwise", () => { session!.RotateCanvas(false); canvas.Fit(); }),
            Item("Flip Canvas Horizontal", () => session!.FlipCanvas(true)),
            Item("Flip Canvas Vertical", () => session!.FlipCanvas(false)));

        Top("F_ilter", Enum.GetValues<FilterKind>().Where(kind => kind != FilterKind.RemoveBackground).Select(kind => (object)Item(FilterSettings.DisplayName(kind) + "…", () => _ = Filter(kind), enabled: () => session!.CanEditPixels)).ToArray());

        mergeItem = Item("Merge Down", () => session!.MergeLayers(), Key.E, ctrl, () => session!.CanMerge);
        clipItem = Item("Create Clipping Mask", () => session!.ToggleClippingMask(session.ActiveLayer!), Key.G, ctrl | alt, () => session!.ActiveLayer is { } l && session.CanClip(l));
        Top("_Layer",
            Item("New Layer", () => session!.AddBlankLayer(), Key.N, ctrl | shift),
            Sub("New Adjustment Layer", Enum.GetValues<AdjustmentKind>().Select(kind => (object)Item(Adjustment.Create(kind).DisplayName + "…", () => _ = NewAdjustmentLayer(kind), id: "New " + Adjustment.Create(kind).DisplayName + " Layer")).ToArray()),
            Item("Edit Adjustment…", () => _ = EditAdjustmentLayer(session!.ActiveLayer!, false), enabled: () => session!.ActiveLayer?.IsAdjustment == true),
            Line(),
            Item("Transform Layer", () => { canvas.ShowTransformControls = true; SelectTool(Tool.Move); }, Key.T, ctrl),
            Item("Duplicate Layer / Layer via Copy", () => session!.LayerViaCopy(), Key.J, ctrl, () => session!.ActiveLayer != null),
            Item("Rename Layer…", layers.BeginRename, Key.F2, enabled: () => session!.ActiveLayer != null),
            Item("Delete Layer", layers.DeleteLayerOrMask, enabled: () => session!.ActiveLayer != null),
            Line(),
            Item("Add Layer Mask", () => session!.AddMask(session.ActiveLayer!), enabled: () => session!.ActiveLayer is { Mask: null }),
            Item("Invert Layer Mask", () => { session!.EditingMask = true; session.Adjust(new InvertAdjustment()); }, enabled: () => session!.ActiveLayer?.Mask != null),
            Item("Apply Layer Mask", () => session!.ApplyMask(session.ActiveLayer!), enabled: () => session!.ActiveLayer is { Mask: not null, Pixels: not null }),
            clipItem,
            Line(),
            Item("Group Selected Layers", () => session!.GroupSelectedLayers(), Key.G, ctrl),
            Item("Ungroup", () => session!.Ungroup(session.ActiveLayer!), Key.G, ctrl | shift, () => session!.ActiveLayer?.IsGroup == true),
            Item("Move Layer Up", () => session!.MoveActiveLayer(1), Key.OemCloseBrackets, ctrl),
            Item("Move Layer Down", () => session!.MoveActiveLayer(-1), Key.OemOpenBrackets, ctrl),
            mergeItem,
            Item("Merge Visible", () => session!.MergeVisible(), enabled: () => session!.CanMergeVisible),
            Item("Stamp Visible", () => session!.StampVisible(), Key.E, ctrl | alt | shift),
            Item("Flatten Image", () => session!.FlattenImage()),
            Line(),
            Sub("Layer Effects", Enum.GetValues<LayerEffectKind>().Select(kind => (object)Item(LayerEffects.DisplayName(kind) + "…", () => _ = NewEffect(kind), enabled: () => session!.ActiveLayer?.Pixels != null, id: "Add " + LayerEffects.DisplayName(kind)))
                .Append(Line()).Append(Item("Delete Effect", () => session!.RemoveSelectedEffect(), enabled: () => session!.SelectedEffect != null)).ToArray()),
            Item("Edit Text…", () => BeginTextEdit(session!.ActiveLayer!), enabled: () => session!.ActiveLayer?.Text != null),
            Item("Rasterize Layer", () => session!.RasterizeShape(session.ActiveLayer!), enabled: () => session!.ActiveLayer?.IsLive == true),
            Item("Enhance Resolution", () => _ = EnhanceResolution(), enabled: () => session!.CanEnhanceResolution(session.ActiveLayer)),
            Item("Rotate Layer 90° Clockwise", () => session!.RotateLayers(90)),
            Item("Rotate Layer 90° Counterclockwise", () => session!.RotateLayers(-90)),
            Item("Rotate Layer 180°", () => session!.RotateLayers(180)),
            Item("Flip Layer Horizontal", () => session!.FlipLayers(true)),
            Item("Flip Layer Vertical", () => session!.FlipLayers(false)));

        var grid = new MenuItem { Header = Loc.T("Pixel Grid (800% and above)"), ToggleType = MenuItemToggleType.CheckBox, IsChecked = canvas.ShowPixelGrid };
        grid.Click += (_, _) => { canvas.ShowPixelGrid = !canvas.ShowPixelGrid; grid.IsChecked = canvas.ShowPixelGrid; canvas.InvalidateVisual(); RememberToolSettings(); };
        // View options are flags on the session, so a checkmark follows the current tab.
        MenuItem ViewToggle(string name, Func<ViewOptions, bool> get, Func<ViewOptions, ViewOptions> flip, Key key = Key.None, KeyModifiers modifiers = KeyModifiers.None, string? id = null)
        {
            var item = Item(name, () =>
            {
                var rulersShown = session!.View.ShowRulers;
                session.View = flip(session.View);
                canvas.ViewOptionsChanged(rulersShown);
                RememberToolSettings();
            }, key, modifiers, id: id);
            item.ToggleType = MenuItemToggleType.CheckBox;
            viewToggles.Add((item, get));
            return item;
        }
        Top("_View",
            Item("Fit Canvas", canvas.Fit, Key.D0, ctrl),
            Item("Actual Pixels", () => canvas.ZoomTo(1), Key.D1, ctrl),
            Item("Zoom In", canvas.ZoomIn, Key.OemPlus, ctrl),
            Item("Zoom Out", canvas.ZoomOut, Key.OemMinus, ctrl),
            Line(), grid,
            Item("Show Transform Controls", () => { canvas.ShowTransformControls = !canvas.ShowTransformControls; canvas.InvalidateVisual(); RebuildOptions(); RememberToolSettings(); }, Key.H, ctrl),
            Line(),
            ViewToggle("Rulers", v => v.ShowRulers, v => v with { ShowRulers = !v.ShowRulers }, Key.R, ctrl),
            Sub("Show",
                ViewToggle("Grid", v => v.ShowGrid, v => v with { ShowGrid = !v.ShowGrid }, Key.OemQuotes, ctrl, "Show Grid"),
                ViewToggle("Guides", v => v.ShowGuides, v => v with { ShowGuides = !v.ShowGuides }, Key.OemSemicolon, ctrl, "Show Guides")),
            Item("Grid Settings…", () => _ = ShowGridSettings()),
            Line(),
            ViewToggle("Snap", v => v.Snap, v => v with { Snap = !v.Snap }, Key.OemSemicolon, ctrl | shift),
            Sub("Snap To",
                ViewToggle("Guides", v => v.SnapToGuides, v => v with { SnapToGuides = !v.SnapToGuides }, id: "Snap To Guides"),
                ViewToggle("Grid", v => v.SnapToGrid, v => v with { SnapToGrid = !v.SnapToGrid }, id: "Snap To Grid"),
                ViewToggle("Layers", v => v.SnapToLayers, v => v with { SnapToLayers = !v.SnapToLayers }, id: "Snap To Layers"),
                ViewToggle("Document Bounds", v => v.SnapToDocumentBounds, v => v with { SnapToDocumentBounds = !v.SnapToDocumentBounds }, id: "Snap To Document Bounds")),
            Line(),
            ViewToggle("Lock Guides", v => v.LockGuides, v => v with { LockGuides = !v.LockGuides }, Key.OemSemicolon, ctrl | alt),
            Item("Clear Guides", () => session!.ClearGuides(), enabled: () => session!.CanClearGuides));
        // '+' is Shift and '=' on most keyboards, so Zoom In answers with Shift held too.
        commands.Add(new Shortcut("Zoom In (with Shift)", "Zoom In", "Menus", new KeyGesture(Key.OemPlus, ctrl | shift), canvas.ZoomIn, () => HasDocument, hidden: true));
        commands.Add(new Shortcut("Zoom In (keypad)", "Zoom In", "Menus", new KeyGesture(Key.Add, ctrl), canvas.ZoomIn, () => HasDocument, hidden: true));
        commands.Add(new Shortcut("Zoom Out (keypad)", "Zoom Out", "Menus", new KeyGesture(Key.Subtract, ctrl), canvas.ZoomOut, () => HasDocument, hidden: true));

        // A build from a repository leaves updates to its package manager, so there is nothing to switch on there.
        var autoUpdates = new MenuItem
        {
            Header = Loc.T("Check for Updates Automatically"),
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = settings.CheckForUpdates,
            IsEnabled = UpdateCheck.Channel == UpdateChannel.GitHub
        };
        autoUpdates.Click += (_, _) =>
        {
            settings.CheckForUpdates = !settings.CheckForUpdates;
            autoUpdates.IsChecked = settings.CheckForUpdates;
            settings.Save();
        };

        // The MCP server, through which an AI agent drives the editor. Off until switched on, and remembered.
        var aiControl = new MenuItem { Header = Loc.T("Allow AI Control"), ToggleType = MenuItemToggleType.CheckBox, IsChecked = settings.AllowAiControl };
        aiControl.Click += async (_, _) => { await SetAiControl(!AiControl); aiControl.IsChecked = settings.AllowAiControl; };

        // The language the interface is drawn in. Each entry is what that language calls itself, so the
        // list stays readable whichever language is showing. The choice is remembered and applied at once.
        var languageMenu = new MenuItem { Header = Loc.T("Language") };
        localisedHeaders.Add((languageMenu, "Language"));
        foreach (var (code, name) in Loc.Available)
        {
            var choice = code;
            var shown = name;
            var item = new MenuItem { Header = Loc.T(shown), ToggleType = MenuItemToggleType.Radio, IsChecked = settings.Language == code };
            // A language's own name needs no translation and falls back to itself; "the system default"
            // is a phrase rather than a name, so it is the one entry a resource actually changes.
            localisedHeaders.Add((item, shown));
            item.Click += (_, _) =>
            {
                SetLanguage(choice);
                foreach (var child in languageMenu.Items.OfType<MenuItem>()) child.IsChecked = false;
                item.IsChecked = true;
            };
            languageMenu.Items.Add(item);
        }

        MenuItem PanelToggle(string title)
        {
            var item = Item(title, () => dock.SetVisible(dock.Section(title), !dock.Section(title).State.Visible), needsDocument: false);
            item.ToggleType = MenuItemToggleType.CheckBox;
            panelToggles.Add((item, title));
            return item;
        }
        Top("_Window", PanelToggle("History"));

        Top("_Help", Item("Keyboard Shortcuts…", () => _ = ShowShortcuts(), Key.F1, needsDocument: false),
            Item("Check for Updates…", () => _ = CheckForUpdatesNow(), needsDocument: false),
            autoUpdates,
            Line(),
            aiControl,
            Line(),
            languageMenu,
            Line(),
            Item("About Composa", () => _ = Prompts.Alert(this, "About Composa",
            $"Composa {AppInfo.Version}\n\nA layer-based image editor for compositing and retouching, built with .NET, Avalonia and Skia. " +
            "It is a from-scratch implementation of the open-source macOS app Compositor by Robbie Tilton (MIT license)."), needsDocument: false));
        BuildToolKeys();
        ApplyShortcutOverrides();
        return menu;
    }

    // The keys of tools that come in groups, named once for the table and for the groups the tool rail opens. Each is also the
    // id a rebound key is saved under, so the spelling must not change.
    private const string MarqueeKey = "Marquee tool (again switches Rectangle and Ellipse)", LassoKey = "Lasso tool (again switches Freehand and Polygonal)",
        MagicKey = "Magic tool", BrushKey = "Brush tool", EraserKey = "Eraser", SmearKey = "Smear tool (again switches its mode)", ShapeKey = "Shape tool (again switches the shape)";

    /// <summary>The keys that pick tools and act on the canvas, kept as a table so the shortcuts window can rebind them.</summary>
    private void BuildToolKeys()
    {
        void Key(string title, Avalonia.Input.Key key, Action run, KeyModifiers modifiers = KeyModifiers.None, bool hidden = false) =>
            toolKeys.Add(new Shortcut(title, title, "Tools and Canvas", new KeyGesture(key, modifiers), run, hidden: hidden));
        Key("Move tool", Avalonia.Input.Key.V, () => SelectTool(Tool.Move));
        Key(MarqueeKey, Avalonia.Input.Key.M, () =>
        {
            if (session!.Tool == Tool.Marquee) session.MarqueeKind = session.MarqueeKind == MarqueeKind.Rectangle ? MarqueeKind.Ellipse : MarqueeKind.Rectangle;
            SelectTool(Tool.Marquee);
        });
        Key(LassoKey, Avalonia.Input.Key.L, () =>
        {
            if (session!.Tool == Tool.Lasso) session.LassoKind = session.LassoKind == LassoKind.Freehand ? LassoKind.Polygonal : LassoKind.Freehand;
            SelectTool(Tool.Lasso);
        });
        Key(MagicKey, Avalonia.Input.Key.W, () => SelectTool(Tool.Wand));
        Key("Crop tool", Avalonia.Input.Key.C, () => SelectTool(Tool.Crop));
        Key(BrushKey, Avalonia.Input.Key.B, () => { session!.EraserMode = false; SelectTool(Tool.Brush); });
        Key(EraserKey, Avalonia.Input.Key.E, () => { session!.EraserMode = true; SelectTool(Tool.Brush); });
        Key("Spot Healing Brush", Avalonia.Input.Key.J, () => SelectTool(Tool.SpotHealing));
        Key("Clone Stamp", Avalonia.Input.Key.S, () => SelectTool(Tool.CloneStamp));
        Key(SmearKey, Avalonia.Input.Key.R, () =>
        {
            if (session!.Tool == Tool.Smear) session.SmearMode = (SmearMode)(((int)session.SmearMode + 1) % 5);
            SelectTool(Tool.Smear);
        });
        Key("Gradient tool", Avalonia.Input.Key.G, () => SelectTool(Tool.Gradient));
        Key(ShapeKey, Avalonia.Input.Key.U, () =>
        {
            if (session!.Tool == Tool.Shape) session.ShapeKind = (ShapeKind)(((int)session.ShapeKind + 1) % Enum.GetValues<ShapeKind>().Length);
            SelectTool(Tool.Shape);
        });
        Key("Next shape", Avalonia.Input.Key.U, () => { session!.ShapeKind = (ShapeKind)(((int)session.ShapeKind + 1) % Enum.GetValues<ShapeKind>().Length); SelectTool(Tool.Shape); }, KeyModifiers.Shift);
        Key("Type tool", Avalonia.Input.Key.T, () => SelectTool(Tool.Text));
        Key("Eyedropper tool", Avalonia.Input.Key.I, () => SelectTool(Tool.Eyedropper));
        Key("Hand tool", Avalonia.Input.Key.H, () => SelectTool(Tool.Hand));
        Key("Zoom tool", Avalonia.Input.Key.Z, () => SelectTool(Tool.Zoom));
        Key("Cycle tool mode", Avalonia.Input.Key.Tab, () => { session!.CycleToolMode(); SelectTool(session.Tool); });
        Key("Swap foreground and background", Avalonia.Input.Key.X, () => { session!.SwapColors(); UpdateColors(); });
        Key("Reset colors to black and white", Avalonia.Input.Key.D, () => { session!.ResetColors(); UpdateColors(); });
        Key("Delete selection, layer or effect", Avalonia.Input.Key.Back, DeletePressed);
        Key("Toggle painting on the mask", Avalonia.Input.Key.OemBackslash, ToggleMaskEditing);
        Key("Toggle painting on the mask (pipe)", Avalonia.Input.Key.OemPipe, ToggleMaskEditing, hidden: true);
    }

    private void ToggleMaskEditing()
    {
        if (session?.ActiveLayer?.Mask == null) return;
        session.EditingMask = !session.EditingMask;
        session.NotifyLayersChanged();
    }

    /// <summary>Every shortcut the window lists, menus first.</summary>
    private IReadOnlyList<Shortcut> AllShortcuts => commands.Concat(toolKeys).ToList();

    /// <summary>Puts the keys saved in the settings on their commands.</summary>
    private void ApplyShortcutOverrides()
    {
        foreach (var shortcut in AllShortcuts)
        {
            if (!settings.Shortcuts.TryGetValue(shortcut.Id, out var stored)) continue;
            KeyGesture? gesture = null;
            if (!string.IsNullOrEmpty(stored))
            {
                try { gesture = KeyGesture.Parse(stored); }
                catch (Exception) { continue; } // A damaged entry keeps the default.
            }
            shortcut.Gesture = gesture;
            if (shortcut.Item != null) shortcut.Item.InputGesture = gesture;
        }
    }

    private async Task ShowShortcuts()
    {
        if (await ShortcutsDialog.Edit(this, AllShortcuts) is not { } chosen) return;
        settings.Shortcuts.Clear();
        foreach (var shortcut in AllShortcuts)
        {
            if (!chosen.TryGetValue(shortcut.Id, out var gesture)) continue;
            shortcut.Gesture = gesture;
            if (shortcut.Item != null) shortcut.Item.InputGesture = gesture;
            if (!Equals(gesture, shortcut.Default)) settings.Shortcuts[shortcut.Id] = gesture?.ToString() ?? "";
        }
        settings.Save();
    }

    private void RefreshMenuState()
    {
        if (recentMenu != null)
        {
            recentMenu.Items.Clear();
            foreach (var path in settings.RecentFiles.Where(p => File.Exists(p) || Directory.Exists(p)))
            {
                var item = new MenuItem { Header = path.Replace("_", "__") };
                item.Click += (_, _) => _ = OpenPaths([path]);
                recentMenu.Items.Add(item);
            }
            recentMenu.IsEnabled = recentMenu.Items.Count > 0;
        }
        foreach (var (item, command) in menuItems) item.IsEnabled = command.Enabled?.Invoke() ?? true;
        foreach (var (item, isChecked) in viewToggles) item.IsChecked = session != null && isChecked(session.View);
        foreach (var (item, section) in panelToggles) item.IsChecked = dock.Section(section).State.Visible;
        if (session == null) return;
        // These headers are rewritten on every open, so each goes through the language here too. The step
        // name inside them stays English: it is what HistoryPanel.IconFor matches the icon on.
        undoItem!.Header = session.History.CanUndo ? Loc.Format("Undo {0}", Loc.T(session.History.UndoName)) : Loc.T("Undo");
        redoItem!.Header = session.History.CanRedo ? Loc.Format("Redo {0}", Loc.T(session.History.RedoName)) : Loc.T("Redo");
        mergeItem!.Header = Loc.T(session.MergeTitle);
        clipItem!.Header = Loc.T(session.ActiveLayer?.Clipped == true ? "Release Clipping Mask" : "Create Clipping Mask");
    }

    /// <summary>Every menu header with the English name behind it, so a language change can re-set the text.</summary>
    private readonly List<(MenuItem Item, string Name)> localisedHeaders = [];

    /// <summary>
    /// Switches the language the interface is drawn in and remembers it. The menu is built once, so its
    /// headers are re-set here from the names recorded while it was built; a control that has already
    /// drawn its text refreshes the next time it is built, which is why lookups happen at draw time
    /// rather than names being stored translated. The English names underneath never change, so a
    /// document, a rebound key, a history icon and an agent's call are all untouched.
    /// </summary>
    private void SetLanguage(string code)
    {
        settings.Language = code;
        settings.Save();
        Loc.Apply(code);
        foreach (var (item, name) in localisedHeaders) item.Header = Loc.T(name);
        RefreshMenuState();
    }

    private void Execute(Shortcut command)
    {
        if (command.Enabled?.Invoke() == false || canvas.IsDragging) return;
        problem = note = null;
        try { command.Run(); }
        catch (Exception error) { _ = Prompts.Alert(this, command.Title, error.Message); }
        UpdateStatus();
    }

    /// <summary>Runs a slow edit with the wait cursor showing.</summary>
    private void Busy(Action action)
    {
        var previous = Cursor;
        Cursor = new Cursor(StandardCursorType.Wait);
        try { action(); }
        finally { Cursor = previous; }
    }

    private async Task<T> Busy<T>(Func<Task<T>> action)
    {
        var previous = Cursor;
        Cursor = new Cursor(StandardCursorType.Wait);
        try { return await action(); }
        finally { Cursor = previous; }
    }

    private void OnSessionLayersChanged()
    {
        if (session?.Tool != Tool.Move) return;
        if (session.ActiveLayerIdOrNull() != optionsLayer) RebuildOptions();
        else refreshOptions?.Invoke();
        optionsLayer = session.ActiveLayerIdOrNull();
    }

    // ---- Keyboard -----------------------------------------------------------------------------------------------

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var focused = FocusManager?.GetFocusedElement();
        if (SwallowAlt(e)) return;
        if (focused is TextBox)
        {
            // Text fields keep their own editing keys; Enter or Escape hands the keyboard back to the canvas.
            if (e.Key is Key.Enter or Key.Escape && session != null) Avalonia.Threading.Dispatcher.UIThread.Post(() => canvas.Focus());
            return;
        }
        if (focused is Control control && control.FindAncestorOfType<MenuItem>() != null) return;
        // A slider field that was just dragged keeps the keys that step or jump its value; letters still reach the tool shortcuts.
        if (focused is Controls.SliderField && e.Key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End) return;

        if (canvas.HandleKeyDown(e)) { e.Handled = true; return; }
        // While text is being typed, letters are text, not tool keys or shortcuts; the key stays unhandled so the
        // platform still delivers the character.
        if (session?.IsEditingText == true) return;
        if (canvas.IsDragging) { e.Handled = true; return; }

        var gesture = commands.FirstOrDefault(c => c.Matches(e));
        if (gesture != null) { Execute(gesture); e.Handled = true; return; }

        if (session == null) return;
        // Tool letters also work with Shift held (Shift+U has its own meaning, so an exact match wins).
        var tool = toolKeys.FirstOrDefault(c => c.Matches(e))
            ?? (e.KeyModifiers == KeyModifiers.Shift ? toolKeys.FirstOrDefault(c => c.Gesture is { KeyModifiers: KeyModifiers.None } g && g.Key == e.Key) : null);
        if (tool == null) return;
        e.Handled = true;
        problem = note = null;
        tool.Run();
        UpdateStatus();
    }

    /// <summary>
    /// Alt is a tool modifier here (subtract from selection, clone source, pick color). Left alone, a bare Alt press
    /// would hand keyboard focus to the menu bar and tool shortcuts would stop working after every Alt-click.
    /// </summary>
    private bool SwallowAlt(KeyEventArgs e)
    {
        if (session == null || e.Key is not (Key.LeftAlt or Key.RightAlt)) return false;
        e.Handled = true;
        canvas.InvalidateVisual();
        return true;
    }

    private void DeletePressed()
    {
        if (session == null) return;
        if (session.SelectedEffect != null) { session.RemoveSelectedEffect(); return; }
        if (session.Selection != null && session.CanEditPixels) session.ClearSelection();
        else if (session.Selection == null) layers.DeleteLayerOrMask();
    }

    // ---- Files --------------------------------------------------------------------------------------------------

    private static readonly FilePickerFileType ProjectType = new("Composa project") { Patterns = ["*" + ProjectFile.Extension] };
    private static readonly string[] ImageExtensions = [.. ImageFiles.ImportExtensions, .. RawImporter.Extensions];
    private static FilePickerFileType ImageType => new(Loc.T("Images")) { Patterns = ImageExtensions.Select(e => "*" + e).ToArray() };
    private static FilePickerFileType RawType => new(Loc.T("Camera RAW")) { Patterns = RawImporter.Extensions.Select(e => "*" + e).ToArray() };
    private static FilePickerFileType LookupType => new(Loc.T("Color lookup tables")) { Patterns = ["*.cube", "*.3dl"] };
    private static readonly FilePickerFileType AnyOpenable = new("Projects and images") { Patterns = ImageExtensions.Select(e => "*" + e).Append("*" + ProjectFile.Extension).ToArray() };

    private async Task NewCanvas()
    {
        var result = await CanvasDialogs.NewCanvas(this, session?.Background ?? SKColors.White, await ClipboardImageSize());
        if (result != null) AddSession(EditorSession.NewCanvas(result.Width, result.Height, result.Background));
    }

    private async Task Open()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = Loc.T("Open"), AllowMultiple = true, FileTypeFilter = [AnyOpenable, ProjectType, ImageType, RawType] });
        await OpenPaths(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    /// <summary>Opens projects in tabs and images as new documents.</summary>
    public async Task OpenPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try { await OpenPath(path); }
            catch (Exception error) { _ = Prompts.Alert(this, "Couldn't open " + Path.GetFileName(path), error.Message); }
        }
    }

    /// <summary>
    /// Opens one file: a project in a tab, an image as a new document, or the tab it is already open in. Null means the
    /// person declined a dialog on the way; anything wrong with the file is thrown, so the caller decides who hears it.
    /// </summary>
    public async Task<EditorSession?> OpenPath(string path)
    {
        EditorSession opened;
        if (sessions.FirstOrDefault(s => s.FilePath == path) is { } open) { SetSession(open); return open; }
        if (PsdImport.IsPsd(path))
        {
            // Photoshop files open as unsaved documents; what had to be converted is shown before anything is applied.
            if (await ImportPhotoshop(path, DocumentLimits.DocumentPixelBudget) is not { } import) return null;
            AddSession(opened = EditorSession.OpenPhotoshop(import, Path.GetFileNameWithoutExtension(path)));
        }
        else if (XcfImport.IsXcf(path))
        {
            // GIMP files open the same way; their guides come along.
            if (await ImportGimp(path, DocumentLimits.DocumentPixelBudget) is not { } import) return null;
            AddSession(opened = EditorSession.OpenGimp(import, Path.GetFileNameWithoutExtension(path)));
        }
        else if (Path.GetExtension(path).Equals(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase))
        {
            opened = new EditorSession(ProjectFile.Load(path));
            opened.MarkSaved(path);
            AddSession(opened);
        }
        else
        {
            var pixels = RawImporter.IsRaw(path) ? await DevelopRaw(path)
                : SvgImporter.IsSvg(path) ? await Busy(() => Task.Run(() => SvgImporter.Render(path))) // At the size the file declares.
                : ImageFiles.Load(path);
            if (pixels == null) return null;
            var document = new Document(pixels.Width, pixels.Height);
            var layer = Layer.Raster(Path.GetFileNameWithoutExtension(path), pixels);
            document.Layers.Add(layer);
            document.SetActive(layer.Id);
            AddSession(opened = new EditorSession(document) { SuggestedName = Path.GetFileNameWithoutExtension(path) });
        }
        settings.AddRecent(Path.GetFullPath(path));
        return opened;
    }

    private async Task PlaceImages()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = Loc.T("Place Images as Layers"), AllowMultiple = true, FileTypeFilter = [ImageType] });
        await PlacePaths(files.Select(f => f.TryGetLocalPath()).OfType<string>(), null);
    }

    /// <summary>Adds images (and Photoshop files, inside a folder each) to the current document as layers, centered on <paramref name="at"/> when given.</summary>
    public async Task PlacePaths(IEnumerable<string> paths, SKPoint? at)
    {
        if (session == null) return;
        foreach (var path in paths)
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (PsdImport.IsPsd(path))
                {
                    // Into an existing document, a Photoshop file's layers arrive inside a folder named after it.
                    var target = session;
                    // The file's layers join what the document already holds, so they get what is left of its budget.
                    if (await ImportPhotoshop(path, DocumentLimits.DocumentPixelBudget - session.Document.RasterPixels()) is not { } import) continue;
                    if (target != session) { import.Discard(); continue; }
                    session.PlacePhotoshop(import, name, at);
                }
                else if (XcfImport.IsXcf(path))
                {
                    var target = session;
                    if (await ImportGimp(path, DocumentLimits.DocumentPixelBudget - session.Document.RasterPixels()) is not { } import) continue;
                    if (target != session) { import.Discard(); continue; }
                    session.PlaceGimp(import, name, at);
                }
                else if (RawImporter.IsRaw(path))
                {
                    var target = session;
                    if (await DevelopRaw(path) is not { } developed) continue;
                    if (target != session) { developed.Dispose(); continue; }
                    session.AddImageLayer(name, developed, at);
                }
                else if (SvgImporter.IsSvg(path))
                {
                    // Fitted to the canvas as it is drawn, so a small icon comes in sharp rather than enlarged from a few pixels.
                    var target = session;
                    var canvas = new SKSizeI(target.Document.Width, target.Document.Height);
                    var budget = DocumentLimits.DocumentPixelBudget - target.Document.RasterPixels();
                    var fitted = await Busy(() => Task.Run(() => SvgImporter.Render(path, canvas, budget)));
                    if (target != session) { fitted.Dispose(); continue; }
                    session.AddImageLayer(name, fitted, at);
                }
                else session.AddImageLayer(name, ImageFiles.Load(path), at);
            }
            catch (Exception error) { _ = Prompts.Alert(this, "Import couldn't finish", error.Message); }
        }
        SelectTool(Tool.Move);
    }

    /// <summary>
    /// Decodes a camera RAW file off the UI thread, puts the develop sheet up and develops the full frame with what was
    /// chosen. Null means the import was cancelled.
    /// </summary>
    private async Task<SKBitmap?> DevelopRaw(string path)
    {
        var raw = await Busy(() => Task.Run(() => RawImporter.Decode(path)));
        if (await RawDevelopDialog.Show(this, Path.GetFileName(path), raw) is not { } settings) return null;
        return await Busy(() => Task.Run(() => raw.Develop(settings)));
    }

    /// <summary>Reads a Photoshop file and, when anything has to be converted, asks before going on. Null means the user declined.</summary>
    private async Task<PsdImport?> ImportPhotoshop(string path, long pixelBudget)
    {
        var import = await Task.Run(() => PsdImport.Load(path, pixelBudget));
        if (import.Conversions.Count == 0 || await ImportConversionDialog.Confirm(this, Path.GetFileName(path), "Photoshop", import.Conversions)) return import;
        import.Discard();
        return null;
    }

    /// <summary>Reads a GIMP file and, when anything has to be converted, asks before going on. Null means the user declined.</summary>
    private async Task<XcfImport?> ImportGimp(string path, long pixelBudget)
    {
        var import = await Task.Run(() => XcfImport.Load(path, pixelBudget));
        if (import.Conversions.Count == 0 || await ImportConversionDialog.Confirm(this, Path.GetFileName(path), "GIMP", import.Conversions)) return import;
        import.Discard();
        return null;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];
        if (paths.Count == 0) return;
        var projects = paths.Where(p => Path.GetExtension(p).Equals(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase)).ToList();
        var images = paths.Except(projects).ToList();
        _ = OpenPaths(projects);
        if (session == null || projects.Count > 0) _ = OpenPaths(images);
        else
        {
            var position = e.GetPosition(canvas);
            var inside = position.X >= 0 && position.Y >= 0 && position.X <= canvas.Bounds.Width && position.Y <= canvas.Bounds.Height;
            _ = PlacePaths(images, inside ? canvas.ToDocument(position) : null);
        }
    }

    private async Task<bool> Save(EditorSession target, bool saveAs)
    {
        var path = target.FilePath;
        if (saveAs || path == null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Loc.T("Save Project"), SuggestedFileName = target.Title + ProjectFile.Extension, DefaultExtension = ProjectFile.Extension.TrimStart('.'), FileTypeChoices = [ProjectType]
            });
            path = file?.TryGetLocalPath();
            if (path == null) return false;
            if (!path.EndsWith(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase)) path += ProjectFile.Extension;
        }
        if (await SaveTo(target, path) is not { } error) return true;
        await Prompts.Alert(this, "Couldn't save", error.Message);
        return false;
    }

    /// <summary>
    /// Saves the document to a project file, as Ctrl+S does once the path is known, and returns what went wrong or
    /// null. Another save of this document still writing finishes first; this one then saves whatever changed since.
    /// </summary>
    public async Task<Exception?> SaveTo(EditorSession target, string path)
    {
        if (saving.TryGetValue(target, out var earlier)) await earlier.Task;
        var task = Write(target, path);
        saving[target] = (path, task);
        UpdateStatus();
        try { return await task; }
        finally
        {
            if (saving.TryGetValue(target, out var current) && current.Task == task) saving.Remove(target);
            UpdateStatus();
        }
    }

    /// <summary>The saves still writing, by document. Close and quit wait for them, and the status bar names them.</summary>
    private readonly Dictionary<EditorSession, (string Path, Task<Exception?> Task)> saving = [];

    /// <summary>True while a save of the document is still being written.</summary>
    public bool IsSaving(EditorSession target) => saving.ContainsKey(target);

    /// <summary>
    /// Writes the document as it is now, off the UI thread, so the tools stay usable while a large project encodes.
    /// Committed bitmaps are immutable, so the snapshot can be read while editing goes on; only that snapshot counts as
    /// saved, and an edit made meanwhile leaves the document modified. The task never faults: close and quit await it.
    /// </summary>
    private async Task<Exception?> Write(EditorSession target, string path)
    {
        var snapshot = target.Document.Clone();
        var state = target.History.CurrentId;
        try { await Task.Run(() => ProjectFile.Save(snapshot, path)); }
        catch (Exception error) { return error; }
        target.MarkSaved(path, state);
        recovery?.Forget(target);
        settings.AddRecent(Path.GetFullPath(path));
        return null;
    }

    private async Task Export(ExportFormat format)
    {
        if (this.session is not { } session) return; // Held locally: the active tab may change while a dialog is open.
        if (format == ExportFormat.Jpeg)
        {
            using var preview = session.Flatten();
            if (await CanvasDialogs.JpegQuality(this, jpegQuality, preview) is not { } quality) return;
            jpegQuality = quality;
        }
        var extension = format switch { ExportFormat.Jpeg => "jpg", ExportFormat.Webp => "webp", _ => "png" };
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.Format("Export {0}", extension.ToUpperInvariant()), SuggestedFileName = session.Title + "." + extension, DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(Loc.Format("{0} image", extension.ToUpperInvariant())) { Patterns = ["*." + extension] }]
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            Busy(() =>
            {
                using var flat = session.Flatten();
                ImageFiles.Save(flat, path, format, format == ExportFormat.Png ? 100 : jpegQuality);
            });
        }
        catch (Exception error) { await Prompts.Alert(this, "Couldn't export", error.Message); }
    }

    /// <summary>The size of the last look exported, so the dialog opens on it.</summary>
    private int lookSize = 33;

    /// <summary>File > Export Look as .cube: the document's adjustment layers baked into one lookup table any editor can load.</summary>
    private async Task ExportLook()
    {
        if (this.session is not { } session) return;
        var (baked, leftOut) = LookBake.Survey(session.Document);
        if (baked.Count == 0)
        {
            ShowProblem(leftOut.Count > 0 ? "None of the adjustment layers can be baked into a look: " + string.Join("; ", leftOut.Select(l => $"{l.Layer.Name} {l.Why}")) + "."
                : "There is no adjustment layer to bake into a look.");
            return;
        }
        if (await LookDialogs.ExportLook(this, baked.Select(l => l.Name).ToList(), leftOut.Select(l => (l.Layer.Name, l.Why)).ToList(), lookSize) is not { } size) return;
        lookSize = size;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("Export Look"), SuggestedFileName = session.Title + ".cube", DefaultExtension = "cube",
            FileTypeChoices = [new FilePickerFileType(Loc.T("Color lookup table")) { Patterns = ["*.cube"] }]
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            var title = session.Title;
            var names = string.Join(", ", baked.Select(l => l.Name));
            Busy(() => File.WriteAllText(path, LookBake.Bake(baked, size, title).ToCube(title, $"Exported from Composa: {names}")));
        }
        catch (Exception error) { await Prompts.Alert(this, "Couldn't export", error.Message); }
    }

    // ---- Clipboard ----------------------------------------------------------------------------------------------

    private async Task Copy(bool merged)
    {
        if (session == null || !(merged ? session.CopyMerged() : session.Copy())) { ShowProblem("There is nothing to copy here."); return; }
        ShowCopied();
        if (EditorSession.Clipboard == null) { await ClearExternalClipboard(); return; }
        await PublishClipboard();
    }

    private async Task Cut()
    {
        if (session == null) return;
        session.Cut();
        ShowCopied();
        await PublishClipboard();
    }

    /// <summary>Copies a document's whole flattened picture, whatever is selected in it: the tab menu's Copy Image.</summary>
    private async Task CopyImage(EditorSession item)
    {
        problem = note = null;
        item.CopyMerged(whole: true);
        ShowCopied();
        await PublishClipboard();
    }

    /// <summary>Says what went to the clipboard, because a copy is otherwise silent and the first thing anyone doubts.</summary>
    private void ShowCopied()
    {
        if (EditorSession.CopiedLayers is { Layers.Count: var count }) ShowNote(count == 1 ? "Copied 1 layer" : $"Copied {count} layers");
        else if (EditorSession.Clipboard is { } image) ShowNote($"Copied {image.Pixels.Width} × {image.Pixels.Height} px");
    }

    /// <summary>Shares the copied pixels with other apps.</summary>
    private async Task PublishClipboard()
    {
        if (EditorSession.Clipboard is not { } image || Clipboard == null) return;
        try
        {
            using var bgra = new SKBitmap(new SKImageInfo(image.Pixels.Width, image.Pixels.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            image.Pixels.CopyTo(bgra, SKColorType.Bgra8888);
            var bitmap = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, bgra.GetPixels(), new Avalonia.PixelSize(bgra.Width, bgra.Height), new Avalonia.Vector(96, 96), bgra.RowBytes);
            await Clipboard.SetBitmapAsync(bitmap);
        }
        catch { /* The in-app clipboard still works when the desktop's clipboard refuses the image. */ }
    }

    /// <summary>Layers with nothing to show other apps (an adjustment) leave the desktop's clipboard empty, so Paste here is not mistaken for an outside image.</summary>
    private async Task ClearExternalClipboard()
    {
        if (Clipboard == null) return;
        try { await Clipboard.ClearAsync(); }
        catch { /* Nothing to share; the in-app clipboard still has the layers. */ }
    }

    private enum PasteKind { Normal, InPlace, Into }

    private async Task Paste(PasteKind kind = PasteKind.Normal)
    {
        if (session == null) return;
        ClipboardImage? external = null;
        try
        {
            if (await ExternalClipboardBitmap() is { } bitmap)
            {
                using (bitmap)
                {
                    using var stream = new MemoryStream();
                    bitmap.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                    stream.Position = 0;
                    external = new ClipboardImage(ImageFiles.Load(stream, "clipboard"), new SKPointI(int.MinValue / 2, int.MinValue / 2));
                }
            }
        }
        catch { /* Fall back to the in-app clipboard. */ }
        var pasted = kind switch
        {
            PasteKind.InPlace => session.PasteInPlace(external),
            PasteKind.Into => session.PasteInto(external),
            _ => session.Paste(external)
        };
        if (pasted == null) ShowProblem("The clipboard has no image.");
        else SelectTool(Tool.Move);
    }

    /// <summary>
    /// The image another app put on the clipboard, or null when the clipboard holds what was copied here, or no image.
    /// Pixels copied in this app keep their position; anything newer from another app wins. Paste and New Canvas both
    /// decide through this, so the size New Canvas offers is the size a paste brings.
    /// </summary>
    private async Task<Bitmap?> ExternalClipboardBitmap()
    {
        if (Clipboard == null) return null;
        try { return await Clipboard.TryGetInProcessDataAsync() == null ? await Clipboard.TryGetBitmapAsync() : null; }
        catch { return null; } // The in-app clipboard still works when the desktop's does not.
    }

    /// <summary>How long New Canvas waits for another app's clipboard before opening without its size.</summary>
    private static readonly TimeSpan ClipboardPatience = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The size of the image Paste would paste, for New Canvas to offer. A desktop clipboard that has not answered in
    /// time offers nothing rather than the in-app clipboard, which may be older than what it holds.
    /// </summary>
    private async Task<(int W, int H)?> ClipboardImageSize()
    {
        var external = ExternalClipboardBitmap();
        if (await Task.WhenAny(external, Task.Delay(ClipboardPatience)) != external)
        {
            _ = external.ContinueWith(late => late.Result?.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
            return null;
        }
        if (await external is { } bitmap)
            using (bitmap) return (bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        return EditorSession.Clipboard is { } image ? (image.Pixels.Width, image.Pixels.Height) : null;
    }

    // ---- Dialog-driven edits ------------------------------------------------------------------------------------

    private async Task ModifySelection(string title, string label, Func<int> initial, int max, Action<int> apply)
    {
        if (await Prompts.Number(this, title, label, initial(), 1, max) is { } value) apply((int)Math.Round(value));
        refreshOptions?.Invoke();
    }

    /// <summary>Select > Subject: what the picture shows in front of its plain backdrop.</summary>
    /// <summary>Select > Color Range: the panel is not modal, so the colors are clicked on the canvas while it is open.</summary>
    private void ShowColorRange()
    {
        if (session == null || !session.BeginColorRange()) return;
        ColorRangeWindow.Open(this, session);
    }

    private async void SelectSubject()
    {
        if (session == null) return;
        var target = session;
        try
        {
            if (SubjectFinder.Resolve(target.Detect) == SubjectDetect.Backdrop)
            {
                NoteFallback(target.Detect);
                if (!target.SelectSubject()) ShowProblem("No subject found: the picture has no plain backdrop to tell it apart from.");
                return;
            }
            var found = await ProgressWindow.Run(this, "Finding the subject…", ct => target.SelectSubjectAsync(Composa.Selections.SelectionMode.Replace, ct));
            if (found == false) ShowProblem("No subject found in the picture.");
        }
        catch (Exception e) { ReportFailure(e); }
    }

    /// <summary>A click with the Object Selection tool: the plain method answers at once, a model through the progress window.</summary>
    private async void SelectObjectAt(int x, int y, Composa.Selections.SelectionMode mode)
    {
        if (session == null) return;
        var target = session;
        try
        {
            if (SubjectFinder.Resolve(target.Detect) == SubjectDetect.Backdrop) { NoteFallback(target.Detect); target.SelectObject(x, y, mode); return; }
            await ProgressWindow.Run(this, "Finding the subject…", async ct => { await target.SelectObjectAsync(x, y, mode, ct); return true; });
        }
        catch (Exception e) { ReportFailure(e); }
    }

    /// <summary>A box dragged with the Object Selection tool: the model runs on the box alone, through the progress window.</summary>
    private async void SelectObjectIn(SKRectI box, Composa.Selections.SelectionMode mode)
    {
        if (session == null) return;
        var target = session;
        try
        {
            NoteFallback(target.Detect);
            await ProgressWindow.Run(this, "Finding the object…", async ct => { await target.SelectObjectInBoxAsync(box, mode, ct); return true; });
        }
        catch (Exception e) { ReportFailure(e); }
    }

    /// <summary>Says once why a model choice was not honoured, when the runtime did not load or the file is not installed.</summary>
    private void NoteFallback(SubjectDetect detect)
    {
        if (SubjectFinder.FallbackReason(detect) is { } reason) ShowNote(reason);
    }

    private Histogram? HistogramOfActive()
    {
        if (session?.ActiveLayer is not { } layer) return null;
        if (layer.Pixels != null && !session.IsEditingMask) return Histogram.Of(layer.Pixels);
        return Histogram.Of(session.Composite());
    }

    private void AutoLevels()
    {
        if (session?.ActiveLayer?.Pixels is { } pixels) session.Adjust(LevelsAdjustment.Auto(Histogram.Of(pixels)));
    }

    private async Task Adjust(AdjustmentKind kind)
    {
        if (session == null) return;
        var target = session;
        var histogram = HistogramOfActive();
        if (!target.BeginPreview(Adjustment.Create(kind).DisplayName)) { ShowProblem("Select a pixel layer or a mask first."); return; }
        var result = await AdjustmentDialogs.Edit(this, Adjustment.Create(kind), target.PreviewAdjustment, histogram, target.Foreground, target.Background, () => target.PreviewOriginal, PickLookupFile);
        if (result == null || result.IsIdentity) target.CancelPreview();
        else { target.PreviewAdjustment(result); target.CommitPreview(); }
    }

    /// <summary>The last Camera Raw grade, so the panel opens where it was left; a hidden group is absent from it.</summary>
    private CameraRawSettings lastCameraRaw = new();

    private async Task Filter(FilterKind kind)
    {
        if (session == null) return;
        var target = session;
        if (!target.BeginFilter(kind)) { ShowProblem("Select a pixel layer or a mask first."); return; }
        if (kind == FilterKind.CameraRaw)
        {
            var original = target.PreviewOriginal!;
            var seed = (uint)Random.Shared.Next();
            var grade = await CameraRawDialog.Show(this, lastCameraRaw, original,
                settings => Busy(() => target.PreviewFilter(new FilterSettings { Kind = kind, CameraRaw = settings, Seed = seed })),
                () => target.ActiveLayer is { } layer ? (target.IsEditingMask ? layer.Mask : layer.Pixels) : null,
                () => PickLookSavePath(target.Title), target.Title);
            if (grade == null) { target.CancelPreview(); return; }
            lastCameraRaw = grade;
            if (grade.IsIdentity) { target.CancelPreview(); return; }
            target.PreviewFilter(new FilterSettings { Kind = kind, CameraRaw = grade, Seed = seed });
            target.CommitPreview();
            return;
        }
        if (kind == FilterKind.RemoveBackground) { await RemoveBackground(target); return; }
        var initial = new FilterSettings { Kind = kind, Radius = kind == FilterKind.Sharpen ? 2 : kind == FilterKind.MotionBlur ? 30 : 8, Amount = kind == FilterKind.Sharpen ? 60 : 20, Seed = (uint)Random.Shared.Next() };
        var result = await AdjustmentDialogs.EditFilter(this, initial, settings => Busy(() => target.PreviewFilter(settings)));
        // A filter left at nothing (a vignette of zero) closes as Cancel does, without an undo step.
        if (result == null || result.IsIdentity) target.CancelPreview();
        else { target.PreviewFilter(result); target.CommitPreview(); }
    }

    /// <summary>
    /// Filter > Remove Background, whose preview is open already. With a model the result is a mask, previewed as
    /// one; with the plain backdrop the pixels are erased as before. The model runs off the UI thread while the
    /// dialog stays live, and a choice made while it runs cancels the run it replaces.
    /// </summary>
    private async Task RemoveBackground(EditorSession target)
    {
        // A mask is edited by erasing; a model's mask would have nowhere to go.
        var initial = new FilterSettings { Kind = FilterKind.RemoveBackground, Detect = target.IsEditingMask ? SubjectDetect.Backdrop : target.Detect, Amount = 20 };
        CancellationTokenSource? running = null;
        async Task Preview(FilterSettings settings)
        {
            running?.Cancel();
            var mine = running = new CancellationTokenSource();
            if (SubjectFinder.Resolve(settings.Detect) == SubjectDetect.Backdrop)
            {
                NoteFallback(settings.Detect);
                target.PreviewRemoveBackground(null);
                Busy(() => target.PreviewFilter(settings));
                return;
            }
            var matte = await Busy(() => target.FindLayerSubjectAsync(settings.Detect, mine.Token));
            if (mine.IsCancellationRequested || !target.IsPreviewing) return;
            target.PreviewRemoveBackground(matte);
            if (matte == null) ShowProblem("No subject found in this layer.");
        }
        async void PreviewSafely(FilterSettings settings)
        {
            try { await Preview(settings); }
            catch (OperationCanceledException) { }
            catch (Exception e) { ReportFailure(e); }
        }
        var result = await AdjustmentDialogs.EditFilter(this, initial, PreviewSafely, canDetect: !target.IsEditingMask);
        running?.Cancel();
        if (result == null || !target.IsPreviewing) { target.CancelPreview(); return; }
        if (!target.IsEditingMask) target.Detect = result.Detect;
        RememberToolSettings();
        try { await Preview(result); }
        catch (OperationCanceledException) { }
        if (!target.IsPreviewing) return;
        if (SubjectFinder.Resolve(result.Detect) != SubjectDetect.Backdrop && target.ActiveLayer is { Mask: null }) { target.CancelPreview(); return; }
        target.CommitPreview();
    }

    private async Task NewAdjustmentLayer(AdjustmentKind kind)
    {
        if (session == null) return;
        var adjustment = Adjustment.Create(kind);
        if (adjustment is InvertAdjustment) { session.AddAdjustmentLayer(adjustment); return; }
        // The layer and its settings are one undo step, and cancelling the dialog leaves no trace of either.
        var target = session;
        var layer = target.AddAdjustmentLayer(adjustment, commit: false);
        var result = await AdjustmentDialogs.Edit(this, adjustment, a => target.SetAdjustment(layer, a), Histogram.Of(target.Composite()), target.Foreground, target.Background, target.Composite, PickLookupFile);
        if (result == null) target.Cancel();
        else
        {
            target.SetAdjustment(layer, result);
            NameByLook(layer, adjustment, result);
            target.Commit();
            target.NotifyLayersChanged();
        }
    }

    /// <summary>Asks where the Camera Raw panel saves its look as a .cube; null when nowhere.</summary>
    private async Task<string?> PickLookSavePath(string title)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("Save Look"), SuggestedFileName = title + ".cube", DefaultExtension = "cube",
            FileTypeChoices = [new FilePickerFileType(Loc.T("Color lookup table")) { Patterns = ["*.cube"] }]
        });
        return file?.TryGetLocalPath();
    }

    /// <summary>Asks for a .cube or .3dl for the Color Lookup dialog; null when none was chosen.</summary>
    private async Task<string?> PickLookupFile()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = Loc.T("Load Color Lookup Table"), FileTypeFilter = [LookupType] });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task EditAdjustmentLayer(Layer layer, bool isNew)
    {
        if (session == null || layer.Adjustment == null) return;
        var target = session;
        var original = layer.Adjustment;
        if (original is InvertAdjustment) return;
        var histogram = Histogram.Of(target.Composite());
        target.Begin("Edit Adjustment");
        var result = await AdjustmentDialogs.Edit(this, original, a => target.SetAdjustment(layer, a), histogram, target.Foreground, target.Background, target.Composite, PickLookupFile);
        if (result != null && !result.ContentEquals(original))
        {
            target.SetAdjustment(layer, result);
            NameByLook(layer, original, result);
            target.Commit();
        }
        else target.Cancel();
    }

    /// <summary>
    /// A look names its layer, as a text layer is named by its words, for as long as the name is still the look's
    /// (or the numbered default): a name the person typed stays. The edit is open, so the rename is part of its step.
    /// </summary>
    private static void NameByLook(Layer layer, Adjustment before, Adjustment after)
    {
        if (after is not ColorLookupAdjustment { Source.Length: > 0 } look || look.Source == layer.Name) return;
        if (layer.Name == (before as ColorLookupAdjustment)?.Source || layer.Name.StartsWith(look.DisplayName, StringComparison.Ordinal)) layer.Name = look.Source;
    }

    /// <summary>Adds an effect to the active layer and opens its settings; cancelling the dialog takes the effect away again.</summary>
    private async Task NewEffect(LayerEffectKind kind)
    {
        if (session == null) return;
        var target = session;
        if (target.ActiveLayer is not { Pixels: not null } layer) { ShowProblem("Select a layer with pixels to add an effect to."); return; }
        if (layer.Effects?.Contains(kind) == true) { await EditEffect(layer, kind); return; }
        target.AddEffect(layer, kind, commit: false);
        if (await EffectsDialog.Edit(this, target, layer, kind)) target.Commit();
        else { target.Cancel(); target.SelectedEffect = null; }
        target.NotifyLayersChanged();
    }

    /// <summary>Edits one effect with a live preview; the dialog undoes as one step.</summary>
    private async Task EditEffect(Layer layer, LayerEffectKind kind)
    {
        if (session == null || layer.Effects?.Contains(kind) != true) return;
        var target = session;
        var original = layer.Effects;
        target.SelectedEffect = (layer.Id, kind);
        target.Begin("Edit " + LayerEffects.DisplayName(kind));
        if (await EffectsDialog.Edit(this, target, layer, kind) && layer.Effects != original) target.Commit();
        else target.Cancel();
        target.NotifyLayersChanged();
    }

    private async Task CanvasSize()
    {
        if (session == null) return;
        if (await CanvasDialogs.CanvasSize(this, session.Document.Width, session.Document.Height) is not { } result) return;
        session.ResizeCanvas(result.Width, result.Height, result.Anchor);
        canvas.Fit();
    }

    private TrimOptions trimOptions = new();

    /// <summary>Image &gt; Trim: the dialog remembers its last choices for the session.</summary>
    private async Task Trim()
    {
        if (session == null) return;
        var target = session;
        if (await TrimDialog.Show(this, trimOptions) is not { } options) return;
        trimOptions = options;
        if (!target.Trim(options)) { ShowProblem("Nothing to trim: no edge is empty in that sense."); return; }
        canvas.Fit();
    }

    private async Task ImageSize()
    {
        if (session == null) return;
        var target = session;
        if (await CanvasDialogs.ImageSize(this, target.Document.Width, target.Document.Height, target.Document.Resolution, settings.Resample) is not { } result) return;
        settings.Resample = result.Resample;
        settings.Save();
        if (!await ResizeImage(target, result.Width, result.Height, result.Resolution, result.Resample)) return;
        canvas.Fit();
    }

    /// <summary>
    /// Layer > Enhance Resolution: the model gives a raster layer shown larger than its pixels enough pixels for its
    /// size on the canvas, behind the progress window, and the layer keeps its place. Cancel changes nothing.
    /// </summary>
    private async Task EnhanceResolution()
    {
        if (session is not { } target || target.ActiveLayer is not { } layer || !target.CanEnhanceResolution(layer)) return;
        if (!UpscaleModels.IsAvailable) { ShowProblem(UpscaleModels.UnavailableReason!); return; }
        var started = DateTime.UtcNow;
        var enhanced = await ProgressWindow.Run(this, "Enhancing…", (ct, status) => target.PrepareEnhancedResolutionAsync(layer, new Progress<(int Done, int Total)>(p =>
        {
            var elapsed = DateTime.UtcNow - started;
            var left = p.Done == 0 ? "" : $", about {Math.Max(1, (int)Math.Round(elapsed.TotalSeconds / p.Done * (p.Total - p.Done)))} s left";
            status.Report($"Enhancing… tile {p.Done} of {p.Total}{left}");
        }), ct));
        if (enhanced == null) return; // Cancelled.
        try { if (!target.EnhanceResolution(layer, enhanced)) ShowProblem("The layer changed while the model ran, so nothing was changed."); }
        finally { enhanced.DisposeUnused(target.Document); }
    }

    /// <summary>
    /// Image Size with its Resample choice. Enhance runs the model over every layer first, behind the progress
    /// window and off the UI thread, and the resize itself then swaps the results in as one undo step; a cancel
    /// changes nothing. Where the model is not available, Enhance resamples as Automatic does and says so.
    /// </summary>
    public async Task<bool> ResizeImage(EditorSession target, int width, int height, double resolution, ResampleMode mode)
    {
        if (mode == ResampleMode.Enhance && !UpscaleModels.IsAvailable) { ShowNote(UpscaleModels.UnavailableReason + " The picture was resampled as Automatic does."); mode = ResampleMode.Automatic; }
        EnhancedLayers? enhanced = null;
        if (mode == ResampleMode.Enhance)
        {
            var started = DateTime.UtcNow;
            enhanced = await ProgressWindow.Run(this, "Enhancing…", (ct, status) => target.PrepareEnhancedAsync(width, height, new Progress<(int Done, int Total)>(p =>
            {
                var elapsed = DateTime.UtcNow - started;
                var left = p.Done == 0 ? "" : $", about {Math.Max(1, (int)Math.Round(elapsed.TotalSeconds / p.Done * (p.Total - p.Done)))} s left";
                status.Report($"Enhancing… tile {p.Done} of {p.Total}{left}");
            }), ct));
            if (enhanced == null) return false; // Cancelled.
        }
        try { Busy(() => target.ResizeImage(width, height, resolution, mode, enhanced)); }
        finally { enhanced?.DisposeUnused(target.Document); }
        return true;
    }

    /// <summary>
    /// View > Grid Settings: the grid shows while the dialog is open, changing as it is edited, and goes back to how it
    /// was on Cancel. The settings are the person's, like the other view options: nothing is saved with the project or undone.
    /// </summary>
    private async Task ShowGridSettings()
    {
        if (session == null) return;
        var target = session;
        var original = target.View;
        void Preview(LayoutGrid grid, GridAppearance appearance)
        {
            target.View = target.View with { ShowGrid = true, Grid = grid, GridAppearance = appearance };
            canvas.InvalidateVisual();
        }
        Preview(original.Grid, original.GridAppearance);
        var result = await GridSettingsDialog.Show(this, original.Grid, original.GridAppearance, Preview);
        target.View = original with { Grid = result?.Grid ?? original.Grid, GridAppearance = result?.Appearance ?? original.GridAppearance };
        canvas.InvalidateVisual();
        RememberToolSettings();
    }
}

internal static class SessionExtensions
{
    public static Guid? ActiveLayerIdOrNull(this EditorSession session) => session.Document.ActiveLayerId;
}
