using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ChBrowser.Models;
using ChBrowser.Services.Image;
using ChBrowser.Services.Storage;
using ChBrowser.ViewModels;
using ChBrowser.Views;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ChBrowser;

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

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += MainWindow_DataContextChanged;
    }

    /// <summary>外部 (= ShortcutManager) から呼ばれる、アドレスバーへフォーカス + 全選択。Phase 15 でショートカット駆動化。</summary>
    public void FocusAddressBar()
    {
        AddressBar.Focus();
        AddressBar.SelectAll();
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

    /// <summary>VM の AddressBarUrl が変わったらテキストを同期。
    /// ただしユーザがアドレスバーを編集中 (= IsKeyboardFocusWithin) は上書きしない (= 入力テキスト保持)。</summary>
    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.AddressBarUrl)) return;
        if (_vm is null) return;
        if (AddressBar.IsKeyboardFocusWithin) return;
        AddressBar.Text = _vm.AddressBarUrl;
    }

    /// <summary>アドレスバー: フォーカス取得時に全選択。Ctrl+L / Tab / クリック (= 別ハンドラ経由) すべてで効く。</summary>
    private void AddressBar_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => AddressBar.SelectAll();

    /// <summary>未フォーカスの状態でクリックした最初の 1 回は全選択にする (= ブラウザの「クリックで全選択」挙動)。
    /// 既にフォーカスがある状態でのクリックは通常通り (= キャレット位置決め) を許容。</summary>
    private void AddressBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!AddressBar.IsKeyboardFocusWithin)
        {
            AddressBar.Focus();
            e.Handled = true;
        }
    }

    /// <summary>Enter で URL ナビゲート、Esc で入力破棄して現在のタブ URL に戻す。</summary>
    private async void AddressBar_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await NavigateAddressBarAndResyncAsync(AddressBar.Text);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_vm is not null) AddressBar.Text = _vm.AddressBarUrl;
            AddressBar.SelectAll();
        }
    }

    /// <summary>VM へナビゲート要求を投げ、完了後に AddressBar.Text を「現在のタブ URL」(= ナビ後の VM 値) に同期。
    /// 成功なら新しい URL、Invalid (= 認識できない URL) なら旧 URL のまま、AddressBarHasError=true で赤枠化。</summary>
    private async Task NavigateAddressBarAndResyncAsync(string text)
    {
        if (_vm is null) return;
        await _vm.NavigateAddressBarAsync(text);
        // エラーのときは入力テキストを保持 (ユーザが直しやすい)、成功なら最新の URL に同期
        if (!_vm.AddressBarHasError) AddressBar.Text = _vm.AddressBarUrl;
        AddressBar.SelectAll();
    }

    /// <summary>アドレスバーから他へフォーカスが移った時、移動先が WebView2 (= 板/スレ表示) なら入力テキストを破棄して
    /// 現在のタブ URL に戻す。それ以外 (メニュー / 設定ウィンドウ等) なら入力テキストを保持し続ける (= 設計書 §5.2.1)。</summary>
    private void AddressBar_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_vm is null) return;
        if (IsInsideWebView(e.NewFocus as DependencyObject))
            AddressBar.Text = _vm.AddressBarUrl;
    }

    private static bool IsInsideWebView(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is WebView2) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    /// <summary>右クリックメニュー「貼り付けて移動」: クリップボード文字列を AddressBar に入れて即ナビゲート。</summary>
    private async void AddressBarPasteAndGo_Click(object sender, RoutedEventArgs e)
    {
        var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        if (string.IsNullOrWhiteSpace(text)) return;
        AddressBar.Text = text;
        await NavigateAddressBarAndResyncAsync(text);
    }

    /// <summary>右ペイン上段「スレ欄」 (ThreadListTabs) がフォーカスを取得 — VM に通知してアドレスバーを板 URL に切替。</summary>
    private void ThreadListPane_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => _vm?.MarkThreadListPaneActive();

    /// <summary>右ペイン下段「スレ表示」 (ThreadTabs) がフォーカスを取得 — VM に通知してアドレスバーをスレ URL に切替。</summary>
    private void ThreadPane_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => _vm?.MarkThreadPaneActive();

    /// <summary>タブ見出しなど WebView2 外の要素クリックは tunnel フェーズで拾う。
    /// (上段 TabControl の見出しクリックは TabItem ではなく直接 WebView2 に focus が飛ぶ場合があり、
    /// GotKeyboardFocus の bubble が HwndHost 越しに上がってこないため、こちらの経路で確実に捕捉する)。
    /// WebView2 内部のクリックは Win32 surface に吸われて上がってこないが、それは JS の paneActivated メッセージで拾う。</summary>
    private void ThreadListPane_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _vm?.MarkThreadListPaneActive();

    private void ThreadPane_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _vm?.MarkThreadPaneActive();

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

        if (IsFinitePositive(s.BoardListWidth))     ColBoardList.Width   = new GridLength(s.BoardListWidth);
        if (IsFinitePositive(s.ThreadListHeight))   RowThreadList.Height = new GridLength(s.ThreadListHeight);
        if (IsFinitePositive(s.FavoritesPaneHeight)) RowFavorites.Height = new GridLength(s.FavoritesPaneHeight);

        // 最大化は最後に当てる (Width/Height/Left/Top は RestoreBounds になる)
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private LayoutState CaptureLayout()
    {
        // 最大化中は RestoreBounds (= 通常時の bounds) を保存する。次回起動時にまず通常サイズで
        // 復元してから WindowState=Maximized で最大化、という流れで意図通りに復元できる。
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

    private void ExitMenu_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>板一覧 WebView2 (Phase 14a) からの postMessage を捌く。
    /// 旧 TreeView の MouseDoubleClick / ContextMenu の代替として、
    /// openBoard / setCategoryExpanded / contextMenu (target=board) を受ける。</summary>
    private async void BoardListWebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;

        var (type, payload) = TryParseMessage(e);

        // Phase 16: ブリッジから shortcut / gesture を受けたら ShortcutManager にディスパッチ
        if (type == "shortcut" || type == "gesture")
        {
            DispatchFromWebView(payload, "板一覧ペイン");
            return;
        }
        if (type == "gestureProgress" || type == "gestureEnd")
        {
            RouteGestureProgress(payload, type, "板一覧ペイン");
            return;
        }
        if (type == "bridgeReady")
        {
            PushBindingsTo(sender, "板一覧ペイン");
            return;
        }

        switch (type)
        {
            case "openBoard":
            {
                var host = payload.TryGetProperty("host", out var hp) ? hp.GetString() : null;
                var dir  = payload.TryGetProperty("directoryName", out var dp) ? dp.GetString() : null;
                var name = payload.TryGetProperty("name", out var np) ? np.GetString() : "";
                if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(dir)) return;
                await main.OpenBoardFromHtmlListAsync(host, dir, name ?? "");
                break;
            }
            case "setCategoryExpanded":
            {
                var cat = payload.TryGetProperty("categoryName", out var cp) ? cp.GetString() : null;
                var exp = payload.TryGetProperty("expanded",     out var ep) && ep.GetBoolean();
                if (!string.IsNullOrEmpty(cat)) main.SetCategoryExpanded(cat, exp);
                break;
            }
            case "contextMenu":
            {
                var target = payload.TryGetProperty("target", out var tp) ? tp.GetString() : null;
                if (target == "board")
                {
                    var host = payload.TryGetProperty("host", out var hp) ? hp.GetString() : null;
                    var dir  = payload.TryGetProperty("directoryName", out var dp) ? dp.GetString() : null;
                    var name = payload.TryGetProperty("name", out var np) ? np.GetString() : "";
                    if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(dir)) return;
                    ShowBoardContextMenu(host, dir, name ?? "");
                }
                break;
            }
        }
    }

    /// <summary>Window.Resources の "BoardContextMenu" を取り出して、選択された板情報を Tag に積んで popup する。
    /// PlacementMode=MousePoint なので、右クリックで動いたカーソル位置にそのまま出る。</summary>
    private void ShowBoardContextMenu(string host, string directoryName, string boardName)
    {
        if (Resources["BoardContextMenu"] is not ContextMenu menu) return;
        // (host, dir, name) のタプルを Tag に持たせ、メニュー項目クリック時にそれを取り出す
        menu.Tag                    = new BoardRef(host, directoryName, boardName);
        menu.PlacementTarget        = BoardListWebView;
        menu.Placement              = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen                 = true;
    }

    /// <summary>板一覧 WebView の右クリックメニューから「お気に入りに追加」が選ばれたときに呼ばれる
    /// (Tag に BoardRef が入っている)。Phase 14a 以前は MenuItem.DataContext から BoardViewModel を取っていた
    /// が、WebView 化により DataContext バインドができなくなったため Tag 経由に変更。</summary>
    private sealed record BoardRef(string Host, string DirectoryName, string BoardName);

    /// <summary>スレ一覧 WebView2 から行ダブルクリック通知を受け取って <see cref="MainViewModel.OpenThreadFromListAsync"/> を呼ぶ。
    /// JS は host/dir/key/title をすべて payload に乗せてくるので、お気に入りディレクトリ展開タブ
    /// (= 行ごとに異なる板由来のスレが混じる) でも板由来タブと同じハンドラで対応できる。</summary>
    private async void ThreadListWebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;

        var (type, payload) = TryParseMessage(e);

        // Phase 14: pane 内クリックでアドレスバー切替
        if (type == "paneActivated")
        {
            main.MarkThreadListPaneActive();
            return;
        }

        // Phase 16: ブリッジから shortcut / gesture を受けたら ShortcutManager にディスパッチ
        if (type == "shortcut" || type == "gesture")
        {
            DispatchFromWebView(payload, "スレ一覧表示領域");
            return;
        }
        if (type == "gestureProgress" || type == "gestureEnd")
        {
            RouteGestureProgress(payload, type, "スレ一覧表示領域");
            return;
        }
        if (type == "bridgeReady")
        {
            PushBindingsTo(sender, "スレ一覧表示領域");
            return;
        }

        if (type != "openThread") return;

        var host  = payload.TryGetProperty("host",          out var hp) ? hp.GetString() : null;
        var dir   = payload.TryGetProperty("directoryName", out var dp) ? dp.GetString() : null;
        var key   = payload.TryGetProperty("key",           out var kp) ? kp.GetString() : null;
        var title = payload.TryGetProperty("title",         out var tp) ? tp.GetString() : null;

        // logState: 一覧側で表示していたマーク状態 (None=0, Cached=1, Updated=2, Dropped=3)。
        // 受け取った値を LogMarkState に戻して OpenThreadFromListAsync にヒント渡し。
        // dat 落ちスレの 404 で「(取得失敗)」表示にならないよう、ここでヒントを伝搬する。
        LogMarkState? hint = null;
        if (payload.TryGetProperty("logState", out var lp) && lp.TryGetInt32(out var li)
            && Enum.IsDefined(typeof(LogMarkState), li))
        {
            hint = (LogMarkState)li;
        }

        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(key)) return;
        await main.OpenThreadFromListAsync(host, dir, key, title ?? "", hint);
    }

    /// <summary>中央ツールバー「新規スレ立て」ボタン。
    /// SelectedThreadListTab が板タブのときだけ有効化されている (XAML 側 DataTrigger)。</summary>
    private void NewThreadButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel main)
            main.OpenNewThreadDialog();
    }

    /// <summary>設定ウィンドウのシングルトン参照。二重起動を防ぐ + 既に開いていれば前面化のみ。</summary>
    private Views.SettingsWindow? _settingsWindow;

    // ----- Phase 11c: タブ動作 -----

    /// <summary>修飾子付き左クリック / 中クリックを設定で割り当てたアクションに振り分ける共通処理。
    /// 子要素の Button (× 等) のクリックは除外する。</summary>
    private static string? PickClickAction(MouseButtonEventArgs e, string ctrlAction, string shiftAction, string altAction, string middleAction)
    {
        // 子要素の ButtonBase を踏んだクリックは閉じる×ボタンなので除外
        if (e.OriginalSource is DependencyObject src && FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(src) is not null)
            return null;

        if (e.ChangedButton == MouseButton.Middle && e.ButtonState == MouseButtonState.Pressed)
            return middleAction;

        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed && e.ClickCount == 1)
        {
            var mods = Keyboard.Modifiers;
            if      ((mods & ModifierKeys.Control) == ModifierKeys.Control) return ctrlAction;
            else if ((mods & ModifierKeys.Shift)   == ModifierKeys.Shift)   return shiftAction;
            else if ((mods & ModifierKeys.Alt)     == ModifierKeys.Alt)     return altAction;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }

    private void ThreadListTabItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TabItem ti || ti.DataContext is not ThreadListTabViewModel tab) return;
        if (DataContext is not MainViewModel main) return;
        var cfg = main.CurrentConfig;
        var action = PickClickAction(e,
            cfg.ThreadListTabCtrlClickAction,
            cfg.ThreadListTabShiftClickAction,
            cfg.ThreadListTabAltClickAction,
            cfg.ThreadListTabMiddleClickAction);
        if (action is null or "none") return;
        main.ExecuteThreadListTabAction(tab, action);
        e.Handled = true;
    }

    private void ThreadListTabItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TabItem ti || ti.DataContext is not ThreadListTabViewModel tab) return;
        if (DataContext is not MainViewModel main) return;
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject src && FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(src) is not null) return;
        var action = main.CurrentConfig.ThreadListTabDoubleClickAction;
        if (action is "" or "none") return;
        main.ExecuteThreadListTabAction(tab, action);
        e.Handled = true;
    }

    private void ThreadTabItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TabItem ti || ti.DataContext is not ThreadTabViewModel tab) return;
        if (DataContext is not MainViewModel main) return;
        var cfg = main.CurrentConfig;
        var action = PickClickAction(e,
            cfg.ThreadTabCtrlClickAction,
            cfg.ThreadTabShiftClickAction,
            cfg.ThreadTabAltClickAction,
            cfg.ThreadTabMiddleClickAction);
        if (action is null or "none") return;
        main.ExecuteThreadTabAction(tab, action);
        e.Handled = true;
    }

    private void ThreadTabItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TabItem ti || ti.DataContext is not ThreadTabViewModel tab) return;
        if (DataContext is not MainViewModel main) return;
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject src && FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(src) is not null) return;
        var action = main.CurrentConfig.ThreadTabDoubleClickAction;
        if (action is "" or "none") return;
        main.ExecuteThreadTabAction(tab, action);
        e.Handled = true;
    }

    /// <summary>ツール → 設定... を選んだとき。設定ウィンドウをモードレスで開く (Phase 11)。
    /// Owner=this により本体は操作可能のまま設定ウィンドウは前面に固定される。</summary>
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

    /// <summary>ツール → NG 設定... を選んだとき (Phase 13)。NG マネージャをモードレス + シングルトンで開く。</summary>
    private void OpenNgWindow_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app) app.ShowNgWindow();
    }

    /// <summary>ツール → ショートカット &amp; ジェスチャー... (Phase 15)。スタブのウィンドウを開く。</summary>
    private void OpenShortcutsWindow_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app) app.ShowShortcutsWindow();
    }

    /// <summary>WebView の JS ブリッジから受信した shortcut / gesture メッセージを ShortcutManager にルーティング (Phase 16)。
    /// category は呼び出し元 (= どの WebView の WebMessageReceived か) で固定する。</summary>
    private static void DispatchFromWebView(System.Text.Json.JsonElement payload, string category)
    {
        if (Application.Current is not App app || app.ShortcutManager is not { } mgr) return;
        var descriptor = payload.TryGetProperty("descriptor", out var dp) ? dp.GetString() : null;
        if (!string.IsNullOrEmpty(descriptor)) mgr.Dispatch(category, descriptor);
    }

    /// <summary>WebView の JS ブリッジから受信したジェスチャー進捗 (gestureProgress / gestureEnd) をルーティング。
    /// type=gestureProgress は payload.value に方向列が入っている、 type=gestureEnd は category=null 扱いでクリア。</summary>
    private static void RouteGestureProgress(System.Text.Json.JsonElement payload, string type, string category)
    {
        if (Application.Current is not App app || app.ShortcutManager is not { } mgr) return;
        if (type == "gestureEnd")
        {
            mgr.NotifyGestureProgress(null, null);
            return;
        }
        var value = payload.TryGetProperty("value", out var vp) ? vp.GetString() : "";
        mgr.NotifyGestureProgress(category, value ?? "");
    }

    /// <summary>bridgeReady 受信時に、その WebView だけに setShortcutBindings を direct push する。
    /// PaneShortcutsJson は値変化が無いとき再 push されないため、bridge 側 (= 新規 navigation 後の JS) が
    /// 「初期化完了したよ」と通知してきたら必ずここで補完する。</summary>
    private static void PushBindingsTo(object? sender, string category)
    {
        if (Application.Current is not App app || app.ShortcutManager is not { } mgr) return;
        if (sender is not Microsoft.Web.WebView2.Wpf.WebView2 wv || wv.CoreWebView2 is null) return;
        var map = mgr.GetBindingsForCategory(category);
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            type     = "setShortcutBindings",
            bindings = map,
        });
        try { wv.CoreWebView2.PostWebMessageAsJson(json); }
        catch (Exception ex) { Debug.WriteLine($"[MainWindow] PushBindingsTo failed: {ex.Message}"); }
    }

    /// <summary>板一覧 WebView の右クリックメニュー「お気に入りに追加」。
    /// 親 ContextMenu の Tag に BoardRef が入っている (= ShowBoardContextMenu でセット)。</summary>
    private void AddBoardToFavorites_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        if (sender is not MenuItem mi) return;
        var owner = ItemsControl.ItemsControlFromItemContainer(mi) as ContextMenu
                  ?? mi.Parent as ContextMenu;
        if (owner?.Tag is not BoardRef br) return;
        main.AddBoardToFavoritesByHostDir(br.Host, br.DirectoryName, br.BoardName);
    }

    // ---- お気に入りペイン (Phase 14b: WebView 化) ----
    //
    // ダブルクリック / 右クリック / D&D は すべて JS の postMessage で受け取る。
    // ContextMenu は Window.Resources に定義したものを Tag に対象 id (FavoriteRef) を積んで popup。
    // クリックハンドラは Tag から id を取り出して MainViewModel の id ベース API を呼ぶ。

    /// <summary>右クリック対象を一意に表す (Window.Resources の ContextMenu の Tag に積む)。</summary>
    private sealed record FavoriteRef(Guid Id);

    /// <summary>JS から送られた id 文字列を Guid にパース (失敗時 null)。</summary>
    private static Guid? ParseId(string? s)
        => Guid.TryParse(s, out var g) ? g : null;

    /// <summary>ContextMenu (sender の親) の Tag から FavoriteRef を取り出す共通処理。</summary>
    private static FavoriteRef? RefFromMenu(object sender)
    {
        if (sender is not MenuItem mi) return null;
        var owner = ItemsControl.ItemsControlFromItemContainer(mi) as ContextMenu
                  ?? mi.Parent as ContextMenu;
        return owner?.Tag as FavoriteRef;
    }

    private async void FavoritesWebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;

        var (type, payload) = TryParseMessage(e);

        // Phase 16: ブリッジから shortcut / gesture を受けたら ShortcutManager にディスパッチ
        if (type == "shortcut" || type == "gesture")
        {
            DispatchFromWebView(payload, "お気に入りペイン");
            return;
        }
        if (type == "gestureProgress" || type == "gestureEnd")
        {
            RouteGestureProgress(payload, type, "お気に入りペイン");
            return;
        }
        if (type == "bridgeReady")
        {
            PushBindingsTo(sender, "お気に入りペイン");
            return;
        }

        switch (type)
        {
            case "openFavorite":
            {
                var idStr = payload.TryGetProperty("id", out var p) ? p.GetString() : null;
                if (ParseId(idStr) is Guid id) await main.OpenFavoriteByIdAsync(id);
                break;
            }
            case "openAllLogs":
            {
                await main.OpenAllLogsAsync();
                break;
            }
            case "setFolderExpanded":
            {
                var idStr = payload.TryGetProperty("id", out var p) ? p.GetString() : null;
                var exp   = payload.TryGetProperty("expanded", out var ep) && ep.GetBoolean();
                if (ParseId(idStr) is Guid id) main.SetFolderExpanded(id, exp);
                break;
            }
            case "moveFavorite":
            {
                var srcStr = payload.TryGetProperty("sourceId", out var sp) ? sp.GetString() : null;
                var tgtStr = payload.TryGetProperty("targetId", out var tp) ? tp.GetString() : null;
                var pos    = payload.TryGetProperty("position", out var pp) ? pp.GetString() : "after";
                if (ParseId(srcStr) is Guid src)
                    main.MoveFavoriteByIds(src, ParseId(tgtStr), pos ?? "after");
                break;
            }
            case "contextMenu":
            {
                var target = payload.TryGetProperty("target", out var tp) ? tp.GetString() : null;
                var idStr  = payload.TryGetProperty("id",     out var ip) ? ip.GetString() : null;
                ShowFavoriteContextMenu(target, ParseId(idStr));
                break;
            }
        }
    }

    /// <summary>対象種別に応じて Window.Resources から ContextMenu を取り出して popup。</summary>
    private void ShowFavoriteContextMenu(string? target, Guid? id)
    {
        var key = target switch
        {
            "folder"       => "FavoriteFolderContextMenu",
            "board"        => "FavoriteBoardContextMenu",
            "thread"       => "FavoriteThreadContextMenu",
            "virtual-root" => "FavoriteVirtualRootContextMenu",
            _              => "FavoriteRootContextMenu",
        };
        if (Resources[key] is not ContextMenu menu) return;
        menu.Tag             = id is Guid g ? new FavoriteRef(g) : null;
        menu.PlacementTarget = FavoritesWebView;
        menu.Placement       = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen          = true;
    }

    private void FavNewFolderHere_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        if (RefFromMenu(sender) is not FavoriteRef r) return;
        if (main.Favorites.FindById(r.Id) is not FavoriteFolderViewModel parent) return;
        var name = InputDialog.Prompt(this, "新規フォルダ", $"「{parent.DisplayName}」配下のフォルダ名:", "新規フォルダ");
        if (string.IsNullOrWhiteSpace(name)) return;
        main.NewFavoriteFolder(parent, name.Trim());
    }

    private void FavNewFolderRoot_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        var name = InputDialog.Prompt(this, "新規フォルダ", "ルート直下のフォルダ名:", "新規フォルダ");
        if (string.IsNullOrWhiteSpace(name)) return;
        main.NewFavoriteFolder(null, name.Trim());
    }

    private void FavRename_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        if (RefFromMenu(sender) is not FavoriteRef r) return;
        if (main.Favorites.FindById(r.Id) is not FavoriteFolderViewModel folder) return;
        var name = InputDialog.Prompt(this, "名前変更", "新しいフォルダ名:", folder.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        main.RenameFavoriteFolder(folder, name.Trim());
    }

    private void FavDelete_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        if (RefFromMenu(sender) is not FavoriteRef r) return;
        if (main.Favorites.FindById(r.Id) is not FavoriteEntryViewModel vm) return;

        var confirmMessage = vm switch
        {
            FavoriteFolderViewModel f => $"フォルダ「{f.DisplayName}」とその中身をすべて削除しますか?",
            _                         => $"「{vm.DisplayName}」をお気に入りから削除しますか?",
        };
        var result = MessageBox.Show(this, confirmMessage, "削除確認",
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (result != MessageBoxResult.OK) return;
        main.DeleteFavoriteEntry(vm);
    }

    private void FavMoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        if (RefFromMenu(sender) is not FavoriteRef r) return;
        if (main.Favorites.FindById(r.Id) is FavoriteEntryViewModel vm)
            main.MoveFavoriteEntryUp(vm);
    }

    private void FavMoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        if (RefFromMenu(sender) is not FavoriteRef r) return;
        if (main.Favorites.FindById(r.Id) is FavoriteEntryViewModel vm)
            main.MoveFavoriteEntryDown(vm);
    }

    /// <summary>「お気に入りチェック」: 仮想ルート / ファイルメニュー / お気に入りペインのリフレッシュボタン
    /// すべてここから入る。<see cref="MainViewModel.CheckFavoritesAsync"/> を fire-and-forget。</summary>
    private void FavCheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        _ = main.CheckFavoritesAsync();
    }

    /// <summary>「すべて開く」: 仮想ルート (Tag null) ならお気に入り全体、フォルダなら配下を、
    /// 板はそのまま板タブ、スレはそのままスレタブで一気に開く。</summary>
    private void FavOpenAll_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        if (RefFromMenu(sender) is FavoriteRef r)
        {
            if (main.Favorites.FindById(r.Id) is FavoriteFolderViewModel folder)
                _ = main.OpenAllInFolderAsync(folder);
        }
        else
        {
            _ = main.OpenAllInRootAsync();
        }
    }

    /// <summary>「板として開く」: 仮想ルート / フォルダ配下の全エントリを統合した板スレ一覧タブで開く
    /// (= 従来のフォルダクリック動作と同じ)。</summary>
    private void FavOpenAsBoard_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main) return;
        if (RefFromMenu(sender) is FavoriteRef r)
        {
            if (main.Favorites.FindById(r.Id) is FavoriteFolderViewModel folder)
                _ = main.OpenFavoritesFolderAsync(folder);
        }
        else
        {
            _ = main.OpenAllRootAsBoardAsync();
        }
    }

    /// <summary>スレ表示 WebView2 からの postMessage を捌く。
    /// openUrl / scrollPosition / imageMetaRequest / openInViewer (Phase 10)。</summary>
    private void ThreadViewWebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var (type, payload) = TryParseMessage(e);

        // Phase 14: pane 内クリックでアドレスバー切替
        if (type == "paneActivated")
        {
            if (DataContext is MainViewModel main) main.MarkThreadPaneActive();
            return;
        }

        // Phase 16: ブリッジから shortcut / gesture を受けたら ShortcutManager にディスパッチ
        if (type == "shortcut" || type == "gesture")
        {
            DispatchFromWebView(payload, "スレッド表示領域");
            return;
        }
        if (type == "gestureProgress" || type == "gestureEnd")
        {
            RouteGestureProgress(payload, type, "スレッド表示領域");
            return;
        }
        if (type == "bridgeReady")
        {
            PushBindingsTo(sender, "スレッド表示領域");
            return;
        }

        if (type == "openUrl")
        {
            HandleOpenUrl(payload);
            return;
        }
        if (type == "scrollPosition")
        {
            HandleScrollPosition(sender, payload);
            return;
        }
        if (type == "readMark")
        {
            HandleReadMark(sender, payload);
            return;
        }
        if (type == "imageMetaRequest")
        {
            HandleImageMetaRequest(sender, payload);
            return;
        }
        if (type == "openInViewer")
        {
            HandleOpenInViewer(payload);
            return;
        }
    }

    /// <summary>ロード済み画像クリック → 画像ビューアウィンドウに送る (Phase 10)。</summary>
    private static void HandleOpenInViewer(JsonElement payload)
    {
        if (!payload.TryGetProperty("url", out var urlProp)) return;
        var url = urlProp.GetString();
        if (string.IsNullOrEmpty(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;
        if (Application.Current is App app) app.ShowImageInViewer(url);
    }

    private static void HandleOpenUrl(JsonElement payload)
    {
        if (!payload.TryGetProperty("url", out var urlProp)) return;
        var url = urlProp.GetString();
        if (string.IsNullOrEmpty(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = uri.ToString(),
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OpenUrl] failed: {ex.Message}");
        }
    }

    private void HandleScrollPosition(object sender, JsonElement payload)
    {
        if (sender is not WebView2 wv) return;
        if (wv.DataContext is not ThreadTabViewModel tab) return;
        if (DataContext is not MainViewModel main) return;
        if (!payload.TryGetProperty("postNumber", out var numProp)) return;
        if (numProp.ValueKind != JsonValueKind.Number) return;
        if (!numProp.TryGetInt32(out var num)) return;
        main.UpdateScrollPosition(tab.Board, tab.ThreadKey, num);
    }

    /// <summary>JS が検出した「ここまで読んだ」レス番号を MainViewModel に通知 (Phase 19)。</summary>
    private void HandleReadMark(object sender, JsonElement payload)
    {
        if (sender is not WebView2 wv) return;
        if (wv.DataContext is not ThreadTabViewModel tab) return;
        if (DataContext is not MainViewModel main) return;
        if (!payload.TryGetProperty("postNumber", out var numProp)) return;
        if (numProp.ValueKind != JsonValueKind.Number) return;
        if (!numProp.TryGetInt32(out var num)) return;
        main.UpdateReadMark(tab.Board, tab.ThreadKey, num);
    }

    /// <summary>
    /// JS から「この URL の HEAD サイズを教えて」を受け取り、HEAD 結果を imageMeta メッセージで返す。
    /// JS 側はサイズしきい値 (5MB) で自動ロードを止めるかプレースホルダ表示するかを決める。
    /// </summary>
    private void HandleImageMetaRequest(object sender, JsonElement payload)
    {
        if (sender is not WebView2 wv) return;
        if (ImageMetaService is null) return;
        if (!payload.TryGetProperty("url", out var urlProp)) return;
        var url = urlProp.GetString();
        if (string.IsNullOrEmpty(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;

        _ = ReplyImageMetaAsync(wv, url);
    }

    private async Task ReplyImageMetaAsync(WebView2 wv, string url)
    {
        try
        {
            // x.com / pixiv 等の非同期展開対象ならまず実体画像 URL に展開する。
            // 展開失敗 (媒体無し / ログイン要 / API エラー) は resolvedUrl=null + ok=false で返す。
            string? resolvedUrl = null;
            var isAsync = UrlExpander.IsAsyncExpandable(url);
            if (isAsync && UrlExpander is not null)
                resolvedUrl = await UrlExpander.ExpandAsync(url).ConfigureAwait(true);

            var actualUrl = resolvedUrl ?? url;

            bool   cached = false;
            long?  size   = null;
            bool   ok;

            if (isAsync && resolvedUrl is null)
            {
                // 非同期展開を試みたが解決できなかった
                ok = false;
            }
            else
            {
                // ローカルキャッシュにあれば HEAD はスキップして cached: true で返す。
                // JS 側はこの場合、しきい値超過でも即ロードする (帯域消費がないため)。
                cached = ImageCacheService?.Contains(actualUrl) ?? false;
                if (cached)
                {
                    ok = true;
                }
                else if (ImageMetaService is not null)
                {
                    var meta = await ImageMetaService.GetAsync(actualUrl).ConfigureAwait(true);
                    ok   = meta.Ok;
                    size = meta.Size;
                }
                else
                {
                    ok = false;
                }
            }

            // WebView2 が破棄済みなら CoreWebView2 が null。早期 return。
            if (wv.CoreWebView2 is null) return;
            var json = JsonSerializer.Serialize(new
            {
                type        = "imageMeta",
                url,
                resolvedUrl,
                ok,
                size,
                cached,
            });
            wv.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ImageMeta] reply failed: {ex.Message}");
        }
    }

    /// <summary>WebMessage を JSON として読んで (type, ルート要素) を返す。</summary>
    private static (string Type, JsonElement Root) TryParseMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            if (string.IsNullOrEmpty(json)) return ("", default);
            using var doc = JsonDocument.Parse(json);
            // 直値 (object) として渡しているので RootElement は object になる想定
            var root = doc.RootElement.Clone(); // dispose 後も使えるようコピー
            var type = root.TryGetProperty("type", out var typeProp) ? (typeProp.GetString() ?? "") : "";
            return (type, root);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebMessage] parse failed: {ex.Message}");
            return ("", default);
        }
    }
}
