using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ChBrowser.Models;
using ChBrowser.Services.Bbs;
using Microsoft.Web.WebView2.Core;
using WpfWebView2 = Microsoft.Web.WebView2.Wpf.WebView2;

namespace ChBrowser.Services.Browser;

/// <summary>ブラウザの投稿窓 (4chan、決定 D40)。掲示板の本物の投稿フォームを開いて書き込みダイアログの内容 (本文・名前・添付) を入れておき、
/// ユーザが確認 (Cloudflare) と CAPTCHA を済ませて投稿ボタンを押すのを待つ。
///
/// <list type="bullet">
/// <item><description>ページの読み込みが終わるたびに <see cref="IBrowserPostProvider.ClassifyBrowserPostPage"/> で結果のページかを見る。
///   成功ならその結果で閉じる。エラー (CAPTCHA の間違い等) ならフォームのページを開き直して入れ直し、エラー文を窓の下に出す。</description></item>
/// <item><description>フォームのページでは、まだ入れていなければ入力する (ページごとに 1 回。ユーザが書き換えた後に上書きしない)。</description></item>
/// <item><description>ユーザが窓を閉じたら、直前のエラーがあればそれを、無ければ <see cref="PostOutcome.Cancelled"/> を返す。</description></item>
/// <item><description>WebView2 は掲示板ごとのプロファイル (<see cref="IBrowserPostProvider.BrowserProfileName"/>) なので、確認の Cookie は次回も使える。</description></item>
/// </list></summary>
public sealed class BrowserPostWindow : Window
{
    private readonly IBrowserPostProvider _provider;
    private readonly PostRequest          _request;
    private readonly Uri                  _pageUrl;
    private readonly WpfWebView2          _webView = new();
    private readonly TextBlock            _status  = new() { Margin = new Thickness(8, 4, 8, 6), TextWrapping = TextWrapping.Wrap };
    private readonly TaskCompletionSource<PostResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PostResult? _lastError;
    private bool        _busy;

