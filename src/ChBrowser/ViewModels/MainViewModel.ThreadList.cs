using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Storage;

namespace ChBrowser.ViewModels;

/// <summary>スレ一覧タブ (板タブ / 全ログ / お気に入り集約) の取得・構築・更新の dispatcher。</summary>
public sealed partial class MainViewModel
{
    /// <summary>「全ログ」タブ識別用の固定 Guid (= <see cref="Guid.Empty"/> はお気に入り仮想ルートが使うので衝突回避)。</summary>
    private static readonly Guid AllLogsTabId = new("ffffffff-ffff-ffff-ffff-fffffffffffe");

    /// <summary>「お気に入り以外の全ログ」タブ識別用の固定 Guid (= <see cref="AllLogsTabId"/> のお気に入り除外版)。</summary>
    private static readonly Guid NonFavLogsTabId = new("ffffffff-ffff-ffff-ffff-fffffffffffd");

    /// <summary>「板一覧以外の取得済み板」タブ識別用の固定 Guid (= 行がスレではなく板の集約タブ)。</summary>
    private static readonly Guid UnlistedBoardsTabId = new("ffffffff-ffff-ffff-ffff-fffffffffffc");

    /// <summary>「次スレ候補検索」タブの ID 計算で <see cref="ComputeNextThreadTabId"/> が使う prefix
    /// (= 同 (board, title) なら同タブを再利用するための deterministic hash)。</summary>
    private const string NextThreadSearchTabIdPrefix = "nextthread:";

    /// <summary>板ダブルクリック時に呼ばれる。既存タブがあればそれを使い、
    /// いずれにしても subject.txt を再取得して新着判定 (緑丸) を反映する。
    /// <paramref name="activate"/>=false で呼ぶと SelectedThreadListTab を切り替えない (= お気に入り一括オープン用)。</summary>
    public async Task LoadThreadListAsync(BoardViewModel boardVm, bool activate = true)
    {
        var board = boardVm.Board;

        // 既存タブを探す (host + directory_name で一致判定)。なければ作る。
        // お気に入りフォルダ展開タブ (Board=null) は対象外。
        var tab = AllThreadListTabs.FirstOrDefault(t =>
            t.Board is not null &&
            t.Board.Host          == board.Host &&
            t.Board.DirectoryName == board.DirectoryName);
        if (tab is null)
        {
            tab = new ThreadListTabViewModel(board, t => RemoveThreadListTab(t))
            {
                // 初期化: 板自身がお気に入り登録済みか (= ツールバーの ★ ボタンの押下状態)。
                // 後続の登録/削除操作で RefreshFavoritedStateOfAllTabs が再同期する。
                IsBoardFavorited = Favorites.FindBoard(board.Host, board.DirectoryName) is not null,
            };
            ThreadListTabs.Add(tab);
        }
        MaybeActivateThreadListTab(tab, activate);

        if (tab.IsBusy) return; // 二重 fetch ガード

        try
        {
            tab.IsBusy        = true;
            tab.Header        = $"{board.BoardName} (取得中)";
            tab.StatusMessage = $"{board.BoardName} のスレ一覧を取得中...";

            var provider       = ChBrowser.Services.Bbs.BbsRegistry.ResolveOrDefault(board.Host);
            var page           = await _subjectClient.FetchPageAsync(
                                     board, new ChBrowser.Services.Bbs.ThreadListQuery(tab.Sort), default).ConfigureAwait(true);
            var subjectThreads = page.Items;
            tab.NextCursor     = page.NextCursor;

            // ローカル dat があるが subject.txt にもう無いスレ (= dat 落ち) も一覧に含める。
            // 一覧が常に部分集合の掲示板 (reddit) では「一覧に無い = 落ちた」ではないので行わない。
            var subjectKeys  = new HashSet<string>(subjectThreads.Select(t => t.Key));
            var localKeys    = _datClient.EnumerateExistingThreadKeys(board);
            var droppedKeys  = provider.ThreadListIsComplete
                ? localKeys.Where(k => !subjectKeys.Contains(k)).ToList()
                : new List<string>();
            var droppedList  = new List<ThreadInfo>(droppedKeys.Count);
            foreach (var key in droppedKeys)
            {
                var title     = await _datClient.ReadThreadTitleFromDiskAsync(board, key).ConfigureAwait(true)
                                ?? "(タイトル不明)";
                var idx       = _threadIndex.Load(board.Host, board.DirectoryName, key);
                var postCount = idx?.LastFetchedPostCount ?? 0;
                var order     = subjectThreads.Count + droppedList.Count + 1;
                droppedList.Add(new ThreadInfo(key, title, postCount, order));
            }
            var allThreads = subjectThreads.Concat(droppedList).ToList();

            var states = new Dictionary<string, LogMarkState>(BuildLogStates(board, allThreads));
            foreach (var key in droppedKeys) states[key] = LogMarkState.Dropped;

            // この板に登録済のお気に入りスレキーを抽出
            var favKeys = new HashSet<string>(
                Favorites.CollectFavoriteThreadKeys()
                         .Where(k => k.Host == board.Host && k.Dir == board.DirectoryName)
                         .Select(k => k.Key));
            tab.SetThreads(allThreads, DateTimeOffset.UtcNow, states, favKeys);

            // この板に属する開きっぱなしのスレタブの状態マークも同期する (全ペイン横断)
            foreach (var threadTab in AllThreadTabs)
            {
                if (threadTab.Board.Host          != board.Host)          continue;
                if (threadTab.Board.DirectoryName != board.DirectoryName) continue;
                threadTab.State = states.TryGetValue(threadTab.ThreadKey, out var s) ? s : LogMarkState.None;
            }

            tab.Header        = $"{board.BoardName}{SortSuffix(tab)} ({allThreads.Count})";
            tab.StatusMessage = droppedList.Count > 0
                ? $"{board.BoardName}: {subjectThreads.Count} スレ (+ dat 落ち {droppedList.Count})"
                : $"{board.BoardName}: {subjectThreads.Count} スレを表示"
                  + (tab.NextCursor is not null ? " (右クリック → 続きを読み込む で次の 100 件)" : "");

            // 板一覧を持たない掲示板 (したらば / reddit) の板名は、ローカルの板情報 (_SETTING.TXT の BBS_TITLE) が唯一の出どころ
            // (「表示済み板」はそこから名前を読む)。アドレスバー以外 (検索結果など) から開いた板は未取得なので、ここで 1 回だけ取っておく。
            _ = EnsureBoardInfoCachedAsync(board);
        }
        catch (Exception ex)
        {
            tab.Header        = $"{board.BoardName} (失敗)";
            tab.StatusMessage = $"スレ一覧の取得に失敗: {ex.Message}";
        }
        finally
        {
            tab.IsBusy = false;
        }
    }

