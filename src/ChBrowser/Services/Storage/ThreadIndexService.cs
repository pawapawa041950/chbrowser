using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ChBrowser.Models;

namespace ChBrowser.Services.Storage;

/// <summary>
/// スレッドごとの <see cref="ThreadIndex"/> を idx.json として読み書きする。
/// 失敗 (壊れた JSON、ディスク I/O エラー等) は飲み込んでログに残すだけ。
/// </summary>
public sealed class ThreadIndexService
{
    private readonly DataPaths _paths;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented        = false,
    };

    public ThreadIndexService(DataPaths paths) => _paths = paths;

    public ThreadIndex? Load(string host, string directoryName, string threadKey)
    {
        var path = _paths.IdxJsonPath(host, directoryName, threadKey);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ThreadIndex>(json, Options);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ThreadIndexService] load failed ({path}): {ex.Message}");
            return null;
        }
    }

    public void Save(string host, string directoryName, string threadKey, ThreadIndex index)
    {
        var path = _paths.IdxJsonPath(host, directoryName, threadKey);
        try
        {
            var json = JsonSerializer.Serialize(index, Options);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ThreadIndexService] save failed ({path}): {ex.Message}");
        }
    }
}
