using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using ClipNest.App.Controls;
using ClipNest.Core.Models;

namespace ClipNest.App.Tests;

public sealed class GalaxyCanvasTests
{
    [AvaloniaFact]
    public void Canvas_HasExpectedDefaults()
    {
        var canvas = new GalaxyCanvas();
        Assert.Equal(1.0, canvas.Camera.Zoom);
        Assert.Null(canvas.SelectedNodeId);
    }

    [AvaloniaFact]
    public void Canvas_SelectNode_UpdatesSelection()
    {
        var canvas = new GalaxyCanvas();
        canvas.SelectNode(42);
        Assert.Equal(42, canvas.SelectedNodeId);
        canvas.SelectNode(null);
        Assert.Null(canvas.SelectedNodeId);
    }

    [AvaloniaFact]
    public void Cluster_HoverHighlights_AndClickActivates()
    {
        var nodes = new[]
        {
            new GalaxyNode
            {
                Asset = new AssetListItem { Id = 1, Sha256 = "h1", FileName = "1.jpg" },
                ClusterId = 1,
                X = 0,
                Y = 0
            }
        };
        var clusters = new[]
        {
            new GalaxyCluster
            {
                Id = 1,
                TagId = 1,
                TagPath = "旅行",
                Label = "旅行",
                CenterX = 0,
                CenterY = 0,
                Radius = 300,
                AssetCount = 1
            }
        };
        var canvas = new GalaxyCanvas { Nodes = nodes, Clusters = clusters };
        var window = new Window { Width = 800, Height = 600, Content = canvas };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        canvas.Camera.Zoom = 0.4;
        canvas.Camera.CenterX = 0;
        canvas.Camera.CenterY = 0;

        GalaxyCluster? activated = null;
        canvas.ClusterActivated += (_, cluster) => activated = cluster;

        var point = new Point(400, 300);
        window.MouseMove(point, RawInputModifiers.None);
        Assert.Equal(1, canvas.HoveredClusterId);

        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);

        Assert.Same(clusters[0], activated);
    }

    [AvaloniaFact]
    public void Wheel_ZoomsGradually_AndStopsWhenSettled()
    {
        var nodes = Enumerable.Range(1, 20).Select(id => new GalaxyNode
        {
            Asset = new AssetListItem { Id = id, Sha256 = $"h{id}", FileName = $"{id}.jpg" },
            ClusterId = 1,
            X = id * 100,
            Y = 0
        }).ToArray();
        var clusters = new[]
        {
            new GalaxyCluster { Id = 1, Label = "旅行", CenterX = 1000, CenterY = 0, Radius = 800 }
        };
        var canvas = new GalaxyCanvas { Nodes = nodes, Clusters = clusters };
        var window = new Window { Width = 800, Height = 600, Content = canvas };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        canvas.Camera.Zoom = 1;
        window.MouseWheel(new Point(400, 300), new Vector(0, 1), RawInputModifiers.None);

        // 滚轮只累计目标倍率，位移由帧回调推进，不是一次跳到位。
        Assert.True(canvas.IsZooming);
        Assert.True(canvas.Camera.Zoom < 1.12);

        var frames = 0;
        while (canvas.StepZoom(16) && frames++ < 500)
        {
        }

        Assert.True(frames < 500, $"缩放缓动未收敛，{frames} 帧后仍在缩放");
        Assert.Equal(1.12, canvas.Camera.Zoom, 3);
        Assert.False(canvas.IsZooming);
    }
}