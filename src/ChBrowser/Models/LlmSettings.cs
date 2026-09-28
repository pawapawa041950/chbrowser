using System.Linq;

namespace ChBrowser.Models;

/// <summary>LLM 接続設定のスナップショット。使うプロファイル (<see cref="LlmProfile"/>) を解決して
/// <see cref="ChBrowser.Services.Llm.LlmClient"/> に渡すための値オブジェクト。</summary>
/// <param name="SupportsImages">画像を入力できるモデルか (プロファイルの設定)。</param>
public sealed record LlmSettings(string ApiUrl, string ApiKey, string Model, int ContextSize, bool SupportsImages = false)
{
    public static LlmSettings Empty { get; } = new("", "", "", 0);

    public static LlmSettings FromProfile(LlmProfile? p)
        => p is null ? Empty : new(p.ApiUrl ?? "", p.ApiKey ?? "", p.Model ?? "", p.ContextSize, p.SupportsImages);

    /// <summary>Id でプロファイルを引く。空・見つからないならデフォルトのプロファイル (それも無ければ最初のもの、無ければ null)。</summary>
    public static LlmProfile? ResolveProfile(AppConfig c, string? id)
    {
        var list = c.LlmProfiles;
        if (list is null || list.Length == 0) return null;
        return (!string.IsNullOrEmpty(id) ? list.FirstOrDefault(p => p.Id == id) : null)
            ?? list.FirstOrDefault(p => p.Id == c.DefaultLlmProfileId)
            ?? list[0];
    }

    /// <summary>デフォルトのプロファイル。</summary>
    public static LlmSettings FromConfig(AppConfig config) => FromProfile(ResolveProfile(config, null));

    /// <summary>エージェントの Strategist (戦略層) 接続 (AIチャット カテゴリで選んだプロファイル)。</summary>
    public static LlmSettings StrategistFromConfig(AppConfig c) => FromProfile(ResolveProfile(c, c.AgentLlmProfileId));

    /// <summary>エージェントの Worker (実行層) 接続。<see cref="AppConfig.SeparateWorkerModel"/> が false なら戦略検討モデルと同じ。</summary>
    public static LlmSettings WorkerFromConfig(AppConfig c)
        => c.SeparateWorkerModel ? FromProfile(ResolveProfile(c, c.WorkerLlmProfileId)) : StrategistFromConfig(c);

    /// <summary>NG 判定 AI の接続 (機能の ON / OFF は <see cref="AppConfig.NgAiEnabled"/>)。</summary>
    public static LlmSettings NgFromConfig(AppConfig c) => FromProfile(ResolveProfile(c, c.NgAiLlmProfileId));

    /// <summary>AI 翻訳の接続。</summary>
    public static LlmSettings TranslateFromConfig(AppConfig c) => FromProfile(ResolveProfile(c, c.TranslateLlmProfileId));

    /// <summary>接続先 (URL とモデル名) が入っているか。</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiUrl) && !string.IsNullOrWhiteSpace(Model);
}
