using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Reddit;
using ChBrowser.Services.Url;

namespace ChBrowser.Services.Bbs;

/// <summary>reddit の提供者 (読み取り、<c>doc/reddit-design.md</c> §4)。
///
/// <list type="bullet">
/// <item><description>板 = subreddit (<c>dir</c> は小文字の名前)、スレ = 投稿 (<c>key</c> は base36 の id)、ホストは常に <c>www.reddit.com</c>、保存ルート <c>data/reddit.com/</c>。</description></item>
/// <item><description>要求は <c>www.reddit.com/…​.json</c> へ送り、App が登録した WebView2 セッション経路 (<see cref="WebViewTransport"/>) を通る (決定 D22)。</description></item>
/// <item><description>スレはスナップショット方式: 投稿 + コメント木を取り、<c>more</c> は新しい順・評価の高い順のスレと
///   <c>/api/morechildren/.json</c> (弾かれたら親コメント起点のスレ) で埋める (回数上限あり、
///   レート制限の残りが少なければ打ち切り。D38)。採番と保存は <see cref="SnapshotThreadFetcher"/>。</description></item>
/// <item><description>レス番号は画面に出さない (<see cref="ShowsPostNumbers"/> = false)。返信先は <see cref="PostExtra.ParentNumber"/>。</description></item>
/// <item><description>書き込み (5-B) は <c>/api/comment</c> / <c>/api/submit</c>、レスの評価は <c>/api/vote</c> (<see cref="Voting"/>)。</description></item>
/// </list></summary>
public sealed class RedditProvider : IBbsProvider, ISnapshotThreadProvider
{
    public const string CanonicalHost = "www.reddit.com";
    private const string Origin = "https://www.reddit.com";

    /// <summary>コメント取得の limit (実測で約 500 が上限。§1.5a)。</summary>
    public const int CommentLimit = 500;
    /// <summary>レート制限の残りがこれを下回ったら more の展開を打ち切る (一覧・スレを開く操作に予算を残す。D38)。
    /// 30 では接続テストの直後などに大きいスレの続きが全く取れなくなったため 10 に下げた (2026-10-29)。</summary>
    public const int ReserveRequests = 10;
    /// <summary>「continue this thread」(中身の件数が分からない続き) の取得の優先度 (まだ持っていないコメントの件数に相当)。</summary>
    private const int ContinueScore = 20;
    /// <summary>morechildren 1 回で送る id の数 (実測で 100 程度が上限)。</summary>
    public const int MoreChildrenBatch = 100;
    /// <summary>中身の無かった「continue this thread」を「取れない分」(<see cref="ThreadSnapshot.Unreachable"/>) に記録するときのキー。</summary>
    private static string ContinueKey(string parentId) => "continue:t1_" + parentId;
    private static string GoneKey(string id)     => "gone:t1_" + id;
    private static string NoBranchKey(string id) => "nobranch:t1_" + id;

    private static readonly Regex BoardPathRegex = new(
        @"^/r/(?<dir>[A-Za-z0-9_]+)(?:/(?:hot|new|top|rising|controversial|best))?/?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ThreadPathRegex = new(
        @"^/r/(?<dir>[A-Za-z0-9_]+)/comments/(?<key>[A-Za-z0-9]+)(?:/(?<slug>[^/]*)(?:/(?<cid>[A-Za-z0-9]+))?)?/?$", RegexOptions.Compiled);


    public string Id          => "reddit";
    public string DisplayName => "reddit";

    public BbsCapabilities Capabilities =>
        BbsCapabilities.ThreadList | BbsCapabilities.ThreadFetch | BbsCapabilities.Posting | BbsCapabilities.ThreadCreation |
        BbsCapabilities.Auth | BbsCapabilities.Attachments | BbsCapabilities.BoardInfo | BbsCapabilities.ListingPaging;

    public Encoding TextEncoding => Encoding.UTF8;
    public IReadOnlyList<string> StorageRoots { get; } = new[] { "reddit.com" };
    public string DefaultHost => CanonicalHost;
    public int PostNumberDigits => 0;
    public IReadOnlyList<string> HostSuffixes { get; } = new[] { "reddit.com" };

    public string ThreadLinkJsPattern
        => @"^https?:\/\/(?<host>(?:[A-Za-z0-9-]+\.)?reddit\.com)\/r\/(?<dir>[A-Za-z0-9_]+)\/comments\/(?<key>[A-Za-z0-9]+)(?:\/[^\/?#]*(?:\/(?<post>[A-Za-z0-9]+))?)?";

    /// <summary>reddit の本文には <c>&gt;&gt;N</c> の慣習が無いので既定は空。返信先は親番号 (B11)。</summary>
    public IReadOnlyList<AnchorRule> DefaultAnchorRules { get; } = Array.Empty<AnchorRule>();

    public bool ShowsPostNumbers => false;
    public bool UsesWatchoi => false;
    public bool ThreadListIsComplete => false;
    public ThreadFetchStrategy FetchStrategy => ThreadFetchStrategy.Snapshot;
    public IReadOnlyList<string> RoutedHosts { get; } = new[] { CanonicalHost };
    public bool UsesNativeDat => false;

    public IReadOnlyList<ListingSort> ListingSorts { get; } = new[]
    {
        new ListingSort("hot",      "注目 (hot)"),
        new ListingSort("new",      "新着 (new)"),
        new ListingSort("top:day",  "トップ (24 時間)"),
        new ListingSort("top:week", "トップ (1 週間)"),
        new ListingSort("top:all",  "トップ (全期間)"),
        new ListingSort("rising",   "急上昇 (rising)"),
    };

    // -----------------------------------------------------------------
    // URL
    // -----------------------------------------------------------------

