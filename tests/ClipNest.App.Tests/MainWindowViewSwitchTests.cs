using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
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

    [AvaloniaFact]
    public void MainWindow_LayoutsCleanly_ForEmptyAndSelectedInspectorAndSettings()
    {
        var settingsPath = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new SettingsService(settingsPath);
            var vm = new MainWindowViewModel(settings: settings);
            var window = new MainWindow(vm, new NullThumbnails(), settings)
            {
                RequestedThemeVariant = ThemeVariant.Dark
            };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                // 排序按钮必须与外层药丸同尺寸同坐标，确保 MenuFlyout 右缘严格对齐且不压住药丸底边。
                Assert.Equal(window.SortPill.Bounds.Width, window.SortButton.Bounds.Width, 1);
                Assert.Equal(window.SortPill.Bounds.Height, window.SortButton.Bounds.Height, 1);
                var sortFlyout = Assert.IsType<MenuFlyout>(window.SortButton.Flyout);
                Assert.Equal(PlacementMode.BottomEdgeAlignedRight, sortFlyout.Placement);
                Assert.True(sortFlyout.VerticalOffset >= 6);

                vm.SelectedItem = new AssetListItem
                {
                    Id = 1,
                    FileName = "sample.png",
                    PrimaryPath = @"E:\pic\sample.png",
                    Width = 1200,
                    Height = 1800
                };
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                vm.OpenSettingsCommand.Execute(null);
                foreach (var section in new[] { "Appearance", "Browsing", "Ai", "Data" })
                {
                    vm.SelectSettingsSectionCommand.Execute(section);
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                }

                Assert.True(vm.IsSettingsOpen);
                Assert.True(vm.IsDataSection);
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