using System;

namespace ChBrowser.Models;

/// <summary>
/// 1 板を表す。bbsmenu.json の category_content[i] に対応。
/// </summary>
public sealed record Board(
    string DirectoryName,   // 例: "news"
    string BoardName,       // 例: "ニュース速報"
    string Url,             // 例: "https://news.5ch.io/news/"
    string CategoryName,    // 例: "ニュース"
    int    CategoryOrder)
{
    /// <summary>URL からホスト部 (例: "news.5ch.io") を返す。</summary>
    public string Host => new Uri(Url).Host;
}
