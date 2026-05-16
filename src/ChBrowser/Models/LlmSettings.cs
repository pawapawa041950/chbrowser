namespace ChBrowser.Models;

/// <summary>LLM 接続設定のスナップショット。<see cref="AppConfig"/> の LLM 系フィールドを
/// 1 つにまとめて <see cref="ChBrowser.Services.Llm.LlmClient"/> に渡すための値オブジェクト。</summary>
public sealed record LlmSettings(string ApiUrl, string ApiKey, string Model, int ContextSize)
{
    /// <summary>AppConfig から現在の LLM 設定を切り出す。</summary>
    public static LlmSettings FromConfig(AppConfig config)
        => new(config.LlmApiUrl ?? "", config.LlmApiKey ?? "", config.LlmModel ?? "", config.LlmContextSize);
}
