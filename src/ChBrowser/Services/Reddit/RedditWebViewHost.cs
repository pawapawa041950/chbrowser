using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using WpfWebView2 = Microsoft.Web.WebView2.Wpf.WebView2;

namespace ChBrowser.Services.Reddit;

/// <summary>reddit のログインセッションを持つ WebView2 (<c>doc/reddit-design.md</c> §5.2〜5.3)。
///
/// <list type="bullet">
/// <item><description>アプリ共通の環境に <b>別プロファイル</b> (<see cref="ProfileName"/>) で載せる。Cookie・ストレージはスレ表示用の
///   WebView と分離され、exe の隣の <c>ChBrowser.exe.WebView2\</c> 内に保存されるのでアプリ再起動後もログインが残る。</description></item>
/// <item><description>画面外に置いた小さな隠しウィンドウに WebView を 1 つ常駐させ、<c>https://www.reddit.com/robots.txt</c> を開いておく。
///   要求はそのページで <c>fetch(…, {credentials:'include'})</c> として実行し、結果を <c>postMessage</c> で受け取る
///   (<c>ExecuteScriptAsync</c> は Promise を待てないため)。</description></item>
/// <item><description>WebView2 は UI スレッド専用なので、呼び出しはすべて <see cref="Dispatcher"/> に回す。初期化は最初の要求時 (起動時には何もしない)。</description></item>
/// <item><description>UA は上書きしない (ブラウザ既定のまま、決定 D30)。</description></item>
/// </list></summary>
public sealed class RedditWebViewHost : IBrowserFetcher, IDisposable
{
    public const string ProfileName = "reddit";
    private const string HomeUrl = "https://www.reddit.com/robots.txt";
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(45);

    private readonly Dispatcher _dispatcher;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BrowserFetchResult>> _pending = new();
    private readonly string _nonce = Guid.NewGuid().ToString("N");
    private Task? _initTask;
    private Window? _hostWindow;
    private WpfWebView2? _webView;

