using System;
using System.Collections.Generic;
using System.Linq;

namespace ChBrowser.Services.Bbs;

/// <summary>スレ一覧の並び順 (<see cref="IBbsProvider.ListingSorts"/>) を持つ掲示板ごとの、ユーザが選んだ既定の並び順。
/// 設定 (<see cref="Models.AppConfig.ListingSortDefaults"/>) の適用時に <see cref="Apply"/> で入れる。
/// 並び順を持つ掲示板なら提供者の種類に関係なく同じ仕組みで扱う (reddit の hot / new、将来の 4chan・ふたばのカタログ順など)。</summary>
public static class ListingSortDefaults
{
    private static IReadOnlyDictionary<string, string> _map = new Dictionary<string, string>();

    /// <summary>提供者 Id → 並び順の Value。<see cref="IBbsProvider.ListingSorts"/> に無い値は無視される。</summary>
    public static void Apply(IReadOnlyDictionary<string, string>? map)
        => _map = map is null ? new Dictionary<string, string>() : new Dictionary<string, string>(map, StringComparer.Ordinal);

    /// <summary>その掲示板の既定の並び順。ユーザ指定が選択肢にあればそれ、無ければ選択肢の先頭。並び順を持たない掲示板は null。</summary>
    public static string? For(IBbsProvider provider)
    {
        if (provider.ListingSorts.Count == 0) return null;
        if (_map.TryGetValue(provider.Id, out var v) && provider.ListingSorts.Any(s => s.Value == v)) return v;
        return provider.ListingSorts[0].Value;
    }
}