    // -----------------------------------------------------------------
    // 並び順とページング (reddit。doc/reddit-design.md §3 B5 / A4)
    // -----------------------------------------------------------------

    /// <summary>タブの実効の並び順 (未指定なら提供者の既定)。並び順を持たない掲示板では null。</summary>
    public string? EffectiveListingSort(ThreadListTabViewModel tab)
    {
        if (tab.Board is not { } board) return null;
        var provider = ChBrowser.Services.Bbs.BbsRegistry.ResolveOrDefault(board.Host);
        if (provider.ListingSorts.Count == 0) return null;
        if (!string.IsNullOrEmpty(tab.Sort)) return tab.Sort;
        return ChBrowser.Services.Bbs.ListingSortDefaults.For(provider);
    }

    /// <summary>設定の「掲示板ごとの既定の並び順」。旧設定 (<see cref="AppConfig.RedditDefaultSort"/>) があれば reddit 分として引き継ぐ。</summary>
    internal static Dictionary<string, string> EffectiveListingSortDefaults(AppConfig config)
    {
        var map = new Dictionary<string, string>(config.ListingSortDefaults ?? new(), StringComparer.Ordinal);
        if (!map.ContainsKey("reddit") && !string.IsNullOrWhiteSpace(config.RedditDefaultSort)) map["reddit"] = config.RedditDefaultSort;
        return map;
    }

    /// <summary>タブ見出しに付ける並び順 (例: " [hot]")。並び順を持たない掲示板では空。</summary>
    private string SortSuffix(ThreadListTabViewModel tab)
        => EffectiveListingSort(tab) is { } s ? $" [{s.Replace(':', ' ')}]" : "";

    /// <summary>並び順を変えて 1 ページ目から取り直す。</summary>
    public Task ChangeThreadListSortAsync(ThreadListTabViewModel tab, string sort)
    {
        if (tab.Board is not { } board) return Task.CompletedTask;
        tab.Sort       = sort;
        tab.NextCursor = null;
        return LoadThreadListAsync(new BoardViewModel(board));
    }

