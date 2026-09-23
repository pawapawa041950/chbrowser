using System;
using System.Collections.Generic;
using System.Linq;
using ChBrowser.Controls;
using ChBrowser.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChBrowser.ViewModels;

public enum ThreadViewMode
{
    Flat,
    Tree,
    DedupTree,
    // 重複なしツリーの新実装。JsonStringEnumConverter(camelCase) で "dedupTree2" に変換され、
    // JS 側 VIEW_MODE_STRATEGIES.dedupTree2 と対応する。UI (サイクル / 設定の「ツリー(重複なし)」) は
    // 旧 DedupTree から dedupTree2 へ置き換え済み。旧 DedupTree のソースは当面残すが UI からは呼ばず、後日削除予定。
    DedupTree2,
}

/// <summary>JS の <c>appendPosts</c> に渡すペイロード (Phase 20)。
/// <see cref="IsIncremental"/> = true なら、dedup-tree モードでこの batch 以降を「末尾 incremental block」として
/// 既存ツリーとは別レンダリングにする (= 既読下に新着を表示するため)。</summary>
public sealed record AppendBatchData(IReadOnlyList<Post> Posts, bool IsIncremental);

/// <summary>JS の <c>updateOwnPosts</c> に渡すペイロード — 自分マークのトグル結果を 1 件ずつ通知する。
/// JS は changes 配列をそのまま受け取り、各 (number, isOwn) で DOM の「自分」バッジを toggle する。</summary>
public sealed record OwnPostsUpdateData(IReadOnlyList<OwnPostChange> Changes);

/// <summary>1 件分の自分マークトグル結果。</summary>
public sealed record OwnPostChange(long Number, bool IsOwn);

/// <summary>JS の <c>updateVotes</c> に渡すペイロード — レスの評価の確定 (成功) / 巻き戻し (失敗) を通知する。
/// class (参照同一性) にして、同じ内容を続けて送っても PropertyChanged が出るようにする。</summary>
public sealed class VotesUpdateData
{
    public IReadOnlyList<VoteChange> Changes { get; }
    public VotesUpdateData(IReadOnlyList<VoteChange> changes) => Changes = changes;
}

/// <summary>JS の <c>updateAuthorProfiles</c> に渡すペイロード (投稿者名 → 情報)。届いた分だけアイコンを差し込む。</summary>
public sealed class AuthorProfilesMessage
{
    public IReadOnlyDictionary<string, ChBrowser.Services.Bbs.AuthorProfile> Profiles { get; }
    public AuthorProfilesMessage(IReadOnlyDictionary<string, ChBrowser.Services.Bbs.AuthorProfile> profiles) => Profiles = profiles;
}

/// <summary>JS の <c>updateTranslations</c> に渡すペイロード。<see cref="Translations"/> は届いた訳文 (レス番号 → 表示用本文)、
/// <see cref="Show"/> は翻訳で表示するレス、<see cref="Hide"/> は原文に戻すレス、<see cref="Loading"/> / <see cref="Loaded"/> は
/// 翻訳を始めた / 終えたレス (🌐 ボタンの読み込み中表示)。class なので同じ内容でも毎回送られる。</summary>
public sealed class TranslationUpdateMessage
{
    public IReadOnlyDictionary<long, string> Translations { get; }
    public IReadOnlyList<long> Show { get; }
    public IReadOnlyList<long> Hide { get; }
    public IReadOnlyList<long> Loading { get; }
    public IReadOnlyList<long> Loaded { get; }
    public TranslationUpdateMessage(IReadOnlyDictionary<long, string> translations, IReadOnlyList<long> show, IReadOnlyList<long> hide,
                                    IReadOnlyList<long> loading, IReadOnlyList<long> loaded)
    {
        Translations = translations;
        Show         = show;
        Hide         = hide;
        Loading      = loading;
        Loaded       = loaded;
    }
}

/// <summary>1 件分の評価の状態。<see cref="Dir"/> は 1 / -1 / 0、<see cref="Ok"/> = false は送信失敗 (表示を元に戻す)。</summary>
public sealed record VoteChange(long Number, int Dir, bool Ok = true);

