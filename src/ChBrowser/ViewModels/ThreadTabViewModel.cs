using System;
using System.Collections.Generic;
using ChBrowser.Controls;
using ChBrowser.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChBrowser.ViewModels;

public enum ThreadViewMode
{
    Flat,
    Tree,
    DedupTree,
}

/// <summary>JS の <c>appendPosts</c> に渡すペイロード (Phase 20)。
/// <see cref="IsIncremental"/> = true なら、dedup-tree モードでこの batch 以降を「末尾 incremental block」として
/// 既存ツリーとは別レンダリングにする (= 既読下に新着を表示するため)。</summary>
public sealed record AppendBatchData(IReadOnlyList<Post> Posts, bool IsIncremental);

/// <summary>
/// 1 スレッド = 1 タブ。WebView2 へは Posts (Post 列) を Bind し、HTML 構築は JS 側で行う。
/// 表示モード (Flat/Tree/DedupTree) は今後 JS 側で実装予定。現状は Flat のみ動作、
/// CycleViewModeCommand は state を切り替えるが描画は変わらない (スタブ)。
/// </summary>
public sealed partial class ThreadTabViewModel : ObservableObject, IThreadDisplayBinding
{
    public Board  Board     { get; }
    public string ThreadKey { get; }

    public IRelayCommand CloseCommand           { get; }
    public IRelayCommand CycleViewModeCommand  { get; }
    public IRelayCommand DeleteCommand          { get; }
    public IRelayCommand RefreshCommand         { get; }
    public IRelayCommand AddToFavoritesCommand  { get; }
    public IRelayCommand WriteCommand           { get; }

    [ObservableProperty]
    private string _header;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private long _datSize;

    /// <summary>NG で透明化された (= JS に送られなかった) レス数の累積 (Phase 13)。
    /// ステータスバーに「あぼーん N」として表示。</summary>
    [ObservableProperty]
    private int _hiddenCount;

    [ObservableProperty]
    private ThreadViewMode _viewMode = ThreadViewMode.Flat;

    /// <summary>このタブが現在保持しているレス全件。スレ ViewModel 内/MainViewModel 内での件数読みだけに使う。
    /// PropertyChanged は発火させない (= WebView2 への描画は <see cref="LatestAppendBatch"/> を経由する単一チャネル)。
    /// 旧実装には Posts attached property + setPosts JS メッセージの全置換チャネルもあったが、
    /// 増分チャネルとの順序競合で「先にレンダ → setPosts([]) で消去」の真っ白現象が出ていたため撤去した。</summary>
    public IReadOnlyList<Post> Posts { get; private set; } = new List<Post>();

    /// <summary>
    /// streaming で受け取った直近のレスバッチと、それが「差分 append (= incremental)」かどうかのフラグ。
    /// WebView2Helper.AppendBatch がこれを観測して JS の window.appendPosts() に送る。
    /// スレ表示への描画は常にこのチャネルだけを通る。
    /// </summary>
    [ObservableProperty]
    private AppendBatchData? _latestAppendBatch;

    /// <summary>
    /// JS にスクロール対象として伝えるレス番号。idx.json から読んだ初期値、または
    /// JS からの scrollPosition メッセージで随時更新される。
    /// </summary>
    [ObservableProperty]
    private int? _scrollTargetPostNumber;

    /// <summary>「ここまで読んだ」帯の対象レス番号 (Phase 19)。
    /// idx.json の <c>LastReadMarkPostNumber</c> から初期化、JS からの readMark メッセージで増加方向のみ更新される。</summary>
    [ObservableProperty]
    private int? _readMarkPostNumber;

    /// <summary>
    /// このタブが現在 TabControl で選択されているか。各タブが専有する WebView2 の
    /// Visibility をこれに bind する (= 選択タブだけ可視、他は Collapsed)。
    /// MainViewModel が SelectedThreadTab 変更時に全タブの IsSelected を更新する。
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>このスレがお気に入りに登録されているか。
    /// ★ ボタンの押下表示 (背景強調) や、トグル動作 (押すと add or remove) のために使う。
    /// MainViewModel がお気に入り変更後に <see cref="MainViewModel.RefreshFavoritedStateOfAllTabs"/> で更新する。</summary>
    [ObservableProperty]
    private bool _isFavorited;