    /// <summary>「続きを読み込む」: 次のページを取得して一覧の末尾に足す (取得分はセッション限り。決定 D29)。</summary>
    public async Task LoadMoreThreadsAsync(ThreadListTabViewModel tab)
    {
        if (tab.Board is not { } board || string.IsNullOrEmpty(tab.NextCursor) || tab.IsBusy) return;
        var header = tab.Header;
        try
        {
            tab.IsBusy        = true;
            tab.StatusMessage = $"{board.BoardName}: 続きを取得中...";
            var page = await _subjectClient.FetchPageAsync(
                board, new ChBrowser.Services.Bbs.ThreadListQuery(tab.Sort, tab.NextCursor), default).ConfigureAwait(true);

            // 既に並んでいるスレは足さない (reddit はページ間で順位が動くので重複しうる)。No は通し番号
            var existing = tab.Items.Where(i => i.Kind == ThreadListItemKind.Thread).Select(i => i.Info).ToList();
            var known    = new HashSet<string>(existing.Select(i => i.Key), StringComparer.Ordinal);
            var added    = page.Items.Where(t => known.Add(t.Key)).ToList();
            var all      = existing.Concat(added).Select((t, i) => t with { Order = i + 1 }).ToList();

            var states  = BuildLogStates(board, all);
            var favKeys = new HashSet<string>(
                Favorites.CollectFavoriteThreadKeys()
                         .Where(k => k.Host == board.Host && k.Dir == board.DirectoryName)
                         .Select(k => k.Key));
            tab.SetThreads(all, DateTimeOffset.UtcNow, states, favKeys);
            tab.NextCursor    = page.NextCursor;
            tab.Header        = $"{board.BoardName}{SortSuffix(tab)} ({all.Count})";
            tab.StatusMessage = $"{board.BoardName}: {added.Count} スレを追加 (合計 {all.Count})"
                                + (page.NextCursor is null ? " — これ以上はありません" : "");
        }
        catch (Exception ex)
        {
            tab.Header        = header;
            tab.StatusMessage = $"続きの取得に失敗: {ex.Message}";
        }
        finally
        {
            tab.IsBusy = false;
        }
    }

    /// <summary>板情報 (SETTING.TXT 相当) をまだローカルに持っていなければ取得して保存する。板一覧を持たない掲示板だけが対象
    /// (板一覧のある掲示板は板名を板一覧から得るので不要)。失敗しても何もしない (板名は dir 名のまま)。</summary>
    private async Task EnsureBoardInfoCachedAsync(Board board)
    {
        var provider = ChBrowser.Services.Bbs.BbsRegistry.ResolveOrDefault(board.Host);
        if ((provider.Capabilities & ChBrowser.Services.Bbs.BbsCapabilities.BoardList) != 0) return;
        if ((provider.Capabilities & ChBrowser.Services.Bbs.BbsCapabilities.BoardInfo) == 0) return;
        if (System.IO.File.Exists(_paths.SettingTxtPath(board.Host, board.DirectoryName))) return;
        try
        {
            await _settingClient.GetOrFetchAsync(board).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[boardInfo] {board.Host}/{board.DirectoryName} の板情報を取得できませんでした: {ex.Message}");
        }
    }

    /// <summary>各スレッドのログ状態を判定する。
    /// ログ無し → None / ログあり &amp; 件数一致 → Cached / ログあり &amp; subject の方が多い → Updated。
    /// <see cref="LogMarkState.RepliedToOwn"/> (= 赤) はここでは付かない (= 永続化されないため、
    /// 直前の差分取得イベントから tab に保持されている <c>tab.HasReplyToOwn</c> 経由でだけ表示される)。 </summary>
    private IReadOnlyDictionary<string, LogMarkState> BuildLogStates(Board board, IReadOnlyList<ThreadInfo> threads)
    {
        var keysWithLog = _datClient.EnumerateExistingThreadKeys(board);
        var dict        = new Dictionary<string, LogMarkState>(keysWithLog.Count);
        if (keysWithLog.Count == 0) return dict;

        foreach (var t in threads)
        {
            if (!keysWithLog.Contains(t.Key)) continue;
            var idx     = _threadIndex.Load(board.Host, board.DirectoryName, t.Key);
            var fetched = idx?.LastFetchedPostCount;
            var hasNew  = fetched is int f && t.PostCount > f;
            dict[t.Key] = hasNew ? LogMarkState.Updated : LogMarkState.Cached;
        }
        return dict;
    }

    /// <summary>指定スレッドのマーク状態変化を、開いている全スレ一覧タブにブロードキャストする。
    /// 板タブ (Board != null) — Board.Host/DirectoryName 一致時に更新。
    /// 集約タブ (Board == null) — Items に該当 (host, dir, key) があるときだけ更新。</summary>
    private void NotifyThreadListLogMark(Board board, string threadKey, LogMarkState state)
    {
        foreach (var t in AllThreadListTabs)
        {
            if (t.Board is not null)
            {
                if (t.Board.Host == board.Host && t.Board.DirectoryName == board.DirectoryName)
                    t.SetLogMark(board.Host, board.DirectoryName, threadKey, state);
            }
            else if (t.ContainsThread(board.Host, board.DirectoryName, threadKey))
            {
                t.SetLogMark(board.Host, board.DirectoryName, threadKey, state);
            }
        }
    }

