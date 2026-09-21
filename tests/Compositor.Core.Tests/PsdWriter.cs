using System.Text;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>
/// Writes small Photoshop files for the tests, following Adobe's Photoshop File Formats Specification: header, image
/// resources, layer records with channels and additional information blocks, and the merged image.
/// </summary>
internal sealed class PsdWriter
{
    public int Width = 100, Height = 80;
    public double Resolution = 72;
    public ushort Version = 1, Depth = 8, Mode = 3, Channels = 3;
    /// <summary>The compression every channel uses unless a layer says otherwise: 0 raw, 1 PackBits, 2 zip, 3 zip with prediction.</summary>
    public int Compression = 1;
    /// <summary>Bottom to top, as the file lists them.</summary>
    public List<PsdWriterLayer> Layers = [];
    /// <summary>The merged image; transparent black when absent.</summary>
    public SKBitmap? Composite;

    public byte[] Build()
    {
        var file = new Buffer();
        file.Ascii("8BPS");
        file.U16(Version);
        file.Zeros(6);
        file.U16(Channels);
        file.U32((uint)Height);
        file.U32((uint)Width);
        file.U16(Depth);
        file.U16(Mode);
        file.U32(0); // color mode data
        var resources = new Buffer();
        resources.Ascii("8BIM");
        resources.U16(1005);
        resources.U8(0); resources.U8(0); // empty Pascal name, padded to even
        resources.U32(16);
        var fixedResolution = (uint)Math.Round(Resolution * 65536);
        resources.U32(fixedResolution); resources.U16(1); resources.U16(1);
        resources.U32(fixedResolution); resources.U16(1); resources.U16(1);
        file.Block(resources);

        var layerInfo = new Buffer();
        if (Layers.Count > 0)
        {
            layerInfo.I16((short)Layers.Count);
            var channelData = new List<byte[]>();
            foreach (var layer in Layers)
            {
                var channels = layer.ChannelData(layer.Compression ?? Compression);
                layerInfo.I32(layer.Top); layerInfo.I32(layer.Left); layerInfo.I32(layer.Top + layer.Height); layerInfo.I32(layer.Left + layer.Width);
                layerInfo.U16((ushort)channels.Count);
                foreach (var (id, data) in channels) { layerInfo.I16(id); layerInfo.U32((uint)data.Length); channelData.Add(data); }
                layerInfo.Ascii("8BIM");
                layerInfo.Ascii(layer.Blend.PadRight(4)[..4]);
                layerInfo.U8(layer.Opacity);
                layerInfo.U8(layer.Clipping ? (byte)1 : (byte)0);
                layerInfo.U8((byte)(layer.Hidden ? 2 : 0));
                layerInfo.U8(0);
                var extra = new Buffer();
                if (layer.Mask != null || layer.MaskDefaultOnly)
                {
                    extra.U32(20);
                    int mw = layer.Mask?.Width ?? 0, mh = layer.Mask?.Height ?? 0;
                    extra.I32(layer.MaskTop); extra.I32(layer.MaskLeft); extra.I32(layer.MaskTop + mh); extra.I32(layer.MaskLeft + mw);
                    extra.U8(layer.MaskDefault);
                    extra.U8((byte)((layer.MaskUnlinked ? 1 : 0) | (layer.MaskDisabled ? 2 : 0) | (layer.MaskFromRender ? 8 : 0)));
                    extra.Zeros(2);
                }
                else extra.U32(0);
                extra.U32(0); // blending ranges
                var name = Encoding.Latin1.GetBytes(layer.Name.Length > 255 ? layer.Name[..255] : layer.Name);
                extra.U8((byte)name.Length);
                extra.Bytes(name);
                extra.Zeros((4 - (name.Length + 1) % 4) % 4);
                foreach (var (key, data) in layer.Extra)
                {
                    extra.Ascii("8BIM");
                    extra.Ascii(key);
                    extra.U32((uint)data.Length);
                    extra.Bytes(data);
                    if (data.Length % 2 == 1) extra.U8(0);
                }
                layerInfo.Block(extra);
            }
            foreach (var data in channelData) layerInfo.Bytes(data);
        }
        var section = new Buffer();
        if (Layers.Count > 0) { section.Block(layerInfo); section.U32(0); }
        file.Block(section);

        // The merged image: one compression for all planes, R, G, B (and A with four channels).
        var planes = new List<byte[]>();
        for (var c = 0; c < Channels; c++) planes.Add(Composite == null ? new byte[Width * Height] : Plane(Composite, c switch { 0 => 0, 1 => 1, 2 => 2, _ => -1 }));
        file.U16((ushort)(Compression == 1 ? 1 : 0));
        if (Compression == 1)
        {
            var rows = planes.Select(p => PackRows(p, Width, Height)).ToList();
            foreach (var packed in rows) foreach (var row in packed) file.U16((ushort)row.Length);
            foreach (var packed in rows) foreach (var row in packed) file.Bytes(row);
        }
        else foreach (var plane in planes) file.Bytes(plane);
        return file.ToArray();
    }

