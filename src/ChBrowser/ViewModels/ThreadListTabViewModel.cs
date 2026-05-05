using System;
using System.Collections.Generic;
using ChBrowser.Models;
using ChBrowser.Services.Render;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChBrowser.ViewModels;

/// <summary>各スレッドのログマーク状態。</summary>
public enum LogMarkState
{
    /// <summary>ログ無し。マーク非表示。</summary>
    None,
    /// <summary>ログあり、最後の取得時から件数変化なし。青丸。</summary>
    Cached,
    /// <summary>ログあり、subject.txt の方が件数多い (新着あり)。緑丸。</summary>
    Updated,
    /// <summary>お気に入り表示時、subject.txt にもう存在しないスレ (= 落ちた)。茶色丸。
    /// 通常の板表示 (subject.txt 由来の一覧) では出ない状態。</summary>
    Dropped,
}

/// <summary>スレ一覧 JS への増分マーク更新エントリ。
/// <see cref="Host"/> / <see cref="DirectoryName"/> / <see cref="Key"/> の 3 つで一意に行を特定する
/// (= 集約タブ "全ログ" / "お気に入り (板として開く)" のように複数の板由来行が混在する場合でも
/// 正しい行だけにマークが当たるように)。</summary>
public sealed record LogMarkChange(string Host, string DirectoryName, string Key, LogMarkState State);

/// <summary>スレ一覧 JS への増分ログマーク更新通知。複数行をまとめて変更可能。</summary>
public sealed record LogMarkPatch(IReadOnlyList<LogMarkChange> Changes);

/// <summary>スレ一覧 JS への増分お気に入りマーク更新エントリ (Phase 18+)。
/// LogMarkChange と同じ識別 (host, dir, key) を使い、行の <c>is-favorited</c> クラスを toggle する。</summary>
public sealed record FavoritedChange(string Host, string DirectoryName, string Key, bool IsFavorited);

/// <summary>スレ一覧 JS への増分お気に入りマーク更新通知。複数行をまとめて変更可能。</summary>
public sealed record FavoritedPatch(IReadOnlyList<FavoritedChange> Changes);

/// <summary>スレ一覧 1 タブ。
/// <see cref="Board"/> != null なら 1 板由来の通常タブ、null ならお気に入りディレクトリ展開タブ。
/// 表示は両者ともに同じ <see cref="ThreadListHtmlBuilder"/> を経由するので JS 側は区別しない。</summary>
public sealed partial class ThreadListTabViewModel : ObservableObject
{
    /// <summary>このタブが 1 板由来 (subject.txt) の場合の板。お気に入りディレクトリ展開タブでは null。</summary>
    public Board? Board { get; }

    /// <summary>お気に入りディレクトリ展開タブの場合、対応する folder の Id。通常タブは null。</summary>
    public Guid? FavoritesFolderId { get; }

    /// <summary>「新規スレ立て」のような板単位アクションを有効化するための判定 (XAML バインド用)。
    /// Board=null のお気に入り展開タブでは false。</summary>
    public bool IsBoardTab => Board is not null;

    /// <summary>現在表示中のスレ一覧 (行ごとの板情報込み)。openThread の payload 検証や差分更新で使う。</summary>
    public IReadOnlyList<ThreadListItem> Items { get; private set; } = Array.Empty<ThreadListItem>();

    public IRelayCommand CloseCommand { get; }

    [ObservableProperty]
    private string _header;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _html = "";

    /// <summary>JS 側に has-log クラスを toggle させるための増分更新。null は no-op。</summary>
    [ObservableProperty]
    private LogMarkPatch? _logMarkUpdate;

    /// <summary>JS 側に is-favorited クラスを toggle させるための増分更新 (Phase 18+)。null は no-op。</summary>
    [ObservableProperty]
    private FavoritedPatch? _favoritedUpdate;

    /// <summary>このタブが現在 TabControl で選択されているか。各タブが専有する WebView2 の
    /// Visibility をこれに bind する (= 選択タブだけ可視、他は Collapsed)。
    /// MainViewModel が SelectedThreadListTab 変更時に全タブの IsSelected を更新する。</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>板タブ (IsBoardTab=true) の場合、その板自身がお気に入りに登録済みかどうか。
    /// スレ一覧ペインのツールバーのお気に入りボタンの押下状態に使う。
    /// 板タブ以外 (お気に入り展開タブ等) では常に false (ボタンは disable)。
    /// MainViewModel.RefreshFavoritedStateOfAllTabs で更新される。</summary>
    [ObservableProperty]
    private bool _isBoardFavorited;

