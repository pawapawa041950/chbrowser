using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ChBrowser.ViewModels;

/// <summary>タブ操作 (各タブ種別の close-others / before / after / 設定駆動アクション dispatch)。</summary>
public sealed partial class MainViewModel
{
    // ----- 直前に閉じたスレタブの再オープン履歴 (= "タブを閉じる" を空タブ領域で発動 → 復元) -----
    /// <summary>履歴上限。これを超えたら古いものから捨てる。</summary>
    private const int MaxRecentlyClosedThreadTabs = 50;

    /// <summary>閉じたスレタブを再オープンするのに必要な最小情報。
    /// idx.json に永続化されている scroll/own posts は <see cref="OpenThreadFromListAsync"/> 経由で復元される。</summary>
    private readonly record struct ClosedThreadTabRef(string Host, string Dir, string Key, string Title);

    private readonly List<ClosedThreadTabRef> _recentlyClosedThreadTabs = new();

    /// <summary>true の間、<see cref="OnThreadTabsCollectionChanged"/> での履歴積みをスキップする。
    /// <see cref="DeleteThreadLog(Board,string,string)"/> のように「ユーザが意図して破棄した」経路では
    /// 復元すると意図と逆方向 (= 削除したログの再 fetch) になるため積まない。</summary>
    private bool _suppressClosedTabHistory;

    /// <summary>スレタブを閉じた時に呼ばれる (= <see cref="OnThreadTabsCollectionChanged"/> から)。
    /// 履歴の末尾に push、上限を超えたら先頭から間引く。</summary>
    internal void PushRecentlyClosedThreadTab(ThreadTabViewModel tab)
    {
        if (_suppressClosedTabHistory) return;
        if (tab.Board is null || string.IsNullOrEmpty(tab.ThreadKey)) return;
        var entry = new ClosedThreadTabRef(tab.Board.Host, tab.Board.DirectoryName, tab.ThreadKey, tab.Title ?? tab.Header ?? "");
        _recentlyClosedThreadTabs.Add(entry);
        while (_recentlyClosedThreadTabs.Count > MaxRecentlyClosedThreadTabs)
            _recentlyClosedThreadTabs.RemoveAt(0);
    }

    /// <summary>履歴の末尾 (= 直近に閉じたスレ) を 1 件取り出して再オープンする。
    /// 連続呼び出しでさかのぼっていく。履歴が空なら no-op。</summary>
    public Task ReopenRecentlyClosedThreadTabAsync()
    {
        if (_recentlyClosedThreadTabs.Count == 0) return Task.CompletedTask;
        var idx = _recentlyClosedThreadTabs.Count - 1;
        var entry = _recentlyClosedThreadTabs[idx];
        _recentlyClosedThreadTabs.RemoveAt(idx);
        return OpenThreadFromListAsync(entry.Host, entry.Dir, entry.Key, entry.Title);
    }

    /// <summary>履歴を保ったまま、suppress フラグを使って一時的に履歴積みを止める scope。
    /// using 文で囲むと自動解除される。</summary>
    internal System.IDisposable BeginSuppressClosedTabHistory()
    {
        _suppressClosedTabHistory = true;
        return new SuppressScope(this);
    }
    private sealed class SuppressScope : System.IDisposable
    {
        private readonly MainViewModel _vm;
        public SuppressScope(MainViewModel vm) { _vm = vm; }
        public void Dispose() => _vm._suppressClosedTabHistory = false;
    }

    // ----- 直前に閉じたスレ一覧タブの再オープン履歴 -----
    /// <summary>閉じたスレ一覧タブの復元レシピ。タブ種別 (板タブ / お気に入り展開 / etc.) ごとに
    /// 異なる open メソッドを呼び分ける必要があるため、close 時にレシピを closure として保持する。</summary>
    private sealed record ClosedThreadListTabRef(string Header, System.Func<MainViewModel, Task> Reopen);

    private readonly List<ClosedThreadListTabRef> _recentlyClosedThreadListTabs = new();
    private const int MaxRecentlyClosedThreadListTabs = 50;

    /// <summary>スレ一覧タブを閉じた時に呼ばれる。タブ種別から復元レシピを組み立てて履歴に積む。
    /// 板タブ → <see cref="LoadThreadListAsync"/>、お気に入り集約 (root / folder) → 専用 open API。
    /// 復元レシピを構築できないタブ (= 「次スレ候補検索」のように元情報を ViewModel が持たない種別) は
    /// 履歴に積まない (= 復元不能なエントリで pop が空振りするのを避ける)。</summary>
    internal void PushRecentlyClosedThreadListTab(ThreadListTabViewModel tab)
    {
        if (_suppressClosedTabHistory) return;

        System.Func<MainViewModel, Task>? reopen = null;

        if (tab.Board is not null)
        {
            var board = tab.Board;
            reopen = vm => vm.LoadThreadListAsync(new BoardViewModel(board));
        }
        else if (tab.FavoritesFolderId is System.Guid id)
        {
            if (id == System.Guid.Empty)
            {
                reopen = vm => vm.OpenAllRootAsBoardAsync();
            }
            else
            {
                // 既存お気に入りフォルダの ID なら復元可能。
                // 無ければ「次スレ候補検索」等の非フォルダ ID なので復元レシピが組めない → push しない。
                if (Favorites.FindById(id) is FavoriteFolderViewModel)
                {
                    var capturedId = id;
                    reopen = vm =>
                    {
                        if (vm.Favorites.FindById(capturedId) is FavoriteFolderViewModel f)
                            return vm.OpenFavoritesFolderAsync(f);
                        return Task.CompletedTask;
                    };
                }
            }
        }

        if (reopen is null) return;
        _recentlyClosedThreadListTabs.Add(new ClosedThreadListTabRef(tab.Header, reopen));
        while (_recentlyClosedThreadListTabs.Count > MaxRecentlyClosedThreadListTabs)
            _recentlyClosedThreadListTabs.RemoveAt(0);
    }

    /// <summary>履歴の末尾を 1 件取り出して再オープンする。空き領域中クリック等から呼ばれる。</summary>
    public Task ReopenRecentlyClosedThreadListTabAsync()
    {
        if (_recentlyClosedThreadListTabs.Count == 0) return Task.CompletedTask;
        var idx = _recentlyClosedThreadListTabs.Count - 1;
        var entry = _recentlyClosedThreadListTabs[idx];
        _recentlyClosedThreadListTabs.RemoveAt(idx);
        return entry.Reopen(this);
    }


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
