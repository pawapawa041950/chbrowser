using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChBrowser.ViewModels;

/// <summary>1 枚のスレッド表示ペインが抱える「タブ集合 + 選択タブ」(複数ペイン化, Phase 2)。
///
/// <para>従来 <see cref="MainViewModel"/> が単一で持っていた <c>ThreadTabs</c> / <c>SelectedThreadTab</c> を
/// ペイン単位に切り出したもの。各 <see cref="Views.Panes.ThreadDisplayPane"/> インスタンスはこの VM を
/// DataContext にして自分のタブ集合だけを表示する。アプリ全体の設定 (タブ幅 / AI NG しきい値 / 設定 JSON 等) や
/// 横断操作 (スレを開く・閉じる・差分取得) は <see cref="Main"/> 経由で参照する。</para>
///
/// <para><see cref="PaneKey"/> はレイアウトツリー (<see cref="Models.LeafLayoutNode.Key"/>) と対応する一意キー。
/// 最初の (静的) ペインは "ThreadDisplay"、Phase 3 で動的生成されるペインは "ThreadDisplay:&lt;id&gt;"。</para></summary>
public sealed partial class ThreadPaneGroupViewModel : ObservableObject
{
    /// <summary>アプリ全体の ViewModel への back-reference (= ペインをまたぐ操作・共有設定の参照元)。</summary>
    public MainViewModel Main { get; }

    /// <summary>レイアウトエンジン / 永続化と対応する一意キー (= PaneKinds.MakeKey)。</summary>
    public string PaneKey { get; set; }

    /// <summary>このペインに属するスレッドタブ。</summary>
    public ObservableCollection<ThreadTabViewModel> Tabs { get; } = new();

    /// <summary>このペインで現在選択中のタブ。</summary>
    [ObservableProperty]
    private ThreadTabViewModel? _selectedTab;

    public ThreadPaneGroupViewModel(MainViewModel main, string paneKey)
    {
        Main    = main;
        PaneKey = paneKey;
    }

    partial void OnSelectedTabChanged(ThreadTabViewModel? value)
    {
        // 各タブの WebView2 は専属インスタンス (ItemsControl 並列描画)。このペインのタブだけ、
        // 選択タブを可視 (IsSelected=true)・他を不可視にする。ペインごとに独立して効く。
        foreach (var t in Tabs) t.IsSelected = ReferenceEquals(t, value);
        // アクティブペインなら、ステータスバー / アドレスバー / AI チャット文脈 / AI NG をこの選択タブに追従させる。
        Main.OnGroupSelectedTabChanged(this, value);
    }
}
