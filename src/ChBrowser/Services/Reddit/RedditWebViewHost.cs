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
    public static async Task InitializeProfileAsync(WpfWebView2 webView)
    {
        var env     = await ChBrowser.Controls.WebView2Helper.GetEnvironmentAsync().ConfigureAwait(true);
        var options = env.CreateCoreWebView2ControllerOptions();
        options.ProfileName = ProfileName;
        await webView.EnsureCoreWebView2Async(env, options).ConfigureAwait(true);
    }

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

    private async Task<BrowserFetchResult> FetchOnUiAsync(BrowserFetchRequest request, CancellationToken ct)
    {
        await EnsureInitializedAsync().ConfigureAwait(true);
        await EnsureOnHomeAsync().ConfigureAwait(true);

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

    /// <summary>fetch は reddit.com オリジンから行う必要がある (同一オリジンで Cookie が付く)。ページが他所へ行っていたら戻す。</summary>
    private async Task EnsureOnHomeAsync()
    {
        var src = _webView!.CoreWebView2.Source ?? "";
        if (Uri.TryCreate(src, UriKind.Absolute, out var u) && u.Host.Equals("www.reddit.com", StringComparison.OrdinalIgnoreCase)) return;
        await NavigateAsync(HomeUrl).ConfigureAwait(true);
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
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
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
