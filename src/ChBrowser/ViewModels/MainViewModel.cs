using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using ChBrowser.Models;
using ChBrowser.Services.Api;
using ChBrowser.Services.Donguri;
using ChBrowser.Services.Storage;
using ChBrowser.Services.Url;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChBrowser.ViewModels;

/// <summary>メインウィンドウの ViewModel。
/// 機能領域別に partial に分割している:
/// <list type="bullet">
/// <item><description><see cref="MainViewModel"/> (本ファイル): ctor / 共通フィールド / アドレスバー / どんぐり / NG / AppConfig 適用</description></item>
/// <item><description><c>MainViewModel.BoardList.cs</c>: 板一覧 (bbsmenu) 取得・選択</description></item>
/// <item><description><c>MainViewModel.ThreadList.cs</c>: スレ一覧タブ (板タブ / 全ログ / お気に入り集約) の構築・更新</description></item>
/// <item><description><c>MainViewModel.Threads.cs</c>: スレ表示タブの生成・取得・更新・削除</description></item>
/// <item><description><c>MainViewModel.Tabs.cs</c>: タブの close-others/before/after / 設定駆動アクション dispatch</description></item>
/// <item><description><c>MainViewModel.Favorites.cs</c>: お気に入り (folder/board/thread) 編集・展開・チェック</description></item>
/// </list></summary>
public sealed partial class MainViewModel : ObservableObject
{
    // ----- サービス参照 (ctor で受け取る) -----
    private readonly BbsmenuClient                    _bbsmenuClient;
    private readonly SubjectTxtClient                 _subjectClient;
    private readonly SettingTxtClient                 _settingClient;
    private readonly DatClient                        _datClient;
    private readonly ThreadIndexService               _threadIndex;
    private readonly PostClient                       _postClient;
    private readonly DonguriService                   _donguri;
    private readonly DispatcherTimer                  _donguriTimer;
    private readonly ChBrowser.Services.Ng.NgService  _ng;
    private readonly DataPaths                        _paths;
    private readonly OpenTabsStorage               _openTabsStorage;
    /// <summary>LLM 連携クライアント。スレッドの「AI」ボタンからチャットウィンドウを開く際に使う。</summary>
    private readonly ChBrowser.Services.Llm.LlmClient _llmClient;

    // ----- ペイン用コレクション -----
    public ObservableCollection<BoardCategoryViewModel> BoardCategories  { get; } = new();
    public ObservableCollection<ThreadListTabViewModel> ThreadListTabs   { get; } = new();
    public ObservableCollection<ThreadTabViewModel>     ThreadTabs       { get; } = new();

    /// <summary>お気に入りペイン (TreeView) のルート。Phase 7 で導入。</summary>
    public FavoritesViewModel Favorites { get; }

    // ----- ステータスバー / 進捗 -----
    [ObservableProperty] private string _statusMessage = "準備完了";
    [ObservableProperty] private bool   _isBusy;

    /// <summary>ログウィンドウ (= 表示メニュー → ログペイン) の表示状態。
    /// MainWindow.xaml.cs が値変化を観測して別ウィンドウ <see cref="ChBrowser.Views.LogWindow"/> を show / hide する。
    /// 起動直後は閉じた状態 (= 必要なときだけメニューから開く)。</summary>
    [ObservableProperty] private bool _isLogPaneVisible;

    /// <summary>StatusMessage が変わるたびに <see cref="ChBrowser.Services.Logging.LogService"/> へ転記。
    /// これにより一時的にステータスバーに出て消える短いメッセージも、ログペインで時系列に追跡できる。</summary>
    partial void OnStatusMessageChanged(string value)
        => ChBrowser.Services.Logging.LogService.Instance.Write(value);

    /// <summary>あぼーん件数のステータスバー表示。SelectedThreadTab の hidden 数を表示。</summary>
    [ObservableProperty] private string _aboneStatus = "あぼーん 0";

    /// <summary>選択中スレタブの dat サイズ (KB) のステータスバー表示。
    /// SelectedThreadTab 切替時 / そのタブの DatSize 更新時に <see cref="OnSelectedThreadTabChanged"/> /
    /// <see cref="OnActiveThreadTabPropertyChanged"/> から書き換える。タブ未選択時は空文字。</summary>
    [ObservableProperty] private string _datSizeStatus = "";

