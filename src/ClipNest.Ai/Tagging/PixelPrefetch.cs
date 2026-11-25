namespace ClipNest.Ai.Tagging;

/// <summary>
/// 单槽位预取，保存下一张图片的模型输入。
/// 队列逐张处理，槽位只需容纳一张；被取走后立即清空，避免多张图片的输入同时驻留内存。
/// </summary>
internal sealed class PixelPrefetch
{
    private readonly object _lock = new();
    private string? _path;
    private Task<float[]>? _task;

    /// <summary>开始准备指定路径的输入。同一路径已有任务时不重复解码。</summary>
    public void Start(string path, Func<Task<float[]>> prepare)
    {
        lock (_lock)
        {
            if (_task is not null && string.Equals(_path, path, StringComparison.Ordinal))
            {
                return;
            }

            Observe(_task);
            _path = path;
            _task = prepare();
        }
    }

    /// <summary>取走匹配路径的预取结果；路径不匹配时返回空值并由调用方自行加载。</summary>
    public Task<float[]>? Take(string path)
    {
        lock (_lock)
        {
            if (_task is null || !string.Equals(_path, path, StringComparison.Ordinal))
            {
                return null;
            }

            var task = _task;
            _path = null;
            _task = null;
            return task;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Observe(_task);
            _path = null;
            _task = null;
        }
    }

    /// <summary>
    /// 暂停或切换模型会丢弃预取结果，被丢弃的任务没有调用方接收异常。
    /// 在这里读取一次，避免异常无人观察。
    /// </summary>
    private static void Observe(Task<float[]>? task)
    {
        task?.ContinueWith(
            static finished => _ = finished.Exception,
            TaskContinuationOptions.OnlyOnFaulted);
    }
}
