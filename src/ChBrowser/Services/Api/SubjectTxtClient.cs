using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Storage;

namespace ChBrowser.Services.Api;

/// <summary>
/// 板のスレ一覧 (subject.txt) を取得・パースする。
/// subject.txt は Shift_JIS。フォーマットは 1 行 1 スレ:
///   <c>&lt;key&gt;.dat&lt;&gt;&lt;title&gt; (&lt;post_count&gt;)</c>
/// </summary>
public sealed class SubjectTxtClient
{
    private readonly MonazillaClient _client;
    private readonly DataPaths       _paths;

    private static readonly Regex LineRegex =
        new(@"^(?<key>\d+)\.dat<>(?<title>.+?)\s*\((?<count>\d+)\)\s*$",
            RegexOptions.Compiled);

    public SubjectTxtClient(MonazillaClient client, DataPaths paths)
    {
        _client = client;
        _paths  = paths;
    }

    /// <summary>サーバから subject.txt を取得し、SJIS バイトのまま保存して返す。</summary>
    public async Task<IReadOnlyList<ThreadInfo>> FetchAndSaveAsync(Board board, CancellationToken ct = default)
    {
        // board.Url は末尾 '/' 付き想定 (例: "https://hayabusa9.5ch.io/news/")
        var url = board.Url.TrimEnd('/') + "/subject.txt";

        using var resp = await _client.Http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        var path = _paths.SubjectTxtPath(board.Host, board.DirectoryName);
        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);

        return Parse(bytes);
    }

    /// <summary>ローカル保存済みの subject.txt があれば読み込む。</summary>
    public async Task<IReadOnlyList<ThreadInfo>> LoadFromDiskAsync(Board board, CancellationToken ct = default)
    {
        var path = _paths.SubjectTxtPath(board.Host, board.DirectoryName);
        if (!File.Exists(path)) return Array.Empty<ThreadInfo>();
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return Parse(bytes);
    }

    private static IReadOnlyList<ThreadInfo> Parse(byte[] sjisBytes)
    {
        var sjis  = Encoding.GetEncoding(932);
        var text  = sjis.GetString(sjisBytes);
        var lines = text.Split('\n');
        var list  = new List<ThreadInfo>(lines.Length);

        var order = 0;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrEmpty(line)) continue;

            var m = LineRegex.Match(line);
            if (!m.Success) continue;

            order++;
            list.Add(new ThreadInfo(
                Key:       m.Groups["key"].Value,
                // SJIS に無い文字 (絵文字等) は &#xXXXX; / &#NNN; で来るので HtmlDecode して実体に展開
                Title:     WebUtility.HtmlDecode(m.Groups["title"].Value),
                PostCount: int.Parse(m.Groups["count"].Value),
                Order:     order));
        }

        return list;
    }
}
