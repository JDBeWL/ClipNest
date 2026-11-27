using System.Text.Json.Serialization;

namespace ClipNest.App.Services;

/// <summary>设置文件与语言包的序列化上下文。源生成保留全部类型，裁剪后不依赖反射。</summary>
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class AppSettingsJsonContext : JsonSerializerContext
{
}
