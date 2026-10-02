using ChBrowser.Models;
using ChBrowser.Services.Llm;

namespace ChBrowser.Services.Agent;

/// <summary>新 3 レイヤーエージェントの <see cref="IAgentEngine"/> 実装。doc/ai-agent-design.md §2 / §5。
///
/// <para>永続 <see cref="Strategist"/> (D8) を保持し、<c>dispatch_task</c> ごとに使い捨て <see cref="Worker"/> を
/// 起動する。L1 <see cref="ToolRuntime"/> は Worker 間で共有 (archive を共有するため)。
/// 1 ターンの複数 dispatch_task は設定 (allowParallel) で同時実行する (D7)。</para>
///
/// <para>背景 (contextPreamble = 対応掲示板・対象スレの状況・自分の書き込み) は両層の system プロンプトの末尾に付け、
/// スレの切り替えで <see cref="UpdateContextPreamble"/> により差し替える。</para></summary>
public sealed class NewAgentEngine : IAgentEngine
{
    private readonly LlmClient   _llm;
    private readonly LlmSettings _workerSettings;
    private readonly ToolRuntime _runtime;
    private readonly Strategist  _strategist;
    private string               _workerSystemPrompt;

    public NewAgentEngine(
        IAgentHost       host,
        AgentToolContext ctx,
        LlmClient        llm,
        LlmSettings      strategistSettings,
        LlmSettings      workerSettings,
        string           contextPreamble,
        bool             allowParallel)
    {
        _llm            = llm;
        _workerSettings = workerSettings;
        _runtime        = new ToolRuntime(ctx);
        _workerSystemPrompt = BuildWorkerPrompt(contextPreamble);

        // Strategist の recall_archive も 1 回の結果を上限に収める (原文の全量で Strategist の文脈を溢れさせない)
        var recallMax = ContextBudget.ToolOutputBudget(strategistSettings);
        _strategist = new Strategist(llm, strategistSettings, host, DispatchAsync, BuildStrategistPrompt(contextPreamble),
                                     argsJson => ContextBudget.CapToolOutput(
                                         ctx.Archive.TryExecute("recall_archive", argsJson) ?? "{\"error\":\"recall 失敗\"}", recallMax, null),
                                     allowParallel);
    }

    public Task RunTurnAsync(string userText, CancellationToken ct) => _strategist.RunTurnAsync(userText, ct);

    /// <inheritdoc/>
    public void UpdateContextPreamble(string contextPreamble)
    {
        _workerSystemPrompt = BuildWorkerPrompt(contextPreamble);
        _strategist.UpdateSystemPrompt(BuildStrategistPrompt(contextPreamble));
    }

    /// <summary><c>dispatch_task</c> の実体: 使い捨て Worker を 1 つ起動してタスクを実行する。</summary>
    private async Task<TaskResult> DispatchAsync(TaskSpec spec, IWorkSection section, CancellationToken ct)
    {
        var worker = new Worker(_llm, _workerSettings, _runtime, _workerSystemPrompt);
        return await worker.RunAsync(spec, section, ct).ConfigureAwait(true);
    }

    private static string BuildWorkerPrompt(string contextPreamble)
        => Worker.DefaultSystemPrompt + "\n\n# 背景\n" + contextPreamble;

    private static string BuildStrategistPrompt(string contextPreamble) =>
        "あなたは掲示板ブラウザ内蔵 AI エージェントの司令塔 (Strategist) です。ユーザの依頼を理解し、作業を Worker に委譲し、" +
        "Worker の報告 (finding) をもとにユーザへ回答します。\n" +
        "\n" +
        "# 役割分担\n" +
        "- **あなた自身はスレや板を読むツールを持たない。** 情報収集もアプリ操作も、記憶や推測で済ませず dispatch_task で Worker に委譲する。\n" +
        "- Worker はスレ / 板の読み取りと横断検索、WEB 検索・ページ取得 (掲示板の外の事実確認や用語の裏取り)、" +
        "アプリのペインに開く操作 (open_thread_list_in_app / open_thread_in_app / open_board_in_app) ができる。\n" +
        "- Worker にはユーザの依頼の原文が渡る。goal には「何をして、何を持ち帰れば完了か (アプリに表示するならそこまで)」を具体的に書く。\n" +
        "- Worker から返るのは finding (報告) と evidence id だけ。原文が必要なときだけ recall_archive(id) で引く。\n" +
        "- 下の「背景」に対象スレの状況 (新着・自分の書き込み・自分への返信) があれば、それで答えられることは委譲せずに答えてよい。\n" +
        "\n" +
        "# 進め方\n" +
        "- 単純な依頼は create_plan を使わず dispatch_task を 1 回でよい。複数の段階に分かれる依頼だけ create_plan で宣言する。\n" +
        "- 互いに独立したタスクは、1 回の応答で dispatch_task を複数同時に呼ぶ (並列に実行される)。依存するタスクは 1 つずつ順に。\n" +
        "- scan_breadth には調べる範囲 (single / few / many / all_boards) を指定する。予算 (ツール回数) はそこから自動で決まるので計算しなくてよい。\n" +
        "- heavy 判定が返ったら、実行前に ask_user で「A=そのまま実施 / B=範囲を絞った軽量版 / C=別案」を提示して確認を取る。\n" +
        "- タスクが partial / failed なら finding の理由を読み、別のやり方 (別の板・別の語・範囲の分割) で dispatch し直すか revise_plan する。同じ委譲を繰り返さない。\n" +
        "- 依頼が曖昧で進められないときだけ ask_user で質問する。推測で補えるなら補って進める。\n" +
        "- 新しいメッセージが前の依頼の続きなら revise_plan / dispatch_task、明確に別件なら create_plan。前の finding は会話に残っているので参照してよい。\n" +
        "\n" +
        "# 「特定の作品 / 製品 / 人物 / 事象に関するスレを探す・集める」依頼\n" +
        "- 対象を確信を持って言い換えられる (正式名称・略称・作者やキャラ・探すべき板が分かる) なら、検索から表示までを 1 タスクで dispatch する。\n" +
        "- 略称・新しめ・曖昧で自信が無ければ 2 段に分ける: (1) web_search で正式名称・別名・関連語・探すべき板を特定して列挙させる → " +
        "(2) その語群を context_hint に入れて、板を横断検索し open_thread_list_in_app で 1 タブに表示させる。\n" +
        "- スレを開く操作は、そのスレを見つけたタスク自身にさせる (finding からは thread_url が欠けることがあり、同じタイトルのタブは置き換わるため、検索と表示を別タスクに割らない)。\n" +
        "\n" +
        "# 最終回答\n" +
        "全タスクが済んだら、ツールを呼ばずに回答をテキストで書く (= ターンの終了)。\n" +
        "- 質問・要約・説明の依頼: finding を材料に、ユーザの問いに直接答える。話題や意見は具体的に、根拠のレス番号 (>>N) やスレを添える。finding に無いことは書かない。\n" +
        "- 「リストアップして」「開いて」「表示して」「集めて」など結果をアプリで見る依頼: 結果はアプリのペインに出ているので、" +
        "「『〜』としてスレ一覧に N 件表示しました」のような短い完了報告にする。チャットに長く列挙しない。\n" +
        "- 完了報告は finding の確証に基づくこと。status が partial / failed のとき、または finding に表示した確証 (『N 件をタブに表示』等) が無いときは" +
        "『表示しました』と書かない。見つからなかった / 開けなかった事実を正直に伝える。\n" +
        "\n" +
        "# 背景\n" + contextPreamble;
}
