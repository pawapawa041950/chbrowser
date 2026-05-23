namespace ChBrowser.Services.Agent;

/// <summary>plan チェックリスト表示用のスナップショット。doc/ai-agent-design.md §4.9 (D14)。</summary>
public sealed record PlanView(IReadOnlyList<PlanItemView> Items);

public sealed record PlanItemView(string Id, string Goal, bool Completed);

/// <summary>新エンジンの UI 出力契約。doc §5.2 / §4.9 (D4 / D14 / D15)。
///
/// <para>「本文 (Strategist の語り = 表向き) / 作業ログ (Worker = 折りたたみ区画)」の二系統に分け、
/// 低レベルな displayBuffer・最終回答境界の機構を host 側に閉じ込める。エンジンは「何を表に出すか」だけ考える。
/// 最初の <see cref="StreamBody"/> 呼び出しで work↔body 境界を確定する (= 旧 speculative boundary は廃止)。</para></summary>
public interface IAgentHost
{
    /// <summary>バブル開始。</summary>
    void Begin();

    /// <summary>Strategist の plan レベル語り (区画外・折りたたみ作業エリア)。</summary>
    void StreamWork(string deltaMd);

    /// <summary>現在の作業エリアの位置を返す (= rollback 用チェックポイント)。
    /// ストリーミング中のラウンドが「最終回答」と判明したとき、作業エリアに流した分を巻き戻して
    /// 本文へ移すために使う (= tool 呼び出しか最終回答かはラウンド完了まで確定しないため)。</summary>
    int WorkCheckpoint();

    /// <summary><see cref="WorkCheckpoint"/> で得た位置まで作業エリアを巻き戻す。</summary>
    void RollbackWork(int checkpoint);

    /// <summary>dispatch_task 1 件 = 折りたたみ区画を開く (並列で複数可)。</summary>
    IWorkSection BeginWorkSection(string title);

    /// <summary>plan チェックリストの作成 / 更新。</summary>
    void PlanUpdated(PlanView plan);

    /// <summary>最終回答 / ask_user の可視本文 (初回呼出で境界確定)。</summary>
    void StreamBody(string deltaMd);

    /// <summary>1 行ステータス (例: <c>[計画 2/5] …</c>)。</summary>
    void Status(string text);

    /// <summary>非致命の注記 (中断 / 一部失敗) — 黄系。</summary>
    void Notice(string text);

    /// <summary>致命エラー (Strategist LLM 失敗 / 設定エラー) — 赤・ターン中断。doc §4.9 (D15)。</summary>
    void Error(string text);

    /// <summary>バブル確定。</summary>
    void End();
}

/// <summary>dispatch_task 1 件に対応する折りたたみ区画。Worker は自分の区画にだけ書く。doc §5.2。</summary>
public interface IWorkSection
{
    /// <summary>Worker の思考 / 語り (区画内)。</summary>
    void Stream(string deltaMd);

    /// <summary>ツール呼び出しマーカー (<paramref name="failed"/>=回復した tool エラー)。</summary>
    void ToolMarker(string label, bool failed);

    /// <summary>区画の完了。done/partial/failed + finding を見出しへ。</summary>
    void Complete(TaskOutcome status, string finding);
}
