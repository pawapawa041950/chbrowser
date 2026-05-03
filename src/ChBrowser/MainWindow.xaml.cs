using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using ChBrowser.Models;
using ChBrowser.Services.Image;
using ChBrowser.Services.Storage;
using ChBrowser.ViewModels;

namespace ChBrowser;

/// <summary>メインウィンドウのコードビハインド。
/// 機能領域別に partial 分割している:
/// <list type="bullet">
/// <item><description><see cref="MainWindow"/> (本ファイル): ctor / DataContext 連携 / レイアウト保存復元 / メニュー → ウィンドウ起動</description></item>
/// <item><description><c>MainWindow.AddressBar.cs</c>: アドレスバー (フォーカス・Enter・Esc・貼り付け移動)</description></item>
/// <item><description><c>MainWindow.Tabs.cs</c>: タブ系 / ペインフォーカス系のイベント</description></item>
/// <item><description><c>MainWindow.WebMessages.cs</c>: 4 ペインの WebView2 postMessage 受信ハンドラとコンテキストメニュー popup</description></item>
/// </list></summary>
public partial class MainWindow : Window
{
    /// <summary>App.OnStartup で設定される。imageMetaRequest メッセージの処理に使う。</summary>
    public ImageMetaService? ImageMetaService { get; set; }

    /// <summary>App.OnStartup で設定される。imageMetaRequest 時にローカルキャッシュ済みかを判定する。</summary>
    public ImageCacheService? ImageCacheService { get; set; }

    /// <summary>App.OnStartup で設定される。x.com / pixiv 等のページ URL を実体画像 URL に展開する。</summary>
    public UrlExpander? UrlExpander { get; set; }

    /// <summary>App.OnStartup で設定される。ウィンドウ位置/サイズ/ペイン幅の永続化用。</summary>
    public LayoutStorage? LayoutStorage { get; set; }

    private MainViewModel? _vm;

    /// <summary>設定ウィンドウのシングルトン参照。二重起動を防ぐ + 既に開いていれば前面化のみ。</summary>
    private Views.SettingsWindow? _settingsWindow;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += MainWindow_DataContextChanged;
    }

    /// <summary>DataContext として MainViewModel が設定されたら、AddressBarUrl の PropertyChanged を購読して
    /// アドレスバー表示を同期する。<see cref="App"/> 側で DataContext が設定されるため、ここでは reactive に拾う。</summary>
    private void MainWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= Vm_PropertyChanged;
        _vm = DataContext as MainViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += Vm_PropertyChanged;
        AddressBar.Text = _vm.AddressBarUrl;
    }

    // -----------------------------------------------------------------
    // ウィンドウのレイアウト保存・復元
    // -----------------------------------------------------------------

    /// <summary>HWND が確保された直後 (まだ Show 前) にレイアウトを当てる。
    /// XAML で指定したデフォルト値を上書きするので、Show 時点でユーザーの最後のサイズで開く。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (LayoutStorage is null) return;
        var state = LayoutStorage.Load();
        if (state is null) return;
        ApplyLayout(state);
    }

    /// <summary>クローズ時に現在のレイアウトを書き出す。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (LayoutStorage is null) return;
        try { LayoutStorage.Save(CaptureLayout()); }
        catch (Exception ex) { Debug.WriteLine($"[MainWindow] layout save failed: {ex.Message}"); }
    }

    private void ApplyLayout(LayoutState s)
    {
        // 値の妥当性を軽くチェック (NaN / 負値 / 異常に大きい値はスルー)
        if (IsFinitePositive(s.WindowWidth)  && s.WindowWidth  >= MinWidth)  Width  = s.WindowWidth;
        if (IsFinitePositive(s.WindowHeight) && s.WindowHeight >= MinHeight) Height = s.WindowHeight;
        if (IsFinite(s.WindowLeft)) Left = s.WindowLeft;
        if (IsFinite(s.WindowTop))  Top  = s.WindowTop;

        if (IsFinitePositive(s.BoardListWidth))      ColBoardList.Width   = new GridLength(s.BoardListWidth);
        if (IsFinitePositive(s.ThreadListHeight))    RowThreadList.Height = new GridLength(s.ThreadListHeight);
        if (IsFinitePositive(s.FavoritesPaneHeight)) RowFavorites.Height  = new GridLength(s.FavoritesPaneHeight);

        // 最大化は最後に当てる (Width/Height/Left/Top は RestoreBounds になる)
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private LayoutState CaptureLayout()
    {
        // 最大化中は RestoreBounds (= 通常時の bounds) を保存する。
        var bounds = WindowState == WindowState.Maximized
            ? RestoreBounds
            : new Rect(Left, Top, Width, Height);

        return new LayoutState(
            WindowLeft:          bounds.Left,
            WindowTop:           bounds.Top,
            WindowWidth:         bounds.Width,
            WindowHeight:        bounds.Height,
            WindowMaximized:     WindowState == WindowState.Maximized,
            BoardListWidth:      ColBoardList.ActualWidth,
            ThreadListHeight:    RowThreadList.ActualHeight,
            FavoritesPaneHeight: RowFavorites.ActualHeight);
    }

    private static bool IsFinite(double v)         => !double.IsNaN(v) && !double.IsInfinity(v);
    private static bool IsFinitePositive(double v) => IsFinite(v) && v > 0;

    // -----------------------------------------------------------------
    // メニュー → ウィンドウ起動 / 終了
    // -----------------------------------------------------------------

    private void ExitMenu_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>ツール → 設定... モードレス + シングルトンで開く (Phase 11)。</summary>
    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }
        if (Application.Current is not App app) return;
        var vm = app.CreateSettingsViewModel();
        _settingsWindow = new Views.SettingsWindow(vm) { Owner = this };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    /// <summary>ツール → NG 設定... NG マネージャをモードレス + シングルトンで開く (Phase 13)。</summary>
    private void OpenNgWindow_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app) app.ShowNgWindow();
    }

    /// <summary>ツール → ショートカット &amp; ジェスチャー... (Phase 15)。</summary>
    private void OpenShortcutsWindow_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app) app.ShowShortcutsWindow();
    }
}
