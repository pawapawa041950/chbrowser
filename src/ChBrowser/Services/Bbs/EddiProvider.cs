using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChBrowser.Models;
using ChBrowser.Services.Url;

namespace ChBrowser.Services.Bbs;

/// <summary>エッヂ掲示板 (bbs.eddibb.cc、サーバ実装は OSS の Eddist) の提供者。外部仕様は <c>doc/multi-bbs-design.md</c> §2 (2026-09-21 実測)。
///
/// <list type="bullet">
/// <item><description>ホストは <c>bbs.eddibb.cc</c> 単一。板 dir は 1 階層 (<c>liveedge</c> / <c>experiment</c>)。</description></item>
/// <item><description>読み取りは 5ch 互換: <c>&lt;dir&gt;/subject.txt</c>、<c>&lt;dir&gt;/dat/&lt;key&gt;.dat</c> (Range 206 / 416 対応、Shift_JIS)、
///   <c>&lt;dir&gt;/SETTING.TXT</c>。dat 落ちしたスレは <c>&lt;dir&gt;/kako/…</c> へ 302 され、未アーカイブなら 404。</description></item>
/// <item><description>板一覧は <c>/api/boards</c> (JSON: name / board_key / default_name)。</description></item>
/// <item><description>スレページ URL はサイト固有の <c>https://bbs.eddibb.cc/&lt;dir&gt;/&lt;key&gt;</c>。5ch 形式
///   <c>/test/read.cgi/&lt;dir&gt;/&lt;key&gt;/</c> もサーバが同じページへ転送するので両方受理する。</description></item>
/// <item><description>書き込みは <c>/test/bbs.cgi</c> (5ch と同じフィールド、Shift_JIS、確認画面なし)。成功は <c>2ch_X:true</c>、
///   エラーは <c>2ch_X:error</c> + <c>&lt;meta name="error_code" content="E-…"&gt;</c>。</description></item>
/// <item><description>認証: 初回投稿はエラー <c>E-Unauthenticated</c> になり、本文に 6 桁の認証コードと認証ページ URL が入る。
///   同じ応答の <c>Set-Cookie: edge-token</c> をアプリが保存し (<see cref="PostFormSpec.PersistsCookies"/>)、ユーザがブラウザの
///   認証ページでコードを入力すると、以降はその Cookie で書き込める。認証完了画面に出るトークンをメール欄に
///   <c>#トークン</c> として付ける経路もある (<see cref="PostRequest.AuthToken"/>)。</description></item>
/// <item><description>保存ルート <c>data/eddibb.cc/</c> (5ch と同じ生 dat)。</description></item>
/// </list></summary>
public sealed class EddiProvider : IBbsProvider
{
    public const string CanonicalHost = "bbs.eddibb.cc";
    private const string RootDomain   = "eddibb.cc";

    /// <summary>板以外のトップレベルパス (Web UI / API)。板 dir として解釈しない。</summary>
    private static readonly HashSet<string> ReservedTopLevel = new(StringComparer.OrdinalIgnoreCase)
    {
        "test", "api", "auth-code", "re-auth", "notices", "terms", "assets", "health-check", "robots.txt", "user",
    };

    private static readonly Regex BoardPathRegex = new(
        @"^/(?<dir>[A-Za-z0-9_-]+)/?$",
        RegexOptions.Compiled);

    /// <summary>サイト固有 <c>/&lt;dir&gt;/&lt;key&gt;[/&lt;post&gt;]</c> と 5ch 形式 <c>/test/read.cgi/&lt;dir&gt;/&lt;key&gt;/[&lt;post&gt;]</c> の両方。</summary>
    private static readonly Regex ThreadPathRegex = new(
        @"^/(?:test/read\.cgi/)?(?<dir>[A-Za-z0-9_-]+)/(?<key>[0-9]+)(?:/(?<post>[0-9]+))?/?(?:[^/].*)?$",
        RegexOptions.Compiled);

