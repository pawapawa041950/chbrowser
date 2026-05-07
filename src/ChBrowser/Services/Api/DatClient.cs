using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Storage;

namespace ChBrowser.Services.Api;

/// <summary>dat 取得結果 (パース済みレス + dat 全体のバイト長)。</summary>
public sealed record DatFetchResult(IReadOnlyList<Post> Posts, long DatSize);

/// <summary>
/// dat の取得・保存・パース。
/// 既存 dat があれば <c>Range: bytes=N-</c> で末尾差分のみ取得する。
/// streaming 版 (<see cref="FetchStreamingAsync"/>) は 50 件単位で <see cref="IProgress{T}"/> に通知しながら逐次パースする。
/// </summary>
public sealed class DatClient
{
    private const int StreamFirstBatchSize = 10;   // 初回はとにかく速く表示開始
    private const int StreamLaterBatchSize = 50;   // 2 回目以降は IPC オーバーヘッド削減のためまとめる
    private const int StreamReadBufferSize = 8192;

    private readonly MonazillaClient _client;
    private readonly DataPaths       _paths;

    public DatClient(MonazillaClient client, DataPaths paths)
    {
        _client = client;
        _paths  = paths;
    }

    /// <summary>サーバから dat を取得し、SJIS バイトのまま追記/置換保存して返す (一括版)。</summary>
    public Task<DatFetchResult> FetchAsync(Board board, string threadKey, CancellationToken ct = default)
        => FetchStreamingAsync(board, threadKey, new Progress<IReadOnlyList<Post>>(_ => { }), ct);

