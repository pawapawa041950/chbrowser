using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ChBrowser.Views.Panes;

/// <summary>ThreadListPane / ThreadDisplayPane が共有する Visual ツリー / ContextMenu 走査ヘルパ。
/// クリックアクションの分岐は ShortcutManager 経由に移行したため、ここはツリー走査だけに縮退。</summary>
internal static class TabClickHelper
{
    public static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }

    /// <summary>ContextMenu の項目を再帰展開して全 MenuItem を列挙する (サブメニュー内の項目も含む)。</summary>
    public static IEnumerable<MenuItem> EnumerateAllMenuItems(ItemsControl root)
    {
        foreach (var item in root.Items)
        {
            if (item is not MenuItem mi) continue;
            yield return mi;
            foreach (var sub in EnumerateAllMenuItems(mi)) yield return sub;
        }
    }
}
