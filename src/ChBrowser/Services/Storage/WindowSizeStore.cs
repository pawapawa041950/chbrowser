using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace ChBrowser.Services.Storage;

/// <summary>
/// サブウィンドウ (AI チャット / 設定 / NG / ログ …) の大きさを、ウィンドウの種類ごとに <c>data/app/window_sizes.json</c> へ保存して
/// 次に開いたときに戻す。メインウィンドウ・画像ビューア (layout.json) と書き込みウィンドウ (config.json) は従来どおり別に保存している。
///
/// <para>使い方: ウィンドウのコンストラクタで (既定の大きさを決めた後、表示する前に) <see cref="Attach"/> を呼ぶ。
/// 大きさ・最大化の状態は変わるたびに覚え、閉じたとき / 隠したとき / アプリ終了時 (<see cref="Flush"/>) にファイルへ書く。
/// 位置は、開く位置を自分で決めるウィンドウ (<see cref="WindowStartupLocation.Manual"/>、例: ログ) だけ戻す。
/// それ以外は従来どおり親ウィンドウの中央に開く。</para>
/// </summary>
public static class WindowSizeStore
{
    /// <summary>保存する 1 ウィンドウ分。最大化中は通常表示時の位置・大きさ (RestoreBounds) を入れる。</summary>
    public sealed record Entry(double Left, double Top, double Width, double Height, bool Maximized);

    private static readonly object Lock = new();
    private static Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private static string? _path;
    private static bool _dirty;

    /// <summary>起動時に 1 回呼ぶ (保存先を決めて読み込む)。</summary>
    public static void Initialize(DataPaths paths)
    {
        lock (Lock)
        {
            _path = Path.Combine(paths.AppDir, "window_sizes.json");
            try
            {
                if (File.Exists(_path))
                    _entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path))
                               ?? new Dictionary<string, Entry>(StringComparer.Ordinal);
                _entries = new Dictionary<string, Entry>(_entries, StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"[WindowSizeStore] load failed: {ex.Message}");
                _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            }
        }
    }

    /// <summary>保存してある大きさを <paramref name="window"/> に当て、以後の変化を覚えるようにする。
    /// <paramref name="key"/> はウィンドウの種類を表す名前 (同じ種類のウィンドウは同じ大きさを共有する)。</summary>
    public static void Attach(Window window, string key)
    {
        Entry? saved;
        lock (Lock) _entries.TryGetValue(key, out saved);
        if (saved is not null) Apply(window, saved);

        void Remember()
        {
            if (Capture(window, key) is not { } e) return;
            lock (Lock)
            {
                if (_entries.TryGetValue(key, out var old) && old == e) return;
                _entries[key] = e;
                _dirty = true;
            }
        }
        window.SizeChanged     += (_, _) => Remember();
        window.LocationChanged += (_, _) => Remember();
        window.StateChanged    += (_, _) => Remember();
        window.Closing         += (_, _) => { Remember(); Flush(); };
        // 閉じずに隠すウィンドウ (ログ) もあるので、見えなくなったときにも書く
        window.IsVisibleChanged += (_, e) => { if (e.NewValue is false) Flush(); };
    }

    /// <summary>その種類のウィンドウの大きさを保存してあるか (初期位置を呼び出し側で決めるウィンドウが、保存済みなら位置を上書きしないために使う)。</summary>
    public static bool HasSaved(string key)
    {
        lock (Lock) return _entries.ContainsKey(key);
    }

    /// <summary>変更があればファイルへ書く (アプリ終了時にも呼ぶ)。</summary>
    public static void Flush()
    {
        string json;
        string path;
        lock (Lock)
        {
            if (!_dirty || _path is null) return;
            json   = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
            path   = _path;
            _dirty = false;
        }
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[WindowSizeStore] save failed: {ex.Message}");
            lock (Lock) _dirty = true;
        }
    }

    private static void Apply(Window window, Entry e)
    {
        // 画面の大きさより大きくはしない (別のディスプレイ構成で保存された値に備える)
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (e.Width > 0)  window.Width  = Math.Min(Math.Max(e.Width,  window.MinWidth),  screen.Width);
        if (e.Height > 0) window.Height = Math.Min(Math.Max(e.Height, window.MinHeight), screen.Height);

        // 位置は自分で位置を決めるウィンドウだけ。タイトルバーが画面内に残る場合に限る
        if (window.WindowStartupLocation == WindowStartupLocation.Manual
            && screen.Contains(new Point(e.Left + 40, e.Top + 10)))
        {
            window.Left = e.Left;
            window.Top  = e.Top;
        }
        // ShowActivated=false のウィンドウは最大化状態では表示できない (WPF の制約で例外になる) ので戻さない
        if (e.Maximized && window.ShowActivated) window.WindowState = WindowState.Maximized;
    }

    private static Entry? Capture(Window window, string key)
    {
        if (!window.IsLoaded) return null;   // 表示前 (初期値を当てている最中) は覚えない
        switch (window.WindowState)
        {
            case WindowState.Normal:
                return window.ActualWidth > 0 && window.ActualHeight > 0
                    ? new Entry(window.Left, window.Top, window.Width, window.Height, false)
                    : null;
            case WindowState.Maximized:
                var r = window.RestoreBounds;
                return r.IsEmpty ? null : new Entry(r.Left, r.Top, r.Width, r.Height, true);
            default:
                // 最小化中は通常表示時の値と、直前の最大化状態を保つ
                var rb = window.RestoreBounds;
                bool wasMax;
                lock (Lock) wasMax = _entries.TryGetValue(key, out var old) && old.Maximized;
                return rb.IsEmpty ? null : new Entry(rb.Left, rb.Top, rb.Width, rb.Height, wasMax);
        }
    }
}
