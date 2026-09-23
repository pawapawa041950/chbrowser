using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Api;

namespace ChBrowser.Services.Bbs;

/// <summary>スナップショット方式のスレの補助情報 (<c>&lt;key&gt;.meta.json</c>)。外部 ID → アプリ内番号の対応を永続化し、
/// 再取得しても同じ投稿には同じ番号が付くようにする (<c>doc/reddit-design.md</c> §2.3、決定 D23)。</summary>
public sealed class ThreadMeta
{
    public int    Version  { get; set; } = 1;
    public string Provider { get; set; } = "";
    public Dictionary<string, long> IdToNumber { get; set; } = new(StringComparer.Ordinal);
    /// <summary>最後に取得した時刻 (epoch 秒)。</summary>
    public long   LastFetchedEpoch { get; set; }
    /// <summary>直近の取得で取り切れなかった分 (reddit の more) が残ったか。</summary>
    public bool   Truncated { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented        = false,
    };

    public static ThreadMeta? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var meta = JsonSerializer.Deserialize<ThreadMeta>(File.ReadAllBytes(path), Options);
            if (meta is null) return null;
            meta.IdToNumber = new Dictionary<string, long>(meta.IdToNumber ?? new(), StringComparer.Ordinal);
            return meta;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            System.Diagnostics.Debug.WriteLine($"[ThreadMeta] load failed ({path}): {ex.Message}");
            return null;
        }
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(this, Options));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>外部 ID からアプリ内番号を引く (<see cref="Url.AddressBarTarget.PostId"/> の解決用)。無ければ null。</summary>
    public long? NumberOf(string externalId)
    {
        if (string.IsNullOrEmpty(externalId)) return null;
        if (IdToNumber.TryGetValue(externalId, out var n)) return n;
        // reddit の URL はコメント id だけ (t1_ 無し) なので、接頭辞付きでも引く。
        foreach (var prefix in new[] { "t1_", "t3_" })
            if (IdToNumber.TryGetValue(prefix + externalId, out n)) return n;
        return null;
    }
}

/// <summary>スナップショット方式 (<see cref="ThreadFetchStrategy.Snapshot"/>) のスレ本文取得。<see cref="DatClient.FetchStreamingAsync"/> から使われる
/// (<c>doc/reddit-design.md</c> §3 B3)。
///
/// <para>流れ:</para>
/// <list type="number">
/// <item><description>ローカルの番号列付きログと <c>meta.json</c> を読み、既存分を 1 バッチ目として即通知する。</description></item>
/// <item><description>提供者の <see cref="ISnapshotThreadProvider.FetchSnapshotAsync"/> でスレ全体を取る。</description></item>
/// <item><description>未知の外部 ID だけを取り出し、スレ本体を先頭に、あとは投稿時刻 (同時刻は ID) の昇順で最大番号の次から採番する
///   (親は子より先に投稿されるので「親の番号 &lt; 子の番号」になり、JS の「後方参照だけを親候補にする」規則と合う)。</description></item>
/// <item><description>親の外部 ID を番号に直して <see cref="PostExtra.ParentNumber"/> に入れる (本文は書き換えない。決定 D24)。
///   親がスレ本体ならトップレベル (= null)。</description></item>
/// <item><description>新規分をログへ追記し、meta を保存し、10 件 → 50 件単位で通知する。既存レスの本文・評価値は更新しない (決定 D26)。</description></item>
/// <item><description>提供者が <see cref="SnapshotFetchOptions.OnPartial"/> で要求 1 回ごとに塊を渡す場合は、3〜5 を塊ごとに行う
///   (大きいスレでも最初の要求の分から表示される)。</description></item>
/// </list></summary>
internal static class SnapshotThreadFetcher
{
    private const int FirstBatchSize = 10;
    private const int LaterBatchSize = 50;

