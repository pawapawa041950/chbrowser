using System;
using System.Collections.Concurrent;
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

/// <summary>ふたば☆ちゃんねる (<c>*.2chan.net</c>) (<c>doc/multi-bbs-design.md</c> §8.8)。
///
/// <list type="bullet">
/// <item><description>板一覧 = <c>www.2chan.net/bbsmenu.html</c> (Shift_JIS)。板ごとにサーバが違う (<c>may</c> / <c>dat</c> / <c>dec</c> …) が、
///   板名 (dir) はサーバをまたいで重複しないので、保存は <c>data/2chan.net/&lt;板&gt;/</c>。板名 → サーバの対応表を持ち、
///   サーバが分からないとき (板一覧に無い取得済み板等) に引く。</description></item>
/// <item><description>スレ一覧 = カタログ (<c>futaba.php?mode=cat</c>、Shift_JIS の HTML)。Cookie <c>cxyl</c> で 15×10 = 150 スレ・本文 40 文字にする。
///   並び順 (<c>sort=</c>) を選べる。カタログは板の全スレではない (多い板は 150 を超える) ので、一覧に無いログをスレ落ちとはみなさない。</description></item>
/// <item><description>スレ = スナップショット方式。初回はスレページ (<c>res/&lt;番号&gt;.htm</c>) からスレ本体を読み、返信は JSON
///   (<c>futaba.php?mode=json&amp;res=&lt;番号&gt;</c>)。2 回目からは既知の最大番号の次から (<c>&amp;start=</c>) の差分だけを取る。
///   スレが無ければ 404 (スレページ) / 「res 無し・消える日時が 1970 年代」(JSON) で、スレ落ちとして扱う。</description></item>
/// <item><description>レス番号はふたば自身の番号 (板全体で一意)。日時は画像の時刻 (<c>tim</c>、ミリ秒) から作り、IP 表示は日時の後ろ、ID 表示は ID 欄。
///   そうだね の数は評価値 (<see cref="PostExtra.Score"/>)。削除依頼で消えた記事は本文の頭に「[削除された記事]」を付けて残す。</description></item>
/// <item><description>アンカーは番号指定 (<c>&gt;&gt;N</c>・<c>&gt;No.N</c>) と添付ファイル名 (<c>&gt;1750599124357.jpg</c>) だけ (決定 D44)。</description></item>
/// <item><description>要求は 1 秒以上空ける。</description></item>
/// <item><description>書き込みは 4chan と同じ投稿窓 (決定 D43、<see cref="IBrowserPostProvider"/>)。結果はページの XHR の応答 / 遷移先を差し込んだ JS で拾う。
///   そうだね は <c>GET /sd.php?板.番号</c> (決定 D45)。</description></item>
/// </list></summary>
public sealed class FutabaProvider : IBbsProvider, ISnapshotThreadProvider, IBrowserPostProvider
{
    public const string RootDomain = "2chan.net";
    private const string MenuUrl   = "https://www.2chan.net/bbsmenu.html";
    /// <summary>カタログの表示設定 (列 15 × 行 10 = 150 スレ、本文 40 文字)。</summary>
    public const string CatalogCookie = "cxyl=15x10x40x0x0";

