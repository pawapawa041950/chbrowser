using System;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using ChBrowser.Controls;
using ChBrowser.Models;
using ChBrowser.Services.Api;
using ChBrowser.Services.Donguri;
using ChBrowser.Services.Image;
using ChBrowser.Services.Storage;
using ChBrowser.Services.Theme;
using ChBrowser.ViewModels;
using ChBrowser.Views;

namespace ChBrowser;

public partial class App : Application
{
    private MonazillaClient?   _monazilla;
    private ImageMetaService?  _imageMeta;
    private ImageCacheService? _imageCache;
    private UrlExpander?       _urlExpander;
    private ConfigStorage?     _configStorage;
    private MainViewModel?     _mainVm;
    private DataPaths?         _paths;
    private ThemeService?      _themeService;
    private ChBrowser.Services.Ng.NgService? _ngService;
    private ChBrowser.Services.Storage.ShortcutStorage? _shortcutStorage;
    private ChBrowser.Services.Shortcuts.ShortcutManager? _shortcutManager;

    /// <summary>WebView 内 JS ブリッジが受信したショートカット/マウス/ジェスチャーを dispatch するために公開 (Phase 16)。</summary>
    public ChBrowser.Services.Shortcuts.ShortcutManager? ShortcutManager => _shortcutManager;
    private AppConfig          _currentConfig = new();
    private ChBrowser.Views.NgWindow? _ngWindow;
    private ChBrowser.Views.ShortcutsWindow? _shortcutsWindow;

    /// <summary>シャットダウン中フラグ (Phase 10)。
    /// ImageViewerWindow の Closing は通常は Hide() に切り替えるが、アプリ終了時は
    /// 通すために使う。</summary>
    public static bool IsShuttingDown { get; private set; }

    /// <summary>画像ビューアの ViewModel + Window (シングルトン、遅延生成)。
    /// 初回 openInViewer メッセージで <see cref="ShowImageInViewer"/> を呼ぶと作成 + Show する。</summary>
    private ImageViewerViewModel?  _imageViewerVm;
    private ImageViewerWindow?     _imageViewerWindow;

