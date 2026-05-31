using ChBrowser.Models;
using ChBrowser.Services.Llm;

namespace ChBrowser.Services.Agent;

/// <summary>新 3 レイヤーエージェントの <see cref="IAgentEngine"/> 実装。doc/ai-agent-design.md §2 / §5。
///
/// <para>永続 <see cref="Strategist"/> (D8) を保持し、<c>dispatch_task</c> ごとに使い捨て <see cref="Worker"/> を
/// 起動する。L1 <see cref="ToolRuntime"/> は Worker 間で共有 (archive を共有するため)。
/// B4 時点では逐次実行 (並列 D7 は B5)。</para></summary>
public sealed class NewAgentEngine : IAgentEngine
{
    private readonly LlmClient   _llm;
    private readonly LlmSettings _workerSettings;
    private readonly ToolRuntime _runtime;
    private readonly string      _workerSystemPrompt;
    private readonly Strategist  _strategist;

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
        _workerSystemPrompt = Worker.DefaultSystemPrompt + "\n\n# 背景\n" + contextPreamble;

        var stratPrompt = BuildStrategistPrompt(contextPreamble);
        _strategist = new Strategist(llm, strategistSettings, host, DispatchAsync, stratPrompt,
                                     argsJson => ctx.Archive.TryExecute("recall_archive", argsJson) ?? "{\"error\":\"recall 失敗\"}",
                                     allowParallel);
    }

    public Task RunTurnAsync(string userText, CancellationToken ct) => _strategist.RunTurnAsync(userText, ct);

    /// <summary><c>dispatch_task</c> の実体: 使い捨て Worker を 1 つ起動してタスクを実行する。</summary>
    private async Task<TaskResult> DispatchAsync(TaskSpec spec, IWorkSection section, CancellationToken ct)
    {
        var worker = new Worker(_llm, _workerSettings, _runtime, _workerSystemPrompt);
        return await worker.RunAsync(spec, section, ct).ConfigureAwait(true);
    }

    private static string BuildStrategistPrompt(string contextPreamble) =>
        "あなたは AI エージェントの戦略担当 (Strategist) です。ユーザの依頼を受け、計画を立て、各タスクを Worker に委譲し、" +
        "結果 (finding) を統合して最終回答を作ります。\n" +
        "\n" +
        "# あなたのツール\n" +
        "- create_plan: 複雑な依頼を複数タスクに分割して宣言する。\n" +
        "- revise_plan: 実行中に計画を作り直す。\n" +
        "- dispatch_task: 1 タスクを Worker に委譲して finding を得る。**単純な依頼は create_plan を呼ばず dispatch_task を直接 1 回**でよい (近道)。\n" +
        "- ask_user: 確認 / 質問してターンを終える。\n" +
        "- recall_archive: finding の evidence id から原文を引く (逐語引用が要るとき)。\n" +
        "\n" +
        "# 進め方\n" +
        "- あなた自身はスレを直接読まない。情報収集は必ず dispatch_task 経由で Worker にやらせる。\n" +
        "- Worker はスレ / 板の読み取りに加え、**WEB 検索 (web_search) と WEB ページ取得 (web_fetch)** もできる。" +
        "5ch の外の最新情報・事実確認・用語や固有名詞の裏取りが要るタスクは、その旨を goal に書いて dispatch_task せよ。\n" +
        "- **「特定の作品 / 製品 / 人物 / 事象に関するスレを探す」型の依頼で、対象が略称・新しめ・曖昧なとき**は、" +
        "Worker が web で正体と別名・関連語(キャラ名 / 作者 / 型番 / シリーズ等)を特定してから探す前提で goal を書く" +
        "(例:「対象を特定 → 正式名称 / 略称 / 関連語で 5ch を横断検索して開く」)。その分 max_tool_calls を web 1〜3 回ぶん多めに取る。\n" +
        "- Worker からは要約 (finding) と evidence id だけが返る (生データは返らない)。\n" +
        "- create_plan / dispatch_task が heavy 判定を返したら、実行前に必ず ask_user で A=実施 / B=軽量版 / C=別案 を提示する。\n" +
        "- タスクが partial / failed のときは finding を読み、別アプローチで dispatch し直すか、計画を revise する。同じ委譲を繰り返さない。\n" +
        "- 全タスクが済んだら、ツールを呼ばずに最終回答を**テキストで**書く (= それがターンの終了)。\n" +
        "- 過去の finding はターンを跨いで参照できる。継続依頼は revise_plan / dispatch_task、明確な別件は create_plan を使う。\n" +
        "- **互いに独立した複数タスク**は、1 ターンで dispatch_task を**複数同時に**呼ぶと並列実行される (= 各 Worker の問い合わせが同時に走り高速化する。並列が許可され接続先が分かれている場合)。依存があるタスク (前の結果を次で使う) は 1 つずつ順に呼ぶこと。\n" +
        "\n" +
        "# 結果はアプリの機能に出力する ★重要\n" +
        "ユーザの依頼が結果を「アプリ上で見る」ことを意図している場合 (例: 「〜のスレをリストアップして」「開いて」「表示して」「集めて」)、" +
        "チャットにテキストで列挙して終わらせてはいけない。Worker はスレ / 板 / スレ一覧をアプリのペインに開く能力を持つ" +
        "(open_thread_in_app / open_board_in_app / open_thread_list_in_app)。\n" +
        "- そういう依頼では、dispatch_task の goal に **「見つけた結果を open_thread_list_in_app でスレ一覧ペインに表示するところまで」** を必ず含める" +
        "(単一スレなら open_thread_in_app、板なら open_board_in_app)。\n" +
        "- 最終回答は「『〜』としてスレッド一覧に N 件表示しました」のような **短い完了報告**にする (結果の本体はアプリ側に出ているので、長い列挙は不要)。\n" +
        "- **ただし完了報告は finding の確証に基づくこと。** dispatch_task の status が partial / failed のとき、または finding に「アプリへ表示した確証」(例: 『open_thread_list_in_app で N 件表示』『N 件をタブに開いた』) が無いときは、**『表示しました』と書いてはいけない**。" +
        "見つからなかった / 開けなかった事実を正直に伝え、可能なら別の板・別表記 (正式名称 / 略称 / 作者 / キャラ名) で再 dispatch する。憶測で完了を報告しないこと。\n" +
        "- 純粋な質問・要約 (例: 「このスレ要約して」「何が話題?」) は従来どおりテキストで答えてよい。出力先はユーザの意図で選ぶ。\n" +
        "\n" +
        "# タスクの予算 (max_tool_calls) の見積もり ★重要\n" +
        "Worker はここで指定したツール回数で必ず打ち切られる (= 足りないと partial で途中終了する)。作業量に合わせて指定すること。既定は 36。\n" +
        "- 単一スレの読解・要約: 12〜24。\n" +
        "- 単一板でのスレ検索: 18〜30。\n" +
        "- **板をまたぐ横断 / テーマ検索**: 板ごとに list_threads + 内容確認 + open が要るので、目安は **「板数 × 6 + 余裕9」**。例: 5 板を横断するなら max_tool_calls=39、scan_breadth=many を指定。\n" +
        "- web_search / web_fetch で地ならしする場合はその回数 (1〜3) も上乗せして見積もること。\n" +
        "- 足りないと予算切れで途中終了するので、横断時は大きめに取ること (上限は 256)。\n" +
        "- 合計が 72 以上 / 全板 (all_boards) になると heavy 判定 → ask_user 確認が入る (妥当)。広すぎる場合は create_plan で板ごとに分割してもよい。\n" +
        "\n" +
        "# 背景\n" + contextPreamble;
}
