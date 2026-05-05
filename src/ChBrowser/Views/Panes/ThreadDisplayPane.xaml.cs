using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ChBrowser.Services.WebView2;
using ChBrowser.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ChBrowser.Views.Panes;

/// <summary>スレッド表示ペイン (Phase 23 抽出)。
/// ヘッダ + 操作ツールバー (差分取得 / 書込 / モード切替 / 削除 / お気に入り) + タブストリップ +
/// 全 ThreadTabViewModel の WebView2 を ItemsControl で並列保持。
/// 旧 MainWindow の ThreadPane の役割と中央ツールバー (旧 Grid.Row=2) を本ペイン内に統合した。</summary>
public partial class ThreadDisplayPane : UserControl
{
    public ThreadDisplayPane()
    {
        InitializeComponent();
        ChBrowser.Controls.PaneDragInitiator.Attach(HeaderBar, ChBrowser.Models.PaneId.ThreadDisplay);
    }

    // ---- ペインフォーカス → ViewModel に通知 ----

    private void Pane_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => (DataContext as MainViewModel)?.MarkThreadPaneActive();

    private void Pane_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => (DataContext as MainViewModel)?.MarkThreadPaneActive();

    // ---- WebView2 → JS メッセージ受信 ----

    private void ThreadViewWebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var (type, payload) = WebMessageBridge.TryParseMessage(e);

        if (type == "paneActivated")
        {
            (DataContext as MainViewModel)?.MarkThreadPaneActive();
            return;
        }
        if (WebMessageBridge.TryDispatchCommonMessage(sender, type, payload, "スレッド表示領域")) return;

