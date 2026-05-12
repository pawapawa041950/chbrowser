using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ChBrowser.Services.Image;
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
        Loaded += (_, __) => WireVideoDownloadCompletionToPane();
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
            // ↑ HandleOpenUrl 内で 5ch.io スレ URL を検出した場合だけ本アプリの新タブで開く分岐をする。
            case "scrollPosition":     HandleScrollPosition(sender, payload); break;
            case "imageMetaRequest":   HandleImageMetaRequest(sender, payload); break;
            case "aiMetadataRequest":  HandleAiMetadataRequest(sender, payload); break;
            case "openInViewer":       HandleOpenInViewer(payload); break;
            case "replyToPost":        HandleReplyToPost(sender, payload); break;
            case "ngAdd":              HandleNgAdd(sender, payload); break;
            case "toggleOwnPost":      HandleToggleOwnPost(sender, payload); break;
            case "postNoContextMenu":  HandlePostNoContextMenu(sender, payload); break;
            case "urlContextMenu":     HandleUrlContextMenu(sender, payload); break;
            case "threadPreviewRequest": HandleThreadPreviewRequest(sender, payload); break;
            case "videoThumbnailCache": HandleVideoThumbnailCache(sender, payload); break;
            case "videoThumbnailCacheFailed":
            {
                // サムネ抽出失敗を VideoDownloadManager に記憶。次回スレッド表示時の自動再試行を抑制する。
                // ユーザ明示クリック (videoDownloadStart) でリセットされる。
                var failUrl = payload.TryGetProperty("url",     out var fup) ? fup.GetString() : "";
                var failErr = payload.TryGetProperty("error",   out var fep) ? fep.GetString() : "";
                var failMsg = payload.TryGetProperty("message", out var fmp) ? fmp.GetString() : "";
                ChBrowser.Services.Logging.LogService.Instance.Write($"[VideoThumbCache] extract FAILED url={failUrl} error={failErr} msg={failMsg}");
                if (!string.IsNullOrEmpty(failUrl)
                    && Application.Current is App app
                    && app.VideoDownloadManagerInstance is { } failMgr)
                {
                    failMgr.MarkThumbFailed(failUrl);
                }
                break;
            }
            case "videoCacheQuery":    HandleVideoCacheQuery(sender, payload); break;
            case "videoDownloadStart": HandleVideoDownloadStart(sender, payload); break;
            case "imageLoadFailed":    HandleImageLoadFailed(payload); break;
            // 全 Kind 失敗状態リセット用の統一メッセージ (Step F)。
            // 旧 imageRetry / videoDownloadStart 内の ResetThumbFailedState 等はこれに集約。
            case "mediaSlotRetry":     HandleMediaSlotRetry(payload); break;
        }
    }

    // ---- 画像 GET 失敗の tracker 記録 / リセット (Step D) ----

    /// <summary>JS の <c>&lt;img&gt;.onerror</c> で画像取得に失敗した URL を tracker に記憶。
    /// 次回スレッド表示時、<see cref="HandleImageMetaRequest"/> の応答で imageLoadFailed=true となり
    /// JS 側は自動 loadSlotImage をスキップして「クリックで再試行」表示にする。</summary>
    private static void HandleImageLoadFailed(JsonElement payload)
    {
        if (!payload.TryGetProperty("url", out var urlProp)) return;
        var url = urlProp.GetString();
        if (string.IsNullOrEmpty(url)) return;
        if (Application.Current is App app && app.MediaAcquisitionTrackerInstance is { } tracker)
        {
            tracker.MarkFailed(url, ChBrowser.Services.Media.MediaAcquisitionKind.Image);
        }
    }

    /// <summary>JS の <c>retrySlot</c> / <c>playMedia</c> 経由 (= ユーザクリック) で全 Kind の失敗状態をクリア (Step F)。
    /// retrySlot は画像 GET 失敗 / SNS 展開失敗の両方で呼ばれ、playMedia は動画再生開始で呼ばれる。
    /// それぞれ別のメッセージにしていたものを統一: 「クリックされた = 全部リセットして再試行したい」と解釈する。
    /// 続く本来の動作メッセージ (imageMetaRequest / videoDownloadStart) では失敗フラグ false になり再試行が走る。</summary>
    private static void HandleMediaSlotRetry(JsonElement payload)
    {
        if (!payload.TryGetProperty("url", out var urlProp)) return;
        var url = urlProp.GetString();
        if (string.IsNullOrEmpty(url)) return;
        if (Application.Current is App app && app.MediaAcquisitionTrackerInstance is { } tracker)
        {
            tracker.ResetAll(url);
        }
    }

    // ---- 動画サムネキャッシュ書き込み (Phase 3) + 状態問い合わせ / DL 起動 (Phase 5) ----

    /// <summary>thread.js の <c>extractAndCacheVideoThumbnail</c> が抽出した JPEG data URI を受け、
    /// <see cref="ImageCacheService"/> に Kind=VideoThumb で保存する。
    /// 保存完了後、sender WebView2 に <c>videoCacheState</c> を push して slot に thumb URL を伝える。</summary>
    private static void HandleVideoThumbnailCache(object sender, JsonElement payload)
    {
        if (!payload.TryGetProperty("url",     out var urlProp))     return;
        if (!payload.TryGetProperty("dataUri", out var dataUriProp)) return;
        var url     = urlProp.GetString();
        var dataUri = dataUriProp.GetString();
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(dataUri)) return;

        if (Application.Current is not App app) return;
        var cache = app.ImageCacheServiceInstance;
        if (cache is null) return;

        // data:image/jpeg;base64,<base64-payload>
        var commaIdx = dataUri.IndexOf(',');
        if (commaIdx < 0) return;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(dataUri[(commaIdx + 1)..]); }
        catch (Exception ex) { Debug.WriteLine($"[VideoThumbCache] base64 decode failed: {ex.Message}"); return; }

        // SaveAsync は Stream を消費するので、MemoryStream に積んで投げる。
        // 保存完了後に sender WebView2 へ state を push (= スロットに thumb 表示を反映)。
        var ms = new System.IO.MemoryStream(bytes);
        _ = SaveThenPushStateAsync(sender, cache, url!, ms);

        static async System.Threading.Tasks.Task SaveThenPushStateAsync(object sender, ImageCacheService cache, string url, System.IO.MemoryStream ms)
        {
            try
            {
                await cache.SaveAsync(url, ms, "image/jpeg", ChBrowser.Services.Image.CacheKind.VideoThumb).ConfigureAwait(true);
            }
            catch (Exception ex) { Debug.WriteLine($"[VideoThumbCache] SaveAsync failed: {ex.Message}"); }
            PushVideoCacheStateTo(sender, cache, url);
        }
    }

    /// <summary>JS が動画スロット表示時/クリック時に「この URL のキャッシュ状態を教えてほしい」と問い合わせるメッセージ。
    /// 状態 = (hasThumb, hasVideo, thumbUrl?, videoUrl?, downloading) を返信する。</summary>
    private static void HandleVideoCacheQuery(object sender, JsonElement payload)
    {
        if (!payload.TryGetProperty("url", out var urlProp)) return;
        var url = urlProp.GetString();
        if (string.IsNullOrEmpty(url)) return;

        if (Application.Current is not App app) return;
        var cache = app.ImageCacheServiceInstance;
        if (cache is null) return;
        PushVideoCacheStateTo(sender, cache, url);
    }

    /// <summary>JS から「DL を開始してほしい」要求。<see cref="VideoDownloadManager.Request"/> を呼ぶ。
    /// 完了/失敗イベントは <see cref="WireVideoDownloadCompletionToPane"/> で sender にプッシュされる。</summary>
    private void HandleVideoDownloadStart(object sender, JsonElement payload)
    {
        if (!payload.TryGetProperty("url", out var urlProp)) return;
        var url = urlProp.GetString();
        if (string.IsNullOrEmpty(url)) return;

        if (Application.Current is not App app) return;
        var mgr = app.VideoDownloadManagerInstance;
        if (mgr is null) return;

        // 失敗状態のリセットは JS 側が先に mediaSlotRetry メッセージを送る前提 (Step F で統一)。
        // ここではダウンロード kick だけに専念。

        // 完了通知を sender に届けるため、URL ごとに待機 sender を覚えておく。
        // 既に DL 中なら Request() は no-op で false を返すが、最後の待機 sender に上書きすればよい
        // (= 同 URL の slot が複数 WebView2 にあった場合は最後のクリックの WebView2 に state push される。
        //   Phase 5 では十分な妥協、Phase 6+ で全 WebView2 broadcast に拡張予定)。
        _pendingDownloadSenders[url] = sender;
        mgr.Request(url);
    }

    /// <summary>UrlContextMenu「キャッシュ削除」項目クリック。
    /// 対象 URL の VideoThumb + Video キャッシュエントリ + DL 失敗状態をクリアし、
    /// state push でスロットを「未DL」状態に戻す。</summary>
    private void DeleteVideoCache_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        var owner = ItemsControl.ItemsControlFromItemContainer(mi) as ContextMenu
                  ?? mi.Parent as ContextMenu;
        if (owner?.Tag is not string url || string.IsNullOrEmpty(url)) return;

        if (Application.Current is not App app) return;
        var cache = app.ImageCacheServiceInstance;
        if (cache is null) return;

        cache.Delete(url, ChBrowser.Services.Image.CacheKind.VideoThumb);
        cache.Delete(url, ChBrowser.Services.Image.CacheKind.Video);
        app.VideoDownloadManagerInstance?.ResetFailedState(url);

        // state push で UI を未DL状態に戻す。push 先はメニューを開いた WebView2 (= owner.PlacementTarget)。
        if (owner.PlacementTarget is WebView2 wv)
        {
            PushVideoCacheStateTo(wv, cache, url);
        }
    }

    /// <summary>進行中 DL に対する応答先 WebView2 のマップ。
    /// <see cref="VideoDownloadManager.DownloadCompleted"/> 発火時に該当 sender に <c>videoCacheState</c> を push する。
    /// pane 単位で持つ (= 同 URL の DL 完了通知は最後にこの pane で要求した WebView2 に届く)。
    /// クロスペイン broadcast は Phase 6+ で検討。</summary>
    private readonly System.Collections.Generic.Dictionary<string, object> _pendingDownloadSenders = new();

    /// <summary>VideoDownloadManager のイベントをこのペインに配線する (Loaded 時 1 回)。
    /// 完了通知を受けたら <see cref="_pendingDownloadSenders"/> から対象 WebView2 を引いて state push。</summary>
    private void WireVideoDownloadCompletionToPane()
    {
        if (Application.Current is not App app) return;
        var mgr = app.VideoDownloadManagerInstance;
        var cache = app.ImageCacheServiceInstance;
        if (mgr is null || cache is null) return;

        EventHandler<ChBrowser.Services.Media.VideoDownloadEventArgs> handler = (s, e) =>
        {
            // UI thread にディスパッチして PostWebMessageAsJson を安全に呼ぶ。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_pendingDownloadSenders.TryGetValue(e.Url, out var sender))
                {
                    _pendingDownloadSenders.Remove(e.Url);
                    PushVideoCacheStateTo(sender, cache, e.Url);
                }
            }));
        };
        mgr.DownloadCompleted += handler;
        mgr.DownloadFailed    += handler;
        // 一度配線したらアンサブスクライブはしない (= ペインはアプリ寿命と同等で問題ない想定)。
    }

    /// <summary>指定 WebView2 に「この URL の現在のキャッシュ状態」を JSON で push するヘルパ。
    /// hasThumb/hasVideo に応じて仮想ホスト URL を埋め込む。</summary>
    private static void PushVideoCacheStateTo(object senderObj, ImageCacheService cache, string url)
    {
        if (senderObj is not WebView2 wv) return;
        if (wv.CoreWebView2 is null) return;

        var hasThumb = cache.TryGet(url, out var thumbHit, ChBrowser.Services.Image.CacheKind.VideoThumb);
        var hasVideo = cache.TryGet(url, out var videoHit, ChBrowser.Services.Image.CacheKind.Video);
        string? thumbUrl = hasThumb ? cache.BuildVirtualHostUrl(thumbHit) : null;
        string? videoUrl = hasVideo ? cache.BuildVirtualHostUrl(videoHit) : null;
        // 動画本体ファイルのサイズ (bytes)。キャッシュ無しなら 0。JS 側でラベル表示に使う。
        long videoSize = hasVideo ? videoHit.Size : 0L;

        var downloading        = false;
        var downloadFailed     = false;
        var thumbExtractFailed = false;
        if (Application.Current is App app && app.VideoDownloadManagerInstance is { } mgr)
        {
            downloading        = mgr.IsDownloading(url);
            downloadFailed     = mgr.IsFailed(url);
            thumbExtractFailed = mgr.IsThumbFailed(url);
        }

        var json = JsonSerializer.Serialize(new
        {
            type = "videoCacheState",
            url,
            hasThumb,
            hasVideo,
            thumbUrl,
            videoUrl,
            videoSize,
            downloading,
            downloadFailed,
            thumbExtractFailed,
        });
        try { wv.CoreWebView2.PostWebMessageAsJson(json); }
        catch (Exception ex) { ChBrowser.Services.Logging.LogService.Instance.Write($"[VideoCache] push failed: {ex.Message}"); }
    }

    // ---- 5ch.io スレ URL ホバー時のプレビューポップアップ (Phase 25) ----

    private void HandleThreadPreviewRequest(object sender, JsonElement payload)
    {
        if (sender is not WebView2 wv) return;
        if (DataContext is not MainViewModel main) return;
        if (!payload.TryGetProperty("host", out var hostProp)) return;
        if (!payload.TryGetProperty("dir",  out var dirProp))  return;
        if (!payload.TryGetProperty("key",  out var keyProp))  return;
        var host = hostProp.GetString();
        var dir  = dirProp.GetString();
        var key  = keyProp.GetString();
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(key)) return;
        var postNo = payload.TryGetProperty("postNumber", out var nProp) && nProp.ValueKind == JsonValueKind.Number
                   ? nProp.GetInt32() : 0;
        var requestId = payload.TryGetProperty("requestId", out var rProp) ? rProp.GetString() ?? "" : "";

        _ = ReplyThreadPreviewAsync(main, wv, host, dir, key, postNo, requestId);
    }

    private static async Task ReplyThreadPreviewAsync(
        MainViewModel main, WebView2 wv,
        string host, string dir, string key, int postNo, string requestId)
    {
        try
        {
            var preview = await main.LoadThreadPreviewAsync(host, dir, key, postNo).ConfigureAwait(true);
            if (wv.CoreWebView2 is null) return;
            var json = JsonSerializer.Serialize(new
            {
                type        = "threadPreview",
                requestId,
                host,
                dir,
                key,
                postNumber  = preview.PostNumber,
                ok          = preview.Ok,
                title       = preview.Title,
                body        = preview.Body,
                name        = preview.Name,
                dateText    = preview.DateText,
                error       = preview.Error,
            });
            wv.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ThreadPreview] reply failed: {ex.Message}");
        }
    }

    // ---- URL (テキストリンク / 画像サムネ) 右クリックメニュー (Phase 25) ----

    /// <summary>JS から「URL リンクが右クリックされた」通知を受け、UrlContextMenu を開く。
    /// 対象 URL は ContextMenu.Tag に積んで <see cref="UrlCopy_Click"/> などから読み出す。
    /// mediaType が "image" / "video" の場合は「ビューアで開く」項目を表示 (それ以外は Collapsed)。</summary>
    private void HandleUrlContextMenu(object sender, JsonElement payload)
    {
        if (sender is not WebView2 wv) return;
        if (!payload.TryGetProperty("url", out var urlProp)) return;
        var url = urlProp.GetString();
        if (string.IsNullOrEmpty(url)) return;
        var mediaType = payload.TryGetProperty("mediaType", out var mtp) ? (mtp.GetString() ?? "") : "";

        if (TryFindResource("UrlContextMenu") is not ContextMenu menu) return;
        // 「ビューアで開く」は image / video のみ表示。
        // 「キャッシュ削除」は video のみ表示。
        // x:Shared="False" なのでこの menu インスタンス内の MenuItem を直接いじる。
        var canOpenInViewer = mediaType == "image" || mediaType == "video";
        var canDeleteCache  = mediaType == "video";
        foreach (var item in menu.Items)
        {
            if (item is not MenuItem mi || mi.Tag is not string tag) continue;
            switch (tag)
            {
                case "openInViewer":     mi.Visibility = canOpenInViewer ? Visibility.Visible : Visibility.Collapsed; break;
                case "deleteVideoCache": mi.Visibility = canDeleteCache  ? Visibility.Visible : Visibility.Collapsed; break;
            }
        }
        menu.PlacementTarget = wv;
        menu.Placement       = PlacementMode.MousePoint;
        menu.Tag             = url;
        menu.IsOpen          = true;
    }

    private void UrlCopy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        var owner = ItemsControl.ItemsControlFromItemContainer(mi) as ContextMenu
                  ?? mi.Parent as ContextMenu;
        if (owner?.Tag is string url && !string.IsNullOrEmpty(url))
        {
            try { Clipboard.SetText(url); }
            catch (Exception ex) { Debug.WriteLine($"[UrlCopy] Clipboard.SetText failed: {ex.Message}"); }
        }
    }

    /// <summary>UrlContextMenu の「ビューアで開く」項目クリック。
    /// 対象 URL を画像ビューアウィンドウの新タブで開く (動画 URL は viewer.js 側で &lt;video&gt; レンダリング)。</summary>
    private void OpenInViewer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        var owner = ItemsControl.ItemsControlFromItemContainer(mi) as ContextMenu
                  ?? mi.Parent as ContextMenu;
        if (owner?.Tag is not string url || string.IsNullOrEmpty(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;
        if (Application.Current is App app) app.ShowImageInViewer(url);
    }

    // ---- レス番号 (post-no) コンテキストメニュー (Phase 25 で HTML から WPF ネイティブに移行) ----

    /// <summary>レス番号メニューに乗せる対象レスの情報。
    /// JS の postNoContextMenu ペイロードから組み立て、ContextMenu.DataContext に積んで
    /// 各 MenuItem の Click ハンドラから読み取る (= ネスト MenuItem でも DataContext 継承で届く)。</summary>
    private sealed record PostNoMenuContext(
        WebView2 Wv, int Number, string Name, string Id, string Watchoi, bool IsOwn);

    /// <summary>JS から「post-no がクリック / 右クリックされた」通知を受け、PostNoContextMenu を開く。
    /// PlacementMode.MousePoint でカーソル位置に出す (= 既存タブ右クリックメニューと同じ流儀)。</summary>
    private void HandlePostNoContextMenu(object sender, JsonElement payload)
    {
        if (sender is not WebView2 wv) return;
        if (!payload.TryGetProperty("number", out var nProp) || !nProp.TryGetInt32(out var num)) return;
        var name    = payload.TryGetProperty("name",    out var npp) ? (npp.GetString() ?? "") : "";
        var id      = payload.TryGetProperty("id",      out var ipp) ? (ipp.GetString() ?? "") : "";
        var watchoi = payload.TryGetProperty("watchoi", out var wpp) ? (wpp.GetString() ?? "") : "";
        var isOwn   = payload.TryGetProperty("isOwn",   out var opp) && opp.ValueKind == JsonValueKind.True;

        if (TryFindResource("PostNoContextMenu") is not ContextMenu menu) return;
        menu.PlacementTarget = wv;
        menu.Placement       = PlacementMode.MousePoint;
        menu.DataContext     = new PostNoMenuContext(wv, num, name, id, watchoi, isOwn);
        menu.IsOpen          = true;
    }

    /// <summary>メニューが開く瞬間に、対象レスの状態に合わせて項目の Header / IsEnabled を書き換える。
    /// - 「自分の書き込みにする / 解除」label をトグル
    /// - NG サブ項目に値を埋め (例: "名前 — 〜")、値が空のときは IsEnabled=false でグレーアウト</summary>
    private void PostNoContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm) return;
        if (cm.DataContext is not PostNoMenuContext ctx) return;

        foreach (var mi in TabClickHelper.EnumerateAllMenuItems(cm))
        {
            switch (mi.Tag as string)
            {
                case "own":
                    mi.Header = ctx.IsOwn ? "自分の書き込み解除" : "自分の書き込みにする";
                    break;
                case "ngName":
                    mi.Header    = "名前 — "       + (string.IsNullOrEmpty(ctx.Name)    ? "(空)"   : ctx.Name);
                    mi.IsEnabled = !string.IsNullOrEmpty(ctx.Name);
                    break;
                case "ngId":
                    mi.Header    = "ID — "         + (string.IsNullOrEmpty(ctx.Id)      ? "(空)"   : ctx.Id);
                    mi.IsEnabled = !string.IsNullOrEmpty(ctx.Id);
                    break;
                case "ngWatchoi":
                    mi.Header    = "ワッチョイ — " + (string.IsNullOrEmpty(ctx.Watchoi) ? "(なし)" : ctx.Watchoi);
                    mi.IsEnabled = !string.IsNullOrEmpty(ctx.Watchoi);
                    break;
            }
        }
    }

    /// <summary>クリックされた MenuItem (= sender) から PostNoMenuContext を取り出すヘルパ。
    /// ContextMenu.DataContext がネスト MenuItem にも継承されるため、サブメニュー項目からも参照できる。</summary>
    private static PostNoMenuContext? PostNoCtxOf(object sender)
        => (sender as MenuItem)?.DataContext as PostNoMenuContext;

    /// <summary>「このレスに飛ぶ」: JS 側に scrollToPost を投げ、本文側 (= primary レス id="rN") に
    /// スクロールしてもらう。あわせて全ポップアップを即時閉じる (= 飛んだ先が popup に隠れないようにする)。
    /// 主用途: アンカーポップアップ内のレスから本文側の該当レスへ移動する。</summary>
    private void PostNoJump_Click(object sender, RoutedEventArgs e)
    {
        if (PostNoCtxOf(sender) is not { } ctx) return;
        if (ctx.Wv.CoreWebView2 is null) return;
        var json = JsonSerializer.Serialize(new { type = "scrollToPost", number = ctx.Number });
        ctx.Wv.CoreWebView2.PostWebMessageAsJson(json);
    }

    private void PostNoReply_Click(object sender, RoutedEventArgs e)
    {
        if (PostNoCtxOf(sender) is not { } ctx) return;
        if (DataContext is not MainViewModel main) return;
        if (ctx.Wv.DataContext is not ThreadTabViewModel tab) return;
        main.OpenReplyDialog(tab, ctx.Number);
    }

    private void PostNoToggleOwn_Click(object sender, RoutedEventArgs e)
    {
        if (PostNoCtxOf(sender) is not { } ctx) return;
        if (DataContext is not MainViewModel main) return;
        if (ctx.Wv.DataContext is not ThreadTabViewModel tab) return;
        main.ToggleOwnPost(tab, ctx.Number, !ctx.IsOwn);
    }

    private void PostNoNgName_Click(object sender, RoutedEventArgs e)
        => OpenNgQuickFromMenu(sender, "name",    c => c.Name);

    private void PostNoNgId_Click(object sender, RoutedEventArgs e)
        => OpenNgQuickFromMenu(sender, "id",      c => c.Id);

    private void PostNoNgWatchoi_Click(object sender, RoutedEventArgs e)
        => OpenNgQuickFromMenu(sender, "watchoi", c => c.Watchoi);

    private void OpenNgQuickFromMenu(object sender, string target, Func<PostNoMenuContext, string> getValue)
    {
        if (PostNoCtxOf(sender) is not { } ctx) return;
        if (DataContext is not MainViewModel main) return;
        if (ctx.Wv.DataContext is not ThreadTabViewModel tab) return;
        var value = getValue(ctx);
        if (string.IsNullOrEmpty(value)) return;
        main.OpenNgQuickAdd(tab, target, value);
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

    /// <summary>JS の <c>postOpenUrl</c> から届いた URL クリック通知を捌く。
    /// 5ch.io / bbspink.com のスレ URL なら本アプリの新タブで開き (= スレ間移動の同一アプリ完結)、
    /// それ以外 (画像 / 外部サイト等) は <see cref="Process.Start"/> でシステムブラウザに渡す。</summary>
    private void HandleOpenUrl(JsonElement payload)
    {
        if (!payload.TryGetProperty("url", out var urlProp)) return;
        var url = urlProp.GetString();
        if (string.IsNullOrEmpty(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;

        ChBrowser.Services.Logging.LogService.Instance.Write($"[openUrl] received: {url}");

        // 5ch.io / bbspink.com スレ URL は本アプリの新タブで開く。
        // AddressBarParser はアドレスバー入力用だが純粋関数なので URL 種別判定にそのまま流用できる。
        var parsed = ChBrowser.Services.Url.AddressBarParser.Parse(url);
        ChBrowser.Services.Logging.LogService.Instance.Write(
            $"[openUrl] parsed: Kind={parsed.Kind}, Host='{parsed.Host}', Dir='{parsed.Directory}', Key='{parsed.ThreadKey}'");

        if (parsed.Kind == ChBrowser.Services.Url.AddressBarTargetKind.Thread
            && DataContext is MainViewModel main)
        {
            // URL に「/<dir>/<key>/<N>」のレス番号が含まれていれば AddressBarParser が
            // parsed.PostNumber に拾ってくれる (= アドレスバー入力経路と JS クリック経路で同じ抽出)。
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[openUrl] → OpenThreadByUrlAsync(host='{parsed.Host}', dir='{parsed.Directory}', key='{parsed.ThreadKey}', scrollToPost={parsed.PostNumber})");
            _ = main.OpenThreadByUrlAsync(parsed.Host, parsed.Directory, parsed.ThreadKey, parsed.PostNumber);
            return;
        }

        ChBrowser.Services.Logging.LogService.Instance.Write($"[openUrl] → external (Process.Start)");
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
        // 受信値を in-memory に保持するだけ (= idx.json への書き出しはタブクローズ / アプリ終了時に
        // MainViewModel.FlushScrollPositionToDisk で一括して行う設計)。
        main.UpdateScrollPosition(tab.Board, tab.ThreadKey, num);
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
            var tracker = (Application.Current as App)?.MediaAcquisitionTrackerInstance;

            if (isAsync && mainWindow.UrlExpander is not null)
            {
                // 過去に SNS 展開失敗済の URL は ExpandAsync をスキップして即「クリックで再試行」経路へ。
                // (= 再起動 / 別タブで同 URL のスロットが描画されても自動再試行しない、ユーザ明示クリックで Reset)
                var preFailed = tracker?.IsFailed(url, ChBrowser.Services.Media.MediaAcquisitionKind.SnsExpand) == true;
                if (preFailed)
                {
                    // 下の "ok=false, resolvedUrl=null" 経路に流す = JS で expand-failed バッジ表示
                }
                else
                {
                    var expand = await mainWindow.UrlExpander.ExpandAsync(url).ConfigureAwait(true);
                    if (expand.IsNoMedia)
                    {
                        // 確定: ソース (= ツイート等) は存在するが画像/動画メディアが付いていない。
                        // JS 側にスロット削除を指示 (= "画像取得失敗" プレースホルダを出さず、サムネ枠ごと消す)。
                        if (wv.CoreWebView2 is null) return;
                        var noMediaJson = JsonSerializer.Serialize(new
                        {
                            type    = "imageMeta",
                            url,
                            noMedia = true,
                        });
                        wv.CoreWebView2.PostWebMessageAsJson(noMediaJson);
                        return;
                    }
                    if (expand.IsResolved) resolvedUrl = expand.Url;
                    // Unavailable: tracker に記録 (= 次回以降の同 URL 描画で自動再試行しない)。
                    else if (expand.IsUnavailable)
                    {
                        tracker?.MarkFailed(url, ChBrowser.Services.Media.MediaAcquisitionKind.SnsExpand);
                    }
                    // expand.IsUnavailable はそのまま落として下の "ok=false" 経路 (= JS で「クリックで再試行」) に出す。
                }
            }

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

            // 過去にこの URL の画像 GET が失敗していたら imageLoadFailed=true で通知
            // (= JS 側は自動 loadSlotImage をスキップして「クリックで再試行」表示)。
            // cached=true (= ローカルファイル存在) のときは fetcher 経路を通らないので失敗フラグは無視。
            var imageLoadFailed = !cached && tracker is not null
                && tracker.IsFailed(actualUrl, ChBrowser.Services.Media.MediaAcquisitionKind.Image);

            var json = JsonSerializer.Serialize(new
            {
                type        = "imageMeta",
                url,
                resolvedUrl,
                ok,
                size,
                cached,
                imageLoadFailed,
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

    /// <summary>「板を開く」: スレが属する板のスレ一覧タブを開く (既存タブがあればアクティブ化)。
    /// アドレスバーから直接スレを開いた経路で「親板に戻りたい」ケース用。</summary>
    private void ThreadTabOpenBoard_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf<ThreadTabViewModel>(sender) is not { } tab) return;
        if (DataContext is not MainViewModel main) return;
        _ = main.OpenBoardByUrlAsync(tab.Board.Host, tab.Board.DirectoryName);
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
