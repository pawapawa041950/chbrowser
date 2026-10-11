using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ChBrowser.Models;
using ChBrowser.Services.Storage;

namespace ChBrowser.Services.Bbs;

/// <summary>板が「同じ板か」と、板の保存場所の名前 (<c>data/&lt;ドメイン&gt;/&lt;保存名&gt;/</c>) を決める (全掲示板共通)。
///
/// <list type="bullet">
/// <item><description>ふつうの板は「ドメイン (<c>5ch.io</c> / <c>2chan.net</c> …) + 板名」で見分け、サーバ (<c>hayabusa9</c> 等) は見ない。
///   板がサーバを移転しても同じ板として扱われ、同じ場所に保存される (従来どおり)。</description></item>
/// <item><description>同じドメインに同じ名前の板が別々のサーバにある場合 (ふたばの二次元裏 <c>may/b</c>・<c>img/b</c>・<c>dat/b</c>) だけ、
///   サーバも含めて見分ける。元からある方 (主) は保存名を板名のまま、ほかは <c>板名@サーバ</c> (<c>b@img</c>) にする
///   (= 主の板の既存のログはそのまま使える)。</description></item>
/// <item><description>名前の重複は板一覧を読んだときに自動で見つけ (<see cref="RegisterBoardList"/>)、<c>data/app/board_duplicates.json</c> に残す
///   (一度決めた主は変えない = 保存場所が変わらない)。掲示板が前もって知っている重複 (<see cref="IBbsProvider.KnownDuplicateBoards"/>) も使う。</description></item>
/// <item><description>重複している名前の板は、移転しても別の板として扱われる (移転と別の板を見分ける手がかりが無いため)。
///   同じ板の別アドレスは掲示板が正式なサーバに寄せる (<see cref="IBbsProvider.CanonicalBoardHost"/>)。</description></item>
/// </list></summary>
public static class BoardIdentity
{
    /// <summary>保存名で、板名とサーバを分ける印 (<c>b@img</c>)。</summary>
    public const char ServerSeparator = '@';

    private static readonly object Lock = new();
    /// <summary>ドメイン → (板名 → 主のサーバ)。</summary>
    private static Dictionary<string, Dictionary<string, string>> _duplicates = new(StringComparer.OrdinalIgnoreCase);
    private static string? _path;

    /// <summary>覚えている重複を読む (起動時に 1 回)。</summary>
    public static void Initialize(string path)
    {
        lock (Lock)
        {
            _path = path;
            try
            {
                if (File.Exists(path)
                    && JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllBytes(path)) is { } loaded)
                    _duplicates = new(loaded.ToDictionary(kv => kv.Key, kv => new Dictionary<string, string>(kv.Value, StringComparer.Ordinal)),
                                      StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                ChBrowser.Services.Logging.LogService.Instance.Write($"[BoardIdentity] 板名の重複の記録を読めません: {ex.Message}");
            }
        }
    }

    /// <summary>板の保存名。ふつうは板名、名前が重複している板で主でないものは <c>板名@サーバ</c>。</summary>
    public static string StorageName(string host, string directoryName)
    {
        var root    = DataPaths.ExtractRootDomain(host);
        var primary = PrimaryServerOf(host, root, directoryName);
        if (primary is null) return directoryName;
        var server = ServerLabel(CanonicalHost(host, directoryName), root);
        return server is null || string.Equals(server, primary, StringComparison.OrdinalIgnoreCase)
            ? directoryName
            : directoryName + ServerSeparator + server;
    }

    /// <summary>2 つの板が同じ板か (ドメイン + 保存名が同じ)。</summary>
    public static bool SameBoard(string hostA, string directoryA, string hostB, string directoryB)
        => string.Equals(DataPaths.ExtractRootDomain(hostA), DataPaths.ExtractRootDomain(hostB), StringComparison.OrdinalIgnoreCase)
        && string.Equals(StorageName(hostA, directoryA), StorageName(hostB, directoryB), StringComparison.Ordinal);

    public static bool SameBoard(Board a, Board b) => SameBoard(a.Host, a.DirectoryName, b.Host, b.DirectoryName);

