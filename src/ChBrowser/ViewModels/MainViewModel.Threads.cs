using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Api;

namespace ChBrowser.ViewModels;

/// <summary>スレ表示タブ (ThreadTab) の生成・取得・更新・削除・スクロール位置/既読位置の永続化。</summary>
public sealed partial class MainViewModel
{
    /// <summary>レスのバッチに NG 判定を適用し、可視分だけ tab.AppendPosts する共通ヘルパ。
    /// バッチ内の連鎖は計算するが、過去バッチに跨る連鎖は対象外 (= タブ再オープン時に正しい連鎖が効く)。
    /// <paramref name="isIncremental"/> = true は「初期表示後の差分追加」を JS に伝える (Phase 20)。</summary>
    private void AppendPostsWithNg(ThreadTabViewModel tab, IReadOnlyList<Post> batch, bool isIncremental = false)
    {
        if (batch.Count == 0) return;
        var hidden = _ng.ComputeHidden(batch.ToList(), tab.Board.Host, tab.Board.DirectoryName);
        if (hidden.Count == 0)
        {
            tab.AppendPosts(batch, isIncremental);
        }
        else
        {
            var visible = new List<Post>(batch.Count - hidden.Count);
            foreach (var p in batch)
                if (!hidden.Contains(p.Number)) visible.Add(p);
            if (visible.Count > 0) tab.AppendPosts(visible, isIncremental);
            tab.HiddenCount += hidden.Count;
        }
        if (ReferenceEquals(tab, SelectedThreadTab))
            AboneStatus = $"あぼーん {tab.HiddenCount}";
    }

    /// <summary>スレ一覧でスレをダブルクリックしたとき呼ばれる。
    /// 既存タブがあればアクティブ化、無ければ新タブを作って dat を取得する。
    /// <paramref name="stateHint"/> はスレ一覧側で表示していたマーク状態 (Dropped 等) を引き継ぐためのヒント。</summary>
    public async Task OpenThreadAsync(Board board, ThreadInfo info, LogMarkState? stateHint = null)
    {
        // 既存タブがあれば、アクティブにした上で差分取得を走らせる
        foreach (var existing in ThreadTabs)
        {
            if (existing.Board.Host          == board.Host &&
                existing.Board.DirectoryName == board.DirectoryName &&
                existing.ThreadKey           == info.Key)
            {
                SelectedThreadTab = existing;
                await RefreshThreadAsync(existing).ConfigureAwait(true);
                return;
            }
        }

        var tab = CreateThreadTab(board, info);

        // 既読位置があれば渡しておく (描画後に JS が該当レスへスクロール)
        var savedIndex = _threadIndex.Load(board.Host, board.DirectoryName, info.Key);
        if (savedIndex?.LastReadPostNumber is int savedPos)
            tab.ScrollTargetPostNumber = savedPos;
        if (savedIndex?.LastReadMarkPostNumber is int savedMark)
            tab.ReadMarkPostNumber = savedMark;
        if (savedIndex?.OwnPostNumbers is { Length: > 0 } savedOwn)
        {
            foreach (var n in savedOwn) tab.OwnPostNumbers.Add(n);
        }

        tab.IsFavorited = Favorites.IsThreadFavorited(board.Host, board.DirectoryName, info.Key);
        tab.State       = stateHint ?? LogMarkState.Cached;

        ThreadTabs.Add(tab);
        SelectedThreadTab = tab;

        try
        {
            tab.IsBusy = true;

            // ---- Step 1: ディスクにキャッシュ済の dat があれば先に表示する ----
            var local = await _datClient.LoadFromDiskAsync(board, info.Key).ConfigureAwait(true);
            var prevCount = 0;
            if (local is not null && local.Posts.Count > 0)
            {
                AppendPostsWithNg(tab, local.Posts);
                tab.DatSize   = local.DatSize;
                prevCount     = local.Posts.Count;
                StatusMessage = $"{info.Title}: {prevCount} レス (差分取得中...)";
            }
            else
            {
                StatusMessage = $"{info.Title} を取得中...";
            }

            // ---- Step 2: サーバから取得 ----
            DatFetchResult result;
            if (local is null)
            {
                var progress = new Progress<IReadOnlyList<Post>>(batch =>
                {
                    AppendPostsWithNg(tab, batch);
                    StatusMessage = $"{info.Title}: {tab.Posts.Count} レス取得中...";
                });
                result = await _datClient.FetchStreamingAsync(board, info.Key, progress).ConfigureAwait(true);
                StatusMessage = $"{info.Title}: {result.Posts.Count} レス ({result.DatSize / 1024} KB)";
            }
            else
            {
                var noProgress = new Progress<IReadOnlyList<Post>>(_ => { });
                result = await _datClient.FetchStreamingAsync(board, info.Key, noProgress).ConfigureAwait(true);

                if (result.Posts.Count > prevCount)
                {
                    var added = new List<Post>(result.Posts.Count - prevCount);
                    for (var i = prevCount; i < result.Posts.Count; i++) added.Add(result.Posts[i]);
                    // disk-first で初期表示済 → HTTP 差分は incremental (Phase 20)
                    AppendPostsWithNg(tab, added, isIncremental: true);
                    StatusMessage = $"{info.Title}: {added.Count} レス追加 (合計 {result.Posts.Count})";
                }
                else if (result.Posts.Count == prevCount)
                {
                    StatusMessage = $"{info.Title}: 新着なし ({result.Posts.Count} レス)";
                }
                else
                {
                    StatusMessage = $"{info.Title}: dat 縮小 ({prevCount} → {result.Posts.Count})";
                }

                tab.DatSize = result.DatSize;
            }

            SaveFetchedPostCount(board, info.Key, result.Posts.Count);

            // dat 落ちヒントで開いたスレは、HTTP が成功しても (= 復活) 一覧側では Dropped のまま。
            if (stateHint != LogMarkState.Dropped)
            {
                NotifyThreadListLogMark(board, info.Key, LogMarkState.Cached);
                tab.State = LogMarkState.Cached;
            }
        }
        catch (Exception ex)
        {
            // ローカル dat が表示できていれば、タイトル/状態色は維持して状況だけステータス通知。
            if (tab.Posts.Count > 0)
            {
                tab.State    = stateHint ?? LogMarkState.Dropped;
                StatusMessage = $"{info.Title}: 取得失敗 (キャッシュ表示中) — {ex.Message}";
            }
            else
            {
                tab.Header    = "(取得失敗)";
                StatusMessage = $"スレ取得失敗: {ex.Message}";
            }
        }
        finally
        {
            tab.IsBusy = false;
        }
    }

