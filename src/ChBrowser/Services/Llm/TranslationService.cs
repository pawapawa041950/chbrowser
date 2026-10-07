using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Storage;

namespace ChBrowser.Services.Llm;

/// <summary>AI 翻訳の共通窓口。スレ表示 (レス本文) / スレ一覧 (スレタイ) / 今後の書き込み窓など、翻訳を使う画面はここを通す。
///
/// <list type="bullet">
/// <item><description>接続: 設定 → AI翻訳 で選んだ LLM プロファイル (<see cref="LlmSettings.TranslateFromConfig"/>)。</description></item>
/// <item><description>同時に投げるリクエスト数: 使う LLM プロファイルの同時実行数まで (<see cref="WithSlotAsync{T}"/>)。
///   枠はプロファイル単位で NG 判定 AI 等とも共有する (<see cref="LlmConcurrency"/>)。</description></item>
/// <item><description>スレタイの訳は、原文 → 訳の対応を <c>cache/title-translations.json</c> に残し、同じタイトルを何度も送らない
///   (タイトルは短く、同じスレが何度も一覧に出るため)。</description></item>
/// </list></summary>
public sealed class TranslationService
{
    /// <summary>最初の 1 回の推論で送るタイトルの数。少ないほど最初の訳が早く出る (ローカル 70 tok/s で 5 件 ≒ 2.5 秒)。</summary>
    public const int FirstTitlesPerRequest = 5;
    /// <summary>2 回目以降の 1 回の推論で送るタイトルの数。1 回ごとに最初の文字まで約 1 秒の固定の待ちがあるので、
    /// まとめるほど全体は早く終わる (150 件: 5 件ずつ 82 秒 → 10 件ずつで 60 秒台の見積もり)。多すぎると取り違え・欠落が増える。</summary>
    public const int TitlesPerRequest = 10;
    /// <summary>タイトルの訳を残す上限件数 (超えたら古いものから捨てる)。</summary>
    private const int MaxCachedTitles = 20000;

    public const string NotConfiguredMessage = "AI翻訳の接続が未設定です (設定 → LLM でプロファイルを登録し、設定 → AI翻訳 で使うプロファイルを選んでください)";

    private readonly LlmClient       _llm;
    private readonly Func<AppConfig> _config;
    private readonly string          _titleCachePath;

    private Dictionary<string, string>? _titles;   // 原文 → 訳 (挿入順 = 古い順)
    private bool _titlesDirty;
    /// <summary>タイトルの訳の読み書きの排他 (まとめ翻訳の要求は並行に終わるので、UI スレッド以外から呼ばれても壊れないように)。</summary>
    private readonly object _titlesLock = new();

    public TranslationService(LlmClient llm, Func<AppConfig> config, DataPaths paths)
    {
        _llm            = llm;
        _config         = config;
        _titleCachePath = paths.TitleTranslationsPath;
    }

    /// <summary>今の設定の翻訳用接続。</summary>
    public LlmSettings Settings => LlmSettings.TranslateFromConfig(_config());

    /// <summary>翻訳の接続が設定されているか。</summary>
    public bool IsConfigured => Settings is { } s && !string.IsNullOrWhiteSpace(s.ApiUrl) && !string.IsNullOrWhiteSpace(s.Model);

    private bool DisableReasoning => _config().TranslateDisableReasoning;

    /// <summary>同時実行の枠 (使う LLM プロファイル単位で共有。設定が変わったら次の要求から新しい上限)。</summary>
    private SemaphoreSlim Gate => LlmConcurrency.Gate(Settings);

    /// <summary>同時実行の枠を 1 つ取ってから <paramref name="body"/> を実行する (枠が空くまで待つ)。
    /// 枠を取った後にしたい処理 (読み込み中の表示など) がある呼び出し元向け。</summary>
    public async Task<T> WithSlotAsync<T>(Func<Task<T>> body, CancellationToken ct)
    {
        var gate = Gate;
        await gate.WaitAsync(ct).ConfigureAwait(true);
        try { return await body().ConfigureAwait(true); }
        finally { gate.Release(); }
    }

    /// <summary>レス本文 1 件を日本語に訳す (表示用に整形した訳。応答が空なら null)。同時実行の枠は呼び出し元が取る
    /// (<see cref="WithSlotAsync{T}"/>)。失敗は <see cref="AiTranslateException"/>。</summary>
    public Task<string?> TranslatePostAsync(string plain, CancellationToken ct)
        => AiTranslator.TranslateOneAsync(_llm, Settings, plain, DisableReasoning, ct);

    /// <summary>Markdown で書かれたレス本文 1 件を、記法を保ったまま日本語に訳す (訳した Markdown。応答が空なら null)。同時実行の枠は呼び出し元が取る。</summary>
    public Task<string?> TranslateMarkdownPostAsync(string markdown, CancellationToken ct)
        => AiTranslator.TranslateMarkdownAsync(_llm, Settings, markdown, DisableReasoning, ct);

