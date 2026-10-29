using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Api;
using ChBrowser.Views;

namespace ChBrowser.ViewModels;

/// <summary>お気に入り (folder/board/thread) の編集・展開・チェック・集約タブ生成。</summary>
public sealed partial class MainViewModel
{
    /// <summary>板を「お気に入り」のルート直下に追加。重複チェックあり。</summary>
    public void AddBoardToFavorites(BoardViewModel boardVm)
    {
        var b = boardVm.Board;
        if (Favorites.ContainsBoardAtRoot(b.Host, b.DirectoryName))
        {
            StatusMessage = $"{b.BoardName} は既にお気に入りに登録済みです";
            return;
        }
        Favorites.AddRoot(new FavoriteBoard
        {
            Host          = b.Host,
            DirectoryName = b.DirectoryName,
            BoardName     = b.BoardName,
        });
        StatusMessage = $"{b.BoardName} をお気に入りに追加しました";
        RefreshFavoritedStateOfAllTabs();
    }

    /// <summary>板お気に入りトグル: 既に登録済 (フォルダ含むツリー上のどこか) なら外す、
    /// 未登録ならルート直下に追加する。<see cref="ToggleThreadFavorite"/> の板版。</summary>
    public void ToggleBoardFavorite(Board board)
    {
        var existing = Favorites.FindBoard(board.Host, board.DirectoryName);
        if (existing is not null)
        {
            Favorites.Remove(existing);
            StatusMessage = $"{board.BoardName} をお気に入りから外しました";
        }
        else
        {
            Favorites.AddRoot(new FavoriteBoard
            {
                Host          = board.Host,
                DirectoryName = board.DirectoryName,
                BoardName     = board.BoardName,
            });
            StatusMessage = $"{board.BoardName} をお気に入りに追加しました";
        }
        RefreshFavoritedStateOfAllTabs();
    }

    /// <summary>スレ ★ ボタン押下: 既に登録済みなら外す、未登録なら追加する (トグル)。
    /// タブが開いていない経路 (= スレ一覧の行を右クリック) からも使えるよう、
    /// プリミティブ受けの <see cref="ToggleThreadFavorite(Board, string, string)"/> に委譲する。</summary>
    public void ToggleThreadFavorite(ThreadTabViewModel tab)
        => ToggleThreadFavorite(tab.Board, tab.ThreadKey, tab.Title);

    /// <summary>(板, スレキー, スレタイトル) を引数に取るプリミティブ版。
    /// スレタブを開かず、スレ一覧の行などから直接トグルしたい時に使う。</summary>
    public void ToggleThreadFavorite(Board board, string threadKey, string title)
    {
        var existing = Favorites.FindThread(board.Host, board.DirectoryName, threadKey);
        if (existing is not null)
        {
            Favorites.Remove(existing);
            StatusMessage = $"{title} をお気に入りから外しました";
        }
        else
        {
            Favorites.AddRoot(new FavoriteThread
            {
                Host          = board.Host,
                DirectoryName = board.DirectoryName,
                ThreadKey     = threadKey,
                Title         = title,
                BoardName     = board.BoardName,
            });
            StatusMessage = $"{title} をお気に入りに追加しました";
        }
        RefreshFavoritedStateOfAllTabs();
    }

    /// <summary>お気に入り状態の変化を、開いている全タブにリアルタイム反映する (Phase 18+)。
    /// ThreadTab: ★ ボタンの押下表示 / ThreadListTab: 各行の is-favorited クラス。</summary>
    public void RefreshFavoritedStateOfAllTabs()
    {
        var favKeys = Favorites.CollectFavoriteThreadKeys();

        foreach (var tab in AllThreadTabs)
        {
            var b = tab.Board;
            tab.IsFavorited = favKeys.Contains((b.Host, b.DirectoryName, tab.ThreadKey));
        }

        foreach (var listTab in AllThreadListTabs)
        {
            listTab.SyncFavoritedFromKeySet(favKeys);
            // 板タブ自身がお気に入り登録済みか (= スレ一覧ペインのツールバー★ボタンの押下状態)
            listTab.IsBoardFavorited = listTab.Board is not null
                && Favorites.FindBoard(listTab.Board.Host, listTab.Board.DirectoryName) is not null;
        }
    }

    /// <summary>D&amp;D による移動を実行。target が null なら root 末尾、folder なら配下、それ以外は target の直後に。</summary>
    public void MoveFavoriteEntry(FavoriteEntryViewModel source, FavoriteEntryViewModel? target)
    {
        if (!Favorites.CanMove(source, target)) return;
        Favorites.Move(source, target);
        RefreshFavoritedStateOfAllTabs();
    }

