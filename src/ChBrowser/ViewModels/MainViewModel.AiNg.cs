using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Ng;
using ChBrowser.Services.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChBrowser.ViewModels;

/// <summary>NG 判定 AI (第1弾)。軽量 LLM で各レスの「攻撃度」を 1..5 で 1 件ずつ判定し、
/// しきい値 (<see cref="AiNgThreshold"/>) 以上のレスをスレ表示上からリアルタイムに非表示にする。
///
/// <para>判定結果はスレ単位で <c>.aing.json</c> に永続化し、再オープン時は既判定分を読み戻して
/// 未判定の新規レスだけを LLM にかけ直す。非表示は CSS クラス (<c>.ai-ng-hidden</c>) による
/// 可逆方式で、しきい値変更で集合が変われば即座に再表示される (= 既存の手動 NG の物理削除とは別系統)。</para></summary>
public sealed partial class MainViewModel
{
    /// <summary>現在の NG しきい値 (= スコアがこの値以上のレスを非表示)。6 以上で OFF。
    /// <see cref="AppConfig.NgAiThreshold"/> のミラー。メニューの ✓ 表示にバインドされる。</summary>
    [ObservableProperty]
    private int _aiNgThreshold = 4;

    /// <summary>NG 判定の有効な同時実行数 (= 並行で投げる LLM リクエスト本数)。設定値
    /// (<see cref="AppConfig.NgAiConcurrency"/>) を 1..16 にクランプして使う。
    /// サーバ側を <c>--parallel</c> でこの値以上にして起動すると並列デコードで高速化する。</summary>
    private int AiNgConcurrency => Math.Clamp(CurrentConfig.NgAiConcurrency, 1, 16);

    /// <summary>ステータスバー用の AI-NG 判定進捗。判定中は「AING判定 N件」、完了で「AING判定完了」、
    /// OFF / 未設定 / 対象なしでは空文字 (= 非表示)。</summary>
    [ObservableProperty]
    private string _aiNgStatus = "";

    /// <summary>逐次判定ループのキャンセル元。タブ切替 / しきい値変更 / 新規レス到着で張り替える。</summary>
    private CancellationTokenSource? _aiNgCts;

    /// <summary>判定ループを「厳密に逐次化」するためのチェーン。新ループは旧ループの終了を待ってから走る
    /// (= タブ切替 / 連続 append で StartAiNgFor が多重発火しても LLM 呼び出しが並走しない)。</summary>
    private Task _aiNgLoopTask = Task.CompletedTask;

    /// <summary>スコアの per-thread 永続化 (lazy)。</summary>
    private AiNgStorage? _aiNgStorageCache;
    private AiNgStorage AiNgStore => _aiNgStorageCache ??= new AiNgStorage(_paths);

    /// <summary>NG 判定 AI が接続設定済みか (URL + モデル名が両方ある)。</summary>
    private bool IsAiNgConfigured =>
        !string.IsNullOrWhiteSpace(CurrentConfig.NgAiApiUrl) &&
        !string.IsNullOrWhiteSpace(CurrentConfig.NgAiModel);

    /// <summary>ツールバーのしきい値メニューから呼ばれる。"1".."6" を受け取りしきい値を更新し、
    /// 永続化 + 選択中タブの再フィルタ + (有効なら) 未判定レスの判定再開を行う。</summary>
    [RelayCommand]
    private void SetAiNgThreshold(string? value)
    {
        if (!int.TryParse(value, out var v)) return;
        v = Math.Clamp(v, 1, 6);
        if (v == AiNgThreshold) { /* それでも再フィルタはしておく */ }
        AiNgThreshold = v;
        UpdateAndPersistConfig(c => c with { NgAiThreshold = v });
        if (SelectedThreadTab is { } tab) StartAiNgFor(tab);
    }

    /// <summary>スレ表示ペインの AI NG メニュー「レスのスコア判定をクリアする」。選択中タブの
    /// LLM 判定情報 (= AiScores) を全消去し、永続ファイル (.aing.json) も削除する。
    /// その後 <see cref="StartAiNgFor"/> で全レス再表示 + (有効なら) 全件を最初から判定し直す。</summary>
    [RelayCommand]
    private void ClearAiNgScores()
    {
        var tab = SelectedThreadTab;
        if (tab is null) return;

        tab.AiScores.Clear();
        tab.AiScoresLoaded = true; // ディスクから読み戻さない (= 今クリアした状態を維持してから再判定)
        AiNgStore.Delete(tab.Board.Host, tab.Board.DirectoryName, tab.ThreadKey);
        AiNgStatus = "";
        ChBrowser.Services.Logging.LogService.Instance.Write($"[AiNg] {tab.Header}: スコア判定を全クリア");

        // 全件再表示 (スコア空 → 非表示なし) + 有効なら最初から判定し直す。
        StartAiNgFor(tab);
    }

