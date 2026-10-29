using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Bbs;
using ChBrowser.Services.Storage;

namespace ChBrowser.ViewModels;

/// <summary>板一覧を持たない掲示板 (したらば / reddit) の「検索」と「表示済み板」(板一覧ペインの提供者ノード配下の 2 項目)。
/// どちらもスレ一覧ペインに板の行 (<see cref="ThreadListItemKind.Board"/>、クリックで板を開く) として並べる。
/// タブは (提供者, 種類[, キーワード]) ごとに 1 枚で、タブの「更新」で検索し直す / 走査し直す。</summary>
public sealed partial class MainViewModel
{
    /// <summary>板の行を並べるタブ (検索結果 / 表示済み板 / 掲示板の一覧) の中身の作り方。タブ ID → 生成関数。
    /// 生成関数にはタブが渡る (届いた分から表示する一覧は、取得の途中でタブの行を差し替える)。</summary>
    private readonly Dictionary<Guid, Func<ThreadListTabViewModel, Task<(List<ThreadListItem> Items, string Status)>>> _boardRowTabSources = new();

    /// <summary>提供者ごとの直近の検索キーワード (ダイアログの初期値)。</summary>
    private readonly Dictionary<string, string> _lastBoardSearch = new(StringComparer.Ordinal);

    /// <summary>板一覧ペインの提供者ノードの項目 (<c>search</c> / <c>shown</c>) から呼ばれる。</summary>
    public async Task RunProviderActionAsync(string providerId, string action, System.Windows.Window? owner)
    {
        var provider = BbsRegistry.FindById(providerId);
        if (provider is null) return;
        switch (action)
        {
            case "search":
            {
                if (!provider.SupportsBoardSearch) return;
                _lastBoardSearch.TryGetValue(provider.Id, out var last);
                var keyword = ChBrowser.Views.InputDialog.Prompt(owner, $"{provider.DisplayName} の板を検索", "キーワード", last ?? "")?.Trim();
                if (string.IsNullOrEmpty(keyword)) return;
                _lastBoardSearch[provider.Id] = keyword;
                await OpenBoardSearchTabAsync(provider, keyword).ConfigureAwait(true);
                break;
            }
            case "shown":
                await OpenShownBoardsTabAsync(provider).ConfigureAwait(true);
                break;
            case "logs":
                await OpenProviderLogsTabAsync(provider).ConfigureAwait(true);
                break;
            default:
                // 掲示板が公式に出している一覧 (ホットなスレッド / ホットな板 / おすすめ)。action = "listing:<id>"
                if (action.StartsWith("listing:", StringComparison.Ordinal)
                    && provider.Listings.FirstOrDefault(l => l.Id == action["listing:".Length..]) is { } listing)
                    await OpenListingTabAsync(provider, listing).ConfigureAwait(true);
                break;
        }
    }

    /// <summary>掲示板の一覧 (<see cref="IBbsProvider.Listings"/>) のタブを開く。板の一覧なら板の行、スレの一覧ならいろいろな板のスレの行
    /// (ログがあるスレは状態マーク付き)。タブの「更新」で取り直す。</summary>
    public Task OpenListingTabAsync(IBbsProvider provider, ProviderListing listing)
    {
        var id = DeterministicTabId($"listing:{provider.Id}:{listing.Id}");
        var header = $"{listing.Name} ({provider.DisplayName})";
        return OpenBoardRowsTabAsync(id, header, async tab =>
        {
            // 何回にも分けて取る一覧 (reddit のおすすめ) は、届いた分からタブに並べる (Progress は UI スレッドで呼ばれる)
            var progress = new Progress<ProviderListingResult>(partial =>
            {
                if (!tab.IsBusy) return;   // 取得が終わった後に遅れて届いた途中経過は無視
                var rows = ListingItems(provider, partial);
                tab.SetItems(rows, DateTimeOffset.UtcNow);
                tab.Header        = $"{header} ({rows.Count} 取得中)";
                tab.StatusMessage = $"{header} を取得中... ({rows.Count} 件)";
            });
            var result = await provider.FetchListingAsync(_subjectClient.Http, listing.Id, default, progress).ConfigureAwait(true);
            var items  = ListingItems(provider, result);
            return (items, $"{provider.DisplayName}「{listing.Name}」: {items.Count} 件");
        });
    }

