using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;

[assembly: AvaloniaTestApplication(typeof(ClipNest.App.Tests.TestAppBuilder))]

namespace ClipNest.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<ClipNest.App.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
