using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ChBrowser.ViewModels;

namespace ChBrowser.Views;

/// <summary>
/// 設計書 §5.6 の投稿フォーム。レス書き込みとスレ立てのどちらでも使う。
/// 既存の <see cref="InputDialog"/> にならって programmatic 構築 (XAML を別ファイルにしない)。
/// バインディング元は <see cref="PostFormViewModel"/>。
/// </summary>
public sealed class PostDialog : Window
{
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
        Closed             += (_, _) => vm.PropertyChanged -= OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 送信成功時、ViewModel が ShouldClose=true にしたタイミングで閉じる
        if (e.PropertyName == nameof(PostFormViewModel.ShouldClose) && _vm.ShouldClose)
        {
            WasSubmitted = true;
            Close();
        }
    }

    private Grid BuildLayout()
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

        Grid.SetRow(headerRow, 1);
        root.Children.Add(headerRow);

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

        // (5) ステータス + 送信/取消
        var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var statusText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray };
        statusText.SetBinding(TextBlock.TextProperty, new Binding(nameof(PostFormViewModel.StatusMessage)));
        Grid.SetColumn(statusText, 0);
        footer.Children.Add(statusText);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

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
