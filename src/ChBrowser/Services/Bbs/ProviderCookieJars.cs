using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Services.Donguri;
using ChBrowser.Services.Storage;

namespace ChBrowser.Services.Bbs;

/// <summary>提供者ごとの Cookie 保管 (<c>data/&lt;保存ルート&gt;/cookies.txt</c>)。
/// 5ch のどんぐり (acorn / MonaTicket) は従来どおり <see cref="DonguriService"/> が別に持ち、ここは
/// <see cref="PostFormSpec.PersistsCookies"/> を宣言した提供者 (エッヂの edge-token / tinker-token 等) の分だけを扱う。
/// <see cref="Api.PostClient"/> が投稿のたびに <see cref="CookieJar.ApplyToRequest(System.Net.Http.HttpRequestMessage)"/> /
/// <see cref="CookieJar.MergeFromResponse"/> を呼び、終了時に保存する。</summary>
public sealed class ProviderCookieJars
{
    private readonly DataPaths                     _paths;
    private readonly Dictionary<string, CookieJar> _jars = new(StringComparer.Ordinal);
    private readonly object                        _lock = new();

    public ProviderCookieJars(DataPaths paths) => _paths = paths;

    /// <summary>提供者の Cookie 保管を返す (初回アクセス時にディスクから読み込む)。</summary>
    public CookieJar Get(IBbsProvider provider)
    {
        lock (_lock)
        {
            if (!_jars.TryGetValue(provider.Id, out var jar))
            {
                jar = new CookieJar(_paths.ProviderCookiesPath(provider));
                _jars[provider.Id] = jar;
            }
            return jar;
        }
    }

    /// <summary>提供者の Cookie をすべて削除して保存する (設定 → 認証 → 「エッヂの Cookie を削除」)。</summary>
    public async Task ClearAsync(IBbsProvider provider, CancellationToken ct = default)
    {
        var jar = Get(provider);
        jar.Clear();
        await jar.SaveAsync(ct).ConfigureAwait(false);
    }
}
