using System;
using System.Text.RegularExpressions;

namespace ChBrowser.Services.Url;

/// <summary>アドレスバーへ入力されたテキストを Board / Thread / Invalid の 3 種類に分類するパーサ (Phase 14)。
/// 純粋関数で副作用なし。設計詳細は 設計書 §7.7 参照。</summary>
public static class AddressBarParser
{
    private static readonly Regex BoardPathRegex  = new(
        @"^/(?<dir>[A-Za-z0-9]+)/?$",
        RegexOptions.Compiled);

    /// <summary>スレ URL のパス。レス番号 (= /key/ の直後の連続数字) を任意マッチ。
    /// 「/100」「/100/」「/100-150」「/100n」のいずれも先頭の数字だけ post グループに入る。</summary>
    private static readonly Regex ThreadPathRegex = new(
        @"^/test/read\.cgi/(?<dir>[A-Za-z0-9]+)/(?<key>[0-9]+)(?:/(?<post>[0-9]+))?.*$",
        RegexOptions.Compiled);

    /// <summary>入力テキストを解釈し、対応する <see cref="AddressBarTarget"/> を返す。
    /// 5ch.io / bbspink.com 以外のホスト、URL として parse できないテキスト、認識不能なパスは
    /// すべて <see cref="AddressBarTargetKind.Invalid"/> を返す。
    /// 5ch.net 由来のホストは 5ch.io に書き換えてから判定する (= 古い URL の貼り付け救済)。</summary>
    public static AddressBarTarget Parse(string? input)
    {
        var trimmed = (input ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed)) return AddressBarTarget.Invalid;

        // プロトコル省略 (例: news.5ch.io/news/) は https:// を前置
        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = "https://" + trimmed;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return AddressBarTarget.Invalid;

        // 5ch.net → 5ch.io 書き換え (host suffix のみ)
        var host = uri.Host;
        if (string.Equals(host, "5ch.net", StringComparison.OrdinalIgnoreCase))
            host = "5ch.io";
        else if (host.EndsWith(".5ch.net", StringComparison.OrdinalIgnoreCase))
            host = host[..^".5ch.net".Length] + ".5ch.io";

        if (!IsAllowedHost(host)) return AddressBarTarget.Invalid;

        var path = uri.AbsolutePath;

        // スレ判定が先 (= /test/read.cgi/<dir>/<key>/)。Board の方が短い path にマッチするので後判定。
        var threadMatch = ThreadPathRegex.Match(path);
        if (threadMatch.Success)
        {
            var postGroup = threadMatch.Groups["post"];
            var postNo    = postGroup.Success && int.TryParse(postGroup.Value, out var n) ? n : 0;
            return new AddressBarTarget(
                AddressBarTargetKind.Thread,
                host,
                threadMatch.Groups["dir"].Value,
                threadMatch.Groups["key"].Value,
                postNo);
        }

        var boardMatch = BoardPathRegex.Match(path);
        if (boardMatch.Success)
        {
            return new AddressBarTarget(
                AddressBarTargetKind.Board,
                host,
                boardMatch.Groups["dir"].Value,
                "");
        }

        return AddressBarTarget.Invalid;
    }

    private static bool IsAllowedHost(string host)
        =>     string.Equals(host, "5ch.io",     StringComparison.OrdinalIgnoreCase)
           ||  string.Equals(host, "bbspink.com", StringComparison.OrdinalIgnoreCase)
           ||  host.EndsWith(".5ch.io",      StringComparison.OrdinalIgnoreCase)
           ||  host.EndsWith(".bbspink.com", StringComparison.OrdinalIgnoreCase);
}

public enum AddressBarTargetKind
{
    Invalid,
    Board,
    Thread,
}

/// <summary>パース結果。Kind=Invalid のときは他フィールドは空。
/// <see cref="PostNumber"/> は Kind=Thread のときに URL 末尾のレス番号 (例: /1234567890/100) を入れる。
/// レス番号指定なし、または Kind!=Thread のときは 0。</summary>
public sealed record AddressBarTarget(
    AddressBarTargetKind Kind,
    string               Host,
    string               Directory,
    string               ThreadKey,
    int                  PostNumber = 0)
{
    public static AddressBarTarget Invalid { get; } = new(AddressBarTargetKind.Invalid, "", "", "", 0);

    /// <summary>Board の正規 URL (= "https://&lt;host&gt;/&lt;dir&gt;/")。Kind!=Board のときは空。</summary>
    public string BoardUrl => Kind == AddressBarTargetKind.Board ? $"https://{Host}/{Directory}/" : "";

    /// <summary>Thread の正規 URL (= "https://&lt;host&gt;/test/read.cgi/&lt;dir&gt;/&lt;key&gt;/")。Kind!=Thread のときは空。</summary>
    public string ThreadUrl => Kind == AddressBarTargetKind.Thread ? $"https://{Host}/test/read.cgi/{Directory}/{ThreadKey}/" : "";
}
