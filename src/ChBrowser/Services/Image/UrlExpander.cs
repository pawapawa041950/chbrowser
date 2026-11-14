using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ChBrowser.Services.Image;

/// <summary>展開結果の判定区分。</summary>
public enum ExpandOutcome
{
    /// <summary>実体画像 URL に解決できた。</summary>
    Resolved,
    /// <summary>ソース (例: ツイート) は存在するが画像/動画メディアが付いていない (= 確定: 再試行しても出ない)。
    /// JS 側はこのスロットを DOM から取り除く。</summary>
    NoMedia,
    /// <summary>API エラー / ネットワーク失敗 / 認証要等で確定できなかった (= 再試行で結果が変わるかもしれない)。
    /// JS 側は「クリックで再試行」プレースホルダを出す。</summary>
    Unavailable,
}

/// <summary>非同期 URL 展開の結果。<see cref="ExpandOutcome"/> で 3 値判別。
/// Urls は Resolved の時のみ 1 件以上 (1 つの URL に複数の画像・動画がある場合は複数)。Url はその先頭。</summary>
public readonly record struct ExpandResult(IReadOnlyList<string>? Urls, ExpandOutcome Outcome)
{
    public string? Url         => Urls is { Count: > 0 } ? Urls[0] : null;
    public bool IsResolved     => Outcome == ExpandOutcome.Resolved;
    public bool IsNoMedia      => Outcome == ExpandOutcome.NoMedia;
    public bool IsUnavailable  => Outcome == ExpandOutcome.Unavailable;

    public static readonly ExpandResult NoMedia     = new(null, ExpandOutcome.NoMedia);
    public static readonly ExpandResult Unavailable = new(null, ExpandOutcome.Unavailable);
    public static ExpandResult Of(string url)                  => new(new[] { url }, ExpandOutcome.Resolved);
    public static ExpandResult Of(IReadOnlyList<string> urls)  => new(urls, ExpandOutcome.Resolved);
}

/// <summary>
/// 画像ホスティングではないページ URL を、実体画像 URL へ非同期展開するサービス。
///
/// <para>
/// 対象と手順はサムネイルルール (<see cref="ThumbnailRuleSet"/>。既定は x.com / pixiv / imgur のアルバム、
/// 利用者が data/app/thumbnail_rules.csv で足せる) で決まる。imgur の単体画像のような同期 (URL 形だけで決まる) 展開は
/// JS 側で完結しているのでここでは扱わない。reddit の動画ページ (<see cref="ResolveVideoAsync"/>) は専用の処理。
/// </para>
///
/// <para>
/// 結果は in-memory <see cref="ConcurrentDictionary{TKey,TValue}"/> でセッション中キャッシュ。
/// <see cref="ExpandOutcome.Unavailable"/> は次回呼び出しで再試行可能にするためキャッシュから外す。
/// <see cref="ExpandOutcome.Resolved"/> / <see cref="ExpandOutcome.NoMedia"/> は確定情報として保持。
/// </para>
/// </summary>
public sealed class UrlExpander : IDisposable
{
    private readonly HttpClient _http;
    private volatile ThumbnailRuleSet _rules;
    private readonly ConcurrentDictionary<string, Task<ExpandResult>> _cache = new(StringComparer.Ordinal);

    /// <summary>取得する内容の大きさの上限 (これを超える応答は読まない)。</summary>
    private const int MaxResponseBytes = 8 * 1024 * 1024;

