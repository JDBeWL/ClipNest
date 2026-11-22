using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using ClipNest.App.Services;
using ClipNest.App.ViewModels;
using ClipNest.App.Views;
using ClipNest.Core.Models;

namespace ClipNest.App.Tests;

public sealed class MainWindowViewSwitchTests
{
    private sealed class NullThumbnails : IThumbnailProvider
    {
        public Task<Bitmap?> GetAsync(
            AssetListItem item, int size, CancellationToken ct = default)
            => Task.FromResult<Bitmap?>(null);
    }

    [AvaloniaFact]
    public void Switch_KeepsOutgoingViewVisibleWhileFading()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new SettingsService(settingsPath);
            var vm = new MainWindowViewModel(settings: settings);
            var window = new MainWindow(vm, new NullThumbnails(), settings);
            try
            {
                Assert.True(window.Gallery.IsVisible);
                Assert.False(window.Wall.IsVisible);
                Assert.False(window.Galaxy.IsVisible);

                vm.ShowWallCommand.Execute(null);

                Assert.True(vm.IsLightWall);
                Assert.True(window.Wall.IsVisible);
                Assert.True(window.Gallery.IsVisible);
                Assert.True(window.Gallery.ZIndex > window.Wall.ZIndex);
                Assert.False(window.Gallery.IsHitTestVisible);
                Assert.True(window.Wall.IsHitTestVisible);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
    }
}