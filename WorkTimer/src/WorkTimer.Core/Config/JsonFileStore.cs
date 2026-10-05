using System.Text.Json;
using System.Text.Json.Serialization;
using WorkTimer.Core.Diagnostics;

namespace WorkTimer.Core.Config;

/// <summary>
/// 通用 JSON 文件读写。三件必须做对的事：
///  1. <b>原子写入</b>：先写 .tmp 再替换，避免写一半断电变成半截 JSON；
///  2. <b>坏文件隔离</b>：解析失败时把原文件改名成 .bad-时间戳 并记日志，
///     而不是直接覆盖 —— 用户手改配置写错了还能救回来；
///  3. <b>容错解析</b>：允许尾随逗号和注释，手改配置的容错空间大一点。
/// </summary>
public sealed class JsonFileStore<T> where T : class, new()
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _path;
    private readonly string _area;

    public JsonFileStore(string path, string area)
    {
        _path = path;
        _area = area;
    }

    public string Path => _path;

    public T Load()
    {
        if (!File.Exists(_path))
        {
            Log.Info(_area, $"配置文件不存在，使用默认值并生成：{_path}");
            var fresh = new T();
            TrySave(fresh);
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json))
            {
                Log.Warn(_area, $"配置文件为空，使用默认值：{_path}");
                return new T();
            }

            var parsed = JsonSerializer.Deserialize<T>(json, Options);
            if (parsed is null)
            {
                Log.Warn(_area, $"配置文件反序列化结果为 null，使用默认值：{_path}");
                return new T();
            }

            Log.Info(_area, $"已加载配置：{_path}");
            return parsed;
        }
        catch (Exception ex)
        {
            var quarantine = Quarantine();
            Log.Error(_area, $"配置文件解析失败，已隔离到 {quarantine}，本次使用默认值", ex);
            return new T();
        }
    }

    public bool TrySave(T value)
    {
        try
        {
            Save(value);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(_area, $"保存配置失败：{_path}", ex);
            return false;
        }
    }

    public void Save(T value)
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(value, Options);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);

        if (File.Exists(_path))
        {
            File.Replace(tmp, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tmp, _path);
        }
    }

    private string Quarantine()
    {
        try
        {
            var bad = $"{_path}.bad-{DateTimeOffset.Now:yyyyMMddHHmmss}";
            File.Move(_path, bad, overwrite: true);
            return bad;
        }
        catch
        {
            return "(隔离失败)";
        }
    }
}
