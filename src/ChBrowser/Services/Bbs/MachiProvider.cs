using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using ChBrowser.Models;
using ChBrowser.Services.Url;

namespace ChBrowser.Services.Bbs;

/// <summary>まちBBS (machi.to) の提供者。外部仕様は <c>doc/multi-bbs-design.md</c> §2 (2026-09-20 実測)。
///
/// <list type="bullet">
/// <item><description>ホストは <c>machi.to</c> 単一。旧仕様の地方サブドメイン (<c>kanto.machi.to</c> 等) は同一サーバの別名なので
///   <c>machi.to</c> に正規化して受理する (決定 D7)。</description></item>
/// <item><description>板 dir は 1 階層 (例 <c>tawara</c>)。板 URL <c>https://machi.to/tawara/</c>、
///   スレ URL <c>https://machi.to/bbs/read.cgi/tawara/&lt;key&gt;/[&lt;post&gt;]</c>。</description></item>
/// <item><description>スレ一覧 <c>&lt;dir&gt;/subject.txt</c> (<c>key.cgi,タイトル(件数)</c>)、本文 <c>bbs/offlaw.cgi/2/&lt;dir&gt;/&lt;key&gt;/[&lt;from&gt;-]</c>
///   7 列 (番号列あり、4 列目に「日付 ID:xxx」、7 列目はホスト識別子、削除は欠番)。dat は無く read.cgi はクライアント非推奨。
///   板一覧 <c>bbsmenu.html</c>、板設定 <c>SETTING.TXT</c> (無い板もある)、投稿 <c>bbs/write.cgi</c>。</description></item>
/// <item><description>Shift_JIS。保存ルート <c>data/machi.to/</c>。ログは番号列付きログ (<see cref="Api.NumberedLogFormat"/>)。</description></item>
/// <item><description>サーバは短時間の大量アクセスを拒否するので、取得は通常の逐次利用に留める。</description></item>
/// </list></summary>
public sealed class MachiProvider : IBbsProvider
{
    public const string CanonicalHost = "machi.to";

    private static readonly Regex BoardPathRegex = new(
        @"^/(?<dir>[A-Za-z0-9_]+)/?$",
        RegexOptions.Compiled);

    private static readonly Regex ThreadPathRegex = new(
        @"^/bbs/read\.cgi/(?<dir>[A-Za-z0-9_]+)/(?<key>[0-9]+)(?:/(?<post>[0-9]+))?.*$",
        RegexOptions.Compiled);

    public string Id          => "machi";
    public string DisplayName => "まちBBS";

    public BbsCapabilities Capabilities =>
        BbsCapabilities.BoardList | BbsCapabilities.ThreadList | BbsCapabilities.ThreadFetch |
        BbsCapabilities.DeltaByNumber | BbsCapabilities.Posting | BbsCapabilities.ThreadCreation;

    public Encoding TextEncoding => Encoding.GetEncoding(932);

    public IReadOnlyList<string> StorageRoots { get; } = new[] { "machi.to" };

    public string DefaultHost => CanonicalHost;

    public int PostNumberDigits => 4;

    public IReadOnlyList<string> HostSuffixes { get; } = new[] { "machi.to" };

    /// <summary>JS 側スレリンク判定。地方サブドメイン付きの旧 URL も拾う。</summary>
    public string ThreadLinkJsPattern
        => @"^https?:\/\/(?<host>(?:[A-Za-z0-9-]+\.)*machi\.to)\/bbs\/read\.cgi\/(?<dir>[A-Za-z0-9_]+)\/(?<key>\d+)(?:\/(?<post>[^/?#]*))?";

    public IReadOnlyList<AnchorRule> DefaultAnchorRules { get; } = new[]
    {
        new AnchorRule(">>N", @">>\s*(?<spec>\d+(?:\s*-\s*\d+)?(?:\s*[,，、]\s*\d+(?:\s*-\s*\d+)?)*)"),
        new AnchorRule("＞＞N (全角)", @"＞＞\s*(?<spec>\d+(?:\s*-\s*\d+)?(?:\s*[,，、]\s*\d+(?:\s*-\s*\d+)?)*)", Enabled: false),
    };

    public string? BoardListUrl => $"https://{CanonicalHost}/bbsmenu.html";
    public string  BoardListCacheExtension => "html";
    public IReadOnlyList<BoardCategory> ParseBoardList(byte[] bytes) => Api.BbsmenuClient.ParseBbsmenuHtml(this, bytes);
    public bool    UsesNativeDat => false;