    /// <summary>スレ更新ボタン / スレ一覧で開いているスレを再クリックされた時に呼ばれる。
    /// HTTP Range で差分のみ取得して、増分レスを JS に append する。</summary>
    public async Task RefreshThreadAsync(ThreadTabViewModel tab)
    {
        if (tab.IsBusy) return;

        var prevCount = tab.Posts.Count;
        try
        {
            tab.IsBusy    = true;
            StatusMessage = $"{tab.Header} を更新中...";

            var noProgress = new Progress<IReadOnlyList<Post>>(_ => { });
            var result     = await _datClient.FetchStreamingAsync(tab.Board, tab.ThreadKey, noProgress).ConfigureAwait(true);

            if (result.Posts.Count > prevCount)
            {
                var newPosts = new List<Post>(result.Posts.Count - prevCount);
                for (var i = prevCount; i < result.Posts.Count; i++) newPosts.Add(result.Posts[i]);
                // RefreshThreadAsync は既存タブの差分追加なので incremental (Phase 20)
                AppendPostsWithNg(tab, newPosts, isIncremental: true);
                StatusMessage = $"{tab.Header}: {newPosts.Count} レス追加 (合計 {result.Posts.Count})";
            }
            else if (result.Posts.Count == prevCount)
            {
                StatusMessage = $"{tab.Header}: 新着なし";
            }
            else
            {
                StatusMessage = $"{tab.Header}: dat 縮小 ({prevCount} → {result.Posts.Count})";
            }

            tab.DatSize = result.DatSize;

            SaveFetchedPostCount(tab.Board, tab.ThreadKey, result.Posts.Count);

            NotifyThreadListLogMark(tab.Board, tab.ThreadKey, LogMarkState.Cached);
            tab.State = LogMarkState.Cached;
        }
        catch (Exception ex)
        {
            StatusMessage = $"{tab.Header} の更新失敗: {ex.Message}";
        }
        finally
        {
            tab.IsBusy = false;
        }
    }

