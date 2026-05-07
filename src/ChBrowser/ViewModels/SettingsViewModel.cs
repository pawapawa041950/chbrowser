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
    [ObservableProperty] private bool   _enableKakikomiLog     = true;
    [ObservableProperty] private string _userAgentOverride     = "";
    [ObservableProperty] private int    _timeoutSec            = 30;
    // 認証カテゴリ (どんぐりメール認証)
    [ObservableProperty] private string _donguriEmail          = "";
    [ObservableProperty] private string _donguriPassword       = "";
    /// <summary>認証パネルに表示するログイン状態のテキスト ("ログイン済" / "失敗: ..." / "試行中..." / "未設定")。
    /// 設定画面オープン時に App から最新値を流し込む + ログイン試行のたびに更新する。</summary>
    [ObservableProperty] private string _donguriLoginStatus    = "未試行";
    [ObservableProperty] private int    _popularThreshold      = 3;
    [ObservableProperty] private string _defaultThreadViewMode = "DedupTree";
    [ObservableProperty] private int    _imageSizeThresholdMb  = 5;
    [ObservableProperty] private int    _idHighlightThreshold  = 5;
    [ObservableProperty] private int    _cacheMaxMb            = 1024;
    [ObservableProperty] private int    _viewerThumbnailSize   = 80;
    [ObservableProperty] private bool   _viewerDetailsPaneDefaultOpen = true;

    // ---- Phase 11b: 3 ペインの 1 クリック設定 ----
    [ObservableProperty] private bool   _favoritesOpenOnSingleClick  = true;
    [ObservableProperty] private bool   _boardListOpenOnSingleClick  = true;
    [ObservableProperty] private bool   _threadListOpenOnSingleClick = true;

    // ---- バッチ処理の同時通信数 (お気に入りチェック等) ----
    [ObservableProperty] private int    _batchConcurrency            = 6;

    // ---- 「タブ」カテゴリ — タブ幅設定 (旧クリックアクション設定はショートカット側へ移設済) ----
    [ObservableProperty] private string _threadListTabWidthMode  = "chars";
    [ObservableProperty] private int    _threadListTabWidthChars = 15;
    [ObservableProperty] private int    _threadListTabWidthPx    = 200;
    [ObservableProperty] private string _threadTabWidthMode      = "chars";
    [ObservableProperty] private int    _threadTabWidthChars     = 15;
    [ObservableProperty] private int    _threadTabWidthPx        = 200;

    /// <summary>HiDPI / TimeoutSec を起動後に変更すると true。バナーで再起動を促す。</summary>
    [ObservableProperty]
    private bool _restartRequired;

    // ---- タブ幅モード切替用の bool ラッパ (XAML の RadioButton.IsChecked から TwoWay バインドする用) ----

    public bool IsThreadListTabWidthByChars
    {
        get => string.Equals(ThreadListTabWidthMode, "chars", StringComparison.Ordinal);
        set { if (value && !IsThreadListTabWidthByChars) ThreadListTabWidthMode = "chars"; }
    }
    public bool IsThreadListTabWidthByPx
    {
        get => string.Equals(ThreadListTabWidthMode, "px", StringComparison.Ordinal);
        set { if (value && !IsThreadListTabWidthByPx) ThreadListTabWidthMode = "px"; }
    }
    public bool IsThreadTabWidthByChars
    {
        get => string.Equals(ThreadTabWidthMode, "chars", StringComparison.Ordinal);
        set { if (value && !IsThreadTabWidthByChars) ThreadTabWidthMode = "chars"; }
    }
    public bool IsThreadTabWidthByPx
    {
        get => string.Equals(ThreadTabWidthMode, "px", StringComparison.Ordinal);
        set { if (value && !IsThreadTabWidthByPx) ThreadTabWidthMode = "px"; }
    }

    // WidthMode 変更時に対応する bool ラッパの PropertyChanged を発火 (= RadioButton 同期用)
    partial void OnThreadListTabWidthModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsThreadListTabWidthByChars));
        OnPropertyChanged(nameof(IsThreadListTabWidthByPx));
    }
    partial void OnThreadTabWidthModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsThreadTabWidthByChars));
        OnPropertyChanged(nameof(IsThreadTabWidthByPx));
    }

    /// <summary>「画像」カテゴリで現在のキャッシュ使用量を表示するための文字列 (例: "512.3 MB / 1024 MB")。
    /// 設定ウィンドウを開くたびに <see cref="RefreshCacheSizeDisplay"/> で更新する。</summary>
    [ObservableProperty]
    private string _cacheSizeDisplay = "計算中…";

    public IRelayCommand RestartNowCommand     { get; }
    public IRelayCommand OpenCacheFolderCommand{ get; }
    public IRelayCommand ClearCacheCommand     { get; }
    /// <summary>通信カテゴリの「Cookie をすべて削除」ボタン用。
    /// CookieJar 全削除 + DonguriState リセット + cookies.txt / state.json 永続化 + ステータスバー更新を呼ぶ。</summary>
    public IRelayCommand ClearCookiesCommand   { get; }

    /// <summary>認証カテゴリの「今すぐログイン」ボタン用。
    /// 入力中の値を <see cref="FlushPendingSave"/> で即時保存 → ConfigStorage に反映 → App 側でログイン試行。</summary>
    public IRelayCommand LoginNowCommand       { get; }

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
    private readonly Action?               _clearCookiesAction;
    private readonly Action?               _loginNowAction;
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
        Action?           extractDefaultCssAction  = null,
        Action?           clearCookiesAction       = null,
        Action?           loginNowAction           = null)
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
        _clearCookiesAction      = clearCookiesAction;
        _loginNowAction          = loginNowAction;

        // カテゴリ枠。NG / ショートカット / マウスジェスチャー は別ウィンドウ管理。
        Categories.Add(new("全般",         "HiDPI モード"));
        Categories.Add(new("通信",         "User-Agent、HTTP タイムアウト"));
        Categories.Add(new("認証",         "どんぐり (5ch) のメール認証"));
        Categories.Add(new("お気に入り",   "クリックで開く動作"));
        Categories.Add(new("板一覧",       "クリックで開く動作"));
        Categories.Add(new("スレッド一覧", "クリックで開く動作"));
        Categories.Add(new("スレッド",     "人気レス閾値、標準表示モード、画像 HEAD しきい値"));
        Categories.Add(new("タブ",         "タブ幅 (スレ一覧タブ / スレッドタブ)"));
        Categories.Add(new("画像",         "キャッシュ上限、キャッシュフォルダを開く、キャッシュクリア"));
        Categories.Add(new("ビューア",     "タブのサムネイルサイズ"));
        Categories.Add(new("デザイン編集", "各ペインの CSS をエディタで開いて編集する"));
        SelectedCategory = Categories[0];

        // 初期値を AppConfig から流し込む (この間は保存させない)
        _suppressSave                = true;
        HiDpiMode                    = initial.HiDpiMode;
        EnableKakikomiLog            = initial.EnableKakikomiLog;
        UserAgentOverride            = initial.UserAgentOverride;
        TimeoutSec                   = initial.TimeoutSec;
        DonguriEmail                 = initial.DonguriEmail;
        DonguriPassword              = initial.DonguriPassword;
        PopularThreshold             = initial.PopularThreshold;
        DefaultThreadViewMode        = initial.DefaultThreadViewMode;
        ImageSizeThresholdMb         = initial.ImageSizeThresholdMb;
        IdHighlightThreshold         = initial.IdHighlightThreshold;
        CacheMaxMb                   = initial.CacheMaxMb;
        ViewerThumbnailSize          = initial.ViewerThumbnailSize;
        ViewerDetailsPaneDefaultOpen = initial.ViewerDetailsPaneDefaultOpen;
        FavoritesOpenOnSingleClick   = initial.FavoritesOpenOnSingleClick;
        BatchConcurrency             = initial.BatchConcurrency;
        BoardListOpenOnSingleClick   = initial.BoardListOpenOnSingleClick;
        ThreadListOpenOnSingleClick  = initial.ThreadListOpenOnSingleClick;
        ThreadListTabWidthMode  = string.IsNullOrEmpty(initial.ThreadListTabWidthMode) ? "chars" : initial.ThreadListTabWidthMode;
        ThreadListTabWidthChars = initial.ThreadListTabWidthChars;
        ThreadListTabWidthPx    = initial.ThreadListTabWidthPx;
        ThreadTabWidthMode      = string.IsNullOrEmpty(initial.ThreadTabWidthMode)     ? "chars" : initial.ThreadTabWidthMode;
        ThreadTabWidthChars     = initial.ThreadTabWidthChars;
        ThreadTabWidthPx        = initial.ThreadTabWidthPx;
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
        ClearCookiesCommand      = new RelayCommand(() => _clearCookiesAction?.Invoke(),
                                                    () => _clearCookiesAction is not null);
        // 「今すぐログイン」: まず未保存の入力を確定 (= debounce 待ちをスキップして即 SaveAndApply) してから login。
        // これがないと「メアド入れて即ボタン押す」で古い (= 空) 値で試行されてしまう。
        LoginNowCommand          = new RelayCommand(() =>
        {
            FlushPendingSave();
            _loginNowAction?.Invoke();
        }, () => _loginNowAction is not null);

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
            case nameof(DonguriLoginStatus):  // ログイン状態は表示専用 (ConfigStorage に書かない)
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
        EnableKakikomiLog           = EnableKakikomiLog,
        UserAgentOverride           = UserAgentOverride,
        TimeoutSec                  = TimeoutSec,
        DonguriEmail                = DonguriEmail,
        DonguriPassword             = DonguriPassword,
        PopularThreshold            = PopularThreshold,
        DefaultThreadViewMode       = DefaultThreadViewMode,
        ImageSizeThresholdMb        = ImageSizeThresholdMb,
        IdHighlightThreshold        = IdHighlightThreshold,
        CacheMaxMb                  = CacheMaxMb,
        ViewerThumbnailSize         = ViewerThumbnailSize,
        ViewerDetailsPaneDefaultOpen = ViewerDetailsPaneDefaultOpen,
        FavoritesOpenOnSingleClick  = FavoritesOpenOnSingleClick,
        BatchConcurrency            = BatchConcurrency,
        BoardListOpenOnSingleClick  = BoardListOpenOnSingleClick,
        ThreadListOpenOnSingleClick = ThreadListOpenOnSingleClick,
        ThreadListTabWidthMode  = ThreadListTabWidthMode,
        ThreadListTabWidthChars = ThreadListTabWidthChars,
        ThreadListTabWidthPx    = ThreadListTabWidthPx,
        ThreadTabWidthMode      = ThreadTabWidthMode,
        ThreadTabWidthChars     = ThreadTabWidthChars,
        ThreadTabWidthPx        = ThreadTabWidthPx,
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
