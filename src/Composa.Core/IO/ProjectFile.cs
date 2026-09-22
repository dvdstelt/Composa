using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Composa.Filters;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.IO;

/// <summary>
/// The project format: a zip archive holding <c>manifest.json</c> and an <c>images/</c> folder with one PNG per
/// layer (and one grayscale PNG per mask). Source pixels are stored untouched; transforms stay separate.
/// </summary>
public static class ProjectFile
{
    public const string Extension = ".cmps";
    public const string Format = "org.composa.project";
    public const int Version = 2;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private sealed class Manifest
    {
        public string Format { get; set; } = ProjectFile.Format;
        public int Version { get; set; } = ProjectFile.Version;
        public string ColorSpace { get; set; } = "sRGB";
        public int Width { get; set; }
        public int Height { get; set; }
        public double Resolution { get; set; } = 72;
        public Guid? ActiveLayerId { get; set; }
        public List<LayerRecord> Layers { get; set; } = [];
        /// <summary>Alignment guides; absent on version 1 files.</summary>
        public List<Guide>? Guides { get; set; }
    }

    private sealed class LayerRecord
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public LayerKind Kind { get; set; }
        public bool Visible { get; set; } = true;
        public double Opacity { get; set; } = 1;
        public BlendMode Blend { get; set; }
        public LayerTransform? Transform { get; set; }
        public string? ImageFile { get; set; }
        public string? MaskFile { get; set; }
        public bool? MaskEnabled { get; set; }
        public bool? Clipped { get; set; }
        public bool? Collapsed { get; set; }
        public Adjustment? Adjustment { get; set; }
        public ShapeStyle? Shape { get; set; }
        public TextStyle? Text { get; set; }
        public LayerEffects? Effects { get; set; }
        public List<LayerRecord>? Children { get; set; }
    }

    public static void Save(Document document, string path)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var stream = File.Create(temp)) Write(document, stream);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static void Write(Document document, Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        var written = new Dictionary<SKBitmap, string>(ReferenceEqualityComparer.Instance);

        string Store(SKBitmap bitmap, string name)
        {
            if (written.TryGetValue(bitmap, out var existing)) return existing;
            var entry = zip.CreateEntry("images/" + name, CompressionLevel.NoCompression);
            using var output = entry.Open();
            var bytes = bitmap.ColorType == SKColorType.Alpha8 ? EncodeMask(bitmap) : ImageFiles.Encode(bitmap, ExportFormat.Png);
            output.Write(bytes);
            return written[bitmap] = name;
        }

        LayerRecord Record(Layer layer) => new()
        {
            Id = layer.Id, Name = layer.Name, Kind = layer.Kind, Visible = layer.Visible, Opacity = layer.Opacity, Blend = layer.Blend,
            Transform = layer.Pixels != null ? layer.Transform : null,
            ImageFile = layer.Pixels != null ? Store(layer.Pixels, $"{layer.Id}.png") : null,
            MaskFile = layer.Mask != null ? Store(layer.Mask, $"{layer.Id}.mask.png") : null,
            MaskEnabled = layer.Mask != null ? layer.MaskEnabled : null,
            Clipped = layer.Clipped ? true : null,
            Collapsed = layer.Collapsed ? true : null,
            Adjustment = layer.Adjustment, Shape = layer.Shape, Text = layer.Text, Effects = layer.Effects,
            Children = layer.IsGroup ? layer.Children.Select(Record).ToList() : null
        };

        var manifest = new Manifest
        {
            Width = document.Width, Height = document.Height, Resolution = document.Resolution,
            ActiveLayerId = document.ActiveLayerId, Layers = document.Layers.Select(Record).ToList(),
            Guides = document.Guides.Count > 0 ? document.Guides.ToList() : null
        };
        using var manifestStream = zip.CreateEntry("manifest.json").Open();
        JsonSerializer.Serialize(manifestStream, manifest, Json);
    }

    public static Document Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static Document Read(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("This is not a Composa project: it has no manifest.");
        if (entry.Length > 4 * 1024 * 1024) throw new InvalidDataException("The project's manifest is too large.");
        Manifest manifest;
        using (var manifestStream = entry.Open())
            manifest = JsonSerializer.Deserialize<Manifest>(manifestStream, Json) ?? throw new InvalidDataException("The project's manifest is empty.");
        if (manifest.Format != Format) throw new InvalidDataException("This is not a Composa project.");
        if (manifest.Version > Version) throw new InvalidDataException($"This project uses format version {manifest.Version}; this app supports up to version {Version}.");
        if (manifest.Width < 1 || manifest.Height < 1 || manifest.Width > Document.MaxSide || manifest.Height > Document.MaxSide)
            throw new InvalidDataException("The project's canvas size is invalid.");

        var document = new Document(manifest.Width, manifest.Height) { Resolution = Math.Clamp(manifest.Resolution, 1, 9600) };
        var cache = new Dictionary<string, SKBitmap>();
        var count = 0;

        SKBitmap Fetch(string name, bool mask)
        {
            if (name.Contains("..") || name.Contains('/') || name.Contains('\\')) throw new InvalidDataException("The project refers to an unsafe path.");
            if (cache.TryGetValue(name, out var cached)) return cached;
            var image = zip.GetEntry("images/" + name) ?? throw new InvalidDataException($"An image inside the project is missing ({name}).");
            using var buffer = new MemoryStream();
            using (var input = image.Open()) input.CopyTo(buffer);
            buffer.Position = 0;
            return cache[name] = mask ? DecodeMask(buffer) : ImageFiles.Load(buffer, name);
        }

        Layer Build(LayerRecord record, int depth)
        {
            if (++count > 10_000 || depth > 64) throw new InvalidDataException("The project has too many layers.");
            var layer = new Layer
            {
                Id = record.Id == Guid.Empty ? Guid.NewGuid() : record.Id, Name = record.Name, Kind = record.Kind, Visible = record.Visible,
                Opacity = double.IsFinite(record.Opacity) ? Math.Clamp(record.Opacity, 0, 1) : 1, Blend = record.Blend,
                MaskEnabled = record.MaskEnabled ?? true, Clipped = record.Clipped ?? false, Collapsed = record.Collapsed ?? false,
                Adjustment = record.Adjustment, Shape = record.Shape, Text = record.Text
            };
            if (record.ImageFile != null && record.Kind == LayerKind.Raster)
            {
                layer.Pixels = Fetch(record.ImageFile, mask: false);
                layer.Transform = IsUsable(record.Transform) ? record.Transform! : LayerTransform.Identity(layer.Pixels.Width, layer.Pixels.Height);
                if (record.Effects is { } effects && !effects.IsEmpty) layer.Effects = effects.Clamped();
            }
            else if (record.Kind == LayerKind.Raster) throw new InvalidDataException($"Layer \"{record.Name}\" has no image.");
            if (record.Kind == LayerKind.Adjustment && record.Adjustment == null) throw new InvalidDataException($"Adjustment layer \"{record.Name}\" has no settings.");
            if (record.MaskFile != null) layer.Mask = Fetch(record.MaskFile, mask: true);
            if (record.Kind == LayerKind.Group && record.Children != null)
                foreach (var child in record.Children) layer.Children.Add(Build(child, depth + 1));
            return layer;
        }

        foreach (var record in manifest.Layers) document.Layers.Add(Build(record, 0));
        if (manifest.Guides != null)
            foreach (var guide in manifest.Guides.Where(g => g.IsValid).Take(1000))
                document.Guides.Add(guide.Id == Guid.Empty ? guide with { Id = Guid.NewGuid() } : guide);
        var active = manifest.ActiveLayerId is { } id && document.Find(id) != null ? id : document.Layers.LastOrDefault()?.Id;
        document.SetActive(active);
        return document;
    }

    /// <summary>A damaged file must not feed non-finite or degenerate placements into rendering.</summary>
    private static bool IsUsable(LayerTransform? t) =>
        t != null && new[] { t.X, t.Y, t.Width, t.Height, t.Rotation }.All(double.IsFinite)
        && t.Width >= 1 && t.Height >= 1 && t.Width <= 1_000_000 && t.Height <= 1_000_000 && Math.Abs(t.X) <= 10_000_000 && Math.Abs(t.Y) <= 10_000_000
        && (t.Distort == null || (t.Distort.Length == 8 && t.Distort.All(float.IsFinite)));

    private static byte[] EncodeMask(SKBitmap mask)
    {
        using var gray = new SKBitmap(new SKImageInfo(mask.Width, mask.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        CopyRows(mask, gray);
        using var data = gray.Encode(SKEncodedImageFormat.Png, 100) ?? throw new IOException("A mask could not be encoded.");
        return data.ToArray();
    }

    private static SKBitmap DecodeMask(Stream stream)
    {
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("A mask inside the project is damaged.");
        using var gray = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        if (codec.GetPixels(gray.Info, gray.GetPixels()) != SKCodecResult.Success) throw new InvalidDataException("A mask inside the project is damaged.");
        var mask = Pixels.NewMask(gray.Width, gray.Height);
        CopyRows(gray, mask);
        return mask;
    }

    private static unsafe void CopyRows(SKBitmap from, SKBitmap to)
    {
        for (var y = 0; y < from.Height; y++)
            Buffer.MemoryCopy((byte*)from.GetPixels() + (long)y * from.RowBytes, (byte*)to.GetPixels() + (long)y * to.RowBytes, to.RowBytes, from.Width);
    }
}
