using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Storage;

namespace ChBrowser.Services.Api;

/// <summary>
/// 板のスレ一覧 (subject.txt 相当) を取得・保存・パースする。
/// 行形式と文字コードは掲示板ごとに違う (5ch: SJIS <c>key.dat&lt;&gt;title (n)</c>、したらば/まちBBS: <c>key.cgi,title(n)</c>) ので、
/// パースは提供者 (<see cref="ChBrowser.Services.Bbs.IBbsProvider.ParseThreadList"/>) に任せ、ここは生バイトの取得と保存だけを行う。
/// </summary>
public sealed class SubjectTxtClient
{
    private readonly MonazillaClient _client;
    private readonly DataPaths       _paths;

    public SubjectTxtClient(MonazillaClient client, DataPaths paths)
    {
        _client = client;
        _paths  = paths;
    }

    /// <summary>サーバから subject.txt を取得し、生バイトのまま保存して返す。</summary>
    public async Task<IReadOnlyList<ThreadInfo>> FetchAndSaveAsync(Board board, CancellationToken ct = default)
    {
        var provider = ChBrowser.Services.Bbs.BbsRegistry.ResolveOrDefault(board.Host);
        var url      = provider.ThreadListUrl(board);

        using var resp = await _client.Http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        var path = _paths.SubjectTxtPath(board.Host, board.DirectoryName);
        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);

        return provider.ParseThreadList(bytes);
    }

    /// <summary>ローカル保存済みの subject.txt があれば読み込む。</summary>
    public async Task<IReadOnlyList<ThreadInfo>> LoadFromDiskAsync(Board board, CancellationToken ct = default)
    {
        var path = _paths.SubjectTxtPath(board.Host, board.DirectoryName);
        if (!File.Exists(path)) return Array.Empty<ThreadInfo>();
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return ChBrowser.Services.Bbs.BbsRegistry.ResolveOrDefault(board.Host).ParseThreadList(bytes);
    }
}
