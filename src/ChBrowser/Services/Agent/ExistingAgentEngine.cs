namespace ChBrowser.Services.Agent;

/// <summary>既存の単一ループ実装 (旧 <c>AiChatViewModel.SendAsync</c> の本体) を
/// <see cref="IAgentEngine"/> として包む薄いラッパ。
///
/// <para>挙動は B0 以前と不変。ループ本体は引き続き <c>AiChatViewModel</c> 内に住み、
/// このラッパは委譲するだけ (= 既存コードを移動・改変しない)。新エンジンはこれに一切触れず
/// 別実装として並走する (doc/ai-agent-design.md §5 / §7)。</para></summary>
public sealed class ExistingAgentEngine : IAgentEngine
{
    private readonly Func<string, CancellationToken, Task> _runTurn;

    public ExistingAgentEngine(Func<string, CancellationToken, Task> runTurn) => _runTurn = runTurn;

    public Task RunTurnAsync(string userText, CancellationToken ct) => _runTurn(userText, ct);
}
