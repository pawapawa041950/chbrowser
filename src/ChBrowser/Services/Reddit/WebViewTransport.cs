using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Services.Bbs;

namespace ChBrowser.Services.Reddit;

/// <summary>reddit への要求を WebView2 のログインセッション内の fetch で送る通信経路 (<c>doc/reddit-design.md</c> §5.3〜5.5、B10)。
/// <see cref="ProviderTransportHandler"/> 経由で呼ばれ、結果を通常の <see cref="HttpResponseMessage"/> に詰め直して返す。
///
/// <list type="bullet">
/// <item><description>要求は直列 (同時 1 件) で、要求の間に最小間隔 (<see cref="MinInterval"/>) を置く。</description></item>
/// <item><description>セッション失効 (401 / ログインページへの転送 / USER_REQUIRED / 未ログインでの「network security」) を検知したら
///   ログイン窓を出し、ログインできたら同じ要求を 1 回だけ送り直す。自動処理中は窓を出さず 401 を返す。</description></item>
/// <item><description>ログイン中なのに「network security」が出たら確認ページとみなし、同じく窓でユーザに通してもらってから送り直す。</description></item>
/// <item><description>429 と <c>X-Ratelimit-Remaining</c> の残り僅かは、<c>Retry-After</c> / <c>X-Ratelimit-Reset</c> (上限 60 秒) だけ待つ。
///   連続した失敗は間隔を 1 → 2 → 4 … 60 秒と延ばす。</description></item>
/// <item><description>書き込み (GET 以外) には modhash を <c>X-Modhash</c> ヘッダで付ける (決定 D35)。</description></item>
/// </list></summary>
public sealed class WebViewTransport : IProviderTransport
{
    private readonly IBrowserFetcher   _fetcher;
    private readonly RedditSessionAuth _auth;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim     _gate = new(1, 1);
    private DateTimeOffset             _nextAllowed = DateTimeOffset.MinValue;
    private TimeSpan                   _failureBackoff = TimeSpan.Zero;

    /// <summary>要求と要求の最小間隔 (既定 1 秒)。</summary>
    public TimeSpan MinInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>待ちが発生したとき (秒数)。ステータスバー表示用。</summary>
    public event Action<int>? Waiting;

