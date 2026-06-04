using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ChBrowser.Models;
using ChBrowser.Services.WebView2;
using ChBrowser.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ChBrowser.Views.Panes;

/// <summary>スレッド一覧ペイン (Phase 23 抽出)。
/// ヘッダ + 新規スレ立てボタン + タブストリップ + 全 ThreadListTabViewModel の WebView2 を ItemsControl で並列保持。
/// 旧 MainWindow の ThreadListPane の役割を全て持ち込む。</summary>
public partial class ThreadListPane : UserControl
{
    public ThreadListPane()
    {
        InitializeComponent();
        ChBrowser.Controls.PaneDragInitiator.Attach(HeaderBar, ChBrowser.Models.PaneId.ThreadList);
    }

    /// <summary>このペインの DataContext (= 担当するスレ一覧グループ, 複数ペイン化)。</summary>
    private ThreadListPaneGroupViewModel? Group => DataContext as ThreadListPaneGroupViewModel;

    /// <summary>アプリ全体の ViewModel (= Group.Main)。横断操作 / 共有設定の参照に使う。</summary>
    private MainViewModel? Vm => (DataContext as ThreadListPaneGroupViewModel)?.Main;

    // ---- ペインフォーカス → ViewModel に通知 (このペインを MRU アクティブにする) ----

