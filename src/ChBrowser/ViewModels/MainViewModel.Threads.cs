using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
        // dat の 1 レス目はスレタイトルを保持している。アドレスバーから直接スレを開いた経路では
        // タブ作成時 Title が空文字なので、最初にこのメソッドに来た batch の中で 1 レス目を見つけたら
        // タイトル / タブヘッダを埋める。お気に入り登録の Title もここで揃う。
        foreach (var p in batch)
        {
            if (p.ThreadTitle is { Length: > 0 } t) { tab.EnsureTitleFromDat(t); break; }
        }
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

    private void OpenPostDialogInternal(ThreadTabViewModel tab, string initialMessage)
    {
        var vm = new PostFormViewModel(_postClient, tab.Board, tab.ThreadKey, tab.Title, DefaultPostAuthMode());
        if (!string.IsNullOrEmpty(initialMessage)) vm.Message = initialMessage;
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
