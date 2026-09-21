using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ChBrowser.Models;
using ChBrowser.Services.Api;

namespace ChBrowser.Services.Bbs;

/// <summary>したらば (rawmode.cgi) と まちBBS (offlaw.cgi) に共通する「.cgi 系」の行形式パーサ。
/// 両者は 2ch 互換 CGI 由来で、スレ一覧 <c>key.cgi,タイトル(件数)</c> と本文 7 列 <c>番号&lt;&gt;名前&lt;&gt;メール&lt;&gt;日付&lt;&gt;本文&lt;&gt;スレタイ&lt;&gt;ID/ホスト</c>
/// の形が同じで、文字コード (EUC-JP / Shift_JIS) と 7 列目・日付列の意味だけが違う。
/// 提供者はここを呼ぶだけにして、差異は引数で渡す。</summary>
internal static class CgiBbsFormats
{
    /// <summary>スレ一覧 1 行: <c>1607927892.cgi,タイトル(230)</c>。タイトルは貪欲 (括弧を含み得る) で、件数は最後の括弧の数字。</summary>
    private static readonly Regex SubjectLineRegex = new(
        @"^(?<key>\d+)\.cgi,(?<title>.*)\((?<count>\d+)\)\s*$",
        RegexOptions.Compiled);

    private static readonly string[] FieldSep = { "<>" };

    /// <summary>subject.txt (cgi 形式) をパース。したらばはファイル末尾に先頭行の複製が付くので、
    /// 同じ key は最初の出現だけ残す。<see cref="ThreadInfo.Order"/> は採用した行の出現順 (1 始まり)。</summary>
    public static IReadOnlyList<ThreadInfo> ParseSubject(byte[] bytes, Encoding encoding)
    {
        var text  = encoding.GetString(bytes);
        var lines = text.Split('\n');
        var list  = new List<ThreadInfo>(lines.Length);
        var seen  = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrEmpty(line)) continue;

            var m = SubjectLineRegex.Match(line);
            if (!m.Success) continue;

            var key = m.Groups["key"].Value;
            if (!seen.Add(key)) continue;

            list.Add(new ThreadInfo(
                Key:       key,
                Title:     WebUtility.HtmlDecode(m.Groups["title"].Value).Trim(),
                PostCount: int.TryParse(m.Groups["count"].Value, out var n) ? n : 0,
                Order:     list.Count + 1));
        }
        return list;
    }

    /// <summary>本文 1 行 (7 列) を <see cref="Post"/> に。
    /// <paramref name="dateHasId"/>=true なら 4 列目が「日付 ID:xxxx」(まちBBS) なので 5ch と同じ分離を行い、7 列目は無視する。
    /// false なら 4 列目は日付のみで 7 列目が ID (したらば)。</summary>
    public static Post? ParseNumberedLine(string line, bool dateHasId)
    {
        line = line.TrimEnd('\r');
        if (line.Length == 0) return null;

        var f = line.Split(FieldSep, StringSplitOptions.None);
        if (f.Length < 5) return null;
        if (!long.TryParse(f[0].Trim(), out var number) || number <= 0) return null;

        string dateText, id;
        if (dateHasId)
        {
            (dateText, id) = DatParser.SplitDateAndId(WebUtility.HtmlDecode(f[3]));
        }
        else
        {
            dateText = WebUtility.HtmlDecode(f[3]).Trim();
            id       = f.Length >= 7 ? WebUtility.HtmlDecode(f[6]).Trim() : "";
        }

        // スレタイ列は 1 レス目にだけ意味がある。まちBBS は削除レスを「削除<>削除<>...<>削除」と全列埋めで返し
        // スレタイ列にも「削除」が入るので、1 レス目以外の値はサーバの都合として読み捨てる。
        var title = number == 1 && f.Length >= 6 ? WebUtility.HtmlDecode(f[5]).Trim() : "";

        return new Post(
            Number:      number,
            Name:        NormalizeName(WebUtility.HtmlDecode(f[1])),
            Mail:        WebUtility.HtmlDecode(f[2]),
            DateText:    dateText,
            Id:          id,
            Body:        NormalizeBody(f[4]),
            ThreadTitle: title.Length > 0 ? title : null);
    }

    /// <summary>名前欄の装飾タグを描画側 (thread.js) が理解する形に寄せる。
    /// まちBBS は運営レスの名前を <c>&lt;font color=#FF0000&gt;どる&lt;/font&gt;</c> で返すが、描画側は
    /// <c>&lt;b&gt;</c> / <c>&lt;a&gt;</c> / <c>&lt;small&gt;</c> 以外のタグを中身ごと落とすため、名前が空になる。
    /// 5ch の名前欄が <c>&lt;b&gt;名前&lt;/b&gt;</c> で来るのに合わせ、<c>&lt;font&gt;</c> は <c>&lt;b&gt;</c> に置き換える
    /// (= 運営名は太字で目立つ)。他のタグは触らない。</summary>
    private static string NormalizeName(string name)
        => FontTagRe.Replace(name, m => m.Value.StartsWith("</", System.StringComparison.Ordinal) ? "</b>" : "<b>");

    private static readonly System.Text.RegularExpressions.Regex FontTagRe = new(
        @"</?font\b[^>]*>", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>本文の <c>&lt;br&gt;</c> を改行に直し、HTML エンティティをデコード。
    /// 2ch 互換 CGI は本文の前後に半角スペース 1 つを付けるので 1 文字だけ剥がす (AA を壊さないため Trim はしない)。
    /// <see cref="DatParser"/> の本文正規化と同じ規約。</summary>
    private static string NormalizeBody(string raw)
    {
        var s = raw.Replace(" <br> ", "\n").Replace("<br>", "\n");
        s = WebUtility.HtmlDecode(s);
        if (s.Length > 0 && s[0]  == ' ') s = s[1..];
        if (s.Length > 0 && s[^1] == ' ') s = s[..^1];
        return s;
    }
}