    /// <summary>MainWindow から呼ばれる。最初の呼び出しで Window と ViewModel を作る (lazy)。
    /// 同じ URL を 2 度送ったら既存タブをアクティブ化、別 URL なら新規タブを追加。</summary>
    public void ShowImageInViewer(string url)
    {
        if (_imageViewerVm is null)
        {
            _imageViewerVm     = new ImageViewerViewModel();
            // 保存処理用に ImageSaver (= ImageCacheService + HttpClient のラッパ) を注入。
            // _imageCache / _monazilla は OnStartup で確実にセットされている。
            var saver = new ImageSaver(_imageCache!, _monazilla!.Http);
            _imageViewerWindow = new ImageViewerWindow(_imageViewerVm, saver, _imageCache!)
            {
                Owner = MainWindow,
            };
            // Phase 16+: ビューアウィンドウを ShortcutManager に attach。
            // "ビューアウィンドウ" カテゴリの KeyBinding がここへ登録される。
            _shortcutManager?.AttachViewerWindow(_imageViewerWindow);
        }
        _imageViewerWindow!.OpenAndShow(url);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // .NET Core 系では Shift_JIS が組み込みで使えないため CodePages プロバイダを登録
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // ---- Phase 11: 設定読み込み (HiDPI / Timeout は次回起動時反映なのでここで取り込む) ----
        var paths        = new DataPaths();
        _paths           = paths;
        _configStorage   = new ConfigStorage(paths);
        _currentConfig   = _configStorage.Load();

        // HiDPI: PerMonitorV2 指定なら任意のウィンドウ作成前に awareness を上げる
        if (_currentConfig.HiDpiMode == "PerMonitorV2")
        {
            try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
            catch (Exception ex) { Debug.WriteLine($"[App] SetProcessDpiAwarenessContext failed: {ex.Message}"); }
        }

        // WebView2 の CoreWebView2Environment を裏で先行作成しておく。
        // 初回スレッドオープン時のコールドスタート (~200-500ms) を消す。await はしない。
        WebView2Helper.StartWarmup();

        _monazilla       = new MonazillaClient();
        // Phase 11: 起動時に Timeout / User-Agent override を反映 (両方とも次回起動時反映扱い)
        _monazilla.Http.Timeout = TimeSpan.FromSeconds(Math.Clamp(_currentConfig.TimeoutSec, 5, 600));
        if (!string.IsNullOrWhiteSpace(_currentConfig.UserAgentOverride))
        {
            try
            {
                _monazilla.Http.DefaultRequestHeaders.UserAgent.Clear();
                _monazilla.Http.DefaultRequestHeaders.UserAgent.ParseAdd(_currentConfig.UserAgentOverride);
            }
            catch (Exception ex) { Debug.WriteLine($"[App] UA override parse failed: {ex.Message}"); }
        }
        var bbsmenu      = new BbsmenuClient(_monazilla, paths);
        var subjectTxt   = new SubjectTxtClient(_monazilla, paths);
        var dat          = new DatClient(_monazilla, paths);
        var threadIndex  = new ThreadIndexService(paths);
        _imageMeta       = new ImageMetaService();
        _imageCache      = new ImageCacheService(paths);
        _imageCache.MaxBytes = (long)Math.Max(64, _currentConfig.CacheMaxMb) * 1024 * 1024;
        _urlExpander     = new UrlExpander();

        // テーマ (post.html / post.css) サービスを登録。
        // 既定テンプレのディスク展開はあえて起動時にはやらない (Phase 11 設定画面の
        // 「テーマファイルを生成」ボタンから明示的に呼ぶ予定)。ディスクに未展開なら
        // ThemeService.LoadActiveTheme が埋め込みリソースから読むので、ソース側の
        // post.html / post.css の変更は次回ビルド + 起動でそのまま画面に反映される。
        var themeService = new ThemeService(paths);
        _themeService    = themeService;
        WebView2Helper.RegisterThemeService(themeService);

        // WebResourceRequested による透過キャッシュ介在を有効化
        WebView2Helper.RegisterImageCache(_imageCache);

        // ウィンドウ/ペインサイズの永続化
        var layoutStorage = new LayoutStorage(paths);

        // お気に入り (Phase 7)
        var favoritesStorage = new FavoritesStorage(paths);

        // どんぐり / 投稿 (Phase 8)
        var cookieJar      = new CookieJar(paths.DonguriCookiesPath);
        var donguriService = new DonguriService(cookieJar, paths);
        var postClient     = new PostClient(_monazilla, donguriService);

        // NG (Phase 13)
        var ngStorage = new NgStorage(paths);
        _ngService    = new ChBrowser.Services.Ng.NgService(ngStorage);

        var mainVm       = new MainViewModel(bbsmenu, subjectTxt, dat, threadIndex, favoritesStorage, postClient, donguriService, _ngService);
        _mainVm          = mainVm;
        // 起動時にも 1 度 ApplyConfig を呼んで JS 側 (= スレ表示が後で開かれた時) に反映できるよう仕込む
        mainVm.ApplyConfig(_currentConfig);

        var window = new MainWindow
        {
            DataContext       = mainVm,
            ImageMetaService  = _imageMeta,
            ImageCacheService = _imageCache,
            UrlExpander       = _urlExpander,
            LayoutStorage     = layoutStorage,
        };
        MainWindow = window;

        // ショートカット & マウスジェスチャー (Phase 15) — MainWindow 作成後に setup
        _shortcutStorage = new ChBrowser.Services.Storage.ShortcutStorage(paths);
        _shortcutManager = new ChBrowser.Services.Shortcuts.ShortcutManager(window, BuildShortcutHandlers(window, mainVm, () => _imageViewerVm));
        // Apply 時に各カテゴリの bind 一覧を WebView 内 JS ブリッジへ push する callback を配線。
        _shortcutManager.OnBindingsApplied = byCategory =>
        {
            PushCategoryBindings(mainVm, byCategory, "スレッド表示領域",   json => mainVm.ThreadShortcutsJson     = json);
            PushCategoryBindings(mainVm, byCategory, "スレ一覧表示領域",   json => mainVm.ThreadListShortcutsJson = json);
            PushCategoryBindings(mainVm, byCategory, "お気に入りペイン",   json => mainVm.FavoritesShortcutsJson  = json);
            PushCategoryBindings(mainVm, byCategory, "板一覧ペイン",       json => mainVm.BoardListShortcutsJson  = json);
        };
        // ジェスチャー進捗の表示更新 (= MainViewModel.GestureStatus → ステータスバー)
        _shortcutManager.OnGestureProgress = (cat, gs) => mainVm.UpdateGestureStatus(cat, gs);
        _shortcutManager.Apply(_shortcutStorage.Load());
        _ = new ChBrowser.Services.Shortcuts.GestureRecognizer(window, _shortcutManager);

        window.Show();

        // 既存キャッシュがあれば即読み込み (await しない)
        _ = mainVm.InitializeAsync();
    }

