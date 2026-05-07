using System.Collections.Generic;
using System.Threading.Tasks;
using ChBrowser.Models;
using CommunityToolkit.Mvvm.Input;

namespace ChBrowser.ViewModels;

/// <summary>板一覧 (bbsmenu) の取得・WebView 用 HTML 生成・選択処理。</summary>
public sealed partial class MainViewModel
{
    [RelayCommand]
    private async Task RefreshBoardListAsync()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            StatusMessage = "板一覧を取得中...";
            var cats = await _bbsmenuClient.FetchAndSaveAsync().ConfigureAwait(true);
            ApplyCategories(cats);
            StatusMessage = $"板一覧を更新しました: {TotalBoards(cats)} 板";
        }
        catch (System.Exception ex)
        {
            StatusMessage = $"板一覧の取得に失敗: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyCategories(IReadOnlyList<BoardCategory> cats)
    {
        BoardCategories.Clear();
        foreach (var c in cats) BoardCategories.Add(new BoardCategoryViewModel(c));
        RefreshBoardListHtml();
    }

    /// <summary>板一覧 WebView2 用の HTML を <see cref="BoardCategories"/> から再生成する。</summary>
    public void RefreshBoardListHtml()
        => BoardListHtml = ChBrowser.Services.Render.BoardListHtmlBuilder.Build(BoardCategories);

    /// <summary>JS 側 setCategoryExpanded メッセージから呼ばれる。
    /// ViewModel の IsExpanded を更新 (HTML は再生成しない — トグルは DOM 上で既に反映されているため)。</summary>
    public void SetCategoryExpanded(string categoryName, bool expanded)
    {
        foreach (var c in BoardCategories)
        {
            if (c.CategoryName == categoryName)
            {
                c.IsExpanded = expanded;
                return;
            }
        }
    }

    /// <summary>板一覧 WebView2 から openBoard メッセージで呼ばれる。
    /// (host, directoryName) で板を解決して LoadThreadListAsync に渡す。</summary>
    public Task OpenBoardFromHtmlListAsync(string host, string directoryName, string boardName)
    {
        var board = ResolveBoard(host, directoryName, boardName);
        var bvm   = new BoardViewModel(board);
        return LoadThreadListAsync(bvm);
    }

    /// <summary>板一覧 WebView2 から contextMenu (target=board) → 「お気に入りに追加」を選んだとき。</summary>
    public void AddBoardToFavoritesByHostDir(string host, string directoryName, string boardName)
    {
        var board = ResolveBoard(host, directoryName, boardName);
        var bvm   = new BoardViewModel(board);
        AddBoardToFavorites(bvm);
    }

    private static int TotalBoards(IReadOnlyList<BoardCategory> cats)
    {
        var n = 0;
        foreach (var c in cats) n += c.Boards.Count;
        return n;
    }

    /// <summary>(host, directoryName) から <see cref="Board"/> を解決する。
    /// 板一覧 (bbsmenu.json) に登録があればそのオブジェクトを、無ければ最低限の Board を組み立てて返す
    /// (お気に入りに登録した板/スレが、bbsmenu.json から消えても開けるようにするため)。
    ///
    /// 解決パス:
    ///   1. host + directoryName 完全一致
    ///   2. root domain (= 5ch.io / bbspink.com) + directoryName 一致 (= ユーザがスレ本文の
    ///      <c>https://hayabusa9.5ch.io/test/read.cgi/{dir}/{key}/</c> 等の任意 subdomain URL から
    ///      クリックして開く経路で、bbsmenu には別の subdomain で登録されている場合の救済)
    ///   3. fallback で URL の host 直書きで Board を組み立て (= bbsmenu 未登録)
    /// 1 で見つからず 2 だけで救った場合、bbsmenu 登録時の正しい subdomain を採用する
    /// (= URL の subdomain で dat を直接 fetch すると 5ch.io は 404 を返すため)。</summary>
    public Board ResolveBoard(string host, string directoryName, string fallbackBoardName)
    {
        // Pass 1: host + dir の厳密一致
        foreach (var cat in BoardCategories)
            foreach (var bvm in cat.Boards)
                if (bvm.Board.Host == host && bvm.Board.DirectoryName == directoryName)
                {
                    ChBrowser.Services.Logging.LogService.Instance.Write(
                        $"[resolveBoard] Pass1 (host+dir exact) hit: host='{host}', dir='{directoryName}' → Url='{bvm.Board.Url}'");
                    return bvm.Board;
                }

        // Pass 2: root domain + dir 一致 (= subdomain 違いで bbsmenu に登録があるケース)
        var rootIn = ChBrowser.Services.Storage.DataPaths.ExtractRootDomain(host);
        foreach (var cat in BoardCategories)
            foreach (var bvm in cat.Boards)
            {
                var rootB = ChBrowser.Services.Storage.DataPaths.ExtractRootDomain(bvm.Board.Host);
                if (string.Equals(rootB, rootIn, StringComparison.OrdinalIgnoreCase)
                    && bvm.Board.DirectoryName == directoryName)
                {
                    ChBrowser.Services.Logging.LogService.Instance.Write(
                        $"[resolveBoard] Pass2 (rootDomain+dir) hit: input host='{host}' → bbsmenu Url='{bvm.Board.Url}' (host='{bvm.Board.Host}')");
                    return bvm.Board;
                }
            }

        // Fallback: bbsmenu に未登録。最低限の Board を URL から組み立て。
        var fallbackUrl = $"https://{host}/{directoryName}/";
        ChBrowser.Services.Logging.LogService.Instance.Write(
            $"[resolveBoard] FALLBACK (bbsmenu に未登録): host='{host}', dir='{directoryName}', categories={BoardCategories.Count} → Url='{fallbackUrl}'");
        return new Board(
            DirectoryName: directoryName,
            BoardName:     string.IsNullOrEmpty(fallbackBoardName) ? directoryName : fallbackBoardName,
            Url:           fallbackUrl,
            CategoryName:  "",
            CategoryOrder: 0);
    }
}