    private void Pane_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Group is { } g) g.Main.MarkThreadListPaneActive(g);
    }

    private void Pane_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Group is { } g) g.Main.MarkThreadListPaneActive(g);
    }

    // ---- ヘッダのボタン ----

    private void NewThreadButton_Click(object sender, RoutedEventArgs e)
    {
        Vm?.OpenNewThreadDialog();
    }

    /// <summary>選択中のスレ一覧タブを再取得する。タブ種別 (板 / 全ログ / お気に入り) を問わず
    /// MainViewModel.RefreshThreadListTabAsync が適切に振り分ける。タブ未選択なら no-op。</summary>
    private void RefreshThreadListButton_Click(object sender, RoutedEventArgs e)
    {
        // このペインの選択タブを更新する (= ボタンは各ペインのヘッダにあるので自ペイン基準)。
        if (Group is not { } g || Vm is not { } main) return;
        if (g.SelectedTab is null) return;
        _ = main.RefreshThreadListTabAsync(g.SelectedTab);
    }

    /// <summary>選択中の板タブの板をお気に入りに追加 / 削除する (トグル)。
    /// 板タブ以外 (お気に入り展開タブ / 全ログタブ) では XAML 側で IsEnabled=False になっているため呼ばれない。</summary>
    private void ToggleBoardFavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (Group is not { } g || Vm is not { } main) return;
        if (g.SelectedTab?.Board is not { } board) return;
        main.ToggleBoardFavorite(board);
    }

    // ---- WebView2 のライフサイクル管理 ----

    /// <summary>スレ一覧タブが ThreadListTabs から削除されると、ItemsControl が対応 DataTemplate コンテナを
    /// 可視ツリーから外し Unloaded が発火する。WebView2 は native HWND / レンダラプロセスを抱える
    /// IDisposable なので、ここで明示 Dispose しないと「WebView2: ChBrowser thread list」プロセスが
    /// GC / ファイナライザ任せで残存し、タブを閉じてもメモリ・プロセスが解放されない
    /// (= スレ表示ペイン側には元々あった処理が、スレ一覧ペイン側に欠落していた)。
    /// ThreadDisplayPane.ThreadViewWebView_Unloaded と同じ流儀:
    /// DataContext が ThreadListTabs にまだ存在する場合のみ一時的 unload (= ペイン構造の再構成等) と
    /// みなして Dispose しない (= 偽陽性で動いている WebView2 を壊さない)。</summary>
    private void ThreadListWebView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not WebView2 wv) return;
        var ctx = wv.DataContext as ThreadListTabViewModel;
        // このタブが "このペインの" タブ集合にまだ居れば一時的 unload とみなし Dispose しない。
        // 居なければ (閉じた / 別ペインへ移動) このペインの WebView2 は不要なので Dispose する (複数ペイン化)。
        if (ctx is not null && Group is { } g && g.Tabs.Contains(ctx)) return;
        try { wv.Dispose(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ThreadListPane] WebView2 Dispose failed: {ex.Message}"); }
    }

    // ---- WebView2 → JS メッセージ受信 ----

    private async void ThreadListWebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (Vm is not { } main || Group is not { } g) return;
        var (type, payload) = WebMessageBridge.TryParseMessage(e);

        if (type == "paneActivated") { main.MarkThreadListPaneActive(g); return; }
        if (WebMessageBridge.TryDispatchCommonMessage(sender, type, payload, "スレ一覧表示領域")) return;

        if (type == "threadListRowMenu") { ShowThreadListRowContextMenu(payload); return; }
        if (type != "openThread") return;

        var host  = payload.TryGetProperty("host",          out var hp) ? hp.GetString() : null;
        var dir   = payload.TryGetProperty("directoryName", out var dp) ? dp.GetString() : null;
        var key   = payload.TryGetProperty("key",           out var kp) ? kp.GetString() : null;
        var title = payload.TryGetProperty("title",         out var tp) ? tp.GetString() : null;

        LogMarkState? hint = null;
        if (payload.TryGetProperty("logState", out var lp) && lp.TryGetInt32(out var li)
            && Enum.IsDefined(typeof(LogMarkState), li))
        {
            hint = (LogMarkState)li;
        }

        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(key)) return;
        await main.OpenThreadFromListAsync(host, dir, key, title ?? "", hint);
    }

    // ---- スレ一覧の各行の右クリックメニュー ----

    /// <summary>JS から飛んできた行 right-click 通知を受けて、WPF 側で <see cref="ContextMenu"/> を popup する。
    /// メニュー項目クリックハンドラには <see cref="ThreadListRowContext"/> を DataContext として伝える
    /// (= タブを開かずに「お気に入り追加 / コピー / 次スレ候補検索 / ログ削除」を実行可能)。</summary>
    private void ShowThreadListRowContextMenu(System.Text.Json.JsonElement payload)
    {
        if (Vm is not { } main) return;
        var host  = payload.TryGetProperty("host",          out var hp) ? hp.GetString() : null;
        var dir   = payload.TryGetProperty("directoryName", out var dp) ? dp.GetString() : null;
        var key   = payload.TryGetProperty("key",           out var kp) ? kp.GetString() : null;
        var title = payload.TryGetProperty("title",         out var tp) ? tp.GetString() : null;
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(key)) return;

        var board = main.ResolveBoard(host, dir, "");
        var ctx   = new ThreadListRowContext(board, key, title ?? "");

        if (TryFindResource("ThreadListRowContextMenu") is not ContextMenu menu) return;
        menu.PlacementTarget = this;
        menu.Placement       = PlacementMode.MousePoint;
        menu.DataContext     = ctx;
        menu.IsOpen          = true;
    }

    /// <summary>右クリックメニューを開く瞬間に Header 文字列を「追加/削除」で動的に切替える。</summary>
    private void ThreadListRowContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm) return;
        if (cm.DataContext is not ThreadListRowContext ctx) return;
        if (Vm is not { } main) return;

        var isFav = main.Favorites.FindThread(ctx.Board.Host, ctx.Board.DirectoryName, ctx.ThreadKey) is not null;
        foreach (var item in TabClickHelper.EnumerateAllMenuItems(cm))
        {
            if ((item.Tag as string) == "fav")
                item.Header = isFav ? "お気に入りから削除" : "お気に入りに追加";
        }
    }

    private static ThreadListRowContext? CtxOf(object sender)
        => (sender as MenuItem)?.DataContext as ThreadListRowContext;

    private void ThreadListRowFav_Click(object sender, RoutedEventArgs e)
    {
        if (CtxOf(sender) is not { } ctx) return;
        Vm?.ToggleThreadFavorite(ctx.Board, ctx.ThreadKey, ctx.Title);
    }

    private void ThreadListRowCopyTitle_Click(object sender, RoutedEventArgs e)
    {
        if (CtxOf(sender) is not { } ctx) return;
        if (!string.IsNullOrEmpty(ctx.Title)) Clipboard.SetText(ctx.Title);
    }

    private void ThreadListRowCopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (CtxOf(sender) is not { } ctx) return;
        Clipboard.SetText(ThreadUrl(ctx));
    }

    private void ThreadListRowCopyTitleAndUrl_Click(object sender, RoutedEventArgs e)
    {
        if (CtxOf(sender) is not { } ctx) return;
        Clipboard.SetText($"{ctx.Title}\n{ThreadUrl(ctx)}");
    }

    private void ThreadListRowFindNext_Click(object sender, RoutedEventArgs e)
    {
        if (CtxOf(sender) is not { } ctx) return;
        if (Vm is not { } main) return;
        _ = main.OpenNextThreadSearchAsync(ctx.Board, ctx.Title, ctx.ThreadKey);
    }

    private void ThreadListRowDeleteLog_Click(object sender, RoutedEventArgs e)
    {
        if (CtxOf(sender) is not { } ctx) return;
        Vm?.DeleteThreadLog(ctx.Board, ctx.ThreadKey, ctx.Title);
    }

    /// <summary>{board, key} → 5ch.io 系の read.cgi 形式 URL を組み立てる
    /// (= <see cref="ThreadTabViewModel.Url"/> と同じ形式で揃える)。</summary>
    private static string ThreadUrl(ThreadListRowContext ctx)
        => $"https://{ctx.Board.Host}/test/read.cgi/{ctx.Board.DirectoryName}/{ctx.ThreadKey}/";

    /// <summary>スレ一覧行の右クリックメニュー操作で必要な値を 1 つに束ねた immutable record。
    /// Board は <see cref="MainViewModel.ResolveBoard"/> で解決済み (= bbsmenu 未登録の板でも fallback Board が入る)。</summary>
    private sealed record ThreadListRowContext(Board Board, string ThreadKey, string Title);

    // ---- タブの D&D 開始検出 (移動/ペイン生成の本体は MainWindow + LayoutHost が担う, 複数ペイン化) ----

    private System.Windows.Point _tabDragStartPoint;
    private ThreadListTabViewModel? _tabDragCandidate;

    private void ThreadListTabItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TabItem ti) { _tabDragCandidate = null; return; }
        // ×ボタン等の上での押下はドラッグにしない (= Command を素直に発火させる)。
        if (e.OriginalSource is DependencyObject src && TabClickHelper.FindAncestor<ButtonBase>(src) is not null)
        {
            _tabDragCandidate = null;
            return;
        }
        _tabDragStartPoint = e.GetPosition(null);
        _tabDragCandidate  = ti.DataContext as ThreadListTabViewModel;
    }

    /// <summary>押下後に閾値を超えて動いたらタブ D&D を開始する。以降の移動/ドロップは MainWindow が
    /// LayoutHost のマウスキャプチャで処理し、自ストリップ内=並べ替え / 別一覧ストリップ=移動 /
    /// ペイン本体=新一覧ペイン生成 を切り替える。</summary>
    private void ThreadListTabItem_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_tabDragCandidate is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _tabDragCandidate = null; return; }

        var pos = e.GetPosition(null);
        if (System.Math.Abs(pos.X - _tabDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            System.Math.Abs(pos.Y - _tabDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var tab = _tabDragCandidate;
        _tabDragCandidate = null;
        if (Group is null || tab is null) return;
        if (Window.GetWindow(this) is MainWindow mw) mw.BeginTabDrag(tab, Group);
    }

    // ---- タブの右クリックメニュー (中/ダブル/修飾+左 は ShortcutManager 側で dispatch) ----

    private void ThreadListTabItem_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TabItem ti) return;
        if (ti.DataContext is null) return;
        if (e.OriginalSource is DependencyObject src && TabClickHelper.FindAncestor<ButtonBase>(src) is not null) return;
        if (TryFindResource("ThreadListTabContextMenu") is not ContextMenu menu) return;

        menu.PlacementTarget = ti;
        menu.Placement       = PlacementMode.MousePoint;
        menu.DataContext     = ti.DataContext;
        menu.IsOpen          = true;
        e.Handled            = true;
    }

    // ---- 右クリックメニュー: 動的な Header / IsEnabled 切替 ----

    private void ThreadListTabContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm) return;
        if (cm.DataContext is not ThreadListTabViewModel tab) return;
        if (Vm is not { } main) return;

        var hasBoard = tab.Board is not null;
        var isFav    = tab.Board is { } b && main.Favorites.FindBoard(b.Host, b.DirectoryName) is not null;

        foreach (var item in TabClickHelper.EnumerateAllMenuItems(cm))
        {
            // openSetting も「Board が無いタブ (= お気に入りフォルダ等) では使えない」項目に該当。
            switch (item.Tag as string)
            {
                case "fav":
                    item.IsEnabled = hasBoard;
                    item.Header    = isFav ? "お気に入りから削除" : "お気に入りに追加";
                    break;
                case "copyUrl":
                case "openSetting":
                case "needsBoard":
                    item.IsEnabled = hasBoard;
                    break;
            }
        }
    }

    private static T? TabOf<T>(object sender) where T : class
        => (sender as MenuItem)?.DataContext as T;

    private void ThreadListTabFav_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf<ThreadListTabViewModel>(sender) is not { Board: { } board }) return;
        Vm?.ToggleBoardFavorite(board);
    }

    private void ThreadListTabCopyTitle_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf<ThreadListTabViewModel>(sender) is not { } tab) return;
        var title = tab.Board?.BoardName ?? tab.Header ?? "";
        if (!string.IsNullOrEmpty(title)) Clipboard.SetText(title);
    }

    private void ThreadListTabCopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf<ThreadListTabViewModel>(sender) is not { Board: { } board }) return;
        Clipboard.SetText(board.Url);
    }

    private void ThreadListTabCopyTitleAndUrl_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf<ThreadListTabViewModel>(sender) is not { Board: { } board }) return;
        Clipboard.SetText($"{board.BoardName}\n{board.Url}");
    }

    /// <summary>「本板をブラウザで開く」: 板の URL を OS 既定ブラウザで開く。
    /// Board が無いタブ (= お気に入りフォルダ等) では Opened 側で IsEnabled=false に落ちている。</summary>
    private void ThreadListTabOpenBoardInBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf<ThreadListTabViewModel>(sender) is not { Board: { } board }) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = board.Url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OpenBoardInBrowser] failed: {ex.Message}");
        }
    }

    /// <summary>「SETTING.TXTをブラウザで開く」: 板の SETTING.TXT を OS 既定ブラウザに渡す。
    /// Board が無いタブ (= お気に入りフォルダ等) では Opened 側で IsEnabled=false に落ちているので
    /// ハンドラには到達しないが、念のためここでも Board null チェックする。</summary>
    private void ThreadListTabOpenSettingTxt_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf<ThreadListTabViewModel>(sender) is not { Board: { } board }) return;
        var url = board.Url.TrimEnd('/') + "/SETTING.TXT";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OpenSettingTxt] failed: {ex.Message}");
        }
    }

    /// <summary>「SETTING.TXTの更新」: 板の SETTING.TXT をサーバから再取得して上書き保存。
    /// 取得結果はステータスバーに反映 (MainViewModel.RefreshSettingTxtAsync 側で StatusMessage を更新)。</summary>
    private void ThreadListTabRefreshSettingTxt_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf<ThreadListTabViewModel>(sender) is not { Board: { } board }) return;
        if (Vm is not { } main) return;
        _ = main.RefreshSettingTxtAsync(board);
    }
}
