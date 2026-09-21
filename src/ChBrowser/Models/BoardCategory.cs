using System.Collections.Generic;

namespace ChBrowser.Models;

/// <summary>
/// 板一覧の 1 カテゴリ (5ch なら bbsmenu.json の menu_list[i]、まちBBS なら bbsmenu.html の地区)。
/// <paramref name="ProviderId"/> は所属する掲示板提供者 (板一覧ペインでは提供者ごとのトップノード配下に並ぶ)。
/// </summary>
public sealed record BoardCategory(
    string CategoryName,
    int    CategoryNumber,
    IReadOnlyList<Board> Boards,
    string ProviderId = "5ch");
