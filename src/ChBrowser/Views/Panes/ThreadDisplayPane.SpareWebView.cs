using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using ChBrowser.Controls;
using ChBrowser.ViewModels;
using Microsoft.Web.WebView2.Wpf;

namespace ChBrowser.Views.Panes;

/// <summary>スレ表示の WebView の用意 (予備の WebView)。
///
/// <para>タブごとの WebView は、タブが表示されたとき (<see cref="ThreadViewHost_Loaded"/>) にテンプレートの入れ物へコードで入れる。
/// 新しく作ると「WebView の起動 + スレ表示のページの読み込み」に低スペック PC で 0.3〜0.5 秒かかり、その間レスの表示を待たせるので、
/// ペインごとに 1 つ、ページまで読み込み済みの予備を <see cref="SpareWebViewHost"/> に置いておき、新しいタブはそれを使う
/// (使ったら少し後に次の予備を作る)。</para>
///
/// <para>別ペインから移動してきたタブ (<see cref="ThreadTabViewModel.NeedsResyncOnAttach"/>) は、ページの準備完了 (ready) を受けて
/// 全レスを送り直す流れなので、従来どおり新しく作る (予備は ready を済ませてしまっているため)。
/// CSS を読み直した後 (<see cref="WebView2Helper.ThreadShellVersion"/> が変わった) の予備は古いページなので使わない。</para></summary>
public partial class ThreadDisplayPane
{
    /// <summary>ペインが表示されてから最初の予備を作るまで (起動直後の処理と重ならないように)。</summary>
    private static readonly TimeSpan SpareFirstDelay = TimeSpan.FromSeconds(2);

    /// <summary>予備を使ってから次の予備を作るまで (開いたタブの表示と重ならないように)。</summary>
    private static readonly TimeSpan SpareNextDelay = TimeSpan.FromSeconds(1);

    private WebView2? _spareWebView;
    private int _spareShellVersion;
    private DispatcherTimer? _spareTimer;

    /// <summary><paramref name="delay"/> 後に予備を作る (既にあれば何もしない)。</summary>
    private void ScheduleSpareWebView(TimeSpan delay)
    {
        _spareTimer ??= CreateSpareTimer();
        _spareTimer.Stop();
        _spareTimer.Interval = delay;
        _spareTimer.Start();
    }

    private DispatcherTimer CreateSpareTimer()
    {
        var t = new DispatcherTimer(DispatcherPriority.Background);
        t.Tick += (_, _) =>
        {
            t.Stop();
            CreateSpareWebView();
        };
        return t;
    }

    private void CreateSpareWebView()
    {
        if (_spareWebView is not null || !IsLoaded) return;
        var wv = NewThreadWebView();
        SpareWebViewHost.Children.Add(wv);
        _spareWebView      = wv;
        _spareShellVersion = WebView2Helper.ThreadShellVersion;
        _ = WebView2Helper.PrewarmThreadShellAsync(wv);
    }

    /// <summary>予備を取り出す (無い / 古いページなら null)。取り出したら次の予備を予約する。</summary>
    private WebView2? TakeSpareWebView()
    {
        if (_spareWebView is not { } wv) return null;
        _spareWebView = null;
        SpareWebViewHost.Children.Remove(wv);
        ScheduleSpareWebView(SpareNextDelay);
        if (_spareShellVersion != WebView2Helper.ThreadShellVersion)
        {
            DisposeQuietly(wv);
            return null;
        }
        return wv;
    }

    /// <summary>予備を捨てる (ペインが外れたとき。また表示されたら作り直す)。</summary>
    private void DiscardSpareWebView()
    {
        _spareTimer?.Stop();
        if (_spareWebView is not { } wv) return;
        _spareWebView = null;
        SpareWebViewHost.Children.Remove(wv);
        DisposeQuietly(wv);
    }

    private static void DisposeQuietly(WebView2 wv)
    {
        try { wv.Dispose(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ThreadDisplayPane] spare WebView2 Dispose failed: {ex.Message}"); }
    }

    /// <summary>スレ表示用の WebView を作り、タブに依らないイベントを付ける (予備も新規もここを通る)。</summary>
    private WebView2 NewThreadWebView()
    {
        var wv = new WebView2();
        wv.WebMessageReceived                  += (s, e) => ThreadViewWebView_WebMessageReceived(s!, e);
        wv.CoreWebView2InitializationCompleted += ThreadViewWebView_CoreInitialized;
        wv.IsVisibleChanged                    += ThreadViewWebView_IsVisibleChanged;
        return wv;
    }

    /// <summary>タブの入れ物が表示されたら、WebView (予備があればそれ、無ければ新規) を入れてタブの表示用の設定を結び付ける。
    /// 一時的に外れて戻った (レイアウトの組み替え等) ときは既に入っているので何もしない。</summary>
    private void ThreadViewHost_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border host || host.Child is not null) return;
        if (host.DataContext is not ThreadTabViewModel tab) return;