    public static async Task<DatFetchResult> FetchAsync(
        HttpClient                     http,
        IBbsProvider                   provider,
        Board                          board,
        string                         threadKey,
        string                         logPath,
        string                         metaPath,
        IProgress<IReadOnlyList<Post>> progress,
        CancellationToken              ct,
        int                            maxExpansions = 10)
    {
        if (provider is not ISnapshotThreadProvider snapshotProvider)
            throw new InvalidOperationException($"提供者 {provider.Id} は Snapshot 取得 (ISnapshotThreadProvider) を実装していません。");

        // 1. 既存ログと meta
        var existing  = new List<Post>();
        var hasLog    = false;
        if (File.Exists(logPath))
        {
            var bytes = await File.ReadAllBytesAsync(logPath, ct).ConfigureAwait(false);
            if (NumberedLogFormat.IsNumberedLog(bytes))
            {
                existing.AddRange(NumberedLogFormat.Parse(bytes));
                hasLog = true;
            }
        }
        var meta = (hasLog ? ThreadMeta.Load(metaPath) : null) ?? new ThreadMeta { Provider = provider.Id };
        // meta が無い / 壊れている場合はログの拡張情報から対応表を復元する
        foreach (var p in existing)
            if (p.Ext?.ExternalId is { Length: > 0 } eid && !meta.IdToNumber.ContainsKey(eid))
                meta.IdToNumber[eid] = p.Number;
        var maxNumber = existing.Count > 0 ? existing.Max(p => p.Number) : 0;
        if (existing.Count > 0) progress.Report(existing);

        // 2. 取得
        ChBrowser.Services.Logging.LogService.Instance.Write(
            $"[snapshotFetch] provider={provider.Id} {board.DirectoryName}/{threadKey} (existing={existing.Count}, known ids={meta.IdToNumber.Count})");
        // 返信を持つ既存レスの外部 ID (番号 → 外部 ID は既存ログの拡張情報から引く)
        var idByNumber = new Dictionary<long, string>();
        foreach (var p in existing) if (p.Ext?.ExternalId is { Length: > 0 } x) idByNumber[p.Number] = x;
        var parentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in existing)
            if (p.Ext?.ParentNumber is long pn && idByNumber.TryGetValue(pn, out var pid)) parentIds.Add(pid);

        // 3〜5 は提供者から届いた塊 (要求 1 回分) ごとに行う: 採番 → ログへ追記 → meta 保存 → 通知。
        // 塊ごとに採番するので、後の塊の投稿は前の塊より大きい番号になる (投稿時刻順は塊の中でだけ保たれる。
        // 親は必ず同じ塊か前の塊にいるので「親の番号 < 子の番号」は崩れない)。
        var fresh        = new List<Post>();
        var nameByNumber = new Dictionary<long, string>();
        foreach (var p in existing) nameByNumber[p.Number] = p.Name;
        string? rootId   = null;
        var limit        = existing.Count > 0 ? LaterBatchSize : FirstBatchSize;

        async Task ConsumeAsync(IReadOnlyList<SnapshotPost> chunk)
        {
            if (chunk.Count == 0) return;
            rootId ??= chunk[0].ExternalId;
            var added = AssignNumbers(chunk, rootId, meta, nameByNumber, maxNumber);
            if (added.Count == 0) return;
            maxNumber = added.Max(p => p.Number);
            fresh.AddRange(added);

            // 保存 (新規はマーカー付きで作成、既存は追記)
            var dir = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (hasLog)
            {
                await using var fs = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.None);
                await fs.WriteAsync(NumberedLogFormat.SerializeAppend(added), ct).ConfigureAwait(false);
            }
            else
            {
                await File.WriteAllBytesAsync(logPath, NumberedLogFormat.Serialize(provider.Id, added), ct).ConfigureAwait(false);
                hasLog = true;
            }
            meta.Save(metaPath);

