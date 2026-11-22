using Avalonia.Headless.XUnit;
using ClipNest.App.Services;

namespace ClipNest.App.Tests;

public sealed class AnimatedImagePlayerTests
{
    // 8×8 双帧 GIF，每帧 100ms，用来验证帧解码与 BGRA 拷贝。
    private const string TwoFrameGif =
        "R0lGODlhCAAIAIEAAP8AAAAAAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQACgAAACwAAAAACAAIAAAIDwABCBxIsKDB"
        + "gwgTKkwYEAAh+QQBCgABACwAAAAACAAIAIEAAP8AAAAAAAAAAAAIDwABCBxIsKDBgwgTKkwYEAA7";

    [AvaloniaFact]
    public async Task LoadAsync_ReturnsPlayableFrames()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clipnest-{Guid.NewGuid():N}.gif");
        await File.WriteAllBytesAsync(path, Convert.FromBase64String(TwoFrameGif));

        try
        {
            var player = await AnimatedImagePlayer.LoadAsync(path, 64);
            Assert.NotNull(player);
            Assert.True(player!.IsAnimated);
            player.Dispose();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
