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

    /// <summary>アプリ共通の HttpClient (板検索など、スレ一覧の周辺で使う要求用。提供者の通信経路もこれで効く)。</summary>
    public System.Net.Http.HttpClient Http => _client.Http;

    public SubjectTxtClient(MonazillaClient client, DataPaths paths)
    {
        _client = client;
        _paths  = paths;
    }

    /// <summary>サーバから subject.txt を取得し、生バイトのまま保存して返す。</summary>
    public async Task<IReadOnlyList<ThreadInfo>> FetchAndSaveAsync(Board board, CancellationToken ct = default)
        => (await FetchPageAsync(board, ChBrowser.Services.Bbs.ThreadListQuery.Default, ct).ConfigureAwait(false)).Items;

    /// <summary>並び順・ページ指定付きでスレ一覧を 1 ページ取得する (<c>doc/reddit-design.md</c> §3 B5)。
    /// 1 ページ目だけ生バイトを <c>_subject.txt</c> に保存する (2 ページ目以降はセッション限り、決定 D29)。
    /// 並び順もページングも無い提供者では <see cref="FetchAndSaveAsync"/> と同じ。</summary>
    public async Task<ChBrowser.Services.Bbs.ThreadListPage> FetchPageAsync(
        Board board, ChBrowser.Services.Bbs.ThreadListQuery query, CancellationToken ct = default)
    {
        var provider = ChBrowser.Services.Bbs.BbsRegistry.ResolveOrDefault(board.Host);
        var url      = provider.ThreadListUrl(board, query);

        using var resp = await _client.Http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        if (query.IsFirstPage)
        {
            var path = _paths.SubjectTxtPath(board.Host, board.DirectoryName);
            await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
        }

        return provider.ParseThreadListPage(bytes);
    }

    /// <summary>ローカル保存済みの subject.txt があれば読み込む。</summary>
    public async Task<IReadOnlyList<ThreadInfo>> LoadFromDiskAsync(Board board, CancellationToken ct = default)
    {
        var path = _paths.SubjectTxtPath(board.Host, board.DirectoryName);
        if (!File.Exists(path)) return Array.Empty<ThreadInfo>();
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return ChBrowser.Services.Bbs.BbsRegistry.ResolveOrDefault(board.Host).ParseThreadListPage(bytes).Items;
    }
}
