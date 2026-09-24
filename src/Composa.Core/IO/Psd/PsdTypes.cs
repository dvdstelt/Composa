using Composa.Model;
using SkiaSharp;

namespace Composa.IO.Psd;

/// <summary>A Photoshop file that cannot be read, with the reason in words a user can act on.</summary>
public sealed class PsdException(string message) : IOException(message)
{
    public static PsdException Truncated() => new("The Photoshop file could not be read. It may be damaged or incomplete.");
    public static PsdException TooLarge() => new($"The Photoshop file is larger than Composa can hold: {DocumentLimits.MaxSide:N0} pixels a side and {DocumentLimits.MaxSurfaceMegapixels} megapixels for any one layer, {DocumentLimits.DocumentBudgetMegapixels} megapixels of layers in all.");
}

/// <summary>One thing that had to change on the way in, reported per layer before anything is applied.</summary>
public sealed record PsdConversion(string LayerName, string Message);

/// <summary>What a Photoshop layer was, judged from the extra data it carries.</summary>
internal enum PsdLayerKind { Raster, Group, Adjustment, Text, SmartObject, Vector, Fill, Other }

/// <summary>The whole file as read: the canvas, its layers bottom to top (dividers included) and, for a flattened file, the merged image.</summary>
internal sealed class PsdFile
{
    public int Width;
    public int Height;
    public double Resolution = 72;
    /// <summary>A Large Document (<c>.psb</c>, header version 2).</summary>
    public bool LargeDocument;
    public List<PsdLayer> Layers = [];
    /// <summary>The merged image, decoded only when the file has no layers of its own.</summary>
    public SKBitmap? Composite;
}

/// <summary>The part of a channel's plane that is decoded: an offset into the plane as the file stores it, and a size.</summary>
internal readonly record struct PsdCrop(int X, int Y, int Width, int Height);

/// <summary>A layer record with its channels decoded, still in Photoshop's terms.</summary>
internal sealed class PsdLayer
{
    public string Name = "";
    /// <summary>The pixel rectangle as it is imported. Cropping to the canvas shrinks it; <see cref="SourceTop"/> and the others keep what the file says.</summary>
    public int Top, Left, Bottom, Right;
    public int SourceTop, SourceLeft, SourceBottom, SourceRight;
    public byte Opacity = 255;
    public byte Fill = 255;
    public bool Clipping;
    public bool Hidden;
    public string BlendKey = "norm";
    public List<(short Id, int Length)> Channels = [];
    public Dictionary<string, byte[]> Extra = [];
    public bool HasMask;
    public int MaskTop, MaskLeft, MaskBottom, MaskRight;
    public int SourceMaskTop, SourceMaskLeft, SourceMaskBottom, SourceMaskRight;
    /// <summary>The part of the file's planes that is read, once the layer has been cropped to the canvas; null reads them whole.</summary>
    public PsdCrop? ImageCrop, MaskCrop;
    /// <summary>The layer or its mask reached past the canvas and was cut to it so the file would fit in memory.</summary>
    public bool Cropped;
    public byte MaskDefault = 255;
    public bool MaskDisabled;
    public bool MaskLinked = true;
    /// <summary>The mask came from rendering other data (a vector mask's raster), so it is not the user's own.</summary>
    public bool MaskFromRender;
    /// <summary>Section divider type from <c>lsct</c>: 1 open folder, 2 closed folder, 3 the hidden divider under a folder's contents.</summary>
    public int? Section;
    /// <summary>Premultiplied RGBA pixels over <see cref="Left"/>, <see cref="Top"/>; null when the layer has no pixel area.</summary>
    public SKBitmap? Image;
    /// <summary>Alpha8 over the mask rectangle; null without a mask or with an empty one.</summary>
    public SKBitmap? MaskImage;

    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
    public int MaskWidth => Math.Max(0, MaskRight - MaskLeft);
    public int MaskHeight => Math.Max(0, MaskBottom - MaskTop);
    public int SourceWidth => Math.Max(0, SourceRight - SourceLeft);
    public int SourceHeight => Math.Max(0, SourceBottom - SourceTop);
    public int SourceMaskWidth => Math.Max(0, SourceMaskRight - SourceMaskLeft);
    public int SourceMaskHeight => Math.Max(0, SourceMaskBottom - SourceMaskTop);
    public bool IsGroup => Section is 1 or 2;
    public bool IsDivider => Section == 3;
}

/// <summary>Big-endian reads over the file's bytes, refusing to run past the end.</summary>
internal ref struct PsdCursor(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> data = data;
    public int Offset;

    public int Length => data.Length;
    public int Remaining => data.Length - Offset;

    private void Need(long count)
    {
        if (count < 0 || Offset < 0 || Offset + count > data.Length) throw PsdException.Truncated();
    }

    public void Skip(long count) { Need(count); Offset += (int)count; }
    public void Seek(long offset) { if (offset < 0 || offset > data.Length) throw PsdException.Truncated(); Offset = (int)offset; }

    public byte U8() { Need(1); return data[Offset++]; }
    public ushort U16() { Need(2); var v = (ushort)(data[Offset] << 8 | data[Offset + 1]); Offset += 2; return v; }
    public short I16() => (short)U16();
    public uint U32() { Need(4); var v = (uint)data[Offset] << 24 | (uint)data[Offset + 1] << 16 | (uint)data[Offset + 2] << 8 | data[Offset + 3]; Offset += 4; return v; }
    public int I32() => (int)U32();
    public ulong U64() { var hi = U32(); var lo = U32(); return (ulong)hi << 32 | lo; }
    public double F64() => BitConverter.Int64BitsToDouble((long)U64());
    public float F32() => BitConverter.Int32BitsToSingle(I32());

    public ReadOnlySpan<byte> Bytes(long count) { Need(count); var slice = data.Slice(Offset, (int)count); Offset += (int)count; return slice; }
    public string Ascii(int count) => System.Text.Encoding.ASCII.GetString(Bytes(count));

    /// <summary>A length-prefixed run of UTF-16 characters, as descriptors and <c>luni</c> hold them.</summary>
    public string Unicode()
    {
        var count = U32();
        if (count > 1_000_000) throw PsdException.Truncated();
        var text = System.Text.Encoding.BigEndianUnicode.GetString(Bytes(count * 2L));
        return text.TrimEnd('\0');
    }

    /// <summary>A key or class ID: a 4-byte length, or 0 meaning four characters.</summary>
    public string Key()
    {
        var length = U32();
        if (length > 4096) throw PsdException.Truncated();
        return Ascii(length == 0 ? 4 : (int)length);
    }
}
