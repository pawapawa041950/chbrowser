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
/// data URL 埋め込みではなく仮想ホスト配信にする)。URL にはファイルの更新時刻を版として付け、更新後にキャッシュの古いフォントが出ないようにする。</para>
///
/// <para>更新: 配布元 (GitHub の googlefonts/noto-emoji) の contents API が返す内容ハッシュ (git の blob SHA-1) と、
/// 手元のファイルから同じ方法で計算したハッシュを比べ、違えば最新版をダウンロードして置き換える (<see cref="CheckAndUpdateAsync"/>)。
/// ダウンロードしたファイルはハッシュが配布元と一致することを確かめてから置く。</para>
///
/// <para>有効条件: <see cref="Active"/> = 「設定 ON」かつ「ダウンロード済み」。どちらか欠ければ何も注入せず、
/// Windows 標準フォント (Segoe UI Emoji) にフォールバックする。</para></summary>
public static class EmojiFontService
{
    /// <summary>@font-face で宣言する内部 font-family 名 (システムの 'Noto Color Emoji' と衝突させない)。</summary>
    public const string FamilyName = "ChBrowser Emoji";

    /// <summary>COLRv1 版 Noto Color Emoji の置き場所 (googlefonts/noto-emoji リポジトリ内のパス。OFL ライセンス・再配布可)。
    /// リポジトリの構成が変わることがある (2026-09 に <c>fonts/</c> → <c>2D/fonts/</c>) ので、新しい順に候補を持つ。</summary>
    public static readonly string[] RepoPaths = { "2D/fonts/Noto-COLRv1.ttf", "fonts/Noto-COLRv1.ttf" };
    private const string ApiBase = "https://api.github.com/repos/googlefonts/noto-emoji/contents/";
    private const string RawBase = "https://raw.githubusercontent.com/googlefonts/noto-emoji/main/";

    private const string FileName = "NotoColorEmoji-COLRv1.ttf";

    private static string? _fontsDir;       // <CacheRootDir>/fonts
    private static string? _virtualUrl;     // https://<host>/fonts/<file>
    private static volatile bool _enabled;

    /// <summary>App.OnStartup で 1 度だけ呼ぶ。フォント保存先と配信 URL、初期 ON/OFF を設定する。
    /// 前回、使用中で置き換えられなかった更新 (<c>.pending</c>) があればここで反映する。</summary>
    /// <param name="fontsDir">フォント保存ディレクトリ (= 仮想ホストにマウントされたフォルダ配下)。</param>
    /// <param name="virtualUrl">そのフォントファイルを指す仮想ホスト URL。</param>
    /// <param name="enabled">設定上のフォント利用フラグ (= <see cref="ChBrowser.Models.AppConfig.UseNotoColorEmoji"/>)。</param>
    public static void Initialize(string fontsDir, string virtualUrl, bool enabled)
    {
        _fontsDir   = fontsDir;
        _virtualUrl = virtualUrl;
        _enabled    = enabled;
        ApplyPendingUpdate();
    }

    /// <summary>設定の ON/OFF を更新する (ApplyConfigImmediate から)。</summary>
    public static void SetEnabled(bool enabled) => _enabled = enabled;

    /// <summary>フォントファイルの絶対パス (未初期化なら null)。</summary>
    public static string? FilePath =>
        _fontsDir is null ? null : Path.Combine(_fontsDir, FileName);

    private static string? PendingPath => FilePath is { } p ? p + ".pending" : null;

    /// <summary>ダウンロード済みか (ファイルが存在するか)。</summary>
    public static bool IsDownloaded => FilePath is { } p && File.Exists(p);

    /// <summary>次回起動時に反映する更新が待っているか。</summary>
    public static bool HasPendingUpdate => PendingPath is { } p && File.Exists(p);

    /// <summary>絵文字フォントを実際に使う状態か (= 設定 ON かつ DL 済み)。</summary>
    public static bool Active => _enabled && IsDownloaded;

