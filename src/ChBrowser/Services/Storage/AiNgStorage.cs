using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ChBrowser.Services.Storage;

/// <summary>AI による NG 判定スコア (レス番号 → 1..5 の攻撃度) を per-thread で永続化する。
/// <c>data/&lt;rootDomain&gt;/&lt;dir&gt;/&lt;threadKey&gt;.aing.json</c>。
/// スレを再オープンしたときに既判定分を読み戻し、未判定の新規レスだけを LLM にかけ直すために使う。</summary>
public sealed class AiNgStorage
{
    private readonly DataPaths _paths;
    public AiNgStorage(DataPaths paths) => _paths = paths;

    private sealed record FileModel(int Version, Dictionary<int, int> Scores);

    /// <summary>保存済みスコア (レス番号 → 1..5) を読む。無ければ空辞書。</summary>
    public Dictionary<int, int> Load(string host, string dir, string threadKey)
    {
        try
        {
            var path = _paths.AiNgScoresPath(host, dir, threadKey);
            if (!File.Exists(path)) return new();
            var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path));
            return model?.Scores ?? new();
        }
        catch
        {
            return new();
        }
    }

    /// <summary>保存済みスコアファイルを削除する (= このスレの LLM 判定情報を全クリア)。無ければ何もしない。</summary>
    public void Delete(string host, string dir, string threadKey)
    {
        try
        {
            var path = _paths.AiNgScoresPath(host, dir, threadKey);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write($"[AiNg] スコア削除に失敗: {ex.Message}");
        }
    }

    /// <summary>スコア辞書を保存する (原子的に tmp→rename)。</summary>
    public void Save(string host, string dir, string threadKey, IReadOnlyDictionary<int, int> scores)
    {
        try
        {
            var path = _paths.AiNgScoresPath(host, dir, threadKey);
            var json = JsonSerializer.Serialize(new FileModel(1, new Dictionary<int, int>(scores)),
                new JsonSerializerOptions { WriteIndented = false });
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        catch (Exception ex)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write($"[AiNg] スコア保存に失敗: {ex.Message}");
        }
    }
}
