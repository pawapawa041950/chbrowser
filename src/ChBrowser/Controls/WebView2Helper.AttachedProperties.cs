using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Wpf;

namespace ChBrowser.Controls;

/// <summary>WebView2 用の添付プロパティ定義。
/// 各プロパティの OnChanged は <see cref="WebView2Helper.PostJsonWhenReadyAsync"/> を呼ぶだけの薄いラッパー。
/// シェル種別 (HtmlPane / ThreadShell / ViewerShell) で待ち合わせ先が分かれる。</summary>
public static partial class WebView2Helper
{
    // ------------------------------------------------------------
    // LogMarkUpdate (スレ一覧: has-log クラスを postMessage で toggle)
    // ------------------------------------------------------------

    public static readonly DependencyProperty LogMarkUpdateProperty =
        DependencyProperty.RegisterAttached(
            "LogMarkUpdate",
            typeof(object),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnLogMarkUpdateChanged));

    public static object? GetLogMarkUpdate(DependencyObject d) => d.GetValue(LogMarkUpdateProperty);
    public static void    SetLogMarkUpdate(DependencyObject d, object? value) => d.SetValue(LogMarkUpdateProperty, value);

    private static void OnLogMarkUpdateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 wv || e.NewValue is null) return;
        var json = JsonSerializer.Serialize(new { type = "updateLogMarks", value = e.NewValue }, PostJsonOptions);
        _ = PostJsonWhenReadyAsync(wv, json, NavScope.HtmlPane);
    }

    // ------------------------------------------------------------
    // FavoritedUpdate (スレ一覧: is-favorited クラスを postMessage で toggle)
    // ------------------------------------------------------------

    public static readonly DependencyProperty FavoritedUpdateProperty =
        DependencyProperty.RegisterAttached(
            "FavoritedUpdate",
            typeof(object),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnFavoritedUpdateChanged));

    public static object? GetFavoritedUpdate(DependencyObject d) => d.GetValue(FavoritedUpdateProperty);
    public static void    SetFavoritedUpdate(DependencyObject d, object? value) => d.SetValue(FavoritedUpdateProperty, value);

    private static void OnFavoritedUpdateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 wv || e.NewValue is null) return;
        var json = JsonSerializer.Serialize(new { type = "updateFavorited", value = e.NewValue }, PostJsonOptions);
        _ = PostJsonWhenReadyAsync(wv, json, NavScope.HtmlPane);
    }

    // ------------------------------------------------------------
    // AppendBatch (スレ表示: スレ表示の唯一の描画チャネル — appendPosts として JS に post)
    //
    // 旧実装には setPosts の全置換チャネルもあったが、
    //   - JS 側は最初から allPosts=[] / DOM=[] で立ち上がるので「明示的に空状態にする」必要がない
    //   - 全置換 + 増分が並走すると、初期 binding 発火と AppendPosts の async スケジュール次第で逆順になり
    //     「先にレンダ → setPosts([]) で消去」の真っ白現象が起きていた
    // ため、appendPosts の単一チャネルに絞って構造的に競合をなくしている。
    // ------------------------------------------------------------

    public static readonly DependencyProperty AppendBatchProperty =
        DependencyProperty.RegisterAttached(
            "AppendBatch",
            typeof(object),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnAppendBatchChanged));

    public static object? GetAppendBatch(DependencyObject d) => d.GetValue(AppendBatchProperty);
    public static void    SetAppendBatch(DependencyObject d, object? value) => d.SetValue(AppendBatchProperty, value);

    private static void OnAppendBatchChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 wv) return;
        if (e.NewValue is not ChBrowser.ViewModels.AppendBatchData data) return;

        // streaming 中もこのバッチで対象レスが現れる可能性があるので scroll target / read mark を併送
        var binding      = wv.DataContext as IThreadDisplayBinding;
        var scrollTarget = binding?.ScrollTargetPostNumber;
        var readMark     = binding?.ReadMarkPostNumber;
        // 「自分の書き込み」のレス番号集合も併送 (= JS 側で「自分」バッジ表示)。
        // 集合は通常極小 (数件) なので毎バッチに同梱しても無害。
        var ownPosts     = binding?.OwnPostNumbers ?? System.Array.Empty<int>();

        var json = JsonSerializer.Serialize(new
        {
            type               = "appendPosts",
            posts              = data.Posts,
            scrollTarget,
            readMarkPostNumber = readMark,
            incremental        = data.IsIncremental,
            ownPostNumbers     = ownPosts,
        }, PostJsonOptions);

        _ = PostJsonWhenReadyAsync(wv, json, NavScope.ThreadShell);
    }

    // ------------------------------------------------------------
    // OwnPostsUpdate (スレ表示: 「自分の書き込み」マークのトグル増分通知)
    // ------------------------------------------------------------

    public static readonly DependencyProperty OwnPostsUpdateProperty =
        DependencyProperty.RegisterAttached(
            "OwnPostsUpdate",
            typeof(object),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnOwnPostsUpdateChanged));

    public static object? GetOwnPostsUpdate(DependencyObject d) => d.GetValue(OwnPostsUpdateProperty);
    public static void    SetOwnPostsUpdate(DependencyObject d, object? value) => d.SetValue(OwnPostsUpdateProperty, value);

    private static void OnOwnPostsUpdateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 wv || e.NewValue is null) return;
        var json = JsonSerializer.Serialize(new { type = "updateOwnPosts", value = e.NewValue }, PostJsonOptions);
        _ = PostJsonWhenReadyAsync(wv, json, NavScope.ThreadShell);
    }

    // ------------------------------------------------------------
    // ViewMode (スレ表示: flat / tree / dedupTree の表示モードを JS に通知)
    // ------------------------------------------------------------

    public static readonly DependencyProperty ViewModeProperty =
        DependencyProperty.RegisterAttached(
            "ViewMode",
            typeof(object),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnViewModeChanged));

    public static object? GetViewMode(DependencyObject d) => d.GetValue(ViewModeProperty);
    public static void    SetViewMode(DependencyObject d, object? value) => d.SetValue(ViewModeProperty, value);

    private static void OnViewModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 wv || e.NewValue is null) return;
        // enum は JsonStringEnumConverter(camelCase) で "flat" / "tree" / "dedupTree" に変換される
        var json = JsonSerializer.Serialize(new { type = "setViewMode", mode = e.NewValue }, PostJsonOptions);
        _ = PostJsonWhenReadyAsync(wv, json, NavScope.ThreadShell);
    }

    // ------------------------------------------------------------
    // ThreadConfigJson (Phase 11: アプリ設定の即時反映)
    //
    // MainViewModel が AppConfig 変更時に「JS に渡す部分だけを抜き出した JSON 文字列」を露出する。
    // 各スレ表示 WebView2 はこの添付プロパティに bind し、変更があれば setConfig メッセージで JS へ push。
    // ------------------------------------------------------------

    public static readonly DependencyProperty ThreadConfigJsonProperty =
        DependencyProperty.RegisterAttached(
            "ThreadConfigJson",
            typeof(string),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnThreadConfigJsonChanged));

    public static string? GetThreadConfigJson(DependencyObject d) => (string?)d.GetValue(ThreadConfigJsonProperty);
    public static void    SetThreadConfigJson(DependencyObject d, string? value) => d.SetValue(ThreadConfigJsonProperty, value);

    private static void OnThreadConfigJsonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 wv) return;
        if (e.NewValue is not string json || string.IsNullOrEmpty(json)) return;
        _ = PostJsonWhenReadyAsync(wv, json, NavScope.ThreadShell);
    }

    // ------------------------------------------------------------
    // ThreadShortcutsJson (Phase 16: スレ表示 WebView 内のショートカット bind 一覧)
    // ------------------------------------------------------------

    public static readonly DependencyProperty ThreadShortcutsJsonProperty =
        DependencyProperty.RegisterAttached(
            "ThreadShortcutsJson",
            typeof(string),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnThreadShortcutsJsonChanged));

    public static string? GetThreadShortcutsJson(DependencyObject d) => (string?)d.GetValue(ThreadShortcutsJsonProperty);
    public static void    SetThreadShortcutsJson(DependencyObject d, string? value) => d.SetValue(ThreadShortcutsJsonProperty, value);

    private static void OnThreadShortcutsJsonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 wv) return;
        if (e.NewValue is not string json || string.IsNullOrEmpty(json)) return;
        _ = PostJsonWhenReadyAsync(wv, json, NavScope.ThreadShell);
    }

    // ------------------------------------------------------------
    // PaneShortcutsJson (Phase 16: 3 ペイン用のショートカット bind 一覧 push)
    // ------------------------------------------------------------

    public static readonly DependencyProperty PaneShortcutsJsonProperty =
        DependencyProperty.RegisterAttached(
            "PaneShortcutsJson",
            typeof(string),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnPaneShortcutsJsonChanged));

    public static string? GetPaneShortcutsJson(DependencyObject d) => (string?)d.GetValue(PaneShortcutsJsonProperty);
    public static void    SetPaneShortcutsJson(DependencyObject d, string? value) => d.SetValue(PaneShortcutsJsonProperty, value);

    private static void OnPaneShortcutsJsonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 wv) return;
        if (e.NewValue is not string json || string.IsNullOrEmpty(json)) return;
        _ = PostJsonWhenReadyAsync(wv, json, NavScope.HtmlPane);
    }

    // ------------------------------------------------------------
    // PaneConfigJson (Phase 11b: お気に入り / 板一覧 / スレッド一覧 ペイン共通の setConfig push)
    // ------------------------------------------------------------

    public static readonly DependencyProperty PaneConfigJsonProperty =
        DependencyProperty.RegisterAttached(
            "PaneConfigJson",
            typeof(string),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnPaneConfigJsonChanged));

    public static string? GetPaneConfigJson(DependencyObject d) => (string?)d.GetValue(PaneConfigJsonProperty);
    public static void    SetPaneConfigJson(DependencyObject d, string? value) => d.SetValue(PaneConfigJsonProperty, value);

    private static void OnPaneConfigJsonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 wv) return;
        if (e.NewValue is not string json || string.IsNullOrEmpty(json)) return;
        _ = PostJsonWhenReadyAsync(wv, json, NavScope.HtmlPane);
    }

    // ------------------------------------------------------------
    // ImageUrl (Phase 10: ビューアウィンドウの 1 タブ用)
    //
    // 添付プロパティが変更されたら、初回はビューアシェル HTML をロード、
    // 以降は { type:'setImage', url } メッセージを JS に送って画像を切替。
    // ------------------------------------------------------------

    public static readonly DependencyProperty ImageUrlProperty =
        DependencyProperty.RegisterAttached(
            "ImageUrl",
            typeof(string),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnImageUrlChanged));

    public static string? GetImageUrl(DependencyObject d) => (string?)d.GetValue(ImageUrlProperty);
    public static void    SetImageUrl(DependencyObject d, string? value) => d.SetValue(ImageUrlProperty, value);

    private static void OnImageUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 wv) return;
        var url  = e.NewValue as string ?? "";
        var json = JsonSerializer.Serialize(new { type = "setImage", url }, PostJsonOptions);
        _ = PostJsonWhenReadyAsync(wv, json, NavScope.ViewerShell);
    }
}
