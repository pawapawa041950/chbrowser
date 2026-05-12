using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ChBrowser.Controls;
using ChBrowser.Models;
using ChBrowser.Services.Image;
using ChBrowser.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;

namespace ChBrowser.Views;

/// <summary>
/// 画像ビューアウィンドウ (Phase 10)。アプリ全体でシングルトン (App.xaml.cs で 1 度だけ生成)。
/// DataContext は <see cref="ImageViewerViewModel"/>。
/// </summary>
public partial class ImageViewerWindow : Window
{
    private readonly ImageSaver              _imageSaver;
    private readonly ImageCacheService       _imageCache;
    private readonly AiImageMetadataService  _aiImageMeta;

    /// <summary>画像詳細ペイン (右側 Grid.Column=2) のデフォルト幅 (px)。
    /// Tab / アコーディオン帯クリックで 0 ↔ <see cref="DetailsPaneDefaultWidth"/> をトグルする。</summary>
    private const double DetailsPaneDefaultWidth = 320;

    /// <summary>現在ペインを開いているか。デフォルトは true (= 起動時から開いた状態)。
    /// XAML 側でも DetailsColumn.Width=320 を初期値にしているので両方を揃えること。</summary>
    private bool _detailsVisible = true;

    /// <summary>全画面表示中か。<see cref="ToggleFullscreen"/> で切り替え、F11 / 右クリックメニュー両方から呼ばれる。</summary>
    private bool _isFullscreen;

    /// <summary>全画面に入る直前のウィンドウ状態 (= 通常表示に戻すときに復元する)。</summary>
    private WindowStyle?      _savedWindowStyle;
    private WindowState?      _savedWindowState;
    private ResizeMode?       _savedResizeMode;
    private GridLength?       _savedDetailsColumnWidth;
    private Visibility?       _savedTabStripVisibility;
    private Visibility?       _savedAccordionBarVisibility;

    /// <summary>DetailsWebView の shell HTML へのナビゲーションが完了したか。
    /// 完了前に push しようとした JSON は <see cref="_pendingDetailsJson"/> に置く。</summary>
    private bool _detailsShellReady;

    /// <summary>shell ロード前に push 要求が来たときに保留する最後の JSON 1 件 (古いものは捨てる)。</summary>
    private string? _pendingDetailsJson;

    public ImageViewerWindow(ImageViewerViewModel vm, ImageSaver imageSaver, ImageCacheService imageCache,
                             AiImageMetadataService aiImageMeta, bool detailsPaneDefaultOpen = true)
    {
        InitializeComponent();
        DataContext  = vm;
        _imageSaver  = imageSaver;
        _imageCache  = imageCache;
        _aiImageMeta = aiImageMeta;

        // 設定値で「閉じた状態スタート」が指定されていれば、XAML で開いた状態 (320) になっているのを閉じる方向に揃える。
        // 既定 = open。ToggleDetailsPane の中身を直に呼ぶと PushDetailsForCurrentTab が走って意味ないので
        // 直接フィールド + Column.Width + chevron を更新する。
        if (!detailsPaneDefaultOpen)
        {
            _detailsVisible       = false;
            DetailsColumn.Width   = new GridLength(0);
            AccordionChevron.Text = "◀";
        }

        // VM のキーボードショートカットからもメニューと同じ動作を実行できるように Window 側のロジックを配線
        vm.SaveImageRequested     = url => _ = SaveImageWithDialog(url);
        vm.CopyUrlRequested       = url => CopyUrl(url);
        vm.OpenInBrowserRequested = url => OpenInBrowser(url);

        // SelectedTab が変わったら詳細ペインの内容も差し替える (= 表示中なら即時反映、隠れていても次に開いた時に表示)。
        vm.PropertyChanged += Vm_PropertyChanged;

        // 詳細ペイン WebView2 を非同期で初期化 (== shell HTML を NavigateToString)。
        Loaded += (_, _) => _ = InitializeDetailsWebViewAsync();

        // 「✕」で閉じても破棄せず、隠してアプリ常駐させる (= 次に画像を開く時は再表示)
        Closing += (_, e) =>
        {
            if (Application.Current?.MainWindow is not null && !App.IsShuttingDown)
            {
                e.Cancel = true;
                Hide();
            }
        };

        // タブが全て閉じられたらウィンドウ自体を Hide する (= 次に画像を開いた時は OpenAndShow が再表示)。
        // ImageViewerViewModel は singleton で再利用するため Close ではなく Hide で十分。
        vm.Tabs.CollectionChanged += (_, _) =>
        {
            if (vm.Tabs.Count == 0 && IsVisible) Hide();
        };
    }

