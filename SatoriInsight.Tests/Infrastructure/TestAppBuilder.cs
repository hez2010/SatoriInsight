using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(SatoriInsight.Tests.TestAppBuilder))]

namespace SatoriInsight.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().WithInterFont();
}
