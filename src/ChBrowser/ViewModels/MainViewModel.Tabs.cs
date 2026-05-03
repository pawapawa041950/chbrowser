using System.Linq;

namespace ChBrowser.ViewModels;

/// <summary>タブ操作 (各タブ種別の close-others / before / after / 設定駆動アクション dispatch)。</summary>
public sealed partial class MainViewModel
{
    /// <summary>スレ一覧タブに対するアクションを実行 (= 設定で割り当てた動作)。</summary>
    public void ExecuteThreadListTabAction(ThreadListTabViewModel tab, string action)
    {
        switch (action)
        {
            case "close":
                tab.CloseCommand.Execute(null);
                break;
            case "refresh":
                // 板タブのみ更新可能 (お気に入りディレクトリ展開タブは Board=null)
                if (tab.Board is not null)
                    _ = LoadThreadListAsync(new BoardViewModel(tab.Board));
                break;
            case "closeOthers": CloseOtherThreadListTabs(tab);  break;
            case "closeLeft":   CloseThreadListTabsBefore(tab); break;
            case "closeRight":  CloseThreadListTabsAfter(tab);  break;
            // none / addFavorite / deleteLog はスレ一覧タブには適用しない
        }
    }

    /// <summary>スレッドタブに対するアクションを実行。</summary>
    public void ExecuteThreadTabAction(ThreadTabViewModel tab, string action)
    {
        switch (action)
        {
            case "close":       tab.CloseCommand.Execute(null);          break;
            case "refresh":     tab.RefreshCommand.Execute(null);        break;
            case "addFavorite": tab.AddToFavoritesCommand.Execute(null); break;
            case "deleteLog":   tab.DeleteCommand.Execute(null);         break;
            case "closeOthers": CloseOtherThreadTabs(tab);  break;
            case "closeLeft":   CloseThreadTabsBefore(tab); break;
            case "closeRight":  CloseThreadTabsAfter(tab);  break;
        }
    }

    private void CloseOtherThreadTabs(ThreadTabViewModel keep)
    {
        // 削除中のコレクション変更を避けるためスナップショットしてから処理
        foreach (var t in ThreadTabs.ToList())
            if (!ReferenceEquals(t, keep)) ThreadTabs.Remove(t);
    }

    private void CloseThreadTabsBefore(ThreadTabViewModel pivot)
    {
        var idx = ThreadTabs.IndexOf(pivot);
        if (idx <= 0) return;
        for (var i = idx - 1; i >= 0; i--) ThreadTabs.RemoveAt(i);
    }

    private void CloseThreadTabsAfter(ThreadTabViewModel pivot)
    {
        var idx = ThreadTabs.IndexOf(pivot);
        if (idx < 0) return;
        while (ThreadTabs.Count > idx + 1) ThreadTabs.RemoveAt(ThreadTabs.Count - 1);
    }

    private void CloseOtherThreadListTabs(ThreadListTabViewModel keep)
    {
        foreach (var t in ThreadListTabs.ToList())
            if (!ReferenceEquals(t, keep)) ThreadListTabs.Remove(t);
    }

    private void CloseThreadListTabsBefore(ThreadListTabViewModel pivot)
    {
        var idx = ThreadListTabs.IndexOf(pivot);
        if (idx <= 0) return;
        for (var i = idx - 1; i >= 0; i--) ThreadListTabs.RemoveAt(i);
    }

    private void CloseThreadListTabsAfter(ThreadListTabViewModel pivot)
    {
        var idx = ThreadListTabs.IndexOf(pivot);
        if (idx < 0) return;
        while (ThreadListTabs.Count > idx + 1) ThreadListTabs.RemoveAt(ThreadListTabs.Count - 1);
    }
}
