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
/// Phase 23 で大幅再構成: ペイン本体は <c>Views/Panes/*.xaml</c> 4 つの UserControl に分離し、
/// MainWindow は「ウィンドウ chrome (メニュー / アドレスバー / ステータスバー) + 4 ペインを binary tree
/// レイアウトで配置するカスタム Panel」だけを持つ。
///
/// <para>partial 分割: 本ファイル (core + メニュー) / MainWindow.AddressBar.cs (アドレスバー)。
/// 旧 MainWindow.Tabs.cs / MainWindow.WebMessages.cs はペインの UserControl に移動して撤去。</para></summary>
public partial class MainWindow : Window
{
    /// <summary>App.OnStartup で設定される。imageMetaRequest の処理に使う (ThreadDisplayPane が参照)。</summary>
    public ImageMetaService? ImageMetaService { get; set; }

    /// <summary>App.OnStartup で設定される。imageMetaRequest 時にローカルキャッシュ済かを判定する。</summary>
    public ImageCacheService? ImageCacheService { get; set; }

    /// <summary>App.OnStartup で設定される。スレ表示でのホバーポップアップ (AI 生成画像のメタ表示) で使う。</summary>
    public AiImageMetadataService? AiImageMetadataService { get; set; }

    /// <summary>App.OnStartup で設定される。x.com / pixiv 等のページ URL を実体画像 URL に展開する。</summary>
    public UrlExpander? UrlExpander { get; set; }

    /// <summary>App.OnStartup で設定される。ウィンドウ位置/サイズ/ペインレイアウトの永続化用。</summary>
    public LayoutStorage? LayoutStorage { get; set; }

    private MainViewModel? _vm;

    /// <summary>設定ウィンドウのシングルトン参照。</summary>
    private Views.SettingsWindow? _settingsWindow;

