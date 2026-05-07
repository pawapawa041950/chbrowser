using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using ChBrowser.Services.Image;
using ChBrowser.Services.Theme;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ChBrowser.Controls;

/// <summary>スレ表示タブ ViewModel が公開する、JS 側に渡すスクロール対象。
/// <see cref="WebView2Helper"/> が DataContext からこの値を読んで appendPosts メッセージに含める。</summary>
public interface IThreadDisplayBinding
{
    /// <summary>次回オープン時に viewport 下端に揃えたいレス番号 (= 「読了 prefix」の最大番号)。
    /// JS の <c>findReadProgressMaxNumber</c> が算定して保存し、appendPosts ペイロード経由で復元時に渡される。</summary>
    int? ScrollTargetPostNumber { get; }

    /// <summary>「以降新レス」ラベルの対象レス番号。null ならラベル非表示。
    /// appendPosts のメッセージに同梱して JS に push し、ラベル描画と dedup-tree の境界判定に使う。</summary>
    int? MarkPostNumber { get; }

    /// <summary>「自分の書き込み」としてマークされているレス番号集合。
    /// appendPosts のメッセージに同梱して JS に push し、レンダ時に「自分」バッジを表示させる。</summary>
    System.Collections.Generic.IReadOnlyCollection<int> OwnPostNumbers { get; }
}

/// <summary>WebView2 が「シェル HTML をどの経路で読み込んでいるか」のスコープ。
/// 添付プロパティが post する JSON メッセージは、対応するシェルのナビ完了を待ってから送る必要がある。
///
/// <list type="bullet">
/// <item><description><see cref="HtmlPane"/> — Html 添付プロパティ経由で NavigateToString された 3 ペイン用 WebView2 (お気に入り / 板一覧 / スレ一覧)</description></item>
/// <item><description><see cref="ThreadShell"/> — スレ表示用 (thread.html)。シェルがまだロードされていなければここで起動する</description></item>
/// <item><description><see cref="ViewerShell"/> — 画像ビューア用 (viewer.html)。シェルがまだロードされていなければここで起動する</description></item>
/// </list></summary>
internal enum NavScope
{
    HtmlPane,
    ThreadShell,
    ViewerShell,
}

/// <summary><see cref="WebView2"/> 用の添付プロパティ群と、共通的な「シェル HTML ロード + JSON post」ユーティリティ。
///
/// <para>このファイル (core) には以下を集約:
/// <list type="bullet">
/// <item><description>CoreWebView2Environment 事前 warmup (<see cref="StartWarmup"/>)</description></item>
/// <item><description>WebResourceRequested ハンドラ (画像ローカルキャッシュ + pixiv Referer)</description></item>
/// <item><description>シェル HTML 構築 (thread / viewer) + キャッシュ</description></item>
/// <item><description>共通ヘルパ (<see cref="PostJsonWhenReadyAsync"/>)</description></item>
/// <item><description><c>Html</c> 添付プロパティ (3 ペインの NavigateToString 経路)</description></item>
/// </list>
/// </para>
///
/// <para>その他の添付プロパティ (LogMarkUpdate / FavoritedUpdate / AppendBatch / ViewMode / 各種 ConfigJson /
/// ShortcutsJson / ImageUrl) は <c>WebView2Helper.AttachedProperties.cs</c> に分割している。</para></summary>
public static partial class WebView2Helper
{
    // ------------------------------------------------------------
    // CoreWebView2Environment 事前 warmup
    // ------------------------------------------------------------

    private static Task<CoreWebView2Environment>? _environmentTask;

    /// <summary>App 起動時に一度だけ呼ぶ。CoreWebView2Environment を裏で生成しておく。</summary>
    public static void StartWarmup()
    {
        _environmentTask ??= CoreWebView2Environment.CreateAsync();
    }

    /// <summary>warmup 済み environment を使って <see cref="WebView2.EnsureCoreWebView2Async"/> を呼ぶ。
    /// 失敗したら通常パスに fallback。完了後に <see cref="InstallWebResourceHandlers"/> を呼ぶ。
    /// PostDialog のプレビューペインなど、添付プロパティ経由を通らない WebView2 を初期化したいケースでも
    /// 同じ初期化 (warmup + 画像キャッシュ + pixiv Referer) を共有させるため internal 公開している。</summary>
    internal static async Task EnsureCoreAsync(WebView2 wv)
    {
        if (_environmentTask is not null)
        {
            CoreWebView2Environment env;
            try
            {
                env = await _environmentTask.ConfigureAwait(true);
            }
            catch
            {
                await wv.EnsureCoreWebView2Async().ConfigureAwait(true);
                InstallWebResourceHandlers(wv);
                return;
            }
            await wv.EnsureCoreWebView2Async(env).ConfigureAwait(true);
        }
        else
        {
            await wv.EnsureCoreWebView2Async().ConfigureAwait(true);
        }

        InstallWebResourceHandlers(wv);
    }

