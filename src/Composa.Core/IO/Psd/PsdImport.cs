using Composa.Editing;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.IO.Psd;

/// <summary>
/// A Photoshop file rebuilt as this editor's layers, as far as they can carry it, with a report of everything that had
/// to be converted on the way. Nothing is applied to a document until the caller decides to, so the report can be
/// shown first. Photoshop is an interchange format here: files are opened, never written.
/// </summary>
public sealed class PsdImport
{
    public int Width { get; }
    public int Height { get; }
    public double Resolution { get; }
    /// <summary>Root layers, bottom to top, with folders holding their children. Meant to be placed once.</summary>
    public List<Layer> Layers { get; }
    public IReadOnlyList<PsdConversion> Conversions { get; }

    private PsdImport(int width, int height, double resolution, List<Layer> layers, List<PsdConversion> conversions)
    {
        Width = width;
        Height = height;
        Resolution = resolution;
        Layers = layers;
        Conversions = conversions;
    }

    /// <summary>Photoshop files are known by their <c>8BPS</c> signature, whatever their extension.</summary>
    public static bool IsPsd(string path) => PsdReader.Matches(path);
    public static bool IsPsd(ReadOnlySpan<byte> data) => PsdReader.Matches(data);

    public static PsdImport Load(string path, long pixelBudget = PsdReader.MaxPixels)
    {
        var info = new FileInfo(path);
        if (info.Length > int.MaxValue) throw PsdException.TooLarge();
        return Load(File.ReadAllBytes(path), pixelBudget);
    }

    public static PsdImport Load(byte[] data, long pixelBudget = PsdReader.MaxPixels)
    {
        var file = PsdReader.Read(data, pixelBudget);
        try { return Build(file, pixelBudget); }
        catch
        {
            foreach (var layer in file.Layers) { layer.Image?.Dispose(); layer.MaskImage?.Dispose(); }
            file.Composite?.Dispose();
            throw;
        }
    }

    /// <summary>Frees the layers' pixels when the import is not going ahead.</summary>
    public void Discard()
    {
        foreach (var layer in Document.Flatten(Layers))
        {
            layer.Pixels?.Dispose();
            layer.Mask?.Dispose();
            layer.Pixels = null;
            layer.Mask = null;
        }
        Layers.Clear();
    }

    /// <summary>A new document holding the layers, the topmost root layer active.</summary>
    public Document ToDocument()
    {
        var document = new Document(Width, Height) { Resolution = Resolution };
        document.Layers.AddRange(Layers);
        document.SetActive(Layers.LastOrDefault()?.Id);
        return document;
    }

    private static readonly Dictionary<string, BlendMode> Blends = new()
    {
        ["norm"] = BlendMode.Normal, ["mul "] = BlendMode.Multiply, ["scrn"] = BlendMode.Screen, ["over"] = BlendMode.Overlay,
        ["dark"] = BlendMode.Darken, ["lite"] = BlendMode.Lighten, ["diff"] = BlendMode.Difference, ["div "] = BlendMode.ColorDodge,
        ["idiv"] = BlendMode.ColorBurn, ["sLit"] = BlendMode.SoftLight, ["hLit"] = BlendMode.HardLight, ["smud"] = BlendMode.Exclusion,
        ["hue "] = BlendMode.Hue, ["sat "] = BlendMode.Saturation, ["colr"] = BlendMode.Color, ["lum "] = BlendMode.Luminosity,
        ["lbrn"] = BlendMode.LinearBurn, ["lddg"] = BlendMode.LinearDodge, ["vLit"] = BlendMode.VividLight, ["lLit"] = BlendMode.LinearLight,
        ["pLit"] = BlendMode.PinLight, ["hMix"] = BlendMode.HardMix, ["fsub"] = BlendMode.Subtract, ["fdiv"] = BlendMode.Divide
        // Dissolve, Darker Color and Lighter Color have no equivalent here and fall through to Normal with a conversion listed.
    };

    private static readonly string[] TextKeys = ["TySh", "tySh", "txt2"];
    private static readonly string[] VectorKeys = ["vmsk", "vsms", "vogk"];
    private static readonly string[] SmartObjectKeys = ["SoLd", "SoLE"];
    private static readonly string[] EffectKeys = ["lfx2", "lrFX", "lmfx"];
    private static readonly string[] OtherFillKeys = ["GdFl", "PtFl"];

