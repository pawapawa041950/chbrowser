using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Llm;
using ChBrowser.Services.Storage;
using CommunityToolkit.Mvvm.Input;

namespace ChBrowser.ViewModels;

/// <summary>AI 翻訳。スレッドペインの 🌐 ボタンのメニュー (「各レスごとに翻訳ボタンを表示する」「このスレを全て翻訳する」)、
/// 各レスの名前行の 🌐 ボタン、レス番号 ▸ メニューから使う。
///
/// <list type="bullet">
/// <item><description>1 回の推論で 1 レスだけ訳す (<see cref="AiTranslator.TranslateOneAsync"/>)。同時に投げるリクエスト数は
///   全スレ合計で <see cref="AppConfig.TranslateConcurrency"/> まで。訳している最中のレスは JS へ loading として知らせ、
///   そのレスの 🌐 ボタンを読み込み中の表示にする。</description></item>
/// <item><description>訳文はスレのログの隣 (<c>&lt;key&gt;.tr.json</c>) に保存し、同じレスを何度も LLM に送らない。
///   スレ全体の翻訳の ON / OFF と、翻訳で表示しているレスも保存する (= 開き直しても同じ見た目)。</description></item>
/// <item><description>スレ全体の翻訳では、もともと日本語のレス・訳す文字が無いレスは送らない。ON の間は新着も自動で訳す。</description></item>
/// <item><description>スレ一覧ペインの 🌐: そのタブのスレタイを訳して表示する (タブごと・その場限り)。訳は <see cref="TranslationService"/> が保存する。</description></item>
/// </list>
/// 接続・同時実行数・タイトルの訳の保存は <see cref="TranslationService"/> (今後の書き込み窓の翻訳も同じものを使う)。</summary>
public sealed partial class MainViewModel
{
    private TranslationStorage? _translationStorage;
    private TranslationStorage TranslationStore => _translationStorage ??= new(_paths);

    private TranslationService? _translation;
    /// <summary>AI 翻訳の共通窓口 (接続・同時実行の上限・タイトルの訳の保存)。書き込み窓などほかの画面にもこれを渡す。</summary>
    public TranslationService Translation => _translation ??= new(_llmClient, () => CurrentConfig, _paths);

    private bool IsTranslateConfigured => Translation.IsConfigured;

    private const string NotConfiguredMessage = TranslationService.NotConfiguredMessage;

    /// <summary>🌐 メニュー「各レスごとに翻訳ボタンを表示する」。掲示板ごとに設定へ保存し、同じ掲示板の開いているスレへ即時に反映する。</summary>
    [RelayCommand]
    private void ToggleShowPostTranslateButtons(ThreadTabViewModel? tab)
    {
        if (tab is null) return;
        var site = SiteIdOf(tab);
        var next = !IsPostTranslateButtonsOn(CurrentConfig, site);
        UpdateAndPersistConfig(c => c with { PostTranslateButtonsBySite = WithSite(c.PostTranslateButtonsBySite, site, next) });
        RefreshSiteTabs(site);
    }

    // ---- 掲示板ごとの表示の切り替え (各レスの 🌐 ボタン / 引用文を返信として扱う) ----

    /// <summary>タブの掲示板 (提供者 Id: "5ch" / "futaba" …)。</summary>
    private static string SiteIdOf(ThreadTabViewModel tab) => ChBrowser.Services.Bbs.BbsRegistry.ResolveOrDefault(tab.Board.Host).Id;

    /// <summary>各レスの 🌐 ボタンを出すか。掲示板ごとの設定が無ければ旧設定 (全掲示板共通) の値。</summary>
    internal static bool IsPostTranslateButtonsOn(AppConfig config, string site)
        => config.PostTranslateButtonsBySite is { } m && m.TryGetValue(site, out var v) ? v : config.ShowPostTranslateButtons;

    /// <summary>引用文を返信として扱うか。掲示板ごとの設定が無ければ既定 (引用で返信するのが普通のふたばだけ ON)。</summary>
    internal static bool IsQuoteRepliesOn(AppConfig config, string site)
        => config.QuoteRepliesBySite is { } m && m.TryGetValue(site, out var v) ? v : site == "futaba";

    private static Dictionary<string, bool> WithSite(Dictionary<string, bool>? map, string site, bool value)
        => new(map ?? new Dictionary<string, bool>(), StringComparer.Ordinal) { [site] = value };

    /// <summary>同じ掲示板の開いているスレへ、掲示板ごとの設定の変更を即時に反映する (setProviderConfig)。</summary>
    private void RefreshSiteTabs(string site)
    {
        foreach (var t in AllThreadTabs)
            if (SiteIdOf(t) == site) t.RefreshProviderConfig();
    }

