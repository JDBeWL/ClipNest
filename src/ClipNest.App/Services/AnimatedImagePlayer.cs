using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using GdiBitmap = System.Drawing.Bitmap;
using GdiImage = System.Drawing.Image;
using AvaloniaImage = Avalonia.Controls.Image;
using AvaloniaPixelFormat = Avalonia.Platform.PixelFormat;
using AvaloniaAlphaFormat = Avalonia.Platform.AlphaFormat;

namespace ClipNest.App.Services;

/// <summary>
/// GIF 逐帧播放器。帧由 GDI+ 解码，它会按 GIF 的处置方式把每一帧合成为完整画面；
/// Skia 只解出帧自身的像素，直接播放会让后续帧丢失背景。
/// </summary>
public sealed class AnimatedImagePlayer : IDisposable
{
    // 所有帧常驻内存，按总像素而不是帧数限制，超长 GIF 自动降低分辨率。
    private const long PixelBudget = 12_000_000;
    private const int MaxFrames = 200;
    private const int DefaultDelayMs = 100;

    private readonly List<WriteableBitmap> _frames;
    private readonly List<int> _delays;
    private readonly DispatcherTimer _timer;
    private int _index;
    private AvaloniaImage? _target;
    private bool _disposed;

    private AnimatedImagePlayer(FrameData data)
    {
        _frames = data.Pixels
            .Select(pixels => CreateBitmap(pixels, data.Width, data.Height))
            .ToList();
        _delays = data.Delays;
        _timer = new DispatcherTimer(DispatcherPriority.Render);
        _timer.Tick += OnTick;
    }

    public bool IsAnimated => _frames.Count > 1;

    /// <summary>读取 GIF 的全部帧；不是动画或格式不支持时返回 null。</summary>
    public static async Task<AnimatedImagePlayer?> LoadAsync(
        string path, int maxSize, CancellationToken ct = default)
    {
        var data = await Task.Run(() => Decode(path, maxSize), ct);
        return data is null ? null : new AnimatedImagePlayer(data);
    }

    /// <summary>把播放器挂到图片控件上并开始循环，换帧只替换 Source。</summary>
    public void Play(AvaloniaImage target)
    {
        if (_frames.Count == 0) return;

        _target = target;
        _target.Source = _frames[0];
        if (!IsAnimated) return;

        _index = 0;
        _timer.Interval = TimeSpan.FromMilliseconds(_delays[0]);
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        if (_target is not null) _target.Source = null;
        _target = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _timer.Tick -= OnTick;
        foreach (var frame in _frames) frame.Dispose();
        _frames.Clear();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_target is null || _frames.Count == 0) return;

        _index = (_index + 1) % _frames.Count;
        _target.Source = _frames[_index];
        _timer.Interval = TimeSpan.FromMilliseconds(_delays[_index]);
    }

    private static FrameData? Decode(string path, int maxSize)
    {
        try
        {
            using var image = GdiImage.FromFile(path);
            if (image.FrameDimensionsList.Length == 0) return null;

            var dimension = new FrameDimension(image.FrameDimensionsList[0]);
            var count = image.GetFrameCount(dimension);
            if (count <= 1 || count > MaxFrames) return null;

            var delays = ReadDelays(image, count);
            var (width, height) = GetTargetSize(image.Width, image.Height, count, maxSize);

            var pixels = new List<byte[]>(count);
            for (var i = 0; i < count; i++)
            {
                image.SelectActiveFrame(dimension, i);
                using var frame = new GdiBitmap(image);
                pixels.Add(ReadPixels(frame, width, height));
            }

            return pixels.Count == 0 ? null : new FrameData(pixels, delays, width, height);
        }
        catch
        {
            return null;
        }
    }

    private static (int Width, int Height) GetTargetSize(
        int width, int height, int frameCount, int maxSize)
    {
        var scale = Math.Min(1.0, maxSize / (double)Math.Max(width, height));

        var total = (double)width * height * frameCount;
        if (total * scale * scale > PixelBudget)
            scale = Math.Sqrt(PixelBudget / total);
        scale = Math.Min(scale, 1.0);

        return (
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static List<int> ReadDelays(GdiImage image, int count)
    {
        var delays = new List<int>(count);
        try
        {
            var value = image.GetPropertyItem(0x5100)?.Value ?? Array.Empty<byte>(); // PropertyTagFrameDelay
            for (var i = 0; i < count; i++)
            {
                var centiseconds = i * 4 + 4 <= value.Length
                    ? BitConverter.ToInt32(value, i * 4)
                    : 0;
                delays.Add(NormalizeDelay(centiseconds * 10));
            }
        }
        catch
        {
            for (var i = 0; i < count; i++) delays.Add(DefaultDelayMs);
        }

        return delays;
    }

    /// <summary>GIF 里 0 与 10ms 的延迟会被浏览器按 100ms 播放，这里保持同样的观感。</summary>
    private static int NormalizeDelay(int milliseconds)
        => milliseconds < 20 ? DefaultDelayMs : Math.Min(milliseconds, 1000);

    private static byte[] ReadPixels(GdiBitmap source, int width, int height)
    {
        var resized = source.Width == width && source.Height == height
            ? null
            : Resize(source, width, height);
        var bitmap = resized ?? source;

        try
        {
            var data = bitmap.LockBits(
                new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var rowBytes = width * 4;
                var pixels = new byte[rowBytes * height];
                for (var y = 0; y < height; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * rowBytes, rowBytes);
                return pixels;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }
        finally
        {
            resized?.Dispose();
        }
    }

    private static GdiBitmap Resize(GdiImage source, int width, int height)
    {
        var target = new GdiBitmap(width, height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(target);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        return target;
    }

    private static WriteableBitmap CreateBitmap(byte[] pixels, int width, int height)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height), new Vector(96, 96), AvaloniaPixelFormat.Bgra8888, AvaloniaAlphaFormat.Premul);

        using var buffer = bitmap.Lock();
        var rowBytes = width * 4;
        for (var y = 0; y < height; y++)
            Marshal.Copy(pixels, y * rowBytes, buffer.Address + y * buffer.RowBytes, rowBytes);

        return bitmap;
    }

    private sealed record FrameData(List<byte[]> Pixels, List<int> Delays, int Width, int Height);
}
