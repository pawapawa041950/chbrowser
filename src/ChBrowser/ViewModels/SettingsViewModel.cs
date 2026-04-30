using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;
using ChBrowser.Models;
using ChBrowser.Services.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChBrowser.ViewModels;

/// <summary>
/// 設定ウィンドウの ViewModel (Phase 11)。VS Code 風の左カテゴリ + 右ペイン構成。
/// 保存は **即時反映**: プロパティ変更 → 300ms debounce → ConfigStorage.Save + ApplyCallback で反映。
/// HiDPI / TimeoutSec を変更すると <see cref="RestartRequired"/> が立ち、上部にバナーが表示される。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public ObservableCollection<SettingsCategoryViewModel> Categories { get; } = new();

    [ObservableProperty]
    private SettingsCategoryViewModel? _selectedCategory;

    // ---- AppConfig 由来のプロパティ (即時反映) ----

    [ObservableProperty] private string _hiDpiMode             = "Unaware";
    [ObservableProperty] private string _userAgentOverride     = "";
    [ObservableProperty] private int    _timeoutSec            = 30;
    [ObservableProperty] private int    _popularThreshold      = 3;
    [ObservableProperty] private string _defaultThreadViewMode = "DedupTree";
    [ObservableProperty] private int    _imageSizeThresholdMb  = 5;
    [ObservableProperty] private int    _cacheMaxMb            = 1024;
    [ObservableProperty] private int    _viewerThumbnailSize   = 80;

    // ---- Phase 11b: 3 ペインの 1 クリック設定 ----
    [ObservableProperty] private bool   _favoritesOpenOnSingleClick  = true;
    [ObservableProperty] private bool   _boardListOpenOnSingleClick  = true;
    [ObservableProperty] private bool   _threadListOpenOnSingleClick = true;

    // ---- Phase 11c: タブ動作 (スレ一覧タブ × 5 イベント、スレッドタブ × 5 イベント) ----
    [ObservableProperty] private string _threadListTabMiddleClickAction = "close";
    [ObservableProperty] private string _threadListTabCtrlClickAction   = "none";
    [ObservableProperty] private string _threadListTabShiftClickAction  = "none";
    [ObservableProperty] private string _threadListTabAltClickAction    = "none";
    [ObservableProperty] private string _threadListTabDoubleClickAction = "refresh";
    [ObservableProperty] private string _threadTabMiddleClickAction     = "close";
    [ObservableProperty] private string _threadTabCtrlClickAction       = "none";
    [ObservableProperty] private string _threadTabShiftClickAction      = "none";
    [ObservableProperty] private string _threadTabAltClickAction        = "none";
    [ObservableProperty] private string _threadTabDoubleClickAction     = "refresh";

    /// <summary>HiDPI / TimeoutSec を起動後に変更すると true。バナーで再起動を促す。</summary>
    [ObservableProperty]
    private bool _restartRequired;

    /// <summary>「画像」カテゴリで現在のキャッシュ使用量を表示するための文字列 (例: "512.3 MB / 1024 MB")。
    /// 設定ウィンドウを開くたびに <see cref="RefreshCacheSizeDisplay"/> で更新する。</summary>
    [ObservableProperty]
    private string _cacheSizeDisplay = "計算中…";

    public IRelayCommand RestartNowCommand     { get; }
    public IRelayCommand OpenCacheFolderCommand{ get; }
    public IRelayCommand ClearCacheCommand     { get; }

    // ---- Phase 11d: デザイン編集 ----
    public IRelayCommand<string>? OpenCssFileCommand { get; }
    public IRelayCommand           OpenThemeFolderCommand   { get; }
    public IRelayCommand           ReloadAllCssCommand      { get; }
    public IRelayCommand           ExtractDefaultCssCommand { get; }

    private readonly ConfigStorage         _storage;
    private readonly Action<AppConfig>     _applyCallback;
    private readonly Func<long>            _getCacheBytes;
    private readonly Action                _clearCacheAction;
    private readonly Action                _openCacheFolderAction;
    private readonly Action                _restartNowAction;
    // Phase 11d
    private readonly Action<string>?       _openCssFileAction;
    private readonly Action?               _openThemeFolderAction;
    private readonly Action?               _reloadAllCssAction;
    private readonly Action?               _extractDefaultCssAction;
    private readonly DispatcherTimer       _saveTimer;
    private readonly AppConfig             _initialConfig;
    private bool                           _suppressSave;

    public SettingsViewModel(
        ConfigStorage     storage,
        AppConfig         initial,
        Action<AppConfig> applyCallback,
        Func<long>        getCacheBytes,
        Action            clearCacheAction,
        Action            openCacheFolderAction,
        Action            restartNowAction,
        Action<string>?   openCssFileAction        = null,
        Action?           openThemeFolderAction    = null,
        Action?           reloadAllCssAction       = null,
        Action?           extractDefaultCssAction  = null)
    {
        _storage                 = storage;
        _initialConfig           = initial;
        _applyCallback           = applyCallback;
        _getCacheBytes           = getCacheBytes;
        _clearCacheAction        = clearCacheAction;
        _openCacheFolderAction   = openCacheFolderAction;
        _restartNowAction        = restartNowAction;
        _openCssFileAction       = openCssFileAction;
        _openThemeFolderAction   = openThemeFolderAction;
        _reloadAllCssAction      = reloadAllCssAction;
        _extractDefaultCssAction = extractDefaultCssAction;

        // カテゴリ枠 — 設計書 §5.7 で確定したカテゴリのうち、Phase 11a で実装するもの 5 つを順に並べる。
        // お気に入り / 板一覧 / スレッド一覧 / タブ動作 / デザイン編集 / NG / ショートカット / マウスジェスチャー
        // は後続フェーズで追加。
        Categories.Add(new("全般",         "HiDPI モード"));
        Categories.Add(new("通信",         "User-Agent、HTTP タイムアウト"));
        Categories.Add(new("お気に入り",   "クリックで開く動作"));
        Categories.Add(new("板一覧",       "クリックで開く動作"));
        Categories.Add(new("スレッド一覧", "クリックで開く動作"));
        Categories.Add(new("スレッド",     "人気レス閾値、標準表示モード、画像 HEAD しきい値"));
        Categories.Add(new("タブ動作",     "スレ一覧タブ / スレッドタブのクリックイベントごとのアクション"));
        Categories.Add(new("画像",         "キャッシュ上限、キャッシュフォルダを開く、キャッシュクリア"));
        Categories.Add(new("ビューア",     "タブのサムネイルサイズ"));
        Categories.Add(new("デザイン編集", "各ペインの CSS をエディタで開いて編集する"));
        SelectedCategory = Categories[0];

        // 初期値を AppConfig から流し込む (この間は保存させない)
        _suppressSave                = true;
        HiDpiMode                    = initial.HiDpiMode;
        UserAgentOverride            = initial.UserAgentOverride;
        TimeoutSec                   = initial.TimeoutSec;
        PopularThreshold             = initial.PopularThreshold;
        DefaultThreadViewMode        = initial.DefaultThreadViewMode;
        ImageSizeThresholdMb         = initial.ImageSizeThresholdMb;
        CacheMaxMb                   = initial.CacheMaxMb;
        ViewerThumbnailSize          = initial.ViewerThumbnailSize;
        FavoritesOpenOnSingleClick   = initial.FavoritesOpenOnSingleClick;
        BoardListOpenOnSingleClick   = initial.BoardListOpenOnSingleClick;
        ThreadListOpenOnSingleClick  = initial.ThreadListOpenOnSingleClick;
        ThreadListTabMiddleClickAction = initial.ThreadListTabMiddleClickAction;
        ThreadListTabCtrlClickAction   = initial.ThreadListTabCtrlClickAction;
        ThreadListTabShiftClickAction  = initial.ThreadListTabShiftClickAction;
        ThreadListTabAltClickAction    = initial.ThreadListTabAltClickAction;
        ThreadListTabDoubleClickAction = initial.ThreadListTabDoubleClickAction;
        ThreadTabMiddleClickAction     = initial.ThreadTabMiddleClickAction;
        ThreadTabCtrlClickAction       = initial.ThreadTabCtrlClickAction;
        ThreadTabShiftClickAction      = initial.ThreadTabShiftClickAction;
        ThreadTabAltClickAction        = initial.ThreadTabAltClickAction;
        ThreadTabDoubleClickAction     = initial.ThreadTabDoubleClickAction;
        _suppressSave                = false;

        // Debounce タイマ — 連続変更でも 1 回の保存に集約
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveAndApply(); };

        // 全プロパティ変更を監視して debounce 開始
        PropertyChanged += OnAnyPropertyChanged;

        RestartNowCommand      = new RelayCommand(() => _restartNowAction());
        OpenCacheFolderCommand = new RelayCommand(() => _openCacheFolderAction());
        ClearCacheCommand      = new RelayCommand(() =>
        {
            _clearCacheAction();
            RefreshCacheSizeDisplay();
        });
        // Phase 11d: デザイン編集 — null callback ならボタン無効化のため CanExecute=false
        OpenCssFileCommand     = new RelayCommand<string>(
            name => { if (!string.IsNullOrEmpty(name)) _openCssFileAction?.Invoke(name); },
            _ => _openCssFileAction is not null);
        OpenThemeFolderCommand   = new RelayCommand(() => _openThemeFolderAction?.Invoke(),
                                                    () => _openThemeFolderAction is not null);
        ReloadAllCssCommand      = new RelayCommand(() => _reloadAllCssAction?.Invoke(),
                                                    () => _reloadAllCssAction is not null);
        ExtractDefaultCssCommand = new RelayCommand(() => _extractDefaultCssAction?.Invoke(),
                                                    () => _extractDefaultCssAction is not null);

        RefreshCacheSizeDisplay();
    }

    private void OnAnyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressSave) return;
        // 保存を発火しないプロパティ
        switch (e.PropertyName)
        {
            case nameof(SelectedCategory):
            case nameof(RestartRequired):
            case nameof(CacheSizeDisplay):
                return;
        }
        // HiDPI / TimeoutSec の変更で再起動バナーを立てる
        if (e.PropertyName is nameof(HiDpiMode) or nameof(TimeoutSec))
        {
            UpdateRestartRequired();
        }
        // Debounce 開始 (既に走っていれば残りをリセット)
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void UpdateRestartRequired()
    {
        RestartRequired =
            HiDpiMode  != _initialConfig.HiDpiMode  ||
            TimeoutSec != _initialConfig.TimeoutSec;
    }

    private void SaveAndApply()
    {
        var snapshot = ToConfig();
        _storage.Save(snapshot);
        try { _applyCallback(snapshot); }
        catch (Exception ex) { Debug.WriteLine($"[SettingsViewModel] apply failed: {ex.Message}"); }
    }

    public AppConfig ToConfig() => new()
    {
        Version                     = 1,
        HiDpiMode                   = HiDpiMode,
        UserAgentOverride           = UserAgentOverride,
        TimeoutSec                  = TimeoutSec,
        PopularThreshold            = PopularThreshold,
        DefaultThreadViewMode       = DefaultThreadViewMode,
        ImageSizeThresholdMb        = ImageSizeThresholdMb,
        CacheMaxMb                  = CacheMaxMb,
        ViewerThumbnailSize         = ViewerThumbnailSize,
        FavoritesOpenOnSingleClick  = FavoritesOpenOnSingleClick,
        BoardListOpenOnSingleClick  = BoardListOpenOnSingleClick,
        ThreadListOpenOnSingleClick = ThreadListOpenOnSingleClick,
        ThreadListTabMiddleClickAction = ThreadListTabMiddleClickAction,
        ThreadListTabCtrlClickAction   = ThreadListTabCtrlClickAction,
        ThreadListTabShiftClickAction  = ThreadListTabShiftClickAction,
        ThreadListTabAltClickAction    = ThreadListTabAltClickAction,
        ThreadListTabDoubleClickAction = ThreadListTabDoubleClickAction,
        ThreadTabMiddleClickAction     = ThreadTabMiddleClickAction,
        ThreadTabCtrlClickAction       = ThreadTabCtrlClickAction,
        ThreadTabShiftClickAction      = ThreadTabShiftClickAction,
        ThreadTabAltClickAction        = ThreadTabAltClickAction,
        ThreadTabDoubleClickAction     = ThreadTabDoubleClickAction,
    };

    public void RefreshCacheSizeDisplay()
    {
        try
        {
            var bytes = _getCacheBytes();
            var mb    = bytes / 1024.0 / 1024.0;
            CacheSizeDisplay = $"{mb:F1} MB / {CacheMaxMb} MB";
        }
        catch
        {
            CacheSizeDisplay = "(取得失敗)";
        }
    }

    /// <summary>ウィンドウが閉じる前に呼ぶ — debounce 中の保存を確定させる。</summary>
    public void FlushPendingSave()
    {
        if (_saveTimer.IsEnabled)
        {
            _saveTimer.Stop();
            SaveAndApply();
        }
    }
}