    /// <summary>ステータスバーに出すどんぐり (acorn) の状態テキスト。30 秒ごとに更新。</summary>
    [ObservableProperty] private string _donguriStatus = "🌰 未取得";

    /// <summary>どんぐりメール認証のログイン状態 (= 設定にメアド有 + 起動時 / 設定変更時に試行)。
    /// 設定でメアドが空なら "" (= ステータスバーに表示しない)。</summary>
    [ObservableProperty] private string _donguriLoginStatus = "";

    /// <summary>マウスジェスチャー入力中のリアルタイム表示 (Phase 16+)。</summary>
    [ObservableProperty] private string _gestureStatus = "";

    /// <summary>「上ボタン」フォルダ (= お気に入りルート直下、名前 "上ボタン") の直下エントリ。
    /// MainWindow 上部のブックマークバー風ペインに表示する (Chrome の bookmark bar 相当)。
    /// Favorites.Changed で再構築。フォルダなし / 子なしなら空 + バー非表示。</summary>
    public System.Collections.ObjectModel.ObservableCollection<FavoriteEntryViewModel> TopButtonsItems { get; } = new();

    /// <summary>「上ボタン」バーを表示するか。<see cref="TopButtonsItems"/> が空なら false。</summary>
    [ObservableProperty] private bool _isTopButtonsBarVisible;

    /// <summary>「上ボタン」フォルダの判定名 (= お気に入りに作るとブックマークバーに自動載録される)。
    /// 実体は <see cref="ChBrowser.Models.FavoriteDefaults.TopButtonsFolderName"/> で、ストレージ側 (初期化時の
    /// デフォルトお気に入り生成) と共有している。</summary>
    public const string TopButtonsFolderName = ChBrowser.Models.FavoriteDefaults.TopButtonsFolderName;

    /// <summary>スレ一覧タブの計算済み幅 (px)。AppConfig の WidthMode に応じて文字数 × 概算 px か、px 値を採用。
    /// XAML 側の TabItem.Width に bind される。</summary>
    [ObservableProperty] private double _threadListTabWidth = 200;

    /// <summary>スレッドタブの計算済み幅 (px)。同上。</summary>
    [ObservableProperty] private double _threadTabWidth     = 200;

    /// <summary>「文字数指定」の文字を px に概算する係数。OS 既定 UI フォントの平均的な日本語 1 文字幅 (px)。
    /// 厳密値ではなく目安。タブには ×ボタン + アイコン + パディング分の固定オフセット (TabPaddingPx) を加算する。</summary>
    private const double CharToPxRatio = 14;
    private const double TabPaddingPx  = 28; // ×ボタン (16) + マージン + 内側パディング

    // ----- WebView2 ペインへ push する HTML / JSON -----

    /// <summary>板一覧 WebView2 にバインドする HTML (Phase 14a)。</summary>
    [ObservableProperty] private string _boardListHtml = "";

    /// <summary>お気に入りペイン WebView2 にバインドする HTML (Phase 14b)。</summary>
    [ObservableProperty] private string _favoritesHtml = "";

    /// <summary>スレ表示 WebView2 に setConfig メッセージとして送る JSON (Phase 11)。</summary>
    [ObservableProperty] private string _threadConfigJson = "";

    /// <summary>スレ表示 WebView へ push するショートカット bind 一覧 (Phase 16)。</summary>
    [ObservableProperty] private string _threadShortcutsJson = "";

    [ObservableProperty] private string _threadListShortcutsJson = "";
    [ObservableProperty] private string _favoritesShortcutsJson  = "";
    [ObservableProperty] private string _boardListShortcutsJson  = "";

    // Phase 11b: 3 ペインの 1 クリック設定。
    [ObservableProperty] private string _favoritesConfigJson  = "";
    [ObservableProperty] private string _boardListConfigJson  = "";
    [ObservableProperty] private string _threadListConfigJson = "";

    /// <summary>お気に入りペインの絞り込み検索クエリ。空欄でフィルタ解除。
    /// WebView2.PaneSearchPush で JS に push され、マッチしないエントリは非表示 + マッチ箇所をハイライト。</summary>
    [ObservableProperty] private string _favoritesSearchQuery = "";

