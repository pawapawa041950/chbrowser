namespace ChBrowser.Models;

/// <summary>LLM 接続設定のスナップショット。<see cref="AppConfig"/> の LLM 系フィールドを
/// 1 つにまとめて <see cref="ChBrowser.Services.Llm.LlmClient"/> に渡すための値オブジェクト。</summary>
public sealed record LlmSettings(string ApiUrl, string ApiKey, string Model, int ContextSize)
{
    /// <summary>AppConfig から現在の LLM 設定 (= 既存エンジン用) を切り出す。</summary>
    public static LlmSettings FromConfig(AppConfig config)
        => new(config.LlmApiUrl ?? "", config.LlmApiKey ?? "", config.LlmModel ?? "", config.LlmContextSize);

    /// <summary>エージェントの Strategist (戦略層) 接続。= メインの「AI モデル」(<see cref="FromConfig"/>)。
    /// 戦略検討モデルは常にメインモデルを使う (分けるのは Worker のみ)。</summary>
    public static LlmSettings StrategistFromConfig(AppConfig c) => FromConfig(c);

    /// <summary>エージェントの Worker (実行層) 接続。
    /// <see cref="AppConfig.SeparateWorkerModel"/> が false ならメインの「AI モデル」と同一を返す
    /// (= 1 設定で両方を動かす)。true なら Worker 設定を使い、未設定の項目はメインモデルにフォールバックする。</summary>
    public static LlmSettings WorkerFromConfig(AppConfig c)
    {
        var main = FromConfig(c);
        if (!c.SeparateWorkerModel) return main;   // 分けない → メインモデルと同じ
        return new(Pick(c.WorkerApiUrl, main.ApiUrl),
                   Pick(c.WorkerApiKey, main.ApiKey),
                   Pick(c.WorkerModel,  main.Model),
                   c.WorkerContextSize > 0 ? c.WorkerContextSize : main.ContextSize);
    }

    private static string Pick(string? primary, string? fallback)
        => string.IsNullOrWhiteSpace(primary) ? (fallback ?? "") : primary!;
}