    /// <summary>お気に入りペインへツリーを送る (<c>setFavorites</c>)。Favorites.Changed 発火のたび、
    /// およびページ (シェル) を読み込んだ JS の <c>ready</c> で呼ばれる。ページは読み直さない。</summary>
    public void PushFavorites()
        => FavoritesPush = new FavoritesMessage(ChBrowser.Services.Render.FavoritesHtmlBuilder.BuildTree(Favorites.Items));

    /// <summary>お気に入りペインのシェル HTML を (CSS の変更を反映して) 作り直す。中身が変わればページが読み直され、
    /// JS の <c>ready</c> でツリーが送り直される。</summary>
    public void RefreshFavoritesHtml()
        => FavoritesHtml = ChBrowser.Services.Render.FavoritesHtmlBuilder.BuildShell();

    /// <summary>JS の openFavorite メッセージから呼ばれる。
    /// id を ViewModel ツリーから引いて種別ごとに既存メソッドへルーティング。</summary>
    public Task OpenFavoriteByIdAsync(Guid id)
    {
        var vm = Favorites.FindById(id);
        return vm switch
        {
            FavoriteFolderViewModel f => OpenFavoritesFolderAsync(f),
            FavoriteBoardViewModel  b => OpenFavoriteBoardAsync(b),
            FavoriteThreadViewModel t => OpenFavoriteThreadAsync(t),
            _ => Task.CompletedTask,
        };
    }

    /// <summary>JS の setFolderExpanded メッセージから呼ばれる。
    /// HTML を再生成しない (DOM 上で details の open はトグル済み)。</summary>
    public void SetFolderExpanded(Guid id, bool expanded)
    {
        if (Favorites.FindById(id) is FavoriteFolderViewModel folder)
            folder.IsExpanded = expanded;
    }

    /// <summary>JS の moveFavorite メッセージから呼ばれる。
    /// position が 'inside' ならフォルダ配下に、'before'/'after' なら兄弟として前後に挿入、
    /// 'rootEnd' (空エリアにドロップ) なら root 末尾。</summary>
    public void MoveFavoriteByIds(Guid sourceId, Guid? targetId, string position)
    {
        var src = Favorites.FindById(sourceId);
        if (src is null) return;

        if (targetId is null || position == "rootEnd")
        {
            Favorites.MoveToRootEnd(src);
            return;
        }

        var dst = Favorites.FindById(targetId.Value);
        if (dst is null) return;
        if (src == dst) return;

        switch (position)
        {
            case "inside":
                if (dst is FavoriteFolderViewModel folder)
                    Favorites.MoveIntoFolder(src, folder);
                break;
            case "before":
                Favorites.MoveAsSiblingBefore(src, dst);
                break;
            case "after":
            default:
                Favorites.MoveAsSiblingAfter(src, dst);
                break;
        }
    }

    /// <summary>お気に入りペインの板エントリを開く (= 通常の板スレ一覧を新規タブで開く)。</summary>
    public Task OpenFavoriteBoardAsync(FavoriteBoardViewModel favBoardVm)
    {
        var b      = favBoardVm.Model;
        var board  = ResolveBoard(b.Host, b.DirectoryName, b.BoardName);
        var dummy  = new BoardViewModel(board);
        return LoadThreadListAsync(dummy);
    }

    /// <summary>お気に入りペインのスレエントリを開く (= 対象スレを新規タブで開く)。</summary>
    public Task OpenFavoriteThreadAsync(FavoriteThreadViewModel favThreadVm)
    {
        var t = favThreadVm.Model;
        return OpenThreadFromListAsync(t.Host, t.DirectoryName, t.ThreadKey, t.Title);
    }

    /// <summary>「すべて開く」: フォルダ配下の全 board / thread を再帰的に展開し、
    /// 板はそのまま板タブ、スレはそのままスレタブで開く。</summary>
    public Task OpenAllInFolderAsync(FavoriteFolderViewModel folder)
        => OpenAllEntriesAsync(folder.Children);

    /// <summary>「すべて開く」: お気に入り全体 (仮想ルート) で同様に開く。</summary>
    public Task OpenAllInRootAsync()
        => OpenAllEntriesAsync(Favorites.Items);