    /// <summary>スレ一覧タブの「更新」を共通エントリで dispatch する。
    /// 板タブ → <see cref="LoadThreadListAsync"/> / 全ログ → <see cref="RefreshAllLogsTab"/> /
    /// お気に入り集約 → 元のフォルダ参照を引き戻して再構築。</summary>
    public Task RefreshThreadListTabAsync(ThreadListTabViewModel tab)
    {
        if (tab.Board is { } board)
            return LoadThreadListAsync(new BoardViewModel(board));

        if (tab.FavoritesFolderId == AllLogsTabId)
        {
            RefreshAllLogsTab(tab);
            return Task.CompletedTask;
        }

        if (tab.FavoritesFolderId == NonFavLogsTabId)
        {
            RefreshNonFavLogsTab(tab);
            return Task.CompletedTask;
        }

        if (tab.FavoritesFolderId == UnlistedBoardsTabId)
        {
            RefreshUnlistedBoardsTab(tab);
            return Task.CompletedTask;
        }

        // 板一覧の無い掲示板の「検索」結果 / 「表示済み板」タブ
        if (TryRefreshBoardRowsTab(tab, out var boardRowsTask)) return boardRowsTask;

        if (tab.FavoritesFolderId is Guid id)
        {
            // 仮想ルート (= Guid.Empty) ならお気に入り全体、それ以外なら ID で folder 参照を引き戻す
            if (id == Guid.Empty)
                return BuildFavoritesAggregateItemsAsync(tab, "お気に入り", Favorites.Items);

            if (Favorites.FindById(id) is FavoriteFolderViewModel folder)
                return BuildFavoritesAggregateItemsAsync(tab, folder.DisplayName, folder.Children);
        }
        return Task.CompletedTask;
    }

    // -----------------------------------------------------------------
    // 全ログ (Phase 18): ローカルに dat を持っている全スレを 1 枚のスレ一覧で表示する
    // -----------------------------------------------------------------

    /// <summary>「全ログ」タブを開く (既存タブがあればアクティブ化、無ければ生成して中身を構築)。
    /// 中身の組み立ては <see cref="BuildAllLogsItems"/> を共有する (= リフレッシュ時もここを通る)。
    /// <paramref name="activate"/>=false は起動時のタブ復元用 (= 選択タブを切り替えない)。</summary>
    public Task OpenAllLogsAsync(bool activate = true)
    {
        var existingTab = AllThreadListTabs.FirstOrDefault(t => t.FavoritesFolderId == AllLogsTabId);
        if (existingTab is not null)
        {
            MaybeActivateThreadListTab(existingTab, activate);
            return Task.CompletedTask;
        }

        var tab = new ThreadListTabViewModel(AllLogsTabId, "全ログ", t => RemoveThreadListTab(t));
        ThreadListTabs.Add(tab);
        MaybeActivateThreadListTab(tab, activate);
        RefreshAllLogsTab(tab);
        return Task.CompletedTask;
    }

    /// <summary>「お気に入り以外の全ログ」タブを開く (= 全ログから ★ 付きスレを除外したもの)。
    /// 既存タブがあればアクティブ化、無ければ生成して中身を構築する。</summary>
    public Task OpenNonFavLogsAsync(bool activate = true)
    {
        var existingTab = AllThreadListTabs.FirstOrDefault(t => t.FavoritesFolderId == NonFavLogsTabId);
        if (existingTab is not null)
        {
            MaybeActivateThreadListTab(existingTab, activate);
            return Task.CompletedTask;
        }

        var tab = new ThreadListTabViewModel(NonFavLogsTabId, "お気に入り以外の全ログ", t => RemoveThreadListTab(t));
        ThreadListTabs.Add(tab);
        MaybeActivateThreadListTab(tab, activate);
        RefreshNonFavLogsTab(tab);
        return Task.CompletedTask;
    }

    /// <summary>「板一覧以外の取得済み板」タブを開く (= ローカルに取得済みデータがあるのに板一覧 (bbsmenu) に無い板を、
    /// 板 1 件 = 1 行で並べる集約タブ。行クリックで板を開く)。既存タブがあればアクティブ化、無ければ生成して中身を構築する。</summary>
    public Task OpenUnlistedBoardsAsync(bool activate = true)
    {
        var existingTab = AllThreadListTabs.FirstOrDefault(t => t.FavoritesFolderId == UnlistedBoardsTabId);
        if (existingTab is not null)
        {
            MaybeActivateThreadListTab(existingTab, activate);
            return Task.CompletedTask;
        }

        var tab = new ThreadListTabViewModel(UnlistedBoardsTabId, "板一覧以外の取得済み板", t => RemoveThreadListTab(t));
        ThreadListTabs.Add(tab);
        MaybeActivateThreadListTab(tab, activate);
        RefreshUnlistedBoardsTab(tab);
        return Task.CompletedTask;
    }

