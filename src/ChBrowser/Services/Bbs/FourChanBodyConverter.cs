using System;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ChBrowser.Services.Bbs;

/// <summary>4chan の本文 (<c>com</c>: サーバが描画した HTML 断片) を、本アプリの本文方言 (改行 + <c>&lt;b&gt;</c> だけ。5ch dat と同じ) に落とす。
/// 純粋関数。
///
/// <list type="bullet">
/// <item><description><c>&lt;br&gt;</c> は改行、<c>&lt;wbr&gt;</c> (長い語の折り返し位置) は消す。</description></item>
/// <item><description>同じスレへの引用 <c>&lt;a href="#pN" class="quotelink"&gt;&gt;&gt;N&lt;/a&gt;</c> は <c>&gt;&gt;N</c> (= アンカー規則でアプリ内アンカーになる)。
///   別スレ・別板への引用 (<c>/g/thread/X#pY</c>、<c>//boards.4chan.org/g/catalog#s=…</c>) は絶対 URL にする (本文中のスレ URL としてアプリで開ける)。</description></item>
/// <item><description>緑字の引用 (<c>&lt;span class="quote"&gt;</c>)・削除済みの引用 (<c>deadlink</c>)・コード (<c>&lt;pre&gt;</c>) は文字だけ残す。</description></item>
/// <item><description>スポイラー (<c>&lt;s&gt;</c>) は <c>[spoiler: …]</c> (reddit と同じ)。EXIF 表 (<c>&lt;table class="exif"&gt;</c>) は捨てる。</description></item>
/// <item><description>本文中の <c>&lt;</c> は (スレ表示がタグと誤認して消さないよう) 全角の <c>＜</c> にする。</description></item>
/// </list></summary>
public static class FourChanBodyConverter
{
    private const string Origin = "https://boards.4chan.org";

    private static readonly Regex TokenRe = new(
        @"<(?<close>/)?(?<name>[a-zA-Z][a-zA-Z0-9]*)(?<attrs>[^>]*?)/?>|<!--[\s\S]*?-->|(?<text>[^<]+|<)", RegexOptions.Compiled);
    private static readonly Regex HrefRe = new(@"href\s*=\s*""(?<v>[^""]*)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <param name="html">4chan の <c>com</c>。</param>
    public static string Convert(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var sb = new StringBuilder(html.Length);
        string? linkHref = null;        // 開いている <a> の href (null = <a> の外)
        var linkText = new StringBuilder();
        var skipDepth = 0;              // EXIF 表の中 (> 0 の間は捨てる)
        var spoiler = 0;

        void Emit(string s)
        {
            if (skipDepth > 0) return;
            if (linkHref is not null) linkText.Append(s); else sb.Append(s);
        }

        foreach (Match m in TokenRe.Matches(html))
        {
            if (m.Groups["text"].Success)
            {
                Emit(WebUtility.HtmlDecode(m.Groups["text"].Value).Replace("<", "＜"));
                continue;
            }
            if (!m.Groups["name"].Success) continue;   // コメント
            var name  = m.Groups["name"].Value.ToLowerInvariant();
            var close = m.Groups["close"].Success;
            var attrs = m.Groups["attrs"].Value;

            switch (name)
            {
                case "br":
                    Emit("\n");
                    break;
                case "wbr":
                    break;
                case "table":
                    if (!close && attrs.Contains("exif", StringComparison.OrdinalIgnoreCase)) skipDepth++;
                    else if (close && skipDepth > 0) skipDepth--;
                    break;
                case "s":
                    if (!close) { spoiler++; Emit("[spoiler: "); }
                    else if (spoiler > 0) { spoiler--; Emit("]"); }
                    break;
                case "b":
                case "strong":
                    Emit(close ? "</b>" : "<b>");
                    break;
                case "a":
                    if (!close)
                    {
                        linkHref = HrefRe.Match(attrs) is { Success: true } hm ? WebUtility.HtmlDecode(hm.Groups["v"].Value) : "";
                        linkText.Clear();
                    }
                    else if (linkHref is not null)
                    {
                        var text = linkText.ToString();
                        var href = linkHref;
                        linkHref = null;
                        Emit(LinkToText(href, text));
                    }
                    break;
                default:
                    // span (quote / deadlink / fortune)・pre・u・i 等は文字だけ残す
                    break;
            }
        }
        if (linkHref is not null) sb.Append(linkText);   // 閉じられていない <a>
        return sb.ToString().Trim('\n');
    }

    /// <summary>リンクを文字にする。同じスレへの引用 (<c>#pN</c>) は <c>&gt;&gt;N</c>、それ以外は絶対 URL (見出しが URL と違えば「見出し URL」)。</summary>
    private static string LinkToText(string href, string text)
    {
        if (href.StartsWith("#p", StringComparison.Ordinal)) return text;       // ">>12345" (表示文字列そのまま)
        string url;
        if (href.StartsWith("//", StringComparison.Ordinal)) url = "https:" + href;
        else if (href.StartsWith("/", StringComparison.Ordinal)) url = Origin + href;
        else if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = href;
        else return text;
        // 別スレへの引用は ">>N" の見た目を残しつつ URL を添える (URL はスレリンクとしてアプリ内で開ける)
        var t = text.Trim();
        if (t.Length == 0 || t == url) return url;
        return t + " " + url;
    }

    /// <summary>本文の先頭から題名代わりの 1 行を作る (スレ一覧で題名 (<c>sub</c>) が無いスレ用)。</summary>
    public static string Snippet(string? html, int max = 80)
    {
        var plain = Convert(html).Replace("<b>", "").Replace("</b>", "");
        var line = plain.Replace('\n', ' ').Trim();
        while (line.Contains("  ", StringComparison.Ordinal)) line = line.Replace("  ", " ");
        return line.Length <= max ? line : line[..max] + "…";
    }
}
