using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ChBrowser.Models;

namespace ChBrowser.Services.Storage;

/// <summary>ショートカット & マウスジェスチャー設定の永続化 (Phase 15)。
/// <c>data/app/shortcuts.json</c> に <see cref="ShortcutSettings"/> をそのまま JSON で保存する。</summary>
public sealed class ShortcutStorage
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented        = true,
    };

    private readonly string _path;

    public ShortcutStorage(DataPaths paths)
    {
        _path = Path.Combine(paths.AppDir, "shortcuts.json");
    }

    public ShortcutSettings Load()
    {
        if (!File.Exists(_path)) return new ShortcutSettings();
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<ShortcutSettings>(json, JsonOpts) ?? new ShortcutSettings();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ShortcutStorage] load {_path} failed: {ex.Message}");
            return new ShortcutSettings();
        }
    }

    public void Save(ShortcutSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(settings, JsonOpts);
            var tmp  = _path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(tmp, _path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ShortcutStorage] save {_path} failed: {ex.Message}");
        }
    }
}
