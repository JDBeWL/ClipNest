using System.Text.Json.Serialization;

namespace ClipNest.App.Services;

/// <summary>设置文件的序列化上下文。源生成保留全部属性，裁剪后不依赖反射。</summary>
[JsonSerializable(typeof(AppSettings))]
internal partial class AppSettingsJsonContext : JsonSerializerContext
{
}
