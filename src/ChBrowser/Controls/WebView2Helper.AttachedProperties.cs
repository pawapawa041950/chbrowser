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
    // ThreadListSearchPush (スレ一覧: タイトル絞り込みクエリを JS に push)
    //
    // 値は string。空文字なら絞り込み解除 (= 全行可視)。null は早期 return (= 初期状態)。
    // JS 側は setListSearch メッセージを受けて行の visibility と title セルのハイライトを更新する。
    // ------------------------------------------------------------

    public static readonly DependencyProperty ThreadListSearchPushProperty =
        DependencyProperty.RegisterAttached(
            "ThreadListSearchPush",
            typeof(string),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnThreadListSearchPushChanged));

    public static string? GetThreadListSearchPush(DependencyObject d) => (string?)d.GetValue(ThreadListSearchPushProperty);
    public static void    SetThreadListSearchPush(DependencyObject d, string? value) => d.SetValue(ThreadListSearchPushProperty, value);

    private static void OnThreadListSearchPushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 wv) return;
        var query = (e.NewValue as string) ?? "";
        var json  = JsonSerializer.Serialize(new { type = "setListSearch", query }, PostJsonOptions);
        _ = PostJsonWhenReadyAsync(wv, json, NavScope.HtmlPane);
    }

    // ------------------------------------------------------------
    // PaneSearchPush (お気に入り / 板一覧 ペイン: 絞り込みクエリを JS に push する汎用 DP)
    //
    // 値は string。空文字なら絞り込み解除 (= 全エントリ可視)。
    // JS 側は setPaneSearch メッセージを受けて、ツリーをフィルタ + マッチ箇所をハイライトする。
    // 対象ペインの JS (favorites.js / board-list.js) で同名のハンドラを実装する。
    // ------------------------------------------------------------

    public static readonly DependencyProperty PaneSearchPushProperty =
        DependencyProperty.RegisterAttached(
            "PaneSearchPush",
            typeof(string),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnPaneSearchPushChanged));

    public static string? GetPaneSearchPush(DependencyObject d) => (string?)d.GetValue(PaneSearchPushProperty);
    public static void    SetPaneSearchPush(DependencyObject d, string? value) => d.SetValue(PaneSearchPushProperty, value);

    private static void OnPaneSearchPushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 wv) return;
        var query = (e.NewValue as string) ?? "";
        var json  = JsonSerializer.Serialize(new { type = "setPaneSearch", query }, PostJsonOptions);
        _ = PostJsonWhenReadyAsync(wv, json, NavScope.HtmlPane);
    }

    // ------------------------------------------------------------
    // ItemsHtmlPatch (スレ一覧: tbody innerHTML を in-place 差分 push)
    //
    // 2 回目以降のリフレッシュで Html を再 NavigateToString すると WebView2 の DOM が破棄され
    // 一瞬画面が真っ白になる (= flash) ため、tbody だけを差し替える経路を別建てする。
    // JS 側の thread-list.js は { type: 'replaceItems', html } を受けて tbody.innerHTML を置き換える。
    // ------------------------------------------------------------

    public static readonly DependencyProperty ItemsHtmlPatchProperty =
        DependencyProperty.RegisterAttached(
            "ItemsHtmlPatch",
            typeof(string),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnItemsHtmlPatchChanged));

    public static string? GetItemsHtmlPatch(DependencyObject d) => (string?)d.GetValue(ItemsHtmlPatchProperty);
    public static void    SetItemsHtmlPatch(DependencyObject d, string? value) => d.SetValue(ItemsHtmlPatchProperty, value);

    private static void OnItemsHtmlPatchChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 wv) return;
        if (e.NewValue is not string html || string.IsNullOrEmpty(html)) return;
        var json = JsonSerializer.Serialize(new { type = "replaceItems", html }, PostJsonOptions);
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

        // streaming 中もこのバッチで対象レスが現れる可能性があるので scroll target / mark を併送
        var binding      = wv.DataContext as IThreadDisplayBinding;
        var scrollTarget = binding?.ScrollTargetPostNumber;
        var markPostNumber = binding?.MarkPostNumber;
        // 「自分の書き込み」のレス番号集合も併送 (= JS 側で「自分」バッジ表示)。
        // 集合は通常極小 (数件) なので毎バッチに同梱しても無害。
        var ownPosts     = binding?.OwnPostNumbers ?? System.Array.Empty<int>();

        ChBrowser.Services.Logging.LogService.Instance.Write(
            $"[appendBatch] serialize: posts={data.Posts.Count}"
            + $" (numbers {(data.Posts.Count > 0 ? data.Posts[0].Number : -1)}..{(data.Posts.Count > 0 ? data.Posts[data.Posts.Count - 1].Number : -1)})"
            + $", incremental={data.IsIncremental}"
            + $", binding.MarkPostNumber={markPostNumber?.ToString() ?? "null"}"
            + $", binding.ScrollTargetPostNumber={scrollTarget?.ToString() ?? "null"}");

        var json = JsonSerializer.Serialize(new
        {
            type             = "appendPosts",
            posts            = data.Posts,
            scrollTarget,
            markPostNumber,
            incremental      = data.IsIncremental,
            ownPostNumbers   = ownPosts,
        }, PostJsonOptions);

        _ = PostJsonWhenReadyAsync(wv, json, NavScope.ThreadShell);
    }

    // ------------------------------------------------------------
    // ThreadResync: WebView 内部 reload で JS state が消えたケースの全 state 再 push。
    //
    // 通常各種 DP は「値変化時のみ発火」なので、WebView2 が C# に通知しないまま reload
    // (= ProcessFailed recovery / メモリ圧迫による discard 等) されると、JS の allPosts /
    // viewMode / filter / 等が初期値に戻ったまま C# は再 push しない真っ白状態に陥る。
    // ThreadDisplayPane が 2 回目以降の 'ready' 受信で本メソッドを呼び、tab 内の現在 state を
    // 1 つの resyncThreadState メッセージに束ねて送り JS 側で全再構築させる。
    // ------------------------------------------------------------
    internal static System.Threading.Tasks.Task SendThreadResyncAsync(
        WebView2 wv,
        ChBrowser.ViewModels.ThreadTabViewModel tab)
    {
        var filter = tab.Filter;
        // enum (ThreadViewMode) は JsonStringEnumConverter(camelCase) によって
        // "flat" / "tree" / "dedupTree" に変換され、JS 側 VIEW_MODE_STRATEGIES のキーと一致する。
        var json = JsonSerializer.Serialize(new
        {
            type           = "resyncThreadState",
            viewMode       = tab.ViewMode,
            posts          = tab.Posts,
            scrollTarget   = (int?)tab.ScrollTargetPostNumber,
            markPostNumber = (int?)tab.MarkPostNumber,
            ownPostNumbers = System.Linq.Enumerable.ToArray(tab.OwnPostNumbers),
            filter = new
            {
                textQuery   = filter?.TextQuery ?? "",
                popularOnly = filter?.PopularOnly == true,
                mediaOnly   = filter?.MediaOnly   == true,
            },
        }, PostJsonOptions);
        ChBrowser.Services.Logging.LogService.Instance.Write(
            $"[threadResync] serialize: tab.Posts={tab.Posts.Count}, ViewMode={tab.ViewMode}, "
            + $"MarkPostNumber={tab.MarkPostNumber?.ToString() ?? "null"}, "
            + $"ScrollTarget={tab.ScrollTargetPostNumber?.ToString() ?? "null"}, "
            + $"OwnPosts={tab.OwnPostNumbers.Count}");
        return PostJsonWhenReadyAsync(wv, json, NavScope.ThreadShell);
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
    // MarkPostNumberPush (スレ表示: 「以降新レス」ラベル位置の単独 push)
    //
    // appendPosts ペイロードでも mark は併送しているが、それは「新着がある=appendPosts が呼ばれる」時のみ。
    // 新着 0 件の refresh で mark を null にクリアしたい場合や、新着なしの状態で mark 値そのものが
    // 変わった場合 (= レアだがありうる) は appendPosts が走らないので、ここで単独 push する。
    //
    // 値の型は object?。int? を boxed で持ち、null も valid な value (= ラベル消去) として扱う。
    // 既存の Own / ViewMode 系 DP は NewValue==null で早期 return しているが、こちらは null 自体が
    // 意味を持つ (= JS 側で markPostNumber を null にしてラベルを取り除く) ので return しない。
    // ------------------------------------------------------------

    public static readonly DependencyProperty MarkPostNumberPushProperty =
        DependencyProperty.RegisterAttached(
            "MarkPostNumberPush",
            typeof(object),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnMarkPostNumberPushChanged));

    public static object? GetMarkPostNumberPush(DependencyObject d) => d.GetValue(MarkPostNumberPushProperty);
    public static void    SetMarkPostNumberPush(DependencyObject d, object? value) => d.SetValue(MarkPostNumberPushProperty, value);

    private static void OnMarkPostNumberPushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 wv) return;
        ChBrowser.Services.Logging.LogService.Instance.Write(
            $"[markPush] new value={e.NewValue?.ToString() ?? "null"} (was {e.OldValue?.ToString() ?? "null"})");
        // value は int? の boxed か null。JsonSerializer は null を JSON null としてシリアライズするので
        // JS 側で typeof === 'number' チェック → 数値なら設定、それ以外 (= null/undefined) なら markPostNumber=null。
        var json = JsonSerializer.Serialize(new { type = "setMarkPostNumber", value = e.NewValue }, PostJsonOptions);
        _ = PostJsonWhenReadyAsync(wv, json, NavScope.ThreadShell);
    }

    // ------------------------------------------------------------
    // FilterPush (スレ表示: テキスト絞り込み + 将来追加されるフィルタ条件を 1 オブジェクトで JS に push)
    //
    // 値の型は ThreadFilter (record)。プロパティ追加だけで条件を増やせる設計。
    // null → 早期 return (= デフォルト初期値 = ThreadFilter() なら必ず non-null なのでこの経路は通常通らない)。
    // 値が来たら setFilter メッセージを serialize して送る。JS 側は記載のフィールドを順次評価する。
    // ------------------------------------------------------------

    public static readonly DependencyProperty FilterPushProperty =
        DependencyProperty.RegisterAttached(
            "FilterPush",
            typeof(object),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnFilterPushChanged));

    public static object? GetFilterPush(DependencyObject d) => d.GetValue(FilterPushProperty);
    public static void    SetFilterPush(DependencyObject d, object? value) => d.SetValue(FilterPushProperty, value);

    private static void OnFilterPushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 wv) return;
        if (e.NewValue is not ChBrowser.Models.ThreadFilter filter) return;
        // ThreadFilter のフィールドをそのまま乗せて push (= 新フィールド追加で自動的に JS 側へ届く)。
        var json = JsonSerializer.Serialize(new
        {
            type        = "setFilter",
            textQuery   = filter.TextQuery,
            popularOnly = filter.PopularOnly,
            mediaOnly   = filter.MediaOnly,
        }, PostJsonOptions);
        _ = PostJsonWhenReadyAsync(wv, json, NavScope.ThreadShell);
    }

    // ------------------------------------------------------------
    // HidePostsPush (スレ表示: 指定レス番号集合を即時 DOM から取り除く)
    //
    // NG ルールが追加された直後に「現状開いているスレで新たに hidden になる番号」を
    // C# 側で計算し、JS に setHiddenPosts メッセージで push する。
    // 値の型は IReadOnlyList<int> (or any IEnumerable<int>)。null と空配列は早期 return。
    // 同じ集合を 2 回流しても DOM 操作は冪等 (= 既に消えているレスは何もしないので再 push は安全)。
    // ------------------------------------------------------------

    public static readonly DependencyProperty HidePostsPushProperty =
        DependencyProperty.RegisterAttached(
            "HidePostsPush",
            typeof(object),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnHidePostsPushChanged));

    public static object? GetHidePostsPush(DependencyObject d) => d.GetValue(HidePostsPushProperty);
    public static void    SetHidePostsPush(DependencyObject d, object? value) => d.SetValue(HidePostsPushProperty, value);

    private static void OnHidePostsPushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 wv) return;
        if (e.NewValue is not System.Collections.Generic.IEnumerable<int> nums) return;
        var arr = System.Linq.Enumerable.ToArray(nums);
        if (arr.Length == 0) return;
        var json = JsonSerializer.Serialize(new { type = "setHiddenPosts", numbers = arr }, PostJsonOptions);
        _ = PostJsonWhenReadyAsync(wv, json, NavScope.ThreadShell);
    }

    // ------------------------------------------------------------
    // ScrollToPostPush (スレ表示: 指定レス番号までスクロールせよ JS に push)
    //
    // 5ch.io スレ URL クリック (= postNo 付き) で本タブにスクロール要求が来た時に
    // ThreadTabViewModel.PendingScrollToPost が新インスタンスにセットされ、本 callback が走る。
    // 値は <see cref="ChBrowser.ViewModels.ScrollToPostRequest"/> (= 同一 number でも new で再 push できる)。
    // ------------------------------------------------------------

    public static readonly DependencyProperty ScrollToPostPushProperty =
        DependencyProperty.RegisterAttached(
            "ScrollToPostPush",
            typeof(object),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnScrollToPostPushChanged));

    public static object? GetScrollToPostPush(DependencyObject d) => d.GetValue(ScrollToPostPushProperty);
    public static void    SetScrollToPostPush(DependencyObject d, object? value) => d.SetValue(ScrollToPostPushProperty, value);

    private static void OnScrollToPostPushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 wv) return;
        if (e.NewValue is not ChBrowser.ViewModels.ScrollToPostRequest req) return;
        if (req.Number <= 0) return;
        var json = JsonSerializer.Serialize(new { type = "scrollToPost", number = req.Number }, PostJsonOptions);
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

    // ------------------------------------------------------------
    // ZoomModePush (Phase 25: ビューア右クリック「原寸表示 / ウィンドウに合わせる」)
    //
    // ImageViewerTabViewModel.PendingZoomMode に新しい ZoomModeRequest がセットされると本 callback が走り、
    // {type:setZoom, mode:"actual"|"fit"} を JS に push する。JS 側 (viewer.js) で transform を組み立て直す。
    // ------------------------------------------------------------

    public static readonly DependencyProperty ZoomModePushProperty =
        DependencyProperty.RegisterAttached(
            "ZoomModePush",
            typeof(object),
            typeof(WebView2Helper),
            new PropertyMetadata(null, OnZoomModePushChanged));

    public static object? GetZoomModePush(DependencyObject d) => d.GetValue(ZoomModePushProperty);
    public static void    SetZoomModePush(DependencyObject d, object? value) => d.SetValue(ZoomModePushProperty, value);

    private static void OnZoomModePushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WebView2 wv) return;
        if (e.NewValue is not ChBrowser.ViewModels.ZoomModeRequest req) return;
        if (string.IsNullOrEmpty(req.Mode)) return;
        var json = JsonSerializer.Serialize(new { type = "setZoom", mode = req.Mode }, PostJsonOptions);
        _ = PostJsonWhenReadyAsync(wv, json, NavScope.ViewerShell);
    }
}
