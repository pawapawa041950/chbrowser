using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ChBrowser.Models;
using ChBrowser.ViewModels;

namespace ChBrowser.Services.Render;

/// <summary>
/// スレ一覧ペイン。ページ (シェル + 表の見出し) は 1 回だけ読み込み、行は JSON で送って JS が組み立てる
/// (<c>setItems</c> メッセージ。板一覧・お気に入りと同じ「シェル + データ push」方式)。
/// 一覧を取り直してもページを読み直さないので、列幅・並べ替え・絞り込み・選択行・スクロール位置がそのまま残る。
/// ページを読み直したとき (CSS の変更 / 別ペインへのタブ移動で WebView が作り直されたとき) は JS の <c>ready</c> で行を送り直す。
/// </summary>
public static class ThreadListHtmlBuilder
{
    private static string? _shellHtmlCache;
    private static readonly object Lock = new();

    /// <summary>スレ一覧ペインのシェル HTML (表の見出しまで。行は空)。</summary>
    public static string BuildShell() => LoadShellHtml();

    private const string TableSkeleton =
        @"<table><thead><tr>" +
        @"<th class=""col-log sortable"" data-sort=""log"" data-sort-type=""num""></th>" +
        @"<th class=""col-no sortable sort-asc"" data-sort=""no"" data-sort-type=""num"">No</th>" +
        @"<th class=""sortable"" data-sort=""title"" data-sort-type=""str"">タイトル</th>" +
        @"<th class=""col-board sortable"" data-sort=""board"" data-sort-type=""str"">板</th>" +
        @"<th class=""col-count sortable"" data-sort=""count"" data-sort-type=""num"">数</th>" +
        @"<th class=""col-momentum sortable"" data-sort=""momentum"" data-sort-type=""num"">勢い</th>" +
        @"</tr></thead><tbody></tbody></table>";

    /// <summary>行の送信データ。勢いは <paramref name="now"/> 時点で計算する (表示と並べ替えに使う)。</summary>
    public static IReadOnlyList<ThreadListRow> BuildRows(IReadOnlyList<ThreadListItem> items, DateTimeOffset now)
    {
        var rows = new List<ThreadListRow>(items.Count);
        foreach (var item in items)
        {
            var t = item.Info;
            if (item.Kind == ThreadListItemKind.Board)
            {
                // 板そのものを表す行 (「板一覧以外の取得済み板」集約タブ)。No / 勢いは空、数 = ローカル dat 件数
                rows.Add(new ThreadListRow("board", "", item.Host, item.DirectoryName, 0, t.Title, item.BoardName, t.PostCount, null, 0, false));
                continue;
            }
            var momentum = CalcMomentum(t, now).ToString("F1", CultureInfo.InvariantCulture);
            rows.Add(new ThreadListRow("thread", t.Key, item.Host, item.DirectoryName, t.Order, t.Title, item.BoardName, t.PostCount,
                momentum, (int)item.State, item.IsFavorited));   // Log: None=0, Cached=1, Updated=2, Dropped=3, RepliedToOwn=4
        }
        return rows;
    }

    /// <summary>シェル HTML キャッシュをクリア (Phase 11d「すべての CSS を再読み込み」用)。</summary>
    public static void InvalidateCache()
    {
        lock (Lock) _shellHtmlCache = null;
    }

    /// <summary>勢い (1 日あたりのレス数)。作成時刻は <see cref="ThreadInfo.CreatedEpoch"/> (reddit 等、key が epoch でない掲示板) を優先し、
    /// 無ければ 5ch 系の慣習どおり key を epoch とみなす。</summary>
    private static double CalcMomentum(ThreadInfo t, DateTimeOffset now)
    {
        var postCount = t.PostCount;
        long threadUnix;
        if (t.CreatedEpoch is long created && created > 0) threadUnix = created;
        else if (!long.TryParse(t.Key, out threadUnix)) return 0;
        // 5ch お知らせスレは unixtime が 9 で始まる擬似的な将来日付 (2260 年代 = 9e9 秒以降)
        // で建てられる。勢い計算上はゼロ扱いにする。9 桁の 9xxxxxxxx (1998 年代) はここでは除外。
        if (threadUnix >= 9_000_000_000L) return 0;
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
            var html   = EmbeddedAssets.Read("thread-list.html");
            var css    = EmbeddedAssets.ReadCss("thread-list.css");  // disk-first
            var js     = EmbeddedAssets.Read("thread-list.js");
            var bridge = EmbeddedAssets.Read("shortcut-bridge.js");
            // 絵文字フォント (Noto Color Emoji) が有効なら絵文字グリフだけ Noto に回す (テキストは据え置き)。
            var emojiCss = ChBrowser.Services.Fonts.EmojiFontService
                .BuildBodyFontCssOrNull("'Segoe UI','Yu Gothic UI','Meiryo'", "sans-serif") ?? "";
            _shellHtmlCache = html
                .Replace("<!--{{ITEMS}}-->",        TableSkeleton)
                .Replace("/*{{CSS}}*/",             css + emojiCss)
                .Replace("/*{{SHORTCUT_BRIDGE}}*/", bridge)
                .Replace("/*{{JS}}*/",              js);
            return _shellHtmlCache;
        }
    }
}

/// <summary>スレ一覧の 1 行 (<c>setItems</c> で JS へ送る)。<see cref="Kind"/> = "thread" / "board" (板そのものの行)。
/// <see cref="Momentum"/> は表示用の文字列 (板の行は null)、<see cref="Log"/> は状態マーク (0〜4、並べ替えにも使う)。</summary>
public sealed record ThreadListRow(string Kind, string Key, string Host, string Dir, int No, string Title, string Board,
                                   int Count, string? Momentum, int Log, bool Fav);