        var wv = tab.NeedsResyncOnAttach ? null : TakeSpareWebView();
        var fromSpare = wv is not null;
        if (wv is null) wv = NewThreadWebView();
        else if (tab.OpenPerfWebView is { } perf)
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[openPerf] {tab.ThreadKey}: 予備の WebView を使用 (ページ{(WebView2Helper.IsThreadShellReady(wv) ? "読み込み済み" : "は読み込み中")}) +{perf.ElapsedMilliseconds} ms");

        host.Child = wv;   // DataContext (= タブ) は入れ物から引き継ぐ
        BindThreadWebView(wv);
        wv.Unloaded += ThreadViewWebView_Unloaded;
        if (fromSpare)
        {
            // 予備は起動した時点でタブを持っていなかったので、起動時に行うタブとの対応付け (ThreadViewWebView_CoreInitialized) をここで行う
            if (wv.CoreWebView2 is not null) _tabToWebView.AddOrUpdate(tab, wv);
            // ページを読み込み済みなら、以後 WebView の起動・ページの読み込みの時刻は出ない (計測はここまで)
            if (WebView2Helper.IsThreadShellReady(wv)) tab.OpenPerfWebView = null;
        }
    }

    /// <summary>タブの表示用の設定を WebView に結び付ける (以前 XAML のテンプレートで書いていたものと同じ。順番も同じ)。</summary>
    private void BindThreadWebView(WebView2 wv)
    {
        void Bind(DependencyProperty dp, string path)
            => BindingOperations.SetBinding(wv, dp, new Binding(path));
        void BindPane(DependencyProperty dp, string path)
            => BindingOperations.SetBinding(wv, dp, new Binding(path) { Source = this });

        Bind(WebView2Helper.AppendBatchProperty,          nameof(ThreadTabViewModel.LatestAppendBatch));
        Bind(WebView2Helper.OwnPostsUpdateProperty,       nameof(ThreadTabViewModel.OwnPostsUpdate));
        Bind(WebView2Helper.VotesUpdateProperty,          nameof(ThreadTabViewModel.VotesUpdate));
        Bind(WebView2Helper.AuthorProfilesUpdateProperty, nameof(ThreadTabViewModel.AuthorProfilesUpdate));
        Bind(WebView2Helper.ScoresUpdateProperty,         nameof(ThreadTabViewModel.ScoresUpdate));
        Bind(WebView2Helper.TranslationUpdateProperty,    nameof(ThreadTabViewModel.TranslationUpdate));
        Bind(WebView2Helper.MarkPostNumberPushProperty,   nameof(ThreadTabViewModel.MarkPostNumber));
        Bind(WebView2Helper.FilterPushProperty,           nameof(ThreadTabViewModel.Filter));
        Bind(WebView2Helper.ViewModeProperty,             nameof(ThreadTabViewModel.ViewMode));
        Bind(WebView2Helper.HidePostsPushProperty,        nameof(ThreadTabViewModel.PendingHidePostNumbers));
        Bind(WebView2Helper.AiHiddenPushProperty,         nameof(ThreadTabViewModel.AiHiddenPostNumbers));
        Bind(WebView2Helper.ScrollToPostPushProperty,     nameof(ThreadTabViewModel.PendingScrollToPost));
        BindPane(WebView2Helper.ThreadConfigJsonProperty, "DataContext.Main.ThreadConfigJson");
        Bind(WebView2Helper.ThreadProviderConfigJsonProperty, nameof(ThreadTabViewModel.ProviderConfigJson));
        BindPane(WebView2Helper.ThreadShortcutsJsonProperty, "DataContext.Main.ThreadShortcutsJson");
        BindingOperations.SetBinding(wv, VisibilityProperty, new Binding(nameof(ThreadTabViewModel.IsSelected))
        {
            Converter = (IValueConverter)FindResource("BoolToVis"),
        });
    }
}