    public bool OwnsHost(string host)
        => string.Equals(host, CanonicalHost, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + CanonicalHost, StringComparison.OrdinalIgnoreCase);

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
            var dir = boardMatch.Groups["dir"].Value;
            if (string.Equals(dir, "bbs", StringComparison.OrdinalIgnoreCase)) return null; // CGI ディレクトリ
            return new AddressBarTarget(AddressBarTargetKind.Board, host, dir, "");
        }

        return null;
    }

    // 正規 URL はホストを常に canonical に寄せる (地方サブドメインの Board が残っていても machi.to で組む)。
    public string BoardUrl(string host, string directoryName)
        => $"https://{CanonicalHost}/{directoryName}/";

    public string ThreadUrl(string host, string directoryName, string threadKey, long postNumber = 0)
        => postNumber > 0
            ? $"https://{CanonicalHost}/bbs/read.cgi/{directoryName}/{threadKey}/{postNumber}"
            : $"https://{CanonicalHost}/bbs/read.cgi/{directoryName}/{threadKey}/";

    public string ThreadListUrl(Board board)
        => $"https://{CanonicalHost}/{board.DirectoryName}/subject.txt";

    public string ThreadFetchUrl(Board board, string threadKey)
        => $"https://{CanonicalHost}/bbs/offlaw.cgi/2/{board.DirectoryName}/{threadKey}/";

    public string ThreadFetchRangeUrl(Board board, string threadKey, long fromNumber)
        => $"https://{CanonicalHost}/bbs/offlaw.cgi/2/{board.DirectoryName}/{threadKey}/{fromNumber}-";

    public string ThreadPageUrl(Board board, string threadKey)
        => ThreadUrl(board.Host, board.DirectoryName, threadKey);

    /// <summary>SETTING.TXT は無い板もある (404)。呼び出し側 (<see cref="Api.SettingTxtClient.GetOrFetchAsync"/>) は失敗を null として扱う。</summary>
    public string? BoardInfoUrl(Board board)
        => $"https://{CanonicalHost}/{board.DirectoryName}/SETTING.TXT";

    public string? PostEndpointUrl(Board board)
        => $"https://{CanonicalHost}/bbs/write.cgi";

    public IReadOnlyList<ThreadInfo> ParseThreadList(byte[] bytes)
        => CgiBbsFormats.ParseSubject(bytes, TextEncoding);

    /// <summary>offlaw.cgi v2 の 1 行: <c>番号&lt;&gt;名前&lt;&gt;メール&lt;&gt;日付 ID:xxxx&lt;&gt;本文&lt;&gt;スレタイ&lt;&gt;ホスト識別子</c>。
    /// 4 列目は 5ch と同じく日付と ID を分離する。7 列目 (ホスト識別子) は当面読み捨て。</summary>
    public Post? ParseThreadLine(string line)
        => CgiBbsFormats.ParseNumberedLine(line, dateHasId: true);

    // -----------------------------------------------------------------
    // 書き込み
    // -----------------------------------------------------------------

    /// <summary>名前 / メール / スレ立てあり。どんぐり認証は無い。</summary>
    public PostFormSpec PostForm { get; } = new(SupportsName: true, SupportsMail: true, SupportsNewThread: true, UsesDonguriAuth: false);

    /// <summary><c>https://machi.to/bbs/write.cgi</c> へのフォーム (Shift_JIS)。レス / スレ立てとも同じエンドポイント。
    /// フィールド: <c>BBS, [KEY], TIME, NAME, MAIL, MESSAGE, [SUBJECT], submit</c>。Referer はレスならスレページ、スレ立てなら板 URL。
    /// (write.cgi のフィールド名は実測ではなく既存クライアントの慣習に基づく。)</summary>
    public PostSubmission BuildPostSubmission(PostRequest req)
    {
        var endpoint = PostEndpointUrl(req.Board)!;
        var fields = new List<KeyValuePair<string, string>>
        {
            new("BBS", req.Board.DirectoryName),
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
        return new PostSubmission(new Uri(endpoint), fields, TextEncoding, "Shift_JIS", referer);
    }

    public PostResult ClassifyPostResponse(string html, PostResponseContext context)
        => PostResponseHtml.ClassifyGeneric(html, context);
}