    /// <summary>One straight (unpremultiplied) channel plane of a bitmap: 0 red, 1 green, 2 blue, -1 alpha, -2 the gray of a mask.</summary>
    public static byte[] Plane(SKBitmap bitmap, int channel)
    {
        var plane = new byte[bitmap.Width * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var color = bitmap.GetPixel(x, y);
            plane[y * bitmap.Width + x] = channel switch { 0 => color.Red, 1 => color.Green, 2 => color.Blue, -1 => color.Alpha, _ => bitmap.ColorType == SKColorType.Alpha8 ? color.Alpha : color.Red };
        }
        return plane;
    }

    public static List<byte[]> PackRows(byte[] plane, int width, int height)
    {
        var rows = new List<byte[]>();
        for (var y = 0; y < height; y++) rows.Add(PackBits(plane.AsSpan(y * width, width)));
        return rows;
    }

    /// <summary>PackBits: runs of three or more equal bytes as a repeat, everything else as literals of up to 128.</summary>
    public static byte[] PackBits(ReadOnlySpan<byte> row)
    {
        var output = new List<byte>();
        var i = 0;
        while (i < row.Length)
        {
            var run = 1;
            while (i + run < row.Length && run < 128 && row[i + run] == row[i]) run++;
            if (run >= 3) { output.Add((byte)(sbyte)(1 - run)); output.Add(row[i]); i += run; continue; }
            var start = i;
            while (i < row.Length && i - start < 128)
            {
                var ahead = 1;
                while (i + ahead < row.Length && ahead < 3 && row[i + ahead] == row[i]) ahead++;
                if (ahead >= 3) break;
                i++;
            }
            output.Add((byte)(i - start - 1));
            for (var j = start; j < i; j++) output.Add(row[j]);
        }
        return output.ToArray();
    }

    public static byte[] Zip(byte[] plane, int width, int height, bool predict)
    {
        var source = (byte[])plane.Clone();
        if (predict)
            for (var y = 0; y < height; y++)
                for (var x = width - 1; x >= 1; x--) source[y * width + x] = (byte)(source[y * width + x] - source[y * width + x - 1]);
        using var output = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(source);
        return output.ToArray();
    }

    // ---- Additional layer information blocks ------------------------------------------------------------------------

    public static byte[] Unicode(string text)
    {
        var buffer = new Buffer();
        buffer.U32((uint)text.Length + 1);
        buffer.Bytes(Encoding.BigEndianUnicode.GetBytes(text));
        buffer.U16(0);
        return buffer.ToArray();
    }

    public static byte[] Section(int type) { var b = new Buffer(); b.U32((uint)type); b.Ascii("8BIM"); b.Ascii(type == 3 ? "norm" : "pass"); return b.ToArray(); }
    public static byte[] FillOpacity(byte fill) { var b = new Buffer(); b.U8(fill); b.Zeros(3); return b.ToArray(); }

