using Avalonia;
using Avalonia.Headless;
using Compositor.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestApp))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Compositor.App.Tests;

public static class TestApp
{
    static TestApp() => Settings.Persist = false;

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
