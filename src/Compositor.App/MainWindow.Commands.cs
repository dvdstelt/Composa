using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Compositor.App.Dialogs;
using Compositor.Editing;
using Compositor.Filters;
using Compositor.IO;
using Compositor.Model;
using Compositor.Rendering;
using SkiaSharp;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private sealed record Command(string Name, KeyGesture? Gesture, Action Run, Func<bool>? Enabled = null);

    private readonly List<Command> commands = [];
    private readonly List<(MenuItem Item, Command Command)> menuItems = [];
    private MenuItem? undoItem, redoItem, mergeItem, clipItem;
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
            var top = new MenuItem { Header = header };
            foreach (var item in items) top.Items.Add(item);
            top.SubmenuOpened += (_, _) => RefreshMenuState();
            menu.Items.Add(top);
            return top;
        }
        MenuItem Item(string name, Action run, Key key = Key.None, KeyModifiers modifiers = KeyModifiers.None, Func<bool>? enabled = null, bool needsDocument = true)
        {
            var gesture = key == Key.None ? null : new KeyGesture(key, modifiers);
            var guard = needsDocument ? () => HasDocument && (enabled?.Invoke() ?? true) : enabled;
            var command = new Command(name, gesture, run, guard);
            commands.Add(command);
            var item = new MenuItem { Header = name, InputGesture = gesture };
            item.Click += (_, _) => Execute(command);
            menuItems.Add((item, command));
            return item;
        }
        MenuItem Sub(string header, params object[] items)
        {
            var sub = new MenuItem { Header = header };
            foreach (var item in items) sub.Items.Add(item);
            return sub;
        }
        Separator Line() => new();

        Top("_File",
            Item("New Canvas…", () => _ = NewCanvas(), Key.N, ctrl, needsDocument: false),
            Item("Open…", () => _ = Open(), Key.O, ctrl, needsDocument: false),
            recentMenu = Sub("Open Recent"),
            Item("Open macOS Project Folder (.comp)…", () => _ = OpenMacProject(), needsDocument: false),
            Item("Place Images as Layers…", () => _ = PlaceImages()),
            Line(),
            Item("Save", () => _ = Save(session!, false), Key.S, ctrl),
            Item("Save As…", () => _ = Save(session!, true), Key.S, ctrl | shift),
            Line(),
            Item("Export PNG…", () => _ = Export(ExportFormat.Png), Key.E, ctrl | shift),
            Item("Export JPEG…", () => _ = Export(ExportFormat.Jpeg), Key.S, ctrl | shift | alt),
            Item("Export WebP…", () => _ = Export(ExportFormat.Webp)),
            Line(),
            Item("Close Project", () => _ = CloseSession(session!), Key.W, ctrl),
            Item("Quit", Close, Key.Q, ctrl, needsDocument: false));

        undoItem = Item("Undo", () => session!.Undo(), Key.Z, ctrl, () => session!.CanUndo);
        redoItem = Item("Redo", () => session!.Redo(), Key.Z, ctrl | shift, () => session!.CanRedo);
        commands.Add(new Command("Redo", new KeyGesture(Key.Y, ctrl), () => session!.Redo(), () => HasDocument && session!.CanRedo));
        Top("_Edit", undoItem, redoItem, Line(),
            Item("Cut", () => _ = Cut(), Key.X, ctrl, () => session!.CanCopy),
            Item("Copy", () => _ = Copy(merged: false), Key.C, ctrl, () => session!.CanCopy),
            Item("Copy Merged", () => _ = Copy(merged: true), Key.C, ctrl | shift),
            Item("Paste", () => _ = Paste(), Key.V, ctrl),
            Line(),
            Item("Fill with Foreground Color", () => session!.Fill(session.Foreground, "Fill"), Key.Back, alt, () => session!.CanFill),
            Item("Fill with Background Color", () => session!.Fill(session.Background, "Fill"), Key.Back, ctrl, () => session!.CanFill),
            Item("Clear", DeletePressed, Key.Delete),
            Item("Content-Aware Fill", () => Busy(() => session!.ContentAwareFill()), Key.Back, shift, () => session!.Selection != null && session.CanEditPixels && !session.IsEditingMask));

        Top("_Select",
            Item("All", () => session!.SelectAll(), Key.A, ctrl),
            Item("Deselect", () => session!.Deselect(), Key.D, ctrl, () => session!.Selection != null),
            Item("Inverse", () => session!.InvertSelection(), Key.I, ctrl | shift),
            Line(),
            Item("Layer's Pixels", () => session!.SelectLayerPixels(session.ActiveLayer!), enabled: () => session!.ActiveLayer?.Pixels != null),
            Item("Layer's Mask", () => session!.SelectLayerMask(session.ActiveLayer!), enabled: () => session!.ActiveLayer?.Mask != null),
            Line(),
            Item("Expand…", () => _ = ModifySelection("Expand Selection", "Expand by", 4, v => session!.ExpandSelection((int)v)), enabled: () => session!.Selection != null),
            Item("Contract…", () => _ = ModifySelection("Contract Selection", "Contract by", 4, v => session!.ContractSelection((int)v)), enabled: () => session!.Selection != null),
            Item("Feather…", () => _ = ModifySelection("Feather Selection", "Feather radius", 8, v => session!.FeatherSelection((float)v)), Key.F6, shift, () => session!.Selection != null));

        Top("_Image",
            Item("Curves…", () => _ = Adjust(AdjustmentKind.Curves), Key.M, ctrl, () => session!.CanEditPixels),
            Item("Levels…", () => _ = Adjust(AdjustmentKind.Levels), Key.L, ctrl, () => session!.CanEditPixels),
            Item("Hue/Saturation…", () => _ = Adjust(AdjustmentKind.HueSaturation), Key.U, ctrl, () => session!.CanEditPixels),
            Item("Brightness/Contrast…", () => _ = Adjust(AdjustmentKind.BrightnessContrast), enabled: () => session!.CanEditPixels),
            Item("Exposure…", () => _ = Adjust(AdjustmentKind.Exposure), enabled: () => session!.CanEditPixels),
            Item("Gradient Map…", () => _ = Adjust(AdjustmentKind.GradientMap), enabled: () => session!.CanEditPixels),
            Item("Grain…", () => _ = Adjust(AdjustmentKind.Grain), enabled: () => session!.CanEditPixels),
            Item("Invert", () => session!.Adjust(new InvertAdjustment()), Key.I, ctrl, () => session!.CanEditPixels),
            Item("Auto Levels", AutoLevels, Key.L, ctrl | shift, () => session!.CanEditPixels),
            Line(),
            Item("Canvas Size…", () => _ = CanvasSize(), Key.C, ctrl | alt),
            Item("Image Size…", () => _ = ImageSize(), Key.I, ctrl | alt),
            Item("Trim Transparent Edges", () => { session!.TrimCanvas(); canvas.Fit(); }),
            Line(),
            Item("Rotate Canvas 90° Clockwise", () => { session!.RotateCanvas(true); canvas.Fit(); }),
            Item("Rotate Canvas 90° Counterclockwise", () => { session!.RotateCanvas(false); canvas.Fit(); }),
            Item("Flip Canvas Horizontal", () => session!.FlipCanvas(true)),
            Item("Flip Canvas Vertical", () => session!.FlipCanvas(false)));

        Top("F_ilter", Enum.GetValues<FilterKind>().Select(kind => (object)Item(FilterSettings.DisplayName(kind) + "…", () => _ = Filter(kind), enabled: () => session!.CanEditPixels)).ToArray());

        mergeItem = Item("Merge Down", () => session!.MergeLayers(), Key.E, ctrl, () => session!.CanMerge);
        clipItem = Item("Create Clipping Mask", () => session!.ToggleClippingMask(session.ActiveLayer!), Key.G, ctrl | alt, () => session!.ActiveLayer is { } l && session.CanClip(l));
        Top("_Layer",
            Item("New Layer", () => session!.AddBlankLayer(), Key.N, ctrl | shift),
            Sub("New Adjustment Layer", Enum.GetValues<AdjustmentKind>().Select(kind => (object)Item(Adjustment.Create(kind).DisplayName + "…", () => _ = NewAdjustmentLayer(kind))).ToArray()),
            Item("Edit Adjustment…", () => _ = EditAdjustmentLayer(session!.ActiveLayer!, false), enabled: () => session!.ActiveLayer?.IsAdjustment == true),
            Line(),
            Item("Transform Layer", () => { canvas.ShowTransformControls = true; SelectTool(Tool.Move); }, Key.T, ctrl),
            Item("Duplicate Layer / Layer via Copy", () => session!.LayerViaCopy(), Key.J, ctrl),
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
            Item("Flatten Image", () => session!.FlattenImage()),
            Line(),
            Sub("Layer Effects", Enum.GetValues<LayerEffectKind>().Select(kind => (object)Item(LayerEffects.DisplayName(kind) + "…", () => _ = NewEffect(kind), enabled: () => session!.ActiveLayer?.Pixels != null))
                .Append(Line()).Append(Item("Delete Effect", () => session!.RemoveSelectedEffect(), enabled: () => session!.SelectedEffect != null)).ToArray()),
            Item("Edit Text…", () => BeginTextEdit(session!.ActiveLayer!), enabled: () => session!.ActiveLayer?.Text != null),
            Item("Rasterize Layer", () => session!.RasterizeShape(session.ActiveLayer!), enabled: () => session!.ActiveLayer?.IsLive == true),
            Item("Rotate Layer 90° Clockwise", () => session!.RotateLayers(90)),
            Item("Rotate Layer 90° Counterclockwise", () => session!.RotateLayers(-90)),
            Item("Rotate Layer 180°", () => session!.RotateLayers(180)),
            Item("Flip Layer Horizontal", () => session!.FlipLayers(true)),
            Item("Flip Layer Vertical", () => session!.FlipLayers(false)));

        var grid = new MenuItem { Header = "Pixel Grid (800% and above)", ToggleType = MenuItemToggleType.CheckBox, IsChecked = canvas.ShowPixelGrid };
        grid.Click += (_, _) => { canvas.ShowPixelGrid = !canvas.ShowPixelGrid; grid.IsChecked = canvas.ShowPixelGrid; canvas.InvalidateVisual(); };
        Top("_View",
            Item("Fit Canvas", canvas.Fit, Key.D0, ctrl),
            Item("Actual Pixels", () => canvas.ZoomTo(1), Key.D1, ctrl),
            Item("Zoom In", canvas.ZoomIn, Key.OemPlus, ctrl),
            Item("Zoom Out", canvas.ZoomOut, Key.OemMinus, ctrl),
            Line(), grid,
            Item("Show Transform Controls", () => { canvas.ShowTransformControls = !canvas.ShowTransformControls; canvas.InvalidateVisual(); RebuildOptions(); }, Key.H, ctrl));
        commands.Add(new Command("Zoom In", new KeyGesture(Key.Add, ctrl), canvas.ZoomIn, () => HasDocument));
        commands.Add(new Command("Zoom Out", new KeyGesture(Key.Subtract, ctrl), canvas.ZoomOut, () => HasDocument));

        Top("_Help", Item("Keyboard Shortcuts", () => _ = ShowShortcuts(), Key.F1, needsDocument: false), Item("About Compositor", () => _ = Prompts.Alert(this, "About Compositor",
            "Compositor for Linux\n\nA layer-based image editor for compositing and retouching, built with .NET, Avalonia and Skia. " +
            "It is a from-scratch Linux implementation of the open-source macOS app Compositor by Robbie Tilton (MIT license)."), needsDocument: false));
        return menu;
    }

    private void RefreshMenuState()
    {
        if (recentMenu != null)
        {
            recentMenu.Items.Clear();
            foreach (var path in settings.RecentFiles.Where(p => File.Exists(p) || Directory.Exists(p)))
            {
                var item = new MenuItem { Header = path.Replace("_", "__") };
                item.Click += (_, _) => OpenPaths([path]);
                recentMenu.Items.Add(item);
            }
            recentMenu.IsEnabled = recentMenu.Items.Count > 0;
        }
        foreach (var (item, command) in menuItems) item.IsEnabled = command.Enabled?.Invoke() ?? true;
        if (session == null) return;
        undoItem!.Header = session.History.CanUndo ? $"Undo {session.History.UndoName}" : "Undo";
        redoItem!.Header = session.History.CanRedo ? $"Redo {session.History.RedoName}" : "Redo";
        mergeItem!.Header = session.MergeTitle;
        clipItem!.Header = session.ActiveLayer?.Clipped == true ? "Release Clipping Mask" : "Create Clipping Mask";
    }

    private void Execute(Command command)
    {
        if (command.Enabled?.Invoke() == false || canvas.IsDragging) return;
        problem = null;
        try { command.Run(); }
        catch (Exception error) { _ = Prompts.Alert(this, command.Name.TrimEnd('…'), error.Message); }
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

        if (canvas.HandleKeyDown(e)) { e.Handled = true; return; }
        if (canvas.IsDragging) { e.Handled = true; return; }

        var gesture = commands.FirstOrDefault(c => c.Gesture != null && c.Gesture.Key == e.Key && c.Gesture.KeyModifiers == e.KeyModifiers);
        if (gesture != null) { Execute(gesture); e.Handled = true; return; }

        if (session == null || e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift)) return;
        var shift = e.KeyModifiers == KeyModifiers.Shift;
        e.Handled = true;
        switch (e.Key)
        {
            case Key.V: SelectTool(Tool.Move); break;
            case Key.M:
                if (session.Tool == Tool.Marquee) session.MarqueeKind = session.MarqueeKind == MarqueeKind.Rectangle ? MarqueeKind.Ellipse : MarqueeKind.Rectangle;
                SelectTool(Tool.Marquee);
                break;
            case Key.L:
                if (session.Tool == Tool.Lasso) session.LassoKind = session.LassoKind == LassoKind.Freehand ? LassoKind.Polygonal : LassoKind.Freehand;
                SelectTool(Tool.Lasso);
                break;
            case Key.W: SelectTool(Tool.Wand); break;
            case Key.C: SelectTool(Tool.Crop); break;
            case Key.B: session.EraserMode = false; SelectTool(Tool.Brush); break;
            case Key.E: session.EraserMode = true; SelectTool(Tool.Brush); break;
            case Key.J: SelectTool(Tool.SpotHealing); break;
            case Key.S: SelectTool(Tool.CloneStamp); break;
            case Key.R:
                if (session.Tool == Tool.Smear) session.SmearMode = (SmearMode)(((int)session.SmearMode + 1) % 5);
                SelectTool(Tool.Smear);
                break;
            case Key.G: SelectTool(Tool.Gradient); break;
            case Key.U:
                if (shift || session.Tool == Tool.Shape) session.ShapeKind = (ShapeKind)(((int)session.ShapeKind + 1) % 3);
                SelectTool(Tool.Shape);
                break;
            case Key.T: SelectTool(Tool.Text); break;
            case Key.I: SelectTool(Tool.Eyedropper); break;
            case Key.H: SelectTool(Tool.Hand); break;
            case Key.Z: SelectTool(Tool.Zoom); break;
            case Key.X: session.SwapColors(); UpdateColors(); break;
            case Key.D: session.ResetColors(); UpdateColors(); break;
            case Key.Back: DeletePressed(); break;
            case Key.OemBackslash or Key.OemPipe when session.ActiveLayer?.Mask != null:
                session.EditingMask = !session.EditingMask;
                session.NotifyLayersChanged();
                break;
            default: e.Handled = false; break;
        }
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

    private static readonly FilePickerFileType ProjectType = new("Compositor project") { Patterns = ["*" + ProjectFile.Extension] };
    private static readonly FilePickerFileType ImageType = new("Images") { Patterns = ImageFiles.ImportExtensions.Select(e => "*" + e).ToArray() };
    private static readonly FilePickerFileType AnyOpenable = new("Projects and images") { Patterns = ImageFiles.ImportExtensions.Select(e => "*" + e).Append("*" + ProjectFile.Extension).ToArray() };

    private async Task NewCanvas()
    {
        var result = await CanvasDialogs.NewCanvas(this, session?.Background ?? SKColors.White);
        if (result != null) AddSession(EditorSession.NewCanvas(result.Width, result.Height, result.Background));
    }

    private async Task Open()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open", AllowMultiple = true, FileTypeFilter = [AnyOpenable, ProjectType, ImageType] });
        OpenPaths(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    /// <summary>Opens projects in tabs and images as new documents.</summary>
    public void OpenPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (sessions.FirstOrDefault(s => s.FilePath == path) is { } open) { SetSession(open); continue; }
                if (MacProject.IsProject(path))
                {
                    // Projects from the macOS app open as unsaved documents; saving writes this app's own format.
                    AddSession(new EditorSession(MacProject.Load(path)) { SuggestedName = Path.GetFileNameWithoutExtension(path.TrimEnd(Path.DirectorySeparatorChar)) });
                }
                else if (Path.GetExtension(path).Equals(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    var loaded = new EditorSession(ProjectFile.Load(path));
                    loaded.MarkSaved(path);
                    AddSession(loaded);
                }
                else
                {
                    var pixels = ImageFiles.Load(path);
                    var document = new Document(pixels.Width, pixels.Height);
                    var layer = Layer.Raster(Path.GetFileNameWithoutExtension(path), pixels);
                    document.Layers.Add(layer);
                    document.SetActive(layer.Id);
                    AddSession(new EditorSession(document) { SuggestedName = Path.GetFileNameWithoutExtension(path) });
                }
            }
            catch (Exception error) { _ = Prompts.Alert(this, "Couldn't open " + Path.GetFileName(path), error.Message); continue; }
            settings.AddRecent(Path.GetFullPath(path));
        }
    }

    private async Task OpenMacProject()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Open macOS Compositor Project (.comp folder)" });
        OpenPaths(folders.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    private async Task PlaceImages()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Place Images as Layers", AllowMultiple = true, FileTypeFilter = [ImageType] });
        PlacePaths(files.Select(f => f.TryGetLocalPath()).OfType<string>(), null);
    }

    private void PlacePaths(IEnumerable<string> paths, SKPoint? at)
    {
        if (session == null) return;
        foreach (var path in paths)
        {
            try { session.AddImageLayer(Path.GetFileNameWithoutExtension(path), ImageFiles.Load(path), at); }
            catch (Exception error) { _ = Prompts.Alert(this, "Import couldn't finish", error.Message); }
        }
        SelectTool(Tool.Move);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];
        if (paths.Count == 0) return;
        var projects = paths.Where(p => MacProject.IsProject(p) || Path.GetExtension(p).Equals(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase)).ToList();
        var images = paths.Except(projects).ToList();
        OpenPaths(projects);
        if (session == null || projects.Count > 0) OpenPaths(images);
        else
        {
            var position = e.GetPosition(canvas);
            var inside = position.X >= 0 && position.Y >= 0 && position.X <= canvas.Bounds.Width && position.Y <= canvas.Bounds.Height;
            PlacePaths(images, inside ? canvas.ToDocument(position) : null);
        }
    }

    private async Task<bool> Save(EditorSession target, bool saveAs)
    {
        var path = target.FilePath;
        if (saveAs || path == null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Project", SuggestedFileName = target.Title + ProjectFile.Extension, DefaultExtension = ProjectFile.Extension.TrimStart('.'), FileTypeChoices = [ProjectType]
            });
            path = file?.TryGetLocalPath();
            if (path == null) return false;
            if (!path.EndsWith(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase)) path += ProjectFile.Extension;
        }
        try
        {
            Busy(() => ProjectFile.Save(target.Document, path));
            target.MarkSaved(path);
            recovery?.Forget(target);
            settings.AddRecent(Path.GetFullPath(path));
            return true;
        }
        catch (Exception error)
        {
            await Prompts.Alert(this, "Couldn't save", error.Message);
            return false;
        }
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
            Title = "Export " + extension.ToUpperInvariant(), SuggestedFileName = session.Title + "." + extension, DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(extension.ToUpperInvariant() + " image") { Patterns = ["*." + extension] }]
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

    // ---- Clipboard ----------------------------------------------------------------------------------------------

    private async Task Copy(bool merged)
    {
        if (session == null || !(merged ? session.CopyMerged() : session.Copy())) { ShowProblem("There is nothing to copy here."); return; }
        await PublishClipboard();
    }

    private async Task Cut()
    {
        if (session == null) return;
        session.Cut();
        await PublishClipboard();
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

    private async Task Paste()
    {
        if (session == null) return;
        ClipboardImage? external = null;
        try
        {
            // Pixels copied in this app keep their position; anything newer from another app wins.
            if (Clipboard != null && await Clipboard.TryGetInProcessDataAsync() == null && await Clipboard.TryGetBitmapAsync() is { } bitmap)
            {
                using var stream = new MemoryStream();
                bitmap.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                stream.Position = 0;
                external = new ClipboardImage(ImageFiles.Load(stream, "clipboard"), new SKPointI(int.MinValue / 2, int.MinValue / 2));
            }
        }
        catch { /* Fall back to the in-app clipboard. */ }
        if (session.Paste(external) == null) ShowProblem("The clipboard has no image.");
        else SelectTool(Tool.Move);
    }

    // ---- Dialog-driven edits ------------------------------------------------------------------------------------

    private async Task ModifySelection(string title, string label, double initial, Action<double> apply)
    {
        if (await Prompts.Number(this, title, label, initial, 1, 500) is { } value) apply(value);
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
        var result = await AdjustmentDialogs.Edit(this, Adjustment.Create(kind), target.PreviewAdjustment, histogram, target.Foreground, target.Background);
        if (result == null || result.IsIdentity) target.CancelPreview();
        else { target.PreviewAdjustment(result); target.CommitPreview(); }
    }

    private async Task Filter(FilterKind kind)
    {
        if (session == null) return;
        var target = session;
        if (!target.BeginPreview(FilterSettings.DisplayName(kind))) { ShowProblem("Select a pixel layer or a mask first."); return; }
        var initial = new FilterSettings { Kind = kind, Radius = kind == FilterKind.Sharpen ? 2 : kind == FilterKind.MotionBlur ? 30 : 8, Amount = kind == FilterKind.Sharpen ? 60 : 20, Seed = (uint)Random.Shared.Next() };
        var result = await AdjustmentDialogs.EditFilter(this, initial, settings => Busy(() => target.PreviewFilter(settings)));
        if (result == null) target.CancelPreview();
        else { target.PreviewFilter(result); target.CommitPreview(); }
    }

    private async Task NewAdjustmentLayer(AdjustmentKind kind)
    {
        if (session == null) return;
        var adjustment = Adjustment.Create(kind);
        if (adjustment is InvertAdjustment) { session.AddAdjustmentLayer(adjustment); return; }
        // The layer and its settings are one undo step, and cancelling the dialog leaves no trace of either.
        var target = session;
        var layer = target.AddAdjustmentLayer(adjustment, commit: false);
        var result = await AdjustmentDialogs.Edit(this, adjustment, a => target.SetAdjustment(layer, a), Histogram.Of(target.Composite()), target.Foreground, target.Background);
        if (result == null) target.Cancel();
        else
        {
            target.SetAdjustment(layer, result);
            target.Commit();
            target.NotifyLayersChanged();
        }
    }

    private async Task EditAdjustmentLayer(Layer layer, bool isNew)
    {
        if (session == null || layer.Adjustment == null) return;
        var target = session;
        var original = layer.Adjustment;
        if (original is InvertAdjustment) return;
        var histogram = Histogram.Of(target.Composite());
        target.Begin("Edit Adjustment");
        var result = await AdjustmentDialogs.Edit(this, original, a => target.SetAdjustment(layer, a), histogram, target.Foreground, target.Background);
        if (result != null && !result.ContentEquals(original))
        {
            target.SetAdjustment(layer, result);
            target.Commit();
        }
        else target.Cancel();
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

    private async Task ImageSize()
    {
        if (session == null) return;
        if (await CanvasDialogs.ImageSize(this, session.Document.Width, session.Document.Height, session.Document.Resolution) is not { } result) return;
        Busy(() => session.ResizeImage(result.Width, result.Height, result.Resolution));
        canvas.Fit();
    }

    private Task ShowShortcuts() => Prompts.Alert(this, "Keyboard Shortcuts",
        "Tools: V Move · M Marquee · L Lasso · W Wand · C Crop · B Brush · E Eraser · J Spot Healing · S Clone Stamp · R Smear · G Gradient · U Shape · T Text · I Eyedropper · H Hand · Z Zoom\n\n" +
        "Canvas: Space pan · Ctrl+wheel zoom · Ctrl+0 fit · Ctrl+1 100%\n\n" +
        "Brushes: [ ] size · { } hardness · 1–0 opacity · Shift-click straight line · Alt-click pick color or clone source\n\n" +
        "Colors: X swap · D reset · Alt+Backspace fill foreground · Ctrl+Backspace fill background\n\n" +
        "Selection: Ctrl+A all · Ctrl+D deselect · Ctrl+Shift+I inverse · Shift add · Alt subtract · Shift+Backspace content-aware fill\n\n" +
        "Layers: Ctrl+Shift+N new · Ctrl+J duplicate / via copy · Ctrl+G group · Ctrl+E merge · Ctrl+Alt+G clipping mask · Ctrl+[ ] reorder · \\ toggle mask editing · Alt-click eye to solo\n\n" +
        "Image: Ctrl+L Levels · Ctrl+M Curves · Ctrl+U Hue/Saturation · Ctrl+I Invert");
}

internal static class SessionExtensions
{
    public static Guid? ActiveLayerIdOrNull(this EditorSession session) => session.Document.ActiveLayerId;
}
