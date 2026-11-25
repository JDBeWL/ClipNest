using ClipNest.Ai.Acceleration;

namespace ClipNest.Ai.Tests;

/// <summary>
/// 线程数设置决定会话的算子线程数；0 表示自动，按逻辑核心数折半。
/// </summary>
public sealed class OrtAcceleratorTests
{
    [Fact]
    public void ResolveIntraOpThreads_UsesConfiguredValueOrHalfCores()
    {
        var original = OrtAccelerator.IntraOpThreads;
        try
        {
            OrtAccelerator.IntraOpThreads = 3;
            Assert.Equal(3, OrtAccelerator.ResolveIntraOpThreads());

            OrtAccelerator.IntraOpThreads = 0;
            Assert.Equal(
                Math.Max(1, Environment.ProcessorCount / 2),
                OrtAccelerator.ResolveIntraOpThreads());
        }
        finally
        {
            OrtAccelerator.IntraOpThreads = original;
        }
    }
}
