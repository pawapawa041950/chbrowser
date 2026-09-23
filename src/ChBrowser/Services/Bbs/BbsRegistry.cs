using System;
using System.Collections.Generic;
using System.Linq;

namespace ChBrowser.Services.Bbs;

/// <summary>登録済みの掲示板提供者の一覧と、ホスト名からの解決。
///
/// <para>静的に保持する理由: URL の解釈 (<see cref="Url.AddressBarParser"/>) や正規 URL の生成は
/// ViewModel / View / JS ブリッジ / AI ツールなど至る所から呼ばれ、DI で配り回すより
/// 「アプリに 1 つの登録簿」として参照する方が既存コードへの影響が小さい。
/// 提供者の追加は <see cref="Register"/> で起動時に行う (既定で 5ch / bbspink・したらば・まちBBS・エッヂは登録済み)。</para></summary>
public static class BbsRegistry
{
    private static readonly List<IBbsProvider> _providers = new()
    {
        new FiveChProvider(),     // index 0 固定 (= FiveCh)
        new ShitarabaProvider(),
        new MachiProvider(),
        new EddiProvider(),
        new RedditProvider(),
    };

    /// <summary>5ch / bbspink の提供者。ホストが解決できない場面の既定 (= 現行動作の維持) に使う。</summary>
    public static IBbsProvider FiveCh => _providers[0];

    public static IReadOnlyList<IBbsProvider> All => _providers;

    /// <summary>提供者を追加登録する (同じ Id は置き換え)。起動時に呼ぶ想定。</summary>
    public static void Register(IBbsProvider provider)
    {
        var idx = _providers.FindIndex(p => string.Equals(p.Id, provider.Id, StringComparison.Ordinal));
        if (idx >= 0) _providers[idx] = provider;
        else          _providers.Add(provider);
    }

    /// <summary>Id で引く。無ければ null。</summary>
    public static IBbsProvider? FindById(string id)
        => _providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    /// <summary>ホスト名 (旧ホスト名可) を所有する提供者。どれも所有しなければ null。</summary>
    public static IBbsProvider? Resolve(string? host)
    {
        if (string.IsNullOrEmpty(host)) return null;
        foreach (var p in _providers)
            if (p.OwnsHost(host)) return p;
        return null;
    }

    /// <summary>ホスト名から提供者を解決し、解決できなければ 5ch を返す。
    /// 「既に Board として存在する = どこかの提供者のもののはず」という前提の場面
    /// (正規 URL 生成、エンドポイント組み立て) で使う。未知ホストでも例外にせず現行動作 (5ch 形式) に倒す。</summary>
    public static IBbsProvider ResolveOrDefault(string? host)
        => Resolve(host) ?? FiveCh;

    /// <summary>保存ルート名 (<c>data/&lt;root&gt;/</c> の root、例: "shitaraba.net") から提供者を解決する。
    /// <see cref="IBbsProvider.StorageRoots"/> に一致する提供者が無ければ <see cref="ResolveOrDefault"/> に倒す
    /// (= 5ch.io / bbspink.com / machi.to はホストとしても有効なのでどちらでも同じ結果になる。
    /// "shitaraba.net" は jbbs. 無しでは <see cref="IBbsProvider.OwnsHost"/> が false なのでこちらが必要)。</summary>
    public static IBbsProvider ResolveByStorageRoot(string root)
    {
        foreach (var p in _providers)
            foreach (var r in p.StorageRoots)
                if (string.Equals(r, root, StringComparison.OrdinalIgnoreCase)) return p;
        return ResolveOrDefault(root);
    }

    /// <summary>全提供者の保存ルート (<c>data/&lt;root&gt;/</c>) の和集合。全ログ走査に使う。重複は除く。</summary>
    public static IReadOnlyList<string> StorageRoots
        => _providers.SelectMany(p => p.StorageRoots)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .ToList();
}
