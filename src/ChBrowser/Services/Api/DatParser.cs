using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using ChBrowser.Models;

namespace ChBrowser.Services.Api;

/// <summary>
/// 5ch dat 形式のパーサ。
/// 1 行 1 レス。フィールドは <c>&lt;&gt;</c> 区切りで 4〜5 個。
/// 本文中の改行は <c>&lt;br&gt;</c> または <c> &lt;br&gt; </c> で表現される。
/// SJIS に無い文字は HTML 数値文字参照 (例: <c>&amp;#x1F600;</c>) で送られてくる。
/// </summary>
public static class DatParser
{
    private static readonly string[] FieldSep = { "<>" };

    /// <summary>SJIS バイト列をパースして <see cref="Post"/> 列を返す。</summary>
    public static IReadOnlyList<Post> Parse(byte[] sjisBytes)
    {
        var sjis = Encoding.GetEncoding(932);
        var text = sjis.GetString(sjisBytes);
        return ParseText(text);
    }

    /// <summary>UTF-16 文字列としてパースする。テスト用。</summary>
    public static IReadOnlyList<Post> ParseText(string text)
    {
        // dat は LF 区切り。CRLF 環境で念のため CR を除く。
        var lines = text.Split('\n');
        var posts = new List<Post>(lines.Length);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0) continue;

            var parts = line.Split(FieldSep, 5, StringSplitOptions.None);
            if (parts.Length < 4) continue; // 壊れた行はスキップ

            var name      = WebUtility.HtmlDecode(parts[0]);
            var mail      = WebUtility.HtmlDecode(parts[1]);
            var (dt, id)  = SplitDateAndId(parts[2]);
            var body      = NormalizeBody(parts[3]);
            var title     = parts.Length >= 5 ? WebUtility.HtmlDecode(parts[4]).Trim() : null;
            if (string.IsNullOrEmpty(title)) title = null;

            posts.Add(new Post(
                Number:      i + 1,
                Name:        name,
                Mail:        mail,
                DateText:    dt,
                Id:          id,
                Body:        body,
                ThreadTitle: title));
        }

        return posts;
    }

    /// <summary>1 行分の dat テキスト (CR/LF を除去済み) を <see cref="Post"/> に変換。空行や壊れた行は null。</summary>
    public static Post? ParseLine(string line, int number)
    {
        line = line.TrimEnd('\r');
        if (line.Length == 0) return null;

        var parts = line.Split(FieldSep, 5, StringSplitOptions.None);
        if (parts.Length < 4) return null;

        var name     = WebUtility.HtmlDecode(parts[0]);
        var mail     = WebUtility.HtmlDecode(parts[1]);
        var (dt, id) = SplitDateAndId(parts[2]);
        var body     = NormalizeBody(parts[3]);
        var title    = parts.Length >= 5 ? WebUtility.HtmlDecode(parts[4]).Trim() : null;
        if (string.IsNullOrEmpty(title)) title = null;

        return new Post(number, name, mail, dt, id, body, title);
    }

    /// <summary>"2026/04/25(土) 12:34:56.78 ID:abc1234" を日付と ID に分離する。</summary>
    private static (string DateText, string Id) SplitDateAndId(string field)
    {
        var s = field.Trim();
        var idx = s.IndexOf("ID:", StringComparison.Ordinal);
        if (idx < 0) return (s, "");

        var datePart = s[..idx].TrimEnd();
        var rest     = s[(idx + 3)..];

        // ID は空白までを取る (BE 等の付加情報が後ろに来ることがある)
        var spaceIdx = rest.IndexOf(' ');
        var id = spaceIdx < 0 ? rest.Trim() : rest[..spaceIdx].Trim();
        return (datePart, id);
    }

    /// <summary>本文の <c>&lt;br&gt;</c> を改行に直し、HTML エンティティをデコード。</summary>
    private static string NormalizeBody(string raw)
    {
        // 5ch dat では " <br> " (前後空白付き) のパターンが多い
        var s = raw.Replace(" <br> ", "\n")
                   .Replace("<br>",   "\n");
        s = WebUtility.HtmlDecode(s);

        // 5ch dat の慣例で本文の前後に半角スペース 1 つが付加されているので剥がす。
        // AA (複数スペース始まり) を壊さないよう、Trim ではなく 1 文字だけ落とす。
        if (s.Length > 0 && s[0]  == ' ') s = s[1..];
        if (s.Length > 0 && s[^1] == ' ') s = s[..^1];
        return s;
    }
}
