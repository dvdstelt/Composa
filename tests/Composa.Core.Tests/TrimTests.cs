using Composa.Editing;
using Composa.Rendering;
using SkiaSharp;
using static Composa.Core.Tests.TestImages;

namespace Composa.Core.Tests;

public class TrimTests
{
    private static SKBitmap Painted(int width, int height, Func<int, int, SKColor> color)
    {
        var bitmap = Pixels.NewColor(width, height);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++) bitmap.SetPixel(x, y, color(x, y));
        return bitmap;
    }

    [Fact]
    public void Transparent_edges_are_measured_only_on_the_sides_asked_for()
    {
        // Content covers x 2…17 and y 4…15 of a 20 × 20 picture.
        using var image = Painted(20, 20, (x, y) => x >= 2 && x < 17 && y >= 4 && y < 15 ? SKColors.Red : SKColors.Transparent);
        Assert.Equal(new SKRectI(2, 4, 17, 15), ImageTrim.Bounds(image, new TrimOptions()));
        Assert.Equal(new SKRectI(0, 4, 20, 15), ImageTrim.Bounds(image, new TrimOptions { Left = false, Right = false }));
        Assert.Equal(new SKRectI(2, 0, 17, 20), ImageTrim.Bounds(image, new TrimOptions { Top = false, Bottom = false }));
        Assert.Null(ImageTrim.Bounds(image, new TrimOptions { Top = false, Bottom = false, Left = false, Right = false }));
    }

    [Fact]
    public void Corner_colors_trim_a_solid_border_within_the_tolerance()
    {
        using var image = Painted(16, 16, (x, y) => x >= 3 && x < 13 && y >= 3 && y < 13 ? SKColors.Yellow : SKColors.Blue);
        Assert.Equal(new SKRectI(3, 3, 13, 13), ImageTrim.Bounds(image, new TrimOptions { BasedOn = TrimBasedOn.TopLeftPixelColor }));
        Assert.Equal(new SKRectI(0, 3, 16, 16), ImageTrim.Bounds(image, new TrimOptions { BasedOn = TrimBasedOn.TopLeftPixelColor, Bottom = false, Left = false, Right = false }));
        using var corner = Painted(16, 16, (x, y) => x >= 12 || y >= 12 ? SKColors.Magenta : SKColors.Cyan);
        Assert.Equal(new SKRectI(0, 0, 12, 12), ImageTrim.Bounds(corner, new TrimOptions { BasedOn = TrimBasedOn.BottomRightPixelColor }));
        // A border that is nearly the corner's color goes with it when the tolerance allows.
        using var noisy = Painted(16, 16, (x, y) => x >= 3 && x < 13 && y >= 3 && y < 13 ? SKColors.Yellow : new SKColor(2, 3, (byte)(250 + x % 4)));
        Assert.Equal(new SKRectI(1, 0, 16, 16), ImageTrim.Bounds(noisy, new TrimOptions { BasedOn = TrimBasedOn.TopLeftPixelColor })); // Only the column that matches exactly.
        Assert.Equal(new SKRectI(3, 3, 13, 13), ImageTrim.Bounds(noisy, new TrimOptions { BasedOn = TrimBasedOn.TopLeftPixelColor, Tolerance = 6 }));
    }

    [Fact]
    public void Nothing_is_left_of_a_uniform_or_empty_picture()
    {
        using var solid = Solid(8, 8, new SKColor(100, 150, 200));
        Assert.Null(ImageTrim.Bounds(solid, new TrimOptions { BasedOn = TrimBasedOn.TopLeftPixelColor }));
        using var clear = Pixels.NewColor(8, 8);
        Assert.Null(ImageTrim.Bounds(clear, new TrimOptions()));
    }

    [Fact]
    public void Trim_crops_the_document_as_one_undo_step_and_moves_the_layers_along()
    {
        var session = EditorSession.NewCanvas(100, 100);
        var square = session.AddImageLayer("Square", Solid(40, 40, SKColors.Red), new SKPoint(40, 50));
        Assert.Equal((20, 30), ((int)square.Transform.X, (int)square.Transform.Y));
        Assert.True(session.Trim());
        Assert.Equal((40, 40), (session.Document.Width, session.Document.Height));
        Assert.Equal((0, 0), ((int)square.Transform.X, (int)square.Transform.Y));
        Assert.Equal("Trim", session.History.UndoName);
        Assert.False(session.Trim());                                     // Already trimmed: nothing changes, no undo step.
        session.Undo();
        Assert.Equal((100, 100), (session.Document.Width, session.Document.Height));
        Assert.Equal(20, session.Document.Find(square.Id)!.Transform.X);
        // A white background makes transparent-pixel trimming find nothing, while the corner color trims it.
        var backed = EditorSession.NewCanvas(60, 60, SKColors.White);
        backed.AddImageLayer("Dot", Solid(10, 10, SKColors.Black), new SKPoint(30, 30));
        Assert.False(backed.Trim());
        Assert.True(backed.Trim(new TrimOptions { BasedOn = TrimBasedOn.TopLeftPixelColor }));
        Assert.Equal((10, 10), (backed.Document.Width, backed.Document.Height));
    }
}