    /// <summary>ログウィンドウのシングルトン参照。lazy 生成 (= 表示メニューで初めて ON にしたとき作る)、
    /// 「✕」で閉じても破棄せず Hide のみ (= 再表示時はインスタンス再利用)。</summary>
    private Views.LogWindow? _logWindow;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += MainWindow_DataContextChanged;
        Loaded             += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // デフォルトレイアウトをロード時に流し込む (LayoutStorage から後で上書きする可能性あり)。
        // OnSourceInitialized よりも後 (= visual tree 構築後) に呼ばないと AdornerLayer が無くて D&D 装飾が機能しない。
        if (LayoutHost.Layout is null) LayoutHost.Layout = PaneLayoutOps.BuildDefault();
    }

    private void MainWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= Vm_PropertyChanged;
        _vm = DataContext as MainViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += Vm_PropertyChanged;
        AddressBar.Text = _vm.AddressBarUrl;
        // 初期状態 (= 起動直後の VM 既定値) をログウィンドウの表示に反映。
        ApplyLogWindowVisibility(_vm.IsLogPaneVisible);
    }

    // -----------------------------------------------------------------
    // ウィンドウのレイアウト保存・復元
    // -----------------------------------------------------------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (LayoutStorage is null) return;
        var state = LayoutStorage.Load();
        if (state is null) return;
        ApplyLayout(state);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        // 開いているすべてのスレタブの「最後にスクロールしていた位置」を idx.json に flush。
        // 通常のタブクローズは MainViewModel の CollectionChanged で拾えるが、アプリ終了は ThreadTabs が
        // 解体されないまま MainWindow が閉じるので、ここで明示的に flush する必要がある。
        try { _vm?.FlushAllThreadScrollPositionsToDisk(); }
        catch (Exception ex) { Debug.WriteLine($"[MainWindow] scroll pos flush failed: {ex.Message}"); }

        if (LayoutStorage is null) return;
        try { LayoutStorage.Save(CaptureLayout()); }
        catch (Exception ex) { Debug.WriteLine($"[MainWindow] layout save failed: {ex.Message}"); }
    }

    private void ApplyLayout(LayoutState s)
    {
        if (IsFinitePositive(s.WindowWidth)  && s.WindowWidth  >= MinWidth)  Width  = s.WindowWidth;
        if (IsFinitePositive(s.WindowHeight) && s.WindowHeight >= MinHeight) Height = s.WindowHeight;
        if (IsFinite(s.WindowLeft)) Left = s.WindowLeft;
        if (IsFinite(s.WindowTop))  Top  = s.WindowTop;

        // ペインレイアウト: 保存値が valid (= 4 leaf 揃ってる) ならそれを採用、
        // null / 不正 ならデフォルトレイアウト。
        var pane = s.PaneLayout;
        LayoutHost.Layout = (pane is not null && pane.IsValidFullLayout())
            ? pane
            : PaneLayoutOps.BuildDefault();

        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private LayoutState CaptureLayout()
    {
        var bounds = WindowState == WindowState.Maximized
            ? RestoreBounds
            : new Rect(Left, Top, Width, Height);

        // ビューアウィンドウのジオメトリは App が保持している (= 開いていれば現在値、未起動なら起動時読み込み値)。
        // どちらも null になり得るが、null は「保存項目なし」として layout.json に出る (受信側でデフォルトに戻る)。
        var viewer = (Application.Current as App)?.CaptureViewerGeometryForSave();

        return new LayoutState(
            WindowLeft:      bounds.Left,
            WindowTop:       bounds.Top,
            WindowWidth:     bounds.Width,
            WindowHeight:    bounds.Height,
            WindowMaximized: WindowState == WindowState.Maximized,
            PaneLayout:      LayoutHost.Layout?.Clone(),
            ViewerWindow:    viewer);
    }

    private static bool IsFinite(double v)         => !double.IsNaN(v) && !double.IsInfinity(v);
    private static bool IsFinitePositive(double v) => IsFinite(v) && v > 0;

    /// <summary>ペインレイアウトが変わった (drag / drop / splitter リサイズ) 通知。
    /// 即時保存はしない (= ウィンドウ閉じる時に <see cref="OnClosing"/> でまとめて保存)。
    /// 必要に応じてここで debounce 保存を入れることも可能。</summary>
    private void LayoutHost_LayoutChanged(object? sender, EventArgs e)
    {
        // 現状は no-op。OnClosing で保存される。
    }

    // -----------------------------------------------------------------
    // メニュー → ウィンドウ起動 / 終了 / 各種アクション
    // -----------------------------------------------------------------

    private void ExitMenu_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>ファイルメニュー → 「お気に入りチェック」。<see cref="MainViewModel.CheckFavoritesAsync"/> を fire-and-forget。</summary>
    private void MenuFavCheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        _ = (DataContext as MainViewModel)?.CheckFavoritesAsync();
    }

    /// <summary>表示メニュー → 「ペインレイアウトをリセット」。デフォルトレイアウトに戻す。</summary>
    private void ResetLayoutMenu_Click(object sender, RoutedEventArgs e)
    {
        LayoutHost.Layout = PaneLayoutOps.BuildDefault();
    }

    /// <summary>VM.IsLogPaneVisible の変化に追従してログウィンドウを show / hide する。
    /// 初回 ON で lazy 生成 (= MainWindow を Owner にして生成、画面右上に初期配置)。
    /// 「✕」で閉じられた場合は <see cref="LogWindow.UserClosed"/> 経由で VM のフラグを落として
    /// メニューのチェックを外す。</summary>
    private void ApplyLogWindowVisibility(bool visible)
    {
        if (visible)
        {
            if (_logWindow is null)
            {
                _logWindow = new Views.LogWindow { Owner = this };
                // 初期位置は MainWindow の右隣 (= 画面端を超えそうなら下にずらす)
                _logWindow.Left = Math.Max(0, Left + Width - _logWindow.Width - 40);
                _logWindow.Top  = Math.Max(0, Top + 60);
                _logWindow.UserClosed += (_, _) =>
                {
                    if (_vm is not null) _vm.IsLogPaneVisible = false;
                };
            }
            _logWindow.Show();
            _logWindow.Activate();
        }
        else
        {
            _logWindow?.Hide();
        }
    }

    /// <summary>ツール → 設定...</summary>
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
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            // App 側の参照もクリアして死蔵参照経由の DonguriLoginStatus 更新を防ぐ。
            if (Application.Current is App a) a.NotifySettingsClosed();
        };
        _settingsWindow.Show();
    }

    /// <summary>ツール → NG 設定...</summary>
    private void OpenNgWindow_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app) app.ShowNgWindow();
    }

    /// <summary>ツール → ショートカット &amp; ジェスチャー...</summary>
    private void OpenShortcutsWindow_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app) app.ShowShortcutsWindow();
    }
}