    /// <summary>板一覧ペインの絞り込み検索クエリ。空欄でフィルタ解除。
    /// WebView2.PaneSearchPush で JS に push され、マッチしない板は非表示 + マッチ箇所をハイライト。</summary>
    [ObservableProperty] private string _boardListSearchQuery = "";

    // ----- アクティブタブ -----
    [ObservableProperty] private ThreadListTabViewModel? _selectedThreadListTab;
    [ObservableProperty] private ThreadTabViewModel?     _selectedThreadTab;

    // ステータスバー (= MainViewModel.StatusMessage) はアクティブペイン (Thread / ThreadList) の選択タブの
    // StatusMessage と同期する。タブを切り替えると過去のメッセージが復元され、ペインを切り替えても
    // そのペインの選択タブの最後のメッセージが見える。
    /// <summary>現在 PropertyChanged を購読しているスレッドタブ。SelectedThreadTab 変更で付け替え。</summary>
    private ThreadTabViewModel? _statusListenerThreadTab;
    /// <summary>現在 PropertyChanged を購読しているスレ一覧タブ。SelectedThreadListTab 変更で付け替え。</summary>
    private ThreadListTabViewModel? _statusListenerThreadListTab;

    partial void OnSelectedThreadTabChanged(ThreadTabViewModel? value)
    {
        // 各タブの WebView2 は専属インスタンス (TabControl ではなく ItemsControl で並列描画)。
        // Visibility 切替で選択タブだけ可視にするため、IsSelected を更新する。
        foreach (var t in ThreadTabs) t.IsSelected = ReferenceEquals(t, value);
        // ステータスバーの「あぼーん N」「dat サイズ」を選択タブのものに更新
        AboneStatus    = value is null ? "あぼーん 0" : $"あぼーん {value.HiddenCount}";
        DatSizeStatus  = value is null ? ""           : FormatDatSize(value.DatSize);

        // 旧タブ購読を解除 → 新タブ購読
        if (_statusListenerThreadTab is not null)
            _statusListenerThreadTab.PropertyChanged -= OnActiveThreadTabPropertyChanged;
        _statusListenerThreadTab = value;
        if (value is not null)
            value.PropertyChanged += OnActiveThreadTabPropertyChanged;

        // アドレスバーは last-activated wins (Phase 14)
        if (value is not null) _lastActivePane = ActivePane.Thread;
        RefreshAddressBarUrl();
        SyncStatusFromActivePane();
    }

