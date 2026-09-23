using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ChBrowser.Models;
using CommunityToolkit.Mvvm.Input;

namespace ChBrowser.ViewModels;

/// <summary>板一覧 (bbsmenu) の取得・WebView 用 HTML 生成・選択処理。</summary>
public sealed partial class MainViewModel
{
    /// <summary>「○○板一覧更新」(提供者 Id 指定)。メニュー「板一覧」は板一覧を持つ提供者からこの項目を自動で組む
    /// (MainWindow.BuildBoardMenu)。新しい掲示板も <see cref="ChBrowser.Services.Bbs.BbsCapabilities.BoardList"/> を宣言すれば並ぶ。</summary>
    [RelayCommand]
    private Task RefreshBoardListOfAsync(string? providerId)
    {
        var p = string.IsNullOrEmpty(providerId) ? null : ChBrowser.Services.Bbs.BbsRegistry.FindById(providerId);
        if (p is null) { StatusMessage = $"掲示板 '{providerId}' の提供者が登録されていません"; return Task.CompletedTask; }
        return RefreshProviderBoardListAsync(p);
    }

    // ショートカット (main.refresh_*_board_list) 用の個別コマンド。中身は上と同じ。
    [RelayCommand] private Task RefreshBoardListAsync()      => RefreshBoardListOfAsync("5ch");
    [RelayCommand] private Task RefreshMachiBoardListAsync() => RefreshBoardListOfAsync("machi");
    [RelayCommand] private Task RefreshEddiBoardListAsync()  => RefreshBoardListOfAsync("eddi");