    /// <summary>「全ログ」タブの中身を再構築する (= ディスク walk + ローカル subject.txt との突合)。
    /// HTTP は呼ばない (ローカル状態のスナップショット)。</summary>
    private void RefreshAllLogsTab(ThreadListTabViewModel tab)
    {
        try
        {
            tab.IsBusy        = true;
            tab.StatusMessage = "全ログを収集中...";
            var items         = BuildAllLogsItems(excludeFavorites: false);
            tab.SetItems(items, DateTimeOffset.UtcNow);
            tab.Header        = $"📁 全ログ ({items.Count})";
            tab.StatusMessage = $"全ログ: {items.Count} 件";
        }
        catch (Exception ex)
        {
            tab.StatusMessage = $"全ログ取得失敗: {ex.Message}";
        }
        finally
        {
            tab.IsBusy = false;
        }
    }

    /// <summary>「お気に入り以外の全ログ」タブの中身を再構築する (= 全ログから ★ 付きスレを除外)。</summary>
    private void RefreshNonFavLogsTab(ThreadListTabViewModel tab)
    {
        try
        {
            tab.IsBusy        = true;
            tab.StatusMessage = "お気に入り以外の全ログを収集中...";
            var items         = BuildAllLogsItems(excludeFavorites: true);
            tab.SetItems(items, DateTimeOffset.UtcNow);
            tab.Header        = $"📁 お気に入り以外の全ログ ({items.Count})";
            tab.StatusMessage = $"お気に入り以外の全ログ: {items.Count} 件";
        }
        catch (Exception ex)
        {
            tab.StatusMessage = $"お気に入り以外の全ログ取得失敗: {ex.Message}";
        }
        finally
        {
            tab.IsBusy = false;
        }
    }

    /// <summary>「板一覧以外の取得済み板」タブの中身を再構築する (= ディスク walk のみ、HTTP は呼ばない)。</summary>
    private void RefreshUnlistedBoardsTab(ThreadListTabViewModel tab)
    {
        try
        {
            tab.IsBusy        = true;
            tab.StatusMessage = "板一覧以外の取得済み板を収集中...";
            var items         = BuildUnlistedBoardItems();
            tab.SetItems(items, DateTimeOffset.UtcNow);
            tab.Header        = $"📁 板一覧以外の取得済み板 ({items.Count})";
            tab.StatusMessage = $"板一覧以外の取得済み板: {items.Count} 件";
        }
        catch (Exception ex)
        {
            tab.StatusMessage = $"板一覧以外の取得済み板の取得失敗: {ex.Message}";
        }
        finally
        {
            tab.IsBusy = false;
        }
    }

    /// <summary><see cref="UnlistedBoardScanner"/> の結果を板行 (<see cref="ThreadListItemKind.Board"/>) に変換する。
    /// タイトル = 板名、板列 = 「提供者名 host」、数 = ローカル dat 件数。Key は空 (= スレではない)。</summary>
    private List<ThreadListItem> BuildUnlistedBoardItems()
    {
        var scanner = new UnlistedBoardScanner(_paths, _settingClient);
        var found   = scanner.Scan(FindBoardByDirectory);
        var items   = new List<ThreadListItem>(found.Count);
        foreach (var ub in found)
        {
            var b    = ub.Board;
            var info = new ThreadInfo("", b.BoardName, ub.DatCount, 0);
            items.Add(new ThreadListItem(info, b.Host, b.DirectoryName,
                                         $"{ub.Provider.DisplayName} {b.Host}",
                                         LogMarkState.None, false, ThreadListItemKind.Board));
        }
        return items;
    }

    /// <summary>開いている集約ログタブ (全ログ / お気に入り以外の全ログ / 板一覧以外の取得済み板) を再走査する
    /// (= 板ログ削除のようにディスク上の板構成が変わった直後用)。</summary>
    private void RefreshAggregateLogTabs()
    {
        foreach (var t in AllThreadListTabs.ToList())
        {
            if      (t.FavoritesFolderId == AllLogsTabId)        RefreshAllLogsTab(t);
            else if (t.FavoritesFolderId == NonFavLogsTabId)     RefreshNonFavLogsTab(t);
            else if (t.FavoritesFolderId == UnlistedBoardsTabId) RefreshUnlistedBoardsTab(t);
        }
    }

