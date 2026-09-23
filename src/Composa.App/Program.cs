using Avalonia;

namespace Composa.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
#if BUNDLED_IMAGEMAGICK
        IO.ImageMagick.Bundled = BundledImageMagick.TryLoad;
#endif
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