    private static PsdImport Build(PsdFile file, long pixelBudget)
    {
        var conversions = new List<PsdConversion>();
        var canvas = new SKSizeI(file.Width, file.Height);
        var remaining = pixelBudget - file.Layers.Sum(l => (long)(l.Image?.Width ?? 0) * (l.Image?.Height ?? 0));

        if (file.Layers.Count == 0)
        {
            // A flattened file: the merged image is all there is, and it becomes the one layer.
            var flat = new List<Layer>();
            if (file.Composite != null) flat.Add(Layer.Raster("Background", file.Composite));
            return new PsdImport(file.Width, file.Height, file.Resolution, flat, conversions);
        }

        // Photoshop lists layers bottom to top: a folder's hidden divider comes first, then its contents, then the
        // folder record itself. Children are collected under the divider's id until their folder arrives.
        var roots = new List<Layer>();
        var pending = new Dictionary<Guid, List<Layer>>();
        var openGroups = new Stack<Guid>();
        var clipping = new HashSet<Guid>();
        foreach (var record in file.Layers)
        {
            if (record.IsDivider)
            {
                var id = Guid.NewGuid();
                openGroups.Push(id);
                pending[id] = [];
                record.Image?.Dispose();
                record.MaskImage?.Dispose();
                continue;
            }
            var name = record.Name.Length == 0 ? "Layer" : record.Name;
            var target = openGroups.Count > 0 ? pending[openGroups.Peek()] : roots;
            Layer? layer;
            if (record.IsGroup)
            {
                var id = openGroups.Count > 0 ? openGroups.Pop() : Guid.NewGuid();
                layer = new Layer { Id = id, Name = name, Kind = LayerKind.Group, Collapsed = record.Section == 2 };
                if (pending.TryGetValue(id, out var children)) layer.Children.AddRange(children);
                target = openGroups.Count > 0 ? pending[openGroups.Peek()] : roots;
                if (record.BlendKey is not ("pass" or "norm")) conversions.Add(new PsdConversion(name, $"Folder blend mode \"{record.BlendKey.Trim()}\" isn't supported. The folder will be pass-through."));
                record.Image?.Dispose();
            }
            else
            {
                layer = BuildLayer(record, name, canvas, ref remaining, conversions);
                if (layer == null) continue;
                if (!Blends.ContainsKey(record.BlendKey) && record.BlendKey != "pass")
                    conversions.Add(new PsdConversion(name, $"Blend mode \"{record.BlendKey.Trim()}\" isn't supported and will be applied as Normal."));
                layer.Blend = Blends.GetValueOrDefault(record.BlendKey, BlendMode.Normal);
                if (record.Clipping) clipping.Add(layer.Id);
            }
            layer.Visible = !record.Hidden;
            layer.Opacity = Opacity(record);
            ApplyMask(record, layer, canvas, conversions);
            target.Add(layer);
        }
        // A file whose folders never closed is damaged: keep their contents at the top level rather than lose them.
        while (openGroups.Count > 0) roots.AddRange(pending[openGroups.Pop()]);
        ResolveClipping(roots, clipping, conversions);
        return new PsdImport(file.Width, file.Height, file.Resolution, roots, conversions);
    }

    /// <summary>Layer opacity times fill opacity, as Photoshop shows them multiplied; a layer with effects keeps fill for the effects, which are dropped here.</summary>
    private static double Opacity(PsdLayer record)
    {
        var hasEffects = EffectKeys.Any(record.Extra.ContainsKey);
        var opacity = record.Opacity / 255.0;
        return Math.Clamp(hasEffects && record.Fill != 255 ? opacity : opacity * (record.Fill / 255.0), 0, 1);
    }