    private BrowserPostWindow(IBrowserPostProvider provider, PostRequest request)
    {
        _provider = provider;
        _request  = request;
        _pageUrl  = provider.BrowserPostPageUrl(request);

        Title  = provider.BrowserPostWindowTitle;
        Width  = 820;
        Height = 860;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var header = new TextBlock
        {
            Margin       = new Thickness(8, 8, 8, 4),
            TextWrapping = TextWrapping.Wrap,
            Text = "書き込みダイアログの内容をこのページの投稿フォームに入れてあります。確認 (CAPTCHA) を済ませて、フォームの「Post」を押してください。" +
                   "投稿の結果はアプリが読み取り、この窓は自動で閉じます (やめるときは窓を閉じてください)。",
        };
        _status.Foreground = Brushes.DimGray;
        _status.Text = "ページを開いています…";

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
            _result.TrySetResult(_lastError ?? new PostResult(PostOutcome.Cancelled, "", ""));
            _webView.Dispose();
        };
    }

    /// <summary>窓を出して、結果が出るか閉じられるまで待つ。UI スレッドから呼ぶこと。</summary>
    public static Task<PostResult> ShowAndWaitAsync(IBrowserPostProvider provider, PostRequest request, Window? owner, CancellationToken ct)
    {
        var w = new BrowserPostWindow(provider, request);
        if (owner is { IsLoaded: true }) w.Owner = owner;
        if (ct.CanBeCanceled) ct.Register(() => w.Dispatcher.InvokeAsync(w.Close));
        w.Show();
        w.Activate();
        return w._result.Task;
    }

    private async Task InitAsync()
    {
        try
        {
            await BrowserProfiles.InitializeAsync(_webView, _provider.BrowserProfileName);
            var core = _webView.CoreWebView2;
            core.NavigationCompleted += async (_, e) => await OnNavigationCompletedAsync(e);
            // 規約等のリンク (別窓) は既定のブラウザで開く (投稿窓の中を移らない)
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                OpenExternal(e.Uri);
            };
            core.Navigate(_pageUrl.AbsoluteUri);
        }
        catch (Exception ex)
        {
            _status.Text = $"ブラウザを初期化できませんでした: {ex.Message}";
        }
    }

    private async Task OnNavigationCompletedAsync(CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_busy || _result.Task.IsCompleted) return;
        _busy = true;
        try
        {
            var core = _webView.CoreWebView2;
            if (core is null) return;
            // URL を解釈できないページ (data: 等) でも結果の判定は HTML で行う
            if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var url)) url = new Uri("about:blank");
            if (!e.IsSuccess)
            {
                _status.Text = $"ページを開けませんでした ({e.WebErrorStatus})。窓を閉じて、もう一度送信してください。";
                return;
            }

            var html = JsonSerializer.Deserialize<string>(
                await core.ExecuteScriptAsync("document.documentElement ? document.documentElement.outerHTML : ''")) ?? "";
            var result = _provider.ClassifyBrowserPostPage(url, html);
            if (result is { Outcome: PostOutcome.Success })
            {
                Log($"投稿成功 {url} (番号 {result.NewPostNumber})");
                _result.TrySetResult(result);
                Close();
                return;
            }
            if (result is not null)
            {
                // エラーのページ: フォームを開き直して入れ直す (窓を閉じればこのエラーが結果になる)
                Log($"投稿エラー {url}: {result.Message}");
                _lastError = result;
                _status.Text = $"投稿できませんでした: {result.Message}\nフォームを開き直しました。もう一度確認を済ませて「Post」を押してください。";
                core.Navigate(_pageUrl.AbsoluteUri);
                return;
            }

            var filled = await FillAsync(core);
            _status.Text = filled switch
            {
                "filled"  => _lastError is null
                    ? "入力しました。確認 (CAPTCHA) を済ませて「Post」を押してください。"
                    : $"前回のエラー: {_lastError.Message}\n入力し直しました。確認 (CAPTCHA) を済ませて「Post」を押してください。",
                "already" => _status.Text,
                _         => "投稿フォームが見つかりません。ページの確認 (ブラウザの確認画面等) を済ませてください。",
            };
            // 確認の画面を抜けた後にフォームのページへ戻っていなければ開き直す
            if (filled == "noform" && !IsSamePage(url, _pageUrl) && url.Host == _pageUrl.Host)
                core.Navigate(_pageUrl.AbsoluteUri);
        }
        catch (Exception ex)
        {
            _status.Text = $"ページを読み取れませんでした: {ex.Message}";
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>フォームに入力する。戻り値: <c>filled</c> (入れた) / <c>already</c> (このページでは入力済み) / <c>noform</c>。</summary>
    private async Task<string> FillAsync(CoreWebView2 core)
    {
        var fill = _provider.BuildBrowserPostFill(_request);
        var file = _request.Attachment is { } a && fill.FileFieldName is not null
            ? new { name = a.FileName, type = MimeTypeOf(a.FileName), data = Convert.ToBase64String(a.Data) }
            : null;
        var data = JsonSerializer.Serialize(new
        {
            form      = fill.FormSelector,
            fields    = fill.Fields.Select(kv => new[] { kv.Key, kv.Value }).ToArray(),
            fileField = fill.FileFieldName,
            file,
            prepare   = fill.PrepareScript,
            focus     = fill.FocusSelector,
        });
        var r = await core.ExecuteScriptAsync(FillScript + "(" + data + ")");
        return JsonSerializer.Deserialize<string>(r) ?? "noform";
    }

    private const string FillScript = @"(function (d) {
  try { if (d.prepare) (new Function(d.prepare))(); } catch (e) { }
  var f = document.querySelector(d.form);
  if (!f) return 'noform';
  if (f.dataset.chbFilled) return 'already';
  var q = function (n) { return f.querySelector('[name=""' + n + '""]'); };
  for (var i = 0; i < d.fields.length; i++) {
    var el = q(d.fields[i][0]);
    if (!el) continue;
    el.value = d.fields[i][1];
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
  }
  if (d.file && d.fileField) {
    var inp = q(d.fileField);
    if (inp) {
      var bin = atob(d.file.data), arr = new Uint8Array(bin.length);
      for (var j = 0; j < bin.length; j++) arr[j] = bin.charCodeAt(j);
      var dt = new DataTransfer();
      dt.items.add(new File([arr], d.file.name, { type: d.file.type }));
      inp.files = dt.files;
      inp.dispatchEvent(new Event('change', { bubbles: true }));
    }
  }
  f.dataset.chbFilled = '1';
  var t = (d.focus && document.querySelector(d.focus)) || f;
  t.scrollIntoView({ block: 'center' });
  return 'filled';
})";

    private static bool IsSamePage(Uri a, Uri b)
        => string.Equals(a.GetLeftPart(UriPartial.Path).TrimEnd('/'), b.GetLeftPart(UriPartial.Path).TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>添付の種類 (ファイル名の拡張子から)。</summary>
    public static string MimeTypeOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png"  => "image/png",
        ".gif"  => "image/gif",
        ".webp" => "image/webp",
        ".webm" => "video/webm",
        ".mp4"  => "video/mp4",
        ".pdf"  => "application/pdf",
        _       => "application/octet-stream",
    };

    private static void OpenExternal(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception ex) { Log($"外部ブラウザで開けませんでした {url}: {ex.Message}"); }
    }

    private static void Log(string message) => ChBrowser.Services.Logging.LogService.Instance.Write("[browserPost] " + message);
}

/// <summary><see cref="IBrowserPoster"/> の実装 (UI スレッドで投稿窓を出す)。</summary>
public sealed class BrowserPostUi : IBrowserPoster
{
    private readonly Dispatcher _dispatcher;

    public BrowserPostUi(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public Task<PostResult> PostAsync(IBrowserPostProvider provider, PostRequest request, CancellationToken ct)
        => _dispatcher.InvokeAsync(() =>
        {
            var main = Application.Current?.MainWindow;
            if (main is null || !main.IsLoaded) return Task.FromResult(new PostResult(PostOutcome.Cancelled, "", ""));
            return BrowserPostWindow.ShowAndWaitAsync(provider, request, main, ct);
        }).Task.Unwrap();
}