    private static readonly Encoding Sjis;
    static FutabaProvider()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Sjis = Encoding.GetEncoding(932);
    }

    public string Id          => "futaba";
    public string DisplayName => "ふたば";

    public BbsCapabilities Capabilities =>
        BbsCapabilities.BoardList | BbsCapabilities.ThreadList | BbsCapabilities.ThreadFetch | BbsCapabilities.Attachments
        | BbsCapabilities.Posting | BbsCapabilities.ThreadCreation;

    public Encoding TextEncoding => Sjis;
    public IReadOnlyList<string> StorageRoots { get; } = new[] { RootDomain };
    public string DefaultHost => "may." + RootDomain;
    /// <summary>レス番号は板全体で一意 (二次元裏で 10 桁)。</summary>
    public int PostNumberDigits => 10;
    public IReadOnlyList<string> HostSuffixes { get; } = new[] { RootDomain };

    public string ThreadLinkJsPattern
        => @"^https?:\/\/(?<host>[a-z0-9]+\.2chan\.net)\/(?<dir>[a-z0-9]+)\/res\/(?<key>\d+)\.htm";

    /// <summary>番号指定 (<c>&gt;&gt;N</c>・<c>&gt;No.N</c>) と添付ファイル名。ふたばには範囲指定の慣習は無い。
    /// 本文での引用 (<c>&gt;引用文</c>) から元のレスを探すことはしない (決定 D44)。</summary>
    public IReadOnlyList<AnchorRule> DefaultAnchorRules { get; } = new[]
    {
        new AnchorRule(">>N",      @">>(?<spec>\d+)", Ranges: false),
        new AnchorRule(">No.N",    @">No\.(?<spec>\d+)", Ranges: false),
        new AnchorRule(">ファイル名", @">(?<spec>\d{10,}\.(?:jpg|jpeg|png|gif|webp|webm|mp4))", AnchorRule.KindAttachment, Ranges: false),
    };

    public bool ShowsPostNumbers => true;
    /// <summary>名前はほぼ「としあき」等の既定名で、ワッチョイの慣習は無い。</summary>
    public bool UsesWatchoi => false;
    /// <summary>カタログは板の全スレではない (150 を超える板がある) ので、一覧に無いログをスレ落ちとはみなさない。</summary>
    public bool ThreadListIsComplete => false;
    public ThreadFetchStrategy FetchStrategy => ThreadFetchStrategy.Snapshot;
    public bool UsesNativeDat => false;

    // -----------------------------------------------------------------
    // 板名 → サーバ
    // -----------------------------------------------------------------

    /// <summary>板名 → サーバ (<c>may.2chan.net</c>)。板一覧の取得で更新する。初期値は 2026-09-26 の板一覧。</summary>
    private static readonly ConcurrentDictionary<string, string> ServerByDir = new(new Dictionary<string, string>
    {
        ["84"] = "dec", ["hinan"] = "www", ["1"] = "zip", ["12"] = "zip", ["25"] = "may", ["26"] = "may", ["27"] = "may", ["d"] = "dat",
        ["z"] = "zip", ["w"] = "dat", ["49"] = "dat", ["62"] = "dec", ["t"] = "dat", ["20"] = "dat", ["21"] = "dat", ["e"] = "dat",
        ["j"] = "dat", ["37"] = "nov", ["45"] = "dat", ["48"] = "dat", ["r"] = "dat", ["img2"] = "dat", ["dec"] = "dec", ["jun"] = "jun",
        ["b"] = "may", ["58"] = "dec", ["59"] = "dec", ["id"] = "may", ["23"] = "dat", ["16"] = "dat", ["43"] = "dat", ["74"] = "dec",
        ["75"] = "dec", ["86"] = "dec", ["78"] = "dec", ["31"] = "jun", ["28"] = "nov", ["56"] = "dec", ["60"] = "dec", ["69"] = "dec",
        ["65"] = "dec", ["64"] = "dec", ["66"] = "dec", ["67"] = "dec", ["68"] = "dec", ["webm"] = "may", ["71"] = "dec", ["82"] = "dec",
        ["61"] = "dec", ["10"] = "dat", ["34"] = "nov", ["11"] = "zip", ["14"] = "zip", ["32"] = "zip", ["15"] = "zip", ["7"] = "zip",
        ["8"] = "zip", ["o"] = "cgi", ["51"] = "jun", ["5"] = "zip", ["3"] = "zip", ["g"] = "cgi", ["2"] = "zip", ["63"] = "dec",
        ["44"] = "dat", ["v"] = "dat", ["y"] = "nov", ["47"] = "jun", ["73"] = "dec", ["81"] = "dec", ["x"] = "dat", ["85"] = "dec",
        ["35"] = "nov", ["36"] = "nov", ["79"] = "dec", ["50"] = "dec", ["f"] = "cgi", ["39"] = "may", ["m"] = "cgi", ["i"] = "cgi",
        ["k"] = "cgi", ["l"] = "dat", ["40"] = "may", ["55"] = "dec", ["p"] = "zip", ["q"] = "nov", ["u"] = "cgi", ["6"] = "zip",
        ["76"] = "dec", ["77"] = "dec", ["53"] = "dec", ["52"] = "dec", ["83"] = "dec", ["9"] = "img", ["70"] = "dec", ["54"] = "ipv6",
        ["layout"] = "may", ["oe"] = "jun", ["72"] = "jun", ["junbi"] = "jun",
    }.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value + "." + RootDomain)), StringComparer.OrdinalIgnoreCase);

    /// <summary>板のサーバ。<paramref name="host"/> がサーバ (<c>may.2chan.net</c> 等) ならそのまま、
    /// 分からない (<c>2chan.net</c> / 空) なら対応表から引く (無ければ既定の <see cref="DefaultHost"/>)。</summary>
    public string ServerOf(string? host, string directoryName)
    {
        if (!string.IsNullOrEmpty(host) && host.EndsWith("." + RootDomain, StringComparison.OrdinalIgnoreCase)
            && !host.Equals("www." + RootDomain, StringComparison.OrdinalIgnoreCase))
            return host.ToLowerInvariant();
        return ServerByDir.TryGetValue(directoryName, out var s) ? s : DefaultHost;
    }

    // -----------------------------------------------------------------
    // URL
    // -----------------------------------------------------------------

    private static readonly Regex ThreadPathRegex = new(@"^/(?<dir>[a-z0-9]+)/res/(?<key>\d+)\.htm$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BoardPathRegex  = new(@"^/(?<dir>[a-z0-9]+)/(?:(?:futaba|\d+)\.htm|futaba\.php)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public bool OwnsHost(string host)
        => host.Equals(RootDomain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + RootDomain, StringComparison.OrdinalIgnoreCase);

    public string NormalizeHost(string host) => OwnsHost(host) ? host.ToLowerInvariant() : host;

    public AddressBarTarget? TryParseUrl(Uri uri, string host)
    {
        var path = uri.AbsolutePath;
        var tm = ThreadPathRegex.Match(path);
        if (tm.Success)
        {
            var dir = tm.Groups["dir"].Value.ToLowerInvariant();
            return new AddressBarTarget(AddressBarTargetKind.Thread, ServerOf(host, dir), dir, tm.Groups["key"].Value);
        }
        var bm = BoardPathRegex.Match(path);
        if (bm.Success)
        {
            var dir = bm.Groups["dir"].Value.ToLowerInvariant();
            if (dir == "bin") return null;
            return new AddressBarTarget(AddressBarTargetKind.Board, ServerOf(host, dir), dir, "");
        }
        return null;
    }

    public string BoardUrl(string host, string directoryName) => $"https://{ServerOf(host, directoryName)}/{directoryName}/futaba.htm";

    /// <summary>スレのページ (<c>res/&lt;番号&gt;.htm</c>)。ふたばにはレス単位のリンクが無いので <paramref name="postNumber"/> は使わない。</summary>
    public string ThreadUrl(string host, string directoryName, string threadKey, long postNumber = 0)
        => $"https://{ServerOf(host, directoryName)}/{directoryName}/res/{threadKey}.htm";

    public string ThreadPageUrl(Board board, string threadKey) => ThreadUrl(board.Host, board.DirectoryName, threadKey);

    private string BoardOrigin(Board board) => "https://" + ServerOf(board.Host, board.DirectoryName);

    public string ThreadListUrl(Board board) => ThreadListUrl(board, ThreadListQuery.Default);

    public string ThreadListUrl(Board board, ThreadListQuery query)
    {
        var sort = string.IsNullOrEmpty(query.Sort) ? ListingSortDefaults.For(this) ?? CatalogSort : query.Sort;
        var url  = $"{BoardOrigin(board)}/{board.DirectoryName}/futaba.php?mode=cat";
        return sort == CatalogSort ? url : url + "&sort=" + sort;
    }

    /// <summary>返信の JSON (全件)。差分は <see cref="ThreadJsonUrl"/> に開始番号を渡す。</summary>
    public string ThreadFetchUrl(Board board, string threadKey) => ThreadJsonUrl(board, threadKey, null);

    public string ThreadJsonUrl(Board board, string threadKey, long? start)
        => $"{BoardOrigin(board)}/{board.DirectoryName}/futaba.php?mode=json&res={threadKey}"
           + (start is long s ? "&start=" + s.ToString(CultureInfo.InvariantCulture) : "");

    public string ThreadFetchRangeUrl(Board board, string threadKey, long fromNumber)
        => throw new NotSupportedException("ふたばはスナップショット方式 (差分は FetchSnapshotAsync が取る)。");

    public string? BoardInfoUrl(Board board) => null;
    public string? PostEndpointUrl(Board board) => null;

    public string? BoardListUrl => MenuUrl;
    public string  BoardListCacheExtension => "html";

    /// <summary>ふたばへの要求にカタログの表示設定の Cookie を付ける (他の要求には影響しない)。</summary>
    public void PrepareRequest(HttpRequestMessage request)
    {
        if (request.RequestUri is { } u && OwnsHost(u.Host) && !request.Headers.Contains("Cookie"))
            request.Headers.TryAddWithoutValidation("Cookie", CatalogCookie);
    }

    // -----------------------------------------------------------------
    // 並び順
    // -----------------------------------------------------------------

    private const string CatalogSort = "cat";

    /// <summary>カタログの並び順 (値はふたばの <c>sort=</c>。<c>cat</c> は指定なし = カタログ順)。</summary>
    public IReadOnlyList<ListingSort> ListingSorts { get; } = new[]
    {
        new ListingSort(CatalogSort, "カタログ順"),
        new ListingSort("1", "新しい順"),
        new ListingSort("2", "古い順"),
        new ListingSort("3", "レスが多い順"),
        new ListingSort("4", "レスが少ない順"),
        new ListingSort("6", "勢い順"),
        new ListingSort("8", "そうだねが多い順"),
    };

    // -----------------------------------------------------------------
    // 板一覧
    // -----------------------------------------------------------------

    private static readonly Regex MenuItemRegex = new(
        @"<b>(?<cat>[^<]+)</b>|<a\s+href=""?https?://(?<srv>[a-z0-9]+)\.2chan\.net/(?<dir>[a-z0-9]+)/(?<page>[^""\s>]*)""?[^>]*>(?<name>[\s\S]*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary><c>bbsmenu.html</c> → 板一覧。<c>&lt;b&gt;区分&lt;/b&gt;</c> ごとのカテゴリ。掲示板でない項目 (あぷ・スクリプト配布・半角) は除く。
    /// 同じ名前の板 (二次元裏は may / dec / jun の 3 つ) は名前にサーバを添える。板名 → サーバの対応表も更新する。</summary>
    public IReadOnlyList<BoardCategory> ParseBoardList(byte[] bytes)
    {
        var html = Sjis.GetString(bytes);
        var items = new List<(string Cat, string Srv, string Dir, string Name)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cat = "ふたば";
        foreach (Match m in MenuItemRegex.Matches(html))
        {
            if (m.Groups["cat"].Success) { cat = WebUtility.HtmlDecode(m.Groups["cat"].Value).Trim(); continue; }
            var page = m.Groups["page"].Value.ToLowerInvariant();
            // 掲示板は futaba.htm (成人向け等は入口ページ *enter*.htm(l) を経由)。それ以外 (up.htm / index2.html / 配布ページ) は掲示板ではない
            if (page != "futaba.htm" && !page.Contains("enter")) continue;
            var dir = m.Groups["dir"].Value.ToLowerInvariant();
            if (!seen.Add(dir)) continue;   // 同じ板が 2 回載っていることがある (ホロライブ)
            var name = WebUtility.HtmlDecode(Regex.Replace(m.Groups["name"].Value, "<[^>]+>", "")).Trim();
            items.Add((cat, m.Groups["srv"].Value.ToLowerInvariant(), dir, name.Length > 0 ? name : dir));
        }
        var dupNames = items.GroupBy(i => i.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();

        var categories = new List<BoardCategory>();
        foreach (var group in items.GroupBy(i => i.Cat))
        {
            var boards = new List<Board>();
            foreach (var i in group)
            {
                var host = i.Srv + "." + RootDomain;
                ServerByDir[i.Dir] = host;
                var name = dupNames.Contains(i.Name) ? $"{i.Name} ({i.Srv})" : i.Name;
                boards.Add(new Board(i.Dir, name, BoardUrl(host, i.Dir), group.Key, boards.Count + 1));
            }
            categories.Add(new BoardCategory(group.Key, categories.Count + 1, boards, Id));
        }
        return categories;
    }

    // -----------------------------------------------------------------
    // スレ一覧 (カタログ)
    // -----------------------------------------------------------------

    private static readonly Regex CatCellRegex  = new(@"<td>(?<cell>[\s\S]*?)</td>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CatResRegex   = new(@"href='res/(?<no>\d+)\.htm'", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CatImgRegex   = new(@"/cat/(?<tim>\d{10,})s\.", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CatTextRegex  = new(@"<small>(?<t>[\s\S]*?)</small>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CatCountRegex = new(@"<font size=2>(?<n>\d+)</font>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>カタログ HTML → スレ一覧。題名はカタログの本文の先頭 (40 文字)、数 = 返信数 + 1 (スレ本体の分)。
    /// 作成時刻はサムネイルの時刻 (<c>/cat/&lt;tim&gt;s.jpg</c>、ミリ秒)。</summary>
    public IReadOnlyList<ThreadInfo> ParseThreadList(byte[] bytes)
    {
        var html  = Sjis.GetString(bytes);
        var items = new List<ThreadInfo>();
        var table = html.IndexOf("id='cattable'", StringComparison.OrdinalIgnoreCase);
        if (table < 0) return items;
        foreach (Match cm in CatCellRegex.Matches(html, table))
        {
            var cell = cm.Groups["cell"].Value;
            if (CatResRegex.Match(cell) is not { Success: true } rm) continue;
            var no    = rm.Groups["no"].Value;
            var title = CatTextRegex.Match(cell) is { Success: true } tm
                ? WebUtility.HtmlDecode(Regex.Replace(tm.Groups["t"].Value, "<[^>]+>", "")).Trim()
                : "";
            if (title.Length == 0) title = $"No.{no}";
            var count = CatCountRegex.Match(cell) is { Success: true } nm && int.TryParse(nm.Groups["n"].Value, out var n) ? n : 0;
            long? created = CatImgRegex.Match(cell) is { Success: true } im && long.TryParse(im.Groups["tim"].Value, out var tim) ? tim / 1000 : null;
            items.Add(new ThreadInfo(no, title, count + 1, items.Count + 1, created));
        }
        return items;
    }

    // -----------------------------------------------------------------
    // スレ (スナップショット + 差分)
    // -----------------------------------------------------------------

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTimeOffset _nextRequest = DateTimeOffset.MinValue;
    /// <summary>要求の最小間隔。ハーネスでは 0 にする。</summary>
    public static TimeSpan RequestInterval { get; set; } = TimeSpan.FromSeconds(1);

    private async Task<byte[]> GetAsync(HttpClient http, string url, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _nextRequest - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                PrepareRequest(req);
                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();   // 404 = スレ落ち (呼び出し側が dat 落ちとして扱う)
                return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _nextRequest = DateTimeOffset.UtcNow + RequestInterval;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>スレ本体が未取得ならスレページから読み、返信は JSON で取る。取得済みなら既知の最大番号の次からの差分だけ。</summary>
    public async Task<ThreadSnapshot> FetchSnapshotAsync(
        HttpClient http, Board board, string threadKey, SnapshotFetchOptions options, CancellationToken ct)
    {
        var origin = BoardOrigin(board);
        var posts  = new List<SnapshotPost>();
        string? notice = null;
        long? start = null;
        if (options.KnownExternalIds.Contains(threadKey))
        {
            var max = options.KnownExternalIds.Select(x => long.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0).DefaultIfEmpty(0).Max();
            start = max + 1;
        }
        else
        {
            var page = Sjis.GetString(await GetAsync(http, ThreadUrl(board.Host, board.DirectoryName, threadKey), ct).ConfigureAwait(false));
            var op = ParseOpHtml(page, origin);
            if (op is null) throw new HttpRequestException("ふたば: スレの形式が想定外です (スレ本体が見つかりません)");
            posts.Add(op.Value.Post);
            notice = op.Value.Notice;
        }

        var json = await GetAsync(http, ThreadJsonUrl(board, threadKey, start), ct).ConfigureAwait(false);
        var parsed = ParseRepliesJson(json, origin);
        if (parsed.Gone) throw new HttpRequestException("ふたば: スレが見つかりません (落ちました)", null, HttpStatusCode.NotFound);
        posts.AddRange(parsed.Posts);
        notice = parsed.Notice ?? notice;
        return new ThreadSnapshot(posts, Notice: notice);
    }

    /// <summary>返信の JSON の解析結果。<see cref="Gone"/> はスレが無い (落ちた) こと。<see cref="Scores"/> は番号 → そうだね の数 (全レス分)。</summary>
    public sealed record RepliesJson(List<SnapshotPost> Posts, bool Gone, string? Notice, IReadOnlyDictionary<long, long> Scores);

    /// <summary><c>futaba.php?mode=json&amp;res=N</c> → 返信 (番号順)。スレ本体は含まれない。</summary>
    public static RepliesJson ParseRepliesJson(byte[] bytes, string origin)
    {
        var posts  = new List<SnapshotPost>();
        var scores = new Dictionary<long, long>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(bytes); }
        catch (JsonException) { throw new HttpRequestException("ふたば: 返信の形式が想定外です"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new HttpRequestException("ふたば: 返信の形式が想定外です");

            // 無いスレは res 無しで「消える日時」が 1970 年代になる
            var hasRes = root.TryGetProperty("res", out var res) && res.ValueKind == JsonValueKind.Object;
            var dieLong = Str(root, "dielong");
            var gone = !hasRes && DateTimeOffset.TryParse(dieLong, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var die) && die.Year < 2000;
            if (gone) return new RepliesJson(posts, true, null, scores);

            if (root.TryGetProperty("sd", out var sd) && sd.ValueKind == JsonValueKind.Object)
                foreach (var kv in sd.EnumerateObject())
                    if (long.TryParse(kv.Name, out var no) && long.TryParse(kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() : kv.Value.GetRawText(), out var c))
                        scores[no] = c;

            if (hasRes)
                foreach (var kv in res.EnumerateObject())
                    if (long.TryParse(kv.Name, out var no) && ToReply(no, kv.Value, origin, scores) is { } sp) posts.Add(sp);
            posts.Sort((a, b) => (a.Number ?? 0).CompareTo(b.Number ?? 0));

            string? notice = null;
            if (Str(root, "die") is { Length: > 0 } dieText) notice = dieText + "頃消えます";
            if (Long(root, "old") is 1) notice = (notice is null ? "" : notice + " ") + "(このスレは古いので、もうすぐ消えます)";
            return new RepliesJson(posts, false, notice, scores);
        }
    }

    private static SnapshotPost? ToReply(long no, JsonElement p, string origin, IReadOnlyDictionary<long, long> scores)
    {
        if (p.ValueKind != JsonValueKind.Object) return null;
        var tim  = long.TryParse(Str(p, "tim"), out var t) ? t : 0;
        var (date, id, ip) = SplitNow(Str(p, "now") ?? "");
        var epoch = tim > 0 ? tim / 1000 : ParseFutabaDate(date) ?? 0;

        var lines = new List<string>();
        if (Str(p, "del") == "del") lines.Add("[削除された記事]");
        if (Str(p, "sub") is { Length: > 0 } sub && sub != "無題") lines.Add("<b>" + WebUtility.HtmlDecode(sub).Replace("<", "＜") + "</b>");
        var body = FutabaBodyConverter.Convert(Str(p, "com"), origin);
        if (body.Length > 0) lines.Add(body);

        List<PostAttachment>? attachments = null;
        if (Str(p, "src") is { Length: > 0 } src && Str(p, "ext") is { Length: > 0 } ext)
        {
            var url = origin + src;
            var thumb = Str(p, "thumb") is { Length: > 0 } th ? origin + th : null;
            attachments = new List<PostAttachment>
            {
                new(url, thumb, Str(p, "tim") + ext, Long(p, "fsize"), (int?)Long(p, "w"), (int?)Long(p, "h"), KindOf(ext)),
            };
            lines.Add(url);
        }

        return new SnapshotPost(
            ExternalId:       no.ToString(CultureInfo.InvariantCulture),
            ParentExternalId: null,
            CreatedEpoch:     epoch,
            Name:             WebUtility.HtmlDecode(Str(p, "name") ?? "").Replace("<", "＜"),
            Mail:             WebUtility.HtmlDecode(Str(p, "email") ?? ""),
            DateText:         DateTextOf(epoch, date, ip),
            Id:               Str(p, "id") is { Length: > 0 } jid ? StripIdPrefix(jid) : id,
            Body:             string.Join("\n", lines),
            ThreadTitle:      null,
            Ext:              new PostExtra(Attachments: attachments, Score: scores.TryGetValue(no, out var sc) && sc > 0 ? sc : null),
            Number:           no);
    }

    private static readonly Regex OpBlockRegex = new(@"<div class=""thre""[^>]*data-res=""(?<no>\d+)""[^>]*>(?<body>[\s\S]*?)</blockquote>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OpSrcRegex   = new(@"<a href=""(?<src>/[^""]+/src/(?<tim>\d+)(?<ext>\.[a-z0-9]+))""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OpThumbRegex = new(@"<img src=""(?<thumb>/[^""]+/thumb/[^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OpSizeRegex  = new(@"-\((?<size>\d+) B\)", RegexOptions.Compiled);
    private static readonly Regex SpanRegex    = new(@"<span class=""(?<cls>csb|cnm|cnw|cntd)"">(?<v>[\s\S]*?)</span>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MailtoRegex  = new(@"<a href=""mailto:(?<mail>[^""]*)""[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex QuoteRegex   = new(@"<blockquote[^>]*>(?<q>[\s\S]*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>スレページ (<c>res/&lt;番号&gt;.htm</c>) からスレ本体を読む (JSON にはスレ本体が無い)。
    /// <c>&lt;div class="thre" data-res="N"&gt;</c> の中の画像・題名 (<c>csb</c>)・名前 (<c>cnm</c>)・日時 (<c>cnw</c>)・本文 (<c>blockquote</c>)、
    /// 消える日時 (<c>cntd</c>)。</summary>
    public static (SnapshotPost Post, string? Notice)? ParseOpHtml(string html, string origin)
    {
        var bm = OpBlockRegex.Match(html);
        if (!bm.Success) return null;
        var no    = long.Parse(bm.Groups["no"].Value, CultureInfo.InvariantCulture);
        var block = bm.Value;

        string sub = "", name = "", mail = "", now = "", notice = "";
        foreach (Match sm in SpanRegex.Matches(block))
        {
            var v = sm.Groups["v"].Value;
            switch (sm.Groups["cls"].Value.ToLowerInvariant())
            {
                case "csb":  sub = PlainText(v); break;
                case "cnm":
                    if (MailtoRegex.Match(v) is { Success: true } mm) mail = WebUtility.HtmlDecode(mm.Groups["mail"].Value);
                    name = PlainText(v);
                    break;
                case "cnw":  now = PlainText(v); break;
                case "cntd": notice = PlainText(v); break;
            }
        }
        // 名前欄の後ろのメール (sage 等) は名前の mailto のほか、名前の外側に付くことがある
        if (mail.Length == 0 && MailtoRegex.Match(block) is { Success: true } mm2) mail = WebUtility.HtmlDecode(mm2.Groups["mail"].Value);

        var (date, id, ip) = SplitNow(now);
        long tim = 0;
        List<PostAttachment>? attachments = null;
        var lines = new List<string>();
        if (sub.Length > 0 && sub != "無題") lines.Add("<b>" + sub.Replace("<", "＜") + "</b>");
        var body = QuoteRegex.Match(block) is { Success: true } qm ? FutabaBodyConverter.Convert(qm.Groups["q"].Value, origin) : "";
        if (body.Length > 0) lines.Add(body);
        if (OpSrcRegex.Match(block) is { Success: true } src)
        {
            long.TryParse(src.Groups["tim"].Value, out tim);
            var url = origin + src.Groups["src"].Value;
            var th  = OpThumbRegex.Match(block);
            attachments = new List<PostAttachment>
            {
                new(url, th.Success ? origin + th.Groups["thumb"].Value : null, src.Groups["tim"].Value + src.Groups["ext"].Value,
                    OpSizeRegex.Match(block) is { Success: true } sz ? long.Parse(sz.Groups["size"].Value, CultureInfo.InvariantCulture) : null,
                    null, null, KindOf(src.Groups["ext"].Value)),
            };
            lines.Add(url);
        }
        var epoch = tim > 0 ? tim / 1000 : ParseFutabaDate(date) ?? 0;
        var title = sub.Length > 0 && sub != "無題" ? sub : FutabaBodyConverter.Snippet(body);
        if (title.Length == 0) title = $"No.{no}";

        var post = new SnapshotPost(
            ExternalId:       no.ToString(CultureInfo.InvariantCulture),
            ParentExternalId: null,
            CreatedEpoch:     epoch,
            Name:             name.Replace("<", "＜"),
            Mail:             mail,
            DateText:         DateTextOf(epoch, date, ip),
            Id:               id,
            Body:             string.Join("\n", lines),
            ThreadTitle:      title,
            Ext:              new PostExtra(Attachments: attachments),
            Number:           no);
        return (post, notice.Length > 0 ? notice : null);
    }

    // -----------------------------------------------------------------
    // 書き込み (投稿窓 = アプリ内ブラウザのふたばの投稿フォーム。決定 D43)
    // -----------------------------------------------------------------

    /// <summary>名前・E-mail (sage 等)・題名 (スレ立て、省略可)・本文・添付 1 つ。画像だけのレスも書ける。
    /// スレ立てに画像が要るかは板による (画像なしの指定は添付が無いときに入れる) ので、判定はふたばに任せる。</summary>
    public PostFormSpec PostForm { get; } = new(
        SupportsName: true, SupportsMail: true, SupportsNewThread: true, UsesDonguriAuth: false,
        SupportsAttachment: true, SubjectOptional: true, MessageOptional: true);

    /// <summary>HTTP では送らない (ページの JS がブラウザらしさの値を入れて送るので、本物のページで送る。<see cref="IBrowserPostProvider"/>)。</summary>
    public PostSubmission BuildPostSubmission(PostRequest request)
        => throw new NotSupportedException("ふたばの書き込みは投稿窓 (アプリ内ブラウザ) で行います。");

    public PostResult ClassifyPostResponse(string html, PostResponseContext context)
        => new(PostOutcome.UnknownError, "ふたばの書き込みは投稿窓 (アプリ内ブラウザ) で行います。", "");

    public string BrowserProfileName => "futaba";
    public string BrowserPostWindowTitle => "ふたばに投稿";
    public string BrowserPostSubmitLabel(PostRequest request) => request.IsReply ? "返信する" : "スレッドを立てる";
    /// <summary>CAPTCHA は無い (ページの JS がブラウザらしさの値を入れるだけ)。</summary>
    public bool BrowserPostHasVerification => false;

    /// <summary>レス: スレのページ、スレ立て: 板のトップ (<c>futaba.htm</c>)。どちらも投稿フォーム <c>#fm</c> がある。</summary>
    public Uri BrowserPostPageUrl(PostRequest request)
        => new(request.IsReply
            ? ThreadUrl(request.Board.Host, request.Board.DirectoryName, request.ThreadKey!)
            : BoardUrl(request.Board.Host, request.Board.DirectoryName));

    /// <summary>投稿フォーム <c>#fm</c> の <c>name</c> / <c>email</c> / <c>sub</c> / <c>com</c> と <c>upfile</c>。
    /// スレ立てで添付が無ければ「画像なし」(<c>textonly</c>) を入れる。削除キー (<c>pwd</c>) はページが Cookie から入れるものを使う。</summary>
    public BrowserPostFill BuildBrowserPostFill(PostRequest request)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("name",  request.Name ?? ""),
            new("email", request.Mail ?? ""),
            new("sub",   request.IsNewThread ? request.Subject ?? "" : ""),
            new("com",   (request.Message ?? "").Replace("\r\n", "\n").Replace('\r', '\n')),
        };
        if (request.IsNewThread) fields.Add(new("textonly", request.Attachment is null ? "on" : ""));
        return new BrowserPostFill("form#fm", fields, "upfile", null, "#ftbl");
    }

    /// <summary>ふたばの結果はページ遷移ではなく <see cref="BrowserPostCaptureScript"/> の知らせで判定する。</summary>
    public PostResult? ClassifyBrowserPostPage(Uri url, string html) => null;

    /// <summary>投稿窓の全ページに差し込む JS。
    /// <list type="bullet">
    /// <item><description>レス: ページの JS (<c>ptfk</c>) が <c>futaba.php</c> へ XHR (<c>mode=regist</c>) で送るので、その応答を知らせる
    ///   (成功は <c>ok</c> か JSON、失敗はエラー文 / エラーの HTML)。</description></item>
    /// <item><description>スレ立て: 普通のフォーム送信 (ページ遷移) なので、送信時に印を付け、次のページでその URL・リフレッシュ先・本文を知らせる。</description></item>
    /// </list></summary>
    public string BrowserPostCaptureScript => @"(function () {
  if (window.__chbCapture) return;
  window.__chbCapture = true;
  var send = function (o) { try { o.type = 'chbPost'; window.chrome.webview.postMessage(o); } catch (e) { } };
  var open = XMLHttpRequest.prototype.open, sendXhr = XMLHttpRequest.prototype.send;
  XMLHttpRequest.prototype.open = function (m, u) { this.__chbM = String(m).toUpperCase(); this.__chbU = String(u); return open.apply(this, arguments); };
  XMLHttpRequest.prototype.send = function (body) {
    var x = this;
    try {
      if (x.__chbM === 'POST' && /futaba\.php/.test(x.__chbU) && body instanceof FormData && body.get('mode') === 'regist') {
        x.addEventListener('load', function () { send({ kind: 'xhr', status: x.status, text: String(x.responseText || '').slice(0, 4000) }); });
        x.addEventListener('error', function () { send({ kind: 'xhr', status: 0, text: '' }); });
      }
    } catch (e) { }
    return sendXhr.apply(this, arguments);
  };
  document.addEventListener('submit', function (e) {
    if (e.target && e.target.id === 'fm') { try { sessionStorage.setItem('chbSubmitted', '1'); } catch (_) { } }
  }, true);
  var flagged = false;
  try { flagged = sessionStorage.getItem('chbSubmitted') === '1'; if (flagged) sessionStorage.removeItem('chbSubmitted'); } catch (_) { }
  if (flagged) {
    var report = function () {
      var m = document.querySelector('meta[http-equiv=""refresh"" i]');
      send({ kind: 'page', url: location.href, title: document.title || '', refresh: m ? (m.getAttribute('content') || '') : '',
             text: (document.body ? document.body.innerText : '').slice(0, 2000) });
    };
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', report); else report();
  }
})();";

    private static readonly Regex ResPageRegex = new(@"(?:^|[/=\s])res/(?<no>\d+)\.htm", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HtmlTitleRegex = new(@"<title[^>]*>(?<t>[\s\S]*?)</title>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary><see cref="BrowserPostCaptureScript"/> の知らせを判定する。
    /// <list type="bullet">
    /// <item><description>XHR (レス): 応答が <c>ok</c> か JSON なら成功 (JSON に投稿番号があれば使う)。それ以外はエラー (HTML ならその題名、文ならその文)。</description></item>
    /// <item><description>ページ (スレ立て): 行き先 / リフレッシュ先が <c>res/N.htm</c> なら成功で N が新しいスレ。
    ///   「書きこみました」等なら成功 (番号不明)。それ以外はエラーとして本文の先頭を出す (投稿窓はフォームを開き直さない)。</description></item>
    /// </list></summary>
    public PostResult? ClassifyBrowserPostMessage(string messageJson)
    {
        using var doc = JsonDocument.Parse(messageJson);
        var m = doc.RootElement;
        if (m.ValueKind != JsonValueKind.Object || Str(m, "type") != "chbPost") return null;
        var text = (Str(m, "text") ?? "").Trim();
        switch (Str(m, "kind"))
        {
            case "xhr":
            {
                var status = Long(m, "status") ?? 0;
                if (status == 0) return new PostResult(PostOutcome.UnknownError, "通信に失敗しました", "");
                if (text == "ok") return new PostResult(PostOutcome.Success, "", "");
                if (text.StartsWith("{", StringComparison.Ordinal) && TryPostNumberFromJson(text, out var ok, out var no))
                {
                    if (ok) return new PostResult(PostOutcome.Success, "", text.Length > 400 ? text[..400] : text,
                        NewPostExternalId: no?.ToString(CultureInfo.InvariantCulture), NewPostNumber: no);
                }
                return new PostResult(PostOutcome.UnknownError, ErrorTextOf(text), text.Length > 400 ? text[..400] : text);
            }
            case "page":
            {
                var url = Str(m, "url") ?? "";
                var target = ResPageRegex.Match(url) is { Success: true } u ? u
                           : ResPageRegex.Match(Str(m, "refresh") ?? "") is { Success: true } r ? r : null;
                if (target is not null && long.TryParse(target.Groups["no"].Value, out var tno))
                    return new PostResult(PostOutcome.Success, "", "", NewPostExternalId: tno.ToString(CultureInfo.InvariantCulture),
                        NewPostNumber: tno, NewThreadKey: tno.ToString(CultureInfo.InvariantCulture));
                if (text.Contains("書きこみました") || text.Contains("書き込みました"))
                    return new PostResult(PostOutcome.Success, "", "");
                var title = (Str(m, "title") ?? "").Trim();
                var body  = text.Length > 200 ? text[..200] + "…" : text;
                return new PostResult(PostOutcome.UnknownError,
                    (title.Length > 0 ? title + ": " : "") + (body.Length > 0 ? body : "結果を判定できませんでした。板の一覧でスレができたか確かめてください"), "");
            }
        }
        return null;
    }

    /// <summary>応答の JSON (<c>{"status":"ok", …}</c> 等) から成功かと投稿番号を読む。番号の項目名は分からないので、よくある名前を順に探す。</summary>
    private static bool TryPostNumberFromJson(string json, out bool ok, out long? number)
    {
        ok = false;
        number = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var status = Str(root, "status");
            ok = status is null || status.Equals("ok", StringComparison.OrdinalIgnoreCase);
            foreach (var key in new[] { "thisno", "resno", "no", "jumpto" })
                if (Long(root, key) is long n && n > 0) { number = n; break; }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>エラーの応答を 1 行の文にする (HTML なら題名と本文の先頭)。</summary>
    private static string ErrorTextOf(string text)
    {
        if (text.Length == 0) return "ふたばが空の応答を返しました";
        if (Regex.IsMatch(text, @"<html[> ]|<body[> ]", RegexOptions.IgnoreCase))
        {
            var title = HtmlTitleRegex.Match(text) is { Success: true } t ? PlainText(t.Groups["t"].Value) : "";
            var body  = PlainText(Regex.Replace(text, @"<(script|style|title)[\s\S]*?</\1>", "", RegexOptions.IgnoreCase));
            if (body.Length > 200) body = body[..200] + "…";
            return title.Length > 0 && !body.Contains(title) ? title + ": " + body : (body.Length > 0 ? body : title);
        }
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    // -----------------------------------------------------------------
    // そうだね (評価。決定 D45: 押したら即送信、取り消し不可)
    // -----------------------------------------------------------------

    /// <summary>そうだね: 加算のみ・取り消し不可。ボタンは「そうだね×数」。</summary>
    public VoteSpec? Voting { get; } = new(VoteMode.UpOnly, UpLabel: "そうだね×", CanUndo: false);

    /// <summary>そうだね の送り先のパス (<c>/sd.php?&lt;板&gt;.&lt;番号&gt;</c>。ページの <c>sd()</c> と同じ)。</summary>
    public static string SodanePath(Board board, long number)
        => $"/sd.php?{board.DirectoryName}.{number.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>そうだね を送るページ (スレのページ) の中で JS を実行する口。App が起動時に入れる (<see cref="Browser.BrowserSessionPage"/>)。</summary>
    public static Browser.IBrowserPageRunner? PageRunner { get; set; }

    /// <summary>そうだね を送る。ページの <c>sd()</c> と同じく、ふたばのスレのページ (投稿窓と同じブラウザセッション、Cookie 付き) から
    /// 同一オリジンで <c>GET /sd.php?板.番号</c> を送る (Cookie の無い素の HTTP では数えられないため)。応答は新しい数で、
    /// 押す前より増えていれば成功。取り消し (<paramref name="direction"/> ≠ 1) はできない。</summary>
    public async Task<VoteResult> VoteAsync(HttpClient http, Board board, string threadKey, Post post, int direction, CancellationToken ct)
    {
        if (direction != 1) return new VoteResult(false, "そうだね は取り消せません");
        if (PageRunner is not { } runner) return new VoteResult(false, "ブラウザのセッションを使えません");
        var path   = SodanePath(board, post.Number);
        var script = "(function () { try { var x = new XMLHttpRequest(); x.open('GET', " + JsonSerializer.Serialize(path) + ", false); x.send(null);"
                   + " return JSON.stringify({ status: x.status, text: String(x.responseText || '').slice(0, 300) }); }"
                   + " catch (e) { return JSON.stringify({ status: 0, text: String(e && e.message || e) }); } })()";
        string raw;
        try
        {
            raw = await runner.RunAsync(ThreadUrl(board.Host, board.DirectoryName, threadKey), script, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or TaskCanceledException)
        {
            return new VoteResult(false, $"そうだね を送れませんでした ({ex.Message})");
        }
        var (status, text) = ParseSodaneReply(raw);
        var result = status is >= 200 and < 300
            ? ClassifySodaneResponse(text, post.Ext?.Score ?? 0)
            : new VoteResult(false, $"そうだね を送れませんでした (HTTP {status}{(text.Length > 0 ? " " + text : "")})");
        ChBrowser.Services.Logging.LogService.Instance.Write(
            $"[futaba] そうだね {board.DirectoryName}.{post.Number}: HTTP {status} 応答「{text}」 前={post.Ext?.Score ?? 0} ok={result.Ok} {result.Message}");
        return result;
    }

    private static (int Status, string Text) ParseSodaneReply(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var st = doc.RootElement.TryGetProperty("status", out var s) && s.TryGetInt32(out var n) ? n : 0;
            return (st, (Str(doc.RootElement, "text") ?? "").Trim());
        }
        catch (JsonException) { return (0, raw); }
    }

    /// <summary>そうだね の応答 (新しい数) を判定する。押す前の数 <paramref name="before"/> より増えていれば成功、
    /// 増えていなければ「数えられなかった」(既に押している等)、数でなければ失敗。</summary>
    public static VoteResult ClassifySodaneResponse(string body, long before)
    {
        var t = (body ?? "").Trim();
        if (!long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return new VoteResult(false, t.Length == 0 ? "そうだね を送れませんでした (空の応答)" : $"そうだね を送れませんでした ({(t.Length > 80 ? t[..80] : t)})");
        return n > before
            ? VoteResult.Success
            : new VoteResult(false, $"そうだね が数えられませんでした (応答の数 {n}、押す前 {before}。既に押しているかもしれません)");
    }

    public Post? ParseThreadLine(string line) => null;

    // -----------------------------------------------------------------
    // 小道具
    // -----------------------------------------------------------------

    private static readonly Regex NowRegex = new(@"^(?<date>\d{2}/\d{2}/\d{2}\(.\)\d{2}:\d{2}:\d{2})\s*(?<rest>.*)$", RegexOptions.Compiled);
    private static readonly Regex IdRegex  = new(@"ID:(?<id>\S+)", RegexOptions.Compiled);
    private static readonly Regex IpRegex  = new(@"IP:\S+", RegexOptions.Compiled);

    /// <summary><c>25/06/21(土)21:44:00 IP:123.1.*(commufa.jp)</c> → 日時・ID (<c>ID:xxxx</c> の xxxx)・IP 表示。</summary>
    public static (string Date, string Id, string Ip) SplitNow(string now)
    {
        var m = NowRegex.Match(now.Trim());
        if (!m.Success) return (now.Trim(), "", "");
        var rest = m.Groups["rest"].Value;
        var id = IdRegex.Match(rest) is { Success: true } im ? im.Groups["id"].Value : "";
        var ip = IpRegex.Match(rest) is { Success: true } pm ? pm.Value : "";
        return (m.Groups["date"].Value, id, ip);
    }

    private static string StripIdPrefix(string id) => id.StartsWith("ID:", StringComparison.Ordinal) ? id[3..] : id;

    private static readonly TimeZoneInfo Jst = FindJst();
    private static TimeZoneInfo FindJst()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST"); }
    }

    /// <summary><c>25/06/21(土)20:52:39</c> (日本時間) → epoch 秒。</summary>
    public static long? ParseFutabaDate(string date)
    {
        var m = Regex.Match(date, @"^(\d{2})/(\d{2})/(\d{2})\(.\)(\d{2}):(\d{2}):(\d{2})");
        if (!m.Success) return null;
        try
        {
            var local = new DateTime(2000 + int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                                     int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value), int.Parse(m.Groups[6].Value), DateTimeKind.Unspecified);
            return new DateTimeOffset(local, Jst.GetUtcOffset(local)).ToUnixTimeSeconds();
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>表示用の日時 (アプリ共通の書式) + IP 表示。時刻が分からなければふたばの表記のまま。</summary>
    private static string DateTextOf(long epoch, string rawDate, string ip)
    {
        var date = epoch > 0 ? BbsDate.FromEpoch(epoch) : rawDate;
        return ip.Length > 0 ? date + " " + ip : date;
    }

    private static string KindOf(string ext) => ext.ToLowerInvariant() switch
    {
        ".webm" or ".mp4" => "video",
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" => "image",
        _ => "file",
    };

    private static string PlainText(string html)
        => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", "")).Trim();

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var x)) return x;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out x)) return x;
        return null;
    }
}
