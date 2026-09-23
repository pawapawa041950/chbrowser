using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using WpfWebView2 = Microsoft.Web.WebView2.Wpf.WebView2;

namespace ChBrowser.Services.Reddit;

/// <summary>reddit にログインしてもらう (または「network security」等の確認ページを通してもらう) 窓 (<c>doc/reddit-design.md</c> §5.4〜5.5)。
/// 中の WebView2 はセッション用の隠し WebView と同じ reddit 用プロファイルなので、ここでのログインがそのままアプリの通信に効く。
/// ログインの資格情報は reddit のページに直接入力され、アプリは受け取らない。
///
/// ページ遷移のたびに <c>/api/me.json</c> を確かめ、ログインが確認できたら自動で閉じる (確認ページの場合は、確認対象の URL が
/// JSON を返すようになったら閉じる)。ユーザが閉じた場合はキャンセル扱い。</summary>
public sealed class RedditLoginWindow : Window
{
    private readonly RedditLoginReason _reason;
    private readonly string            _url;
    private readonly IBrowserFetcher   _fetcher;
    private readonly WpfWebView2       _webView = new();
    private readonly TextBlock         _status  = new() { Margin = new Thickness(8, 4, 8, 4), TextWrapping = TextWrapping.Wrap };
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _checking;

    private RedditLoginWindow(RedditLoginReason reason, string url, IBrowserFetcher fetcher)
    {
        _reason  = reason;
        _url     = url;
        _fetcher = fetcher;

        Title  = reason == RedditLoginReason.Login ? "reddit にログイン" : "reddit: ブラウザでの確認";
        Width  = 560;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var header = new TextBlock
        {
            Margin       = new Thickness(8, 8, 8, 4),
            TextWrapping = TextWrapping.Wrap,
            Text = reason == RedditLoginReason.Login
                ? "reddit のページでログインしてください。ログインが確認できると、この窓は自動で閉じて元の操作を続けます。" +
                  "パスワード等はアプリには渡らず、ログイン状態 (Cookie) はアプリ内ブラウザの reddit 用プロファイルに保存されます。"
                : "reddit が確認を求めています。ページの指示に従って確認を済ませてください。済んだらこの窓は自動で閉じます " +
                  "(閉じない場合は右上の × で閉じてください)。",
        };
        _status.Foreground = Brushes.DimGray;

        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        dock.Children.Add(header);
        dock.Children.Add(_status);
        dock.Children.Add(_webView);
        Content = dock;

        Loaded += async (_, _) => await InitAsync();
        Closed += (_, _) =>
        {
            _result.TrySetResult(false);
            _webView.Dispose();
        };
    }

    /// <summary>窓を出して、閉じられるまで待つ。戻り値はログイン (確認) が済んだか。UI スレッドから呼ぶこと。</summary>
    public static Task<bool> ShowAndWaitAsync(RedditLoginReason reason, string url, IBrowserFetcher fetcher, Window? owner)
    {
        var w = new RedditLoginWindow(reason, url, fetcher);
        if (owner is { IsLoaded: true }) w.Owner = owner;
        w.Show();
        w.Activate();
        return w._result.Task;
    }

    private async Task InitAsync()
    {
        try
        {
            await RedditWebViewHost.InitializeProfileAsync(_webView);
            _webView.CoreWebView2.NavigationCompleted += async (_, _) => await CheckAsync();
            // 確認ページのときは、弾かれた API の URL (JSON) ではなく人が見る reddit のトップを開く。
            // 閉じてよいかの判定は元の URL が JSON を返すかで行う (CheckAsync)。
            _webView.CoreWebView2.Navigate(_reason == RedditLoginReason.Challenge ? "https://www.reddit.com/" : _url);
        }
        catch (Exception ex)
        {
            _status.Text = $"ブラウザを初期化できませんでした: {ex.Message}";
        }
    }

    /// <summary>ログイン / 確認が済んだかをセッション側の fetch で確かめ、済んでいれば閉じる。</summary>
    private async Task CheckAsync()
    {
        if (_checking || _result.Task.IsCompleted) return;
        _checking = true;
        try
        {
            var target = _reason == RedditLoginReason.Login ? RedditSessionAuth.MeUrl : _url;
            var r = await _fetcher.FetchAsync(
                new BrowserFetchRequest("GET", target, new[] { new System.Collections.Generic.KeyValuePair<string, string>("Accept", "application/json") }),
                CancellationToken.None);
            var ok = _reason == RedditLoginReason.Login
                ? RedditSessionAuth.ParseMe(r).Name is { Length: > 0 }
                : r.Status is >= 200 and < 300 && RedditResponse.LooksLikeJson(r);
            if (ok)
            {
                _result.TrySetResult(true);
                Close();
                return;
            }
            _status.Text = _reason == RedditLoginReason.Login ? "ログインを待っています…" : "確認を待っています…";
        }
        catch (Exception ex)
        {
            _status.Text = $"状態を確認できませんでした: {ex.Message}";
        }
        finally
        {
            _checking = false;
        }
    }
}

/// <summary><see cref="IRedditLoginUi"/> の実装 (UI スレッドでログイン窓を出す)。</summary>
public sealed class RedditLoginUi : IRedditLoginUi
{
    private readonly Dispatcher      _dispatcher;
    private readonly IBrowserFetcher _fetcher;

    public RedditLoginUi(Dispatcher dispatcher, IBrowserFetcher fetcher)
    {
        _dispatcher = dispatcher;
        _fetcher    = fetcher;
    }

    public Task<bool> ShowAsync(RedditLoginReason reason, string? url, CancellationToken ct)
        => _dispatcher.InvokeAsync(() =>
        {
            // メインウィンドウが閉じた後 (終了処理中) には窓を出さない (プロセスが終わらなくなるため)
            var main = Application.Current?.MainWindow;
            if (main is null || !main.IsLoaded) return Task.FromResult(false);
            return RedditLoginWindow.ShowAndWaitAsync(reason, url ?? RedditSessionAuth.LoginUrl, _fetcher, main);
        }).Task.Unwrap();
}