    private static Layer? BuildLayer(PsdLayer record, string name, SKSizeI canvas, ref long remaining, List<PsdConversion> conversions)
    {
        var extra = record.Extra;
        void Note(string message) => conversions.Add(new PsdConversion(name, message));
        var kind = TextKeys.Any(extra.ContainsKey) ? PsdLayerKind.Text
            : VectorKeys.Any(extra.ContainsKey) ? PsdLayerKind.Vector
            : SmartObjectKeys.Any(extra.ContainsKey) ? PsdLayerKind.SmartObject
            : PsdAdjustments.IsAdjustment(extra) ? PsdLayerKind.Adjustment
            : extra.ContainsKey("SoCo") ? PsdLayerKind.Fill
            : OtherFillKeys.Any(extra.ContainsKey) ? PsdLayerKind.Other
            : PsdLayerKind.Raster;
        if (EffectKeys.Any(extra.ContainsKey)) Note("Layer effects were discarded, so the appearance may differ.");

        if (kind == PsdLayerKind.Adjustment)
        {
            record.Image?.Dispose();
            var adjustment = PsdAdjustments.Parse(extra, out var approximate);
            if (adjustment == null) { Note("This adjustment type isn't supported and was skipped."); record.MaskImage?.Dispose(); return null; }
            if (approximate) Note("Adjustment parameters may not match Photoshop exactly.");
            var layer = Layer.ForAdjustment(adjustment);
            layer.Name = name;
            return layer;
        }
        if (kind == PsdLayerKind.Text) Note("Editable Photoshop text becomes pixels and can't be retyped.");
        if (kind == PsdLayerKind.SmartObject) Note("The smart object was rasterized. Linked contents can't be edited.");
        if (kind == PsdLayerKind.Other) Note("Gradient and pattern fills aren't supported; the layer was imported empty.");

        if (kind is PsdLayerKind.Vector or PsdLayerKind.Fill)
        {
            if (PsdVector.LiveShape(extra, canvas, remaining) is { } live)
            {
                record.Image?.Dispose();
                foreach (var note in live.Notes) Note(note);
                remaining -= (long)live.Bounds.Width * live.Bounds.Height;
                var pixels = EditorSession.RenderShape(live.Style, live.Bounds.Width, live.Bounds.Height);
                return AsShape(Layer.Raster(name, pixels, live.Bounds.Left, live.Bounds.Top), live.Style);
            }
            if (kind == PsdLayerKind.Fill && PsdVector.FillColor(extra) is { } fill)
            {
                // A solid color fill with no path covers the whole canvas.
                record.Image?.Dispose();
                var style = new ShapeStyle(ShapeKind.Rectangle, fill, 0);
                return AsShape(Layer.Raster(name, EditorSession.RenderShape(style, canvas.Width, canvas.Height)), style);
            }
            if (record.Image == null && PsdVector.Rasterized(extra, canvas, remaining) is { } raster)
            {
                Note("Vector shape was rasterized to pixels.");
                remaining -= (long)raster.Bounds.Width * raster.Bounds.Height;
                return Layer.Raster(name, raster.Image, raster.Bounds.Left, raster.Bounds.Top);
            }
            if (kind == PsdLayerKind.Vector) Note("Vector shape was rasterized to pixels.");
        }
        if (record.Image is { } image) return Layer.Raster(name, image, record.Left, record.Top);
        // No pixel area: an empty layer over the canvas, as Photoshop shows it.
        return Layer.Raster(name, Pixels.NewColor(canvas.Width, canvas.Height));
    }

    /// <summary>The layer as a live shape: redrawn sharp whenever it is scaled.</summary>
    private static Layer AsShape(Layer layer, ShapeStyle style)
    {
        layer.Shape = style;
        return layer;
    }

    /// <summary>
    /// Photoshop keeps a mask over its own rectangle with a default value outside it; here a mask shares its layer's
    /// pixel grid (or the document's, for folders and adjustments), so the plane is placed into one of that size.
    /// </summary>
    private static void ApplyMask(PsdLayer record, Layer layer, SKSizeI canvas, List<PsdConversion> conversions)
    {
        if (!record.HasMask || record.MaskFromRender) { record.MaskImage?.Dispose(); return; }
        int width, height, dx, dy;
        if (layer.Pixels is { } pixels)
        {
            width = pixels.Width; height = pixels.Height;
            dx = record.MaskLeft - (int)Math.Round(layer.Transform.X); dy = record.MaskTop - (int)Math.Round(layer.Transform.Y);
        }
        else
        {
            width = canvas.Width; height = canvas.Height;
            dx = record.MaskLeft; dy = record.MaskTop;
        }
        var mask = Pixels.NewMask(width, height, record.MaskDefault);
        if (record.MaskImage is { } plane)
        {
            using (plane)
            using (var surface = new SKCanvas(mask))
            {
                using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
                surface.DrawBitmap(plane, dx, dy, paint);
            }
            Pixels.Invalidate(mask);
        }
        layer.Mask = mask;
        layer.MaskEnabled = !record.MaskDisabled;
        if (!record.MaskLinked) conversions.Add(new PsdConversion(layer.Name, "The mask was unlinked from its layer in Photoshop; here it moves with the layer."));
    }

    /// <summary>A clipped layer follows the sibling below it here, so clipping onto a folder or an adjustment has no base to keep.</summary>
    private static void ResolveClipping(List<Layer> siblings, HashSet<Guid> clipping, List<PsdConversion> conversions)
    {
        for (var i = 0; i < siblings.Count; i++)
        {
            var layer = siblings[i];
            if (layer.IsGroup) ResolveClipping(layer.Children, clipping, conversions);
            if (!clipping.Contains(layer.Id)) continue;
            Layer? baseLayer = null;
            for (var j = i - 1; j >= 0; j--)
                if (!clipping.Contains(siblings[j].Id)) { baseLayer = siblings[j]; break; }
            if (baseLayer is { Pixels: not null }) layer.Clipped = true;
            else conversions.Add(new PsdConversion(layer.Name, "This clipping mask's base isn't supported, so clipping was skipped."));
        }
    }
}
