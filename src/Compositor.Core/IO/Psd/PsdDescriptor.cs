namespace Compositor.IO.Psd;

/// <summary>
/// Reads Photoshop's descriptor structure (the typed key/value tree that vector origination, solid fills and stroke
/// settings are stored as) into dictionaries. Unknown item types end the read; whatever was read before is kept.
/// </summary>
internal static class PsdDescriptor
{
    /// <summary>A descriptor after a 4-byte version, as most additional layer information blocks store them.</summary>
    public static Dictionary<string, object?>? ReadVersioned(byte[] data)
    {
        try
        {
            var cursor = new PsdCursor(data);
            var version = cursor.U32();
            if (version != 16) return null;
            return Read(ref cursor);
        }
        catch (PsdException) { return null; }
        catch (UnknownItem) { return null; }
    }

    public static Dictionary<string, object?>? TryRead(ReadOnlySpan<byte> data)
    {
        try
        {
            var cursor = new PsdCursor(data);
            return Read(ref cursor);
        }
        catch (PsdException) { return null; }
        catch (UnknownItem) { return null; }
    }

    private sealed class UnknownItem : Exception;

    private static Dictionary<string, object?> Read(ref PsdCursor cursor)
    {
        _ = cursor.Unicode(); // class name
        _ = cursor.Key();     // class ID
        var count = cursor.U32();
        if (count > 10_000) throw PsdException.Truncated();
        var items = new Dictionary<string, object?>();
        for (var i = 0; i < count; i++)
        {
            var key = cursor.Key();
            items[key] = Item(ref cursor, cursor.Ascii(4));
        }
        return items;
    }

    private static object? Item(ref PsdCursor cursor, string type)
    {
        switch (type)
        {
            case "Objc":
            case "GlbO":
                return Read(ref cursor);
            case "VlLs":
            {
                var count = cursor.U32();
                if (count > 100_000) throw PsdException.Truncated();
                var list = new List<object?>();
                for (var i = 0; i < count; i++) list.Add(Item(ref cursor, cursor.Ascii(4)));
                return list;
            }
            case "doub": return cursor.F64();
            case "UntF": cursor.Skip(4); return cursor.F64();
            case "UnFl":
            {
                cursor.Skip(4);
                var count = cursor.U32();
                if (count > 100_000) throw PsdException.Truncated();
                var list = new List<object?>();
                for (var i = 0; i < count; i++) list.Add(cursor.F64());
                return list;
            }
            case "TEXT": return cursor.Unicode();
            case "enum": _ = cursor.Key(); return cursor.Key();
            case "long": return (double)cursor.I32();
            case "comp": return (double)(long)cursor.U64();
            case "bool": return cursor.U8() != 0;
            case "type":
            case "GlbC":
                _ = cursor.Unicode(); return cursor.Key();
            case "alis":
            case "tdta":
            case "Pth ":
                cursor.Skip(cursor.U32()); return null;
            default:
                throw new UnknownItem();
        }
    }

    public static double? Number(Dictionary<string, object?>? items, string key) => items != null && items.TryGetValue(key, out var v) && v is double d && double.IsFinite(d) ? d : null;
    public static bool? Flag(Dictionary<string, object?>? items, string key) => items != null && items.TryGetValue(key, out var v) && v is bool b ? b : null;
    public static Dictionary<string, object?>? Child(Dictionary<string, object?>? items, string key) => items != null && items.TryGetValue(key, out var v) ? v as Dictionary<string, object?> : null;
    public static List<object?>? List(Dictionary<string, object?>? items, string key) => items != null && items.TryGetValue(key, out var v) ? v as List<object?> : null;

    /// <summary>A <c>Clr </c> record's red, green and blue, given as 0 to 255 (or 0 to 1 by older writers), as an opaque ARGB color.</summary>
    public static uint? Color(Dictionary<string, object?>? color)
    {
        if (Number(color, "Rd  ") is not { } r || Number(color, "Grn ") is not { } g || Number(color, "Bl  ") is not { } b) return null;
        byte Channel(double value) => (byte)Math.Round(value > 1 ? Math.Clamp(value, 0, 255) : Math.Clamp(value, 0, 1) * 255);
        return 0xFF000000u | (uint)Channel(r) << 16 | (uint)Channel(g) << 8 | Channel(b);
    }
}
