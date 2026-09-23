using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using ChBrowser.Models;

namespace ChBrowser.Services.Api;

/// <summary>番号列付きログ (アプリ固有形式) の読み書き。<c>doc/multi-bbs-design.md</c> §5.2。
///
/// <para>5ch / bbspink 以外の掲示板のレスは、提供者が取得結果をこの形式に変換して <c>&lt;key&gt;.dat</c> に保存する
/// (5ch はサーバの生 dat を Range 差分で追記するため従来どおり Shift_JIS の生 dat のまま)。
/// 読み手は先頭行の版マーカーで判別する (<see cref="IsNumberedLog"/>)。</para>
///
/// <para>形式 (UTF-8、LF 区切り、1 行 1 レス、先頭行は版マーカー):</para>
/// <code>
/// #chb-log v1 provider=&lt;id&gt;
/// 番号&lt;&gt;名前&lt;&gt;メール&lt;&gt;日付&lt;&gt;本文&lt;&gt;スレタイ&lt;&gt;ID&lt;&gt;拡張JSON
/// </code>
/// <list type="bullet">
/// <item><description>番号は掲示板の実番号 (欠番は欠番のまま)。</description></item>
/// <item><description>各列は HTML エンティティでエスケープ (= 列内に <c>&lt;&gt;</c> が現れない)。本文の改行は <c>&lt;br&gt;</c>。
///   5ch dat と同じ規約なので、本文の見え方・アンカー判定は共通。</description></item>
/// <item><description>スレタイは 1 レス目のみ。拡張 JSON は添付ファイル・作成時刻・元 ID 等 (現状は読み飛ばし、将来 <see cref="Post"/> に載せる)。</description></item>
/// </list></summary>
public static class NumberedLogFormat
{
    public const string MarkerPrefix = "#chb-log v1";
    private static readonly byte[] MarkerBytes = Encoding.UTF8.GetBytes("#chb-log");
    private static readonly string[] FieldSep  = { "<>" };

    /// <summary>先頭が版マーカーなら true (= この形式)。5ch の生 dat は名前欄から始まるので誤判定しない。</summary>
    public static bool IsNumberedLog(ReadOnlySpan<byte> bytes)
        => bytes.Length >= MarkerBytes.Length && bytes[..MarkerBytes.Length].SequenceEqual(MarkerBytes);

    /// <summary>版マーカー行を作る。</summary>
    public static string MarkerLine(string providerId) => $"{MarkerPrefix} provider={providerId}";

    /// <summary>版マーカー行から provider id を取り出す。マーカーでなければ null。</summary>
    public static string? ProviderIdOf(string firstLine)
    {
        if (!firstLine.StartsWith(MarkerPrefix, StringComparison.Ordinal)) return null;
        var idx = firstLine.IndexOf("provider=", StringComparison.Ordinal);
        return idx < 0 ? "" : firstLine[(idx + "provider=".Length)..].Trim();
    }

    public static IReadOnlyList<Post> Parse(byte[] utf8Bytes) => ParseText(Encoding.UTF8.GetString(utf8Bytes));

    public static IReadOnlyList<Post> ParseText(string text)
    {
        var lines = text.Split('\n');
        var posts = new List<Post>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0) continue;
            if (i == 0 && line.StartsWith("#chb-log", StringComparison.Ordinal)) continue;
            var post = ParseLine(line);
            if (post is not null) posts.Add(post);
        }
        return posts;
    }

    /// <summary>1 行を <see cref="Post"/> に。番号列が無い / 数値でない行は null。</summary>
    public static Post? ParseLine(string line)
    {
        line = line.TrimEnd('\r');
        if (line.Length == 0 || line[0] == '#') return null;
        var f = line.Split(FieldSep, StringSplitOptions.None);
        if (f.Length < 5) return null;
        if (!long.TryParse(f[0], out var number) || number <= 0) return null;
        var title = f.Length >= 6 ? WebUtility.HtmlDecode(f[5]).Trim() : "";
        return new Post(
            Number:      number,
            Name:        WebUtility.HtmlDecode(f[1]),
            Mail:        WebUtility.HtmlDecode(f[2]),
            DateText:    WebUtility.HtmlDecode(f[3]),
            Id:          f.Length >= 7 ? WebUtility.HtmlDecode(f[6]) : "",
            Body:        DecodeBody(f[4]),
            ThreadTitle: title.Length > 0 ? title : null,
            Ext:         f.Length >= 8 ? ParseExtra(WebUtility.HtmlDecode(f[7])) : null);
    }

    /// <summary>1 レスを 1 行に。拡張 JSON は <paramref name="extraJson"/> が指定されればそれを、無ければ <see cref="Post.Ext"/> を直列化する。</summary>
    public static string FormatLine(Post p, string? extraJson = null)
        => string.Join("<>", new[]
        {
            p.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Esc(p.Name), Esc(p.Mail), Esc(p.DateText), EncodeBody(p.Body),
            Esc(p.ThreadTitle ?? ""), Esc(p.Id), Esc(extraJson ?? SerializeExtra(p.Ext)),
        });

    private static readonly System.Text.Json.JsonSerializerOptions ExtraJsonOptions = new()
    {
        PropertyNamingPolicy   = System.Text.Json.JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>拡張情報を 8 列目用の JSON にする (null なら空文字 = 従来と同じ行)。</summary>
    public static string SerializeExtra(PostExtra? ext)
        => ext is null ? "" : System.Text.Json.JsonSerializer.Serialize(ext, ExtraJsonOptions);

    /// <summary>8 列目の JSON を拡張情報に。空 / 壊れた JSON は null (= 読み飛ばし。ログ全体は読める)。</summary>
    public static PostExtra? ParseExtra(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<PostExtra>(json, ExtraJsonOptions); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>マーカー行 + 全レスを UTF-8 バイト列にする (末尾 LF あり)。</summary>
    public static byte[] Serialize(string providerId, IEnumerable<Post> posts)
    {
        var sb = new StringBuilder();
        sb.Append(MarkerLine(providerId)).Append('\n');
        foreach (var p in posts) sb.Append(FormatLine(p)).Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>ログ末尾に追記するためのバイト列 (マーカー無し)。</summary>
    public static byte[] SerializeAppend(IEnumerable<Post> posts)
    {
        var sb = new StringBuilder();
        foreach (var p in posts) sb.Append(FormatLine(p)).Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>ファイル先頭を見てこの形式ならスレタイ (1 レス目のスレタイ列) を返す。違う形式 / 読めない場合は false。
    /// 全ログ走査など「タイトルだけ要る」場面向け (= 全 parse しない)。</summary>
    public static bool TryReadTitle(string path, out string? title)
    {
        title = null;
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[MarkerBytes.Length];
            var n = fs.Read(head, 0, head.Length);
            if (n < head.Length || !IsNumberedLog(head)) return false;
            fs.Position = 0;
            using var reader = new StreamReader(fs, Encoding.UTF8);
            _ = reader.ReadLine();                     // マーカー
            var first = reader.ReadLine();
            title = first is null ? null : ParseLine(first)?.ThreadTitle;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NumberedLogFormat] read title failed ({path}): {ex.Message}");
            return false;
        }
    }

    private static string Esc(string s) => WebUtility.HtmlEncode(s ?? "");

    private static string EncodeBody(string body)
        => Esc(body ?? "").Replace("\r\n", "\n").Replace("\n", "<br>");

    private static string DecodeBody(string raw)
        => WebUtility.HtmlDecode(raw.Replace(" <br> ", "\n").Replace("<br>", "\n"));
}
