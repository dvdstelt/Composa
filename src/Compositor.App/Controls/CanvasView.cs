using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Compositor.Editing;
using Compositor.Model;
using Compositor.Rendering;
using Geometry = Compositor.Model.Geometry;
using Compositor.Selections;
using SkiaSharp;

namespace Compositor.App.Controls;

/// <summary>The editing surface: shows the flattened document with zoom and pan, draws tool overlays, and turns pointer input into edits.</summary>
public sealed partial class CanvasView : Control
{
    private EditorSession? session;
    private double zoom = 1;
    private Point origin; // Where the document's top-left sits, in control coordinates.
    private bool fitPending = true;
    private SKPath? selectionOutline;
    private bool outlineStale = true;
    private float antsPhase;
    private readonly DispatcherTimer antsTimer;

    // What is on screen, rendered straight from the layers at screen resolution. Its cost follows the window size,
    // not the document size, which keeps 24-megapixel documents as responsive as small ones.
    private SKBitmap? viewCache;
    private (float Scale, SKPoint Origin, int Width, int Height) viewKey;
    private SKRectI viewDirty;
    private bool viewStale = true;

    public CanvasView()
    {
        Focusable = true;
        ClipToBounds = true;
        antsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) =>
        {
            if (session?.Selection == null && polygon.Count == 0 && session?.TextEdit == null) return;
            antsPhase = (antsPhase + 1) % 8;
            InvalidateVisual();
        });
        antsTimer.Start();
        // The Space key-up that ends panning goes wherever focus went, so panning ends with the focus.
        LostFocus += (_, _) =>
        {
            if (!spaceDown) return;
            spaceDown = false;
            UpdateCursor();
        };
    }

    public event Action? ViewChanged;
    public event Action<string>? Problem;
    /// <summary>Raised when a tool changed session state the chrome displays (colors, brush size, crop presence).</summary>
    public event Action? ToolStateChanged;
    /// <summary>The document pixel under the pointer, or null once it leaves the canvas.</summary>
    public event Action<SKPointI?>? PointerAt;

    public bool ShowPixelGrid { get; set; } = true;
    public bool ShowTransformControls { get; set; } = true;
    public double Zoom => zoom;

    public EditorSession? Session
    {
        get => session;
        set
        {
            if (session == value) return;
            if (session != null)
            {
                CancelInteraction();
                session.CanvasChanged -= OnCanvasChanged;
                session.SelectionChanged -= OnSelectionChanged;
                session.LayersChanged -= OnLayersChanged;
            }
            session = value;
            if (session != null)
            {
                session.CanvasChanged += OnCanvasChanged;
                session.SelectionChanged += OnSelectionChanged;
                session.LayersChanged += OnLayersChanged;
            }
            // A crop rectangle belongs to the document it was drawn on.
            cropRect = null;
            spaceDown = false;
            outlineStale = true;
            fitPending = true;
            viewStale = true;
            UpdateCursor();
            InvalidateVisual();
            ViewChanged?.Invoke();
        }
    }

    private void OnCanvasChanged(SKRectI? area)
    {
        if (area is { } changed && !viewStale)
        {
            var (scale, shown, _, _) = viewKey;
            var mapped = new SKRectI(
                (int)Math.Floor((changed.Left - shown.X) * scale) - 1, (int)Math.Floor((changed.Top - shown.Y) * scale) - 1,
                (int)Math.Ceiling((changed.Right - shown.X) * scale) + 1, (int)Math.Ceiling((changed.Bottom - shown.Y) * scale) + 1);
            viewDirty = Geometry.Union(viewDirty, mapped);
        }
        else viewStale = true;
        if (area == null) outlineStale = true;
        InvalidateVisual();
        if (area == null) ViewChanged?.Invoke();
    }

    private void OnSelectionChanged()
    {
        outlineStale = true;
        InvalidateVisual();
    }

    private void OnLayersChanged() => InvalidateVisual();

    // ---- Viewport -----------------------------------------------------------------------------------------------

    private double Scaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
    /// <summary>Control units per document pixel; at 100% one document pixel is one device pixel.</summary>
    private double UnitsPerPixel => zoom / Scaling;

    public Point ToScreen(SKPoint p) => new(origin.X + p.X * UnitsPerPixel, origin.Y + p.Y * UnitsPerPixel);
    public SKPoint ToDocument(Point p) => new((float)((p.X - origin.X) / UnitsPerPixel), (float)((p.Y - origin.Y) / UnitsPerPixel));

    public void Fit()
    {
        if (!FitCore()) return;
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    /// <summary>Computes the fitted zoom and origin without touching the visual tree, so it is safe during a render pass.</summary>
    private bool FitCore()
    {
        if (session == null || Bounds.Width < 10 || Bounds.Height < 10) { fitPending = true; return false; }
        fitPending = false;
        var document = session.Document;
        var inset = RulerInset;
        var available = new Size(Math.Max(50, Bounds.Width - inset - 48), Math.Max(50, Bounds.Height - inset - 48));
        zoom = Math.Min(available.Width * Scaling / document.Width, available.Height * Scaling / document.Height);
        zoom = Math.Clamp(Math.Min(zoom, 1.0 * Math.Max(1, Scaling)), 0.01, 64);
        origin = new Point(inset + (Bounds.Width - inset - document.Width * UnitsPerPixel) / 2, inset + (Bounds.Height - inset - document.Height * UnitsPerPixel) / 2);
        return true;
    }

    public void ZoomTo(double value, Point? anchor = null)
    {
        if (session == null) return;
        var around = anchor ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
        var fixedPoint = ToDocument(around);
        zoom = Math.Clamp(value, 0.01, 64);
        origin = new Point(around.X - fixedPoint.X * UnitsPerPixel, around.Y - fixedPoint.Y * UnitsPerPixel);
        ClampOrigin();
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    public void ZoomIn() => ZoomTo(zoom * 1.25);
    public void ZoomOut() => ZoomTo(zoom / 1.25);

    private void PanBy(Vector delta)
    {
        origin += delta;
        ClampOrigin();
        InvalidateVisual();
    }

    private void ClampOrigin()
    {
        if (session == null) return;
        // Keep at least a sliver of the canvas in view.
        double w = session.Document.Width * UnitsPerPixel, h = session.Document.Height * UnitsPerPixel;
        origin = new Point(Math.Clamp(origin.X, 40 - w, Math.Max(40 - w, Bounds.Width - 40)), Math.Clamp(origin.Y, 40 - h, Math.Max(40 - h, Bounds.Height - 40)));
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (fitPending) Fit();
        else if (session != null)
        {
            origin += new Vector((e.NewSize.Width - e.PreviousSize.Width) / 2, (e.NewSize.Height - e.PreviousSize.Height) / 2);
            ClampOrigin();
        }
    }

    // ---- Rendering ----------------------------------------------------------------------------------------------

    private sealed class DrawOperation(Rect bounds, Action<SKCanvas> draw) : ICustomDrawOperation
    {
        public Rect Bounds { get; } = bounds;
        public void Dispose() { }
        public bool Equals(ICustomDrawOperation? other) => false;
        public bool HitTest(Point p) => true;

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height));
            draw(canvas);
            canvas.Restore();
        }
    }

    private static readonly SKColor Backdrop = new(0x1E, 0x1E, 0x1E);

    public override void Render(DrawingContext context)
    {
        if (fitPending && FitCore()) Dispatcher.UIThread.Post(() => ViewChanged?.Invoke());
        var size = Bounds.Size;
        if (session == null)
        {
            context.Custom(new DrawOperation(new Rect(size), canvas => canvas.Clear(Backdrop)));
            return;
        }
        if (outlineStale)
        {
            selectionOutline = session.Selection != null ? SelectionMask.Outline(session.Selection) : null;
            outlineStale = false;
        }
        // Everything the render thread needs is captured here, on the UI thread.
        var units = (float)UnitsPerPixel;
        var view = SKMatrix.CreateScale(units, units).PostConcat(SKMatrix.CreateTranslation((float)origin.X, (float)origin.Y));
        var documentRect = new SKRect(0, 0, session.Document.Width, session.Document.Height);
        var (cache, cacheSource, cacheTarget) = UpdateViewCache(view, size);
        var overlay = CaptureOverlay(view);
        var rulers = CaptureRulers((float)Scaling);
        var grid = ShowPixelGrid && zoom >= 8;
        var nearest = zoom >= 1;
        var outline = selectionOutline;
        var phase = antsPhase;
        var scaling = (float)Scaling;

        context.Custom(new DrawOperation(new Rect(size), canvas =>
        {
            canvas.Clear(Backdrop);
            var screenRect = view.MapRect(documentRect);
            using (var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 90), ImageFilter = SKImageFilter.CreateBlur(8, 8) })
                canvas.DrawRect(screenRect, shadow);
            DrawCheckerboard(canvas, screenRect);

            if (cache != null)
            {
                canvas.Save();
                canvas.ClipRect(screenRect);
                using var image = SKImage.FromPixels(cache.PeekPixels());
                canvas.DrawImage(image, cacheSource, cacheTarget, new SKSamplingOptions(nearest ? SKFilterMode.Nearest : SKFilterMode.Linear));
                canvas.Restore();
            }

            if (grid) DrawPixelGrid(canvas, view, documentRect, new SKRect(0, 0, (float)size.Width, (float)size.Height));
            if (outline != null) DrawAnts(canvas, outline, view, phase, scaling);
            overlay?.Invoke(canvas);
            rulers?.Invoke(canvas);
        }));
    }

    /// <summary>Brings the on-screen render up to date and returns it with the rectangles to draw it from and to.</summary>
    private (SKBitmap? Cache, SKRect Source, SKRect Target) UpdateViewCache(SKMatrix view, Size size)
    {
        if (session == null || !view.TryInvert(out var inverse)) return (null, default, default);
        var shown = Geometry.Intersect(Geometry.RoundOut(inverse.MapRect(new SKRect(0, 0, (float)size.Width, (float)size.Height))), session.Document.Bounds);
        if (shown.IsEmpty) return (null, default, default);
        // Zoomed in, the cache holds document pixels that are then enlarged crisply, so the screen shows exactly what
        // an export would contain. Zoomed out, it holds device pixels, and its grid is snapped to the screen's own
        // pixel grid: drawn at a fractional offset it would be resampled, and fine detail would shimmer while panning.
        float scale;
        SKPoint viewOrigin;
        int width, height;
        SKRect target;
        if (zoom >= 1)
        {
            scale = 1;
            viewOrigin = new SKPoint(shown.Left, shown.Top);
            width = shown.Width;
            height = shown.Height;
            target = view.MapRect(new SKRect(shown.Left, shown.Top, shown.Right, shown.Bottom));
        }
        else
        {
            scale = (float)zoom;
            var scaling = Scaling;
            double left = Math.Floor(origin.X * scaling + shown.Left * zoom), top = Math.Floor(origin.Y * scaling + shown.Top * zoom);
            double right = Math.Ceiling(origin.X * scaling + shown.Right * zoom), bottom = Math.Ceiling(origin.Y * scaling + shown.Bottom * zoom);
            viewOrigin = new SKPoint((float)((left - origin.X * scaling) / zoom), (float)((top - origin.Y * scaling) / zoom));
            width = Math.Max(1, (int)(right - left));
            height = Math.Max(1, (int)(bottom - top));
            target = new SKRect((float)(left / scaling), (float)(top / scaling), (float)(right / scaling), (float)(bottom / scaling));
        }
        if (viewCache == null || viewCache.Width < width || viewCache.Height < height)
        {
            // The old bitmap may still be in use by the render thread, so it is left to the garbage collector.
            viewCache = Pixels.NewColor(Math.Max(width, viewCache?.Width ?? 0) + 64, Math.Max(height, viewCache?.Height ?? 0) + 64);
            viewStale = true;
        }
        var key = (scale, viewOrigin, width, height);
        var whole = new SKRectI(0, 0, width, height);
        var dirty = viewStale || key != viewKey ? whole : Geometry.Intersect(viewDirty, whole);
        viewKey = key;
        viewStale = false;
        viewDirty = SKRectI.Empty;
        if (!dirty.IsEmpty) session.RenderView(viewCache, dirty, new RenderView(scale, viewOrigin));
        return (viewCache, new SKRect(0, 0, width, height), target);
    }

    private static void DrawCheckerboard(SKCanvas canvas, SKRect rect)
    {
        const int cell = 8;
        using var tile = new SKBitmap(cell * 2, cell * 2);
        tile.Erase(new SKColor(0xFF, 0xFF, 0xFF));
        using (var tileCanvas = new SKCanvas(tile))
        using (var gray = new SKPaint { Color = new SKColor(0xCC, 0xCC, 0xCC) })
        {
            tileCanvas.DrawRect(cell, 0, cell, cell, gray);
            tileCanvas.DrawRect(0, cell, cell, cell, gray);
        }
        using var shader = SKShader.CreateBitmap(tile, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, SKMatrix.CreateTranslation(rect.Left, rect.Top));
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(rect, paint);
    }

    private static void DrawPixelGrid(SKCanvas canvas, SKMatrix view, SKRect document, SKRect viewport)
    {
        if (!view.TryInvert(out var inverse)) return;
        var visible = inverse.MapRect(viewport);
        visible.Intersect(document);
        using var paint = new SKPaint { Color = new SKColor(128, 128, 128, 90), StrokeWidth = 1, IsAntialias = false };
        for (var x = (int)Math.Floor(visible.Left); x <= Math.Ceiling(visible.Right); x++)
        {
            var top = view.MapPoint(x, visible.Top);
            var bottom = view.MapPoint(x, visible.Bottom);
            canvas.DrawLine(top.X, top.Y, bottom.X, bottom.Y, paint);
        }
        for (var y = (int)Math.Floor(visible.Top); y <= Math.Ceiling(visible.Bottom); y++)
        {
            var left = view.MapPoint(visible.Left, y);
            var right = view.MapPoint(visible.Right, y);
            canvas.DrawLine(left.X, left.Y, right.X, right.Y, paint);
        }
    }

    internal static void DrawAnts(SKCanvas canvas, SKPath documentPath, SKMatrix view, float phase, float scaling)
    {
        using var path = new SKPath();
        documentPath.Transform(in view, path);
        var width = 1f / scaling;
        using var white = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = width, IsAntialias = false };
        using var black = new SKPaint
        {
            Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = width, IsAntialias = false,
            PathEffect = SKPathEffect.CreateDash([4, 4], phase)
        };
        canvas.DrawPath(path, white);
        canvas.DrawPath(path, black);
    }
}
