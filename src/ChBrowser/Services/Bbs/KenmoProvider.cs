using System;
using System.Collections.Generic;
using ChBrowser.Models;

namespace ChBrowser.Services.Bbs;

/// <summary>嫌儲.org (kenmo.org / 00c.org) の提供者。5ch 互換掲示板 (2026-11-04 実測、共通部分は <see cref="FiveChCompatibleProvider"/>)。
///
/// <list type="bullet">
/// <item><description>ホストは <c>kenmo.org</c> と <c>00c.org</c> の 2 つ (板ごとにどちらか。板一覧に両方が混ざって載る)。板 dir は 1 階層。</description></item>
/// <item><description>読み取りは 5ch と同じ (Range 206、新着無しは 416 + <c>Content-Range: bytes */全長</c>、Shift_JIS)。無いスレは 404。</description></item>
/// <item><description>板一覧は <c>https://kenmo.org/bbsmenu.html</c> (まちBBS と同じ <c>&lt;b&gt;カテゴリ&lt;/b&gt;</c> + <c>&lt;a href&gt;</c> 形式、Shift_JIS)。</description></item>
/// <item><description>保存ルート <c>data/kenmo.org/</c> と <c>data/00c.org/</c> (5ch と同じ生 dat)。</description></item>
/// </list></summary>
public sealed class KenmoProvider : FiveChCompatibleProvider
{
    private const string MainHost = "kenmo.org";
    private const string SubHost  = "00c.org";

    public override string Id          => "kenmo";
    public override string DisplayName => "嫌儲.org";

    public override IReadOnlyList<string> StorageRoots { get; } = new[] { MainHost, SubHost };
    public override string DefaultHost => MainHost;
    public override IReadOnlyList<string> HostSuffixes { get; } = new[] { MainHost, SubHost };

    public override bool OwnsHost(string host)
        => IsHost(host, MainHost) || IsHost(host, SubHost);

    private static bool IsHost(string host, string root)
        => string.Equals(host, root, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + root, StringComparison.OrdinalIgnoreCase);

    public override string? BoardListUrl => $"https://{MainHost}/bbsmenu.html";
    public override string  BoardListCacheExtension => "html";
    public override IReadOnlyList<BoardCategory> ParseBoardList(byte[] bytes) => Api.BbsmenuClient.ParseBbsmenuHtml(this, bytes);
}
