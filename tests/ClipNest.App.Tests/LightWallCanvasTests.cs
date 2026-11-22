using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using ClipNest.App.Controls;
using ClipNest.Core.Models;

namespace ClipNest.App.Tests;

public sealed class LightWallCanvasTests
{
    [AvaloniaFact]
    public void Canvas_HasExpectedDefaults()
    {
        var canvas = new LightWallCanvas();
        Assert.Equal(160, canvas.TileSize);
        Assert.Equal(12, canvas.Gap);
    }

    [AvaloniaFact]
    public void Wheel_GlidesScroll_AndStopsWhenSettled()
    {
        var canvas = new LightWallCanvas
        {
            Items = Enumerable.Range(1, 400)
                .Select(id => new AssetListItem
                {
                    Id = id, Sha256 = $"h{id}", FileName = $"{id}.jpg"
                })
                .ToArray()
        };
        var window = new Window { Width = 400, Height = 300, Content = canvas };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(canvas.Bounds.Height > 0);

        window.MouseWheel(new Point(200, 150), new Vector(0, -1), RawInputModifiers.None);

        // 滚轮只改目标值，位移由帧回调推进。
        Assert.True(canvas.TargetScrollOffset > 0);
        Assert.True(canvas.ScrollOffset <= canvas.TargetScrollOffset);

        var frames = 0;
        while (canvas.Tick(16) && frames++ < 500)
        {
        }

        Assert.True(frames < 500, $"缓动未收敛，{frames} 帧后仍在滚动");
        Assert.Equal(canvas.TargetScrollOffset, canvas.ScrollOffset, 3);
    }
}
