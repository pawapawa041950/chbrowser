using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using ChBrowser.Views.Panes;

namespace ChBrowser.Services.Shortcuts;

/// <summary>WPF visual から、所属するペインの <see cref="ShortcutAction.Category"/> 文字列を解決する。
/// マウスジェスチャー / マウス操作のディスパッチを「開始位置のペイン」にスコープするために使う。
///
/// <para>判定ルール (Phase 23 docking 化以降): 4 ペインの UserControl 型 (<see cref="FavoritesPane"/> /
/// <see cref="BoardListPane"/> / <see cref="ThreadListPane"/> / <see cref="ThreadDisplayPane"/>) に
/// 到達するまでツリーを遡る。スレ系の 2 ペインだけ追加で「ヘッダ部分 (タブストリップ含む) / ボディ部分」を
/// 区別するため <see cref="TabPanel"/> / <see cref="TabItem"/> 通過フラグを保持する。
/// どこにも該当しなければ「メインウィンドウ」(= chrome / メニューバー / アドレスバー上)。</para></summary>
public static class CategoryResolver
{
    public static string Resolve(DependencyObject? source)
    {
        bool passedTabHeader = false;
        DependencyObject? cur = source;
        while (cur is not null)
        {
            if (cur is TabItem || cur is TabPanel) passedTabHeader = true;

            switch (cur)
            {
                case FavoritesPane:     return "お気に入りペイン";
                case BoardListPane:     return "板一覧ペイン";
                case ThreadListPane:    return passedTabHeader ? "スレ一覧のタブ領域"     : "スレ一覧表示領域";
                case ThreadDisplayPane: return passedTabHeader ? "スレッドタブ表示領域"   : "スレッド表示領域";
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