    public static byte[] SolidColor(SKColor color)
    {
        var buffer = new Buffer();
        buffer.U32(16);
        buffer.Bytes(new Descriptor().Add("Clr ", Descriptor.Objc(Rgb(color))).ToArray());
        return buffer.ToArray();
    }

    public static Descriptor Rgb(SKColor color) => new Descriptor().Add("Rd  ", Descriptor.Doub(color.Red)).Add("Grn ", Descriptor.Doub(color.Green)).Add("Bl  ", Descriptor.Doub(color.Blue));

    /// <summary>Vector origination: <paramref name="type"/> 1 rectangle, 2 rounded rectangle, 5 ellipse, over a box in canvas pixels.</summary>
    public static byte[] Origination(int type, SKRect box, double? radius = null, bool radiiFirst = false)
    {
        var shape = new Descriptor();
        var bbox = new Descriptor().Add("unitValueQuadVersion", Descriptor.Long(1))
            .Add("Top ", Descriptor.UntF("#Pxl", box.Top)).Add("Left", Descriptor.UntF("#Pxl", box.Left)).Add("Btom", Descriptor.UntF("#Pxl", box.Bottom)).Add("Rght", Descriptor.UntF("#Pxl", box.Right));
        Descriptor? radii = radius is { } r ? new Descriptor().Add("unitValueQuadVersion", Descriptor.Long(1))
            .Add("topRight", Descriptor.UntF("#Pxl", r)).Add("topLeft", Descriptor.UntF("#Pxl", r)).Add("bottomLeft", Descriptor.UntF("#Pxl", r)).Add("bottomRight", Descriptor.UntF("#Pxl", r)) : null;
        shape.Add("keyOriginType", Descriptor.Long(type));
        if (radiiFirst && radii != null) shape.Add("keyOriginRRectRadii", Descriptor.Objc(radii));
        shape.Add("keyOriginShapeBBox", Descriptor.Objc(bbox));
        if (!radiiFirst && radii != null) shape.Add("keyOriginRRectRadii", Descriptor.Objc(radii));
        shape.Add("keyOriginIndex", Descriptor.Long(0));
        var root = new Descriptor().Add("keyDescriptorList", Descriptor.List(Descriptor.Objc(shape)));
        var buffer = new Buffer();
        buffer.U32(1);
        buffer.U32(16);
        buffer.Bytes(root.ToArray());
        return buffer.ToArray();
    }

    /// <summary>Stroke settings: whether the fill and the stroke are on, and the stroke's width and color.</summary>
    public static byte[] StrokeSettings(bool fill, bool stroke, double width, SKColor color)
    {
        var settings = new Descriptor().Add("strokeStyleVersion", Descriptor.Long(2)).Add("fillEnabled", Descriptor.Bool(fill)).Add("strokeEnabled", Descriptor.Bool(stroke))
            .Add("strokeStyleLineWidth", Descriptor.UntF("#Pxl", width))
            .Add("strokeStyleContent", Descriptor.Objc(new Descriptor().Add("Clr ", Descriptor.Objc(Rgb(color)))));
        var buffer = new Buffer();
        buffer.U32(16);
        buffer.Bytes(settings.ToArray());
        return buffer.ToArray();
    }

    /// <summary>A vector mask holding one closed subpath through the corners, as fractions of the canvas.</summary>
    public static byte[] VectorMask(int canvasWidth, int canvasHeight, params SKPoint[] anchors)
    {
        var buffer = new Buffer();
        buffer.U32(3);
        buffer.U32(0);
        buffer.U16(6); buffer.Zeros(24); // path fill rule
        buffer.U16(8); buffer.Zeros(24); // initial fill rule
        buffer.U16(0); buffer.U16((ushort)anchors.Length); buffer.Zeros(22);
        foreach (var anchor in anchors)
        {
            buffer.U16(1);
            for (var i = 0; i < 3; i++) { buffer.I32(Fixed(anchor.Y / canvasHeight)); buffer.I32(Fixed(anchor.X / canvasWidth)); }
        }
        return buffer.ToArray();
    }

