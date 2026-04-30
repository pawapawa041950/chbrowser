using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using ChBrowser.Models;
using ChBrowser.ViewModels;

namespace ChBrowser.Services.Render;

/// <summary>
/// スレ一覧 (subject.txt 由来 or お気に入りディレクトリ由来) を 1 枚の HTML として組み立てる。
///
/// Phase 11d で外部 CSS/JS 構成に統一: `Resources/thread-list.html` をシェルとして読み、
/// `<!--{{ITEMS}}-->` にテーブルを埋め込み、`/*{{CSS}}*/` `/*{{JS}}*/` にディスク優先 CSS/JS を注入する
/// (= favorites/board-list と同じプレースホルダ方式)。
/// </summary>
public static class ThreadListHtmlBuilder
{
    private static string? _shellHtmlCache;
    private static readonly object Lock = new();

    public static string Build(
        IReadOnlyList<ThreadListItem> items,
        DateTimeOffset                now)
    {
        var sb = new StringBuilder(8192);
        sb.Append(@"<table><thead><tr>");
        sb.Append(@"<th class=""col-log sortable"" data-sort=""log"" data-sort-type=""num""></th>");
        sb.Append(@"<th class=""col-no sortable sort-asc"" data-sort=""no"" data-sort-type=""num"">No</th>");
        sb.Append(@"<th class=""sortable"" data-sort=""title"" data-sort-type=""str"">タイトル</th>");
        sb.Append(@"<th class=""col-board sortable"" data-sort=""board"" data-sort-type=""str"">板</th>");
        sb.Append(@"<th class=""col-count sortable"" data-sort=""count"" data-sort-type=""num"">数</th>");
        sb.Append(@"<th class=""col-momentum sortable"" data-sort=""momentum"" data-sort-type=""num"">勢い</th>");
        sb.Append(@"</tr></thead><tbody>");

        foreach (var item in items)
        {
            var t        = item.Info;
            var momentum = CalcMomentum(t.Key, now, t.PostCount);
            var state    = item.State;
            var sortVal  = (int)state; // None=0, Cached=1, Updated=2, Dropped=3

            sb.Append(@"<tr class=""");
            switch (state)
            {
                case LogMarkState.Cached:  sb.Append("has-log "); break;
                case LogMarkState.Updated: sb.Append("has-update "); break;
                case LogMarkState.Dropped: sb.Append("has-dropped "); break;
            }
            if (item.IsFavorited) sb.Append("is-favorited ");
            sb.Append('"');
            sb.Append(@" data-key=""").Append(HtmlEscape.Attr(t.Key)).Append('"');
            sb.Append(@" data-host=""").Append(HtmlEscape.Attr(item.Host)).Append('"');
            sb.Append(@" data-dir=""").Append(HtmlEscape.Attr(item.DirectoryName)).Append('"');
            sb.Append(@" data-no=""").Append(t.Order).Append('"');
            sb.Append(@" data-title=""").Append(HtmlEscape.Attr(t.Title)).Append('"');
            sb.Append(@" data-board=""").Append(HtmlEscape.Attr(item.BoardName)).Append('"');
            sb.Append(@" data-count=""").Append(t.PostCount).Append('"');
            sb.Append(@" data-momentum=""").Append(momentum.ToString("F1", CultureInfo.InvariantCulture)).Append('"');
            sb.Append(@" data-log=""").Append(sortVal).Append('"');
            sb.Append('>');
            sb.Append(@"<td class=""col-log""><span class=""log-mark""></span></td>");
            sb.Append(@"<td class=""col-no"">").Append(t.Order).Append("</td>");
            sb.Append("<td>").Append(HtmlEscape.Text(t.Title)).Append("</td>");
            sb.Append(@"<td class=""col-board"">").Append(HtmlEscape.Text(item.BoardName)).Append("</td>");
            sb.Append(@"<td class=""col-count"">").Append(t.PostCount).Append("</td>");
            sb.Append(@"<td class=""col-momentum"">").Append(momentum.ToString("F1", CultureInfo.InvariantCulture)).Append("</td>");
            sb.Append("</tr>");
        }

        sb.Append("</tbody></table>");

        return LoadShellHtml().Replace("<!--{{ITEMS}}-->", sb.ToString());
    }

    /// <summary>シェル HTML キャッシュをクリア (Phase 11d「すべての CSS を再読み込み」用)。</summary>
    public static void InvalidateCache()
    {
        lock (Lock) _shellHtmlCache = null;
    }

    private static double CalcMomentum(string key, DateTimeOffset now, int postCount)
    {
        if (!long.TryParse(key, out var threadUnix)) return 0;
        var nowUnix = now.ToUnixTimeSeconds();
        var diff = Math.Max(1, nowUnix - threadUnix);
        return 86400.0 / diff * postCount;
    }

    private static string LoadShellHtml()
    {
        if (_shellHtmlCache is not null) return _shellHtmlCache;
        lock (Lock)
        {
            if (_shellHtmlCache is not null) return _shellHtmlCache;
            var asm    = typeof(ThreadListHtmlBuilder).Assembly;
            var html   = ReadEmbeddedText(asm, "ChBrowser.Resources.thread-list.html");
            // Phase 11d: テーマサービスがあれば disk 優先で CSS を取得 (= ユーザ編集を反映)。
            // 未登録時は埋め込み既定にフォールバック。
            var css    = ChBrowser.Services.Theme.ThemeService.CurrentInstance?.LoadCss("thread-list.css")
                         ?? ReadEmbeddedText(asm, "ChBrowser.Resources.thread-list.css");
            var js     = ReadEmbeddedText(asm, "ChBrowser.Resources.thread-list.js");
            var bridge = ReadEmbeddedText(asm, "ChBrowser.Resources.shortcut-bridge.js");
            _shellHtmlCache = html
                .Replace("/*{{CSS}}*/",             css)
                .Replace("/*{{SHORTCUT_BRIDGE}}*/", bridge)
                .Replace("/*{{JS}}*/",              js);
            return _shellHtmlCache;
        }
    }

    private static string ReadEmbeddedText(Assembly asm, string name)
    {
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"Embedded resource not found: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
