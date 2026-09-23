using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ChBrowser.Services.Reddit;

/// <summary>reddit の <c>body_html</c> / <c>selftext_html</c> (サーバが Markdown から描画した HTML) を、本アプリの本文方言
/// (改行 + <c>&lt;a href&gt;</c> / <c>&lt;b&gt;</c> / <c>&lt;hr&gt;</c> だけ。5ch dat と同じ) に落とす (<c>doc/reddit-design.md</c> §2.2、決定 D25)。
/// Markdown を自前で解釈せず、reddit の描画結果に合わせる。純粋関数。
///
/// <list type="bullet">
/// <item><description>段落は空行区切り、<c>br</c> は改行、引用は各行頭に <c>&gt; </c>、箇条書きは <c>・</c> / <c>1.</c>、表は <c>| a | b |</c>。</description></item>
/// <item><description>強調は <c>&lt;b&gt;</c>、斜体・取り消し・コード・上付きは記号 (<c>*</c> <c>~</c> <c>`</c> <c>^</c>) で代替 (スレ表示のタグ白名単を広げない)。</description></item>
/// <item><description>リンクは表示文字列と URL が同じならプレーン URL (= 画像・動画の自動展開に乗る)、違えば <c>&lt;a href&gt;</c>。
///   相対 URL (<c>/r/x</c>、<c>/u/x</c>) は <c>https://www.reddit.com</c> を前置。</description></item>
/// <item><description>スポイラーは <c>[spoiler: …]</c>。本文中の <c>&lt;</c> は (スレ表示がタグと誤認して消さないよう) 全角の <c>＜</c> にする。</description></item>
/// </list></summary>
public static class RedditBodyConverter
{
    private const string Origin = "https://www.reddit.com";
    private static readonly Regex TokenRe = new(@"<(?<close>/)?(?<name>[a-zA-Z][a-zA-Z0-9]*)(?<attrs>[^>]*?)(?<self>/)?>|<!--[\s\S]*?-->|(?<text>[^<]+|<)",
                                                 RegexOptions.Compiled);
    private static readonly Regex AttrRe = new(@"(?<k>[a-zA-Z-]+)\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')", RegexOptions.Compiled);