/// <summary>「指定レス番号までスクロール」要求のラッパー。
/// <see cref="ThreadTabViewModel.PendingScrollToPost"/> に新インスタンスを setter することで、
/// 同 number を立て続けに 2 回投げても (= 同じスレ URL を 2 回連続でクリック等) PropertyChanged が発火し、
/// AttachedProperty 側で JS への push が必ず走るようにする。
/// <para>注意: <b>record にしないこと</b>。record は構造的等価性なので、同じ Number で new した 2 個目は
/// SetProperty 内の比較で「変化なし」と判定され PropertyChanged が出ない。class にして参照同一性で毎回発火させる。</para></summary>
public sealed class ScrollToPostRequest
{
    public long Number { get; }
    public ScrollToPostRequest(long number) => Number = number;
}

/// <summary>
/// 1 スレッド = 1 タブ。WebView2 へは Posts (Post 列) を Bind し、HTML 構築は JS 側で行う。
/// 表示モード (Flat / Tree / DedupTree) はすべて JS 側で実装済 (thread.js)、
/// <see cref="CycleViewModeCommand"/> でトグル → setViewMode メッセージで JS が再描画する。
/// </summary>
public sealed partial class ThreadTabViewModel : ObservableObject, IThreadDisplayBinding, IPaneTab
{
    public Board  Board     { get; }
    public string ThreadKey { get; }

    /// <summary>このスレの正規 URL (提供者の形式。5ch なら <c>test/read.cgi</c>)。
    /// アドレスバー表示やコンテキストメニューの「URLコピー」で使う。</summary>
    public string Url => ChBrowser.Services.Bbs.BbsRegistry.ResolveOrDefault(Board.Host)
        .ThreadUrl(Board.Host, Board.DirectoryName, ThreadKey);

    /// <summary>このスレの掲示板提供者に依存するスレ表示設定 (レス番号の表示可否 / 番号ジャンプ桁数 / アンカー規則)。
    /// appendPosts / resyncThreadState の各メッセージに <c>provider</c> として同梱し、JS はレス描画の前に適用する
    /// (= 送信順に依存しない。<c>doc/reddit-design.md</c> §3 B11)。<see cref="RefreshProviderConfig"/> で作り直す。</summary>
    public object ProviderConfig { get; private set; } = new { };

    /// <summary>設定変更 (アンカー規則の編集等) を開いているスレ表示へ即時に届けるための <c>setProviderConfig</c> メッセージ JSON。</summary>
    [ObservableProperty]
    private string? _providerConfigJson;

    /// <summary><see cref="ProviderConfig"/> / <see cref="ProviderConfigJson"/> を現在の提供者・アンカー規則から作り直す。
    /// タブ生成時と設定適用時 (<c>MainViewModel.ApplyConfig</c>) に呼ばれる。</summary>
    public void RefreshProviderConfig()
    {
        var provider = ChBrowser.Services.Bbs.BbsRegistry.ResolveOrDefault(Board.Host);
        ProviderConfig = new
        {
            providerId       = provider.Id,
            showPostNumbers  = provider.ShowsPostNumbers,
            watchoi          = provider.UsesWatchoi,
            postNumberDigits = provider.PostNumberDigits,
            // 投稿者のアイコン (名前の前) とプロフィールのカード
            authorProfiles   = provider.SupportsAuthorProfiles,
            // レスの評価ボタン (null = 評価できない。評価値があれば表示だけ)
            voting           = provider.Voting is { } v
                ? new { mode = v.Mode == ChBrowser.Services.Bbs.VoteMode.UpDown ? "updown" : "up", upLabel = v.UpLabel, downLabel = v.DownLabel, canUndo = v.CanUndo }
                : null,
            anchorRules     = ChBrowser.Services.Bbs.AnchorRuleRegistry.For(provider).Rules
                                   .Select(r => new { name = r.Name, pattern = r.Pattern, kind = r.Kind, ranges = r.Ranges, enabled = r.Enabled })
                                   .ToArray(),
        };
        ProviderConfigJson = System.Text.Json.JsonSerializer.Serialize(new { type = "setProviderConfig", config = ProviderConfig });
    }

