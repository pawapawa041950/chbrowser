using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ChBrowser.Services.Fonts;

/// <summary>絵文字表示用に Noto Color Emoji (COLRv1 ビルド) を自ディレクトリへダウンロードし、
/// 各 WebView シェルの CSS に <c>@font-face</c> を注入するためのサービス (静的)。
///
/// <para>なぜ COLRv1 版か: 従来の <c>NotoColorEmoji.ttf</c> は CBDT/CBLC (ビットマップ) カラー形式で、
/// Windows の Chromium/WebView2 では <c>@font-face</c> 経由でカラー描画されない。COLRv1 ビルドなら
/// 確実にカラー表示される。</para>
///
/// <para>配信は既存の画像キャッシュ仮想ホスト (<see cref="ChBrowser.Services.Image.ImageCacheService.VirtualHostName"/>)
/// を流用する。フォントはそのキャッシュルート直下の <c>fonts/</c> に置き、
/// <c>https://&lt;host&gt;/fonts/&lt;file&gt;</c> で参照する (= NavigateToString の 2MB 制限を回避するため
/// data URL 埋め込みではなく仮想ホスト配信にする)。</para>
///
/// <para>有効条件: <see cref="Active"/> = 「設定 ON」かつ「ダウンロード済み」。どちらか欠ければ何も注入せず、
/// Windows 標準フォント (Segoe UI Emoji) にフォールバックする。</para></summary>
public static class EmojiFontService
{
    /// <summary>@font-face で宣言する内部 font-family 名 (システムの 'Noto Color Emoji' と衝突させない)。</summary>
    public const string FamilyName = "ChBrowser Emoji";

    /// <summary>COLRv1 版 Noto Color Emoji の取得元 (OFL ライセンス・再配布可)。GitHub raw (リダイレクトあり)。</summary>
    public const string DownloadUrl =
        "https://github.com/googlefonts/noto-emoji/raw/main/fonts/Noto-COLRv1.ttf";

    private const string FileName = "NotoColorEmoji-COLRv1.ttf";

    private static string? _fontsDir;       // <CacheRootDir>/fonts
    private static string? _virtualUrl;     // https://<host>/fonts/<file>
    private static volatile bool _enabled;

    /// <summary>App.OnStartup で 1 度だけ呼ぶ。フォント保存先と配信 URL、初期 ON/OFF を設定する。</summary>
    /// <param name="fontsDir">フォント保存ディレクトリ (= 仮想ホストにマウントされたフォルダ配下)。</param>
    /// <param name="virtualUrl">そのフォントファイルを指す仮想ホスト URL。</param>
    /// <param name="enabled">設定上のフォント利用フラグ (= <see cref="ChBrowser.Models.AppConfig.UseNotoColorEmoji"/>)。</param>
    public static void Initialize(string fontsDir, string virtualUrl, bool enabled)
    {
        _fontsDir   = fontsDir;
        _virtualUrl = virtualUrl;
        _enabled    = enabled;
    }

    /// <summary>設定の ON/OFF を更新する (ApplyConfigImmediate から)。</summary>
    public static void SetEnabled(bool enabled) => _enabled = enabled;

    /// <summary>フォントファイルの絶対パス (未初期化なら null)。</summary>
    public static string? FilePath =>
        _fontsDir is null ? null : Path.Combine(_fontsDir, FileName);

    /// <summary>ダウンロード済みか (ファイルが存在するか)。</summary>
    public static bool IsDownloaded => FilePath is { } p && File.Exists(p);

    /// <summary>絵文字フォントを実際に使う状態か (= 設定 ON かつ DL 済み)。</summary>
    public static bool Active => _enabled && IsDownloaded;

    /// <summary>シェル CSS の末尾に追記する <c>@font-face</c> + body フォント上書きを返す。
    /// <see cref="Active"/> でなければ null (= 何も注入しない)。
    /// 絵文字グリフだけ Noto に回すため、各ペインの本来のテキストフォントを先頭に残し、
    /// その後ろに絵文字フォント → Segoe UI Emoji → generic を並べる。</summary>
    /// <param name="textFonts">そのペインが本来使うテキストフォント (例: <c>'MS Pゴシック','MS PGothic'</c>)。</param>
    /// <param name="generic">末尾の総称フォント (例: <c>monospace</c> / <c>sans-serif</c>)。</param>
    public static string? BuildBodyFontCssOrNull(string textFonts, string generic)
    {
        if (!Active || _virtualUrl is null) return null;
        return
            $"\n@font-face{{font-family:'{FamilyName}';src:url('{_virtualUrl}');}}\n" +
            $"body{{font-family:{textFonts},'{FamilyName}','Segoe UI Emoji',{generic};}}\n";
    }

    /// <summary>COLRv1 版 Noto Color Emoji をダウンロードして保存先に配置する。
    /// 一時ファイルへ落として sfnt 署名を検証してから本配置へ移動する (= 破損ファイルを掴まない)。</summary>
    public static async Task DownloadAsync(IProgress<double>? progress, CancellationToken ct)
    {
        if (_fontsDir is null || FilePath is null)
            throw new InvalidOperationException("EmojiFontService.Initialize が呼ばれていません。");

        Directory.CreateDirectory(_fontsDir);
        var tmp = FilePath + ".download";

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            // GitHub は UA 無しリクエストを弾くことがあるので付与。raw はリダイレクトされるが既定で追従する。
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ChBrowser");
            using var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                                       .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength ?? -1L;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    read += n;
                    if (total > 0) progress?.Report((double)read / total);
                }
            }
        }

        ValidateFontOrThrow(tmp);

        // 既存があれば置き換え (File.Move は overwrite 引数で上書き)。
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>ダウンロード済みフォントを削除する。</summary>
    public static void Delete()
    {
        try { if (FilePath is { } p && File.Exists(p)) File.Delete(p); }
        catch { /* 失敗は無視 (使用中等) */ }
    }

    /// <summary>先頭 4 バイトの sfnt 署名とサイズで「本物のフォントファイル」かを最低限検証する。
    /// HTML エラーページ等を掴んだ場合にここで弾く。</summary>
    private static void ValidateFontOrThrow(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < 100 * 1024)
        {
            TryDelete(path);
            throw new InvalidDataException("ダウンロードしたファイルが小さすぎます (フォントではない可能性)。");
        }

        Span<byte> head = stackalloc byte[4];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (fs.Read(head) < 4) { TryDelete(path); throw new InvalidDataException("ファイルが短すぎます。"); }
        }
        uint sig = (uint)(head[0] << 24 | head[1] << 16 | head[2] << 8 | head[3]);
        // 0x00010000 = TrueType, 'OTTO' = CFF, 'true'/'typ1' = Apple, 'ttcf' = collection。
        bool ok = sig == 0x00010000u || sig == 0x4F54544Fu /*OTTO*/
               || sig == 0x74727565u /*true*/ || sig == 0x74746366u /*ttcf*/;
        if (!ok)
        {
            TryDelete(path);
            throw new InvalidDataException("フォント形式として認識できませんでした。");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 無視 */ }
    }
}
