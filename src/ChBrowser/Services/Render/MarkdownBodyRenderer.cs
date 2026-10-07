using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ChBrowser.Models;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace ChBrowser.Services.Render;

/// <summary>本文を Markdown で書く掲示板 (今は reddit) の、Markdown の整形表示の設定。
/// 掲示板 (<see cref="ChBrowser.Services.Bbs.IBbsProvider.MarkdownBody"/>) が持ち、null の掲示板は整形表示しない。</summary>
/// <param name="LinkBase">相対リンク (<c>/r/xxx</c> 等) の前に付ける URL (例: <c>https://www.reddit.com</c>)。null なら相対リンクはリンクにしない。</param>
/// <param name="Spoilers"><c>&gt;!ネタバレ!&lt;</c> をネタバレ (クリックで見える) として扱うか (reddit の記法)。</param>
public sealed record MarkdownBodySpec(string? LinkBase, bool Spoilers);

/// <summary>レス本文の Markdown (<see cref="PostExtra.Markdown"/>) を、スレ表示に出す HTML にする (全掲示板共通)。
///
/// <list type="bullet">
/// <item><description>本文の扱いは変えない: NG・検索・AI・翻訳は今までどおり本文 (<see cref="Post.Body"/>) を使い、これは表示だけの層。
///   Markdown を持たないレス (古いログ・Markdown でない掲示板) は今までどおりの表示。</description></item>
/// <item><description>安全のため Markdown 中の生の HTML は文字として出す。リンクは http / https だけ (相対リンクは <see cref="MarkdownBodySpec.LinkBase"/> を前置)。</description></item>
/// <item><description>画像の埋め込み (<c>![](...)</c>) は出さない: 画像・動画は本文の URL から作る媒体の欄 (サムネイル) に今までどおり出る。</description></item>
/// <item><description>描画結果はレスごとに覚えておく (スレの送り直しのたびに描き直さない)。</description></item>
/// </list></summary>
public static class MarkdownBodyRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras()   // ~~打ち消し線~~
        .UsePipeTables()       // | 表 |
        .UseAutoLinks()        // 裸の URL を自動リンク
        .DisableHtml()         // 生の HTML は文字として出す
        .Build();

    // ネタバレの目印 (私用領域の文字。Markdown の解釈を通り抜けた後で span に置き換える)
    private const char SpoilerOpen = '', SpoilerClose = '';
    private static readonly Regex SpoilerRe = new(@">!(?<t>.+?)!<", RegexOptions.Compiled);

    private static readonly ConditionalWeakTable<PostExtra, string> Cache = new();

    /// <summary>レスの Markdown の HTML (Markdown が無ければ null)。同じレスは 2 回目から覚えた結果を返す。</summary>
    public static string? HtmlFor(Post post, MarkdownBodySpec spec)
    {
        if (post.Ext is not { Markdown: { Length: > 0 } md } ext) return null;
        if (Cache.TryGetValue(ext, out var cached)) return cached;
        var html = Render(md, spec);
        Cache.AddOrUpdate(ext, html);
        return html;
    }

    private static readonly ConditionalWeakTable<string, string> TextCache = new();

    /// <summary><see cref="Render"/> の、同じ文字列 (インスタンス) の結果を覚える版 (訳した Markdown を送り直すたびに描き直さない)。</summary>
    public static string RenderCached(string markdown, MarkdownBodySpec spec)
    {
        if (TextCache.TryGetValue(markdown, out var cached)) return cached;
        var html = Render(markdown, spec);
        TextCache.AddOrUpdate(markdown, html);
        return html;
    }

    /// <summary>Markdown → スレ表示に出す HTML。</summary>
    public static string Render(string markdown, MarkdownBodySpec spec)
    {
        var src = markdown.Replace("\r\n", "\n");
        if (spec.Spoilers) src = SpoilerRe.Replace(src, m => SpoilerOpen + m.Groups["t"].Value + SpoilerClose);

        var doc = Markdown.Parse(src, Pipeline);
        foreach (var link in doc.Descendants<LinkInline>().ToList())
        {
            if (link.IsImage) { link.Remove(); continue; }          // 画像は媒体の欄に出る
            link.Url = SafeUrl(link.Url, spec);
        }
        foreach (var auto in doc.Descendants<AutolinkInline>().ToList())
            auto.Url = SafeUrl(auto.Url, spec) ?? "";

        var html = doc.ToHtml(Pipeline);
        // URL を許さなかったリンクは文字だけにする
        html = Regex.Replace(html, @"<a href=""""[^>]*>(?<t>[\s\S]*?)</a>", "${t}");
        // リンクはスレ表示の本文リンクと同じ形 (href を付けず data-url。クリックはスレ表示が data-url で開く。
        // href があるとスレ表示のページ自体がその URL へ移ってしまう)
        html = Regex.Replace(html, @"<a href=""(?<u>[^""]*)""", @"<a data-url=""${u}"" title=""${u}""");
        if (spec.Spoilers)
            html = html.Replace(SpoilerOpen.ToString(), "<span class=\"md-spoiler\" title=\"ネタバレ (クリックで表示)\">")
                       .Replace(SpoilerClose.ToString(), "</span>");
        return html.Trim();
    }

    /// <summary>Markdown 中の文字列を、記号として解釈されないようにする (Markdown を組み立てる側が、ただの文字を混ぜるときに使う)。</summary>
    public static string EscapeText(string text)
        => Regex.Replace(text ?? "", @"([\\`*_{}\[\]()#+\-.!|>~<])", @"\$1");

    private static string? SafeUrl(string? url, MarkdownBodySpec spec)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var u = url.Trim();
        if (u.StartsWith("//", StringComparison.Ordinal)) return "https:" + u;
        if (u.StartsWith("/", StringComparison.Ordinal)) return spec.LinkBase is { } b ? b.TrimEnd('/') + u : null;
        if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return u;
        if (u.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) return "https://" + u;
        return null;   // javascript: 等・掲示板独自の記法 (reddit の emote|… 等) はリンクにしない
    }
}
