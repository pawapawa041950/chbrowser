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
        Streamer.Begin();
    }

    void IAgentHost.StreamWork(string deltaMd)
    {
        Streamer.WorkText(deltaMd);
        HostFlush(false);
    }

    int IAgentHost.WorkCheckpoint() => Streamer.WorkCheckpoint();

    void IAgentHost.RollbackWork(int checkpoint) => Streamer.RollbackWork(checkpoint);

    IWorkSection IAgentHost.BeginWorkSection(string title)
    {
        var id = "s" + (++_hostSectionCounter);
        Streamer.BeginSection(id, title);
        return new HostWorkSection(this, id);
    }

    void IAgentHost.PlanUpdated(PlanView plan)
    {
        var items = new List<(string, bool)>(plan.Items.Count);
        var done = 0;
        foreach (var i in plan.Items) { if (i.Completed) done++; items.Add((i.Goal, i.Completed)); }
        Streamer.Plan(items);
        Streamer.Summary($"計画 {done}/{plan.Items.Count}");
    }

    void IAgentHost.StreamBody(string deltaMd)
    {
        Streamer.BodyText(deltaMd);
        HostFlush(false);
    }

    void IAgentHost.Status(string text)
    {
        StatusMessage = text ?? "";
        if (!string.IsNullOrEmpty(text)) Streamer.Summary(text);
    }

    void IAgentHost.Notice(string text)
    {
        HostFlush(true);          // 保留中の本文を先に確定してから注記を追記
        Streamer.Notice(text);
    }

    void IAgentHost.Error(string text)
    {
        Streamer.Error(text);     // FlushAll + error イベント (JS が現バブル確定 + エラーバブル追加)
        StatusMessage = "";
    }

    void IAgentHost.End()
    {
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
            _vm.Streamer.SectionText(_id, deltaMd);
            _vm.HostFlush(false);
        }

        public void ToolMarker(string label, bool failed)
        {
            _vm.Streamer.SectionTool(_id, label, failed);
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
            _vm.Streamer.SectionComplete(_id, s, finding);
        }
    }
}