    private static readonly Regex ErrorCodeRegex = new(
        @"<meta\s+name\s*=\s*[""']error_code[""']\s+content\s*=\s*[""']E-(?<code>[A-Za-z]+)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AuthCodeRegex = new(
        @"(?:再認証コード|認証コード)\s*'(?<code>[A-Za-z0-9]{4,12})'",
        RegexOptions.Compiled);

    private static readonly Regex AuthUrlRegex = new(
        @"(?<url>https?://[^\s<>""']+/(?:auth-code|re-auth))",
        RegexOptions.Compiled);

    public string Id          => "eddi";
    public string DisplayName => "エッヂ";

    public BbsCapabilities Capabilities =>
        BbsCapabilities.BoardList | BbsCapabilities.ThreadList | BbsCapabilities.ThreadFetch |
        BbsCapabilities.DeltaByRange | BbsCapabilities.Posting | BbsCapabilities.ThreadCreation |
        BbsCapabilities.Auth | BbsCapabilities.BoardInfo;

    public Encoding TextEncoding => Encoding.GetEncoding(932);

    public IReadOnlyList<string> StorageRoots { get; } = new[] { RootDomain };

    public string DefaultHost => CanonicalHost;

    public int PostNumberDigits => 4;

    public IReadOnlyList<string> HostSuffixes { get; } = new[] { RootDomain };

    /// <summary>JS 側スレリンク判定。サイト固有形式と 5ch 形式の両方。板 URL (key 無し) や dat / kako の URL には当たらない。</summary>
    public string ThreadLinkJsPattern
        => @"^https?:\/\/(?<host>(?:[A-Za-z0-9-]+\.)*eddibb\.cc)\/(?:test\/read\.cgi\/)?(?<dir>[A-Za-z0-9_-]+)\/(?<key>\d+)(?:\/(?<post>[^/?#]*))?";

    public IReadOnlyList<AnchorRule> DefaultAnchorRules { get; } = new[]
    {
        new AnchorRule(">>N", @">>\s*(?<spec>\d+(?:\s*-\s*\d+)?(?:\s*[,，、]\s*\d+(?:\s*-\s*\d+)?)*)"),
        new AnchorRule("＞＞N (全角)", @"＞＞\s*(?<spec>\d+(?:\s*-\s*\d+)?(?:\s*[,，、]\s*\d+(?:\s*-\s*\d+)?)*)", Enabled: false),
    };

    public bool OwnsHost(string host)
        => string.Equals(host, RootDomain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + RootDomain, StringComparison.OrdinalIgnoreCase);

    /// <summary>保存ルート名 (<c>eddibb.cc</c>) だけしか無い場面でも正規ホストへ寄せる。</summary>
    public string NormalizeHost(string host)
        => OwnsHost(host) ? CanonicalHost : host;

    public AddressBarTarget? TryParseUrl(Uri uri, string host)
    {
        var path = uri.AbsolutePath;

        var threadMatch = ThreadPathRegex.Match(path);
        if (threadMatch.Success && !ReservedTopLevel.Contains(threadMatch.Groups["dir"].Value))
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
            if (ReservedTopLevel.Contains(dir)) return null;
            return new AddressBarTarget(AddressBarTargetKind.Board, host, dir, "");
        }

