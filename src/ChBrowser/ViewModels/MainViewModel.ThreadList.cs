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

    /// <summary>「次スレ候補検索」タブの ID 計算で <see cref="ComputeNextThreadTabId"/> が使う prefix
    /// (= 同 (board, title) なら同タブを再利用するための deterministic hash)。</summary>
    private const string NextThreadSearchTabIdPrefix = "nextthread:";

    /// <summary>板ダブルクリック時に呼ばれる。既存タブがあればそれを使い、
    /// いずれにしても subject.txt を再取得して新着判定 (緑丸) を反映する。</summary>
    public async Task LoadThreadListAsync(BoardViewModel boardVm)
    {
        var board = boardVm.Board;

        // 既存タブを探す (host + directory_name で一致判定)。なければ作る。
        // お気に入りフォルダ展開タブ (Board=null) は対象外。
        var tab = ThreadListTabs.FirstOrDefault(t =>
            t.Board is not null &&
            t.Board.Host          == board.Host &&
            t.Board.DirectoryName == board.DirectoryName);
        if (tab is null)
        {
            tab = new ThreadListTabViewModel(board, t => ThreadListTabs.Remove(t))
            {
                // 初期化: 板自身がお気に入り登録済みか (= ツールバーの ★ ボタンの押下状態)。
                // 後続の登録/削除操作で RefreshFavoritedStateOfAllTabs が再同期する。
                IsBoardFavorited = Favorites.FindBoard(board.Host, board.DirectoryName) is not null,
            };
            ThreadListTabs.Add(tab);
        }
        SelectedThreadListTab = tab;

        if (tab.IsBusy) return; // 二重 fetch ガード

        try
        {
            tab.IsBusy    = true;
            tab.Header    = $"{board.BoardName} (取得中)";
            StatusMessage = $"{board.BoardName} のスレ一覧を取得中...";

            var subjectThreads = await _subjectClient.FetchAndSaveAsync(board).ConfigureAwait(true);

            // ローカル dat があるが subject.txt にもう無いスレ (= dat 落ち) も一覧に含める
            var subjectKeys  = new HashSet<string>(subjectThreads.Select(t => t.Key));
            var localKeys    = _datClient.EnumerateExistingThreadKeys(board);
            var droppedKeys  = localKeys.Where(k => !subjectKeys.Contains(k)).ToList();
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

            // この板に属する開きっぱなしのスレタブの状態マークも同期する
            foreach (var threadTab in ThreadTabs)
            {
                if (threadTab.Board.Host          != board.Host)          continue;
                if (threadTab.Board.DirectoryName != board.DirectoryName) continue;
                threadTab.State = states.TryGetValue(threadTab.ThreadKey, out var s) ? s : LogMarkState.None;
            }

            tab.Header    = $"{board.BoardName} ({allThreads.Count})";
            StatusMessage = droppedList.Count > 0
                ? $"{board.BoardName}: {subjectThreads.Count} スレ (+ dat 落ち {droppedList.Count})"
                : $"{board.BoardName}: {subjectThreads.Count} スレを表示";
        }
        catch (Exception ex)
        {
            tab.Header    = $"{board.BoardName} (失敗)";
            StatusMessage = $"スレ一覧の取得に失敗: {ex.Message}";
        }
        finally
        {
            tab.IsBusy = false;
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
        foreach (var t in ThreadListTabs)
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
    /// 中身の組み立ては <see cref="BuildAllLogsItems"/> を共有する (= リフレッシュ時もここを通る)。</summary>
    public Task OpenAllLogsAsync()
    {
        var existingTab = ThreadListTabs.FirstOrDefault(t => t.FavoritesFolderId == AllLogsTabId);
        if (existingTab is not null)
        {
            SelectedThreadListTab = existingTab;
            return Task.CompletedTask;
        }

        var tab = new ThreadListTabViewModel(AllLogsTabId, "全ログ", t => ThreadListTabs.Remove(t));
        ThreadListTabs.Add(tab);
        SelectedThreadListTab = tab;
        RefreshAllLogsTab(tab);
        return Task.CompletedTask;
    }

    /// <summary>「全ログ」タブの中身を再構築する (= ディスク walk + ローカル subject.txt との突合)。
    /// HTTP は呼ばない (ローカル状態のスナップショット)。</summary>
    private void RefreshAllLogsTab(ThreadListTabViewModel tab)
    {
        try
        {
            tab.IsBusy    = true;
            StatusMessage = "全ログを収集中...";
            var items     = BuildAllLogsItems();
            tab.SetItems(items, DateTimeOffset.UtcNow);
            tab.Header    = $"📁 全ログ ({items.Count})";
            StatusMessage = $"全ログ: {items.Count} 件";
        }
        catch (Exception ex)
        {
            StatusMessage = $"全ログ取得失敗: {ex.Message}";
        }
        finally
        {
            tab.IsBusy = false;
        }
    }

    /// <summary>data/&lt;rootDomain&gt;/&lt;dir&gt;/*.dat を全件走査し、各 dat を 1 行 (<see cref="ThreadListItem"/>) に変換する。
    /// subject.txt があれば突合してタイトル / postCount / 状態 (青/緑) を引き、
    /// なければ dat の 1 行目から title を取って Dropped (茶) でマーク。</summary>
    private List<ThreadListItem> BuildAllLogsItems()
    {
        var items   = new List<ThreadListItem>();
        var favSet  = Favorites.CollectFavoriteThreadKeys();

        foreach (var rootDomain in new[] { "5ch.io", "bbspink.com" })
        {
            var rootDir = System.IO.Path.Combine(_paths.Root, rootDomain);
            if (!System.IO.Directory.Exists(rootDir)) continue;

            foreach (var dirPath in System.IO.Directory.EnumerateDirectories(rootDir))
            {
                var dirName  = System.IO.Path.GetFileName(dirPath);
                var datFiles = System.IO.Directory.EnumerateFiles(dirPath, "*.dat").ToList();
                if (datFiles.Count == 0) continue;

                var board     = FindBoardByDirectory(rootDomain, dirName)
                              ?? new Board(dirName, dirName, $"https://{rootDomain}/{dirName}/", "", 0);
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
                        state = LogMarkState.Dropped;
                    }

                    var fav = favSet.Contains((board.Host, dirName, key));
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
        var tab   = ThreadListTabs.FirstOrDefault(t => t.FavoritesFolderId == tabId);
        if (tab is null)
        {
            tab = new ThreadListTabViewModel(tabId, $"🔍 候補: {Truncate(sourceTitle, 18)}", t => ThreadListTabs.Remove(t));
            ThreadListTabs.Add(tab);
        }
        SelectedThreadListTab = tab;

        if (tab.IsBusy) return;

        try
        {
            tab.IsBusy    = true;
            StatusMessage = $"次スレ候補を検索中... ({board.BoardName})";

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
            tab.Header    = $"🔍 候補: {Truncate(sourceTitle, 18)} ({items.Count})";
            StatusMessage = $"次スレ候補: {items.Count} 件 ({board.BoardName})";
        }
        catch (Exception ex)
        {
            StatusMessage = $"次スレ候補検索失敗: {ex.Message}";
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
        => s.Length <= max ? s : s[..max] + "…";

    /// <summary>dat の 1 行目 (= 1 レス目) から title フィールドを抽出する。
    /// dat 形式: <c>name&lt;&gt;mail&lt;&gt;date_id&lt;&gt;body&lt;&gt;title</c>。失敗時 null。</summary>
    private static string? ReadDatTitle(string datPath)
    {
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
