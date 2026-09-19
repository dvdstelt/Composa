using System.Text.Json;
using Compositor.Filters;
using Compositor.Model;
using Compositor.Rendering;
using SkiaSharp;

namespace Compositor.IO;

/// <summary>
/// Reads projects saved by Compositor for macOS: a <c>.comp</c> package, which on Linux is an ordinary folder holding
/// <c>manifest.json</c> and <c>images/</c> (format versions 1–7). Reading is tolerant: fields this app has no
/// equivalent for are skipped rather than rejected.
/// </summary>
public static class MacProject
{
    public const string Format = "com.compositor.project";

    public static bool IsProject(string path) => Directory.Exists(path) && File.Exists(Path.Combine(path, "manifest.json"));

    public static Document Load(string folder)
    {
        var manifestPath = Path.Combine(folder, "manifest.json");
        if (!File.Exists(manifestPath)) throw new InvalidDataException("This folder is not a Compositor project: it has no manifest.json.");
        if (new FileInfo(manifestPath).Length > 4 * 1024 * 1024) throw new InvalidDataException("The project's manifest is too large.");
        using var json = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = json.RootElement;
        if (Text(root, "format") != Format) throw new InvalidDataException("This is not a Compositor project.");
        var version = Int(root, "version", 1);
        if (version > 7) throw new InvalidDataException($"This project uses format version {version}; versions 1–7 can be opened.");

        var document = new Document(Int(root, "width", 1), Int(root, "height", 1)) { Resolution = Math.Clamp(Number(root, "resolution", 72), 1, 9600) };
        if (!root.TryGetProperty("layers", out var records) || records.ValueKind != JsonValueKind.Array) return document;

        var layers = new Dictionary<Guid, Layer>();
        var parents = new Dictionary<Guid, Guid>();
        var clipSources = new Dictionary<Guid, Guid>();
        var order = new List<Layer>();
        foreach (var record in records.EnumerateArray())
        {
            var id = Guid.TryParse(Text(record, "id"), out var parsed) ? parsed : Guid.NewGuid();
            var isGroup = record.TryGetProperty("isGroup", out var group) && group.ValueKind == JsonValueKind.True;
            var adjustment = record.TryGetProperty("adjustment", out var settings) && settings.ValueKind == JsonValueKind.Object ? ReadAdjustment(settings) : null;
            var layer = new Layer
            {
                Id = id, Name = Text(record, "name") ?? "Layer", Kind = isGroup ? LayerKind.Group : adjustment != null ? LayerKind.Adjustment : LayerKind.Raster,
                Visible = !record.TryGetProperty("isVisible", out var visible) || visible.ValueKind != JsonValueKind.False,
                Opacity = Math.Clamp(Number(record, "opacity", 1), 0, 1), Blend = ReadBlend(Text(record, "blendMode")), Adjustment = adjustment,
                MaskEnabled = !record.TryGetProperty("maskEnabled", out var enabled) || enabled.ValueKind != JsonValueKind.False
            };
            var transform = record.TryGetProperty("transform", out var t) ? ReadTransform(t) : null;
            if (layer.Kind == LayerKind.Raster)
            {
                layer.Pixels = Text(record, "imageFile") is { } file
                    ? ImageFiles.Load(Resolve(folder, file))
                    // A blank layer has no image asset on macOS; here it becomes transparent pixels over its rectangle.
                    : Pixels.NewColor((int)Math.Clamp(transform?.Width ?? document.Width, 1, Document.MaxSide), (int)Math.Clamp(transform?.Height ?? document.Height, 1, Document.MaxSide));
                layer.Transform = transform ?? LayerTransform.Identity(layer.Pixels.Width, layer.Pixels.Height);
                if (record.TryGetProperty("shape", out var shape) && shape.ValueKind == JsonValueKind.Object) layer.Shape = ReadShape(shape);
            }
            if (Text(record, "maskFile") is { } maskFile) layer.Mask = LoadMask(Resolve(folder, maskFile), layer, document);
            if (Guid.TryParse(Text(record, "parentID"), out var parent)) parents[id] = parent;
            if (Guid.TryParse(Text(record, "maskSourceID"), out var source)) clipSources[id] = source;
            layers[id] = layer;
            order.Add(layer);
        }

        // The manifest lists layers bottom to top, each group directly after its contents.
        foreach (var layer in order)
        {
            if (parents.TryGetValue(layer.Id, out var parentId) && layers.TryGetValue(parentId, out var parentLayer) && parentLayer.IsGroup
                && ReachesRoot(parentId)) parentLayer.Children.Add(layer);
            else document.Layers.Add(layer);
        }

        // A damaged manifest can make folders contain each other; such layers are kept at the top level instead of vanishing.
        bool ReachesRoot(Guid id)
        {
            for (var depth = 0; depth < 64; depth++)
            {
                if (!parents.TryGetValue(id, out var next)) return true;
                id = next;
            }
            return false;
        }
        // macOS links a clipped layer to its base by ID; here a clipped layer follows the sibling below it.
        foreach (var (id, _) in clipSources)
        {
            var siblings = document.SiblingsOf(id);
            if (siblings != null && siblings.IndexOf(layers[id]) > 0) layers[id].Clipped = true;
        }
        var active = Guid.TryParse(Text(root, "activeLayerID"), out var activeId) && layers.ContainsKey(activeId) ? activeId : document.Layers.LastOrDefault()?.Id;
        document.SetActive(active);
        return document;
    }