    public IRelayCommand CloseCommand           { get; }
    public IRelayCommand CycleViewModeCommand  { get; }
    public IRelayCommand DeleteCommand          { get; }
    public IRelayCommand RefreshCommand         { get; }
    public IRelayCommand AddToFavoritesCommand  { get; }
    public IRelayCommand WriteCommand           { get; }
    /// <summary>ツールバー「AI」ボタン用。このスレの内容を文脈に LLM チャットウィンドウを開く。</summary>
    public IRelayCommand AiChatCommand          { get; }
    /// <summary>🌐 メニュー「このスレを全て翻訳する」: スレ全体の翻訳の ON / OFF。</summary>
    public IRelayCommand ToggleTranslationCommand { get; }

    // ---- AI 翻訳 ----
    /// <summary>このスレの訳文 (レス番号 → 表示用本文)。<c>.tr.json</c> から復元し、翻訳のたびに増える。appendPosts / resync に同梱する。</summary>
    public Dictionary<long, string> Translations { get; } = new();
    /// <summary>翻訳で表示しているレス番号 (原文に戻したレスは訳文を残したまま外す)。</summary>
    public HashSet<long> TranslatedShown { get; } = new();
    /// <summary>スレ全体の翻訳が ON (ツールバー 🌐 の押下状態。新着も自動で翻訳する)。</summary>
    [ObservableProperty] private bool _isTranslationOn;
    /// <summary>スレ全体の翻訳を実行中 (ツールバー 🌐 の表示用)。</summary>
    [ObservableProperty] private bool _isTranslating;
    /// <summary>いま LLM で訳しているレス番号 (🌐 ボタンの読み込み中表示。resync に同梱)。</summary>
    public HashSet<long> TranslatingPosts { get; } = new();
    /// <summary>訳文の送信チャネル (TranslationUpdate 添付プロパティが観測して updateTranslations を送る)。</summary>
    [ObservableProperty] private TranslationUpdateMessage? _translationUpdate;
    /// <summary>実行中の翻訳の取り消し (OFF にしたとき / タブを閉じたとき)。</summary>
    internal System.Threading.CancellationTokenSource? TranslateCts { get; set; }

    [ObservableProperty]
    private string _header;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private long _datSize;

    /// <summary>NG で透明化された (= JS に送られなかった) レス数の累積 (Phase 13)。
    /// ステータスバーに「あぼーん N」として表示。</summary>
    [ObservableProperty]
    private int _hiddenCount;

    /// <summary>NG hidden 数のルール別内訳 (Guid = NgRule.Id、int = このタブで累積した直接マッチ件数)。
    /// ステータスバー「あぼーん N」のクリックで内訳メニューを出す時に使う。</summary>
    public Dictionary<Guid, int> HiddenByRule { get; } = new();

    /// <summary>連鎖あぼーんで hidden になったレス数の累積 (= どのルールにも直接マッチせず、
    /// 別 hidden レスへのアンカー経由でのみ非表示になっているもの)。</summary>
    [ObservableProperty]
    private int _hiddenByChain;

    /// <summary>このタブで NG (直接 + 連鎖) により hidden になったレス番号の累積集合。
    /// 差分取得で新着バッチを判定するとき、連鎖あぼーんの種として <c>NgService.ComputeHiddenWithBreakdown</c> に渡す
    /// (= 以前あぼーんされたレスへアンカーしている新着も連鎖で隠す)。<see cref="HiddenCount"/> と同じ寿命で累積する。</summary>
    public HashSet<long> HiddenPostNumbers { get; } = new();

