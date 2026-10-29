using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using ChBrowser.Services.Logging;

namespace ChBrowser.Views;

/// <summary>ログウィンドウ。<see cref="LogService.Instance"/> のログを読取専用 TextBox に表示する。MainWindow から show / hide される。
/// 「✕」で閉じても破棄せず Hide だけ (= 同じインスタンスを再利用)、
/// メインウィンドウの「表示 → ログペイン」チェックも同期する (= Closing で false に倒す)。
///
/// <para>表示は見えている間だけ、<see cref="PullInterval"/> ごとに前回からの増分を末尾へ追記する (ログ 1 行ごとには触らない)。
/// 隠れている間は何もせず、次に見えたときに溜まった分をまとめて取り込む。</para></summary>
public partial class LogWindow : Window
{
    /// <summary>新しいログを取り込む間隔。</summary>
    private static readonly TimeSpan PullInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>「✕」(= Closing) で発火。MainWindow がチェックボックス同期に使う。
    /// e.Cancel を立てて実体破棄を防ぐので、このイベントを受け取った側は Hide() 相当の状態に倒すだけでよい。</summary>
    public event EventHandler? UserClosed;

    private readonly DispatcherTimer _pullTimer;
    private long _cursor;
    private int  _generation = -1;   // まだ一度も読んでいない (= 最初は全文を置き換えで取り込む)

    public LogWindow()
    {
        InitializeComponent();
        ChBrowser.Services.Storage.WindowSizeStore.Attach(this, "Log");   // 前回の大きさで開く
        Closing += LogWindow_Closing;

        _pullTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PullInterval };
        _pullTimer.Tick += (_, _) => PullNewLines();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) { PullNewLines(); _pullTimer.Start(); }
            else _pullTimer.Stop();
        };
    }

    /// <summary>前回からの増分をテキスト欄の末尾に足し、末尾までスクロールする
    /// (古い分が詰められた / クリアされたときだけ全文で置き換える)。</summary>
    private void PullNewLines()
    {
        var (text, reset, cursor, generation) = LogService.Instance.ReadSince(_cursor, _generation);
        _cursor     = cursor;
        _generation = generation;
        if (reset) LogBox.Text = text;
        else if (text.Length > 0) LogBox.AppendText(text);
        else return;
        LogBox.ScrollToEnd();
    }

    /// <summary>「✕」押下時: 実体は破棄せず Hide のみ。Owner = MainWindow が閉じる時は
    /// 通常の Close 経路で破棄され、こちらは Cancel しないので素直に閉じる (= Owner 破棄連鎖)。</summary>
    private void LogWindow_Closing(object? sender, CancelEventArgs e)
    {
        // Owner (= MainWindow) が閉じている最中なら従属閉鎖で OK、Cancel しない。
        if (Owner is { } owner && !owner.IsLoaded) { _pullTimer.Stop(); return; }
        e.Cancel = true;
        Hide();
        UserClosed?.Invoke(this, EventArgs.Empty);
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        LogService.Instance.Clear();
        PullNewLines();
    }
}