    private static int Fixed(double fraction) => (int)Math.Round(fraction * 0x1000000);

    public static byte[] Levels((int Black, int White, int OutBlack, int OutWhite, int Gamma100)[] ranges)
    {
        var buffer = new Buffer();
        buffer.U16(2);
        for (var i = 0; i < 29; i++)
        {
            var r = i < ranges.Length ? ranges[i] : (0, 255, 0, 255, 100);
            buffer.U16((ushort)r.Item1); buffer.U16((ushort)r.Item2); buffer.U16((ushort)r.Item3); buffer.U16((ushort)r.Item4); buffer.U16((ushort)r.Item5);
        }
        return buffer.ToArray();
    }

    public static byte[] Curves(params (int Channel, (int Input, int Output)[] Points)[] channels)
    {
        var buffer = new Buffer();
        buffer.U8(0);
        buffer.U16(1);
        uint mask = 0;
        foreach (var (channel, _) in channels) mask |= 1u << channel;
        buffer.U32(mask);
        foreach (var (_, points) in channels.OrderBy(c => c.Channel))
        {
            buffer.U16((ushort)points.Length);
            foreach (var (input, output) in points) { buffer.U16((ushort)output); buffer.U16((ushort)input); }
        }
        return buffer.ToArray();
    }

    public static byte[] HueSaturation(bool colorize, (int H, int S, int L) colorized, (int H, int S, int L) master, params (int H, int S, int L)[] ranges)
    {
        var buffer = new Buffer();
        buffer.U16(2);
        buffer.U8(colorize ? (byte)1 : (byte)0);
        buffer.U8(0);
        void Triple((int H, int S, int L) t) { buffer.I16((short)t.H); buffer.I16((short)t.S); buffer.I16((short)t.L); }
        Triple(colorized);
        Triple(master);
        for (var i = 0; i < 6; i++)
        {
            for (var j = 0; j < 4; j++) buffer.I16((short)(i * 60 + j * 15));
            Triple(i < ranges.Length ? ranges[i] : (0, 0, 0));
        }
        return buffer.ToArray();
    }

    public static byte[] BrightnessContrast(short brightness, short contrast) { var b = new Buffer(); b.I16(brightness); b.I16(contrast); b.I16(0); b.U8(0); b.U8(0); return b.ToArray(); }
    public static byte[] Exposure(float exposure, float offset, float gamma) { var b = new Buffer(); b.U16(1); b.F32(exposure); b.F32(offset); b.F32(gamma); return b.ToArray(); }

    // ---- Descriptor structure ----------------------------------------------------------------------------------------

    public sealed class Descriptor
    {
        private readonly List<(string Key, byte[] Item)> items = [];

        public Descriptor Add(string key, byte[] item) { items.Add((key, item)); return this; }

        public byte[] ToArray()
        {
            var buffer = new Buffer();
            buffer.U32(0);        // empty class name
            buffer.Key("null");   // class ID
            buffer.U32((uint)items.Count);
            foreach (var (key, item) in items) { buffer.Key(key); buffer.Bytes(item); }
            return buffer.ToArray();
        }

        public static byte[] Doub(double value) { var b = new Buffer(); b.Ascii("doub"); b.F64(value); return b.ToArray(); }
        public static byte[] UntF(string unit, double value) { var b = new Buffer(); b.Ascii("UntF"); b.Ascii(unit); b.F64(value); return b.ToArray(); }
        public static byte[] Bool(bool value) { var b = new Buffer(); b.Ascii("bool"); b.U8(value ? (byte)1 : (byte)0); return b.ToArray(); }
        public static byte[] Long(int value) { var b = new Buffer(); b.Ascii("long"); b.I32(value); return b.ToArray(); }
        public static byte[] Objc(Descriptor descriptor) { var b = new Buffer(); b.Ascii("Objc"); b.Bytes(descriptor.ToArray()); return b.ToArray(); }
        public static byte[] List(params byte[][] items) { var b = new Buffer(); b.Ascii("VlLs"); b.U32((uint)items.Length); foreach (var item in items) b.Bytes(item); return b.ToArray(); }
    }