    /// <summary>スレッドペインの「引用文を返信として扱う」ボタン。掲示板ごとに設定へ保存し、同じ掲示板の開いているスレへ即時に反映する
    /// (スレ表示はツリー・返信数・引用行のアンカーを描き直す)。</summary>
    [RelayCommand]
    private void ToggleQuoteReplies(ThreadTabViewModel? tab)
    {
        if (tab is null) return;
        var site = SiteIdOf(tab);
        var next = !IsQuoteRepliesOn(CurrentConfig, site);
        UpdateAndPersistConfig(c => c with { QuoteRepliesBySite = WithSite(c.QuoteRepliesBySite, site, next) });
        RefreshSiteTabs(site);
        StatusMessage = $"引用文を返信として扱う: {(next ? "ON" : "OFF")} ({ChBrowser.Services.Bbs.BbsRegistry.FindById(site)?.DisplayName ?? site})";
    }

    /// <summary>タブ生成時に保存済みの翻訳を読む (レスの描画時点で訳文で出せるよう、appendPosts に同梱される)。</summary>
    private void LoadTranslation(ThreadTabViewModel tab)
    {
        var t = TranslationStore.Load(tab.Board.Host, tab.Board.DirectoryName, tab.ThreadKey);
        foreach (var (n, body) in t.Posts) tab.Translations[n] = body;
        foreach (var n in t.Shown) if (tab.Translations.ContainsKey(n)) tab.TranslatedShown.Add(n);
        tab.IsTranslationOn = t.ThreadOn;
    }

    private void SaveTranslation(ThreadTabViewModel tab)
    {
        if (tab.LogDeleted) return;   // 翻訳中にログを削除したスレ: 消した .tr.json を作り直さない
        TranslationStore.Save(tab.Board.Host, tab.Board.DirectoryName, tab.ThreadKey,
            new ThreadTranslation(tab.IsTranslationOn, tab.TranslatedShown.OrderBy(n => n).ToList(), new Dictionary<long, string>(tab.Translations)));
    }

    private static void PushTranslation(ThreadTabViewModel tab, IReadOnlyDictionary<long, string>? translations = null,
                                        IReadOnlyList<long>? show = null, IReadOnlyList<long>? hide = null,
                                        IReadOnlyList<long>? loading = null, IReadOnlyList<long>? loaded = null)
        => tab.TranslationUpdate = new TranslationUpdateMessage(
            translations ?? new Dictionary<long, string>(), show ?? Array.Empty<long>(), hide ?? Array.Empty<long>(),
            loading ?? Array.Empty<long>(), loaded ?? Array.Empty<long>());

    private static void SetTranslateStatus(ThreadTabViewModel tab, string message) => tab.StatusMessage = message;

    /// <summary>🌐 メニュー「このスレを全て翻訳する」: スレ全体の翻訳の ON / OFF。ON にすると保存済みの訳文をすぐ出し、残りを翻訳する。
    /// OFF にすると実行中の翻訳を止め、全レスを原文に戻す (訳文は残す = 次に ON にしたとき送らない)。</summary>
    public async Task ToggleThreadTranslationAsync(ThreadTabViewModel tab)
    {
        if (tab.IsTranslationOn)
        {
            tab.TranslateCts?.Cancel();
            tab.IsTranslationOn = false;
            var hide = tab.TranslatedShown.ToList();
            tab.TranslatedShown.Clear();
            PushTranslation(tab, hide: hide);
            SaveTranslation(tab);
            SetTranslateStatus(tab, "原文の表示に戻しました");
            return;
        }
        if (!IsTranslateConfigured)
        {
            SetTranslateStatus(tab, NotConfiguredMessage);
            StatusMessage = NotConfiguredMessage;
            return;
        }
        tab.IsTranslationOn = true;
        var visible = new HashSet<long>(tab.Posts.Select(p => p.Number));
        var show = tab.Translations.Keys.Where(n => visible.Contains(n) && tab.TranslatedShown.Add(n)).ToList();
        if (show.Count > 0) PushTranslation(tab, show: show);
        SaveTranslation(tab);
        await TranslateMissingAsync(tab).ConfigureAwait(true);
    }

    /// <summary>1 レスを翻訳する (同時実行の上限の中で、訳している間は loading を表示)。成功なら訳文を保存して翻訳表示にする。
    /// 戻り値: 訳せたか。LLM が失敗 (接続エラー等) したら <see cref="AiTranslateException"/> をそのまま投げる。</summary>
    private Task<bool> TranslateOneAndShowAsync(ThreadTabViewModel tab, long number, string plain, CancellationToken ct)
        => Translation.WithSlotAsync(async () =>
        {
            tab.TranslatingPosts.Add(number);
            PushTranslation(tab, loading: new[] { number });
            try
            {
                var body = await Translation.TranslatePostAsync(plain, ct).ConfigureAwait(true);
                if (body is null) return false;
                tab.Translations[number] = body;
                tab.TranslatedShown.Add(number);
                PushTranslation(tab, new Dictionary<long, string> { [number] = body }, show: new[] { number });
                return true;
            }
            finally
            {
                tab.TranslatingPosts.Remove(number);
                PushTranslation(tab, loaded: new[] { number });
            }
        }, ct);

