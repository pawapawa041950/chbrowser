using System;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;

namespace ChBrowser.Services.Llm;

/// <summary>
/// LLM のストリーミング応答 (markdown と想定) を HTML に変換する。
/// AI チャットウィンドウの WebView2 シェル (ai-chat.html) はこの HTML を innerHTML に流し込むだけなので、
/// 安全性のため <see cref="MarkdownPipelineBuilder.DisableHtml"/> で raw HTML をリテラル文字列扱いにする
/// (= LLM 出力に &lt;script&gt; 等が混ざってもコードとして実行されない)。
///
/// <para><b>ブロックマーカー</b>: 通常の markdown に加えて以下 2 種類の独自タグを境界として扱い、特別な
/// 見た目で囲む。マーカー外側 / 内側はそれぞれ独立に再帰レンダされるので、入れ子になっても (= AI が
/// &lt;think&gt; の途中で tool 呼び出しを挟むなど) 正しく描画される。</para>
/// <list type="bullet">
///   <item><c>&lt;think&gt;...&lt;/think&gt;</c> — 推論モデルの思考過程。&lt;details&gt; で折りたたみ可能なブロック。
///         LLM が直接 inline で出力するパターンと、LlmClient が <c>reasoning_content</c> を合成して挿入する
///         パターンの両方をここで吸収する。</item>
///   <item><c>&lt;tool-call&gt;...&lt;/tool-call&gt;</c> — エージェントループでツールを呼んだことを示す
///         UI 装飾マーカー。AiChatViewModel が round 境界で displayBuffer に差し込む。中身は既にエスケープ済の
///         プレーンテキスト前提なので、markdown には通さない。</item>
/// </list>
/// 閉じタグだけ来た (= ストリーミング途中で順序が崩れた) 場合は無音で読み飛ばす。
/// 開きタグだけ来た (= 閉じ忘れ) 場合は末尾まで内容として扱う。
/// </summary>
internal static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras()   // ~~打ち消し線~~ など
        .UsePipeTables()       // | 表 |
        .UseAutoLinks()        // 裸の URL を自動リンク
        .DisableHtml()         // raw HTML はリテラル文字列として出力 (= XSS 防止)
        .Build();

    private const string ThinkOpen    = "<think>";
    private const string ThinkClose   = "</think>";
    private const string ToolOpen     = "<tool-call>";
    private const string ToolClose    = "</tool-call>";

    /// <summary>markdown 文字列を HTML に変換する。null は空文字として扱う。
    /// <c>&lt;think&gt;</c> / <c>&lt;tool-call&gt;</c> ブロックは個別スタイルで囲む。
    /// harmony 系トークン (gpt-oss / 一部の Gemma chat template が流す <c>&lt;|channel|&gt;analysis&lt;|message|&gt;</c> 等)
    /// が混ざっていれば、最初に <c>&lt;think&gt;</c> / <c>&lt;/think&gt;</c> に正規化してから通常の処理に進む。
    ///
    /// <para><b>Agent work 折りたたみ</b>:
    /// <paramref name="agentSummary"/> が非 null なら、出力全体を <c>&lt;details class="agent-work"&gt;</c> で
    /// 囲み、デフォルトで折りたたんだ 1 行に「現在何をしているか」のサマリを出す。
    /// <paramref name="agentFinalBoundary"/> が与えられた場合は、その index で markdown を 2 分割し、
    /// 前半 (= 思考過程やツール呼び出し) を折りたたみ、後半 (= 最終回答本文) を通常表示する。</para></summary>
    public static string ToHtml(string? markdown, string? agentSummary = null, int? agentFinalBoundary = null)
    {
        var s = markdown ?? "";
        if (s.Length == 0) return "";

        // agent モード (= AI チャット): summary が渡ったら折りたたみで包む。
        if (agentSummary is not null)
        {
            if (agentFinalBoundary is int b && b >= 0 && b <= s.Length)
            {
                // 境界で分割: 前半 (agent work) を折りたたみ、後半 (最終回答) はそのまま。
                // 各セグメントを個別に normalize して RenderFlat に通す。
                // 最終回答エリアからは <think>...</think> を除去する (= 折りたたみバーの外で思考過程が
                // ダラダラ展開されないようにする。思考過程は archive / history に残るので、必要なら
                // recall_archive で確認可)。streaming 途中の閉じ忘れ <think> も末尾までを暫定除去。
                var preRaw  = s.Substring(0, b);
                var postRaw = StripThinkBlocksForFinalArea(s.Substring(b));
                var preNorm  = NormalizeHarmonyMarkers(preRaw);
                var postNorm = NormalizeHarmonyMarkers(postRaw);

                var sb = new StringBuilder();
                sb.Append("<details class=\"agent-work\"><summary>");
                sb.Append(EscapeHtml(agentSummary));
                sb.Append("</summary><div class=\"agent-work-body\">");
                sb.Append(RenderFlat(preNorm));
                sb.Append("</div></details>");
                if (postNorm.Length > 0) sb.Append(RenderFlat(postNorm));
                return sb.ToString();
            }
            else
            {
                // 境界未確定 (= まだ最終回答に至っていない): 全体を折りたたみ。
                var sn = NormalizeHarmonyMarkers(s);
                if (sn.Trim().Length == 0)
                {
                    // 中身がまだ無いなら、サマリだけ表示する 1 行を出す (= ストリーミング初期段階の見た目)。
                    return "<details class=\"agent-work\"><summary>" + EscapeHtml(agentSummary) + "</summary></details>";
                }
                var sb = new StringBuilder();
                sb.Append("<details class=\"agent-work\"><summary>");
                sb.Append(EscapeHtml(agentSummary));
                sb.Append("</summary><div class=\"agent-work-body\">");
                sb.Append(RenderFlat(sn));
                sb.Append("</div></details>");
                return sb.ToString();
            }
        }

        // 非 agent モード: 従来通り。
        s = NormalizeHarmonyMarkers(s);
        return RenderFlat(s);
    }

    /// <summary>HTML の <c>&lt;summary&gt;</c> 等に安全に埋め込むためのエスケープ。</summary>
    private static string EscapeHtml(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
    }

    /// <summary>最終回答エリア (= agent-work 折りたたみの外側) から <c>&lt;think&gt;...&lt;/think&gt;</c>
    /// ブロックを除去する。閉じている think は通常マッチで除去、ストリーミング途中の閉じ忘れ
    /// <c>&lt;think&gt;...$</c> も末尾まで暫定除去する (= 閉じが届けば次の再レンダで通常マッチに切り替わる)。
    /// 連続改行は 1 つに圧縮しておく (= 除去後の空白を整える)。</summary>
    private static readonly Regex StripClosedThinkRe = new(@"<think>.*?</think>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex StripUnclosedThinkRe = new(@"<think>[\s\S]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CollapseBlankLinesRe = new(@"\n{3,}", RegexOptions.Compiled);
    private static string StripThinkBlocksForFinalArea(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        // ストリーミング中で <think> が空文字列 (= まだタグ自体すら来ていない) の場合は触らない。
        if (s.IndexOf("<think>", StringComparison.OrdinalIgnoreCase) < 0) return s;
        var t = StripClosedThinkRe.Replace(s, "");
        t = StripUnclosedThinkRe.Replace(t, "");
        t = CollapseBlankLinesRe.Replace(t, "\n\n");
        return t.TrimStart();
    }

    // ---- harmony 形式の正規化 ----
    //
    // 一部のモデル (gpt-oss、Gemma の harmony 系 chat template 等) は思考過程と最終回答を
    // 以下のような独自トークンで区切る:
    //   <|channel|>analysis<|message|>...<|end|>
    //   <|start|>assistant<|channel|>final<|message|>...
    // OpenAI-compatible なフロント (llama.cpp 等) がこれを reasoning_content/content に
    // 分離してくれれば LlmClient 側で <think>...</think> に変換できるが、分離せずに content
    // ストリームに素のまま流してくるケースがあり、<think> が閉じられない / 余分なトークンが
    // ユーザに見える状況になる。
    //
    // ここで:
    //   <|channel|>analysis<|message|> → <think>
    //   <|channel|>final<|message|>    → </think>
    //   <|channel|>(その他)<|message|> → 空
    //   単独の <|channel|>             → </think> (= 安全側に倒し、開いている think を閉じる)
    //   <|start|>{role} / <|end|> / <|return|> / <|message|> → 空
    // と置換し、後段の <think>/</think> ベースのレンダリングと整合させる。
    //
    // chat template によっては先頭の `|` が落ちて <channel|>final<message|> のような形で来る
    // こともあるので、両端の `|` はオプショナル扱いにする。

    private const RegexOptions HarmonyOpts = RegexOptions.IgnoreCase | RegexOptions.Compiled;
    private static readonly Regex HarmonyFinalRe    = new(@"<\|?channel\|?>\s*final\s*<\|?message\|?>",    HarmonyOpts);
    private static readonly Regex HarmonyAnalysisRe = new(@"<\|?channel\|?>\s*analysis\s*<\|?message\|?>", HarmonyOpts);
    private static readonly Regex HarmonyOtherChRe  = new(@"<\|?channel\|?>\s*\w+\s*<\|?message\|?>",      HarmonyOpts);
    private static readonly Regex HarmonyChannelOrphanRe = new(@"<\|?channel\|?>",                        HarmonyOpts);
    private static readonly Regex HarmonyStartRoleRe = new(@"<\|?start\|?>\s*(?:system|user|assistant|tool)\s*", HarmonyOpts);
    private static readonly Regex HarmonyFramingRe  = new(@"<\|?(?:start|end|return|message)\|?>",        HarmonyOpts);

    private static string NormalizeHarmonyMarkers(string s)
    {
        // 高速 early-out: harmony 系トークンの兆候 (= '|' か 'channel'/'message') が一切無ければ no-op。
        // ほぼ全モデルの大多数の応答はこのパスを通る。
        if (s.IndexOf('|') < 0 &&
            s.IndexOf("channel", StringComparison.OrdinalIgnoreCase) < 0 &&
            s.IndexOf("message", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return s;
        }
        s = HarmonyFinalRe.Replace(s,    "</think>");
        s = HarmonyAnalysisRe.Replace(s, "<think>");
        s = HarmonyOtherChRe.Replace(s,  "");
        s = HarmonyChannelOrphanRe.Replace(s, "</think>"); // 単独 channel は安全側で think 閉じ
        s = HarmonyStartRoleRe.Replace(s, "");
        s = HarmonyFramingRe.Replace(s,  "");
        return s;
    }

    /// <summary>マーカーを跨ぐ markdown を境界ごとに切ってレンダする。再帰可。
    /// 各 boundary は (think_open / think_close / tool_open / tool_close) の 4 種類。
    /// 不正なマーカー (orphan close 等) は黙って読み飛ばすので、ストリーミング中の途中状態でも壊れない。</summary>
    private static string RenderFlat(string s)
    {
        var sb = new StringBuilder();
        var i  = 0;
        while (i < s.Length)
        {
            var (kind, pos) = FindNextBoundary(s, i);
            if (kind == BoundaryKind.None)
            {
                sb.Append(Markdown.ToHtml(s.Substring(i), Pipeline));
                break;
            }

            // boundary 直前までを通常 markdown として描画。
            if (pos > i)
                sb.Append(Markdown.ToHtml(s.Substring(i, pos - i), Pipeline));

            switch (kind)
            {
                case BoundaryKind.ThinkOpen:
                {
                    var contentStart = pos + ThinkOpen.Length;
                    var closeIdx     = s.IndexOf(ThinkClose, contentStart, StringComparison.OrdinalIgnoreCase);
                    var contentEnd   = closeIdx < 0 ? s.Length : closeIdx;
                    var inner        = s.Substring(contentStart, contentEnd - contentStart);

                    sb.Append("<details class=\"think-block\" open><summary>思考過程</summary><div class=\"think-body\">");
                    // 入れ子 (= think 内に tool-call 等) もここで処理されるよう再帰呼び出し。
                    sb.Append(RenderFlat(inner));
                    sb.Append("</div></details>");

                    if (closeIdx < 0) return sb.ToString(); // 末尾までが think 内
                    i = closeIdx + ThinkClose.Length;
                    break;
                }
                case BoundaryKind.ToolOpen:
                {
                    var contentStart = pos + ToolOpen.Length;
                    var closeIdx     = s.IndexOf(ToolClose, contentStart, StringComparison.OrdinalIgnoreCase);
                    var contentEnd   = closeIdx < 0 ? s.Length : closeIdx;
                    var inner        = s.Substring(contentStart, contentEnd - contentStart);

                    // tool-call の中身は呼び出し側で既にエスケープ済 (= プレーンテキスト)。markdown には通さず
                    // そのまま流し込む (= JS / HTML 解釈もされない、エスケープ済なので安全)。
                    sb.Append("<div class=\"tool-call-line\"><span class=\"tool-call-label\">ツール</span><span class=\"tool-call-args\">")
                      .Append(inner)
                      .Append("</span></div>");

                    if (closeIdx < 0) return sb.ToString();
                    i = closeIdx + ToolClose.Length;
                    break;
                }
                case BoundaryKind.ThinkClose:
                    // 対応する open が無い orphan: 表示せず黙ってスキップ。
                    i = pos + ThinkClose.Length;
                    break;
                case BoundaryKind.ToolClose:
                    i = pos + ToolClose.Length;
                    break;
            }
        }
        return sb.ToString();
    }

    private enum BoundaryKind { None, ThinkOpen, ThinkClose, ToolOpen, ToolClose }

    /// <summary><paramref name="start"/> 以降で最も近いマーカー境界を返す。何もなければ None。</summary>
    private static (BoundaryKind Kind, int Pos) FindNextBoundary(string s, int start)
    {
        var thinkOpen  = s.IndexOf(ThinkOpen,  start, StringComparison.OrdinalIgnoreCase);
        var thinkClose = s.IndexOf(ThinkClose, start, StringComparison.OrdinalIgnoreCase);
        var toolOpen   = s.IndexOf(ToolOpen,   start, StringComparison.OrdinalIgnoreCase);
        var toolClose  = s.IndexOf(ToolClose,  start, StringComparison.OrdinalIgnoreCase);

        var best     = int.MaxValue;
        var bestKind = BoundaryKind.None;
        if (thinkOpen  >= 0 && thinkOpen  < best) { best = thinkOpen;  bestKind = BoundaryKind.ThinkOpen;  }
        if (thinkClose >= 0 && thinkClose < best) { best = thinkClose; bestKind = BoundaryKind.ThinkClose; }
        if (toolOpen   >= 0 && toolOpen   < best) { best = toolOpen;   bestKind = BoundaryKind.ToolOpen;   }
        if (toolClose  >= 0 && toolClose  < best) { best = toolClose;  bestKind = BoundaryKind.ToolClose;  }

        return (bestKind, bestKind == BoundaryKind.None ? -1 : best);
    }
}
