namespace Composa.Model;

/// <summary>
/// The size and memory ceilings a document is held to, in one place. Two ideas that once shared a number are kept
/// apart here: how large a <em>single</em> surface may be (a canvas, a layer, a mask, an export) and how much raster a
/// <em>whole document</em> may hold across all of its layers. A 58-megapixel print banner carrying 29 layers is an
/// ordinary Photoshop document and needs far more than one surface's worth of allowance, even though no single
/// surface in it is unusual. Both pixel ceilings stay below the side limit squared, so a square at the side limit is
/// still refused as oversized, which is how several tests express "too large".
/// </summary>
public static class DocumentLimits
{
    /// <summary>Longest side, in pixels, of any canvas, layer, mask or generated surface.</summary>
    public const int MaxSide = 30_000;

    /// <summary>Largest single surface. At four bytes a pixel one allocation is at most 800 MB, and a filter holds a few of them at once.</summary>
    public const long MaxSurfacePixels = 200_000_000;

    /// <summary>
    /// Total raster one document may hold, summed over every imported layer and mask. Only documents that genuinely
    /// contain this much ever reach it. Scaled to the machine: a quarter of its memory at four bytes a pixel (about
    /// 500 megapixels on 8 GB), never less than one surface and never more than 800 megapixels, which 16 GB reaches.
    /// </summary>
    public static long DocumentPixelBudget { get; } = Math.Clamp(PhysicalMemory() / 16, MaxSurfacePixels, 800_000_000);

    /// <summary>The ceilings as megapixels, for the messages that quote them back to the reader.</summary>
    public static int MaxSurfaceMegapixels => (int)(MaxSurfacePixels / 1_000_000);
    public static int DocumentBudgetMegapixels => (int)(DocumentPixelBudget / 1_000_000);

    /// <summary>Whether one surface of these dimensions may exist at all.</summary>
    public static bool FitsSurface(long width, long height) =>
        width >= 1 && height >= 1 && width <= MaxSide && height <= MaxSide && width * height <= MaxSurfacePixels;

    private static long PhysicalMemory()
    {
        // The GC knows the machine's memory, or the container's limit when there is one; either is the right ceiling.
        var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return total > 0 ? total : 8L * 1024 * 1024 * 1024;
    }
}