    /// <summary>現在のどんぐり Cookie / メール認証ログイン状態から、
    /// 投稿ダイアログの認証モード初期値を推定する。
    /// メール認証済み → MailAuth / acorn だけ → Cookie / なにもない → None。</summary>
    private PostAuthMode DefaultPostAuthMode()
    {
        // ログイン状態は MainViewModel.DonguriLoginStatus に App.xaml.cs から push される。
        // "ログイン済" を含む文字列なら mail auth Cookie が CookieJar に居ると見做す。
        if (DonguriLoginStatus is { Length: > 0 } s && s.Contains("ログイン済", StringComparison.Ordinal))
            return PostAuthMode.MailAuth;
        if (_donguri.AcornValue is not null) return PostAuthMode.Cookie;
        return PostAuthMode.None;
    }

    /// <summary>スレ表示タブの「書き込み」ボタンから呼ばれる。投稿ダイアログ (PostDialog) を開き、
    /// 送信成功時はそのスレの差分取得を走らせて新規投稿を表示に取り込む。</summary>
    private void OpenPostDialog(ThreadTabViewModel tab)
    {
        OpenPostDialogInternal(tab, "");
    }

    /// <summary>スレ表示の post-no クリックメニュー → 「返信」で呼ばれる。
    /// 投稿ダイアログを「&gt;&gt;N\n」プリフィル状態で開く。</summary>
    public void OpenReplyDialog(ThreadTabViewModel tab, int postNumber)
    {
        OpenPostDialogInternal(tab, $">>{postNumber}\n");
    }

    private void OpenPostDialogInternal(ThreadTabViewModel tab, string initialMessage)
    {
        var vm = new PostFormViewModel(_postClient, tab.Board, tab.ThreadKey, tab.Title, DefaultPostAuthMode());
        if (!string.IsNullOrEmpty(initialMessage)) vm.Message = initialMessage;
        var dlg = new ChBrowser.Views.PostDialog(vm, System.Windows.Application.Current?.MainWindow);
        dlg.Closed += async (_, _) =>
        {
            if (!dlg.WasSubmitted) return;
            UpdateDonguriStatus();
            await RefreshThreadAsync(tab).ConfigureAwait(true);
        };
        dlg.Show();
    }

