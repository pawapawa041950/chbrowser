using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using ChBrowser.Models;
using ChBrowser.Services.Url;

namespace ChBrowser.Services.Bbs;

/// <summary>したらば掲示板 (jbbs.shitaraba.net) の提供者。外部仕様は <c>doc/multi-bbs-design.md</c> §2 (2026-09-20 実測)。
///
/// <list type="bullet">
/// <item><description>ホストは <c>jbbs.shitaraba.net</c> 単一。旧 <c>jbbs.livedoor.jp</c> は正規ホストに書き換えて受理。</description></item>
/// <item><description>板 dir は <b>2 階層</b> <c>&lt;category&gt;/&lt;number&gt;</c> (例 <c>internet/12249</c>)。
///   板 URL <c>https://jbbs.shitaraba.net/internet/12249/</c>、スレ URL <c>https://jbbs.shitaraba.net/bbs/read.cgi/internet/12249/&lt;key&gt;/[&lt;post&gt;]</c>。</description></item>
/// <item><description>スレ一覧 <c>&lt;dir&gt;/subject.txt</c> (<c>key.cgi,タイトル(件数)</c>、末尾に先頭行の複製)、
///   本文 <c>bbs/rawmode.cgi/&lt;dir&gt;/&lt;key&gt;/[&lt;from&gt;-]</c> 7 列 (番号列あり、削除は欠番)、
///   板設定 <c>bbs/api/setting.cgi/&lt;dir&gt;/</c> (<c>BBS_TITLE</c> = 板名)、投稿 <c>bbs/write.cgi</c>。板一覧は無い。</description></item>
/// <item><description><b>EUC-JP</b>。保存ルートは <c>data/shitaraba.net/</c> (dir に '/' を含むので <c>internet/12249/</c> と入れ子になる)。
///   ログは番号列付きログ (<see cref="Api.NumberedLogFormat"/>)。</description></item>
/// </list></summary>
public sealed class ShitarabaProvider : IBbsProvider
{
    public const string CanonicalHost = "jbbs.shitaraba.net";
    private const string LegacyHost   = "jbbs.livedoor.jp";

    private static readonly Regex BoardPathRegex = new(
        @"^/(?<dir>[A-Za-z0-9_]+/[0-9]+)/?$",
        RegexOptions.Compiled);

    /// <summary>スレ URL。<c>/bbs/read.cgi/&lt;cat&gt;/&lt;num&gt;/&lt;key&gt;[/&lt;post&gt;...]</c>。
    /// 「/3」「/3-10」「/l50」のいずれも先頭の連続数字だけを post に取る (5ch と同じ流儀)。</summary>
    private static readonly Regex ThreadPathRegex = new(
        @"^/bbs/read\.cgi/(?<dir>[A-Za-z0-9_]+/[0-9]+)/(?<key>[0-9]+)(?:/(?<post>[0-9]+))?.*$",
        RegexOptions.Compiled);

    public string Id          => "shitaraba";
    public string DisplayName => "したらば";

    public BbsCapabilities Capabilities =>
        BbsCapabilities.ThreadList | BbsCapabilities.ThreadFetch | BbsCapabilities.DeltaByNumber |
        BbsCapabilities.BoardInfo  | BbsCapabilities.Posting     | BbsCapabilities.ThreadCreation;

    public Encoding TextEncoding => Encoding.GetEncoding("euc-jp");

    public IReadOnlyList<string> StorageRoots { get; } = new[] { "shitaraba.net" };

    public string DefaultHost => CanonicalHost;

    public int PostNumberDigits => 4;

    public IReadOnlyList<string> HostSuffixes { get; } = new[] { "shitaraba.net", "livedoor.jp" };

    /// <summary>JS 側スレリンク判定。ホストは <c>jbbs.</c> 始まりを要求 (livedoor.jp 全体を拾わないため)。dir は 2 階層。</summary>
    public string ThreadLinkJsPattern
        => @"^https?:\/\/(?<host>jbbs\.[A-Za-z0-9.-]+)\/bbs\/read\.cgi\/(?<dir>[A-Za-z0-9_]+\/\d+)\/(?<key>\d+)(?:\/(?<post>[^/?#]*))?";

    /// <summary>5ch と同じ <c>&gt;&gt;N</c> 系。したらばの本文でも同じ記法が使われる。</summary>
    public IReadOnlyList<AnchorRule> DefaultAnchorRules { get; } = new[]
    {
        new AnchorRule(">>N", @">>\s*(?<spec>\d+(?:\s*-\s*\d+)?(?:\s*[,，、]\s*\d+(?:\s*-\s*\d+)?)*)"),
        new AnchorRule("＞＞N (全角)", @"＞＞\s*(?<spec>\d+(?:\s*-\s*\d+)?(?:\s*[,，、]\s*\d+(?:\s*-\s*\d+)?)*)", Enabled: false),
    };