    /// <summary>スレ全体の翻訳が ON のとき、まだ訳していないレス (日本語・訳す文字が無いレスを除く) を 1 件ずつ翻訳する
    /// (上限までは並行)。翻訳中に届いたレスも最後に拾う。接続エラーならそこで止めてステータスに出す。</summary>
    private async Task TranslateMissingAsync(ThreadTabViewModel tab)
    {
        if (!tab.IsTranslationOn || tab.IsTranslating || !IsTranslateConfigured) return;
        var cts = new CancellationTokenSource();
        tab.TranslateCts = cts;
        tab.IsTranslating = true;
        var failed = new HashSet<long>();          // 今回の実行で訳せなかったレス (同じ実行で送り直さない)
        var done = 0;
        var saveCounter = 0;
        string? error = null;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var targets = new List<TranslateItem>();
                foreach (var p in tab.Posts)
                {
                    if (tab.Translations.ContainsKey(p.Number) || failed.Contains(p.Number) || tab.TranslatingPosts.Contains(p.Number)) continue;
                    var plain = AiTranslator.ToPlain(p.Body);
                    if (AiTranslator.NeedsTranslation(plain)) targets.Add(new TranslateItem(p.Number, plain));
                }
                if (targets.Count == 0) break;

                var total = done + targets.Count;
                SetTranslateStatus(tab, $"翻訳中… {done}/{total} レス");
                var tasks = targets.Select(async it =>
                {
                    try
                    {
                        if (cts.IsCancellationRequested) return;
                        var ok = await TranslateOneAndShowAsync(tab, it.Number, it.Text, cts.Token).ConfigureAwait(true);
                        if (!ok) failed.Add(it.Number);
                        done++;
                        SetTranslateStatus(tab, $"翻訳中… {done}/{total} レス");
                        if (++saveCounter % 10 == 0) SaveTranslation(tab);
                    }
                    catch (AiTranslateException ex)
                    {
                        error ??= ex.Message;
                        cts.Cancel();   // 接続エラー等は残りも失敗するので止める
                    }
                    catch (OperationCanceledException) { }
                }).ToList();
                await Task.WhenAll(tasks).ConfigureAwait(true);
            }
        }
        finally
        {
            tab.IsTranslating = false;
            if (ReferenceEquals(tab.TranslateCts, cts)) tab.TranslateCts = null;
            SaveTranslation(tab);
        }

        if (error is not null)
        {
            SetTranslateStatus(tab, $"翻訳に失敗しました: {error}");
            StatusMessage = $"翻訳に失敗しました: {error}";
        }
        else if (!tab.IsTranslationOn) { /* 途中で OFF にした */ }
        else if (done == 0) SetTranslateStatus(tab, "翻訳が必要なレスはありません (日本語のレスは翻訳しません)");
        else SetTranslateStatus(tab, failed.Count > 0 ? $"翻訳しました ({done - failed.Count} レス、{failed.Count} レスは翻訳できませんでした)" : $"翻訳しました ({done} レス)");
    }

    /// <summary>各レスの 🌐 ボタン: 翻訳で表示中なら原文に戻し、そうでなければ翻訳する (保存済みの訳文があればそれを出す)。</summary>
    public Task TogglePostTranslationAsync(ThreadTabViewModel tab, long number)
    {
        if (tab.TranslatedShown.Contains(number))
        {
            ShowOriginal(tab, number);
            return Task.CompletedTask;
        }
        return TranslatePostAsync(tab, number);
    }

    /// <summary>1 レスを翻訳表示にする (各レスの 🌐 ボタン / レス番号 ▸ メニュー「このレスを翻訳」)。保存済みの訳文があればそれを出し、
    /// 無ければ 1 件だけ翻訳する (明示の操作なので日本語のレスでも送る)。</summary>
    public async Task TranslatePostAsync(ThreadTabViewModel tab, long number)
    {
        if (tab.Translations.ContainsKey(number))
        {
            if (tab.TranslatedShown.Add(number)) PushTranslation(tab, show: new[] { number });
            SaveTranslation(tab);
            return;
        }
        if (tab.TranslatingPosts.Contains(number)) return;   // 既に訳している最中
        if (!IsTranslateConfigured)
        {
            SetTranslateStatus(tab, NotConfiguredMessage);
            StatusMessage = NotConfiguredMessage;
            return;
        }
        var post = tab.Posts.FirstOrDefault(p => p.Number == number);
        if (post is null) return;
        var plain = AiTranslator.ToPlain(post.Body);
        if (AiTranslator.Mask(plain).Masked.Count(char.IsLetter) == 0)
        {
            SetTranslateStatus(tab, "このレスには翻訳する文字がありません");
            return;
        }

        SetTranslateStatus(tab, "レスを翻訳中…");
        bool ok;
        try
        {
            ok = await TranslateOneAndShowAsync(tab, number, plain, CancellationToken.None).ConfigureAwait(true);
        }
        catch (AiTranslateException ex)
        {
            SetTranslateStatus(tab, $"翻訳に失敗しました: {ex.Message}");
            return;
        }
        if (!ok)
        {
            SetTranslateStatus(tab, "翻訳できませんでした (AI の応答が空でした)");
            return;
        }
        SaveTranslation(tab);
        SetTranslateStatus(tab, "レスを翻訳しました");
    }

    /// <summary>そのレスを原文の表示に戻す (訳文は残す)。各レスの 🌐 ボタン / ▸ メニュー「原文に戻す」。</summary>
    public void ShowOriginal(ThreadTabViewModel tab, long number)
    {
        if (!tab.TranslatedShown.Remove(number)) return;
        PushTranslation(tab, hide: new[] { number });
        SaveTranslation(tab);
    }

    // ---- スレ一覧ペイン: スレタイの翻訳 (タブごと・その場限り) ----

    /// <summary>スレ一覧ペインの 🌐: このタブのスレタイの翻訳の ON / OFF。ON にすると保存済みの訳をすぐ出し、残りを訳す。
    /// OFF にすると実行中の翻訳を止めて原文に戻す (訳は残す = 次に ON にしたとき送らない)。</summary>
    public async Task ToggleThreadListTranslationAsync(ThreadListTabViewModel tab)
    {
        if (tab.IsTitleTranslationOn)
        {
            tab.TitleTranslateCts?.Cancel();
            tab.IsTitleTranslationOn = false;
            tab.ResendItems();
            tab.StatusMessage = "スレタイを原文の表示に戻しました";
            return;
        }
        if (!IsTranslateConfigured)
        {
            tab.StatusMessage = NotConfiguredMessage;
            StatusMessage     = NotConfiguredMessage;
            return;
        }
        tab.IsTitleTranslationOn = true;
        tab.ResendItems();   // 保存済みの訳をすぐ出す
        await TranslateTitlesAsync(tab).ConfigureAwait(true);
    }

    /// <summary>タブのスレタイのうち、まだ訳が無い日本語以外のものを訳す。訳せた分から表示を更新する。
    /// 実行中に一覧が入れ替わったら (更新 / 続きの読み込み)、終わった後に新しい行も拾う。</summary>
    private async Task TranslateTitlesAsync(ThreadListTabViewModel tab)
    {
        if (!tab.IsTitleTranslationOn || tab.IsTranslatingTitles || !IsTranslateConfigured) return;
        var cts = new CancellationTokenSource();
        tab.TitleTranslateCts   = cts;
        tab.IsTranslatingTitles = true;
        var total = 0;
        string? error = null;
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var titles = tab.Items.Where(i => i.Kind == ThreadListItemKind.Thread).Select(i => i.Info.Title)
                                      .Where(Translation.TitleNeedsTranslation).Distinct().ToList();
                if (titles.Count == 0) break;
                tab.StatusMessage = $"スレタイを翻訳中… 0/{titles.Count}";
                var got = await Translation.TranslateTitlesAsync(titles, done =>
                {
                    if (cts.IsCancellationRequested) return;
                    tab.StatusMessage = $"スレタイを翻訳中… {done}/{titles.Count}";
                    tab.ResendItems();
                }, cts.Token).ConfigureAwait(true);
                total += got;
                if (got == 0) break;   // 1 件も訳せなかった (応答の形が合わない等): 同じものを送り続けない
            }
        }
        catch (OperationCanceledException) { }
        catch (AiTranslateException ex) { error = ex.Message; }
        finally
        {
            tab.IsTranslatingTitles = false;
            if (ReferenceEquals(tab.TitleTranslateCts, cts)) tab.TitleTranslateCts = null;
        }

        if (!tab.IsTitleTranslationOn) return;   // 途中で OFF にした
        tab.ResendItems();
        if (error is not null)
        {
            tab.StatusMessage = $"スレタイの翻訳に失敗しました: {error}";
            StatusMessage     = tab.StatusMessage;
        }
        else
        {
            tab.StatusMessage = total > 0 ? $"スレタイを翻訳しました ({total} 件)" : "翻訳が必要なスレタイはありません (日本語のタイトルは翻訳しません)";
        }
    }
}
