using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
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

/// <summary>
/// スレ表示タブ ViewModel が公開する、JS 側に渡すスクロール対象。
/// WebView2Helper が DataContext からこの値を読んで setPosts / appendPosts メッセージに含める。
/// </summary>
public interface IThreadDisplayBinding
{
    int? ScrollTargetPostNumber { get; }

    /// <summary>「ここまで読んだ」帯の対象レス番号 (Phase 19)。null なら帯を表示しない。
    /// appendPosts のメッセージに同梱して JS に push する。</summary>
    int? ReadMarkPostNumber { get; }
}

/// <summary>
/// <see cref="WebView2"/> に対する 2 種類の Bind を提供する添付プロパティ。
///
/// <para>
/// <see cref="HtmlProperty"/> はサーバ生成済みの HTML 文字列を <c>NavigateToString</c> でロードする
/// レガシー経路 (スレ一覧で使用)。
/// </para>
///
/// <para>
/// <see cref="PostsProperty"/> はスレ表示用の新経路。最初に Resources の thread.html (CSS/JS 同梱)
/// をロードしておき、その後の Posts 変更は <see cref="CoreWebView2.PostWebMessageAsJson(string)"/> で
/// JS 側の message リスナに送る (fire-and-forget IPC、ExecuteScriptAsync より低オーバーヘッド)。
/// </para>
///
/// <para>
/// <see cref="StartWarmup"/> は <see cref="CoreWebView2Environment.CreateAsync"/> をバックグラウンドで先行
/// 起動し、初回 WebView2 のコールドスタートを潰す目的で App.OnStartup から呼ぶ。
/// </para>
/// </summary>
public static class WebView2Helper
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

    /// <summary>warmup 済み environment を使って <see cref="WebView2.EnsureCoreWebView2Async"/> を呼ぶ。</summary>
    private static async Task EnsureCoreAsync(WebView2 wv)
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
                // warmup が失敗していたら通常パスに fallback
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
    // WebResourceRequested ハンドラ (シェル仮想ホスト + 画像ローカルキャッシュ)
    // ------------------------------------------------------------
    //
    // 「シェル仮想ホスト」: スレ表示シェル HTML を NavigateToString ではなく
    // Navigate("https://chbrowser.local/thread.html") でロードし、その URL の
    // WebResourceRequested を捕捉して埋め込み HTML をその場で返す。
    //   - NavigateToString で生成されるページの origin は null (data: URL) になり、
    //     YouTube embed の origin チェックで Error 153 になる。仮想ホスト経由なら
    //     ページに本物の https origin が付与され、embed が動作する。
    //   - 仮想ホスト名は ChBrowser 内でしか解決されないので、外部に漏れる心配はない。

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

        // ---- 画像コンテキストの WebResourceRequested 介在 ----
        // <img> や CSS background-image などブラウザが画像として要求する全リソースを対象にする。
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Image);

        core.WebResourceRequested += (s, e) =>
        {
            var url = e.Request.Uri;

            // pixiv の i.pximg.net は Referer: https://www.pixiv.net/ が無いと 403 を返す。
            // 画像コンテキストでこの host を踏むのは UrlExpander が pixiv を解決した時のみ。
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
                var bytes = File.ReadAllBytes(hit.FilePath);
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
                if (cache.Contains(url)) return; // 既にキャッシュ済み (ヒットを serve した後の追跡発火など)

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
    // Html (legacy: サーバ生成 HTML を NavigateToString)
    // ------------------------------------------------------------

    public static readonly DependencyProperty HtmlProperty =
        DependencyProperty.RegisterAttached(
            "Html",
            typeof(string),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnHtmlChanged));

    public static string? GetHtml(DependencyObject d) => (string?)d.GetValue(HtmlProperty);
    public static void   SetHtml(DependencyObject d, string? value) => d.SetValue(HtmlProperty, value);

    private static readonly ConditionalWeakTable<WebView2, HtmlNavState> HtmlNavStates = new();

    private sealed class HtmlNavState
    {
        public TaskCompletionSource? CurrentNav;
    }

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
            // 表示されない」ことがあるため Loaded を待つ。タブ初回 materialize 直後は OnHtmlChanged が
            // Loaded の前に走るケースがある。
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
    // LogMarkUpdate (スレ一覧: has-log クラスを postMessage で toggle)
    // ------------------------------------------------------------

    public static readonly DependencyProperty LogMarkUpdateProperty =
        DependencyProperty.RegisterAttached(
            "LogMarkUpdate",
            typeof(object),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnLogMarkUpdateChanged));

    public static object? GetLogMarkUpdate(DependencyObject d) => d.GetValue(LogMarkUpdateProperty);
    public static void    SetLogMarkUpdate(DependencyObject d, object? value) => d.SetValue(LogMarkUpdateProperty, value);

    private static async void OnLogMarkUpdateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 wv) return;
        if (e.NewValue is null) return;

        try
        {
            await EnsureCoreAsync(wv).ConfigureAwait(true);

            // Html 経由のナビゲーションが完了するまで待つ (PostMessage 配送のため)
            var state = HtmlNavStates.GetValue(wv, _ => new HtmlNavState());
            if (state.CurrentNav is { } nav) await nav.Task.ConfigureAwait(true);

            var json = JsonSerializer.Serialize(
                new { type = "updateLogMarks", value = e.NewValue },
                PostJsonOptions);
            wv.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebView2Helper] LogMarkUpdate push failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------
    // FavoritedUpdate (スレ一覧: is-favorited クラスを postMessage で toggle)
    // ------------------------------------------------------------

    public static readonly DependencyProperty FavoritedUpdateProperty =
        DependencyProperty.RegisterAttached(
            "FavoritedUpdate",
            typeof(object),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnFavoritedUpdateChanged));

    public static object? GetFavoritedUpdate(DependencyObject d) => d.GetValue(FavoritedUpdateProperty);
    public static void    SetFavoritedUpdate(DependencyObject d, object? value) => d.SetValue(FavoritedUpdateProperty, value);

    private static async void OnFavoritedUpdateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 wv) return;
        if (e.NewValue is null) return;

        try
        {
            await EnsureCoreAsync(wv).ConfigureAwait(true);
            var state = HtmlNavStates.GetValue(wv, _ => new HtmlNavState());
            if (state.CurrentNav is { } nav) await nav.Task.ConfigureAwait(true);

            var json = JsonSerializer.Serialize(
                new { type = "updateFavorited", value = e.NewValue },
                PostJsonOptions);
            wv.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebView2Helper] FavoritedUpdate push failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------
    // AppendBatch (スレ表示: スレ表示の唯一の描画チャネル — appendPosts として JS に post)
    //
    // 旧実装には Posts (= setPosts) の全置換チャネルもあったが、
    //   - JS 側は最初から allPosts=[] / DOM=[] で立ち上がるので「明示的に空状態にする」必要がない
    //   - 全置換チャネル ＋ 増分チャネルが並走すると、初期 binding 発火と AppendPosts 呼び出しが
    //     async 連続のスケジュール次第で逆順になり「先にレンダ → setPosts([]) で消去」の真っ白現象が起きていた
    // ため、appendPosts の単一チャネルに絞って構造的に競合をなくしている。
    // ------------------------------------------------------------

    private static readonly ConditionalWeakTable<WebView2, ShellState> ShellStates = new();

    /// <summary>WebView2 1 台ごとのシェル HTML ナビゲーション状態。</summary>
    private sealed class ShellState
    {
        public Task? NavigationTask;
    }

    private static readonly JsonSerializerOptions PostJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters           = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static readonly DependencyProperty AppendBatchProperty =
        DependencyProperty.RegisterAttached(
            "AppendBatch",
            typeof(object),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnAppendBatchChanged));

    public static object? GetAppendBatch(DependencyObject d) => d.GetValue(AppendBatchProperty);
    public static void    SetAppendBatch(DependencyObject d, object? value) => d.SetValue(AppendBatchProperty, value);

    private static async void OnAppendBatchChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 wv) return;
        if (e.NewValue is null) return; // null = 増分なし (Reset 時など)

        var state = ShellStates.GetValue(wv, _ => new ShellState());

        try
        {
            await EnsureCoreAsync(wv).ConfigureAwait(true);
            state.NavigationTask ??= NavigateToShellAsync(wv);
            await state.NavigationTask.ConfigureAwait(true);

            // streaming 中もこのバッチで対象レスが現れる可能性があるので scroll target を併送
            var binding      = wv.DataContext as IThreadDisplayBinding;
            var scrollTarget = binding?.ScrollTargetPostNumber;
            var readMark     = binding?.ReadMarkPostNumber;
            // e.NewValue は AppendBatchData (Phase 20)。Posts と IsIncremental を分けて送る。
            object posts;
            bool incremental;
            if (e.NewValue is ChBrowser.ViewModels.AppendBatchData data)
            {
                posts       = data.Posts;
                incremental = data.IsIncremental;
            }
            else
            {
                // 旧型 (IReadOnlyList<Post>) フォールバック (= 互換性のため残すが現状到達経路なし)
                posts       = e.NewValue;
                incremental = false;
            }
            var json = JsonSerializer.Serialize(
                new { type = "appendPosts", posts, scrollTarget, readMarkPostNumber = readMark, incremental },
                PostJsonOptions);
            wv.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebView2Helper] AppendBatch push failed: {ex.Message}");
        }
    }

    // ScrollTarget は独立した attached property を持たない。
    // タブ ViewModel が IThreadDisplayBinding.ScrollTargetPostNumber を露出し、
    // OnPostsChanged / OnAppendBatchChanged が DataContext から都度読んで setPosts/appendPosts に同梱する。
    // これによりエコー戻しの jolt や DataContext 変化との競合がなくなる。

    // ------------------------------------------------------------
    // ViewMode (スレ表示: flat / tree / dedupTree の表示モードを JS に通知)
    // ------------------------------------------------------------

    public static readonly DependencyProperty ViewModeProperty =
        DependencyProperty.RegisterAttached(
            "ViewMode",
            typeof(object),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnViewModeChanged));

    public static object? GetViewMode(DependencyObject d) => d.GetValue(ViewModeProperty);
    public static void    SetViewMode(DependencyObject d, object? value) => d.SetValue(ViewModeProperty, value);

    private static async void OnViewModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 wv) return;
        if (e.NewValue is null) return;

        var state = ShellStates.GetValue(wv, _ => new ShellState());
        try
        {
            await EnsureCoreAsync(wv).ConfigureAwait(true);
            state.NavigationTask ??= NavigateToShellAsync(wv);
            await state.NavigationTask.ConfigureAwait(true);

            // enum は JsonStringEnumConverter(camelCase) で "flat" / "tree" / "dedupTree" に変換される
            var json = JsonSerializer.Serialize(
                new { type = "setViewMode", mode = e.NewValue },
                PostJsonOptions);
            wv.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebView2Helper] ViewMode push failed: {ex.Message}");
        }
    }


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

    // ------------------------------------------------------------
    // ThreadConfigJson (Phase 11: アプリ設定の即時反映)
    //
    // MainViewModel が AppConfig 変更時に「JS に渡す部分だけを抜き出した JSON 文字列」を露出する。
    // 各スレ表示 WebView2 はこの添付プロパティに bind し、変更があれば setConfig メッセージで JS へ push。
    // ------------------------------------------------------------

    public static readonly DependencyProperty ThreadConfigJsonProperty =
        DependencyProperty.RegisterAttached(
            "ThreadConfigJson",
            typeof(string),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnThreadConfigJsonChanged));

    public static string? GetThreadConfigJson(DependencyObject d) => (string?)d.GetValue(ThreadConfigJsonProperty);
    public static void    SetThreadConfigJson(DependencyObject d, string? value) => d.SetValue(ThreadConfigJsonProperty, value);

    private static async void OnThreadConfigJsonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 wv) return;
        if (e.NewValue is not string json || string.IsNullOrEmpty(json)) return;

        var state = ShellStates.GetValue(wv, _ => new ShellState());
        try
        {
            await EnsureCoreAsync(wv).ConfigureAwait(true);
            // スレ表示シェルの読み込みを待つ (PostsAppended と同じ前提)
            state.NavigationTask ??= NavigateToShellAsync(wv);
            await state.NavigationTask.ConfigureAwait(true);
            // json 自体が "{type:'setConfig', ...}" の形で送られてくる前提
            wv.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebView2Helper] ThreadConfigJson push failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------
    // ThreadShortcutsJson (Phase 16: スレ表示 WebView 内のショートカット/マウス操作/ジェスチャー対応)
    //
    // ShortcutManager.OnBindingsApplied 経由で MainViewModel.ThreadShortcutsJson が更新されると、
    // 各スレ表示 WebView2 に setShortcutBindings メッセージとして push される。
    // JS 側ブリッジは bindings リストを受け取り、合致する入力を preventDefault + 通知する。
    // ------------------------------------------------------------

    public static readonly DependencyProperty ThreadShortcutsJsonProperty =
        DependencyProperty.RegisterAttached(
            "ThreadShortcutsJson",
            typeof(string),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnThreadShortcutsJsonChanged));

    public static string? GetThreadShortcutsJson(DependencyObject d) => (string?)d.GetValue(ThreadShortcutsJsonProperty);
    public static void    SetThreadShortcutsJson(DependencyObject d, string? value) => d.SetValue(ThreadShortcutsJsonProperty, value);

    private static async void OnThreadShortcutsJsonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 wv) return;
        if (e.NewValue is not string json || string.IsNullOrEmpty(json)) return;

        var state = ShellStates.GetValue(wv, _ => new ShellState());
        try
        {
            await EnsureCoreAsync(wv).ConfigureAwait(true);
            state.NavigationTask ??= NavigateToShellAsync(wv);
            await state.NavigationTask.ConfigureAwait(true);
            wv.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebView2Helper] ThreadShortcutsJson push failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------
    // PaneShortcutsJson (Phase 16: スレ一覧 / お気に入り / 板一覧 ペイン用のショートカット bind 一覧 push)
    //
    // ThreadShortcutsJson のペイン版。Html 添付プロパティで NavStates 完了済みの WebView2 へ setShortcutBindings を送る。
    // ------------------------------------------------------------

    public static readonly DependencyProperty PaneShortcutsJsonProperty =
        DependencyProperty.RegisterAttached(
            "PaneShortcutsJson",
            typeof(string),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnPaneShortcutsJsonChanged));

    public static string? GetPaneShortcutsJson(DependencyObject d) => (string?)d.GetValue(PaneShortcutsJsonProperty);
    public static void    SetPaneShortcutsJson(DependencyObject d, string? value) => d.SetValue(PaneShortcutsJsonProperty, value);

    private static async void OnPaneShortcutsJsonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 wv) return;
        if (e.NewValue is not string json || string.IsNullOrEmpty(json)) return;

        try
        {
            await EnsureCoreAsync(wv).ConfigureAwait(true);
            var state = HtmlNavStates.GetValue(wv, _ => new HtmlNavState());
            if (state.CurrentNav is { } nav) await nav.Task.ConfigureAwait(true);
            wv.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebView2Helper] PaneShortcutsJson push failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------
    // PaneConfigJson (Phase 11b: お気に入り / 板一覧 / スレッド一覧 ペイン共通)
    //
    // 3 ペイン用の WebView2 (Html 添付プロパティで navigated 済みのもの) に setConfig メッセージを送る。
    // スレ表示用 ThreadConfigJson とは別系統 (= シェル HTML が異なるため、ナビ完了管理も別)。
    // 各ペインの JS は受信した setConfig をそれぞれ独自に解釈する (openOnSingleClick 等)。
    // ------------------------------------------------------------

    public static readonly DependencyProperty PaneConfigJsonProperty =
        DependencyProperty.RegisterAttached(
            "PaneConfigJson",
            typeof(string),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnPaneConfigJsonChanged));

    public static string? GetPaneConfigJson(DependencyObject d) => (string?)d.GetValue(PaneConfigJsonProperty);
    public static void    SetPaneConfigJson(DependencyObject d, string? value) => d.SetValue(PaneConfigJsonProperty, value);

    private static async void OnPaneConfigJsonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 wv) return;
        if (e.NewValue is not string json || string.IsNullOrEmpty(json)) return;

        try
        {
            await EnsureCoreAsync(wv).ConfigureAwait(true);
            // ペイン用 WebView2 は Html 添付プロパティ経由でナビ済みのはず。
            // HtmlNavStates の CurrentNav が完了するまで待ってから push する。
            var state = HtmlNavStates.GetValue(wv, _ => new HtmlNavState());
            if (state.CurrentNav is { } nav) await nav.Task.ConfigureAwait(true);

            wv.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebView2Helper] PaneConfigJson push failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------
    // ImageUrl (Phase 10: ビューアウィンドウの 1 タブ用)
    //
    // 添付プロパティが変更されたら、初回はビューアシェル HTML をロード、
    // 以降は { type:'setImage', url } メッセージを JS に送って画像を切替。
    // ------------------------------------------------------------

    private static readonly ConditionalWeakTable<WebView2, ViewerState> ViewerStates = new();
    private sealed class ViewerState
    {
        public Task? NavigationTask;
    }

    public static readonly DependencyProperty ImageUrlProperty =
        DependencyProperty.RegisterAttached(
            "ImageUrl",
            typeof(string),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnImageUrlChanged));

    public static string? GetImageUrl(DependencyObject d) => (string?)d.GetValue(ImageUrlProperty);
    public static void    SetImageUrl(DependencyObject d, string? value) => d.SetValue(ImageUrlProperty, value);

    private static async void OnImageUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 wv) return;
        var url = e.NewValue as string;

        var state = ViewerStates.GetValue(wv, _ => new ViewerState());
        try
        {
            await EnsureCoreAsync(wv).ConfigureAwait(true);
            state.NavigationTask ??= NavigateToViewerShellAsync(wv);
            await state.NavigationTask.ConfigureAwait(true);

            var json = JsonSerializer.Serialize(
                new { type = "setImage", url = url ?? "" },
                PostJsonOptions);
            wv.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebView2Helper] ImageUrl push failed: {ex.Message}");
        }
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

    private static string? _viewerShellHtmlCache;
    private static readonly object ViewerShellLock = new();

    private static string LoadViewerShellHtml()
    {
        if (_viewerShellHtmlCache is not null) return _viewerShellHtmlCache;
        lock (ViewerShellLock)
        {
            if (_viewerShellHtmlCache is not null) return _viewerShellHtmlCache;
            var asm    = typeof(WebView2Helper).Assembly;
            var html   = ReadEmbeddedText(asm, "ChBrowser.Resources.viewer.html");
            var css    = ReadEmbeddedText(asm, "ChBrowser.Resources.viewer.css");
            var js     = ReadEmbeddedText(asm, "ChBrowser.Resources.viewer.js");
            var bridge = ReadEmbeddedText(asm, "ChBrowser.Resources.shortcut-bridge.js");
            _viewerShellHtmlCache = html
                .Replace("/*{{CSS}}*/",             css)
                .Replace("/*{{SHORTCUT_BRIDGE}}*/", bridge)
                .Replace("/*{{JS}}*/",              js);
            return _viewerShellHtmlCache;
        }
    }

    // ------------------------------------------------------------
    // 埋め込みリソース読み込み (thread.html + CSS + JS を結合)
    // ------------------------------------------------------------

    private static string? _shellHtmlCache;
    private static readonly object ShellLock = new();

    /// <summary>スレ表示シェル / ビューアシェルの static HTML キャッシュを両方破棄する (Phase 11d)。
    /// 設定画面の「すべての CSS を再読み込み」から呼ばれる。
    /// 既に開いている WebView2 は再ナビゲートしないが、次に新規生成される (= タブを開き直した) WebView2
    /// は新しい CSS を読み込んだシェル HTML を使う。</summary>
    public static void InvalidateShellCaches()
    {
        lock (ShellLock)       _shellHtmlCache       = null;
        lock (ViewerShellLock) _viewerShellHtmlCache = null;
    }

    private static string LoadThreadShellHtml()
    {
        if (_shellHtmlCache is not null) return _shellHtmlCache;
        lock (ShellLock)
        {
            if (_shellHtmlCache is not null) return _shellHtmlCache;
            var asm    = typeof(WebView2Helper).Assembly;
            var html   = ReadEmbeddedText(asm, "ChBrowser.Resources.thread.html");
            var css    = ReadEmbeddedText(asm, "ChBrowser.Resources.thread.css");
            var js     = ReadEmbeddedText(asm, "ChBrowser.Resources.thread.js");
            var bridge = ReadEmbeddedText(asm, "ChBrowser.Resources.shortcut-bridge.js");

            // テーマ (post.html テンプレ + post.css) を注入。テーマ未登録時は埋め込み既定を使う。
            var theme        = _themeService?.LoadActiveTheme();
            var postTemplate = theme?.PostHtmlTemplate ?? ReadEmbeddedText(asm, "ChBrowser.Resources.post.html");
            var postCss      = theme?.PostCss          ?? ReadEmbeddedText(asm, "ChBrowser.Resources.post.css");

            _shellHtmlCache = html
                .Replace("/*{{CSS}}*/",                css + "\n" + postCss)
                .Replace("/*{{SHORTCUT_BRIDGE}}*/",    bridge)
                .Replace("/*{{JS}}*/",                 js)
                .Replace("<!--{{POST_TEMPLATE}}-->",   postTemplate);
            return _shellHtmlCache;
        }
    }

    private static string ReadEmbeddedText(Assembly asm, string resourceName)
    {
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
