using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ChBrowser.Models;

namespace ChBrowser.Services.Storage;

/// <summary>
/// <see cref="FavoritesData"/> を <c>data/app/favorites.json</c> に読み書きする。
/// 詳細は 4.4.2 を参照。
///
/// <para>
/// 書き込みは <c>.tmp</c> に書いてから rename して atomic 化する。読み込み失敗 (ファイル不在 / 破損) は
/// 空 <see cref="FavoritesData"/> を返す。失敗してもアプリは止めない (お気に入りはオプショナルなので)。
/// </para>
/// </summary>
public sealed class FavoritesStorage
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented        = true,
    };

    private readonly string _path;

    public FavoritesStorage(DataPaths paths)
    {
        _path = paths.FavoritesJsonPath;
    }

    /// <summary>お気に入り全体を読む。ファイルが無い / 壊れている場合は空データ。</summary>
    public FavoritesData Load()
    {
        try
        {
            if (!File.Exists(_path)) return new FavoritesData();
            using var fs = File.OpenRead(_path);
            return JsonSerializer.Deserialize<FavoritesData>(fs, JsonOpts) ?? new FavoritesData();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FavoritesStorage] load failed: {ex.Message}");
            return new FavoritesData();
        }
    }

    /// <summary>お気に入り全体を保存。失敗してもアプリ動作は止めない。</summary>
    public void Save(FavoritesData data)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            using (var fs = File.Create(tmp))
            {
                JsonSerializer.Serialize(fs, data, JsonOpts);
            }
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(tmp, _path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FavoritesStorage] save failed: {ex.Message}");
        }
    }
}