    // ---- スレタイ ----

    /// <summary>保存済みのタイトルの訳 (無ければ null)。</summary>
    public string? CachedTitle(string title)
    {
        lock (_titlesLock) return Titles.TryGetValue(title, out var t) ? t : null;
    }

    /// <summary>タイトルを訳す必要があるか (日本語でなく、まだ訳が無い)。</summary>
    public bool TitleNeedsTranslation(string title)
        => !string.IsNullOrWhiteSpace(title) && CachedTitle(title) is null && AiTranslator.NeedsTranslation(title);

    /// <summary>タイトルをまとめて訳す (最初は <see cref="FirstTitlesPerRequest"/> 件、以降 <see cref="TitlesPerRequest"/> 件ずつ・同時実行の枠の中で)。訳せた分は保存し、
    /// 1 回分の要求が終わるごとに <paramref name="onBatch"/> を呼ぶ (画面を少しずつ更新するため)。
    /// 戻り値: 訳せた件数。失敗 (接続エラー等) は <see cref="AiTranslateException"/> (それまでの分は保存済み)。</summary>
    public async Task<int> TranslateTitlesAsync(IReadOnlyList<string> titles, Action<int>? onBatch, CancellationToken ct)
    {
        var todo = titles.Where(TitleNeedsTranslation).Distinct(StringComparer.Ordinal).ToList();
        var done = 0;
        try
        {
            var tasks = new List<Task>();
            for (var i = 0; i < todo.Count; )
            {
                var size  = i == 0 ? FirstTitlesPerRequest : TitlesPerRequest;   // 最初だけ少なく (= 最初の訳を早く出す)
                var batch = todo.GetRange(i, Math.Min(size, todo.Count - i));
                i += batch.Count;
                tasks.Add(WithSlotAsync(async () =>
                {
                    ct.ThrowIfCancellationRequested();
                    var got = await AiTranslator.TranslateTitlesAsync(_llm, Settings, batch, DisableReasoning, ct).ConfigureAwait(true);
                    // 応答から取れなかったタイトルは 1 件ずつ送り直す (まとめた応答はモデルが形を崩しやすく、同じ組み合わせで
                    // 送り直しても同じように崩れて、何度押しても訳されないタイトルが残るため。1 件だけなら崩れにくい)
                    if (batch.Count > 1 && got.Count < batch.Count)
                    {
                        for (var k = 0; k < batch.Count; k++)
                        {
                            if (got.ContainsKey(k)) continue;
                            ct.ThrowIfCancellationRequested();
                            var single = await AiTranslator.TranslateTitlesAsync(_llm, Settings, new[] { batch[k] }, DisableReasoning, ct).ConfigureAwait(true);
                            if (single.TryGetValue(0, out var tr1)) got[k] = tr1;
                        }
                    }
                    int now;
                    lock (_titlesLock)
                    {
                        foreach (var (idx, tr) in got) AddTitle(batch[idx], tr);
                        now = done += got.Count;
                    }
                    onBatch?.Invoke(now);
                    return true;
                }, ct));
            }
            await Task.WhenAll(tasks).ConfigureAwait(true);
        }
        finally
        {
            SaveTitles();
        }
        return done;
    }

    /// <summary>タイトルの訳を消す (おかしな訳を消して訳し直せるようにする)。戻り値: 消した件数。</summary>
    public int RemoveTitles(IEnumerable<string> titles)
    {
        var n = 0;
        lock (_titlesLock)
        {
            foreach (var t in titles)
                if (Titles.Remove(t)) n++;
            if (n > 0) _titlesDirty = true;
        }
        if (n > 0) SaveTitles();
        return n;
    }

    private Dictionary<string, string> Titles => _titles ??= LoadTitles();

    /// <summary>訳を 1 件足す (<see cref="_titlesLock"/> の中で呼ぶ)。</summary>
    private void AddTitle(string original, string translated)
    {
        var map = Titles;
        map.Remove(original);   // 入れ直して「新しい」側へ
        map[original] = translated;
        if (map.Count > MaxCachedTitles)
            foreach (var k in map.Keys.Take(map.Count - MaxCachedTitles).ToList()) map.Remove(k);
        _titlesDirty = true;
    }

    private Dictionary<string, string> LoadTitles()
    {
        try
        {
            if (File.Exists(_titleCachePath))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(_titleCachePath))
                       ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write($"[TranslationService] タイトルの訳の読み込みに失敗: {ex.Message}");
        }
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private void SaveTitles()
    {
        byte[] bytes;
        lock (_titlesLock)
        {
            if (!_titlesDirty || _titles is null) return;
            bytes = JsonSerializer.SerializeToUtf8Bytes(_titles, JsonOpts);
        }
        try
        {
            var tmp = _titleCachePath + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, _titleCachePath, overwrite: true);
            _titlesDirty = false;
        }
        catch (IOException ex)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write($"[TranslationService] タイトルの訳の保存に失敗: {ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
