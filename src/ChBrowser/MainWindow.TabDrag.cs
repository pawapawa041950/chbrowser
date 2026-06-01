using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using ChBrowser.Controls;
using ChBrowser.Models;
using ChBrowser.ViewModels;
using ChBrowser.Views.Panes;

namespace ChBrowser;

/// <summary>タブ D&D のオーケストレーション (複数ペイン化, Phase 3)。
///
/// <para>ドラッグの開始検出は <see cref="ThreadDisplayPane"/> が行い、閾値超えで <see cref="BeginTabDrag"/> を呼ぶ。
/// 以降の移動/ドロップは <see cref="PaneLayoutPanel"/> (LayoutHost) が <c>Mouse.Capture</c> で引き受け、
/// 位置を本クラスに通知する (= WebView2 を跨いだペイン本体へのドロップとオーバーレイ表示のため)。</para>
///
/// <para>ドロップ先の判定:
/// <list type="bullet">
/// <item><description>スレ表示ペインのタブストリップ上 → そのペインへ移動 (同一ペインなら並べ替え)。挿入線を表示。</description></item>
/// <item><description>いずれかのペイン本体 (4 方向ゾーン) → その辺に新しいスレ表示ペインを生成して移動。ゾーンを表示。</description></item>
/// </list>
/// 移動の結果、元ペインが空になったら (最後の 1 枚を除き) 自動で閉じる。</para></summary>
public partial class MainWindow
{
    private ThreadTabViewModel?        _dragTab;
    private ThreadPaneGroupViewModel?  _dragSourceGroup;

    // 現在のドロップ先候補。
    private ThreadPaneGroupViewModel?  _stripTargetGroup;
    private int                        _stripExclusiveIndex;
    private (string LeafKey, DropSide Side)? _bodyTarget;

    // タブストリップ上の挿入線アドーナー。
    private TabInsertionAdorner? _insertionAdorner;
    private TabItem?             _insertionAdornerTab;
    private bool                 _insertionAdornerAfter;

    /// <summary><see cref="ThreadDisplayPane"/> がタブのドラッグ閾値超えを検出したときに呼ぶ。</summary>
    public void BeginTabDrag(ThreadTabViewModel tab, ThreadPaneGroupViewModel sourceGroup)
    {
        if (_vm is null) return;
        _dragTab          = tab;
        _dragSourceGroup  = sourceGroup;
        _stripTargetGroup = null;
        _bodyTarget       = null;
        LayoutHost.ExternalDragMove   += OnTabDragMove;
        LayoutHost.ExternalDragCommit += OnTabDragCommit;
        LayoutHost.ExternalDragEnd    += OnTabDragEnd;
        LayoutHost.BeginExternalDrag();
    }

    private void OnTabDragMove(Point posInHost)
    {
        if (_dragTab is null) return;

        // (1) スレ表示ペインのタブストリップ上か？
        if (FindStripTargetAt(posInHost) is { } strip)
        {
            _stripTargetGroup    = strip.Group;
            _stripExclusiveIndex = strip.ExclusiveIndex;
            _bodyTarget          = null;
            LayoutHost.ShowExternalDropZone(null);
            ShowInsertionAdorner(strip.AdornTab, strip.AdornAfter);
            return;
        }

        // (2) ペイン本体 (= 新ペイン生成) か？
        _stripTargetGroup = null;
        RemoveInsertionAdorner();
        if (LayoutHost.ComputeBodyDropZone(posInHost) is { } bz)
        {
            _bodyTarget = (bz.LeafKey, bz.Side);
            LayoutHost.ShowExternalDropZone(bz.Zone);
        }
        else
        {
            _bodyTarget = null;
            LayoutHost.ShowExternalDropZone(null);
        }
    }

    private void OnTabDragCommit(Point posInHost)
    {
        var tab    = _dragTab;
        var source = _dragSourceGroup;
        if (tab is null || source is null || _vm is null) return;

        if (_stripTargetGroup is { } target)
        {
            _vm.MoveTabToGroupAt(tab, target, _stripExclusiveIndex);
        }
        else if (_bodyTarget is { } body)
        {
            CreatePaneWithTab(tab, body.LeafKey, body.Side);
        }
        // 元ペインが空になった場合の自動クローズは VM の ThreadGroupEmptied → OnThreadGroupEmptied で行う
        // (= タブ移動の Remove でも × で閉じた Remove でも同じ経路)。ここでは何もしない。
    }