    /// <summary>各 action Id に対応するハンドラを組み立てる (Phase 15)。
    /// ハンドラ第 1 引数 source は、マウス操作 / ジェスチャー由来の場合に <c>e.OriginalSource</c>
    /// (= クリック対象の WPF visual)、キーボードショートカット由来の場合は null。
    /// タブ操作系は source から対象タブ VM を解決し、解決できなければ SelectedTab フォールバック。
    /// 未実装機能 (戻る/進む 履歴 等) は登録しない (= ShortcutManager 側で skip される)。</summary>
    private static System.Collections.Generic.Dictionary<string, System.Action<object?>> BuildShortcutHandlers(MainWindow window, MainViewModel vm, System.Func<ImageViewerViewModel?> getViewerVm)
    {
        return new System.Collections.Generic.Dictionary<string, System.Action<object?>>
        {
            ["main.focus_address_bar"]   = _ => window.FocusAddressBar(),
            ["main.refresh_board_list"]  = _ => { if (vm.RefreshBoardListCommand.CanExecute(null)) vm.RefreshBoardListCommand.Execute(null); },
            ["main.exit"]                = _ => Current.Shutdown(),

            // ----- スレ一覧 -----
            ["thread_list.refresh"]                 = RefreshThreadList,
            ["thread_list.refresh_in_tab"]          = RefreshThreadList,
            ["thread_list.close_current"]           = CloseThreadListTab,
            ["thread_list.close_current_in_body"]   = CloseThreadListTab,
            ["thread_list.next_tab"]                = _ => CycleThreadListTab(vm, +1),
            ["thread_list.next_tab_in_body"]        = _ => CycleThreadListTab(vm, +1),
            ["thread_list.prev_tab"]                = _ => CycleThreadListTab(vm, -1),
            ["thread_list.prev_tab_in_body"]        = _ => CycleThreadListTab(vm, -1),

            // ----- スレッド -----
            ["thread.refresh"]                      = RefreshThread,
            ["thread.refresh_in_tab"]               = RefreshThread,
            ["thread.close_current"]                = CloseThread,
            ["thread.close_current_in_body"]        = CloseThread,
            ["thread.next_tab"]                     = _ => CycleThreadTab(vm, +1),
            ["thread.next_tab_in_body"]             = _ => CycleThreadTab(vm, +1),
            ["thread.prev_tab"]                     = _ => CycleThreadTab(vm, -1),
            ["thread.prev_tab_in_body"]             = _ => CycleThreadTab(vm, -1),
            ["thread.new_thread"]                   = _ =>
            {
                if (vm.SelectedThreadListTab is { IsBoardTab: true }) vm.OpenNewThreadDialog();
            },
            ["thread.delete_log"]                   = src =>
            {
                if (ResolveTargetThreadTab(src, vm) is { } t) vm.DeleteThreadLog(t);
            },
            // scroll_top / scroll_bottom は WebView 内の document.scrollTo を JS ローカルで処理するため、
            // C# 側の handler はバインディング一覧への登録目的の no-op (= JS には setShortcutBindings で descriptor が
            // push される。JS ブリッジは local actionId table を持って C# 経由なしで実行する)。
            ["thread.scroll_top"]                   = _ => { },
            ["thread.scroll_bottom"]                = _ => { },
            ["thread_list.scroll_top"]              = _ => { },
            ["thread_list.scroll_bottom"]           = _ => { },

            // ----- ビューアウィンドウ -----
            // close / save / next / prev は VM の Command 経由で実行 (= 既存の Esc/Ctrl+S/←→ と同等)。
            // zoom_in/out / rotate_right/left は viewer.js の localActions で処理されるので C# 側は no-op。
            ["viewer.close"]      = _ => { var v = getViewerVm(); if (v?.CloseCurrentTabCommand.CanExecute(null) == true) v.CloseCurrentTabCommand.Execute(null); },
            ["viewer.save"]       = _ => { var v = getViewerVm(); if (v?.SaveCurrentTabCommand .CanExecute(null) == true) v.SaveCurrentTabCommand .Execute(null); },
            ["viewer.next_image"] = _ => { var v = getViewerVm(); if (v?.NextTabCommand        .CanExecute(null) == true) v.NextTabCommand        .Execute(null); },
            ["viewer.prev_image"] = _ => { var v = getViewerVm(); if (v?.PrevTabCommand        .CanExecute(null) == true) v.PrevTabCommand        .Execute(null); },
            ["viewer.zoom_in"]      = _ => { },
            ["viewer.zoom_out"]     = _ => { },
            ["viewer.rotate_right"] = _ => { },
            ["viewer.rotate_left"]  = _ => { },
        };

        void RefreshThreadList(object? src)
        {
            var t = ResolveTargetThreadListTab(src, vm);
            if (t?.Board is { } b) _ = vm.LoadThreadListAsync(new BoardViewModel(b));
        }
        void CloseThreadListTab(object? src) => ResolveTargetThreadListTab(src, vm)?.CloseCommand?.Execute(null);
        void RefreshThread(object? src) { if (ResolveTargetThreadTab(src, vm) is { } t) _ = vm.RefreshThreadAsync(t); }
        void CloseThread(object? src) => ResolveTargetThreadTab(src, vm)?.CloseCommand?.Execute(null);
    }

