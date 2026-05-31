using System;
using System.Collections.Generic;
using ChBrowser.Services.Agent;
using ChBrowser.Services.Llm;

namespace ChBrowser.ViewModels;

/// <summary>新エージェント (NewAgentEngine) の UI 出力先 (<see cref="IAgentHost"/>) 実装。
/// doc/ai-agent-design.md §5.2 / §4.9 (D4 / D14 / D15)。
///
/// <para><b>イベント駆動・追記式</b>: <see cref="TranscriptStreamer"/> にセマンティックなイベントを渡し、
/// streamer がチャネル別 (work / body / sec:&lt;id&gt;) にテキストを「ブロック / think / ツール行」へ
/// 分解して差分イベント (begin/seg/trunc/section/…) を <see cref="AssistantEvent"/> 経由で WebView へ流す。
/// 確定ブロックは凍結され、末尾の伸長中ブロックだけ再変換されるので、旧来の「全 HTML 総入れ替え」による
/// クリック握り潰し / 選択喪失 / 折りたたみ喪失 / O(n²) 再変換が解消される。</para>
///
/// <para>テキスト delta は <see cref="HostFlush"/> で <see cref="RenderThrottleMs"/> 間引きして送出する。
/// 区画完了 / 中断 / エラー / 終了などの構造イベントは streamer 側で該当チャネルを確定フラッシュしてから送る。</para></summary>
public sealed partial class AiChatViewModel : IAgentHost
{
    private TranscriptStreamer? _streamer;
    private TranscriptStreamer  Streamer => _streamer ??= new TranscriptStreamer(o => AssistantEvent?.Invoke(o));

    private int  _hostSectionCounter;
    private long _hostLastRenderTick;

    // 計測 (回答欄末尾に TTFT / 推論 / 合計 / token/s / 総トークンを表示する)。
    private readonly ChBrowser.Services.Llm.AgentTurnMetrics _turnMetrics = new();
    private long _turnStartTick;
    private long _turnFirstDeltaTick;

    /// <summary>最初の delta 到達時刻を 1 度だけ記録する (= TTFT 用)。</summary>
    private void HostMarkFirstDelta()
    {
        if (_turnFirstDeltaTick == 0) _turnFirstDeltaTick = Environment.TickCount64;
    }

    /// <summary>このターンの計測値を集計して metrics イベントを送る。</summary>
    private void HostEmitMetrics()
    {
        var now      = Environment.TickCount64;
        var totalMs  = _turnStartTick > 0 ? now - _turnStartTick : 0;
        var ttftMs   = _turnFirstDeltaTick > 0 ? _turnFirstDeltaTick - _turnStartTick : totalMs;
        var reasonMs = _turnMetrics.ReasoningMs;
        var tokens   = _turnMetrics.CompletionTokens;
        var genMs    = _turnMetrics.GenMs;
        var tps      = genMs > 0 ? tokens * 1000.0 / genMs : 0.0;
        Streamer.Metrics(ttftMs / 1000.0, reasonMs / 1000.0, totalMs / 1000.0, tps, tokens);
    }

    // 作業バー (agent-work の summary) に出す進捗カウンタ。値が変わるたびに summary を更新する。
    private int _hostTaskDone;       // 完了した区画 (dispatch_task) 数
    private int _hostSectionsBegun;  // 開始した区画数
    private int _hostPlanCount;      // 直近の plan のタスク総数 (= 宣言済み)
    private int _hostToolUses;       // ツール使用回数 (= ToolMarker の発火数)

    /// <summary>作業バー文言を組み立てて更新する。<paramref name="finished"/> でラベルを「思考工程」に切替える。
    /// 総タスク数は「宣言済み plan 数」と「実際に開始した区画数」の大きい方 (= fast-path で plan が無くても出る)。</summary>
    private void HostUpdateSummary(bool finished)
    {
        var label = finished ? "思考工程" : "作業中…";
        var total = System.Math.Max(_hostSectionsBegun, _hostPlanCount);
        Streamer.Summary($"{label}　タスク {_hostTaskDone}/{total}　ツール使用回数 {_hostToolUses}");
    }

