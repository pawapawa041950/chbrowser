using System.Collections.Generic;

namespace ChBrowser.Models;

/// <summary>
/// bbsmenu.json の menu_list[i] に対応する 1 カテゴリ。
/// </summary>
public sealed record BoardCategory(
    string CategoryName,
    int    CategoryNumber,
    IReadOnlyList<Board> Boards);