    private void OnTabDragEnd()
    {
        LayoutHost.ExternalDragMove   -= OnTabDragMove;
        LayoutHost.ExternalDragCommit -= OnTabDragCommit;
        LayoutHost.ExternalDragEnd    -= OnTabDragEnd;
        RemoveInsertionAdorner();
        _dragTab          = null;
        _dragSourceGroup  = null;
        _stripTargetGroup = null;
        _bodyTarget       = null;
    }

    /// <summary>タブストリップ上のドロップ先情報。<see cref="ExclusiveIndex"/> は「ドラッグ中タブを除いた
    /// target.Tabs」での挿入位置。<see cref="AdornTab"/>/<see cref="AdornAfter"/> は挿入線の表示位置。</summary>
    private sealed record StripTarget(ThreadPaneGroupViewModel Group, int ExclusiveIndex, TabItem? AdornTab, bool AdornAfter);

    private StripTarget? FindStripTargetAt(Point posInHost)
    {
        if (LayoutHost.InputHitTest(posInHost) is not DependencyObject hit) return null;

        // スレ表示ペインのタブストリップ (x:Name=ThreadTabStrip) の中だけを対象にする
        // (= スレ一覧ペインの TabControl 等は除外)。
        var strip = TabClickHelper.FindAncestor<TabControl>(hit);
        if (strip is null || strip.Name != "ThreadTabStrip") return null;
        var pane = TabClickHelper.FindAncestor<ThreadDisplayPane>(strip);
        if (pane?.DataContext is not ThreadPaneGroupViewModel group) return null;

        var dragged = _dragTab;
        var excl     = group.Tabs.Where(t => !ReferenceEquals(t, dragged)).ToList();

        var overItem = TabClickHelper.FindAncestor<TabItem>(hit);
        if (overItem?.DataContext is ThreadTabViewModel overTab && !ReferenceEquals(overTab, dragged))
        {
            var p     = LayoutHost.TranslatePoint(posInHost, overItem);
            bool after = p.X > overItem.ActualWidth / 2;
            int  j     = excl.IndexOf(overTab);
            int  k     = after ? j + 1 : j;
            return new StripTarget(group, k, overItem, after);
        }

        // タブの上ではない (= ストリップの空き領域 / ドラッグ中タブ自身の上 / 0 タブ) → 末尾に追加扱い。
        TabItem? lastItem = strip.Items.Count > 0
            ? strip.ItemContainerGenerator.ContainerFromIndex(strip.Items.Count - 1) as TabItem
            : null;
        return new StripTarget(group, excl.Count, lastItem, true);
    }

    /// <summary>指定 leaf の side 側に新しいスレ表示ペインを生成し、ドラッグ中タブをそこへ移す。</summary>
    private void CreatePaneWithTab(ThreadTabViewModel tab, string targetLeafKey, DropSide side)
    {
        if (_vm is null || LayoutHost.Layout is null) return;

        var instanceId = Guid.NewGuid().ToString("N");
        var newLeaf    = new LeafLayoutNode(PaneId.ThreadDisplay, instanceId);
        var newLayout  = PaneLayoutOps.SplitAtLeaf(LayoutHost.Layout, targetLeafKey, side, newLeaf);
        if (!newLayout.IsValidFullLayout()) return;

        // VM グループ + ペインコントロールを生成してレイアウトに参加させる。
        var group = _vm.AddThreadGroup(newLeaf.Key);
        var pane  = new ThreadDisplayPane();
        PaneLayoutPanel.SetPaneId(pane, PaneId.ThreadDisplay);
        PaneLayoutPanel.SetInstanceId(pane, instanceId);
        pane.DataContext = group;
        LayoutHost.Children.Add(pane);

        LayoutHost.ReplaceLayout(newLayout);

        // タブを新ペインへ移動 (空の新ペインの先頭へ)。
        _vm.MoveTabToGroupAt(tab, group, 0);
    }