    /// <summary>一覧の中身 → スレ一覧の行 (板の行、またはいろいろな板のスレの行。ログがあるスレは状態マーク付き)。</summary>
    private List<ThreadListItem> ListingItems(IBbsProvider provider, ProviderListingResult result)
    {
        var items = new List<ThreadListItem>();
        foreach (var h in result.Boards)
        {
            var info = new ThreadInfo("", h.Board.BoardName, (int)Math.Min(int.MaxValue, h.Members ?? 0), items.Count + 1);
            items.Add(new ThreadListItem(info, h.Board.Host, h.Board.DirectoryName,
                h.Description.Length > 0 ? h.Description : $"{provider.DisplayName} {h.Board.Host}",
                LogMarkState.None, Favorites.FindBoard(h.Board.Host, h.Board.DirectoryName) is not null, ThreadListItemKind.Board));
        }
        if (result.Threads.Count > 0)
        {
            // ログの状態は板ごとに判定する (いろいろな板のスレが混ざる)
            var states = new Dictionary<(string, string, string), LogMarkState>();
            foreach (var g in result.Threads.GroupBy(t => (t.Board.Host, t.Board.DirectoryName)))
            {
                var board = g.First().Board;
                foreach (var (key, st) in BuildLogStates(board, g.Select(t => t.Info).ToList()))
                    states[(board.Host, board.DirectoryName, key)] = st;
            }
            var favKeys = Favorites.CollectFavoriteThreadKeys();
            foreach (var t in result.Threads)
            {
                var b = t.Board;
                items.Add(new ThreadListItem(t.Info with { Order = items.Count + 1 }, b.Host, b.DirectoryName, b.BoardName,
                    states.TryGetValue((b.Host, b.DirectoryName, t.Info.Key), out var st) ? st : LogMarkState.None,
                    favKeys.Contains((b.Host, b.DirectoryName, t.Info.Key))));
            }
        }
        return items;
    }

    /// <summary>板検索の結果タブを開く (同じ提供者・キーワードなら同じタブを使い、検索し直す)。</summary>
    public Task OpenBoardSearchTabAsync(IBbsProvider provider, string keyword)
    {
        var id = DeterministicTabId($"boardsearch:{provider.Id}:{keyword}");
        var header = $"🔍 {provider.DisplayName}: {Truncate(keyword, 16)}";
        return OpenBoardRowsTabAsync(id, header, async _ =>
        {
            var hits = await provider.SearchBoardsAsync(_subjectClient.Http, keyword, default).ConfigureAwait(true);
            var items = new List<ThreadListItem>(hits.Count);
            foreach (var h in hits)
            {
                var info = new ThreadInfo("", h.Board.BoardName, (int)Math.Min(int.MaxValue, h.Members ?? 0), items.Count + 1);
                items.Add(new ThreadListItem(info, h.Board.Host, h.Board.DirectoryName,
                    h.Description.Length > 0 ? h.Description : $"{provider.DisplayName} {h.Board.Host}",
                    LogMarkState.None, Favorites.FindBoard(h.Board.Host, h.Board.DirectoryName) is not null, ThreadListItemKind.Board));
            }
            return (items, $"{provider.DisplayName} の板検索「{keyword}」: {items.Count} 件");
        });
    }

    /// <summary>その掲示板の取得済みスレ (ローカルにログがあるスレ) を全板分まとめた一覧タブを開く (全ログの掲示板別版)。
    /// ネットワークは使わない。タブの「更新」で走査し直す。</summary>
    public Task OpenProviderLogsTabAsync(IBbsProvider provider)
    {
        var id = DeterministicTabId($"logs:{provider.Id}");
        var header = $"📜 {provider.DisplayName}: ログ";
        return OpenBoardRowsTabAsync(id, header, _ =>
        {
            var items = BuildAllLogsItems(excludeFavorites: false, provider.Id);
            return Task.FromResult((items, $"{provider.DisplayName} のログ: {items.Count} 件"));
        });
    }