    public UrlExpander(ThumbnailRuleSet rules)
    {
        _rules = rules;
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression   = DecompressionMethods.All,
            AllowAutoRedirect        = true,
            MaxAutomaticRedirections = 5,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 ChBrowser/0.1");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ja,en;q=0.8");
    }

    /// <summary>URL が非同期展開対象 (取得ルールのどれかに一致する) かを判定。</summary>
    public bool IsAsyncExpandable(string url) => _rules.IsMatch(url);

    /// <summary>
    /// URL を実体画像 URL (複数のこともある) に展開。返値の <see cref="ExpandResult.Outcome"/> で 3 値判別。
    /// 同じ URL の同時呼び出しは Task を共有する (= GetOrAdd)。
    /// <see cref="ExpandOutcome.Unavailable"/> はキャッシュから外して次回呼び出しで再試行可能にする。
    /// </summary>
    public async Task<ExpandResult> ExpandAsync(string url)
    {
        if (string.IsNullOrEmpty(url)) return ExpandResult.Unavailable;

        var task   = _cache.GetOrAdd(url, ExpandInternalAsync);
        var result = await task.ConfigureAwait(false);

        if (result.IsUnavailable)
        {
            // 自分の task と一致するキャッシュ entry だけ削除する。
            _cache.TryRemove(new KeyValuePair<string, Task<ExpandResult>>(url, task));
        }

        return result;
    }

    /// <summary>取得ルールを入れ替える (設定画面での変更時)。覚えている結果は古いルールのものなので捨てる。</summary>
    public void SetRules(ThumbnailRuleSet rules)
    {
        _rules = rules;
        _cache.Clear();
    }

    /// <summary>取得ルールに従って展開する。失敗理由 (HTTP / 内容の読み取り / 何も取れない) は LogService に書く (= ログウィンドウで確認可)。</summary>
    private async Task<ExpandResult> ExpandInternalAsync(string url)
    {
        var (result, failure) = await RunRuleAsync(_rules, url, null).ConfigureAwait(false);
        if (failure is not null) ChBrowser.Services.Logging.LogService.Instance.Write("[UrlExpand] " + failure);
        return result;
    }

    /// <summary>設定画面の「試す」: <paramref name="rules"/> (編集中のルール) で <paramref name="url"/> を展開し、経過と結果を文章で返す。
    /// 結果は覚えない (何度でも取得し直す)。</summary>
    public async Task<string> TestAsync(ThumbnailRuleSet rules, string url)
    {
        var trace = new List<string>();
        var (result, failure) = await RunRuleAsync(rules, url, trace).ConfigureAwait(false);
        if (failure is not null) trace.Add("失敗: " + failure);
        else if (result.IsNoMedia) trace.Add("結果: 画像なし (サムネイルの枠を出しません)");
        else if (result.IsResolved)
        {
            trace.Add($"結果: {result.Urls!.Count} 件");
            trace.AddRange(result.Urls!.Take(20).Select((u, i) => $"  {i + 1}. {u}"));
            if (result.Urls!.Count > 20) trace.Add($"  … 他 {result.Urls.Count - 20} 件");
        }
        return string.Join("\n", trace);
    }

    /// <summary>ルールを 1 回実行する。<paramref name="trace"/> があれば経過 (どのルールか、取得先、HTTP の結果) を足す。
    /// 取れなかったときは理由の文面も返す (画像なしと確定した場合は理由なし)。</summary>
    private async Task<(ExpandResult Result, string? Failure)> RunRuleAsync(ThumbnailRuleSet rules, string url, List<string>? trace)
    {
        if (rules.Find(url) is not { } hit) return (ExpandResult.Unavailable, "この URL に一致するルールがありません");
        var (rule, m) = hit;
        trace?.Add($"ルール: {rule.Name}");
        try
        {
            // 取得せずに URL を組み立てるだけのルール
            if (rule.Result is not null)
            {
                var built = ThumbnailRule.Fill(rule.Result, m);
                return Uri.TryCreate(built, UriKind.Absolute, out var bu) && (bu.Scheme == Uri.UriSchemeHttp || bu.Scheme == Uri.UriSchemeHttps)
                    ? (ExpandResult.Of(bu.AbsoluteUri), null)
                    : (ExpandResult.Unavailable, $"{rule.Name}: 組み立てた URL が不正です: {built}");
            }

            var fetchUrl = ThumbnailRule.Fill(rule.FetchUrl!, m);
            if (!Uri.TryCreate(fetchUrl, UriKind.Absolute, out var fu) || (fu.Scheme != Uri.UriSchemeHttp && fu.Scheme != Uri.UriSchemeHttps))
                return (ExpandResult.Unavailable, $"{rule.Name}: 取得先の URL が不正です: {fetchUrl}");
            trace?.Add($"取得先: {fu}");

            using var req = new HttpRequestMessage(HttpMethod.Get, fu);
            foreach (var (k, v) in rule.FetchHeaders) req.Headers.TryAddWithoutValidation(k, v);

            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            trace?.Add($"応答: HTTP {(int)res.StatusCode} ({res.Content.Headers.ContentType?.MediaType ?? "種類不明"})");
            if (!res.IsSuccessStatusCode)
                return (ExpandResult.Unavailable, $"{rule.Name}: {fu} → HTTP {(int)res.StatusCode}");
            if (res.Content.Headers.ContentLength is long len && len > MaxResponseBytes)
                return (ExpandResult.Unavailable, $"{rule.Name}: {fu} → 応答が大きすぎます ({len} バイト)");
            var content = await res.Content.ReadAsStringAsync().ConfigureAwait(false);

            var urls = ThumbnailRuleSet.ExtractUrls(rule, content, fu);
            if (urls.Count > 0) return (ExpandResult.Of(urls), null);

            // 何も取れなかった: ルールが「画像が無いと確定」としていれば NoMedia (JS がサムネイルの枠を消す)、
            // そうでなければ取得失敗 (ログインが要る・非公開等。後で取れるかもしれない)
            if (rule.EmptyMeansNoMedia) return (ExpandResult.NoMedia, null);
            return (ExpandResult.Unavailable, $"{rule.Name}: {fu} → 画像の URL を取り出せませんでした");
        }
        catch (JsonException ex)
        {
            return (ExpandResult.Unavailable, $"{rule.Name}: 応答を JSON として読めません ({ex.Message})");
        }
        catch (Exception ex)
        {
            return (ExpandResult.Unavailable, $"{rule.Name}: {url}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------
    // reddit の動画ページ (v.redd.it/<id>) — 再生リスト (DASH) の一番大きい mp4
    // ---------------------------------------------------------------

    /// <summary><c>https://v.redd.it/&lt;id&gt;</c> (動画そのものではなく reddit の動画ページへ転送される URL)。
    /// JS の <c>V_REDDIT_RE</c> と同期して維持する。</summary>
    private static readonly Regex RedditVideoRe = new(
        @"^https?://v\.redd\.it/(?<id>[A-Za-z0-9]+)/?(?:[?#].*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ConcurrentDictionary<string, Task<ExpandResult>> _videoCache = new(StringComparer.Ordinal);

    /// <summary>reddit の動画ページの URL (<c>v.redd.it/&lt;id&gt;</c>) か。</summary>
    public static bool IsRedditVideoPage(string url) => !string.IsNullOrEmpty(url) && RedditVideoRe.IsMatch(url);

    /// <summary>reddit の動画ページの URL を、再生できる mp4 の URL (<c>v.redd.it/&lt;id&gt;/CMAF_720.mp4</c> 等) にする。
    /// 再生リスト (<c>DASHPlaylist.mpd</c>。ログイン不要の配信用ホスト) の映像のうち一番大きいもの。
    /// 映像と音声は別のファイルなので音声は付かない (スレの動画 (<c>reddit_video.fallback_url</c>) と同じ)。
    /// 結果は <see cref="ExpandAsync"/> と同じく覚え、<see cref="ExpandOutcome.Unavailable"/> だけ次回やり直す。</summary>
    public async Task<ExpandResult> ResolveVideoAsync(string url)
    {
        var m = RedditVideoRe.Match(url ?? "");
        if (!m.Success) return ExpandResult.Unavailable;
        var task   = _videoCache.GetOrAdd(url!, _ => ResolveRedditVideoAsync(m.Groups["id"].Value));
        var result = await task.ConfigureAwait(false);
        if (result.IsUnavailable) _videoCache.TryRemove(new KeyValuePair<string, Task<ExpandResult>>(url!, task));
        return result;
    }

    private async Task<ExpandResult> ResolveRedditVideoAsync(string id)
    {
        try
        {
            var baseUrl = $"https://v.redd.it/{id}/";
            using var res = await _http.GetAsync(baseUrl + "DASHPlaylist.mpd").ConfigureAwait(false);
            if (res.StatusCode == HttpStatusCode.NotFound || res.StatusCode == HttpStatusCode.Forbidden)
            {
                ChBrowser.Services.Logging.LogService.Instance.Write($"[UrlExpand] v.redd.it/{id} → HTTP {(int)res.StatusCode} (動画が無い)");
                return ExpandResult.NoMedia;
            }
            if (!res.IsSuccessStatusCode)
            {
                ChBrowser.Services.Logging.LogService.Instance.Write($"[UrlExpand] v.redd.it/{id} → HTTP {(int)res.StatusCode}");
                return ExpandResult.Unavailable;
            }
            var xml = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            var file = PickLargestVideo(xml);
            if (file is null)
            {
                ChBrowser.Services.Logging.LogService.Instance.Write($"[UrlExpand] v.redd.it/{id}: 再生リストに映像がありません");
                return ExpandResult.NoMedia;
            }
            return ExpandResult.Of(baseUrl + file);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write($"[UrlExpand] v.redd.it/{id}: {ex.GetType().Name}: {ex.Message}");
            return ExpandResult.Unavailable;
        }
    }

    /// <summary>DASH の再生リスト (MPD) から、映像 (mimeType が video/…) のうち画素数の一番大きいもののファイル名 (BaseURL)。</summary>
    internal static string? PickLargestVideo(string mpdXml)
    {
        var doc = System.Xml.Linq.XDocument.Parse(mpdXml);
        string? best = null;
        long bestScore = -1;
        foreach (var rep in doc.Descendants().Where(e => e.Name.LocalName == "Representation"))
        {
            var set  = rep.Parent;
            var mime = (string?)rep.Attribute("mimeType") ?? (string?)set?.Attribute("mimeType") ?? "";
            var kind = (string?)set?.Attribute("contentType") ?? "";
            if (!mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) && !kind.Equals("video", StringComparison.OrdinalIgnoreCase)) continue;
            var file = rep.Elements().FirstOrDefault(e => e.Name.LocalName == "BaseURL")?.Value.Trim();
            if (string.IsNullOrEmpty(file) || file.Contains("://", StringComparison.Ordinal) || file.StartsWith('/')) continue;
            long.TryParse((string?)rep.Attribute("width"), out var w);
            long.TryParse((string?)rep.Attribute("height"), out var h);
            long.TryParse((string?)rep.Attribute("bandwidth"), out var bw);
            var score = w * h > 0 ? w * h * 1000 + bw / 1000 : bw / 1000;   // 画素数優先、同じなら帯域の大きい方
            if (score > bestScore) { bestScore = score; best = file; }
        }
        return best;
    }

    public void Dispose() => _http.Dispose();
}
