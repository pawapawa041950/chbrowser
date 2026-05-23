namespace ChBrowser.Services.Agent;

/// <summary>Agent 本体の抽象。1 ユーザ送信 = 1 ターン。実装は <see cref="NewAgentEngine"/>
/// (3 レイヤー: Strategist / Worker / ToolRuntime)。旧単一ループ実装は本機能昇格時に撤去済み
/// (doc/ai-agent-design.md §5.1)。
///
/// <para>UI 出力先 (IAgentHost) と共有ツール土台 (AgentToolContext) は実装ごとに
/// コンストラクタで注入する。セッション内で不変なため per-call ではなく構築時に渡す。</para></summary>
public interface IAgentEngine
{
    /// <summary>1 ユーザ送信を処理する。</summary>
    Task RunTurnAsync(string userText, CancellationToken ct);
}
