using System;

namespace ChBrowser.Services.Url;

/// <summary>アドレスバーへ入力されたテキストを Board / Thread / Invalid の 3 種類に分類するパーサ (Phase 14)。
/// 純粋関数で副作用なし。設計詳細は 設計書 §7.7 参照。</summary>
public static class AddressBarParser
{
    /// <summary>入力テキストを解釈し、対応する <see cref="AddressBarTarget"/> を返す。
    /// どの提供者も所有しないホスト、URL として parse できないテキスト、認識不能なパスは
    /// すべて <see cref="AddressBarTargetKind.Invalid"/> を返す。
    /// 旧ホスト名 (5ch.net 等) は提供者の <see cref="ChBrowser.Services.Bbs.IBbsProvider.NormalizeHost"/> で
    /// 正規ホストに書き換えてから判定する (= 古い URL の貼り付け救済)。
    /// URL 形式の知識はすべて <see cref="ChBrowser.Services.Bbs.BbsRegistry"/> に登録された提供者側にある。</summary>
    public static AddressBarTarget Parse(string? input)
    {
        var trimmed = (input ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed)) return AddressBarTarget.Invalid;

        // プロトコル省略 (例: news.5ch.io/news/) は https:// を前置
        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = "https://" + trimmed;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return AddressBarTarget.Invalid;

        var provider = ChBrowser.Services.Bbs.BbsRegistry.Resolve(uri.Host);
        if (provider is null) return AddressBarTarget.Invalid;

        var host = provider.NormalizeHost(uri.Host);
        return provider.TryParseUrl(uri, host) ?? AddressBarTarget.Invalid;
    }
}

public enum AddressBarTargetKind
{
    Invalid,
    Board,
    Thread,
}

/// <summary>パース結果。Kind=Invalid のときは他フィールドは空。
/// <see cref="PostNumber"/> は Kind=Thread のときに URL 末尾のレス番号 (例: /1234567890/100) を入れる。
/// レス番号指定なし、または Kind!=Thread のときは 0。</summary>
public sealed record AddressBarTarget(
    AddressBarTargetKind Kind,
    string               Host,
    string               Directory,
    string               ThreadKey,
    long                 PostNumber = 0)
{
    public static AddressBarTarget Invalid { get; } = new(AddressBarTargetKind.Invalid, "", "", "", 0);

    /// <summary>Board の正規 URL (提供者の形式。5ch なら "https://&lt;host&gt;/&lt;dir&gt;/")。Kind!=Board のときは空。</summary>
    public string BoardUrl => Kind == AddressBarTargetKind.Board
        ? ChBrowser.Services.Bbs.BbsRegistry.ResolveOrDefault(Host).BoardUrl(Host, Directory) : "";

    /// <summary>Thread の正規 URL (提供者の形式。5ch なら "https://&lt;host&gt;/test/read.cgi/&lt;dir&gt;/&lt;key&gt;/")。Kind!=Thread のときは空。</summary>
    public string ThreadUrl => Kind == AddressBarTargetKind.Thread
        ? ChBrowser.Services.Bbs.BbsRegistry.ResolveOrDefault(Host).ThreadUrl(Host, Directory, ThreadKey) : "";
}