    /// <summary>板が、ドメイン <paramref name="rootDomain"/> の保存名 <paramref name="storageName"/> の板か (保存フォルダから板を探すとき)。</summary>
    public static bool HasStorageName(Board board, string rootDomain, string storageName)
        => string.Equals(DataPaths.ExtractRootDomain(board.Host), rootDomain, StringComparison.OrdinalIgnoreCase)
        && string.Equals(StorageName(board.Host, board.DirectoryName), storageName, StringComparison.Ordinal);

    /// <summary>保存名 → (サーバのホスト、板名)。<c>b@img</c> + <c>2chan.net</c> → (<c>img.2chan.net</c>, <c>b</c>)。
    /// サーバの付かない保存名はホスト null (= 板一覧や掲示板の既定から決める)。</summary>
    public static (string? Host, string DirectoryName) ParseStorageName(string rootDomain, string storageName)
    {
        var at = storageName.LastIndexOf(ServerSeparator);
        if (at <= 0 || at == storageName.Length - 1) return (null, storageName);
        return (storageName[(at + 1)..] + "." + rootDomain, storageName[..at]);
    }

    /// <summary>板一覧から、同じドメインで同じ名前の板が別々のサーバにあるものを見つけて覚える。
    /// 主は一覧で先に出てくる方 (主の板一覧の板が先に並ぶ)。一度覚えた主は変えない。</summary>
    public static void RegisterBoardList(IEnumerable<Board> boards)
    {
        var groups = boards
            .Select(b => (Board: b, Root: DataPaths.ExtractRootDomain(b.Host)))
            .GroupBy(x => (Root: x.Root.ToLowerInvariant(), x.Board.DirectoryName));
        var changed = false;
        lock (Lock)
        {
            foreach (var g in groups)
            {
                var servers = g.Select(x => ServerLabel(CanonicalHost(x.Board.Host, x.Board.DirectoryName), x.Root))
                               .Where(s => s is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (servers.Count < 2) continue;
                if (!_duplicates.TryGetValue(g.Key.Root, out var byDir))
                    _duplicates[g.Key.Root] = byDir = new Dictionary<string, string>(StringComparer.Ordinal);
                if (byDir.ContainsKey(g.Key.DirectoryName)) continue;
                byDir[g.Key.DirectoryName] = servers[0]!.ToLowerInvariant();
                changed = true;
                ChBrowser.Services.Logging.LogService.Instance.Write(
                    $"[BoardIdentity] 同名の板を検出: {g.Key.Root}/{g.Key.DirectoryName} (サーバ {string.Join(", ", servers)}、主 = {servers[0]})");
            }
            if (changed) Save();
        }
    }

    private static void Save()
    {
        if (_path is null) return;
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(_duplicates, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (IOException ex)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write($"[BoardIdentity] 板名の重複の記録を保存できません: {ex.Message}");
        }
    }

    /// <summary>名前が重複している板なら主のサーバ (重複していなければ null)。覚えている分 → 掲示板が前もって知っている分。</summary>
    private static string? PrimaryServerOf(string host, string root, string directoryName)
    {
        lock (Lock)
            if (_duplicates.TryGetValue(root, out var byDir) && byDir.TryGetValue(directoryName, out var p)) return p;
        return BbsRegistry.ResolveOrDefault(host).KnownDuplicateBoards.TryGetValue(directoryName, out var known) ? known : null;
    }

    private static string CanonicalHost(string host, string directoryName)
        => BbsRegistry.ResolveOrDefault(host).CanonicalBoardHost(host, directoryName);

    /// <summary>ホストのサーバ部分 (<c>img.2chan.net</c> → <c>img</c>)。ドメインそのもの (サーバ不明) なら null。</summary>
    private static string? ServerLabel(string host, string root)
    {
        if (string.IsNullOrEmpty(host) || host.Length <= root.Length + 1) return null;
        if (!host.EndsWith("." + root, StringComparison.OrdinalIgnoreCase)) return null;
        return host[..^(root.Length + 1)].ToLowerInvariant();
    }
}
