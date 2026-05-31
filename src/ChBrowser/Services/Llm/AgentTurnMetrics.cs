namespace ChBrowser.Services.Llm;

/// <summary>1 ユーザ送信 (= 1 アシスタントバブル) の LLM 計測の集計。
/// <see cref="LlmClient.ActiveMetrics"/> に設定すると、各 <see cref="LlmClient.ChatStreamAsync"/> 完了時に
/// 当該呼び出しの値が加算される (Strategist + 全 Worker 分が合算される)。
///
/// <para>TTFT と総時間はバブル全体の壁時計で測る方が自然なので host (AiChatViewModel) 側で計測する。
/// ここでは「LLM 生成に費やした時間 (<see cref="GenMs"/>)」「推論 (reasoning_content) 時間 (<see cref="ReasoningMs"/>)」
/// 「出力トークン数 (<see cref="CompletionTokens"/>)」を合算する。すべて UI スレッド上で直列に加算されるため
/// ロック不要 (並列 Worker でも継続は UI スレッドに戻る)。</para></summary>
public sealed class AgentTurnMetrics
{
    /// <summary>このターンで行った LLM 呼び出し回数。</summary>
    public int LlmCalls { get; private set; }
    /// <summary>推論 (reasoning_content) に費やした時間の合計 (ms)。reasoning を出さないモデルでは 0。</summary>
    public long ReasoningMs { get; private set; }
    /// <summary>LLM 生成時間 (各呼び出しのリクエスト→完了) の合計 (ms)。token/s の分母に使う。</summary>
    public long GenMs { get; private set; }
    /// <summary>出力トークン数の合計 (usage 由来。取得できなければ概算)。</summary>
    public long CompletionTokens { get; private set; }

    public void Reset()
    {
        LlmCalls = 0;
        ReasoningMs = 0;
        GenMs = 0;
        CompletionTokens = 0;
    }

    public void Add(int reasoningMs, int genMs, int completionTokens)
    {
        LlmCalls++;
        if (reasoningMs > 0)      ReasoningMs      += reasoningMs;
        if (genMs > 0)            GenMs            += genMs;
        if (completionTokens > 0) CompletionTokens += completionTokens;
    }
}
