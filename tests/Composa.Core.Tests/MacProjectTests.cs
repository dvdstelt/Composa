using Composa.Filters;
using Composa.IO;
using Composa.Model;
using Composa.Rendering;
using SkiaSharp;

namespace Composa.Core.Tests;

public class MacProjectTests
{
    [Fact]
    public void Reads_a_macos_project_package()
    {
        var folder = Path.Combine(Path.GetTempPath(), "composa-tests-" + Guid.NewGuid().ToString("N"), "Sample.comp");
        Directory.CreateDirectory(Path.Combine(folder, "images"));
        try
        {
            Guid photo = Guid.NewGuid(), blank = Guid.NewGuid(), group = Guid.NewGuid(), levels = Guid.NewGuid();
            using (var red = TestImages.Solid(20, 10, SKColors.Red)) ImageFiles.Save(red, Path.Combine(folder, "images", $"{photo.ToString().ToUpperInvariant()}.png"), ExportFormat.Png);
            using (var white = TestImages.Solid(1, 1, SKColors.White)) ImageFiles.Save(white, Path.Combine(folder, "images", $"{photo.ToString().ToUpperInvariant()}.mask.png"), ExportFormat.Png);
            File.WriteAllText(Path.Combine(folder, "manifest.json"), $$"""
            {
              "format": "com.compositor.project", "version": 7, "colorSpace": "sRGB", "resolution": 300,
              "documentID": "{{Guid.NewGuid()}}", "width": 40, "height": 30, "activeLayerID": "{{blank.ToString().ToUpperInvariant()}}",
              "layers": [
                { "id": "{{photo.ToString().ToUpperInvariant()}}", "name": "Photo", "isVisible": true, "parentID": "{{group.ToString().ToUpperInvariant()}}",
                  "imageFile": "{{photo.ToString().ToUpperInvariant()}}.png", "maskFile": "{{photo.ToString().ToUpperInvariant()}}.mask.png", "maskEnabled": true,
                  "opacity": 0.5, "blendMode": "Color Dodge",
                  "transform": { "origin": [10, 5], "size": [20, 10], "rotation": 0, "flipX": true, "flipY": false, "sampling": "High quality" } },
                { "id": "{{blank.ToString().ToUpperInvariant()}}", "name": "Blank", "isVisible": false, "parentID": "{{group.ToString().ToUpperInvariant()}}",
                  "blendMode": "Linear Dodge (Add)", "maskSourceID": "{{photo.ToString().ToUpperInvariant()}}",
                  "transform": { "origin": [0, 0], "size": [40, 30], "rotation": 0, "flipX": false, "flipY": false, "sampling": "High quality" } },
                { "id": "{{group.ToString().ToUpperInvariant()}}", "name": "Folder", "isVisible": true, "isGroup": true,
                  "transform": { "origin": [0, 0], "size": [40, 30], "rotation": 0, "flipX": false, "flipY": false, "sampling": "High quality" } },
                { "id": "{{levels.ToString().ToUpperInvariant()}}", "name": "Levels", "isVisible": true,
                  "transform": { "origin": [0, 0], "size": [40, 30], "rotation": 0, "flipX": false, "flipY": false, "sampling": "High quality" },
                  "adjustment": { "kind": "Levels", "hue": 0, "saturation": 0, "lightness": 0, "colorize": false,
                    "levels": { "channel": "RGB", "ranges": [ { "black": 10, "gamma": 1.2, "white": 240, "outputBlack": 0, "outputWhite": 255 },
                      { "black": 0, "gamma": 1, "white": 255, "outputBlack": 0, "outputWhite": 255 }, { "black": 0, "gamma": 1, "white": 255, "outputBlack": 0, "outputWhite": 255 },
                      { "black": 0, "gamma": 1, "white": 255, "outputBlack": 0, "outputWhite": 255 } ] },
                    "curves": { "channel": "RGB", "channels": [] } } },
                { "id": "{{Guid.NewGuid().ToString().ToUpperInvariant()}}", "name": "Balance", "isVisible": true,
                  "transform": { "origin": [0, 0], "size": [40, 30], "rotation": 0, "flipX": false, "flipY": false, "sampling": "High quality" },
                  "adjustment": { "kind": "Color Balance", "hue": 0, "saturation": 0, "lightness": 0, "colorize": false,
                    "colorBalanceSettings": { "shadowCyanRed": 12, "shadowMagentaGreen": 0, "shadowYellowBlue": 0, "midCyanRed": 0, "midMagentaGreen": -8, "midYellowBlue": 0,
                      "highlightCyanRed": 0, "highlightMagentaGreen": 0, "highlightYellowBlue": 25, "preserveLuminosity": false } } },
                { "id": "{{Guid.NewGuid().ToString().ToUpperInvariant()}}", "name": "Mono", "isVisible": true,
                  "transform": { "origin": [0, 0], "size": [40, 30], "rotation": 0, "flipX": false, "flipY": false, "sampling": "High quality" },
                  "adjustment": { "kind": "Black & White", "hue": 0, "saturation": 0, "lightness": 0, "colorize": false,
                    "blackWhiteSettings": { "reds": 55, "yellows": 60, "greens": 40, "cyans": 60, "blues": 20, "magentas": 80, "tint": true, "tintHue": 210, "tintSaturation": 15 } } },
                { "id": "{{Guid.NewGuid().ToString().ToUpperInvariant()}}", "name": "Invert", "isVisible": true,
                  "transform": { "origin": [0, 0], "size": [40, 30], "rotation": 0, "flipX": false, "flipY": false, "sampling": "High quality" },
                  "adjustment": { "kind": "Invert", "hue": 0, "saturation": 0, "lightness": 0, "colorize": false } }
              ]
            }
            """);

            Assert.True(MacProject.IsProject(folder));
            var document = MacProject.Load(folder);
            Assert.Equal((40, 30, 300d), (document.Width, document.Height, document.Resolution));
            Assert.Equal(5, document.Layers.Count);
            var folderLayer = document.Layers[0];
            Assert.True(folderLayer.IsGroup);
            Assert.Equal(["Photo", "Blank"], folderLayer.Children.Select(l => l.Name));
            var loadedPhoto = folderLayer.Children[0];
            Assert.Equal((BlendMode.ColorDodge, 0.5, true), (loadedPhoto.Blend, loadedPhoto.Opacity, loadedPhoto.Transform.FlipHorizontal));
            Assert.Equal(10, loadedPhoto.Transform.X);
            Assert.Equal((20, 10), (loadedPhoto.Mask!.Width, loadedPhoto.Mask.Height));
            Assert.True(folderLayer.Children[1].Clipped);
            Assert.False(folderLayer.Children[1].Visible);
            Assert.Equal(BlendMode.LinearDodge, folderLayer.Children[1].Blend);
            var adjustment = Assert.IsType<LevelsAdjustment>(document.Layers[1].Adjustment);
            Assert.Equal(1.2, adjustment.Ranges[0].Gamma);
            var balance = Assert.IsType<ColorBalanceAdjustment>(document.Layers[2].Adjustment);
            Assert.Equal((12d, -8d, 25d, false), (balance.Shadows[0], balance.Midtones[1], balance.Highlights[2], balance.PreserveLuminosity));
            var mono = Assert.IsType<BlackAndWhiteAdjustment>(document.Layers[3].Adjustment);
            Assert.Equal((55d, true, 210d, 15d), (mono.Reds, mono.Tint, mono.TintHue, mono.TintSaturation));
            Assert.IsType<InvertAdjustment>(document.Layers[4].Adjustment);
            Assert.Equal(blank, document.ActiveLayerId);
            using var flat = DocumentRenderer.Flatten(document);
            Assert.True(flat.GetPixel(15, 8).Alpha > 0);
        }
        finally { Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true); }
    }
}

