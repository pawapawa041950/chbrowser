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
        }
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
    /// 対象 URL は ContextMenu.Tag に積んで <see cref="UrlCopy_Click"/> などから読み出す。</summary>
    private void HandleUrlContextMenu(object sender, JsonElement payload)
    {
        if (sender is not WebView2 wv) return;
        if (!payload.TryGetProperty("url", out var urlProp)) return;
        var url = urlProp.GetString();
        if (string.IsNullOrEmpty(url)) return;

        if (TryFindResource("UrlContextMenu") is not ContextMenu menu) return;
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

            if (isAsync && mainWindow.UrlExpander is not null)
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
                // expand.IsUnavailable はそのまま落として下の "ok=false" 経路 (= JS で「クリックで再試行」) に出す。
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
