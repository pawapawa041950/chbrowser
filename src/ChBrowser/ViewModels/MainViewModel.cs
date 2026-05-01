using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using ChBrowser.Models;
using ChBrowser.Services.Api;
using ChBrowser.Services.Donguri;
using ChBrowser.Services.Storage;
using ChBrowser.Services.Url;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChBrowser.ViewModels;

/// <summary>メインウィンドウの ViewModel。</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly BbsmenuClient        _bbsmenuClient;
    private readonly SubjectTxtClient     _subjectClient;
    private readonly DatClient            _datClient;
    private readonly ThreadIndexService   _threadIndex;
    private readonly PostClient           _postClient;
    private readonly DonguriService       _donguri;
    private readonly DispatcherTimer      _donguriTimer;
    private readonly ChBrowser.Services.Ng.NgService _ng;
    private readonly DataPaths            _paths;

    public ObservableCollection<BoardCategoryViewModel> BoardCategories  { get; } = new();
    public ObservableCollection<ThreadListTabViewModel> ThreadListTabs   { get; } = new();
    public ObservableCollection<ThreadTabViewModel>     ThreadTabs       { get; } = new();

    /// <summary>お気に入りペイン (TreeView) のルート。Phase 7 で導入。</summary>
    public FavoritesViewModel Favorites { get; }

    [ObservableProperty]
    private string _statusMessage = "準備完了";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>板一覧 WebView2 にバインドする HTML (Phase 14a)。
    /// <see cref="ApplyCategories"/> 直後と <see cref="SetCategoryExpanded"/> 直後に再生成する。</summary>
    [ObservableProperty]
    private string _boardListHtml = "";

    /// <summary>お気に入りペイン WebView2 にバインドする HTML (Phase 14b)。
    /// <see cref="FavoritesViewModel.Changed"/> 発火時に再生成する。</summary>
    [ObservableProperty]
    private string _favoritesHtml = "";

    /// <summary>スレ表示 WebView2 に setConfig メッセージとして送る JSON (Phase 11)。
    /// 設定画面で値が変わるたびに <see cref="ApplyConfig"/> が更新する。
    /// 添付プロパティ <c>WebView2Helper.ThreadConfigJson</c> がこれを観測して全タブに push。</summary>
    [ObservableProperty]
    private string _threadConfigJson = "";

    /// <summary>スレ表示 WebView へ push するショートカット bind 一覧 (Phase 16)。
    /// JS 側ブリッジが受信して suppress 対象を更新するための setShortcutBindings メッセージ JSON。</summary>
    [ObservableProperty]
    private string _threadShortcutsJson = "";

    [ObservableProperty] private string _threadListShortcutsJson = "";
    [ObservableProperty] private string _favoritesShortcutsJson  = "";
    [ObservableProperty] private string _boardListShortcutsJson  = "";

    /// <summary>マウスジェスチャー入力中のリアルタイム表示 (Phase 16+)。
    /// 入力中は "ジェスチャー [&lt;カテゴリ&gt;] ↓→" の形式、未入力時は空文字。</summary>
    [ObservableProperty]
    private string _gestureStatus = "";

    /// <summary>WPF / WebView ブリッジ両方から呼ばれる。category=null で表示クリア。</summary>
    public void UpdateGestureStatus(string? category, string? gesture)
    {
        if (string.IsNullOrEmpty(category))
        {
            GestureStatus = "";
            return;
        }
        GestureStatus = string.IsNullOrEmpty(gesture)
            ? $"ジェスチャー [{category}]"
            : $"ジェスチャー [{category}] {gesture}";
    }

    // Phase 11b: 3 ペインの 1 クリック設定。各ペイン用に個別の JSON を持ち、
    // それぞれ別の WebView2 (favorites / board-list / thread-list) に PaneConfigJson 添付プロパティで配信。
    [ObservableProperty] private string _favoritesConfigJson  = "";
    [ObservableProperty] private string _boardListConfigJson  = "";
    [ObservableProperty] private string _threadListConfigJson = "";

    /// <summary>ステータスバーに出すどんぐり (acorn) の状態テキスト。
    /// 例: "🥜 未取得" / "🥜 Lv≈3 (15分経過, 残り 2h45m)" / "🥜 失効"。
    /// 30秒ごとに <see cref="_donguriTimer"/> が更新する。</summary>
    [ObservableProperty]
    private string _donguriStatus = "🥜 未取得";

    [ObservableProperty]
    private ThreadListTabViewModel? _selectedThreadListTab;

    [ObservableProperty]
    private ThreadTabViewModel? _selectedThreadTab;

    partial void OnSelectedThreadTabChanged(ThreadTabViewModel? value)
    {
        // 各タブの WebView2 は専属インスタンス (TabControl ではなく ItemsControl で並列描画)。
        // Visibility 切替で選択タブだけ可視にするため、IsSelected を更新する。
        foreach (var t in ThreadTabs) t.IsSelected = ReferenceEquals(t, value);
        // ステータスバーの「あぼーん N」を選択タブのものに更新
        AboneStatus = value is null ? "あぼーん 0" : $"あぼーん {value.HiddenCount}";
        // アドレスバーは last-activated wins (Phase 14): 最後に切替/選択した方の URL を表示
        if (value is not null) _lastActivePane = ActivePane.Thread;
        RefreshAddressBarUrl();
    }

    partial void OnSelectedThreadListTabChanged(ThreadListTabViewModel? value)
    {
        // 各 WebView は ItemsControl で並列保持し Visibility 切替で表示制御するため、IsSelected を更新。
        // (旧 ContentTemplate 構成だとタブ切替で WebView が再生成されホワイトフラッシュが出ていた)
        foreach (var t in ThreadListTabs) t.IsSelected = ReferenceEquals(t, value);
        // アドレスバーは last-activated wins (Phase 14)
        if (value is not null) _lastActivePane = ActivePane.ThreadList;
        RefreshAddressBarUrl();
    }

    // -----------------------------------------------------------------
    // Phase 14: アドレスバー
    // -----------------------------------------------------------------

    private enum ActivePane { None, ThreadList, Thread }
    private ActivePane _lastActivePane = ActivePane.None;

    /// <summary>右ペイン上段「スレ欄」(ThreadListTabs) がアクティブ化された (= フォーカス取得 / 選択切替) と
    /// 通知する。アドレスバーは現在の <see cref="SelectedThreadListTab"/> の板 URL を表示する。</summary>
    public void MarkThreadListPaneActive()
    {
        _lastActivePane = ActivePane.ThreadList;
        RefreshAddressBarUrl();
    }

    /// <summary>右ペイン下段「スレ表示」(ThreadTabs) がアクティブ化された (= フォーカス取得 / 選択切替) と
    /// 通知する。アドレスバーは現在の <see cref="SelectedThreadTab"/> のスレ URL を表示する。</summary>
    public void MarkThreadPaneActive()
    {
        _lastActivePane = ActivePane.Thread;
        RefreshAddressBarUrl();
    }

    /// <summary>アドレスバーに表示する現在のタブの URL (= 最後にアクティブ化した方の URL)。
    /// 板タブのみアクティブ → 板 URL、スレタブ最後にアクティブ → スレ URL、どちらも未選択 → 空。
    /// 表示 URL は <see cref="Board.Url"/> (= bbsmenu 由来の正規 URL、サブドメイン込み) をそのまま使う。</summary>
    [ObservableProperty]
    private string _addressBarUrl = "";

    /// <summary>アドレスバーをエラー表示 (赤枠) するためのフラグ。
    /// 不正な URL / 対応外ホストを Enter したとき true。次の有効入力 or タブ切替で false。</summary>
    [ObservableProperty]
    private bool _addressBarHasError;

    /// <summary>現在のアクティブタブから AddressBarUrl を再計算する。
    /// SelectedThreadTab / SelectedThreadListTab の変更ハンドラと、URL ナビゲート完了後に呼ばれる。</summary>
    private void RefreshAddressBarUrl()
    {
        AddressBarHasError = false;
        AddressBarUrl = _lastActivePane switch
        {
            ActivePane.Thread     => SelectedThreadTab is { } t ? BuildThreadUrl(t) : (SelectedThreadListTab?.Board?.Url ?? ""),
            ActivePane.ThreadList => SelectedThreadListTab?.Board?.Url ?? "",
            _                     => "",
        };
    }

    private static string BuildThreadUrl(ThreadTabViewModel tab)
        => $"https://{tab.Board.Host}/test/read.cgi/{tab.Board.DirectoryName}/{tab.ThreadKey}/";

    /// <summary>アドレスバーの Enter で呼ばれる。入力テキストを <see cref="AddressBarParser"/> で解釈し、
    /// 板/スレに応じて開く (既存タブがあればアクティブ化)。
    /// 不正なら <see cref="AddressBarHasError"/>=true + ステータスバーへエラー文。</summary>
    public async Task NavigateAddressBarAsync(string input)
    {
        var target = AddressBarParser.Parse(input);
        switch (target.Kind)
        {
            case AddressBarTargetKind.Board:
                AddressBarHasError = false;
                await OpenBoardByUrlAsync(target.Host, target.Directory).ConfigureAwait(true);
                break;
            case AddressBarTargetKind.Thread:
                AddressBarHasError = false;
                await OpenThreadByUrlAsync(target.Host, target.Directory, target.ThreadKey).ConfigureAwait(true);
                break;
            default:
                AddressBarHasError = true;
                StatusMessage = "認識できない URL です (5ch.io / bbspink.com の板 / スレ URL を入力してください)";
                break;
        }
    }

    /// <summary>板 URL を開く: 既存 ThreadListTab を root domain + dir 一致で検索 → アクティブ化、
    /// 無ければ <see cref="ResolveBoard"/> で Board を組み立てて新規開く。</summary>
    public async Task OpenBoardByUrlAsync(string host, string dir)
    {
        var rootIn = DataPaths.ExtractRootDomain(host);
        foreach (var tab in ThreadListTabs)
        {
            if (tab.Board is not { } b) continue;
            if (string.Equals(DataPaths.ExtractRootDomain(b.Host), rootIn, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(b.DirectoryName, dir, StringComparison.Ordinal))
            {
                SelectedThreadListTab = tab;
                return;
            }
        }
        var board = ResolveBoard(host, dir, "");
        await LoadThreadListAsync(new BoardViewModel(board)).ConfigureAwait(true);
    }

    /// <summary>スレ URL を開く: 既存 ThreadTab を root domain + dir + key 一致で検索 → アクティブ化、
    /// 無ければ <see cref="OpenThreadFromListAsync"/> 経由で新規開く。</summary>
    public async Task OpenThreadByUrlAsync(string host, string dir, string key)
    {
        var rootIn = DataPaths.ExtractRootDomain(host);
        foreach (var tab in ThreadTabs)
        {
            if (string.Equals(DataPaths.ExtractRootDomain(tab.Board.Host), rootIn, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(tab.Board.DirectoryName, dir, StringComparison.Ordinal) &&
                string.Equals(tab.ThreadKey,           key, StringComparison.Ordinal))
            {
                SelectedThreadTab = tab;
                return;
            }
        }
        await OpenThreadFromListAsync(host, dir, key, "").ConfigureAwait(true);
    }

    public MainViewModel(
        BbsmenuClient        bbsmenuClient,
        SubjectTxtClient     subjectClient,
        DatClient            datClient,
        ThreadIndexService   threadIndex,
        FavoritesStorage     favoritesStorage,
        PostClient           postClient,
        DonguriService       donguri,
        ChBrowser.Services.Ng.NgService ng,
        DataPaths            paths)
    {
        _bbsmenuClient = bbsmenuClient;
        _subjectClient = subjectClient;
        _datClient     = datClient;
        _threadIndex   = threadIndex;
        _postClient    = postClient;
        _donguri       = donguri;
        _ng            = ng;
        _paths         = paths;
        Favorites      = new FavoritesViewModel(favoritesStorage);

        // お気に入りツリーが変わるたびに HTML を再生成 (Phase 14b)
        Favorites.Changed += RefreshFavoritesHtml;

        // どんぐり (acorn) の経過時間表示を 30 秒ごとに更新。起動直後にも 1 度実行。
        _donguriTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _donguriTimer.Tick += (_, _) => UpdateDonguriStatus();
        _donguriTimer.Start();
        UpdateDonguriStatus();
    }

    /// <summary>acorn の発行時刻 + 経過秒数からステータス文字列を組み立てる。
    /// 設計書 §3.5: 0→1 で約 5 分、約 3 時間で失効、IP 変更で Lv0 リセット。
    /// 推定 Lv は age / 300 (= 5 分間隔) のフロアとして表示する (実際のサーバ判定とは異なる目安)。</summary>
    private void UpdateDonguriStatus()
    {
        var age = _donguri.EstimatedAcornAgeSeconds;
        if (age is null)
        {
            DonguriStatus = "🥜 未取得";
            return;
        }
        const int lifetimeSec = 3 * 60 * 60; // 3 時間
        if (age >= lifetimeSec)
        {
            DonguriStatus = "🥜 失効 (再取得が必要)";
            return;
        }
        var lv        = (int)(age / 300);            // 5 分 = Lv 1 step の目安
        var ageStr    = FormatHm(age.Value);
        var remaining = lifetimeSec - age.Value;
        DonguriStatus = $"🥜 Lv≈{lv} ({ageStr}経過, 残り {FormatHm(remaining)})";
    }

    /// <summary>秒数を "Xh Ym" / "Ym Zs" / "Zs" の短縮表記に。ステータスバー幅節約のため。</summary>
    private static string FormatHm(long seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1)   return $"{(int)ts.TotalHours}h{ts.Minutes:00}m";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m{ts.Seconds:00}s";
        return $"{ts.Seconds}s";
    }

    // -----------------------------------------------------------------
    // Phase 11: アプリ設定 (AppConfig) の保持と適用
    // -----------------------------------------------------------------

    /// <summary>現在の設定値。新規スレタブ生成時の初期 ViewMode 等で参照される。</summary>
    public Models.AppConfig CurrentConfig { get; private set; } = new();

    /// <summary>NgService への外部アクセス (NgWindow から再ロード等を呼ぶため)。</summary>
    public ChBrowser.Services.Ng.NgService NgService => _ng;

    /// <summary>あぼーん件数のステータスバー表示。SelectedThreadTab の hidden 数を表示。</summary>
    [ObservableProperty]
    private string _aboneStatus = "あぼーん 0";

    /// <summary>NG ルール変更後に呼ぶ — NgService をディスクから再ロード。
    /// 既に開いている全タブには即時反映しない (DOM 上の post を後から消す経路を持たないため)。
    /// ユーザにはタブを開き直すよう案内する。</summary>
    public void ReapplyNgToOpenTabs()
    {
        _ng.Reload();
        StatusMessage = "NG ルールを保存しました — 開いているスレタブは閉じて開き直すと反映されます";
    }

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

    // -----------------------------------------------------------------
    // Phase 11c: タブ動作 (中クリック / 装飾付き左クリック / 左ダブルクリック)
    // -----------------------------------------------------------------

    /// <summary>スレ一覧タブに対するアクションを実行 (= 設定で割り当てた動作)。</summary>
    public void ExecuteThreadListTabAction(ThreadListTabViewModel tab, string action)
    {
        switch (action)
        {
            case "close":
                tab.CloseCommand.Execute(null);
                break;
            case "refresh":
                // 板タブのみ更新可能 (お気に入りディレクトリ展開タブは Board=null)
                if (tab.Board is not null)
                    _ = LoadThreadListAsync(new BoardViewModel(tab.Board));
                break;
            case "closeOthers":
                CloseOtherThreadListTabs(tab);
                break;
            case "closeLeft":
                CloseThreadListTabsBefore(tab);
                break;
            case "closeRight":
                CloseThreadListTabsAfter(tab);
                break;
            // none / addFavorite / deleteLog はスレ一覧タブには適用しない
        }
    }

    /// <summary>スレッドタブに対するアクションを実行。</summary>
    public void ExecuteThreadTabAction(ThreadTabViewModel tab, string action)
    {
        switch (action)
        {
            case "close":       tab.CloseCommand.Execute(null);          break;
            case "refresh":     tab.RefreshCommand.Execute(null);        break;
            case "addFavorite": tab.AddToFavoritesCommand.Execute(null); break;
            case "deleteLog":   tab.DeleteCommand.Execute(null);         break;
            case "closeOthers": CloseOtherThreadTabs(tab); break;
            case "closeLeft":   CloseThreadTabsBefore(tab); break;
            case "closeRight":  CloseThreadTabsAfter(tab);  break;
        }
    }

    private void CloseOtherThreadTabs(ThreadTabViewModel keep)
    {
        // 削除中のコレクション変更を避けるためスナップショットしてから処理
        foreach (var t in ThreadTabs.ToList())
            if (!ReferenceEquals(t, keep)) ThreadTabs.Remove(t);
    }

    private void CloseThreadTabsBefore(ThreadTabViewModel pivot)
    {
        var idx = ThreadTabs.IndexOf(pivot);
        if (idx <= 0) return;
        for (var i = idx - 1; i >= 0; i--) ThreadTabs.RemoveAt(i);
    }

    private void CloseThreadTabsAfter(ThreadTabViewModel pivot)
    {
        var idx = ThreadTabs.IndexOf(pivot);
        if (idx < 0) return;
        while (ThreadTabs.Count > idx + 1) ThreadTabs.RemoveAt(ThreadTabs.Count - 1);
    }

    private void CloseOtherThreadListTabs(ThreadListTabViewModel keep)
    {
        foreach (var t in ThreadListTabs.ToList())
            if (!ReferenceEquals(t, keep)) ThreadListTabs.Remove(t);
    }

    private void CloseThreadListTabsBefore(ThreadListTabViewModel pivot)
    {
        var idx = ThreadListTabs.IndexOf(pivot);
        if (idx <= 0) return;
        for (var i = idx - 1; i >= 0; i--) ThreadListTabs.RemoveAt(i);
    }

    private void CloseThreadListTabsAfter(ThreadListTabViewModel pivot)
    {
        var idx = ThreadListTabs.IndexOf(pivot);
        if (idx < 0) return;
        while (ThreadListTabs.Count > idx + 1) ThreadListTabs.RemoveAt(ThreadListTabs.Count - 1);
    }

    /// <summary>App.xaml.cs の初期化時に最初に 1 度呼ばれ、以降は設定画面の即時反映でも呼ばれる。
    /// HttpClient.Timeout や HiDPI 等「次回起動時に反映」の項目はここでは触らない (= App 側で起動時に読む)。</summary>
    public void ApplyConfig(Models.AppConfig config)
    {
        CurrentConfig = config;

        // スレ表示 (thread.js) 向け
        var threadPayload = new
        {
            type                  = "setConfig",
            popularThreshold      = config.PopularThreshold,
            imageSizeThresholdMb  = config.ImageSizeThresholdMb,
            showReadMark          = config.ShowReadMark,
        };
        ThreadConfigJson = System.Text.Json.JsonSerializer.Serialize(threadPayload);

        // Phase 11b: 3 ペイン向け。各ペインは自分の JSON だけ受け取り、setConfig.openOnSingleClick を解釈する。
        FavoritesConfigJson  = System.Text.Json.JsonSerializer.Serialize(new
        {
            type               = "setConfig",
            openOnSingleClick  = config.FavoritesOpenOnSingleClick,
        });
        BoardListConfigJson  = System.Text.Json.JsonSerializer.Serialize(new
        {
            type               = "setConfig",
            openOnSingleClick  = config.BoardListOpenOnSingleClick,
        });
        ThreadListConfigJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            type               = "setConfig",
            openOnSingleClick  = config.ThreadListOpenOnSingleClick,
        });
    }

    /// <summary>ローカル保存済みの bbsmenu とお気に入りを最初に読み込む。なければ空のまま。</summary>
    public async Task InitializeAsync()
    {
        // お気に入りはローカル JSON 1 個読むだけなので同期的に
        Favorites.Reload();

        try
        {
            var cats = await _bbsmenuClient.LoadFromDiskAsync().ConfigureAwait(true);
            ApplyCategories(cats);
            if (cats.Count == 0)
                StatusMessage = "板一覧未取得 - ファイル → 板一覧を更新 を実行してください";
            else
                StatusMessage = $"板一覧 (キャッシュ): {TotalBoards(cats)} 板";
        }
        catch (Exception ex)
        {
            StatusMessage = $"板一覧の読み込みに失敗: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshBoardListAsync()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            StatusMessage = "板一覧を取得中...";
            var cats = await _bbsmenuClient.FetchAndSaveAsync().ConfigureAwait(true);
            ApplyCategories(cats);
            StatusMessage = $"板一覧を更新しました: {TotalBoards(cats)} 板";
        }
        catch (Exception ex)
        {
            StatusMessage = $"板一覧の取得に失敗: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyCategories(IReadOnlyList<BoardCategory> cats)
    {
        BoardCategories.Clear();
        foreach (var c in cats) BoardCategories.Add(new BoardCategoryViewModel(c));
        RefreshBoardListHtml();
    }

    /// <summary>板一覧 WebView2 用の HTML を <see cref="BoardCategories"/> から再生成する。</summary>
    public void RefreshBoardListHtml()
        => BoardListHtml = ChBrowser.Services.Render.BoardListHtmlBuilder.Build(BoardCategories);

    /// <summary>JS 側 setCategoryExpanded メッセージから呼ばれる。
    /// ViewModel の IsExpanded を更新 (HTML は再生成しない — トグルは DOM 上で既に反映されているため)。</summary>
    public void SetCategoryExpanded(string categoryName, bool expanded)
    {
        foreach (var c in BoardCategories)
        {
            if (c.CategoryName == categoryName)
            {
                c.IsExpanded = expanded;
                return;
            }
        }
    }

    /// <summary>板一覧 WebView2 から openBoard メッセージで呼ばれる。
    /// (host, directoryName) で板を解決して LoadThreadListAsync に渡す。</summary>
    public Task OpenBoardFromHtmlListAsync(string host, string directoryName, string boardName)
    {
        var board = ResolveBoard(host, directoryName, boardName);
        var bvm   = new BoardViewModel(board);
        return LoadThreadListAsync(bvm);
    }

    /// <summary>板一覧 WebView2 から contextMenu (target=board) → 「お気に入りに追加」を選んだとき。</summary>
    public void AddBoardToFavoritesByHostDir(string host, string directoryName, string boardName)
    {
        var board = ResolveBoard(host, directoryName, boardName);
        var bvm   = new BoardViewModel(board);
        AddBoardToFavorites(bvm);
    }

    private static int TotalBoards(IReadOnlyList<BoardCategory> cats)
    {
        var n = 0;
        foreach (var c in cats) n += c.Boards.Count;
        return n;
    }

    /// <summary>
    /// 板ダブルクリック時に呼ばれる。既存タブがあればそれを使い、
    /// いずれにしても subject.txt を再取得して新着判定 (緑丸) を反映する。
    /// </summary>
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
            tab = new ThreadListTabViewModel(board, t => ThreadListTabs.Remove(t));
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

            // この板に登録済のお気に入りスレキーを抽出 (HashSet<string> で渡す)
            var favKeys = new HashSet<string>(
                Favorites.CollectFavoriteThreadKeys()
                         .Where(k => k.Host == board.Host && k.Dir == board.DirectoryName)
                         .Select(k => k.Key));
            tab.SetThreads(allThreads, DateTimeOffset.UtcNow, states, favKeys);

            // この板に属する開きっぱなしのスレタブの状態マークも同期する
            // (dat 落ち判定で「茶色」になったり、新着判定で「緑」になったり)
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

    /// <summary>
    /// 各スレッドのログ状態を判定する。
    /// ログ無し → None / ログあり &amp; 件数一致 → Cached / ログあり &amp; subject の方が多い → Updated。
    /// </summary>
    private IReadOnlyDictionary<string, LogMarkState> BuildLogStates(Board board, IReadOnlyList<ThreadInfo> threads)
    {
        var keysWithLog = _datClient.EnumerateExistingThreadKeys(board);
        var dict        = new Dictionary<string, LogMarkState>(keysWithLog.Count);
        if (keysWithLog.Count == 0) return dict;

        foreach (var t in threads)
        {
            if (!keysWithLog.Contains(t.Key)) continue;
            var idx        = _threadIndex.Load(board.Host, board.DirectoryName, t.Key);
            var fetched    = idx?.LastFetchedPostCount;
            var hasNew     = fetched is int f && t.PostCount > f;
            dict[t.Key]    = hasNew ? LogMarkState.Updated : LogMarkState.Cached;
        }
        return dict;
    }

    /// <summary>
    /// スレ一覧でスレをダブルクリックしたとき呼ばれる。
    /// 既存タブがあればアクティブ化、無ければ新タブを作って dat を取得する。
    /// <paramref name="stateHint"/> はスレ一覧側で表示していたマーク状態 (Dropped 等) を引き継ぐためのヒント。
    /// dat 落ちスレを開く時、HTTP 取得が 404 になっても初期状態を Dropped に保ち、
    /// 取得失敗時にも (ローカル dat があれば) タイトルとマーク色を維持する。</summary>
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

        var tab = new ThreadTabViewModel(
            board, info,
            closeCallback:          t => ThreadTabs.Remove(t),
            deleteCallback:         t => DeleteThreadLog(t),
            refreshCallback:        t => _ = RefreshThreadAsync(t),
            addToFavoritesCallback: t => ToggleThreadFavorite(t),
            writeCallback:          t => OpenPostDialog(t));

        // Phase 11: 設定の「標準の表示モード」を新規タブの初期値に
        tab.ViewMode = CurrentConfig.DefaultThreadViewMode switch
        {
            "Tree"      => ThreadViewMode.Tree,
            "DedupTree" => ThreadViewMode.DedupTree,
            _           => ThreadViewMode.Flat,
        };

        // 既読位置があれば渡しておく (描画後に JS が該当レスへスクロール)
        var savedIndex = _threadIndex.Load(board.Host, board.DirectoryName, info.Key);
        if (savedIndex?.LastReadPostNumber is int savedPos)
            tab.ScrollTargetPostNumber = savedPos;
        // 「ここまで読んだ」帯の対象 (Phase 19)
        if (savedIndex?.LastReadMarkPostNumber is int savedMark)
            tab.ReadMarkPostNumber = savedMark;

        // ★ ボタンの押下表示を初期化 (既にお気に入り登録済みのスレを開いた場合)
        tab.IsFavorited = Favorites.IsThreadFavorited(board.Host, board.DirectoryName, info.Key);

        // タブのマーク色は、スレ一覧から渡されたヒントを尊重 (dat 落ちなら茶)
        tab.State = stateHint ?? LogMarkState.Cached;

        ThreadTabs.Add(tab);
        SelectedThreadTab = tab;

        try
        {
            tab.IsBusy = true;

            // ---- Step 1: ディスクにキャッシュ済の dat があれば先に表示する ----
            // HTTP roundtrip (TCP/SSL handshake + Range リクエスト + サーバ応答) を待たず、
            // 「ファイル読み + パース」だけで描画を開始するので、既存スレの再オープンが体感的に瞬時になる。
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
            // ローカル無し: streaming で逐次表示 (初回取得)
            // ローカル有り: noop progress で全取得 → 結果から差分計算 → 新規分のみ append
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
                    // dat が縮んだ (あぼーん編集等) — 既に表示中の方が多い。今は警告のみ、
                    // 整合性が必要なら後でリフレッシュボタン経由で全置換する余地あり。
                    StatusMessage = $"{info.Title}: dat 縮小 ({prevCount} → {result.Posts.Count})";
                }

                tab.DatSize = result.DatSize;
            }

            // 取得時のレス件数を idx.json に記録 (次回 subject.txt と比較して新着判定するため)
            var existingIdx = _threadIndex.Load(board.Host, board.DirectoryName, info.Key);
            var updatedIdx  = (existingIdx ?? new ThreadIndex(null, null))
                with { LastFetchedPostCount = result.Posts.Count };
            _threadIndex.Save(board.Host, board.DirectoryName, info.Key, updatedIdx);

            // スレ一覧の対応行を「Cached (青)」マークに戻す (新着があれば緑だったのを上書き)。
            // ただし dat 落ちヒントで開いたスレは、HTTP が成功しても (= サーバが復活していても)
            // 一覧側では引き続き Dropped 表示のままで構わないので通知しない。
            if (stateHint != LogMarkState.Dropped)
            {
                NotifyThreadListLogMark(board, info.Key, LogMarkState.Cached);
                tab.State = LogMarkState.Cached;
            }
        }
        catch (Exception ex)
        {
            // ローカル dat が表示できていれば、タイトル/状態色は維持して状況だけステータス通知。
            // ローカルすら無ければ従来通り「(取得失敗)」をタブ見出しに出す。
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

    /// <summary>
    /// スレ更新ボタンから呼ばれる、もしくはスレ一覧で開いているスレを再クリックされた時に呼ばれる。
    /// HTTP Range で差分のみ取得して、増分レスを JS に append する。
    /// </summary>
    public async Task RefreshThreadAsync(ThreadTabViewModel tab)
    {
        if (tab.IsBusy) return; // 二重起動ガード

        var prevCount = tab.Posts.Count;
        try
        {
            tab.IsBusy    = true;
            StatusMessage = $"{tab.Header} を更新中...";

            // streaming progress は使わず最終 result で差分計算する (DOM 二重 push を避けるため)
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
                // dat が縮んだ (あぼーん編集等)。タブ内容と乖離するので警告のみ。
                StatusMessage = $"{tab.Header}: dat 縮小 ({prevCount} → {result.Posts.Count})";
            }

            tab.DatSize = result.DatSize;

            // idx.json の取得済み件数を更新 (新着判定をリセット)
            var existingIdx = _threadIndex.Load(tab.Board.Host, tab.Board.DirectoryName, tab.ThreadKey);
            var updatedIdx  = (existingIdx ?? new ThreadIndex(null, null))
                with { LastFetchedPostCount = result.Posts.Count };
            _threadIndex.Save(tab.Board.Host, tab.Board.DirectoryName, tab.ThreadKey, updatedIdx);

            // スレ一覧の対応行とタブの状態マークを緑→青に戻す
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

    /// <summary>スレ表示タブの「書き込み」ボタンから呼ばれる。投稿ダイアログ (PostDialog) を開き、
    /// 送信成功時はそのスレの差分取得を走らせて新規投稿を表示に取り込む。
    /// ダイアログ生成は <see cref="ChBrowser.Views.PostDialog"/> に依存するが、ここから直接 new するのは
    /// MVVM 上はやや距離があるものの、本アプリは Views/InputDialog の前例があり、別途 Service レイヤーを
    /// 用意するほどでも無いので簡潔さ優先で構わない。</summary>
    /// <summary>投稿ダイアログをモードレスで開く。本体は操作可能のままで、Owner 関係により
    /// ダイアログは本体の前面に固定される。完了は <see cref="ChBrowser.Views.PostDialog.Closed"/>
    /// で待ち、送信成功なら差分取得を走らせる。</summary>
    private void OpenPostDialog(ThreadTabViewModel tab)
    {
        var vm  = new PostFormViewModel(_postClient, tab.Board, tab.ThreadKey, tab.Title);
        var dlg = new ChBrowser.Views.PostDialog(vm, System.Windows.Application.Current?.MainWindow);
        dlg.Closed += async (_, _) =>
        {
            if (!dlg.WasSubmitted) return;
            UpdateDonguriStatus();
            await RefreshThreadAsync(tab).ConfigureAwait(true);
        };
        dlg.Show();
    }

    /// <summary>選択中の板スレ一覧タブから「新規スレ立て」ボタンで呼ばれる (Phase 8c)。
    /// PostFormViewModel をスレ立てモードでモードレスに開き、成功時は subject.txt を再取得して
    /// 新スレが一覧に出るようにする。本体ウィンドウは操作可能のまま。</summary>
    public void OpenNewThreadDialog()
    {
        var listTab = SelectedThreadListTab;
        if (listTab?.Board is null)
        {
            StatusMessage = "新規スレ立ては板タブで実行してください";
            return;
        }
        var board = listTab.Board;
        var vm    = new PostFormViewModel(_postClient, board);
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
    /// お気に入りに登録されていた場合は同時に外す (= dat が無くなったスレをお気に入りに残しても開けないため)。</summary>
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

        // お気に入り登録があれば外す (★ 表示の即時反映 + favorites.json への永続化を含む)
        var favEntry      = Favorites.FindThread(tab.Board.Host, tab.Board.DirectoryName, tab.ThreadKey);
        var alsoUnfaved   = favEntry is not null;
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

        // ThreadTabViewModel の Board は非 null (スレ表示タブは必ず Board を持つ)。
        var tab = ThreadTabs.FirstOrDefault(t =>
            t.Board.Host          == board.Host &&
            t.Board.DirectoryName == board.DirectoryName &&
            t.ThreadKey           == threadKey);
        if (tab is not null) tab.ScrollTargetPostNumber = topPostNumber;
    }

    /// <summary>JS からの「ここまで読んだ」位置通知 (Phase 19)。
    /// 値は減少しない (= 既存値より大きい場合のみ更新)、idx.json に永続化 + 該当タブの ReadMarkPostNumber を更新。</summary>
    public void UpdateReadMark(Board board, string threadKey, int postNumber)
    {
        var existing = _threadIndex.Load(board.Host, board.DirectoryName, threadKey);
        var prev     = existing?.LastReadMarkPostNumber ?? 0;
        if (postNumber <= prev) return; // never decrease

        var updated = (existing ?? new ThreadIndex(null, null)) with { LastReadMarkPostNumber = postNumber };
        _threadIndex.Save(board.Host, board.DirectoryName, threadKey, updated);

        var tab = ThreadTabs.FirstOrDefault(t =>
            t.Board.Host          == board.Host &&
            t.Board.DirectoryName == board.DirectoryName &&
            t.ThreadKey           == threadKey);
        if (tab is not null) tab.ReadMarkPostNumber = postNumber;
    }

    /// <summary>指定スレッドのマーク状態変化を、開いている全スレ一覧タブにブロードキャストする (Phase 18 でリファクタ)。
    /// <list type="bullet">
    /// <item><description>板タブ (Board != null) — Board.Host / DirectoryName が一致するときに更新</description></item>
    /// <item><description>集約タブ (お気に入り「板として開く」/ 全ログ、Board == null) — Items に該当 (host, dir, key) が
    /// あるときだけ更新 (= 対象が含まれない集約タブには通知しない)</description></item>
    /// </list>
    /// 各タブは行単位で増分通知 (`SetLogMark`) を受け、JS 側で <c>tr[data-host=][data-dir=][data-key=]</c> で
    /// 一意な行を特定してマークを差し替える (= 再描画なし)。</summary>
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

    // ---------------------------------------------------------------------
    // Phase 7: お気に入り
    // ---------------------------------------------------------------------

    /// <summary>(host, directoryName) から <see cref="Board"/> を解決する。
    /// 板一覧 (bbsmenu.json) に登録があればそのオブジェクトを、無ければ最低限の Board を組み立てて返す
    /// (お気に入りに登録した板/スレが、bbsmenu.json から消えても開けるようにするため)。</summary>
    public Board ResolveBoard(string host, string directoryName, string fallbackBoardName)
    {
        foreach (var cat in BoardCategories)
            foreach (var bvm in cat.Boards)
                if (bvm.Board.Host == host && bvm.Board.DirectoryName == directoryName)
                    return bvm.Board;

        return new Board(
            DirectoryName: directoryName,
            BoardName:     string.IsNullOrEmpty(fallbackBoardName) ? directoryName : fallbackBoardName,
            Url:           $"https://{host}/{directoryName}/",
            CategoryName:  "",
            CategoryOrder: 0);
    }

    /// <summary>JS の openThread メッセージ (host/dir/key/title 同梱) からスレを開く。
    /// 通常の板タブ・お気に入りディレクトリ展開タブの両方の経路でこれを呼ぶ。
    /// <paramref name="stateHint"/> は一覧側で表示していたマーク色を引き継ぐためのヒント。</summary>
    public Task OpenThreadFromListAsync(string host, string directoryName, string key, string title, LogMarkState? stateHint = null)
    {
        var board = ResolveBoard(host, directoryName, "");
        var info  = new ThreadInfo(key, title, 0, 0); // PostCount/Order は dat 取得後に意味を持たない
        return OpenThreadAsync(board, info, stateHint);
    }

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
        // 板お気に入り変更でも、関連スレタブの「板」表示には影響しないが将来用に統一して呼ぶ
        RefreshFavoritedStateOfAllTabs();
    }

    /// <summary>スレ ★ ボタン押下: 既に登録済みなら外す、未登録なら追加する (トグル)。
    /// 操作後に <see cref="RefreshFavoritedStateOfAllTabs"/> で全タブの ★ 表示を更新。</summary>
    public void ToggleThreadFavorite(ThreadTabViewModel tab)
    {
        var b = tab.Board;
        var existing = Favorites.FindThread(b.Host, b.DirectoryName, tab.ThreadKey);
        if (existing is not null)
        {
            Favorites.Remove(existing);
            StatusMessage = $"{tab.Title} をお気に入りから外しました";
        }
        else
        {
            Favorites.AddRoot(new FavoriteThread
            {
                Host          = b.Host,
                DirectoryName = b.DirectoryName,
                ThreadKey     = tab.ThreadKey,
                Title         = tab.Title,
                BoardName     = b.BoardName,
            });
            StatusMessage = $"{tab.Title} をお気に入りに追加しました";
        }
        RefreshFavoritedStateOfAllTabs();
    }

    /// <summary>お気に入り状態の変化を、開いている全タブにリアルタイム反映する (Phase 18+)。
    /// <list type="bullet">
    /// <item><description>ThreadTab: ★ ボタンの押下表示 (<see cref="ThreadTabViewModel.IsFavorited"/>) を同期</description></item>
    /// <item><description>ThreadListTab: 各行の is-favorited クラス (= ★ 背景) を増分通知で toggle</description></item>
    /// </list>
    /// お気に入りトグル / 削除 / 新規追加 のたびに呼ぶ。</summary>
    public void RefreshFavoritedStateOfAllTabs()
    {
        var favKeys = Favorites.CollectFavoriteThreadKeys();

        // ThreadTab: ★ ボタン
        foreach (var tab in ThreadTabs)
        {
            var b = tab.Board;
            tab.IsFavorited = favKeys.Contains((b.Host, b.DirectoryName, tab.ThreadKey));
        }

        // ThreadListTab: 各行の is-favorited クラス (増分 patch + Items snapshot 同期は VM 側で完結)
        foreach (var listTab in ThreadListTabs)
            listTab.SyncFavoritedFromKeySet(favKeys);
    }

    /// <summary>D&amp;D による移動を実行。target が null なら root 末尾、folder なら配下、それ以外は target の直後に。</summary>
    public void MoveFavoriteEntry(FavoriteEntryViewModel source, FavoriteEntryViewModel? target)
    {
        if (!Favorites.CanMove(source, target)) return;
        Favorites.Move(source, target);
        RefreshFavoritedStateOfAllTabs(); // 移動だけでは状態は変わらないが念のため
    }

    // ---------- Phase 14b: WebView 化したお気に入りペインとの橋渡し ----------

    /// <summary>HTML 再生成。Favorites.Changed 発火のたびに呼ばれる。</summary>
    public void RefreshFavoritesHtml()
        => FavoritesHtml = ChBrowser.Services.Render.FavoritesHtmlBuilder.Build(Favorites.Items);

    // ---------- Phase 11d: 「すべての CSS を再読み込み」用 ----------

    /// <summary>ThemeService のディスクキャッシュ + 各 HtmlBuilder のシェル HTML キャッシュを破棄し、
    /// 3 ペイン (お気に入り / 板一覧 / スレ一覧) の HTML を再生成して即時反映させる。
    /// 設定画面の「すべての CSS を再読み込み」ボタンから呼ぶ。
    /// スレ表示 / ビューアは対象外 (= 開き直しが必要、UI で案内)。</summary>
    public void ReloadAllPaneCss(ChBrowser.Services.Theme.ThemeService theme)
    {
        // 1. ThemeService の disk キャッシュをクリア (= 次の LoadCss でディスク再読込)
        theme.InvalidateCache();
        // 2. 各 HtmlBuilder のシェル HTML キャッシュをクリア
        ChBrowser.Services.Render.FavoritesHtmlBuilder.InvalidateCache();
        ChBrowser.Services.Render.BoardListHtmlBuilder.InvalidateCache();
        ChBrowser.Services.Render.ThreadListHtmlBuilder.InvalidateCache();
        // 3. WebView2Helper のスレ表示 / ビューアシェル HTML キャッシュをクリア
        //    → タブを閉じて開き直したとき、新規 WebView2 は新 CSS でシェルを再構築する
        ChBrowser.Controls.WebView2Helper.InvalidateShellCaches();
        // 4. ペイン HTML を再生成 → Html 添付プロパティ経由で WebView2 に再ナビ
        RefreshFavoritesHtml();
        RefreshBoardListHtml();
        // スレ一覧タブはタブごとに Items を再構築 (今表示中の items を使ってリビルド)
        var now = DateTimeOffset.UtcNow;
        foreach (var tab in ThreadListTabs)
        {
            // SetItems は Html プロパティを再設定するので WebView2 が再ナビゲートされる
            tab.SetItems(tab.Items, now);
        }
        StatusMessage = "CSS を再読み込みしました (スレ表示タブは開き直し必要)";
    }

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
    /// HTML を再生成しない (DOM 上で details の open はトグル済み、ViewModel は次回再生成時の出し分け用に保持)。</summary>
    public void SetFolderExpanded(Guid id, bool expanded)
    {
        if (Favorites.FindById(id) is FavoriteFolderViewModel folder)
            folder.IsExpanded = expanded;
    }

    /// <summary>JS の moveFavorite メッセージから呼ばれる。
    /// position が 'inside' ならフォルダ配下に、'before'/'after' なら兄弟として前後に挿入。
    /// position が 'rootEnd' (空エリアにドロップ) なら root 末尾。
    /// 循環防止は <see cref="FavoritesViewModel.CanReparent"/> で各分岐の前に検証 (= バグ #260 の対策)。</summary>
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
    /// 板はそのまま板タブ、スレはそのままスレタブで開く。
    /// 既存タブがあればアクティブ化されるだけなので重複オープンにはならない。</summary>
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

    /// <summary>「板として開く」: お気に入り全体 (仮想ルート) を 1 つの統合スレ一覧タブで開く
    /// (= 既存 <see cref="OpenFavoritesFolderAsync"/> のルート版)。タブ識別は <c>Guid.Empty</c>。</summary>
    public Task OpenAllRootAsBoardAsync()
        => OpenFavoritesAggregateTabAsync(Guid.Empty, "お気に入り", Favorites.Items);

    /// <summary>「全ログ」: ローカルに dat を持っている全スレを 1 枚のスレ一覧として表示する (Phase 18)。
    /// 既存タブがあればアクティブ化、無ければ新規タブを生成して中身を構築する。
    /// 中身の組み立ては <see cref="BuildAllLogsItems"/> を共有する (= リフレッシュ時もここを通る)。
    /// タブ識別 (<see cref="ThreadListTabViewModel.FavoritesFolderId"/> 流用) は固定 sentinel。</summary>
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

                // BoardCategories から (rootDomain, dirName) で正規 Board を引く。
                // 板が bbsmenu に無くなったケースでは fallback Board を作る (Host = rootDomain)。
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
                        // subject.txt に無い (= 落ちた or subject 未取得) — dat 1 行目から title を抽出
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

    /// <summary>「全ログ」タブ識別用の固定 Guid (= <see cref="Guid.Empty"/> はお気に入り仮想ルートが使うので衝突回避)。</summary>
    private static readonly Guid AllLogsTabId = new("ffffffff-ffff-ffff-ffff-fffffffffffe");

    /// <summary>スレ一覧タブの「更新」を共通エントリで dispatch する (Phase 18 リファクタ)。
    /// <list type="bullet">
    /// <item><description>板タブ (<c>Board</c> 付き) → <see cref="LoadThreadListAsync"/> (subject.txt 再取得)</description></item>
    /// <item><description>全ログ → <see cref="RefreshAllLogsTab"/> (ディスク walk 再実行)</description></item>
    /// <item><description>お気に入り集約 (フォルダ / 仮想ルート板として開く) → 元のフォルダ参照を引き戻して再構築</description></item>
    /// </list>
    /// App.xaml.cs の <c>thread_list.refresh</c> ショートカットハンドラから呼ばれる。</summary>
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

    /// <summary>お気に入りフォルダを開いてその中身を 1 枚のスレ一覧として表示する。
    /// フォルダ内のすべての board/thread を再帰収集し、各 board の subject.txt を取得して
    /// 統合した <see cref="ThreadListItem"/> 列を作る。subject.txt から消えたお気に入りスレは
    /// <see cref="LogMarkState.Dropped"/> (茶) でマーク。
    ///
    /// 注意: フォルダの内容は <c>folderVm.Children</c> (ObservableCollection) を walk する。
    /// <c>folderVm.Model.Children</c> はロード時のスナップショットで D&amp;D 等の在memory編集を反映しないため使わない。</summary>
    public Task OpenFavoritesFolderAsync(FavoriteFolderViewModel folderVm)
        => OpenFavoritesAggregateTabAsync(folderVm.Model.Id, folderVm.DisplayName, folderVm.Children);

    /// <summary>お気に入りフォルダ (or 仮想ルート) の中身を 1 枚のスレ一覧として表示する共通実装。
    /// <paramref name="aggregateId"/> はタブ識別用 (フォルダなら <c>FavoriteFolder.Id</c>、仮想ルートなら <c>Guid.Empty</c>)。
    /// 既存タブがあればアクティブ化、無ければ新規タブを作って中身を <see cref="BuildFavoritesAggregateItemsAsync"/> で構築する。</summary>
    private Task OpenFavoritesAggregateTabAsync(
        Guid aggregateId,
        string aggregateName,
        IEnumerable<FavoriteEntryViewModel> children)
    {
        // 既存タブがあればアクティブ化するだけ
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
    /// items を再構築する (= subject.txt 再取得 → Dropped / 新着判定 → タブに反映)。
    /// 新規オープン時もリフレッシュ時も同じ経路を通る。</summary>
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
            // 「板として開く」では fav board エントリは展開せず、登録スレだけを表示する仕様 (= 板の中身を引かない)。
            var boards  = new List<FavoriteBoard>();
            var threads = new List<FavoriteThread>();
            foreach (var child in children)
                CollectFavoriteEntriesFromVm(child, boards, threads);

            // 登録スレの出元板の subject.txt のみ取得 (新着 / Dropped 判定に必要)。
            // 同じ板に複数スレ登録があっても 1 回だけフェッチ。
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

            // 登録スレを subject.txt と突き合わせて並べる。subject.txt に無ければ Dropped。
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

    /// <summary>VM ツリーを再帰的に走査して board/thread エントリを収集する (フォルダ自身は無視)。
    /// VM 側 (<c>ObservableCollection</c>) を見るので、D&amp;D / 新規フォルダ等の在memory編集が即座に反映される。
    /// 永続化前のスナップショット (<c>FavoriteEntry.Children</c>) は使わない。</summary>
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

    // ---- お気に入りペインの編集操作 (Phase 7 セッション 3) ----

    /// <summary>新規フォルダを <paramref name="parent"/> 直下 (null ならルート直下) に作成する。</summary>
    public void NewFavoriteFolder(FavoriteFolderViewModel? parent, string name)
    {
        var folder = new FavoriteFolder { Name = name };
        if (parent is null) Favorites.AddRoot(folder);
        else                Favorites.AddInto(parent, folder);
        StatusMessage = $"フォルダ「{name}」を作成しました";
    }

    /// <summary>フォルダ名を変更して保存。</summary>
    public void RenameFavoriteFolder(FavoriteFolderViewModel folder, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || folder.Name == newName) return;
        folder.Name = newName;
        Favorites.Save();
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

    /// <summary>「お気に入りチェック」: お気に入り登録スレの subject.txt を確認し、
    /// 新着があるスレ (= subject.txt の post 数 &gt; idx.json の lastFetchedPostCount) を新規タブで開く。
    /// subject.txt から消えた (= 落ちた) スレは除外。
    /// subject.txt 取得 / dat 取得の通信本数は合算で <see cref="AppConfig.BatchConcurrency"/> までに制限し、
    /// 進捗は <see cref="StatusMessage"/> で表示する。</summary>
    public async Task CheckFavoritesAsync()
    {
        // 1. お気に入り (フォルダ含む) を再帰展開
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

        // 2. 出元板 (= 登録スレが属する板) のユニーク集合を作る。fav board は新仕様では展開しないので無視。
        var resolved = new Dictionary<(string, string), Board>();
        foreach (var ft in threads)
        {
            var key = (ft.Host, ft.DirectoryName);
            if (resolved.ContainsKey(key)) continue;
            resolved[key] = ResolveBoard(ft.Host, ft.DirectoryName, ft.BoardName);
        }

        // バッチ全体で 1 つの semaphore を共有。subject.txt 取得 (Phase 3) と dat 取得 (Phase 5)
        // が同時に走っても合算で BatchConcurrency 本までしか動かない。
        var concurrency = Math.Max(1, CurrentConfig.BatchConcurrency);
        using var sem   = new SemaphoreSlim(concurrency, concurrency);
        var dispatcher  = System.Windows.Application.Current?.Dispatcher;

        // ---- Phase 3: subject.txt を並列取得 ----
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

        // ---- Phase 4: 新着判定。落ちた (subject.txt に無い) スレはスキップ。 ----
        var toOpen = new List<(Board board, ThreadInfo info)>();
        foreach (var ft in threads)
        {
            var key = (ft.Host, ft.DirectoryName);
            if (!subjects.TryGetValue(key, out var infos)) continue;
            if (!resolved.TryGetValue(key, out var board)) continue;

            var info = infos.FirstOrDefault(t => t.Key == ft.ThreadKey);
            if (info is null) continue; // 落ちた → 新仕様では除外

            var idx  = _threadIndex.Load(ft.Host, ft.DirectoryName, ft.ThreadKey);
            var prev = idx?.LastFetchedPostCount ?? 0;
            if (info.PostCount > prev) toOpen.Add((board, info));
        }

        if (toOpen.Count == 0)
        {
            StatusMessage = "お気に入りチェック完了: 新着なし";
            return;
        }

        // ---- Phase 5: dat を並列取得 (同じ semaphore を共有)。
        //      取得本体だけを semaphore 配下で実行し、UI 反映 (タブ作成 / Posts 反映) は完了順に逐次行う。 ----
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

        // 結果を完了順ではなく開始順 (toOpen 順) に処理。早く完了したタスクは await が即座に戻るので
        // トータル時間は max(各タスク) に支配され、順次 await でも並列性能を損なわない。
        var i = 0;
        foreach (var t in datTasks)
        {
            i++;
            StatusMessage = $"お気に入りチェック: 更新スレ取得 {i}/{totalUpdate} (完了 {datDone}/{totalUpdate})";
            var (board, info, result, error) = await t.ConfigureAwait(true);
            if (result is null) continue;
            try { OpenThreadFromPrefetched(board, info, result); }
            catch (Exception ex) { Debug.WriteLine($"[Favorites] open prefetched failed: {ex.Message}"); }
        }

        StatusMessage = $"お気に入りチェック完了: 更新 {toOpen.Count} 件";
    }

    /// <summary>事前に取得済みの <see cref="DatFetchResult"/> からスレタブを作成 / 既存タブに反映する。
    /// HTTP は呼ばない (= 並列 fetch 済の結果を流し込むだけ)。
    /// <see cref="OpenThreadAsync"/> の「タブ作成 + idx 更新 + log mark 通知」部分を切り出した版。</summary>
    private void OpenThreadFromPrefetched(Board board, ThreadInfo info, DatFetchResult result)
    {
        // 既存タブがあれば差分を append して終わり
        foreach (var existing in ThreadTabs)
        {
            if (existing.Board.Host          == board.Host &&
                existing.Board.DirectoryName == board.DirectoryName &&
                existing.ThreadKey           == info.Key)
            {
                var prevCount = existing.Posts.Count;
                if (result.Posts.Count > prevCount)
                {
                    var added = new List<Post>(result.Posts.Count - prevCount);
                    for (var n = prevCount; n < result.Posts.Count; n++) added.Add(result.Posts[n]);
                    // お気に入りチェックでの既存タブ差分追加なので incremental (Phase 20)
                    AppendPostsWithNg(existing, added, isIncremental: true);
                }
                existing.DatSize = result.DatSize;

                var idx0 = (_threadIndex.Load(board.Host, board.DirectoryName, info.Key) ?? new ThreadIndex(null, null))
                    with { LastFetchedPostCount = result.Posts.Count };
                _threadIndex.Save(board.Host, board.DirectoryName, info.Key, idx0);

                NotifyThreadListLogMark(board, info.Key, LogMarkState.Cached);
                existing.State = LogMarkState.Cached;
                return;
            }
        }

        // 新規タブ作成 (= OpenThreadAsync 前半部と同等)
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

        var savedIndex = _threadIndex.Load(board.Host, board.DirectoryName, info.Key);
        if (savedIndex?.LastReadPostNumber is int savedPos)
            tab.ScrollTargetPostNumber = savedPos;
        if (savedIndex?.LastReadMarkPostNumber is int savedMark)
            tab.ReadMarkPostNumber = savedMark;

        tab.IsFavorited = Favorites.IsThreadFavorited(board.Host, board.DirectoryName, info.Key);
        tab.State       = LogMarkState.Cached;

        ThreadTabs.Add(tab);
        SelectedThreadTab = tab;

        AppendPostsWithNg(tab, result.Posts);
        tab.DatSize = result.DatSize;

        var idx = (_threadIndex.Load(board.Host, board.DirectoryName, info.Key) ?? new ThreadIndex(null, null))
            with { LastFetchedPostCount = result.Posts.Count };
        _threadIndex.Save(board.Host, board.DirectoryName, info.Key, idx);

        NotifyThreadListLogMark(board, info.Key, LogMarkState.Cached);
    }
}
