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
/// <item><description>スレはスナップショット方式: 投稿 + コメント木を取り、<c>more</c> を <c>/api/morechildren</c> で展開 (1 回 100 件、回数上限あり、
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
    /// <summary>morechildren 1 回で送る id の数 (実測で 100 程度が上限)。</summary>
    public const int MoreChildrenBatch = 100;
    /// <summary>レート制限の残りがこれを下回ったら more の展開を打ち切る (一覧・スレを開く操作に予算を残す。D38)。</summary>
    public const int ReserveRequests = 30;

    private static readonly Regex BoardPathRegex = new(
        @"^/r/(?<dir>[A-Za-z0-9_]+)(?:/(?:hot|new|top|rising|controversial|best))?/?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ThreadPathRegex = new(
        @"^/r/(?<dir>[A-Za-z0-9_]+)/comments/(?<key>[A-Za-z0-9]+)(?:/(?<slug>[^/]*)(?:/(?<cid>[A-Za-z0-9]+))?)?/?$", RegexOptions.Compiled);

    private static readonly CultureInfo Ja = CultureInfo.GetCultureInfo("ja-JP");

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
        var url = $"{Origin}/subreddits/search.json?q={Uri.EscapeDataString(keyword)}&limit=50&raw_json=1";
        var (bytes, _) = await GetAsync(http, url, ct).ConfigureAwait(false);
        return ParseSubredditListing(bytes);
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

        // 続き (more) の展開: 未取得かつ既知でない id を大きい塊から 100 件ずつ
        var pending = new List<string>();
        foreach (var more in tree.Mores.OrderByDescending(m => m.Count))
            foreach (var id in more.Children)
                if (!have.Contains("t1_" + id) && !options.KnownExternalIds.Contains("t1_" + id)) pending.Add(id);
        // 「continue this thread」は、その親が既に返信を持っていれば (= 前回取り直し済み) 省く
        bool NeedContinue(string parent) => options.KnownParentIds?.Contains("t1_" + parent) != true;
        var continues = new Queue<string>(tree.Continues.Where(NeedContinue));

        var calls = 0;
        var queue = new Queue<string>(pending.Distinct(StringComparer.Ordinal));
        while ((queue.Count > 0 || continues.Count > 0) && calls < options.MaxExpansions)
        {
            if (remaining is double rem && rem < ReserveRequests)
            {
                ChBrowser.Services.Logging.LogService.Instance.Write($"[reddit] more 展開を打ち切り (ratelimit remaining={rem})");
                break;
            }
            calls++;
            MoreResult more;
            if (queue.Count > 0)
            {
                var batch = new List<string>();
                while (queue.Count > 0 && batch.Count < MoreChildrenBatch) batch.Add(queue.Dequeue());
                var mcUrl = $"{Origin}/api/morechildren.json?api_type=json&link_id=t3_{threadKey}&children={string.Join(",", batch)}&sort=old&raw_json=1";
                (bytes, remaining) = await GetAsync(http, mcUrl, ct).ConfigureAwait(false);
                more = ParseMoreChildrenResponse(bytes);
            }
            else
            {
                // 「continue this thread」: その枝だけをスレとして取り直す
                var parent = continues.Dequeue();
                var cUrl = $"{Origin}/r/{board.DirectoryName}/comments/{threadKey}/_/{parent}.json?sort=old&limit={CommentLimit}&raw_json=1";
                (bytes, remaining) = await GetAsync(http, cUrl, ct).ConfigureAwait(false);
                var sub = ParseCommentsResponse(bytes);
                more = new MoreResult(sub.Comments, sub.Mores, sub.Continues);
            }
            foreach (var c in more.Comments)
                if (have.Add(c.ExternalId)) posts.Add(c);
            foreach (var m in more.Mores.OrderByDescending(m => m.Count))
                foreach (var id in m.Children)
                    if (!have.Contains("t1_" + id) && !options.KnownExternalIds.Contains("t1_" + id)) queue.Enqueue(id);
            foreach (var c in more.Continues) if (NeedContinue(c)) continues.Enqueue(c);
            await ReportPartialAsync().ConfigureAwait(false);
        }

        var truncated = queue.Any(id => !have.Contains("t1_" + id)) || continues.Count > 0;
        ChBrowser.Services.Logging.LogService.Instance.Write(
            $"[reddit] snapshot r/{board.DirectoryName}/{threadKey}: posts={posts.Count}, more calls={calls}, truncated={truncated}");
        return new ThreadSnapshot(posts, truncated);
    }

    private static async Task<(byte[] Bytes, double? Remaining)> GetAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        double? remaining = resp.Headers.TryGetValues("x-ratelimit-remaining", out var v)
            && double.TryParse(v.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : null;
        return (await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false), remaining);
    }

    /// <summary>「続き」1 つ。<see cref="Children"/> は t1 の id (接頭辞なし)。</summary>
    public sealed record MoreRef(string ParentId, int Count, IReadOnlyList<string> Children);

    public sealed record CommentTree(SnapshotPost? Submission, List<SnapshotPost> Comments, List<MoreRef> Mores, List<string> Continues);
    private sealed record MoreResult(List<SnapshotPost> Comments, List<MoreRef> Mores, List<string> Continues);

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
    private static MoreResult ParseMoreChildrenResponse(byte[] bytes)
    {
        var comments = new List<SnapshotPost>();
        var mores    = new List<MoreRef>();
        var conts    = new List<string>();
        if (!TryParse(bytes, out var doc)) return new MoreResult(comments, mores, conts);
        using (doc)
        {
            if (doc.RootElement.TryGetProperty("json", out var j) && j.TryGetProperty("data", out var d)
                && d.TryGetProperty("things", out var things) && things.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in things.EnumerateArray()) AddThing(t, comments, mores, conts, recurse: true);
            }
        }
        return new MoreResult(comments, mores, conts);
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
        if (Str(d, "link_flair_text") is { Length: > 0 } flair) lines.Add("[" + flair + "]");
        var self = RedditBodyConverter.Convert(Str(d, "selftext_html"));
        if (self.Length > 0) lines.Add(self);

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
        foreach (var a in attachments) lines.Add(a.Url);

        // リンク投稿: 先の URL (画像直リンクならそのまま画像として出る)。ギャラリー / 動画 / 自分自身へのリンクは除く
        var link = Str(d, "url_overridden_by_dest") ?? Str(d, "url");
        if (!Bool(d, "is_self") && attachments.Count == 0 && link is { Length: > 0 }
            && !link.Contains("/comments/" + id, StringComparison.Ordinal))
        {
            lines.Add(link.StartsWith("/", StringComparison.Ordinal) ? Origin + link : link);
            if (Str(d, "post_hint") == "image") attachments.Add(new PostAttachment(link, Kind: "image"));
        }
        if (Str(d, "removed_by_category") is { Length: > 0 } removed) lines.Add($"[削除されました: {removed}]");

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
                MyVote:      LikesOf(d)));
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
                MyVote:      LikesOf(d)));
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
    public static string FormatDate(long epoch)
    {
        if (epoch <= 0) return "";
        var t = DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime();
        return t.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture) + "(" + t.ToString("ddd", Ja) + ") " + t.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

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