    /// <summary>
    /// streaming 版: HTTP レスポンスをチャンク読みしながらパース、<paramref name="progress"/> に
    /// バッチ単位で逐次通知する。差分 (Range) ロジックは維持。
    /// </summary>
    public async Task<DatFetchResult> FetchStreamingAsync(
        Board                              board,
        string                             threadKey,
        IProgress<IReadOnlyList<Post>>     progress,
        CancellationToken                  ct = default)
    {
        var url  = $"{board.Url.TrimEnd('/')}/dat/{threadKey}.dat";
        var path = _paths.DatPath(board.Host, board.DirectoryName, threadKey);

        long existing = File.Exists(path) ? new FileInfo(path).Length : 0;

        ChBrowser.Services.Logging.LogService.Instance.Write(
            $"[datFetch] GET {url} (board.Host='{board.Host}', existing={existing} bytes)");

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
            req.Headers.Range = new RangeHeaderValue(existing, null);

        using var resp = await _client.Http
            .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        ChBrowser.Services.Logging.LogService.Instance.Write(
            $"[datFetch]   status={(int)resp.StatusCode} ({resp.StatusCode}) for {url}");

        var allPosts = new List<Post>();
        long finalSize;

        switch ((int)resp.StatusCode)
        {
            case 206: // Partial Content - 既存 + 末尾差分
                {
                    // 既存分はディスクから一括読み出し → 1 バッチで先に通知
                    var existingBytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                    var existingPosts = DatParser.Parse(existingBytes);
                    allPosts.AddRange(existingPosts);
                    if (existingPosts.Count > 0) progress.Report(existingPosts);

                    // 新規分を append しつつ streaming パース
                    await using var fs     = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
                    await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await StreamPostsAsync(stream, fs, allPosts, existingPosts.Count + 1, progress, ct).ConfigureAwait(false);
                    finalSize = new FileInfo(path).Length;
                    break;
                }
            case 200: // OK - 全置換 (サーバが Range を無視 or 初回取得)
                {
                    await using var fs     = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                    await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await StreamPostsAsync(stream, fs, allPosts, 1, progress, ct).ConfigureAwait(false);
                    finalSize = new FileInfo(path).Length;
                    break;
                }
            case 304: // Not Modified - 既存をパースして 1 バッチで通知
                {
                    var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                    var posts = DatParser.Parse(bytes);
                    allPosts.AddRange(posts);
                    if (posts.Count > 0) progress.Report(posts);
                    finalSize = bytes.LongLength;
                    break;
                }
            case 416: // Range Not Satisfiable - dat が縮んだ → 全取得し直し
                {
                    using var req2  = new HttpRequestMessage(HttpMethod.Get, url);
                    using var resp2 = await _client.Http
                        .SendAsync(req2, HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(false);
                    resp2.EnsureSuccessStatusCode();
                    await using var fs     = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                    await using var stream = await resp2.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await StreamPostsAsync(stream, fs, allPosts, 1, progress, ct).ConfigureAwait(false);
                    finalSize = new FileInfo(path).Length;
                    break;
                }
            case 404: // dat 落ち (アーカイブ送り)。read.cgi の HTML から逆変換を試みる。
                {
                    var fallbackBytes = await TryHtmlFallbackAsync(board, threadKey, ct).ConfigureAwait(false);
                    if (fallbackBytes is null || fallbackBytes.Length == 0)
                    {
                        // 変換失敗時は通常の 404 例外を投げて、上位 (OpenThreadAsync) のブラウザフォールバックに任せる。
                        resp.EnsureSuccessStatusCode();
                        finalSize = existing;
                        break;
                    }
                    // 変換成功: 通常の dat と同じパスにキャッシュ書き出し → 以降は普通に取れる。
                    await using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                        await fs.WriteAsync(fallbackBytes, ct).ConfigureAwait(false);
                    var posts = DatParser.Parse(fallbackBytes);
                    allPosts.AddRange(posts);
                    if (posts.Count > 0) progress.Report(posts);
                    finalSize = fallbackBytes.LongLength;
                    break;
                }
            default:
                resp.EnsureSuccessStatusCode();
                finalSize = existing;
                break;
        }

        return new DatFetchResult(allPosts, finalSize);
    }

    /// <summary>dat 404 時のフォールバック: <c>read.cgi</c> から HTML を取得して
    /// <see cref="HtmlToDatConverter"/> で dat 形式バイト列に変換する。
    /// HTML 取得自体が失敗 / パース不能の場合は null を返し、呼出元は通常の 404 として扱う。</summary>
    private async Task<byte[]?> TryHtmlFallbackAsync(Board board, string threadKey, CancellationToken ct)
    {
        var htmlUrl = $"https://{board.Host}/test/read.cgi/{board.DirectoryName}/{threadKey}/";
        ChBrowser.Services.Logging.LogService.Instance.Write(
            $"[datFetch]   404 fallback: GET {htmlUrl}");
        try
        {
            using var hreq  = new HttpRequestMessage(HttpMethod.Get, htmlUrl);
            using var hresp = await _client.Http.SendAsync(hreq, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[datFetch]     html status={(int)hresp.StatusCode}");
            if (!hresp.IsSuccessStatusCode) return null;
            var bytes = await hresp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var converted = HtmlToDatConverter.Convert(bytes);
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[datFetch]     html→dat: input={bytes.Length} bytes, output={converted?.Length ?? 0} bytes");
            return converted;
        }
        catch (Exception ex)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write(
                $"[datFetch]     html fallback failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>板ディレクトリにある *.dat のスレッドキー一覧を返す (ログが存在するスレ)。</summary>
    public IReadOnlySet<string> EnumerateExistingThreadKeys(Board board)
    {
        var dir = _paths.BoardDir(board.Host, board.DirectoryName);
        var set = new HashSet<string>();
        if (!Directory.Exists(dir)) return set;
        foreach (var path in Directory.EnumerateFiles(dir, "*.dat"))
        {
            var key = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrEmpty(key)) set.Add(key);
        }
        return set;
    }

    /// <summary>
    /// dat とインデックスファイル (.idx.json があれば) を削除。成功で true。
    /// 既に存在しなければ false (no-op)。
    /// </summary>
    public bool DeleteLog(Board board, string threadKey)
    {
        var datPath = _paths.DatPath(board.Host, board.DirectoryName, threadKey);
        var idxPath = _paths.IdxJsonPath(board.Host, board.DirectoryName, threadKey);
        var any = false;
        if (File.Exists(datPath)) { File.Delete(datPath); any = true; }
        if (File.Exists(idxPath)) { File.Delete(idxPath); }
        return any;
    }

    /// <summary>ローカル保存済みの dat があればパースして返す。無ければ空。</summary>
    public async Task<DatFetchResult?> LoadFromDiskAsync(Board board, string threadKey, CancellationToken ct = default)
    {
        var path = _paths.DatPath(board.Host, board.DirectoryName, threadKey);
        if (!File.Exists(path)) return null;

        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return new DatFetchResult(DatParser.Parse(bytes), bytes.LongLength);
    }

    /// <summary>dat の先頭 1 行だけ読んでスレタイトルを取り出す。
    /// 全 parse する <see cref="LoadFromDiskAsync"/> より大幅に速いので、dat 落ち候補を一覧に出すような
    /// 大量ループの中で使うことを想定。読み込めない / 1 行目が壊れている等は null。</summary>
    public async Task<string?> ReadThreadTitleFromDiskAsync(Board board, string threadKey, CancellationToken ct = default)
    {
        var path = _paths.DatPath(board.Host, board.DirectoryName, threadKey);
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            using var reader       = new StreamReader(stream, Encoding.GetEncoding(932));
            var firstLine          = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (firstLine is null) return null;
            var post = DatParser.ParseLine(firstLine, 1);
            return post?.ThreadTitle;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DatClient] read title failed for {threadKey}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// HTTP ストリームから SJIS バイトを読み、'\n' で行分割して 1 行 1 Post をパース。
    /// 同時にディスク (<paramref name="diskStream"/>) にも append 書き込み。
    /// 50 件単位で <paramref name="progress"/> に通知。
    /// </summary>
    private static async Task StreamPostsAsync(
        Stream                             httpStream,
        Stream                             diskStream,
        List<Post>                         accumulator,
        int                                startNumber,
        IProgress<IReadOnlyList<Post>>     progress,
        CancellationToken                  ct)
    {
        var sjis    = Encoding.GetEncoding(932);
        var readBuf = new byte[StreamReadBufferSize];
        var lineBuf = new MemoryStream(256);
        var batch   = new List<Post>(StreamLaterBatchSize);
        var nextNum = startNumber;
        var nextLimit = StreamFirstBatchSize; // 1 回目のみ小さい閾値で早期表示

        int read;
        while ((read = await httpStream.ReadAsync(readBuf.AsMemory(0, readBuf.Length), ct).ConfigureAwait(false)) > 0)
        {
            await diskStream.WriteAsync(readBuf.AsMemory(0, read), ct).ConfigureAwait(false);

            for (var i = 0; i < read; i++)
            {
                var b = readBuf[i];
                if (b == 0x0A /* '\n' */)
                {
                    var lineLen = (int)lineBuf.Length;
                    if (lineLen > 0)
                    {
                        var lineStr = sjis.GetString(lineBuf.GetBuffer(), 0, lineLen);
                        var post    = DatParser.ParseLine(lineStr, nextNum);
                        if (post is not null)
                        {
                            accumulator.Add(post);
                            batch.Add(post);
                            nextNum++;
                            if (batch.Count >= nextLimit)
                            {
                                progress.Report(batch.ToArray());
                                batch.Clear();
                                nextLimit = StreamLaterBatchSize;
                            }
                        }
                    }
                    lineBuf.SetLength(0);
                }
                else
                {
                    lineBuf.WriteByte(b);
                }
            }
        }

        // 末尾改行なしの行も処理 (5ch dat では通常起きないが防御)
        if (lineBuf.Length > 0)
        {
            var lineStr = sjis.GetString(lineBuf.GetBuffer(), 0, (int)lineBuf.Length);
            var post    = DatParser.ParseLine(lineStr, nextNum);
            if (post is not null)
            {
                accumulator.Add(post);
                batch.Add(post);
            }
        }
        if (batch.Count > 0) progress.Report(batch.ToArray());
    }
}