    private static string Resolve(string folder, string file)
    {
        if (file.Contains("..") || file.Contains('/') || file.Contains('\\')) throw new InvalidDataException("The project refers to an unsafe path.");
        var path = Path.Combine(folder, "images", file);
        if (!File.Exists(path)) throw new InvalidDataException($"An image inside the project is missing ({file}).");
        return path;
    }

    private static unsafe SKBitmap LoadMask(string path, Layer layer, Document document)
    {
        using var decoded = ImageFiles.Load(path);
        int width = decoded.Width, height = decoded.Height;
        // A uniform 1×1 mask stands for a whole untouched mask.
        if (width == 1 && height == 1)
        {
            width = layer.Pixels?.Width ?? document.Width;
            height = layer.Pixels?.Height ?? document.Height;
            return Pixels.NewMask(width, height, decoded.GetPixel(0, 0).Red);
        }
        var mask = Pixels.NewMask(width, height);
        byte* source = (byte*)decoded.GetPixels(), target = (byte*)mask.GetPixels();
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            target[(long)y * mask.RowBytes + x] = source[(long)y * decoded.RowBytes + x * 4];
        // Masks share their layer's pixel grid here; folder and adjustment masks share the document's.
        int wantWidth = layer.Pixels?.Width ?? document.Width, wantHeight = layer.Pixels?.Height ?? document.Height;
        if (width != wantWidth || height != wantHeight)
        {
            using var unscaled = mask;
            return Editing.EditorSession.Resample(unscaled, wantWidth, wantHeight);
        }
        return mask;
    }

    private static LayerTransform? ReadTransform(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var (x, y) = Pair(element, "origin");
        var (w, h) = Pair(element, "size");
        if (w < 1 || h < 1) return null;
        return new LayerTransform
        {
            X = x, Y = y, Width = w, Height = h, Rotation = Number(element, "rotation", 0),
            FlipHorizontal = element.TryGetProperty("flipX", out var fx) && fx.ValueKind == JsonValueKind.True,
            FlipVertical = element.TryGetProperty("flipY", out var fy) && fy.ValueKind == JsonValueKind.True
        };
    }

