namespace ClipNest.App.Services;

/// <summary>
/// ONNX Runtime 依赖 x64 VC++ 运行库。缺失时 AI 标注与语义搜索不可用，
/// 浏览、导入与整理仍可正常使用。
/// </summary>
internal static class VcRuntimeProbe
{
    private static readonly string[] RequiredFiles =
    {
        "VCRUNTIME140.dll",
        "VCRUNTIME140_1.dll",
        "MSVCP140.dll"
    };

    public static bool IsAvailable()
    {
        var app = AppContext.BaseDirectory;
        var system = Environment.SystemDirectory;

        // 应用目录优先于 System32；随包附带运行库时无需修改检测。
        return RequiredFiles.All(name =>
            File.Exists(Path.Combine(app, name)) || File.Exists(Path.Combine(system, name)));
    }
}
