using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Api;

namespace ChBrowser.Services.Bbs;

/// <summary>「番号以降」差分取得の掲示板 (したらば rawmode.cgi / まちBBS offlaw.cgi) 向けのスレ本文取得。
/// <see cref="IBbsProvider.UsesNativeDat"/> が false の提供者で <see cref="DatClient.FetchStreamingAsync"/> から使われる。
///
/// <para>流れ:</para>
/// <list type="number">
/// <item><description>ローカルの番号列付きログ (<see cref="NumberedLogFormat"/>) があれば読み、1 バッチ目として即通知。最大番号を差分境界にする。</description></item>
/// <item><description>ログ無しなら <see cref="IBbsProvider.ThreadFetchUrl"/> を、有れば <see cref="IBbsProvider.ThreadFetchRangeUrl"/> (最大番号 + 1 以降) を GET。
///   応答は提供者の文字コードで復号し、1 行ずつ <see cref="IBbsProvider.ParseThreadLine"/> でサーバの実番号付き <see cref="Post"/> に。
///   境界番号のエコー (<c>&lt;= maxNumber</c>) と番号の逆行は捨てる。</description></item>
/// <item><description>新規レスを番号列付きログとして追記 (新規ファイルなら版マーカー付きで作成)。10 件 → 50 件単位で通知。</description></item>
/// </list>
/// <para>HTTP エラーは 5ch 経路と同じく <see cref="HttpRequestException"/> (StatusCode 付き) を投げ、上位で 404 / その他を判定する。
/// 差分の 200 空応答は「新着なし」。5ch 用の read.cgi 逆変換 (<c>HtmlToDatConverter</c>) は使わない。</para></summary>
internal static class NumberedThreadFetcher
{
    private const int FirstBatchSize = 10;
    private const int LaterBatchSize = 50;

    public static async Task<DatFetchResult> FetchAsync(
        HttpClient                     http,
        IBbsProvider                   provider,
        Board                          board,
        string                         threadKey,
        string                         path,
        IProgress<IReadOnlyList<Post>> progress,
        CancellationToken              ct)
    {
        // 1. 既存ログ
        var existing  = new List<Post>();
        var hasLog    = false;
        long maxNumber = 0;
        if (File.Exists(path))
        {
            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            if (NumberedLogFormat.IsNumberedLog(bytes))
            {
                existing.AddRange(NumberedLogFormat.Parse(bytes));
                hasLog = true;
                foreach (var p in existing) if (p.Number > maxNumber) maxNumber = p.Number;
            }
            // 版マーカー無し (壊れた / 想定外のファイル) は全取得で置き換える
        }
        if (existing.Count > 0) progress.Report(existing);

        // 2. 取得
        var url = hasLog && maxNumber > 0
            ? provider.ThreadFetchRangeUrl(board, threadKey, maxNumber + 1)
            : provider.ThreadFetchUrl(board, threadKey);

        ChBrowser.Services.Logging.LogService.Instance.Write(
            $"[datFetch] GET {url} (provider={provider.Id}, existing={existing.Count} posts, max={maxNumber})");

        using var req  = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

        ChBrowser.Services.Logging.LogService.Instance.Write(
            $"[datFetch]   status={(int)resp.StatusCode} ({resp.StatusCode}) for {url}");

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var text = provider.TextEncoding.GetString(body);

        // 3. 行パース (境界エコーと逆行を捨てる)
        var fresh = new List<Post>();
        var last  = maxNumber;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var post = provider.ParseThreadLine(line);
            if (post is null || post.Number <= last) continue;
            fresh.Add(post);
            last = post.Number;
        }

        // 4. 保存 (新規はマーカー付きで作成、既存は追記)。取れたものが無いときは触らない。
        if (fresh.Count > 0)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (hasLog)
            {
                await using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
                await fs.WriteAsync(NumberedLogFormat.SerializeAppend(fresh), ct).ConfigureAwait(false);
            }
            else
            {
                await File.WriteAllBytesAsync(path, NumberedLogFormat.Serialize(provider.Id, fresh), ct).ConfigureAwait(false);
            }
        }

        // 5. 通知 (初回 10 件、以降 50 件単位)
        var limit = existing.Count > 0 ? LaterBatchSize : FirstBatchSize;
        for (var i = 0; i < fresh.Count;)
        {
            var n = Math.Min(limit, fresh.Count - i);
            progress.Report(fresh.GetRange(i, n));
            i    += n;
            limit = LaterBatchSize;
        }

        var all = new List<Post>(existing.Count + fresh.Count);
        all.AddRange(existing);
        all.AddRange(fresh);
        var size = File.Exists(path) ? new FileInfo(path).Length : 0;
        return new DatFetchResult(all, size);
    }
}
