using System;
using System.Collections.Generic;
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
}

/// <summary>JS の <c>appendPosts</c> に渡すペイロード (Phase 20)。
/// <see cref="IsIncremental"/> = true なら、dedup-tree モードでこの batch 以降を「末尾 incremental block」として
/// 既存ツリーとは別レンダリングにする (= 既読下に新着を表示するため)。</summary>
public sealed record AppendBatchData(IReadOnlyList<Post> Posts, bool IsIncremental);

/// <summary>JS の <c>updateOwnPosts</c> に渡すペイロード — 自分マークのトグル結果を 1 件ずつ通知する。
/// JS は changes 配列をそのまま受け取り、各 (number, isOwn) で DOM の「自分」バッジを toggle する。</summary>
public sealed record OwnPostsUpdateData(IReadOnlyList<OwnPostChange> Changes);

/// <summary>1 件分の自分マークトグル結果。</summary>
public sealed record OwnPostChange(int Number, bool IsOwn);

/// <summary>
/// 1 スレッド = 1 タブ。WebView2 へは Posts (Post 列) を Bind し、HTML 構築は JS 側で行う。
/// 表示モード (Flat / Tree / DedupTree) はすべて JS 側で実装済 (thread.js)、
/// <see cref="CycleViewModeCommand"/> でトグル → setViewMode メッセージで JS が再描画する。
/// </summary>
public sealed partial class ThreadTabViewModel : ObservableObject, IThreadDisplayBinding
{
    public Board  Board     { get; }
    public string ThreadKey { get; }

    /// <summary>このスレの正規 URL (5ch.io / bbspink.com の <c>test/read.cgi</c> 形式)。
    /// アドレスバー表示やコンテキストメニューの「URLコピー」で使う。</summary>
    public string Url => $"https://{Board.Host}/test/read.cgi/{Board.DirectoryName}/{ThreadKey}/";

    public IRelayCommand CloseCommand           { get; }
    public IRelayCommand CycleViewModeCommand  { get; }
    public IRelayCommand DeleteCommand          { get; }
    public IRelayCommand RefreshCommand         { get; }
    public IRelayCommand AddToFavoritesCommand  { get; }
    public IRelayCommand WriteCommand           { get; }

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

    /// <summary>1 batch 分の <see cref="ChBrowser.Services.Ng.NgHiddenBreakdown"/> を内訳カウンタに加算する。
    /// MainViewModel.AppendPostsWithNg から呼ばれる。</summary>
    public void AddHiddenBreakdown(ChBrowser.Services.Ng.NgHiddenBreakdown breakdown)
    {
        foreach (var (ruleId, count) in breakdown.ByRuleDirect)
            HiddenByRule[ruleId] = HiddenByRule.TryGetValue(ruleId, out var c) ? c + count : count;
        HiddenByChain += breakdown.ChainOnly;
    }

    /// <summary>JS に「これらのレス番号を即時 DOM から消して」と push するためのトリガ。
    /// 値が変わると WebView2Helper.HidePostsPush (= setHiddenPosts) で送られる。
    /// 同じ集合を 2 回送る (= ユーザが立て続けに NG 追加する) ケースに備え、IReadOnlyList を新インスタンス
    /// で setter する (= 参照同一だと PropertyChanged が飛ばない可能性がある)。 </summary>
    [ObservableProperty]
    private IReadOnlyList<int>? _pendingHidePostNumbers;

    [ObservableProperty]
    private ThreadViewMode _viewMode = ThreadViewMode.Flat;

    /// <summary>このタブが現在保持しているレス全件。スレ ViewModel 内/MainViewModel 内での件数読みだけに使う。
    /// PropertyChanged は発火させない (= WebView2 への描画は <see cref="LatestAppendBatch"/> を経由する単一チャネル)。
    /// 旧実装には Posts attached property + setPosts JS メッセージの全置換チャネルもあったが、
    /// 増分チャネルとの順序競合で「先にレンダ → setPosts([]) で消去」の真っ白現象が出ていたため撤去した。</summary>
    public IReadOnlyList<Post> Posts { get; private set; } = new List<Post>();

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
    private int? _scrollTargetPostNumber;

    /// <summary>「以降新レス」ラベルの対象レス番号 (= ラベルがその直前に挿入される番号)。
    /// 永続化はしない (= 本アプリ起動以降の差分取得で来た新着のみを示す session-local な値)。
    /// 新規タブ生成時は null、<see cref="MainViewModel.RefreshThreadAsync"/> 等で差分取得が新着を
    /// もたらした瞬間にその先頭番号で更新される。タブ閉じ / アプリ再起動でリセットされる。
    /// JS 側はここの値を <c>appendPosts</c> ペイロード経由で受け取り、ラベル位置と
    /// dedup-tree モードでの「親ごと描写」境界に使う。</summary>
    [ObservableProperty]
    private int? _markPostNumber;

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
    public HashSet<int> OwnPostNumbers { get; } = new();

    /// <summary>WebView2 への増分通知 — 自分マークのトグル結果を JS 側に push するためのチャネル。
    /// <see cref="ChBrowser.Controls.WebView2Helper"/> の OwnPostsUpdate 添付プロパティがこれを観測して
    /// updateOwnPosts メッセージを送る。</summary>
    [ObservableProperty]
    private OwnPostsUpdateData? _ownPostsUpdate;

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

    IReadOnlyCollection<int> IThreadDisplayBinding.OwnPostNumbers => OwnPostNumbers;

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
        Action<ThreadTabViewModel>?          writeCallback          = null)
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
        // enum 順で次のモードに進む (一周したら先頭に戻る)。新モードを <see cref="ThreadViewMode"/> に
        // 追加すると、ここを触らずにそのままサイクルに含まれる。
        CycleViewModeCommand   = new RelayCommand(() =>
        {
            var values = (ThreadViewMode[])Enum.GetValues(typeof(ThreadViewMode));
            var idx    = Array.IndexOf(values, ViewMode);
            ViewMode   = values[(idx + 1) % values.Length];
        });
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
        ICollection<int> newlyHiddenNumbers,
        ChBrowser.Services.Ng.NgHiddenBreakdown breakdown)
    {
        Posts = newVisible;
        HiddenCount += newlyHiddenNumbers.Count;
        AddHiddenBreakdown(breakdown);
        // 同じ集合を立て続けに送る場合に PropertyChanged が飛ぶよう、毎回新インスタンスを setter する。
        PendingHidePostNumbers = new List<int>(newlyHiddenNumbers);
    }

    // ViewMode 変更時の追加通知は不要 (= XAML は <c>{Binding ViewMode, Value={x:Static ThreadViewMode.Xxx}}</c> で
    // 直接比較しており、IsViewModeFlat 等の bool 派生プロパティは持たない)。

    private static string TruncateForTab(string title)
    {
        const int max = 24;
        return title.Length <= max ? title : title[..max] + "…";
    }
}