    /// <summary>1 batch 分の <see cref="ChBrowser.Services.Ng.NgHiddenBreakdown"/> を内訳カウンタと
    /// <see cref="HiddenPostNumbers"/> に加算する。MainViewModel.AppendPostsWithNg / ReplaceVisiblePostsAfterNgAdd から呼ばれる。</summary>
    public void AddHiddenBreakdown(ChBrowser.Services.Ng.NgHiddenBreakdown breakdown)
    {
        foreach (var (ruleId, count) in breakdown.ByRuleDirect)
            HiddenByRule[ruleId] = HiddenByRule.TryGetValue(ruleId, out var c) ? c + count : count;
        HiddenByChain += breakdown.ChainOnly;
        HiddenPostNumbers.UnionWith(breakdown.HiddenNumbers);
    }

    /// <summary>JS に「これらのレス番号を即時 DOM から消して」と push するためのトリガ。
    /// 値が変わると WebView2Helper.HidePostsPush (= setHiddenPosts) で送られる。
    /// 同じ集合を 2 回送る (= ユーザが立て続けに NG 追加する) ケースに備え、IReadOnlyList を新インスタンス
    /// で setter する (= 参照同一だと PropertyChanged が飛ばない可能性がある)。 </summary>
    [ObservableProperty]
    private IReadOnlyList<long>? _pendingHidePostNumbers;

    /// <summary>NG 判定 AI が「攻撃的 (= スコア >= しきい値)」と判定したレス番号の「全集合」。
    /// 値が変わると WebView2Helper.AiHiddenPush (= setAiHidden) で JS に送られ、対象レスに
    /// <c>.ai-ng-hidden</c> クラスが付け外しされる (= 集合に無い番号は再表示される / 可逆)。
    /// <see cref="PendingHidePostNumbers"/> (= 物理削除) とは別系統。 </summary>
    [ObservableProperty]
    private IReadOnlyList<long>? _aiHiddenPostNumbers;

    /// <summary>このスレで AI が出した NG スコア (レス番号 → 1..5)。永続化分の読み戻し + 逐次判定で更新される。
    /// しきい値と突き合わせて <see cref="AiHiddenPostNumbers"/> を再計算するための元データ。</summary>
    public Dictionary<long, int> AiScores { get; } = new();

    /// <summary>永続化済みスコア (<c>.aing.json</c>) を一度読み込んだか。スレ再オープン時の二重ロード防止。</summary>
    public bool AiScoresLoaded { get; set; }

    /// <summary>このタブが別ペインへ移動した直後 (= 移動先で WebView2 が新規生成される) を示すフラグ。
    /// 移動先 WebView の初回 ready 時に、既存の <see cref="Posts"/> を一括 resync して再描画させるために使う
    /// (複数ペイン化 Phase 3)。通常の新規オープンでは false (= appendPosts チャネルで描画される)。</summary>
    public bool NeedsResyncOnAttach { get; set; }

    /// <summary>JS に「このレスまでスクロールしてくれ」と push するためのトリガ (Phase 25)。
    /// 5ch.io スレ URL のクリック (= postNo 付き) で本タブにスクロール要求が来た時に、新しい
    /// <see cref="ScrollToPostRequest"/> インスタンスを setter することで AttachedProperty 経由で
    /// JS に scrollToPost メッセージが飛ぶ。値が変わると WebView2Helper.ScrollToPostPush から JS に届く。</summary>
    [ObservableProperty]
    private ScrollToPostRequest? _pendingScrollToPost;

    [ObservableProperty]
    private ThreadViewMode _viewMode = ThreadViewMode.Flat;

    // 表示モード切替ボタンが巡回するモード順。旧 DedupTree は dedupTree2 へ置き換え中のため除外 (= UI から呼ばない)。
    private static readonly ThreadViewMode[] CycleViewModes =
        { ThreadViewMode.Flat, ThreadViewMode.Tree, ThreadViewMode.DedupTree2 };

    /// <summary>このタブが現在保持しているレス全件。スレ ViewModel 内/MainViewModel 内での件数読みだけに使う。
    /// PropertyChanged は発火させない (= WebView2 への描画は <see cref="LatestAppendBatch"/> を経由する単一チャネル)。
    /// 旧実装には Posts attached property + setPosts JS メッセージの全置換チャネルもあったが、
    /// 増分チャネルとの順序競合で「先にレンダ → setPosts([]) で消去」の真っ白現象が出ていたため撤去した。</summary>
    public IReadOnlyList<Post> Posts { get; private set; } = new List<Post>();

