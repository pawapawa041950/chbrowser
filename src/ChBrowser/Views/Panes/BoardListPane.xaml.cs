using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ChBrowser.Services.WebView2;
using ChBrowser.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace ChBrowser.Views.Panes;

/// <summary>板一覧ペイン (Phase 14a で WebView 化、Phase 23 で UserControl 抽出)。
/// JS の openBoard / setCategoryExpanded / contextMenu (target=board) を受けて MainViewModel に流す。</summary>
public partial class BoardListPane : UserControl
{
    /// <summary>右クリックメニューに乗せる対象板。</summary>
    private sealed record BoardRef(string Host, string DirectoryName, string BoardName);

    public BoardListPane()
    {
        InitializeComponent();
        ChBrowser.Controls.PaneDragInitiator.Attach(HeaderBar, ChBrowser.Models.PaneId.BoardList);
    }

    private async void BoardListWebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        var (type, payload) = WebMessageBridge.TryParseMessage(e);
        if (WebMessageBridge.TryDispatchCommonMessage(sender, type, payload, "板一覧ペイン")) return;

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

    private void ShowBoardContextMenu(string host, string directoryName, string boardName)
    {
        if (TryFindResource("BoardContextMenu") is not ContextMenu menu) return;
        menu.Tag             = new BoardRef(host, directoryName, boardName);
        menu.PlacementTarget = BoardListWebView;
        menu.Placement       = PlacementMode.MousePoint;
        menu.IsOpen          = true;
    }

    private void AddBoardToFavorites_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        if (sender is not MenuItem mi) return;
        var owner = ItemsControl.ItemsControlFromItemContainer(mi) as ContextMenu
                  ?? mi.Parent as ContextMenu;
        if (owner?.Tag is not BoardRef br) return;
        main.AddBoardToFavoritesByHostDir(br.Host, br.DirectoryName, br.BoardName);
    }
}
