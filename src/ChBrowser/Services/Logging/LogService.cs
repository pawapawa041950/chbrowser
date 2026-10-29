using System;
using System.Text;

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
/// <para><see cref="Write"/> は溜め場所に足すだけで画面には触れない (どのスレッドからでも呼べ、軽い)。
/// 表示側 (ログウィンドウ) は見えている間だけ <see cref="ReadSince"/> で前回からの増分を取り、テキスト欄に追記する。
/// 以前は 1 行ごとにログ全文 (最大 20 万文字) を作り直してテキスト欄を丸ごと置き換えていたため、ログが溜まるほど
/// 1 行の書き込みが UI スレッドを長く止め (遅い PC で 1 行 70ms 程度)、スレ表示の描画まで遅らせていた。</para></summary>
public sealed class LogService
{
    public static LogService Instance { get; } = new();

    /// <summary>ログ全体の上限文字数。超えたら古い分から半分に詰める (= rolling buffer)。</summary>
    private const int MaxChars = 200_000;

    private readonly StringBuilder _sb = new();
    private readonly object _lock = new();

    /// <summary><see cref="_sb"/> の先頭が、これまでに書いたログ全体の何文字目か (詰めたり消したりすると進む)。</summary>
    private long _base;

    /// <summary>クリアするたびに増える番号 (表示側が「全部消えた」ことに気付くため)。</summary>
    private int _generation;

    private LogService() { }

    /// <summary>1 行追加 (どのスレッドからでもよい)。空 / null は no-op。</summary>
    public void Write(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}";
        lock (_lock)
        {
            _sb.Append(line);
            if (_sb.Length > MaxChars)
            {
                // 半分まで縮める (= 直近のログを優先して保持)
                var drop = _sb.Length - MaxChars / 2;
                _sb.Remove(0, drop);
                _base += drop;
            }
        }
    }

    /// <summary>今あるログ全体 (調査・テスト用)。</summary>
    public string Text
    {
        get { lock (_lock) return _sb.ToString(); }
    }

    /// <summary>表示側が前回読んだ位置 (<paramref name="cursor"/>、世代 <paramref name="generation"/>) からの増分を返す。
    /// 古い分が詰められて前回の位置が無くなった / クリアされた場合は、全文を <c>Reset = true</c> で返す (表示を置き換える)。
    /// 戻り値の <c>Cursor</c> / <c>Generation</c> を次回に渡す。</summary>
    public (string Text, bool Reset, long Cursor, int Generation) ReadSince(long cursor, int generation)
    {
        lock (_lock)
        {
            var end = _base + _sb.Length;
            if (generation != _generation || cursor < _base || cursor > end)
                return (_sb.ToString(), true, end, _generation);
            var offset = (int)(cursor - _base);
            return (offset == _sb.Length ? "" : _sb.ToString(offset, _sb.Length - offset), false, end, _generation);
        }
    }

    /// <summary>ログをすべて消す (= LogPane の「クリア」ボタンから呼ばれる)。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _base += _sb.Length;
            _sb.Clear();
            _generation++;
        }
    }
}
