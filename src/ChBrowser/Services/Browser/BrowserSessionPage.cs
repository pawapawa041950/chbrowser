using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using WpfWebView2 = Microsoft.Web.WebView2.Wpf.WebView2;

namespace ChBrowser.Services.Browser;

/// <summary>掲示板のページの中で JS を実行する口 (<see cref="BrowserSessionPage"/>)。掲示板のページが自分で送る要求
/// (ふたばの そうだね 等) を、同じページ・同じ Cookie から送るのに使う。</summary>
public interface IBrowserPageRunner
{
    /// <summary><paramref name="pageUrl"/> を開いて (既に開いていればそのまま)、<paramref name="script"/> を実行し、その戻り値 (文字列) を返す。
    /// スクリプトは同期的に文字列を返す式にすること (<c>ExecuteScriptAsync</c> は Promise を待てない)。</summary>
    Task<string> RunAsync(string pageUrl, string script, CancellationToken ct);
}

/// <summary>掲示板ごとのブラウザセッション (<see cref="BrowserProfiles"/>) で、画面外の小窓に WebView2 を 1 つ常駐させ、
/// そのページの中で JS を実行する (<c>doc/multi-bbs-design.md</c> §8.8.6)。WebView2 は UI スレッド専用なので呼び出しは
/// <see cref="Dispatcher"/> に回し、同時に 1 件ずつ実行する。初期化は最初の要求時。</summary>
public sealed class BrowserSessionPage : IBrowserPageRunner, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly string     _profileName;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Window?      _hostWindow;
    private WpfWebView2? _webView;
    private Task?        _initTask;
    private bool         _disposed;

    public BrowserSessionPage(Dispatcher dispatcher, string profileName)
    {
        _dispatcher  = dispatcher;
        _profileName = profileName;
    }

    public Task<string> RunAsync(string pageUrl, string script, CancellationToken ct)
        => _dispatcher.InvokeAsync(() => RunOnUiAsync(pageUrl, script, ct)).Task.Unwrap();

    private async Task<string> RunOnUiAsync(string pageUrl, string script, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            await EnsureInitializedAsync().ConfigureAwait(true);
            var core = _webView!.CoreWebView2;
            if (!IsSamePage(core.Source, pageUrl))
            {
                var ok = await NavigateAsync(pageUrl, ct).ConfigureAwait(true);
                if (!ok) throw new InvalidOperationException("ページを開けませんでした");
            }
            var json = await core.ExecuteScriptAsync(script).ConfigureAwait(true);
            return JsonSerializer.Deserialize<string>(json) ?? "";
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsSamePage(string? current, string target)
        => Uri.TryCreate(current, UriKind.Absolute, out var a) && Uri.TryCreate(target, UriKind.Absolute, out var b)
           && string.Equals(a.GetLeftPart(UriPartial.Path), b.GetLeftPart(UriPartial.Path), StringComparison.OrdinalIgnoreCase);

    private Task EnsureInitializedAsync()
    {
        // 終了処理の後に隠し窓を作り直すと「最後のウィンドウ」が残ってプロセスが終わらなくなる
        if (_disposed) return Task.FromException(new ObjectDisposedException(nameof(BrowserSessionPage), "アプリの終了中です"));
        if (_initTask is { IsFaulted: true } or { IsCanceled: true })
        {
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
        // WebView2 は HWND を持つウィンドウ内に無いと初期化が完了しないので、画面外の小窓に載せる (reddit のセッションと同じ)
        _webView = new WpfWebView2();
        _hostWindow = new Window
        {
            Title         = $"ChBrowser {_profileName} session",
            Width         = 320,
            Height        = 240,
            Left          = -32000,
            Top           = -32000,
            WindowStyle   = WindowStyle.None,
            ResizeMode    = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content       = _webView,
        };
        _hostWindow.Show();
        await BrowserProfiles.InitializeAsync(_webView, _profileName).ConfigureAwait(true);
        if (_disposed) { try { _hostWindow.Close(); } catch (InvalidOperationException) { } throw new ObjectDisposedException(nameof(BrowserSessionPage)); }
        // 画面外なので音・新しい窓は出さない
        _webView.CoreWebView2.IsMuted = true;
        _webView.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
        // ページの alert 等は出さない (応答は呼び出し側が判定する)
        _webView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
    }

    private async Task<bool> NavigateAsync(string url, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Done(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            _webView!.CoreWebView2.NavigationCompleted -= Done;
            if (!e.IsSuccess)
                ChBrowser.Services.Logging.LogService.Instance.Write($"[{_profileName} session] navigate {url} failed: {e.WebErrorStatus} (http {e.HttpStatusCode})");
            tcs.TrySetResult(e.IsSuccess);
        }
        _webView!.CoreWebView2.NavigationCompleted += Done;
        _webView.CoreWebView2.Navigate(url);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var reg = timeout.Token.Register(() => tcs.TrySetResult(false));
        return await tcs.Task.ConfigureAwait(true);
    }

    public void Dispose()
    {
        _disposed = true;
        try { _hostWindow?.Close(); } catch (InvalidOperationException) { }
        _webView?.Dispose();
    }
}
