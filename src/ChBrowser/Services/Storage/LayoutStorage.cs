using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ChBrowser.Models;

namespace ChBrowser.Services.Storage;

/// <summary>
/// <see cref="LayoutState"/> を <c>data/app/layout.json</c> に読み書きする。
///
/// <para>
/// 書き込みは <c>.tmp</c> に書いてから rename して atomic 化する (途中で kill されたときに
/// 半端な JSON で起動できなくなるのを防ぐため)。読み込み失敗 (ファイル不在・破損) は null。
/// </para>
/// </summary>
public sealed class LayoutStorage
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented        = true,
    };

    private readonly string _path;

    public LayoutStorage(DataPaths paths)
    {
        _path = paths.LayoutJsonPath;
    }

    /// <summary>レイアウトを読む。ファイルが無い / 壊れている場合は null。</summary>
    public LayoutState? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            using var fs = File.OpenRead(_path);
            return JsonSerializer.Deserialize<LayoutState>(fs, JsonOpts);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LayoutStorage] load failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>レイアウトを保存。失敗してもアプリ動作は止めない。</summary>
    public void Save(LayoutState state)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            using (var fs = File.Create(tmp))
            {
                JsonSerializer.Serialize(fs, state, JsonOpts);
            }
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(tmp, _path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LayoutStorage] save failed: {ex.Message}");
        }
    }
}
