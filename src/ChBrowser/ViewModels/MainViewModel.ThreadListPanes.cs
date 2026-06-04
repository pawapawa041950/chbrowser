using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace ChBrowser.ViewModels;

/// <summary>スレッド一覧ペインの複数枚対応 (= スレ表示の <c>MainViewModel.cs</c> 側 ThreadPaneGroups 機構の鏡映)。
/// 単一だった <c>ThreadListTabs</c> / <c>SelectedThreadListTab</c> を「アクティブ一覧グループへの facade」にし、
/// 横断操作用に <see cref="AllThreadListTabs"/> を提供する。タブ移動 / 空ペイン自動クローズも対応。</summary>
public sealed partial class MainViewModel
{
    /// <summary>スレッド一覧ペインの集合。起動時は 1 枚。Phase B のタブ D&amp;D で増減する。</summary>
    public ObservableCollection<ThreadListPaneGroupViewModel> ThreadListPaneGroups { get; } = new();

    /// <summary>「最後に操作した」スレ一覧ペイン (MRU)。新規スレ一覧タブの行き先・ステータス追従の基準。</summary>
    private ThreadListPaneGroupViewModel _activeThreadListGroup = null!;
    public  ThreadListPaneGroupViewModel ActiveThreadListGroup => _activeThreadListGroup;

    /// <summary>アクティブ一覧ペインのタブ集合 (= 従来の ThreadListTabs 互換 facade)。
    /// 「新規タブを追加 / 件数を見る」等の "アクティブペインに対する操作" はこれを使う。</summary>
    public ObservableCollection<ThreadListTabViewModel> ThreadListTabs => _activeThreadListGroup.Tabs;

    /// <summary>アクティブ一覧ペインの選択タブ (= 従来の SelectedThreadListTab 互換 facade)。</summary>
    public ThreadListTabViewModel? SelectedThreadListTab
    {
        get => _activeThreadListGroup.SelectedTab;
        set => _activeThreadListGroup.SelectedTab = value;
    }

    /// <summary>全スレ一覧ペインのタブを横断列挙する (= 既存タブ検索 / 全件保存 / 状態一括同期 用)。</summary>
    public IEnumerable<ThreadListTabViewModel> AllThreadListTabs
        => ThreadListPaneGroups.SelectMany(g => g.Tabs);

    /// <summary>指定タブが属する一覧ペインを返す (無ければ null)。</summary>
    public ThreadListPaneGroupViewModel? ThreadListGroupOf(ThreadListTabViewModel tab)
        => ThreadListPaneGroups.FirstOrDefault(g => g.Tabs.Contains(tab));

    /// <summary>タブを所属一覧ペインから取り除く (= 閉じる)。所属不明ならアクティブペインから試みる。</summary>
    public void RemoveThreadListTab(ThreadListTabViewModel tab)
        => (ThreadListGroupOf(tab) ?? _activeThreadListGroup).Tabs.Remove(tab);

    /// <summary>指定タブの所属一覧ペインをアクティブにして選択する (= 既存タブ検索のヒット時の活性化)。</summary>
    private void ActivateThreadListTab(ThreadListTabViewModel tab)
    {
        var group = ThreadListGroupOf(tab) ?? _activeThreadListGroup;
        SetActiveThreadListGroup(group);
        group.SelectedTab = tab;
    }

    /// <summary>新しい一覧ペインを登録する (= Tabs の CollectionChanged を購読 + コレクションに追加)。</summary>
    private void RegisterThreadListGroup(ThreadListPaneGroupViewModel group)
    {
        group.Tabs.CollectionChanged += OnThreadListTabsCollectionChanged;
        ThreadListPaneGroups.Add(group);
    }

    /// <summary>一覧ペインを新設する (Phase B のタブ→ペイン本体ドロップ用)。VM (グループ) だけ作る。</summary>
    public ThreadListPaneGroupViewModel AddThreadListGroup(string paneKey)
    {
        var group = new ThreadListPaneGroupViewModel(this, paneKey);
        RegisterThreadListGroup(group);
        return group;
    }