    /// <summary>板の取得済みログ (= <c>data/&lt;root&gt;/&lt;dir&gt;/</c> 丸ごと) を確認ダイアログの上で削除する
    /// (「板一覧以外の取得済み板」の行右クリック「板を削除」)。お気に入り登録は触らない。
    /// 削除前にその板のスレタブ / スレ一覧タブを閉じる (タブ close 時の idx.json flush がディレクトリを作り直さないよう、
    /// close → 削除の順)。パスは <see cref="DataPaths.BoardDir"/> と同じ組み立てだが EnsureDir を伴わないよう直接組む
    /// (= 存在しない板を選んでも空フォルダを作らない)。</summary>
    public void DeleteBoardLogs(string host, string directoryName, string boardName)
    {
        var rootIn   = DataPaths.ExtractRootDomain(host);
        var boardDir = System.IO.Path.Combine(_paths.Root, rootIn, directoryName);
        var datCount = System.IO.Directory.Exists(boardDir)
            ? System.IO.Directory.EnumerateFiles(boardDir, "*.dat").Count()
            : 0;

        var message = $"{boardName} ({host}/{directoryName}) の取得済みログ (スレ {datCount} 件) をすべて削除します。\n"
                    + "お気に入り登録は残ります。よろしいですか?";
        var answer = System.Windows.MessageBox.Show(
            message, "板を削除", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.Yes) return;

        static bool SameBoard(Board b, string rootIn, string dir)
            => string.Equals(DataPaths.ExtractRootDomain(b.Host), rootIn, StringComparison.OrdinalIgnoreCase)
            && string.Equals(b.DirectoryName, dir, StringComparison.Ordinal);

        foreach (var threadTab in AllThreadTabs.Where(t => SameBoard(t.Board, rootIn, directoryName)).ToList())
            RemoveThreadTab(threadTab);
        foreach (var listTab in AllThreadListTabs.Where(t => t.Board is { } b && SameBoard(b, rootIn, directoryName)).ToList())
            RemoveThreadListTab(listTab);

        try
        {
            if (System.IO.Directory.Exists(boardDir))
                System.IO.Directory.Delete(boardDir, recursive: true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"板の削除に失敗: {ex.Message}";
            RefreshAggregateLogTabs();
            return;
        }

        RefreshAggregateLogTabs();
        StatusMessage = $"{boardName} の取得済みログを削除しました (スレ {datCount} 件)";
    }

    /// <summary>data/&lt;rootDomain&gt;/&lt;dir&gt;/*.dat を全件走査し、各 dat を 1 行 (<see cref="ThreadListItem"/>) に変換する。
    /// subject.txt があれば突合してタイトル / postCount / 状態 (青/緑) を引き、
    /// なければ dat の 1 行目から title を取って Dropped (茶) でマーク。
    /// <paramref name="excludeFavorites"/>=true ならお気に入り登録済みのスレを一覧から除外する。
    /// 板 dir が 2 階層の掲示板 (したらば <c>internet/12249</c>) は <c>data/&lt;root&gt;/internet/12249/*.dat</c> と入れ子になるので、
    /// 1 階層目に *.dat が無ければ 1 段だけ降りて <c>"&lt;parent&gt;/&lt;child&gt;"</c> を dir 名として扱う。</summary>
    private List<ThreadListItem> BuildAllLogsItems(bool excludeFavorites)
    {
        var items   = new List<ThreadListItem>();
        var favSet  = Favorites.CollectFavoriteThreadKeys();

        foreach (var rootDomain in ChBrowser.Services.Bbs.BbsRegistry.StorageRoots)
        {
            var rootDir = System.IO.Path.Combine(_paths.Root, rootDomain);
            if (!System.IO.Directory.Exists(rootDir)) continue;

            var boardDirs = new List<(string DirName, List<string> DatFiles)>();
            foreach (var dirPath in System.IO.Directory.EnumerateDirectories(rootDir))
            {
                var dirName  = System.IO.Path.GetFileName(dirPath);
                var datFiles = System.IO.Directory.EnumerateFiles(dirPath, "*.dat").ToList();
                if (datFiles.Count > 0)
                {
                    boardDirs.Add((dirName, datFiles));
                    continue;
                }
                // 2 階層 dir: 直下に dat が無ければ子ディレクトリを板として見る
                foreach (var subPath in System.IO.Directory.EnumerateDirectories(dirPath))
                {
                    var subDats = System.IO.Directory.EnumerateFiles(subPath, "*.dat").ToList();
                    if (subDats.Count == 0) continue;
                    boardDirs.Add((dirName + "/" + System.IO.Path.GetFileName(subPath), subDats));
                }
            }

            foreach (var (dirName, datFiles) in boardDirs)
            {

                var board     = FindBoardByDirectory(rootDomain, dirName)
                              ?? UnlistedBoardScanner.BuildFallbackBoard(rootDomain, dirName, dirName);
                var subjList  = LoadSubjectFromDiskSync(board);
                var subjByKey = subjList.ToDictionary(t => t.Key);
                var states    = BuildLogStates(board, subjList);

                foreach (var datFile in datFiles)
                {
                    var key = System.IO.Path.GetFileNameWithoutExtension(datFile);
                    if (string.IsNullOrEmpty(key)) continue;

                    ThreadInfo info;
                    LogMarkState state;
                    if (subjByKey.TryGetValue(key, out var subj))
                    {
                        info  = subj;
                        state = states.TryGetValue(key, out var s) ? s : LogMarkState.Cached;
                    }
                    else
                    {
                        var title        = ReadDatTitle(datFile) ?? "(タイトル不明)";
                        var idx          = _threadIndex.Load(board.Host, dirName, key);
                        var fetchedCount = idx?.LastFetchedPostCount ?? 0;
                        info  = new ThreadInfo(key, title, fetchedCount, 0);
                        // 一覧が常に部分集合の掲示板 (reddit) では「一覧に無い = 落ちた」ではない
                        state = ChBrowser.Services.Bbs.BbsRegistry.ResolveOrDefault(board.Host).ThreadListIsComplete
                            ? LogMarkState.Dropped : LogMarkState.Cached;
                    }

                    var fav = favSet.Contains((board.Host, dirName, key));
                    if (excludeFavorites && fav) continue;
                    items.Add(new ThreadListItem(info, board.Host, dirName, board.BoardName, state, fav));
                }
            }
        }
        return items;
    }

    /// <summary>BoardCategories から (rootDomain, directoryName) で板を検索する (host のサブドメイン違いを吸収)。</summary>
    private Board? FindBoardByDirectory(string rootDomain, string directoryName)
    {
        foreach (var cat in BoardCategories)
            foreach (var bvm in cat.Boards)
                if (bvm.Board.DirectoryName == directoryName
                    && DataPaths.ExtractRootDomain(bvm.Board.Host) == rootDomain)
                    return bvm.Board;
        return null;
    }

    /// <summary>ローカル <c>_subject.txt</c> を同期読みする (Async API しかないので blocking で読む)。
    /// HTTP は呼ばない。ファイル無しなら空配列。</summary>
    private IReadOnlyList<ThreadInfo> LoadSubjectFromDiskSync(Board board)
    {
        try
        {
            return _subjectClient.LoadFromDiskAsync(board).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AllLogs] subject load failed: {ex.Message}");
            return Array.Empty<ThreadInfo>();
        }
    }

