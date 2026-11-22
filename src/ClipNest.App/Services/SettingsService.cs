using System.Text.Json;

namespace ClipNest.App.Services;

public sealed class SettingsService
{
    private readonly string _path;

    public SettingsService(string path)
    {
        _path = path;
        Load();
    }

    public AppSettings Settings { get; private set; } = new();

    /// <summary>数据目录，数据库、缓存、模型与设置文件都位于此处。</summary>
    public string DataRoot => Path.GetDirectoryName(_path) ?? string.Empty;

    public void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            Settings = JsonSerializer.Deserialize(
                File.ReadAllText(_path), AppSettingsJsonContext.Default.AppSettings) ?? new();
        }
        catch (JsonException)
        {
            Settings = new AppSettings();
        }
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(
            _path, JsonSerializer.Serialize(Settings, AppSettingsJsonContext.Default.AppSettings));
    }
}