            // 通知 (最初は 10 件で早く描き始め、以降は 50 件単位)
            for (var i = 0; i < added.Count;)
            {
                var n = Math.Min(limit, added.Count - i);
                progress.Report(added.GetRange(i, n));
                i    += n;
                limit = LaterBatchSize;
            }
        }

        var snapshot = await snapshotProvider.FetchSnapshotAsync(
            http, board, threadKey,
            new SnapshotFetchOptions(new HashSet<string>(meta.IdToNumber.Keys, StringComparer.Ordinal), maxExpansions, parentIds,
                                     OnPartial: ConsumeAsync),
            ct).ConfigureAwait(false);

        // 塊で渡されなかった残り (OnPartial を使わない提供者ならここで全部) を処理する。採番済みの ID は飛ばされる。
        await ConsumeAsync(snapshot.Posts).ConfigureAwait(false);

        meta.LastFetchedEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        meta.Truncated        = snapshot.Truncated;
        if (File.Exists(logPath)) meta.Save(metaPath);
        ChBrowser.Services.Logging.LogService.Instance.Write(
            $"[snapshotFetch]   snapshot={snapshot.Posts.Count} posts, new={fresh.Count}, truncated={snapshot.Truncated}");

        var all = new List<Post>(existing.Count + fresh.Count);
        all.AddRange(existing);
        all.AddRange(fresh);
        var size = File.Exists(logPath) ? new FileInfo(logPath).Length : 0;
        return new DatFetchResult(all, size);
    }

    /// <summary>スナップショットのうち未知の投稿に番号を振って <see cref="Post"/> にする (<paramref name="meta"/> の対応表も更新する)。
    /// 純粋な計算 (IO 無し) なのでハーネスから直接検証できる。</summary>
    internal static List<Post> AssignNumbers(ThreadSnapshot snapshot, ThreadMeta meta, IReadOnlyList<Post> existing, long maxNumber)
    {
        if (snapshot.Posts.Count == 0) return new List<Post>();
        var nameByNumber = new Dictionary<long, string>();
        foreach (var p in existing) nameByNumber[p.Number] = p.Name;
        return AssignNumbers(snapshot.Posts, snapshot.Posts[0].ExternalId, meta, nameByNumber, maxNumber);
    }

    /// <summary>投稿の塊 1 つを採番する。<paramref name="rootId"/> はスレ本体の外部 ID (塊に含まれていれば先頭にし、親がこれならトップレベル)。
    /// <paramref name="nameByNumber"/> (番号 → 投稿者名、返信先表示用) は今回の分を足して更新する。</summary>
    internal static List<Post> AssignNumbers(
        IReadOnlyList<SnapshotPost> posts, string rootId, ThreadMeta meta, Dictionary<long, string> nameByNumber, long maxNumber)
    {
        var fresh = new List<Post>();
        if (posts.Count == 0) return fresh;

        var unknown = posts
            .Where(p => !string.IsNullOrEmpty(p.ExternalId) && !meta.IdToNumber.ContainsKey(p.ExternalId))
            .GroupBy(p => p.ExternalId, StringComparer.Ordinal).Select(g => g.First())      // 同じ ID の重複は最初だけ
            .OrderBy(p => string.Equals(p.ExternalId, rootId, StringComparison.Ordinal) ? 0 : 1)   // スレ本体を先頭に
            .ThenBy(p => p.CreatedEpoch)
            .ThenBy(p => p.ExternalId, StringComparer.Ordinal)
            .ToList();

        // 先に全部の番号を決めてから親を解決する (= 同じ回に来た親子の順序に依存しない)
        var next = maxNumber;
        foreach (var sp in unknown) meta.IdToNumber[sp.ExternalId] = ++next;

        // 親の投稿者名: 既存分 (呼び出し側が渡す) + この塊
        foreach (var sp in posts)
            if (meta.IdToNumber.TryGetValue(sp.ExternalId, out var n)) nameByNumber[n] = sp.Name;

        foreach (var sp in unknown)
        {
            var number = meta.IdToNumber[sp.ExternalId];
            long? parent = null;
            if (!string.IsNullOrEmpty(sp.ParentExternalId) &&
                !string.Equals(sp.ParentExternalId, rootId, StringComparison.Ordinal) &&
                meta.IdToNumber.TryGetValue(sp.ParentExternalId, out var pn) && pn != number)
            {
                parent = pn;
            }
            var ext = (sp.Ext ?? new PostExtra()) with
            {
                ExternalId   = sp.ExternalId,
                ParentNumber = parent,
                ParentName   = parent is long pnum && nameByNumber.TryGetValue(pnum, out var pname) ? pname : null,
                CreatedEpoch = sp.Ext?.CreatedEpoch ?? (sp.CreatedEpoch > 0 ? sp.CreatedEpoch : null),
            };
            fresh.Add(new Post(
                Number:      number,
                Name:        sp.Name,
                Mail:        sp.Mail,
                DateText:    sp.DateText,
                Id:          sp.Id,
                Body:        sp.Body,
                ThreadTitle: number == 1 ? sp.ThreadTitle : null,
                Ext:         ext));
        }
        return fresh;
    }
}
