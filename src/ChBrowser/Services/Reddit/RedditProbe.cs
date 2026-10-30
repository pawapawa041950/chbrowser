using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ChBrowser.Services.Reddit;

/// <summary>reddit の接続テスト (設定 → 認証 → 「接続テスト」)。段階 A0 の実測 (<c>doc/reddit-design.md</c> §1.5) を
/// アプリ内のログインセッション経由で行い、結果を <c>data/reddit.com/probe.txt</c> に書き出す。
/// 要求は通常の HttpClient に投げるので、実際のアプリの通信と同じ経路 (WebView2 セッション) を通る。</summary>
public static class RedditProbe
{
    public static async Task<string> RunAsync(HttpClient http, string reportPath, CancellationToken ct, RedditWebViewHost? host = null)
    {
        var sb = new StringBuilder();
        void L(string s) { sb.AppendLine(s); ChBrowser.Services.Logging.LogService.Instance.Write("[redditProbe] " + s); }
        L($"reddit 接続テスト {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");

        // 0. セッションの診断 (窓を出さず、通信経路を通さずに直接確かめる)
        if (host is not null)
        {
            try
            {
                foreach (var line in await host.DiagnoseAsync(ct, Path.GetDirectoryName(reportPath)).ConfigureAwait(false)) L("0. " + line);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                L($"0. 診断に失敗: {ex.Message}");
            }
        }

        async Task<(int status, JsonDocument? json, string info)> Get(string url)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                var rl = string.Join(" ", new[] { "x-ratelimit-used", "x-ratelimit-remaining", "x-ratelimit-reset" }
                    .Select(h => resp.Headers.TryGetValues(h, out var v) ? $"{h[12..]}={v.First()}" : null).Where(x => x is not null));
                var info = $"{(int)resp.StatusCode} {resp.ReasonPhrase} {bytes.Length}B {sw.ElapsedMilliseconds}ms"
                         + (resp.Content.Headers.ContentType is { } cty ? $" [{cty.MediaType}]" : "")
                         + (rl.Length > 0 ? $" ratelimit({rl})" : " ratelimit(ヘッダ無し)");
                JsonDocument? doc = null;
                try { doc = JsonDocument.Parse(bytes); } catch (JsonException) { }
                return ((int)resp.StatusCode, doc, info);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return (0, null, $"失敗: {ex.Message} ({sw.ElapsedMilliseconds}ms)");
            }
        }

        // ここから先はアプリの通信経路 (HttpClient → WebView2 セッション) を通す。0. の診断と重なる要求は送らない
        // (reddit の要求回数の上限 10 分 100 回を接続テストで使い切らないため)。

        // 1. 一覧 (テストに使うスレを選ぶ)
        var (_, list, i1) = await Get("https://www.reddit.com/r/popular/hot.json?limit=25&raw_json=1");
        L($"1. r/popular/hot: {i1}");
        string? sub = null, id = null, permalink = null, title = null, videoUrl = null;
        if (list is not null && list.RootElement.TryGetProperty("data", out var ld))
        {
            var children = ld.GetProperty("children").EnumerateArray().Select(c => c.GetProperty("data")).ToList();
            L($"   件数={children.Count} after={Str(ld, "after")}");
            var best = children.OrderByDescending(c => c.TryGetProperty("num_comments", out var n) ? n.GetInt32() : 0).FirstOrDefault();
            if (best.ValueKind == JsonValueKind.Object)
            {
                sub = Str(best, "subreddit"); id = Str(best, "id"); permalink = Str(best, "permalink"); title = Str(best, "title");
                L($"   最多コメントの投稿: r/{sub} {id} num_comments={best.GetProperty("num_comments").GetInt32()} title={Trunc(title, 60)}");
            }
            foreach (var c in children)
                if (c.TryGetProperty("secure_media", out var sm) && sm.ValueKind == JsonValueKind.Object
                    && sm.TryGetProperty("reddit_video", out var rv) && Str(rv, "fallback_url") is { } fu) { videoUrl = fu; break; }
        }

        if (sub is not null && id is not null)
        {
            // 2. スレ (アプリと同じ sort=old、limit=500)
            var (_, thread, i2) = await Get($"https://www.reddit.com/r/{sub}/comments/{id}.json?sort=old&limit=500&raw_json=1");
            L($"2. comments: {i2} {(thread is null ? "(JSON でない)" : ThreadStats(thread, out _))}");

            // 3. reddit の機械翻訳 (?tl=ja) が取れるか: JSON に付けた場合と、ブラウザ表示用のページに付けた場合
            await ProbeTranslationAsync(http, thread, sub, id, permalink, title, Path.GetDirectoryName(reportPath) ?? ".", Get, L, ct).ConfigureAwait(false);
        }

        // 4. 動画 (v.redd.it) は Cookie 無しの通常の HTTP で取れるか (reddit の要求回数には数えられない)
        if (videoUrl is not null)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, videoUrl);
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                L($"4. 動画 fallback_url (通常の HTTP): {(int)resp.StatusCode} {resp.Content.Headers.ContentType?.MediaType} {Trunc(videoUrl, 80)}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                L($"4. 動画 fallback_url: 失敗 {ex.Message}");
            }
        }
        else L("4. 動画: 一覧に reddit 動画が無かったので未測定");

        var text = sb.ToString();
        try { await File.WriteAllTextAsync(reportPath, text, new UTF8Encoding(false), ct).ConfigureAwait(false); }
        catch (IOException ex) { text += $"(レポートの保存に失敗: {ex.Message})"; }
        return text;
    }

    /// <summary>3. reddit の機械翻訳 (URL の <c>?tl=ja</c>。ブラウザでは翻訳済みのポスト・コメントが出る) を取れるか調べる。
    /// 3a. スレの JSON に <c>tl=ja</c> を付けて、2. (原文) と同じコメントの本文が日本語に変わっているかを比べる。
    /// 3b. ブラウザ表示用のページに <c>?tl=ja</c> を付けて取り、日本語の量とスレタイを見る (<c>probe_translated.html</c> に保存)。
    /// 取れなくても困らない要求として送る (弾かれても窓を出さない)。読み取りのみ。</summary>
    private static async Task ProbeTranslationAsync(
        HttpClient http, JsonDocument? original, string sub, string id, string? permalink, string? title, string dir,
        Func<string, Task<(int status, JsonDocument? json, string info)>> get, Action<string> L, CancellationToken ct)
    {
        static int Kana(string? t) => t is null ? 0 : System.Text.RegularExpressions.Regex.Matches(t, @"[぀-ヿ]").Count;
        static string? PostTitle(JsonDocument? doc)
        {
            try
            {
                return doc is { RootElement: { ValueKind: JsonValueKind.Array } r } && r.GetArrayLength() > 0
                    ? Str(r[0].GetProperty("data").GetProperty("children")[0].GetProperty("data"), "title") : null;
            }
            catch (Exception ex) when (ex is KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException) { return null; }
        }

        // 3a. JSON + tl=ja
        (int, JsonDocument?, string) got;
        using (ChBrowser.Services.Bbs.ProviderRequestContext.Optional())
            got = await get($"https://www.reddit.com/r/{sub}/comments/{id}.json?sort=old&limit=500&raw_json=1&tl=ja").ConfigureAwait(false);
        var (_, translated, info) = got;
        if (translated is null || original is null)
        {
            L($"3a. JSON + tl=ja: {info} (比べられませんでした: {(translated is null ? "JSON でない" : "原文が取れていない")})");
        }
        else
        {
            var orig = ChBrowser.Services.Bbs.RedditProvider.ParseCommentsResponse(JsonSerializer.SerializeToUtf8Bytes(original.RootElement));
            var tr   = ChBrowser.Services.Bbs.RedditProvider.ParseCommentsResponse(JsonSerializer.SerializeToUtf8Bytes(translated.RootElement));
            var byId = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var c in orig.Comments) byId[c.ExternalId] = c.Body;
            int common = 0, changed = 0, kanaOrig = 0, kanaTr = 0;
            (string From, string To)? sample = null;
            foreach (var c in tr.Comments)
            {
                if (!byId.TryGetValue(c.ExternalId, out var ob)) continue;
                common++;
                if (Kana(ob) > 0) kanaOrig++;
                if (Kana(c.Body) > 0) kanaTr++;
                if (ob != c.Body) { changed++; sample ??= (ob, c.Body); }
            }
            L($"3a. JSON + tl=ja: {info} コメント={tr.Comments.Count} 原文と共通={common} 本文が変わった={changed} " +
              $"かなを含む 原文={kanaOrig} tl=ja={kanaTr}");
            L($"    スレタイ 原文={Trunc(PostTitle(original) ?? title, 60)} / tl=ja={Trunc(PostTitle(translated), 60)}");
            if (sample is { } sm) L($"    例: {Trunc(sm.From, 60)} → {Trunc(sm.To, 60)}");
        }

        // 3b. ブラウザ表示用のページ + ?tl=ja (スレタイ部分付きの URL にする。付いていないと転送のページが返る)
        var url = "https://www.reddit.com" + (permalink ?? $"/r/{sub}/comments/{id}/") + "?tl=ja";
        var sw  = Stopwatch.StartNew();
        try
        {
            using var optional = ChBrowser.Services.Bbs.ProviderRequestContext.Optional();
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            try { File.WriteAllText(Path.Combine(dir, "probe_translated.html"), html, new UTF8Encoding(false)); }
            catch (IOException ex) { L($"    (probe_translated.html を保存できませんでした: {ex.Message})"); }
            var postTitle = System.Text.RegularExpressions.Regex.Match(html, @"post-title=""(?<t>[^""]*)""");
            var comments  = System.Text.RegularExpressions.Regex.Matches(html, @"<shreddit-comment\b").Count;
            var marks     = System.Text.RegularExpressions.Regex.Matches(html, "translat", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
            L($"3b. ページ + ?tl=ja: {(int)resp.StatusCode} {html.Length} 文字 {sw.ElapsedMilliseconds}ms [{resp.Content.Headers.ContentType?.MediaType}] " +
              $"shreddit-comment={comments} かなの文字数={Kana(html)} translat の出現={marks} (probe_translated.html に保存)");
            L($"    スレタイ (post-title)={Trunc(postTitle.Success ? System.Net.WebUtility.HtmlDecode(postTitle.Groups["t"].Value) : "(見つからず)", 60)}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            L($"3b. ページ + ?tl=ja: 失敗 {ex.Message} ({sw.ElapsedMilliseconds}ms)");
        }
    }

    /// <summary>comments の 2 要素配列からコメント数・more・深さ・トップレベルの時刻順を数える。</summary>
    private static string ThreadStats(JsonDocument doc, out List<string> moreIds)
    {
        moreIds = new List<string>();
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2) return "(形が想定外)";
        int comments = 0, mores = 0, moreChildren = 0, continues = 0, maxDepth = 0;
        var topTimes = new List<double>();
        var ids = moreIds;
        void Walk(JsonElement listing, int depth, bool top)
        {
            if (listing.ValueKind != JsonValueKind.Object || !listing.TryGetProperty("data", out var d)) return;
            foreach (var c in d.GetProperty("children").EnumerateArray())
            {
                var kind = Str(c, "kind");
                var data = c.GetProperty("data");
                if (kind == "t1")
                {
                    comments++;
                    maxDepth = Math.Max(maxDepth, depth);
                    if (top && data.TryGetProperty("created_utc", out var cu)) topTimes.Add(cu.GetDouble());
                    if (data.TryGetProperty("replies", out var rep)) Walk(rep, depth + 1, false);
                }
                else if (kind == "more")
                {
                    var n = data.GetProperty("children").GetArrayLength();
                    if (n == 0) continues++;
                    mores++; moreChildren += n;
                    foreach (var x in data.GetProperty("children").EnumerateArray()) ids.Add(x.GetString()!);
                }
            }
        }
        Walk(root[1], 0, true);
        var ascending = topTimes.Zip(topTimes.Skip(1), (a, b) => a <= b).All(x => x);
        return $"コメント={comments} more={mores} (未取得 id={moreChildren}, continue={continues}) 最大深さ={maxDepth} トップレベル時刻昇順={ascending} ({topTimes.Count} 件)";
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Trunc(string? s, int n) => s is null ? "" : s.Length <= n ? s : s[..n] + "…";
}