    public string? BoardListUrl => null;
    public string  BoardListCacheExtension => "";
    public IReadOnlyList<BoardCategory> ParseBoardList(byte[] bytes) => Array.Empty<BoardCategory>();

    // -----------------------------------------------------------------
    // 板検索 (板一覧が無いので、ポータルのキーワード検索を使う)
    // -----------------------------------------------------------------

    /// <summary>ポータル (rentalbbs.shitaraba.com) の掲示板検索。<c>query</c> は UTF-8、1 ページ 20 件、成人向けは除外。
    /// (2026-09-23 実測。フォームの <c>word</c> パラメータは文字コードの扱いが壊れているので、結果ページのリンクが使う <c>query</c> を使う。)</summary>
    public const string SearchUrl = "https://rentalbbs.shitaraba.com/jbbs/search/";
    private const int SearchMaxPages = 3;

    private static readonly Regex SearchHitRegex = new(
        @"<a href=""https?://jbbs\.shitaraba\.net/(?<cat>[a-z]+)/(?<num>[0-9]+)/?"">(?<name>[^<]*)</a>(?:(?!<li\b)[\s\S])*?<p>(?<desc>[\s\S]*?)</p>",
        RegexOptions.Compiled);

    public bool SupportsBoardSearch => true;

    public async System.Threading.Tasks.Task<IReadOnlyList<BoardSearchHit>> SearchBoardsAsync(
        System.Net.Http.HttpClient http, string keyword, System.Threading.CancellationToken ct)
    {
        var hits = new List<BoardSearchHit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 1; page <= SearchMaxPages; page++)
        {
            var url = $"{SearchUrl}?filter=exclude_adult&query={Uri.EscapeDataString(keyword)}" + (page > 1 ? $"&page={page}" : "");
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var html  = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var found = ParseSearchResults(html);
            foreach (var h in found)
                if (seen.Add(h.Board.DirectoryName)) hits.Add(h);
            if (found.Count < 20) break;   // 最終ページ
        }
        return hits;
    }

    /// <summary>検索結果ページ (UTF-8 HTML) から板を取り出す。</summary>
    public IReadOnlyList<BoardSearchHit> ParseSearchResults(string html)
    {
        var list = new List<BoardSearchHit>();
        foreach (Match m in SearchHitRegex.Matches(html))
        {
            var dir  = m.Groups["cat"].Value + "/" + m.Groups["num"].Value;
            var name = System.Net.WebUtility.HtmlDecode(m.Groups["name"].Value).Trim();
            var desc = System.Net.WebUtility.HtmlDecode(Regex.Replace(m.Groups["desc"].Value, "<[^>]+>", "")).Trim();
            if (name.Length == 0) name = dir;
            list.Add(new BoardSearchHit(new Board(dir, name, BoardUrl(CanonicalHost, dir), "", 0), desc));
        }
        return list;
    }
    public bool    UsesNativeDat => false;

    public bool OwnsHost(string host)
        => string.Equals(host, CanonicalHost, StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, LegacyHost,    StringComparison.OrdinalIgnoreCase);

    public string NormalizeHost(string host)
        => OwnsHost(host) ? CanonicalHost : host;

    public AddressBarTarget? TryParseUrl(Uri uri, string host)
    {
        var path = uri.AbsolutePath;

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
        {
            // "/bbs/<n>/" は CGI パスの誤認 (bbs はカテゴリ名として存在しない)
            var dir = boardMatch.Groups["dir"].Value;
            if (dir.StartsWith("bbs/", StringComparison.OrdinalIgnoreCase)) return null;
            return new AddressBarTarget(AddressBarTargetKind.Board, host, dir, "");
        }

        return null;
    }

    // 正規 URL はホストを常に canonical に寄せる (旧ホストの Board が残っていても canonical で組む)。
    public string BoardUrl(string host, string directoryName)
        => $"https://{CanonicalHost}/{directoryName}/";

    public string ThreadUrl(string host, string directoryName, string threadKey, long postNumber = 0)
        => postNumber > 0
            ? $"https://{CanonicalHost}/bbs/read.cgi/{directoryName}/{threadKey}/{postNumber}"
            : $"https://{CanonicalHost}/bbs/read.cgi/{directoryName}/{threadKey}/";

    public string ThreadListUrl(Board board)
        => $"https://{CanonicalHost}/{board.DirectoryName}/subject.txt";

    public string ThreadFetchUrl(Board board, string threadKey)
        => $"https://{CanonicalHost}/bbs/rawmode.cgi/{board.DirectoryName}/{threadKey}/";

    public string ThreadFetchRangeUrl(Board board, string threadKey, long fromNumber)
        => $"https://{CanonicalHost}/bbs/rawmode.cgi/{board.DirectoryName}/{threadKey}/{fromNumber}-";

    public string ThreadPageUrl(Board board, string threadKey)
        => ThreadUrl(board.Host, board.DirectoryName, threadKey);

    public string? BoardInfoUrl(Board board)
        => $"https://{CanonicalHost}/bbs/api/setting.cgi/{board.DirectoryName}/";

    /// <summary>板レベルの書き込みフォーム (スレ立て)。レスへの書き込みは末尾に <c>&lt;key&gt;/</c> を足す (投稿実装側で組む)。</summary>
    public string? PostEndpointUrl(Board board)
        => $"https://{CanonicalHost}/bbs/write.cgi/{board.DirectoryName}/";

    public IReadOnlyList<ThreadInfo> ParseThreadList(byte[] bytes)
        => CgiBbsFormats.ParseSubject(bytes, TextEncoding);

    /// <summary>rawmode.cgi の 1 行: <c>番号&lt;&gt;名前&lt;&gt;メール&lt;&gt;日付&lt;&gt;本文&lt;&gt;スレタイ&lt;&gt;ID</c>。</summary>
    public Post? ParseThreadLine(string line)
        => CgiBbsFormats.ParseNumberedLine(line, dateHasId: false);

    // -----------------------------------------------------------------
    // 書き込み
    // -----------------------------------------------------------------

    /// <summary>名前 / メール / スレ立てあり。どんぐり認証は無い。</summary>
    public PostFormSpec PostForm { get; } = new(SupportsName: true, SupportsMail: true, SupportsNewThread: true, UsesDonguriAuth: false);

    /// <summary>write.cgi へのフォーム (EUC-JP)。
    /// レス: <c>https://jbbs.shitaraba.net/bbs/write.cgi/&lt;category&gt;/&lt;number&gt;/&lt;key&gt;/</c>、
    /// スレ立て: <c>.../write.cgi/&lt;category&gt;/&lt;number&gt;/new/</c>。
    /// フィールド: <c>DIR, BBS, [KEY], TIME, NAME, MAIL, MESSAGE, [SUBJECT], submit</c>。Referer はレスならスレページ、スレ立てなら板 URL。
    /// (write.cgi のフィールド名・パス形式は実測ではなく既存クライアントの慣習に基づく。)</summary>
    public PostSubmission BuildPostSubmission(PostRequest req)
    {
        // dir は "<category>/<number>" の 2 階層
        var dir      = req.Board.DirectoryName.Trim('/');
        var slash    = dir.IndexOf('/');
        var category = slash >= 0 ? dir[..slash]       : dir;
        var number   = slash >= 0 ? dir[(slash + 1)..] : "";

        var baseUrl  = PostEndpointUrl(req.Board)!; // https://jbbs.shitaraba.net/bbs/write.cgi/<dir>/
        var endpoint = req.IsNewThread ? baseUrl + "new/" : baseUrl + req.ThreadKey + "/";

        var fields = new List<KeyValuePair<string, string>>
        {
            new("DIR", category),
            new("BBS", number),
        };
        if (req.IsReply) fields.Add(new("KEY", req.ThreadKey!));
        fields.Add(new("TIME",    PostResponseHtml.UnixNow()));
        fields.Add(new("NAME",    req.Name));
        fields.Add(new("MAIL",    req.Mail));
        fields.Add(new("MESSAGE", req.Message));
        if (req.IsNewThread) fields.Add(new("SUBJECT", req.Subject!));
        fields.Add(new("submit", req.IsNewThread ? "新規スレッド作成" : "書き込む"));

        var referer = req.IsReply
            ? ThreadUrl(req.Board.Host, req.Board.DirectoryName, req.ThreadKey!)
            : BoardUrl(req.Board.Host, req.Board.DirectoryName);
        return new PostSubmission(new Uri(endpoint), fields, TextEncoding, "EUC-JP", referer);
    }

    public PostResult ClassifyPostResponse(string html, PostResponseContext context)
        => PostResponseHtml.ClassifyGeneric(html, context);
}
