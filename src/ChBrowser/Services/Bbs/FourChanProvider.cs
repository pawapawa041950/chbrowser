using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Url;

namespace ChBrowser.Services.Bbs;

/// <summary>4chan (<c>boards.4chan.org</c>)。公式の読み取り API (<c>a.4cdn.org</c>、JSON) を使う (<c>doc/multi-bbs-design.md</c> §8.8)。
///
/// <list type="bullet">
/// <item><description>板一覧 = <c>boards.json</c> (板名は <c>/g/ - Technology</c>)、スレ一覧 = <c>/&lt;板&gt;/catalog.json</c>
///   (題名が無いスレは本文の先頭、数 = 返信数 + 1)、スレ = <c>/&lt;板&gt;/thread/&lt;番号&gt;.json</c> (毎回スレ全体 = スナップショット方式)。</description></item>
/// <item><description>レス番号は 4chan 自身の番号 (板全体で一意・9 桁程度) をそのまま使う (決定 D2)。スレの key はスレ本体の番号。</description></item>
/// <item><description>本文 (<c>com</c>) は HTML → 本文方言 (<see cref="FourChanBodyConverter"/>)。添付画像は本文末尾に URL を置いて画像スロットで出す
///   (<c>i.4cdn.org/&lt;板&gt;/&lt;tim&gt;&lt;ext&gt;</c>、拡張情報にも記録)。</description></item>
/// <item><description>API へは 1 秒以上空けて要求する (4chan の API 規約)。</description></item>
/// <item><description>書き込みは後の段階 (Cloudflare の確認と CAPTCHA があるため、アプリ内ブラウザで行う。決定 D40)。</description></item>
/// </list></summary>
public sealed class FourChanProvider : IBbsProvider, ISnapshotThreadProvider
{
    public const string CanonicalHost = "boards.4chan.org";
    private const string Origin    = "https://" + CanonicalHost;
    private const string ApiOrigin = "https://a.4cdn.org";
    private const string ImgOrigin = "https://i.4cdn.org";

