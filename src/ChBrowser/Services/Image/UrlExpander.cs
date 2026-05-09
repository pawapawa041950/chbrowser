using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    /// 同じ URL の同時呼び出しは Task を共有する (= GetOrAdd)。
    /// 失敗結果はキャッシュから外して次回呼び出しで再試行可能にする
    /// (= 一度のネットワーク失敗・API 障害でその URL が永続的に null 化するのを避ける)。
    /// </summary>
    public async Task<string?> ExpandAsync(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;

        var task   = _cache.GetOrAdd(url, ExpandInternalAsync);
        var result = await task.ConfigureAwait(false);

        if (result is null)
        {
            // 自分の task と一致するキャッシュ entry だけ削除する (= 別呼び出しが再 GetOrAdd で
            // 差し替えていた場合に巻き込まないため、KeyValuePair オーバーロードを使う)。
            _cache.TryRemove(new KeyValuePair<string, Task<string?>>(url, task));
        }

        return result;
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
            ChBrowser.Services.Logging.LogService.Instance.Write($"[UrlExpand] {url}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // ---------------------------------------------------------------
    // x.com (Twitter) — fxtwitter API (動画/GIF はサムネイル URL を返す)
    // ---------------------------------------------------------------

    /// <summary>
    /// fxtwitter API でツイートを取得し、表示用画像 URL を返す。
    /// 失敗理由 (HTTP / JSON / メディア無し) は LogService に書き出す (= ログウィンドウで確認可)。
    ///
    /// <para>fxtwitter のレスポンス構造:</para>
    /// <list type="bullet">
    ///   <item><description><c>tweet.media.photos[]</c> — 写真。<c>url</c> が画像 URL。</description></item>
    ///   <item><description><c>tweet.media.videos[]</c> — 動画 / GIF。<c>thumbnail_url</c> がサムネイル画像 URL、<c>url</c> は .mp4。</description></item>
    ///   <item><description><c>tweet.media.all[]</c> — 上記を表示順で混ぜたもの。<c>type</c> で識別。</description></item>
    ///   <item><description><c>tweet.quote</c> — 引用元ツイート。引用 RT ではメディアが quote 側にある。</description></item>
    /// </list>
    /// メイン tweet にメディアがあればそれを、無ければ quote 内のメディアを探す
    /// (= 引用 RT で本文側に写真が無く quote 側に写真があるパターンに対応)。
    /// </summary>
    private async Task<string?> ExpandTwitterAsync(string statusId)
    {
        // ユーザ名はパスに必要だが fxtwitter は値を検証しないので "i" を入れておく。
        var apiUrl = $"https://api.fxtwitter.com/i/status/{statusId}";
        using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[UrlExpand] fxtwitter status={statusId} → HTTP {(int)res.StatusCode}");
            return null;
        }

        await using var stream = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("tweet", out var tweet) || tweet.ValueKind != JsonValueKind.Object)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[UrlExpand] fxtwitter status={statusId}: tweet object missing");
            return null;
        }

        // 1) メイン tweet のメディアを試す
        var fromMain = TryExtractMediaUrl(tweet);
        if (fromMain is not null) return fromMain;

        // 2) 引用ツイート (= quote tweet) のメディアを試す。引用 RT で本文側に画像が無く、
        //    quote 側に画像があるパターン (5ch でよく見る) に対応。
        if (tweet.TryGetProperty("quote", out var quote) && quote.ValueKind == JsonValueKind.Object)
        {
            var fromQuote = TryExtractMediaUrl(quote);
            if (fromQuote is not null) return fromQuote;
        }

        // 媒体がそもそも無いツイート (テキストのみ) はここに来る。静かに null。
        return null;
    }

    /// <summary>fxtwitter の tweet object (メインまたは quote) から表示用画像 URL を抽出。
    /// 優先順位: photos[0].url → videos[0].thumbnail_url → all[0].thumbnail_url/url。
    /// メディアが無い / 取り出せない場合は null。</summary>
    private static string? TryExtractMediaUrl(JsonElement tweet)
    {
        if (!tweet.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Object)
            return null;

        // 1) 写真 — photos[0].url
        if (media.TryGetProperty("photos", out var photos)
            && photos.ValueKind == JsonValueKind.Array && photos.GetArrayLength() > 0
            && photos[0].ValueKind == JsonValueKind.Object
            && photos[0].TryGetProperty("url", out var pu)
            && pu.ValueKind == JsonValueKind.String)
        {
            return pu.GetString();
        }

        // 2) 動画 / GIF — videos[0].thumbnail_url (サムネイル画像)
        //    .mp4 そのものではなく thumbnail_url を返すのは、JS 側がここに <img> を作る前提のため。
        if (media.TryGetProperty("videos", out var videos)
            && videos.ValueKind == JsonValueKind.Array && videos.GetArrayLength() > 0
            && videos[0].ValueKind == JsonValueKind.Object
            && videos[0].TryGetProperty("thumbnail_url", out var vt)
            && vt.ValueKind == JsonValueKind.String)
        {
            return vt.GetString();
        }

        // 3) フォールバック: all[0] から (将来 photos/videos 配列が削られた場合の保険)
        if (media.TryGetProperty("all", out var all)
            && all.ValueKind == JsonValueKind.Array && all.GetArrayLength() > 0
            && all[0].ValueKind == JsonValueKind.Object)
        {
            if (all[0].TryGetProperty("thumbnail_url", out var at) && at.ValueKind == JsonValueKind.String)
                return at.GetString();
            if (all[0].TryGetProperty("url",           out var au) && au.ValueKind == JsonValueKind.String)
                return au.GetString();
        }

        return null;
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
