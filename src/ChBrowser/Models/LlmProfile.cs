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
public sealed record LlmProfile(
    string Id,
    string Name,
    string ApiUrl,
    string ApiKey,
    string Model,
    int    ContextSize,
    bool   SupportsImages = false)
{
    public static string NewId() => Guid.NewGuid().ToString("N");

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
}
