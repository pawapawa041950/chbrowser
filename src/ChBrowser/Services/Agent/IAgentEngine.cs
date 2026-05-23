namespace ChBrowser.Services.Agent;

/// <summary>差し替え可能な Agent 本体。1 ユーザ送信 = 1 ターン。
/// 既存ループ (<see cref="ExistingAgentEngine"/>) と新エンジンを実行時に切替えるための seam
/// (doc/ai-agent-design.md §5.1 / 構築順 B0)。
///
/// <para>UI 出力先 (IAgentHost) と共有ツール土台 (AgentToolContext) は実装ごとに
/// コンストラクタで注入する。セッション内で不変なため per-call ではなく構築時に渡す。</para></summary>
public interface IAgentEngine
{
    /// <summary>1 ユーザ送信を処理する。</summary>
    Task RunTurnAsync(string userText, CancellationToken ct);
}