    /// <summary>マウス由来の操作 (source != null) なら、source から該当 <see cref="ThreadTabViewModel"/> を解決して返す
    /// (= 解決できなければ null = 何もしない)。キーボード由来 (source == null) なら現在の選択タブにフォールバック。
    /// これにより「スレッドタブ以外の場所をクリックした時、現在のスレタブが誤って操作される」事象を防ぐ。</summary>
    private static ThreadTabViewModel? ResolveTargetThreadTab(object? source, MainViewModel vm)
        => source is null ? vm.SelectedThreadTab : ResolveThreadTabFromSource(source);

    /// <summary>同上の <see cref="ThreadListTabViewModel"/> 版。</summary>
    private static ThreadListTabViewModel? ResolveTargetThreadListTab(object? source, MainViewModel vm)
        => source is null ? vm.SelectedThreadListTab : ResolveThreadListTabFromSource(source);

    /// <summary>OriginalSource (or 任意の DependencyObject) から visual + logical tree を上って
    /// DataContext が <see cref="ThreadTabViewModel"/> の最初の要素を返す。
    /// タブ見出しクリック / タブ × ボタンクリック / WebView2 上のクリック (= まれ、HwndHost が吸う前の縁) などで解決される。</summary>
    private static ThreadTabViewModel? ResolveThreadTabFromSource(object? source)
    {
        var cur = source as System.Windows.DependencyObject;
        while (cur is not null)
        {
            if (cur is System.Windows.FrameworkElement fe && fe.DataContext is ThreadTabViewModel t) return t;
            cur = ChBrowser.Services.Shortcuts.CategoryResolver.GetAnyParent(cur);
        }
        return null;
    }