    public sealed class Buffer
    {
        private readonly MemoryStream stream = new();
        public void U8(byte value) => stream.WriteByte(value);
        public void U16(ushort value) { stream.WriteByte((byte)(value >> 8)); stream.WriteByte((byte)value); }
        public void I16(short value) => U16((ushort)value);
        public void U32(uint value) { U16((ushort)(value >> 16)); U16((ushort)value); }
        public void I32(int value) => U32((uint)value);
        public void F64(double value) { var bits = (ulong)BitConverter.DoubleToInt64Bits(value); U32((uint)(bits >> 32)); U32((uint)bits); }
        public void F32(float value) => U32((uint)BitConverter.SingleToInt32Bits(value));
        public void Ascii(string text) => Bytes(Encoding.ASCII.GetBytes(text));
        public void Bytes(ReadOnlySpan<byte> bytes) => stream.Write(bytes);
        public void Zeros(int count) { for (var i = 0; i < count; i++) stream.WriteByte(0); }
        /// <summary>A four-character key as a zero length, any other as its length.</summary>
        public void Key(string key) { if (key.Length == 4) U32(0); else U32((uint)key.Length); Ascii(key); }
        /// <summary>A length-prefixed block.</summary>
        public void Block(Buffer inner) { var bytes = inner.ToArray(); U32((uint)bytes.Length); Bytes(bytes); }
        public byte[] ToArray() => stream.ToArray();
    }
}

internal sealed class PsdWriterLayer
{
    public string Name = "Layer";
    public SKBitmap? Image;
    public int Left, Top;
    /// <summary>The pixel area when there is no image; 0 makes an empty layer.</summary>
    public int EmptyWidth, EmptyHeight;
    public byte Opacity = 255;
    public bool Clipping;
    public bool Hidden;
    public string Blend = "norm";
    public bool IncludeAlpha = true;
    public int? Compression;
    public SKBitmap? Mask;
    public bool MaskDefaultOnly;
    public int MaskLeft, MaskTop;
    public byte MaskDefault = 255;
    public bool MaskDisabled, MaskUnlinked, MaskFromRender;
    public List<(string Key, byte[] Data)> Extra = [];

    public int Width => Image?.Width ?? EmptyWidth;
    public int Height => Image?.Height ?? EmptyHeight;

    public PsdWriterLayer With(string key, byte[] data) { Extra.Add((key, data)); return this; }

    public List<(short Id, byte[] Data)> ChannelData(int compression)
    {
        var channels = new List<(short, byte[])>();
        if (Image != null)
        {
            if (IncludeAlpha) channels.Add((-1, Encode(PsdWriter.Plane(Image, -1), Image.Width, Image.Height, compression)));
            for (short c = 0; c < 3; c++) channels.Add((c, Encode(PsdWriter.Plane(Image, c), Image.Width, Image.Height, compression)));
        }
        else
        {
            for (short c = 0; c < 3; c++) channels.Add((c, Encode([], 0, 0, compression)));
        }
        if (Mask != null) channels.Add((-2, Encode(PsdWriter.Plane(Mask, -2), Mask.Width, Mask.Height, compression)));
        return channels;
    }

    private static byte[] Encode(byte[] plane, int width, int height, int compression)
    {
        var buffer = new PsdWriter.Buffer();
        if (width == 0 || height == 0) { buffer.U16(0); return buffer.ToArray(); }
        buffer.U16((ushort)compression);
        switch (compression)
        {
            case 0: buffer.Bytes(plane); break;
            case 1:
                var rows = PsdWriter.PackRows(plane, width, height);
                foreach (var row in rows) buffer.U16((ushort)row.Length);
                foreach (var row in rows) buffer.Bytes(row);
                break;
            default: buffer.Bytes(PsdWriter.Zip(plane, width, height, compression == 3)); break;
        }
        return buffer.ToArray();
    }
}