public class ImageMagickFallbackTests
{
    [Fact]
    public void Tiff_opens_through_imagemagick_when_it_is_installed()
    {
        var folder = Path.Combine(Path.GetTempPath(), "composa-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var png = Path.Combine(folder, "source.png");
            var tiff = Path.Combine(folder, "source.tiff");
            using (var red = TestImages.Solid(12, 8, SKColors.Red)) ImageFiles.Save(red, png, ExportFormat.Png);
            try
            {
                using var convert = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("magick", [png, tiff]) { RedirectStandardError = true });
                convert!.WaitForExit();
            }
            catch (System.ComponentModel.Win32Exception) { return; } // ImageMagick is not installed here.
            if (!File.Exists(tiff)) return;
            using var loaded = ImageFiles.Load(tiff);
            Assert.Equal((12, 8), (loaded.Width, loaded.Height));
            TestImages.AssertColor(SKColors.Red, loaded.GetPixel(3, 3));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}

public class DamagedFileTests
{
    [Fact]
    public void A_project_with_a_nonsense_transform_still_opens()
    {
        var session = Composa.Editing.EditorSession.NewCanvas(40, 30, SKColors.Red);
        using var stream = new MemoryStream();
        ProjectFile.Write(session.Document, stream);
        // Rewrite the manifest with a zero-sized, non-finite placement.
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = zip.GetEntry("manifest.json")!;
            string json;
            using (var reader = new StreamReader(entry.Open())) json = reader.ReadToEnd();
            entry.Delete();
            json = System.Text.RegularExpressions.Regex.Replace(json, "\"width\": 40,\\s*\"height\": 30,\\s*\"rotation\"", "\"width\": 0, \"height\": -5, \"rotation\"");
            Assert.Contains("\"height\": -5", json);
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            writer.Write(json);
        }
        stream.Position = 0;
        var loaded = ProjectFile.Read(stream);
        Assert.Equal(40, loaded.Layers[0].Transform.Width);
        using var flat = DocumentRenderer.Flatten(loaded);
        TestImages.AssertColor(SKColors.Red, flat.GetPixel(5, 5));
    }
}
