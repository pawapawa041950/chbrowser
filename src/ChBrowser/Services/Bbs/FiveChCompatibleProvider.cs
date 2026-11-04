using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using ChBrowser.Models;
using ChBrowser.Services.Url;

namespace ChBrowser.Services.Bbs;

/// <summary>5ch 互換掲示板 (5ch と同じ URL 形式・ファイル形式・書き込みフォームを持つ掲示板) の共通部分。
/// 板の URL はその板のホストで組む (板ごとにホストが違ってよい)。派生クラスはホストと板一覧の出どころだけを決める
/// (<see cref="KenmoProvider"/> = 決まったホスト + bbsmenu.html、<see cref="UserBoardsProvider"/> = ユーザが追加した板)。
///
/// <list type="bullet">
/// <item><description>板 URL: <c>https://&lt;host&gt;/&lt;dir&gt;/</c>、スレ URL: <c>https://&lt;host&gt;/test/read.cgi/&lt;dir&gt;/&lt;key&gt;/[&lt;post&gt;]</c>。</description></item>
/// <item><description>スレ一覧 <c>subject.txt</c>、本文 <c>dat/&lt;key&gt;.dat</c> (Range 差分、新着無しは 416)、板情報 <c>SETTING.TXT</c>、
///   書き込み <c>/test/bbs.cgi</c>。Shift_JIS。生 dat をそのまま保存する。</description></item>
/// <item><description>書き込みは 5ch のどんぐりを使わず、サーバが発行する Cookie (確認画面など) を提供者ごとの保管に持ち回る。
///   結果判定は 5ch 互換掲示板の汎用判定 (<see cref="PostResponseHtml.ClassifyGeneric"/>)。</description></item>
/// </list></summary>
public abstract class FiveChCompatibleProvider : IBbsProvider
{
    private static readonly Regex BoardPathRegex = new(
        @"^/(?<dir>[A-Za-z0-9_]+)/?$",
        RegexOptions.Compiled);

    /// <summary>5ch と同じスレ URL のパス。レス番号は /key/ の直後の連続数字。</summary>
    private static readonly Regex ThreadPathRegex = new(
        @"^/test/read\.cgi/(?<dir>[A-Za-z0-9_]+)/(?<key>[0-9]+)(?:/(?<post>[0-9]+))?.*$",
        RegexOptions.Compiled);

    /// <summary>JS 側スレリンク判定の 5ch 形式 (ホストは <see cref="IBbsProvider.HostSuffixes"/> で絞る)。</summary>
    protected const string ReadCgiThreadLinkJsPattern =
        @"^https?:\/\/(?<host>[A-Za-z0-9.-]+)\/test\/read\.cgi\/(?<dir>[A-Za-z0-9_]+)\/(?<key>\d+)(?:\/(?<post>[^/?#]*))?";

    public abstract string Id          { get; }
    public abstract string DisplayName { get; }
    public abstract IReadOnlyList<string> StorageRoots { get; }
    public abstract string DefaultHost { get; }
    public abstract IReadOnlyList<string> HostSuffixes { get; }
    public abstract bool OwnsHost(string host);
    public abstract string? BoardListUrl { get; }
    public abstract string  BoardListCacheExtension { get; }
    public abstract IReadOnlyList<BoardCategory> ParseBoardList(byte[] bytes);

    /// <summary>既定は板一覧をネットワークから取る (<see cref="IBbsProvider.LocalBoardList"/>)。</summary>
    public virtual IReadOnlyList<BoardCategory>? LocalBoardList => null;

    /// <summary>既定は保存ルートをホストの末尾 2 つにする (<see cref="IBbsProvider.StorageRootOf"/>)。</summary>
    public virtual string? StorageRootOf(string host) => null;

    /// <summary>板一覧が空のときの案内 (<see cref="IBbsProvider.BoardListEmptyHint"/>)。既定は共通の「未取得 (板一覧更新)」。</summary>
    public virtual string? BoardListEmptyHint => null;

    public virtual BbsCapabilities Capabilities =>
        BbsCapabilities.BoardList | BbsCapabilities.ThreadList | BbsCapabilities.ThreadFetch |
        BbsCapabilities.DeltaByRange | BbsCapabilities.Posting | BbsCapabilities.ThreadCreation |
        BbsCapabilities.BoardInfo;

    public Encoding TextEncoding => Encoding.GetEncoding(932);

    public int PostNumberDigits => 4;

    public virtual string ThreadLinkJsPattern => ReadCgiThreadLinkJsPattern;

    public IReadOnlyList<AnchorRule> DefaultAnchorRules { get; } = new[]
    {
        new AnchorRule(">>N", @">>\s*(?<spec>\d+(?:\s*-\s*\d+)?(?:\s*[,，、]\s*\d+(?:\s*-\s*\d+)?)*)"),
        new AnchorRule("＞＞N (全角)", @"＞＞\s*(?<spec>\d+(?:\s*-\s*\d+)?(?:\s*[,，、]\s*\d+(?:\s*-\s*\d+)?)*)", Enabled: false),
    };

    /// <summary>板はホストごとに別なので、ホストはそのまま使う (大文字小文字だけ揃える)。</summary>
    public virtual string NormalizeHost(string host) => host.ToLowerInvariant();

    public AddressBarTarget? TryParseUrl(Uri uri, string host)
    {
        var path = uri.AbsolutePath;

        // スレ判定が先 (板の方が短い path にマッチする。板 dir が test の板もあるので /test/read.cgi/… を先に見る)
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

    /// <summary>URL の板 (板 URL、またはスレ URL の板)。5ch 互換の形でなければ null。板を追加するときに入力を解釈する。</summary>
    public static (string Host, string DirectoryName)? ParseBoardLocation(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host)) return null;
        var host = uri.Host.ToLowerInvariant();
        if (ThreadPathRegex.Match(uri.AbsolutePath) is { Success: true } t) return (host, t.Groups["dir"].Value);
        if (BoardPathRegex.Match(uri.AbsolutePath) is { Success: true } b) return (host, b.Groups["dir"].Value);
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

    /// <summary>5ch と同じ生 dat を Range 差分で保存する。</summary>
    public bool UsesNativeDat => true;

    public string ThreadFetchRangeUrl(Board board, string threadKey, long fromNumber)
        => throw new NotSupportedException($"{DisplayName} は番号以降の差分取得 (DeltaByNumber) を持たない。Range で差分を取る。");

    /// <summary>subject.txt は 5ch と同じ <c>key.dat&lt;&gt;title (n)</c>。</summary>
    public IReadOnlyList<ThreadInfo> ParseThreadList(byte[] bytes)
        => FiveChProvider.ParseSubjectTxt(bytes, TextEncoding);

    public Post? ParseThreadLine(string line) => null;

    /// <summary>名前 / メール / スレ立てあり。どんぐりは使わず、サーバが発行する Cookie を提供者ごとの保管に持つ。</summary>
    public PostFormSpec PostForm { get; } = new(
        SupportsName: true, SupportsMail: true, SupportsNewThread: true, UsesDonguriAuth: false, PersistsCookies: true);

    /// <summary>5ch と同じ bbs.cgi のフォーム。Referer は板 URL。</summary>
    public PostSubmission BuildPostSubmission(PostRequest req)
        => FiveChProvider.BuildBbsCgiSubmission(req, PostEndpointUrl(req.Board)!, TextEncoding, BoardUrl(req.Board.Host, req.Board.DirectoryName));

    public PostResult ClassifyPostResponse(string html, PostResponseContext context)
        => PostResponseHtml.ClassifyGeneric(html, context);
}