    /// <summary>指定タブの AI-NG 処理を (再) 開始する。
    /// 1) 進行中ループをキャンセル、2) 永続スコアを読み戻し (初回のみ)、3) 現スコア+しきい値で再フィルタ、
    /// 4) 有効 (設定済み + しきい値 &lt;= 5) なら未判定レスの逐次判定ループを起動する。</summary>
    private void StartAiNgFor(ThreadTabViewModel? tab)
    {
        // 進行中ループに停止要求を出す。CTS は Dispose しない (= WaitHandle を確保していないので Dispose は
        // 実質 no-op。Dispose 済み CTS に対して後続の Cancel() を呼ぶと ObjectDisposedException になるため、
        // GC に委ねて Cancel() を常に安全にする)。
        try { _aiNgCts?.Cancel(); }
        catch (ObjectDisposedException) { /* 既に完了済みループの CTS。無視 */ }
        _aiNgCts = null;
        if (tab is null) { AiNgStatus = ""; return; }

        // 永続スコアの読み戻し (タブの生存期間で 1 度だけ)。
        if (!tab.AiScoresLoaded)
        {
            var saved = AiNgStore.Load(tab.Board.Host, tab.Board.DirectoryName, tab.ThreadKey);
            foreach (var kv in saved) tab.AiScores[kv.Key] = kv.Value;
            tab.AiScoresLoaded = true;
        }

        // 既知スコア + 現しきい値で即時に再フィルタ (= 判定を待たずに既判定分はすぐ反映)。
        tab.RecomputeAiHidden(AiNgThreshold);

        // OFF or 未設定なら判定はしない (再表示だけ済んでいる)。
        if (AiNgThreshold > 5 || !IsAiNgConfigured) { AiNgStatus = ""; return; }

        // 直前ループの完了を待ってから走る形でチェーンする (= 厳密に逐次・並走させない)。
        var cts  = new CancellationTokenSource();
        _aiNgCts = cts;
        var prev = _aiNgLoopTask;
        _aiNgLoopTask = RunAiNgLoopAsync(tab, cts, prev);
    }