    // ------------------------------------------------------------
    // 共通ヘルパ: シェル準備が整ってから JSON を post する
    // ------------------------------------------------------------

    /// <summary>WebView2 のシェル準備 (= 該当する NavScope のナビ完了) を待ってから、
    /// 与えられた JSON メッセージを <see cref="CoreWebView2.PostWebMessageAsJson"/> で送る。
    ///
    /// FIFO 保証: 同一 WebView2 への複数同時送信を直列化する。
    /// 直列化なしの場合、3 つの async 呼び出しが同じ shell-nav Task を await すると、
    /// Task 完了後の継続再開順は .NET の TaskAwaiter 内部実装に依存し FIFO が保証されない。
    /// その結果「最初に発火した append が後着でレス順を破壊」する事象が観測されたため、
    /// 各 WebView2 ごとの「直前送信完了 Task」をチェーンして送信順を保証する。
    ///
    /// 例外は呼び出し元に伝搬させず Debug.WriteLine で握り潰す (= 添付プロパティハンドラが
    /// async void で動くため、UI スレッドへの伝搬を防ぐ意味でもここで吸収する)。</summary>
    private static async Task PostJsonWhenReadyAsync(WebView2 wv, string json, NavScope scope)
    {
        // 前回の send 完了を予約する: my=この送信の完了通知 TCS、prev=直前の send Task。
        // ロックは状態の読み取り/差し替えだけで、await 自体はロック外で行う。
        var state = SendQueues.GetValue(wv, _ => new SendQueueState());
        Task previous;
        var myCompletion = new TaskCompletionSource();
        lock (state.Lock)
        {
            previous = state.LastSend;
            state.LastSend = myCompletion.Task;
        }

        try
        {
            // 前の送信完了まで待機 (= FIFO 保証)。前のが失敗した場合もこちらは続行する。
            try { await previous.ConfigureAwait(true); } catch { /* 前の失敗を引き継がない */ }

            await EnsureCoreAsync(wv).ConfigureAwait(true);

            var waitTask = scope switch
            {
                NavScope.HtmlPane    => GetHtmlPaneNavTask(wv),
                NavScope.ThreadShell => GetOrStartThreadShellNav(wv),
                NavScope.ViewerShell => GetOrStartViewerShellNav(wv),
                _                    => Task.CompletedTask,
            };
            await waitTask.ConfigureAwait(true);

            if (wv.CoreWebView2 is null) return;
            wv.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebView2Helper] post failed (scope={scope}): {ex.Message}");
        }
        finally
        {
            // 例外を拾っても必ず完了通知する (= でないと後続が永久にブロックされる)。
            myCompletion.TrySetResult();
        }
    }

    private sealed class SendQueueState
    {
        public Task         LastSend = Task.CompletedTask;
        public readonly object Lock  = new();
    }
    private static readonly ConditionalWeakTable<WebView2, SendQueueState> SendQueues = new();

    private static Task GetHtmlPaneNavTask(WebView2 wv)
    {
        var s = HtmlNavStates.GetValue(wv, _ => new HtmlNavState());
        return s.CurrentNav?.Task ?? Task.CompletedTask;
    }

    private static Task GetOrStartThreadShellNav(WebView2 wv)
    {
        var s = ShellStates.GetValue(wv, _ => new ShellState());
        s.NavigationTask ??= NavigateToShellAsync(wv);
        return s.NavigationTask;
    }

    private static Task GetOrStartViewerShellNav(WebView2 wv)
    {
        var s = ViewerStates.GetValue(wv, _ => new ViewerState());
        s.NavigationTask ??= NavigateToViewerShellAsync(wv);
        return s.NavigationTask;
    }

    /// <summary>JSON シリアライザ設定。enum は camelCase に、プロパティ名も camelCase に。</summary>
    private static readonly JsonSerializerOptions PostJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters           = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // ------------------------------------------------------------
    // WebResourceRequested ハンドラ (シェル仮想ホスト + 画像ローカルキャッシュ)
    // ------------------------------------------------------------

    private static ImageCacheService? _imageCache;
    private static readonly ConditionalWeakTable<WebView2, object> _resourceHandlersInstalled = new();

    /// <summary>App.OnStartup で 1 度だけ呼ぶ。画像キャッシュ機能を有効化する。</summary>
    public static void RegisterImageCache(ImageCacheService cache) => _imageCache = cache;

    private static ThemeService? _themeService;

    /// <summary>App.OnStartup で 1 度だけ呼ぶ。テーマ機構を有効化する。LoadThreadShellHtml が
    /// post.html (テンプレ) と post.css をスレシェルに注入する。</summary>
    public static void RegisterThemeService(ThemeService theme) => _themeService = theme;

    private static void InstallWebResourceHandlers(WebView2 wv)
    {
        if (_imageCache is null) return; // 画像キャッシュ未登録なら何もしない
        if (_resourceHandlersInstalled.TryGetValue(wv, out _)) return;
        _resourceHandlersInstalled.Add(wv, new object());

        var core = wv.CoreWebView2;
        if (core is null) return;

        // <img> や CSS background-image などブラウザが画像として要求する全リソースを対象にする。
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Image);

        core.WebResourceRequested += (s, e) =>
        {
            var url = e.Request.Uri;

            // pixiv の i.pximg.net は Referer: https://www.pixiv.net/ が無いと 403 を返す。
            if (url.IndexOf(".pximg.net", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try { e.Request.Headers.SetHeader("Referer", "https://www.pixiv.net/"); }
                catch (Exception ex) { Debug.WriteLine($"[ImageCache] set referer failed: {ex.Message}"); }
            }

            var cache = _imageCache;
            if (cache is null) return;
            if (!cache.TryGet(url, out var hit)) return;

            try
            {
                // Stream は WebView2 が消費・dispose する。同じファイルを 2 回キャッシュヒットさせる場合に備え、
                // ファイル → メモリへ全読みしてから渡す (FileStream のままだと読み中の競合が起きうる)。
                var bytes  = File.ReadAllBytes(hit.FilePath);
                var ms     = new MemoryStream(bytes, writable: false);
                var headers = $"Content-Type: {hit.ContentType}";
                e.Response  = core.Environment.CreateWebResourceResponse(ms, 200, "OK", headers);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageCache] hit serve failed for {url}: {ex.Message}");
                // Response 未設定 → 通常通りネットワークへ流れる
            }
        };

        core.WebResourceResponseReceived += async (s, e) =>
        {
            var cache = _imageCache;
            if (cache is null) return;
            try
            {
                if (e.Response is null) return;
                if (e.Response.StatusCode != 200) return;
                if (!string.Equals(e.Request.Method, "GET", StringComparison.OrdinalIgnoreCase)) return;

                var contentType = e.Response.Headers.GetHeader("content-type");
                if (string.IsNullOrEmpty(contentType)) return;
                if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return;

                var url = e.Request.Uri;
                if (cache.Contains(url)) return; // 既にキャッシュ済み

                Stream? stream;
                try { stream = await e.Response.GetContentAsync(); }
                catch (Exception ex) { Debug.WriteLine($"[ImageCache] GetContent failed: {ex.Message}"); return; }
                if (stream is null) return;

                await cache.SaveAsync(url, stream, contentType).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ImageCache] capture failed: {ex.Message}");
            }
        };
    }

    // ------------------------------------------------------------
    // ナビゲーション状態 (WebView2 1 台ごとに保持)
    // ------------------------------------------------------------

    private static readonly ConditionalWeakTable<WebView2, HtmlNavState>   HtmlNavStates  = new();
    private static readonly ConditionalWeakTable<WebView2, ShellState>     ShellStates    = new();
    private static readonly ConditionalWeakTable<WebView2, ViewerState>    ViewerStates   = new();

    private sealed class HtmlNavState { public TaskCompletionSource? CurrentNav; }
    private sealed class ShellState   { public Task? NavigationTask; }
    private sealed class ViewerState  { public Task? NavigationTask; }

    // ------------------------------------------------------------
    // Html 添付プロパティ (3 ペインのサーバ生成 HTML を NavigateToString)
    // ------------------------------------------------------------

    public static readonly DependencyProperty HtmlProperty =
        DependencyProperty.RegisterAttached(
            "Html",
            typeof(string),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnHtmlChanged));

    public static string? GetHtml(DependencyObject d) => (string?)d.GetValue(HtmlProperty);
    public static void   SetHtml(DependencyObject d, string? value) => d.SetValue(HtmlProperty, value);

    private static async void OnHtmlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 wv) return;

        // 空 / null は no-op。
        // ThreadListTabViewModel._html の初期値は "" で、TabControl が ContentTemplate を materialize した
        // 直後に「空文字 → 本物の HTML」の 2 回 OnHtmlChanged が走る経路があった。1 つ目の WebView2 は
        // 初期化コストが大きく、2 つの NavigateToString が近接して走るとサーバ応答とコールドスタートの
        // 兼ね合いで「空ナビが後勝ち → 真っ白のまま固まる」事象が発生していたため、
        // 空 Html では一切 NavigateToString を呼ばないことにする。
        var html = e.NewValue as string;
        if (string.IsNullOrEmpty(html)) return;

        try
        {
            // WebView2 が visual tree に入る前に EnsureCoreWebView2Async を呼ぶと「初期化は完了するが
            // 表示されない」ことがあるため Loaded を待つ。
            if (!wv.IsLoaded)
            {
                var loadedTcs = new TaskCompletionSource();
                void OnLoaded(object? _, RoutedEventArgs __)
                {
                    wv.Loaded -= OnLoaded;
                    loadedTcs.TrySetResult();
                }
                wv.Loaded += OnLoaded;
                await loadedTcs.Task.ConfigureAwait(true);
            }

            await EnsureCoreAsync(wv).ConfigureAwait(true);

            var state = HtmlNavStates.GetValue(wv, _ => new HtmlNavState());

            // 直前のナビが進行中なら完了を待つ (= 連続ナビの直列化)。
            // NavigateToString を続けて呼ぶと WebView2 内部で前のものをキャンセルするが、
            // NavigationCompleted ハンドラの登録/解除がレースして CurrentNav が誤って resolve される
            // 可能性があるため、ここで明示的に並べ直す。
            if (state.CurrentNav is { } prev && !prev.Task.IsCompleted)
            {
                try { await prev.Task.ConfigureAwait(true); }
                catch { /* 前のナビが失敗しても次は走らせる */ }
            }

            var tcs = new TaskCompletionSource();
            state.CurrentNav = tcs;

            void Handler(object? s, CoreWebView2NavigationCompletedEventArgs args)
            {
                wv.CoreWebView2.NavigationCompleted -= Handler;
                tcs.TrySetResult();
            }
            wv.CoreWebView2.NavigationCompleted += Handler;
            wv.CoreWebView2.NavigateToString(html);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebView2Helper] NavigateToString failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------
    // シェル HTML ロード (スレ表示 / ビューア)
    // ------------------------------------------------------------

    private static async Task NavigateToShellAsync(WebView2 wv)
    {
        var tcs = new TaskCompletionSource();
        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs args)
        {
            wv.CoreWebView2.NavigationCompleted -= Handler;
            tcs.TrySetResult();
        }
        wv.CoreWebView2.NavigationCompleted += Handler;
        wv.CoreWebView2.NavigateToString(LoadThreadShellHtml());
        await tcs.Task.ConfigureAwait(true);
    }

    private static async Task NavigateToViewerShellAsync(WebView2 wv)
    {
        var tcs = new TaskCompletionSource();
        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs args)
        {
            wv.CoreWebView2.NavigationCompleted -= Handler;
            tcs.TrySetResult();
        }
        wv.CoreWebView2.NavigationCompleted += Handler;
        wv.CoreWebView2.NavigateToString(LoadViewerShellHtml());
        await tcs.Task.ConfigureAwait(true);
    }

    // ------------------------------------------------------------
    // 埋め込みリソース読み込み (thread.html / viewer.html を CSS/JS と結合)
    // ------------------------------------------------------------

    private static string? _shellHtmlCache;
    private static string? _viewerShellHtmlCache;
    private static string? _viewerDetailsShellHtmlCache;
    private static readonly object ShellLock              = new();
    private static readonly object ViewerShellLock        = new();
    private static readonly object ViewerDetailsShellLock = new();

    /// <summary>スレ表示シェル / ビューアシェルの static HTML キャッシュを両方破棄する (Phase 11d)。
    /// 設定画面の「すべての CSS を再読み込み」から呼ばれる。
    /// 既に開いている WebView2 は再ナビゲートしないが、次に新規生成される (= タブを開き直した) WebView2
    /// は新しい CSS を読み込んだシェル HTML を使う。</summary>
    public static void InvalidateShellCaches()
    {
        lock (ShellLock)              _shellHtmlCache              = null;
        lock (ViewerShellLock)        _viewerShellHtmlCache        = null;
        lock (ViewerDetailsShellLock) _viewerDetailsShellHtmlCache = null;
    }

    /// <summary>スレ表示シェル HTML をロード (CSS + thread.js + post.html テンプレ + post.css を統合)。
    /// 通常はこのクラス内部 (NavigateToShellAsync) からだけ使うが、書き込みダイアログのプレビューペイン
    /// (PostDialog) からも同じシェルを流用するため internal 公開する。</summary>
    internal static string LoadThreadShellHtml()
    {
        if (_shellHtmlCache is not null) return _shellHtmlCache;
        lock (ShellLock)
        {
            if (_shellHtmlCache is not null) return _shellHtmlCache;
            var html   = ChBrowser.Services.Render.EmbeddedAssets.Read("thread.html");
            // thread.css / post.css はディスク優先 (= ユーザがテーマ編集した内容を反映)。
            var css    = ChBrowser.Services.Render.EmbeddedAssets.ReadCss("thread.css");
            var js     = ChBrowser.Services.Render.EmbeddedAssets.Read("thread.js");
            var bridge = ChBrowser.Services.Render.EmbeddedAssets.Read("shortcut-bridge.js");

            // テーマ (post.html テンプレ + post.css) を注入。テーマ未登録時は埋め込み既定を使う。
            var theme        = _themeService?.LoadActiveTheme();
            var postTemplate = theme?.PostHtmlTemplate ?? ChBrowser.Services.Render.EmbeddedAssets.Read("post.html");
            var postCss      = theme?.PostCss          ?? ChBrowser.Services.Render.EmbeddedAssets.Read("post.css");

            _shellHtmlCache = html
                .Replace("/*{{CSS}}*/",                css + "\n" + postCss)
                .Replace("/*{{SHORTCUT_BRIDGE}}*/",    bridge)
                .Replace("/*{{JS}}*/",                 js)
                .Replace("<!--{{POST_TEMPLATE}}-->",   postTemplate);
            return _shellHtmlCache;
        }
    }

    private static string LoadViewerShellHtml()
    {
        if (_viewerShellHtmlCache is not null) return _viewerShellHtmlCache;
        lock (ViewerShellLock)
        {
            if (_viewerShellHtmlCache is not null) return _viewerShellHtmlCache;
            var html   = ChBrowser.Services.Render.EmbeddedAssets.Read("viewer.html");
            var css    = ChBrowser.Services.Render.EmbeddedAssets.Read("viewer.css");
            var js     = ChBrowser.Services.Render.EmbeddedAssets.Read("viewer.js");
            var bridge = ChBrowser.Services.Render.EmbeddedAssets.Read("shortcut-bridge.js");
            _viewerShellHtmlCache = html
                .Replace("/*{{CSS}}*/",             css)
                .Replace("/*{{SHORTCUT_BRIDGE}}*/", bridge)
                .Replace("/*{{JS}}*/",              js);
            return _viewerShellHtmlCache;
        }
    }

    /// <summary>画像ビューアの「画像詳細ペイン」用シェル HTML を返す。
    /// ImageViewerWindow が WebView2 に NavigateToString する。設定画面と同じく一度だけビルドしてキャッシュ。</summary>
    internal static string LoadViewerDetailsShellHtml()
    {
        if (_viewerDetailsShellHtmlCache is not null) return _viewerDetailsShellHtmlCache;
        lock (ViewerDetailsShellLock)
        {
            if (_viewerDetailsShellHtmlCache is not null) return _viewerDetailsShellHtmlCache;
            var html = ChBrowser.Services.Render.EmbeddedAssets.Read("viewer-details.html");
            var css  = ChBrowser.Services.Render.EmbeddedAssets.Read("viewer-details.css");
            var js   = ChBrowser.Services.Render.EmbeddedAssets.Read("viewer-details.js");
            _viewerDetailsShellHtmlCache = html
                .Replace("/*{{CSS}}*/", css)
                .Replace("/*{{JS}}*/",  js);
            return _viewerDetailsShellHtmlCache;
        }
    }
}
