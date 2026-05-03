using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ChBrowser.Models;
using ChBrowser.ViewModels;
using ChBrowser.Views;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ChBrowser;

/// <summary>3 ペイン (板一覧 / お気に入り / スレ一覧) と スレ表示 WebView2 からの postMessage 受信ハンドラと、
/// それに付随するコンテキストメニュー popup 処理を集約した partial。
///
/// メッセージ種別ごとの分岐は各 <c>WebMessageReceived</c> 内に書かれている (= type で switch / if 列挙)。
/// shortcut / gesture / gestureProgress / gestureEnd / bridgeReady の 4 系統は各ペイン共通なので
/// <see cref="DispatchFromWebView"/> / <see cref="RouteGestureProgress"/> / <see cref="PushBindingsTo"/>
/// に共通化してある。</summary>
public partial class MainWindow
{
    // -----------------------------------------------------------------
    // 共通: 4 系統 (shortcut / gesture / gestureProgress / gestureEnd / bridgeReady) のディスパッチ
    // -----------------------------------------------------------------

    /// <summary>WebView2 が受け取ったメッセージ JSON のうち、4 共通系統 (shortcut / gesture / gestureProgress /
    /// gestureEnd / bridgeReady) を dispatch する。これらが消費されたら true を返す (呼び元はその時点で return)。</summary>
    private static bool TryDispatchCommonMessage(object? sender, string type, JsonElement payload, string category)
    {
        if (type == "shortcut" || type == "gesture")
        {
            DispatchFromWebView(payload, category);
            return true;
        }
        if (type == "gestureProgress" || type == "gestureEnd")
        {
            RouteGestureProgress(payload, type, category);
            return true;
        }
        if (type == "bridgeReady")
        {
            PushBindingsTo(sender, category);
            return true;
        }
        return false;
    }

    /// <summary>WebView の JS ブリッジから受信した shortcut / gesture メッセージを ShortcutManager にルーティング。
    /// category は呼び出し元 (= どの WebView の WebMessageReceived か) で固定する。</summary>
    private static void DispatchFromWebView(JsonElement payload, string category)
    {
        if (Application.Current is not App app || app.ShortcutManager is not { } mgr) return;
        var descriptor = payload.TryGetProperty("descriptor", out var dp) ? dp.GetString() : null;
        if (!string.IsNullOrEmpty(descriptor)) mgr.Dispatch(category, descriptor);
    }

    /// <summary>WebView の JS ブリッジから受信したジェスチャー進捗 (gestureProgress / gestureEnd) をルーティング。</summary>
    private static void RouteGestureProgress(JsonElement payload, string type, string category)
    {
        if (Application.Current is not App app || app.ShortcutManager is not { } mgr) return;
        if (type == "gestureEnd")
        {
            mgr.NotifyGestureProgress(null, null);
            return;
        }
        var value = payload.TryGetProperty("value", out var vp) ? vp.GetString() : "";
        mgr.NotifyGestureProgress(category, value ?? "");
    }

    /// <summary>bridgeReady 受信時に、その WebView だけに setShortcutBindings を direct push する。
    /// PaneShortcutsJson は値変化が無いとき再 push されないため、bridge 側 (= 新規 navigation 後の JS) が
    /// 「初期化完了したよ」と通知してきたら必ずここで補完する。</summary>
    private static void PushBindingsTo(object? sender, string category)
    {
        if (Application.Current is not App app || app.ShortcutManager is not { } mgr) return;
        if (sender is not WebView2 wv || wv.CoreWebView2 is null) return;
        var map = mgr.GetBindingsForCategory(category);
        var json = JsonSerializer.Serialize(new
        {
            type     = "setShortcutBindings",
            bindings = map,
        });
        try { wv.CoreWebView2.PostWebMessageAsJson(json); }
        catch (Exception ex) { Debug.WriteLine($"[MainWindow] PushBindingsTo failed: {ex.Message}"); }
    }

