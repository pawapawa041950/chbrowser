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

        // 1. ログイン状態
        var (s1, me, i1) = await Get(RedditSessionAuth.MeUrl);
        var meData = me?.RootElement.TryGetProperty("data", out var md) == true ? md : default;
        L($"1. me.json: {i1} name={Str(meData, "name")} modhash={(Str(meData, "modhash") is { Length: > 0 } mh ? $"あり({mh.Length}桁)" : "なし")}");

        // 2. 一覧
        var (s2, list, i2) = await Get("https://www.reddit.com/r/popular/hot.json?limit=25&raw_json=1");
        L($"2. r/popular/hot: {i2}");
        string? sub = null, id = null, videoUrl = null;
        if (list is not null && list.RootElement.TryGetProperty("data", out var ld))
        {
            var children = ld.GetProperty("children").EnumerateArray().Select(c => c.GetProperty("data")).ToList();
            L($"   件数={children.Count} after={Str(ld, "after")}");
            var best = children.OrderByDescending(c => c.TryGetProperty("num_comments", out var n) ? n.GetInt32() : 0).FirstOrDefault();
            if (best.ValueKind == JsonValueKind.Object)
            {
                sub = Str(best, "subreddit"); id = Str(best, "id");
                L($"   最多コメントの投稿: r/{sub} {id} num_comments={best.GetProperty("num_comments").GetInt32()} title={Trunc(Str(best, "title"), 60)}");
            }
            foreach (var c in children)
                if (c.TryGetProperty("secure_media", out var sm) && sm.ValueKind == JsonValueKind.Object
                    && sm.TryGetProperty("reddit_video", out var rv) && Str(rv, "fallback_url") is { } fu) { videoUrl = fu; break; }
        }

        if (sub is not null && id is not null)
        {
            // 3. スレ (sort=old、limit 違い)
            foreach (var limit in new[] { 500, 2048 })
            {
                var (s3, thread, i3) = await Get($"https://www.reddit.com/r/{sub}/comments/{id}.json?sort=old&limit={limit}&raw_json=1");
                var stats = thread is null ? "(JSON でない)" : ThreadStats(thread, out _);
                L($"3. comments limit={limit}: {i3} {stats}");
            }

            // 4. 続き (morechildren、GET で可能か)
            var (_, full, _) = await Get($"https://www.reddit.com/r/{sub}/comments/{id}.json?sort=old&limit=500&raw_json=1");
            if (full is not null)
            {
                ThreadStats(full, out var moreIds);
                if (moreIds.Count > 0)
                {
                    // アプリの「続き」の取り方 (morechildren の代わり) を試す。アプリと同じく「取れなくても困らない要求」として送る
                    var tree = ChBrowser.Services.Bbs.RedditProvider.ParseCommentsResponse(JsonSerializer.SerializeToUtf8Bytes(full.RootElement));
                    var have = new HashSet<string>(tree.Comments.Select(c => c.ExternalId), StringComparer.Ordinal);
                    // 4a. 返信の続き → 親コメントを起点にしたスレの JSON
                    var replyMore = tree.Mores.Where(m => m.ParentId.StartsWith("t1_", StringComparison.Ordinal)).OrderByDescending(m => m.Count).FirstOrDefault();
                    if (replyMore is not null)
                    {
                        (int, JsonDocument?, string) got;
                        using (ChBrowser.Services.Bbs.ProviderRequestContext.Optional())
                            got = await Get($"https://www.reddit.com/r/{sub}/comments/{id}/_/{replyMore.ParentId[3..]}.json?sort=old&limit=500&raw_json=1");
                        var (_, pj, i4) = got;
                        var st = pj is null ? null : ChBrowser.Services.Bbs.RedditProvider.ParseCommentsResponse(JsonSerializer.SerializeToUtf8Bytes(pj.RootElement));
                        var hit = st is null ? 0 : replyMore.Children.Count(c => st.Comments.Any(x => x.ExternalId == "t1_" + c));
                        L($"4a. 親コメント起点 ({replyMore.ParentId}, 続き {replyMore.Count} 件): {i4} comments={st?.Comments.Count ?? -1} 続きのうち取れた={hit}/{replyMore.Children.Count} 新規={st?.Comments.Count(c => !have.Contains(c.ExternalId)) ?? -1}");
                    }
                    else L("4a. 親コメント起点: 返信の続きが無いスレだったので未測定");
                    // 4b. トップレベルの続き → 新しい順のスレの JSON
                    var topMore = tree.Mores.FirstOrDefault(m => m.ParentId.StartsWith("t3_", StringComparison.Ordinal));
                    {
                        (int, JsonDocument?, string) got;
                        using (ChBrowser.Services.Bbs.ProviderRequestContext.Optional())
                            got = await Get($"https://www.reddit.com/r/{sub}/comments/{id}.json?sort=new&limit=500&raw_json=1");
                        var (_, nj, i4) = got;
                        var nt = nj is null ? null : ChBrowser.Services.Bbs.RedditProvider.ParseCommentsResponse(JsonSerializer.SerializeToUtf8Bytes(nj.RootElement));
                        var hit = nt is null || topMore is null ? 0 : topMore.Children.Count(c => nt.Comments.Any(x => x.ExternalId == "t1_" + c));
                        L($"4b. 新しい順 (sort=new): {i4} comments={nt?.Comments.Count ?? -1} 新規={nt?.Comments.Count(c => !have.Contains(c.ExternalId)) ?? -1} " +
                          $"トップレベルの続きのうち取れた={hit}/{topMore?.Children.Count ?? 0}");
                    }
                }
                else L("4. 続き (more) の無いスレだったので未測定");
            }

            // 5. 板情報
            var (_, about, i5) = await Get($"https://www.reddit.com/r/{sub}/about.json?raw_json=1");
            var ad = about?.RootElement.TryGetProperty("data", out var a) == true ? a : default;
            L($"5. r/{sub}/about: {i5} title={Trunc(Str(ad, "title"), 40)} subscribers={(ad.ValueKind == JsonValueKind.Object && ad.TryGetProperty("subscribers", out var su) ? su.ToString() : "")}");

            // 8. スレの HTML と、その中の「続き」の部分読み込み (morechildren が GET / POST とも弾かれるため、その代わりを探す)
            await ProbeThreadHtmlAsync(http, host, sub, id, Path.GetDirectoryName(reportPath) ?? ".", L, ct).ConfigureAwait(false);
        }

        // 6. 購読一覧
        var (_, subs, i6) = await Get("https://www.reddit.com/subreddits/mine/subscriber.json?limit=100&raw_json=1");
        var subCount = subs?.RootElement.TryGetProperty("data", out var sd) == true ? sd.GetProperty("children").GetArrayLength() : -1;
        L($"6. 購読一覧: {i6} 件数={subCount}");

        // 7. 動画 (v.redd.it) は Cookie 無しの通常の HTTP で取れるか
        if (videoUrl is not null)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, videoUrl);
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                L($"7. 動画 fallback_url (通常の HTTP): {(int)resp.StatusCode} {resp.Content.Headers.ContentType?.MediaType} {Trunc(videoUrl, 80)}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                L($"7. 動画 fallback_url: 失敗 {ex.Message}");
            }
        }
        else L("7. 動画: 一覧に reddit 動画が無かったので未測定");

        var text = sb.ToString();
        try { await File.WriteAllTextAsync(reportPath, text, new UTF8Encoding(false), ct).ConfigureAwait(false); }
        catch (IOException ex) { text += $"(レポートの保存に失敗: {ex.Message})"; }
        return text;
    }

    /// <summary>8. ブラウザ表示用のスレの HTML (<c>/r/{sub}/comments/{id}/</c>) を取って保存し、中の部分読み込み (<c>/svc/shreddit/...</c>) の
    /// うちコメント関係のものを書き出し、「続き」(more-comments) を 2 件まで実際に取って保存する
    /// (<c>probe_thread.html</c> / <c>probe_more_N.html</c>。Web 版がどうやって続きを読み込んでいるかを調べるため)。
    /// 取れなくても困らない要求として送る (弾かれても窓を出さない)。読み取りのみ。</summary>
    private static async Task ProbeThreadHtmlAsync(HttpClient http, RedditWebViewHost? host, string sub, string id, string dir, Action<string> L, CancellationToken ct)
    {
        async Task<(int Status, string Text, string Info)> GetText(string url)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var optional = ChBrowser.Services.Bbs.ProviderRequestContext.Optional();
                using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return ((int)resp.StatusCode, text,
                        $"{(int)resp.StatusCode} {text.Length} 文字 {sw.ElapsedMilliseconds}ms [{resp.Content.Headers.ContentType?.MediaType}]");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return (0, "", $"失敗: {ex.Message} ({sw.ElapsedMilliseconds}ms)");
            }
        }
        void Save(string name, string text)
        {
            try { File.WriteAllText(Path.Combine(dir, name), text, new UTF8Encoding(false)); }
            catch (IOException ex) { L($"   ({name} を保存できませんでした: {ex.Message})"); }
        }
        static int Count(string html, string tag) => System.Text.RegularExpressions.Regex.Matches(html, "<" + tag + @"\b").Count;

        var (_, html, info) = await GetText($"https://www.reddit.com/r/{sub}/comments/{id}/").ConfigureAwait(false);
        // スレタイ部分 (slug) の無い URL は、正しい URL へ移る (location.replace) だけのページが返るので、その先を取り直す
        var redirect = System.Text.RegularExpressions.Regex.Match(html, @"location\.replace\(&quot;(?<u>/r/[^&]+)&quot;\)");
        if (redirect.Success)
        {
            L($"8. スレの HTML (転送のページ): {info} → {redirect.Groups["u"].Value}");
            (_, html, info) = await GetText("https://www.reddit.com" + redirect.Groups["u"].Value).ConfigureAwait(false);
        }
        Save("probe_thread.html", html);
        L($"8. スレの HTML: {info} shreddit-comment={Count(html, "shreddit-comment")} (probe_thread.html に保存)");
        if (html.Length == 0) return;

        var urls = System.Text.RegularExpressions.Regex.Matches(html, @"(?:src|action|href)=""(?<u>/svc/shreddit/[^""]+)""")
            .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups["u"].Value)).Distinct().ToList();
        var commentUrls = urls.Where(u => u.Contains("comment", StringComparison.OrdinalIgnoreCase)).ToList();
        L($"8. 部分読み込みの URL {urls.Count} 件 (うちコメント関係 {commentUrls.Count} 件):");
        foreach (var u in commentUrls.Take(12)) L("   " + Trunc(u, 240));

        // 「続き」の要素の書き方の例 (前後 400 文字)
        var at = html.IndexOf("more-comments", StringComparison.Ordinal);
        if (at >= 0)
            L("   例: " + System.Text.RegularExpressions.Regex.Replace(html[Math.Max(0, at - 400)..Math.Min(html.Length, at + 400)], @"\s+", " "));

        // 続き (more-comments) は <faceplate-partial method="post"> の中の cursor を POST で送る。トップレベルの続き (ページ末尾) と
        // 返信の続きを 1 つずつ、csrf_token (Cookie) を付けない / 付けるの 2 通りで試す
        var partials = System.Text.RegularExpressions.Regex.Matches(html,
                @"<faceplate-partial\b[^>]*src=""(?<u>/svc/shreddit/more-comments/[^""]+)""[^>]*>\s*<input type=""hidden"" name=""cursor"" value=""(?<c>[^""]+)""")
            .Select(m => (Url: System.Net.WebUtility.HtmlDecode(m.Groups["u"].Value), Cursor: System.Net.WebUtility.HtmlDecode(m.Groups["c"].Value)))
            .ToList();
        L($"8. 続きの要素 (cursor 付き) {partials.Count} 件");
        var picks = partials.Where(p => p.Url.Contains("top-level=1", StringComparison.Ordinal)).Take(1)
            .Concat(partials.Where(p => !p.Url.Contains("top-level=1", StringComparison.Ordinal)).Take(1)).ToList();
        var csrf = host is null ? null : await host.GetCookieValueAsync("csrf_token").ConfigureAwait(false);
        L($"8. csrf_token Cookie: {(csrf is null ? "無し" : $"あり ({csrf.Length} 文字)")}");
        var k = 0;
        foreach (var (u, cursor) in picks)
        {
            foreach (var withCsrf in new[] { false, true })
            {
                if (withCsrf && csrf is null) continue;
                k++;
                var sw = Stopwatch.StartNew();
                string text, mi;
                try
                {
                    using var optional = ChBrowser.Services.Bbs.ProviderRequestContext.Optional();
                    var form = new Dictionary<string, string> { ["cursor"] = cursor };
                    if (withCsrf) form["csrf_token"] = csrf!;
                    using var content = new FormUrlEncodedContent(form);
                    using var resp = await http.PostAsync("https://www.reddit.com" + u, content, ct).ConfigureAwait(false);
                    text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    mi = $"{(int)resp.StatusCode} {text.Length} 文字 {sw.ElapsedMilliseconds}ms [{resp.Content.Headers.ContentType?.MediaType}]";
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    text = ""; mi = $"失敗: {ex.Message} ({sw.ElapsedMilliseconds}ms)";
                }
                Save($"probe_more_{k}.html", text);
                L($"8. 続き {k} (POST, {(u.Contains("top-level=1", StringComparison.Ordinal) ? "トップレベル" : "返信")}, csrf_token {(withCsrf ? "あり" : "なし")}): {mi} " +
                  $"shreddit-comment={Count(text, "shreddit-comment")} (probe_more_{k}.html に保存) {Trunc(u, 160)}");
            }
        }
        if (k == 0) L("8. 続き (more-comments) の要素が見つからなかったので未取得");
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
