using Composa.Model;
using SkiaSharp;

namespace Composa.IO.Psd;

/// <summary>
/// Reads Photoshop <c>.psd</c> and <c>.psb</c> files following Adobe's published Photoshop File Formats Specification
/// (File Header, Color Mode Data, Image Resources, Layer and Mask Information, Image Data). This is an original
/// implementation of the <c>8BPS</c> header, layer records, PackBits and the additional layer information blocks;
/// nothing here is copied from or derived from GIMP, psd-tools or any other reader. A Large Document (PSB, header
/// version 2) is the same format with a handful of fields widened: the layer section and layer info lengths, each
/// channel's data length and a fixed set of additional-info keys carry 8-byte lengths, and PackBits row counts take 4.
/// </summary>
internal static class PsdReader
{
    public const int MaxLayers = 10_000;

    private static readonly byte[] Magic = "8BPS"u8.ToArray();

    public static bool Matches(ReadOnlySpan<byte> data) => data.Length >= 4 && data[..4].SequenceEqual(Magic);

    public static bool Matches(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            return stream.Read(head) == 4 && Matches(head);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>The channel IDs that are unpacked: transparency, red, green, blue and the user mask. Spot and other planes are skipped.</summary>
    private static bool Wanted(short id) => id is -1 or 0 or 1 or 2 or -2;

    /// <summary>The additional-info keys whose length is 8 bytes in a PSB even under an <c>8BIM</c> signature.</summary>
    private static readonly HashSet<string> LargeKeys = ["LMsk", "Lr16", "Lr32", "Layr", "Mt16", "Mt32", "Mtrn", "Alph", "FMsk", "lnk2", "FEid", "FXid", "PxSD"];

    /// <summary>A length field that is 4 bytes in a PSD and 8 in a PSB, refused when it would not fit an offset.</summary>
    private static long Length(ref PsdCursor cursor, bool largeDocument)
    {
        var value = largeDocument ? cursor.U64() : cursor.U32();
        if (value > int.MaxValue) throw PsdException.TooLarge();
        return (long)value;
    }

    public static PsdFile Read(ReadOnlySpan<byte> data, long pixelBudget)
    {
        var cursor = new PsdCursor(data);
        if (!Matches(data)) throw new PsdException("This is not a Photoshop file.");
        cursor.Skip(4);
        var version = cursor.U16();
        if (version is not (1 or 2)) throw new PsdException("This Photoshop file uses a format version Composa can't read.");
        var largeDocument = version == 2;
        cursor.Skip(6);
        _ = cursor.U16(); // channels in the merged image; read again with the image data
        var height = cursor.I32();
        var width = cursor.I32();
        var depth = cursor.U16();
        var mode = cursor.U16();
        // The canvas is a size, not an allocation: only its layers count against the budget, and only when the
        // file is flattened is the merged image (one canvas-sized surface) read at all.
        if (!DocumentLimits.FitsSurface(width, height)) throw PsdException.TooLarge();
        if (depth != 8 || mode != 3) throw new PsdException("Only 8-bit RGB Photoshop files can be imported.");
        var file = new PsdFile { Width = width, Height = height, LargeDocument = largeDocument };

        // Color mode data: only indexed and duotone files have any.
        cursor.Skip(cursor.U32());

        // Image resources: the resolution is the one block that matters here.
        var resourcesLength = cursor.U32();
        var resourcesEnd = cursor.Offset + resourcesLength;
        if (resourcesEnd > cursor.Length) throw PsdException.Truncated();
        while (cursor.Offset + 12 <= resourcesEnd)
        {
            if (cursor.Ascii(4) != "8BIM") break;
            var id = cursor.U16();
            var nameLength = cursor.U8();
            cursor.Skip(nameLength);
            if ((nameLength + 1) % 2 == 1) cursor.Skip(1);
            var length = cursor.U32();
            var dataStart = cursor.Offset;
            if (id == 1005 && length >= 4)
            {
                // Horizontal resolution as a 16.16 fixed-point number of pixels per inch.
                var resolution = cursor.U32() / 65536.0;
                file.Resolution = double.IsFinite(resolution) && resolution >= 1 ? Math.Min(9600, resolution) : 72;
            }
            cursor.Seek(dataStart + length);
            if (length % 2 == 1) cursor.Skip(1);
        }
        cursor.Seek(resourcesEnd);

        // Layer and mask information.
        var sectionLength = Length(ref cursor, largeDocument);
        var sectionEnd = cursor.Offset + sectionLength;
        if (sectionEnd > cursor.Length) throw PsdException.Truncated();
        var used = 0L;
        if (sectionLength >= (largeDocument ? 10 : 6))
        {
            _ = Length(ref cursor, largeDocument); // layer info length
            var count = Math.Abs((int)cursor.I16());
            if (count > MaxLayers) throw PsdException.TooLarge();
            for (var i = 0; i < count; i++) file.Layers.Add(ReadRecord(ref cursor, largeDocument));
            // A file whose layers fit imports whole, every pixel kept. Only one that would be refused falls back: each
            // layer and mask reaching past the canvas is cut to it and the budget checked again. The decision is made
            // from the records before a pixel is read, with the same test the decoder applies, so the two cannot disagree.
            if (!FitsBudget(file.Layers, pixelBudget))
            {
                foreach (var layer in file.Layers) CropToCanvas(layer, width, height);
                if (!FitsBudget(file.Layers, pixelBudget)) throw PsdException.TooLarge();
            }
            foreach (var layer in file.Layers)
            {
                DecodeChannels(ref cursor, layer, pixelBudget - used, largeDocument);
                if (layer.Image != null) used += (long)layer.Image.Width * layer.Image.Height;
                if (layer.MaskImage != null) used += (long)layer.MaskImage.Width * layer.MaskImage.Height;
            }
        }
        cursor.Seek(sectionEnd);

        // A flattened file has only the merged image to offer.
        if (file.Layers.Count == 0 && cursor.Remaining >= 2)
        {
            if ((long)width * height > pixelBudget - used) throw PsdException.TooLarge();
            file.Composite = ReadComposite(ref cursor, data, width, height, largeDocument);
        }
        return file;
    }

    private static PsdLayer ReadRecord(ref PsdCursor cursor, bool largeDocument)
    {
        var layer = new PsdLayer { Top = cursor.I32(), Left = cursor.I32(), Bottom = cursor.I32(), Right = cursor.I32() };
        (layer.SourceTop, layer.SourceLeft, layer.SourceBottom, layer.SourceRight) = (layer.Top, layer.Left, layer.Bottom, layer.Right);
        var channelCount = cursor.U16();
        if (channelCount > 56) throw PsdException.TooLarge();
        for (var i = 0; i < channelCount; i++)
        {
            var id = cursor.I16();
            layer.Channels.Add((id, (int)Length(ref cursor, largeDocument)));
        }
        if (cursor.Ascii(4) != "8BIM") throw PsdException.Truncated();
        layer.BlendKey = cursor.Ascii(4);
        layer.Opacity = cursor.U8();
        layer.Clipping = cursor.U8() != 0;
        var flags = cursor.U8();
        layer.Hidden = (flags & 2) != 0;
        cursor.Skip(1);
        var extraLength = cursor.U32();
        var extraEnd = cursor.Offset + extraLength;
        if (extraEnd > cursor.Length) throw PsdException.Truncated();

        var maskLength = cursor.U32();
        var maskEnd = cursor.Offset + maskLength;
        if (maskLength >= 20)
        {
            layer.HasMask = true;
            layer.MaskTop = cursor.I32();
            layer.MaskLeft = cursor.I32();
            layer.MaskBottom = cursor.I32();
            layer.MaskRight = cursor.I32();
            (layer.SourceMaskTop, layer.SourceMaskLeft, layer.SourceMaskBottom, layer.SourceMaskRight) = (layer.MaskTop, layer.MaskLeft, layer.MaskBottom, layer.MaskRight);
            layer.MaskDefault = cursor.U8();
            var maskFlags = cursor.U8();
            layer.MaskLinked = (maskFlags & 1) == 0;
            layer.MaskDisabled = (maskFlags & 2) != 0;
            layer.MaskFromRender = (maskFlags & 8) != 0;
        }
        cursor.Seek(maskEnd);
        cursor.Skip(cursor.U32()); // blending ranges
        var nameLength = cursor.U8();
        layer.Name = System.Text.Encoding.Latin1.GetString(cursor.Bytes(nameLength));
        cursor.Skip((4 - (nameLength + 1) % 4) % 4);

        while (cursor.Offset + 12 <= extraEnd)
        {
            var signature = cursor.Ascii(4);
            if (signature != "8BIM" && signature != "8B64") break;
            var key = cursor.Ascii(4);
            // An 8B64 block always has an 8-byte length; in a PSB a fixed set of keys has one under 8BIM too.
            var length = Length(ref cursor, signature == "8B64" || (largeDocument && LargeKeys.Contains(key)));
            if (cursor.Offset + length > extraEnd) throw PsdException.Truncated();
            layer.Extra[key] = cursor.Bytes(length).ToArray();
            if (length % 2 == 1 && cursor.Offset < extraEnd) cursor.Skip(1);
        }
        cursor.Seek(extraEnd);

        if (layer.Extra.TryGetValue("luni", out var unicode) && ReadUnicodeName(unicode) is { Length: > 0 } name) layer.Name = name;
        if (layer.Extra.TryGetValue("iOpa", out var fill) && fill.Length >= 1) layer.Fill = fill[0];
        if ((layer.Extra.TryGetValue("lsct", out var section) || layer.Extra.TryGetValue("lsdk", out section)) && section.Length >= 4)
            layer.Section = (int)new PsdCursor(section).U32();
        return layer;
    }

    private static string? ReadUnicodeName(byte[] data)
    {
        try { return new PsdCursor(data).Unicode(); }
        catch (PsdException) { return null; }
    }

    private static void DecodeChannels(ref PsdCursor cursor, PsdLayer layer, long remainingPixels, bool largeDocument)
    {
        int width = layer.Width, height = layer.Height, maskWidth = layer.MaskWidth, maskHeight = layer.MaskHeight;
        if (!FitsBudget(width, height, maskWidth, maskHeight, layer.HasMask, remainingPixels)) throw PsdException.TooLarge();
        var planes = new Dictionary<short, byte[]>();
        foreach (var (id, length) in layer.Channels)
        {
            var start = cursor.Offset;
            if (Wanted(id) && length >= 2)
            {
                var compression = cursor.U16();
                var payload = cursor.Bytes(length - 2);
                var isMask = id == -2;
                // The plane is laid out at the size the file gives; the crop picks the imported part out of it.
                int sourceW = isMask ? layer.SourceMaskWidth : layer.SourceWidth, sourceH = isMask ? layer.SourceMaskHeight : layer.SourceHeight;
                int w = isMask ? maskWidth : width, h = isMask ? maskHeight : height;
                if (w > 0 && h > 0) planes[id] = PsdChannels.Decode(compression, sourceW, sourceH, payload, largeDocument, isMask ? layer.MaskCrop : layer.ImageCrop);
            }
            cursor.Seek(start + (long)Math.Max(0, length));
        }
        if (layer.HasMask && maskWidth > 0 && maskHeight > 0 && planes.TryGetValue(-2, out var gray))
            layer.MaskImage = PsdChannels.MaskImage(maskWidth, maskHeight, gray);
        if (width <= 0 || height <= 0) return;
        layer.Image = PsdChannels.ColorImage(width, height, Plane(planes, 0), Plane(planes, 1), Plane(planes, 2), Plane(planes, -1));
    }

    private static bool FitsBudget(List<PsdLayer> layers, long pixelBudget)
    {
        var used = 0L;
        foreach (var layer in layers)
        {
            if (!FitsBudget(layer.Width, layer.Height, layer.MaskWidth, layer.MaskHeight, layer.HasMask, pixelBudget - used)) return false;
            used += (long)layer.Width * layer.Height;
            if (layer.HasMask) used += (long)layer.MaskWidth * layer.MaskHeight;
        }
        return true;
    }

    /// <summary>Cuts the layer's pixel rectangle and mask rectangle to the canvas, remembering which part of each plane that leaves.</summary>
    private static void CropToCanvas(PsdLayer layer, int canvasWidth, int canvasHeight)
    {
        var image = Crop(layer.Left, layer.Top, layer.Right, layer.Bottom, canvasWidth, canvasHeight);
        if (image is { } crop)
        {
            layer.Left += crop.X;
            layer.Top += crop.Y;
            layer.Right = layer.Left + crop.Width;
            layer.Bottom = layer.Top + crop.Height;
            layer.ImageCrop = crop;
            layer.Cropped = true;
        }
        if (!layer.HasMask) return;
        if (Crop(layer.MaskLeft, layer.MaskTop, layer.MaskRight, layer.MaskBottom, canvasWidth, canvasHeight) is { } maskCrop)
        {
            layer.MaskLeft += maskCrop.X;
            layer.MaskTop += maskCrop.Y;
            layer.MaskRight = layer.MaskLeft + maskCrop.Width;
            layer.MaskBottom = layer.MaskTop + maskCrop.Height;
            layer.MaskCrop = maskCrop;
            layer.Cropped = true;
        }
    }

    /// <summary>The part of a rectangle inside the canvas, as an offset and size within it, or null when nothing needs cutting.</summary>
    private static PsdCrop? Crop(int left, int top, int right, int bottom, int canvasWidth, int canvasHeight)
    {
        var croppedLeft = Math.Min(canvasWidth, Math.Max(0, left));
        var croppedTop = Math.Min(canvasHeight, Math.Max(0, top));
        var croppedRight = Math.Max(croppedLeft, Math.Min(canvasWidth, right));
        var croppedBottom = Math.Max(croppedTop, Math.Min(canvasHeight, bottom));
        var crop = new PsdCrop(croppedLeft - left, croppedTop - top, croppedRight - croppedLeft, croppedBottom - croppedTop);
        return crop.X == 0 && crop.Y == 0 && crop.Width == right - left && crop.Height == bottom - top ? null : crop;
    }

    /// <summary>Whether a layer's pixels and mask each fit one surface and, added together, what is left of the budget.</summary>
    private static bool FitsBudget(int width, int height, int maskWidth, int maskHeight, bool hasMask, long remainingPixels)
    {
        var needed = 0L;
        if (width > 0 && height > 0)
        {
            if (!DocumentLimits.FitsSurface(width, height)) return false;
            needed += (long)width * height;
        }
        if (hasMask && maskWidth > 0 && maskHeight > 0)
        {
            if (!DocumentLimits.FitsSurface(maskWidth, maskHeight)) return false;
            needed += (long)maskWidth * maskHeight;
        }
        return needed <= Math.Max(0, remainingPixels);
    }

    private static byte[]? Plane(Dictionary<short, byte[]> planes, short id) => planes.TryGetValue(id, out var plane) ? plane : null;

    /// <summary>The merged image at the end of the file: one compression method for all channels, planes in R, G, B, A order.</summary>
    private static SKBitmap ReadComposite(ref PsdCursor cursor, ReadOnlySpan<byte> data, int width, int height, bool largeDocument)
    {
        var channels = (int)new PsdCursor(data) { Offset = 12 }.U16();
        if (channels < 3) throw new PsdException("Only 8-bit RGB Photoshop files can be imported.");
        var compression = cursor.U16();
        var count = (long)width * height;
        var planes = new byte[Math.Min(channels, 4)][];
        switch (compression)
        {
            case PsdChannels.Raw:
                for (var c = 0; c < planes.Length; c++) planes[c] = cursor.Bytes(count).ToArray();
                break;
            case PsdChannels.Rle:
            {
                // Every row's byte count for every channel comes first, then the packed rows channel by channel.
                var counts = new int[channels * height];
                for (var i = 0; i < counts.Length; i++) counts[i] = PsdChannels.RowCount(ref cursor, largeDocument);
                var rest = data[cursor.Offset..];
                var consumed = 0;
                for (var c = 0; c < planes.Length; c++)
                {
                    planes[c] = new byte[count];
                    consumed += PsdChannels.UnpackRows(rest[consumed..], counts.AsSpan(c * height, height), width, height, planes[c], 0);
                }
                break;
            }
            default:
                throw new PsdException("This Photoshop file uses an image compression method that isn't supported.");
        }
        return PsdChannels.ColorImage(width, height, planes[0], planes[1], planes[2], planes.Length > 3 ? planes[3] : null);
    }
}
