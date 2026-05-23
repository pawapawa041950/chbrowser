using System;
using System.Collections.Generic;
using System.Text;
using ChBrowser.Services.Agent;
using ChBrowser.Services.Llm;

namespace ChBrowser.ViewModels;

/// <summary>新エージェント (NewAgentEngine) の UI 出力先 (<see cref="IAgentHost"/>) 実装。
/// doc/ai-agent-design.md §5.2 / §4.9 (D4 / D14 / D15)。
///
/// <para>既存の表示イベント (<see cref="AssistantMessageStarted"/> / <see cref="AssistantHtmlUpdated"/> /
/// <see cref="AssistantMessageFinished"/> / <see cref="ErrorAdded"/>) と <see cref="MarkdownRenderer"/> を
/// そのまま再利用する (= ai-chat.html / AiChatWindow は無改造)。</para>
///
/// <para><b>区画分離 (B5)</b>: 作業エリアは「Strategist の plan レベル語り (<see cref="_hostWork"/>)」と
/// 「dispatch_task ごとの区画 (<see cref="_sections"/>)」に分かれる。並列実行時に複数 Worker が同時に
/// ストリームしてもそれぞれ自分の区画バッファに書くのでログが混ざらない。
/// 実行は UI スレッド上の async 並行 (= 複数 LLM リクエストを同時 in-flight にするがスレッドは増やさない) なので、
/// バッファアクセスはすべて UI スレッドで直列化される (= ロック不要)。</para></summary>
public sealed partial class AiChatViewModel : IAgentHost
{
    /// <summary>dispatch_task 1 件 = 1 区画のバッファ。</summary>
    private sealed class SectionBuf
    {
        public string        Title   = "";
        public readonly StringBuilder Body = new();
        public bool          Done;
        public TaskOutcome   Status;
        public string        Finding = "";
    }

    private readonly StringBuilder    _hostWork = new();   // Strategist の plan レベル語り (区画外・境界の前)
    private readonly List<SectionBuf> _sections = new();   // dispatch_task ごとの区画 (作成順)
    private readonly StringBuilder    _hostBody = new();   // 可視本文 (最終回答 / ask_user)
    private bool   _hostBoundarySet;
    private bool   _hostBubbleStarted;
    private string _hostSummary = "作業中…";
    private long   _hostLastRenderTick;

    void IAgentHost.Begin()
    {
        _hostWork.Clear();
        _sections.Clear();
        _hostBody.Clear();
        _hostBoundarySet    = false;
        _hostSummary        = "作業中…";
        _hostBubbleStarted  = true;
        _hostLastRenderTick = 0;
        AssistantMessageStarted?.Invoke();
    }

    void IAgentHost.StreamWork(string deltaMd)
    {
        _hostWork.Append(deltaMd);
        HostRender(false);
    }

    int IAgentHost.WorkCheckpoint() => _hostWork.Length;

    void IAgentHost.RollbackWork(int checkpoint)
    {
        if (checkpoint >= 0 && checkpoint <= _hostWork.Length)
            _hostWork.Length = checkpoint;
        HostRender(false);
    }

    IWorkSection IAgentHost.BeginWorkSection(string title)
    {
        var buf = new SectionBuf { Title = title };
        _sections.Add(buf);
        HostRender(false);
        return new HostWorkSection(this, buf);
    }

    void IAgentHost.PlanUpdated(PlanView plan)
    {
        var done = 0;
        foreach (var i in plan.Items) if (i.Completed) done++;
        _hostSummary = $"計画 {done}/{plan.Items.Count}";
        _hostWork.Append("\n\n<tool-call>計画: ")
                 .Append(done).Append('/').Append(plan.Items.Count).Append(" タスク</tool-call>\n\n");
        HostRender(false);
    }

    void IAgentHost.StreamBody(string deltaMd)
    {
        _hostBoundarySet = true;   // 最初の本文出力で work↔body 境界を確定
        _hostBody.Append(deltaMd);
        HostRender(false);
    }

    void IAgentHost.Status(string text)
    {
        if (!string.IsNullOrEmpty(text)) _hostSummary = text;
        StatusMessage = text ?? "";
    }

    void IAgentHost.Notice(string text)
    {
        _hostBoundarySet = true;
        _hostBody.Append("\n\n*").Append(text).Append("*\n\n");
        HostRender(true);
    }

    void IAgentHost.Error(string text)
    {
        HostRender(true);
        if (_hostBubbleStarted) AssistantMessageFinished?.Invoke();
        ErrorAdded?.Invoke(text);
        StatusMessage      = "";
        _hostBubbleStarted = false;
    }

    void IAgentHost.End()
    {
        HostRender(true);
        if (_hostBubbleStarted) AssistantMessageFinished?.Invoke();
        StatusMessage      = "";
        _hostBubbleStarted = false;
    }

    /// <summary>作業エリア (_hostWork + 各区画) + 本文を結合し、境界つきで HTML 化して表示更新する。
    /// throttle で連続 delta の再レンダを間引く (= 既存ループと同じ <see cref="RenderThrottleMs"/>)。</summary>
    private void HostRender(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - _hostLastRenderTick < RenderThrottleMs) return;
        _hostLastRenderTick = now;

        var work = new StringBuilder();
        work.Append(_hostWork);
        foreach (var s in _sections)
        {
            work.Append("\n\n<tool-call>▼ ").Append(EscapeHtml(s.Title)).Append("</tool-call>\n\n");
            work.Append(s.Body);
            if (s.Done)
            {
                var icon = s.Status switch
                {
                    TaskOutcome.Done    => "✓",
                    TaskOutcome.Partial => "◐",
                    _                   => "✗",
                };
                work.Append("\n\n<tool-call>").Append(icon).Append(' ').Append(EscapeHtml(s.Finding)).Append("</tool-call>\n\n");
            }
        }

        var workStr  = work.ToString();
        var combined = workStr + _hostBody.ToString();
        int? boundary = _hostBoundarySet ? workStr.Length : null;
        AssistantHtmlUpdated?.Invoke(MarkdownRenderer.ToHtml(combined, _hostSummary, boundary));
    }

    /// <summary>1 タスク = 1 区画。自分の <see cref="SectionBuf"/> にだけ書き込むので、
    /// 並列実行で複数 Worker が同時にストリームしてもログが混ざらない (B5)。</summary>
    private sealed class HostWorkSection : IWorkSection
    {
        private readonly AiChatViewModel _vm;
        private readonly SectionBuf      _buf;

        public HostWorkSection(AiChatViewModel vm, SectionBuf buf) { _vm = vm; _buf = buf; }

        public void Stream(string deltaMd)
        {
            _buf.Body.Append(deltaMd);
            _vm.HostRender(false);
        }

        public void ToolMarker(string label, bool failed)
        {
            _buf.Body.Append("\n\n<tool-call>").Append(failed ? "⚠ " : "").Append(EscapeHtml(label)).Append("</tool-call>\n\n");
            _vm.HostRender(false);
        }

        public void Complete(TaskOutcome status, string finding)
        {
            _buf.Done    = true;
            _buf.Status  = status;
            _buf.Finding = finding;
            _vm.HostRender(true);
        }
    }
}
