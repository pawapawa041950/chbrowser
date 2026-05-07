using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ChBrowser.Controls;

/// <summary>
/// ペインヘッダ用の「折りたためる検索ボックス」UserControl。
///
/// <para>3 状態:
/// <list type="number">
/// <item><description>折りたたみ (= 🔍 ボタンのみ表示) — 既定状態</description></item>
/// <item><description>展開 (= TextBox + ✕ ボタン) — ユーザが 🔍 をクリック / バインド先の <see cref="Text"/> が
///     非空 (= タブ切替で per-tab 検索が復元された) のいずれか</description></item>
/// <item><description>✕ クリック → <see cref="Text"/> を空に + 折りたたみに戻る</description></item>
/// </list>
/// </para>
///
/// <para><see cref="Text"/> は <see cref="FrameworkPropertyMetadataOptions.BindsTwoWayByDefault"/> で
/// 公開する依存プロパティ。ホスト側は <c>Text="{Binding ...}"</c> でバインドするだけで OK。</para>
/// </summary>
public partial class SearchBoxToggle : UserControl
{
    /// <summary>検索文字列。ホスト側 ViewModel との two-way binding 用。</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SearchBoxToggle),
        new FrameworkPropertyMetadata("",
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnTextChanged));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>展開状態のときの TextBox の ToolTip 文字列 (例: "本文 / 名前行に文字列を含むレスだけ表示")。
    /// 設定なしなら ToolTip 無し。</summary>
    public static readonly DependencyProperty HintTextProperty = DependencyProperty.Register(
        nameof(HintText), typeof(string), typeof(SearchBoxToggle),
        new PropertyMetadata("", OnHintTextChanged));

    public string HintText
    {
        get => (string)GetValue(HintTextProperty);
        set => SetValue(HintTextProperty, value);
    }

    public SearchBoxToggle()
    {
        InitializeComponent();
        // 初期状態は Text の有無に従う (= per-tab 検索の場合、最初から非空のことがある)。
        UpdateVisibility(forceExpand: false);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SearchBoxToggle s) return;
        // Text が外部から非空に変わったら強制展開 (= タブ切替で復元されたケース)。
        // 空に戻った場合はユーザ操作の状態 (= _userExpanded) を尊重して触らない。
        var newText = e.NewValue as string ?? "";
        if (!string.IsNullOrEmpty(newText)) s.UpdateVisibility(forceExpand: true);
    }

    private static void OnHintTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SearchBoxToggle s && s.SearchTextBox is not null)
            s.SearchTextBox.ToolTip = e.NewValue as string;
    }

    /// <summary>ユーザが明示的に展開状態にしたか (= 🔍 をクリックした) フラグ。
    /// Text が空でもこの flag が true なら展開を維持。✕ クリックで false に戻す。</summary>
    private bool _userExpanded;

    private void UpdateVisibility(bool forceExpand)
    {
        var shouldExpand = forceExpand || !string.IsNullOrEmpty(Text) || _userExpanded;
        if (shouldExpand)
        {
            if (ExpandedRoot is not null) ExpandedRoot.Visibility = Visibility.Visible;
            if (ExpandButton is not null) ExpandButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            if (ExpandedRoot is not null) ExpandedRoot.Visibility = Visibility.Collapsed;
            if (ExpandButton is not null) ExpandButton.Visibility = Visibility.Visible;
        }
    }

    private void ExpandButton_Click(object sender, RoutedEventArgs e)
    {
        _userExpanded = true;
        UpdateVisibility(forceExpand: true);
        // visibility 反映後にフォーカスを移したいので Dispatcher 経由で 1 cycle 後に。
        Dispatcher.BeginInvoke(new System.Action(() =>
        {
            SearchTextBox.Focus();
            Keyboard.Focus(SearchTextBox);
            SearchTextBox.SelectAll();
        }), DispatcherPriority.Loaded);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        // Text を先にクリア → ユーザフラグをリセット → 折りたたみ。
        Text = "";
        _userExpanded = false;
        UpdateVisibility(forceExpand: false);
    }

    /// <summary>Esc で折りたたみ (= ✕ と同じ動作)、Enter は単に focus 維持。</summary>
    private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseButton_Click(sender, e);
            e.Handled = true;
        }
    }
}
