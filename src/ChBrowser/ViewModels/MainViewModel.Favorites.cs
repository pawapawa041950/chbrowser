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

        foreach (var tab in ThreadTabs)
        {
            var b = tab.Board;
            tab.IsFavorited = favKeys.Contains((b.Host, b.DirectoryName, tab.ThreadKey));
        }

        foreach (var listTab in ThreadListTabs)
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

    /// <summary>HTML 再生成。Favorites.Changed 発火のたびに呼ばれる。</summary>
    public void RefreshFavoritesHtml()
        => FavoritesHtml = ChBrowser.Services.Render.FavoritesHtmlBuilder.Build(Favorites.Items);

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

        foreach (var fb in boards)
        {
            var board = ResolveBoard(fb.Host, fb.DirectoryName, fb.BoardName);
            await LoadThreadListAsync(new BoardViewModel(board)).ConfigureAwait(true);
        }
        foreach (var ft in threads)
        {
            await OpenThreadFromListAsync(ft.Host, ft.DirectoryName, ft.ThreadKey, ft.Title).ConfigureAwait(true);
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
        var existingTab = ThreadListTabs.FirstOrDefault(t => t.FavoritesFolderId == aggregateId);
        if (existingTab is not null)
        {
            SelectedThreadListTab = existingTab;
            return Task.CompletedTask;
        }

        var tab = new ThreadListTabViewModel(aggregateId, aggregateName, t => ThreadListTabs.Remove(t));
        ThreadListTabs.Add(tab);
        SelectedThreadListTab = tab;
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
            tab.IsBusy    = true;
            StatusMessage = $"お気に入り「{aggregateName}」を取得中...";

            // VM ツリーを再帰的に走って thread エントリを集める。
            // 「板として開く」では fav board エントリは展開せず、登録スレだけを表示する。
            var boards  = new List<FavoriteBoard>();
            var threads = new List<FavoriteThread>();
            foreach (var child in children)
                CollectFavoriteEntriesFromVm(child, boards, threads);

            // 登録スレの出元板の subject.txt のみ取得 (新着 / Dropped 判定に必要)。
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
                    subjectByBoard[key] = await _subjectClient.FetchAndSaveAsync(board).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Favorites] subject {ft.Host}/{ft.DirectoryName} 失敗: {ex.Message}");
                    subjectByBoard[key] = Array.Empty<ThreadInfo>();
                }
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
                    info  = new ThreadInfo(ft.ThreadKey, ft.Title, 0, 0);
                    state = LogMarkState.Dropped;
                }
                else
                {
                    var board  = resolvedBoards[key];
                    var states = BuildLogStates(board, new[] { info });
                    state = states.TryGetValue(info.Key, out var s) ? s : LogMarkState.None;
                }
                items.Add(new ThreadListItem(info, ft.Host, ft.DirectoryName, ft.BoardName, state, IsFavorited: true));
            }

            tab.SetItems(items, DateTimeOffset.UtcNow);
            tab.Header   = $"★ {aggregateName} ({items.Count})";
            StatusMessage = $"お気に入り「{aggregateName}」: {items.Count} 件";
        }
        catch (Exception ex)
        {
            StatusMessage = $"お気に入り展開失敗: {ex.Message}";
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
    /// 新着があるスレを新規タブで開く (subject.txt から消えた = 落ちたスレは除外)。
    /// subject.txt 取得 / dat 取得の通信本数は合算で <see cref="AppConfig.BatchConcurrency"/> までに制限する。</summary>
    public async Task CheckFavoritesAsync()
    {
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
        var dispatcher  = System.Windows.Application.Current?.Dispatcher;

        // ---- subject.txt を並列取得 ----
        var subjects    = new ConcurrentDictionary<(string, string), IReadOnlyList<ThreadInfo>>();
        var totalBoards = resolved.Count;
        var subjDone    = 0;

        StatusMessage = $"お気に入りチェック: subject 取得 0/{totalBoards}";

        var subjectTasks = resolved.Select(async kv =>
        {
            await sem.WaitAsync().ConfigureAwait(false);
            try
            {
                try
                {
                    var infos = await _subjectClient.FetchAndSaveAsync(kv.Value).ConfigureAwait(false);
                    subjects[kv.Key] = infos;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Favorites] subject {kv.Key} 失敗: {ex.Message}");
                    subjects[kv.Key] = Array.Empty<ThreadInfo>();
                }
            }
            finally
            {
                sem.Release();
            }
            var d = Interlocked.Increment(ref subjDone);
            if (dispatcher is not null)
            {
                await dispatcher.InvokeAsync(
                    () => StatusMessage = $"お気に入りチェック: subject 取得 {d}/{totalBoards}");
            }
        }).ToList();
        await Task.WhenAll(subjectTasks).ConfigureAwait(true);

        // ---- 新着判定。落ちたスレはスキップ。 ----
        var toOpen = new List<(Board board, ThreadInfo info)>();
        foreach (var ft in threads)
        {
            var key = (ft.Host, ft.DirectoryName);
            if (!subjects.TryGetValue(key, out var infos)) continue;
            if (!resolved.TryGetValue(key, out var board)) continue;

            var info = infos.FirstOrDefault(t => t.Key == ft.ThreadKey);
            if (info is null) continue; // 落ちた → 除外

            var idx  = _threadIndex.Load(ft.Host, ft.DirectoryName, ft.ThreadKey);
            var prev = idx?.LastFetchedPostCount ?? 0;
            if (info.PostCount > prev) toOpen.Add((board, info));
        }

        if (toOpen.Count == 0)
        {
            StatusMessage = "お気に入りチェック完了: 新着なし";
            return;
        }

        // ---- dat を並列取得 (同じ semaphore を共有)。
        //      取得本体だけを semaphore 配下、UI 反映は完了順に逐次。 ----
        var totalUpdate = toOpen.Count;
        var datDone     = 0;
        StatusMessage = $"お気に入りチェック: 更新スレ取得 0/{totalUpdate}";

        var datTasks = toOpen.Select(async pair =>
        {
            await sem.WaitAsync().ConfigureAwait(false);
            try
            {
                var noProgress = new Progress<IReadOnlyList<Post>>(_ => { });
                var result     = await _datClient.FetchStreamingAsync(pair.board, pair.info.Key, noProgress).ConfigureAwait(false);
                return (pair.board, pair.info, Result: (DatFetchResult?)result, Error: (Exception?)null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Favorites] dat {pair.board.DirectoryName}/{pair.info.Key} 失敗: {ex.Message}");
                return (pair.board, pair.info, Result: (DatFetchResult?)null, Error: (Exception?)ex);
            }
            finally
            {
                sem.Release();
                Interlocked.Increment(ref datDone);
            }
        }).ToList();

        // 結果を完了順ではなく開始順 (toOpen 順) に処理。
        var i = 0;
        foreach (var t in datTasks)
        {
            i++;
            StatusMessage = $"お気に入りチェック: 更新スレ取得 {i}/{totalUpdate} (完了 {datDone}/{totalUpdate})";
            var (board, info, result, error) = await t.ConfigureAwait(true);
            if (result is null) continue;
            try { await OpenThreadFromPrefetchedAsync(board, info, result).ConfigureAwait(true); }
            catch (Exception ex) { Debug.WriteLine($"[Favorites] open prefetched failed: {ex.Message}"); }
        }

        StatusMessage = $"お気に入りチェック完了: 更新 {toOpen.Count} 件";
    }

    /// <summary>事前に取得済みの <see cref="DatFetchResult"/> からスレタブを作成 / 既存タブに反映する。
    /// HTTP は呼ばない (= 並列 fetch 済の結果を流し込むだけ)。
    ///
    /// async である理由: 新規タブ作成ブランチで、ThreadTabs.Add 直後に AppendPostsWithNg を 2 回続けると、
    /// WPF が WebView2 を materialize する dispatcher cycle を回す前に LatestAppendBatch が 2 回上書きされ、
    /// 1 回目 (= cache 部分) の DP callback が発火しない race condition が起きる。
    /// <see cref="System.Threading.Tasks.Task.Yield"/> で 1 cycle 譲ると、Add → materialize → binding 確立まで
    /// 進んでから append が走るので問題なく両方届く。
    /// 既存タブブランチは tab が既に bound 済なので race にならない (= 同期処理で十分)。 </summary>
    private async Task OpenThreadFromPrefetchedAsync(Board board, ThreadInfo info, DatFetchResult result)
    {
        // 既存タブがあれば差分を append して終わり
        var existing = FindThreadTab(board, info.Key);
        if (existing is not null)
        {
            var prevCount = existing.Posts.Count;
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
            SaveFetchedPostCount(board, info.Key, result.Posts.Count);
            // 最終状態を ComputeMarkState で算定 (= HasReplyToOwn が true なら RepliedToOwn、それ以外は Cached)。
            var finalState = ComputeMarkState(existing, stateHint: null);
            NotifyThreadListLogMark(board, info.Key, finalState);
            existing.State = finalState;
            return;
        }

        // 新規タブ作成。idx.json から既知情報を復元する:
        //   - LastReadPostNumber → tab.ScrollTargetPostNumber (= 前回スクロール位置の復元)
        //   - LastFetchedPostCount → prevFetchedCount (= 前回取得時のレス数。delta の境界として使う)
        //   - OwnPostNumbers → tab.OwnPostNumbers (= 自分の書き込みマークの復元。HasReplyToOwn 判定に必要)
        var tab = CreateThreadTab(board, info);

        var savedIndex = _threadIndex.Load(board.Host, board.DirectoryName, info.Key);
        if (savedIndex?.LastReadPostNumber is int savedPos)
            tab.ScrollTargetPostNumber = savedPos;
        if (savedIndex?.OwnPostNumbers is { Length: > 0 } savedOwn)
        {
            foreach (var n in savedOwn) tab.OwnPostNumbers.Add(n);
        }
        var prevFetchedCount = savedIndex?.LastFetchedPostCount ?? 0;

        tab.IsFavorited = Favorites.IsThreadFavorited(board.Host, board.DirectoryName, info.Key);
        tab.State       = LogMarkState.Cached;

        ThreadTabs.Add(tab);
        SelectedThreadTab = tab;

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
        SaveFetchedPostCount(board, info.Key, result.Posts.Count);

        var newTabState = ComputeMarkState(tab, stateHint: null);
        NotifyThreadListLogMark(board, info.Key, newTabState);
        tab.State = newTabState;
    }
}
