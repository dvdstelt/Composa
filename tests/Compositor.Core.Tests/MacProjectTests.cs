using Compositor.Filters;
using Compositor.IO;
using Compositor.Model;
using Compositor.Rendering;
using SkiaSharp;

namespace Compositor.Core.Tests;

public class MacProjectTests
{
    [Fact]
    public void Reads_a_macos_project_package()
    {
        var folder = Path.Combine(Path.GetTempPath(), "compositor-tests-" + Guid.NewGuid().ToString("N"), "Sample.comp");
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
                  "maskSourceID": "{{photo.ToString().ToUpperInvariant()}}",
                  "transform": { "origin": [0, 0], "size": [40, 30], "rotation": 0, "flipX": false, "flipY": false, "sampling": "High quality" } },
                { "id": "{{group.ToString().ToUpperInvariant()}}", "name": "Folder", "isVisible": true, "isGroup": true,
                  "transform": { "origin": [0, 0], "size": [40, 30], "rotation": 0, "flipX": false, "flipY": false, "sampling": "High quality" } },
                { "id": "{{levels.ToString().ToUpperInvariant()}}", "name": "Levels", "isVisible": true,
                  "transform": { "origin": [0, 0], "size": [40, 30], "rotation": 0, "flipX": false, "flipY": false, "sampling": "High quality" },
                  "adjustment": { "kind": "Levels", "hue": 0, "saturation": 0, "lightness": 0, "colorize": false,
                    "levels": { "channel": "RGB", "ranges": [ { "black": 10, "gamma": 1.2, "white": 240, "outputBlack": 0, "outputWhite": 255 },
                      { "black": 0, "gamma": 1, "white": 255, "outputBlack": 0, "outputWhite": 255 }, { "black": 0, "gamma": 1, "white": 255, "outputBlack": 0, "outputWhite": 255 },
                      { "black": 0, "gamma": 1, "white": 255, "outputBlack": 0, "outputWhite": 255 } ] },
                    "curves": { "channel": "RGB", "channels": [] } } }
              ]
            }
            """);

            Assert.True(MacProject.IsProject(folder));
            var document = MacProject.Load(folder);
            Assert.Equal((40, 30, 300d), (document.Width, document.Height, document.Resolution));
            Assert.Equal(2, document.Layers.Count);
            var folderLayer = document.Layers[0];
            Assert.True(folderLayer.IsGroup);
            Assert.Equal(["Photo", "Blank"], folderLayer.Children.Select(l => l.Name));
            var loadedPhoto = folderLayer.Children[0];
            Assert.Equal((BlendMode.ColorDodge, 0.5, true), (loadedPhoto.Blend, loadedPhoto.Opacity, loadedPhoto.Transform.FlipHorizontal));
            Assert.Equal(10, loadedPhoto.Transform.X);
            Assert.Equal((20, 10), (loadedPhoto.Mask!.Width, loadedPhoto.Mask.Height));
            Assert.True(folderLayer.Children[1].Clipped);
            Assert.False(folderLayer.Children[1].Visible);
            var adjustment = Assert.IsType<LevelsAdjustment>(document.Layers[1].Adjustment);
            Assert.Equal(1.2, adjustment.Ranges[0].Gamma);
            Assert.Equal(blank, document.ActiveLayerId);
            using var flat = DocumentRenderer.Flatten(document);
            Assert.True(flat.GetPixel(15, 8).Alpha > 0);
        }
        finally { Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true); }
    }
}
