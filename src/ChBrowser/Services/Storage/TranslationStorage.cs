using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ChBrowser.Services.Storage;

/// <summary>1 スレ分の AI 翻訳 (<c>&lt;key&gt;.tr.json</c>)。</summary>
/// <param name="ThreadOn">スレ全体の翻訳が ON (新着も自動で翻訳する)。</param>
/// <param name="Shown">翻訳で表示しているレス番号 (原文に戻したレスは訳文を残したまま外す)。</param>
/// <param name="Posts">レス番号 → 訳文 (表示用に整形済み)。</param>
public sealed record ThreadTranslation(bool ThreadOn, List<long> Shown, Dictionary<long, string> Posts)
{
    public static ThreadTranslation Empty() => new(false, new List<long>(), new Dictionary<long, string>());
}

/// <summary><see cref="ThreadTranslation"/> の読み書き (スレのログの隣)。同じレスを何度も LLM に送らないために訳文を残す。
/// ログ削除 (<c>DatClient.DeleteLog</c>) で一緒に消える。</summary>
public sealed class TranslationStorage
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented        = false,
        Encoder              = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly DataPaths _paths;
    public TranslationStorage(DataPaths paths) => _paths = paths;

    public ThreadTranslation Load(string host, string dir, string threadKey)
    {
        try
        {
            var path = _paths.TranslationPath(host, dir, threadKey);
            if (!File.Exists(path)) return ThreadTranslation.Empty();
            var t = JsonSerializer.Deserialize<ThreadTranslation>(File.ReadAllBytes(path), Options);
            return t is null ? ThreadTranslation.Empty() : t with { Shown = t.Shown ?? new(), Posts = t.Posts ?? new() };
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write($"[translate] 翻訳の読み込みに失敗: {ex.Message}");
            return ThreadTranslation.Empty();
        }
    }

    public void Save(string host, string dir, string threadKey, ThreadTranslation t)
    {
        try
        {
            var path = _paths.TranslationPath(host, dir, threadKey);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(t, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write($"[translate] 翻訳の保存に失敗: {ex.Message}");
        }
    }
}