    public RedditWebViewHost(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>reddit 用プロファイルで WebView2 を初期化する (ログイン窓の WebView もこれで同じセッションを共有する)。</summary>
    public static Task InitializeProfileAsync(WpfWebView2 webView)
        => ChBrowser.Services.Browser.BrowserProfiles.InitializeAsync(webView, ProfileName);

    public Task<BrowserFetchResult> FetchAsync(BrowserFetchRequest request, CancellationToken ct)
        => _dispatcher.InvokeAsync(() => FetchOnUiAsync(request, ct)).Task.Unwrap();

    public Task ClearCookiesAsync()
        => _dispatcher.InvokeAsync(async () =>
        {
            await EnsureInitializedAsync().ConfigureAwait(true);
            _webView!.CoreWebView2.CookieManager.DeleteAllCookies();
        }).Task.Unwrap();

    /// <summary>reddit のログイン Cookie の名前。</summary>
    public const string LoginCookieName = "reddit_session";

    /// <summary>reddit の Cookie の値 (無ければ null)。Web 版の部分読み込みの POST に付ける <c>csrf_token</c> を読むのに使う。</summary>
    public Task<string?> GetCookieValueAsync(string name)
        => _dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await EnsureInitializedAsync().ConfigureAwait(true);
                var cookies = await _webView!.CoreWebView2.CookieManager.GetCookiesAsync("https://www.reddit.com/").ConfigureAwait(true);
                foreach (var c in cookies)
                    if (string.Equals(c.Name, name, StringComparison.Ordinal) && !string.IsNullOrEmpty(c.Value)) return c.Value;
                return (string?)null;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                return null;
            }
        }).Task.Unwrap();

    public Task<bool?> HasLoginCookieAsync()
        => _dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await EnsureInitializedAsync().ConfigureAwait(true);
                var cookies = await _webView!.CoreWebView2.CookieManager.GetCookiesAsync("https://www.reddit.com/").ConfigureAwait(true);
                foreach (var c in cookies)
                    if (string.Equals(c.Name, LoginCookieName, StringComparison.Ordinal) && !string.IsNullOrEmpty(c.Value)) return (bool?)true;
                return false;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                return null;
            }
        }).Task.Unwrap();

    /// <summary>接続テスト用の診断: 隠しページの状態・Cookie の名前 (値は出さない)・要求の種類ごとの通り方を、
    /// ログイン窓を出さずに (通信経路を通さず直接) 確かめて行ごとに返す。最後に隠しページを元に戻す。</summary>
    public Task<IReadOnlyList<string>> DiagnoseAsync(CancellationToken ct, string? sampleDir = null)
        => _dispatcher.InvokeAsync(async () =>
        {
            var lines = new List<string>();
            await EnsureInitializedAsync().ConfigureAwait(true);
            var wv = _webView!.CoreWebView2;
            lines.Add($"UA: {wv.Settings.UserAgent}");

            async Task Page(string label)
            {
                var raw = await wv.ExecuteScriptAsync(
                    "JSON.stringify({href: location.href, title: document.title, type: document.contentType, " +
                    "challenge: !!document.querySelector('input[name=js_challenge]'), text: (document.body ? document.body.innerText : '').slice(0, 80)})")
                    .ConfigureAwait(true);
                var json = JsonSerializer.Deserialize<string>(raw) ?? "";
                lines.Add($"{label}: {json}");
            }

            async Task Cookies()
            {
                var cookies = await wv.CookieManager.GetCookiesAsync("https://www.reddit.com/").ConfigureAwait(true);
                var names = new List<string>();
                foreach (var c in cookies)
                    names.Add($"{c.Name}({c.Domain}{(c.IsSession ? ", session" : $", 期限 {c.Expires:yyyy-MM-dd HH:mm}")}{(c.IsHttpOnly ? ", httpOnly" : "")}, {c.Value.Length}文字)");
                lines.Add($"Cookie ({names.Count} 個): {string.Join(" / ", names)}");
            }

            async Task Try(string label, string url, bool jsonAccept, string? referrer = null, string? saveAs = null)
            {
                await Task.Delay(300, ct).ConfigureAwait(true);   // 連続で送りすぎない
                try
                {
                    var headers = jsonAccept
                        ? new[] { new KeyValuePair<string, string>("Accept", "application/json") }
                        : Array.Empty<KeyValuePair<string, string>>();
                    var r = await FetchOnceOnUiAsync(new BrowserFetchRequest("GET", url, headers), ct, referrer).ConfigureAwait(true);
                    var head = System.Text.Encoding.UTF8.GetString(r.Body, 0, Math.Min(r.Body.Length, 262144));
                    var kind = RedditResponse.LooksLikeJson(r) ? "JSON"
                             : RedditResponse.IsNetworkSecurityBlock(r) ? "network security"
                             : head.Contains("js_challenge", StringComparison.Ordinal) ? "JS 確認ページ"
                             : "その他";
                    var extra = url.Contains("/api/me.json", StringComparison.Ordinal) && kind == "JSON"
                        ? $" name={(RedditSessionAuth.ParseMe(r).Name is { Length: > 0 } nm ? nm : "(無し)")}" : "";
                    lines.Add($"{label}: {r.Status} {r.Header("content-type")} {r.Body.Length}B → {kind}{extra}{(r.Redirected ? $" 転送先 {r.FinalUrl}" : "")}");
                    if (saveAs is not null && sampleDir is not null && r.Status == 200)
                    {
                        try { System.IO.File.WriteAllBytes(System.IO.Path.Combine(sampleDir, saveAs), r.Body); }
                        catch (System.IO.IOException) { }
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    lines.Add($"{label}: 失敗 {ex.Message}");
                }
            }

            // アプリが使う読み取りの要求 (書き込み・評価は試さない)。送り元はアプリと同じ (ホーム)
            const string O = "https://www.reddit.com";
            var endpoints = new (string Label, string Url, string? Save)[]
            {
                ("me.json",                     $"{O}/api/me.json?raw_json=1", null),
                ("板のスレ一覧 hot.json",        $"{O}/r/redditdev/hot.json?limit=5&raw_json=1", null),
                ("板情報 about.json",            $"{O}/r/redditdev/about.json?raw_json=1", null),
                ("スレ comments.json",           $"{O}/r/redditdev/comments/1wubcvf.json?sort=old&limit=20&raw_json=1", null),
                ("ホットなスレ r/popular",       $"{O}/r/popular/hot.json?limit=5&raw_json=1", null),
                ("ホットな板 subreddits/popular", $"{O}/subreddits/popular.json?limit=5&raw_json=1", null),
                ("購読一覧 subreddits/mine",      $"{O}/subreddits/mine/subscriber.json?limit=5&raw_json=1", null),
                ("板検索 subreddits/search",      $"{O}/subreddits/search.json?q=python&limit=5&raw_json=1", null),
                ("板検索 autocomplete_v2",        $"{O}/api/subreddit_autocomplete_v2.json?query=python&include_over_18=true&include_profiles=false&limit=10&raw_json=1", null),
                ("板検索 search_reddit_names",    $"{O}/api/search_reddit_names.json?query=python&include_over_18=true", null),
                ("板検索 search.json type=sr",    $"{O}/search.json?q=python&type=sr&limit=5&raw_json=1", null),
                ("板検索 HTML",                  $"{O}/search/?q=python&type=communities", "probe_search_communities.html"),
                ("投稿者情報 user_data_by_account_ids", $"{O}/api/user_data_by_account_ids.json?ids=t2_4amlb", null),
            };
            async Task Probes(string prefix)
            {
                foreach (var (label, url, save) in endpoints)
                    await Try($"{prefix} {label}", url, save is null, saveAs: save).ConfigureAwait(true);
                await Try($"{prefix} me.json (送り元=今のページ)", $"{O}/api/me.json?raw_json=1", true, "about:client").ConfigureAwait(true);
            }

            // コメントの続き (morechildren): アプリは GET が弾かれたら同じ内容を POST で送り直す。その両方を試す (読み取りのみ)
            async Task MoreChildren(string prefix)
            {
                try
                {
                    await Task.Delay(300, ct).ConfigureAwait(true);
                    var json = new[] { new KeyValuePair<string, string>("Accept", "application/json") };
                    var me = await FetchOnceOnUiAsync(new BrowserFetchRequest("GET", $"{O}/api/me.json?raw_json=1", json), ct).ConfigureAwait(true);
                    var modhash = RedditSessionAuth.ParseMe(me).Modhash;
                    await Task.Delay(300, ct).ConfigureAwait(true);
                    var thread = await FetchOnceOnUiAsync(new BrowserFetchRequest("GET",
                        $"{O}/r/redditdev/comments/1wubcvf.json?sort=old&limit=20&raw_json=1", json), ct).ConfigureAwait(true);
                    var ids = new List<string>();
                    using (var doc = JsonDocument.Parse(thread.Body))
                        foreach (var c in doc.RootElement[1].GetProperty("data").GetProperty("children").EnumerateArray())
                            if (c.GetProperty("kind").GetString() == "more")
                                foreach (var id in c.GetProperty("data").GetProperty("children").EnumerateArray())
                                    if (ids.Count < 20 && id.GetString() is { Length: > 0 } s) ids.Add(s);
                    if (ids.Count == 0) { lines.Add($"{prefix} 続き (morechildren): 続きの無いスレだったので未測定"); return; }

                    var query = $"api_type=json&link_id=t3_1wubcvf&children={string.Join(",", ids)}&sort=old&raw_json=1";
                    await Try($"{prefix} 続き morechildren (GET, {ids.Count} 件)", $"{O}/api/morechildren.json?{query}", true).ConfigureAwait(true);
                    // ネット上で「通る」と報告されている別の経路 (2026-10): www の末尾スラッシュ付き、old.reddit.com、api.reddit.com。
                    // 別ホストはそのホストのページに移ってから送る (同一オリジン)
                    await Try($"{prefix} 続き morechildren (GET, www 末尾スラッシュ)", $"{O}/api/morechildren/.json?{query}", true).ConfigureAwait(true);
                    await Try($"{prefix} 続き morechildren (GET, old.reddit.com)", $"https://old.reddit.com/api/morechildren.json?{query}", true,
                              saveAs: "probe_morechildren_old.json").ConfigureAwait(true);
                    await Try($"{prefix} 続き morechildren (GET, api.reddit.com)", $"https://api.reddit.com/api/morechildren.json?{query}", true).ConfigureAwait(true);
                    await Try($"{prefix} スレ comments.json (GET, old.reddit.com)",
                              $"https://old.reddit.com/r/redditdev/comments/1wubcvf.json?sort=old&limit=20&raw_json=1", true).ConfigureAwait(true);

                    await Task.Delay(300, ct).ConfigureAwait(true);
                    var headers = new List<KeyValuePair<string, string>>
                    {
                        new("Accept", "application/json"),
                        new("Content-Type", "application/x-www-form-urlencoded"),
                    };
                    if (modhash is { Length: > 0 }) headers.Add(new("X-Modhash", modhash));
                    var r = await FetchOnceOnUiAsync(new BrowserFetchRequest("POST", $"{O}/api/morechildren.json", headers,
                        System.Text.Encoding.UTF8.GetBytes(query)), ct).ConfigureAwait(true);
                    var things = -1;
                    if (RedditResponse.LooksLikeJson(r))
                    {
                        using var doc = JsonDocument.Parse(r.Body);
                        if (doc.RootElement.TryGetProperty("json", out var j) && j.TryGetProperty("data", out var d)
                            && d.TryGetProperty("things", out var th)) things = th.GetArrayLength();
                    }
                    var kind = RedditResponse.LooksLikeJson(r) ? "JSON" : RedditResponse.IsNetworkSecurityBlock(r) ? "network security" : "その他";
                    lines.Add($"{prefix} 続き morechildren (POST, {ids.Count} 件, modhash {(modhash is { Length: > 0 } ? "あり" : "なし")}): " +
                              $"{r.Status} {r.Header("content-type")} {r.Body.Length}B → {kind}{(things >= 0 ? $" things={things}" : "")}");
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                              or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException)
                {
                    lines.Add($"{prefix} 続き (morechildren): 失敗 {ex.Message}");
                }
            }

            // あなたへのおすすめ: ホームの HTML と、そこに書かれている続きのページ (/svc/shreddit/feeds/home-feed?…)
            async Task HomeFeed(string prefix)
            {
                try
                {
                    await Task.Delay(300, ct).ConfigureAwait(true);
                    var home = await FetchOnceOnUiAsync(new BrowserFetchRequest("GET", $"{O}/", Array.Empty<KeyValuePair<string, string>>()), ct).ConfigureAwait(true);
                    var html = System.Text.Encoding.UTF8.GetString(home.Body);
                    var posts = System.Text.RegularExpressions.Regex.Matches(html, @"<shreddit-post\b").Count;
                    var m = System.Text.RegularExpressions.Regex.Match(html, @"src=""(?<src>/svc/shreddit/feeds/(?:interest-)?home-feed\?[^""]*)""");
                    lines.Add($"{prefix} おすすめ ホーム HTML: {home.Status} {home.Body.Length}B shreddit-post={posts} 続き={(m.Success ? "あり" : "なし")}");
                    if (!m.Success) return;
                    await Try($"{prefix} おすすめ 続きのページ", O + System.Net.WebUtility.HtmlDecode(m.Groups["src"].Value), false).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    lines.Add($"{prefix} おすすめ: 失敗 {ex.Message}");
                }
            }

            await NavigateAsync(HomeUrl).ConfigureAwait(true);
            await Page("隠しページ").ConfigureAwait(true);
            await Cookies().ConfigureAwait(true);
            await Probes("[robots.txt 上]").ConfigureAwait(true);
            await MoreChildren("[robots.txt 上]").ConfigureAwait(true);
            await HomeFeed("[robots.txt 上]").ConfigureAwait(true);

            // 比較用: ホームを開いて (JS の確認ページがあればそれも通して) から、もう一度試す
            await OpenRealPageAsync(ct).ConfigureAwait(true);
            await Page("ホームを開いた後の隠しページ").ConfigureAwait(true);
            await Probes("[ホーム上]").ConfigureAwait(true);

            // 元の robots.txt に戻す (アプリの通常の状態)
            await NavigateAsync(HomeUrl).ConfigureAwait(true);
            return (IReadOnlyList<string>)lines;
        }).Task.Unwrap();

    /// <summary>fetch の送り元 (Referer) として名乗る URL。reddit は送り元が reddit のページでない要求 (robots.txt 等) を
    /// ログイン中でも「network security」で弾くようになった (2026-10、接続テストの診断で確認)。</summary>
    private const string ReferrerUrl = "https://www.reddit.com/";

    // 送り元を指定しても弾かれたときに隠しページでホームを開いて送り直す処理は外した: 接続テストの診断 (2026-10-28) で、
    // 送り元の指定で通らない要求 (板検索・morechildren の GET) はホーム上からでも通らず、重いホームを開き続けるだけだったため
    private Task<BrowserFetchResult> FetchOnUiAsync(BrowserFetchRequest request, CancellationToken ct)
        => FetchOnceOnUiAsync(request, ct);

    /// <summary>(診断用) 隠しページで reddit のホームを開き、JS の確認ページが済むまで (最大 15 秒) 待つ。</summary>
    private async Task OpenRealPageAsync(CancellationToken ct)
    {
        _webView!.CoreWebView2.IsMuted = true;   // ホームの動画等が隠しページで鳴らないように
        await NavigateAsync("https://www.reddit.com/").ConfigureAwait(true);
        for (var i = 0; i < 30; i++)
        {
            var raw = await _webView!.CoreWebView2.ExecuteScriptAsync(
                "document.readyState === 'complete' && !document.querySelector('input[name=js_challenge]')").ConfigureAwait(true);
            if (raw == "true") break;
            await Task.Delay(500, ct).ConfigureAwait(true);
        }
    }

    private async Task<BrowserFetchResult> FetchOnceOnUiAsync(BrowserFetchRequest request, CancellationToken ct, string? referrer = null)
    {
        await EnsureInitializedAsync().ConfigureAwait(true);
        var host = await EnsureOnOriginAsync(request.Url).ConfigureAwait(true);

        var id  = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<BrowserFetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(FetchTimeout);
        using var reg = timeout.Token.Register(() =>
        {
            if (_pending.TryRemove(id, out var t))
                t.TrySetException(ct.IsCancellationRequested
                    ? new OperationCanceledException(ct)
                    : new TaskCanceledException($"reddit: 応答がありません ({FetchTimeout.TotalSeconds} 秒)"));
        });

        var spec = JsonSerializer.Serialize(new
        {
            id,
            nonce   = _nonce,
            method  = request.Method,
            url     = request.Url,
            headers = ToHeaderObject(request.Headers),
            body    = request.Body is null ? null : Convert.ToBase64String(request.Body),
            referrer = referrer ?? (host == "www.reddit.com" ? ReferrerUrl : $"https://{host}/"),
        });
        await _webView!.CoreWebView2.ExecuteScriptAsync(FetchScript.Replace("__SPEC__", spec)).ConfigureAwait(true);
        return await tcs.Task.ConfigureAwait(true);
    }

    private static Dictionary<string, string> ToHeaderObject(IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in headers) d[k] = v;
        return d;
    }

    /// <summary>ページ内で走らせる fetch。本文はバイナリのまま base64 で返す (文字コードは呼び出し側が決める)。</summary>
    private const string FetchScript = """
(async () => {
  const q = __SPEC__;
  const reply = (o) => { o.chbFetch = q.id; o.nonce = q.nonce; window.chrome.webview.postMessage(o); };
  try {
    const init = { method: q.method, headers: q.headers, credentials: 'include', redirect: 'follow', cache: 'no-store' };
    if (q.referrer) init.referrer = q.referrer;
    if (q.body != null) init.body = Uint8Array.from(atob(q.body), c => c.charCodeAt(0));
    const r = await fetch(q.url, init);
    const buf = new Uint8Array(await r.arrayBuffer());
    let s = '';
    for (let i = 0; i < buf.length; i += 0x8000) s += String.fromCharCode.apply(null, buf.subarray(i, i + 0x8000));
    const h = [];
    r.headers.forEach((v, k) => h.push([k, v]));
    reply({ status: r.status, statusText: r.statusText, url: r.url, redirected: r.redirected, headers: h, body: btoa(s) });
  } catch (e) {
    reply({ error: String(e && e.message || e) });
  }
})();
""";

    private bool _disposed;

    private Task EnsureInitializedAsync()
    {
        // 終了処理の後に来た要求で隠し窓を作り直すと、また「最後のウィンドウ」が残ってプロセスが終わらなくなる
        if (_disposed) return Task.FromException(new ObjectDisposedException(nameof(RedditWebViewHost), "アプリの終了中です"));
        if (_initTask is { IsFaulted: true } or { IsCanceled: true })
        {
            // 前回の初期化が失敗していたら作り直す (WebView2 ランタイムの一時的な失敗等)
            try { _hostWindow?.Close(); } catch (InvalidOperationException) { }
            _webView?.Dispose();
            _hostWindow = null;
            _webView    = null;
            _initTask   = null;
        }
        return _initTask ??= InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        // WebView2 は visual tree 上 (= HWND を持つウィンドウ内) に無いと初期化が完了しないので、画面外の小窓に載せる。
        _webView = new WpfWebView2();
        _hostWindow = new Window
        {
            Title            = "ChBrowser reddit session",
            Width            = 320,
            Height           = 240,
            Left             = -32000,
            Top              = -32000,
            WindowStyle      = WindowStyle.None,
            ResizeMode       = ResizeMode.NoResize,
            ShowInTaskbar    = false,
            ShowActivated    = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content          = _webView,
        };
        _hostWindow.Show();
        await InitializeProfileAsync(_webView).ConfigureAwait(true);
        if (_disposed) { try { _hostWindow.Close(); } catch (InvalidOperationException) { } throw new ObjectDisposedException(nameof(RedditWebViewHost)); }
        _webView.CoreWebView2.WebMessageReceived += OnWebMessage;
        await NavigateAsync(HomeUrl).ConfigureAwait(true);
        ChBrowser.Services.Logging.LogService.Instance.Write($"[reddit] session webview ready (profile={ProfileName}, ua={_webView.CoreWebView2.Settings.UserAgent})");
    }

    /// <summary>fetch は要求先と同じオリジンから行う必要がある (同一オリジンで Cookie が付く。別オリジンは CORS で読めない)。
    /// 要求先が reddit の別ホスト (old.reddit.com 等) ならそのホストの robots.txt へ、それ以外は www の robots.txt へ移る。移った先のホストを返す。</summary>
    private async Task<string> EnsureOnOriginAsync(string requestUrl)
    {
        var host = Uri.TryCreate(requestUrl, UriKind.Absolute, out var r) && r.Host.EndsWith(".reddit.com", StringComparison.OrdinalIgnoreCase)
            ? r.Host.ToLowerInvariant() : "www.reddit.com";
        var src = _webView!.CoreWebView2.Source ?? "";
        if (Uri.TryCreate(src, UriKind.Absolute, out var u) && u.Host.Equals(host, StringComparison.OrdinalIgnoreCase)) return host;
        await NavigateAsync(host == "www.reddit.com" ? HomeUrl : $"https://{host}/robots.txt").ConfigureAwait(true);
        return host;
    }

    private Task NavigateAsync(string url)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Done(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            _webView!.CoreWebView2.NavigationCompleted -= Done;
            if (!e.IsSuccess)
                ChBrowser.Services.Logging.LogService.Instance.Write($"[reddit] navigate {url} failed: {e.WebErrorStatus} (http {e.HttpStatusCode})");
            tcs.TrySetResult();
        }
        _webView!.CoreWebView2.NavigationCompleted += Done;
        _webView.CoreWebView2.Navigate(url);
        return tcs.Task;
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var perf = System.Diagnostics.Stopwatch.StartNew();   // 重さの調査用 (UI スレッドで動く)
        var json = e.WebMessageAsJson;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("chbFetch", out var idEl) || idEl.GetString() is not { } id) return;
            // ページ側の他のスクリプトから偽の応答を掴まされないよう、起動ごとの乱数と突き合わせる
            if (!root.TryGetProperty("nonce", out var n) || n.GetString() != _nonce) return;
            if (!_pending.TryRemove(id, out var tcs)) return;

            if (root.TryGetProperty("error", out var err))
            {
                tcs.TrySetException(new HttpRequestException($"reddit: 通信に失敗しました ({err.GetString()})"));
                return;
            }
            var headers = new List<KeyValuePair<string, string>>();
            if (root.TryGetProperty("headers", out var hs) && hs.ValueKind == JsonValueKind.Array)
                foreach (var pair in hs.EnumerateArray())
                    if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() == 2)
                        headers.Add(new(pair[0].GetString() ?? "", pair[1].GetString() ?? ""));
            tcs.TrySetResult(new BrowserFetchResult(
                root.GetProperty("status").GetInt32(),
                root.TryGetProperty("statusText", out var st) ? st.GetString() ?? "" : "",
                root.TryGetProperty("url", out var url) ? url.GetString() ?? "" : "",
                root.TryGetProperty("redirected", out var rd) && rd.ValueKind == JsonValueKind.True,
                headers,
                Convert.FromBase64String(root.GetProperty("body").GetString() ?? "")));
            if (perf.ElapsedMilliseconds >= 30)
                ChBrowser.Services.Logging.LogService.Instance.Write(
                    $"[perf] reddit の応答の受け取り {json.Length / 1024} KB に {perf.ElapsedMilliseconds} ms (UI スレッド)");
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write($"[reddit] bad web message: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var kv in _pending) kv.Value.TrySetCanceled();
        _pending.Clear();
        try { _hostWindow?.Close(); } catch (InvalidOperationException) { }
        _webView?.Dispose();
    }
}