    // -----------------------------------------------------------------
    // 画像詳細ペイン (Tab で toggle)
    // -----------------------------------------------------------------

    /// <summary>ショートカット handler ("viewer.toggle_details") + アコーディオン帯クリックの両方から呼ばれる。
    /// 列幅を 0 ↔ 320 で切替。シェブロン (▶/◀) も同時に向きを反転させる。
    /// 表示時 (= 開く方向) は現在タブのメタを push し直す。</summary>
    public void ToggleDetailsPane()
    {
        _detailsVisible = !_detailsVisible;
        DetailsColumn.Width = _detailsVisible
            ? new GridLength(DetailsPaneDefaultWidth, GridUnitType.Pixel)
            : new GridLength(0);
        // シェブロンの向きを更新:
        //   開いている時 ▶ — クリックすると右に閉じる (= ペインが右端へ収納される) 印象
        //   閉じている時 ◀ — クリックすると左に開く (= ペインが左方向に展開される)
        AccordionChevron.Text = _detailsVisible ? "▶" : "◀";
        if (_detailsVisible) PushDetailsForCurrentTab();
    }

    /// <summary>アコーディオン帯クリックハンドラ。<see cref="ToggleDetailsPane"/> を呼ぶだけ。</summary>
    private void AccordionBar_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ToggleDetailsPane();
        e.Handled = true;
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImageViewerViewModel.SelectedTab))
        {
            PushDetailsForCurrentTab();
        }
    }

    private async Task InitializeDetailsWebViewAsync()
    {
        try
        {
            await DetailsWebView.EnsureCoreWebView2Async().ConfigureAwait(true);
            var tcs = new TaskCompletionSource();
            void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs args)
            {
                DetailsWebView.CoreWebView2.NavigationCompleted -= OnNav;
                tcs.TrySetResult();
            }
            DetailsWebView.CoreWebView2.NavigationCompleted += OnNav;
            DetailsWebView.CoreWebView2.NavigateToString(WebView2Helper.LoadViewerDetailsShellHtml());
            await tcs.Task.ConfigureAwait(true);
            _detailsShellReady = true;

            // shell ロード前に保留されていたメッセージがあれば送信。
            if (_pendingDetailsJson is { } pending)
            {
                _pendingDetailsJson = null;
                try { DetailsWebView.CoreWebView2.PostWebMessageAsJson(pending); }
                catch (Exception ex) { Debug.WriteLine($"[Viewer] flush pending details failed: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Viewer] details shell init failed: {ex.Message}");
        }
    }

    /// <summary>現在 SelectedTab の URL について <see cref="AiImageMetadataService"/> でメタを取得し、
    /// 詳細ペイン JS に <c>setDetails</c> メッセージとして push する。
    /// shell が未ロードなら <see cref="_pendingDetailsJson"/> に積んでおく。</summary>
    private async void PushDetailsForCurrentTab()
    {
        if (DataContext is not ImageViewerViewModel vm) return;
        var url = vm.SelectedTab?.Url;

        try
        {
            object payload;
            if (string.IsNullOrEmpty(url))
            {
                payload = new { type = "setDetails", url = (string?)null, details = (object?)null };
            }
            else
            {
                var meta = await _aiImageMeta.TryGetAsync(url).ConfigureAwait(true);
                payload = new
                {
                    type    = "setDetails",
                    url,
                    details = meta is null ? null : new
                    {
                        format     = meta.Format,
                        fileSize   = meta.FileSize,
                        width      = meta.Width,
                        height     = meta.Height,
                        model      = meta.Model,
                        generator  = meta.Generator,
                        positive   = meta.Positive,
                        negative   = meta.Negative,
                        parameters = meta.Parameters,
                    },
                };
            }

            var json = JsonSerializer.Serialize(payload);
            if (_detailsShellReady && DetailsWebView.CoreWebView2 is not null)
            {
                DetailsWebView.CoreWebView2.PostWebMessageAsJson(json);
            }
            else
            {
                _pendingDetailsJson = json;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Viewer] PushDetails failed: {ex.Message}");
        }
    }

    /// <summary>タブが <see cref="ImageViewerViewModel.Tabs"/> から削除されると Unloaded が発火する。
    /// WebView2 は IDisposable な native HWND を抱える (= 動画再生/JS 実行を継続する) ため、
    /// ここで明示的に Dispose しないと閉じたタブの動画音声が鳴り続ける + 再オープン時に二重再生になる。
    /// WPF は teardown 中に DataContext を null に戻してから Unloaded を発火するケースがあるので、
    /// ctx が拾えなかった場合は「本当に閉じられた」と解釈して Dispose する。
    /// ctx が拾えてかつ Tabs にまだ存在する場合のみ「一時的 unload」とみなして Dispose しない。</summary>
    private void ViewerWebView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not WebView2 wv) return;
        var ctx = wv.DataContext as ImageViewerTabViewModel;
        if (ctx is not null && DataContext is ImageViewerViewModel vm && vm.Tabs.Contains(ctx)) return;
        try { wv.Dispose(); }
        catch (Exception ex) { Debug.WriteLine($"[ImageViewerWindow] WebView2 Dispose failed: {ex.Message}"); }
    }

    /// <summary>JS の viewer.js から飛んでくるメッセージを捌く。</summary>
    private void ViewerWebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (DataContext is not ImageViewerViewModel vm) return;

        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var tp) ? tp.GetString() : null;

            // Phase 16+: ブリッジから shortcut / gesture / bridgeReady / gestureProgress を受信。
            // category は "ビューアウィンドウ" 固定。
            if (type == "shortcut" || type == "gesture")
            {
                if (Application.Current is App app && app.ShortcutManager is { } mgr)
                {
                    var descriptor = root.TryGetProperty("descriptor", out var dp) ? dp.GetString() : null;
                    if (!string.IsNullOrEmpty(descriptor)) mgr.Dispatch("ビューアウィンドウ", descriptor);
                }
                return;
            }
            if (type == "gestureProgress" || type == "gestureEnd")
            {
                if (Application.Current is App app && app.ShortcutManager is { } mgr)
                {
                    if (type == "gestureEnd") mgr.NotifyGestureProgress(null, null);
                    else
                    {
                        var value = root.TryGetProperty("value", out var vp) ? vp.GetString() : "";
                        mgr.NotifyGestureProgress("ビューアウィンドウ", value ?? "");
                    }
                }
                return;
            }
            if (type == "bridgeReady")
            {
                if (Application.Current is App app && app.ShortcutManager is { } mgr
                    && sender is Microsoft.Web.WebView2.Wpf.WebView2 wv && wv.CoreWebView2 is { } core)
                {
                    var map = mgr.GetBindingsForCategory("ビューアウィンドウ");
                    var json = JsonSerializer.Serialize(new { type = "setShortcutBindings", bindings = map });
                    try { core.PostWebMessageAsJson(json); } catch (Exception ex) { Debug.WriteLine($"[ImageViewer] PushBindings failed: {ex.Message}"); }
                }
                return;
            }

            switch (type)
            {
                case "wheelTab":
                {
                    var dir = root.TryGetProperty("direction", out var dp) ? dp.GetString() : null;
                    if (dir == "next") vm.NextTab();
                    else               vm.PrevTab();
                    break;
                }
                case "contextMenu":
                {
                    if (vm.SelectedTab is { Url: var url } && !string.IsNullOrEmpty(url))
                        ShowImageContextMenu(url);
                    break;
                }
                case "imageReady":
                {
                    // ビューア WebView2 が画像ロード完了 → キャッシュ書き込みも (ほぼ) 完了している。
                    // タブのサムネイルを HTTP URL からキャッシュ file path に切り替えて、
                    // 特殊ヘッダ要求 (pixiv の Referer 等) を回避する。
                    if (sender is Microsoft.Web.WebView2.Wpf.WebView2 wv
                        && wv.DataContext is ImageViewerTabViewModel tab)
                    {
                        tab.RefreshThumbnailFromCache(_imageCache);
                        // 画像本体のロードが完了 = キャッシュにファイルが揃った。
                        // 詳細ペインがまだ「取得できません」を出していた場合に備え、もう一度 push し直す。
                        if (ReferenceEquals(tab, vm.SelectedTab)) PushDetailsForCurrentTab();
                    }
                    break;
                }
                case "videoThumbnailCache":
                {
                    // viewer.js が <video> のフレーム (seeked 後) を canvas で抽出して送ってきた
                    // JPEG data URI。スレッド側と同じ ImageCacheService (Kind=VideoThumb) に保存する。
                    // 保存後に該当タブの ThumbnailPath をローカルファイルパスに差し替える。
                    var dataUri = root.TryGetProperty("dataUri", out var dp2) ? dp2.GetString() : null;
                    var thumbUrl = root.TryGetProperty("url", out var up2) ? up2.GetString() : null;
                    if (!string.IsNullOrEmpty(dataUri) && !string.IsNullOrEmpty(thumbUrl))
                    {
                        _ = SaveVideoThumbAndRefreshAsync(vm, thumbUrl!, dataUri!);
                    }
                    break;
                }
                case "videoThumbnailCacheFailed":
                {
                    var failUrl  = root.TryGetProperty("url",     out var fup) ? fup.GetString() : "";
                    var failErr  = root.TryGetProperty("error",   out var fep) ? fep.GetString() : "";
                    var failMsg  = root.TryGetProperty("message", out var fmp) ? fmp.GetString() : "";
                    ChBrowser.Services.Logging.LogService.Instance.Write($"[VideoThumb] viewer capture failed url={failUrl} error={failErr} msg={failMsg}");
                    break;
                }
                // imageError は今のところ無視 (status 表示は Phase 10c 以降で検討)
            }
        }
        catch (JsonException) { /* malformed message — ignore */ }
    }

    /// <summary>viewer.js から届いた動画フレーム data URI を <see cref="ImageCacheService"/> に
    /// Kind=VideoThumb で保存し、対応タブの <see cref="ImageViewerTabViewModel.ThumbnailPath"/> を
    /// ローカルファイルパスに差し替える。data URI の decode 失敗 / I/O 失敗時は何もしない
    /// (= タブは ▶ オーバーレイのみのまま)。
    ///
    /// 旧実装の temp file 経路 (<c>%TEMP%/ChBrowser_VideoThumbs</c>) は廃止し、スレッド側 / ビューワ側で
    /// 統一して ImageCacheService 経由でキャッシュ管理する。これにより:
    /// <list type="bullet">
    /// <item><description>thread / viewer のどちらで先に抽出してももう一方からキャッシュヒットで利用できる</description></item>
    /// <item><description>LRU 削除や上限管理が一貫</description></item>
    /// </list></summary>
    private async Task SaveVideoThumbAndRefreshAsync(ImageViewerViewModel vm, string url, string dataUri)
    {
        // data:image/jpeg;base64,<payload>
        var commaIdx = dataUri.IndexOf(',');
        if (commaIdx < 0) return;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(dataUri[(commaIdx + 1)..]); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoThumb] base64 decode failed: {ex.Message}");
            return;
        }

        try
        {
            using var ms = new MemoryStream(bytes);
            await _imageCache.SaveAsync(url, ms, "image/jpeg", CacheKind.VideoThumb).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoThumb] SaveAsync failed: {ex.Message}");
            return;
        }

        // 保存後 (or 既にキャッシュ済) の物理ファイルパスを取って該当タブに反映。
        if (!_imageCache.TryGet(url, out var hit, CacheKind.VideoThumb)) return;
        foreach (var t in vm.Tabs)
        {
            if (t.Url == url)
            {
                t.ThumbnailPath = hit.FilePath;
                break;
            }
        }
    }

    /// <summary>layout.json から読んだ <see cref="ViewerWindowGeometry"/> を反映する。
    /// 値の妥当性チェック (NaN / 負値 / MinWidth/MinHeight 未満) を入れて、
    /// 不正値は無視して既定 (XAML 値) のままにする。</summary>
    public void ApplyGeometry(ViewerWindowGeometry g)
    {
        if (IsFinitePositive(g.Width)  && g.Width  >= MinWidth)  Width  = g.Width;
        if (IsFinitePositive(g.Height) && g.Height >= MinHeight) Height = g.Height;
        if (IsFinite(g.Left)) Left = g.Left;
        if (IsFinite(g.Top))  Top  = g.Top;
        if (g.Maximized) WindowState = WindowState.Maximized;
    }

    /// <summary>現在のジオメトリを保存用にスナップショット。最大化中は <see cref="Window.RestoreBounds"/>
    /// (= 通常表示時のサイズ・位置) を採用する。</summary>
    public ViewerWindowGeometry CaptureGeometry()
    {
        var bounds = WindowState == WindowState.Maximized
            ? RestoreBounds
            : new Rect(Left, Top, Width, Height);
        return new ViewerWindowGeometry(
            Left:      bounds.Left,
            Top:       bounds.Top,
            Width:     bounds.Width,
            Height:    bounds.Height,
            Maximized: WindowState == WindowState.Maximized);
    }

    private static bool IsFinite(double v)         => !double.IsNaN(v) && !double.IsInfinity(v);
    private static bool IsFinitePositive(double v) => IsFinite(v) && v > 0;

    /// <summary>新規にタブを開いてウィンドウを Show + 前面化する。</summary>
    public void OpenAndShow(string url)
    {
        if (DataContext is not ImageViewerViewModel vm) return;
        var tab = vm.OpenOrAddTab(url);
        // 画像タブ: 既にキャッシュにあれば即座にサムネをローカルファイルに切替 (HTTP fetch を避ける)
        tab.RefreshThumbnailFromCache(_imageCache);
        // Phase 6: 動画タブはサムネキャッシュ流用 + 動画本体キャッシュ参照 / 未キャッシュなら並列 DL kick
        if (tab.IsVideo && Application.Current is App app && app.VideoDownloadManagerInstance is { } mgr)
        {
            tab.InitializeForVideo(_imageCache, mgr);
        }
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Window.Resources の "ImageContextMenu" を取り出して URL を Tag に積んで popup。</summary>
    private void ShowImageContextMenu(string url)
    {
        if (Resources["ImageContextMenu"] is not ContextMenu menu) return;
        menu.Tag             = url;
        menu.PlacementTarget = this;
        menu.Placement       = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen          = true;
    }

    /// <summary>ContextMenu.Tag から URL を取り出すヘルパ (各クリックハンドラで共通)。</summary>
    private static string? UrlFromMenu(object sender)
    {
        if (sender is not MenuItem mi) return null;
        var owner = ItemsControl.ItemsControlFromItemContainer(mi) as ContextMenu
                  ?? mi.Parent as ContextMenu;
        return owner?.Tag as string;
    }

    private async void SaveImage_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlFromMenu(sender);
        if (string.IsNullOrEmpty(url)) return;
        await SaveImageWithDialog(url);
    }

    private void CopyImageUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlFromMenu(sender);
        if (string.IsNullOrEmpty(url)) return;
        CopyUrl(url);
    }

    private void OpenInBrowser_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlFromMenu(sender);
        if (string.IsNullOrEmpty(url)) return;
        OpenInBrowser(url);
    }

    private void ZoomActual_Click(object sender, RoutedEventArgs e) => RequestZoomOnSelectedTab("actual");
    private void ZoomFit_Click(object sender, RoutedEventArgs e)    => RequestZoomOnSelectedTab("fit");

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    /// <summary>ContextMenu を開いた瞬間に「全画面表示」MenuItem のチェック状態を現在値に同期する
    /// (= F11 ショートカット経由で切り替えた場合でも、メニュー再表示時に正しいチェック状態になる)。</summary>
    private void ImageContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm) return;
        foreach (var item in cm.Items)
        {
            if (item is MenuItem mi && (mi.Tag as string) == "fullscreen")
            {
                mi.IsChecked = _isFullscreen;
                break;
            }
        }
    }

    /// <summary>全画面表示と通常表示を切り替える。右クリック「全画面表示」/ F11 の両方から呼ばれる。
    /// 全画面時はタブストリップ / アコーディオン帯 / 詳細ペインを隠して画像エリアを画面いっぱいにする。</summary>
    public void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _savedWindowStyle            = WindowStyle;
            _savedWindowState            = WindowState;
            _savedResizeMode             = ResizeMode;
            _savedDetailsColumnWidth     = DetailsColumn.Width;
            _savedTabStripVisibility     = TabStrip.Visibility;
            _savedAccordionBarVisibility = AccordionBar.Visibility;

            // WindowStyle=None + Maximized は順序が重要 (= 既に Maximized だと borderless 化が反映されない)。
            // 一度 Normal に戻してからスタイルを変える。
            WindowState  = WindowState.Normal;
            WindowStyle  = WindowStyle.None;
            ResizeMode   = ResizeMode.NoResize;
            WindowState  = WindowState.Maximized;

            // タブストリップ / アコーディオン帯 / 詳細ペインを隠す → 画像のみ表示
            TabStrip.Visibility       = Visibility.Collapsed;
            AccordionBar.Visibility   = Visibility.Collapsed;
            DetailsColumn.Width       = new GridLength(0);

            _isFullscreen = true;
        }
        else
        {
            if (_savedWindowState is { } ws)    WindowState = ws;
            if (_savedWindowStyle is { } wst)   WindowStyle = wst;
            if (_savedResizeMode  is { } rm)    ResizeMode  = rm;

            if (_savedTabStripVisibility     is { } tv) TabStrip.Visibility     = tv;
            if (_savedAccordionBarVisibility is { } av) AccordionBar.Visibility = av;
            // 詳細ペインは「閉じてた時は閉じたまま戻す」が自然なので _detailsVisible を尊重して復元。
            DetailsColumn.Width = _detailsVisible
                ? new GridLength(DetailsPaneDefaultWidth, GridUnitType.Pixel)
                : new GridLength(0);

            _isFullscreen = false;
        }
    }

    private void RequestZoomOnSelectedTab(string mode)
    {
        if (DataContext is not ImageViewerViewModel vm) return;
        if (vm.SelectedTab is not { } tab) return;
        tab.PendingZoomMode = new ZoomModeRequest(mode);
    }

    /// <summary>ContextMenu / Ctrl+S の両方から呼ばれる。SaveFileDialog を出して保存する。</summary>
    private async System.Threading.Tasks.Task SaveImageWithDialog(string url)
    {
        var dlg = new SaveFileDialog
        {
            FileName  = ImageSaver.SuggestFileName(url),
            Filter    = "画像ファイル (*.jpg;*.png;*.gif;*.webp)|*.jpg;*.png;*.gif;*.webp|すべてのファイル (*.*)|*.*",
            Title     = "画像を保存",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            await _imageSaver.SaveAsync(url, dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存に失敗しました: {ex.Message}", "ChBrowser",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyUrl(string url)
    {
        try { Clipboard.SetText(url); }
        catch (Exception ex) { Debug.WriteLine($"[ImageViewer] CopyUrl failed: {ex.Message}"); }
    }

    private static void OpenInBrowser(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = uri.ToString(),
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ImageViewer] OpenInBrowser failed: {ex.Message}");
        }
    }
}
