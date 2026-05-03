using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ChBrowser.Models;
using ChBrowser.Services.WebView2;
using ChBrowser.ViewModels;
using ChBrowser.Views;
using Microsoft.Web.WebView2.Core;

namespace ChBrowser.Views.Panes;

/// <summary>お気に入りペイン (Phase 14b で WebView 化、Phase 23 で UserControl 抽出)。
/// 旧 MainWindow.xaml に inline 配置されていたお気に入り表示部分 + 「お気に入りチェック」ボタンを束ねる。
///
/// <para>JS の postMessage で contextMenu / openFavorite / openAllLogs / setFolderExpanded /
/// moveFavorite を受けて MainViewModel の対応 API を呼ぶ。
/// 右クリックメニューは MainWindow.Resources の "FavoriteFolderContextMenu" 等を <c>FindResource</c> で
/// 取り出して popup する (旧構成と同じ流儀)。</para></summary>
public partial class FavoritesPane : UserControl
{
    /// <summary>右クリック対象を一意に表す (ContextMenu の Tag に積む)。</summary>
    private sealed record FavoriteRef(Guid Id);

    public FavoritesPane()
    {
        InitializeComponent();
        ChBrowser.Controls.PaneDragInitiator.Attach(HeaderBar, PaneId.Favorites);
    }

    /// <summary>ヘッダのリフレッシュボタンと、ファイルメニュー / 仮想ルート右クリックの「お気に入りチェック」共通エントリ。</summary>
    public void FavCheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        _ = main.CheckFavoritesAsync();
    }

    private async void FavoritesWebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        var (type, payload) = WebMessageBridge.TryParseMessage(e);
        if (WebMessageBridge.TryDispatchCommonMessage(sender, type, payload, "お気に入りペイン")) return;

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
        if (TryFindResource(key) is not ContextMenu menu) return;
        menu.Tag             = id is Guid g ? new FavoriteRef(g) : null;
        menu.PlacementTarget = FavoritesWebView;
        menu.Placement       = PlacementMode.MousePoint;
        menu.IsOpen          = true;
    }

    // ---- ContextMenu 各項目クリックハンドラ (旧 MainWindow.WebMessages.cs より移植) ----

    private static Guid? ParseId(string? s) => Guid.TryParse(s, out var g) ? g : null;

    private static FavoriteRef? RefFromMenu(object sender)
    {
        if (sender is not MenuItem mi) return null;
        var owner = ItemsControl.ItemsControlFromItemContainer(mi) as ContextMenu
                  ?? mi.Parent as ContextMenu;
        return owner?.Tag as FavoriteRef;
    }

    public void FavNewFolderHere_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        if (RefFromMenu(sender) is not FavoriteRef r) return;
        if (main.Favorites.FindById(r.Id) is not FavoriteFolderViewModel parent) return;
        var name = InputDialog.Prompt(Window.GetWindow(this), "新規フォルダ", $"「{parent.DisplayName}」配下のフォルダ名:", "新規フォルダ");
        if (string.IsNullOrWhiteSpace(name)) return;
        main.NewFavoriteFolder(parent, name.Trim());
    }

    public void FavNewFolderRoot_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        var name = InputDialog.Prompt(Window.GetWindow(this), "新規フォルダ", "ルート直下のフォルダ名:", "新規フォルダ");
        if (string.IsNullOrWhiteSpace(name)) return;
        main.NewFavoriteFolder(null, name.Trim());
    }

    public void FavRename_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        if (RefFromMenu(sender) is not FavoriteRef r) return;
        if (main.Favorites.FindById(r.Id) is not FavoriteFolderViewModel folder) return;
        var name = InputDialog.Prompt(Window.GetWindow(this), "名前変更", "新しいフォルダ名:", folder.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        main.RenameFavoriteFolder(folder, name.Trim());
    }

    public void FavDelete_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        if (RefFromMenu(sender) is not FavoriteRef r) return;
        if (main.Favorites.FindById(r.Id) is not FavoriteEntryViewModel vm) return;

        var confirmMessage = vm switch
        {
            FavoriteFolderViewModel f => $"フォルダ「{f.DisplayName}」とその中身をすべて削除しますか?",
            _                         => $"「{vm.DisplayName}」をお気に入りから削除しますか?",
        };
        var result = MessageBox.Show(Window.GetWindow(this), confirmMessage, "削除確認",
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (result != MessageBoxResult.OK) return;
        main.DeleteFavoriteEntry(vm);
    }

    public void FavMoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        if (RefFromMenu(sender) is not FavoriteRef r) return;
        if (main.Favorites.FindById(r.Id) is FavoriteEntryViewModel vm)
            main.MoveFavoriteEntryUp(vm);
    }

    public void FavMoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        if (RefFromMenu(sender) is not FavoriteRef r) return;
        if (main.Favorites.FindById(r.Id) is FavoriteEntryViewModel vm)
            main.MoveFavoriteEntryDown(vm);
    }

    public void FavOpenAll_Click(object sender, RoutedEventArgs e)
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

    public void FavOpenAsBoard_Click(object sender, RoutedEventArgs e)
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
}
