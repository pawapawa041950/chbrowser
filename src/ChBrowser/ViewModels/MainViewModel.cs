using System;
using System.Collections.ObjectModel;
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
    private readonly DatClient                        _datClient;
    private readonly ThreadIndexService               _threadIndex;
    private readonly PostClient                       _postClient;
    private readonly DonguriService                   _donguri;
    private readonly DispatcherTimer                  _donguriTimer;
    private readonly ChBrowser.Services.Ng.NgService  _ng;
    private readonly DataPaths                        _paths;

    // ----- ペイン用コレクション -----
    public ObservableCollection<BoardCategoryViewModel> BoardCategories  { get; } = new();
    public ObservableCollection<ThreadListTabViewModel> ThreadListTabs   { get; } = new();
    public ObservableCollection<ThreadTabViewModel>     ThreadTabs       { get; } = new();

    /// <summary>お気に入りペイン (TreeView) のルート。Phase 7 で導入。</summary>
    public FavoritesViewModel Favorites { get; }

    // ----- ステータスバー / 進捗 -----
    [ObservableProperty] private string _statusMessage = "準備完了";
    [ObservableProperty] private bool   _isBusy;

    /// <summary>あぼーん件数のステータスバー表示。SelectedThreadTab の hidden 数を表示。</summary>
    [ObservableProperty] private string _aboneStatus = "あぼーん 0";

    /// <summary>ステータスバーに出すどんぐり (acorn) の状態テキスト。30 秒ごとに更新。</summary>
    [ObservableProperty] private string _donguriStatus = "🥜 未取得";

    /// <summary>マウスジェスチャー入力中のリアルタイム表示 (Phase 16+)。</summary>
    [ObservableProperty] private string _gestureStatus = "";

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

    // ----- アクティブタブ -----
    [ObservableProperty] private ThreadListTabViewModel? _selectedThreadListTab;
    [ObservableProperty] private ThreadTabViewModel?     _selectedThreadTab;

    partial void OnSelectedThreadTabChanged(ThreadTabViewModel? value)
    {
        // 各タブの WebView2 は専属インスタンス (TabControl ではなく ItemsControl で並列描画)。
        // Visibility 切替で選択タブだけ可視にするため、IsSelected を更新する。
        foreach (var t in ThreadTabs) t.IsSelected = ReferenceEquals(t, value);
        // ステータスバーの「あぼーん N」を選択タブのものに更新
        AboneStatus = value is null ? "あぼーん 0" : $"あぼーん {value.HiddenCount}";
        // アドレスバーは last-activated wins (Phase 14)
        if (value is not null) _lastActivePane = ActivePane.Thread;
        RefreshAddressBarUrl();
    }

    partial void OnSelectedThreadListTabChanged(ThreadListTabViewModel? value)
    {
        foreach (var t in ThreadListTabs) t.IsSelected = ReferenceEquals(t, value);
        if (value is not null) _lastActivePane = ActivePane.ThreadList;
        RefreshAddressBarUrl();
    }

    // -----------------------------------------------------------------
    // ジェスチャー進捗
    // -----------------------------------------------------------------

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
    }

    /// <summary>右ペイン下段「スレ表示」(ThreadTabs) がアクティブ化された通知。</summary>
    public void MarkThreadPaneActive()
    {
        _lastActivePane = ActivePane.Thread;
        RefreshAddressBarUrl();
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
                await OpenThreadByUrlAsync(target.Host, target.Directory, target.ThreadKey).ConfigureAwait(true);
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

    /// <summary>スレ URL を開く: 既存 ThreadTab があればアクティブ化、無ければ新規。</summary>
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

    // -----------------------------------------------------------------
    // ctor / 初期化
    // -----------------------------------------------------------------

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

        Favorites.Changed += RefreshFavoritesHtml;

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

    /// <summary>acorn の発行時刻 + 経過秒数からステータス文字列を組み立てる。
    /// 設計書 §3.5: 0→1 で約 5 分、約 3 時間で失効、IP 変更で Lv0 リセット。</summary>
    private void UpdateDonguriStatus()
    {
        var age = _donguri.EstimatedAcornAgeSeconds;
        if (age is null)
        {
            DonguriStatus = "🥜 未取得";
            return;
        }
        const int lifetimeSec = 3 * 60 * 60;
        if (age >= lifetimeSec)
        {
            DonguriStatus = "🥜 失効 (再取得が必要)";
            return;
        }
        var lv        = (int)(age / 300);
        var ageStr    = FormatHm(age.Value);
        var remaining = lifetimeSec - age.Value;
        DonguriStatus = $"🥜 Lv≈{lv} ({ageStr}経過, 残り {FormatHm(remaining)})";
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

    /// <summary>App.xaml.cs の初期化時に最初に 1 度呼ばれ、以降は設定画面の即時反映でも呼ばれる。
    /// HttpClient.Timeout や HiDPI 等「次回起動時に反映」の項目はここでは触らない。</summary>
    public void ApplyConfig(AppConfig config)
    {
        CurrentConfig = config;

        // スレ表示 (thread.js) 向け
        ThreadConfigJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            type                  = "setConfig",
            popularThreshold      = config.PopularThreshold,
            imageSizeThresholdMb  = config.ImageSizeThresholdMb,
            showReadMark          = config.ShowReadMark,
            idHighlightThreshold  = config.IdHighlightThreshold,
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