    private void OnActiveThreadTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ThreadTabViewModel.StatusMessage))
            SyncStatusFromActivePane();
        if (e.PropertyName == nameof(ThreadTabViewModel.DatSize) && sender is ThreadTabViewModel tab)
            DatSizeStatus = FormatDatSize(tab.DatSize);
    }

    /// <summary>dat サイズ (バイト) を「N KB」表記に整形。1024 で割って整数 + 桁区切り。</summary>
    private static string FormatDatSize(long bytes) => $"{bytes / 1024:N0} KB";

    private void OnActiveThreadListTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ThreadListTabViewModel.StatusMessage))
            SyncStatusFromActivePane();
    }

    /// <summary>アクティブペインの選択タブの StatusMessage が非空なら <see cref="StatusMessage"/> に反映する。
    /// 空のときは何もしない (= ステータスバーは前の値のまま = 直前のグローバル状態 / 別タブの状態を尊重)。</summary>
    private void SyncStatusFromActivePane()
    {
        string? msg = _lastActivePane switch
        {
            ActivePane.Thread     => SelectedThreadTab?.StatusMessage,
            ActivePane.ThreadList => SelectedThreadListTab?.StatusMessage,
            _                     => null,
        };
        if (!string.IsNullOrEmpty(msg)) StatusMessage = msg;
    }

    partial void OnSelectedThreadListTabChanged(ThreadListTabViewModel? value)
    {
        foreach (var t in ThreadListTabs) t.IsSelected = ReferenceEquals(t, value);

        if (_statusListenerThreadListTab is not null)
            _statusListenerThreadListTab.PropertyChanged -= OnActiveThreadListTabPropertyChanged;
        _statusListenerThreadListTab = value;
        if (value is not null)
            value.PropertyChanged += OnActiveThreadListTabPropertyChanged;

        if (value is not null) _lastActivePane = ActivePane.ThreadList;
        RefreshAddressBarUrl();
        SyncStatusFromActivePane();
    }

    // -----------------------------------------------------------------
    // ジェスチャー進捗
    // -----------------------------------------------------------------

    /// <summary>WPF / WebView ブリッジ両方から呼ばれる。category=null で表示クリア。
    /// <paramref name="matchedActionName"/> が非空なら「→ アクション名」を末尾に付ける
    /// (= 今右クリックを離せば発火するコマンドの予告表示)。</summary>
    public void UpdateGestureStatus(string? category, string? gesture, string? matchedActionName = null)
    {
        if (string.IsNullOrEmpty(category))
        {
            GestureStatus = "";
            return;
        }
        var prefix = string.IsNullOrEmpty(gesture)
            ? $"ジェスチャー [{category}]"
            : $"ジェスチャー [{category}] {gesture}";
        GestureStatus = string.IsNullOrEmpty(matchedActionName)
            ? prefix
            : $"{prefix} : {matchedActionName}";
    }

    // -----------------------------------------------------------------
    // Phase 14: アドレスバー
    // -----------------------------------------------------------------

    private enum ActivePane { None, ThreadList, Thread }
    private ActivePane _lastActivePane = ActivePane.None;

    /// <summary>右ペイン上段「スレ欄」(ThreadListTabs) がアクティブ化された通知。</summary>
    public void MarkThreadListPaneActive()
    {
        _lastActivePane = ActivePane.ThreadList;
        RefreshAddressBarUrl();
        SyncStatusFromActivePane();
    }

    /// <summary>右ペイン下段「スレ表示」(ThreadTabs) がアクティブ化された通知。</summary>
    public void MarkThreadPaneActive()
    {
        _lastActivePane = ActivePane.Thread;
        RefreshAddressBarUrl();
        SyncStatusFromActivePane();
    }

    /// <summary>アドレスバーに表示する現在のタブの URL (= 最後にアクティブ化した方の URL)。</summary>
    [ObservableProperty] private string _addressBarUrl = "";

    /// <summary>アドレスバーをエラー表示 (赤枠) するためのフラグ。</summary>
    [ObservableProperty] private bool _addressBarHasError;

    /// <summary>現在のアクティブタブから AddressBarUrl を再計算する。</summary>
    private void RefreshAddressBarUrl()
    {
        AddressBarHasError = false;
        AddressBarUrl = _lastActivePane switch
        {
            ActivePane.Thread     => SelectedThreadTab?.Url ?? SelectedThreadListTab?.Board?.Url ?? "",
            ActivePane.ThreadList => SelectedThreadListTab?.Board?.Url ?? "",
            _                     => "",
        };
    }

    /// <summary>アドレスバーの Enter で呼ばれる。入力テキストを <see cref="AddressBarParser"/> で解釈し、
    /// 板/スレに応じて開く (既存タブがあればアクティブ化)。</summary>
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
                await OpenThreadByUrlAsync(target.Host, target.Directory, target.ThreadKey, target.PostNumber).ConfigureAwait(true);
                break;
            default:
                AddressBarHasError = true;
                StatusMessage = "認識できない URL です (5ch.io / bbspink.com の板 / スレ URL を入力してください)";
                break;
        }
    }

    /// <summary>板 URL を開く: 既存 ThreadListTab があればアクティブ化、無ければ新規。</summary>
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

    /// <summary>スレ URL を開く: 既存 ThreadTab があればアクティブ化、無ければ新規。
    /// <paramref name="scrollToPostNumber"/> > 0 のとき、開いた直後に JS へ scrollToPost を push する
    /// (= 5ch.io URL に「/&lt;dir&gt;/&lt;key&gt;/&lt;N&gt;」のレス番号が含まれているクリック経路用)。</summary>
    public async Task OpenThreadByUrlAsync(string host, string dir, string key, int scrollToPostNumber = 0)
    {
        var rootIn = DataPaths.ExtractRootDomain(host);
        foreach (var tab in ThreadTabs)
        {
            if (string.Equals(DataPaths.ExtractRootDomain(tab.Board.Host), rootIn, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(tab.Board.DirectoryName, dir, StringComparison.Ordinal) &&
                string.Equals(tab.ThreadKey,           key, StringComparison.Ordinal))
            {
                SelectedThreadTab = tab;
                if (scrollToPostNumber > 0)
                    tab.PendingScrollToPost = new ScrollToPostRequest(scrollToPostNumber);
                return;
            }
        }
        await OpenThreadFromListAsync(host, dir, key, "").ConfigureAwait(true);
        if (scrollToPostNumber > 0)
        {
            // 新タブ生成パス: 上の OpenThreadFromListAsync が OpenThreadAsync を await しているので、
            // ここに来た時点で dat 取得 (= JS への appendPosts 全 batch 投入) は終わっている。
            // SelectedThreadTab が今作ったタブを指しているはずなので、それに対して push する。
            var newTab = SelectedThreadTab;
            if (newTab is not null
                && string.Equals(DataPaths.ExtractRootDomain(newTab.Board.Host), rootIn, StringComparison.OrdinalIgnoreCase)
                && string.Equals(newTab.Board.DirectoryName, dir, StringComparison.Ordinal)
                && string.Equals(newTab.ThreadKey,           key, StringComparison.Ordinal))
            {
                newTab.PendingScrollToPost = new ScrollToPostRequest(scrollToPostNumber);
            }
        }
    }

    // -----------------------------------------------------------------
    // ctor / 初期化
    // -----------------------------------------------------------------

    public MainViewModel(
        BbsmenuClient        bbsmenuClient,
        SubjectTxtClient     subjectClient,
        SettingTxtClient     settingClient,
        DatClient            datClient,
        ThreadIndexService   threadIndex,
        FavoritesStorage     favoritesStorage,
        PostClient           postClient,
        DonguriService       donguri,
        ChBrowser.Services.Ng.NgService ng,
        DataPaths            paths,
        OpenTabsStorage   openTabsStorage,
        ChBrowser.Services.Llm.LlmClient llmClient)
    {
        _bbsmenuClient   = bbsmenuClient;
        _subjectClient   = subjectClient;
        _settingClient   = settingClient;
        _datClient       = datClient;
        _threadIndex     = threadIndex;
        _postClient      = postClient;
        _donguri         = donguri;
        _ng              = ng;
        _paths           = paths;
        _openTabsStorage = openTabsStorage;
        _llmClient       = llmClient;
        Favorites        = new FavoritesViewModel(favoritesStorage);

        Favorites.Changed += RefreshFavoritesHtml;
        // 「上ボタン」バー: お気に入り変更のたびに再構築 (= フォルダ直下を ObservableCollection に流し込む)。
        Favorites.Changed += RefreshTopButtons;
        RefreshTopButtons();

        // タブを閉じる瞬間に「最後にスクロールしていた位置」を idx.json に書き出すため、
        // CollectionChanged を購読する。スクロール中の都度書き込みは行わない設計
        // (= MainViewModel.UpdateScrollPosition は in-memory 更新のみ)。
        ThreadTabs.CollectionChanged += OnThreadTabsCollectionChanged;

        // スレ一覧タブの close 履歴管理 (= 中クリック空領域復元用)。
        ThreadListTabs.CollectionChanged += (_, e) =>
        {
            if (e.OldItems is null) return;
            foreach (var item in e.OldItems)
                if (item is ThreadListTabViewModel t) PushRecentlyClosedThreadListTab(t);
        };

        // どんぐり経過時間表示を 30 秒ごとに更新。起動直後にも 1 度実行。
        _donguriTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _donguriTimer.Tick += (_, _) => UpdateDonguriStatus();
        _donguriTimer.Start();
        UpdateDonguriStatus();
    }

    /// <summary>ローカル保存済みの bbsmenu とお気に入りを最初に読み込む。なければ空のまま。</summary>
    public async Task InitializeAsync()
    {
        Favorites.Reload();

        try
        {
            var cats = await _bbsmenuClient.LoadFromDiskAsync().ConfigureAwait(true);
            ApplyCategories(cats);
            StatusMessage = cats.Count == 0
                ? "板一覧未取得 - ファイル → 板一覧を更新 を実行してください"
                : $"板一覧 (キャッシュ): {TotalBoards(cats)} 板";
        }
        catch (Exception ex)
        {
            StatusMessage = $"板一覧の読み込みに失敗: {ex.Message}";
        }
    }

    // -----------------------------------------------------------------
    // どんぐり (acorn) 経過時間表示
    // -----------------------------------------------------------------

    /// <summary>外部 (App.ClearDonguriCookiesNow 等) から強制再描画を要求するための public ラッパ。
    /// 内部実装は <see cref="UpdateDonguriStatus"/> と同じ。</summary>
    public void RefreshDonguriStatusFromCookieJar() => UpdateDonguriStatus();

    /// <summary>WidthMode に応じてタブ幅 (px) を算出する。下限 60 px。</summary>
    private static double ComputeTabWidth(string mode, int chars, int px)
    {
        if (string.Equals(mode, "px", StringComparison.Ordinal))
            return Math.Max(60, px);
        // chars (default)
        return Math.Max(60, chars * CharToPxRatio + TabPaddingPx);
    }

    /// <summary>「上ボタン」バー表示用 ObservableCollection を再構築する。
    /// お気に入りツリー (root) を走査して名前が <see cref="TopButtonsFolderName"/> のフォルダを探し、
    /// その直下を <see cref="TopButtonsItems"/> に流し込む。フォルダ無し / 子無しなら空 + バー非表示。</summary>
    private void RefreshTopButtons()
    {
        TopButtonsItems.Clear();
        var folder = Favorites.Items
            .OfType<FavoriteFolderViewModel>()
            .FirstOrDefault(f => f.Name == TopButtonsFolderName);
        if (folder is not null)
        {
            foreach (var child in folder.Children) TopButtonsItems.Add(child);
        }
        IsTopButtonsBarVisible = TopButtonsItems.Count > 0;
    }

    /// <summary>acorn の発行時刻 + 経過秒数からステータス文字列を組み立てる。
    /// 設計書 §3.5: 0→1 で約 5 分、約 3 時間で失効、IP 変更で Lv0 リセット。</summary>
    private void UpdateDonguriStatus()
    {
        var age = _donguri.EstimatedAcornAgeSeconds;
        if (age is null)
        {
            DonguriStatus = "🌰 未取得";
            return;
        }
        const int lifetimeSec = 3 * 60 * 60;
        if (age >= lifetimeSec)
        {
            DonguriStatus = "🌰 失効 (再取得が必要)";
            return;
        }
        var lv        = (int)(age / 300);
        var ageStr    = FormatHm(age.Value);
        var remaining = lifetimeSec - age.Value;
        DonguriStatus = $"🌰 Lv≈{lv} ({ageStr}経過, 残り {FormatHm(remaining)})";
    }

    /// <summary>秒数を "Xh Ym" / "Ym Zs" / "Zs" の短縮表記に。</summary>
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
    public AppConfig CurrentConfig { get; private set; } = new();

    /// <summary>NgService への外部アクセス (NgWindow から再ロード等を呼ぶため)。</summary>
    public ChBrowser.Services.Ng.NgService NgService => _ng;

    /// <summary>NG ルール変更後に呼ぶ — NgService をディスクから再ロード。
    /// 既に開いている全タブには即時反映しない (DOM 上の post を後から消す経路を持たないため)。</summary>
    public void ReapplyNgToOpenTabs()
    {
        _ng.Reload();
        StatusMessage = "NG ルールを保存しました — 開いているスレタブは閉じて開き直すと反映されます";
    }

    /// <summary>選択中のスレタブの「あぼーん N」内訳を、UI で表示しやすい形に組み立てて返す。
    /// 内訳は HiddenByRule (ルール ID → 件数) を NgService の現在ルールと突き合わせて NgRule を解決し、
    /// 件数の多い順に並べる。連鎖あぼーんが 1 件以上あれば末尾に追加。
    /// ルールが既に削除済みの場合 (= NgService に Id が無い) は <see cref="AboneBreakdownItem.Rule"/> = null。
    /// MainWindow の StatusBarItem クリックハンドラから呼ばれる。</summary>
    public IReadOnlyList<AboneBreakdownItem> GetSelectedTabAboneBreakdown()
    {
        var tab = SelectedThreadTab;
        if (tab is null) return Array.Empty<AboneBreakdownItem>();

        var ruleById = _ng.All.Rules.ToDictionary(r => r.Id);
        var items    = new List<AboneBreakdownItem>(tab.HiddenByRule.Count + 1);

        foreach (var (ruleId, count) in tab.HiddenByRule.OrderByDescending(kv => kv.Value))
        {
            ruleById.TryGetValue(ruleId, out var rule);
            items.Add(new AboneBreakdownItem(ruleId, rule, count, IsChain: false));
        }
        if (tab.HiddenByChain > 0)
            items.Add(new AboneBreakdownItem(RuleId: null, Rule: null, Count: tab.HiddenByChain, IsChain: true));

        return items;
    }

    /// <summary>App.xaml.cs の初期化時に最初に 1 度呼ばれ、以降は設定画面の即時反映でも呼ばれる。
    /// HttpClient.Timeout や HiDPI 等「次回起動時に反映」の項目はここでは触らない。</summary>
    public void ApplyConfig(AppConfig config)
    {
        CurrentConfig = config;

        // タブ幅を即時反映。文字数指定なら 1 文字 ≈ 14px + パディング、px 指定なら値そのまま (下限 60)。
        ThreadListTabWidth = ComputeTabWidth(
            config.ThreadListTabWidthMode, config.ThreadListTabWidthChars, config.ThreadListTabWidthPx);
        ThreadTabWidth     = ComputeTabWidth(
            config.ThreadTabWidthMode,     config.ThreadTabWidthChars,     config.ThreadTabWidthPx);

        // スレ表示 (thread.js) 向け
        ThreadConfigJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            type                  = "setConfig",
            popularThreshold      = config.PopularThreshold,
            imageSizeThresholdMb  = config.ImageSizeThresholdMb,
            idHighlightThreshold  = config.IdHighlightThreshold,
            metaPopupClickOnly    = config.MetaPopupClickOnly,
        });

        // Phase 11b: 3 ペイン向け。各ペインは自分の JSON だけ受け取り、setConfig.openOnSingleClick を解釈する。
        FavoritesConfigJson  = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "setConfig", openOnSingleClick = config.FavoritesOpenOnSingleClick,
        });
        BoardListConfigJson  = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "setConfig", openOnSingleClick = config.BoardListOpenOnSingleClick,
        });
        ThreadListConfigJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "setConfig", openOnSingleClick = config.ThreadListOpenOnSingleClick,
        });
    }

    // -----------------------------------------------------------------
    // Phase 11d: 「すべての CSS を再読み込み」
    // -----------------------------------------------------------------

    /// <summary>ThemeService のディスクキャッシュ + 各 HtmlBuilder のシェル HTML キャッシュを破棄し、
    /// 3 ペイン (お気に入り / 板一覧 / スレ一覧) の HTML を再生成して即時反映させる。
    /// スレ表示 / ビューアは対象外 (= 開き直しが必要、UI で案内)。</summary>
    public void ReloadAllPaneCss(ChBrowser.Services.Theme.ThemeService theme)
    {
        theme.InvalidateCache();
        ChBrowser.Services.Render.FavoritesHtmlBuilder.InvalidateCache();
        ChBrowser.Services.Render.BoardListHtmlBuilder.InvalidateCache();
        ChBrowser.Services.Render.ThreadListHtmlBuilder.InvalidateCache();
        ChBrowser.Controls.WebView2Helper.InvalidateShellCaches();
        RefreshFavoritesHtml();
        RefreshBoardListHtml();
        var now = DateTimeOffset.UtcNow;
        foreach (var tab in ThreadListTabs)
        {
            // SetItems は Html プロパティを再設定するので WebView2 が再ナビゲートされる
            tab.SetItems(tab.Items, now);
        }
        StatusMessage = "CSS を再読み込みしました (スレ表示タブは開き直し必要)";
    }
}

/// <summary>ステータスバーの「あぼーん N」クリック時に出す内訳メニューの 1 項目分。
/// <see cref="MainViewModel.GetSelectedTabAboneBreakdown"/> が組み立てて返す。
///
/// <para><see cref="IsChain"/> = true は「連鎖あぼーん」項目 (= 特定ルールに帰属しない、無効化メニュー項目)。</para>
/// <para><see cref="Rule"/> = null は対象ルールが既に削除された状態 (= ルールへ飛ぶ操作も無効、件数のみ表示)。</para></summary>
public sealed record AboneBreakdownItem(System.Guid? RuleId, ChBrowser.Models.NgRule? Rule, int Count, bool IsChain);