    /// <summary>CSS で参照する URL。ファイルの更新時刻を版として付ける (更新後にブラウザのキャッシュで古いフォントが出ないように)。</summary>
    private static string? VersionedUrl
        => _virtualUrl is null || FilePath is not { } p || !File.Exists(p) ? _virtualUrl
         : _virtualUrl + "?v=" + File.GetLastWriteTimeUtc(p).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);

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
            $"\n{BuildFontFaceCssOrNull()}\n" +
            $"body{{font-family:{textFonts},'{FamilyName}','Segoe UI Emoji',{generic};}}\n";
    }

    /// <summary><c>@font-face</c> 宣言だけを返す (font-family の上書きは呼び出し側で行うケース用)。
    /// <see cref="Active"/> でなければ null。書き込みウィンドウのテキスト入力 (textarea) のように
    /// body ではなく特定要素にフォントを当てたいときに使う。</summary>
    public static string? BuildFontFaceCssOrNull()
    {
        if (!Active || _virtualUrl is null) return null;
        return $"@font-face{{font-family:'{FamilyName}';src:url('{VersionedUrl}');}}";
    }

    // -----------------------------------------------------------------
    // ダウンロード / 更新
    // -----------------------------------------------------------------

    /// <summary>配布元の最新版の情報 (GitHub の contents API)。<see cref="Sha"/> は git の blob SHA-1 (内容のハッシュ)。</summary>
    public sealed record RemoteInfo(string Path, string Sha, long Size, string DownloadUrl);

    /// <summary>更新確認・更新の結果。</summary>
    public enum UpdateOutcome
    {
        /// <summary>手元が最新。</summary>
        UpToDate,
        /// <summary>新しい版をダウンロードして置き換えた。</summary>
        Updated,
        /// <summary>新しい版をダウンロードしたが使用中で置き換えられないので、次回起動時に置き換える。</summary>
        UpdatedOnNextStart,
    }

    /// <summary>GitHub への要求を差し替える口 (ハーネス用)。null なら通常の HTTP。</summary>
    public static HttpMessageHandler? HandlerForTest { get; set; }

    private static HttpClient NewHttp()
    {
        var http = HandlerForTest is { } h ? new HttpClient(h, disposeHandler: false) : new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(5);
        // GitHub は UA 無しリクエストを弾くので付与。raw はリダイレクトされるが既定で追従する。
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ChBrowser");
        return http;
    }

    /// <summary>配布元の最新版の情報を取る (候補のパスを新しい順に試す)。見つからなければ例外。</summary>
    public static async Task<RemoteInfo> GetRemoteInfoAsync(CancellationToken ct)
    {
        using var http = NewHttp();
        foreach (var path in RepoPaths)
        {
            using var resp = await http.GetAsync(ApiBase + path, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) continue;
            if (!resp.IsSuccessStatusCode)
            {
                // 403 / 429 = API の回数制限 (未認証は 1 時間 60 回)
                throw new HttpRequestException((int)resp.StatusCode is 403 or 429
                    ? "GitHub への問い合わせ回数の上限に達しました。しばらく待ってから試してください"
                    : $"配布元の情報を取得できませんでした (HTTP {(int)resp.StatusCode})");
            }
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var r    = doc.RootElement;
            var sha  = r.TryGetProperty("sha", out var s1) && s1.ValueKind == System.Text.Json.JsonValueKind.String ? s1.GetString() ?? "" : "";
            var size = r.TryGetProperty("size", out var s2) && s2.TryGetInt64(out var n) ? n : -1;
            var url  = r.TryGetProperty("download_url", out var s3) && s3.ValueKind == System.Text.Json.JsonValueKind.String ? s3.GetString() ?? "" : "";
            if (sha.Length == 0) continue;
            return new RemoteInfo(path, sha, size, url.Length > 0 ? url : RawBase + path);
        }
        throw new HttpRequestException("配布元にフォントが見つかりません (配布元の構成が変わった可能性があります)");
    }

    /// <summary>ファイルの git blob SHA-1 (GitHub の <c>sha</c> と同じ計算: <c>"blob &lt;長さ&gt;\0" + 中身</c>)。</summary>
    public static string GitBlobSha1(string path)
    {
        using var sha1 = System.Security.Cryptography.SHA1.Create();
        using var fs   = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var header = System.Text.Encoding.ASCII.GetBytes($"blob {fs.Length}\0");
        sha1.TransformBlock(header, 0, header.Length, null, 0);
        var buffer = new byte[81920];
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0) sha1.TransformBlock(buffer, 0, read, null, 0);
        sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha1.Hash!).ToLowerInvariant();
    }

    /// <summary>手元のフォントが最新か確かめ、新しい版があればダウンロードして置き換える (「更新を確認」ボタン)。
    /// 次回起動時に反映する更新が既に待っていて、それが最新なら何もしない。</summary>
    public static async Task<UpdateOutcome> CheckAndUpdateAsync(IProgress<double>? progress, CancellationToken ct)
    {
        if (FilePath is null) throw new InvalidOperationException("EmojiFontService.Initialize が呼ばれていません。");
        var info = await GetRemoteInfoAsync(ct).ConfigureAwait(false);
        if (HasPendingUpdate && SameContent(PendingPath!, info.Sha)) return UpdateOutcome.UpdatedOnNextStart;
        if (IsDownloaded && SameContent(FilePath, info.Sha)) return UpdateOutcome.UpToDate;
        return await DownloadAsync(info, progress, ct).ConfigureAwait(false);
    }

    private static bool SameContent(string path, string sha) => string.Equals(GitBlobSha1(path), sha, StringComparison.OrdinalIgnoreCase);

    /// <summary>最新版の COLRv1 版 Noto Color Emoji をダウンロードして保存先に配置する (初回の「ダウンロード」ボタン)。</summary>
    public static async Task DownloadAsync(IProgress<double>? progress, CancellationToken ct)
    {
        var info = await GetRemoteInfoAsync(ct).ConfigureAwait(false);
        await DownloadAsync(info, progress, ct).ConfigureAwait(false);
    }

    /// <summary>一時ファイルへ落とし、フォントの署名と配布元の内容ハッシュを検証してから本配置へ移す (= 破損・途中切れのファイルを掴まない)。</summary>
    private static async Task<UpdateOutcome> DownloadAsync(RemoteInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        if (_fontsDir is null || FilePath is null)
            throw new InvalidOperationException("EmojiFontService.Initialize が呼ばれていません。");

        Directory.CreateDirectory(_fontsDir);
        var tmp = FilePath + ".download";

        using (var http = NewHttp())
        {
            using var resp = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                                       .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength ?? (info.Size > 0 ? info.Size : -1L);
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
        if (!SameContent(tmp, info.Sha))
        {
            TryDelete(tmp);
            throw new InvalidDataException("ダウンロードしたファイルが配布元の内容と一致しません (途中で切れた可能性があります)。もう一度試してください。");
        }

        // 既存があれば置き換え。表示中のペインが掴んでいて置き換えられなければ、次回起動時に置き換える
        try
        {
            File.Move(tmp, FilePath, overwrite: true);
            TryDelete(PendingPath!);
            return UpdateOutcome.Updated;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.Move(tmp, PendingPath!, overwrite: true);
            return UpdateOutcome.UpdatedOnNextStart;
        }
    }

    /// <summary>前回置き換えられなかった更新 (<c>.pending</c>) を反映する (起動時、まだどのペインもフォントを読んでいないうち)。</summary>
    private static void ApplyPendingUpdate()
    {
        try
        {
            if (PendingPath is { } pending && File.Exists(pending)) File.Move(pending, FilePath!, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[EmojiFont] pending update failed: {ex.Message}");
        }
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