    private static ThreadListTabViewModel? ResolveThreadListTabFromSource(object? source)
    {
        var cur = source as System.Windows.DependencyObject;
        while (cur is not null)
        {
            if (cur is System.Windows.FrameworkElement fe && fe.DataContext is ThreadListTabViewModel t) return t;
            cur = ChBrowser.Services.Shortcuts.CategoryResolver.GetAnyParent(cur);
        }
        return null;
    }

    private static void CycleThreadTab(MainViewModel vm, int step)
    {
        if (vm.ThreadTabs.Count == 0) return;
        var idx = vm.SelectedThreadTab is null ? -1 : vm.ThreadTabs.IndexOf(vm.SelectedThreadTab);
        if (idx < 0) idx = 0;
        idx = (idx + step + vm.ThreadTabs.Count) % vm.ThreadTabs.Count;
        vm.SelectedThreadTab = vm.ThreadTabs[idx];
    }

    /// <summary>OnBindingsApplied callback の本体。指定カテゴリの descriptor → actionId マップを JSON 化して
    /// setShortcutBindings として push。JS 側ブリッジは descriptor → actionId で local action 判定 (scroll 等) と
    /// suppress/dispatch をする。</summary>
    private static void PushCategoryBindings(
        MainViewModel vm,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> byCategory,
        string category,
        Action<string> setter)
    {
        var map = byCategory.TryGetValue(category, out var b) ? b
                                                              : (IReadOnlyDictionary<string, string>)new Dictionary<string, string>();
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            type     = "setShortcutBindings",
            bindings = map,
        });
        setter(json);
    }

    private static void CycleThreadListTab(MainViewModel vm, int step)
    {
        if (vm.ThreadListTabs.Count == 0) return;
        var idx = vm.SelectedThreadListTab is null ? -1 : vm.ThreadListTabs.IndexOf(vm.SelectedThreadListTab);
        if (idx < 0) idx = 0;
        idx = (idx + step + vm.ThreadListTabs.Count) % vm.ThreadListTabs.Count;
        vm.SelectedThreadListTab = vm.ThreadListTabs[idx];
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsShuttingDown = true;
        _imageViewerWindow?.Close();
        _ngWindow?.Close();
        _shortcutsWindow?.Close();
        _urlExpander?.Dispose();
        _imageCache?.Dispose();
        _imageMeta?.Dispose();
        _monazilla?.Dispose();
        base.OnExit(e);
    }

    // -----------------------------------------------------------------
    // Phase 11: 設定ウィンドウ生成 (MainWindow.OpenSettings_Click から呼ばれる)
    // -----------------------------------------------------------------

    /// <summary>SettingsViewModel をアプリ Service への適用コールバック付きで組み立てる。</summary>
    public SettingsViewModel CreateSettingsViewModel()
    {
        if (_configStorage is null || _imageCache is null || _paths is null || _monazilla is null || _mainVm is null || _themeService is null)
            throw new InvalidOperationException("App services are not yet initialized");

        return new SettingsViewModel(
            storage:               _configStorage,
            initial:               _currentConfig,
            applyCallback:         ApplyConfigImmediate,
            getCacheBytes:         () => CalculateCacheBytes(_paths.CacheImagesDir),
            clearCacheAction:      ClearImageCacheNow,
            openCacheFolderAction: () => OpenInExplorer(_paths.CacheImagesDir),
            restartNowAction:      RestartApp,
            // Phase 11d: デザイン編集
            openCssFileAction:        OpenCssFile,
            openThemeFolderAction:    () => OpenInExplorer(_themeService.ThemeFolderPath),
            reloadAllCssAction:       () => _mainVm!.ReloadAllPaneCss(_themeService),
            extractDefaultCssAction:  ExtractDefaultThemeFiles);
    }

    /// <summary>指定 CSS ファイルを関連付けエディタで開く。
    /// ディスクに無ければ埋め込み既定を書き出してから開く (= ユーザは「開く」 1 操作で編集できる)。</summary>
    private void OpenCssFile(string fileName)
    {
        if (_themeService is null) return;
        var path = _themeService.ResolveCssPath(fileName);
        try
        {
            if (!System.IO.File.Exists(path))
            {
                // 1 ファイルだけ生成したいが現状の API は全展開しか無いので、まず全展開してからこのファイルを開く
                _themeService.ExtractDefaultThemeFiles();
            }
            if (!System.IO.File.Exists(path))
            {
                MessageBox.Show(MainWindow ?? Current.MainWindow, $"ファイルを生成できませんでした: {path}",
                    "ChBrowser", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Process.Start(new ProcessStartInfo
            {
                FileName        = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Debug.WriteLine($"[App] OpenCssFile failed: {ex.Message}"); }
    }

    /// <summary>NG 設定ウィンドウをモードレス + シングルトンで開く (Phase 13)。</summary>
    public void ShowNgWindow()
    {
        if (_mainVm is null || _ngService is null) return;
        if (_ngWindow is { IsVisible: true }) { _ngWindow.Activate(); return; }

        // 板選択用の一覧を MainViewModel から構築。
        // 表示名は「英名 (日本語名)」形式 (= 検索は英名で行うため英名を先頭にする)。
        var boards = new List<BoardScopeViewModel>();
        foreach (var cat in _mainVm.BoardCategories)
            foreach (var bvm in cat.Boards)
                boards.Add(new BoardScopeViewModel(bvm.Board.Host, bvm.Board.DirectoryName, $"{bvm.Board.DirectoryName} ({bvm.BoardName})"));

        var vm = new NgWindowViewModel(
            _ngService,
            boards,
            showWarning: (title, detail) =>
            {
                var msg = string.IsNullOrEmpty(detail) ? title : $"{title}\n\n{detail}";
                MessageBox.Show(_ngWindow ?? MainWindow ?? Current.MainWindow, msg,
                    "ChBrowser NG 設定", MessageBoxButton.OK, MessageBoxImage.Warning);
            },
            onClosed: () => _mainVm.ReapplyNgToOpenTabs());

        _ngWindow = new ChBrowser.Views.NgWindow(vm) { Owner = MainWindow };
        _ngWindow.Closed += (_, _) => _ngWindow = null;
        _ngWindow.Show();
    }

    /// <summary>ショートカット & マウスジェスチャー設定ウィンドウをモードレス + シングルトンで開く (Phase 15)。
    /// VM の Save() からは _shortcutStorage への永続化と _shortcutManager の再適用が同時に走る。</summary>
    public void ShowShortcutsWindow()
    {
        if (_shortcutStorage is null || _shortcutManager is null) return;
        if (_shortcutsWindow is { IsVisible: true }) { _shortcutsWindow.Activate(); return; }

        var vm = new ChBrowser.ViewModels.ShortcutsWindowViewModel(
            loadSettings: _shortcutStorage.Load,
            saveSettings: settings =>
            {
                _shortcutStorage.Save(settings);
                _shortcutManager.Apply(settings);
            });
        _shortcutsWindow = new ChBrowser.Views.ShortcutsWindow(vm) { Owner = MainWindow };
        _shortcutsWindow.Closed += (_, _) => _shortcutsWindow = null;
        _shortcutsWindow.Show();
    }

    private void ExtractDefaultThemeFiles()
    {
        if (_themeService is null) return;
        try
        {
            _themeService.ExtractDefaultThemeFiles();
            MessageBox.Show(MainWindow ?? Current.MainWindow,
                $"既定 CSS を {_themeService.ThemeFolderPath} に生成しました (既存ファイルは上書きしません)。",
                "ChBrowser", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { Debug.WriteLine($"[App] ExtractDefaultThemeFiles failed: {ex.Message}"); }
    }

    /// <summary>SettingsViewModel から呼ばれる。即時反映できるものをここで触る。
    /// HiDPI / TimeoutSec は次回起動時反映なので「保存だけ」(値は SettingsViewModel が ConfigStorage.Save 済み)。</summary>
    private void ApplyConfigImmediate(AppConfig config)
    {
        _currentConfig = config;

        // User-Agent: 即時反映 (進行中のリクエストに影響しないよう、新規リクエストから新 UA)
        try
        {
            if (_monazilla is not null)
            {
                _monazilla.Http.DefaultRequestHeaders.UserAgent.Clear();
                if (!string.IsNullOrWhiteSpace(config.UserAgentOverride))
                {
                    _monazilla.Http.DefaultRequestHeaders.UserAgent.ParseAdd(config.UserAgentOverride);
                }
                else
                {
                    // 既定の Monazilla/1.00 ChBrowser/<ver> を再構築
                    var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
                    _monazilla.Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Monazilla", "1.00"));
                    _monazilla.Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ChBrowser", version));
                }
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[App] UA apply failed: {ex.Message}"); }

        // 画像キャッシュ上限
        if (_imageCache is not null)
            _imageCache.MaxBytes = (long)Math.Max(64, config.CacheMaxMb) * 1024 * 1024;

        // ビューアサムネサイズ
        if (_imageViewerVm is not null)
            _imageViewerVm.ThumbnailSize = Math.Clamp(config.ViewerThumbnailSize, 32, 256);

        // スレ表示 JS への broadcast (popularThreshold / imageSizeThresholdMb)
        _mainVm?.ApplyConfig(config);
    }

    private static long CalculateCacheBytes(string dir)
    {
        if (!System.IO.Directory.Exists(dir)) return 0;
        long total = 0;
        try
        {
            foreach (var f in System.IO.Directory.EnumerateFiles(dir, "*", System.IO.SearchOption.AllDirectories))
            {
                try { total += new System.IO.FileInfo(f).Length; }
                catch { /* 個別ファイルアクセスエラーは無視 */ }
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[App] cache size calc failed: {ex.Message}"); }
        return total;
    }

    private void ClearImageCacheNow()
    {
        if (_paths is null) return;
        var dir = _paths.CacheImagesDir;
        var confirm = MessageBox.Show(MainWindow ?? Current.MainWindow,
            "画像キャッシュをすべて削除しますか?",
            "ChBrowser",
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;

        try
        {
            // index.json と中身全部消す。サブディレクトリ (<hh>/) も含めて再帰削除
            if (System.IO.Directory.Exists(dir))
            {
                foreach (var sub in System.IO.Directory.EnumerateDirectories(dir))
                {
                    try { System.IO.Directory.Delete(sub, recursive: true); }
                    catch (Exception ex) { Debug.WriteLine($"[App] delete subdir failed {sub}: {ex.Message}"); }
                }
                foreach (var f in System.IO.Directory.EnumerateFiles(dir))
                {
                    try { System.IO.File.Delete(f); }
                    catch (Exception ex) { Debug.WriteLine($"[App] delete file failed {f}: {ex.Message}"); }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] cache clear failed: {ex.Message}");
        }
        // ImageCacheService 内部の index は次回アクセス時に miss → 自然に再構築されるが、
        // 既存インメモリ index と整合性が取れない可能性がある。次回起動で完全クリア状態。
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            if (!System.IO.Directory.Exists(path)) System.IO.Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName        = "explorer.exe",
                Arguments       = $"\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) { Debug.WriteLine($"[App] open explorer failed: {ex.Message}"); }
    }

    private void RestartApp()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = exe,
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[App] restart failed: {ex.Message}"); }
        Shutdown();
    }

    // -----------------------------------------------------------------
    // HiDPI 切替用 P/Invoke (Win10 1607+)
    // -----------------------------------------------------------------

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