    private async Task OpenAllEntriesAsync(IEnumerable<FavoriteEntryViewModel> entries)
    {
        var boards  = new List<FavoriteBoard>();
        var threads = new List<FavoriteThread>();
        foreach (var vm in entries) CollectFavoriteEntriesFromVm(vm, boards, threads);

        if (boards.Count == 0 && threads.Count == 0)
        {
            StatusMessage = "開く対象がありません";
            return;
        }

        StatusMessage = $"お気に入りを一括オープン中... (板 {boards.Count} / スレ {threads.Count})";

        // 一括オープン中は各タブの自動アクティブ化を抑止 (= 連続追加でペインが切り替わるとチカチカするため)。
        // ※ activate:false はこの一括ループ自身にだけ作用する (= 進行中にユーザが板一覧から開いたタブは
        // 普通通り activate:true で来るので、ユーザの単発操作はちゃんと選択される)。
        foreach (var fb in boards)
        {
            var board = ResolveBoard(fb.Host, fb.DirectoryName, fb.BoardName);
            await LoadThreadListAsync(new BoardViewModel(board), activate: false).ConfigureAwait(true);
        }
        foreach (var ft in threads)
        {
            await OpenThreadFromListAsync(ft.Host, ft.DirectoryName, ft.ThreadKey, ft.Title, activate: false).ConfigureAwait(true);
        }

        StatusMessage = $"お気に入りオープン完了: 板 {boards.Count} / スレ {threads.Count}";
    }

    /// <summary>「板として開く」: お気に入り全体 (仮想ルート) を 1 つの統合スレ一覧タブで開く。</summary>
    public Task OpenAllRootAsBoardAsync()
        => OpenFavoritesAggregateTabAsync(Guid.Empty, "お気に入り", Favorites.Items);

    /// <summary>お気に入りフォルダを開いてその中身を 1 枚のスレ一覧として表示する。
    /// folderVm.Children (ObservableCollection) を walk し、folderVm.Model.Children は使わない
    /// (ロード時のスナップショットで D&amp;D の在memory編集を反映しないため)。</summary>
    public Task OpenFavoritesFolderAsync(FavoriteFolderViewModel folderVm)
        => OpenFavoritesAggregateTabAsync(folderVm.Model.Id, folderVm.DisplayName, folderVm.Children);

    /// <summary>お気に入りフォルダ (or 仮想ルート) の中身を 1 枚のスレ一覧として表示する共通実装。</summary>
    private Task OpenFavoritesAggregateTabAsync(
        Guid aggregateId,
        string aggregateName,
        IEnumerable<FavoriteEntryViewModel> children)
    {
        var existingTab = AllThreadListTabs.FirstOrDefault(t => t.FavoritesFolderId == aggregateId);
        if (existingTab is not null)
        {
            ActivateThreadListTab(existingTab);
            return Task.CompletedTask;
        }

        var tab = new ThreadListTabViewModel(aggregateId, aggregateName, t => RemoveThreadListTab(t));
        ThreadListTabs.Add(tab);
        ActivateThreadListTab(tab);
        return BuildFavoritesAggregateItemsAsync(tab, aggregateName, children);
    }