    /// <summary>その掲示板で一度でも開いた (取得データがローカルにある) 板の一覧タブを開く。ネットワークは使わない。</summary>
    public Task OpenShownBoardsTabAsync(IBbsProvider provider)
    {
        var id = DeterministicTabId($"shownboards:{provider.Id}");
        var header = $"📁 {provider.DisplayName}: 表示済み板";
        return OpenBoardRowsTabAsync(id, header, async _ =>
        {
            var scanner = new UnlistedBoardScanner(_paths, _settingClient);
            // 板一覧を持たない掲示板なので「一覧に載っているか」は問わず、この提供者の保存ルートの板をすべて出す
            var found = scanner.Scan((_, _) => null).Where(ub => ub.Provider.Id == provider.Id).ToList();

            // 板名はローカルの板情報 (_SETTING.TXT の BBS_TITLE) から読む。板情報を持っていない板 (検索結果から開いた板など) は
            // 名前が dir 名 (internet/12345) になってしまうので、足りない分だけ取得してから読み直す。
            var missing = found.Where(ub => !System.IO.File.Exists(_paths.SettingTxtPath(ub.Board.Host, ub.Board.DirectoryName))).ToList();
            if (missing.Count > 0 && (provider.Capabilities & BbsCapabilities.BoardInfo) != 0)
            {
                foreach (var ub in missing.Take(30)) await EnsureBoardInfoCachedAsync(ub.Board).ConfigureAwait(true);
                found = scanner.Scan((_, _) => null).Where(ub => ub.Provider.Id == provider.Id).ToList();
            }
            var items = new List<ThreadListItem>(found.Count);
            foreach (var ub in found.OrderBy(u => u.Board.BoardName, StringComparer.CurrentCultureIgnoreCase))
            {
                var b = ub.Board;
                items.Add(new ThreadListItem(new ThreadInfo("", b.BoardName, ub.DatCount, items.Count + 1), b.Host, b.DirectoryName,
                    $"{provider.DisplayName} {b.Host}", LogMarkState.None,
                    Favorites.FindBoard(b.Host, b.DirectoryName) is not null, ThreadListItemKind.Board));
            }
            return (items, $"{provider.DisplayName} の表示済み板: {items.Count} 件");
        });
    }

    private async Task OpenBoardRowsTabAsync(Guid id, string header, Func<ThreadListTabViewModel, Task<(List<ThreadListItem> Items, string Status)>> source)
    {
        _boardRowTabSources[id] = source;
        var tab = AllThreadListTabs.FirstOrDefault(t => t.FavoritesFolderId == id);
        if (tab is null)
        {
            tab = new ThreadListTabViewModel(id, header, t => { _boardRowTabSources.Remove(id); RemoveThreadListTab(t); });
            tab.Header = header;
            ThreadListTabs.Add(tab);
        }
        ActivateThreadListTab(tab);
        await RefreshBoardRowsTabAsync(tab, header).ConfigureAwait(true);
    }

    /// <summary>板の行タブ (検索結果 / 表示済み板) なら中身を作り直して true。<see cref="RefreshThreadListTabAsync"/> から呼ばれる。</summary>
    private bool TryRefreshBoardRowsTab(ThreadListTabViewModel tab, out Task task)
    {
        if (tab.FavoritesFolderId is Guid id && _boardRowTabSources.ContainsKey(id))
        {
            var header = tab.Header;
            var paren = header.LastIndexOf(" (", StringComparison.Ordinal);
            task = RefreshBoardRowsTabAsync(tab, paren > 0 ? header[..paren] : header);
            return true;
        }
        task = Task.CompletedTask;
        return false;
    }

    private async Task RefreshBoardRowsTabAsync(ThreadListTabViewModel tab, string header)
    {
        if (tab.IsBusy || tab.FavoritesFolderId is not Guid id || !_boardRowTabSources.TryGetValue(id, out var source)) return;
        try
        {
            tab.IsBusy        = true;
            tab.Header        = $"{header} (取得中)";
            tab.StatusMessage = $"{header} を取得中...";
            var (items, status) = await source(tab).ConfigureAwait(true);
            tab.SetItems(items, DateTimeOffset.UtcNow);
            tab.Header        = $"{header} ({items.Count})";
            tab.StatusMessage = status;
        }
        catch (Exception ex)
        {
            tab.Header        = $"{header} (失敗)";
            tab.StatusMessage = $"{header}: 取得に失敗しました — {ex.Message}";
        }
        finally
        {
            tab.IsBusy = false;
        }
    }

    private static Guid DeterministicTabId(string key)
    {
        using var sha = System.Security.Cryptography.SHA1.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key));
        var guid = new byte[16];
        Array.Copy(hash, guid, 16);
        return new Guid(guid);
    }
}
