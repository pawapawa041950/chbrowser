using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using ChBrowser.Services.Storage;

namespace ChBrowser.Services.Theme;

/// <summary>
/// テーマ (各ペインの CSS と スレ表示の post.html / post.css) を管理する。
///
/// <para>Phase 11d で 5 CSS (favorites / board-list / thread-list / thread / post) のディスク優先読みを担当。
/// 設計書 §5.7 / §5.3.1 参照。</para>
///
/// <para>
/// 動作:
/// <list type="bullet">
/// <item><description>RELEASE: <c>data/themes/&lt;ActiveThemeName&gt;/&lt;name&gt;.css</c> が存在すればそれを、無ければ埋め込み既定を読む</description></item>
/// <item><description>DEBUG: 常に埋め込みリソースを読む (= ソース変更を即反映、ディスクキャッシュに邪魔されない)</description></item>
/// </list>
/// </para>
///
/// <para>
/// テンプレ仕様 (テンプレ作成者向けコントラクト) は <see cref="LoadActiveTheme"/> 用の post.html についてのみ:
/// <list type="bullet">
///   <item><description>変数: <c>{{number}} {{name}} {{date}} {{id}} {{body}} {{replyCount}} {{children}} {{isEmbedded}} {{domId}}</c></description></item>
///   <item><description>条件分岐: <c>{{#if var}}...{{/if}}</c> (1 段ネストのみ)</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class ThemeService
{
    /// <summary>現状はテーマ切替 UI が無いので "default" 固定。将来検討。</summary>
    public const string ActiveThemeName = "default";

    /// <summary>HtmlBuilder などからグローバルアクセスするための弱参照。
    /// App.OnStartup で ThemeService が生成されたら設定される。Builder 側はこれを参照して
    /// disk-first CSS を取得する (= 引数経由の DI を要しないため既存 static API を維持できる)。</summary>
    public static ThemeService? CurrentInstance { get; private set; }

    private readonly DataPaths _paths;
    private readonly Assembly  _asm = typeof(ThemeService).Assembly;
    private readonly Dictionary<string, string?> _diskCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();

    /// <summary>「ファイル名 → 埋め込みリソース名」マッピング (Phase 11d: 5 CSS)。</summary>
    private static readonly Dictionary<string, string> CssResources = new(StringComparer.OrdinalIgnoreCase)
    {
        { "favorites.css",  "ChBrowser.Resources.favorites.css"  },
        { "board-list.css", "ChBrowser.Resources.board-list.css" },
        { "thread-list.css","ChBrowser.Resources.thread-list.css"},
        { "thread.css",     "ChBrowser.Resources.thread.css"     },
        { "post.css",       "ChBrowser.Resources.post.css"       },
    };

    private const string PostHtmlResource = "ChBrowser.Resources.post.html";

    public ThemeService(DataPaths paths)
    {
        _paths          = paths;
        CurrentInstance = this;
    }

    /// <summary>指定ファイル名 (= "favorites.css" 等) の CSS を「ディスク優先 + 埋め込み既定」で取得。
    /// 未登録のファイル名は null を返す。
    ///
    /// <para>Debug/Release 共通の動作: <c>data/themes/default/&lt;file&gt;</c> があればそれを返し、
    /// 無ければ埋め込み既定を返す。設定画面のデザイン編集 UI でユーザがディスクを編集した内容が
    /// (Debug ビルドでも) 即時反映されるようにする。</para>
    ///
    /// <para>開発者向け注記: ソースの <c>Resources/&lt;file&gt;.css</c> を編集して試したい場合は、
    /// <c>data/themes/default/&lt;file&gt;.css</c> を手動で削除してから起動する (= 埋め込みにフォールバックさせる)。
    /// 「既定 CSS を生成」ボタンは既存ファイルを保護するため、編集中のディスクファイルを上書きはしない。</para></summary>
    public string? LoadCss(string fileName)
    {
        if (!CssResources.TryGetValue(fileName, out var resource)) return null;

        lock (_cacheLock)
        {
            if (_diskCache.TryGetValue(fileName, out var cached)) return cached ?? ReadEmbedded(resource);
        }
        var diskPath = Path.Combine(_paths.ThemesDir, ActiveThemeName, fileName);
        var disk     = TryReadFile(diskPath);
        lock (_cacheLock)
        {
            _diskCache[fileName] = disk;
        }
        return disk ?? ReadEmbedded(resource);
    }

    /// <summary>テーマ default フォルダ配下の CSS ファイルへの絶対パス。
    /// 設定画面の「開く」ボタンで使用。存在保証はしない (未配置時は <see cref="ExtractDefaultCss"/> で生成可)。</summary>
    public string ResolveCssPath(string fileName)
        => Path.Combine(_paths.ThemesDir, ActiveThemeName, fileName);

    /// <summary>テーマフォルダ (= <c>data/themes/default/</c>) の絶対パス。設定画面のフォルダ起動ボタンで使用。</summary>
    public string ThemeFolderPath
        => Path.Combine(_paths.ThemesDir, ActiveThemeName);

    /// <summary>ディスクに無い CSS (5 ファイル + post.html) を埋め込み既定から書き出す。
    /// 既存ファイルは触らない (= ユーザ編集を保護)。設定画面の「既定 CSS を生成」ボタンで使う。</summary>
    public void ExtractDefaultThemeFiles()
    {
        try
        {
            var dir = Path.Combine(_paths.ThemesDir, ActiveThemeName);
            Directory.CreateDirectory(dir);
            foreach (var (file, res) in CssResources)
                ExtractIfMissing(Path.Combine(dir, file), res);
            ExtractIfMissing(Path.Combine(dir, "post.html"), PostHtmlResource);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ThemeService] extract defaults failed: {ex.Message}");
        }
    }

    /// <summary>キャッシュをクリア (= 次の <see cref="LoadCss"/> でディスク再読込)。
    /// 設定画面の「すべての CSS を再読み込み」ボタンから呼ぶ。</summary>
    public void InvalidateCache()
    {
        lock (_cacheLock) _diskCache.Clear();
    }

    /// <summary>アクティブな post.html / post.css を読む (スレ表示シェル用、既存 API)。
    /// Debug/Release 共通で disk-first + 埋め込み fallback。<see cref="LoadCss"/> と同じ方針。</summary>
    public ThemeContent LoadActiveTheme()
    {
        var dir = Path.Combine(_paths.ThemesDir, ActiveThemeName);
        var html = TryReadFile(Path.Combine(dir, "post.html")) ?? ReadEmbedded(PostHtmlResource);
        var css  = LoadCss("post.css") ?? ReadEmbedded(CssResources["post.css"]);
        return new ThemeContent(html, css);
    }

    private void ExtractIfMissing(string path, string resourceName)
    {
        if (File.Exists(path)) return;
        try
        {
            File.WriteAllText(path, ReadEmbedded(resourceName), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ThemeService] extract {path} failed: {ex.Message}");
        }
    }

    private static string? TryReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ThemeService] read {path} failed: {ex.Message}");
            return null;
        }
    }

    private string ReadEmbedded(string resourceName)
    {
        using var stream = _asm.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>テーマ 1 個分の生コンテンツ (post.html テンプレ + post.css スタイル)。</summary>
public sealed record ThemeContent(string PostHtmlTemplate, string PostCss);
