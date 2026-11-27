using Avalonia;
using Avalonia.Media;
using ClipNest.App.Services;
using ClipNest.App.Theme;
using Serilog;

namespace ClipNest.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var dataRoot = AppPaths.ResolveDataRoot();
        var logRoot = AppPaths.ResolveLogRoot(dataRoot);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(logRoot, "clipnest-.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();

        try
        {
            Log.Information("ClipNest starting");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "ClipNest crashed");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions
            {
                // 按系统常用中日文字体再到内置思源黑体的顺序兜底缺字，避免回退链落到可变字体的细体或随机缓存字体。
                FontFallbacks =
                [
                    new FontFallback { FontFamily = new FontFamily("Microsoft YaHei UI") },
                    new FontFallback { FontFamily = new FontFamily("Yu Gothic UI") },
                    new FontFallback { FontFamily = new FontFamily(AppFonts.BundledNotoUri) }
                ]
            })
            .LogToTrace();
}