    /// <summary>直近の dat 取得で得た「dat 連番ベースのレス総数」(= 取得した dat 上の件数そのもの)。
    /// 差分取得の境界 (prevCount) に使う。<see cref="Posts"/> は NG 透明化で送られなかったレスを含まない
    /// (= dat 件数より少ない) ため、差分境界に <c>Posts.Count</c> を使うと NG で消えた件数ぶん境界が手前にずれ、
    /// 既出レスが新着として再 append される (= 二重表示) / 「以降新レス」ラベルがずれる。
    /// 取得成功のたびに <c>result.Posts.Count</c> で更新する。NG 追加 (<see cref="ReplaceVisiblePostsAfterNgAdd"/>)
    /// では変化させない (= dat 件数は NG で減らないため)。</summary>
    public int FetchedPostCount { get; set; }

    /// <summary>
    /// streaming で受け取った直近のレスバッチと、それが「差分 append (= incremental)」かどうかのフラグ。
    /// WebView2Helper.AppendBatch がこれを観測して JS の window.appendPosts() に送る。
    /// スレ表示への描画は常にこのチャネルだけを通る。
    /// </summary>
    [ObservableProperty]
    private AppendBatchData? _latestAppendBatch;

    /// <summary>
    /// JS にスクロール対象として伝えるレス番号。idx.json から読んだ初期値、または
    /// JS からの scrollPosition メッセージで随時更新される。
    /// </summary>
    [ObservableProperty]
    private long? _scrollTargetPostNumber;

    /// <summary>「以降新レス」ラベルの対象レス番号 (= ラベルがその直前に挿入される番号)。
    /// 永続化はしない (= 本アプリ起動以降の差分取得で来た新着のみを示す session-local な値)。
    /// 新規タブ生成時は null、<see cref="MainViewModel.RefreshThreadAsync"/> 等で差分取得が新着を
    /// もたらした瞬間にその先頭番号で更新される。タブ閉じ / アプリ再起動でリセットされる。
    /// JS 側はここの値を <c>appendPosts</c> ペイロード経由で受け取り、ラベル位置と
    /// dedup-tree モードでの「親ごと描写」境界に使う。</summary>
    [ObservableProperty]
    private long? _markPostNumber;

    /// <summary>「直前の差分取得で来た新着レスのいずれかが、自分のレス (<see cref="OwnPostNumbers"/>) を参照していた」
    /// と検出された状態。立っているとスレ一覧の状態マークが赤 (<see cref="LogMarkState.RepliedToOwn"/>) になる。
    ///
    /// 仕様 (= Phase 23+):
    ///   - 永続化しない (= idx.json に持たない)。アプリ再起動でリセットされる。
    ///   - cache load では立てない (= 既存スレ内の旧返信は赤化対象外)。
    ///   - <see cref="MainViewModel.ToggleOwnPost"/> でも立てない (= own 切替は対象イベントではない)。
    ///   - 差分取得 (= ApplyFetchDelta / Favorites cycle) で「新着レス」を取った瞬間にだけ
    ///     <see cref="MainViewModel.DeltaHasReplyToOwn"/> でチェックされ、true / false がセットされる。
    ///   - 直後の状態算定 (<see cref="MainViewModel.ComputeMarkState"/>) で読まれ、スレ一覧マーク色を決める。</summary>
    [ObservableProperty]
    private bool _hasReplyToOwn;

    /// <summary>
    /// このタブが現在 TabControl で選択されているか。各タブが専有する WebView2 の
    /// Visibility をこれに bind する (= 選択タブだけ可視、他は Collapsed)。
    /// MainViewModel が SelectedThreadTab 変更時に全タブの IsSelected を更新する。
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>このスレがお気に入りに登録されているか。
    /// ★ ボタンの押下表示 (背景強調) や、トグル動作 (押すと add or remove) のために使う。
    /// MainViewModel がお気に入り変更後に <see cref="MainViewModel.RefreshFavoritedStateOfAllTabs"/> で更新する。</summary>
    [ObservableProperty]
    private bool _isFavorited;

