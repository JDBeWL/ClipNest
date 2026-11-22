using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClipNest.App.Services;
using ClipNest.App.Views;
using ClipNest.Core.Models;

namespace ClipNest.App.Tests;

public sealed class ViewerWindowTests
{
    private sealed class NullThumbnails : IThumbnailProvider
    {
        public Task<Bitmap?> GetAsync(AssetListItem item, int size, CancellationToken ct = default)
            => Task.FromResult<Bitmap?>(null);
    }

    private sealed class FixedThumbnails : IThumbnailProvider
    {
        private readonly Bitmap _bitmap;
        public FixedThumbnails(Bitmap bitmap) => _bitmap = bitmap;

        public Task<Bitmap?> GetAsync(AssetListItem item, int size, CancellationToken ct = default)
            => Task.FromResult<Bitmap?>(_bitmap);
    }

    [AvaloniaFact]
    public void Title_Is_FileName()
    {
        var item = new AssetListItem { Id = 1, FileName = "a.jpg" };
        var window = new ViewerWindow(new[] { item }, 0, new NullThumbnails());

        Assert.Equal("a.jpg", window.Title);
    }

    [AvaloniaFact]
    public void ZoomIn_AnchorsOffsetWithinSameLayoutPass()
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(2000, 1500), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        var item = new AssetListItem
        {
            Id = 1, FileName = "a.jpg", PrimaryPath = "a.jpg", Width = 2000, Height = 1500,
        };
        var window = new ViewerWindow(new[] { item }, 0, new FixedThumbnails(bitmap));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var scroll = window.FindControl<ScrollViewer>("ImageScroll");
        var image = window.FindControl<Image>("ImageView");
        Assert.NotNull(scroll);
        Assert.NotNull(image);

        var zoomBefore = image!.Width / 2000;
        var offsetBefore = scroll!.Offset;
        var viewport = new Vector(scroll.Viewport.Width, scroll.Viewport.Height);

        var zoomIn = window.GetVisualDescendants().OfType<Button>()
            .First(b => Equals(ToolTip.GetTip(b), "放大"));
        zoomIn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        // 尺寸更新后的同一布局 pass 内偏移就必须锚定到位，
        // 否则合成器会先渲染一帧「新尺寸 + 旧偏移」，缩放会出现一次抖动。
        window.UpdateLayout();

        var factor = Math.Min(4.0, zoomBefore * 1.25) / zoomBefore;
        var desired = (offsetBefore + viewport / 2) * factor - viewport / 2;
        var maxX = Math.Max(0, scroll.Extent.Width - viewport.X);
        var maxY = Math.Max(0, scroll.Extent.Height - viewport.Y);
        var expected = new Vector(
            Math.Clamp(desired.X, 0, maxX), Math.Clamp(desired.Y, 0, maxY));

        Assert.True(
            Math.Abs(scroll.Offset.X - expected.X) < 0.5 &&
            Math.Abs(scroll.Offset.Y - expected.Y) < 0.5,
            $"offset {scroll.Offset}, expected {expected}");

        window.Close();
        bitmap.Dispose();
    }
}
