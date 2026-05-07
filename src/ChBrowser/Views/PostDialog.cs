using System;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ChBrowser.Controls;
using ChBrowser.Models;
using ChBrowser.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ChBrowser.Views;

/// <summary>
/// 設計書 §5.6 の投稿フォーム。レス書き込みとスレ立てのどちらでも使う。
/// 既存の <see cref="InputDialog"/> にならって programmatic 構築 (XAML を別ファイルにしない)。
/// バインディング元は <see cref="PostFormViewModel"/>。
///
/// <para>左側にプレビューペインを持ち、「レビュー表示」ボタンで toggle できる (= post.html / post.css を
/// そのまま適用したスレ表示シェルを WebView2 で流用、name/mail/body の入力で都度更新)。
/// プレビューを開くとウィンドウが「左へ」拡大する (= 右端固定)。</para>
/// </summary>
public sealed class PostDialog : Window
{
    // ---- preview ペイン (= 左側) ----
    /// <summary>ペイン幅 (px)。書き込み欄と同サイズに揃える、というユーザ要件に基づく。</summary>
    private const double PreviewPaneWidth = 520;

    private bool                _isPreviewVisible;
    private bool                _previewShellReady;
    private double              _savedLeft;
    private double              _savedWidth;
    private double              _savedMinWidth;
    private ColumnDefinition?   _previewCol;
    private Border?             _previewBorder;
    private WebView2?           _previewWebView;
    private DispatcherTimer?    _previewDebounceTimer;
    private Button?             _previewToggleBtn;
    private Grid?               _outerGrid;

    private readonly PostFormViewModel _vm;

    /// <summary>送信成功で閉じられたかどうか。モードレスで開いている (= DialogResult を使えない) ため
    /// 呼び出し側はこのフラグを Closed イベント内で見て後処理 (RefreshThread 等) するかを判定する。</summary>
    public bool WasSubmitted { get; private set; }

    public PostDialog(PostFormViewModel vm, Window? owner)
    {
        _vm                   = vm;
        DataContext           = vm;
        Title                 = vm.DialogTitle;
        Width                 = 520;
        Height                = vm.IsNewThread ? 460 : 420;
        MinWidth              = 420;
        MinHeight             = 320;
        Owner                 = owner;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ShowInTaskbar         = false;

        Content = BuildLayout();

        vm.PropertyChanged += OnVmPropertyChanged;
        Closed             += (_, _) =>
        {
            vm.PropertyChanged -= OnVmPropertyChanged;
            _previewDebounceTimer?.Stop();
        };
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 送信成功時、ViewModel が ShouldClose=true にしたタイミングで閉じる
        if (e.PropertyName == nameof(PostFormViewModel.ShouldClose) && _vm.ShouldClose)
        {
            WasSubmitted = true;
            Close();
        }
        // プレビュー反映対象 (= 名前 / メール / 本文 / 題名 / IsSage)。
        if (e.PropertyName is nameof(PostFormViewModel.Name)
                          or nameof(PostFormViewModel.Mail)
                          or nameof(PostFormViewModel.Message)
                          or nameof(PostFormViewModel.IsSage))
        {
            SchedulePreviewPush();
        }
    }

    /// <summary>外側 Grid (col 0=preview / col 1=form) を組み立てて返す。
    /// preview は初期状態 0 幅、トグルで <see cref="PreviewPaneWidth"/>。</summary>
    private Grid BuildLayout()
    {
        _outerGrid = new Grid();
        _previewCol = new ColumnDefinition { Width = new GridLength(0) };
        _outerGrid.ColumnDefinitions.Add(_previewCol);
        _outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 左ペイン (preview) — 初期は 0 幅。要求があれば lazy で WebView2 を生成。
        _previewBorder = new Border
        {
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Background      = Brushes.White,
            Visibility      = Visibility.Collapsed,
        };
        Grid.SetColumn(_previewBorder, 0);
        _outerGrid.Children.Add(_previewBorder);

        // 右ペイン (form) — 既存ロジック
        var form = BuildFormPanel();
        Grid.SetColumn(form, 1);
        _outerGrid.Children.Add(form);

        return _outerGrid;
    }

