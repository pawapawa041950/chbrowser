using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
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

    /// <summary>CORS proxy 用の HttpClient (Phase 3)。
    /// 5ch 共有先 (tadaup.jp 等) は Monazilla UA を弾く / 特殊ヘッダを期待することがあるため、
    /// 5ch 本体用の <see cref="MonazillaClient.Http"/> ではなくブラウザ UA の専用 HttpClient を使う。
    /// 初回利用時に lazy 生成。</summary>
    private static HttpClient? _corsProxyHttp;
    private static readonly object _corsProxyHttpLock = new();
    private static HttpClient GetCorsProxyHttpClient()
    {
        if (_corsProxyHttp is not null) return _corsProxyHttp;
        lock (_corsProxyHttpLock)
        {
            if (_corsProxyHttp is not null) return _corsProxyHttp;
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect        = true,
                MaxAutomaticRedirections = 5,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            };
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36 ChBrowser");
            http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ja,en;q=0.8");
            _corsProxyHttp = http;
            return _corsProxyHttp;
        }
    }

    /// <summary>(現状未使用) 外部から HttpClient を差し替えるための拡張点。
    /// 標準では <see cref="GetCorsProxyHttpClient"/> のブラウザ UA HttpClient を使う。</summary>
    public static void RegisterHttpClient(HttpClient http)
    {
        lock (_corsProxyHttpLock) { _corsProxyHttp = http; }
    }

    private static void InstallWebResourceHandlers(WebView2 wv)
    {
        if (_imageCache is null) return; // 画像キャッシュ未登録なら何もしない
        if (_resourceHandlersInstalled.TryGetValue(wv, out _)) return;
        _resourceHandlersInstalled.Add(wv, new object());

        var core = wv.CoreWebView2;
        if (core is null) return;

        // Phase 2: キャッシュフォルダを HTTPS 仮想ホスト化する (画像 / 動画サムネ / 動画本体すべて共通)。
        // https://chbrowser-cache.local/images/aa/<hash>.jpg
        // https://chbrowser-cache.local/videos/aa/<hash>.mp4
        // のような URL を <img>/<video> の src に直接渡せば、WebView2 がローカルファイルを配信する。
        // Phase 5+ のキャッシュ済動画再生で利用する (動画は範囲リクエストも仮想ホスト側で適切に処理される)。
        try
        {
            core.SetVirtualHostNameToFolderMapping(
                ImageCacheService.VirtualHostName,
                _imageCache.CacheRootDir,
                CoreWebView2HostResourceAccessKind.Allow);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VirtualHost] mapping setup failed: {ex.Message}");
        }

        // <img> や CSS background-image などブラウザが画像として要求する全リソースを対象にする。
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Image);
        // <video> / <audio> 要素のメディアリクエストも対象 (Phase 3 で CORS proxy が必要)。
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Media);

        core.WebResourceRequested += (s, e) =>
        {
            var url = e.Request.Uri;
            var ctx = e.ResourceContext;

            // ---- Media (= <video> / <audio>) リクエスト処理 ----
            if (ctx == CoreWebView2WebResourceContext.Media)
            {
                // crossOrigin='anonymous' な <video> からのリクエストは Origin ヘッダが付く。
                // この場合だけ C# 側で取得して Access-Control-Allow-Origin: * を付けて返す
                // (= CORS proxy)。Origin が無い通常の <video> は WebView2 にそのまま処理させる。
                //
                // GetHeader は対象が無いと COMException ERROR_NOT_FOUND を投げる SDK 仕様なので
                // TryGetHeaderSafe で握り潰す (= ストリーミング動画の range request 毎の first-chance 例外抑制)。
                var origin = TryGetHeaderSafe(e, "Origin");
                if (!string.IsNullOrEmpty(origin))
                {
                    ChBrowser.Services.Logging.LogService.Instance.Write($"[CorsProxy] intercept url={url} origin={origin}");
                    var deferral = e.GetDeferral();
                    _ = ProxyCorsMediaAsync(core, e, url, deferral);
                }
                return; // CORS 不要なメディアリクエストは素通り
            }

            // ---- Image リクエスト処理 (既存ロジック) ----

            // pixiv の i.pximg.net は Referer: https://www.pixiv.net/ が無いと 403 を返す。
            if (url.IndexOf(".pximg.net", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try { e.Request.Headers.SetHeader("Referer", "https://www.pixiv.net/"); }
                catch (Exception ex) { Debug.WriteLine($"[ImageCache] set referer failed: {ex.Message}"); }
            }

            var cache = _imageCache;
            if (cache is null) return;
            if (!cache.TryGet(url, out var hit)) return;

            // GetDeferral で応答を deferred 化 → ハンドラはここで即時 return し WebView2 dispatcher を解放。
            // FileStream を別スレッドで開き (= ファイル全読みはせず lazy 読み)、
            // CreateWebResourceResponse + Response 設定 + Complete を完了通知に集約する。
            // 旧実装の File.ReadAllBytes (= 巨大画像で数十 ms 同期ブロック + 全画像分のメモリ確保)
            // が解消される。FileShare.Read で同じファイルを 2 経路から並列読みされても安全。
            var deferralImg = e.GetDeferral();
            _ = ServeFromCacheAsync(core, e, hit, url, deferralImg);
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

    /// <summary>crossOrigin='anonymous' な <c>&lt;video&gt;</c> からのリクエスト (= Origin ヘッダ付き) を
    /// C# HttpClient で取り直し、<c>Access-Control-Allow-Origin: *</c> を付けて返す。
    /// これにより canvas tainted を回避してサムネ抽出 (Phase 3) が可能になる。
    ///
    /// <para>UA はブラウザ風 (<see cref="GetCorsProxyHttpClient"/>) を使う
    /// (= Monazilla UA を弾く 5ch 共有先 (tadaup.jp 等) に備える)。</para>
    ///
    /// <para>レスポンスはストリーミングで WebView2 に渡し、ストリームの dispose で
    /// HttpResponseMessage も一緒に dispose する (= 大きい動画でも全 buffer せず、
    /// preload=metadata で途中 abort されるケースで帯域を無駄遣いしない)。</para>
    ///
    /// <para>失敗時は <c>e.Response</c> をセットせずに deferral.Complete() する
    /// (= WebView2 が通常通り CDN にリクエスト → CORS 違反でブロックされる流れ。
    /// JS 側の <c>video.onerror</c> で検知できる)。</para></summary>
    private static async Task ProxyCorsMediaAsync(
        CoreWebView2 core,
        CoreWebView2WebResourceRequestedEventArgs e,
        string url,
        CoreWebView2Deferral deferral)
    {
        // WebView2 のイベント引数は COM オブジェクトで、worker thread からの
        // ヘッダ取得は例外を投げることがある (Range/Referer が無いと ERROR_NOT_FOUND)。
        // そのため UI スレッドの段階で必要なヘッダを抜き出してから await Task.Yield に入る。
        string? range   = TryGetHeaderSafe(e, "Range");
        string? referer = TryGetHeaderSafe(e, "Referer");

        HttpResponseMessage? resp = null;
        try
        {
            await Task.Yield();
            var http = GetCorsProxyHttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, url);

            if (!string.IsNullOrEmpty(range))
            {
                try { req.Headers.TryAddWithoutValidation("Range", range); }
                catch (Exception ex) { Debug.WriteLine($"[CorsProxy] range header set failed: {ex.Message}"); }
            }
            if (!string.IsNullOrEmpty(referer))
            {
                try { req.Headers.TryAddWithoutValidation("Referer", referer); } catch { }
            }

            resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            ChBrowser.Services.Logging.LogService.Instance.Write($"[CorsProxy] response status={(int)resp.StatusCode} url={url}");

            // ヘッダを構築 (CRLF 区切り、CreateWebResourceResponse の仕様)。
            // upstream の Access-Control-* は捨てて C# 側で必ず * を付け直す (= 確実に CORS-clean)。
            var sb = new StringBuilder();
            foreach (var h in resp.Headers)
            {
                if (h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var v in h.Value) sb.Append(h.Key).Append(": ").Append(v).Append("\r\n");
            }
            if (resp.Content is not null)
            {
                foreach (var h in resp.Content.Headers)
                {
                    if (h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var v in h.Value) sb.Append(h.Key).Append(": ").Append(v).Append("\r\n");
                }
            }
            sb.Append("Access-Control-Allow-Origin: *\r\n");

            if (resp.Content is null)
            {
                Debug.WriteLine($"[CorsProxy] response.Content was null url={url}");
                return;
            }

            // MemoryStream にプリバッファ。理由:
            //   - WebView2 にネットワークストリームを直接渡すと Length 等のメタデータクエリで
            //     NotSupportedException が出る (= first-chance 例外がデバッガで毎回ブレーク)。
            //   - CORS proxy 経路は thumbnail extraction (preload=metadata) 専用なので、
            //     データサイズは現実的に小さい (大きい mp4 でもサーバ側で early-abort される)。
            //   - 安定性 / デバッグ体験 > 帯域効率、で MemoryStream を選択。
            var ms = new MemoryStream();
            await resp.Content.CopyToAsync(ms).ConfigureAwait(false);
            ms.Position = 0;

            var status     = (int)resp.StatusCode;
            var reason     = resp.ReasonPhrase ?? "OK";
            var headerText = sb.ToString();

            // CreateWebResourceResponse は UI スレッドで呼ぶ必要があるため Dispatcher へ。
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var webResp = core.Environment.CreateWebResourceResponse(ms, status, reason, headerText);
                    e.Response = webResp;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CorsProxy] CreateResponse failed: {ex.Message}");
                    ms.Dispose();
                }
            });
        }
        catch (Exception ex)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write($"[CorsProxy] failed url={url}: {ex.GetType().Name} - {ex.Message}");
        }
        finally
        {
            resp?.Dispose();
            deferral.Complete();
        }
    }

    /// <summary>WebView2 のイベント引数からヘッダを安全に取得 (= 無い場合の COMException も握り潰す)。</summary>
    private static string? TryGetHeaderSafe(CoreWebView2WebResourceRequestedEventArgs e, string name)
    {
        try { return e.Request.Headers.GetHeader(name); }
        catch { return null; }
    }

    /// <summary>キャッシュヒットしたローカルファイルを <see cref="FileStream"/> で開いて
    /// WebView2 に応答として渡す。Worker thread で実行することで dispatcher を即時解放する。
    /// FileStream は WebView2 が必要分だけ lazy に読み、応答完了後に dispose する。</summary>
    private static async Task ServeFromCacheAsync(
        CoreWebView2 core,
        CoreWebView2WebResourceRequestedEventArgs e,
        Services.Image.ImageCacheHit hit,
        string url,
        CoreWebView2Deferral deferral)
    {
        try
        {
            await Task.Yield(); // 即座に worker thread (= ThreadPool) に処理を逃がす
            var fs = new FileStream(
                hit.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            var headers  = $"Content-Type: {hit.ContentType}";
            e.Response   = core.Environment.CreateWebResourceResponse(fs, 200, "OK", headers);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ImageCache] hit serve failed for {url}: {ex.Message}");
            // Response 未設定 → WebView2 が通常通りネットワーク fetch にフォールバック
        }
        finally
        {
            deferral.Complete();
        }
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