    private static readonly Regex BoardPathRegex  = new(@"^/(?<dir>[a-z0-9]{1,10})(?:/(?:catalog|archive|\d+))?/?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ThreadPathRegex = new(@"^/(?<dir>[a-z0-9]{1,10})/thread/(?<key>\d+)(?:/[^/]*)?/?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PostFragmentRegex = new(@"^#[pq](?<post>\d+)$", RegexOptions.Compiled);

    public string Id          => "4chan";
    public string DisplayName => "4chan";

    public BbsCapabilities Capabilities =>
        BbsCapabilities.BoardList | BbsCapabilities.ThreadList | BbsCapabilities.ThreadFetch | BbsCapabilities.Attachments;

    public Encoding TextEncoding => Encoding.UTF8;
    public IReadOnlyList<string> StorageRoots { get; } = new[] { "4chan.org" };
    public string DefaultHost => CanonicalHost;
    /// <summary>レス番号は板全体で一意の 9 桁程度。</summary>
    public int PostNumberDigits => 10;
    public IReadOnlyList<string> HostSuffixes { get; } = new[] { "4chan.org", "4channel.org" };

    public string ThreadLinkJsPattern
        => @"^https?:\/\/(?<host>boards\.4chan(?:nel)?\.org)\/(?<dir>[a-z0-9]{1,10})\/thread\/(?<key>\d+)(?:\/[^#?\s]*)?(?:#p(?<post>\d+))?";

    /// <summary>4chan の引用は <c>&gt;&gt;番号</c> だけ (範囲・列挙の慣習は無い)。</summary>
    public IReadOnlyList<AnchorRule> DefaultAnchorRules { get; } = new[]
    {
        new AnchorRule(">>N", @">>(?<spec>\d+)", Ranges: false),
    };

    public bool ShowsPostNumbers => true;
    /// <summary>名前はほぼ Anonymous で、ワッチョイの慣習は無い。</summary>
    public bool UsesWatchoi => false;
    /// <summary>カタログは板の生きているスレ全部 = 一覧に無いローカルログはスレ落ち。</summary>
    public bool ThreadListIsComplete => true;
    public ThreadFetchStrategy FetchStrategy => ThreadFetchStrategy.Snapshot;
    public bool UsesNativeDat => false;

    // -----------------------------------------------------------------
    // URL
    // -----------------------------------------------------------------

    public bool OwnsHost(string host)
        => host.Equals("4chan.org", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".4chan.org", StringComparison.OrdinalIgnoreCase)
        || host.Equals("4channel.org", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".4channel.org", StringComparison.OrdinalIgnoreCase);

    /// <summary>旧 <c>boards.4channel.org</c> / <c>www.4chan.org</c> 等はすべて <c>boards.4chan.org</c> に寄せる。</summary>
    public string NormalizeHost(string host) => OwnsHost(host) ? CanonicalHost : host;

    public AddressBarTarget? TryParseUrl(Uri uri, string host)
    {
        var path = uri.AbsolutePath;
        var tm = ThreadPathRegex.Match(path);
        if (tm.Success)
        {
            var fm = PostFragmentRegex.Match(uri.Fragment);
            var post = fm.Success && long.TryParse(fm.Groups["post"].Value, out var n) ? n : 0;
            return new AddressBarTarget(AddressBarTargetKind.Thread, CanonicalHost,
                tm.Groups["dir"].Value.ToLowerInvariant(), tm.Groups["key"].Value, post);
        }
        var bm = BoardPathRegex.Match(path);
        if (bm.Success)
            return new AddressBarTarget(AddressBarTargetKind.Board, CanonicalHost, bm.Groups["dir"].Value.ToLowerInvariant(), "");
        return null;
    }

    public string BoardUrl(string host, string directoryName) => $"{Origin}/{directoryName}/";

    public string ThreadUrl(string host, string directoryName, string threadKey, long postNumber = 0)
        => $"{Origin}/{directoryName}/thread/{threadKey}" + (postNumber > 0 ? $"#p{postNumber}" : "");

    public string ThreadPageUrl(Board board, string threadKey) => ThreadUrl(board.Host, board.DirectoryName, threadKey);

    public string ThreadListUrl(Board board) => $"{ApiOrigin}/{board.DirectoryName}/catalog.json";

    public string ThreadFetchUrl(Board board, string threadKey) => $"{ApiOrigin}/{board.DirectoryName}/thread/{threadKey}.json";

    public string ThreadFetchRangeUrl(Board board, string threadKey, long fromNumber)
        => throw new NotSupportedException("4chan はスナップショット方式 (番号以降の差分取得は無い)。");

    /// <summary>板名は板一覧 (<c>boards.json</c>) から分かるので板情報は取らない。</summary>
    public string? BoardInfoUrl(Board board) => null;

    public string? PostEndpointUrl(Board board) => null;

    public string? BoardListUrl => $"{ApiOrigin}/boards.json";
    public string  BoardListCacheExtension => "json";

    /// <summary>画像の URL (<c>i.4cdn.org/&lt;板&gt;/&lt;tim&gt;&lt;ext&gt;</c>)。</summary>
    public static string ImageUrl(string board, string tim, string ext) => $"{ImgOrigin}/{board}/{tim}{ext}";
    /// <summary>サムネイルの URL (<c>&lt;tim&gt;s.jpg</c>)。</summary>
    public static string ThumbUrl(string board, string tim) => $"{ImgOrigin}/{board}/{tim}s.jpg";

    // -----------------------------------------------------------------
    // 板一覧 / スレ一覧
    // -----------------------------------------------------------------

    private const string BoardListCategoryName = "4chan";

    /// <summary><c>boards.json</c> → 板一覧 (1 カテゴリ)。板名は <c>/g/ - Technology</c>。</summary>
    public IReadOnlyList<BoardCategory> ParseBoardList(byte[] bytes)
    {
        var boards = new List<Board>();
        if (!TryParse(bytes, out var doc)) return Array.Empty<BoardCategory>();
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("boards", out var arr) || arr.ValueKind != JsonValueKind.Array) return Array.Empty<BoardCategory>();
            foreach (var b in arr.EnumerateArray())
            {
                if (Str(b, "board") is not { Length: > 0 } dir) continue;
                var title = WebUtility.HtmlDecode(Str(b, "title") ?? dir);
                boards.Add(new Board(dir, $"/{dir}/ - {title}", BoardUrl(CanonicalHost, dir), BoardListCategoryName, boards.Count + 1));
            }
        }
        return boards.Count == 0 ? Array.Empty<BoardCategory>() : new[] { new BoardCategory(BoardListCategoryName, 1, boards, Id) };
    }

    /// <summary><c>catalog.json</c> (ページの配列 → threads) → スレ一覧。題名が無ければ本文の先頭、数 = 返信数 + 1 (スレ本体の分)。
    /// 固定スレは 📌、書き込めないスレは 🔒 を題名の頭に付ける。</summary>
    public IReadOnlyList<ThreadInfo> ParseThreadList(byte[] bytes)
    {
        var items = new List<ThreadInfo>();
        if (!TryParse(bytes, out var doc)) return items;
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return items;
            foreach (var page in doc.RootElement.EnumerateArray())
            {
                if (!page.TryGetProperty("threads", out var threads) || threads.ValueKind != JsonValueKind.Array) continue;
                foreach (var t in threads.EnumerateArray())
                {
                    if (Long(t, "no") is not long no) continue;
                    var title = Str(t, "sub") is { Length: > 0 } sub ? WebUtility.HtmlDecode(sub).Trim() : FourChanBodyConverter.Snippet(Str(t, "com"));
                    if (title.Length == 0) title = $"No.{no}";
                    if (Flag(t, "closed")) title = "🔒 " + title;
                    if (Flag(t, "sticky")) title = "📌 " + title;
                    items.Add(new ThreadInfo(
                        Key:          no.ToString(CultureInfo.InvariantCulture),
                        Title:        title,
                        PostCount:    (int)(Long(t, "replies") ?? 0) + 1,
                        Order:        items.Count + 1,
                        CreatedEpoch: Long(t, "time")));
                }
            }
        }
        return items;
    }

    // -----------------------------------------------------------------
    // スレ (スナップショット)
    // -----------------------------------------------------------------

    private static readonly SemaphoreSlim ApiGate = new(1, 1);
    private static DateTimeOffset _nextApiRequest = DateTimeOffset.MinValue;
    /// <summary>API 要求の最小間隔 (4chan の API 規約: 1 秒に 1 回まで)。ハーネスでは 0 にする。</summary>
    public static TimeSpan ApiInterval { get; set; } = TimeSpan.FromSeconds(1);

    public async Task<ThreadSnapshot> FetchSnapshotAsync(
        HttpClient http, Board board, string threadKey, SnapshotFetchOptions options, CancellationToken ct)
    {
        var url = ThreadFetchUrl(board, threadKey);
        byte[] bytes;
        await ApiGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _nextApiRequest - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            try
            {
                using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();   // 404 = スレ落ち (呼び出し側が dat 落ちとして扱う)
                bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _nextApiRequest = DateTimeOffset.UtcNow + ApiInterval;
            }
        }
        finally
        {
            ApiGate.Release();
        }
        var posts = ParseThread(bytes, board.DirectoryName);
        if (posts.Count == 0) throw new HttpRequestException("4chan: スレの形式が想定外です (投稿がありません)");
        return new ThreadSnapshot(posts);
    }

    /// <summary><c>thread/&lt;番号&gt;.json</c> (<c>{"posts":[...]}</c>、先頭がスレ本体) → スナップショット。</summary>
    public static List<SnapshotPost> ParseThread(byte[] bytes, string board)
    {
        var posts = new List<SnapshotPost>();
        if (!TryParse(bytes, out var doc)) return posts;
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("posts", out var arr) || arr.ValueKind != JsonValueKind.Array) return posts;
            foreach (var p in arr.EnumerateArray())
                if (ToPost(p, board, isOp: posts.Count == 0) is { } sp) posts.Add(sp);
        }
        return posts;
    }

    private static SnapshotPost? ToPost(JsonElement p, string board, bool isOp)
    {
        if (Long(p, "no") is not long no) return null;
        var time = Long(p, "time") ?? 0;

        // 名前 = name + トリップ。capcode (## Mod 等)・国旗・板独自の旗はメール欄の位置に出す (reddit の OP / MOD と同じ扱い)
        var name = WebUtility.HtmlDecode(Str(p, "name") ?? "Anonymous").Replace("<", "＜");
        if (Str(p, "trip") is { Length: > 0 } trip) name += " " + trip;
        var flags = new List<string>();
        if (Str(p, "capcode") is { Length: > 0 } cap) flags.Add("## " + char.ToUpperInvariant(cap[0]) + cap[1..]);
        if (Str(p, "country_name") is { Length: > 0 } country) flags.Add(country);
        if (Str(p, "flag_name") is { Length: > 0 } flag) flags.Add(flag);
        if (isOp && Flag(p, "sticky")) flags.Add("固定");
        if (isOp && Flag(p, "closed")) flags.Add("ロック");
        if (isOp && Flag(p, "archived")) flags.Add("過去ログ");

        var lines = new List<string>();
        if (!isOp && Str(p, "sub") is { Length: > 0 } rsub) lines.Add("<b>" + WebUtility.HtmlDecode(rsub).Replace("<", "＜") + "</b>");
        var body = FourChanBodyConverter.Convert(Str(p, "com"));
        if (body.Length > 0) lines.Add(body);

        // 添付 (1 レス 1 ファイル)。本文末尾に URL を置く = スレ表示の画像 / 動画スロットに乗る
        List<PostAttachment>? attachments = null;
        if (Flag(p, "filedeleted"))
        {
            lines.Add("[ファイル削除済み]");
        }
        else if (Long(p, "tim") is long tim && Str(p, "ext") is { Length: > 0 } ext)
        {
            var timS = tim.ToString(CultureInfo.InvariantCulture);
            var url  = ImageUrl(board, timS, ext);
            var fileName = WebUtility.HtmlDecode(Str(p, "filename") ?? timS) + ext;
            var kind = ext is ".webm" or ".mp4" ? "video" : ext is ".pdf" or ".swf" ? "file" : "image";
            attachments = new List<PostAttachment>
            {
                new(url, ThumbUrl(board, timS), fileName, Long(p, "fsize"), (int?)Long(p, "w"), (int?)Long(p, "h"), kind),
            };
            lines.Add(url);
        }

        string? threadTitle = null;
        if (isOp)
            threadTitle = Str(p, "sub") is { Length: > 0 } sub ? WebUtility.HtmlDecode(sub).Trim() : FourChanBodyConverter.Snippet(Str(p, "com"));

        var noS = no.ToString(CultureInfo.InvariantCulture);
        return new SnapshotPost(
            ExternalId:       noS,
            ParentExternalId: null,                  // 返信先は本文の >>N (構造上の親は持たない)
            CreatedEpoch:     time,
            Name:             name,
            Mail:             string.Join(" ", flags),
            DateText:         BbsDate.FromEpoch(time),
            Id:               Str(p, "id") ?? "",
            Body:             string.Join("\n", lines),
            ThreadTitle:      threadTitle,
            Ext:              new PostExtra(Attachments: attachments),
            Number:           no);
    }

    // -----------------------------------------------------------------
    // 書き込み (後の段階。決定 D40)
    // -----------------------------------------------------------------

    public PostFormSpec PostForm { get; } = new(SupportsName: false, SupportsMail: false, SupportsNewThread: false, UsesDonguriAuth: false);

    public PostSubmission BuildPostSubmission(PostRequest request)
        => throw new NotSupportedException("4chan の書き込みはまだ対応していません。");

    public PostResult ClassifyPostResponse(string html, PostResponseContext context)
        => new(PostOutcome.UnknownError, "4chan の書き込みはまだ対応していません。", "");

    public Post? ParseThreadLine(string line) => null;

    // -----------------------------------------------------------------
    // JSON 小道具
    // -----------------------------------------------------------------

    private static bool TryParse(byte[] bytes, out JsonDocument doc)
    {
        try { doc = JsonDocument.Parse(bytes); return true; }
        catch (JsonException) { doc = null!; return false; }
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
           && v.TryGetInt64(out var x) ? x : null;

    /// <summary>4chan の真偽値は 0 / 1 の数値。</summary>
    private static bool Flag(JsonElement e, string name) => Long(e, name) is > 0;
}
