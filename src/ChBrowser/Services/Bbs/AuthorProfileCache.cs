using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace ChBrowser.Services.Bbs;

/// <summary>投稿者情報 (<see cref="AuthorProfile"/>) の保存 (<c>data/&lt;掲示板&gt;/authors.json</c>、投稿者名 → 情報)。
/// アイコンやカルマは頻繁には変わらないので <see cref="Lifetime"/> の間は取り直さない (= スレを開き直しても要求を使わない)。
/// 見つからなかった投稿者 (退会・凍結) も「無い」ことを <see cref="MissingLifetime"/> の間覚えておく。
/// ファイルは数万人分 (数 MB) になり、読み書きに遅い PC で数百 ms かかるので、UI スレッドでは行わない
/// (読み込みは <see cref="PreloadAsync"/>、保存は <see cref="Put"/> が裏で行う)。どのスレッドから使ってもよい (ロックで守る)。</summary>
public sealed class AuthorProfileCache
{
    public static readonly TimeSpan Lifetime        = TimeSpan.FromDays(7);
    public static readonly TimeSpan MissingLifetime = TimeSpan.FromDays(1);

    private sealed class Entry
    {
        public AuthorProfile? Profile { get; set; }
        public long           FetchedEpoch { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented        = false,
        Encoder              = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Func<IBbsProvider, string> _pathOf;
    private readonly Dictionary<string, Dictionary<string, Entry>> _byProvider = new(StringComparer.Ordinal);
    /// <summary><see cref="_byProvider"/> (と各表) を守る。</summary>
    private readonly object _lock = new();
    /// <summary>ファイルへの書き込みを 1 つずつにする。</summary>
    private readonly object _saveLock = new();
    /// <summary>保存の順番 (<see cref="Put"/> ごとに増える)。裏の保存が前後しても古い内容で上書きしないために使う。</summary>
    private long _saveVersion, _savedVersion;

    /// <param name="pathOf">提供者 → 保存ファイルのパス。</param>
    public AuthorProfileCache(Func<IBbsProvider, string> pathOf) => _pathOf = pathOf;

    /// <summary>提供者の保存済みの情報をファイルから読んでおく (裏で読む。2 回目以降は何もしない)。
    /// UI スレッドから <see cref="Get"/> / <see cref="Stale"/> を呼ぶ前に待っておくと、そこでファイルを読まずに済む。</summary>
    public Task PreloadAsync(IBbsProvider provider)
    {
        lock (_lock) if (_byProvider.ContainsKey(provider.Id)) return Task.CompletedTask;
        return Task.Run(() => { lock (_lock) Table(provider); });
    }

    /// <summary>提供者の表 (無ければファイルから読む)。<see cref="_lock"/> の中で呼ぶ。</summary>
    private Dictionary<string, Entry> Table(IBbsProvider provider)
    {
        if (_byProvider.TryGetValue(provider.Id, out var t)) return t;
        t = new Dictionary<string, Entry>(StringComparer.Ordinal);
        try
        {
            var path = _pathOf(provider);
            if (File.Exists(path))
                foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllBytes(path), Options) ?? new())
                    t[k] = v;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write($"[authors] {provider.Id} の投稿者情報を読めませんでした: {ex.Message}");
        }
        _byProvider[provider.Id] = t;
        return t;
    }

    /// <summary>保存済みの情報 (期限切れも含む。表示は古くてもよいので出す)。</summary>
    public Dictionary<string, AuthorProfile> Get(IBbsProvider provider, IEnumerable<string> names)
    {
        lock (_lock)
        {
            var t = Table(provider);
            var result = new Dictionary<string, AuthorProfile>(StringComparer.Ordinal);
            foreach (var n in names)
                if (t.TryGetValue(n, out var e) && e.Profile is { } p) result[n] = p;
            return result;
        }
    }

    /// <summary>取り直しが要る投稿者名 (未取得 / 期限切れ)。</summary>
    public List<string> Stale(IBbsProvider provider, IEnumerable<string> names, DateTimeOffset now)
    {
        lock (_lock)
        {
            var t = Table(provider);
            var nowEpoch = now.ToUnixTimeSeconds();
            return names.Where(n =>
            {
                if (!t.TryGetValue(n, out var e)) return true;
                var life = e.Profile is null ? MissingLifetime : Lifetime;
                return nowEpoch - e.FetchedEpoch > (long)life.TotalSeconds;
            }).ToList();
        }
    }

    /// <summary>取得結果を覚えて保存する。<paramref name="missing"/> は問い合わせたが見つからなかった投稿者名。
    /// 覚えるのはすぐ (呼んだ直後の <see cref="Get"/> に出る)、ファイルへの保存は裏で行う。</summary>
    public void Put(IBbsProvider provider, IReadOnlyDictionary<string, AuthorProfile> byName, IEnumerable<string> missing, DateTimeOffset now)
    {
        Dictionary<string, Entry> snapshot;
        long version;
        lock (_lock)
        {
            var t = Table(provider);
            var epoch = now.ToUnixTimeSeconds();
            foreach (var (name, p) in byName) t[name] = new Entry { Profile = p, FetchedEpoch = epoch };
            foreach (var name in missing) if (!byName.ContainsKey(name)) t[name] = new Entry { Profile = null, FetchedEpoch = epoch };
            // 保存する内容の写し (Entry は書き換えず差し替えるだけなので、表の写しで足りる)
            snapshot = new Dictionary<string, Entry>(t, StringComparer.Ordinal);
            version  = ++_saveVersion;
        }
        var path = _pathOf(provider);
        _ = Task.Run(() => Save(provider, path, snapshot, version));
    }

    private void Save(IBbsProvider provider, string path, Dictionary<string, Entry> snapshot, long version)
    {
        lock (_saveLock)
        {
            if (version <= _savedVersion) return;   // もっと新しい内容を既に書いた
            var perf = System.Diagnostics.Stopwatch.StartNew();   // 重さの調査用
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = path + ".tmp";
                File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(snapshot, Options));
                File.Move(tmp, path, overwrite: true);
                _savedVersion = version;
                if (perf.ElapsedMilliseconds >= 30)
                    ChBrowser.Services.Logging.LogService.Instance.Write(
                        $"[perf] 投稿者情報の保存 ({snapshot.Count} 人分) に {perf.ElapsedMilliseconds} ms (裏で実行)");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ChBrowser.Services.Logging.LogService.Instance.Write($"[authors] {provider.Id} の投稿者情報を保存できませんでした: {ex.Message}");
            }
        }
    }
}