    // -----------------------------------------------------------------
    // 次スレ候補検索 (スレ表示タブ右クリックメニューから)
    // -----------------------------------------------------------------

    /// <summary>あるスレ表示タブから「同じ板にある似たタイトルのスレ」を一覧として表示する。
    /// 元スレタイトルとの最長共通部分文字列で類似度を採点し、上位を専用の集約タブで表示する。
    /// 同じ (板, タイトル) で再度呼ばれたら既存タブを再利用する。</summary>
    public Task OpenNextThreadSearchAsync(ThreadTabViewModel sourceTab)
    {
        if (sourceTab is null) return Task.CompletedTask;
        return OpenNextThreadSearchAsync(sourceTab.Board, sourceTab.Title ?? "", sourceTab.ThreadKey);
    }

    /// <summary>(板, スレタイトル, 自身を除外する key) を直接渡すプリミティブ版。
    /// タブを開いていない経路 (= スレ一覧行の右クリック) から呼べる。</summary>
    public async Task OpenNextThreadSearchAsync(Board board, string sourceTitle, string excludeKey)
    {
        if (string.IsNullOrWhiteSpace(sourceTitle))
        {
            StatusMessage = "次スレ候補: 元スレのタイトルが空です";
            return;
        }

        // 同じ検索の繰り返しは同じタブを再利用 (deterministic Guid)。
        var tabId = ComputeNextThreadTabId(board.Host, board.DirectoryName, sourceTitle);
        var tab   = AllThreadListTabs.FirstOrDefault(t => t.FavoritesFolderId == tabId);
        if (tab is null)
        {
            tab = new ThreadListTabViewModel(tabId, $"🔍 候補: {Truncate(sourceTitle, 18)}", t => RemoveThreadListTab(t));
            ThreadListTabs.Add(tab);
        }
        ActivateThreadListTab(tab);

        if (tab.IsBusy) return;

        try
        {
            tab.IsBusy        = true;
            tab.StatusMessage = $"次スレ候補を検索中... ({board.BoardName})";

            var subjects = await _subjectClient.FetchAndSaveAsync(board).ConfigureAwait(true);
            var matches  = FuzzyMatchByTitle(sourceTitle, subjects, excludeKey);

            // ローカルログの状態 (青/緑) と お気に入り ★ を行に乗せる。
            var states  = BuildLogStates(board, matches);
            var favKeys = Favorites.CollectFavoriteThreadKeys();
            var items   = new List<ThreadListItem>(matches.Count);
            foreach (var info in matches)
            {
                var st  = states.TryGetValue(info.Key, out var s) ? s : LogMarkState.None;
                var fav = favKeys.Contains((board.Host, board.DirectoryName, info.Key));
                items.Add(new ThreadListItem(info, board.Host, board.DirectoryName, board.BoardName, st, fav));
            }

            tab.SetItems(items, DateTimeOffset.UtcNow);
            tab.Header        = $"🔍 候補: {Truncate(sourceTitle, 18)} ({items.Count})";
            tab.StatusMessage = $"次スレ候補: {items.Count} 件 ({board.BoardName})";
        }
        catch (Exception ex)
        {
            tab.StatusMessage = $"次スレ候補検索失敗: {ex.Message}";
        }
        finally
        {
            tab.IsBusy = false;
        }
    }