    /// <summary>未判定レスを 1 件ずつ LLM で採点し、結果を即時に反映 (再フィルタ) するループ。
    /// 開始時に <paramref name="previous"/> (= 直前のループ) の終了を待つことで、複数ループが並走しない。
    /// 判定済 / 既知スコアありのレスはスキップする。LLM 失敗 (-1) のレスはスキップし、次回 (再オープン /
    /// 新規レス到着での再開時) に再試行される。
    /// 永続化 (<c>.aing.json</c> 保存) は「すべて判定し終わってから」1 回だけ行う (= 途中保存しない)。
    /// 進捗はステータスバー (<see cref="AiNgStatus"/>) に「AING判定 N件」/ 完了で「AING判定完了」と出す。</summary>
    private async Task RunAiNgLoopAsync(ThreadTabViewModel tab, CancellationTokenSource cts, Task previous)
    {
        var ct = cts.Token;
        try
        {
            // 直前ループが (キャンセル後でも) 完全に抜けるのを待つ = LLM 呼び出しを並走させない。
            try { await previous.ConfigureAwait(true); } catch { /* 直前ループの結果は問わない */ }
            ct.ThrowIfCancellationRequested();

            var settings = LlmSettings.NgFromConfig(CurrentConfig);
            // 直前ループ完了後の最新 Posts を見る (= 待っている間に増えた分も拾える)。
            var snapshot = new List<Post>(tab.Posts);
            var unjudged = new List<Post>();
            foreach (var p in snapshot)
                if (!tab.AiScores.ContainsKey(p.Number)) unjudged.Add(p);

            // まだレス未ロード (= スレ表示直後) なら何も出さない。新規レス到着で再度呼ばれて拾われる。
            if (snapshot.Count == 0) { AiNgStatus = ""; return; }
            // 全件判定済み: 既判定があれば「完了」(NG 該当数を併記)、無ければ空 (= 表示直後の誤「完了」を出さない)。
            if (unjudged.Count == 0)
            {
                AiNgStatus = tab.AiScores.Count > 0
                    ? $"AING判定完了 (NG Score {AiNgThreshold}以上 {tab.AiHiddenPostNumbers?.Count ?? 0}件)"
                    : "";
                return;
            }

            // 同時実行数 (AiNgConcurrency) を上限に並行して判定する (= サーバの並列デコードで高速化)。
            // 状態更新 (AiScores / 進捗表示) は UI スレッドの継続上で行われる (ConfigureAwait(true)) ため
            // 直列化されるが、念のため lock で保護する。完了時にまとめて 1 回だけ保存する (途中保存しない)。
            using var gate = new SemaphoreSlim(AiNgConcurrency);
            var judgedCount = 0;
            var maxJudgedNo = 0;
            var sync = new object();

            var tasks = unjudged.Select(async post =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(true);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var score = await AiNgJudge.JudgeAsync(_llmClient, settings, post, CurrentConfig.NgAiDisableReasoning, ct).ConfigureAwait(true);
                    ct.ThrowIfCancellationRequested();
                    if (score < 1) return; // LLM 失敗 (-1): 保存せず次回再試行

                    lock (sync)
                    {
                        tab.AiScores[post.Number] = score;
                        judgedCount++;
                        if (post.Number > maxJudgedNo) maxJudgedNo = post.Number;
                    }
                    tab.RecomputeAiHidden(AiNgThreshold);        // リアルタイム反映
                    // 進捗表示: 判定済みの最大レス No (並行なので前後はするが概ね増加) + NG 該当数。
                    var ngCount = tab.AiHiddenPostNumbers?.Count ?? 0;
                    AiNgStatus = $"レスNo {maxJudgedNo}判定済(NG Score {AiNgThreshold}以上 {ngCount}件)";
                }
                finally
                {
                    gate.Release();
                }
            });
            await Task.WhenAll(tasks).ConfigureAwait(true);

            // ここに到達 = キャンセルされずに最後まで走り切った = 完了。まとめて 1 回だけ保存する。
            if (judgedCount > 0)
            {
                AiNgStore.Save(tab.Board.Host, tab.Board.DirectoryName, tab.ThreadKey, tab.AiScores);
                ChBrowser.Services.Logging.LogService.Instance.Write(
                    $"[AiNg] {tab.Header}: 判定完了 (今回 {judgedCount} 件 / スコア保持 {tab.AiScores.Count} 件 / しきい値 {AiNgThreshold})");
            }
            AiNgStatus = $"AING判定完了 (NG Score {AiNgThreshold}以上 {tab.AiHiddenPostNumbers?.Count ?? 0}件)";
        }
        catch (OperationCanceledException) { /* タブ切替などによる正常キャンセル (途中保存しない) */ }
        catch (Exception ex)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write($"[AiNg] 判定ループ例外: {ex.Message}");
        }
        // CTS は Dispose しない: WaitHandle 未確保なので不要、かつ _aiNgCts がまだ指している間に Dispose すると
        // 後続 StartAiNgFor の Cancel() が ObjectDisposedException になるため (GC に委ねる)。
    }

    /// <summary>選択中タブに新規レスが追加されたタイミングで判定を再開する (= 新規レスだけが未判定なので拾われる)。
    /// <see cref="AppendPostsWithNg"/> から呼ばれる。</summary>
    private void OnPostsAppendedForAiNg(ThreadTabViewModel tab)
    {
        if (!ReferenceEquals(tab, SelectedThreadTab)) return;
        StartAiNgFor(tab);
    }

    /// <summary>選択中タブで現在 AI-NG により非表示になっているレス (= スコアが現しきい値以上) を
    /// レス番号昇順で返す。ステータスバーの「AING判定…」クリックで一覧表示するために使う。</summary>
    public IReadOnlyList<(int Number, int Score)> GetSelectedTabAiNgHidden()
    {
        var tab = SelectedThreadTab;
        if (tab is null || AiNgThreshold > 5) return Array.Empty<(int, int)>();
        var list = new List<(int Number, int Score)>();
        foreach (var kv in tab.AiScores)
            if (kv.Value >= AiNgThreshold) list.Add((kv.Key, kv.Value));
        list.Sort((a, b) => a.Number.CompareTo(b.Number));
        return list;
    }
}