        switch (type)
        {
            case "openUrl":            HandleOpenUrl(payload); break;
            case "scrollPosition":     HandleScrollPosition(sender, payload); break;
            case "readMark":           HandleReadMark(sender, payload); break;
            case "imageMetaRequest":   HandleImageMetaRequest(sender, payload); break;
            case "aiMetadataRequest":  HandleAiMetadataRequest(sender, payload); break;
            case "openInViewer":       HandleOpenInViewer(payload); break;
            case "replyToPost":        HandleReplyToPost(sender, payload); break;
            case "ngAdd":              HandleNgAdd(sender, payload); break;
            case "toggleOwnPost":      HandleToggleOwnPost(sender, payload); break;
        }
    }

    /// <summary>JS の post-no クリックメニュー → 「自分の書き込み」トグルで呼ばれる。
    /// number と isOwn (新しい状態) を受け取り、MainViewModel に状態更新を依頼する。</summary>
    private void HandleToggleOwnPost(object sender, JsonElement payload)
    {
        if (sender is not WebView2 wv) return;
        if (wv.DataContext is not ThreadTabViewModel tab) return;
        if (DataContext is not MainViewModel main) return;
        if (!payload.TryGetProperty("number", out var nProp) || !nProp.TryGetInt32(out var num)) return;
        if (!payload.TryGetProperty("isOwn",  out var oProp) || oProp.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return;
        var isOwn = oProp.GetBoolean();
        main.ToggleOwnPost(tab, num, isOwn);
    }

    /// <summary>JS の post-no クリックメニューで「返信」を選んだとき。
    /// 元レス番号を受け取り、書き込みダイアログを「&gt;&gt;N\n」入りで開く。</summary>
    private void HandleReplyToPost(object sender, JsonElement payload)
    {
        if (sender is not WebView2 wv) return;
        if (wv.DataContext is not ThreadTabViewModel tab) return;
        if (DataContext is not MainViewModel main) return;
        if (!payload.TryGetProperty("number", out var nProp) || !nProp.TryGetInt32(out var num)) return;
        main.OpenReplyDialog(tab, num);
    }

    /// <summary>JS の post-no クリックメニューで「NG登録 (名前/ID/ワッチョイ)」を選んだとき。
    /// 抽出済の値 (target / value) と元レス番号を渡し、C# 側で NG 登録ダイアログを開く。</summary>
    private void HandleNgAdd(object sender, JsonElement payload)
    {
        if (sender is not WebView2 wv) return;
        if (wv.DataContext is not ThreadTabViewModel tab) return;
        if (DataContext is not MainViewModel main) return;
        var target = payload.TryGetProperty("target", out var tp) ? (tp.GetString() ?? "") : "";
        var value  = payload.TryGetProperty("value",  out var vp) ? (vp.GetString() ?? "") : "";
        if (string.IsNullOrEmpty(target)) return;
        main.OpenNgQuickAdd(tab, target, value);
    }

    /// <summary>JS が画像のホバーで「この URL の AI 生成メタを欲しい」と要求してきた時のハンドラ。
    /// キャッシュ済みファイルを <see cref="AiImageMetadataService"/> で読み、SD WebUI infotext を抽出して返す。
    /// 解析できなかった場合は <c>hasData=false</c> を返す (= JS 側はポップアップを出さない)。</summary>
    private void HandleAiMetadataRequest(object sender, JsonElement payload)
    {
        if (sender is not WebView2 wv) return;
        var mainWindow = Window.GetWindow(this) as MainWindow;
        if (mainWindow?.AiImageMetadataService is null) return;
        if (!payload.TryGetProperty("url", out var urlProp)) return;
        var url = urlProp.GetString();
        if (string.IsNullOrEmpty(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;

        _ = ReplyAiMetadataAsync(mainWindow, wv, url);
    }

    private static async Task ReplyAiMetadataAsync(MainWindow mainWindow, WebView2 wv, string url)
    {
        try
        {
            // 「キャッシュに来ているか」を先に判定する。来ていなければ JS 側に「キャッシュ未到着」と伝える
            // (= JS は no-data をキャッシュせず、次のホバーで再試行できるようにする)。
            var cached = mainWindow.ImageCacheService?.Contains(url) ?? false;
            var meta   = cached
                ? await mainWindow.AiImageMetadataService!.TryGetAsync(url).ConfigureAwait(true)
                : null;
            if (wv.CoreWebView2 is null) return;
            var json = JsonSerializer.Serialize(new
            {
                type      = "aiMetadata",
                url,
                cached,
                hasData   = meta is { HasAiData: true },
                model     = meta?.Model,
                generator = meta?.Generator,
                positive  = meta?.Positive,
                negative  = meta?.Negative,
            });
            wv.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AiMeta] reply failed: {ex.Message}");
        }
    }

    private static void HandleOpenInViewer(JsonElement payload)
    {
        if (!payload.TryGetProperty("url", out var urlProp)) return;
        var url = urlProp.GetString();
        if (string.IsNullOrEmpty(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;
        if (Application.Current is App app) app.ShowImageInViewer(url);
    }

    private static void HandleOpenUrl(JsonElement payload)
    {
        if (!payload.TryGetProperty("url", out var urlProp)) return;
        var url = urlProp.GetString();
        if (string.IsNullOrEmpty(url)) return;
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
            Debug.WriteLine($"[OpenUrl] failed: {ex.Message}");
        }
    }

    private void HandleScrollPosition(object sender, JsonElement payload)
    {
        if (sender is not WebView2 wv) return;
        if (wv.DataContext is not ThreadTabViewModel tab) return;
        if (DataContext is not MainViewModel main) return;
        if (!payload.TryGetProperty("postNumber", out var numProp)) return;
        if (numProp.ValueKind != JsonValueKind.Number) return;
        if (!numProp.TryGetInt32(out var num)) return;
        main.UpdateScrollPosition(tab.Board, tab.ThreadKey, num);
    }

    private void HandleReadMark(object sender, JsonElement payload)
    {
        if (sender is not WebView2 wv) return;
        if (wv.DataContext is not ThreadTabViewModel tab) return;
        if (DataContext is not MainViewModel main) return;
        if (!payload.TryGetProperty("postNumber", out var numProp)) return;
        if (numProp.ValueKind != JsonValueKind.Number) return;
        if (!numProp.TryGetInt32(out var num)) return;
        main.UpdateReadMark(tab.Board, tab.ThreadKey, num);
    }

    private void HandleImageMetaRequest(object sender, JsonElement payload)
    {
        if (sender is not WebView2 wv) return;
        var mainWindow = Window.GetWindow(this) as MainWindow;
        if (mainWindow?.ImageMetaService is null) return;
        if (!payload.TryGetProperty("url", out var urlProp)) return;
        var url = urlProp.GetString();
        if (string.IsNullOrEmpty(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;

        _ = ReplyImageMetaAsync(mainWindow, wv, url);
    }

    private static async Task ReplyImageMetaAsync(MainWindow mainWindow, WebView2 wv, string url)
    {
        try
        {
            string? resolvedUrl = null;
            var isAsync = ChBrowser.Services.Image.UrlExpander.IsAsyncExpandable(url);
            if (isAsync && mainWindow.UrlExpander is not null)
                resolvedUrl = await mainWindow.UrlExpander.ExpandAsync(url).ConfigureAwait(true);

            var actualUrl = resolvedUrl ?? url;

            bool   cached = false;
            long?  size   = null;
            bool   ok;

            if (isAsync && resolvedUrl is null)
            {
                ok = false;
            }
            else
            {
                cached = mainWindow.ImageCacheService?.Contains(actualUrl) ?? false;
                if (cached)
                {
                    ok = true;
                }
                else if (mainWindow.ImageMetaService is not null)
                {
                    var meta = await mainWindow.ImageMetaService.GetAsync(actualUrl).ConfigureAwait(true);
                    ok   = meta.Ok;
                    size = meta.Size;
                }
                else
                {
                    ok = false;
                }
            }

            if (wv.CoreWebView2 is null) return;
            var json = JsonSerializer.Serialize(new
            {
                type        = "imageMeta",
                url,
                resolvedUrl,
                ok,
                size,
                cached,
            });
            wv.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ImageMeta] reply failed: {ex.Message}");
        }
    }

    // ---- タブの右クリックメニュー (中/ダブル/修飾+左 は ShortcutManager 側で dispatch) ----

    private void ThreadTabItem_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TabItem ti) return;
        if (ti.DataContext is null) return;
        if (e.OriginalSource is DependencyObject src && TabClickHelper.FindAncestor<ButtonBase>(src) is not null) return;
        if (TryFindResource("ThreadTabContextMenu") is not ContextMenu menu) return;

        menu.PlacementTarget = ti;
        menu.Placement       = PlacementMode.MousePoint;
        menu.DataContext     = ti.DataContext;
        menu.IsOpen          = true;
        e.Handled            = true;
    }

    // ---- 右クリックメニュー: 動的な Header 切替 ----

    private void ThreadTabContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm) return;
        if (cm.DataContext is not ThreadTabViewModel tab) return;

        foreach (var item in TabClickHelper.EnumerateAllMenuItems(cm))
        {
            if ((item.Tag as string) == "fav")
                item.Header = tab.IsFavorited ? "お気に入りから削除" : "お気に入りに追加";
        }
    }

    private static T? TabOf<T>(object sender) where T : class
        => (sender as MenuItem)?.DataContext as T;

    private void ThreadTabFav_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf<ThreadTabViewModel>(sender) is not { } tab) return;
        (DataContext as MainViewModel)?.ToggleThreadFavorite(tab);
    }

    private void ThreadTabCopyTitle_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf<ThreadTabViewModel>(sender) is not { } tab) return;
        Clipboard.SetText(tab.Title ?? "");
    }

    private void ThreadTabCopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf<ThreadTabViewModel>(sender) is not { } tab) return;
        Clipboard.SetText(tab.Url);
    }

    private void ThreadTabCopyTitleAndUrl_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf<ThreadTabViewModel>(sender) is not { } tab) return;
        Clipboard.SetText($"{tab.Title}\n{tab.Url}");
    }

    private void ThreadTabFindNext_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf<ThreadTabViewModel>(sender) is not { } tab) return;
        if (DataContext is not MainViewModel main) return;
        _ = main.OpenNextThreadSearchAsync(tab);
    }

    private void ThreadTabDeleteLog_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf<ThreadTabViewModel>(sender) is not { } tab) return;
        (DataContext as MainViewModel)?.DeleteThreadLog(tab);
    }
}
