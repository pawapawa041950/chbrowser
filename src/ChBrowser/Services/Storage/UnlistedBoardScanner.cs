using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ChBrowser.Models;
using ChBrowser.Services.Api;
using ChBrowser.Services.Bbs;

namespace ChBrowser.Services.Storage;

/// <summary>「板一覧に無いが取得済みデータをローカルに持つ板」1 件。
/// <see cref="Provider"/> は保存ルートから解決した提供者 (板列の「したらば jbbs.shitaraba.net」表示用)、
/// <see cref="DatCount"/> はその板ディレクトリ直下の <c>*.dat</c> 件数。</summary>
public sealed record UnlistedBoard(Board Board, IBbsProvider Provider, int DatCount);

/// <summary>
/// <c>data/&lt;root&gt;/</c> を走査して「取得済みだが板一覧 (bbsmenu) に載っていない板」を列挙する
/// (お気に入りペイン「機能 → 板一覧以外の取得済み板」の材料)。典型例はアドレスバーから開いたしたらば板。
///
/// <para>走査規則は全ログ (<c>MainViewModel.BuildAllLogsItems</c>) と同じ: 1 階層目のディレクトリに
/// <c>*.dat</c> / <c>_subject.txt</c> が無ければ 1 段だけ降りて <c>"&lt;parent&gt;/&lt;child&gt;"</c> を板 dir とみなす
/// (したらばの <c>internet/12249</c> 形式)。ディレクトリが「取得済み板」と数えられる条件は
/// <c>_subject.txt</c> があるか <c>*.dat</c> が 1 つ以上あること。</para>
///
/// <para>板一覧に載っているかどうかの判定は呼び出し側から <c>Func&lt;root, dir, Board?&gt;</c> で受け取る
/// (= ViewModel の <c>FindBoardByDirectory</c> をそのまま渡せる。テストでは任意の判定を差し込める)。</para>
/// </summary>
public sealed class UnlistedBoardScanner
{
    private readonly DataPaths        _paths;
    private readonly SettingTxtClient _settingClient;

    public UnlistedBoardScanner(DataPaths paths, SettingTxtClient settingClient)
    {
        _paths         = paths;
        _settingClient = settingClient;
    }

    /// <summary>板一覧に無い板の <see cref="Board"/> を保存ルート + dir 名から組み立てる (全ログの fallback と共用)。
    /// 提供者は保存ルートから解決し (<see cref="BbsRegistry.ResolveByStorageRoot"/>)、
    /// URL のホストは <see cref="IBbsProvider.DefaultHost"/> を使う。ただし保存ルート自体がその提供者の
    /// 有効なホストなら (5ch.io / bbspink.com / machi.to) ルートをそのまま使い、<see cref="DataPaths.BoardDir"/> が
    /// 同じ保存場所へ戻れるようにする (= bbspink.com の板が 5ch.io 側のディレクトリを見に行かないように)。
    /// 5ch.io の板は従来通り host = "5ch.io"、したらばは "jbbs.shitaraba.net" になる。</summary>
    public static Board BuildFallbackBoard(string rootDomain, string directoryName, string boardName)
    {
        var provider = BbsRegistry.ResolveByStorageRoot(rootDomain);
        var host     = provider.OwnsHost(rootDomain) ? rootDomain : provider.DefaultHost;
        return new Board(directoryName, boardName, provider.BoardUrl(host, directoryName), "", 0);
    }

    /// <summary>全保存ルートを走査し、<paramref name="resolveListed"/> が null を返した (= 板一覧に無い) 板を返す。
    /// 板名は <c>_SETTING.TXT</c> の <c>BBS_TITLE</c>、無ければ dir 名。ネットワークは使わない。</summary>
    public List<UnlistedBoard> Scan(Func<string, string, Board?> resolveListed)
    {
        var result = new List<UnlistedBoard>();
        foreach (var root in BbsRegistry.StorageRoots)
        {
            var rootDir = Path.Combine(_paths.Root, root);
            if (!Directory.Exists(rootDir)) continue;

            var provider = BbsRegistry.ResolveByStorageRoot(root);
            foreach (var (dirName, datCount) in EnumerateFetchedBoardDirs(rootDir))
            {
                if (resolveListed(root, dirName) is not null) continue;

                var board = BuildFallbackBoard(root, dirName, dirName);
                var title = ReadBoardTitle(board);
                if (!string.IsNullOrWhiteSpace(title)) board = board with { BoardName = title.Trim() };
                result.Add(new UnlistedBoard(board, provider, datCount));
            }
        }
        return result;
    }

    /// <summary><paramref name="rootDir"/> (= <c>data/&lt;root&gt;</c>) 直下から「取得済み板」ディレクトリを列挙する。
    /// 戻り値の dir 名は 1 階層なら <c>"news"</c>、2 階層なら <c>"internet/12249"</c>。</summary>
    public static List<(string DirName, int DatCount)> EnumerateFetchedBoardDirs(string rootDir)
    {
        var list = new List<(string, int)>();
        foreach (var dirPath in Directory.EnumerateDirectories(rootDir))
        {
            var dirName = Path.GetFileName(dirPath);
            if (TryCountFetched(dirPath, out var datCount))
            {
                list.Add((dirName, datCount));
                continue;
            }
            // 2 階層 dir: 直下に dat / subject が無ければ子ディレクトリを板として見る
            foreach (var subPath in Directory.EnumerateDirectories(dirPath))
            {
                if (!TryCountFetched(subPath, out var subCount)) continue;
                list.Add((dirName + "/" + Path.GetFileName(subPath), subCount));
            }
        }
        return list;
    }

    /// <summary><paramref name="dirPath"/> が取得済み板 (= <c>_subject.txt</c> or <c>*.dat</c> あり) なら true。</summary>
    private static bool TryCountFetched(string dirPath, out int datCount)
    {
        datCount = Directory.EnumerateFiles(dirPath, "*.dat").Count();
        return datCount > 0 || File.Exists(Path.Combine(dirPath, "_subject.txt"));
    }

    /// <summary>ローカル <c>_SETTING.TXT</c> の <c>BBS_TITLE</c> を同期読みする (Async API しかないので blocking で読む;
    /// <c>LoadSubjectFromDiskSync</c> と同じ流儀)。無い / 壊れていれば null。</summary>
    private string? ReadBoardTitle(Board board)
    {
        try
        {
            var settings = _settingClient.LoadFromDiskAsync(board).GetAwaiter().GetResult();
            return settings is not null && settings.TryGetValue("BBS_TITLE", out var title) ? title : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UnlistedBoards] SETTING.TXT load failed for {board.Host}/{board.DirectoryName}: {ex.Message}");
            return null;
        }
    }
}
