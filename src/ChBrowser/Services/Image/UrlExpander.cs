using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ChBrowser.Services.Image;

/// <summary>
/// 画像ホスティングではないページ URL を、実体画像 URL へ非同期展開するサービス。
///
/// <para>
/// 対象: x.com (Twitter) / pixiv。imgur のような同期 (URL 形だけで決まる) 展開は JS 側で完結
/// しているのでここでは扱わない。
/// </para>
///
/// <para>
/// 実装ポリシー:
/// <list type="bullet">
///   <item><description>x.com → <c>api.fxtwitter.com</c> の公開 JSON (認証不要)。最初の photo を返す。</description></item>
///   <item><description>pixiv → <c>www.pixiv.net/ajax/illust/&lt;id&gt;</c> (Referer 必須)。<c>urls.regular</c> を返す。
///     R-18 や非公開作品はログインが必要なため null を返すことがある。</description></item>
/// </list>
/// 結果は in-memory <see cref="ConcurrentDictionary{TKey,TValue}"/> でセッション中キャッシュし、
/// 同じ URL に対する展開リクエストは 1 度だけ実行される。永続化はしない (Phase 6 続きで検討)。
/// </para>
/// </summary>
public sealed class UrlExpander : IDisposable
{
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, Task<string?>> _cache = new(StringComparer.Ordinal);

    // ---- パターン (JS の ASYNC_EXPANDER_RES と同期して維持する) ----
    private static readonly Regex TwitterRe = new(
        @"^https?://(?:www\.|m\.|mobile\.)?(?:twitter|x|fxtwitter|vxtwitter)\.com/(?<user>[A-Za-z0-9_]+)/status/(?<id>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PixivRe = new(
        @"^https?://(?:www\.)?pixiv\.net/(?:en/)?artworks/(?<id>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public UrlExpander()
    {
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

    /// <summary>URL が非同期展開対象 (x.com / pixiv 等) かを高速に判定。</summary>
    public static bool IsAsyncExpandable(string url) =>
        !string.IsNullOrEmpty(url) && (TwitterRe.IsMatch(url) || PixivRe.IsMatch(url));

    /// <summary>
    /// URL を実体画像 URL に展開。失敗 (媒体無し / API エラー / login 要) は null。
    /// 同じ URL の同時呼び出しは Task を共有する。
    /// </summary>
    public Task<string?> ExpandAsync(string url)
    {
        if (string.IsNullOrEmpty(url)) return Task.FromResult<string?>(null);
        return _cache.GetOrAdd(url, ExpandInternalAsync);
    }

    private async Task<string?> ExpandInternalAsync(string url)
    {
        try
        {
            var tw = TwitterRe.Match(url);
            if (tw.Success) return await ExpandTwitterAsync(tw.Groups["id"].Value).ConfigureAwait(false);

            var px = PixivRe.Match(url);
            if (px.Success) return await ExpandPixivAsync(px.Groups["id"].Value).ConfigureAwait(false);

            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UrlExpand] {url}: {ex.Message}");
            return null;
        }
    }

    // ---------------------------------------------------------------
    // x.com (Twitter) — fxtwitter API
    // ---------------------------------------------------------------

    private async Task<string?> ExpandTwitterAsync(string statusId)
    {
        // ユーザ名はパスに必要だが fxtwitter は値を検証しないので "i" を入れておく。
        var apiUrl = $"https://api.fxtwitter.com/i/status/{statusId}";
        using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) return null;

        await using var stream = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("tweet", out var tweet)) return null;
        if (tweet.ValueKind != JsonValueKind.Object) return null;
        if (!tweet.TryGetProperty("media", out var media)) return null;
        if (media.ValueKind != JsonValueKind.Object) return null;
        if (!media.TryGetProperty("photos", out var photos)) return null;
        if (photos.ValueKind != JsonValueKind.Array || photos.GetArrayLength() == 0) return null;

        var first = photos[0];
        if (first.ValueKind != JsonValueKind.Object) return null;
        if (!first.TryGetProperty("url", out var u)) return null;
        return u.GetString();
    }

    // ---------------------------------------------------------------
    // pixiv — ajax/illust
    // ---------------------------------------------------------------

    private async Task<string?> ExpandPixivAsync(string illustId)
    {
        var apiUrl = $"https://www.pixiv.net/ajax/illust/{illustId}";
        using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Referrer = new Uri("https://www.pixiv.net/");

        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) return null;

        await using var stream = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);

        // { error: false, body: { urls: { regular: "..." }, ... } }
        if (doc.RootElement.TryGetProperty("error", out var err) &&
            err.ValueKind == JsonValueKind.True) return null;

        if (!doc.RootElement.TryGetProperty("body", out var body)) return null;
        if (body.ValueKind != JsonValueKind.Object) return null;
        if (!body.TryGetProperty("urls", out var urls)) return null;
        if (urls.ValueKind != JsonValueKind.Object) return null;

        // regular > original > small の優先で取得 (regular が表示用、original はフルサイズ)
        if (urls.TryGetProperty("regular",  out var u) && u.ValueKind == JsonValueKind.String) return u.GetString();
        if (urls.TryGetProperty("original", out u) && u.ValueKind == JsonValueKind.String) return u.GetString();
        if (urls.TryGetProperty("small",    out u) && u.ValueKind == JsonValueKind.String) return u.GetString();
        return null;
    }

    public void Dispose() => _http.Dispose();
}