    public WebViewTransport(IBrowserFetcher fetcher, RedditSessionAuth auth, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _fetcher = fetcher;
        _auth    = auth;
        _delay   = delay ?? ((t, ct) => Task.Delay(t, ct));
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var fetchReq = await ToFetchRequestAsync(request, ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await SendOnceAsync(fetchReq, ct).ConfigureAwait(false);

            switch (Classify(result))
            {
                case Kind.RateLimited:
                {
                    var wait = RetryAfter(result) ?? TimeSpan.FromSeconds(10);
                    await WaitAsync(wait, ct).ConfigureAwait(false);
                    result = await SendOnceAsync(fetchReq, ct).ConfigureAwait(false);
                    break;
                }
                case Kind.Blocked:
                {
                    // ログインが切れたのか、ログイン中に確認ページが出たのかを見分ける。
                    // ログイン Cookie が無ければ未ログイン (匿名の要求は me.json も含めて弾かれる)、あれば me.json で確かめる
                    // (RefreshStateAsync が Cookie の有無も見る)。
                    await _auth.RefreshStateAsync(ct).ConfigureAwait(false);
                    var reason = _auth.State.IsLoggedIn || _auth.State.Kind == AuthStateKind.NeedsAttention
                        ? RedditLoginReason.Challenge : RedditLoginReason.Login;
                    if (reason == RedditLoginReason.Challenge) _auth.MarkNeedsAttention(); else _auth.MarkLoggedOut();
                    result = await ReloginAndRetryAsync(fetchReq, reason, result, ct).ConfigureAwait(false);
                    break;
                }
                case Kind.Expired:
                    _auth.MarkLoggedOut();
                    result = await ReloginAndRetryAsync(fetchReq, RedditLoginReason.Login, result, ct).ConfigureAwait(false);
                    break;
            }

            ObserveRateLimit(result);
            var kind = Classify(result);
            _failureBackoff = kind == Kind.Ok || result.Status < 500
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(Math.Min(60, Math.Max(1, _failureBackoff.TotalSeconds * 2)));
            if (kind is Kind.Expired or Kind.Blocked) return LoginRequiredResponse(request, result);
            return ToHttpResponse(request, result);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<BrowserFetchResult> ReloginAndRetryAsync(
        BrowserFetchRequest req, RedditLoginReason reason, BrowserFetchResult failed, CancellationToken ct)
    {
        var url = reason == RedditLoginReason.Challenge ? req.Url : RedditSessionAuth.LoginUrl;
        if (!await _auth.RequestLoginAsync(reason, url, ct).ConfigureAwait(false))
        {
            // 窓を閉じた後の me.json はログイン中を返しうるが、確認ページはまだ通っていない
            if (reason == RedditLoginReason.Challenge) _auth.MarkNeedsAttention();
            return failed;
        }
        var retried = await SendOnceAsync(ModhashRefreshed(req), ct).ConfigureAwait(false);
        if (reason == RedditLoginReason.Challenge && RedditResponse.IsNetworkSecurityBlock(retried)) _auth.MarkNeedsAttention();
        return retried;
    }

    /// <summary>ログインし直した後は modhash が変わるので付け直す。</summary>
    private BrowserFetchRequest ModhashRefreshed(BrowserFetchRequest req)
    {
        if (string.Equals(req.Method, "GET", StringComparison.OrdinalIgnoreCase)) return req;
        var headers = req.Headers.Where(h => !h.Key.Equals("X-Modhash", StringComparison.OrdinalIgnoreCase)).ToList();
        if (_auth.Modhash is { Length: > 0 } mh) headers.Add(new("X-Modhash", mh));
        return req with { Headers = headers };
    }

    private async Task<BrowserFetchResult> SendOnceAsync(BrowserFetchRequest req, CancellationToken ct)
    {
        // 状態がまだ分からなければ先に確かめる (modhash を得るため。GET だけなら不要だが 1 回きり)
        if (_auth.State.Kind == AuthStateKind.Unknown) await _auth.EnsureCheckedAsync(ct).ConfigureAwait(false);

        var now  = DateTimeOffset.UtcNow;
        var wait = _nextAllowed - now;
        if (_failureBackoff > TimeSpan.Zero && _failureBackoff > wait) wait = _failureBackoff;
        if (wait > TimeSpan.Zero) await WaitAsync(wait, ct).ConfigureAwait(false);

        try
        {
            return await _fetcher.FetchAsync(ModhashRefreshed(req), ct).ConfigureAwait(false);
        }
        finally
        {
            _nextAllowed = DateTimeOffset.UtcNow + MinInterval;
        }
    }

    private async Task WaitAsync(TimeSpan wait, CancellationToken ct)
    {
        if (wait > TimeSpan.FromSeconds(60)) wait = TimeSpan.FromSeconds(60);
        if (wait >= TimeSpan.FromSeconds(2)) Waiting?.Invoke((int)Math.Ceiling(wait.TotalSeconds));
        await _delay(wait, ct).ConfigureAwait(false);
    }

    private void ObserveRateLimit(BrowserFetchResult r)
    {
        var remaining = ParseDouble(r.Header("x-ratelimit-remaining"));
        var reset     = ParseDouble(r.Header("x-ratelimit-reset"));
        if (remaining is double rem && rem < 2 && reset is double sec && sec > 0)
        {
            var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Min(60, sec));
            if (until > _nextAllowed) _nextAllowed = until;
        }
    }

    private static TimeSpan? RetryAfter(BrowserFetchResult r)
    {
        var s = ParseDouble(r.Header("retry-after")) ?? ParseDouble(r.Header("x-ratelimit-reset"));
        return s is double sec && sec > 0 ? TimeSpan.FromSeconds(Math.Min(60, sec)) : null;
    }

    private static double? ParseDouble(string? s)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private enum Kind { Ok, RateLimited, Expired, Blocked }

    private static Kind Classify(BrowserFetchResult r)
    {
        if (r.Status == 429) return Kind.RateLimited;
        if (r.Status == 401 || RedditResponse.IsLoginRedirect(r) || RedditResponse.IsUserRequired(r)) return Kind.Expired;
        if (RedditResponse.IsNetworkSecurityBlock(r)) return Kind.Blocked;
        return Kind.Ok;
    }

    // -----------------------------------------------------------------
    // HttpRequestMessage ⇔ fetch
    // -----------------------------------------------------------------

    /// <summary>fetch に渡せない (ブラウザが管理する) ヘッダ。</summary>
    private static readonly HashSet<string> SkipHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "User-Agent", "Cookie", "Connection", "Content-Length", "Accept-Encoding", "Referer", "Origin",
        "Range", "If-Modified-Since", "If-None-Match", "Expect", "Transfer-Encoding",
    };

    public static async Task<BrowserFetchRequest> ToFetchRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var h in request.Headers)
            if (!SkipHeaders.Contains(h.Key)) headers.Add(new(h.Key, string.Join(", ", h.Value)));
        byte[]? body = null;
        if (request.Content is not null)
        {
            foreach (var h in request.Content.Headers)
                if (!SkipHeaders.Contains(h.Key)) headers.Add(new(h.Key, string.Join(", ", h.Value)));
            body = await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        return new BrowserFetchRequest(request.Method.Method, request.RequestUri!.AbsoluteUri, headers, body);
    }

    public static HttpResponseMessage ToHttpResponse(HttpRequestMessage request, BrowserFetchResult r)
    {
        var resp = new HttpResponseMessage((HttpStatusCode)r.Status)
        {
            ReasonPhrase = string.IsNullOrEmpty(r.StatusText) ? null : r.StatusText,
            Content      = new ByteArrayContent(r.Body),
        };
        foreach (var (key, value) in r.Headers)
        {
            if (key.Equals("content-length", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("content-encoding", StringComparison.OrdinalIgnoreCase)) continue; // 本文はブラウザが展開済み
            if (!resp.Headers.TryAddWithoutValidation(key, value))
                resp.Content.Headers.TryAddWithoutValidation(key, value);
        }
        // リダイレクト追従後の URL を最終 URI として見せる (PostClient 等が転送の有無を判定に使う)
        if (r.Redirected && Uri.TryCreate(r.FinalUrl, UriKind.Absolute, out var final)) request.RequestUri = final;
        resp.RequestMessage = request;
        return resp;
    }

    private static HttpResponseMessage LoginRequiredResponse(HttpRequestMessage request, BrowserFetchResult r)
    {
        var resp = ToHttpResponse(request, r);
        resp.StatusCode   = HttpStatusCode.Unauthorized;
        resp.ReasonPhrase = RedditResponse.IsNetworkSecurityBlock(r)
            ? "reddit: ブラウザでの確認が必要です"
            : "reddit: ログインが必要です";
        return resp;
    }
}