    /// <summary>タブ見出しに表示する状態マーク。意味は <see cref="LogMarkState"/> と同じ:
    ///   Cached (青) = ログあり / 件数一致、Updated (緑) = 新着あり、Dropped (茶) = subject.txt から消えた。
    /// 初期は Cached (= dat 取得直後)。板の subject.txt 再取得時に MainViewModel が更新する。</summary>
    [ObservableProperty]
    private LogMarkState _state = LogMarkState.Cached;

    /// <summary>「自分の書き込み」としてマークされているレス番号集合。
    /// idx.json から復元 + post-no メニューの「自分の書き込み」トグルで増減する。
    /// <see cref="IThreadDisplayBinding.OwnPostNumbers"/> 経由で WebView2 の appendPosts ペイロードに同梱され、
    /// JS 側で「自分」バッジ表示に使われる。</summary>
    public HashSet<long> OwnPostNumbers { get; } = new();

    /// <summary>WebView2 への増分通知 — 自分マークのトグル結果を JS 側に push するためのチャネル。
    /// <see cref="ChBrowser.Controls.WebView2Helper"/> の OwnPostsUpdate 添付プロパティがこれを観測して
    /// updateOwnPosts メッセージを送る。</summary>
    [ObservableProperty]
    private OwnPostsUpdateData? _ownPostsUpdate;

    /// <summary>アプリから送ったレスの評価 (レス番号 → 1 / -1 / 0)。idx.json から復元し、appendPosts / resync に同梱する。
    /// 取得時点の評価 (<see cref="PostExtra.MyVote"/>) より優先される。</summary>
    public Dictionary<long, int> MyVotes { get; } = new();

    /// <summary>WebView2 への増分通知 — 評価の確定 / 巻き戻しを JS 側に push するチャネル (VotesUpdate 添付プロパティが観測)。</summary>
    [ObservableProperty]
    private VotesUpdateData? _votesUpdate;

    /// <summary>このスレの投稿者情報 (投稿者名 → アイコン・プロフィール)。届いたものを貯め、resync に同梱する。</summary>
    public Dictionary<string, ChBrowser.Services.Bbs.AuthorProfile> AuthorProfiles { get; } = new(StringComparer.Ordinal);

    /// <summary>投稿者情報の送信チャネル (AuthorProfilesUpdate 添付プロパティが観測して updateAuthorProfiles を送る)。</summary>
    [ObservableProperty]
    private AuthorProfilesMessage? _authorProfilesUpdate;

    /// <summary>投稿者情報を足して JS へ送る。</summary>
    public void AddAuthorProfiles(IReadOnlyDictionary<string, ChBrowser.Services.Bbs.AuthorProfile> profiles)
    {
        if (profiles.Count == 0) return;
        foreach (var (n, p) in profiles) AuthorProfiles[n] = p;
        AuthorProfilesUpdate = new AuthorProfilesMessage(profiles);
    }

    /// <summary>絞り込みのテキストボックス (= スレッドペイン ヘッダ左) にバインドされる文字列。
    /// 変更で <see cref="Filter"/> が再構築される (= JS への push 経由で表示が即時更新される)。</summary>
    [ObservableProperty]
    private string _searchQuery = "";

    /// <summary>このタブ専用のステータス文字列 (= スレ取得進捗 / 結果 / エラー等)。
    /// MainViewModel が SelectedThreadTab 切替時にこの値を読み取ってステータスバーに反映する仕組みのため、
    /// タブごとに最後の状態が保持され、タブを切り替えれば過去のメッセージが復元される。
    /// 空文字なら「特に通知なし」(= ステータスバーは前の値のまま、もしくは別タブ / グローバルのものを維持)。</summary>
    [ObservableProperty]
    private string _statusMessage = "";

    /// <summary>「人気のレス」フィルタトグル (= 👍 ボタン)。tree モードでは popular の配下も含めて表示。</summary>
    [ObservableProperty]
    private bool _isPopularFilterOn;

