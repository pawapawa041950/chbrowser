using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using ChBrowser.Models;
using ChBrowser.Services.Url;

namespace ChBrowser.Services.Bbs;

/// <summary>嫌儲.org (kenmo.org / 00c.org) の提供者。5ch 互換掲示板 (2026-11-04 実測)。
///
/// <list type="bullet">
/// <item><description>ホストは <c>kenmo.org</c> と <c>00c.org</c> の 2 つ (板ごとにどちらか。板一覧に両方が混ざって載る)。板 dir は 1 階層。</description></item>
/// <item><description>読み取りは 5ch と同じ: <c>&lt;dir&gt;/subject.txt</c>、<c>&lt;dir&gt;/dat/&lt;key&gt;.dat</c> (Range 206、新着無しは
///   416 + <c>Content-Range: bytes */全長</c>、Shift_JIS)、<c>&lt;dir&gt;/SETTING.TXT</c>。無いスレは 404。</description></item>
/// <item><description>スレ URL は 5ch 形式 <c>/test/read.cgi/&lt;dir&gt;/&lt;key&gt;/[&lt;post&gt;]</c>。</description></item>
/// <item><description>板一覧は <c>https://kenmo.org/bbsmenu.html</c> (まちBBS と同じ <c>&lt;b&gt;カテゴリ&lt;/b&gt;</c> + <c>&lt;a href&gt;</c> 形式、Shift_JIS)。</description></item>
/// <item><description>書き込みは 5ch と同じ <c>/test/bbs.cgi</c> のフォーム。どんぐり (5ch の acorn) は使わず、確認画面などで
///   サーバが発行する Cookie は提供者ごとの保管に持ち回る。結果判定は 5ch 互換掲示板の汎用判定。</description></item>
/// <item><description>保存ルート <c>data/kenmo.org/</c> と <c>data/00c.org/</c> (5ch と同じ生 dat)。</description></item>
/// </list></summary>
public sealed class KenmoProvider : IBbsProvider
{
    private const string MainHost = "kenmo.org";
    private const string SubHost  = "00c.org";

    private static readonly Regex BoardPathRegex = new(
        @"^/(?<dir>[A-Za-z0-9_]+)/?$",
        RegexOptions.Compiled);

    /// <summary>5ch と同じスレ URL のパス。レス番号は /key/ の直後の連続数字。</summary>
    private static readonly Regex ThreadPathRegex = new(
        @"^/test/read\.cgi/(?<dir>[A-Za-z0-9_]+)/(?<key>[0-9]+)(?:/(?<post>[0-9]+))?.*$",
        RegexOptions.Compiled);

    public string Id          => "kenmo";
    public string DisplayName => "嫌儲.org";

    public BbsCapabilities Capabilities =>
        BbsCapabilities.BoardList | BbsCapabilities.ThreadList | BbsCapabilities.ThreadFetch |
        BbsCapabilities.DeltaByRange | BbsCapabilities.Posting | BbsCapabilities.ThreadCreation |
        BbsCapabilities.BoardInfo;

    public Encoding TextEncoding => Encoding.GetEncoding(932);

    public IReadOnlyList<string> StorageRoots { get; } = new[] { MainHost, SubHost };

    public string DefaultHost => MainHost;

    public int PostNumberDigits => 4;

    public IReadOnlyList<string> HostSuffixes { get; } = new[] { MainHost, SubHost };

    /// <summary>JS 側スレリンク判定 (5ch 形式。ホストは kenmo.org / 00c.org)。</summary>
    public string ThreadLinkJsPattern
        => @"^https?:\/\/(?<host>(?:[A-Za-z0-9-]+\.)*(?:kenmo\.org|00c\.org))\/test\/read\.cgi\/(?<dir>[A-Za-z0-9_]+)\/(?<key>\d+)(?:\/(?<post>[^/?#]*))?";

    public IReadOnlyList<AnchorRule> DefaultAnchorRules { get; } = new[]
    {
        new AnchorRule(">>N", @">>\s*(?<spec>\d+(?:\s*-\s*\d+)?(?:\s*[,，、]\s*\d+(?:\s*-\s*\d+)?)*)"),
        new AnchorRule("＞＞N (全角)", @"＞＞\s*(?<spec>\d+(?:\s*-\s*\d+)?(?:\s*[,，、]\s*\d+(?:\s*-\s*\d+)?)*)", Enabled: false),
    };