    /// <summary>CGPoint and CGSize are written as two-element arrays; older tools wrote objects.</summary>
    private static (double, double) Pair(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)) return (0, 0);
        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() >= 2) return (value[0].GetDouble(), value[1].GetDouble());
        if (value.ValueKind == JsonValueKind.Object)
            return (Number(value, "x", Number(value, "width", 0)), Number(value, "y", Number(value, "height", 0)));
        return (0, 0);
    }

    private static BlendMode ReadBlend(string? name) => name switch
    {
        "Multiply" => BlendMode.Multiply, "Screen" => BlendMode.Screen, "Overlay" => BlendMode.Overlay, "Darken" => BlendMode.Darken,
        "Lighten" => BlendMode.Lighten, "Difference" => BlendMode.Difference, "Color Dodge" => BlendMode.ColorDodge, "Color Burn" => BlendMode.ColorBurn,
        "Hue" => BlendMode.Hue, "Saturation" => BlendMode.Saturation, "Color" => BlendMode.Color, "Luminosity" => BlendMode.Luminosity,
        _ => BlendMode.Normal
    };

    private static ShapeStyle ReadShape(JsonElement shape)
    {
        var kind = Text(shape, "kind")?.ToLowerInvariant() switch { "ellipse" => ShapeKind.Ellipse, "roundedrectangle" or "rounded rectangle" => ShapeKind.RoundedRectangle, _ => ShapeKind.Rectangle };
        var radius = Number(shape, "cornerRadius", 0);
        if (kind == ShapeKind.Rectangle && radius > 0) kind = ShapeKind.RoundedRectangle;
        return new ShapeStyle(kind, (uint)UnitColor(shape), radius);
    }

    private static SKColor UnitColor(JsonElement color) => new(
        (byte)Math.Round(Math.Clamp(Number(color, "red", 0), 0, 1) * 255), (byte)Math.Round(Math.Clamp(Number(color, "green", 0), 0, 1) * 255),
        (byte)Math.Round(Math.Clamp(Number(color, "blue", 0), 0, 1) * 255));

    private static Adjustment? ReadAdjustment(JsonElement a)
    {
        switch (Text(a, "kind"))
        {
            case "Levels":
                var levels = new LevelsAdjustment();
                if (a.TryGetProperty("levels", out var l) && l.TryGetProperty("ranges", out var ranges) && ranges.ValueKind == JsonValueKind.Array)
                {
                    var i = 0;
                    foreach (var r in ranges.EnumerateArray().Take(4))
                        levels = levels.WithRange(i++, new LevelsRange
                        {
                            InputBlack = Number(r, "black", 0), InputWhite = Number(r, "white", 255), Gamma = Number(r, "gamma", 1),
                            OutputBlack = Number(r, "outputBlack", 0), OutputWhite = Number(r, "outputWhite", 255)
                        });
                }
                return levels;
            case "Curves":
                var curves = new CurvesAdjustment();
                if (a.TryGetProperty("curves", out var c) && c.TryGetProperty("channels", out var channels) && channels.ValueKind == JsonValueKind.Array)
                {
                    var i = 0;
                    foreach (var channel in channels.EnumerateArray().Take(4))
                        curves = curves.WithChannel(i++, channel.EnumerateArray().Select(p => new CurvePoint(Number(p, "x", 0), Number(p, "y", 0))).ToList());
                }
                return curves;
            case "Exposure":
                return a.TryGetProperty("exposureSettings", out var e)
                    ? new ExposureAdjustment { Exposure = Number(e, "exposure", 0), Offset = Number(e, "offset", 0), Gamma = Number(e, "gamma", 1) }
                    : new ExposureAdjustment();
            case "Gradient Map":
                if (!a.TryGetProperty("gradientMapSettings", out var g)) return new GradientMapAdjustment();
                return new GradientMapAdjustment
                {
                    Shadows = g.TryGetProperty("shadows", out var dark) ? (uint)UnitColor(dark) : 0xFF000000,
                    Highlights = g.TryGetProperty("highlights", out var light) ? (uint)UnitColor(light) : 0xFFFFFFFF,
                    Reversed = g.TryGetProperty("reversed", out var reversed) && reversed.ValueKind == JsonValueKind.True
                };
            case "Grain":
                if (!a.TryGetProperty("grainSettings", out var n)) return new GrainAdjustment();
                return new GrainAdjustment { Amount = Number(n, "amount", 25), Size = Number(n, "size", 1.5), Roughness = Number(n, "roughness", 50), Seed = (uint)Number(n, "seed", 0) };
            case "Hue/Saturation":
                var hue = new HueSaturationAdjustment { Colorize = a.TryGetProperty("colorize", out var colorize) && colorize.ValueKind == JsonValueKind.True };
                hue = hue.WithShift(HueRange.Master, new HslShift(Number(a, "hue", 0), Number(a, "saturation", 0), Number(a, "lightness", 0)));
                if (a.TryGetProperty("hsvSettings", out var hsv) && hsv.ValueKind == JsonValueKind.Object)
                {
                    hue = hue with { Colorize = hsv.TryGetProperty("colorize", out var hc) && hc.ValueKind == JsonValueKind.True };
                    if (hsv.TryGetProperty("adjustments", out var perRange))
                        foreach (var (name, value) in KeyedValues(perRange))
                            if (Enum.TryParse<HueRange>(name, ignoreCase: true, out var range))
                                hue = hue.WithShift(range, new HslShift(Number(value, "hue", 0), Number(value, "saturation", 0), Number(value, "lightness", 0)));
                }
                return hue;
            default:
                return null;
        }
    }

    /// <summary>Swift writes a dictionary keyed by an enum as a flat array of alternating keys and values.</summary>
    private static IEnumerable<(string Key, JsonElement Value)> KeyedValues(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject()) yield return (property.Name, property.Value);
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var items = element.EnumerateArray().ToList();
            for (var i = 0; i + 1 < items.Count; i += 2)
                if (items[i].ValueKind == JsonValueKind.String) yield return (items[i].GetString()!, items[i + 1]);
        }
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double Number(JsonElement parent, string name, double fallback) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && double.IsFinite(value.GetDouble()) ? value.GetDouble() : fallback;

    private static int Int(JsonElement parent, string name, int fallback) => (int)Math.Round(Number(parent, name, fallback));
}