    /// <summary>提供者 1 つ分の板一覧を取得して差し替える。他の提供者の分は保持する。</summary>
    private async Task RefreshProviderBoardListAsync(ChBrowser.Services.Bbs.IBbsProvider provider)
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            StatusMessage = $"{provider.DisplayName} の板一覧を取得中...";
            var cats = await _bbsmenuClient.FetchAndSaveAsync(provider).ConfigureAwait(true);
            ApplyCategories(provider.Id, cats);
            StatusMessage = $"{provider.DisplayName} の板一覧を更新しました: {TotalBoards(cats)} 板";
        }
        catch (System.Exception ex)
        {
            StatusMessage = $"{provider.DisplayName} の板一覧の取得に失敗: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>提供者 Id → その板一覧。板一覧ペインは提供者ごとのトップノード配下にカテゴリを並べる。</summary>
    private readonly Dictionary<string, IReadOnlyList<BoardCategory>> _categoriesByProvider = new(System.StringComparer.Ordinal);

    /// <summary>提供者ノードの開閉状態 (既定: 開)。起動時に board_tree.json から復元する。</summary>
    private Dictionary<string, bool>? _providerExpandedStore;
    private Dictionary<string, bool> _providerExpanded
    {
        get
        {
            if (_providerExpandedStore is null)
            {
                _providerExpandedStore = new(System.StringComparer.Ordinal);
                foreach (var id in BoardTreeState.CollapsedProviders) _providerExpandedStore[id] = false;
            }
            return _providerExpandedStore;
        }
    }

    // 板一覧ツリーの開閉状態 (data/app/board_tree.json)。トグルのたびに保存し、起動時・板一覧の再取得時に当てる。
    private ChBrowser.Services.Storage.BoardTreeStateStorage? _boardTreeStorage;
    private ChBrowser.Services.Storage.BoardTreeState? _boardTreeState;
    private ChBrowser.Services.Storage.BoardTreeStateStorage BoardTreeStorage => _boardTreeStorage ??= new(_paths);
    private ChBrowser.Services.Storage.BoardTreeState BoardTreeState => _boardTreeState ??= BoardTreeStorage.Load();
    private HashSet<string>? _expandedCategoryKeys;
    private HashSet<string> ExpandedCategoryKeys => _expandedCategoryKeys ??= new(BoardTreeState.ExpandedCategories, System.StringComparer.Ordinal);

    private void SaveBoardTreeState()
    {
        var state = BoardTreeState;
        state.CollapsedProviders = _providerExpanded.Where(kv => !kv.Value).Select(kv => kv.Key).OrderBy(x => x, System.StringComparer.Ordinal).ToList();
        state.ExpandedCategories = ExpandedCategoryKeys.OrderBy(x => x, System.StringComparer.Ordinal).ToList();
        BoardTreeStorage.Save(state);
    }

    /// <summary>5ch の板一覧を差し替える (互換 API)。</summary>
    private void ApplyCategories(IReadOnlyList<BoardCategory> cats) => ApplyCategories("5ch", cats);

    /// <summary>提供者 1 つ分の板一覧を差し替え、板一覧ペインにはその掲示板のノードだけを送る。</summary>
    private void ApplyCategories(string providerId, IReadOnlyList<BoardCategory> cats)
    {
        SetProviderCategories(providerId, cats);
        RebuildBoardCategories();
        PushBoardTree(providerId);
    }

    /// <summary>提供者の板一覧を内部表に入れる (表示は変えない)。提供者 Id が入っていない (旧経路 / 5ch JSON) 場合は付け直す。</summary>
    private void SetProviderCategories(string providerId, IReadOnlyList<BoardCategory> cats)
        => _categoriesByProvider[providerId] = cats.Select(c => c.ProviderId == providerId ? c : c with { ProviderId = providerId }).ToList();

    /// <summary>ディスクに保存済みの板一覧を全提供者分まとめて読む (起動時)。読み込みは並行で行い、板一覧ペインへは 1 回だけ送る。
    /// 読めなかった提供者は空のまま (他の提供者は表示する)。戻り値は読めた板の総数。</summary>
    private async Task<int> LoadAllBoardListsFromDiskAsync()
    {
        var providers = ChBrowser.Services.Bbs.BbsRegistry.All
            .Where(p => (p.Capabilities & ChBrowser.Services.Bbs.BbsCapabilities.BoardList) != 0)
            .ToList();
        var loads = providers.Select(async p =>
        {
            try { return (p, cats: await _bbsmenuClient.LoadFromDiskAsync(p).ConfigureAwait(false), error: (string?)null); }
            catch (System.Exception ex) { return (p, cats: (IReadOnlyList<BoardCategory>)System.Array.Empty<BoardCategory>(), error: (string?)ex.Message); }
        }).ToList();
        var results = await Task.WhenAll(loads).ConfigureAwait(true);

        var total = 0;
        foreach (var (p, cats, error) in results)
        {
            if (error is not null)
                ChBrowser.Services.Logging.LogService.Instance.Write($"[boardList] {p.Id} の保存済み板一覧を読めませんでした: {error}");
            SetProviderCategories(p.Id, cats);
            total += TotalBoards(cats);
        }
        RebuildBoardCategories();
        PushBoardTree();
        return total;
    }

    /// <summary><see cref="BoardCategories"/> を提供者の登録順に組み直す (板の解決・AI 用の平坦な一覧もこれを見る)。</summary>
    private void RebuildBoardCategories()
    {
        BoardCategories.Clear();
        foreach (var provider in ChBrowser.Services.Bbs.BbsRegistry.All)
        {
            if (!_categoriesByProvider.TryGetValue(provider.Id, out var list)) continue;
            foreach (var c in list)
            {
                BoardCategories.Add(new BoardCategoryViewModel(c)
                {
                    // 前回終了時 (または再取得前) に開いていたカテゴリは開いたまま
                    IsExpanded = ExpandedCategoryKeys.Contains(ChBrowser.Services.Storage.BoardTreeState.CategoryKey(c.ProviderId, c.CategoryName)),
                });
            }
        }
    }

    /// <summary>板一覧ペインへ中身を送る (<c>setBoardTree</c>)。<paramref name="providerId"/> を指定するとその掲示板のノードだけ
    /// (JS はそのノードだけ差し替える)、null なら全体。ページ (シェル) の読み込み直後は JS の <c>ready</c> を受けて全体を送る。</summary>
    public void PushBoardTree(string? providerId = null)
    {
        // 板一覧を持つ掲示板は従来どおり。持たない掲示板 (したらば / reddit) も「検索」「表示済み板」を出すためにノードを作る。
        var nodes = ChBrowser.Services.Bbs.BbsRegistry.All
            .Where(p => providerId is null || p.Id == providerId)
            .Select(p => new ChBrowser.Services.Render.BoardListProviderNode(
                p.Id, p.DisplayName, !_providerExpanded.TryGetValue(p.Id, out var ex) || ex,
                HasBoardList:   (p.Capabilities & ChBrowser.Services.Bbs.BbsCapabilities.BoardList) != 0,
                SupportsSearch: p.SupportsBoardSearch))
            .ToList();
        BoardTreePush = new BoardTreeMessage(providerId is null,
            ChBrowser.Services.Render.BoardListHtmlBuilder.BuildTree(nodes, BoardCategories));
    }

    /// <summary>板一覧ペインのシェル HTML を (CSS の変更を反映して) 作り直す。中身が変わればページが読み直され、
    /// JS の <c>ready</c> で全体が送り直される。</summary>
    public void RefreshBoardListHtml() => BoardListHtml = ChBrowser.Services.Render.BoardListHtmlBuilder.BuildShell();

    /// <summary>JS 側 setProviderExpanded メッセージから呼ばれる (HTML は再生成しない)。</summary>
    public void SetProviderExpanded(string providerId, bool expanded)
    {
        if (_providerExpanded.TryGetValue(providerId, out var cur) ? cur == expanded : expanded) return;
        _providerExpanded[providerId] = expanded;
        SaveBoardTreeState();
    }

    /// <summary>JS 側 setCategoryExpanded メッセージから呼ばれる。
    /// ViewModel の IsExpanded を更新 (HTML は再生成しない — トグルは DOM 上で既に反映されているため)。
    /// 同名カテゴリが別提供者にあり得るので providerId でも絞る (空なら名前だけで探す = 旧経路)。</summary>
    public void SetCategoryExpanded(string categoryName, bool expanded, string? providerId = null)
    {
        foreach (var c in BoardCategories)
        {
            if (c.CategoryName == categoryName && (string.IsNullOrEmpty(providerId) || c.ProviderId == providerId))
            {
                c.IsExpanded = expanded;
                var key = ChBrowser.Services.Storage.BoardTreeState.CategoryKey(c.ProviderId, c.CategoryName);
                if (expanded ? ExpandedCategoryKeys.Add(key) : ExpandedCategoryKeys.Remove(key)) SaveBoardTreeState();
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
        var fallbackUrl = ChBrowser.Services.Bbs.BbsRegistry.ResolveOrDefault(host).BoardUrl(host, directoryName);
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
