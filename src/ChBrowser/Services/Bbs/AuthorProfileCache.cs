using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ChBrowser.Services.Bbs;

/// <summary>投稿者情報 (<see cref="AuthorProfile"/>) の保存 (<c>data/&lt;掲示板&gt;/authors.json</c>、投稿者名 → 情報)。
/// アイコンやカルマは頻繁には変わらないので <see cref="Lifetime"/> の間は取り直さない (= スレを開き直しても要求を使わない)。
/// 見つからなかった投稿者 (退会・凍結) も「無い」ことを <see cref="MissingLifetime"/> の間覚えておく。
/// UI スレッドからだけ使う (ロック無し)。</summary>
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

    /// <param name="pathOf">提供者 → 保存ファイルのパス。</param>
    public AuthorProfileCache(Func<IBbsProvider, string> pathOf) => _pathOf = pathOf;

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
        var t = Table(provider);
        var result = new Dictionary<string, AuthorProfile>(StringComparer.Ordinal);
        foreach (var n in names)
            if (t.TryGetValue(n, out var e) && e.Profile is { } p) result[n] = p;
        return result;
    }

    /// <summary>取り直しが要る投稿者名 (未取得 / 期限切れ)。</summary>
    public List<string> Stale(IBbsProvider provider, IEnumerable<string> names, DateTimeOffset now)
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

    /// <summary>取得結果を覚えて保存する。<paramref name="missing"/> は問い合わせたが見つからなかった投稿者名。</summary>
    public void Put(IBbsProvider provider, IReadOnlyDictionary<string, AuthorProfile> byName, IEnumerable<string> missing, DateTimeOffset now)
    {
        var t = Table(provider);
        var epoch = now.ToUnixTimeSeconds();
        foreach (var (name, p) in byName) t[name] = new Entry { Profile = p, FetchedEpoch = epoch };
        foreach (var name in missing) if (!byName.ContainsKey(name)) t[name] = new Entry { Profile = null, FetchedEpoch = epoch };
        try
        {
            var path = _pathOf(provider);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(t, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch (IOException ex)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write($"[authors] {provider.Id} の投稿者情報を保存できませんでした: {ex.Message}");
        }
    }
}
