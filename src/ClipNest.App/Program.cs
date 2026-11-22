using Avalonia;
using ClipNest.App.Services;
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
        => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