    /// <summary>スレ表示の post-no クリックメニュー → 「NG登録」で呼ばれる。
    /// 即時 NG 登録ダイアログ (NgQuickAddDialog) を、現在板スコープ + 期限 1 日 + 抽出値プリフィルで開く。
    /// OK で <see cref="ChBrowser.Services.Ng.NgService"/> に rule を追加する。
    ///
    /// <para>この経路は WebView2 の WebMessageReceived → UI スレッドで呼ばれるが、
    /// そこから直接 <see cref="System.Windows.Window.ShowDialog"/> すると WebView2 native 層の
    /// 入力処理と modal 再入が競合して稀に STATUS_BREAKPOINT (0x80000003) で落ちる。
    /// 1 cycle 遅らせて WebView2 callback スタックを巻き戻してから modal を開く。</para></summary>
    public void OpenNgQuickAdd(ThreadTabViewModel tab, string target, string value)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(new Action(() => OpenNgQuickAddCore(tab, target, value)));
    }

    private void OpenNgQuickAddCore(ThreadTabViewModel tab, string target, string value)
    {
        try
        {
            if (tab.Board is null)
            {
                StatusMessage = "NG 登録: 対象スレの板情報が取得できませんでした";
                return;
            }

            var dlg = new ChBrowser.Views.NgQuickAddDialog(tab.Board, target, value)
            {
                Owner = System.Windows.Application.Current?.MainWindow,
            };
            var ok = dlg.ShowDialog();
            if (ok != true || dlg.CreatedRule is not { } rule) return;

            // 既存 + 新規 を 1 つの set にして保存。
            var newRules = new System.Collections.Generic.List<ChBrowser.Models.NgRule>(_ng.All.Rules) { rule };
            _ng.Save(new ChBrowser.Models.NgRuleSet { Version = 1, Rules = newRules });

            StatusMessage = $"NG ルールを追加しました ({rule.Target}: {rule.Pattern}) — 開いているスレタブは閉じて開き直すと反映されます";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OpenNgQuickAdd] failed: {ex}");
            StatusMessage = $"NG 登録ダイアログでエラー: {ex.Message}";
        }
    }

    /// <summary>選択中の板スレ一覧タブから「新規スレ立て」ボタンで呼ばれる (Phase 8c)。
    /// 成功時は subject.txt を再取得して新スレが一覧に出るようにする。</summary>
    public void OpenNewThreadDialog()
    {
        var listTab = SelectedThreadListTab;
        if (listTab?.Board is null)
        {
            StatusMessage = "新規スレ立ては板タブで実行してください";
            return;
        }
        var board = listTab.Board;
        var vm    = new PostFormViewModel(_postClient, board, DefaultPostAuthMode());
        var dlg   = new ChBrowser.Views.PostDialog(vm, System.Windows.Application.Current?.MainWindow);
        dlg.Closed += async (_, _) =>
        {
            if (!dlg.WasSubmitted) return;
            UpdateDonguriStatus();
            StatusMessage = $"スレ立て成功 — {board.BoardName} の一覧を更新中...";
            await LoadThreadListAsync(new BoardViewModel(board)).ConfigureAwait(true);
        };
        dlg.Show();
    }

    /// <summary>スレ表示タブのゴミ箱アイコンから呼ばれる。dat 削除 + タブ close + スレ一覧の青丸を消す。
    /// お気に入りに登録されていた場合は同時に外す。</summary>
    public void DeleteThreadLog(ThreadTabViewModel tab)
    {
        try
        {
            _datClient.DeleteLog(tab.Board, tab.ThreadKey);
        }
        catch (Exception ex)
        {
            StatusMessage = $"ログ削除に失敗: {ex.Message}";
            return;
        }
        ThreadTabs.Remove(tab);
        NotifyThreadListLogMark(tab.Board, tab.ThreadKey, LogMarkState.None);

        var favEntry    = Favorites.FindThread(tab.Board.Host, tab.Board.DirectoryName, tab.ThreadKey);
        var alsoUnfaved = favEntry is not null;
        if (favEntry is not null)
        {
            Favorites.Remove(favEntry);
            RefreshFavoritedStateOfAllTabs();
        }

        StatusMessage = alsoUnfaved
            ? $"{tab.Header} のログを削除しました (お気に入りからも外しました)"
            : $"{tab.Header} のログを削除しました";
    }

    /// <summary>JS からのスクロール位置通知を idx.json に保存し、タブ側にも反映する (タブ復帰時の再復元用)。</summary>
    public void UpdateScrollPosition(Board board, string threadKey, int topPostNumber)
    {
        var existing = _threadIndex.Load(board.Host, board.DirectoryName, threadKey);
        var updated  = (existing ?? new ThreadIndex(null, null)) with { LastReadPostNumber = topPostNumber };
        _threadIndex.Save(board.Host, board.DirectoryName, threadKey, updated);

        var tab = FindThreadTab(board, threadKey);
        if (tab is not null) tab.ScrollTargetPostNumber = topPostNumber;
    }

    /// <summary>JS からの「ここまで読んだ」位置通知 (Phase 19)。
    /// 値は減少しない (= 既存値より大きい場合のみ更新)、idx.json に永続化 + 該当タブの ReadMarkPostNumber を更新。</summary>
    public void UpdateReadMark(Board board, string threadKey, int postNumber)
    {
        var existing = _threadIndex.Load(board.Host, board.DirectoryName, threadKey);
        var prev     = existing?.LastReadMarkPostNumber ?? 0;
        if (postNumber <= prev) return;

        var updated = (existing ?? new ThreadIndex(null, null)) with { LastReadMarkPostNumber = postNumber };
        _threadIndex.Save(board.Host, board.DirectoryName, threadKey, updated);

        var tab = FindThreadTab(board, threadKey);
        if (tab is not null) tab.ReadMarkPostNumber = postNumber;
    }

    /// <summary>JS の post-no メニュー → 「自分の書き込み」トグル経由で呼ばれる。
    /// タブの <see cref="ThreadTabViewModel.OwnPostNumbers"/> を更新 + idx.json 永続化 +
    /// JS への増分通知 (<c>updateOwnPosts</c>) を発火する。
    /// 同じ要望が連投されたときの冪等性も担保 (= isOwn=true で既に入っている場合は no-op)。</summary>
    public void ToggleOwnPost(ThreadTabViewModel tab, int postNumber, bool isOwn)
    {
        bool changed = isOwn
            ? tab.OwnPostNumbers.Add(postNumber)
            : tab.OwnPostNumbers.Remove(postNumber);
        if (!changed) return;

        // idx.json に永続化
        var existing = _threadIndex.Load(tab.Board.Host, tab.Board.DirectoryName, tab.ThreadKey);
        var updated  = (existing ?? new ThreadIndex(null, null)) with
        {
            OwnPostNumbers = tab.OwnPostNumbers.OrderBy(n => n).ToArray(),
        };
        _threadIndex.Save(tab.Board.Host, tab.Board.DirectoryName, tab.ThreadKey, updated);

        // JS への増分 push
        tab.OwnPostsUpdate = new OwnPostsUpdateData(new[] { new OwnPostChange(postNumber, isOwn) });
    }

    /// <summary>JS の openThread メッセージ (host/dir/key/title 同梱) からスレを開く。
    /// 通常の板タブ・お気に入りディレクトリ展開タブの両方の経路でこれを呼ぶ。</summary>
    public Task OpenThreadFromListAsync(string host, string directoryName, string key, string title, LogMarkState? stateHint = null)
    {
        var board = ResolveBoard(host, directoryName, "");
        var info  = new ThreadInfo(key, title, 0, 0); // PostCount/Order は dat 取得後に意味を持たない
        return OpenThreadAsync(board, info, stateHint);
    }

    /// <summary>(host, dir, key) で開いている ThreadTab を引く。なければ null。</summary>
    private ThreadTabViewModel? FindThreadTab(Board board, string threadKey)
        => ThreadTabs.FirstOrDefault(t =>
            t.Board.Host          == board.Host &&
            t.Board.DirectoryName == board.DirectoryName &&
            t.ThreadKey           == threadKey);

    /// <summary>idx.json の <c>LastFetchedPostCount</c> を更新する (取得成功直後に呼ぶ)。
    /// 既存値があれば <c>with</c> で上書き、無ければ新規作成。</summary>
    private void SaveFetchedPostCount(Board board, string threadKey, int postCount)
    {
        var existing = _threadIndex.Load(board.Host, board.DirectoryName, threadKey);
        var updated  = (existing ?? new ThreadIndex(null, null)) with { LastFetchedPostCount = postCount };
        _threadIndex.Save(board.Host, board.DirectoryName, threadKey, updated);
    }

    /// <summary>新規 ThreadTab を「コールバック + 初期 ViewMode」セット込みで生成する。
    /// このメソッドだけが <c>new ThreadTabViewModel(...)</c> を呼ぶ唯一の場所。</summary>
    private ThreadTabViewModel CreateThreadTab(Board board, ThreadInfo info)
    {
        var tab = new ThreadTabViewModel(
            board, info,
            closeCallback:          t => ThreadTabs.Remove(t),
            deleteCallback:         t => DeleteThreadLog(t),
            refreshCallback:        t => _ = RefreshThreadAsync(t),
            addToFavoritesCallback: t => ToggleThreadFavorite(t),
            writeCallback:          t => OpenPostDialog(t));

        tab.ViewMode = CurrentConfig.DefaultThreadViewMode switch
        {
            "Tree"      => ThreadViewMode.Tree,
            "DedupTree" => ThreadViewMode.DedupTree,
            _           => ThreadViewMode.Flat,
        };
        return tab;
    }
}
