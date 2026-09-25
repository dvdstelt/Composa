using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Composa.App.Tests;

/// <summary>
/// Screenshots are how a UI change is checked without a display. They land in artifacts/screenshots, which CI keeps
/// when a run fails.
/// </summary>
internal static class Screenshots
{
    public static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/screenshots");

    /// <summary>Renders what is pending for a window and saves its frame as name.png. Returns false when there is no frame.</summary>
    public static bool Save(TopLevel window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        // Avalonia encodes through an image that only points at the frame's pixels, and nothing else holds the frame
        // once that image exists. A garbage collection during the encode then frees the pixels under the encoder and
        // the test process dies with a segmentation fault. The using keeps the frame alive until the file is written.
        using var frame = window.CaptureRenderedFrame();
        if (frame == null) return false;
        Directory.CreateDirectory(Folder);
        frame.Save(Path.Combine(Folder, name + ".png"), PngBitmapEncoderOptions.Default);
        return true;
    }
}
