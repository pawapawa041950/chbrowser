using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Storage;

namespace ChBrowser.Services.Api;

/// <summary>
/// 板の SETTING.TXT を取得・パースする。
/// SETTING.TXT は SJIS の plain text で、 1 行 1 つの <c>KEY=VALUE</c>。
/// 例: <c>BBS_LINE_NUMBER=32</c> (= 1 投稿の最大行数)。
///
/// fetch ポリシは「明示要求時のみ取得」: 書き込みダイアログ起動経路では <see cref="GetOrFetchAsync"/>
/// を使い、ローカル未保存のときだけネットワークに行く (= 既に持っていれば古くてもそのまま使う)。
/// ユーザの「SETTING.TXTの更新」操作からは <see cref="FetchAndSaveAsync"/> を直接呼ぶ。
/// </summary>
public sealed class SettingTxtClient
{
    private readonly MonazillaClient _client;
    private readonly DataPaths       _paths;

    public SettingTxtClient(MonazillaClient client, DataPaths paths)
    {
        _client = client;
        _paths  = paths;
    }

    /// <summary>サーバから SETTING.TXT を取得し、SJIS バイトのまま保存する。
    /// 取得失敗時はファイルを上書きせず例外を呼び元に伝える。</summary>
    public async Task<IReadOnlyDictionary<string, string>> FetchAndSaveAsync(Board board, CancellationToken ct = default)
    {
        // board.Url は末尾 '/' 付き想定 (例: "https://hayabusa9.5ch.io/news/")
        var url = board.Url.TrimEnd('/') + "/SETTING.TXT";

        using var resp = await _client.Http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        var path = _paths.SettingTxtPath(board.Host, board.DirectoryName);
        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);

        return Parse(bytes);
    }

    /// <summary>ローカル保存済みの SETTING.TXT があれば読み込んでパースする。なければ null。</summary>
    public async Task<IReadOnlyDictionary<string, string>?> LoadFromDiskAsync(Board board, CancellationToken ct = default)
    {
        var path = _paths.SettingTxtPath(board.Host, board.DirectoryName);
        if (!File.Exists(path)) return null;
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return Parse(bytes);
    }

    /// <summary>ローカルに無ければ取得して保存、有ればそのまま読む。
    /// 取得時にネットワーク失敗しても呼び出し元が「不明」として扱える: 失敗時は null。</summary>
    public async Task<IReadOnlyDictionary<string, string>?> GetOrFetchAsync(Board board, CancellationToken ct = default)
    {
        var cached = await LoadFromDiskAsync(board, ct).ConfigureAwait(false);
        if (cached is not null) return cached;
        try
        {
            return await FetchAndSaveAsync(board, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingTxtClient] fetch failed for {board.Host}/{board.DirectoryName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>SETTING.TXT の <c>BBS_LINE_NUMBER</c> を読み、実際の上限値 (= 記載値 × 2) を返す。
    /// 5ch サーバは BBS_LINE_NUMBER に対して 2 倍までを実投稿の上限としているため、
    /// 表示・判定はこの「× 2 後」の値で行う (= 記載が 32 なら実際の上限は 64 行)。
    /// キーが無い・数値でない場合は null。</summary>
    public static int? GetLineLimit(IReadOnlyDictionary<string, string>? settings)
    {
        if (settings is null) return null;
        if (!settings.TryGetValue("BBS_LINE_NUMBER", out var v)) return null;
        return int.TryParse(v.Trim(), out var n) ? n * 2 : null;
    }

    private static IReadOnlyDictionary<string, string> Parse(byte[] sjisBytes)
    {
        var sjis  = Encoding.GetEncoding(932);
        var text  = sjis.GetString(sjisBytes);
        var lines = text.Split('\n');
        // 同一キーが複数行に出てきた場合は後勝ち (= 5ch SETTING.TXT には実例がほぼ無いが念のため)。
        // 大文字小文字を区別する: BBS_LINE_NUMBER のように規約上 UPPER_SNAKE_CASE で固定されているため。
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrEmpty(line)) continue;
            if (line[0] == '#') continue; // コメント (規約に明文化は無いが慣習的に '#' で始まる行はコメントとして無視)

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;        // '=' が無い行、または行頭が '=' (key 無し) はスキップ
            var key = line.Substring(0, eq);
            var val = line.Substring(eq + 1);
            dict[key] = val;
        }
        return dict;
    }
}
