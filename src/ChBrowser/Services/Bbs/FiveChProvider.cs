using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ChBrowser.Models;
using ChBrowser.Services.Url;

namespace ChBrowser.Services.Bbs;

/// <summary>5ch.io / bbspink.com の提供者。既存の 5ch 直書きロジック (URL 形式・エンコーディング・保存ルート) を
/// そのまま移したもので、動作は従来と同一。
///
/// <list type="bullet">
/// <item><description>板 URL: <c>https://&lt;host&gt;/&lt;dir&gt;/</c>、スレ URL: <c>https://&lt;host&gt;/test/read.cgi/&lt;dir&gt;/&lt;key&gt;/[&lt;post&gt;]</c></description></item>
/// <item><description>スレ一覧 <c>subject.txt</c>、本文 <c>dat/&lt;key&gt;.dat</c> (Range 差分)、板設定 <c>SETTING.TXT</c>、投稿 <c>/test/bbs.cgi</c></description></item>
/// <item><description>Shift_JIS。保存ルートは <c>data/5ch.io/</c> と <c>data/bbspink.com/</c>。</description></item>
/// <item><description>旧ホスト <c>*.5ch.net</c> は <c>*.5ch.io</c> に正規化して受理する。</description></item>
/// </list></summary>
public sealed class FiveChProvider : IBbsProvider
{
    private static readonly Regex BoardPathRegex = new(
        @"^/(?<dir>[A-Za-z0-9]+)/?$",
        RegexOptions.Compiled);

    /// <summary>スレ URL のパス。レス番号 (= /key/ の直後の連続数字) を任意マッチ。
    /// 「/100」「/100/」「/100-150」「/100n」のいずれも先頭の数字だけ post グループに入る。</summary>
    private static readonly Regex ThreadPathRegex = new(
        @"^/test/read\.cgi/(?<dir>[A-Za-z0-9]+)/(?<key>[0-9]+)(?:/(?<post>[0-9]+))?.*$",
        RegexOptions.Compiled);

    /// <summary>subject.txt の 1 行: <c>&lt;key&gt;.dat&lt;&gt;&lt;title&gt; (&lt;post_count&gt;)</c>。旧 SubjectTxtClient から移設 (動作同一)。</summary>
    private static readonly Regex SubjectLineRegex = new(
        @"^(?<key>\d+)\.dat<>(?<title>.+?)\s*\((?<count>\d+)\)\s*$",
        RegexOptions.Compiled);

    public string Id          => "5ch";
    public string DisplayName => "5ch";

    public BbsCapabilities Capabilities =>
        BbsCapabilities.BoardList | BbsCapabilities.ThreadList | BbsCapabilities.ThreadFetch |
        BbsCapabilities.DeltaByRange | BbsCapabilities.Posting | BbsCapabilities.ThreadCreation |
        BbsCapabilities.Auth | BbsCapabilities.BoardInfo;

    public Encoding TextEncoding => Encoding.GetEncoding(932);

    public IReadOnlyList<string> StorageRoots { get; } = new[] { "5ch.io", "bbspink.com" };

    public string DefaultHost => "5ch.io";

    public int PostNumberDigits => 4;

    public IReadOnlyList<string> HostSuffixes { get; } = new[] { "5ch.io", "5ch.net", "bbspink.com" };

    /// <summary>JS 側の旧 FIVECH_THREAD_RE と同じ: /test/read.cgi/&lt;dir&gt;/&lt;key&gt;(/&lt;postSpec&gt;)?</summary>
    public string ThreadLinkJsPattern
        => @"^https?:\/\/(?<host>[A-Za-z0-9.-]+)\/test\/read\.cgi\/(?<dir>[A-Za-z0-9]+)\/(?<key>\d+)(?:\/(?<post>[^/?#]*))?";

    /// <summary>従来の判定と同一: <c>&gt;&gt;N</c> (範囲 N-M・カンマリスト可、">>" と番号の間の空白可)。
    /// 全角 <c>＞＞N</c> は従来判定に含まれていなかったので既定では無効にしてある (設定で有効化できる)。</summary>
    public IReadOnlyList<AnchorRule> DefaultAnchorRules { get; } = new[]
    {
        new AnchorRule(">>N", @">>\s*(?<spec>\d+(?:\s*-\s*\d+)?(?:\s*[,，、]\s*\d+(?:\s*-\s*\d+)?)*)"),
        new AnchorRule("＞＞N (全角)", @"＞＞\s*(?<spec>\d+(?:\s*-\s*\d+)?(?:\s*[,，、]\s*\d+(?:\s*-\s*\d+)?)*)", Enabled: false),
    };

