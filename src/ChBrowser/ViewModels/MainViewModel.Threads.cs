using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Api;
using ChBrowser.Services.Llm;
using ChBrowser.Services.Storage;
using ChBrowser.Services.Url;
using CommunityToolkit.Mvvm.Input;

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
        // dat の 1 レス目はスレタイトルを保持している。アドレスバーから直接スレを開いた経路では
        // タブ作成時 Title が空文字なので、最初にこのメソッドに来た batch の中で 1 レス目を見つけたら
        // タイトル / タブヘッダを埋める。お気に入り登録の Title もここで揃う。
        foreach (var p in batch)
        {
            if (p.ThreadTitle is { Length: > 0 } t) { tab.EnsureTitleFromDat(t); break; }
        }
        var breakdown = _ng.ComputeHiddenWithBreakdown(batch.ToList(), tab.Board.Host, tab.Board.DirectoryName);
        var hidden    = breakdown.HiddenNumbers;
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
            tab.AddHiddenBreakdown(breakdown);
        }
        if (ReferenceEquals(tab, SelectedThreadTab))
            AboneStatus = $"あぼーん {tab.HiddenCount}";
    }

    // ---- 「自分の書き込みへの返信あり」検出 (= 状態マーク赤化) ----
    //
    // 仕様 (= Phase 23+):
    //   1. cache load 時はスキャンしない (= 既存スレ内の旧返信は赤化しない)。
    //   2. 差分取得 (= ApplyFetchDelta / 個別 refresh) で取得した「新着レス」の本文に、
    //      tab.OwnPostNumbers のレスへのアンカー (>>N / >>N-M) が含まれていれば <c>tab.HasReplyToOwn = true</c>。
    //   3. それ以外 (= 新着 0 件、dat 縮小、cache load) では <c>tab.HasReplyToOwn = false</c> にリセット。
    //   4. 最終的なスレ一覧の状態マークは <see cref="ComputeMarkState"/> で集約決定 (= Dropped &gt; RepliedToOwn &gt; Cached)。
    //   5. 永続化なし (= idx.json に持たない)。アプリ再起動 / 別経由の状態更新で自由に上書きされる。
    //   6. ToggleOwnPost からは発火しない (= 既存レスの own 切替で赤にはしない)。

    /// <summary>「&gt;&gt;N」「&gt;&gt;N-M」形式のレスアンカーを本文から抽出する正規表現。
    /// JS 側 <c>URL_OR_ANCHOR_RE</c> のアンカー部と同じ意味で、5ch dat の生本文 / HTML 化済本文どちらにも当たる。 </summary>
    private static readonly Regex AnchorRefRe = new(@">>\s*(\d+)(?:\s*-\s*(\d+))?", RegexOptions.Compiled);

    /// <summary>差分取得で来た新着 batch のうち、いずれかが tab.OwnPostNumbers のレスにアンカーしているかを判定する。
    /// 純粋関数 (= 副作用なし、tab.HasReplyToOwn の更新は呼び出し元責任)。 </summary>
    private static bool DeltaHasReplyToOwn(ThreadTabViewModel tab, IReadOnlyList<Post> deltaPosts)
    {
        if (tab.OwnPostNumbers.Count == 0) return false;
        if (deltaPosts.Count == 0) return false;
        foreach (var p in deltaPosts)
        {
            if (p.Body is null) continue;
            // 自分のレス自身は除外 (= 自分が自分宛にアンカーしていても「返信あり」扱いしない)。
            if (tab.OwnPostNumbers.Contains(p.Number)) continue;
            foreach (Match m in AnchorRefRe.Matches(p.Body))
            {
                if (!int.TryParse(m.Groups[1].Value, out var from)) continue;
                var to = m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out var t) ? t : from;
                if (to < from) (from, to) = (to, from);
                // range が極端に広いケース (荒らし対策) は判定スキップ
                if (to - from > 1024) continue;
                for (var n = from; n <= to; n++)
                {
                    if (tab.OwnPostNumbers.Contains(n)) return true;
                }
            }
        }
        return false;
    }

    /// <summary>取得 / 更新の終端で「最終的にスレ一覧に push する状態」を 1 か所で算定する。
    /// 優先順: <see cref="LogMarkState.Dropped"/> &gt; <see cref="LogMarkState.RepliedToOwn"/> &gt; <see cref="LogMarkState.Cached"/>。
    /// 緑 (Updated) は subject.txt 由来で、ここに来るのはスレを実際に開いて取得した直後なので Cached が基本。 </summary>
    private static LogMarkState ComputeMarkState(ThreadTabViewModel tab, LogMarkState? stateHint)
    {
        if (stateHint == LogMarkState.Dropped) return LogMarkState.Dropped;
        if (tab.HasReplyToOwn)                 return LogMarkState.RepliedToOwn;
        return LogMarkState.Cached;
    }

    /// <summary>HTTP fetch 結果を tab に反映する: 新着があれば append + 「以降新レス」mark を新着先頭に立て、
    /// 無ければ既存 mark を消す。<see cref="OpenThreadAsync"/> (cache 読み込み後の delta 経路) と
    /// <see cref="RefreshThreadAsync"/> 共通のロジック。
    ///
    /// <paramref name="prevCount"/>=0 (= ローカルキャッシュ無し or 空) の場合は「全件初取得」相当で
    /// あり、新着扱いではないので mark は立てない (= ラベル無し)。
    ///
    /// mark は <see cref="AppendPostsWithNg"/> より前に書く (= LatestAppendBatch DP 発火時に
    /// OnAppendBatchChanged が binding.MarkPostNumber を読んで JSON に乗せるため)。
    ///
    /// 副作用: <see cref="StatusMessage"/> と <c>tab.DatSize</c> も更新する。 </summary>
    private void ApplyFetchDelta(ThreadTabViewModel tab, int prevCount, DatFetchResult result, string headerForStatus)
    {
        ChBrowser.Services.Logging.LogService.Instance.Write(
            $"[fetchDelta] {headerForStatus}: prevCount={prevCount}, result.Count={result.Posts.Count}");

        if (result.Posts.Count > prevCount)
        {
            var added = new List<Post>(result.Posts.Count - prevCount);
            for (var i = prevCount; i < result.Posts.Count; i++) added.Add(result.Posts[i]);
            if (prevCount > 0)
            {
                tab.MarkPostNumber = prevCount + 1;
                ChBrowser.Services.Logging.LogService.Instance.Write(
                    $"[fetchDelta]   → set tab.MarkPostNumber={prevCount + 1}, then AppendPostsWithNg(added={added.Count}, incremental=true)");
            }
            else
            {
                ChBrowser.Services.Logging.LogService.Instance.Write(
                    $"[fetchDelta]   → prevCount=0 (初取得相当) なので mark は立てず、AppendPostsWithNg(added={added.Count}, incremental=true)");
            }
            AppendPostsWithNg(tab, added, isIncremental: true);
            // 仕様: 「差分取得で来た新着」が own への返信を含む場合だけ赤化フラグを立てる。
            // cache load や ToggleOwnPost では発火しない。
            tab.HasReplyToOwn = DeltaHasReplyToOwn(tab, added);
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[fetchDelta]   → HasReplyToOwn={tab.HasReplyToOwn} (delta scan over {added.Count} new posts)");
            tab.StatusMessage = $"{headerForStatus}: {added.Count} レス追加 (合計 {result.Posts.Count})";
        }
        else if (result.Posts.Count == prevCount)
        {
            // 新着 0 件: 既存の「以降新レス」ラベルを消す (= mark 消去 → MarkPostNumberPush で JS にも push)。
            // 仕様: 状態マーク変更が起きるイベントなので、HasReplyToOwn も false にリセット (= 上書き許可)。
            tab.MarkPostNumber = null;
            tab.HasReplyToOwn  = false;
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[fetchDelta]   → 新着 0 件、tab.MarkPostNumber=null、HasReplyToOwn=false");
            tab.StatusMessage = $"{headerForStatus}: 新着なし ({result.Posts.Count} レス)";
        }
        else
        {
            // dat 縮小も「新着情報のクリア」と同等扱い (= mark / HasReplyToOwn 両方クリア)。
            tab.MarkPostNumber = null;
            tab.HasReplyToOwn  = false;
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[fetchDelta]   → dat 縮小、tab.MarkPostNumber=null、HasReplyToOwn=false");
            tab.StatusMessage = $"{headerForStatus}: dat 縮小 ({prevCount} → {result.Posts.Count})";
        }
        tab.DatSize = result.DatSize;
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
        // アドレスバー直接入力等で title が無い経路はタブ見出しが空のままになるため、
        // dat 取得が走るまでの間 placeholder を出しておく (= AppendPostsWithNg 内の
        // EnsureTitleFromDat で 1 レス目到着と同時に正しいタイトルに置き換わる)。
        if (string.IsNullOrEmpty(info.Title)) tab.Header = "(取得中…)";

        // 既読位置があれば渡しておく (描画後に JS が該当レスを viewport 下端に揃える)。
        // 「以降新レス」ラベル位置 (MarkPostNumber) は永続化しない設計のため、ここでは初期化しない
        // (= タブ生成時のデフォルト null のままで OK。本セッション中のリフレッシュで新着が来た瞬間に立つ)。
        var savedIndex = _threadIndex.Load(board.Host, board.DirectoryName, info.Key);
        if (savedIndex?.LastReadPostNumber is int savedPos)
            tab.ScrollTargetPostNumber = savedPos;
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
                tab.DatSize       = local.DatSize;
                prevCount         = local.Posts.Count;
                tab.StatusMessage = $"{info.Title}: {prevCount} レス (差分取得中...)";
                // 仕様: cache load では「自分への返信」検知を走らせない (= 既存スレ内の旧返信は赤化しない)。
                // 初回 false で OK。次の差分取得が新着 + 返信を含む場合に ApplyFetchDelta が立てる。
                tab.HasReplyToOwn = false;
            }
            else
            {
                tab.StatusMessage = $"{info.Title} を取得中...";
            }

            // ---- Step 2: サーバから取得 ----
            DatFetchResult result;
            if (local is null)
            {
                var progress = new Progress<IReadOnlyList<Post>>(batch =>
                {
                    AppendPostsWithNg(tab, batch);
                    tab.StatusMessage = $"{info.Title}: {tab.Posts.Count} レス取得中...";
                });
                result = await _datClient.FetchStreamingAsync(board, info.Key, progress).ConfigureAwait(true);
                tab.StatusMessage = $"{info.Title}: {result.Posts.Count} レス ({result.DatSize / 1024} KB)";
            }
            else
            {
                var noProgress = new Progress<IReadOnlyList<Post>>(_ => { });
                result = await _datClient.FetchStreamingAsync(board, info.Key, noProgress).ConfigureAwait(true);
                ApplyFetchDelta(tab, prevCount, result, info.Title);
            }

            SaveFetchedPostCount(board, info.Key, result.Posts.Count);

            // 最終状態は ComputeMarkState で算定 (= Dropped > RepliedToOwn > Cached の優先順)。
            // tab.HasReplyToOwn は ApplyFetchDelta が直前にセット済 (= delta scan の結果)。
            var finalState = ComputeMarkState(tab, stateHint);
            NotifyThreadListLogMark(board, info.Key, finalState);
            tab.State = finalState;
        }
        catch (System.Net.Http.HttpRequestException hex)
            when (hex.StatusCode == System.Net.HttpStatusCode.NotFound && tab.Posts.Count == 0)
        {
            // 5ch.io でのアーカイブ (dat 落ち) スレは raw dat が 404 になり、本アプリで読めない。
            // ローカルキャッシュも無いケース (= タブを今初めて作っているケース) では、失敗タブを残しても
            // ユーザにとって価値が無いので削除し、システムブラウザで read.cgi の HTML を開いて代替する。
            // ローカルキャッシュがある場合は別 catch (= キャッシュ表示維持) でフォールスルーする。
            var fallbackUrl = $"https://{board.Host}/test/read.cgi/{board.DirectoryName}/{info.Key}/";
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[openThread] 404 で dat 取得不可。ブラウザ fallback: {fallbackUrl}");
            StatusMessage = $"dat 落ち (404) — ブラウザで開きます: {fallbackUrl}";
            ThreadTabs.Remove(tab);
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = fallbackUrl,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex2) { System.Diagnostics.Debug.WriteLine($"[browser fallback] {ex2.Message}"); }
            return;
        }
        catch (Exception ex)
        {
            // ローカル dat が表示できていれば、タイトル/状態色は維持して状況だけステータス通知。
            if (tab.Posts.Count > 0)
            {
                tab.State         = stateHint ?? LogMarkState.Dropped;
                tab.StatusMessage = $"{info.Title}: 取得失敗 (キャッシュ表示中) — {ex.Message}";
            }
            else
            {
                tab.Header        = "(取得失敗)";
                tab.StatusMessage = $"スレ取得失敗: {ex.Message}";
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
            tab.IsBusy        = true;
            tab.StatusMessage = $"{tab.Header} を更新中...";

            var noProgress = new Progress<IReadOnlyList<Post>>(_ => { });
            var result     = await _datClient.FetchStreamingAsync(tab.Board, tab.ThreadKey, noProgress).ConfigureAwait(true);
            ApplyFetchDelta(tab, prevCount, result, tab.Header);

            SaveFetchedPostCount(tab.Board, tab.ThreadKey, result.Posts.Count);

            // 最終状態算定: HasReplyToOwn は ApplyFetchDelta の delta scan で直前に決まっているので
            // ここではそれを ComputeMarkState で集約するだけ。Refresh 経路は stateHint なしなので Dropped 評価しない。
            var finalState = ComputeMarkState(tab, stateHint: null);
            NotifyThreadListLogMark(tab.Board, tab.ThreadKey, finalState);
            tab.State = finalState;
        }
        catch (Exception ex)
        {
            tab.StatusMessage = $"{tab.Header} の更新失敗: {ex.Message}";
        }
        finally
        {
            tab.IsBusy = false;
        }
    }

    /// <summary>現在のどんぐり Cookie / メール認証ログイン状態から、
    /// 投稿ダイアログの認証モード初期値を推定する。
    /// メール認証済み → MailAuth / 通常 (anon) acorn だけ → Cookie / なにもない → None。
    /// 新設計では「Cookie モード」は anon スロット (= state.json) を使うため、
    /// jar 側の <see cref="DonguriService.AcornValue"/> ではなく
    /// <see cref="DonguriService.AnonAcornValue"/> で判定する。</summary>
    private PostAuthMode DefaultPostAuthMode()
    {
        // ログイン状態は MainViewModel.DonguriLoginStatus に App.xaml.cs から push される。
        // "ログイン済" を含む文字列なら mail auth Cookie が CookieJar に居ると見做す。
        if (DonguriLoginStatus is { Length: > 0 } s && s.Contains("ログイン済", StringComparison.Ordinal))
            return PostAuthMode.MailAuth;
        if (_donguri.AnonAcornValue is not null) return PostAuthMode.Cookie;
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

    // ---- AI チャット (LLM 連携) ----
    //
    // スレッドタブの「AI」ボタンから呼ばれる。スレ単位でモードレスのチャットウィンドウを開き、
    // システムプロンプトに「そのスレの全レス内容」を埋め込んだ状態で LLM と会話する。
    // 同じスレで再度ボタンを押した場合は既存ウィンドウをアクティブ化する (= 会話履歴を失わない)。

    /// <summary>AI チャットウィンドウはアプリ全体で 1 つだけ持つシングルトン。
    /// attached スレは <see cref="SelectedThreadTab"/> に追従して動的に切り替わる
    /// (= スレタブ閉じてもウィンドウは残り、別タブに切り替わると中身が差し替わる)。
    /// null のときは「閉じている」状態、non-null のときは「開いている」状態。</summary>
    private ChBrowser.Views.AiChatWindow? _aiChatWindow;
    private AiChatViewModel?              _aiChatViewModel;

    /// <summary>アドレスバーの「AI」ボタンから呼ばれるコマンド。
    /// 現在選択中のスレッドタブがあればそのスレを文脈に、無ければスレッド非アタッチの AI チャットを開く。
    /// 既に開いていれば現在の <see cref="SelectedThreadTab"/> に合わせて中身を切り替えてアクティブ化する。</summary>
    [RelayCommand]
    private void OpenAiChatFromTopBar()
        => OpenAiChat(SelectedThreadTab);

    /// <summary>AI チャットウィンドウを開く (= まだ無ければ生成、既にあれば <paramref name="tab"/> に合わせて context 切替)。
    /// 会話履歴は AiChatViewModel 内で維持され、ウィンドウを閉じない限りタブ切替や全タブ閉じで失われない。
    /// 会話開始時点の <see cref="ThreadTabViewModel.Posts"/> のスナップショットから <see cref="ThreadToolset"/>
    /// を作り、AI には function calling 経由でレスを取りに来させる方式。dat 全量をプロンプトに乗せない
    /// (= 100k トークン超のスレでも回せる)。</summary>
    public void OpenAiChat(ThreadTabViewModel? tab)
    {
        if (_aiChatWindow is not null && _aiChatViewModel is not null)
        {
            // 既に開いている: SelectedThreadTab の context に合わせて中身を差し替え + activate。
            SwitchAiChatContextTo(tab);
            try { _aiChatWindow.Activate(); } catch { /* 閉じる途中等は無視 */ }
            return;
        }

        var settings = LlmSettings.FromConfig(CurrentConfig);
        var toolset      = BuildToolsetForTab(tab);
        var systemPrompt = BuildAiSystemPrompt(toolset);
        var title        = ResolveAiChatTitle(tab);
        var vm           = new AiChatViewModel(_llmClient, settings, systemPrompt, title, toolset);
        var window       = new ChBrowser.Views.AiChatWindow(vm, System.Windows.Application.Current?.MainWindow);
        _aiChatWindow    = window;
        _aiChatViewModel = vm;
        window.Closed += (_, _) =>
        {
            _aiChatWindow    = null;
            _aiChatViewModel = null;
        };
        window.Show();
    }

    /// <summary>AI チャットウィンドウが開いている状態で <see cref="SelectedThreadTab"/> が変わったときの context 差替。
    /// SelectedThreadTab の partial method (= <c>OnSelectedThreadTabChanged</c>) と、attached スレタブが
    /// 閉じられた瞬間 (= <c>OnThreadTabsCollectionChanged</c>) の両方から呼ばれる。
    /// ウィンドウが閉じていれば no-op。</summary>
    private void SwitchAiChatContextTo(ThreadTabViewModel? tab)
    {
        if (_aiChatViewModel is null) return;
        var toolset      = BuildToolsetForTab(tab);
        var systemPrompt = BuildAiSystemPrompt(toolset);
        var title        = ResolveAiChatTitle(tab);
        _aiChatViewModel.SwitchContext(toolset, systemPrompt, title);
    }

    /// <summary>tab non-null なら attached モード、null なら非アタッチモードの ThreadToolset を構築する。
    /// 永続化された既読位置 (idx.json) と VM 上の動的状態 (Mark / Own / HasReplyToOwn) も注入。</summary>
    private ThreadToolset BuildToolsetForTab(ThreadTabViewModel? tab)
    {
        var (openThread, openBoard, openThreadList) = BuildOpenInAppCallbacks();
        var dataLoader = BuildThreadDataLoader();
        if (tab is null)
        {
            return new ThreadToolset(
                dataLoader:               dataLoader,
                openThreadInAppAsync:     openThread,
                openBoardInAppAsync:      openBoard,
                openThreadListInAppAsync: openThreadList);
        }
        var savedIdx = _threadIndex.Load(tab.Board.Host, tab.Board.DirectoryName, tab.ThreadKey);
        return new ThreadToolset(
            dataLoader:               dataLoader,
            openThreadInAppAsync:     openThread,
            openBoardInAppAsync:      openBoard,
            openThreadListInAppAsync: openThreadList,
            attachedBoard:            tab.Board,
            attachedThreadKey:        tab.ThreadKey,
            attachedTitle:            tab.Title,
            attachedPosts:            tab.Posts,
            attachedLastRead:         savedIdx?.LastReadPostNumber,
            attachedMarkPostNumber:   tab.MarkPostNumber,
            attachedOwnPostNumbers:   tab.OwnPostNumbers,
            attachedHasReplyToOwn:    tab.HasReplyToOwn);
    }

    /// <summary>AI チャットウィンドウの「タイトル領域」用文字列。attached あれば そのスレタイ、無ければ汎用ラベル。</summary>
    private static string ResolveAiChatTitle(ThreadTabViewModel? tab)
        => string.IsNullOrEmpty(tab?.Title) ? "(スレッド指定なし)" : tab.Title!;

    /// <summary>ThreadToolset 用の <see cref="ThreadDataLoader"/> を 1 つ構築する。
    /// flat 板リストの provider はクロージャ経由で <see cref="BoardCategories"/> を常に最新の状態で読む。
    /// LRU キャッシュはこのインスタンスに紐づくので、AI チャットウィンドウごとに独立した会話キャッシュになる。</summary>
    private ThreadDataLoader BuildThreadDataLoader()
        => new(
            subject:            _subjectClient,
            dat:                _datClient,
            flatBoardsProvider: () => BoardCategories
                .SelectMany(c => c.Boards.Select(bvm => bvm.Board))
                .ToArray(),
            resolveBoard:       ResolveBoard);

    /// <summary>open_thread_in_app / open_board_in_app / open_thread_list_in_app から呼ばれる
    /// UI アクション delegate を 3 つ作って返す。それぞれ既存の対応関数を叩く薄ラッパ。
    /// 失敗時は AI 側に伝わるメッセージを文字列で返す (= 例外で UI を落とさない)。</summary>
    private (
        Func<string, Task<string>>                                               openThread,
        Func<string, Task<string>>                                               openBoard,
        Func<string, IReadOnlyList<AiSearchResultEntry>, Task<string>>           openThreadList)
        BuildOpenInAppCallbacks()
    {
        Func<string, Task<string>> openThread = async url =>
        {
            var t = AddressBarParser.Parse(url);
            if (t.Kind != AddressBarTargetKind.Thread)
                return $"thread_url の解釈に失敗: {url}";
            try
            {
                await OpenThreadByUrlAsync(t.Host, t.Directory, t.ThreadKey).ConfigureAwait(true);
                return $"スレッドをアプリで開きました: {url}";
            }
            catch (Exception ex)
            {
                return $"スレッドを開けませんでした: {ex.Message}";
            }
        };
        Func<string, Task<string>> openBoard = async url =>
        {
            var t = AddressBarParser.Parse(url);
            if (t.Kind != AddressBarTargetKind.Board)
                return $"board_url の解釈に失敗: {url}";
            try
            {
                await OpenBoardByUrlAsync(t.Host, t.Directory).ConfigureAwait(true);
                return $"板をアプリで開きました: {url}";
            }
            catch (Exception ex)
            {
                return $"板を開けませんでした: {ex.Message}";
            }
        };
        Func<string, IReadOnlyList<AiSearchResultEntry>, Task<string>> openThreadList = async (title, entries) =>
        {
            try
            {
                await OpenAiSearchResultsAsync(title, entries).ConfigureAwait(true);
                return $"検索結果タブを開きました: \"{title}\" ({entries.Count} 件)";
            }
            catch (Exception ex)
            {
                return $"検索結果タブを開けませんでした: {ex.Message}";
            }
        };
        return (openThread, openBoard, openThreadList);
    }

    /// <summary>AI からの「複数スレッドを 1 タブに並べて見せる」要求を受けて新規スレ一覧タブを作る。
    /// 同 <paramref name="label"/> で 2 度呼ばれたら既存タブを再利用 (deterministic Guid)。
    /// 各エントリの板を <see cref="ResolveBoard"/> で正規化し、板ごとにログ状態 (青/緑/茶) を計算して行に乗せる。</summary>
    public async Task OpenAiSearchResultsAsync(string label, IReadOnlyList<AiSearchResultEntry> entries)
    {
        var log = ChBrowser.Services.Logging.LogService.Instance;
        log.Write($"[OpenAiSearchResults] 開始: label=\"{label}\", entries={entries.Count}");

        // 同じ検索ラベルなら同じタブを再利用 (deterministic Guid)。
        var tabId  = ComputeAiSearchResultsTabId(label);
        var tab    = ThreadListTabs.FirstOrDefault(t => t.FavoritesFolderId == tabId);
        var reused = tab is not null;
        if (tab is null)
        {
            tab = new ThreadListTabViewModel(tabId, $"🤖 {Truncate(label, 24)}", t => ThreadListTabs.Remove(t));
            ThreadListTabs.Add(tab);
        }
        SelectedThreadListTab = tab;
        log.Write($"[OpenAiSearchResults]   タブ {(reused ? "再利用" : "新規作成")}: tabId={tabId}, ThreadListTabs.Count={ThreadListTabs.Count}");

        if (tab.IsBusy)
        {
            log.Write("[OpenAiSearchResults] tab.IsBusy=true のため処理スキップ");
            return;
        }

        try
        {
            tab.IsBusy        = true;
            tab.StatusMessage = $"AI 検索結果を構築中... ({entries.Count} 件)";
            // 全件構築前に UI に「タブ生成 + ステータス更新」を 1 フレーム見せる (= AI ツール連打中の応答性向上)。
            await Task.Yield();

            // 各エントリを (board, ThreadInfo) に分解。同じ板への問い合わせはまとめてキャッシュする。
            var perBoardLogStates = new Dictionary<(string Host, string Dir), IReadOnlySet<string>>();
            var allFavKeys        = Favorites.CollectFavoriteThreadKeys();

            var items     = new List<ThreadListItem>(entries.Count);
            var orderSeq  = 0;
            var skipped   = 0;
            foreach (var e in entries)
            {
                var bp = AddressBarParser.Parse(e.ThreadUrl);
                if (bp.Kind != AddressBarTargetKind.Thread)
                {
                    log.Write($"[OpenAiSearchResults]   skip (URL parse失敗): {e.ThreadUrl}");
                    skipped++;
                    continue;
                }

                var board = ResolveBoard(bp.Host, bp.Directory, "");
                var info  = new ThreadInfo(bp.ThreadKey, e.Title, e.PostCount, ++orderSeq);

                var boardKey = (board.Host, board.DirectoryName);
                if (!perBoardLogStates.TryGetValue(boardKey, out var keysWithLog))
                {
                    keysWithLog = _datClient.EnumerateExistingThreadKeys(board);
                    perBoardLogStates[boardKey] = keysWithLog;
                }

                LogMarkState state;
                if (!keysWithLog.Contains(info.Key))
                {
                    state = LogMarkState.None;
                }
                else
                {
                    var idx     = _threadIndex.Load(board.Host, board.DirectoryName, info.Key);
                    var fetched = idx?.LastFetchedPostCount;
                    var hasNew  = fetched is int f && info.PostCount > 0 && info.PostCount > f;
                    state       = hasNew ? LogMarkState.Updated : LogMarkState.Cached;
                }

                var fav = allFavKeys.Contains((board.Host, board.DirectoryName, info.Key));
                items.Add(new ThreadListItem(info, board.Host, board.DirectoryName, board.BoardName, state, fav));
            }

            log.Write($"[OpenAiSearchResults]   items 構築完了: items={items.Count}, skipped={skipped}, boards={perBoardLogStates.Count}");
            tab.SetItems(items, DateTimeOffset.UtcNow);
            tab.Header        = $"🤖 {Truncate(label, 24)} ({items.Count})";
            tab.StatusMessage = $"AI 検索結果: {items.Count} 件 (\"{label}\")";
            log.Write($"[OpenAiSearchResults] 完了: tab.Header=\"{tab.Header}\"");
        }
        catch (Exception ex)
        {
            log.Write($"[OpenAiSearchResults] 例外: {ex.GetType().Name}: {ex.Message}");
            log.Write($"[OpenAiSearchResults] stacktrace: {ex.StackTrace}");
            tab.StatusMessage = $"AI 検索結果タブの構築失敗: {ex.Message}";
        }
        finally
        {
            tab.IsBusy = false;
        }
    }

    /// <summary>AI 検索結果タブの deterministic Guid (= 同ラベルで再呼出時に同タブを再利用)。
    /// <see cref="ComputeNextThreadTabId"/> と同じ仕掛けで「prefix:label」を SHA-1 した上位 16 バイトから Guid を作る。</summary>
    private static Guid ComputeAiSearchResultsTabId(string label)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("aisearch:" + label);
        using var sha = System.Security.Cryptography.SHA1.Create();
        var hash = sha.ComputeHash(bytes);
        var guid = new byte[16];
        Array.Copy(hash, guid, 16);
        return new Guid(guid);
    }

    /// <summary>function calling + plan-revise + cross-thread access 前提のシステムプロンプトを組み立てる。
    /// attached モード / スタンドアロンモードのどちらでも同じ関数で対応 (toolset.HasAttached で分岐)。
    /// dat 本体は含めず、必要なレス本文は LLM がツール経由で取りに来る。指示追従性のため
    /// 「create_plan → 各タスク実行 → complete_task → revise_plan (必要時) → 最終回答」の段取りを強制する。</summary>
    private static string BuildAiSystemPrompt(ThreadToolset toolset)
    {
        var sb = new StringBuilder();
        if (toolset.HasAttached)
        {
            sb.AppendLine("あなたは5chネラーです。ユーザは attached されたスレッドや、他のスレ / 板についても質問してくることがあります。");
            sb.AppendLine("スレッドの内容はあらかじめ全部渡されてはいません。提供されているツール (function calling) を使い、必要な部分を取得しながら回答してください。");
            sb.AppendLine();
            sb.AppendLine("【attached スレッド】");
            sb.Append("- タイトル: ").AppendLine(toolset.ThreadTitle);
            if (!string.IsNullOrEmpty(toolset.BoardName))
                sb.Append("- 板: ").AppendLine(toolset.BoardName);
            sb.AppendLine();
            sb.AppendLine("【スレ現況スナップショット (会話開始時点・固定値)】");
            sb.Append(toolset.BuildInitialStateSnapshot());
            sb.AppendLine("※ この情報は get_thread_state を呼ばなくても既に読めている状態。");
            sb.AppendLine("  スレが進んでいる可能性があると判断したときだけ get_thread_state で再取得すること。");
            sb.AppendLine();
            var ownSummary = toolset.BuildOwnPostsWithRepliesSummary();
            if (!string.IsNullOrEmpty(ownSummary))
            {
                sb.AppendLine("【自分の書き込みとそれへの反応 (会話開始時点・固定値)】");
                sb.Append(ownSummary);
                sb.AppendLine();
                sb.AppendLine("※ 上記は会話開始時点のスナップショット。返信が省略されている場合は get_my_posts / find_replies_to で完全取得可。");
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine("あなたは5chネラーかつ AI アシスタントです。特定のスレッドにはアタッチされていませんが、");
            sb.AppendLine("ワークスペース横断ツール (list_boards / list_threads / get_posts(thread_url=...) / open_*) を使えば");
            sb.AppendLine("板一覧から目的のスレを探し当ててその内容を読み、必要ならアプリで開かせる、まで可能です。");
            sb.AppendLine();
        }

        sb.AppendLine("【作業の進め方 (必ず守ること)】");
        sb.AppendLine("1. まず最初に `create_plan` を呼び、ユーザの依頼を達成するためのタスク列を宣言する。");
        sb.AppendLine("   - タスク id は \"t1\", \"t2\", ... のような短い文字列にする。");
        sb.AppendLine("   - 各 description は「何を取得して何を確認するか」が読んで分かるように書く。");
        sb.AppendLine("   - 単純な質問なら 1 タスクでよい。複雑な依頼ほど細かく分割する。");
        sb.AppendLine("2. **create_plan / revise_plan の直後に必ず `estimate_and_confirm` を呼んでコスト見積りをする** (= 必須ステップ、強制ガードあり)。");
        sb.AppendLine("   - サーバが heavy 判定したら、その応答に含まれる confirmation_template を最終回答テキストとしてそのまま出力し、ユーザの返答を待つ (= ループ停止)。");
        sb.AppendLine("   - サーバが light 判定したら、そのまま plan 実行に進む。");
        sb.AppendLine("   - estimate_and_confirm を呼ばずに他ツール (list_*, get_*, find_*, open_*, search_*) を呼ぶとエラー JSON が返り進行できないので必ず先に呼ぶ。");
        sb.AppendLine("3. plan のタスクを 1 つずつ実行する。スレッド読み取りやワークスペース横断ツール群を使う。");
        sb.AppendLine("4. 1 タスク終わるごとに必ず `complete_task` を呼び、finding にこのタスクで得た知見を 1〜3 文で残す。");
        sb.AppendLine("   - **板やスレを特定 / 列挙したタスクの finding には、必ず thread_url / board_url を含めること。**");
        sb.AppendLine("     例: 「漫画板 (https://mao.5ch.io/comic/) の総合スレ >>1767582872 (https://fate.5ch.io/test/read.cgi/comic/1767582872/) を特定」のように URL を残す。");
        sb.AppendLine("     URL を残さないと後で recall_archive で引き直す羽目になる (= コスト無駄)。");
        sb.AppendLine("5. 途中で当初の plan が不適切だと判明したら遠慮なく `revise_plan` で plan を書き直す。");
        sb.AppendLine("   想定外の発見 / 余計な調査が要らないと分かった / 順序を変えたい / 抜けがあった、いずれも書き直して OK。");
        sb.AppendLine("   revise_plan 後は再度 estimate_and_confirm が必要 (= ガードが立ち直る)。");
        sb.AppendLine("6. すべてのタスクが完了したら、findings を統合してユーザへの最終回答をテキストで出力する。最終回答は markdown OK。");
        sb.AppendLine("   - 最終回答だけ出してツールを呼ばないラウンド = エージェントループ終了の合図。");
        sb.AppendLine();
        sb.AppendLine("【重い作業前の確認ルール】");
        sb.AppendLine("create_plan を立てた直後は、**強制で estimate_and_confirm ツールを呼ぶ**こと (= ガードあり)。");
        sb.AppendLine("ツールにこれから実行する plan のコスト見積りを渡し、サーバが heavy / light を判定する。");
        sb.AppendLine();
        sb.AppendLine("**heavy 判定の条件** (サーバ側ロジック):");
        sb.AppendLine("- estimated_tool_calls >= 8 (= 残り見込みツール呼び出し 8 回以上)");
        sb.AppendLine("- scan_breadth = \"many\" (= 5 板以上) または \"all_boards\"");
        sb.AppendLine("- reads_full_thread = true (= 1 スレで 200 レス超を読み通す)");
        sb.AppendLine();
        sb.AppendLine("**サーバ応答ハンドリング**:");
        sb.AppendLine("- severity=\"light\" / verdict=\"proceed\" の場合: そのまま plan のタスク実行に進む");
        sb.AppendLine("- severity=\"heavy\" / verdict=\"halt_and_confirm\" の場合:");
        sb.AppendLine("    **次のラウンドでツール呼び出しせず、応答に含まれる confirmation_template の文章をそのまま最終回答テキストとして出力**。");
        sb.AppendLine("    エージェントループはここで一旦停止 → ユーザの返答を待つ。");
        sb.AppendLine();
        sb.AppendLine("ユーザの次の発言を受けて plan を確定する:");
        sb.AppendLine("- 「(A) で」「実施」「やって」「そのまま」等 → 元の plan のまま実行に進む (新しい create_plan / revise_plan は不要、estimate は再呼出してから実行)");
        sb.AppendLine("- 「(B) で」「軽量で」「軽くして」等 → revise_plan で軽量化してから estimate_and_confirm を再呼出し → 実行");
        sb.AppendLine("- 「(C) で」または新指示 → revise_plan で plan を組み直し → estimate_and_confirm → 実行");
        sb.AppendLine();
        sb.AppendLine("**見積りは正直に**: 軽い作業なのに大袈裟に申告して毎回確認させたり、重い作業を過小評価して飛び込んだりしない。");
        sb.AppendLine("数えるツール: list_* / get_* / find_* / search_* / open_*。estimate_and_confirm / create_plan / complete_task / revise_plan は数えない。");
        sb.AppendLine();
        sb.AppendLine("【plan / task 用ツール】");
        sb.AppendLine("- create_plan(tasks) : 計画を宣言 (最初に必ず呼ぶ)");
        sb.AppendLine("- estimate_and_confirm(estimated_tool_calls, scan_breadth, reads_full_thread, plan_summary, lighter_alternative?) :");
        sb.AppendLine("    create_plan / revise_plan の直後に必ず呼ぶ。サーバが heavy 判定したら confirmation_template を最終回答として出力。");
        sb.AppendLine("- complete_task(id, finding) : 1 タスクを完了 + 知見メモを残す (URL は finding に必ず含める)");
        sb.AppendLine("- revise_plan(tasks) : 計画を書き直す (= 既存 id は status/finding を引き継ぐ)");
        sb.AppendLine();
        sb.AppendLine("【過去内容の参照ツール】");
        sb.AppendLine("セッション内の出来事 (= 過去のユーザ発言 / アシスタント本文 / 思考過程 / ツール結果) は");
        sb.AppendLine("コンテキスト節約のためプロンプトから省略されていることがある。必要なら以下で引き戻せる。");
        sb.AppendLine("- list_archive(filter?) : 目録を取得。kind / task_id / keyword / tool_name / limit で絞り込める。");
        sb.AppendLine("- recall_archive(id) : 指定 id の原文を完全に取り出す。長文を再展開するのでコンテキストを食う、必要最小限に使う。");
        sb.AppendLine("プレースホルダ ( {\"omitted\":true,...,\"archive_id\":\"aN\"} ) を見たら、その archive_id を recall_archive に渡せば原文が戻る。");
        sb.AppendLine();
        sb.AppendLine("【セッション状態の自動注入】");
        sb.AppendLine("毎ラウンド先頭に「[セッション状態スナップショット]」という system メッセージが自動的に挿入される。");
        sb.AppendLine("その中身は (a) 現在の計画と各タスクの finding、(b) 参照可能な archive エントリ目録 (= 新しい順、上位 30 件の brief)。");
        sb.AppendLine();
        sb.AppendLine("【スレッド読み取りツール】 (= thread_url 省略時は attached スレを対象とする)");
        sb.AppendLine("- get_thread_meta(thread_url?) : スレタイ + 板 + 総レス数 + >>1 全文");
        if (toolset.HasAttached)
            sb.AppendLine("- get_thread_state : attached スレの状態 (既読位置 / 新着範囲 / 自分のレス / 返信フラグ) を 1 発取得");
        sb.AppendLine("- get_posts(start, end, thread_url?) : 範囲指定でレス取得 (1 度に最大 50 件)");
        sb.AppendLine("- search_posts(keyword, limit?, thread_url?) : 本文部分一致検索");
        sb.AppendLine("- get_post(number, thread_url?) : 単一レス全文");
        sb.AppendLine("- get_posts_by_id(id, thread_url?) : 特定 ID の全レス");
        if (toolset.HasAttached)
            sb.AppendLine("- get_my_posts(include_replies?, replies_per_post_limit?) : attached スレで自分の書き込みとその返信ツリー");
        sb.AppendLine("- find_replies_to(number, limit?, thread_url?) : 指定レスに >>N でぶら下がっているレス一覧");
        sb.AppendLine("- find_popular_posts(top_k?, range_start?, range_end?, min_count?, thread_url?) : 被アンカー数が多いレスを上位から");
        sb.AppendLine();
        sb.AppendLine("【ワークスペース横断ツール】");
        sb.AppendLine("- list_boards(keyword?, limit?) : アプリにロード済みの板一覧 (= bbsmenu)。keyword で板名 / カテゴリ部分一致フィルタ");
        sb.AppendLine("- list_threads(board_url, keyword?, limit?) : 指定板のスレ一覧 (= subject.txt)。keyword でスレタイ部分一致");
        sb.AppendLine("- open_thread_in_app(thread_url) : 指定スレをユーザのアプリで開く (= スレ表示ペインにタブ追加)");
        sb.AppendLine("- open_board_in_app(board_url) : 指定板をユーザのアプリで開く (= スレ一覧ペインにタブ追加)");
        sb.AppendLine("- open_thread_list_in_app(title, threads) : **複数のスレを 1 つのタブに並べて見せる** (= スレ一覧ペインに新規タブ作成)");
        sb.AppendLine("  - 板をまたいだ検索結果や、テーマの近いスレ群を提示するときに使う。1 件だけなら open_thread_in_app の方が向く。");
        sb.AppendLine("  - threads は list_threads で得た thread_url / title / post_count をそのまま流用すれば OK。");
        sb.AppendLine();
        sb.AppendLine("【代表的な使い分け】");
        if (toolset.HasAttached)
        {
            sb.AppendLine("- 「新着の話題は？」→ スナップショットの新着範囲を見る → get_posts で範囲読み込み → find_popular_posts(range) で人気レス補強");
            sb.AppendLine("- 「自分への返信ある？」→ スナップショット冒頭の『自分の書き込みとそれへの反応』で多くは即答可。省略があれば get_my_posts / find_replies_to");
        }
        sb.AppendLine("- 「読んでおくべきレスは？」→ find_popular_posts で被アンカー多いレスを抽出 → get_post で本文確認");
        sb.AppendLine("- 「ある話題に対する反応は？」→ 該当レス番号に find_replies_to");
        sb.AppendLine("- 「この作品のアニメスレを探して」(= 横断検索 + 確証 + 開く):");
        sb.AppendLine("  1) list_boards(keyword=\"アニメ\") で候補板を取得");
        sb.AppendLine("  2) 各候補板に対し list_threads(board_url, keyword=作品名) でスレ候補を絞る");
        sb.AppendLine("  3) 確信が持てない場合は get_posts(thread_url=候補, start=1, end=10) で >>1〜10 を読んで本物か判定");
        sb.AppendLine("  4) 確証が取れたら open_thread_in_app(thread_url) でユーザのアプリで開く");
        sb.AppendLine("  5) findings に「どの作品・どの板・どのスレを開いたか」を残して最終回答");
        sb.AppendLine("- 「○○の関連スレを集めて」「△△に関するスレ一覧見せて」(= 複数ヒットを 1 タブに):");
        sb.AppendLine("  まず「単純な単語マッチで取れる」か「曖昧 / 略称 / ジャンル判定が必要」かを判定する。");
        sb.AppendLine();
        sb.AppendLine("  **(A) 単純な単語マッチで取れるケース** (例: 「初音ミク」「ガンダム」「ワンピース」など、スレタイにそのまま入りやすい):");
        sb.AppendLine("    1) list_boards で関連カテゴリの板候補を取得");
        sb.AppendLine("    2) 各板に list_threads(board_url, keyword=作品名) でヒットを集める");
        sb.AppendLine("    3) ヒットした {thread_url, title, post_count} を open_thread_list_in_app の threads 配列にそのまま流し込む");
        sb.AppendLine();
        sb.AppendLine("  **(B) 曖昧 / 略称 / ジャンル判定が必要なケース** (例: 「ソニー製品」「ダン飯 (=ダンジョン飯)」「異世界転生もの」):");
        sb.AppendLine("    単純な keyword マッチでは取りこぼす (= スレタイに「ソニー」と直書きされてない、ダンジョン飯は「ダン飯」と略される 等)。");
        sb.AppendLine("    **scan モード 3 段階** で進める。板段階でも keyword を絞り込まないのが重要 (= 「漫画」keyword だと「マンガ」「コミック」「漫画作品」等の関連板を取りこぼす)。");
        sb.AppendLine();
        sb.AppendLine("    1) **板 scan** — list_boards を **keyword 省略**で呼ぶ。全板 (= 200 件前後) のタイトル / カテゴリが返ってくる。");
        sb.AppendLine("       返ってきたカテゴリ列を読んで、テーマに関連するカテゴリを **複数まとめて pick** する。");
        sb.AppendLine("       例: 「ダン飯関連」→ category=\"漫画\" / \"漫画作品\" / \"漫画キャラ\" / \"漫画サロン\" / \"アニメ\" / \"アニメ実況\" / \"声優\" の **全板を pick**");
        sb.AppendLine("            (= 漫画系板も アニメ系板もそれぞれ複数あるので、1 カテゴリ 1 板ではなく、関連カテゴリ全体を拾うこと)");
        sb.AppendLine("       例: 「ソニー製品」→ category=\"家電\" / \"AV機器\" / \"PCハードウェア\" / \"ゲーム機\" / \"携帯ゲーム\" / \"デジカメ\" の **全板**");
        sb.AppendLine("       **板段階で絞りすぎない** ことが取りこぼし回避の最大のポイント。漫画 / アニメ系板はそれぞれ数十あり、漫画板 1 つだけ見て終わるのは NG。");
        sb.AppendLine();
        sb.AppendLine("    2) **スレ scan** — 上で pick した板すべてに対し、list_threads(board_url) を **keyword 省略**で呼ぶ。");
        sb.AppendLine("       各板からタイトル一覧 (= 100 件単位) が返ってくる。");
        sb.AppendLine();
        sb.AppendLine("    3) **AI 自身がタイトルから判定** — 全部のタイトルを読んで、テーマに関連すると判断したスレだけを pick。");
        sb.AppendLine("       略称 / 連想 / シリーズ / ジャンル判定は LLM (= あなた) の知識で行う:");
        sb.AppendLine("         - ソニー: 「PS5 総合スレ」「α7 III 質問スレ」「WH-1000XM5 ノイズキャンセル」「Bravia 4K」→ 全部関連");
        sb.AppendLine("         - ダン飯: 「ダン飯ネタバレ」「ダンジョン飯 Part 50」「マルシル萌え」「ライオス」→ 全部関連");
        sb.AppendLine("         - 異世界転生: 「無職転生」「ありふれた職業」「Re:ゼロ」「転スラ」→ 全部関連");
        sb.AppendLine();
        sb.AppendLine("    4) pick したスレの {thread_url, title, post_count} を open_thread_list_in_app に詰める。");
        sb.AppendLine();
        sb.AppendLine("  どちらのパターンでも:");
        sb.AppendLine("  - **URL は必ず list_threads の戻り値からコピペする** (= 自分で組み立てない)");
        sb.AppendLine("  - 1 件だけに絞れる場合は open_thread_in_app、複数なら open_thread_list_in_app");
        sb.AppendLine("  - findings に「板名: ヒット件数」のサマリを残して最終回答");
        sb.AppendLine("  - list_threads を 1 度も呼んでいないのに open_thread_list_in_app を呼ぼうとしたら、それは思考エラー (= URL を捏造している)。先に list_threads から戻ること。");
        sb.AppendLine();
        sb.AppendLine("【方針】");
        sb.AppendLine("- レス番号を引用するときは「>>N」の形式で書くこと。他スレを引用するときは「板/スレタイ >>N」のように板名 / スレタイ + レス番号で文脈を補うこと。");
        sb.AppendLine("- 推測ではなく実際のスレ内容を根拠に回答すること。スレに無いことは「スレ内には書かれていない」と答える。");
        sb.AppendLine("- **thread_url / board_url は絶対に推測や記憶で生成しない**。必ず以下のいずれかから取得した URL のみを使う:");
        sb.AppendLine("  (a) attached スレッドの thread_url (= スナップショットに記載済み)");
        sb.AppendLine("  (b) list_boards / list_threads / get_thread_meta 等のツール結果に含まれていた URL");
        sb.AppendLine("  (c) ユーザがメッセージ中で明示的に渡してきた URL");
        sb.AppendLine("  「https://krsw.5ch.io/ai/thread_id_1/」のように <key> 部分を placeholder で埋めた URL や、");
        sb.AppendLine("  「以前見た覚えがある」レベルの記憶 URL は禁止 (= 必ず list_threads を最新で呼び直す)。");
        sb.AppendLine("- 大量のレスを読む必要があるときは get_posts を分割して呼ぶこと (=「読んだフリ」をしない)。");
        sb.AppendLine("- ユーザが見ることを意図した依頼 (= 「開いて」「見せて」「探して開いて」「次スレ開いて」「○○板見せて」など) を出してきた場合、");
        sb.AppendLine("  対象が特定できた時点で **open_thread_in_app / open_board_in_app を必ず呼んでアプリで開く**こと。");
        sb.AppendLine("  「見つけました、URL は…」とテキスト報告だけで終わらせない。報告 + open ツール呼び出し までで 1 タスク。");
        sb.AppendLine("  本物か微妙な候補が複数ある場合は、最有力候補 1 つを開いた上で「他の候補もあれば教えて」と書く方が親切。");
        sb.AppendLine("- **最終回答は必ず <think>...</think> の外側で本文を出力すること。<think> ブロックだけで応答を終わらせない**");
        sb.AppendLine("  (= 思考過程は <think> 内、ユーザ向けの結論はその外、という分離を守る)。");
        sb.AppendLine("- 思考時間が長くなりすぎないように。要点が見えたら think を閉じてユーザ向けの本文に移ること。");
        return sb.ToString();
    }

    private void OpenPostDialogInternal(ThreadTabViewModel tab, string initialMessage)
    {
        var vm = new PostFormViewModel(_postClient, tab.Board, tab.ThreadKey, tab.Title, DefaultPostAuthMode());
        if (!string.IsNullOrEmpty(initialMessage)) vm.Message = initialMessage;
        _ = ApplyLineLimitFromSettingAsync(vm, tab.Board);
        var dlg = new ChBrowser.Views.PostDialog(vm, System.Windows.Application.Current?.MainWindow);
        dlg.Closed += async (_, _) =>
        {
            if (!dlg.WasSubmitted) return;
            // 投稿前の post 数と submittedMessage を保持しておき、refresh 後に
            // 「increment した新着のうち本文が一番似ているもの」を own としてマークする。
            var prevCount        = tab.Posts.Count;
            var submittedMessage = vm.Message ?? "";
            UpdateDonguriStatus();
            await RefreshThreadAsync(tab).ConfigureAwait(true);
            AutoMarkSubmittedPostAsOwn(tab, prevCount, submittedMessage);
        };
        dlg.Show();
    }

    /// <summary>板の SETTING.TXT から行数上限 (BBS_LINE_NUMBER) を非同期で取り出し、ダイアログの VM に注入する。
    /// ローカル未保存ならネットワーク取得、有ればローカル読みのみ (= 自動更新はしない、設計通り)。
    /// 取得失敗時は <see cref="PostFormViewModel.LineLimit"/> を null のままにする (= ダイアログ側は「N 行」のみ表示)。
    /// ダイアログ open と並行で走るため、初回は表示が「N 行」→「N / M 行」に切り替わる過渡状態が見える。</summary>
    private async Task ApplyLineLimitFromSettingAsync(PostFormViewModel vm, Board board)
    {
        try
        {
            var settings = await _settingClient.GetOrFetchAsync(board).ConfigureAwait(true);
            vm.LineLimit = SettingTxtClient.GetLineLimit(settings);
        }
        catch (Exception ex)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[SETTING.TXT] 行数上限取得に失敗 {board.Host}/{board.DirectoryName}: {ex.Message}");
        }
    }

    /// <summary>スレ一覧タブの右クリック「SETTING.TXTの更新」用エントリ。
    /// ローカルキャッシュを無視して常にサーバから再取得し、保存する。成功 / 失敗はステータスバーに表示。</summary>
    public async Task RefreshSettingTxtAsync(Board board)
    {
        StatusMessage = $"SETTING.TXT を取得中 — {board.BoardName}…";
        try
        {
            await _settingClient.FetchAndSaveAsync(board).ConfigureAwait(true);
            StatusMessage = $"SETTING.TXT を更新しました — {board.BoardName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"SETTING.TXT 取得失敗: {ex.Message}";
        }
    }

    // ---- 自分の書き込みの自動 own マーク ----
    //
    // 仕様: 本アプリの投稿ダイアログ経由で投稿成功した直後の refresh で増えた新着のうち、
    // 「投稿した本文 (submittedMessage) との類似度が最大、かつ閾値以上」のレスを own にする。
    // 見つからなければ LogService にその旨を出して何もしない (= サイレント諦め)。
    // 新規スレ立て (= 1 レス目自明) には適用しない。

    /// <summary>類似度しきい値。これ以上で「同一 post」とみなす。
    /// 5ch dat 本文は <c>&lt;br&gt;</c> / HTML 化 / entity 等の加工が入るため、正規化後でも完全一致しないことが多い。
    /// 0.7 だと短文で誤検知しがち、0.9 だと AA 等の加工が強い投稿で取りこぼす。0.8 を実用デフォルトに採用。 </summary>
    private const double OwnPostSimilarityThreshold = 0.8;

    /// <summary>refresh で増えた新着レスのうち、submittedMessage に最も近いものを own としてマーク。
    /// 該当なし (= 閾値超えなし or 新着 0 件) なら LogService に出してサイレントに終了。 </summary>
    private void AutoMarkSubmittedPostAsOwn(ThreadTabViewModel tab, int prevCount, string submittedMessage)
    {
        if (string.IsNullOrWhiteSpace(submittedMessage))
        {
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[autoOwn] {tab.Header}: submittedMessage が空、自動 own スキップ");
            return;
        }
        if (tab.Posts.Count <= prevCount)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[autoOwn] {tab.Header}: refresh で新着が来なかった (= サーバ伝播遅延の可能性)、自動 own 諦め");
            return;
        }

        var target = NormalizeBodyForMatch(submittedMessage);
        if (target.Length == 0)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[autoOwn] {tab.Header}: 正規化後の submittedMessage が空、自動 own スキップ");
            return;
        }

        // 新着 (= prevCount..end) を全部走査して、類似度が最大かつ閾値以上のレスを探す。
        Post?  bestPost = null;
        double bestSim  = 0.0;
        for (var i = prevCount; i < tab.Posts.Count; i++)
        {
            var p = tab.Posts[i];
            var bodyNorm = NormalizeBodyForMatch(p.Body ?? "");
            if (bodyNorm.Length == 0) continue;
            var sim = StringSimilarity(target, bodyNorm);
            if (sim > bestSim) { bestSim = sim; bestPost = p; }
        }

        if (bestPost is null || bestSim < OwnPostSimilarityThreshold)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[autoOwn] {tab.Header}: 自分の書き込みが見つからず諦め (新着={tab.Posts.Count - prevCount}, 最高類似度={bestSim:F2}, 閾値={OwnPostSimilarityThreshold:F2})");
            return;
        }

        ChBrowser.Services.Logging.LogService.Instance.Write(
            $"[autoOwn] {tab.Header}: r{bestPost.Number} を自動 own にマーク (類似度={bestSim:F2})");
        ToggleOwnPost(tab, bestPost.Number, isOwn: true);
    }

    /// <summary>本文と submittedMessage を比較するために正規化する。
    /// dat の <c>&lt;br&gt;</c> は改行に、<c>&lt;a&gt;...&lt;/a&gt;</c> は visible テキストだけに、HTML entity は復元、
    /// 改行コードは LF、各行 trim、前後の空白を除く。 </summary>
    private static string NormalizeBodyForMatch(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = System.Text.RegularExpressions.Regex.Replace(s, @"\s*<br\s*/?>\s*", "\n");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"<a\s[^>]*>([^<]*)</a>", "$1");
        t = System.Net.WebUtility.HtmlDecode(t);
        t = t.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = t.Split('\n');
        var sb = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line.Trim());
        }
        // 連続改行は 1 つに
        var collapsed = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\n+", "\n");
        return collapsed.Trim();
    }

    /// <summary>2 つの文字列の類似度 (= 0.0〜1.0)。Levenshtein 距離を max(len) で正規化したもの。
    /// 完全一致で 1.0、まったく違うほど 0.0 に近づく。両方空文字列なら 1.0。 </summary>
    private static double StringSimilarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1.0;
        var max  = Math.Max(a.Length, b.Length);
        if (max == 0) return 1.0;
        var dist = LevenshteinDistance(a, b);
        return 1.0 - (double)dist / max;
    }

    /// <summary>Levenshtein 距離 (= 編集距離: 1 文字単位の挿入/削除/置換の最少回数)。
    /// 計算量 O(|a|×|b|)、空間 O(min(|a|,|b|))。5ch のレス本文は数百〜千文字程度なので実用上問題ない。 </summary>
    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        // 短い方を b にして空間 O(min(...))
        if (a.Length < b.Length) (a, b) = (b, a);
        var prev = new int[b.Length + 1];
        var cur  = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
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

            // 開いている全スレタブに「新ルールで新たに hidden になるレス番号」を即時反映する。
            // 各タブの現在可視レス (tab.Posts) に対して NgService で再計算 → 差分集合を JS に push。
            // 連鎖あぼーんは「過去の hidden レス経由」までは追えない (= 過去 hidden は tab.Posts に居ない) が、
            // 「現状可視レス内の連鎖」までは正しく扱える。完全な反映が要る場合はタブを開き直す運用。
            ApplyNewlyHiddenToOpenTabs(rule);

            StatusMessage = $"NG ルールを追加しました ({rule.Target}: {rule.Pattern})";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OpenNgQuickAdd] failed: {ex}");
            StatusMessage = $"NG 登録ダイアログでエラー: {ex.Message}";
        }
    }

    /// <summary>NG ルール追加直後に、開いている全スレタブで「現状可視レスのうち新たに hidden になる番号」を
    /// 計算し、JS に <c>setHiddenPosts</c> で push する。あわせてタブの内部状態 (Posts / HiddenCount /
    /// HiddenByRule / HiddenByChain) も整合させる。
    ///
    /// 制約: tab.Posts は「現時点で可視」のレスのみ保持。過去に既に hidden になっていたレス経由の連鎖は
    /// この経路では追えない。完全に正しい結果が必要なら従来通りスレを開き直す運用 (= 全レスから再計算)。</summary>
    private void ApplyNewlyHiddenToOpenTabs(ChBrowser.Models.NgRule justAdded)
    {
        foreach (var tab in ThreadTabs)
        {
            if (tab.Posts.Count == 0) continue;
            var breakdown = _ng.ComputeHiddenWithBreakdown(
                tab.Posts.ToList(), tab.Board.Host, tab.Board.DirectoryName);
            var newlyHidden = breakdown.HiddenNumbers;
            if (newlyHidden.Count == 0) continue;

            // 内部 Posts を「可視のみ」に更新 + カウンタ加算
            var visible = new System.Collections.Generic.List<ChBrowser.Models.Post>(tab.Posts.Count - newlyHidden.Count);
            foreach (var p in tab.Posts)
                if (!newlyHidden.Contains(p.Number)) visible.Add(p);
            tab.ReplaceVisiblePostsAfterNgAdd(visible, newlyHidden, breakdown);

            if (ReferenceEquals(tab, SelectedThreadTab))
                AboneStatus = $"あぼーん {tab.HiddenCount}";
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
        _ = ApplyLineLimitFromSettingAsync(vm, board);
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
        => DeleteThreadLog(tab.Board, tab.ThreadKey, tab.Header);

    /// <summary>(板, スレキー, 表示用タイトル) のプリミティブ版。タブが開いていない経路
    /// (= スレ一覧行の右クリックメニュー) から呼べる。タブが開いていれば close も同時に行う。</summary>
    public void DeleteThreadLog(Board board, string threadKey, string title)
    {
        try
        {
            _datClient.DeleteLog(board, threadKey);
        }
        catch (Exception ex)
        {
            StatusMessage = $"ログ削除に失敗: {ex.Message}";
            return;
        }

        // 該当 ThreadTab が開いていれば close (= 「ログ削除」直後にスレ表示が残るのを避ける)。
        // ログ削除でも「うっかり消した → 復元したい」というユースケースが普通にあるため、
        // 再オープン履歴には積む (= 通常の close と同じ扱い)。再オープン時は dat を取り直して開く。
        var openTab = FindThreadTab(board, threadKey);
        if (openTab is not null) ThreadTabs.Remove(openTab);

        NotifyThreadListLogMark(board, threadKey, LogMarkState.None);

        var favEntry    = Favorites.FindThread(board.Host, board.DirectoryName, threadKey);
        var alsoUnfaved = favEntry is not null;
        if (favEntry is not null)
        {
            Favorites.Remove(favEntry);
            RefreshFavoritedStateOfAllTabs();
        }

        StatusMessage = alsoUnfaved
            ? $"{title} のログを削除しました (お気に入りからも外しました)"
            : $"{title} のログを削除しました";
    }

    /// <summary>JS からのスクロール位置通知を tab の in-memory プロパティ <see cref="ThreadTabViewModel.ScrollTargetPostNumber"/>
    /// にだけ反映する。idx.json への永続化は行わない (= タブを閉じる / アプリを終了する瞬間に
    /// <see cref="FlushScrollPositionToDisk"/> でまとめて書き込む方式)。
    /// これにより通常のスクロール中に頻繁な disk I/O が走らず、idx.json の hot-write も発生しない。
    ///
    /// 引数の <paramref name="readMaxPostNumber"/> は JS の <c>findReadProgressMaxNumber</c> が算定する
    /// 「先頭から連番が途切れず下端まで見終えた最大番号」(= 読了 prefix の最大番号)。 </summary>
    public void UpdateScrollPosition(Board board, string threadKey, int readMaxPostNumber)
    {
        var tab = FindThreadTab(board, threadKey);
        if (tab is not null) tab.ScrollTargetPostNumber = readMaxPostNumber;
    }

    /// <summary>タブの最新の <see cref="ThreadTabViewModel.ScrollTargetPostNumber"/> を idx.json に書き出す。
    /// 呼び出しタイミング: タブを閉じる瞬間 (<see cref="OnThreadTabsCollectionChanged"/>) と
    /// アプリ終了時 (<see cref="FlushAllThreadScrollPositionsToDisk"/>)。
    /// 値が null (= idx.json から読み込まれず JS からの送信もまだ来てない) なら no-op。
    /// 既存値の不要な上書きを避けるためである。 </summary>
    public void FlushScrollPositionToDisk(ThreadTabViewModel tab)
    {
        if (tab.ScrollTargetPostNumber is not int n) return;
        try
        {
            var existing = _threadIndex.Load(tab.Board.Host, tab.Board.DirectoryName, tab.ThreadKey);
            // 既存値と同じなら disk write を省略 (= 単なる cosmetic IO 抑止)
            if (existing?.LastReadPostNumber == n) return;
            var updated = (existing ?? new ThreadIndex(null, null)) with { LastReadPostNumber = n };
            _threadIndex.Save(tab.Board.Host, tab.Board.DirectoryName, tab.ThreadKey, updated);
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FlushScrollPos] {tab.Header}: {ex.Message}");
        }
    }

    /// <summary>現在開いているすべてのスレタブのスクロール位置を idx.json に flush する。
    /// アプリ終了時 (<see cref="MainWindow.OnClosing"/>) から呼ばれる。 </summary>
    public void FlushAllThreadScrollPositionsToDisk()
    {
        foreach (var tab in ThreadTabs) FlushScrollPositionToDisk(tab);
    }

    /// <summary>現在開いている全タブ (スレ一覧タブ + スレタブ) を <c>open_tabs.json</c> に保存する。
    /// それぞれのコレクションの順番をそのまま JSON に書き出す (= 次回起動で同じ並びで再オープン)。
    /// アプリ終了時 (<see cref="MainWindow.OnClosing"/>) から呼ばれる。
    /// 設定 <see cref="AppConfig.RestoreOpenTabsOnStartup"/> が OFF でも常に書き出すので、後で ON に戻したら復元可。</summary>
    public void SaveOpenTabsToDisk()
    {
        // スレ一覧タブ: 「板タブ」と「お気に入りフォルダタブ」を Kind で区別。
        // FavoritesFolderId == Guid.Empty は「お気に入り全体 (= 仮想ルート)」を表す。
        var listEntries = new System.Collections.Generic.List<OpenThreadListTabEntry>(ThreadListTabs.Count);
        foreach (var tab in ThreadListTabs)
        {
            if (tab.Board is { } b)
            {
                listEntries.Add(new OpenThreadListTabEntry(
                    Kind:          "board",
                    Host:          b.Host,
                    DirectoryName: b.DirectoryName,
                    FolderId:      null));
            }
            else if (tab.FavoritesFolderId is Guid id)
            {
                listEntries.Add(new OpenThreadListTabEntry(
                    Kind:          "favoritesFolder",
                    Host:          null,
                    DirectoryName: null,
                    FolderId:      id.ToString()));
            }
            // 上記いずれにも該当しないタブ (= 想定外、現状無し) は保存対象外。
        }

        var threadEntries = new System.Collections.Generic.List<OpenThreadTabEntry>(ThreadTabs.Count);
        foreach (var tab in ThreadTabs)
        {
            threadEntries.Add(new OpenThreadTabEntry(
                Host:          tab.Board.Host,
                DirectoryName: tab.Board.DirectoryName,
                Key:           tab.ThreadKey,
                Title:         tab.Title ?? ""));
        }

        _openTabsStorage.Save(listEntries, threadEntries);
    }

    /// <summary>前回終了時に保存されたタブ一覧 (スレ一覧タブ + スレタブ) を読み込み、保存されていた順番で再オープンする。
    /// 順番保証の仕組み:
    ///   - <see cref="LoadThreadListAsync"/> / <see cref="OpenFavoritesFolderAsync"/> / <see cref="OpenAllRootAsBoardAsync"/>
    ///     はいずれも同期前段で ThreadListTabs.Add を行い、その後で subject.txt / 集約取得を await する。
    ///   - <see cref="OpenThreadAsync"/> も同様に同期前段で ThreadTabs.Add を行ってから dat fetch を await する。
    ///   - ここで fire-and-forget で順に呼べば、各呼び出しの sync 部 (= タブ追加) は呼び出し順に同期実行され、
    ///     その後の I/O 部分だけが並列で走る (= 並びは保存順 = ユーザの要件、復元時間は最遅 I/O に律速)。
    /// 復元の発火タイミング: <see cref="InitializeAsync"/> 完了後 (= bbsmenu / Favorites がロード済の状態)。
    /// 設定 <see cref="AppConfig.RestoreOpenTabsOnStartup"/> による on/off は呼び出し側 (App) で判定する。</summary>
    public void RestoreOpenTabs()
    {
        var saved = _openTabsStorage.Load();
        if (saved.ThreadListTabs.Count == 0 && saved.ThreadTabs.Count == 0) return;

        ChBrowser.Services.Logging.LogService.Instance.Write(
            $"[restoreOpenTabs] 復元開始: スレ一覧タブ {saved.ThreadListTabs.Count} 件, スレタブ {saved.ThreadTabs.Count} 件");

        // (1) スレ一覧タブを先に — 復元順序のユーザ視認性のため、左ペインのタブを先に並べてからスレタブを並べる。
        foreach (var entry in saved.ThreadListTabs)
        {
            try
            {
                if (string.Equals(entry.Kind, "board", StringComparison.Ordinal))
                {
                    if (string.IsNullOrEmpty(entry.Host) || string.IsNullOrEmpty(entry.DirectoryName)) continue;
                    var board = ResolveBoard(entry.Host, entry.DirectoryName, "");
                    _ = LoadThreadListAsync(new BoardViewModel(board));
                }
                else if (string.Equals(entry.Kind, "favoritesFolder", StringComparison.Ordinal))
                {
                    if (!Guid.TryParse(entry.FolderId, out var id)) continue;
                    if (id == Guid.Empty)
                    {
                        _ = OpenAllRootAsBoardAsync();
                    }
                    else if (Favorites.FindById(id) is FavoriteFolderViewModel folder)
                    {
                        _ = OpenFavoritesFolderAsync(folder);
                    }
                    // フォルダが既に削除されていた場合は何もしない (= サイレントに skip)。
                }
            }
            catch (Exception ex)
            {
                ChBrowser.Services.Logging.LogService.Instance.Write(
                    $"[restoreOpenTabs] スレ一覧タブ復元失敗 ({entry.Kind} {entry.Host}/{entry.DirectoryName} {entry.FolderId}): {ex.Message}");
            }
        }

        // (2) スレタブ。OpenThreadAsync は同期前段で ThreadTabs.Add するので await 不要。
        foreach (var entry in saved.ThreadTabs)
        {
            try
            {
                _ = OpenThreadFromListAsync(entry.Host, entry.DirectoryName, entry.Key, entry.Title);
            }
            catch (Exception ex)
            {
                ChBrowser.Services.Logging.LogService.Instance.Write(
                    $"[restoreOpenTabs] スレタブ復元失敗 ({entry.Host}/{entry.DirectoryName}/{entry.Key}): {ex.Message}");
            }
        }
    }

    /// <summary>ThreadTabs の CollectionChanged ハンドラ。
    /// Remove (タブクローズ) のたびに、削除されたタブのスクロール位置を idx.json に永続化する。
    /// Reset (= Clear()) は OldItems が null になるため捕捉できないが、この app では使われていないので問題なし。 </summary>
    private void OnThreadTabsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is null) return;
        foreach (var item in e.OldItems)
        {
            if (item is ThreadTabViewModel tab)
            {
                FlushScrollPositionToDisk(tab);
                // 「タブを閉じる」操作の復元用履歴に積む (= DeleteThreadLog 等 suppress 中なら no-op)。
                PushRecentlyClosedThreadTab(tab);
                // 注: AI チャットウィンドウは閉じない。SelectedThreadTab がこの後 null か別タブに変わり、
                // OnSelectedThreadTabChanged → SwitchAiChatContextTo が発火して中身が自動切替する。
            }
        }
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

        // 仕様: own のトグルは「直前の差分取得」イベントではないので、ここで HasReplyToOwn は触らない。
        // 次の差分取得 (Refresh / OpenThread の HTTP fetch) で新着 + own 参照が見つかったときに赤化する。
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
            writeCallback:          t => OpenPostDialog(t),
            aiChatCallback:         t => OpenAiChat(t));

        tab.ViewMode = CurrentConfig.DefaultThreadViewMode switch
        {
            "Tree"      => ThreadViewMode.Tree,
            "DedupTree" => ThreadViewMode.DedupTree,
            _           => ThreadViewMode.Flat,
        };
        return tab;
    }
}