    /// <summary>「画像/動画」フィルタトグル (= 🖼 ボタン)。本文に画像/動画 URL を含むレスのみ表示。</summary>
    [ObservableProperty]
    private bool _isMediaFilterOn;

    /// <summary>現在のフィルタ条件。<see cref="SearchQuery"/> / <see cref="IsPopularFilterOn"/> /
    /// <see cref="IsMediaFilterOn"/> から合成され、<see cref="ChBrowser.Controls.WebView2Helper"/> の
    /// FilterPush 添付プロパティ経由で JS に push される。各タブが独立した状態を持つ。</summary>
    [ObservableProperty]
    private ThreadFilter _filter = new ThreadFilter();

    partial void OnSearchQueryChanged(string value)        => RebuildFilter();
    partial void OnIsPopularFilterOnChanged(bool value)    => RebuildFilter();
    partial void OnIsMediaFilterOnChanged(bool value)      => RebuildFilter();

    private void RebuildFilter()
    {
        // SearchQuery と toggle 群を 1 つの ThreadFilter にまとめる。
        // テキストクエリは AND 条件、トグル 2 つは互いに OR (= JS 側 postMatchesFilter で合成判定)。
        Filter = new ThreadFilter(
            TextQuery:   SearchQuery ?? "",
            PopularOnly: IsPopularFilterOn,
            MediaOnly:   IsMediaFilterOn);
    }

    IReadOnlyCollection<long> IThreadDisplayBinding.OwnPostNumbers => OwnPostNumbers;

    /// <summary>このスレを開いた時の元タイトル (お気に入り登録時 / kakikomi.txt 用)。
    /// アドレスバーから直接スレを開いた経路では初期値が空文字で、dat の 1 レス目を取得した
    /// タイミングで <see cref="EnsureTitleFromDat"/> が埋める。一度埋まったら以降は変更しない。</summary>
    public string Title { get; private set; }

    /// <summary>dat の 1 レス目の <see cref="Post.ThreadTitle"/> をスレタイトルとして反映する。
    /// 既に <see cref="Title"/> が設定済 (= 板タブやお気に入り経由で開いた経路) なら何もしない。</summary>
    public void EnsureTitleFromDat(string title)
    {
        if (string.IsNullOrEmpty(title)) return;
        if (!string.IsNullOrEmpty(Title)) return;
        Title  = title;
        Header = TruncateForTab(title);
    }

    public ThreadTabViewModel(
        Board                                board,
        ThreadInfo                           info,
        Action<ThreadTabViewModel>           closeCallback,
        Action<ThreadTabViewModel>?          deleteCallback         = null,
        Action<ThreadTabViewModel>?          refreshCallback        = null,
        Action<ThreadTabViewModel>?          addToFavoritesCallback = null,
        Action<ThreadTabViewModel>?          writeCallback          = null,
        Action<ThreadTabViewModel>?          aiChatCallback         = null,
        Action<ThreadTabViewModel>?          translateCallback      = null)
    {
        Board                  = board;
        ThreadKey              = info.Key;
        Title                  = info.Title;
        _header                = TruncateForTab(info.Title);
        CloseCommand           = new RelayCommand(() => closeCallback(this));
        DeleteCommand          = new RelayCommand(() => deleteCallback?.Invoke(this));
        RefreshCommand         = new RelayCommand(() => refreshCallback?.Invoke(this));
        AddToFavoritesCommand  = new RelayCommand(() => addToFavoritesCallback?.Invoke(this));
        WriteCommand           = new RelayCommand(() => writeCallback?.Invoke(this));
        AiChatCommand          = new RelayCommand(() => aiChatCallback?.Invoke(this));
        ToggleTranslationCommand = new RelayCommand(() => translateCallback?.Invoke(this));
        RefreshProviderConfig();
        // 表示モード切替ボタンのサイクル順で次へ進む (一周したら先頭へ)。
        // 旧 DedupTree は dedupTree2 へ置き換え中のためサイクルから除外している (ソースは残すが UI からは呼ばない)。
        CycleViewModeCommand   = new RelayCommand(() =>
        {
            var idx  = Array.IndexOf(CycleViewModes, ViewMode);
            ViewMode = CycleViewModes[(idx + 1) % CycleViewModes.Length]; // idx=-1 (サイクル外) → 先頭(Flat)
        });
    }

