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
    public static async Task<string> RunAsync(HttpClient http, string reportPath, CancellationToken ct)
    {
        var sb = new StringBuilder();
        void L(string s) { sb.AppendLine(s); ChBrowser.Services.Logging.LogService.Instance.Write("[redditProbe] " + s); }
        L($"reddit 接続テスト {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");

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
                    foreach (var n in new[] { Math.Min(100, moreIds.Count), Math.Min(150, moreIds.Count) }.Distinct())
                    {
                        var ids = string.Join(",", moreIds.Take(n));
                        var (s4, mc, i4) = await Get($"https://www.reddit.com/api/morechildren.json?api_type=json&link_id=t3_{id}&children={ids}&sort=old&raw_json=1");
                        var things = mc?.RootElement.TryGetProperty("json", out var j) == true && j.TryGetProperty("data", out var jd)
                                     && jd.TryGetProperty("things", out var th) ? th.GetArrayLength() : -1;
                        var errors = mc?.RootElement.TryGetProperty("json", out var j2) == true && j2.TryGetProperty("errors", out var er) ? er.ToString() : "";
                        L($"4. morechildren (GET, children={n}): {i4} things={things} errors={errors}");
                    }
                }
                else L("4. morechildren: 続き (more) の無いスレだったので未測定");
            }

            // 5. 板情報
            var (_, about, i5) = await Get($"https://www.reddit.com/r/{sub}/about.json?raw_json=1");
            var ad = about?.RootElement.TryGetProperty("data", out var a) == true ? a : default;
            L($"5. r/{sub}/about: {i5} title={Trunc(Str(ad, "title"), 40)} subscribers={(ad.ValueKind == JsonValueKind.Object && ad.TryGetProperty("subscribers", out var su) ? su.ToString() : "")}");
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