    private Grid BuildFormPanel()
    {
        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // (0) スレ立てなら subject、レスならスキップ
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // (1) Name + Mail + sage
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // (2) "本文:"
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // (3) message
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // (4) error banner
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // (5) status + buttons

        // (0) Subject (スレ立て時のみ)。レス書き込みなら row 0 は空 = Auto は 0 高さ。
        if (_vm.IsNewThread)
        {
            var subjectRow = BuildLabeledTextBox("題名:", nameof(PostFormViewModel.Subject));
            subjectRow.Margin = new Thickness(0, 0, 0, 8);
            Grid.SetRow(subjectRow, 0);
            root.Children.Add(subjectRow);
        }

        // (1) Name + Mail + sage
        var headerRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });             // "名前:"
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) }); // name box
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });             // "メール:"
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });   // mail box
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });             // sage check

        var nameLabel = new TextBlock { Text = "名前:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        Grid.SetColumn(nameLabel, 0); headerRow.Children.Add(nameLabel);

        var nameBox = new TextBox { Margin = new Thickness(0, 0, 12, 0) };
        nameBox.SetBinding(TextBox.TextProperty, new Binding(nameof(PostFormViewModel.Name)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        Grid.SetColumn(nameBox, 1); headerRow.Children.Add(nameBox);

        var mailLabel = new TextBlock { Text = "メール:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        Grid.SetColumn(mailLabel, 2); headerRow.Children.Add(mailLabel);

        var mailBox = new TextBox { Margin = new Thickness(0, 0, 12, 0) };
        mailBox.SetBinding(TextBox.TextProperty, new Binding(nameof(PostFormViewModel.Mail)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        Grid.SetColumn(mailBox, 3); headerRow.Children.Add(mailBox);

        var sageCheck = new CheckBox { Content = "sage", VerticalAlignment = VerticalAlignment.Center };
        sageCheck.SetBinding(ToggleButton_IsCheckedProxy, new Binding(nameof(PostFormViewModel.IsSage)) { Mode = BindingMode.TwoWay });
        Grid.SetColumn(sageCheck, 4); headerRow.Children.Add(sageCheck);

        // 認証モード行 (= 投稿時に送る Cookie 集合の切替) と headerRow を 1 つの StackPanel にまとめて
        // Grid 行 1 (Auto) にまとめて配置する。メール認証 Lv が高くて目立つのを避けたい時に
        // 「Cookie だけ送る」「全く送らない」を選べるようにしてある。
        var authRow = BuildAuthModeRow();
        authRow.Margin = new Thickness(0, 0, 0, 8);

        var headerStack = new StackPanel();
        headerStack.Children.Add(authRow);
        headerStack.Children.Add(headerRow);
        Grid.SetRow(headerStack, 1);
        root.Children.Add(headerStack);

        // (2) "本文:" ラベル
        var bodyLabel = new TextBlock { Text = "本文:", Margin = new Thickness(0, 0, 0, 4) };
        Grid.SetRow(bodyLabel, 2);
        root.Children.Add(bodyLabel);

        // (3) message text box (multi-line)
        var msgBox = new TextBox
        {
            AcceptsReturn         = true,
            AcceptsTab            = true,
            TextWrapping          = TextWrapping.Wrap,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily            = new FontFamily("Yu Gothic UI, Meiryo, Segoe UI"),
            MinHeight             = 120,
        };
        msgBox.SetBinding(TextBox.TextProperty, new Binding(nameof(PostFormViewModel.Message)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        Grid.SetRow(msgBox, 3);
        root.Children.Add(msgBox);

        // (4) エラーバナー (ErrorMessage が空でないとき表示)
        var errorBanner = new Border
        {
            Background  = new SolidColorBrush(Color.FromRgb(0xFC, 0xE4, 0xE4)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0x7A, 0x7A)),
            BorderThickness = new Thickness(1),
            Padding     = new Thickness(8, 4, 8, 4),
            Margin      = new Thickness(0, 8, 0, 0),
            Visibility  = Visibility.Collapsed,
        };
        var errorText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground   = new SolidColorBrush(Color.FromRgb(0x80, 0x20, 0x20)),
        };
        errorText.SetBinding(TextBlock.TextProperty, new Binding(nameof(PostFormViewModel.ErrorMessage)));
        errorBanner.Child = errorText;
        // ErrorMessage が空文字のとき Collapsed にしたいので DataTrigger 相当を Style で組む
        errorBanner.SetBinding(VisibilityProperty, new Binding(nameof(PostFormViewModel.ErrorMessage))
        {
            Converter = new EmptyStringToVisibilityConverter(),
        });
        Grid.SetRow(errorBanner, 4);
        root.Children.Add(errorBanner);

        // (5) ステータス + レビュー / 送信 / 取消
        var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var statusText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray };
        statusText.SetBinding(TextBlock.TextProperty, new Binding(nameof(PostFormViewModel.StatusMessage)));
        Grid.SetColumn(statusText, 0);
        footer.Children.Add(statusText);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

        _previewToggleBtn = new Button { Content = "レビュー表示", Width = 100, Margin = new Thickness(0, 0, 8, 0) };
        _previewToggleBtn.Click += (_, _) => TogglePreview();
        btnPanel.Children.Add(_previewToggleBtn);

        var sendBtn = new Button { Content = "送信", Width = 90, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        sendBtn.SetBinding(ButtonBase_CommandProxy, new Binding(nameof(PostFormViewModel.SubmitCommand)));
        btnPanel.Children.Add(sendBtn);

        var cancelBtn = new Button { Content = "取消", Width = 90, IsCancel = true };
        cancelBtn.Click += (_, _) =>
        {
            // モードレス (= DialogResult が使えない) ので Close のみ。WasSubmitted は false のまま
            if (!_vm.IsBusy) { Close(); }
        };
        btnPanel.Children.Add(cancelBtn);

        Grid.SetColumn(btnPanel, 1);
        footer.Children.Add(btnPanel);

        Grid.SetRow(footer, 5);
        root.Children.Add(footer);

        Loaded += (_, _) =>
        {
            // 編集 UX: スレ立てなら題名から、レスなら本文からフォーカス
            if (_vm.IsNewThread)
            {
                var subjectBoxes = FindVisualChildrenOfType<TextBox>(this);
                foreach (var tb in subjectBoxes)
                {
                    var binding = BindingOperations.GetBinding(tb, TextBox.TextProperty);
                    if (binding?.Path?.Path == nameof(PostFormViewModel.Subject))
                    {
                        Keyboard.Focus(tb);
                        break;
                    }
                }
            }
            else
            {
                Keyboard.Focus(msgBox);
            }
        };

        return root;
    }

    // -----------------------------------------------------------------
    // プレビュー
    // -----------------------------------------------------------------

    /// <summary>レビュー表示 / 非表示を切り替える。表示時は「右端固定で左方向に拡大」する。</summary>
    private void TogglePreview()
    {
        if (_outerGrid is null || _previewCol is null || _previewBorder is null || _previewToggleBtn is null) return;

        if (!_isPreviewVisible)
        {
            // 現在のジオメトリを保存 (閉じる時に戻す)
            _savedLeft     = Left;
            _savedWidth    = Width;
            _savedMinWidth = MinWidth;

            // 左方向に拡大 (= 右端を固定)。スクリーン左端からはみ出すなら 0 で頭打ち。
            var newLeft  = Math.Max(0, Left - PreviewPaneWidth);
            var deltaLeft = Left - newLeft;
            Left  = newLeft;
            Width = Width + deltaLeft;
            MinWidth = _savedMinWidth + PreviewPaneWidth;

            _previewCol.Width    = new GridLength(PreviewPaneWidth);
            _previewBorder.Visibility = Visibility.Visible;

            // 初回トグルで lazy に WebView2 を生成。シェルが nav 完了したら初期 push される。
            if (_previewWebView is null) InitializePreviewWebView();

            _previewToggleBtn.Content = "レビュー閉じる";
            _isPreviewVisible = true;

            // 既に shell ready ならその場で push (= 状態切替えに即追従)
            SchedulePreviewPush();
        }
        else
        {
            _previewCol.Width = new GridLength(0);
            _previewBorder.Visibility = Visibility.Collapsed;

            // 元のジオメトリに戻す。Width を先に縮めてから Left を戻す (途中で MinWidth 違反が出ないように)。
            MinWidth = _savedMinWidth;
            Width    = _savedWidth;
            Left     = _savedLeft;

            _previewToggleBtn.Content = "レビュー表示";
            _isPreviewVisible = false;
        }
    }

    private void InitializePreviewWebView()
    {
        _previewWebView = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.White };
        _previewBorder!.Child = _previewWebView;
        // プレビュー内の画像サムネクリック等を本アプリのビューアに転送するため、
        // WebMessageReceived を購読する (= スレ表示と同様の挙動を再現)。
        _previewWebView.WebMessageReceived += OnPreviewWebMessageReceived;
        _ = NavigatePreviewWebViewAsync();
    }

    /// <summary>プレビュー WebView2 からの JS メッセージを処理する。
    /// 現状 <c>openInViewer</c> (画像サムネクリック) のみハンドル — 画像ビューアに送る。
    /// 他 (openUrl, anchor 等) はプレビュー文脈では意味が薄いので無視。</summary>
    private void OnPreviewWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var tp) ? tp.GetString() : null;
            if (type != "openInViewer") return;
            if (!root.TryGetProperty("url", out var up)) return;
            var url = up.GetString();
            if (string.IsNullOrEmpty(url)) return;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;
            if (System.Windows.Application.Current is App app) app.ShowImageInViewer(url);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PostDialog] preview message failed: {ex.Message}");
        }
    }

    private async Task NavigatePreviewWebViewAsync()
    {
        try
        {
            // EnsureCoreAsync 経由で初期化 (= 画像キャッシュ + pixiv Referer ハンドラを同梱で install)。
            // 直接 EnsureCoreWebView2Async だけ呼ぶと WebResourceRequested が無く、cache 透過と Referer 補正が効かない。
            await WebView2Helper.EnsureCoreAsync(_previewWebView!).ConfigureAwait(true);
            var tcs = new TaskCompletionSource();
            void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e)
            {
                _previewWebView!.CoreWebView2.NavigationCompleted -= OnNav;
                tcs.TrySetResult();
            }
            _previewWebView!.CoreWebView2.NavigationCompleted += OnNav;
            _previewWebView.CoreWebView2.NavigateToString(WebView2Helper.LoadThreadShellHtml());
            await tcs.Task.ConfigureAwait(true);
            _previewShellReady = true;
            PushPreview();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PostDialog] preview shell nav failed: {ex.Message}");
        }
    }

    /// <summary>入力変化に対して 200ms debounce をかけてプレビューを更新する。
    /// 連続タイピング中に毎回 push すると WebView の再描画コストが嵩むため。</summary>
    private void SchedulePreviewPush()
    {
        if (!_isPreviewVisible) return;
        if (_previewDebounceTimer is null)
        {
            _previewDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _previewDebounceTimer.Tick += (_, _) =>
            {
                _previewDebounceTimer!.Stop();
                PushPreview();
            };
        }
        _previewDebounceTimer.Stop();
        _previewDebounceTimer.Start();
    }

    private void PushPreview()
    {
        if (!_previewShellReady || _previewWebView?.CoreWebView2 is null) return;
        if (!_isPreviewVisible) return;

        // WPF TextBox は \r\n で改行が来るが、thread.js の processPlain は \n でしか split しない。
        // post-body の white-space: pre-wrap が残った \r を改行扱いしてしまい、<br> と二重になるので
        // ここで \n 単一に正規化する。dat 由来の Post.Body は元々 \n だけなので thread 表示側は無対策で OK。
        var bodyNormalized = (_vm.Message ?? "").Replace("\r\n", "\n").Replace("\r", "\n");

        var post = new
        {
            number      = 1,
            name        = string.IsNullOrEmpty(_vm.Name) ? "名無しさん" : _vm.Name,
            mail        = _vm.Mail ?? "",
            dateText    = FormatDateLikeDat(DateTime.Now),
            id          = "PreviewID0",
            body        = bodyNormalized,
            threadTitle = (string?)null,
        };
        var json = JsonSerializer.Serialize(new { type = "setPreview", post });
        try { _previewWebView.CoreWebView2.PostWebMessageAsJson(json); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[PostDialog] preview push failed: {ex.Message}"); }
    }

    /// <summary>dat フォーマットの日付文字列を生成 (例: "2026/05/05(火) 12:34:56.78")。
    /// 5ch のレス表示は曜日を日本語 1 文字で出すので、英語の DayOfWeek を ja に変換する。</summary>
    private static string FormatDateLikeDat(DateTime dt)
    {
        var weekday = new[] { "日", "月", "火", "水", "木", "金", "土" }[(int)dt.DayOfWeek];
        return $"{dt:yyyy/MM/dd}({weekday}) {dt:HH:mm:ss}.{(dt.Millisecond / 10):00}";
    }

    /// <summary>「認証: [○なし] [●Cookie] [○メール認証]」の 1 行 RadioButton 群を組み立てる。
    /// VM.AuthMode と双方向同期する (各 RadioButton の Checked イベントで VM 更新、
    /// VM.PropertyChanged で UI 反映)。</summary>
    private FrameworkElement BuildAuthModeRow()
    {
        const string groupName = "PostAuthModeGroup";

        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        var label = new TextBlock
        {
            Text                = "どんぐり:",
            VerticalAlignment   = VerticalAlignment.Center,
            Margin              = new Thickness(0, 0, 8, 0),
        };
        panel.Children.Add(label);

        RadioButton MakeRadio(string content, PostAuthMode mode, string tooltip)
        {
            var r = new RadioButton
            {
                Content             = content,
                GroupName           = groupName,
                Margin              = new Thickness(0, 0, 12, 0),
                VerticalAlignment   = VerticalAlignment.Center,
                ToolTip             = tooltip,
                IsChecked           = _vm.AuthMode == mode,
            };
            // ユーザクリック → VM 更新。VM 側からの変更ループは IsChecked 比較で抑止される (SetCurrentValue 経路)。
            r.Checked += (_, _) => { if (_vm.AuthMode != mode) _vm.AuthMode = mode; };
            return r;
        }

        var rNone   = MakeRadio("なし",           PostAuthMode.None,
                                "Cookie を一切付けずに投稿。初回投稿相当 (= サーバが新規 anon acorn を発行する経路)。");
        var rCookie = MakeRadio("通常",            PostAuthMode.Cookie,
                                "通常 (anon) acorn / MonaTicket だけ送る。メール認証 acorn とは別スロットで管理され、3 時間以内に再投稿し続ける限り Lv が積み上がる。");
        var rMail   = MakeRadio("メール認証",      PostAuthMode.MailAuth,
                                "メール認証 acorn + 関連 Cookie を全部送る (= 最高 Lv)。設定 → 認証 で事前ログイン要。");

        panel.Children.Add(rNone);
        panel.Children.Add(rCookie);
        panel.Children.Add(rMail);

        // VM → UI: 起動時 / 外部変更で IsChecked を再同期。
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(PostFormViewModel.AuthMode)) return;
            rNone.IsChecked   = _vm.AuthMode == PostAuthMode.None;
            rCookie.IsChecked = _vm.AuthMode == PostAuthMode.Cookie;
            rMail.IsChecked   = _vm.AuthMode == PostAuthMode.MailAuth;
        };

        return panel;
    }

    /// <summary>"ラベル: [ TextBox を残幅で広げる ]" の 1 行を作る (Subject 行に使用)。</summary>
    private static FrameworkElement BuildLabeledTextBox(string label, string boundProperty)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Width = 60 };
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        var tb = new TextBox();
        tb.SetBinding(TextBox.TextProperty, new Binding(boundProperty) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        Grid.SetColumn(tb, 1);
        grid.Children.Add(tb);

        return grid;
    }

    private static System.Collections.Generic.IEnumerable<T> FindVisualChildrenOfType<T>(DependencyObject root) where T : DependencyObject
    {
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var nested in FindVisualChildrenOfType<T>(child)) yield return nested;
        }
    }

    // 名前付きプロキシ (依存プロパティ参照は静的フィールドに保持しておくのが見やすい)
    private static readonly DependencyProperty ToggleButton_IsCheckedProxy = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty;
    private static readonly DependencyProperty ButtonBase_CommandProxy     = System.Windows.Controls.Primitives.ButtonBase.CommandProperty;
}

/// <summary>空文字なら Collapsed、それ以外は Visible。エラーバナーの表示制御に使う。</summary>
internal sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, System.Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, System.Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => Binding.DoNothing;
}