    public bool OwnsHost(string host)
        =>     Eq(host, "5ch.io")  || EndsWith(host, ".5ch.io")
           ||  Eq(host, "5ch.net") || EndsWith(host, ".5ch.net")
           ||  Eq(host, "bbspink.com") || EndsWith(host, ".bbspink.com");

    /// <summary>5ch.net → 5ch.io 書き換え (host suffix のみ)。古い URL の貼り付け救済。</summary>
    public string NormalizeHost(string host)
    {
        if (Eq(host, "5ch.net")) return "5ch.io";
        if (EndsWith(host, ".5ch.net")) return host[..^".5ch.net".Length] + ".5ch.io";
        return host;
    }

    public AddressBarTarget? TryParseUrl(Uri uri, string host)
    {
        var path = uri.AbsolutePath;

        // スレ判定が先 (= /test/read.cgi/<dir>/<key>/)。Board の方が短い path にマッチするので後判定。
        var threadMatch = ThreadPathRegex.Match(path);
        if (threadMatch.Success)
        {
            var postGroup = threadMatch.Groups["post"];
            var postNo    = postGroup.Success && long.TryParse(postGroup.Value, out var n) ? n : 0;
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

        return null;
    }

    public string BoardUrl(string host, string directoryName)
        => $"https://{host}/{directoryName}/";

    public string ThreadUrl(string host, string directoryName, string threadKey, long postNumber = 0)
        => postNumber > 0
            ? $"https://{host}/test/read.cgi/{directoryName}/{threadKey}/{postNumber}"
            : $"https://{host}/test/read.cgi/{directoryName}/{threadKey}/";

    // board.Url は末尾 '/' 付き想定 (例: "https://hayabusa9.5ch.io/news/")
    public string ThreadListUrl(Board board)                => board.Url.TrimEnd('/') + "/subject.txt";
    public string ThreadFetchUrl(Board board, string key)   => $"{board.Url.TrimEnd('/')}/dat/{key}.dat";
    public string ThreadPageUrl(Board board, string key)    => ThreadUrl(board.Host, board.DirectoryName, key);
    public string? BoardInfoUrl(Board board)                => board.Url.TrimEnd('/') + "/SETTING.TXT";

    /// <summary>Board.Url は "https://hayabusa9.5ch.io/news/" なので bbs.cgi はホストルート直下の test/bbs.cgi。</summary>
    public string? PostEndpointUrl(Board board)
        => new Uri(new Uri(board.Url), "/test/bbs.cgi").ToString();

    public string? BoardListUrl => "https://menu.5ch.io/bbsmenu.json";
    public string  BoardListCacheExtension => "json";
    public IReadOnlyList<BoardCategory> ParseBoardList(byte[] bytes) => Api.BbsmenuClient.ParseBbsmenuJson(bytes);

    /// <summary>5ch は生 dat を Range 差分で追記保存する (行番号 = レス番号)。</summary>
    public bool UsesNativeDat => true;

    /// <summary>5ch の差分は HTTP Range (バイト) で、番号以降取得は無い。</summary>
    public string ThreadFetchRangeUrl(Board board, string threadKey, long fromNumber)
        => throw new NotSupportedException("5ch は番号以降の差分取得 (DeltaByNumber) を持たない。Range で差分を取る。");

    /// <summary>Shift_JIS の subject.txt をパース。SJIS に無い文字 (絵文字等) は <c>&amp;#xXXXX;</c> で来るので HtmlDecode する。</summary>
    public IReadOnlyList<ThreadInfo> ParseThreadList(byte[] bytes)
        => ParseSubjectTxt(bytes, TextEncoding);

    /// <summary>5ch 形式 subject.txt のパーサ本体。同形式のエッヂ (<see cref="EddiProvider"/>) からも使う。</summary>
    internal static IReadOnlyList<ThreadInfo> ParseSubjectTxt(byte[] bytes, Encoding encoding)
    {
        var text  = encoding.GetString(bytes);
        var lines = text.Split('\n');
        var list  = new List<ThreadInfo>(lines.Length);

        var order = 0;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrEmpty(line)) continue;

            var m = SubjectLineRegex.Match(line);
            if (!m.Success) continue;

            order++;
            list.Add(new ThreadInfo(
                Key:       m.Groups["key"].Value,
                Title:     WebUtility.HtmlDecode(m.Groups["title"].Value),
                PostCount: int.Parse(m.Groups["count"].Value),
                Order:     order));
        }
        return list;
    }

    /// <summary>5ch の dat は行番号 = レス番号なので 1 行単独では番号が決まらない。<see cref="Api.DatParser.ParseLine"/> を使う。</summary>
    public Post? ParseThreadLine(string line) => null;

    // -----------------------------------------------------------------
    // 書き込み (旧 PostClient の 5ch 直書きロジックをそのまま移したもの。バイト列・判定は従来と同一)
    // -----------------------------------------------------------------

    /// <summary>名前 / メール / スレ立てあり、どんぐり Cookie を使う。</summary>
    public PostFormSpec PostForm { get; } = new(SupportsName: true, SupportsMail: true, SupportsNewThread: true, UsesDonguriAuth: true);

    /// <summary>bbs.cgi へのフォーム: <c>FROM, mail, MESSAGE, bbs, time, [key], [subject], submit</c> (この順)。Shift_JIS。Referer は板 URL。</summary>
    public PostSubmission BuildPostSubmission(PostRequest req)
    {
        var endpoint = PostEndpointUrl(req.Board)!;
        var fields = new List<KeyValuePair<string, string>>
        {
            new("FROM",    req.Name),
            new("mail",    req.Mail),
            new("MESSAGE", req.Message),
            new("bbs",     req.Board.DirectoryName),
            new("time",    PostResponseHtml.UnixNow()),
        };
        if (req.IsReply)     fields.Add(new("key",     req.ThreadKey!));
        if (req.IsNewThread) fields.Add(new("subject", req.Subject!));
        fields.Add(new("submit", req.IsNewThread ? "新規スレッド作成" : "書き込む"));
        return new PostSubmission(new Uri(endpoint), fields, TextEncoding, "Shift_JIS", req.Board.Url);
    }

    /// <summary>HTML 全文から PostOutcome を推定する。判定は 5ch サーバの定文言マッチで、誤判定の可能性ありなので
    /// 抜粋を <see cref="PostResult.RawHtmlSnippet"/> に同梱して呼び出し側で目視できるようにしておく。</summary>
    public PostResult ClassifyPostResponse(string html, PostResponseContext context)
    {
        var bodyText = PostResponseHtml.ExtractBodyText(html);
        var snippet  = PostResponseHtml.Snippet(bodyText);

        if (PostResponseHtml.Contains(html, PostResponseHtml.SuccessTokens))
            return new PostResult(PostOutcome.Success, "", snippet);

        // broken_acorn は HTML の id/class 名で出ることが多い
        if (html.Contains("broken_acorn", StringComparison.OrdinalIgnoreCase) ||
            bodyText.Contains("どんぐりが壊れています"))
            return new PostResult(PostOutcome.BrokenAcorn, "どんぐりが壊れています。再取得します。", snippet);

        if (bodyText.Contains("レベル") && (bodyText.Contains("不足") || bodyText.Contains("足りません")))
            return new PostResult(PostOutcome.LevelInsufficient, "どんぐりレベルが不足しています。", snippet);

        if (bodyText.Contains("規制") || bodyText.Contains("ERROR") || bodyText.Contains("Forbidden") ||
            bodyText.Contains("書き込めません"))
            return new PostResult(PostOutcome.BlockedByRule, PostResponseHtml.ShortenForUser(bodyText), snippet);

        if (PostResponseHtml.Contains(html, PostResponseHtml.ConfirmTokens))
            return new PostResult(PostOutcome.NeedsConfirm, "", snippet);

        // 何も該当しなければ未分類エラー扱い (後続で Outcome 増やす余地あり)
        return new PostResult(PostOutcome.UnknownError, PostResponseHtml.ShortenForUser(bodyText), snippet);
    }

    private static bool Eq(string a, string b)         => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool EndsWith(string a, string suf) => a.EndsWith(suf, StringComparison.OrdinalIgnoreCase);
}
