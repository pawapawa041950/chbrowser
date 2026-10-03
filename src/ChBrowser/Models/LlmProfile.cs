using System;
using System.Collections.Generic;
using System.Linq;

namespace ChBrowser.Models;

/// <summary>LLM の接続プロファイル 1 件 (設定ウィンドウ「LLM」カテゴリで名前を付けて複数登録する)。
/// AI チャット / NG 判定 AI / AI 翻訳 はどのプロファイルを使うかを Id で指定する (空ならデフォルトのプロファイル)。</summary>
/// <param name="Id">識別子 (名前を変えても参照が切れないよう、作成時に振る)。</param>
/// <param name="Name">表示名。</param>
/// <param name="ApiUrl">OpenAI 互換 API の URL (base URL でも /chat/completions まで含めても可)。</param>
/// <param name="ApiKey">API キー (Bearer)。空なら Authorization 無し。config.json に平文保存。</param>
/// <param name="Model">モデル名。</param>
/// <param name="ContextSize">コンテキストサイズ (トークン数)。</param>
/// <param name="SupportsImages">画像を入力できるモデルか。</param>
/// <param name="Concurrency">このプロファイルに同時に投げるリクエストの上限 (1〜<see cref="MaxConcurrency"/>)。NG 判定 AI と AI 翻訳が同じプロファイルを使うときは合計でこの数まで
/// (= LLM サーバの並列数に合わせる)。0 は未設定 (旧設定からの移行前) で、読み込み時に <see cref="LlmProfileMigration.FillConcurrency"/> が埋める。</param>
public sealed record LlmProfile(
    string Id,
    string Name,
    string ApiUrl,
    string ApiKey,
    string Model,
    int    ContextSize,
    bool   SupportsImages = false,
    int    Concurrency    = 0)
{
    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>新しく作るプロファイルの同時実行数。</summary>
    public const int DefaultConcurrency = 2;

    /// <summary>同時実行数の上限 (vLLM 等の並列処理の強いサーバ向けに大きめ)。</summary>
    public const int MaxConcurrency = 128;

    /// <summary>実際に使う同時実行数 (未設定なら既定値、1〜<see cref="MaxConcurrency"/> に収める)。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int EffectiveConcurrency => Concurrency <= 0 ? DefaultConcurrency : Math.Clamp(Concurrency, 1, MaxConcurrency);

    /// <summary>接続先 (URL とモデル名) が入っているか。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiUrl) && !string.IsNullOrWhiteSpace(Model);
}

/// <summary>旧形式の LLM 設定 (AI / 作業モデル / NG 判定 AI / AI 翻訳 がそれぞれ URL・キー・モデル・コンテキストサイズを持つ) を
/// プロファイル形式 (<see cref="AppConfig.LlmProfiles"/>) に移す。<see cref="AppConfig.LlmProfiles"/> が null (= 未移行) のときだけ行う。
/// 同じ接続 (URL・キー・モデル・コンテキストサイズがすべて同じ) は 1 つのプロファイルにまとめる。</summary>
public static class LlmProfileMigration
{
    public static AppConfig Migrate(AppConfig c, out bool migrated)
    {
        migrated = false;
        if (c.LlmProfiles is not null) return c;
        migrated = true;

        var profiles = new List<LlmProfile>();
        string? Add(string name, string? url, string? key, string? model, int ctx)
        {
            url = (url ?? "").Trim(); key = key ?? ""; model = (model ?? "").Trim();
            if (url.Length == 0 && model.Length == 0) return null;
            var same = profiles.FirstOrDefault(p => p.ApiUrl == url && p.ApiKey == key && p.Model == model && p.ContextSize == ctx);
            if (same is not null) return same.Id;
            var p = new LlmProfile(LlmProfile.NewId(), name, url, key, model, ctx);
            profiles.Add(p);
            return p.Id;
        }
        static string Pick(string? primary, string? fallback) => string.IsNullOrWhiteSpace(primary) ? (fallback ?? "") : primary!;

        var main = Add("メイン", c.LlmApiUrl, c.LlmApiKey, c.LlmModel, c.LlmContextSize);
        string? worker = null;
        if (c.SeparateWorkerModel)
            worker = Add("作業モデル", Pick(c.WorkerApiUrl, c.LlmApiUrl), Pick(c.WorkerApiKey, c.LlmApiKey), Pick(c.WorkerModel, c.LlmModel),
                         c.WorkerContextSize > 0 ? c.WorkerContextSize : c.LlmContextSize);
        var ng = Add("NG判定", c.NgAiApiUrl, c.NgAiApiKey, c.NgAiModel, c.NgAiContextSize);
        string? translate = null;
        if (!string.IsNullOrWhiteSpace(c.TranslateApiUrl) || !string.IsNullOrWhiteSpace(c.TranslateModel)
            || !string.IsNullOrWhiteSpace(c.TranslateApiKey) || c.TranslateContextSize > 0)
            translate = Add("翻訳", Pick(c.TranslateApiUrl, c.LlmApiUrl), Pick(c.TranslateApiKey, c.LlmApiKey), Pick(c.TranslateModel, c.LlmModel),
                            c.TranslateContextSize > 0 ? c.TranslateContextSize : c.LlmContextSize);

        var defaultId = main ?? profiles.FirstOrDefault()?.Id ?? "";
        string Ref(string? id) => id is null || id == defaultId ? "" : id;   // デフォルトと同じなら「デフォルトを使う」
        return c with
        {
            LlmProfiles           = profiles.ToArray(),
            DefaultLlmProfileId   = defaultId,
            AgentLlmProfileId     = Ref(main),
            WorkerLlmProfileId    = Ref(worker),
            NgAiLlmProfileId      = Ref(ng),
            TranslateLlmProfileId = Ref(translate),
            // 旧設定では NG 判定 AI は接続先が入っていれば動いていた (空なら機能オフ)
            NgAiEnabled           = ng is not null,
        };
    }

    /// <summary>同時実行数が未設定 (0) のプロファイルに値を入れる (2026-10: NG 判定 AI / AI 翻訳ごとの設定からプロファイルの設定へ移した)。
    /// NG 判定 AI / AI 翻訳が使っているプロファイルは旧設定の値 (両方なら大きい方)、どちらも使っていなければ既定値。</summary>
    public static AppConfig FillConcurrency(AppConfig c, out bool changed)
    {
        changed = false;
        if (c.LlmProfiles is not { Length: > 0 } list || list.All(p => p.Concurrency > 0)) return c;
        changed = true;
        var ngId = LlmSettings.ResolveProfile(c, c.NgAiLlmProfileId)?.Id;
        var trId = LlmSettings.ResolveProfile(c, c.TranslateLlmProfileId)?.Id;
        LlmProfile Fill(LlmProfile p)
        {
            if (p.Concurrency > 0) return p;
            var olds = new List<int>();
            if (p.Id == ngId) olds.Add(c.NgAiConcurrency);
            if (p.Id == trId) olds.Add(c.TranslateConcurrency);
            var v = olds.Count > 0 ? olds.Max() : LlmProfile.DefaultConcurrency;
            return p with { Concurrency = Math.Clamp(v, 1, LlmProfile.MaxConcurrency) };
        }
        return c with { LlmProfiles = list.Select(Fill).ToArray() };
    }
}
