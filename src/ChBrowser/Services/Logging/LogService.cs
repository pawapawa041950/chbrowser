using System;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChBrowser.Services.Logging;

/// <summary>アプリ内ログ (= Log ペインに表示) のサービス。
/// 静的シングルトン <see cref="Instance"/> 経由でどこからでも書き込める。
///
/// <para>用途:
/// <list type="bullet">
/// <item><description>StatusMessage 変更を流して履歴として保持 (= MainViewModel が partial method で hook)</description></item>
/// <item><description>リリース版でデバッグ情報を出したいとき (= <c>Debug.WriteLine</c> 代替) — DevTools が無くても確認できる</description></item>
/// </list></para>
///
/// <para>UI バインドには <see cref="Text"/> を使う (= ObservableProperty なので変化通知される)。
/// LogPane の <c>TextBox</c> がこれにバインドされ、追記のたびに表示が更新される。</para></summary>
public sealed partial class LogService : ObservableObject
{
    public static LogService Instance { get; } = new();

    /// <summary>ログ全体の上限文字数。超えたら古い分から半分に詰める (= rolling buffer)。</summary>
    private const int MaxChars = 200_000;

    private readonly StringBuilder _sb = new();
    private readonly object _lock = new();

    /// <summary>UI バインド用の集約文字列。タイムスタンプ + 本文 + 改行 を行単位で並べる。</summary>
    [ObservableProperty]
    private string _text = "";

    private LogService() { }

    /// <summary>1 行追加。UI スレッド外から呼ばれた場合は UI スレッドに marshal される。
    /// 空 / null は no-op。</summary>
    public void Write(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}";

        var app = Application.Current;
        if (app is { Dispatcher: { } d } && !d.CheckAccess())
        {
            d.BeginInvoke(new Action(() => Append(line)));
            return;
        }
        Append(line);
    }

    private void Append(string line)
    {
        lock (_lock)
        {
            _sb.Append(line);
            if (_sb.Length > MaxChars)
            {
                // 半分まで縮める (= 直近のログを優先して保持)
                var keep = MaxChars / 2;
                _sb.Remove(0, _sb.Length - keep);
            }
            Text = _sb.ToString();
        }
    }

    /// <summary>ログをすべて消す (= LogPane の「クリア」ボタンから呼ばれる)。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _sb.Clear();
            Text = "";
        }
    }
}
