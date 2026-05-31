using System;
using System.Collections.Generic;
using System.Text;

namespace ChBrowser.Services.Llm;

/// <summary>
/// AI チャットの表示を「イベント駆動・追記式」で WebView へ流すための、サーバ側セグメント化エンジン。
///
/// <para><b>背景</b>: 旧実装は毎デルタで全文を Markdig→HTML 化し <c>innerHTML</c> を総入れ替えしていた。
/// これがクリック握り潰し / 選択喪失 / 折りたたみ喪失 / チラつき / O(n²) 再変換の原因だった。
/// 本クラスは入力テキストを <b>チャネル別に「テキストブロック / 思考 (think) / ツール行」のセグメント列</b>へ
/// 分解し、<b>確定済みセグメントは一度だけ HTML 化して凍結</b>・<b>末尾の伸長中セグメントだけ再変換</b>する。
/// 差分だけを <c>seg</c> / <c>trunc</c> イベントとして送るので、JS 側はノードを作り替えず追記・末尾更新できる。</para>
///
/// <para>チャネル: <c>work</c> (Strategist 語り) / <c>body</c> (最終回答) / <c>sec:&lt;id&gt;</c> (dispatch_task 区画)。
/// section の begin/done, plan, begin/end/notice/error はチャネル外の構造イベントとして即時送出する。</para>
///
/// <para>think マーカー (<c>&lt;think&gt;…&lt;/think&gt;</c>) と harmony トークンはここで吸収する
/// (= JS はマーカーを一切解釈しない)。ツール行は <see cref="SectionTool"/> の明示呼び出しから来るので
/// マーカー文字列には依存しない。</para>
/// </summary>
public sealed class TranscriptStreamer
{
    private readonly Action<object> _send;
    private readonly Dictionary<string, Channel> _channels = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);

    public TranscriptStreamer(Action<object> send) => _send = send;

    // ---- ターン / 構造イベント (即時送出) ----

    public void Begin()
    {
        _channels.Clear();
        _dirty.Clear();
        _send(new { type = "begin" });
    }

    public void Plan(IReadOnlyList<(string Text, bool Done)> items)
    {
        var arr = new object[items.Count];
        for (var i = 0; i < items.Count; i++) arr[i] = new { text = items[i].Text, done = items[i].Done };
        _send(new { type = "plan", items = arr });
    }

    public void BeginSection(string id, string title)
    {
        Ch("sec:" + id); // チャネルを確保 (空でも存在させる)
        _send(new { type = "section", id, title });
    }

    public void SectionComplete(string id, string status, string finding)
    {
        FlushChannel("sec:" + id);              // 区画本文の保留分を先に反映
        _dirty.Remove("sec:" + id);
        _send(new { type = "sectionDone", id, status, finding });
    }

    /// <summary>作業エリア折りたたみバーの文言を更新する。</summary>
    public void Summary(string text) => _send(new { type = "summary", text });

    public void Notice(string text)  => _send(new { type = "notice", text });
    public void Error(string text)   { FlushAll(); _send(new { type = "error", text }); }
    public void End()                { FlushAll(); _send(new { type = "end" }); }

    // ---- テキスト投入 (蓄積 + dirty。実送出は Flush) ----

    public void WorkText(string delta)               { Ch("work").AppendText(delta);          _dirty.Add("work"); }
    public void BodyText(string delta)               { Ch("body").AppendText(delta);          _dirty.Add("body"); }
    public void SectionText(string id, string delta) { Ch("sec:" + id).AppendText(delta);     _dirty.Add("sec:" + id); }
    public void SectionTool(string id, string label, bool failed) { Ch("sec:" + id).AddTool(label, failed); _dirty.Add("sec:" + id); }

    /// <summary>work チャネルの現在のテキスト長 (= rollback 用チェックポイント)。</summary>
    public int WorkCheckpoint() => Ch("work").TextLength;

    /// <summary>work を指定長まで巻き戻す (= 最終回答を body へ移すときに直近ラウンドの語りを消す)。</summary>
    public void RollbackWork(int keep)
    {
        Ch("work").TruncateText(keep);
        FlushChannel("work");
        _dirty.Remove("work");
    }

    /// <summary>dirty な全チャネルの差分を送出する (AgentHost が throttle して呼ぶ)。</summary>
    public void Flush()
    {
        if (_dirty.Count == 0) return;
        // コピーして列挙 (FlushChannel が _dirty を触らない前提だが安全側)。
        var ids = new List<string>(_dirty);
        _dirty.Clear();
        foreach (var id in ids) FlushChannel(id);
    }

    private void FlushAll()
    {
        var ids = new List<string>(_dirty);
        _dirty.Clear();
        foreach (var id in ids) FlushChannel(id);
    }

    private Channel Ch(string id)
    {
        if (!_channels.TryGetValue(id, out var ch))
        {
            ch = new Channel(id);
            _channels[id] = ch;
        }
        return ch;
    }

    /// <summary>1 チャネルのセグメント列を再計算し、前回送出分との差分だけを emit する。
    /// 凍結セグメント (= 末尾以外) は内容不変なら再変換も再送もしない。末尾の伸長中セグメントのみ毎回再変換。</summary>
    private void FlushChannel(string id)
    {
        if (!_channels.TryGetValue(id, out var ch)) return;
        var segs = ch.ComputeSegments();
        var prev = ch.Emitted;

        for (var i = 0; i < segs.Count; i++)
        {
            var seg = segs[i];
            if (i < prev.Count && prev[i].SameAs(seg))
                continue; // 内容不変 (= 凍結済みで変化なし) → 何もしない

            object msg;
            if (seg.Kind == SegKind.Tool)
                msg = new { type = "seg", ch = id, idx = i, kind = "tool", label = seg.Label, failed = seg.Failed };
            else
            {
                var html = MarkdownRenderer.RenderMarkdownBlock(seg.Raw);
                msg = new { type = "seg", ch = id, idx = i, kind = seg.Kind == SegKind.Think ? "think" : "text", html };
            }
            _send(msg);

            if (i < prev.Count) prev[i] = Emitted.From(seg);
            else                prev.Add(Emitted.From(seg));
        }

        if (prev.Count > segs.Count)
        {
            _send(new { type = "trunc", ch = id, keep = segs.Count });
            prev.RemoveRange(segs.Count, prev.Count - segs.Count);
        }
    }

    // ============================ 内部型 ============================

    private enum SegKind { Text, Think, Tool }

    private readonly struct Seg
    {
        public readonly SegKind Kind;
        public readonly string  Raw;     // Text/Think の生 markdown
        public readonly string  Label;   // Tool
        public readonly bool    Failed;  // Tool
        private Seg(SegKind kind, string raw, string label, bool failed) { Kind = kind; Raw = raw; Label = label; Failed = failed; }
        public static Seg Text(string raw)  => new(SegKind.Text,  raw, "", false);
        public static Seg Think(string raw) => new(SegKind.Think, raw, "", false);
        public static Seg Tool(string label, bool failed) => new(SegKind.Tool, "", label, failed);
    }

    /// <summary>前回送出したセグメントの内容 (差分判定用)。</summary>
    private struct Emitted
    {
        public SegKind Kind;
        public string  Key;     // Text/Think は Raw、Tool は label+failed
        public static Emitted From(Seg s) => new()
        {
            Kind = s.Kind,
            Key  = s.Kind == SegKind.Tool ? (s.Failed ? "1|" : "0|") + s.Label : s.Raw,
        };
        public bool SameAs(Seg s)
        {
            if (Kind != s.Kind) return false;
            var key = s.Kind == SegKind.Tool ? (s.Failed ? "1|" : "0|") + s.Label : s.Raw;
            return string.Equals(Key, key, StringComparison.Ordinal);
        }
    }

    /// <summary>1 チャネル。テキスト部 (StringBuilder) とツール行 (label/failed) を投入順に保持する。</summary>
    private sealed class Channel
    {
        public readonly string Id;
        public readonly List<Emitted> Emitted = new();

        // 投入順の parts: テキスト塊 と ツール行 が交互/混在しうる。
        private readonly List<Part> _parts = new();

        public Channel(string id) => Id = id;

        public void AppendText(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return;
            if (_parts.Count == 0 || !_parts[^1].IsText)
                _parts.Add(Part.NewText());
            _parts[^1].Text!.Append(delta);
        }

        public void AddTool(string label, bool failed) => _parts.Add(Part.NewTool(label, failed));

        public int TextLength
        {
            get
            {
                var n = 0;
                foreach (var p in _parts) if (p.IsText) n += p.Text!.Length;
                return n;
            }
        }

        /// <summary>テキスト総長を <paramref name="keep"/> まで末尾から削る (= work rollback)。</summary>
        public void TruncateText(int keep)
        {
            if (keep < 0) keep = 0;
            var total = TextLength;
            if (keep >= total) return;
            var remove = total - keep;
            for (var i = _parts.Count - 1; i >= 0 && remove > 0; i--)
            {
                if (!_parts[i].IsText) continue;
                var sb = _parts[i].Text!;
                var cut = Math.Min(sb.Length, remove);
                sb.Length -= cut;
                remove    -= cut;
            }
        }

        /// <summary>parts をフラットなセグメント列へ展開する。
        /// テキスト部は harmony 正規化 → think 分割 → さらにブロック分割 (空行区切り・コードフェンス考慮)。</summary>
        public List<Seg> ComputeSegments()
        {
            var result = new List<Seg>();
            foreach (var p in _parts)
            {
                if (!p.IsText) { result.Add(Seg.Tool(p.Label!, p.Failed)); continue; }
                SegmentText(p.Text!.ToString(), result);
            }
            return result;
        }

        /// <summary>1 テキスト塊を think とテキストブロックに分解して <paramref name="outList"/> へ追加する。</summary>
        private static void SegmentText(string raw, List<Seg> outList)
        {
            if (string.IsNullOrEmpty(raw)) return;
            raw = MarkdownRenderer.NormalizeHarmony(raw);

            var i = 0;
            while (i < raw.Length)
            {
                var open = IndexOfCI(raw, "<think>", i);
                if (open < 0)
                {
                    AddTextBlocks(raw.Substring(i), outList);
                    break;
                }
                if (open > i) AddTextBlocks(raw.Substring(i, open - i), outList);

                var contentStart = open + "<think>".Length;
                var close = IndexOfCI(raw, "</think>", contentStart);
                var contentEnd = close < 0 ? raw.Length : close;
                var inner = raw.Substring(contentStart, contentEnd - contentStart);
                if (inner.Trim().Length > 0) outList.Add(Seg.Think(inner));
                if (close < 0) break;
                i = close + "</think>".Length;
            }
        }

        /// <summary>テキストを空行区切りのブロックに分け、各ブロックをセグメントにする。
        /// コードフェンス (``` / ~~~) 内の空行では分割しない。</summary>
        private static void AddTextBlocks(string text, List<Seg> outList)
        {
            if (string.IsNullOrEmpty(text)) return;
            var cur = new StringBuilder();
            var inFence = false;
            foreach (var line in text.Split('\n'))
            {
                var trimmedStart = line.TrimStart();
                if (trimmedStart.StartsWith("```", StringComparison.Ordinal) || trimmedStart.StartsWith("~~~", StringComparison.Ordinal))
                    inFence = !inFence;

                var blank = line.Trim().Length == 0;
                if (blank && !inFence)
                {
                    if (cur.Length > 0) { FlushBlock(cur, outList); }
                }
                else
                {
                    cur.Append(line).Append('\n');
                }
            }
            if (cur.Length > 0) FlushBlock(cur, outList);
        }

        private static void FlushBlock(StringBuilder cur, List<Seg> outList)
        {
            var block = cur.ToString();
            cur.Clear();
            if (block.Trim().Length > 0) outList.Add(Seg.Text(block));
        }

        private static int IndexOfCI(string s, string needle, int start)
            => s.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);

        private sealed class Part
        {
            public bool          IsText;
            public StringBuilder? Text;
            public string?       Label;
            public bool          Failed;
            public static Part NewText() => new() { IsText = true, Text = new StringBuilder() };
            public static Part NewTool(string label, bool failed) => new() { IsText = false, Label = label, Failed = failed };
        }
    }
}
