using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace ChBrowser.Services.Shortcuts;

/// <summary>WPF visual から、所属するペインの <see cref="ShortcutAction.Category"/> 文字列を解決する (Phase 15)。
/// マウスジェスチャー / マウス操作のディスパッチを「開始位置のペイン」にスコープするために使う。
///
/// <para>判定ルール: 名前付きペインルート (<c>ThreadListPane</c> / <c>ThreadPane</c> / <c>FavoritesWebView</c> /
/// <c>BoardListWebView</c>) に到達するまでの間に <see cref="TabPanel"/> / <see cref="TabItem"/> を通過していたら
/// 「タブ領域」、そうでなければ「表示領域」を返す。どこにも該当しなければ「メインウィンドウ」(= chrome)。</para></summary>
public static class CategoryResolver
{
    public static string Resolve(DependencyObject? source)
    {
        bool passedTabHeader = false;
        DependencyObject? cur = source;
        while (cur is not null)
        {
            if (cur is TabItem || cur is TabPanel) passedTabHeader = true;

            if (cur is FrameworkElement fe)
            {
                switch (fe.Name)
                {
                    case "ThreadListPane":
                        return passedTabHeader ? "スレ一覧のタブ領域" : "スレ一覧表示領域";
                    case "ThreadPane":
                        return passedTabHeader ? "スレッドタブ表示領域" : "スレッド表示領域";
                    case "FavoritesWebView":
                        return "お気に入りペイン";
                    case "BoardListWebView":
                        return "板一覧ペイン";
                }
            }
            cur = GetAnyParent(cur);
        }
        return "メインウィンドウ";
    }

    /// <summary>visual / logical 両方の親をチェックして返す。
    /// <see cref="VisualTreeHelper.GetParent"/> は <see cref="Visual"/> / <see cref="Visual3D"/> 以外
    /// (Run などの <see cref="System.Windows.Documents.TextElement"/>) では例外を投げるので、
    /// Visual 系のときだけ呼ぶ。</summary>
    internal static DependencyObject? GetAnyParent(DependencyObject d)
    {
        DependencyObject? parent = null;
        if (d is Visual || d is Visual3D)
            parent = VisualTreeHelper.GetParent(d);
        return parent ?? LogicalTreeHelper.GetParent(d);
    }
}
