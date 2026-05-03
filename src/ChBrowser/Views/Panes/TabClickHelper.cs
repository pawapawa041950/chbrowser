using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ChBrowser.Views.Panes;

/// <summary>ThreadListPane / ThreadDisplayPane が共有するタブクリック解析ヘルパ (Phase 23 抽出)。
/// 修飾子付き左クリック / 中クリック / ダブルクリックの解析と、Visual ツリー走査を集約。</summary>
internal static class TabClickHelper
{
    /// <summary>修飾子付き左クリック / 中クリックを設定で割り当てたアクションに振り分ける。
    /// 子要素の Button (× 等) のクリックは除外する。</summary>
    public static string? PickClickAction(MouseButtonEventArgs e, string ctrlAction, string shiftAction, string altAction, string middleAction)
    {
        if (e.OriginalSource is DependencyObject src && FindAncestor<ButtonBase>(src) is not null)
            return null;

        if (e.ChangedButton == MouseButton.Middle && e.ButtonState == MouseButtonState.Pressed)
            return middleAction;

        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed && e.ClickCount == 1)
        {
            var mods = Keyboard.Modifiers;
            if      ((mods & ModifierKeys.Control) == ModifierKeys.Control) return ctrlAction;
            else if ((mods & ModifierKeys.Shift)   == ModifierKeys.Shift)   return shiftAction;
            else if ((mods & ModifierKeys.Alt)     == ModifierKeys.Alt)     return altAction;
        }
        return null;
    }

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