    public static string Convert(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var w = new Writer();
        var lists = new Stack<(bool Ordered, int Counter)>();
        var spans = new Stack<bool>();                 // true = スポイラー
        string? linkHref = null;
        StringBuilder? linkText = null;
        var inPre = 0;
        var inHeading = false;

        foreach (Match m in TokenRe.Matches(html))
        {
            if (m.Groups["text"].Success)
            {
                var raw = WebUtility.HtmlDecode(m.Groups["text"].Value);
                if (inPre == 0)
                {
                    raw = raw.Replace("\r", "").Replace("\n", " ");
                    if (w.AtLineStart || w.EndsWithSpaceOrBar)
                    {
                        raw = raw.TrimStart();                                   // 行頭 / 空白・表の区切りの直後の空白は捨てる
                        if (raw.Length == 0) continue;
                    }
                }
                raw = raw.Replace("<", "＜");
                if (linkText is not null) { linkText.Append(raw); continue; }
                if (inPre > 0) w.AppendPre(raw); else w.Append(raw);
                continue;
            }
            if (!m.Groups["name"].Success) continue;           // コメント
            var name  = m.Groups["name"].Value.ToLowerInvariant();
            var close = m.Groups["close"].Success;
            var attrs = m.Groups["attrs"].Value;

            switch (name)
            {
                case "p":
                case "div":
                    if (!close) w.Paragraph(); else w.EndLine();
                    break;
                case "br":
                    w.NewLine();
                    break;
                case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                    if (!close) { w.Paragraph(); w.Append("<b>"); inHeading = true; }
                    else if (inHeading) { w.Append("</b>"); w.EndLine(); inHeading = false; }
                    break;
                case "strong":
                case "b":
                    if (!inHeading) Emit(close ? "</b>" : "<b>");
                    break;
                case "em":
                case "i":
                    Emit("*");
                    break;
                case "del":
                case "s":
                case "strike":
                    Emit("~");
                    break;
                case "code":
                    if (inPre == 0) Emit("`");
                    break;
                case "sup":
                    if (!close) Emit("^");
                    break;
                case "span":
                    if (!close)
                    {
                        var spoiler = Attr(attrs, "class")?.Contains("md-spoiler-text", StringComparison.Ordinal) == true;
                        spans.Push(spoiler);
                        if (spoiler) Emit("[spoiler: ");
                    }
                    else if (spans.Count > 0 && spans.Pop()) Emit("]");
                    break;
                case "blockquote":
                    if (!close) { w.Paragraph(); w.QuoteDepth++; }
                    else { w.EndLine(); w.QuoteDepth = Math.Max(0, w.QuoteDepth - 1); w.BlankPending = true; }
                    break;
                case "ul":
                case "ol":
                    if (!close) { if (lists.Count == 0) w.Paragraph(); lists.Push((name == "ol", 0)); }
                    else { if (lists.Count > 0) lists.Pop(); w.EndLine(); if (lists.Count == 0) w.BlankPending = true; }
                    break;
                case "li":
                    if (!close)
                    {
                        w.NewLineIfNeeded();
                        var indent = new string(' ', Math.Max(0, lists.Count - 1) * 2);
                        if (lists.Count > 0)
                        {
                            var (ordered, counter) = lists.Pop();
                            counter++;
                            lists.Push((ordered, counter));
                            w.Append(indent + (ordered ? counter + ". " : "・ "));
                        }
                        else w.Append("・ ");
                    }
                    else w.EndLine();
                    break;
                case "pre":
                    if (!close) { w.Paragraph(); inPre++; }
                    else { inPre = Math.Max(0, inPre - 1); w.EndLine(); w.BlankPending = true; }
                    break;
                case "hr":
                    w.NewLineIfNeeded();
                    w.Append("<hr>");
                    w.EndLine();
                    break;
                case "table":
                    if (!close) w.Paragraph(); else { w.EndLine(); w.BlankPending = true; }
                    break;
                case "tr":
                    if (!close) { w.NewLineIfNeeded(); w.Append("|"); } else w.EndLine();
                    break;
                case "td":
                case "th":
                    if (!close) w.Append(" "); else w.Append(" |");
                    break;
                case "a":
                    if (!close)
                    {
                        linkHref = Absolutize(WebUtility.HtmlDecode(Attr(attrs, "href") ?? ""));
                        linkText = new StringBuilder();
                    }
                    else if (linkText is not null)
                    {
                        EmitLink(w, linkHref ?? "", linkText.ToString().Trim());
                        linkHref = null;
                        linkText = null;
                    }
                    break;
                case "img":
                    // 画像の埋め込み (アンカーの中なら href 側で出るので無視)
                    if (linkText is null && Attr(attrs, "src") is { Length: > 0 } src)
                    {
                        w.NewLineIfNeeded();
                        w.Append(Absolutize(WebUtility.HtmlDecode(src)));
                        w.EndLine();
                    }
                    break;
            }
        }
        if (linkText is not null) EmitLink(w, linkHref ?? "", linkText.ToString().Trim());
        return w.ToString();

        void Emit(string s)
        {
            if (linkText is not null) return;      // リンク文字列の中の装飾は落とす (<a> の中身はプレーンに)
            w.Append(s);
        }
    }

    private static void EmitLink(Writer w, string href, string text)
    {
        if (string.IsNullOrEmpty(href)) { w.Append(text); return; }
        if (text.Length == 0 || SameUrl(text, href)) { w.AppendUrl(href); return; }
        w.Append("<a href=\"" + href.Replace("\"", "%22") + "\">" + text + "</a>");
    }

