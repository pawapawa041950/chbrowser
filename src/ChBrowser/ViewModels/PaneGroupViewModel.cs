using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChBrowser.ViewModels;

/// <summary>ペイン内に並ぶ「タブ」の共通契約 (複数ペイン化)。
/// 各タブの WebView2 の可視制御を、所属ペインが選択タブに合わせて切り替えるために使う。</summary>
public interface IPaneTab
{
    /// <summary>このタブが所属ペインで選択中か (= WebView2 の Visibility にバインド)。</summary>
    bool IsSelected { get; set; }
}

/// <summary>種類 (スレ表示 / スレ一覧) を問わずペインを扱うための非総称インターフェース (複数ペイン化)。
/// タブ D&amp;D のドロップ位置計算 (= 種類共通) で、どちらのグループでも同じコードで挿入位置を求めるために使う。</summary>
public interface IPaneGroup
{
    /// <summary>レイアウト / 永続化と対応する一意キー。</summary>
    string PaneKey { get; }
    /// <summary>現在のタブを <see cref="IPaneTab"/> 列としてスナップショットする (= 挿入位置の index 計算用)。</summary>
    IReadOnlyList<IPaneTab> TabsSnapshot { get; }
}

/// <summary>1 枚のペインが抱える「タブ集合 + 選択タブ」の汎用基底 (複数ペイン化)。
/// スレ表示 (<see cref="ThreadPaneGroupViewModel"/>) とスレ一覧 (<see cref="ThreadListPaneGroupViewModel"/>) で共有する。
///
/// <para>各ペインの UserControl はこの VM を DataContext にして自分のタブ集合だけを表示する。アプリ全体の設定や
/// 横断操作は <see cref="Main"/> 経由で参照する。<see cref="PaneKey"/> はレイアウトツリー
/// (<see cref="Models.LeafLayoutNode.Key"/>) と対応する一意キー。</para></summary>
public abstract class PaneGroupViewModel<TTab> : ObservableObject, IPaneGroup where TTab : class, IPaneTab
{
    IReadOnlyList<IPaneTab> IPaneGroup.TabsSnapshot => Tabs.Cast<IPaneTab>().ToList();

    /// <summary>アプリ全体の ViewModel への back-reference (= ペインをまたぐ操作・共有設定の参照元)。</summary>
    public MainViewModel Main { get; }

    /// <summary>レイアウトエンジン / 永続化と対応する一意キー (= PaneKinds.MakeKey)。</summary>
    public string PaneKey { get; set; }

    /// <summary>このペインに属するタブ。</summary>
    public ObservableCollection<TTab> Tabs { get; } = new();

    private TTab? _selectedTab;
    /// <summary>このペインで現在選択中のタブ。</summary>
    public TTab? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (ReferenceEquals(_selectedTab, value)) return;
            _selectedTab = value;
            OnPropertyChanged();
            // 各タブの WebView2 は専属インスタンス (ItemsControl 並列描画)。このペインのタブだけ、
            // 選択タブを可視 (IsSelected=true)・他を不可視にする。ペインごとに独立して効く。
            foreach (var t in Tabs) t.IsSelected = ReferenceEquals(t, value);
            // アクティブペインなら、ステータス等をこの選択タブに追従させる (派生クラスが Main へ通知)。
            NotifySelectionChanged(value);
        }
    }

    protected PaneGroupViewModel(MainViewModel main, string paneKey)
    {
        Main    = main;
        PaneKey = paneKey;
    }

    /// <summary>選択タブが変わったときに Main へ通知する (種類ごとに通知先メソッドが異なる)。</summary>
    protected abstract void NotifySelectionChanged(TTab? value);
}