    /// <summary>復元したレイアウトツリー中の各スレ表示ペイン (leaf) に対し、対応するグループ + コントロールを
    /// 用意する (複数ペイン化 Phase 4 の起動時復元)。静的ペイン (XAML の ThreadDisplayPaneCtrl + 初期グループ) を
    /// 先頭の ThreadDisplay leaf に割り当て直し、残りの leaf 分だけ動的にグループ + コントロールを生成する。
    /// タブの中身は後段の <see cref="MainViewModel.RestoreOpenTabs"/> がペインキーを突き合わせて流し込む。</summary>
    private void ReconcilePanesToLayout()
    {
        if (_vm is null || LayoutHost.Layout is null) return;

        var keys = LayoutHost.Layout.EnumerateLeaves()
            .Where(l => l.Pane == PaneId.ThreadDisplay)
            .Select(l => l.Key)
            .ToList();
        if (keys.Count == 0) return; // 通常ありえない (IsValidFullLayout が ThreadDisplay ≥1 を保証)

        // (1) 静的ペインを先頭キーへ割り当て直す (= 元 "ThreadDisplay" 以外のキーで保存されていた場合に追従)。
        var staticGroup = ThreadDisplayPaneCtrl.DataContext as ThreadPaneGroupViewModel ?? _vm.ActiveThreadGroup;
        staticGroup.PaneKey = keys[0];
        PaneKinds.TryParseKey(keys[0], out _, out var firstInstance);
        PaneLayoutPanel.SetInstanceId(ThreadDisplayPaneCtrl, firstInstance);

        // (2) 残りの leaf キーごとにグループ + コントロールを生成して参加させる。
        for (int i = 1; i < keys.Count; i++)
        {
            PaneKinds.TryParseKey(keys[i], out _, out var inst);
            var group = _vm.AddThreadGroup(keys[i]);
            var pane  = new ThreadDisplayPane();
            PaneLayoutPanel.SetPaneId(pane, PaneId.ThreadDisplay);
            PaneLayoutPanel.SetInstanceId(pane, inst);
            pane.DataContext = group;
            LayoutHost.Children.Add(pane);
        }
    }

    /// <summary>VM がペイン空化を通知してきたときのハンドラ。再入を避けるため遅延実行する
    /// (= タブ移動 / 閉じ処理の最中にレイアウトツリーやコントロールを触らない)。</summary>
    internal void OnThreadGroupEmptied(ThreadPaneGroupViewModel group)
        => Dispatcher.BeginInvoke(new Action(() => CloseEmptyPane(group)));

    /// <summary>空になったスレ表示ペインを閉じる (最後の 1 枚は残す)。レイアウトから leaf を除き、
    /// 対応コントロールを外し、VM グループを破棄する。冪等 (= 既に閉じ済 / 非空 / 最後の 1 枚なら何もしない)。</summary>
    private void CloseEmptyPane(ThreadPaneGroupViewModel group)
    {
        if (_vm is null || LayoutHost.Layout is null) return;
        if (!_vm.ThreadPaneGroups.Contains(group)) return; // 既に閉じ済
        if (group.Tabs.Count > 0) return;                   // 遅延中に再びタブが入った
        if (_vm.ThreadPaneGroups.Count <= 1) return;        // 最後の 1 枚は空でも残す

        var newLayout = PaneLayoutOps.RemoveLeaf(LayoutHost.Layout, group.PaneKey);
        if (newLayout is null) return;

        var ctrl = LayoutHost.Children.OfType<ThreadDisplayPane>()
            .FirstOrDefault(p => ReferenceEquals(p.DataContext, group));
        if (ctrl is not null) LayoutHost.Children.Remove(ctrl);

        _vm.RemoveThreadGroup(group);
        LayoutHost.ReplaceLayout(newLayout);
    }

    // ---- 挿入線アドーナー (タブストリップ上のドロップ位置プレビュー) ----

    private void ShowInsertionAdorner(TabItem? tab, bool after)
    {
        if (tab is null) { RemoveInsertionAdorner(); return; }
        if (ReferenceEquals(_insertionAdornerTab, tab) && _insertionAdornerAfter == after && _insertionAdorner is not null)
            return;
        RemoveInsertionAdorner();
        var layer = AdornerLayer.GetAdornerLayer(tab);
        if (layer is null) return;
        _insertionAdorner      = new TabInsertionAdorner(tab, after);
        _insertionAdornerTab   = tab;
        _insertionAdornerAfter = after;
        layer.Add(_insertionAdorner);
    }

    private void RemoveInsertionAdorner()
    {
        if (_insertionAdorner is not null && _insertionAdornerTab is not null
            && AdornerLayer.GetAdornerLayer(_insertionAdornerTab) is { } layer)
        {
            layer.Remove(_insertionAdorner);
        }
        _insertionAdorner    = null;
        _insertionAdornerTab = null;
    }
}
