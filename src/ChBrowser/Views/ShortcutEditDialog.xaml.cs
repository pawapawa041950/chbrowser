using System.Windows;
using System.Windows.Input;

namespace ChBrowser.Views;

/// <summary>ショートカット編集ダイアログ (Phase 15、現状はスタブ)。
/// キャプチャ領域にフォーカスを当てて新しいキーコンビネーションを押すと <see cref="LiveCaptureText"/> に表示される。
/// OK で <see cref="DialogResult"/>=true、<see cref="NewBinding"/> プロパティに記録された文字列が入る (= 呼び出し元で代入する想定)。
/// 永続化と実機能への適用は後続フェーズ。</summary>
public partial class ShortcutEditDialog : Window
{
    public string ActionName  { get; }
    public string NewBinding  { get; private set; }

    private string _capturedBinding = "";

    public ShortcutEditDialog(string actionName, string currentBinding)
    {
        InitializeComponent();
        ActionName = actionName;
        NewBinding = currentBinding;
        _capturedBinding = currentBinding;
        CurrentBindingRun.Text = string.IsNullOrEmpty(currentBinding) ? "(未設定)" : currentBinding;

        Loaded            += (_, _) => CaptureArea.Focus();
        CaptureArea.PreviewKeyDown += CaptureArea_PreviewKeyDown;
    }

    /// <summary>キーキャプチャ — 修飾キー単独 (Ctrl 等) を除いた最初の有効キーで割り当て確定。
    /// スタブなのでこのダイアログ内では即時 NewBinding に反映するだけ、永続化はしない。</summary>
    private void CaptureArea_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        // 修飾キー単独は無視
        if (key is Key.LeftCtrl or Key.RightCtrl
                or Key.LeftAlt  or Key.RightAlt
                or Key.LeftShift or Key.RightShift
                or Key.LWin or Key.RWin) return;

        var mods = Keyboard.Modifiers;
        var sb = new System.Text.StringBuilder();
        if ((mods & ModifierKeys.Control) != 0) sb.Append("Ctrl+");
        if ((mods & ModifierKeys.Alt)     != 0) sb.Append("Alt+");
        if ((mods & ModifierKeys.Shift)   != 0) sb.Append("Shift+");
        if ((mods & ModifierKeys.Windows) != 0) sb.Append("Win+");
        sb.Append(key);

        _capturedBinding = sb.ToString();
        LiveCaptureText.Text = _capturedBinding;
        e.Handled = true;
    }

    private void CaptureArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => CaptureArea.Focus();

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _capturedBinding     = "";
        LiveCaptureText.Text = "(未設定)";
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        NewBinding   = _capturedBinding;
        DialogResult = true;
    }
}