    /// <summary>空になった一覧ペインを破棄する。最低 1 枚は維持。破棄対象がアクティブなら別ペインへ繰り上げ。</summary>
    public void RemoveThreadListGroup(ThreadListPaneGroupViewModel group)
    {
        if (ThreadListPaneGroups.Count <= 1) return;
        group.Tabs.CollectionChanged -= OnThreadListTabsCollectionChanged;
        var wasActive = ReferenceEquals(group, _activeThreadListGroup);
        ThreadListPaneGroups.Remove(group);
        if (wasActive)
        {
            _activeThreadListGroup = ThreadListPaneGroups[0];
            OnPropertyChanged(nameof(ActiveThreadListGroup));
            OnPropertyChanged(nameof(ThreadListTabs));
            OnPropertyChanged(nameof(SelectedThreadListTab));
            _lastActivePane = ActivePane.ThreadList;
            HandleActiveSelectedThreadListTabChanged(_activeThreadListGroup.SelectedTab);
        }
    }

    /// <summary>アクティブ一覧ペイン (MRU) を張り替える。変われば選択タブ追従の更新を行う。</summary>
    public void SetActiveThreadListGroup(ThreadListPaneGroupViewModel group)
    {
        if (ReferenceEquals(_activeThreadListGroup, group)) return;
        _activeThreadListGroup = group;
        OnPropertyChanged(nameof(ActiveThreadListGroup));
        OnPropertyChanged(nameof(ThreadListTabs));
        OnPropertyChanged(nameof(SelectedThreadListTab));
        _lastActivePane = ActivePane.ThreadList;
        HandleActiveSelectedThreadListTabChanged(group.SelectedTab);
    }

    /// <summary>一覧ペインの選択タブが変わったときにそのペインから呼ばれる。アクティブペインのときだけ追従更新。</summary>
    internal void OnThreadListGroupSelectedTabChanged(ThreadListPaneGroupViewModel group, ThreadListTabViewModel? value)
    {
        if (!ReferenceEquals(group, _activeThreadListGroup)) return;
        OnPropertyChanged(nameof(SelectedThreadListTab));
        HandleActiveSelectedThreadListTabChanged(value);
    }

    /// <summary>タブを別一覧ペイン (または同一ペイン内の別位置) へ移動する (Phase B)。
    /// <paramref name="exclusiveIndex"/> は「ドラッグ中タブを除いた target.Tabs」での挿入位置。
    /// 同一ペインなら Move (= WebView2 を作り直さない)、別ペインなら Remove+Insert で移す (移動先で WebView 再生成)。</summary>
    public void MoveThreadListTabToGroupAt(ThreadListTabViewModel tab, ThreadListPaneGroupViewModel target, int exclusiveIndex)
    {
        var source = ThreadListGroupOf(tab);
        if (source is null) return;

        if (ReferenceEquals(source, target))
        {
            int from = target.Tabs.IndexOf(tab);
            if (from < 0) return;
            int to = System.Math.Clamp(exclusiveIndex, 0, target.Tabs.Count - 1);
            if (to != from) target.Tabs.Move(from, to);
            target.SelectedTab = tab;
            return;
        }

        // 移動先では WebView2 が新規生成される。Html は初回のみ設定する設計なので、現在の Items から
        // Html を作り直して「移動先の初期 HTML」を最新にする (= 古い一覧が出るのを防ぐ)。
        tab.RebuildHtmlForReattach();
        SuppressTabCloseSideEffects = true;
        try
        {
            source.Tabs.Remove(tab);
            target.Tabs.Insert(System.Math.Clamp(exclusiveIndex, 0, target.Tabs.Count), tab);
        }
        finally { SuppressTabCloseSideEffects = false; }

        target.SelectedTab = tab;
        SetActiveThreadListGroup(target);
        if (source.SelectedTab is null || source.Tabs.IndexOf(source.SelectedTab) < 0)
            source.SelectedTab = source.Tabs.Count > 0 ? source.Tabs[^1] : null;
    }

    /// <summary>あるスレ一覧ペインのタブが 0 枚になったときに発火 (× で閉じた / 別ペインへ移動した、いずれでも)。
    /// View 側 (MainWindow) が受けて、最後の 1 枚を除きそのペインを閉じる。</summary>
    public event System.Action<ThreadListPaneGroupViewModel>? ThreadListGroupEmptied;

    /// <summary>一覧ペインの Tabs の CollectionChanged。Move は無視、空化通知、close 副作用 (復元履歴 push)。</summary>
    private void OnThreadListTabsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Move) return;

        if (ThreadListPaneGroups.FirstOrDefault(g => ReferenceEquals(g.Tabs, sender)) is { Tabs.Count: 0 } emptied)
            ThreadListGroupEmptied?.Invoke(emptied);

        if (SuppressTabCloseSideEffects) return;
        if (e.OldItems is null) return;
        foreach (var item in e.OldItems)
            if (item is ThreadListTabViewModel t) PushRecentlyClosedThreadListTab(t);
    }
}