    /// <summary>通常の板タブ。</summary>
    public ThreadListTabViewModel(Board board, Action<ThreadListTabViewModel> closeCallback)
    {
        Board         = board;
        _header       = board.BoardName;
        CloseCommand  = new RelayCommand(() => closeCallback(this));
    }

    /// <summary>お気に入りディレクトリ展開タブ (Phase 7)。</summary>
    public ThreadListTabViewModel(Guid favoritesFolderId, string header, Action<ThreadListTabViewModel> closeCallback)
    {
        Board              = null;
        FavoritesFolderId  = favoritesFolderId;
        _header            = "★ " + header;
        CloseCommand       = new RelayCommand(() => closeCallback(this));
    }

    /// <summary>1 板由来のスレ一覧をセット (通常タブ用)。
    /// <paramref name="favoritedKeys"/> に含まれるスレは行に <c>is-favorited</c> クラスが付き、状態マーク背景に ★ が表示される。</summary>
    public void SetThreads(
        IReadOnlyList<ThreadInfo>                                 threads,
        DateTimeOffset                                            now,
        IReadOnlyDictionary<string, LogMarkState>?                logStates       = null,
        IReadOnlySet<string>?                                     favoritedKeys   = null)
    {
        if (Board is null) throw new InvalidOperationException("SetThreads は Board 付きタブ専用");
        var items = new List<ThreadListItem>(threads.Count);
        foreach (var t in threads)
        {
            var state = logStates    is not null && logStates.TryGetValue(t.Key, out var s) ? s : LogMarkState.None;
            var fav   = favoritedKeys is not null && favoritedKeys.Contains(t.Key);
            items.Add(new ThreadListItem(t, Board.Host, Board.DirectoryName, Board.BoardName, state, fav));
        }
        SetItems(items, now);
    }

    /// <summary>事前に組み立てた <see cref="ThreadListItem"/> 列を直接セット (お気に入り展開タブ用)。</summary>
    public void SetItems(IReadOnlyList<ThreadListItem> items, DateTimeOffset now)
    {
        Items            = items;
        Html             = ThreadListHtmlBuilder.Build(items, now);
        LogMarkUpdate    = null; // 新しい一覧を出したので保留中の差分はリセット
        FavoritedUpdate  = null;
    }

    /// <summary>1 件のスレッドのマーク状態を変更する増分通知を送る (集約タブ対応のため host/dir も指定)。</summary>
    public void SetLogMark(string host, string directoryName, string key, LogMarkState state)
        => LogMarkUpdate = new LogMarkPatch(new[] { new LogMarkChange(host, directoryName, key, state) });

    /// <summary>複数行のお気に入り状態をまとめて変更する増分通知 (Phase 18+)。</summary>
    public void SetFavoritedPatch(IReadOnlyList<FavoritedChange> changes)
    {
        if (changes.Count == 0) return;
        FavoritedUpdate = new FavoritedPatch(changes);
    }

    /// <summary>現在の <see cref="Items"/> と <paramref name="favKeys"/> を突き合わせて、
    /// 差分の <see cref="FavoritedChange"/> 列を返しつつ、Items 内の <c>IsFavorited</c> も新値に同期する
    /// (= 次回の diff 計算で正しく動くよう、C# 側の snapshot もミューテートする)。
    /// 差分があれば <see cref="FavoritedUpdate"/> を発火 → JS に push される。
    /// 差分が無ければ何もしない。</summary>
    public void SyncFavoritedFromKeySet(IReadOnlySet<(string Host, string Dir, string Key)> favKeys)
    {
        if (Items.Count == 0) return;

        List<FavoritedChange>? changes = null;
        var newItems = new List<ThreadListItem>(Items.Count);
        foreach (var item in Items)
        {
            var nowFav = favKeys.Contains((item.Host, item.DirectoryName, item.Info.Key));
            if (item.IsFavorited != nowFav)
            {
                changes ??= new List<FavoritedChange>();
                changes.Add(new FavoritedChange(item.Host, item.DirectoryName, item.Info.Key, nowFav));
                newItems.Add(item with { IsFavorited = nowFav });
            }
            else
            {
                newItems.Add(item);
            }
        }
        if (changes is null) return;
        Items           = newItems;
        FavoritedUpdate = new FavoritedPatch(changes);
    }

    /// <summary>このタブの <see cref="Items"/> に (host, dir, key) で一致する行が存在するか。
    /// 集約タブで「自分が表示中のスレかどうか」を判定して、無関係な通知を無視するために使う。</summary>
    public bool ContainsThread(string host, string directoryName, string key)
    {
        foreach (var it in Items)
            if (it.Host == host && it.DirectoryName == directoryName && it.Info.Key == key)
                return true;
        return false;
    }
}
