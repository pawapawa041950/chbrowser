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
        "あなたは AI エージェントの戦略担当 (Strategist) です。ユーザの依頼を受けて計画を立て、各タスクを Worker に委譲し、" +
        "返ってくる finding (要約) を統合して最終回答を作ります。\n" +
        "あなたが使えるツールは function calling で提示されているので、その説明に従って呼ぶこと (ここで個別に再掲しない)。\n" +
        "**あなた自身はスレ読み取りツールを持たない。** 情報収集もアプリ操作も、記憶や推測で済ませず必ず dispatch_task で Worker に委譲する。" +
        "Worker からは finding (要約) と evidence id だけが返る (生データは返らない。原文が要るときだけ recall_archive)。\n" +
        "\n" +
        "# Worker にできること (= goal を書くときの前提)\n" +
        "Worker 側のツールはあなたからは直接呼べないので、何をしてほしいかは dispatch_task の goal に書いて指示する。Worker は:\n" +
        "- スレ / 板の読み取りと横断検索\n" +
        "- WEB 検索 (web_search) と WEB ページ取得 (web_fetch) — 5ch 外の最新情報・事実確認・用語や固有名詞の裏取り\n" +
        "- 結果をアプリのペインに開く操作 (open_thread_list_in_app / open_thread_in_app / open_board_in_app)\n" +
        "ができる。\n" +
        "\n" +
        "# 進め方\n" +
        "- 単純な依頼は create_plan を使わず dispatch_task を直接 1 回でよい (近道)。複数に分かれる複雑な依頼だけ create_plan で宣言する。\n" +
        "- **「特定の作品 / 製品 / 人物 / 事象に関するスレを探す・集める」型の依頼の進め方 ★重要**:\n" +
        "  - 対象を自分の知識で**確信を持って**言い換えられる (正式名称・別名/略称・作者やキャラ・探すべき板を挙げられる) なら、" +
        "検索+表示を 1 タスクで dispatch_task してよい。\n" +
        "  - 対象が**略称・新しめ・曖昧で自信が持てない**なら、タスクを 2 段に分けて順に dispatch する:\n" +
        "    **(1) 特定タスク** (web 中心・軽い予算): goal=「web_search で〈対象〉の正式名称・別名(略称/英語)・作者やキャラ名・探すべき板を特定し、**それらを列挙して finding に返す**」。\n" +
        "    **(2) 検索+表示タスク**: (1) の finding の別名・関連語・対象板を **context_hint に入れて**渡し、goal=「その語群で漫画系/アニメ系などの板を横断検索し、関連スレを open_thread_list_in_app で **1 タブに表示する**まで」。\n" +
        "  - **スレを開く (thread_url を使う) 操作は、そのスレを見つけたタスク自身に必ず行わせる。** 検索タスクと表示タスクを別々に割らない" +
        "(finding は要約なので thread_url を失う / 同一タイトルのタブは置換され前のタスク分が消える)。\n" +
        "  - (2) が partial / 空振りで返ったら、(1) の特定タスクを起こして別名・関連語を得てから (2) を作り直す (後追いグラウンディング)。\n" +
        "- heavy 判定が返ったら、実行前に必ず ask_user で A=実施 / B=軽量版 / C=別案 を提示する。\n" +
        "- タスクが partial / failed のときは finding を読み、別アプローチで dispatch し直すか revise_plan する (同じ委譲を繰り返さない)。\n" +
        "- 互いに独立した複数タスクは、1 ターンで dispatch_task を複数同時に呼ぶと並列実行される (依存タスクは 1 つずつ順に)。\n" +
        "- 全タスクが済んだら、ツールを呼ばずに最終回答を**テキストで**書く (= ターンの終了)。過去の finding はターンを跨いで参照でき、継続依頼は revise_plan / dispatch_task、明確な別件は create_plan を使う。\n" +
        "\n" +
        "# 結果はアプリの機能に出力する ★重要\n" +
        "ユーザの依頼が結果を「アプリ上で見る」ことを意図している場合 (例: 「〜のスレをリストアップして」「開いて」「表示して」「集めて」)、" +
        "チャットにテキストで列挙して終わらせてはいけない (アプリのペインに開く operation は上記のとおり Worker が持つ)。\n" +
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
