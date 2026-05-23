using ChBrowser.Services.Llm;

namespace ChBrowser.Services.Agent;

/// <summary>新エンジンの各層に渡す共有ツール土台。doc/ai-agent-design.md §5.3。
///
/// <para>既存 Toolset をそのまま保持する。新エンジンの L1 ToolRuntime はこれを
/// 検証・正規化・archive 記録 (id 返却) で包み、Worker には正規化結果と archive id だけを返す (D5)。
/// plan まわりは Strategist 用ツール定義を別途新設し、ここからは plan データ構造・置換/マージ
/// ロジックのみ再利用する (= <c>PlanToolset.GetToolDefinitions()</c> はそのまま提示しない / 資料 §4.1)。</para></summary>
public sealed class AgentToolContext
{
    /// <summary>plan の状態 (タスク一覧) と置換/マージのロジック。</summary>
    public PlanToolset Plan { get; }

    /// <summary>スレ読み取り・横断・開く系。スレ非アタッチなら null。
    /// AI チャットの context 切替 (<c>AiChatViewModel.SwitchContext</c>) で差し替えられるよう set 可能。
    /// <see cref="ToolRuntime"/> はこれを毎回参照するので、差し替えると以降の Worker が新スレを使う。</summary>
    public ThreadToolset? Thread { get; set; }

    /// <summary>セッション共有の archive。並列 Worker から同時追記されるためスレッドセーフ化が要る (§5.3)。</summary>
    public ChatArchive Archive { get; }

    public AgentToolContext(PlanToolset plan, ThreadToolset? thread, ChatArchive archive)
    {
        Plan    = plan;
        Thread  = thread;
        Archive = archive;
    }
}
