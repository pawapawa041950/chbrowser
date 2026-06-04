namespace ChBrowser.ViewModels;

/// <summary>1 枚のスレッド表示ペインが抱える「タブ集合 + 選択タブ」(複数ペイン化)。
/// 共通基底 <see cref="PaneGroupViewModel{TTab}"/> にロジックを寄せ、ここは選択変更通知先だけを指定する。
/// 各 <see cref="Views.Panes.ThreadDisplayPane"/> インスタンスはこの VM を DataContext にする。</summary>
public sealed class ThreadPaneGroupViewModel : PaneGroupViewModel<ThreadTabViewModel>
{
    public ThreadPaneGroupViewModel(MainViewModel main, string paneKey) : base(main, paneKey) { }

    protected override void NotifySelectionChanged(ThreadTabViewModel? value)
        => Main.OnGroupSelectedTabChanged(this, value);
}