    /// <summary>text delta 送出の throttle。構造イベントや完了時は force=true で即時フラッシュ。</summary>
    private void HostFlush(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - _hostLastRenderTick < RenderThrottleMs) return;
        _hostLastRenderTick = now;
        Streamer.Flush();
    }

    void IAgentHost.Begin()
    {
        _hostSectionCounter = 0;
        _hostLastRenderTick = 0;
        _hostTaskDone = _hostSectionsBegun = _hostPlanCount = _hostToolUses = 0;
        // 計測リセット + LlmClient に集計先を設定。
        _turnMetrics.Reset();
        _turnStartTick      = Environment.TickCount64;
        _turnFirstDeltaTick = 0;
        _llmClient.ActiveMetrics = _turnMetrics;
        Streamer.Begin();
        HostUpdateSummary(false);
    }

    void IAgentHost.StreamWork(string deltaMd)
    {
        HostMarkFirstDelta();
        Streamer.WorkText(deltaMd);
        HostFlush(false);
    }

    int IAgentHost.WorkCheckpoint() => Streamer.WorkCheckpoint();

    void IAgentHost.RollbackWork(int checkpoint) => Streamer.RollbackWork(checkpoint);

    IWorkSection IAgentHost.BeginWorkSection(string title)
    {
        var id = "s" + (++_hostSectionCounter);
        _hostSectionsBegun++;
        Streamer.BeginSection(id, title);
        HostUpdateSummary(false);
        return new HostWorkSection(this, id);
    }

    void IAgentHost.PlanUpdated(PlanView plan)
    {
        var items = new List<(string, bool)>(plan.Items.Count);
        foreach (var i in plan.Items) items.Add((i.Goal, i.Completed));
        Streamer.Plan(items);
        _hostPlanCount = plan.Items.Count;
        HostUpdateSummary(false);
    }

    void IAgentHost.StreamBody(string deltaMd)
    {
        HostMarkFirstDelta();
        Streamer.BodyText(deltaMd);
        HostFlush(false);
    }

    void IAgentHost.Status(string text)
    {
        // 1 行ステータスは WPF ステータスバーへ。作業バー文言は進捗カウンタ (HostUpdateSummary) が担う。
        StatusMessage = text ?? "";
    }

    void IAgentHost.Notice(string text)
    {
        HostFlush(true);          // 保留中の本文を先に確定してから注記を追記
        Streamer.Notice(text);
    }

    void IAgentHost.Error(string text)
    {
        HostUpdateSummary(true);  // ラベルを「思考工程」に
        HostEmitMetrics();
        Streamer.Error(text);     // FlushAll + error イベント (JS が現バブル確定 + エラーバブル追加)
        StatusMessage = "";
    }

    void IAgentHost.End()
    {
        HostUpdateSummary(true);  // ラベルを「思考工程」に
        HostEmitMetrics();
        Streamer.End();           // FlushAll + end イベント
        StatusMessage = "";
    }

    /// <summary>1 タスク = 1 区画。自分の section id にだけ書き込むので、並列実行で複数 Worker が
    /// 同時にストリームしてもログが混ざらない (B5)。</summary>
    private sealed class HostWorkSection : IWorkSection
    {
        private readonly AiChatViewModel _vm;
        private readonly string          _id;

        public HostWorkSection(AiChatViewModel vm, string id) { _vm = vm; _id = id; }

        public void Stream(string deltaMd)
        {
            _vm.HostMarkFirstDelta();
            _vm.Streamer.SectionText(_id, deltaMd);
            _vm.HostFlush(false);
        }

        public void ToolMarker(string label, bool failed)
        {
            _vm._hostToolUses++;
            _vm.Streamer.SectionTool(_id, label, failed);
            _vm.HostUpdateSummary(false);
            _vm.HostFlush(false);
        }

        public void Complete(TaskOutcome status, string finding)
        {
            var s = status switch
            {
                TaskOutcome.Done    => "done",
                TaskOutcome.Partial => "partial",
                _                   => "failed",
            };
            _vm._hostTaskDone++;
            _vm.Streamer.SectionComplete(_id, s, finding);
            _vm.HostUpdateSummary(false);
        }
    }
}