    /// <summary>お気に入り集約タブ (フォルダの「板として開く」/ 仮想ルートの「板として開く」) の
    /// items を再構築する (= subject.txt 再取得 → Dropped / 新着判定 → タブに反映)。</summary>
    private async Task BuildFavoritesAggregateItemsAsync(
        ThreadListTabViewModel tab,
        string aggregateName,
        IEnumerable<FavoriteEntryViewModel> children)
    {
        try
        {
            tab.IsBusy        = true;
            tab.StatusMessage = $"お気に入り「{aggregateName}」を取得中...";

            // VM ツリーを再帰的に走って thread エントリを集める。
            // 「板として開く」では fav board エントリは展開せず、登録スレだけを表示する。
            var boards  = new List<FavoriteBoard>();
            var threads = new List<FavoriteThread>();
            foreach (var child in children)
                CollectFavoriteEntriesFromVm(child, boards, threads);

            // 「板として開く」では更新チェックは行わない (= サーバには問い合わせない)。
            // ローカルにキャッシュ済みの subject.txt があればそれを使い、無ければ空扱い。
            // 「Dropped (subject.txt から消えた)」「Updated (新着あり)」のマーク色は付けず、
            // dat の有無だけで Cached/None を出す (= BuildLogStates は読了状態の比較ベースなので、
            // 直近の subject.txt が空でも誤判定はしない、ただし Updated は出ない)。
            var subjectByBoard = new Dictionary<(string host, string dir), IReadOnlyList<ThreadInfo>>();
            var resolvedBoards = new Dictionary<(string host, string dir), Board>();
            foreach (var ft in threads)
            {
                var key = (ft.Host, ft.DirectoryName);
                if (subjectByBoard.ContainsKey(key)) continue;
                var board = ResolveBoard(ft.Host, ft.DirectoryName, ft.BoardName);
                resolvedBoards[key] = board;
                try
                {
                    subjectByBoard[key] = await _subjectClient.LoadFromDiskAsync(board).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Favorites] subject {ft.Host}/{ft.DirectoryName} (disk only) 失敗: {ex.Message}");
                    subjectByBoard[key] = Array.Empty<ThreadInfo>();
                }
            }

            // ログ状態は板ごとにまとめて 1 回で判定する (BuildLogStates は板フォルダの *.dat を列挙するので、
            // スレ 1 件ごとに呼ぶと、お気に入りの数だけ板フォルダを列挙し直して UI スレッドが止まる)
            var statesByBoard = new Dictionary<(string host, string dir), IReadOnlyDictionary<string, LogMarkState>>();
            foreach (var (key, infos) in subjectByBoard)
            {
                var keys   = threads.Where(t => (t.Host, t.DirectoryName) == key).Select(t => t.ThreadKey).ToHashSet(StringComparer.Ordinal);
                var listed = infos.Where(t => keys.Contains(t.Key)).ToList();
                statesByBoard[key] = listed.Count == 0
                    ? new Dictionary<string, LogMarkState>()
                    : BuildLogStates(resolvedBoards[key], listed);
            }

            var items     = new List<ThreadListItem>();
            var addedKeys = new HashSet<(string, string, string)>();

            foreach (var ft in threads)
            {
                var key = (ft.Host, ft.DirectoryName);
                if (!addedKeys.Add((ft.Host, ft.DirectoryName, ft.ThreadKey))) continue;

                var infos = subjectByBoard.TryGetValue(key, out var v) ? v : Array.Empty<ThreadInfo>();
                var info  = infos.FirstOrDefault(t => t.Key == ft.ThreadKey);
                LogMarkState state;
                if (info is null)
                {
                    // 「板として開く」ではネットワークアクセスをしないため、subject.txt キャッシュ自体が
                    // 無い (= infos が空) ケースもありうる。その場合は Dropped と決めつけずに
                    // ローカルの dat があれば Cached、なければ None として表示する (= 誤情報を出さない)。
                    info = new ThreadInfo(ft.ThreadKey, ft.Title, 0, 0);
                    if (infos.Count == 0)
                    {
                        var board = resolvedBoards[key];
                        var datExists = System.IO.File.Exists(_paths.DatPath(board.Host, board.DirectoryName, ft.ThreadKey));
                        state = datExists ? LogMarkState.Cached : LogMarkState.None;
                    }
                    else
                    {
                        // subject.txt は読めたが該当 key が無い = 本当に dat 落ちと判断
                        state = LogMarkState.Dropped;
                    }
                }
                else
                {
                    state = statesByBoard[key].TryGetValue(info.Key, out var s) ? s : LogMarkState.None;
                }
                items.Add(new ThreadListItem(info, ft.Host, ft.DirectoryName, ft.BoardName, state, IsFavorited: true));
            }

            tab.SetItems(items, DateTimeOffset.UtcNow);
            tab.Header        = $"★ {aggregateName} ({items.Count})";
            tab.StatusMessage = $"お気に入り「{aggregateName}」: {items.Count} 件";
        }
        catch (Exception ex)
        {
            tab.StatusMessage = $"お気に入り展開失敗: {ex.Message}";
        }
        finally
        {
            tab.IsBusy = false;
        }
    }

    /// <summary>VM ツリーを再帰的に走査して board/thread エントリを収集する (フォルダ自身は無視)。</summary>
    private static void CollectFavoriteEntriesFromVm(
        FavoriteEntryViewModel  vm,
        List<FavoriteBoard>     boards,
        List<FavoriteThread>    threads)
    {
        switch (vm)
        {
            case FavoriteFolderViewModel f:
                foreach (var c in f.Children) CollectFavoriteEntriesFromVm(c, boards, threads);
                break;
            case FavoriteBoardViewModel  b: boards.Add(b.Model);  break;
            case FavoriteThreadViewModel t: threads.Add(t.Model); break;
        }
    }

    // ---- お気に入りペインの編集操作 ----

    /// <summary>新規フォルダを <paramref name="parent"/> 直下 (null ならルート直下) に作成する。</summary>
    public void NewFavoriteFolder(FavoriteFolderViewModel? parent, string name)
    {
        var folder = new FavoriteFolder { Name = name };
        if (parent is null) Favorites.AddRoot(folder);
        else                Favorites.AddInto(parent, folder);
        StatusMessage = $"フォルダ「{name}」を作成しました";
    }

    /// <summary>フォルダ名を変更して保存。
    /// <see cref="FavoritesViewModel.RenameFolder"/> 経由で呼ぶことで Save + Changed 通知が同時に走り、
    /// MainViewModel が購読している RefreshFavoritesHtml が連動して再描画される。</summary>
    public void RenameFavoriteFolder(FavoriteFolderViewModel folder, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || folder.Name == newName) return;
        Favorites.RenameFolder(folder, newName);
        StatusMessage = $"フォルダ名を「{newName}」に変更しました";
    }

    /// <summary>エントリを削除して保存。フォルダ削除時は子もまとめて消える。</summary>
    public void DeleteFavoriteEntry(FavoriteEntryViewModel vm)
    {
        Favorites.Remove(vm);
        StatusMessage = $"「{vm.DisplayName}」を削除しました";
        RefreshFavoritedStateOfAllTabs();
    }

    /// <summary>兄弟内で 1 つ上に移動。</summary>
    public void MoveFavoriteEntryUp(FavoriteEntryViewModel vm)   => Favorites.MoveUp(vm);

    /// <summary>兄弟内で 1 つ下に移動。</summary>
    public void MoveFavoriteEntryDown(FavoriteEntryViewModel vm) => Favorites.MoveDown(vm);

    // ---- お気に入りチェック ----

    /// <summary>「お気に入りチェック」: お気に入り登録スレの subject.txt を確認し、
    /// 新着があるスレを新規タブで開く。dat 落ちしたスレ (一覧が全スレを載せる掲示板で一覧から消えた / 取得で 404) は、
    /// 保存済みのログを「dat 落ち」の状態でタブに出す (お気に入りから外す判断ができるように)。
    /// subject.txt 取得 / dat 取得の通信本数は合算で <see cref="AppConfig.BatchConcurrency"/> までに制限する。
    /// 一覧の取れた板から判定し、取得の済んだスレから順にタブへ出す (全部の取得を待たない)。</summary>
    public async Task CheckFavoritesAsync()
    {
        // 多数の要求をまとめて投げる自動処理: reddit のログインが切れていてもログイン窓は出さない (doc/reddit-design.md D37)
        using var background = ChBrowser.Services.Bbs.ProviderRequestContext.Background();
        var boards  = new List<FavoriteBoard>();
        var threads = new List<FavoriteThread>();
        foreach (var topVm in Favorites.Items)
        {
            CollectFavoriteEntriesFromVm(topVm, boards, threads);
        }

        if (threads.Count == 0)
        {
            StatusMessage = "お気に入りスレ無し: チェックスキップ";
            return;
        }

        // 出元板 (= 登録スレが属する板) のユニーク集合。fav board は新仕様では展開しないので無視。
        var resolved = new Dictionary<(string, string), Board>();
        foreach (var ft in threads)
        {
            var key = (ft.Host, ft.DirectoryName);
            if (resolved.ContainsKey(key)) continue;
            resolved[key] = ResolveBoard(ft.Host, ft.DirectoryName, ft.BoardName);
        }

        // バッチ全体で 1 つの semaphore を共有 (合算で BatchConcurrency 本まで)
        var concurrency = Math.Max(1, CurrentConfig.BatchConcurrency);
        using var sem   = new SemaphoreSlim(concurrency, concurrency);
        var dispatcher  = System.Windows.Application.Current.Dispatcher;

        // 板ごとに「一覧の取得 → 新着の判定 → 新着スレの取得 → 表示」を流す。一覧の取れた板から判定し、取得の済んだスレから
        // 順に表示する (= reddit 等の遅い板を待たずに、他の板のスレは先に読める)。表示 (タブへの反映) は UI スレッドで 1 本ずつ。
        var threadsByBoard = threads.GroupBy(ft => (ft.Host, ft.DirectoryName)).ToDictionary(g => g.Key, g => g.ToList());
        var toDropped   = new List<(Board board, ThreadInfo info)>();   // UI スレッドでだけ触る
        var totalBoards = resolved.Count;
        int subjDone = 0, found = 0, datDone = 0, opened = 0;
        using var openLock = new SemaphoreSlim(1, 1);
        void Report() => StatusMessage = found == 0
            ? $"お気に入りチェック: 一覧 {subjDone}/{totalBoards}"
            : $"お気に入りチェック: 一覧 {subjDone}/{totalBoards}、更新スレ 取得 {datDone}/{found} (表示 {opened})";
        Report();

        async Task FetchAndOpenAsync(Board board, ThreadInfo info)
        {
            DatFetchResult? result = null;
            Exception? error = null;
            await sem.WaitAsync().ConfigureAwait(false);
            try
            {
                var noProgress = new Progress<IReadOnlyList<Post>>(_ => { });
                result = await _datClient.FetchStreamingAsync(board, info.Key, noProgress).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Favorites] dat {board.DirectoryName}/{info.Key} 失敗: {ex.Message}");
                error = ex;
            }
            finally
            {
                sem.Release();
                Interlocked.Increment(ref datDone);
            }

            // 一括追加中はタブ自動アクティブ化を抑止 (activate:false) (= 連続追加でチカチカするため)。
            // ※ ユーザが進行中に板一覧から開くタブは activate:true で来るので普通に選択される。
            await dispatcher.InvokeAsync(async () =>
            {
                await openLock.WaitAsync().ConfigureAwait(true);
                try
                {
                    if (result is null)
                    {
                        // 取得で「スレが無い」(404) と分かった: dat 落ちとして出す
                        if (error is not null && DatFetchResult.IsDroppedError(error)) toDropped.Add((board, info));
                        return;
                    }
                    try { await OpenThreadFromPrefetchedAsync(board, info, result, activate: false).ConfigureAwait(true); }
                    catch (Exception ex) { Debug.WriteLine($"[Favorites] open prefetched failed: {ex.Message}"); }
                    opened++;
                }
                finally
                {
                    openLock.Release();
                    Report();
                }
            }).Task.Unwrap().ConfigureAwait(false);
        }

        async Task CheckBoardAsync((string, string) key, Board board)
        {
            IReadOnlyList<ThreadInfo> infos;
            var ok = false;
            await sem.WaitAsync().ConfigureAwait(false);
            try
            {
                infos = await _subjectClient.FetchAndSaveAsync(board).ConfigureAwait(false);
                ok = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Favorites] subject {key} 失敗: {ex.Message}");
                infos = Array.Empty<ThreadInfo>();
            }
            finally
            {
                sem.Release();
            }

            // ---- 新着判定 (UI スレッドで)。落ちたスレは dat 落ちとして別に集める。 ----
            var opens = await dispatcher.InvokeAsync(() =>
            {
                subjDone++;
                var list = new List<ThreadInfo>();
                foreach (var ft in threadsByBoard[key])
                {
                    var info = infos.FirstOrDefault(t => t.Key == ft.ThreadKey);
                    if (info is null)
                    {
                        // 一覧から消えた: 一覧が全スレを載せる掲示板 (5ch 等) で、一覧を取得できていれば dat 落ち。
                        // 一覧が一部だけの掲示板 (reddit) では「一覧に無い」は落ちたことを意味しないので何もしない
                        if (ok && ChBrowser.Services.Bbs.BbsRegistry.ResolveOrDefault(board.Host).ThreadListIsComplete)
                            toDropped.Add((board, new ThreadInfo(ft.ThreadKey, ft.Title, 0, 0)));
                        continue;
                    }

                    // 新着の判定: 一覧のレス数が「前回取得したレス数」と「前回取得時に一覧に載っていたレス数」の大きい方より多いか。
                    // reddit 等は一覧のコメント数に削除済み・取得しきれないコメントが含まれるので、取得数とだけ比べると毎回開いてしまう。
                    var idx  = _threadIndex.Load(ft.Host, ft.DirectoryName, ft.ThreadKey);
                    var prev = idx?.NewPostBaseline ?? 0;
                    if (info.PostCount > prev) list.Add(info);
                }
                found += list.Count;
                Report();
                return list;
            });
            await Task.WhenAll(opens.Select(info => FetchAndOpenAsync(board, info))).ConfigureAwait(false);
        }

        await Task.WhenAll(resolved.Select(kv => CheckBoardAsync(kv.Key, kv.Value))).ConfigureAwait(true);

        var droppedCount = await OpenDroppedFavoritesAsync(toDropped).ConfigureAwait(true);
        StatusMessage = found == 0
            ? "お気に入りチェック完了: 新着なし" + DroppedSummary(droppedCount)
            : $"お気に入りチェック完了: 更新 {found} 件" + DroppedSummary(droppedCount);
    }

    private static string DroppedSummary(int count) => count > 0 ? $"、dat 落ち {count} 件" : "";

    /// <summary>お気に入りチェックで dat 落ちと分かったスレを、保存済みのログで「dat 落ち」の状態のタブとして出す (通信はしない)。
    /// 開いているタブなら状態だけ変える。ログが無いスレは出せないので数えない。戻り値: 出した件数。</summary>
    private async Task<int> OpenDroppedFavoritesAsync(IReadOnlyList<(Board board, ThreadInfo info)> dropped)
    {
        var count = 0;
        foreach (var (board, info) in dropped)
        {
            try
            {
                var local = await _datClient.LoadFromDiskAsync(board, info.Key).ConfigureAwait(true);
                if (local is null || local.Posts.Count == 0) continue;
                await OpenThreadFromPrefetchedAsync(board, info, local, activate: false, dropped: true).ConfigureAwait(true);
                count++;
            }
            catch (Exception ex) { Debug.WriteLine($"[Favorites] open dropped failed: {ex.Message}"); }
        }
        return count;
    }

    /// <summary>事前に取得済みの <see cref="DatFetchResult"/> からスレタブを作成 / 既存タブに反映する。
    /// HTTP は呼ばない (= 並列 fetch 済の結果を流し込むだけ)。
    /// <paramref name="activate"/>=false で呼ぶと SelectedThreadTab を切り替えない (= お気に入りチェックの一括用)。
    ///
    /// async である理由: 新規タブ作成ブランチで、ThreadTabs.Add 直後に AppendPostsWithNg を 2 回続けると、
    /// WPF が WebView2 を materialize する dispatcher cycle を回す前に LatestAppendBatch が 2 回上書きされ、
    /// 1 回目 (= cache 部分) の DP callback が発火しない race condition が起きる。
    /// <see cref="System.Threading.Tasks.Task.Yield"/> で 1 cycle 譲ると、Add → materialize → binding 確立まで
    /// 進んでから append が走るので問題なく両方届く。
    /// 既存タブブランチは tab が既に bound 済なので race にならない (= 同期処理で十分)。 </summary>
    /// <param name="dropped">dat 落ちと分かっているスレ (お気に入りチェックで一覧から消えていた)。状態を「dat 落ち」にする。
    /// <paramref name="result"/> が dat 落ち (<see cref="DatFetchResult.Dropped"/>) でも同じ。</param>
    private async Task OpenThreadFromPrefetchedAsync(Board board, ThreadInfo info, DatFetchResult result, bool activate = true, bool dropped = false)
    {
        dropped |= result.Dropped;
        // 既存タブがあれば差分を append して終わり
        var existing = FindThreadTab(board, info.Key);
        if (existing is not null)
        {
            // 差分境界は dat 連番ベースの件数 (= FetchedPostCount) を使う。existing.Posts.Count は NG 透明化分を
            // 含まないので、これを境界にすると NG で消した件数ぶん既出レスが新着扱いで再 append される (= 二重表示)。
            var prevCount = existing.FetchedPostCount > 0 ? existing.FetchedPostCount : existing.Posts.Count;
            if (result.Posts.Count > prevCount)
            {
                var added = new List<Post>(result.Posts.Count - prevCount);
                for (var n = prevCount; n < result.Posts.Count; n++) added.Add(result.Posts[n]);
                AppendPostsWithNg(existing, added, isIncremental: true);
                // 仕様: 「差分取得で来た新着」が own への返信を含む場合だけ赤化フラグを立てる。
                existing.HasReplyToOwn = DeltaHasReplyToOwn(existing, added);
            }
            else
            {
                // 新着 0 件 = 状態更新イベントなので赤化フラグはリセット (= 上書き許可)。
                existing.HasReplyToOwn = false;
            }
            existing.DatSize = result.DatSize;
            if (result.ScoreUpdates is { Count: > 0 } scores) existing.UpdateScores(scores);   // 既存レスのいいね数の更新 (reddit 等)
            SaveFetchedPostCount(board, info.Key, result.Posts.Count, listedPostCount: info.PostCount);
            existing.FetchedPostCount = result.Posts.Count;
            // 最終状態を ComputeMarkState で算定 (= dat 落ち > HasReplyToOwn が true なら RepliedToOwn > Cached)。
            var finalState = ComputeMarkState(existing, dropped ? LogMarkState.Dropped : null);
            NotifyThreadListLogMark(board, info.Key, finalState);
            existing.State = finalState;
            if (dropped) existing.StatusMessage = $"{existing.Header}{DroppedNote}";
            UpdateTruncatedNote(existing);   // 取り切れなかったら「続きあり」(reddit)
            _ = LoadAuthorProfilesAsync(existing, fetchStale: !dropped);   // 投稿者のアイコン (reddit。新着の投稿者の分)
            if (existing.IsTranslationOn) _ = TranslateMissingAsync(existing);   // スレ全体の翻訳が ON なら新着も訳す
            return;
        }

        // 新規タブ作成。idx.json から既知情報を復元する:
        //   - LastReadPostNumber → tab.ScrollTargetPostNumber (= 前回スクロール位置の復元)
        //   - LastFetchedPostCount → prevFetchedCount (= 前回取得時のレス数。delta の境界として使う)
        //   - OwnPostNumbers → tab.OwnPostNumbers (= 自分の書き込みマークの復元。HasReplyToOwn 判定に必要)
        var tab = CreateThreadTab(board, info);

        var savedIndex = _threadIndex.Load(board.Host, board.DirectoryName, info.Key);
        if (savedIndex?.LastReadPostNumber is long savedPos)
            tab.ScrollTargetPostNumber = savedPos;
        if (savedIndex?.OwnPostNumbers is { Length: > 0 } savedOwn)
        {
            foreach (var n in savedOwn) tab.OwnPostNumbers.Add(n);
        }
        if (savedIndex?.MyVotes is { Count: > 0 } savedVotes)
        {
            foreach (var (n, dir) in savedVotes) tab.MyVotes[n] = dir;
        }
        var prevFetchedCount = savedIndex?.LastFetchedPostCount ?? 0;

        tab.IsFavorited = Favorites.IsThreadFavorited(board.Host, board.DirectoryName, info.Key);
        tab.State       = LogMarkState.Cached;

        ThreadTabs.Add(tab);
        MaybeActivateThreadTab(tab, activate);

        // ★ race 回避 (1): Background 優先度で待ち、ItemsControl の DataTemplate materialize +
        //    AppendBatch DP の binding 確立 + binding の初期 push まで完了させる。
        //    Task.Yield() は WPF SyncContext で Normal priority に post されるため
        //    DataBind / Render より「先」に再開してしまい、binding 確立前に AppendPostsWithNg
        //    が走って DP callback が空打ちになる (= 取得済みレスが消える原因)。
        //    Background priority は DataBind / Render より低いので、Render 完了後に再開する。
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(
            () => { },
            System.Windows.Threading.DispatcherPriority.Background);

        // result.Posts (= 全件) を「既存分」と「delta」に分割して append。
        //   - 既存分 (= 1..prevFetchedCount): cache load 相当、isIncremental=false で投入
        //   - delta  (= prevFetchedCount+1..end): 新着扱い、isIncremental=true で投入 + mark / HasReplyToOwn 判定
        // prevFetchedCount==0 (= 初取得相当) のときは全件を 1 batch 投入し、mark / 太字対象は出さない。
        if (prevFetchedCount > 0 && result.Posts.Count > prevFetchedCount)
        {
            var preFetched = new List<Post>(prevFetchedCount);
            for (var i = 0; i < prevFetchedCount && i < result.Posts.Count; i++) preFetched.Add(result.Posts[i]);
            AppendPostsWithNg(tab, preFetched);

            // ★ race 回避 (2): 1 回目の LatestAppendBatch 更新が WPF binding 経由で DP まで届くのを
            //    確実にしてから 2 回目を書く。これがないと WPF の binding update coalescing で
            //    1 回目 (preFetched) が 2 回目 (delta) によって上書きされ、preFetched が JS に届かない。
            //    やはり Background priority で待つ。
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => { },
                System.Windows.Threading.DispatcherPriority.Background);

            var delta = new List<Post>(result.Posts.Count - prevFetchedCount);
            for (var i = prevFetchedCount; i < result.Posts.Count; i++) delta.Add(result.Posts[i]);
            // mark は AppendPostsWithNg より前に書く必要がある (= 同期 DP 発火時に JSON に乗せるため)。
            tab.MarkPostNumber = prevFetchedCount + 1;
            AppendPostsWithNg(tab, delta, isIncremental: true);
            tab.HasReplyToOwn = DeltaHasReplyToOwn(tab, delta);
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[favCycle] {info.Title}: 新規タブ作成、delta={delta.Count} (prev={prevFetchedCount} → now={result.Posts.Count}), HasReplyToOwn={tab.HasReplyToOwn}");
        }
        else
        {
            AppendPostsWithNg(tab, result.Posts);
            tab.HasReplyToOwn = false;
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[favCycle] {info.Title}: 新規タブ作成 (初取得相当、prev={prevFetchedCount}, now={result.Posts.Count})、mark / 太字無し");
        }

        tab.DatSize = result.DatSize;
        SaveFetchedPostCount(board, info.Key, result.Posts.Count, listedPostCount: info.PostCount);
        tab.FetchedPostCount = result.Posts.Count;

        var newTabState = ComputeMarkState(tab, dropped ? LogMarkState.Dropped : null);
        NotifyThreadListLogMark(board, info.Key, newTabState);
        tab.State = newTabState;
        if (dropped) tab.StatusMessage = $"{info.Title}{DroppedNote}";
        // 投稿者のアイコン (reddit)。dat 落ちのスレは通信せず保存済みのものだけ出す
        _ = LoadAuthorProfilesAsync(tab, fetchStale: !dropped);
        if (tab.IsTranslationOn) _ = TranslateMissingAsync(tab);   // スレ全体の翻訳が ON (保存済みの設定) なら未訳のレスを訳す
    }
}
