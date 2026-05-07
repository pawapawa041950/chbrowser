using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ChBrowser.Services.Logging;

namespace ChBrowser.Views;

/// <summary>ログウィンドウ。<see cref="LogService.Instance"/> の Text に bind した
/// 読取専用 TextBox を表示する。MainWindow から show / hide される。
/// 「✕」で閉じても破棄せず Hide だけ (= 同じインスタンスを再利用)、
/// メインウィンドウの「表示 → ログペイン」チェックも同期する (= Closing で false に倒す)。</summary>
public partial class LogWindow : Window
{
    /// <summary>「✕」(= Closing) で発火。MainWindow がチェックボックス同期に使う。
    /// e.Cancel を立てて実体破棄を防ぐので、このイベントを受け取った側は Hide() 相当の状態に倒すだけでよい。</summary>
    public event System.EventHandler? UserClosed;

    public LogWindow()
    {
        InitializeComponent();
        Closing += LogWindow_Closing;
    }

    /// <summary>「✕」押下時: 実体は破棄せず Hide のみ。Owner = MainWindow が閉じる時は
    /// 通常の Close 経路で破棄され、こちらは Cancel しないので素直に閉じる (= Owner 破棄連鎖)。</summary>
    private void LogWindow_Closing(object? sender, CancelEventArgs e)
    {
        // Owner (= MainWindow) が閉じている最中なら従属閉鎖で OK、Cancel しない。
        if (Owner is { } owner && !owner.IsLoaded) return;
        e.Cancel = true;
        Hide();
        UserClosed?.Invoke(this, System.EventArgs.Empty);
    }

    /// <summary>ログ追加のたびに末尾までスクロール。ユーザがコピー目的で上にスクロール中でも
    /// CaretIndex 移動はテキスト選択を妨げない (= 選択範囲は CaretIndex とは独立に保持される)。</summary>
    private void LogBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            tb.CaretIndex = tb.Text?.Length ?? 0;
            tb.ScrollToEnd();
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
        => LogService.Instance.Clear();
}