    /// <summary><see cref="AiScores"/> としきい値から「非表示にすべきレス番号集合」を作り直し、
    /// <see cref="AiHiddenPostNumbers"/> に新インスタンスで代入する (= JS へ setAiHidden が飛ぶ)。
    /// しきい値 6 以上 (= OFF) の時は空集合 = 全再表示。逐次判定 / しきい値変更のたびに呼ぶ。</summary>
    public void RecomputeAiHidden(int threshold)
    {
        var hidden = new List<long>();
        if (threshold <= 5)
        {
            foreach (var kv in AiScores)
                if (kv.Value >= threshold) hidden.Add(kv.Key);
        }
        AiHiddenPostNumbers = hidden; // 空配列でも push する (= 全クリアの意味になる)
    }

    /// <summary>レスを末尾に追加。内部 <see cref="Posts"/> を更新したあと、
    /// <see cref="LatestAppendBatch"/> 経由で WebView2 (JS) に増分を送る。
    /// <paramref name="isIncremental"/> = true は「初期表示が完了した後の差分追加」を示し
    /// (= リフレッシュ / お気に入りチェック後の差分等)、JS 側の dedup-tree 描画で 2 セクション構成
    /// (既存ツリー + 末尾の incremental tail block) に切り替えるシグナルになる (Phase 20)。</summary>
    public void AppendPosts(IReadOnlyList<Post> batch, bool isIncremental = false)
    {
        if (batch.Count == 0) return;
        var merged = new List<Post>(Posts.Count + batch.Count);
        merged.AddRange(Posts);
        merged.AddRange(batch);
        Posts = merged;
        LatestAppendBatch = new AppendBatchData(batch, isIncremental);
    }

    /// <summary>NG ルール追加直後に、可視 Posts を絞り込み + JS 側に「これらを DOM から消して」と push する。
    /// MainViewModel.ApplyNewlyHiddenToOpenTabs から呼ばれる。
    ///
    /// 引数:
    ///  - <paramref name="newVisible"/>: 新ルール適用後に可視として残すレス
    ///  - <paramref name="newlyHiddenNumbers"/>: 新たに hidden になるレス番号集合 (JS に送る対象)
    ///  - <paramref name="breakdown"/>: 内訳 (per-rule + 連鎖) — HiddenByRule / HiddenByChain に加算する</summary>
    public void ReplaceVisiblePostsAfterNgAdd(
        IReadOnlyList<Post> newVisible,
        ICollection<long> newlyHiddenNumbers,
        ChBrowser.Services.Ng.NgHiddenBreakdown breakdown)
    {
        Posts = newVisible;
        HiddenCount += newlyHiddenNumbers.Count;
        AddHiddenBreakdown(breakdown);
        // 同じ集合を立て続けに送る場合に PropertyChanged が飛ぶよう、毎回新インスタンスを setter する。
        PendingHidePostNumbers = new List<long>(newlyHiddenNumbers);
    }

    // ViewMode 変更時の追加通知は不要 (= XAML は <c>{Binding ViewMode, Value={x:Static ThreadViewMode.Xxx}}</c> で
    // 直接比較しており、IsViewModeFlat 等の bool 派生プロパティは持たない)。

    private static string TruncateForTab(string title)
    {
        const int max = 24;
        if (title.Length <= max) return title;
        // サロゲートペア (絵文字等) の途中で切らない。title[..24] の素の切断だと末尾に孤立上位
        // サロゲートが残り、不正な UTF-16 になる (= タブ描画側の ConvertToUtf32 が例外 →
        // カラー絵文字描画がモノクロにフォールバックする実害があった)。
        var cut = max;
        if (char.IsHighSurrogate(title[cut - 1])) cut--;
        return title[..cut] + "…";
    }
}