    /// <summary>タイトルの最長共通部分文字列で類似度を採点し、上位 50 件を返す
    /// (元スレ自身は <paramref name="excludeKey"/> で除外)。
    /// しきい値を 6 文字に設定: 「Part」「【】」のような短いプレフィックスだけでヒットしないようにする。</summary>
    private static List<ThreadInfo> FuzzyMatchByTitle(
        string sourceTitle,
        IReadOnlyList<ThreadInfo> all,
        string excludeKey)
    {
        const int MinScore   = 6;
        const int MaxResults = 50;

        var srcLower = sourceTitle.ToLowerInvariant();
        var scored   = new List<(int score, ThreadInfo info)>();

        foreach (var t in all)
        {
            if (t.Key == excludeKey) continue;
            var score = LongestCommonSubstringLength(srcLower, t.Title.ToLowerInvariant());
            if (score < MinScore) continue;
            scored.Add((score, t));
        }

        return scored
            .OrderByDescending(x => x.score)
            .ThenByDescending(x => x.info.PostCount) // 同スコアならレス数が多い方を上に
            .Take(MaxResults)
            .Select(x => x.info)
            .ToList();
    }

    /// <summary>2 文字列の最長共通部分文字列の長さを DP で計算する (空間 O(min(a,b)))。</summary>
    private static int LongestCommonSubstringLength(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        // 短い方を内側に置いて DP テーブルを 1 次元化
        if (a.Length > b.Length) (a, b) = (b, a);
        var prev = new int[a.Length + 1];
        var curr = new int[a.Length + 1];
        var max  = 0;
        for (var i = 1; i <= b.Length; i++)
        {
            for (var j = 1; j <= a.Length; j++)
            {
                curr[j] = b[i - 1] == a[j - 1] ? prev[j - 1] + 1 : 0;
                if (curr[j] > max) max = curr[j];
            }
            (prev, curr) = (curr, prev);
            Array.Clear(curr, 0, curr.Length);
        }
        return max;
    }

    /// <summary>(boardHost, boardDir, sourceTitle) から deterministic な Guid を作る
    /// (= 同じ検索を 2 回呼んだら同じタブを再利用するため)。</summary>
    private static Guid ComputeNextThreadTabId(string boardHost, string boardDir, string sourceTitle)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            $"{NextThreadSearchTabIdPrefix}{boardHost}:{boardDir}:{sourceTitle}");
        using var sha = System.Security.Cryptography.SHA1.Create();
        var hash = sha.ComputeHash(bytes);
        var guid = new byte[16];
        Array.Copy(hash, guid, 16);
        return new Guid(guid);
    }

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        // サロゲートペア (絵文字等) の途中で切らない (ThreadTabViewModel.TruncateForTab と同じ理由)。
        var cut = max;
        if (char.IsHighSurrogate(s[cut - 1])) cut--;
        return s[..cut] + "…";
    }

    /// <summary>dat の 1 行目 (= 1 レス目) から title フィールドを抽出する。
    /// dat 形式: <c>name&lt;&gt;mail&lt;&gt;date_id&lt;&gt;body&lt;&gt;title</c>。失敗時 null。</summary>
    private static string? ReadDatTitle(string datPath)
    {
        if (ChBrowser.Services.Api.NumberedLogFormat.TryReadTitle(datPath, out var numberedTitle)) return numberedTitle;
        try
        {
            var sjis  = System.Text.Encoding.GetEncoding(932);
            using var sr = new System.IO.StreamReader(datPath, sjis);
            var first = sr.ReadLine();
            if (string.IsNullOrEmpty(first)) return null;
            var fields = first.Split("<>", StringSplitOptions.None);
            if (fields.Length < 5) return null;
            return System.Net.WebUtility.HtmlDecode(fields[4]);
        }
        catch
        {
            return null;
        }
    }
}
