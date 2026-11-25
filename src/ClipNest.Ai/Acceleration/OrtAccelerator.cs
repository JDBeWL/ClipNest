using Microsoft.ML.OnnxRuntime;

namespace ClipNest.Ai.Acceleration;

/// <summary>推理实际落在哪种设备上。</summary>
public enum AcceleratorKind
{
    Cpu,
    DirectMl
}

/// <summary>
/// 会话创建的唯一入口。默认尝试 DirectML，即 Windows 上的 DX12 GPU 路径，失败则退回 CPU。
/// </summary>
/// <remarks>
/// CUDA 只覆盖 NVIDIA，ROCm 与 MIGraphX 的 ONNX Runtime 只有 Linux 构建，
/// OpenVINO 只覆盖 Intel。Windows 上覆盖 AMD、NVIDIA 与 Intel 核显的通用路径只有 DirectML，
/// 因此默认尝试它。
/// </remarks>
public static class OrtAccelerator
{
    private const string SettingVariable = "CLIPNEST_ACCEL";

    private static readonly Lazy<bool> DirectMlCompiled = new(() =>
    {
        try
        {
            return OrtEnv.Instance().GetAvailableProviders()
                .Any(name => name.Contains("Dml", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    });

    /// <summary>上一次请求 GPU 的会话实际使用的设备。固定走 CPU 的会话不影响该值。</summary>
    public static AcceleratorKind Active { get; private set; } = AcceleratorKind.Cpu;

    /// <summary>是否已创建过请求 GPU 的会话。没有这类会话时不能把「尚未推理」报告为「在用 CPU」。</summary>
    public static bool HasSession { get; private set; }

    /// <summary>
    /// ONNX Runtime 的算子线程数；0 表示按逻辑核心数取一半。
    /// 改动只影响之后创建的会话，已有会话保持原线程数。
    /// </summary>
    public static int IntraOpThreads { get; set; }

    /// <summary>实际使用的线程数，至少 1。</summary>
    public static int ResolveIntraOpThreads() =>
        IntraOpThreads > 0 ? IntraOpThreads : Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>用户是否显式要求关闭加速（CLIPNEST_ACCEL=cpu）。</summary>
    public static bool DisabledBySettings =>
        string.Equals(
            Environment.GetEnvironmentVariable(SettingVariable),
            "cpu",
            StringComparison.OrdinalIgnoreCase);

    public static bool IsGpuAvailable => DirectMlCompiled.Value && !DisabledBySettings;

    /// <summary>
    /// 创建会话。<paramref name="allowGpu"/> 为假时固定走 CPU；
    /// CLIP 的输入是动态形状，文本长度随查询变化，放 GPU 上会反复重编译计算图，反而更慢。
    /// </summary>
    public static InferenceSession CreateSession(string modelPath, bool allowGpu)
    {
        if (allowGpu && IsGpuAvailable)
        {
            try
            {
                var gpu = new InferenceSession(modelPath, BuildOptions(useGpu: true));
                Active = AcceleratorKind.DirectMl;
                HasSession = true;
                return gpu;
            }
            catch (Exception)
            {
                // 显卡可用但拿不到 D3D12 设备（远程桌面、驱动异常、虚拟机）时进入此分支。
                // 失败后必须新建 SessionOptions；追加过 provider 的实例不能复用。
            }
        }

        var cpu = new InferenceSession(modelPath, BuildOptions(useGpu: false));
        if (allowGpu)
        {
            // 只有请求过 GPU 的会话才更新状态，避免 CLIP 这类固定走 CPU 的会话把显示改成 CPU。
            Active = AcceleratorKind.Cpu;
            HasSession = true;
        }

        return cpu;
    }

    private static SessionOptions BuildOptions(bool useGpu)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = ResolveIntraOpThreads(),
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };

        if (useGpu)
        {
            // deviceId 0 表示系统默认适配器。显存里放的是模型权重，不占用进程内存。
            options.AppendExecutionProvider_DML(0);
        }

        return options;
    }

    /// <summary>
    /// 供设置页显示的一行文字。没有请求 GPU 的会话时报告当前能力；
    /// 若直接读上一次设备，刚启动、尚无标注任务时会显示 CPU，看起来像加速未生效。
    /// </summary>
    public static string Describe()
    {
        if (HasSession) return Active == AcceleratorKind.DirectMl ? "GPU" : "CPU";
        return IsGpuAvailable ? "GPU 就绪" : "CPU";
    }
}