    /// <summary>タブ見出しに表示する状態マーク。意味は <see cref="LogMarkState"/> と同じ:
    ///   Cached (青) = ログあり / 件数一致、Updated (緑) = 新着あり、Dropped (茶) = subject.txt から消えた。
    /// 初期は Cached (= dat 取得直後)。板の subject.txt 再取得時に MainViewModel が更新する。</summary>
    [ObservableProperty]
    private LogMarkState _state = LogMarkState.Cached;

    public bool IsViewModeFlat       => ViewMode == ThreadViewMode.Flat;
    public bool IsViewModeTree       => ViewMode == ThreadViewMode.Tree;
    public bool IsViewModeDedupTree  => ViewMode == ThreadViewMode.DedupTree;

    /// <summary>このスレを開いた時の元タイトル (お気に入り登録時の Title 用)。</summary>
    public string Title { get; }

    public ThreadTabViewModel(
        Board                                board,
        ThreadInfo                           info,
        Action<ThreadTabViewModel>           closeCallback,
        Action<ThreadTabViewModel>?          deleteCallback         = null,
        Action<ThreadTabViewModel>?          refreshCallback        = null,
        Action<ThreadTabViewModel>?          addToFavoritesCallback = null,
        Action<ThreadTabViewModel>?          writeCallback          = null)
    {
        Board                  = board;
        ThreadKey              = info.Key;
        Title                  = info.Title;
        _header                = TruncateForTab(info.Title);
        CloseCommand           = new RelayCommand(() => closeCallback(this));
        DeleteCommand          = new RelayCommand(() => deleteCallback?.Invoke(this));
        RefreshCommand         = new RelayCommand(() => refreshCallback?.Invoke(this));
        AddToFavoritesCommand  = new RelayCommand(() => addToFavoritesCallback?.Invoke(this));
        WriteCommand           = new RelayCommand(() => writeCallback?.Invoke(this));
        CycleViewModeCommand   = new RelayCommand(() =>
        {
            ViewMode = ViewMode switch
            {
                ThreadViewMode.Flat      => ThreadViewMode.Tree,
                ThreadViewMode.Tree      => ThreadViewMode.DedupTree,
                ThreadViewMode.DedupTree => ThreadViewMode.Flat,
                _                         => ThreadViewMode.Flat,
            };
        });
    }

    /// <summary>レスを末尾に追加。内部 <see cref="Posts"/> を更新したあと、
    /// <see cref="LatestAppendBatch"/> 経由で WebView2 (JS) に増分を送る。
    /// <paramref name="isIncremental"/> = true は「初期表示が完了した後の差分追加」を示し
    /// (= リフレッシュ / お気に入りチェック後の差分等)、JS 側の dedup-tree 描画で 2 セクション構成
    /// (既存ツリー + 末尾の incremental tail block) に切り替えるシグナルになる (Phase 20)。</summary>
    public void AppendPosts(IReadOnlyList<Post> batch, bool isIncremental = false)
    {
        if (batch.Count == 0) return;
        var merged = new List<Post>(Posts.Count + batch.Count);
        merged.AddRange(Posts);
        merged.AddRange(batch);
        Posts = merged;
        LatestAppendBatch = new AppendBatchData(batch, isIncremental);
    }

    partial void OnViewModeChanged(ThreadViewMode value)
    {
        OnPropertyChanged(nameof(IsViewModeFlat));
        OnPropertyChanged(nameof(IsViewModeTree));
        OnPropertyChanged(nameof(IsViewModeDedupTree));
        // Tree/DedupTree は JS 側に未実装。現状は ViewMode 切替で再描画は起きない。
    }

    private static string TruncateForTab(string title)
    {
        const int max = 24;
        return title.Length <= max ? title : title[..max] + "…";
    }
}