    /// <summary>WebMessage を JSON として読んで (type, ルート要素) を返す。</summary>
    private static (string Type, JsonElement Root) TryParseMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            if (string.IsNullOrEmpty(json)) return ("", default);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.Clone(); // dispose 後も使えるようコピー
            var type = root.TryGetProperty("type", out var typeProp) ? (typeProp.GetString() ?? "") : "";
            return (type, root);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebMessage] parse failed: {ex.Message}");
            return ("", default);
        }
    }

    // -----------------------------------------------------------------
    // 板一覧 WebView2 (Phase 14a)
    // -----------------------------------------------------------------

    /// <summary>板一覧 WebView の右クリックメニューに乗せる対象板。</summary>
    private sealed record BoardRef(string Host, string DirectoryName, string BoardName);

    /// <summary>板一覧 WebView2 からの postMessage を捌く。
    /// openBoard / setCategoryExpanded / contextMenu (target=board) を受ける。</summary>
    private async void BoardListWebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        var (type, payload) = TryParseMessage(e);
        if (TryDispatchCommonMessage(sender, type, payload, "板一覧ペイン")) return;

        switch (type)
        {
            case "openBoard":
            {
                var host = payload.TryGetProperty("host", out var hp) ? hp.GetString() : null;
                var dir  = payload.TryGetProperty("directoryName", out var dp) ? dp.GetString() : null;
                var name = payload.TryGetProperty("name", out var np) ? np.GetString() : "";
                if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(dir)) return;
                await main.OpenBoardFromHtmlListAsync(host, dir, name ?? "");
                break;
            }
            case "setCategoryExpanded":
            {
                var cat = payload.TryGetProperty("categoryName", out var cp) ? cp.GetString() : null;
                var exp = payload.TryGetProperty("expanded",     out var ep) && ep.GetBoolean();
                if (!string.IsNullOrEmpty(cat)) main.SetCategoryExpanded(cat, exp);
                break;
            }
            case "contextMenu":
            {
                var target = payload.TryGetProperty("target", out var tp) ? tp.GetString() : null;
                if (target == "board")
                {
                    var host = payload.TryGetProperty("host", out var hp) ? hp.GetString() : null;
                    var dir  = payload.TryGetProperty("directoryName", out var dp) ? dp.GetString() : null;
                    var name = payload.TryGetProperty("name", out var np) ? np.GetString() : "";
                    if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(dir)) return;
                    ShowBoardContextMenu(host, dir, name ?? "");
                }
                break;
            }
        }
    }

    /// <summary>Window.Resources の "BoardContextMenu" を取り出して、選択された板情報を Tag に積んで popup する。</summary>
    private void ShowBoardContextMenu(string host, string directoryName, string boardName)
    {
        if (Resources["BoardContextMenu"] is not ContextMenu menu) return;
        menu.Tag             = new BoardRef(host, directoryName, boardName);
        menu.PlacementTarget = BoardListWebView;
        menu.Placement       = PlacementMode.MousePoint;
        menu.IsOpen          = true;
    }

    /// <summary>板一覧 WebView の右クリックメニューから「お気に入りに追加」が選ばれたとき。</summary>
    private void AddBoardToFavorites_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        if (sender is not MenuItem mi) return;
        var owner = ItemsControl.ItemsControlFromItemContainer(mi) as ContextMenu
                  ?? mi.Parent as ContextMenu;
        if (owner?.Tag is not BoardRef br) return;
        main.AddBoardToFavoritesByHostDir(br.Host, br.DirectoryName, br.BoardName);
    }

    // -----------------------------------------------------------------
    // スレ一覧 WebView2 (Phase 14a 同等の WebView 化)
    // -----------------------------------------------------------------

    /// <summary>スレ一覧 WebView2 から行ダブルクリック / paneActivated 通知を受け取る。
    /// JS は host/dir/key/title をすべて payload に乗せてくるので、お気に入りディレクトリ展開タブ
    /// (= 行ごとに異なる板由来のスレが混じる) でも板由来タブと同じハンドラで対応できる。</summary>
    private async void ThreadListWebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        var (type, payload) = TryParseMessage(e);

        if (type == "paneActivated") { main.MarkThreadListPaneActive(); return; }
        if (TryDispatchCommonMessage(sender, type, payload, "スレ一覧表示領域")) return;

        if (type != "openThread") return;

        var host  = payload.TryGetProperty("host",          out var hp) ? hp.GetString() : null;
        var dir   = payload.TryGetProperty("directoryName", out var dp) ? dp.GetString() : null;
        var key   = payload.TryGetProperty("key",           out var kp) ? kp.GetString() : null;
        var title = payload.TryGetProperty("title",         out var tp) ? tp.GetString() : null;

        // logState: 一覧側で表示していたマーク状態 (None=0, Cached=1, Updated=2, Dropped=3)。
        // dat 落ちスレの 404 で「(取得失敗)」表示にならないよう、ここでヒントを伝搬する。
        LogMarkState? hint = null;
        if (payload.TryGetProperty("logState", out var lp) && lp.TryGetInt32(out var li)
            && Enum.IsDefined(typeof(LogMarkState), li))
        {
            hint = (LogMarkState)li;
        }

        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(key)) return;
        await main.OpenThreadFromListAsync(host, dir, key, title ?? "", hint);
    }

    // -----------------------------------------------------------------
    // お気に入りペイン WebView2 (Phase 14b)
    // -----------------------------------------------------------------
    //
    // ダブルクリック / 右クリック / D&D は すべて JS の postMessage で受け取る。
    // ContextMenu は Window.Resources に定義したものを Tag に対象 id (FavoriteRef) を積んで popup。
    // クリックハンドラは Tag から id を取り出して MainViewModel の id ベース API を呼ぶ。

    /// <summary>右クリック対象を一意に表す (Window.Resources の ContextMenu の Tag に積む)。</summary>
    private sealed record FavoriteRef(Guid Id);

    /// <summary>JS から送られた id 文字列を Guid にパース (失敗時 null)。</summary>
    private static Guid? ParseId(string? s)
        => Guid.TryParse(s, out var g) ? g : null;

    /// <summary>ContextMenu (sender の親) の Tag から FavoriteRef を取り出す共通処理。</summary>
    private static FavoriteRef? RefFromMenu(object sender)
    {
        if (sender is not MenuItem mi) return null;
        var owner = ItemsControl.ItemsControlFromItemContainer(mi) as ContextMenu
                  ?? mi.Parent as ContextMenu;
        return owner?.Tag as FavoriteRef;
    }

    private async void FavoritesWebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        var (type, payload) = TryParseMessage(e);
        if (TryDispatchCommonMessage(sender, type, payload, "お気に入りペイン")) return;

        switch (type)
        {
            case "openFavorite":
            {
                var idStr = payload.TryGetProperty("id", out var p) ? p.GetString() : null;
                if (ParseId(idStr) is Guid id) await main.OpenFavoriteByIdAsync(id);
                break;
            }
            case "openAllLogs":
            {
                await main.OpenAllLogsAsync();
                break;
            }
            case "setFolderExpanded":
            {
                var idStr = payload.TryGetProperty("id", out var p) ? p.GetString() : null;
                var exp   = payload.TryGetProperty("expanded", out var ep) && ep.GetBoolean();
                if (ParseId(idStr) is Guid id) main.SetFolderExpanded(id, exp);
                break;
            }
            case "moveFavorite":
            {
                var srcStr = payload.TryGetProperty("sourceId", out var sp) ? sp.GetString() : null;
                var tgtStr = payload.TryGetProperty("targetId", out var tp) ? tp.GetString() : null;
                var pos    = payload.TryGetProperty("position", out var pp) ? pp.GetString() : "after";
                if (ParseId(srcStr) is Guid src)
                    main.MoveFavoriteByIds(src, ParseId(tgtStr), pos ?? "after");
                break;
            }
            case "contextMenu":
            {
                var target = payload.TryGetProperty("target", out var tp) ? tp.GetString() : null;
                var idStr  = payload.TryGetProperty("id",     out var ip) ? ip.GetString() : null;
                ShowFavoriteContextMenu(target, ParseId(idStr));
                break;
            }
        }
    }

    /// <summary>対象種別に応じて Window.Resources から ContextMenu を取り出して popup。</summary>
    private void ShowFavoriteContextMenu(string? target, Guid? id)
    {
        var key = target switch
        {
            "folder"       => "FavoriteFolderContextMenu",
            "board"        => "FavoriteBoardContextMenu",
            "thread"       => "FavoriteThreadContextMenu",
            "virtual-root" => "FavoriteVirtualRootContextMenu",
            _              => "FavoriteRootContextMenu",
        };
        if (Resources[key] is not ContextMenu menu) return;
        menu.Tag             = id is Guid g ? new FavoriteRef(g) : null;
        menu.PlacementTarget = FavoritesWebView;
        menu.Placement       = PlacementMode.MousePoint;
        menu.IsOpen          = true;
    }

    private void FavNewFolderHere_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        if (RefFromMenu(sender) is not FavoriteRef r) return;
        if (main.Favorites.FindById(r.Id) is not FavoriteFolderViewModel parent) return;
        var name = InputDialog.Prompt(this, "新規フォルダ", $"「{parent.DisplayName}」配下のフォルダ名:", "新規フォルダ");
        if (string.IsNullOrWhiteSpace(name)) return;
        main.NewFavoriteFolder(parent, name.Trim());
    }

    private void FavNewFolderRoot_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        var name = InputDialog.Prompt(this, "新規フォルダ", "ルート直下のフォルダ名:", "新規フォルダ");
        if (string.IsNullOrWhiteSpace(name)) return;
        main.NewFavoriteFolder(null, name.Trim());
    }

    private void FavRename_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        if (RefFromMenu(sender) is not FavoriteRef r) return;
        if (main.Favorites.FindById(r.Id) is not FavoriteFolderViewModel folder) return;
        var name = InputDialog.Prompt(this, "名前変更", "新しいフォルダ名:", folder.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        main.RenameFavoriteFolder(folder, name.Trim());
    }

    private void FavDelete_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        if (RefFromMenu(sender) is not FavoriteRef r) return;
        if (main.Favorites.FindById(r.Id) is not FavoriteEntryViewModel vm) return;

        var confirmMessage = vm switch
        {
            FavoriteFolderViewModel f => $"フォルダ「{f.DisplayName}」とその中身をすべて削除しますか?",
            _                         => $"「{vm.DisplayName}」をお気に入りから削除しますか?",
        };
        var result = MessageBox.Show(this, confirmMessage, "削除確認",
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (result != MessageBoxResult.OK) return;
        main.DeleteFavoriteEntry(vm);
    }

    private void FavMoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        if (RefFromMenu(sender) is not FavoriteRef r) return;
        if (main.Favorites.FindById(r.Id) is FavoriteEntryViewModel vm)
            main.MoveFavoriteEntryUp(vm);
    }

    private void FavMoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        if (RefFromMenu(sender) is not FavoriteRef r) return;
        if (main.Favorites.FindById(r.Id) is FavoriteEntryViewModel vm)
            main.MoveFavoriteEntryDown(vm);
    }

    /// <summary>「お気に入りチェック」: 仮想ルート / ファイルメニュー / お気に入りペインのリフレッシュボタン
    /// すべてここから入る。<see cref="MainViewModel.CheckFavoritesAsync"/> を fire-and-forget。</summary>
    private void FavCheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        _ = main.CheckFavoritesAsync();
    }

    /// <summary>「すべて開く」: 仮想ルート (Tag null) ならお気に入り全体、フォルダなら配下を、
    /// 板はそのまま板タブ、スレはそのままスレタブで一気に開く。</summary>
    private void FavOpenAll_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        if (RefFromMenu(sender) is FavoriteRef r)
        {
            if (main.Favorites.FindById(r.Id) is FavoriteFolderViewModel folder)
                _ = main.OpenAllInFolderAsync(folder);
        }
        else
        {
            _ = main.OpenAllInRootAsync();
        }
    }

    /// <summary>「板として開く」: 仮想ルート / フォルダ配下の全エントリを統合した板スレ一覧タブで開く。</summary>
    private void FavOpenAsBoard_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        if (RefFromMenu(sender) is FavoriteRef r)
        {
            if (main.Favorites.FindById(r.Id) is FavoriteFolderViewModel folder)
                _ = main.OpenFavoritesFolderAsync(folder);
        }
        else
        {
            _ = main.OpenAllRootAsBoardAsync();
        }
    }

    // -----------------------------------------------------------------
    // スレ表示 WebView2 (Phase 5+)
    // -----------------------------------------------------------------

    /// <summary>スレ表示 WebView2 からの postMessage を捌く。
    /// openUrl / scrollPosition / readMark / imageMetaRequest / openInViewer (Phase 10/19)。</summary>
    private void ThreadViewWebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var (type, payload) = TryParseMessage(e);

        if (type == "paneActivated")
        {
            if (DataContext is MainViewModel main) main.MarkThreadPaneActive();
            return;
        }
        if (TryDispatchCommonMessage(sender, type, payload, "スレッド表示領域")) return;

        switch (type)
        {
            case "openUrl":            HandleOpenUrl(payload); break;
            case "scrollPosition":     HandleScrollPosition(sender, payload); break;
            case "readMark":           HandleReadMark(sender, payload); break;
            case "imageMetaRequest":   HandleImageMetaRequest(sender, payload); break;
            case "openInViewer":       HandleOpenInViewer(payload); break;
        }
    }

    /// <summary>ロード済み画像クリック → 画像ビューアウィンドウに送る (Phase 10)。</summary>
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

    /// <summary>JS が検出した「ここまで読んだ」レス番号を MainViewModel に通知 (Phase 19)。</summary>
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

    /// <summary>JS から「この URL の HEAD サイズを教えて」を受け取り、HEAD 結果を imageMeta メッセージで返す。
    /// JS 側はサイズしきい値 (5MB) で自動ロードを止めるかプレースホルダ表示するかを決める。</summary>
    private void HandleImageMetaRequest(object sender, JsonElement payload)
    {
        if (sender is not WebView2 wv) return;
        if (ImageMetaService is null) return;
        if (!payload.TryGetProperty("url", out var urlProp)) return;
        var url = urlProp.GetString();
        if (string.IsNullOrEmpty(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;

        _ = ReplyImageMetaAsync(wv, url);
    }

    private async Task ReplyImageMetaAsync(WebView2 wv, string url)
    {
        try
        {
            // x.com / pixiv 等の非同期展開対象ならまず実体画像 URL に展開する。
            // 展開失敗 (媒体無し / ログイン要 / API エラー) は resolvedUrl=null + ok=false で返す。
            string? resolvedUrl = null;
            var isAsync = ChBrowser.Services.Image.UrlExpander.IsAsyncExpandable(url);
            if (isAsync && UrlExpander is not null)
                resolvedUrl = await UrlExpander.ExpandAsync(url).ConfigureAwait(true);

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
                // ローカルキャッシュにあれば HEAD はスキップして cached: true で返す。
                cached = ImageCacheService?.Contains(actualUrl) ?? false;
                if (cached)
                {
                    ok = true;
                }
                else if (ImageMetaService is not null)
                {
                    var meta = await ImageMetaService.GetAsync(actualUrl).ConfigureAwait(true);
                    ok   = meta.Ok;
                    size = meta.Size;
                }
                else
                {
                    ok = false;
                }
            }

            if (wv.CoreWebView2 is null) return; // WebView2 が破棄済みなら早期 return
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
}
