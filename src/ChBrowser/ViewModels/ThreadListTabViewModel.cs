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

/// <summary>スレ一覧 JS への増分マーク更新エントリ。</summary>
public sealed record LogMarkChange(string Key, LogMarkState State);

/// <summary>スレ一覧 JS への増分ログマーク更新通知。複数キーをまとめて変更可能。</summary>
public sealed record LogMarkPatch(IReadOnlyList<LogMarkChange> Changes);

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
        Items          = items;
        Html           = ThreadListHtmlBuilder.Build(items, now);
        LogMarkUpdate  = null; // 新しい一覧を出したので保留中の差分はリセット
    }

    /// <summary>1 件のスレッドのマーク状態を変更する増分通知を送る。</summary>
    public void SetLogMark(string key, LogMarkState state)
        => LogMarkUpdate = new LogMarkPatch(new[] { new LogMarkChange(key, state) });
}
