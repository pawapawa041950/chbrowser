using ChBrowser.Models;

namespace ChBrowser.ViewModels;

/// <summary>板一覧 TreeView の子ノード (板) に対応する ViewModel。</summary>
public sealed class BoardViewModel
{
    public Board Board { get; }

    public BoardViewModel(Board board)
    {
        Board = board;
    }

    public string BoardName => Board.BoardName;
}
