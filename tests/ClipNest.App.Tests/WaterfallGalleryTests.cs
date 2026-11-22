using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using ClipNest.App.Controls;
using ClipNest.Core.Models;

namespace ClipNest.App.Tests;

public sealed class WaterfallGalleryTests
{
    [AvaloniaFact]
    public void Wheel_GlidesScroll_ToTarget()
    {
        var gallery = new WaterfallGallery
        {
            Items = Enumerable.Range(1, 400)
                .Select(id => new AssetListItem
                {
                    Id = id, Sha256 = $"h{id}", FileName = $"{id}.jpg"
                })
                .ToArray()
        };
        var window = new Window { Width = 800, Height = 600, Content = gallery };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(gallery.Bounds.Height > 0);

        window.MouseWheel(new Point(300, 300), new Vector(0, -1), RawInputModifiers.None);

        // 滚轮只改目标值，位移由帧回调推进，不是一次跳到位。
        Assert.True(gallery.TargetScrollOffset > 0);
        Assert.True(gallery.ScrollOffset < gallery.TargetScrollOffset);

        var frames = 0;
        while (gallery.StepAnimation(16) && frames++ < 500)
        {
        }

        Assert.True(frames < 500, $"缓动未收敛，{frames} 帧后仍在滚动");
        Assert.Equal(gallery.TargetScrollOffset, gallery.ScrollOffset, 3);
    }
}