    public bool OwnsHost(string host)
        => IsHost(host, MainHost) || IsHost(host, SubHost);

    private static bool IsHost(string host, string root)
        => string.Equals(host, root, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + root, StringComparison.OrdinalIgnoreCase);

    /// <summary>板はホストごとに別 (kenmo.org の板と 00c.org の板) なので、ホストはそのまま使う。</summary>
    public string NormalizeHost(string host) => host.ToLowerInvariant();

    public AddressBarTarget? TryParseUrl(Uri uri, string host)
    {
        var path = uri.AbsolutePath;

        // スレ判定が先 (板の方が短い path にマッチする。テスト板 /test/ もあるので /test/read.cgi/… を先に見る)
        var threadMatch = ThreadPathRegex.Match(path);
        if (threadMatch.Success)
        {
            var postGroup = threadMatch.Groups["post"];
            var postNo    = postGroup.Success && long.TryParse(postGroup.Value, out var n) ? n : 0;
            return new AddressBarTarget(AddressBarTargetKind.Thread, host,
                threadMatch.Groups["dir"].Value, threadMatch.Groups["key"].Value, postNo);
        }

        var boardMatch = BoardPathRegex.Match(path);
        if (boardMatch.Success)
            return new AddressBarTarget(AddressBarTargetKind.Board, host, boardMatch.Groups["dir"].Value, "");

        return null;
    }

    public string BoardUrl(string host, string directoryName)
        => $"https://{host}/{directoryName}/";

    public string ThreadUrl(string host, string directoryName, string threadKey, long postNumber = 0)
        => postNumber > 0
            ? $"https://{host}/test/read.cgi/{directoryName}/{threadKey}/{postNumber}"
            : $"https://{host}/test/read.cgi/{directoryName}/{threadKey}/";

    public string ThreadListUrl(Board board)                => $"https://{board.Host}/{board.DirectoryName}/subject.txt";
    public string ThreadFetchUrl(Board board, string key)   => $"https://{board.Host}/{board.DirectoryName}/dat/{key}.dat";
    public string ThreadPageUrl(Board board, string key)    => ThreadUrl(board.Host, board.DirectoryName, key);
    public string? BoardInfoUrl(Board board)                => $"https://{board.Host}/{board.DirectoryName}/SETTING.TXT";
    public string? PostEndpointUrl(Board board)             => $"https://{board.Host}/test/bbs.cgi";

    public string? BoardListUrl => $"https://{MainHost}/bbsmenu.html";
    public string  BoardListCacheExtension => "html";
    public IReadOnlyList<BoardCategory> ParseBoardList(byte[] bytes) => Api.BbsmenuClient.ParseBbsmenuHtml(this, bytes);

    /// <summary>5ch と同じ生 dat を Range 差分で保存する。</summary>
    public bool UsesNativeDat => true;

    public string ThreadFetchRangeUrl(Board board, string threadKey, long fromNumber)
        => throw new NotSupportedException("嫌儲.org は番号以降の差分取得 (DeltaByNumber) を持たない。Range で差分を取る。");

    /// <summary>subject.txt は 5ch と同じ <c>key.dat&lt;&gt;title (n)</c>。</summary>
    public IReadOnlyList<ThreadInfo> ParseThreadList(byte[] bytes)
        => FiveChProvider.ParseSubjectTxt(bytes, TextEncoding);

    public Post? ParseThreadLine(string line) => null;

    // -----------------------------------------------------------------
    // 書き込み
    // -----------------------------------------------------------------

    /// <summary>名前 / メール / スレ立てあり。どんぐりは使わず、サーバが発行する Cookie を提供者ごとの保管に持つ。</summary>
    public PostFormSpec PostForm { get; } = new(
        SupportsName: true, SupportsMail: true, SupportsNewThread: true, UsesDonguriAuth: false, PersistsCookies: true);

    /// <summary>5ch と同じ bbs.cgi のフォーム。Referer は板 URL。</summary>
    public PostSubmission BuildPostSubmission(PostRequest req)
        => FiveChProvider.BuildBbsCgiSubmission(req, PostEndpointUrl(req.Board)!, TextEncoding, BoardUrl(req.Board.Host, req.Board.DirectoryName));

    public PostResult ClassifyPostResponse(string html, PostResponseContext context)
        => PostResponseHtml.ClassifyGeneric(html, context);
}