    private static bool SameUrl(string text, string href)
    {
        static string N(string s) => Regex.Replace(s.Trim(), @"^https?://(www\.)?", "", RegexOptions.IgnoreCase).TrimEnd('/');
        return string.Equals(N(text), N(href), StringComparison.OrdinalIgnoreCase);
    }

    private static string Absolutize(string href)
        => href.StartsWith("/", StringComparison.Ordinal) && !href.StartsWith("//", StringComparison.Ordinal) ? Origin + href
         : href.StartsWith("//", StringComparison.Ordinal) ? "https:" + href
         : href;

    private static string? Attr(string attrs, string key)
    {
        foreach (Match m in AttrRe.Matches(attrs))
            if (string.Equals(m.Groups["k"].Value, key, StringComparison.OrdinalIgnoreCase)) return m.Groups["v"].Value;
        return null;
    }

    /// <summary>行単位で組み立てる。引用の深さは「行を始めたときの深さ」で行頭に <c>&gt; </c> を付ける。</summary>
    private sealed class Writer
    {
        private readonly StringBuilder _sb = new();
        private bool _lineOpen;
        public int  QuoteDepth;
        public bool BlankPending;

        public bool AtLineStart => !_lineOpen;

        /// <summary>今の行が空白か表の区切り (<c>|</c>) で終わっている (= 続く空白は不要)。</summary>
        public bool EndsWithSpaceOrBar => _lineOpen && _sb.Length > 0 && (_sb[^1] == ' ' || _sb[^1] == '|');

        private bool _softSpace;

        public void Append(string s)
        {
            if (s.Length == 0) return;
            if (!_lineOpen) StartLine();
            else if (_softSpace && !char.IsWhiteSpace(s[0])) _sb.Append(' ');
            _softSpace = false;
            _sb.Append(s);
        }

        /// <summary>プレーン URL を出す。前後の文字とくっつくと URL として認識されないので、必要なときだけ空白で区切る。</summary>
        public void AppendUrl(string url)
        {
            if (_lineOpen && _sb.Length > 0 && !char.IsWhiteSpace(_sb[^1])) _sb.Append(' ');
            Append(url);
            _softSpace = true;
        }

        /// <summary>整形済み (pre) の文字列: 改行をそのまま行区切りにする。</summary>
        public void AppendPre(string s)
        {
            var parts = s.Replace("\r", "").Split('\n');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0) NewLine();
                if (parts[i].Length > 0) Append(parts[i]);
            }
        }

        private void StartLine()
        {
            if (BlankPending && _sb.Length > 0) { _sb.Append('\n'); }
            BlankPending = false;
            if (_sb.Length > 0 && _sb[^1] != '\n') _sb.Append('\n');
            for (var i = 0; i < QuoteDepth; i++) _sb.Append("> ");
            _lineOpen  = true;
            _softSpace = false;
        }

        /// <summary>段落の開始: 前に何か書いてあれば空行を 1 つ挟む。</summary>
        public void Paragraph()
        {
            EndLine();
            if (_sb.Length > 0) BlankPending = true;
        }

        public void EndLine()
        {
            if (_lineOpen) { TrimLineEnd(); _sb.Append('\n'); _lineOpen = false; }
        }

        public void NewLine()
        {
            if (!_lineOpen) StartLine();
            TrimLineEnd();
            _sb.Append('\n');
            _lineOpen = false;
            // 改行直後の行もすぐ引用記号を付けるため、次の Append で StartLine が走る
        }

        public void NewLineIfNeeded() => EndLine();

        private void TrimLineEnd()
        {
            while (_sb.Length > 0 && _sb[^1] == ' ') _sb.Length--;
        }

        public override string ToString()
        {
            var s = _sb.ToString().Replace("\r", "");
            s = Regex.Replace(s, @"[ \t]+\n", "\n");
            s = Regex.Replace(s, @"\n{3,}", "\n\n");
            return s.Trim('\n', ' ');
        }
    }
}
