using System;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ChBrowser.Services.Bbs;

/// <summary>ふたばの本文 (JSON の <c>com</c> / スレページの <c>&lt;blockquote&gt;</c>: サーバが描画した HTML 断片) を、本アプリの本文方言
/// (改行 + <c>&lt;b&gt;</c> だけ。5ch dat と同じ) に落とす。純粋関数。
///
/// <list type="bullet">
/// <item><description><c>&lt;br&gt;</c> は改行。引用の緑字 (<c>&lt;font color="#789922"&gt;&gt;引用&lt;/font&gt;</c>) 等の装飾は文字だけ残す
///   (引用は行頭の <c>&gt;</c> で分かる。<c>&gt;No.123</c> / <c>&gt;1750599124357.jpg</c> はアンカー規則でアプリ内アンカーになる)。</description></item>
/// <item><description>リンクは URL にする。外部リンクの転送 (<c>/bin/jump.php?URL</c>) は外し、板内の相対リンクは絶対 URL にする
///   (見出しが URL と違えば「見出し URL」)。</description></item>
/// <item><description>本文中の <c>&lt;</c> は (スレ表示がタグと誤認して消さないよう) 全角の <c>＜</c> にする。</description></item>
/// </list></summary>
public static class FutabaBodyConverter
{
    private static readonly Regex TokenRe = new(
        @"<(?<close>/)?(?<name>[a-zA-Z][a-zA-Z0-9]*)(?<attrs>[^>]*?)/?>|<!--[\s\S]*?-->|(?<text>[^<]+|<)", RegexOptions.Compiled);
    private static readonly Regex HrefRe = new(@"href\s*=\s*[""'](?<v>[^""']*)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <param name="html">ふたばの本文 HTML。</param>
    /// <param name="origin">板のサーバ (<c>https://may.2chan.net</c>)。相対リンクの解決に使う。</param>
    public static string Convert(string? html, string origin)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var sb = new StringBuilder(html.Length);
        string? linkHref = null;        // 開いている <a> の href (null = <a> の外)
        var linkText = new StringBuilder();

        void Emit(string s)
        {
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
            switch (name)
            {
                case "br":
                    Emit("\n");
                    break;
                case "b":
                case "strong":
                    Emit(close ? "</b>" : "<b>");
                    break;
                case "a":
                    if (!close)
                    {
                        linkHref = HrefRe.Match(m.Groups["attrs"].Value) is { Success: true } hm ? WebUtility.HtmlDecode(hm.Groups["v"].Value) : "";
                        linkText.Clear();
                    }
                    else if (linkHref is not null)
                    {
                        var text = linkText.ToString();
                        var href = linkHref;
                        linkHref = null;
                        Emit(LinkToText(href, text, origin));
                    }
                    break;
                default:
                    // font (引用の緑字・削除の赤字)・span 等は文字だけ残す
                    break;
            }
        }
        if (linkHref is not null) sb.Append(linkText);   // 閉じられていない <a>
        return sb.ToString().Trim('\n');
    }

    /// <summary>リンクを文字にする。<c>/bin/jump.php?URL</c> (外部リンクの転送) は外す。見出しが URL と同じなら URL だけ。</summary>
    private static string LinkToText(string href, string text, string origin)
    {
        const string Jump = "/bin/jump.php?";
        var j = href.IndexOf(Jump, StringComparison.Ordinal);
        if (j >= 0) href = href[(j + Jump.Length)..];
        string url;
        if (href.StartsWith("//", StringComparison.Ordinal)) url = "https:" + href;
        else if (href.StartsWith("/", StringComparison.Ordinal)) url = origin + href;
        else if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = href;
        else return text;
        var t = text.Trim();
        if (t.Length == 0 || t == url || t == href) return url;
        return t + " " + url;
    }

    /// <summary>本文の先頭から題名代わりの 1 行を作る (スレ本体の題名が「無題」のとき)。</summary>
    public static string Snippet(string plainBody, int max = 60)
    {
        var line = plainBody.Replace("<b>", "").Replace("</b>", "").Replace('\n', ' ').Trim();
        while (line.Contains("  ", StringComparison.Ordinal)) line = line.Replace("  ", " ");
        return line.Length <= max ? line : line[..max] + "…";
    }
}
