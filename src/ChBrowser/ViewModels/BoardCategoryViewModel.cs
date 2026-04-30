using System.Collections.Generic;
using ChBrowser.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChBrowser.ViewModels;

/// <summary>板一覧 TreeView の親ノード (カテゴリ) に対応する ViewModel。
/// Phase 14 で WebView2 化したあとも、HTML を再生成するときに <c>&lt;details open&gt;</c> を
/// 出し分けるために <see cref="IsExpanded"/> を保持する。</summary>
public sealed partial class BoardCategoryViewModel : ObservableObject
{
    public string CategoryName { get; }
    public IReadOnlyList<BoardViewModel> Boards { get; }

    /// <summary>初期は閉じた状態 (= bbsmenu のカテゴリ数が 50+ になりうるので折りたたんでおいた方が見やすい)。
    /// JS から setCategoryExpanded メッセージで更新される。</summary>
    [ObservableProperty]
    private bool _isExpanded;

    public BoardCategoryViewModel(BoardCategory category)
    {
        CategoryName = category.CategoryName;
        var list = new List<BoardViewModel>(category.Boards.Count);
        foreach (var b in category.Boards) list.Add(new BoardViewModel(b));
        Boards = list;
    }
}