        return null;
    }

    public string BoardUrl(string host, string directoryName)
        => $"https://{CanonicalHost}/{directoryName}/";

    /// <summary>サイトが表示する形式に合わせる (<c>/liveedge/1789955799</c>)。レス番号付きは <c>/&lt;key&gt;/&lt;post&gt;</c>。</summary>
    public string ThreadUrl(string host, string directoryName, string threadKey, long postNumber = 0)
        => postNumber > 0
            ? $"https://{CanonicalHost}/{directoryName}/{threadKey}/{postNumber}"
            : $"https://{CanonicalHost}/{directoryName}/{threadKey}";

    public string ThreadListUrl(Board board)                => $"https://{CanonicalHost}/{board.DirectoryName}/subject.txt";
    public string ThreadFetchUrl(Board board, string key)   => $"https://{CanonicalHost}/{board.DirectoryName}/dat/{key}.dat";
    public string ThreadPageUrl(Board board, string key)    => ThreadUrl(board.Host, board.DirectoryName, key);
    public string? BoardInfoUrl(Board board)                => $"https://{CanonicalHost}/{board.DirectoryName}/SETTING.TXT";
    public string? PostEndpointUrl(Board board)             => $"https://{CanonicalHost}/test/bbs.cgi";

    public string? BoardListUrl => $"https://{CanonicalHost}/api/boards";
    public string  BoardListCacheExtension => "json";

    private const string BoardListCategoryName = "エッヂ掲示板";

    /// <summary><c>/api/boards</c>: <c>[{"name":"エッヂ","board_key":"liveedge","default_name":"…"}, …]</c>。1 カテゴリにまとめる。</summary>
    public IReadOnlyList<BoardCategory> ParseBoardList(byte[] bytes)
    {
        var boards = new List<Board>();
        using var doc = JsonDocument.Parse(bytes);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<BoardCategory>();
        var order = 0;
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var key  = el.TryGetProperty("board_key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
            var name = el.TryGetProperty("name",      out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            if (string.IsNullOrEmpty(key)) continue;
            if (string.IsNullOrEmpty(name)) name = key;
            boards.Add(new Board(key, name, BoardUrl(CanonicalHost, key), BoardListCategoryName, ++order));
        }
        if (boards.Count == 0) return Array.Empty<BoardCategory>();
        return new[] { new BoardCategory(BoardListCategoryName, 1, boards, Id) };
    }

    /// <summary>5ch と同じ生 dat を Range 差分で保存する。</summary>
    public bool UsesNativeDat => true;

    public string ThreadFetchRangeUrl(Board board, string threadKey, long fromNumber)
        => throw new NotSupportedException("エッヂは番号以降の差分取得 (DeltaByNumber) を持たない。Range で差分を取る。");

    /// <summary>subject.txt は 5ch と同じ <c>key.dat&lt;&gt;title (n)</c>。</summary>
    public IReadOnlyList<ThreadInfo> ParseThreadList(byte[] bytes)
        => FiveChProvider.ParseSubjectTxt(bytes, TextEncoding);

    public Post? ParseThreadLine(string line) => null;

    // -----------------------------------------------------------------
    // 書き込み
    // -----------------------------------------------------------------

    /// <summary>名前 / メール / スレ立てあり。どんぐりは使わず、サーバが発行する Cookie (edge-token / tinker-token) を
    /// 提供者ごとの Cookie 保管に持つ。<c>edge-token</c> があれば手入力トークンは不要。</summary>
    public PostFormSpec PostForm { get; } = new(
        SupportsName: true, SupportsMail: true, SupportsNewThread: true, UsesDonguriAuth: false,
        PersistsCookies: true, AuthCookieName: "edge-token");

    /// <summary>bbs.cgi へのフォーム: <c>FROM, mail, MESSAGE, bbs, time, [key], [subject], submit</c>。Shift_JIS。
    /// <see cref="PostRequest.AuthToken"/> があればメール欄を <c>mail#トークン</c> にする (サーバは '#' 以降を認証トークンとして読み、表示には残さない)。</summary>
    public PostSubmission BuildPostSubmission(PostRequest req)
    {
        var endpoint = PostEndpointUrl(req.Board)!;
        var mail = req.Mail;
        if (!string.IsNullOrWhiteSpace(req.AuthToken))
            mail = $"{req.Mail}#{req.AuthToken.Trim()}";

        var fields = new List<KeyValuePair<string, string>>
        {
            new("FROM",    req.Name),
            new("mail",    mail),
            new("MESSAGE", req.Message),
            new("bbs",     req.Board.DirectoryName),
            new("time",    PostResponseHtml.UnixNow()),
        };
        if (req.IsReply)     fields.Add(new("key",     req.ThreadKey!));
        if (req.IsNewThread) fields.Add(new("subject", req.Subject!));
        fields.Add(new("submit", req.IsNewThread ? "新規スレッド作成" : "書き込む"));

        var referer = req.IsReply
            ? ThreadUrl(req.Board.Host, req.Board.DirectoryName, req.ThreadKey!)
            : BoardUrl(req.Board.Host, req.Board.DirectoryName);
        return new PostSubmission(new Uri(endpoint), fields, TextEncoding, "Shift_JIS", referer);
    }

    /// <summary>エラー種別が「規制・制限」に当たるもの (それ以外は未分類エラーとして文言をそのまま見せる)。</summary>
    private static readonly HashSet<string> RuleErrorCodes = new(StringComparer.Ordinal)
    {
        "NgWordDetected", "ImageUrlBelowLv2", "TooManyCreatingRes", "TooManyCreatingThread",
        "TooManyCreatingThreadWithoutTinker", "ResCreationSpanRestriction", "ReadOnlyBoard",
        "TemporarilySuspended", "InactiveThread", "TmpCanNotCreateThread", "RevokedAuthedToken",
        "EmailAuthenticatedUnsupportedUserAgent",
    };

    /// <summary>Eddist の応答は機械判定できる印を持つ: 成功 <c>2ch_X:true</c>、エラー <c>2ch_X:error</c> と
    /// <c>error_code</c> メタ。認証要求 (<c>Unauthenticated</c> / <c>ReAuthRequired</c>) は本文の認証コードと URL を
    /// <see cref="PostResult.AuthCode"/> / <see cref="PostResult.AuthUrl"/> に載せる。</summary>
    public PostResult ClassifyPostResponse(string html, PostResponseContext context)
    {
        var bodyText = PostResponseHtml.ExtractBodyText(html);
        var snippet  = PostResponseHtml.Snippet(bodyText);

        if (html.Contains("2ch_X:true", StringComparison.Ordinal) ||
            PostResponseHtml.Contains(html, PostResponseHtml.SuccessTokens))
            return new PostResult(PostOutcome.Success, "", snippet);

        var codeMatch = ErrorCodeRegex.Match(html);
        var code      = codeMatch.Success ? codeMatch.Groups["code"].Value : "";
        var message   = CleanErrorText(bodyText);

        if (code is "Unauthenticated" or "ReAuthRequired")
        {
            var authCode = AuthCodeRegex.Match(bodyText) is { Success: true } ac ? ac.Groups["code"].Value : "";
            var authUrl  = AuthUrlRegex.Match(bodyText)  is { Success: true } au ? au.Groups["url"].Value
                         : $"https://{CanonicalHost}/{(code == "ReAuthRequired" ? "re-auth" : "auth-code")}";
            return new PostResult(PostOutcome.AuthRequired, message, snippet, AuthUrl: authUrl, AuthCode: authCode);
        }

        if (code.Length > 0 || html.Contains("2ch_X:error", StringComparison.Ordinal))
        {
            var outcome = RuleErrorCodes.Contains(code) ? PostOutcome.BlockedByRule : PostOutcome.UnknownError;
            return new PostResult(outcome, message, snippet);
        }

        if (context.StatusCode >= 400)
            return new PostResult(PostOutcome.UnknownError, $"HTTP {context.StatusCode}: {PostResponseHtml.ShortenForUser(bodyText)}", snippet);

        return new PostResult(PostOutcome.UnknownError, PostResponseHtml.ShortenForUser(bodyText), snippet);
    }

    /// <summary>Eddist のエラー本文は「エラー！ &lt;理由&gt;」なので先頭の「エラー！」を落として理由だけにする。</summary>
    private static string CleanErrorText(string bodyText)
    {
        var s = bodyText.Trim();
        if (s.StartsWith("エラー！", StringComparison.Ordinal)) s = s["エラー！".Length..].Trim();
        else if (s.StartsWith("エラー!", StringComparison.Ordinal)) s = s["エラー!".Length..].Trim();
        return PostResponseHtml.ShortenForUser(s);
    }
}