    public bool OwnsHost(string host)
        => string.Equals(host, "reddit.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".reddit.com", StringComparison.OrdinalIgnoreCase);

    public string NormalizeHost(string host) => OwnsHost(host) ? CanonicalHost : host;

    public AddressBarTarget? TryParseUrl(Uri uri, string host)
    {
        var path = uri.AbsolutePath;
        var tm = ThreadPathRegex.Match(path);
        if (tm.Success)
        {
            var cid = tm.Groups["cid"].Success ? tm.Groups["cid"].Value.ToLowerInvariant() : null;
            return new AddressBarTarget(AddressBarTargetKind.Thread, CanonicalHost,
                tm.Groups["dir"].Value.ToLowerInvariant(), tm.Groups["key"].Value.ToLowerInvariant(), 0, cid);
        }
        var bm = BoardPathRegex.Match(path);
        if (bm.Success)
            return new AddressBarTarget(AddressBarTargetKind.Board, CanonicalHost, bm.Groups["dir"].Value.ToLowerInvariant(), "");
        return null;
    }

    public string BoardUrl(string host, string directoryName) => $"{Origin}/r/{directoryName}/";

    /// <summary>スレの正規 URL。<paramref name="postNumber"/> はアプリ内の番号で reddit の id に直せないので無視する
    /// (コメントを指す URL が要る場面は meta.json から組む。§2.1)。</summary>
    public string ThreadUrl(string host, string directoryName, string threadKey, long postNumber = 0)
        => $"{Origin}/r/{directoryName}/comments/{threadKey}/";

    public string ThreadPageUrl(Board board, string threadKey) => ThreadUrl(board.Host, board.DirectoryName, threadKey);

    public string ThreadListUrl(Board board) => ThreadListUrl(board, ThreadListQuery.Default);

    public string ThreadListUrl(Board board, ThreadListQuery query)
    {
        var sort = string.IsNullOrEmpty(query.Sort) ? ListingSortDefaults.For(this) ?? "hot" : query.Sort;
        var t = "";
        var colon = sort.IndexOf(':');
        if (colon > 0) { t = sort[(colon + 1)..]; sort = sort[..colon]; }
        var sb = new StringBuilder($"{Origin}/r/{board.DirectoryName}/{sort}.json?limit=100&raw_json=1");
        if (t.Length > 0) sb.Append("&t=").Append(Uri.EscapeDataString(t));
        if (!string.IsNullOrEmpty(query.After)) sb.Append("&after=").Append(Uri.EscapeDataString(query.After));
        return sb.ToString();
    }

    public string ThreadFetchUrl(Board board, string threadKey)
        => $"{Origin}/r/{board.DirectoryName}/comments/{threadKey}.json?sort=old&limit={CommentLimit}&raw_json=1";

    public string ThreadFetchRangeUrl(Board board, string threadKey, long fromNumber)
        => throw new NotSupportedException("reddit はスナップショット方式 (番号以降の差分取得は無い)。");

    public string? BoardInfoUrl(Board board) => $"{Origin}/r/{board.DirectoryName}/about.json?raw_json=1";

    /// <summary>レスの書き込み先 (スレ立ては <see cref="SubmitEndpoint"/>)。ログインセッション経由で送り、modhash は通信経路が付ける (D35)。</summary>
    public string? PostEndpointUrl(Board board) => CommentEndpoint;

    public const string CommentEndpoint = Origin + "/api/comment?raw_json=1";
    public const string SubmitEndpoint  = Origin + "/api/submit?raw_json=1";

    /// <summary>reddit は板一覧を持たない (板一覧ペインでは「検索」「表示済み板」を出す。2026-09-23 ユーザ要望)。</summary>
    public string? BoardListUrl => null;
    public string  BoardListCacheExtension => "";

    // -----------------------------------------------------------------
    // 一覧 / 板情報 / 板一覧
    // -----------------------------------------------------------------

    public IReadOnlyList<ThreadInfo> ParseThreadList(byte[] bytes) => ParseThreadListPage(bytes).Items;

    /// <summary>Listing (t3) → スレ一覧 1 ページ。数 = コメント数 + 1 (投稿本体の分。取得後の件数と比べるため)。</summary>
    public ThreadListPage ParseThreadListPage(byte[] bytes)
    {
        var items = new List<ThreadInfo>();
        string? after = null;
        if (!TryParse(bytes, out var doc)) return new ThreadListPage(items, null);
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("data", out var data)) return new ThreadListPage(items, null);
            after = Str(data, "after");
            if (data.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in children.EnumerateArray())
                {
                    if (Str(c, "kind") != "t3" || !c.TryGetProperty("data", out var d)) continue;
                    var id = Str(d, "id");
                    if (string.IsNullOrEmpty(id)) continue;
                    var title = (Str(d, "title") ?? "").Replace('\n', ' ').Trim();
                    if (Bool(d, "stickied")) title = "📌 " + title;
                    if (Bool(d, "over_18")) title = "[NSFW] " + title;
                    items.Add(new ThreadInfo(
                        Key:          id,
                        Title:        title,
                        PostCount:    Int(d, "num_comments") + 1,
                        Order:        items.Count + 1,
                        CreatedEpoch: Epoch(d, "created_utc"),
                        Score:        Long(d, "score")));
                }
            }
        }
        return new ThreadListPage(items, after);
    }

    /// <summary>about.json → 板情報。板名 (<c>BBS_TITLE</c>) は <c>r/名前</c> (reddit では名前で呼ぶのが普通なので title より優先)。</summary>
    public IReadOnlyDictionary<string, string> ParseBoardInfo(byte[] bytes)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!TryParse(bytes, out var doc)) return dict;
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("data", out var d)) return dict;
            var prefixed = Str(d, "display_name_prefixed") ?? (Str(d, "display_name") is { } dn ? "r/" + dn : null);
            if (prefixed is not null) dict["BBS_TITLE"] = prefixed;
            if (Str(d, "title") is { Length: > 0 } title) dict["BBS_SUBTITLE"] = title;
            if (Str(d, "public_description") is { Length: > 0 } desc) dict["BBS_DESCRIPTION"] = desc;
            if (d.TryGetProperty("subscribers", out var subs) && subs.ValueKind == JsonValueKind.Number) dict["SUBSCRIBERS"] = subs.ToString();
            if (Bool(d, "over18")) dict["OVER18"] = "1";
        }
        return dict;
    }

    public IReadOnlyList<BoardCategory> ParseBoardList(byte[] bytes) => Array.Empty<BoardCategory>();

    /// <summary>subreddit の検索 (<c>/subreddits/search.json</c>、ログインセッション経由)。数 = 購読者数、説明 = title。</summary>
    public bool SupportsBoardSearch => true;

    public async Task<IReadOnlyList<BoardSearchHit>> SearchBoardsAsync(HttpClient http, string keyword, CancellationToken ct)
    {
        var q   = Uri.EscapeDataString(keyword);
        var url = $"https://old.reddit.com/subreddits/search.json?q={q}&limit=50&raw_json=1";
        // www の検索は 2026-10-30 からログイン中でも「network security」で弾かれる (old.reddit.com の同じ検索は通る。接続テストで確認)。
        // old.reddit → www → 入力補完用の API (最大 10 件) の順に、窓を出さずに試す
        foreach (var candidate in new[]
                 {
                     url,
                     $"{Origin}/subreddits/search.json?q={q}&limit=50&raw_json=1",
                     $"{Origin}/api/subreddit_autocomplete_v2.json?query={q}&include_over_18=true&include_profiles=false&limit=10&raw_json=1",
                 })
        {
            try
            {
                byte[] got;
                using (ProviderRequestContext.Optional())
                    (got, _) = await GetAsync(http, candidate, ct).ConfigureAwait(false);
                var hits = ParseSubredditListing(got);
                ChBrowser.Services.Logging.LogService.Instance.Write($"[reddit] 板検索 {candidate.Split('?')[0]} → {hits.Count} 件");
                return hits;
            }
            catch (HttpRequestException ex)
            {
                ChBrowser.Services.Logging.LogService.Instance.Write($"[reddit] 板検索 {candidate.Split('?')[0]} が失敗: {ex.Message}");
            }
        }
        // どちらも弾かれた: ログイン切れ等の可能性があるので、通常の要求として送る (必要ならログイン / 確認の窓が出る)
        var (bytes, _) = await GetAsync(http, url, ct).ConfigureAwait(false);
        return ParseSubredditListing(bytes);
    }

    // ---- 板一覧ペインの一覧 (ホット / おすすめ) ----

    /// <summary>r/popular (全体のホットな投稿)、人気の subreddit、ログイン中のホーム (おすすめ = best)。どれもログインセッション経由。</summary>
    public IReadOnlyList<ProviderListing> Listings { get; } = new[]
    {
        new ProviderListing("hot",       "🔥 ホットなスレッド"),
        new ProviderListing("hotBoards", "🔥 ホットな板"),
        new ProviderListing("forYou",    "✨ あなたへのおすすめ"),
    };

    public async Task<ProviderListingResult> FetchListingAsync(HttpClient http, string listingId, CancellationToken ct,
                                                               IProgress<ProviderListingResult>? progress = null)
    {
        // おすすめは HTML から読む: JSON の best / トップページは「購読している subreddit の投稿」で、購読が無いと空
        // (2026-10-22 ユーザ環境で確認)。ブラウザのホームに出る「おすすめ」は HTML (<shreddit-post>) にしか無い
        if (listingId == "forYou") return ProviderListingResult.OfThreads(await FetchHomeFeedHtmlAsync(http, ct, progress).ConfigureAwait(false));

        var url = listingId switch
        {
            "hot"       => $"{Origin}/r/popular/hot.json?limit=100&raw_json=1",
            "hotBoards" => $"{Origin}/subreddits/popular.json?limit=100&raw_json=1",
            _           => throw new NotSupportedException($"reddit には一覧「{listingId}」がありません。"),
        };
        var (bytes, _) = await GetAsync(http, url, ct).ConfigureAwait(false);
        if (listingId == "hotBoards")
        {
            var boards = ParseSubredditListing(bytes);
            LogListing(listingId, url, bytes, boards.Count);
            return ProviderListingResult.OfBoards(boards);
        }
        var threads = ParseCrossBoardListing(bytes);
        LogListing(listingId, url, bytes, threads.Count);
        return ProviderListingResult.OfThreads(threads);
    }

    /// <summary>ホームの「おすすめ」を HTML から読む: トップページ (/) の <c>&lt;shreddit-post&gt;</c> と、スクロールで読み込まれる続き
    /// (<c>/svc/shreddit/feeds/interest-home-feed?…</c>、2026-10 に home-feed から改名。ページ内の <c>src</c> に書かれている) を数ページたどる。
    /// 1 ページは約 1MB・要求の間隔は 1 秒以上なので全部で 10 秒ほどかかる。ページを読むたびに取れた分を <paramref name="progress"/> で知らせる。</summary>
    private async Task<IReadOnlyList<ListedThread>> FetchHomeFeedHtmlAsync(HttpClient http, CancellationToken ct,
                                                                           IProgress<ProviderListingResult>? progress)
    {
        const int MaxPages = 5, Want = 100;
        var list = new List<ListedThread>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? next = $"{Origin}/";
        for (var page = 0; page < MaxPages && next is not null && list.Count < Want; page++)
        {
            byte[] bytes;
            if (page == 0)
                (bytes, _) = await GetAsync(http, next, ct).ConfigureAwait(false);
            else
            {
                // 2 ページ目以降は取れなくても困らない: 弾かれても窓を出さず、それまでに取れた分を返す
                try
                {
                    using (ProviderRequestContext.Optional())
                        (bytes, _) = await GetAsync(http, next, ct).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    ChBrowser.Services.Logging.LogService.Instance.Write(
                        $"[reddit listing] forYou: 続きのページの取得に失敗したので {list.Count} 件で打ち切ります ({ex.Message}): {next}");
                    break;
                }
            }
            var html  = System.Text.Encoding.UTF8.GetString(bytes);
            var before = list.Count;
            var tags  = ParseShredditPosts(html, list, seen);
            var m = HomeFeedNextRegex.Match(html);
            var nextUrl = m.Success ? Origin + System.Net.WebUtility.HtmlDecode(m.Groups["src"].Value) : null;
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[reddit listing] forYou (HTML): {next} → {bytes.Length} bytes, shreddit-post={tags}, 一覧に足した件数={list.Count - before}, 続き={(nextUrl is null ? "なし" : nextUrl)}");
            if (nextUrl is null)
            {
                // 続きの場所が見つからない (ページの作りが変わった等) ときの調査用: ページ内の部分読み込みの URL 候補を出す
                var candidates = System.Text.RegularExpressions.Regex.Matches(html, @"(?<u>/svc/shreddit/[^""'\s<>]{0,200})")
                    .Select(x => x.Groups["u"].Value).Distinct()
                    .OrderByDescending(u => u.Contains("feed", StringComparison.OrdinalIgnoreCase)).Take(12).ToList();
                // ページそのものも保存する (作りの変化を調べるため。毎回上書き)
                try
                {
                    var dir = System.IO.Path.Combine(new ChBrowser.Services.Storage.DataPaths().Root, "reddit.com");
                    System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, $"foryou_page{page + 1}.html"), bytes);
                }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
                ChBrowser.Services.Logging.LogService.Instance.Write(
                    $"[reddit listing] forYou: 続きの URL が見つかりません。ページ内の /svc/shreddit/ の候補 {candidates.Count} 件: " +
                    (candidates.Count == 0 ? $"(無し) 先頭: {System.Text.RegularExpressions.Regex.Replace(html.Length > 300 ? html[..300] : html, @"\s+", " ")}"
                                           : string.Join(" | ", candidates.Select(System.Net.WebUtility.HtmlDecode))));
            }
            next = nextUrl is not null && nextUrl != next ? nextUrl : null;
            if (next is not null && list.Count < Want && list.Count > before)
                progress?.Report(ProviderListingResult.OfThreads(list.ToArray()));   // 続きがあるときだけ (最後は戻り値で渡す)
        }
        return list;
    }

    private static readonly System.Text.RegularExpressions.Regex ShredditPostRegex =
        new(@"<shreddit-post\b[^>]*>", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex HtmlAttrRegex =
        new(@"(?<name>[\w-]+)=""(?<val>[^""]*)""", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex NsfwAttrRegex =
        new(@"\snsfw(?=[\s>=/])", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex HomeFeedNextRegex =
        new(@"src=""(?<src>/svc/shreddit/feeds/(?:interest-)?home-feed\?[^""]*)""", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex PermalinkRegex =
        new(@"^/r/(?<sub>[A-Za-z0-9_]+)/comments/(?<id>[a-z0-9]+)/", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>HTML の <c>&lt;shreddit-post&gt;</c> 要素 (属性 permalink / post-title / comment-count / score / created-timestamp) → 板付きのスレ。
    /// 戻り値: 見つけた要素の数 (広告等で使えないものも含む)。</summary>
    public int ParseShredditPosts(string html, List<ListedThread> list, HashSet<string> seen)
    {
        var found = 0;
        foreach (System.Text.RegularExpressions.Match tag in ShredditPostRegex.Matches(html))
        {
            found++;
            var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match a in HtmlAttrRegex.Matches(tag.Value))
                attrs[a.Groups["name"].Value] = System.Net.WebUtility.HtmlDecode(a.Groups["val"].Value);
            if (!attrs.TryGetValue("permalink", out var permalink)) continue;
            var pm = PermalinkRegex.Match(permalink);
            if (!pm.Success) continue;                                  // ユーザーページの投稿・広告等
            var id = pm.Groups["id"].Value;
            if (!seen.Add(id)) continue;
            var sub = pm.Groups["sub"].Value;
            var title = (attrs.GetValueOrDefault("post-title") ?? "").Replace('\n', ' ').Trim();
            if (attrs.ContainsKey("nsfw") || NsfwAttrRegex.IsMatch(tag.Value)) title = "[NSFW] " + title;   // 値の無い属性 (nsfw) もある
            long? created = DateTimeOffset.TryParse(attrs.GetValueOrDefault("created-timestamp"), CultureInfo.InvariantCulture,
                                                    DateTimeStyles.AssumeUniversal, out var ts) ? ts.ToUnixTimeSeconds() : null;
            var comments = int.TryParse(attrs.GetValueOrDefault("comment-count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cc) ? cc : 0;
            long? score  = long.TryParse(attrs.GetValueOrDefault("score"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sc) ? sc : null;
            var dir = sub.ToLowerInvariant();
            list.Add(new ListedThread(new Board(dir, "r/" + sub, BoardUrl(CanonicalHost, dir), "", 0),
                new ThreadInfo(id, title, comments + 1, list.Count + 1, created, score)));
        }
        return found;
    }

    /// <summary>一覧の取得結果をログペインに出す (件数が合わないときの調査用)。応答の形 (Listing か / 子の種類と数 / 先頭) を出す。</summary>
    private static void LogListing(string listingId, string url, byte[] bytes, int parsed)
    {
        var info = "";
        if (TryParse(bytes, out var doc))
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data)
                    && data.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
                {
                    var kinds = children.EnumerateArray().GroupBy(c => Str(c, "kind") ?? "?").Select(g => $"{g.Key}×{g.Count()}");
                    info = $"kind={Str(root, "kind")} children={children.GetArrayLength()} ({string.Join(", ", kinds)})";
                }
                else info = $"Listing ではない ({root.ValueKind}{(root.ValueKind == JsonValueKind.Object ? ": " + string.Join(",", root.EnumerateObject().Select(pr => pr.Name).Take(8)) : "")})";
            }
        else info = "JSON ではない";
        var head = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 160)).Replace('\n', ' ').Replace('\r', ' ');
        ChBrowser.Services.Logging.LogService.Instance.Write(
            $"[reddit listing] {listingId}: {url} → {bytes.Length} bytes, {info}, 一覧に出した件数={parsed} | 先頭: {head}");
    }

    /// <summary>いろいろな subreddit の投稿が混ざった Listing (t3) → 板付きのスレ。スレの中身は <see cref="ParseThreadListPage"/> と同じ。</summary>
    public IReadOnlyList<ListedThread> ParseCrossBoardListing(byte[] bytes)
    {
        var page = ParseThreadListPage(bytes);
        var subById = new Dictionary<string, string>(StringComparer.Ordinal);
        if (TryParse(bytes, out var doc))
            using (doc)
                if (doc.RootElement.TryGetProperty("data", out var data)
                    && data.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
                    foreach (var c in children.EnumerateArray())
                        if (c.TryGetProperty("data", out var d) && Str(d, "id") is { Length: > 0 } id && Str(d, "subreddit") is { Length: > 0 } sub)
                            subById[id] = sub;
        var list = new List<ListedThread>(page.Items.Count);
        foreach (var t in page.Items)
        {
            if (!subById.TryGetValue(t.Key, out var sub) || sub.StartsWith("u_", StringComparison.Ordinal)) continue;   // ユーザーページの投稿は除く
            var dir = sub.ToLowerInvariant();
            list.Add(new ListedThread(new Board(dir, "r/" + sub, BoardUrl(CanonicalHost, dir), "", 0), t with { Order = list.Count + 1 }));
        }
        return list;
    }

    /// <summary>t5 (subreddit) の Listing → 検索結果。</summary>
    public IReadOnlyList<BoardSearchHit> ParseSubredditListing(byte[] bytes)
    {
        var hits = new List<BoardSearchHit>();
        if (!TryParse(bytes, out var doc)) return hits;
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array) return hits;
            foreach (var c in children.EnumerateArray())
            {
                if (!c.TryGetProperty("data", out var d) || Str(d, "display_name") is not { Length: > 0 } name) continue;
                if (name.StartsWith("u_", StringComparison.Ordinal)) continue;   // ユーザーページは除く
                var dir   = name.ToLowerInvariant();
                var title = (Str(d, "title") ?? "").Trim();
                var desc  = (Str(d, "public_description") ?? "").Replace('\n', ' ').Trim();
                var text  = title.Length > 0 && desc.Length > 0 ? $"{title} — {desc}" : title + desc;
                if (Bool(d, "over18")) text = "[NSFW] " + text;
                hits.Add(new BoardSearchHit(new Board(dir, "r/" + name, BoardUrl(CanonicalHost, dir), "", 0), text, Long(d, "subscribers")));
            }
        }
        return hits;
    }

    // -----------------------------------------------------------------
    // スレ (スナップショット)
    // -----------------------------------------------------------------

    public async Task<ThreadSnapshot> FetchSnapshotAsync(
        HttpClient http, Board board, string threadKey, SnapshotFetchOptions options, CancellationToken ct)
    {
        var url = ThreadFetchUrl(board, threadKey);
        var (bytes, remaining) = await GetAsync(http, url, ct).ConfigureAwait(false);
        var tree = ParseCommentsResponse(bytes);
        if (tree.Submission is null) throw new HttpRequestException("reddit: スレの形式が想定外です (投稿が見つかりません)");

        var posts = new List<SnapshotPost> { tree.Submission };
        posts.AddRange(tree.Comments);
        var have = new HashSet<string>(posts.Select(p => p.ExternalId), StringComparer.Ordinal);

        // このアプリから書き込んだコメント (書き込み応答に全体が入っている) を足す。大きいスレでは新しいコメントが
        // 未展開の more の中に入って取れないことがあるため、自分の書き込みは確実に取り込む。
        foreach (var mine in TakePostedComments(threadKey))
            if (have.Add(mine.ExternalId)) posts.Add(mine);

        // ここまでの分を先に渡す (大きいスレでも more の展開を待たずに表示が始まる)
        var reported = 0;
        async Task ReportPartialAsync()
        {
            if (options.OnPartial is null || reported >= posts.Count) return;
            var chunk = posts.GetRange(reported, posts.Count - reported);
            reported  = posts.Count;
            await options.OnPartial(chunk).ConfigureAwait(false);
        }
        await ReportPartialAsync().ConfigureAwait(false);

        // 続き (more) の展開。取り方 (要求 1 回ずつ、上から優先):
        //   1. 新しい順 (sort=new) のスレ: トップレベルに未取得があるとき。新着が入る
        //   2. 評価の高い順 (sort=top) のスレ: 初回の取得だけ。スレの真ん中のうち評価の高いコメントが入る
        //   3. morechildren: 未取得の ID を指定して 100 件ずつ (続きの大きい塊から = スレの真ん中を古い方から埋めていく)
        //   4. (morechildren が弾かれたときだけ) 親コメントを起点にしたスレ (/comments/{id}/_/{親}.json)
        // 取りに行っても取れなかった分 (削除済み等・4 で取れない大きな枝の真ん中) は Unreachable として返し、次回からは数えない。
        // 取れない分の記録は 2 種類: "gone:t1_x" (morechildren で頼んでも返らない = 削除済み等。常に数えない) と
        // "nobranch:t1_x" (morechildren が使えないときの取り方で取れない。morechildren が使えれば取れるので、使えないときだけ数えない)
        var unreachable = options.UnreachableIds ?? new HashSet<string>(StringComparer.Ordinal);
        var moreChildrenBlocked = DateTime.UtcNow < s_moreChildrenBlockedUntil;
        bool Unknown(string id) => !have.Contains("t1_" + id) && !options.KnownExternalIds.Contains("t1_" + id)
                                   && !unreachable.Contains(GoneKey(id)) && !(moreChildrenBlocked && unreachable.Contains(NoBranchKey(id)));
        // 「continue this thread」は、その親が既に返信を持っていれば (= 前回取り直し済み) / 前回取って中身が無ければ省く
        bool NeedContinue(string parent) => options.KnownParentIds?.Contains("t1_" + parent) != true && !unreachable.Contains(ContinueKey(parent));
        var allMores      = new List<MoreRef>();
        var topMores      = new List<MoreRef>();
        var moresByParent = new Dictionary<string, List<MoreRef>>(StringComparer.Ordinal);   // 親コメント id (接頭辞なし) → その下の続き (今回見たもの全部)
        var continueIds   = new HashSet<string>(StringComparer.Ordinal);
        var requested     = new HashSet<string>(StringComparer.Ordinal);   // morechildren で頼んだ id
        // 4 で取りに行く枝: 親コメント id → 優先度の計算に使う続き。「continue this thread」は続きの中身が分からないので空のまま
        var parents = new Dictionary<string, List<MoreRef>>(StringComparer.Ordinal);
        var tried   = new Dictionary<string, (bool Old, bool New, int Stored)>(StringComparer.Ordinal);   // 4 で取った枝と並び・増えた件数
        // 4 の枝の取り方: まだ返信を保存していない枝は古い順。既に保存している枝と、今回古い順で取ってもまだ残る枝は新しい順。両方取った枝はもう取らない
        string? NextSort(string id)
        {
            var t = tried.TryGetValue(id, out var x) ? x : default;
            if (!t.Old && options.KnownParentIds?.Contains("t1_" + id) != true) return "old";
            return t.New ? null : "new";
        }
        bool needNewest = false, newestDone = false, topDone = options.KnownExternalIds.Count > 0;
        void Collect(IEnumerable<MoreRef> mores, IEnumerable<string> conts)
        {
            foreach (var m in mores)
            {
                allMores.Add(m);
                if (m.ParentId.StartsWith("t1_", StringComparison.Ordinal))
                {
                    var p = m.ParentId[3..];
                    if (!moresByParent.TryGetValue(p, out var all)) moresByParent[p] = all = new List<MoreRef>();
                    all.Add(m);
                    if (!m.Children.Any(Unknown) || NextSort(p) is null) continue;
                    if (!parents.TryGetValue(p, out var refs)) parents[p] = refs = new List<MoreRef>();
                    refs.Add(m);
                }
                else
                {
                    topMores.Add(m);
                    if (m.Children.Any(Unknown)) needNewest = true;
                }
            }
            foreach (var c in conts)
            {
                continueIds.Add(c);
                if (NeedContinue(c) && !tried.ContainsKey(c) && !parents.ContainsKey(c)) parents[c] = new List<MoreRef>();
            }
        }
        // 4 の枝の優先度 = その下でまだ持っていない (かつ取れないと分かっていない) コメントの数。
        // 「continue this thread」は中身が分からないので中くらいの値とする。0 の枝は取らない
        int Score(List<MoreRef> refs) => refs.Count == 0 ? ContinueScore : refs.Sum(r => r.Children.Count(Unknown));
        void Prune()
        {
            foreach (var k in parents.Where(kv => Score(kv.Value) == 0 || NextSort(kv.Key) is null).Select(kv => kv.Key).ToList()) parents.Remove(k);
        }
        // morechildren で次に頼む id (続きの大きい塊から、まだ頼んでいない未取得の id を最大 100 件)
        List<string> NextBatch()
        {
            var batch = new List<string>();
            foreach (var m in allMores.OrderByDescending(m => m.Count))
                foreach (var id in m.Children)
                {
                    if (batch.Count >= MoreChildrenBatch) return batch;
                    if (Unknown(id) && requested.Add(id)) batch.Add(id);
                }
            return batch;
        }
        Collect(tree.Mores, tree.Continues);
        Prune();
        if (!needNewest) topDone = true;   // トップレベルの続きが無い (小さいスレ) なら評価順も要らない

        var calls = 0;
        string? truncatedReason = null;
        while (calls < options.MaxExpansions)
        {
            if (remaining is double rem && rem < ReserveRequests)
            {
                var minutes = RateLimitResetMinutes();
                ChBrowser.Services.Logging.LogService.Instance.Write($"[reddit] more 展開を打ち切り (ratelimit remaining={rem}, 回復まで約 {minutes} 分)");
                truncatedReason = $"reddit の要求回数の上限が近いため続きの取得を中断しました。約 {minutes} 分後に更新すると続きを取得します";
                break;
            }
            string? mUrl = null, what = null, parent = null, sort = null;
            List<string>? batch = null;
            if (needNewest && !newestDone)
            {
                newestDone = true;
                mUrl = $"{Origin}/r/{board.DirectoryName}/comments/{threadKey}.json?sort=new&limit={CommentLimit}&raw_json=1";
                what = "新しい順";
            }
            else if (!topDone)
            {
                topDone = true;
                mUrl = $"{Origin}/r/{board.DirectoryName}/comments/{threadKey}.json?sort=top&limit={CommentLimit}&raw_json=1";
                what = "評価の高い順";
            }
            else if (!moreChildrenBlocked && (batch = NextBatch()).Count > 0)
            {
                what = $"morechildren {batch.Count} 件";
            }
            // 親コメント起点: morechildren が使えないときは全部の枝、使えるときは「continue this thread」(ID の無い続き) だけ
            else if (parents.Keys.Where(k => moreChildrenBlocked || continueIds.Contains(k)).ToList() is { Count: > 0 } candidates)
            {
                batch  = null;
                parent = candidates.OrderByDescending(k => Score(parents[k])).First();
                parents.Remove(parent);
                sort = NextSort(parent)!;
                mUrl = $"{Origin}/r/{board.DirectoryName}/comments/{threadKey}/_/{parent}.json?sort={sort}&limit={CommentLimit}&raw_json=1";
                what = $"t1_{parent} 以下, {(sort == "old" ? "古い順" : "新しい順")}";
            }
            else break;
            calls++;

            List<SnapshotPost> comments;
            List<MoreRef> mores;
            List<string> conts;
            if (batch is not null)
            {
                var got = await FetchMoreChildrenAsync(http, threadKey, batch, ct).ConfigureAwait(false);
                if (got is null)
                {
                    // 弾かれた: この回 (と以降の取得) は親コメント起点の取り方に切り替える。頼んだ id は頼み直せるよう戻す
                    moreChildrenBlocked = true;
                    s_moreChildrenBlockedUntil = DateTime.UtcNow + MoreChildrenRetryAfter;
                    foreach (var id in batch) requested.Remove(id);
                    continue;
                }
                (bytes, remaining) = got.Value;
                (comments, mores, conts) = ParseMoreChildrenResponse(bytes);
            }
            else
            {
                try
                {
                    using (ProviderRequestContext.Optional())
                        (bytes, remaining) = await GetAsync(http, mUrl!, ct).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    if (parent is not null) parents[parent] = new List<MoreRef>();   // 取れなかった枝は「続きあり」として残す
                    ChBrowser.Services.Logging.LogService.Instance.Write($"[reddit] more 展開を打ち切り ({what} の取得に失敗: {ex.Message})");
                    break;
                }
                var sub = ParseCommentsResponse(bytes);
                (comments, mores, conts) = (sub.Comments, sub.Mores, sub.Continues);
            }

            var before = posts.Count;
            var stored = 0;
            foreach (var c in comments)
                if (have.Add(c.ExternalId))
                {
                    posts.Add(c);
                    if (!options.KnownExternalIds.Contains(c.ExternalId)) stored++;
                }
            if (parent is not null)
            {
                var t = tried.TryGetValue(parent, out var x) ? x : default;
                tried[parent] = sort == "old" ? (true, t.New, t.Stored + stored) : (t.Old, true, t.Stored + stored);
            }
            ChBrowser.Services.Logging.LogService.Instance.Write($"[reddit] 続き ({what}): {posts.Count - before} 件 (うち未保存 {stored} 件)");
            Collect(mores, conts);
            Prune();
            await ReportPartialAsync().ConfigureAwait(false);
        }

        // 取りに行っても取れなかった分を記録する:
        //   morechildren: 頼んだのに返ってこなかった id (削除済み等)
        //   (morechildren が使えないとき) トップレベル: 新しい順まで取って残った分 (スレの真ん中)
        //   (同) 枝: 新しい順まで取って残った分、または古い順で 1 件も増えなかった枝の分 (古い順で増えた枝は、次回新しい順で取る)
        //   「continue this thread」: 取って 1 件も増えなかったもの (削除済み等)
        var newlyUnreachable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in requested)
            if (Unknown(id)) newlyUnreachable.Add(GoneKey(id));
        void MarkRest(IEnumerable<MoreRef> mores)
        {
            foreach (var m in mores)
                foreach (var id in m.Children)
                    if (Unknown(id)) newlyUnreachable.Add(NoBranchKey(id));
        }
        if (moreChildrenBlocked && newestDone) MarkRest(topMores);
        foreach (var (id, t) in tried)
        {
            if (t.New || t.Stored == 0)
                MarkRest(moresByParent.TryGetValue(id, out var ms) ? ms : new List<MoreRef>());
            if (t.Stored == 0 && continueIds.Contains(id)) newlyUnreachable.Add(ContinueKey(id));
        }

        var truncated = parents.Count > 0 || (needNewest && !newestDone)
                        || allMores.Any(m => m.Children.Any(id => Unknown(id) && !newlyUnreachable.Contains(GoneKey(id)) && !newlyUnreachable.Contains(NoBranchKey(id))));
        ChBrowser.Services.Logging.LogService.Instance.Write(
            $"[reddit] snapshot r/{board.DirectoryName}/{threadKey}: posts={posts.Count}, more calls={calls}, truncated={truncated}, " +
            $"morechildren={(moreChildrenBlocked ? "使えない" : "使用")}, 取れない分 前回まで={unreachable.Count} 今回={newlyUnreachable.Count}");
        return new ThreadSnapshot(posts, truncated, Unreachable: newlyUnreachable, TruncatedReason: truncated ? truncatedReason : null);
    }

    /// <summary>morechildren が弾かれたら、しばらく (<see cref="MoreChildrenRetryAfter"/>) は送らずに親コメント起点の取り方を使う
    /// (毎回弾かれる要求を送らない)。</summary>
    private static DateTime s_moreChildrenBlockedUntil;
    private static readonly TimeSpan MoreChildrenRetryAfter = TimeSpan.FromMinutes(30);
    /// <summary>morechildren を送る先。<c>www.reddit.com/api/morechildren.json</c> は 2026-10 から弾かれるが、末尾にスラッシュを付けた
    /// <c>/api/morechildren/.json</c> と <c>old.reddit.com</c> はログイン中なら通る (接続テストで確認、2026-10-29)。前者から順に試し、通った方を使い続ける。</summary>
    private static readonly string[] MoreChildrenEndpoints =
    {
        $"{Origin}/api/morechildren/.json",
        "https://old.reddit.com/api/morechildren.json",
    };
    private static int s_moreChildrenEndpoint;

    /// <summary>「続き」(morechildren) を取る。補助の要求として送り (弾かれても窓を出さない)、送り先を順に試す。どれも駄目なら null。</summary>
    private async Task<(byte[] Bytes, double? Remaining)?> FetchMoreChildrenAsync(HttpClient http, string threadKey, List<string> batch, CancellationToken ct)
    {
        var query = $"api_type=json&link_id=t3_{threadKey}&children={string.Join(",", batch)}&sort=old&raw_json=1";
        using var optional = ProviderRequestContext.Optional();
        for (var i = s_moreChildrenEndpoint; i < MoreChildrenEndpoints.Length; i++)
        {
            try
            {
                var got = await GetAsync(http, $"{MoreChildrenEndpoints[i]}?{query}", ct).ConfigureAwait(false);
                if (i != s_moreChildrenEndpoint)
                    ChBrowser.Services.Logging.LogService.Instance.Write($"[reddit] 続きの取得 (morechildren) の送り先を {MoreChildrenEndpoints[i]} に切り替えました");
                s_moreChildrenEndpoint = i;
                return got;
            }
            catch (HttpRequestException ex)
            {
                ChBrowser.Services.Logging.LogService.Instance.Write($"[reddit] 続きの取得 (morechildren, {MoreChildrenEndpoints[i]}) に失敗: {ex.Message}");
            }
        }
        return null;
    }

    /// <summary>直近の応答の「レート制限が回復するまでの秒数」(x-ratelimit-reset) と受け取った時刻。</summary>
    private static (double Seconds, DateTime At)? s_rateLimitReset;

    /// <summary>レート制限が回復するまでの分数 (切り上げ、最低 1)。分からなければ 10 (reddit の窓の長さ)。</summary>
    private static int RateLimitResetMinutes()
    {
        if (s_rateLimitReset is not { } r) return 10;
        var left = r.Seconds - (DateTime.UtcNow - r.At).TotalSeconds;
        return Math.Max(1, (int)Math.Ceiling(left / 60));
    }

    private static async Task<(byte[] Bytes, double? Remaining)> GetAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        if (resp.Headers.TryGetValues("x-ratelimit-reset", out var rs)
            && double.TryParse(rs.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var reset))
            s_rateLimitReset = (reset, DateTime.UtcNow);
        double? remaining = resp.Headers.TryGetValues("x-ratelimit-remaining", out var v)
            && double.TryParse(v.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : null;
        return (await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false), remaining);
    }

    /// <summary>「続き」1 つ。<see cref="Children"/> は t1 の id (接頭辞なし)。</summary>
    public sealed record MoreRef(string ParentId, int Count, IReadOnlyList<string> Children);

    public sealed record CommentTree(SnapshotPost? Submission, List<SnapshotPost> Comments, List<MoreRef> Mores, List<string> Continues);

    /// <summary>comments API の 2 要素配列 → 投稿 + コメント (木を平坦化、深さ優先) + 続き。</summary>
    public static CommentTree ParseCommentsResponse(byte[] bytes)
    {
        var comments = new List<SnapshotPost>();
        var mores    = new List<MoreRef>();
        var conts    = new List<string>();
        SnapshotPost? submission = null;
        if (!TryParse(bytes, out var doc)) return new CommentTree(null, comments, mores, conts);
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2) return new CommentTree(null, comments, mores, conts);
            foreach (var c in Children(root[0]))
                if (Str(c, "kind") == "t3" && c.TryGetProperty("data", out var d)) { submission = ToSubmission(d); break; }
            WalkListing(root[1], comments, mores, conts);
        }
        return new CommentTree(submission, comments, mores, conts);
    }

    /// <summary>morechildren の応答 (平坦な things) → コメント + 続き。</summary>
    private static (List<SnapshotPost> Comments, List<MoreRef> Mores, List<string> Continues) ParseMoreChildrenResponse(byte[] bytes)
    {
        var comments = new List<SnapshotPost>();
        var mores    = new List<MoreRef>();
        var conts    = new List<string>();
        if (!TryParse(bytes, out var doc)) return (comments, mores, conts);
        using (doc)
        {
            if (doc.RootElement.TryGetProperty("json", out var j) && j.TryGetProperty("data", out var d)
                && d.TryGetProperty("things", out var things) && things.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in things.EnumerateArray()) AddThing(t, comments, mores, conts, recurse: true);
            }
        }
        return (comments, mores, conts);
    }

    private static void WalkListing(JsonElement listing, List<SnapshotPost> comments, List<MoreRef> mores, List<string> conts)
    {
        foreach (var c in Children(listing)) AddThing(c, comments, mores, conts, recurse: true);
    }

    private static void AddThing(JsonElement thing, List<SnapshotPost> comments, List<MoreRef> mores, List<string> conts, bool recurse)
    {
        if (!thing.TryGetProperty("data", out var d)) return;
        switch (Str(thing, "kind"))
        {
            case "t1":
                comments.Add(ToComment(d));
                if (recurse && d.TryGetProperty("replies", out var rep) && rep.ValueKind == JsonValueKind.Object)
                    WalkListing(rep, comments, mores, conts);
                break;
            case "more":
                var ids = d.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array
                    ? ch.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                    : new List<string>();
                var parent = Str(d, "parent_id") ?? "";
                if (ids.Count == 0)
                {
                    // count 0 の more = 「continue this thread」(深すぎて打ち切られた枝)。親コメントの id で取り直す
                    if (parent.StartsWith("t1_", StringComparison.Ordinal)) conts.Add(parent[3..]);
                }
                else mores.Add(new MoreRef(parent, Int(d, "count"), ids));
                break;
        }
    }

    private static IEnumerable<JsonElement> Children(JsonElement listing)
    {
        if (listing.ValueKind == JsonValueKind.Object && listing.TryGetProperty("data", out var d)
            && d.TryGetProperty("children", out var c) && c.ValueKind == JsonValueKind.Array)
            foreach (var x in c.EnumerateArray()) yield return x;
    }

    private static SnapshotPost ToSubmission(JsonElement d)
    {
        var id      = Str(d, "id") ?? "";
        var author  = Str(d, "author") ?? "[deleted]";
        var created = Epoch(d, "created_utc") ?? 0;
        var flags   = new List<string>();
        AddDistinguished(d, flags);
        if (Bool(d, "over_18")) flags.Add("NSFW");
        if (Bool(d, "spoiler")) flags.Add("ネタバレ");
        if (Bool(d, "locked"))  flags.Add("ロック");

        var attachments = new List<PostAttachment>();
        var lines = new List<string>();
        var mdLines = new List<string>();   // 整形表示用の Markdown (本文と同じ並び。自己投稿の本文が Markdown のときだけ使う)
        if (Str(d, "link_flair_text") is { Length: > 0 } flair)
        {
            lines.Add("[" + flair + "]");
            mdLines.Add(ChBrowser.Services.Render.MarkdownBodyRenderer.EscapeText("[" + flair + "]"));
        }
        var self = RedditBodyConverter.Convert(Str(d, "selftext_html"));
        if (self.Length > 0) lines.Add(self);
        var selfMd = Str(d, "selftext");
        if (self.Length > 0 && !string.IsNullOrWhiteSpace(selfMd)) mdLines.Add(selfMd);

        // ギャラリー: gallery_data の順に media_metadata の元画像
        if (Bool(d, "is_gallery") && d.TryGetProperty("media_metadata", out var mm) && mm.ValueKind == JsonValueKind.Object)
        {
            var order = new List<string>();
            if (d.TryGetProperty("gallery_data", out var gd) && gd.TryGetProperty("items", out var gi) && gi.ValueKind == JsonValueKind.Array)
                foreach (var item in gi.EnumerateArray()) if (Str(item, "media_id") is { } mid) order.Add(mid);
            if (order.Count == 0) foreach (var p in mm.EnumerateObject()) order.Add(p.Name);
            foreach (var mid in order)
            {
                if (!mm.TryGetProperty(mid, out var meta) || !meta.TryGetProperty("s", out var s)) continue;
                var u = Str(s, "u") ?? Str(s, "gif") ?? Str(s, "mp4");
                if (u is null) continue;
                attachments.Add(new PostAttachment(u, Width: Int(s, "x"), Height: Int(s, "y"),
                    Kind: Str(s, "mp4") is not null ? "video" : "image"));
            }
        }
        // 動画 (reddit ホスト): 音声なしの mp4
        if (d.TryGetProperty("secure_media", out var sm) && sm.ValueKind == JsonValueKind.Object
            && sm.TryGetProperty("reddit_video", out var rv) && Str(rv, "fallback_url") is { } fb)
        {
            attachments.Add(new PostAttachment(fb, Width: Int(rv, "width"), Height: Int(rv, "height"), Kind: "video"));
        }
        foreach (var a in attachments) { lines.Add(a.Url); mdLines.Add(a.Url); }   // 裸の URL (自動リンクになる)

        // リンク投稿: 先の URL (画像直リンクならそのまま画像として出る)。ギャラリー / 動画 / 自分自身へのリンクは除く
        var link = Str(d, "url_overridden_by_dest") ?? Str(d, "url");
        if (!Bool(d, "is_self") && attachments.Count == 0 && link is { Length: > 0 }
            && !link.Contains("/comments/" + id, StringComparison.Ordinal))
        {
            var abs = link.StartsWith("/", StringComparison.Ordinal) ? Origin + link : link;
            lines.Add(abs);
            mdLines.Add(abs);
            if (Str(d, "post_hint") == "image") attachments.Add(new PostAttachment(link, Kind: "image"));
        }
        if (Str(d, "removed_by_category") is { Length: > 0 } removed)
        {
            lines.Add($"[削除されました: {removed}]");
            mdLines.Add(ChBrowser.Services.Render.MarkdownBodyRenderer.EscapeText($"[削除されました: {removed}]"));
        }

        var permalink = Str(d, "permalink") is { } pl ? Origin + pl : null;
        return new SnapshotPost(
            ExternalId:       "t3_" + id,
            ParentExternalId: null,
            CreatedEpoch:     created,
            Name:             author,
            Mail:             string.Join(" ", flags),
            DateText:         FormatDate(created),
            Id:               author,
            Body:             string.Join("\n\n", lines),
            ThreadTitle:      (Str(d, "title") ?? "").Replace('\n', ' ').Trim(),
            Ext: new PostExtra(
                Score:       ScoreOf(d),
                EditedEpoch: Edited(d),
                Permalink:   permalink,
                Attachments: attachments.Count > 0 ? attachments : null,
                MyVote:      LikesOf(d),
                AuthorId:    Str(d, "author_fullname"),
                // 本文が Markdown の自己投稿だけ (リンク投稿・画像投稿は整形するものが無いので今までどおり)
                Markdown:    !string.IsNullOrWhiteSpace(selfMd) && self.Length > 0 ? string.Join("\n\n", mdLines) : null));
    }

    private static SnapshotPost ToComment(JsonElement d)
    {
        var id      = Str(d, "id") ?? "";
        var author  = Str(d, "author") ?? "[deleted]";
        var created = Epoch(d, "created_utc") ?? 0;
        var flags   = new List<string>();
        if (Bool(d, "is_submitter")) flags.Add("OP");
        AddDistinguished(d, flags);
        if (Bool(d, "stickied")) flags.Add("固定");
        var body = RedditBodyConverter.Convert(Str(d, "body_html"));
        var markdown = body.Length > 0 && Str(d, "body") is { Length: > 0 } md ? md : null;   // 整形表示用の元の Markdown
        if (body.Length == 0) body = (Str(d, "body") ?? "").Replace("<", "＜");
        var permalink = Str(d, "permalink") is { } pl ? Origin + pl : null;
        return new SnapshotPost(
            ExternalId:       Str(d, "name") ?? "t1_" + id,
            ParentExternalId: Str(d, "parent_id"),
            CreatedEpoch:     created,
            Name:             author,
            Mail:             string.Join(" ", flags),
            DateText:         FormatDate(created),
            Id:               author,
            Body:             body,
            ThreadTitle:      null,
            Ext: new PostExtra(
                Depth:       d.TryGetProperty("depth", out var dp) && dp.ValueKind == JsonValueKind.Number ? dp.GetInt32() : null,
                Score:       ScoreOf(d),
                EditedEpoch: Edited(d),
                Permalink:   permalink,
                MyVote:      LikesOf(d),
                AuthorId:    Str(d, "author_fullname"),
                Markdown:    markdown));
    }

    /// <summary>評価値。<c>score_hidden</c> (投稿直後は隠す subreddit がある) なら null (偽の 1 を出さない)。</summary>
    private static long? ScoreOf(JsonElement d) => Bool(d, "score_hidden") ? null : Long(d, "score");

    /// <summary>取得したアカウントの評価 (<c>likes</c>: true = 賛成、false = 反対、null = なし) を 1 / -1 / null に。</summary>
    private static int? LikesOf(JsonElement d)
        => d.TryGetProperty("likes", out var l) ? l.ValueKind switch { JsonValueKind.True => 1, JsonValueKind.False => -1, _ => null } : null;

    private static void AddDistinguished(JsonElement d, List<string> flags)
    {
        switch (Str(d, "distinguished"))
        {
            case "moderator": flags.Add("MOD");   break;
            case "admin":     flags.Add("ADMIN"); break;
        }
    }

    /// <summary>5ch と同じ書式 (ローカル時刻): <c>2026/09/23(火) 12:34:56</c>。</summary>
    public static string FormatDate(long epoch) => BbsDate.FromEpoch(epoch);

    // -----------------------------------------------------------------
    // 書き込み (段階 5-B で実装)
    // -----------------------------------------------------------------

    public PostFormSpec PostForm { get; } = new(
        SupportsName: false, SupportsMail: false, SupportsNewThread: true, UsesDonguriAuth: false,
        BodyFormat: PostBodyFormat.Markdown, RequiresLogin: true, SupportsReplyTarget: true);

    /// <summary>レス: <c>/api/comment</c> (<c>thing_id</c> = 返信先。コメント id か、スレ本体なら <c>t3_&lt;key&gt;</c>)。
    /// スレ立て: <c>/api/submit</c> (テキスト投稿 <c>kind=self</c>)。本文は Markdown のまま送る。UTF-8 のフォーム。</summary>
    public PostSubmission BuildPostSubmission(PostRequest req)
    {
        var text = (req.Message ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        var fields = new List<KeyValuePair<string, string>> { new("api_type", "json") };
        if (req.IsNewThread)
        {
            fields.Add(new("sr",          req.Board.DirectoryName));
            fields.Add(new("kind",        "self"));
            fields.Add(new("title",       (req.Subject ?? "").Trim()));
            fields.Add(new("text",        text));
            fields.Add(new("sendreplies", "true"));
            return new PostSubmission(new Uri(SubmitEndpoint), fields, Encoding.UTF8, "UTF-8",
                BoardUrl(CanonicalHost, req.Board.DirectoryName));
        }
        var target = string.IsNullOrEmpty(req.ReplyTargetExternalId) ? "t3_" + req.ThreadKey : req.ReplyTargetExternalId;
        fields.Add(new("thing_id", target));
        fields.Add(new("text",     text));
        return new PostSubmission(new Uri(CommentEndpoint), fields, Encoding.UTF8, "UTF-8",
            ThreadUrl(CanonicalHost, req.Board.DirectoryName, req.ThreadKey ?? ""));
    }

    /// <summary>書き込み応答 (api_type=json) を判定する。
    /// <c>json.errors</c> が空なら成功 (新しい投稿の id を <see cref="PostResult.NewPostExternalId"/> に)、
    /// <c>RATELIMIT</c> は規制扱い (「少し待って」の文言付き)、<c>USER_REQUIRED</c> / 401 / 403 はログインが必要。</summary>
    public PostResult ClassifyPostResponse(string html, PostResponseContext context)
    {
        var body = html ?? "";
        var snippet = body.Length > 400 ? body[..400] : body;
        if (context.StatusCode is 401 or 403)
            return new PostResult(PostOutcome.AuthRequired,
                "reddit へのログインが必要です。ステータスバーの reddit 表示 (または 設定 → 認証 → reddit) からログインしてから、もう一度送信してください。",
                snippet);
        if (!TryParse(Encoding.UTF8.GetBytes(body), out var doc))
            return new PostResult(PostOutcome.UnknownError, $"reddit の応答を解釈できませんでした (HTTP {context.StatusCode})", snippet);
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("json", out var json))
                return new PostResult(PostOutcome.UnknownError, $"reddit の応答が想定外です (HTTP {context.StatusCode})", snippet);

            if (json.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                var first = errors[0];
                var code  = first.ValueKind == JsonValueKind.Array && first.GetArrayLength() > 0 ? first[0].GetString() ?? "" : "";
                var msg   = first.ValueKind == JsonValueKind.Array && first.GetArrayLength() > 1 ? first[1].GetString() ?? "" : first.ToString();
                return code switch
                {
                    "USER_REQUIRED" => new PostResult(PostOutcome.AuthRequired,
                        "reddit へのログインが必要です。ステータスバーの reddit 表示からログインしてから、もう一度送信してください。", snippet),
                    "RATELIMIT" => new PostResult(PostOutcome.BlockedByRule, $"投稿間隔の制限です。少し待ってから送信してください ({msg})", snippet),
                    _ => new PostResult(PostOutcome.UnknownError, $"{code}: {msg}", snippet),
                };
            }

            // 成功: レスは things[0] (t1 全体)、スレ立ては data.name
            string? newId = null;
            if (json.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("things", out var things) && things.ValueKind == JsonValueKind.Array && things.GetArrayLength() > 0
                    && things[0].TryGetProperty("data", out var t))
                {
                    newId = Str(t, "name");
                    if (Str(things[0], "kind") == "t1" && Str(t, "link_id") is { Length: > 3 } link)
                        RememberPostedComment(link[3..], ToComment(t));
                }
                newId ??= Str(data, "name");
            }
            return new PostResult(PostOutcome.Success, "", snippet, NewPostExternalId: newId);
        }
    }

    // -----------------------------------------------------------------
    // 評価 (upvote / downvote)
    // -----------------------------------------------------------------

    /// <summary>賛成 (👍)・反対 (👎)・取り消し。評価値は賛成 − 反対。</summary>
    public VoteSpec? Voting { get; } = new(VoteMode.UpDown);

    public const string VoteEndpoint = Origin + "/api/vote";

    /// <summary><c>POST /api/vote</c> (<c>id</c> = t1_ / t3_、<c>dir</c> = 1 / 0 / -1)。modhash は通信経路が付ける。</summary>
    public async Task<VoteResult> VoteAsync(HttpClient http, Board board, string threadKey, Post post, int direction, CancellationToken ct)
    {
        var id = post.Ext?.ExternalId;
        if (string.IsNullOrEmpty(id)) return new VoteResult(false, "投稿の ID が分かりません");
        using var content = new FormUrlEncodedContent(BuildVoteForm(id, direction));
        using var resp    = await http.PostAsync(VoteEndpoint, content, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var result = ClassifyVoteResponse((int)resp.StatusCode, body);
        ChBrowser.Services.Logging.LogService.Instance.Write(
            $"[reddit] vote {id} dir={direction}: HTTP {(int)resp.StatusCode} ok={result.Ok} {result.Message}");
        return result;
    }

    public static IReadOnlyList<KeyValuePair<string, string>> BuildVoteForm(string externalId, int direction) => new KeyValuePair<string, string>[]
    {
        new("id",       externalId),
        new("dir",      Math.Sign(direction).ToString(CultureInfo.InvariantCulture)),
        new("api_type", "json"),
    };

    /// <summary>評価の応答を判定する。成功は 200 で <c>{}</c> (または <c>json.errors</c> が空)。
    /// 401 / 403 / <c>USER_REQUIRED</c> はログインが必要、それ以外は理由 (<c>reason</c> / <c>explanation</c> / <c>message</c>) を返す
    /// (アーカイブ済み・ロック中のスレは 400 等で断られる)。</summary>
    public static VoteResult ClassifyVoteResponse(int status, string body)
    {
        if (status is 401 or 403) return new VoteResult(false, "ログインが必要です", AuthRequired: true);
        string? reason = null;
        if (TryParse(Encoding.UTF8.GetBytes(body ?? ""), out var doc))
        {
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("json", out var json) && json.TryGetProperty("errors", out var errors)
                        && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                    {
                        var first = errors[0];
                        var code  = first.ValueKind == JsonValueKind.Array && first.GetArrayLength() > 0 ? first[0].GetString() ?? "" : "";
                        var msg   = first.ValueKind == JsonValueKind.Array && first.GetArrayLength() > 1 ? first[1].GetString() ?? "" : first.ToString();
                        if (code == "USER_REQUIRED") return new VoteResult(false, "ログインが必要です", AuthRequired: true);
                        return new VoteResult(false, code.Length > 0 ? $"{code}: {msg}" : msg);
                    }
                    reason = Str(root, "explanation") ?? Str(root, "reason") ?? Str(root, "message");
                }
            }
        }
        if (status is >= 200 and < 300) return VoteResult.Success;
        return new VoteResult(false, reason is { Length: > 0 } ? $"{reason} (HTTP {status})" : $"HTTP {status}");
    }

    // -----------------------------------------------------------------
    // 投稿者情報 (アイコン・プロフィール)
    // -----------------------------------------------------------------

    public bool SupportsAuthorProfiles => true;
    public string? AuthorProfileUrlTemplate => $"{Origin}/user/{{name}}/";

    /// <summary>本文は Markdown (reddit の記法)。相対リンク (<c>/r/xxx</c>) は reddit へ、<c>&gt;!…!&lt;</c> はネタバレ。</summary>
    public ChBrowser.Services.Render.MarkdownBodySpec MarkdownBody { get; } = new(Origin, Spoilers: true);

    /// <summary>1 回の取得で問い合わせる上限 (100 人 × 3 回)。残りは次にスレを開いた / 更新したときに取る。</summary>
    public const int AuthorBatchSize = 100, MaxAuthorBatches = 3;

    /// <summary><c>/api/user_data_by_account_ids</c> (1 回 100 人まで。見つからない ID は無視され、全部見つからなければ 404)。
    /// レート制限の残りが少なければ打ち切る (スレを開く操作に予算を残す)。</summary>
    public async Task<AuthorProfileFetch> FetchAuthorProfilesAsync(
        HttpClient http, IReadOnlyCollection<string> authorIds, CancellationToken ct)
    {
        var result = new Dictionary<string, AuthorProfile>(StringComparer.Ordinal);
        var asked  = new List<string>();
        var ids = authorIds.Where(i => i.StartsWith("t2_", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        for (var b = 0; b < ids.Count && b / AuthorBatchSize < MaxAuthorBatches; b += AuthorBatchSize)
        {
            var batch = ids.Skip(b).Take(AuthorBatchSize).ToList();
            var url = $"{Origin}/api/user_data_by_account_ids.json?ids={string.Join(",", batch)}&raw_json=1";
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if ((int)resp.StatusCode == 404) { asked.AddRange(batch); continue; }   // 1 人も見つからない (退会済み等)
            resp.EnsureSuccessStatusCode();
            asked.AddRange(batch);
            foreach (var (id, p) in ParseUserData(await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false))) result[id] = p;
            if (resp.Headers.TryGetValues("x-ratelimit-remaining", out var v)
                && double.TryParse(v.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rem) && rem < ReserveRequests)
            {
                ChBrowser.Services.Logging.LogService.Instance.Write($"[reddit] 投稿者情報の取得を打ち切り (ratelimit remaining={rem})");
                break;
            }
        }
        return new AuthorProfileFetch(result, asked);
    }

    /// <summary><c>user_data_by_account_ids</c> の応答 (アカウント ID → name / created_utc / link_karma / comment_karma / profile_img / profile_over_18)。</summary>
    public static IReadOnlyDictionary<string, AuthorProfile> ParseUserData(byte[] bytes)
    {
        var result = new Dictionary<string, AuthorProfile>(StringComparer.Ordinal);
        if (!TryParse(bytes, out var doc)) return result;
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var u in doc.RootElement.EnumerateObject())
            {
                if (u.Value.ValueKind != JsonValueKind.Object || Str(u.Value, "name") is not { Length: > 0 } name) continue;
                var stats   = new List<AuthorStat>();
                var link    = Long(u.Value, "link_karma");
                var comment = Long(u.Value, "comment_karma");
                if (link is not null)    stats.Add(new AuthorStat("投稿カルマ",     link.Value.ToString("N0", CultureInfo.InvariantCulture)));
                if (comment is not null) stats.Add(new AuthorStat("コメントカルマ", comment.Value.ToString("N0", CultureInfo.InvariantCulture)));
                result[u.Name] = new AuthorProfile(
                    Name:         name,
                    DisplayName:  "u/" + name,
                    IconUrl:      Str(u.Value, "profile_img") is { Length: > 0 } img ? img : null,
                    ProfileUrl:   $"{Origin}/user/{name}/",
                    CreatedEpoch: Epoch(u.Value, "created_utc"),
                    Stats:        stats,
                    Nsfw:         Bool(u.Value, "profile_over_18"));
            }
        }
        return result;
    }

    // このアプリから書き込んだコメント (スレ key → 書き込み応答のコメント)。次のスレ取得で確実に取り込むために持っておく。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<SnapshotPost>> _postedComments = new(StringComparer.Ordinal);

    private static void RememberPostedComment(string threadKey, SnapshotPost post)
        => _postedComments.AddOrUpdate(threadKey, _ => new List<SnapshotPost> { post }, (_, list) => { lock (list) list.Add(post); return list; });

    private static IReadOnlyList<SnapshotPost> TakePostedComments(string threadKey)
        => _postedComments.TryRemove(threadKey, out var list) ? list : Array.Empty<SnapshotPost>();

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

    private static bool Bool(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int Int(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
           && v.TryGetDouble(out var x) ? (int)x : 0;

    private static long? Long(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
           && v.TryGetDouble(out var x) ? (long)x : null;

    private static long? Epoch(JsonElement e, string name) => Long(e, name);

    /// <summary>edited は false か epoch 秒。</summary>
    private static long? Edited(JsonElement d)
        => d.TryGetProperty("edited", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var x) && x > 0 ? (long)x : null;
}
