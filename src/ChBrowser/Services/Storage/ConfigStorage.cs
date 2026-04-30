using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ChBrowser.Models;

namespace ChBrowser.Services.Storage;

/// <summary>
/// <see cref="AppConfig"/> を <c>data/app/config.json</c> に読み書きする (Phase 11)。
/// 保存は atomic-tmp-rename、読み込み失敗・未知フィールドは既定値補填。
/// </summary>
public sealed class ConfigStorage
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented        = true,
    };

    private readonly string _path;

    public ConfigStorage(DataPaths paths)
    {
        _path = paths.ConfigJsonPath;
    }

    /// <summary>ファイルが無ければ既定値で読み込み完了扱い。
    /// 読み込み失敗 (パース不能) も既定値にフォールバック (= ユーザの設定が壊れていてもアプリは起動する)。</summary>
    public AppConfig Load()
    {
        if (!File.Exists(_path)) return new AppConfig();
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppConfig>(json, JsonOpts) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ConfigStorage] load failed: {ex.Message}");
            return new AppConfig();
        }
    }

    /// <summary>config.json を atomic に書き出す (.tmp に書いてから rename)。</summary>
    public void Save(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(config, JsonOpts);
            var tmp  = _path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(tmp, _path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ConfigStorage] save failed: {ex.Message}");
        }
    }
}
