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
    // ドラッグ中のタブ (= スレ表示 / スレ一覧 いずれか) とその種類。
    private IPaneTab? _dragTab;
    private PaneId    _dragKind;

    // 現在のドロップ先候補。strip ターゲットのグループは IPaneGroup (= 具象は Thread*PaneGroupViewModel)。
    private object?  _stripTargetGroup;
    private int      _stripExclusiveIndex;
    private (string LeafKey, DropSide Side)? _bodyTarget;

    // タブストリップ上の挿入線アドーナー。
    private TabInsertionAdorner? _insertionAdorner;
    private TabItem?             _insertionAdornerTab;
    private bool                 _insertionAdornerAfter;

    /// <summary>スレ表示タブのドラッグ閾値超えを <see cref="ThreadDisplayPane"/> が検出したときに呼ぶ。</summary>
    public void BeginTabDrag(ThreadTabViewModel tab, ThreadPaneGroupViewModel sourceGroup)
        => BeginTabDragCore(tab, PaneId.ThreadDisplay);

    /// <summary>スレ一覧タブのドラッグ閾値超えを <see cref="ThreadListPane"/> が検出したときに呼ぶ。</summary>
    public void BeginTabDrag(ThreadListTabViewModel tab, ThreadListPaneGroupViewModel sourceGroup)
        => BeginTabDragCore(tab, PaneId.ThreadList);

    private void BeginTabDragCore(IPaneTab tab, PaneId kind)
    {
        if (_vm is null) return;
        _dragTab          = tab;
        _dragKind         = kind;
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

        // (1) ドラッグ中タブと「同じ種類」のタブストリップ上か？
        if (FindStripTargetAt(posInHost) is { } strip)
        {
            _stripTargetGroup    = strip.Group;
            _stripExclusiveIndex = strip.ExclusiveIndex;
            _bodyTarget          = null;
            LayoutHost.ShowExternalDropZone(null);
            ShowInsertionAdorner(strip.AdornTab, strip.AdornAfter);
            return;
        }

        // (2) ペイン本体 (= ドラッグ中タブの種類で新ペイン生成) か？ 任意の種類のペイン上で 4 方向に出せる。
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
        var tab = _dragTab;
        if (tab is null || _vm is null) return;

        if (_stripTargetGroup is { } group)
        {
            // 同種ストリップへの移動 (= 同一ペインなら並べ替え)。
            if (_dragKind == PaneId.ThreadDisplay && tab is ThreadTabViewModel t && group is ThreadPaneGroupViewModel g)
                _vm.MoveTabToGroupAt(t, g, _stripExclusiveIndex);
            else if (_dragKind == PaneId.ThreadList && tab is ThreadListTabViewModel lt && group is ThreadListPaneGroupViewModel lg)
                _vm.MoveThreadListTabToGroupAt(lt, lg, _stripExclusiveIndex);
        }
        else if (_bodyTarget is { } body)
        {
            // ペイン本体 → その辺にドラッグ中タブの種類の新ペインを生成して移動。
            if (_dragKind == PaneId.ThreadDisplay && tab is ThreadTabViewModel t)
                CreatePaneWithTab(t, body.LeafKey, body.Side);
            else if (_dragKind == PaneId.ThreadList && tab is ThreadListTabViewModel lt)
                CreateListPaneWithTab(lt, body.LeafKey, body.Side);
        }
        // 元ペインが空になった場合の自動クローズは VM の *GroupEmptied → On*GroupEmptied で行う
        // (= タブ移動の Remove でも × で閉じた Remove でも同じ経路)。ここでは何もしない。
    }

    private void OnTabDragEnd()
    {
        LayoutHost.ExternalDragMove   -= OnTabDragMove;
        LayoutHost.ExternalDragCommit -= OnTabDragCommit;
        LayoutHost.ExternalDragEnd    -= OnTabDragEnd;
        RemoveInsertionAdorner();
        _dragTab          = null;
        _stripTargetGroup = null;
        _bodyTarget       = null;
    }

    /// <summary>タブストリップ上のドロップ先情報。<see cref="Group"/> は IPaneGroup (= 具象 Thread*PaneGroupViewModel)。
    /// <see cref="ExclusiveIndex"/> は「ドラッグ中タブを除いた target.Tabs」での挿入位置。</summary>
    private sealed record StripTarget(object Group, int ExclusiveIndex, TabItem? AdornTab, bool AdornAfter);

    private StripTarget? FindStripTargetAt(Point posInHost)
    {
        if (LayoutHost.InputHitTest(posInHost) is not DependencyObject hit) return null;
        var strip = TabClickHelper.FindAncestor<TabControl>(hit);
        if (strip is null) return null;

        // ドラッグ中タブと同じ種類のストリップだけを対象にする (= 異種ストリップへは移動させない)。
        IPaneGroup? group = _dragKind switch
        {
            PaneId.ThreadDisplay when strip.Name == "ThreadTabStrip"
                => TabClickHelper.FindAncestor<ThreadDisplayPane>(strip)?.DataContext as IPaneGroup,
            PaneId.ThreadList when strip.Name == "ThreadListTabStrip"
                => TabClickHelper.FindAncestor<ThreadListPane>(strip)?.DataContext as IPaneGroup,
            _ => null,
        };
        if (group is null) return null;

        var dragged = _dragTab;
        var excl     = group.TabsSnapshot.Where(t => !ReferenceEquals(t, dragged)).ToList();

        var overItem = TabClickHelper.FindAncestor<TabItem>(hit);
        if (overItem?.DataContext is IPaneTab overTab && !ReferenceEquals(overTab, dragged))
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

        var group = _vm.AddThreadGroup(newLeaf.Key);
        var pane  = new ThreadDisplayPane();
        PaneLayoutPanel.SetPaneId(pane, PaneId.ThreadDisplay);
        PaneLayoutPanel.SetInstanceId(pane, instanceId);
        pane.DataContext = group;
        LayoutHost.Children.Add(pane);

        LayoutHost.ReplaceLayout(newLayout);
        _vm.MoveTabToGroupAt(tab, group, 0);
    }

    /// <summary>指定 leaf の side 側に新しいスレ一覧ペインを生成し、ドラッグ中タブをそこへ移す。</summary>
    private void CreateListPaneWithTab(ThreadListTabViewModel tab, string targetLeafKey, DropSide side)
    {
        if (_vm is null || LayoutHost.Layout is null) return;

        var instanceId = Guid.NewGuid().ToString("N");
        var newLeaf    = new LeafLayoutNode(PaneId.ThreadList, instanceId);
        var newLayout  = PaneLayoutOps.SplitAtLeaf(LayoutHost.Layout, targetLeafKey, side, newLeaf);
        if (!newLayout.IsValidFullLayout()) return;

        var group = _vm.AddThreadListGroup(newLeaf.Key);
        var pane  = new ThreadListPane();
        PaneLayoutPanel.SetPaneId(pane, PaneId.ThreadList);
        PaneLayoutPanel.SetInstanceId(pane, instanceId);
        pane.DataContext = group;
        LayoutHost.Children.Add(pane);

        LayoutHost.ReplaceLayout(newLayout);
        _vm.MoveThreadListTabToGroupAt(tab, group, 0);
    }

    /// <summary>復元したレイアウトツリー中の各スレ表示ペイン (leaf) に対し、対応するグループ + コントロールを
    /// 用意する (複数ペイン化 Phase 4 の起動時復元)。静的ペイン (XAML の ThreadDisplayPaneCtrl + 初期グループ) を
    /// 先頭の ThreadDisplay leaf に割り当て直し、残りの leaf 分だけ動的にグループ + コントロールを生成する。
    /// タブの中身は後段の <see cref="MainViewModel.RestoreOpenTabs"/> がペインキーを突き合わせて流し込む。</summary>
    private void ReconcilePanesToLayout()
    {
        if (_vm is null || LayoutHost.Layout is null) return;
        ReconcileKindToLayout(PaneId.ThreadDisplay);
        ReconcileKindToLayout(PaneId.ThreadList);
    }

    /// <summary>指定種類 (ThreadDisplay / ThreadList) について、レイアウトツリー中の各 leaf に対応する
    /// グループ + コントロールを用意する。静的ペイン (XAML のコントロール + 初期グループ) を先頭 leaf に
    /// 割り当て直し、残りの leaf 分だけ動的にグループ + コントロールを生成する。</summary>
    private void ReconcileKindToLayout(PaneId kind)
    {
        var keys = LayoutHost.Layout!.EnumerateLeaves()
            .Where(l => l.Pane == kind)
            .Select(l => l.Key)
            .ToList();
        if (keys.Count == 0) return; // 通常ありえない (IsValidFullLayout が各複数可種 ≥1 を保証)

        if (kind == PaneId.ThreadDisplay)
        {
            var staticGroup = ThreadDisplayPaneCtrl.DataContext as ThreadPaneGroupViewModel ?? _vm!.ActiveThreadGroup;
            staticGroup.PaneKey = keys[0];
            PaneKinds.TryParseKey(keys[0], out _, out var firstInstance);
            PaneLayoutPanel.SetInstanceId(ThreadDisplayPaneCtrl, firstInstance);
            for (int i = 1; i < keys.Count; i++)
            {
                PaneKinds.TryParseKey(keys[i], out _, out var inst);
                var group = _vm!.AddThreadGroup(keys[i]);
                var pane  = new ThreadDisplayPane();
                PaneLayoutPanel.SetPaneId(pane, PaneId.ThreadDisplay);
                PaneLayoutPanel.SetInstanceId(pane, inst);
                pane.DataContext = group;
                LayoutHost.Children.Add(pane);
            }
        }
        else if (kind == PaneId.ThreadList)
        {
            var staticGroup = ThreadListPaneCtrl.DataContext as ThreadListPaneGroupViewModel ?? _vm!.ActiveThreadListGroup;
            staticGroup.PaneKey = keys[0];
            PaneKinds.TryParseKey(keys[0], out _, out var firstInstance);
            PaneLayoutPanel.SetInstanceId(ThreadListPaneCtrl, firstInstance);
            for (int i = 1; i < keys.Count; i++)
            {
                PaneKinds.TryParseKey(keys[i], out _, out var inst);
                var group = _vm!.AddThreadListGroup(keys[i]);
                var pane  = new ThreadListPane();
                PaneLayoutPanel.SetPaneId(pane, PaneId.ThreadList);
                PaneLayoutPanel.SetInstanceId(pane, inst);
                pane.DataContext = group;
                LayoutHost.Children.Add(pane);
            }
        }
    }

    /// <summary>VM がスレ一覧ペイン空化を通知してきたときのハンドラ。再入回避のため遅延実行。</summary>
    internal void OnThreadListGroupEmptied(ThreadListPaneGroupViewModel group)
        => Dispatcher.BeginInvoke(new Action(() => CloseEmptyListPane(group)));

    /// <summary>空になったスレ一覧ペインを閉じる (最後の 1 枚は残す)。冪等。</summary>
    private void CloseEmptyListPane(ThreadListPaneGroupViewModel group)
    {
        if (_vm is null || LayoutHost.Layout is null) return;
        if (!_vm.ThreadListPaneGroups.Contains(group)) return;
        if (group.Tabs.Count > 0) return;
        if (_vm.ThreadListPaneGroups.Count <= 1) return;

        var newLayout = PaneLayoutOps.RemoveLeaf(LayoutHost.Layout, group.PaneKey);
        if (newLayout is null) return;

        var ctrl = LayoutHost.Children.OfType<ThreadListPane>()
            .FirstOrDefault(p => ReferenceEquals(p.DataContext, group));
        if (ctrl is not null) LayoutHost.Children.Remove(ctrl);

        _vm.RemoveThreadListGroup(group);
        LayoutHost.ReplaceLayout(newLayout);
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
