using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChBrowser.Services.Image;

/// <summary>AI 生成画像 (Stable Diffusion WebUI infotext) のメタデータを抽出するサービス。
///
/// <para>抽出対象:
/// <list type="bullet">
/// <item>PNG: tEXt / iTXt / zTXt チャンク内の <c>parameters</c> / <c>UserComment</c> / <c>Comment</c> キー。</item>
/// <item>JPEG / WebP: EXIF UserComment (APP1 セグメント / RIFF EXIF チャンク) 内の SD WebUI infotext。</item>
/// <item>MP4 / MOV: moov/udta/meta の keys+ilst (QuickTime 形式) と ©cmt / ---- (iTunes 形式)。
///   comment に JSON ラッパー ({"prompt": ..., "workflow": ...}) で包まれた ComfyUI グラフも展開。</item>
/// <item>WebM / MKV: Matroska Tags の SimpleTag (ComfyUI SaveWEBM の <c>prompt</c> JSON)。</item>
/// </list>
/// </para>
///
/// <para>NuGet 依存追加なし — PNG チャンク・JPEG マーカー・WebP RIFF を直接パースする。
/// SD WebUI infotext 形式 ("Steps: 20, Sampler: Euler a, ...") をモデル / プロンプト / ネガティブ /
/// パラメータ Dictionary に分解して返す。AI 生成データが取れなかったら null。</para>
///
/// <para>呼び出し元は URL を渡し、<see cref="ImageCacheService"/> 経由でローカル画像ファイルパスを引いて
/// 解析する。キャッシュ未ヒットの URL は null を返す (= 画像本体無しでは EXIF/PNG チャンク解析不可)。</para></summary>
public sealed class AiImageMetadataService
{
    private readonly ImageCacheService _cache;

    public AiImageMetadataService(ImageCacheService cache) { _cache = cache; }

    /// <summary>URL からキャッシュを引いて解析。
    /// キャッシュ未ヒット / 非対応形式 / 解析例外なら null。
    /// 画像 (CacheKind.Image) に無ければ動画キャッシュ (CacheKind.Video) も探す
    /// (= ComfyUI 生成動画のメタデータ抽出。スレ表示の動画スロットは DL 済み動画を Video kind で持つ)。
    /// 形式は認識できたが AI 生成データが無いファイルについては、基本情報 (format/size/dimensions) のみが入った
    /// インスタンスが返る (AI フィールドは null)。<see cref="AiImageMetadata.HasAiData"/> で判別可能。</summary>
    public Task<AiImageMetadata?> TryGetAsync(string url)
    {
        if (!_cache.TryGet(url, out var hit) && !_cache.TryGet(url, out hit, CacheKind.Video))
            return Task.FromResult<AiImageMetadata?>(null);
        var path = hit.FilePath;
        // ファイル I/O + パースをスレッドプールへ。サイズの大きい PNG (数 MB) でも UI を止めない。
        return Task.Run<AiImageMetadata?>(() => TryExtractFromFile(path));
    }

    private static AiImageMetadata? TryExtractFromFile(string path)
    {
        try
        {
            // 先頭 12 バイトのシグネチャで動画コンテナを先に判別する。動画は GB 級になり得るため
            // ReadAllBytes せず、ストリームでメタデータ格納ボックスだけを読む (viewer 実装の移植)。
            using (var fs = File.OpenRead(path))
            {
                var head = new byte[12];
                if (fs.Read(head, 0, 12) < 12) return null;

                // MP4/MOV/M4V (ISO-BMFF): [size]"ftyp"
                if (head[4] == 'f' && head[5] == 't' && head[6] == 'y' && head[7] == 'p')
                {
                    fs.Position = 0;
                    return ExtractFromMp4(fs);
                }
                // WebM/MKV (EBML): 1A 45 DF A3
                if (head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3)
                {
                    fs.Position = 0;
                    return ExtractFromWebm(fs);
                }
            }

            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 12) return null;

            // PNG: 89 50 4E 47 0D 0A 1A 0A
            if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
                return ExtractFromPng(data);

            // JPEG: FF D8 FF
            if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
                return ExtractFromJpeg(data);

            // WebP: "RIFF" .... "WEBP"
            if (data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F'
                && data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P')
                return ExtractFromWebp(data);

            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AiImageMeta] extract failed: {ex.Message}");
            return null;
        }
    }

    // -----------------------------------------------------------------
    // PNG
    // -----------------------------------------------------------------

    private static AiImageMetadata? ExtractFromPng(byte[] data)
    {
        long fileSize = data.LongLength;
        var (w, h) = GetPngDimensions(data);

        // 全 text 系チャンク (tEXt/zTXt/iTXt) を keyword → value 辞書として集める。
        // 同じ keyword が複数あった場合は最初のものを優先 (SD WebUI / Comfy / NovelAI とも 1 個書きが標準)。
        var chunks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        int i = 8;
        while (i + 12 <= data.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(i, 4));
            if (len < 0 || i + 8 + len + 4 > data.Length) break;
            string type = Encoding.ASCII.GetString(data, i + 4, 4);
            int payload = i + 8;

            string? key = null, value = null;
            if (type == "tEXt")
            {
                int nul = Array.IndexOf<byte>(data, 0, payload, len);
                if (nul > payload)
                {
                    key   = Encoding.Latin1.GetString(data, payload, nul - payload);
                    value = Encoding.Latin1.GetString(data, nul + 1, payload + len - (nul + 1));
                }
            }
            else if (type == "zTXt")
            {
                int nul = Array.IndexOf<byte>(data, 0, payload, len);
                if (nul > payload && nul + 2 <= payload + len)
                {
                    key   = Encoding.Latin1.GetString(data, payload, nul - payload);
                    int compStart = nul + 2;
                    int compLen   = payload + len - compStart;
                    value = TryInflate(data, compStart, compLen, asUtf8: false);
                }
            }
            else if (type == "iTXt")
            {
                int nul1 = Array.IndexOf<byte>(data, 0, payload, len);
                if (nul1 > payload && nul1 + 4 <= payload + len)
                {
                    key = Encoding.Latin1.GetString(data, payload, nul1 - payload);
                    byte compFlag = data[nul1 + 1];
                    int p = nul1 + 3;
                    int nul2 = Array.IndexOf<byte>(data, 0, p, payload + len - p);
                    if (nul2 >= 0)
                    {
                        int p2 = nul2 + 1;
                        int nul3 = Array.IndexOf<byte>(data, 0, p2, payload + len - p2);
                        if (nul3 >= 0)
                        {
                            int textStart = nul3 + 1;
                            int textLen   = payload + len - textStart;
                            value = compFlag != 0
                                ? TryInflate(data, textStart, textLen, asUtf8: true)
                                : Encoding.UTF8.GetString(data, textStart, textLen);
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value)
                && !chunks.ContainsKey(key))
            {
                chunks[key] = value;
            }

            i = payload + len + 4; // skip CRC
            if (type == "IEND") break;
        }

        // ---- 戦略 1: SD WebUI infotext (parameters / UserComment / Comment 内のテキスト) ----
        if (chunks.TryGetValue("parameters", out var sdParams) && IsSDWebUIInfotext(sdParams))
            return BuildResult(sdParams, "PNG", fileSize, w, h);

        foreach (var k in new[] { "UserComment", "Comment" })
        {
            if (chunks.TryGetValue(k, out var v) && IsSDWebUIInfotext(v))
                return BuildResult(v, "PNG", fileSize, w, h);
        }

        // ---- 戦略 1.5: XMP (iTXt "XML:com.adobe.xmp") 内の exif:UserComment ----
        // Affinity Photo 等の画像編集ソフトで再保存されると tEXt parameters が剥がれ、
        // infotext が XMP の exif:UserComment へ移ることがある。
        if (chunks.TryGetValue("XML:com.adobe.xmp", out var xmp))
        {
            var info = TryGetInfotextFromXmp(xmp);
            if (!string.IsNullOrEmpty(info) && IsSDWebUIInfotext(info))
                return BuildResult(info, "PNG", fileSize, w, h);
        }

        // ---- 戦略 2: ComfyUI prompt JSON (workflow グラフを辿って positive/negative を取り出す) ----
        if (chunks.TryGetValue("prompt", out var comfyPrompt))
        {
            var meta = TryParseComfyPrompt(comfyPrompt, "PNG", fileSize, w, h);
            if (meta is { HasAiData: true }) return meta;
        }

        // ---- 戦略 2.5: prompt 以外のチャンク (Comment / UserComment / Description 等) に ComfyUI グラフが
        //      「生 JSON」「{"prompt": ...} ラッパー」「Prompt: {...} プレフィックス」で入っているケース。
        //      サードパーティの保存ノード / ffmpeg 系ツールがこの書き方をする (動画側の対策と同じパターン)。
        if (!chunks.ContainsKey("prompt"))
        {
            foreach (var (ck, cv) in chunks)
            {
                if (ck.Equals("parameters", StringComparison.OrdinalIgnoreCase)) continue; // SD infotext は戦略 1 の領分
                var g = TryExtractComfyGraphJson(UnLatin1ToUtf8(cv));
                if (g is null) continue;
                var meta = TryParseComfyPrompt(g, "PNG", fileSize, w, h);
                if (meta is { HasAiData: true }) return meta;
            }
        }

        // ---- 戦略 3: NovelAI tEXt メタ (Software=NovelAI + Comment JSON / Description) ----
        if (IsNovelAiChunks(chunks))
        {
            var meta = TryParseNovelAiPngTexts(chunks, "PNG", fileSize, w, h);
            if (meta is { HasAiData: true }) return meta;
        }

        // ---- 戦略 4: alpha-LSB ステルス (tEXt が剥がされた画像でも NAI/SD WebUI 由来を救う) ----
        var stealth = TryExtractStealthPngInfo(data);
        if (!string.IsNullOrEmpty(stealth))
        {
            var meta = TryBuildFromStealthPayload(stealth, "PNG", fileSize, w, h);
            if (meta is { HasAiData: true }) return meta;
        }

        // ---- 戦略 5: AI 由来の署名は残っているが中身を解釈しきれなかった場合、
        //      ラベル (ComfyUI / SD WebUI / 生成AI) だけ確定させ、わかる範囲の生データを見せる。----
        var partial = TryBuildPartialFromPngChunks(chunks, fileSize, w, h);
        if (partial != null) return partial;

        // AI 生成として解釈できなかった場合でも、text チャンクがあれば一般メタデータとして公開する
        // (撮影・編集ソフトのコメント等。パース不能でも取得できるデータはそのまま見せる)。
        var otherPng = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in chunks)
            AddGeneralMeta(otherPng, k, UnLatin1ToUtf8(v));
        return new AiImageMetadata
        {
            Format = "PNG", FileSize = fileSize, Width = w, Height = h,
            OtherMetadata = otherPng,
        };
    }

    /// <summary>PNG text チャンクに AI 由来の署名はあるが正規パース (戦略 1〜4) を通らなかった場合の
    /// 部分結果。ツールを特定できればその名前、断片しか無ければ「生成AI」をラベルにする。
    /// 該当しなければ null (= 一般メタデータ扱い)。</summary>
    private static AiImageMetadata? TryBuildPartialFromPngChunks(
        Dictionary<string, string> chunks, long fileSize, int w, int h)
    {
        if (chunks.Count == 0) return null;

        var other = new Dictionary<string, string>(StringComparer.Ordinal);
        void DumpAllChunks()
        {
            foreach (var (k, v) in chunks) AddGeneralMeta(other, k, UnLatin1ToUtf8(v));
        }

        // ComfyUI 署名: prompt / workflow チャンク (JSON が壊れていて戦略 2 で解釈できなかったケース)。
        if (chunks.ContainsKey("prompt") || chunks.ContainsKey("workflow"))
        {
            DumpAllChunks();
            return BuildPartialAiResult("ComfyUI", other, "PNG", fileSize, w, h);
        }

        // SD WebUI 署名: parameters チャンクはあるが infotext 形式として解釈できなかったケース。
        if (chunks.ContainsKey("parameters"))
        {
            DumpAllChunks();
            return BuildPartialAiResult("SD WebUI", other, "PNG", fileSize, w, h);
        }

        // NovelAI 形の Comment JSON だけ残っているケース (Software/Source チャンクが剥がされて
        // 戦略 3 を通らなかった画像)。プロンプトを取り出せたらツール不明のまま「生成AI」を貼る。
        if (chunks.TryGetValue("Comment", out var commentRaw))
        {
            var json = UnLatin1ToUtf8(commentRaw).Trim();
            if (json.StartsWith("{", StringComparison.Ordinal) && json.Contains("\"prompt\"", StringComparison.Ordinal))
            {
                string? positive = null, negative = null;
                var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
                ParseNovelAiCommentJson(json, ref positive, ref negative, parameters);
                if (!string.IsNullOrEmpty(positive) || parameters.Count > 0)
                {
                    if (w > 0 && h > 0 && !parameters.ContainsKey("Size")) parameters["Size"] = $"{w}x{h}";
                    parameters["Generator"] = "生成AI";
                    return new AiImageMetadata
                    {
                        Format = "PNG", FileSize = fileSize, Width = w, Height = h,
                        Positive = positive, Negative = negative,
                        Generator = "生成AI", Parameters = parameters,
                    };
                }
            }
        }

        // 任意のチャンク値に既知ツール名 (Software="Made with InvokeAI" 等) か
        // infotext 断片 ("Negative prompt:" / "Steps:" 等) があればラベルを貼る。
        foreach (var v in chunks.Values)
        {
            var text = UnLatin1ToUtf8(v);
            var gen = DetectKnownGeneratorName(text)
                      ?? (LooksLikeAiInfotextFragment(text) ? "生成AI" : null);
            if (gen == null) continue;
            DumpAllChunks();
            return BuildPartialAiResult(gen, other, "PNG", fileSize, w, h);
        }

        return null;
    }

    /// <summary>生成 AI 由来なのは確実だが内容を解釈しきれなかった場合の部分結果。
    /// Generator ラベルと、わかる範囲の生データ (切り詰め済み OtherMetadata) だけを返す。</summary>
    private static AiImageMetadata BuildPartialAiResult(
        string generator, Dictionary<string, string> other,
        string format, long fileSize, int width, int height)
    {
        return new AiImageMetadata
        {
            Format = format, FileSize = fileSize, Width = width, Height = height,
            Generator  = generator,
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["Generator"] = generator },
            OtherMetadata = other,
        };
    }

    // "Forge" は "forged" 等の一般語と衝突するため入れない (Forge は Version フィールドで判定済み)。
    private static readonly string[] KnownGeneratorNames =
    {
        "ComfyUI", "NovelAI", "Stable Diffusion", "InvokeAI", "Fooocus", "SwarmUI",
        "Midjourney", "DALL-E", "DreamStudio", "Draw Things", "AUTOMATIC1111",
    };

    /// <summary>文字列中に既知の生成ツール名があればその名前を返す (ラベル用)。無ければ null。</summary>
    private static string? DetectKnownGeneratorName(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (var name in KnownGeneratorNames)
            if (text.Contains(name, StringComparison.OrdinalIgnoreCase)) return name;
        return null;
    }

    /// <summary>SD infotext の断片らしさ判定。IsSDWebUIInfotext (2 キーワード必須) より弱く、
    /// カメラ EXIF にはまず現れない語が 1 つでもあれば true。部分結果のラベル貼りにのみ使う
    /// ("Size:" や "Model:" のような一般語は誤検出しやすいので含めない)。</summary>
    private static bool LooksLikeAiInfotextFragment(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (text.Contains("Negative prompt:", StringComparison.Ordinal)) return true;
        ReadOnlySpan<string> kws = new[]
            { "Steps:", "Sampler:", "CFG scale:", "Model hash:", "Denoising strength:" };
        foreach (var k in kws)
            if (text.Contains(k, StringComparison.Ordinal)) return true;
        return false;
    }

    private static (int w, int h) GetPngDimensions(byte[] data)
    {
        // 8-byte sig + IHDR (4 length + 4 type + 4 width + 4 height + ...)
        if (data.Length < 24) return (0, 0);
        int w = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16, 4));
        int h = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20, 4));
        return (w, h);
    }

    private static string? TryInflate(byte[] src, int offset, int length, bool asUtf8)
    {
        try
        {
            using var ms       = new MemoryStream(src, offset, length, writable: false);
            using var inflater = new ZLibStream(ms, CompressionMode.Decompress);
            using var sr       = new StreamReader(inflater, asUtf8 ? Encoding.UTF8 : Encoding.Latin1);
            return sr.ReadToEnd();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AiImageMeta] inflate failed: {ex.Message}");
            return null;
        }
    }

    // -----------------------------------------------------------------
    // JPEG
    // -----------------------------------------------------------------

    private static AiImageMetadata? ExtractFromJpeg(byte[] data)
    {
        long fileSize = data.LongLength;
        var (w, h) = GetJpegDimensions(data);
        var (tiffStart, tiffEnd) = FindJpegExifBlock(data);
        return BuildFromExifBlock(data, tiffStart, tiffEnd, "JPEG", fileSize, w, h);
    }

    private static (int w, int h) GetJpegDimensions(byte[] data)
    {
        int i = 2; // skip SOI (FF D8)
        while (i + 4 <= data.Length)
        {
            // マーカは複数 0xFF パディングが許される
            while (i < data.Length && data[i] == 0xFF) i++;
            if (i >= data.Length) return (0, 0);
            byte marker = data[i++];
            if (marker == 0xD9 || marker == 0xDA) return (0, 0); // EOI / SOS
            if (marker is >= 0xD0 and <= 0xD7) continue;          // RST0..7 (no length)
            if (marker == 0x01) continue;                         // TEM
            if (i + 2 > data.Length) return (0, 0);
            int segLen = (data[i] << 8) | data[i + 1];
            // SOF: C0..CF without C4 (DHT) / C8 (JPG) / CC (DAC)
            if (marker is >= 0xC0 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                if (i + 7 > data.Length) return (0, 0);
                int h = (data[i + 3] << 8) | data[i + 4];
                int w = (data[i + 5] << 8) | data[i + 6];
                return (w, h);
            }
            i += segLen;
        }
        return (0, 0);
    }

    /// <summary>JPEG の APP1 (FF E1) "Exif\0\0" セグメントを探し、TIFF 本体の [start, end) を返す。
    /// 見つからなければ (-1, -1)。</summary>
    private static (int start, int end) FindJpegExifBlock(byte[] data)
    {
        int i = 2;
        while (i + 4 <= data.Length)
        {
            if (data[i] != 0xFF) return (-1, -1);
            while (i < data.Length && data[i] == 0xFF) i++;
            if (i >= data.Length) return (-1, -1);
            byte marker = data[i++];
            if (marker == 0xD9 || marker == 0xDA) return (-1, -1);
            if (marker is >= 0xD0 and <= 0xD7) continue;
            if (marker == 0x01) continue;
            if (i + 2 > data.Length) return (-1, -1);
            int segLen = (data[i] << 8) | data[i + 1];
            int segStart = i + 2;
            int segEnd   = Math.Min(data.Length, i + segLen);

            if (marker == 0xE1 && segStart + 6 <= segEnd
                && data[segStart] == 'E' && data[segStart + 1] == 'x'
                && data[segStart + 2] == 'i' && data[segStart + 3] == 'f'
                && data[segStart + 4] == 0   && data[segStart + 5] == 0)
            {
                return (segStart + 6, segEnd); // "Exif\0\0" の直後が TIFF ヘッダ
            }

            i += segLen;
        }
        return (-1, -1);
    }

    /// <summary>EXIF UserComment 値の "ASCII\0\0\0" / "UNICODE\0" prefix を直接 byte 検索して読む。
    /// IFD パーサ無しの簡易 fallback。SD WebUI infotext には十分。</summary>
    private static string? FindUserCommentInBuffer(byte[] data, int start, int end)
    {
        ReadOnlySpan<byte> ascii   = new byte[] { (byte)'A', (byte)'S', (byte)'C', (byte)'I', (byte)'I', 0, 0, 0 };
        ReadOnlySpan<byte> unicode = new byte[] { (byte)'U', (byte)'N', (byte)'I', (byte)'C', (byte)'O', (byte)'D', (byte)'E', 0 };

        var span = data.AsSpan(start, end - start);

        int pos = span.IndexOf(ascii);
        if (pos >= 0)
        {
            int dataStart = start + pos + ascii.Length;
            int max       = Math.Min(end, dataStart + 65536);
            int p = dataStart;
            while (p < max && data[p] != 0) p++;
            if (p > dataStart) return Encoding.UTF8.GetString(data, dataStart, p - dataStart);
        }

        pos = span.IndexOf(unicode);
        if (pos >= 0)
        {
            int dataStart = start + pos + unicode.Length;
            int max       = Math.Min(end, dataStart + 131072);
            // EXIF TIFF のエンディアンを正規に取らずに LE / BE 両方を試して印字可能率の高い方を採る。
            // 多くの撮影機器は LE。ChBrowser の対象 (AI 画像) も LE が多数派。
            int leLen = ScanUtf16Length(data, dataStart, max, littleEndian: true);
            string leStr = DecodeUtf16(data, dataStart, leLen, littleEndian: true);
            int beLen = ScanUtf16Length(data, dataStart, max, littleEndian: false);
            string beStr = DecodeUtf16(data, dataStart, beLen, littleEndian: false);
            return PrintableScore(leStr) >= PrintableScore(beStr) ? leStr : beStr;
        }

        return null;
    }

    private static int ScanUtf16Length(byte[] data, int start, int max, bool littleEndian)
    {
        int p = start;
        while (p + 1 < max)
        {
            byte hi = littleEndian ? data[p + 1] : data[p];
            byte lo = littleEndian ? data[p]     : data[p + 1];
            if (hi == 0 && lo == 0) break;
            p += 2;
        }
        return p - start;
    }

    private static string DecodeUtf16(byte[] data, int start, int len, bool littleEndian)
    {
        if (len <= 0) return "";
        var enc = littleEndian ? Encoding.Unicode : Encoding.BigEndianUnicode;
        return enc.GetString(data, start, len & ~1);
    }

    /// <summary>SD WebUI infotext は必ずパラメータ行 ("Steps:", "Sampler:" 等) に ASCII を含むので、
    /// 「ASCII 比率」を見れば BE/LE 判別がつく:
    /// 例 BE で "M a s t e r" → 00 4D 00 61 ... → BE decode は ASCII 'M', 'a' 等。LE decode は U+4D00, U+6100 等の CJK で ASCII 0%。
    /// 単純な "0x20+ printable" だと CJK (= U+3000 以降) も printable 扱いになり誤検出するため、
    /// ASCII 範囲 (= 0x20-0x7E) を厳密にカウントして比率を返す。</summary>
    private static double PrintableScore(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int ascii = 0;
        foreach (var c in s)
        {
            if (c == '\t' || c == '\n' || c == '\r') { ascii++; continue; }
            if (c >= 0x20 && c <= 0x7E) ascii++;
        }
        return (double)ascii / s.Length;
    }

    // -----------------------------------------------------------------
    // WebP
    // -----------------------------------------------------------------

    private static AiImageMetadata? ExtractFromWebp(byte[] data)
    {
        long fileSize = data.LongLength;
        var (w, h) = GetWebpDimensions(data);
        var (tiffStart, tiffEnd) = FindWebpExifBlock(data);
        var meta = BuildFromExifBlock(data, tiffStart, tiffEnd, "WEBP", fileSize, w, h);
        if (meta.HasAiData) return meta;

        // EXIF から拾えなければ "XMP " チャンクを見る (PNG の戦略 1.5 と同じ Affinity Photo 等の再保存ケース)。
        var xmp = FindWebpXmpText(data);
        if (xmp is not null)
        {
            var info = TryGetInfotextFromXmp(xmp);
            if (!string.IsNullOrEmpty(info) && IsSDWebUIInfotext(info))
                return BuildResult(info, "WEBP", fileSize, w, h);
            // AI として解釈できない XMP も一般メタデータとして見せる (切り詰めあり)。
            AddGeneralMeta(meta.OtherMetadata, "XMP", xmp);
            // XMP に既知ツール名 (xmp:CreatorTool 等) や infotext 断片が残っていれば
            // ラベル付きの部分結果に格上げする。
            var gen = DetectKnownGeneratorName(xmp)
                      ?? (LooksLikeAiInfotextFragment(xmp) ? "生成AI" : null);
            if (gen != null)
                return BuildPartialAiResult(gen, meta.OtherMetadata, "WEBP", fileSize, w, h);
        }
        return meta;
    }

    /// <summary>WebP RIFF の "XMP " チャンク (XMP XML) を UTF-8 文字列で返す。無ければ null。</summary>
    private static string? FindWebpXmpText(byte[] data)
    {
        int i = 12;
        while (i + 8 <= data.Length)
        {
            string fourcc = Encoding.ASCII.GetString(data, i, 4);
            int size      = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i + 4, 4));
            int payload   = i + 8;
            if (payload + size > data.Length) break;
            if (fourcc == "XMP ") return Encoding.UTF8.GetString(data, payload, size);
            i = payload + size + (size & 1);
        }
        return null;
    }

    private static (int w, int h) GetWebpDimensions(byte[] data)
    {
        if (data.Length < 30) return (0, 0);
        int i = 12; // "RIFF<size>WEBP" まで飛ばす
        while (i + 8 <= data.Length)
        {
            string fourcc = Encoding.ASCII.GetString(data, i, 4);
            int size      = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i + 4, 4));
            int payload   = i + 8;
            if (payload + size > data.Length) break;

            if (fourcc == "VP8X" && size >= 10)
            {
                // flags(1) reserved(3) (W-1) 24bit LE (H-1) 24bit LE
                int wMinus1 = data[payload + 4] | (data[payload + 5] << 8) | (data[payload + 6] << 16);
                int hMinus1 = data[payload + 7] | (data[payload + 8] << 8) | (data[payload + 9] << 16);
                return (wMinus1 + 1, hMinus1 + 1);
            }
            if (fourcc == "VP8 " && size >= 10)
            {
                // 3 bytes frame tag, 3 bytes "9D 01 2A", then W (14b LE) | scale, H (14b LE) | scale
                int p = payload + 6;
                if (p + 4 <= data.Length)
                {
                    int w14 = (data[p] | (data[p + 1] << 8)) & 0x3FFF;
                    int h14 = (data[p + 2] | (data[p + 3] << 8)) & 0x3FFF;
                    return (w14, h14);
                }
            }
            if (fourcc == "VP8L" && size >= 5 && data[payload] == 0x2F)
            {
                int p = payload + 1;
                if (p + 4 <= data.Length)
                {
                    uint val = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p, 4));
                    int w14 = (int)((val & 0x3FFF) + 1);
                    int h14 = (int)(((val >> 14) & 0x3FFF) + 1);
                    return (w14, h14);
                }
            }

            // RIFF chunk は奇数サイズだと 1 byte パディング
            i = payload + size + (size & 1);
        }
        return (0, 0);
    }

    /// <summary>WebP RIFF の "EXIF" チャンクを探し、TIFF 本体の [start, end) を返す。
    /// 見つからなければ (-1, -1)。一部エンコーダは EXIF チャンク先頭に "Exif\0\0" を付けるので吸収する。</summary>
    private static (int start, int end) FindWebpExifBlock(byte[] data)
    {
        int i = 12;
        while (i + 8 <= data.Length)
        {
            string fourcc = Encoding.ASCII.GetString(data, i, 4);
            int size      = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i + 4, 4));
            int payload   = i + 8;
            if (payload + size > data.Length) break;
            if (fourcc == "EXIF")
            {
                int ts = payload;
                if (size >= 6 && data[payload] == 'E' && data[payload + 1] == 'x'
                    && data[payload + 2] == 'i' && data[payload + 3] == 'f'
                    && data[payload + 4] == 0   && data[payload + 5] == 0)
                    ts = payload + 6;
                return (ts, payload + size);
            }
            i = payload + size + (size & 1);
        }
        return (-1, -1);
    }

    // -----------------------------------------------------------------
    // XMP (PNG iTXt "XML:com.adobe.xmp" / WebP "XMP " チャンク 共通)
    // -----------------------------------------------------------------

    /// <summary>XMP XML から exif:UserComment の本文を取り出して XML エンティティを復号する。
    /// Affinity Photo 2 等は再保存時に SD WebUI infotext を exif:UserComment として XMP へ書き込む。
    /// 要素形式 (&lt;exif:UserComment&gt;&lt;rdf:Alt&gt;&lt;rdf:li&gt;...) と属性形式 (exif:UserComment="...") の両方を見る。</summary>
    private static string? TryGetInfotextFromXmp(string xmp)
    {
        var m = Regex.Match(xmp, @"<exif:UserComment>.*?<rdf:li[^>]*>(.*?)</rdf:li>", RegexOptions.Singleline);
        if (!m.Success)
            m = Regex.Match(xmp, @"exif:UserComment\s*=\s*""([^""]*)""", RegexOptions.Singleline);
        if (!m.Success) return null;

        var s = m.Groups[1].Value;
        if (string.IsNullOrWhiteSpace(s)) return null;
        // &lt; / &amp; / &#xA; 等の XML エンティティを復号 (XML エンティティは HTML エンティティの部分集合)。
        return System.Net.WebUtility.HtmlDecode(s);
    }

    // -----------------------------------------------------------------
    // EXIF (TIFF) → メタデータ 共通経路 (JPEG / WebP)
    //
    // SD WebUI は EXIF UserComment (charset-prefix 付き) に infotext を入れる。
    // ComfyUI は WebP/JPEG 保存時に EXIF ASCII タグへ JSON を入れる:
    //   ImageDescription (0x010e) = "Workflow: {UI グラフ JSON}"  (nodes 配列形式 / パース対象外)
    //   Make             (0x010f) = "Prompt: {API グラフ JSON}"   (node-id キー形式 / TryParseComfyPrompt 対象)
    // タグ値は仕様上 ASCII だが ComfyUI は UTF-8 バイトをそのまま書くため UTF-8 で復号する。
    // -----------------------------------------------------------------

    private static AiImageMetadata BuildFromExifBlock(
        byte[] data, int tiffStart, int tiffEnd, string format, long fileSize, int width, int height)
    {
        if (tiffStart >= 0 && tiffEnd > tiffStart)
        {
            var tags = ParseExifAsciiStringTags(data, tiffStart, tiffEnd);

            // 1) いずれかの ASCII タグに SD WebUI infotext がある (一部エンコーダは ImageDescription に書く)。
            foreach (var v in tags.Values)
                if (IsSDWebUIInfotext(v))
                    return BuildResult(v, format, fileSize, width, height);

            // 2) ComfyUI: ImageDescription="Workflow: ..." / Make="Prompt: ..." の JSON を辿る。
            var comfy = TryComfyFromExifTags(tags, format, fileSize, width, height);
            if (comfy is { HasAiData: true }) return comfy;

            // 3) 従来経路: EXIF UserComment (ASCII/UNICODE prefix) を byte-scan で拾う。
            //    SD 形式でない UserComment はここでは返さず、下の一般メタデータへ回す
            //    (従来は BuildResult に渡していたが、どうせ基本情報しか返らなかった)。
            var uc = FindUserCommentInBuffer(data, tiffStart, tiffEnd);
            if (!string.IsNullOrEmpty(uc) && IsSDWebUIInfotext(uc!))
                return BuildResult(uc, format, fileSize, width, height);

            // 3.5) UserComment に ComfyUI グラフ (生 JSON / {"prompt": ...} ラッパー) が入っているケース。
            if (!string.IsNullOrEmpty(uc))
            {
                var g = TryExtractComfyGraphJson(uc);
                if (g is not null)
                {
                    var meta = TryParseComfyPrompt(g, format, fileSize, width, height);
                    if (meta is { HasAiData: true }) return meta;
                }
            }

            // 4) AI 生成として解釈できなかったが EXIF 自体はある。
            //    まず AI 由来の署名 (ComfyUI プレフィックス / 既知ツール名 / infotext 断片) を探し、
            //    見つかればラベル付きの部分結果、無ければ撮影機材等の一般メタデータとして公開する。
            var other = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (tag, val) in tags)
                AddGeneralMeta(other, ExifTagName(tag), val);
            if (!string.IsNullOrEmpty(uc))
                AddGeneralMeta(other, "UserComment", uc!);

            string? partialGen = null;
            foreach (var val in tags.Values)
            {
                // ComfyUI 署名: "Prompt: {...}" / "Workflow: {...}" はあるが JSON が解釈できなかったケース。
                if (val.StartsWith("Prompt:", StringComparison.Ordinal)
                    || val.StartsWith("Workflow:", StringComparison.Ordinal))
                {
                    partialGen = "ComfyUI";
                    break;
                }
            }
            if (partialGen == null)
            {
                foreach (var val in tags.Values)
                {
                    partialGen = DetectKnownGeneratorName(val)
                                 ?? (LooksLikeAiInfotextFragment(val) ? "生成AI" : null);
                    if (partialGen != null) break;
                }
            }
            if (partialGen == null && !string.IsNullOrEmpty(uc))
                partialGen = DetectKnownGeneratorName(uc!)
                             ?? (LooksLikeAiInfotextFragment(uc!) ? "生成AI" : null);

            if (partialGen != null)
                return BuildPartialAiResult(partialGen, other, format, fileSize, width, height);

            return new AiImageMetadata
            {
                Format = format, FileSize = fileSize, Width = width, Height = height,
                OtherMetadata = other,
            };
        }

        // 何も拾えなければ基本情報のみ。
        return new AiImageMetadata { Format = format, FileSize = fileSize, Width = width, Height = height };
    }

    /// <summary>一般メタデータの表示名。IFD0 でよく使われる ASCII タグのみ命名し、他は 16 進表記。</summary>
    private static string ExifTagName(int tag) => tag switch
    {
        0x010e => "ImageDescription",
        0x010f => "Make",
        0x0110 => "Model",
        0x0131 => "Software",
        0x0132 => "DateTime",
        0x013b => "Artist",
        0x8298 => "Copyright",
        _      => $"Tag 0x{tag:X4}",
    };

    private static void AddGeneralMeta(Dictionary<string, string> dict, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || dict.ContainsKey(key)) return;
        dict[key] = value.Trim();
    }

    /// <summary>TIFF IFD0 を走査し、ASCII (type=2) タグを tag → 文字列の辞書で返す。
    /// オフセットはすべて tiffStart 基準。エンディアン (II/MM) を解釈する簡易パーサ。</summary>
    private static Dictionary<int, string> ParseExifAsciiStringTags(byte[] data, int tiffStart, int tiffEnd)
    {
        var result = new Dictionary<int, string>();
        try
        {
            if (tiffStart + 8 > tiffEnd) return result;
            bool le;
            if (data[tiffStart] == 'I' && data[tiffStart + 1] == 'I') le = true;
            else if (data[tiffStart] == 'M' && data[tiffStart + 1] == 'M') le = false;
            else return result;

            uint ReadU16(int o) => le
                ? (uint)(data[o] | (data[o + 1] << 8))
                : (uint)((data[o] << 8) | data[o + 1]);
            uint ReadU32(int o) => le
                ? (uint)(data[o] | (data[o + 1] << 8) | (data[o + 2] << 16) | (data[o + 3] << 24))
                : (uint)((data[o] << 24) | (data[o + 1] << 16) | (data[o + 2] << 8) | data[o + 3]);

            int ifd = tiffStart + (int)ReadU32(tiffStart + 4);
            if (ifd + 2 > tiffEnd || ifd < tiffStart) return result;
            int n = (int)ReadU16(ifd);
            int p = ifd + 2;
            for (int k = 0; k < n && p + 12 <= tiffEnd; k++, p += 12)
            {
                int tag   = (int)ReadU16(p);
                int type  = (int)ReadU16(p + 2);
                int count = (int)ReadU32(p + 4);
                if (type != 2 || count <= 0) continue; // ASCII のみ

                int valStart = count <= 4 ? p + 8 : tiffStart + (int)ReadU32(p + 8);
                if (valStart < tiffStart || valStart + count > tiffEnd) continue;

                int len = count;
                while (len > 0 && data[valStart + len - 1] == 0) len--; // 末尾 NUL を除去
                if (len <= 0) continue;

                var s = Encoding.UTF8.GetString(data, valStart, len);
                if (!result.ContainsKey(tag)) result[tag] = s;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AiImageMeta] exif ascii tag parse failed: {ex.Message}");
        }
        return result;
    }

    /// <summary>ComfyUI が EXIF ASCII タグに書く "Prompt: {...}" / "Workflow: {...}" から API グラフ JSON を
    /// 取り出して <see cref="TryParseComfyPrompt"/> に渡す。Make ("Prompt:") を優先。</summary>
    private static AiImageMetadata? TryComfyFromExifTags(
        Dictionary<int, string> tags, string format, long fileSize, int width, int height)
    {
        foreach (var tag in new[] { 0x010f, 0x010e }) // Make → ImageDescription の順
        {
            if (!tags.TryGetValue(tag, out var raw) || string.IsNullOrEmpty(raw)) continue;
            int brace = raw.IndexOf('{');
            if (brace < 0) continue;
            var json = raw.Substring(brace);
            var meta = TryParseComfyPrompt(json, format, fileSize, width, height);
            if (meta is { HasAiData: true }) return meta;
        }

        // その他の ASCII タグも走査: サードパーティの保存ノードは XPComment / Software 等の別タグに
        // 生グラフ JSON や {"prompt": ...} ラッパーで書くことがある (動画側の対策と同じパターン)。
        foreach (var raw in tags.Values)
        {
            var g = TryExtractComfyGraphJson(raw);
            if (g is null) continue;
            var meta = TryParseComfyPrompt(g, format, fileSize, width, height);
            if (meta is { HasAiData: true }) return meta;
        }
        return null;
    }

    // -----------------------------------------------------------------
    // ComfyUI prompt JSON パース (workflow グラフを辿る)
    //
    // ComfyUI は PNG の tEXt チャンク (key="prompt") に「API workflow」と呼ばれる JSON を埋め込む。
    // 形式: { "<nodeId>": { "class_type": "...", "inputs": { ... } }, ... }
    //
    // 主要 class_type:
    //   - KSampler / KSamplerAdvanced / SamplerCustom            ← positive / negative input を直接持つ
    //   - SamplerCustomAdvanced                                  ← guider 経由 (BasicGuider/CFGGuider)
    //   - CLIPTextEncode / CLIPTextEncodeSDXL / Flux Text Encode ← positive/negative の終端 (text 入力)
    //   - CheckpointLoaderSimple / CheckpointLoader              ← model
    //   - EmptyLatentImage / EmptySD3LatentImage                 ← width/height
    //
    // input の値は 「リテラル (string/number/bool)」 か 「[refNodeId, outIdx] の配列参照」のどちらか。
    // 配列参照なら refNodeId のノードを再帰的に辿って終端のテキストを取り出す。
    // 循環や非常に深いグラフへの保険として depth は 8 で打ち切る。
    // -----------------------------------------------------------------

    private static AiImageMetadata? TryParseComfyPrompt(string json, string format, long fileSize, int width, int height)
    {
        try
        {
            // ComfyUI の prompt JSON はカスタムノード由来で NaN / Infinity という非標準リテラルを
            // 含むことがある (DPRandomGenerator の "is_changed": [NaN] 等)。System.Text.Json は
            // これを不正 JSON として拒否するため、文字列外のものだけ null に置換してから読む。
            using var doc  = System.Text.Json.JsonDocument.Parse(SanitizeJsonNonStandardLiterals(json));
            var       root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

            // sampler ノードを 1 つ選ぶ (= 最初に見つかった "Sampler" 含む class_type)。
            // モデル / latent 寸法は別途グラフ全体から拾う。
            // sampler が無いワークフロー (画像加工のみ等) もあるので、class_type を 1 つでも持っていれば
            // ComfyUI API グラフとみなし、取れる情報だけ (Generator / Model / Source image 等) を返す。
            bool isComfyGraph = false;
            System.Text.Json.JsonElement? samplerInputs = null;
            string? model    = null;
            string? srcImage = null;
            int?    latW     = null;
            int?    latH     = null;
            // text encoder (CLIP) / VAE はローダが複数あり得る (Dual/TripleCLIPLoader、動画+音声 VAE 等) ので
            // 全部集めて ", " 連結で出す。出現順を保ちつつ重複は除く。
            List<string>? textEncoders = null;
            List<string>? vaes         = null;

            foreach (var prop in root.EnumerateObject())
            {
                var node = prop.Value;
                if (node.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if (!node.TryGetProperty("class_type", out var ctElem)) continue;
                var ct = ctElem.GetString() ?? "";
                isComfyGraph = true;

                // sampler を 1 つ見つけたら hold (= 後段で positive/negative を辿る)。
                // KSamplerSelect (= sampler_name を持つだけの選択ノード) のような「辿る入力を持たない」
                // sampler を先に掴むと本命 (SamplerCustomAdvanced 等) を見逃すため、
                // positive / negative / guider のいずれかを持つ sampler ノードを優先して差し替える。
                if (ct.Contains("Sampler", StringComparison.OrdinalIgnoreCase)
                    && node.TryGetProperty("inputs", out var ipSampler))
                {
                    var hasTraceable = ipSampler.TryGetProperty("positive", out _)
                                    || ipSampler.TryGetProperty("negative", out _)
                                    || ipSampler.TryGetProperty("guider",   out _);
                    if (samplerInputs is null) samplerInputs = ipSampler;
                    else if (hasTraceable
                             && samplerInputs is { } cur
                             && !cur.TryGetProperty("positive", out _)
                             && !cur.TryGetProperty("negative", out _)
                             && !cur.TryGetProperty("guider",   out _))
                    {
                        samplerInputs = ipSampler; // 辿れない sampler を掴んでいたら本命に差し替え
                    }
                }

                // model: ローダ系ノードから取得。
                //   - CheckpointLoaderSimple / CheckpointLoader → ckpt_name
                //   - UNETLoader (Flux 等)                       → unet_name
                //   - DiffusionModelLoader / 一般 ModelLoader    → model_name
                // class_type の正確な名前を網羅すると custom node に追従できないので、
                // 「ロード系の名前 + 既知の入力名」の組合せで判定する。
                if (model is null
                    && (ct.StartsWith("Checkpoint",    StringComparison.OrdinalIgnoreCase)
                        || ct.Contains("UNETLoader",   StringComparison.OrdinalIgnoreCase)
                        || ct.Contains("UnetLoader",   StringComparison.OrdinalIgnoreCase)
                        || ct.Contains("ModelLoader",  StringComparison.OrdinalIgnoreCase))
                    && node.TryGetProperty("inputs", out var ip2))
                {
                    foreach (var fieldName in new[] { "ckpt_name", "unet_name", "model_name" })
                    {
                        if (ip2.TryGetProperty(fieldName, out var nv)
                            && nv.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            model = nv.GetString();
                            break;
                        }
                    }
                }

                // latent サイズ: EmptyLatentImage / EmptySD3LatentImage 等
                if (latW is null
                    && (ct.Contains("EmptyLatent", StringComparison.OrdinalIgnoreCase)
                        || ct.Contains("EmptySD3Latent", StringComparison.OrdinalIgnoreCase)
                        || ct.Contains("LatentImage", StringComparison.OrdinalIgnoreCase)))
                {
                    if (node.TryGetProperty("inputs", out var ip)
                        && ip.TryGetProperty("width", out var wEl) && wEl.ValueKind == System.Text.Json.JsonValueKind.Number
                        && ip.TryGetProperty("height", out var hEl) && hEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        latW = wEl.GetInt32();
                        latH = hEl.GetInt32();
                    }
                }

                // 入力画像 (img2img / 画像加工ワークフロー): LoadImage / "D2 Load Image" 等の image 入力 (= ファイル名リテラル)。
                if (srcImage is null
                    && ct.Replace(" ", "").Contains("LoadImage", StringComparison.OrdinalIgnoreCase)
                    && node.TryGetProperty("inputs", out var ip3)
                    && ip3.TryGetProperty("image", out var imgEl)
                    && imgEl.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    srcImage = imgEl.GetString();
                }

                // text encoder (CLIP) ローダ: CLIPLoader / DualCLIPLoader / TripleCLIPLoader (+GGUF 派生等)。
                // 入力名は clip_name (単発) / clip_name1..4 (複数) を網羅する。
                if (ct.Contains("CLIPLoader", StringComparison.OrdinalIgnoreCase)
                    && node.TryGetProperty("inputs", out var ipClip))
                {
                    foreach (var fieldName in new[] { "clip_name", "clip_name1", "clip_name2", "clip_name3", "clip_name4" })
                    {
                        if (ipClip.TryGetProperty(fieldName, out var cv)
                            && cv.ValueKind == System.Text.Json.JsonValueKind.String
                            && cv.GetString() is { Length: > 0 } cname)
                        {
                            textEncoders ??= new List<string>();
                            if (!textEncoders.Contains(cname)) textEncoders.Add(cname);
                        }
                    }
                }

                // VAE ローダ: VAELoader (+派生)。動画ワークフローでは映像用 + 音声用の 2 つが載ることがある。
                if (ct.Contains("VAELoader", StringComparison.OrdinalIgnoreCase)
                    && node.TryGetProperty("inputs", out var ipVae)
                    && ipVae.TryGetProperty("vae_name", out var vv)
                    && vv.ValueKind == System.Text.Json.JsonValueKind.String
                    && vv.GetString() is { Length: > 0 } vname)
                {
                    vaes ??= new List<string>();
                    if (!vaes.Contains(vname)) vaes.Add(vname);
                }
            }

            if (!isComfyGraph) return null;

            string? positive = null;
            string? negative = null;
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);

            if (samplerInputs is { } sIn)
            {
                // positive / negative。SamplerCustomAdvanced は guider 経由なので fallback で辿る。
                positive = ResolveTextRef(sIn, "positive", root, depth: 0);
                negative = ResolveTextRef(sIn, "negative", root, depth: 0);

                if ((positive is null || negative is null)
                    && sIn.TryGetProperty("guider", out var gRef)
                    && gRef.ValueKind == System.Text.Json.JsonValueKind.Array
                    && gRef.GetArrayLength() >= 1
                    && gRef[0].ValueKind == System.Text.Json.JsonValueKind.String
                    && root.TryGetProperty(gRef[0].GetString()!, out var guiderNode)
                    && guiderNode.TryGetProperty("inputs", out var gIn))
                {
                    positive ??= ResolveTextRef(gIn, "positive",     root, depth: 0)
                              ?? ResolveTextRef(gIn, "conditioning", root, depth: 0);
                    negative ??= ResolveTextRef(gIn, "negative",     root, depth: 0);
                }

                // パラメータ収集 (sampler の入力)。
                // 値が配列参照 (= 他ノードのウィジェット値: rgthree Seed/Config 等) の場合は 1 ホップ辿ってリテラル化する。
                CopyComfyParam(sIn, "steps",         parameters, "Steps",     root);
                CopyComfyParam(sIn, "cfg",           parameters, "CFG scale", root);
                CopyComfyParam(sIn, "sampler_name",  parameters, "Sampler",   root);
                CopyComfyParam(sIn, "scheduler",     parameters, "Scheduler", root);
                CopyComfyParam(sIn, "seed",          parameters, "Seed",      root);
                CopyComfyParam(sIn, "noise_seed",    parameters, "Seed",      root); // KSamplerAdvanced 系
                CopyComfyParam(sIn, "denoise",       parameters, "Denoise",   root);
            }

            // sampler が positive/negative を直接持たない (Impact BasicPipe / rgthree パイプ系) 場合の保険。
            // グラフ全体から positive と negative の両入力を持つノード (ToBasicPipe 等) を探して辿る。
            if (string.IsNullOrEmpty(positive) || string.IsNullOrEmpty(negative))
            {
                foreach (var prop in root.EnumerateObject())
                {
                    var node = prop.Value;
                    if (node.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    if (!node.TryGetProperty("inputs", out var ip)) continue;
                    if (!ip.TryGetProperty("positive", out _) || !ip.TryGetProperty("negative", out _)) continue;

                    if (string.IsNullOrEmpty(positive)) positive = ResolveTextRef(ip, "positive", root, depth: 0);
                    if (string.IsNullOrEmpty(negative)) negative = ResolveTextRef(ip, "negative", root, depth: 0);
                    if (!string.IsNullOrEmpty(positive) && !string.IsNullOrEmpty(negative)) break;
                }
            }

            if (latW is int lw && latH is int lh)  parameters["Size"] = $"{lw}x{lh}";
            if (!string.IsNullOrEmpty(model))      parameters["Model"] = model!;
            if (textEncoders is { Count: > 0 })    parameters["Text encoder"] = string.Join(", ", textEncoders);
            if (vaes is { Count: > 0 })            parameters["VAE"] = string.Join(", ", vaes);
            if (!string.IsNullOrEmpty(srcImage))   parameters["Source image"] = srcImage!;
            // 参考 viewer に合わせて Generator を埋める。ComfyUI 由来 (= prompt JSON が valid だった) のは確定。
            parameters["Generator"] = "ComfyUI";

            return new AiImageMetadata
            {
                Format      = format,
                FileSize    = fileSize,
                Width       = width,
                Height      = height,
                Model       = model,
                Positive    = positive,
                Negative    = negative,
                Generator   = "ComfyUI",
                // ComfyUI 生 JSON は数十KBになりやすいので RawInfotext には載せない (= 詳細ペインの infotext 全文表示は SD WebUI 由来時のみ)。
                RawInfotext = null,
                Parameters  = parameters,
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AiImageMeta] Comfy parse failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>JSON 文字列リテラルの外に現れる NaN / Infinity / -Infinity を null へ置換する。
    /// プロンプト本文 ("NaN" を含む文字列) は inString 追跡で保護される。該当トークンが無ければ元の文字列を返す。</summary>
    private static string SanitizeJsonNonStandardLiterals(string json)
    {
        if (json.IndexOf("NaN", StringComparison.Ordinal) < 0
            && json.IndexOf("Infinity", StringComparison.Ordinal) < 0) return json;

        var sb = new StringBuilder(json.Length);
        bool inString = false;
        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < json.Length) { sb.Append(json[++i]); continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; sb.Append(c); continue; }

            if (c == 'N' && IsBareToken(json, i, "NaN"))      { sb.Append("null"); i += 2; continue; }
            if (c == 'I' && IsBareToken(json, i, "Infinity")) { sb.Append("null"); i += 7; continue; }
            if (c == '-' && i + 1 < json.Length && IsBareToken(json, i + 1, "Infinity"))
            {
                sb.Append("null");
                i += 8;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool IsBareToken(string s, int i, string token)
    {
        if (i + token.Length > s.Length) return false;
        if (string.CompareOrdinal(s, i, token, 0, token.Length) != 0) return false;
        int after = i + token.Length;
        return after >= s.Length || !(char.IsLetterOrDigit(s[after]) || s[after] == '_');
    }

    /// <summary>ComfyUI workflow ノードの input から指定フィールドの値を解決して文字列にする。
    /// リテラル文字列ならそのまま、配列参照 (= [refNodeId, outIdx]) なら参照先ノードの text 系フィールドを再帰的に辿る。</summary>
    private static string? ResolveTextRef(System.Text.Json.JsonElement inputs, string field,
                                          System.Text.Json.JsonElement root, int depth)
    {
        if (depth > 8) return null;
        if (!inputs.TryGetProperty(field, out var v)) return null;

        if (v.ValueKind == System.Text.Json.JsonValueKind.String) return v.GetString();
        if (v.ValueKind == System.Text.Json.JsonValueKind.Array && v.GetArrayLength() >= 1
            && v[0].ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var refId = v[0].GetString()!;
            if (root.TryGetProperty(refId, out var refNode))
                return ExtractTextFromComfyNode(refNode, root, depth + 1);
        }
        return null;
    }

    /// <summary>ComfyUI ノードからプロンプト文字列を取り出す。
    /// CLIPTextEncode のような text 系フィールドを保有していればそれ、無ければ conditioning 系の参照を辿る。</summary>
    private static string? ExtractTextFromComfyNode(System.Text.Json.JsonElement node,
                                                     System.Text.Json.JsonElement root, int depth)
    {
        if (depth > 8) return null;
        if (!node.TryGetProperty("inputs", out var inputs)) return null;

        // 直接の text 系フィールド (CLIPTextEncode は "text"、SDXL は text_g/text_l、Flux は clip_l/t5xxl)。
        // text が他ノードへの参照になっている場合に備え、文字列を保持/受け渡しする中継ノードのフィールドも辿る:
        //   - PrimitiveString / PrimitiveStringMultiline / String (rgthree 等) は "value"
        //   - RegexReplace / 文字列加工ノードは "string"
        //   - PreviewAny / Display 系のパススルーノードは "source"
        // これらを終端まで辿らないと、CLIPTextEncode.text が primitive ノード参照のときにプロンプトが空になる。
        // text 系を優先し value/string/source は後置 (= 本物の text があればそちらを採用)。
        // 重複は除き改行で連結する。
        var texts = new List<string>();
        foreach (var fieldName in new[] { "text", "text_g", "text_l", "clip_l", "t5xxl", "prompt", "value", "string", "source" })
        {
            if (!inputs.TryGetProperty(fieldName, out var p)) continue;
            if (p.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var s = p.GetString();
                if (!string.IsNullOrEmpty(s) && !texts.Contains(s)) texts.Add(s);
            }
            else if (p.ValueKind == System.Text.Json.JsonValueKind.Array && p.GetArrayLength() >= 1
                     && p[0].ValueKind == System.Text.Json.JsonValueKind.String
                     && root.TryGetProperty(p[0].GetString()!, out var refNode))
            {
                var s = ExtractTextFromComfyNode(refNode, root, depth + 1);
                if (!string.IsNullOrEmpty(s) && !texts.Contains(s)) texts.Add(s);
            }
        }
        if (texts.Count > 0) return string.Join("\n", texts);

        // 既知フィールドで取れない場合の汎用フォールバック: フィールド名に "text" / "prompt" / "string" を
        // 含む入力をテキスト候補として扱う (カスタムノードの editable_text_widget / populated_text /
        // wildcard_text や、JoinStrings の string1 / string2 のような連番フィールドに対応。
        // 名前が非標準でも意味はフィールド名に現れることが多い)。
        //   - "negative" を名前に含むものは除外 (= positive 追跡経路への negative 汚染防止。負側は専用経路で辿る)
        //   - "system" を名前に含むものは除外 (= LLM への system_prompt は画像プロンプトそのものではない)
        //   - モデルファイル名っぽい値 (.safetensors 等) は除外
        foreach (var prop in inputs.EnumerateObject())
        {
            var nm = prop.Name.ToLowerInvariant();
            if (!(nm.Contains("text") || nm.Contains("prompt") || nm.Contains("string"))) continue;
            if (nm.Contains("negative") || nm.Contains("system")) continue;
            var p = prop.Value;
            if (p.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var s = p.GetString();
                if (!string.IsNullOrEmpty(s) && !LooksLikeAssetFileName(s!) && !texts.Contains(s!)) texts.Add(s!);
            }
            else if (p.ValueKind == System.Text.Json.JsonValueKind.Array && p.GetArrayLength() >= 1
                     && p[0].ValueKind == System.Text.Json.JsonValueKind.String
                     && root.TryGetProperty(p[0].GetString()!, out var refNode2))
            {
                var s = ExtractTextFromComfyNode(refNode2, root, depth + 1);
                if (!string.IsNullOrEmpty(s) && !texts.Contains(s!)) texts.Add(s!);
            }
        }
        if (texts.Count > 0) return string.Join("\n", texts);

        // スイッチノード (ComfySwitchNode 等: on_true / on_false + switch 入力): 実行された枝へ中継する。
        // switch の真偽はリテラル or 参照 (PrimitiveBoolean 等) を辿って解決し、判定できた場合はその枝のみ、
        // 判定不能なら on_false → on_true の順に両方試す (先に取れた方)。
        // これが無いと BasicGuider → switch → MiniMax 系のようなグラフでプロンプト追跡が switch で途切れる。
        if (inputs.TryGetProperty("on_true", out _) || inputs.TryGetProperty("on_false", out _))
        {
            var sw = ResolveComfySwitchState(inputs, root);
            var branches = sw switch
            {
                true  => new[] { "on_true" },
                false => new[] { "on_false" },
                null  => new[] { "on_false", "on_true" },
            };
            foreach (var f in branches)
            {
                if (!inputs.TryGetProperty(f, out var bv)) continue;
                if (bv.ValueKind != System.Text.Json.JsonValueKind.Array || bv.GetArrayLength() < 1) continue;
                if (bv[0].ValueKind != System.Text.Json.JsonValueKind.String) continue;
                if (!root.TryGetProperty(bv[0].GetString()!, out var branchNode)) continue;
                var s = ExtractTextFromComfyNode(branchNode, root, depth + 1);
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }

        // ConditioningCombine / ConditioningConcat 等は conditioning_1 / conditioning_2 / from / to を持つので合成する。
        var combined = new List<string>();
        foreach (var prop in inputs.EnumerateObject())
        {
            var nm = prop.Name.ToLowerInvariant();
            if (!(nm.StartsWith("conditioning") || nm == "from" || nm == "to")) continue;
            if (prop.Value.ValueKind != System.Text.Json.JsonValueKind.Array || prop.Value.GetArrayLength() < 1) continue;
            if (prop.Value[0].ValueKind != System.Text.Json.JsonValueKind.String) continue;
            if (!root.TryGetProperty(prop.Value[0].GetString()!, out var refNode)) continue;
            var s = ExtractTextFromComfyNode(refNode, root, depth + 1);
            if (!string.IsNullOrEmpty(s) && !combined.Contains(s)) combined.Add(s);
        }
        return combined.Count > 0 ? string.Join("\n", combined) : null;
    }

    /// <summary>値がモデル/アセットのファイル名に見えるか (= テキストフォールバックの誤検出防止)。</summary>
    private static bool LooksLikeAssetFileName(string s)
        => s.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)
        || s.EndsWith(".sft",  StringComparison.OrdinalIgnoreCase)
        || s.EndsWith(".ckpt", StringComparison.OrdinalIgnoreCase)
        || s.EndsWith(".pt",   StringComparison.OrdinalIgnoreCase)
        || s.EndsWith(".pth",  StringComparison.OrdinalIgnoreCase)
        || s.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
        || s.EndsWith(".bin",  StringComparison.OrdinalIgnoreCase);

    /// <summary>スイッチノードの <c>switch</c> 入力の真偽を解決する。
    /// リテラル (bool / "True"/"False" 文字列) はそのまま、参照 ([nodeId, outIdx]) なら参照先の
    /// <c>value</c> / <c>boolean</c> フィールド (PrimitiveBoolean 等) を最大 4 ホップ辿る。
    /// 判定できなければ null (= 呼び出し側は両枝を試す)。</summary>
    private static bool? ResolveComfySwitchState(System.Text.Json.JsonElement inputs, System.Text.Json.JsonElement root)
    {
        if (!inputs.TryGetProperty("switch", out var v)) return null;
        for (var hop = 0; hop < 4; hop++)
        {
            switch (v.ValueKind)
            {
                case System.Text.Json.JsonValueKind.True:  return true;
                case System.Text.Json.JsonValueKind.False: return false;
                case System.Text.Json.JsonValueKind.String:
                    var s = v.GetString();
                    if (string.Equals(s, "true",  StringComparison.OrdinalIgnoreCase)) return true;
                    if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
                    return null;
                case System.Text.Json.JsonValueKind.Array
                    when v.GetArrayLength() >= 1
                         && v[0].ValueKind == System.Text.Json.JsonValueKind.String
                         && root.TryGetProperty(v[0].GetString()!, out var refNode)
                         && refNode.TryGetProperty("inputs", out var refInputs):
                    if (refInputs.TryGetProperty("value",   out var nv)) { v = nv; continue; }
                    if (refInputs.TryGetProperty("boolean", out var nb)) { v = nb; continue; }
                    return null;
                default:
                    return null;
            }
        }
        return null;
    }

    private static void CopyComfyParam(System.Text.Json.JsonElement inputs, string srcKey,
                                       Dictionary<string, string> parameters, string outKey,
                                       System.Text.Json.JsonElement root)
    {
        // 既に同 outKey に他の sampler 系入力で値が入っていたら上書きしない (seed と noise_seed の優先順位差を吸収)。
        if (parameters.ContainsKey(outKey)) return;
        if (!inputs.TryGetProperty(srcKey, out var v)) return;
        v = ResolveScalarRef(v, srcKey, root, depth: 0); // 配列参照ならリテラルまで辿る
        var s = v.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => v.GetString(),
            System.Text.Json.JsonValueKind.Number => v.ToString(),    // int も double もそのまま文字列化
            System.Text.Json.JsonValueKind.True   => "True",
            System.Text.Json.JsonValueKind.False  => "False",
            _ => null,
        };
        if (!string.IsNullOrEmpty(s)) parameters[outKey] = s!;
    }

    /// <summary>パラメータ値が配列参照 (= [refNodeId, outIdx]) のとき、参照先ノードの input から
    /// 同名 (または同義) のリテラル widget 値を辿って取り出す (rgthree の Seed / KSampler Config 等)。
    /// リテラルが見つからなければ元の値を返す。depth で循環/深掘りを打ち切る。</summary>
    private static System.Text.Json.JsonElement ResolveScalarRef(
        System.Text.Json.JsonElement v, string key, System.Text.Json.JsonElement root, int depth)
    {
        if (depth > 8) return v;
        if (v.ValueKind != System.Text.Json.JsonValueKind.Array || v.GetArrayLength() < 1) return v;
        if (v[0].ValueKind != System.Text.Json.JsonValueKind.String) return v;
        if (!root.TryGetProperty(v[0].GetString()!, out var node)) return v;
        if (!node.TryGetProperty("inputs", out var ip)) return v;

        foreach (var name in ScalarSynonyms(key))
        {
            if (!ip.TryGetProperty(name, out var nv)) continue;
            if (nv.ValueKind is System.Text.Json.JsonValueKind.Number
                or System.Text.Json.JsonValueKind.String
                or System.Text.Json.JsonValueKind.True
                or System.Text.Json.JsonValueKind.False)
                return nv;
            if (nv.ValueKind == System.Text.Json.JsonValueKind.Array)
                return ResolveScalarRef(nv, key, root, depth + 1);
        }
        return v;
    }

    private static string[] ScalarSynonyms(string key) => key switch
    {
        "seed"         => new[] { "seed", "noise_seed", "value" },
        "noise_seed"   => new[] { "noise_seed", "seed", "value" },
        "steps"        => new[] { "steps", "steps_total", "value" },
        "cfg"          => new[] { "cfg", "value" },
        "sampler_name" => new[] { "sampler_name", "value" },
        "scheduler"    => new[] { "scheduler", "value" },
        "denoise"      => new[] { "denoise", "value" },
        _              => new[] { key, "value" },
    };

    // -----------------------------------------------------------------
    // SD WebUI infotext パース (file-details.js の parseSDWebUIInfotext を C# に移植)
    // -----------------------------------------------------------------

    /// <summary>抽出された infotext (= PNG パラメータチャンク or EXIF UserComment) を SD WebUI 形式として
    /// パースし、AI フィールドを埋めた <see cref="AiImageMetadata"/> を返す。
    /// infotext が無い / SD 形式でない場合は基本情報 (format/size/dimensions) のみのインスタンスを返す
    /// (= 詳細ペインで「画像情報のみ」表示するため)。</summary>
    private static AiImageMetadata BuildResult(string? infotext, string format, long fileSize, int width, int height)
    {
        if (string.IsNullOrEmpty(infotext) || !IsSDWebUIInfotext(infotext))
        {
            return new AiImageMetadata
            {
                Format   = format,
                FileSize = fileSize,
                Width    = width,
                Height   = height,
            };
        }

        var parsed = ParseSDWebUIInfotext(infotext);
        var generator = DetectSDWebUIGenerator(infotext, parsed.Parameters);
        // 参考 viewer の挙動に合わせ、Generator は parameters 末尾にも入れて詳細グリッドで表示できるようにする。
        parsed.Parameters["Generator"] = generator;
        return new AiImageMetadata
        {
            Format       = format,
            FileSize     = fileSize,
            Width        = width,
            Height       = height,
            Positive     = parsed.Positive,
            Negative     = parsed.Negative,
            Model        = parsed.Parameters.TryGetValue("Model", out var m) ? m : null,
            Generator    = generator,
            RawInfotext  = infotext,
            Parameters   = parsed.Parameters,
        };
    }

    /// <summary>SD WebUI infotext を出力したアプリを Version フィールドや本文から推定する。
    /// 参考実装 (viewer/public/file-details.js parseSDWebUIInfotext) の判定をそのまま移植:
    /// <list type="bullet">
    /// <item>本文に "Fooocus" を含む → "Fooocus"</item>
    /// <item>Version が "scom" で始まる → "scom" (姉妹プロジェクト ../scom。"scom 1.0.0" 形式)</item>
    /// <item>Version が "f\d" で始まる → "SD WebUI Forge" (Forge は "f2.0.1v1.10.1-..." のように先頭 f)</item>
    /// <item>Version が "v\d" で始まる → "SD WebUI (A1111)"</item>
    /// <item>その他 → "SD WebUI" (汎用 / 派生不明)</item>
    /// </list>
    /// </summary>
    private static string DetectSDWebUIGenerator(string infotext, Dictionary<string, string> parameters)
    {
        if (Regex.IsMatch(infotext, "Fooocus", RegexOptions.IgnoreCase)) return "Fooocus";
        if (parameters.TryGetValue("Version", out var ver) && !string.IsNullOrEmpty(ver))
        {
            if (Regex.IsMatch(ver, @"^scom\b", RegexOptions.IgnoreCase)) return "scom";
            if (Regex.IsMatch(ver, @"^f\d")) return "SD WebUI Forge";
            if (Regex.IsMatch(ver, @"^v\d")) return "SD WebUI (A1111)";
        }
        return "SD WebUI";
    }

    private static bool IsSDWebUIInfotext(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        ReadOnlySpan<string> kws = new[] { "Steps:", "Sampler:", "CFG scale:", "Seed:", "Size:", "Model hash:", "Model:" };
        int hits = 0;
        foreach (var k in kws)
            if (text.Contains(k, StringComparison.Ordinal)) hits++;
        return hits >= 2;
    }

    // 「key: value, key: "quoted, with comma", key: value」形式の最終行をパースする。
    // file-details.js と同じく \w[\w \-/]+: のキー形に限定して誤マッチを抑える。
    private static readonly Regex ParamRegex = new(
        @"\s*(\w[\w \-\/]+):\s*(""(?:\\.|[^\\""])+""|[^,]*)(?:,|$)",
        RegexOptions.Compiled);

    // -----------------------------------------------------------------
    // NovelAI tEXt メタ
    //
    // NovelAI 生成 PNG は以下の tEXt を持つ:
    //   Title           = "NovelAI generated image"
    //   Description     = (人間可読の) プロンプト
    //   Software        = "NovelAI"
    //   Source          = "NovelAI Diffusion V4.5 C02D4F98" 等 (モデル名 + ハッシュ)
    //   Generation time = 秒
    //   Comment         = JSON ({ "prompt", "uc", "steps", "scale", "sampler", "noise_schedule",
    //                              "seed", "width", "height", "cfg_rescale", "v4_prompt", "signed_hash", ... })
    // SD WebUI infotext とは別形式なので IsSDWebUIInfotext は通らない (= ここで別経路を用意)。
    // -----------------------------------------------------------------

    private static bool IsNovelAiChunks(Dictionary<string, string> chunks)
    {
        if (chunks.TryGetValue("Software", out var sw)
            && sw.IndexOf("NovelAI", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (chunks.TryGetValue("Source", out var src)
            && src.IndexOf("NovelAI", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    private static AiImageMetadata? TryParseNovelAiPngTexts(
        Dictionary<string, string> chunks, string format, long fileSize, int width, int height)
    {
        string? positive = chunks.TryGetValue("Description", out var desc) ? UnLatin1ToUtf8(desc) : null;
        string? negative = null;
        string? model    = chunks.TryGetValue("Source",      out var src)  ? UnLatin1ToUtf8(src)  : null;
        var     parameters = new Dictionary<string, string>(StringComparer.Ordinal);

        if (chunks.TryGetValue("Comment", out var commentLatin1))
        {
            var commentJson = UnLatin1ToUtf8(commentLatin1);
            ParseNovelAiCommentJson(commentJson, ref positive, ref negative, parameters);
        }

        if (!parameters.ContainsKey("Size") && width > 0 && height > 0)
            parameters["Size"] = $"{width}x{height}";
        if (!string.IsNullOrEmpty(model)) parameters["Model"] = model!;
        parameters["Generator"] = "NovelAI";

        return new AiImageMetadata
        {
            Format      = format,
            FileSize    = fileSize,
            Width       = width,
            Height      = height,
            Positive    = positive,
            Negative    = negative,
            Model       = model,
            Generator   = "NovelAI",
            RawInfotext = null, // NovelAI は SD infotext 文字列形式を持たないので「全文表示」は出さない
            Parameters  = parameters,
        };
    }

    private static void ParseNovelAiCommentJson(string json,
        ref string? positive, ref string? negative, Dictionary<string, string> parameters)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            if (root.TryGetProperty("prompt", out var p) && p.ValueKind == JsonValueKind.String)
            {
                var s = p.GetString();
                if (!string.IsNullOrEmpty(s)) positive = s;
            }
            if (root.TryGetProperty("uc", out var u) && u.ValueKind == JsonValueKind.String)
            {
                var s = u.GetString();
                if (!string.IsNullOrEmpty(s)) negative = s;
            }

            CopyNovelAiParam(root, "steps",          parameters, "Steps");
            CopyNovelAiParam(root, "scale",          parameters, "CFG scale");
            CopyNovelAiParam(root, "sampler",        parameters, "Sampler");
            CopyNovelAiParam(root, "noise_schedule", parameters, "Scheduler");
            CopyNovelAiParam(root, "seed",           parameters, "Seed");
            CopyNovelAiParam(root, "cfg_rescale",    parameters, "CFG rescale");

            // width/height が Comment に明示されていれば IHDR より優先 (Comment 値は生成パラメータの真実)。
            if (root.TryGetProperty("width",  out var wEl) && wEl.ValueKind == JsonValueKind.Number
             && root.TryGetProperty("height", out var hEl) && hEl.ValueKind == JsonValueKind.Number)
            {
                parameters["Size"] = $"{wEl.GetInt32()}x{hEl.GetInt32()}";
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AiImageMeta] NovelAI comment parse failed: {ex.Message}");
        }
    }

    private static void CopyNovelAiParam(JsonElement root, string srcKey,
        Dictionary<string, string> parameters, string outKey)
    {
        if (parameters.ContainsKey(outKey)) return;
        if (!root.TryGetProperty(srcKey, out var v)) return;
        var s = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            JsonValueKind.True   => "True",
            JsonValueKind.False  => "False",
            _ => null,
        };
        if (!string.IsNullOrEmpty(s)) parameters[outKey] = s!;
    }

    /// <summary>PNG tEXt は仕様上 Latin-1 だが、NovelAI / Comfy 等は UTF-8 バイトをそのまま入れる。
    /// Latin-1 で復号した文字列をバイト列に戻し、UTF-8 として再解釈できれば置き換える (= 日本語等を救う)。</summary>
    private static string UnLatin1ToUtf8(string latinDecoded)
    {
        if (string.IsNullOrEmpty(latinDecoded)) return latinDecoded;
        var bytes = Encoding.Latin1.GetBytes(latinDecoded);
        try
        {
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return utf8.GetString(bytes);
        }
        catch
        {
            return latinDecoded;
        }
    }

    // -----------------------------------------------------------------
    // alpha-LSB ステルス (stealth_pngcomp / stealth_pnginfo)
    //
    // 仕様: https://github.com/NovelAI/novelai-image-metadata (nai_meta.py)
    //   - 各 pixel の alpha LSB を MSB-first で連結したビット列。
    //   - 重要: ビット順は **列優先 (column-major)**。numpy で alpha.T.reshape(-1) しているため、
    //     col 0 を上から下、続いて col 1 を上から下、…という順序になる。
    //     行優先で読むと magic ('stealth_pngcomp') が一致しないので注意。
    //   - 先頭 15 byte (120 bits) が magic ("stealth_pngcomp" / "stealth_pnginfo")。
    //   - 続く 32 bits が payload bit length (big-endian)。
    //   - 続く payload bits が本体。pngcomp なら gzip 圧縮 JSON
    //     ({Description, Software, Source, Comment, ...} 形式、tEXt と同等の内容)。
    //     pnginfo なら無圧縮 UTF-8 文字列。
    // alpha 直書きが無い PNG (color type 0/2/3) には適用しない。
    // ashen-sensored/sd_webui_stealth_pnginfo (A1111 SD WebUI 拡張) は行優先で書く別仕様だが、
    // 同じ magic を使うのでこのデコーダで誤検出する可能性はある。実害が出たら別途吸収する。
    // -----------------------------------------------------------------

    private static string? TryExtractStealthPngInfo(byte[] data)
    {
        // IHDR バイト 25 = color type。4 = Grayscale+Alpha / 6 = RGB+Alpha のみが alpha LSB 直書き対象。
        if (data.Length < 26) return null;
        int colorType = data[25];
        if (colorType != 4 && colorType != 6) return null;

        try
        {
            using var ms = new MemoryStream(data, writable: false);
            var decoder = new PngBitmapDecoder(ms,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;
            BitmapSource src = decoder.Frames[0];

            if (src.Format != PixelFormats.Bgra32 && src.Format != PixelFormats.Pbgra32)
                src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

            int width  = src.PixelWidth;
            int height = src.PixelHeight;
            // 巨大画像でメモリ爆発しないよう上限 (8192x8192 ≒ 256 MiB) を付ける。
            if ((long)width * height > 64L * 1024 * 1024) return null;

            int stride = width * 4;
            var pixels = new byte[(long)height * stride];
            src.CopyPixels(pixels, stride, 0);

            return DecodeStealthAlphaLsb(pixels, width, height);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AiImageMeta] stealth decode failed: {ex.Message}");
            return null;
        }
    }

    private static string? DecodeStealthAlphaLsb(byte[] bgra, int width, int height)
    {
        long totalPixels = (long)width * height;
        if (totalPixels < (15 + 4) * 8) return null; // magic+length に届かない

        // 列優先順 (NovelAI 仕様) で alpha LSB を走査するためのインデクサ。
        // bit position n → 列 c = n / height, 行 r = n % height。
        // bgra は行優先 BGRA32 ストレージなので、pixel offset = (r * width + c) * 4 + 3 (alpha)。
        int bitPos = 0;
        byte NextAlphaBit()
        {
            int bp = bitPos++;
            int c  = bp / height;
            int r  = bp % height;
            return (byte)(bgra[(r * width + c) * 4 + 3] & 1);
        }

        // 15 byte の magic
        var magicBuf = new byte[15];
        for (int b = 0; b < 15; b++)
        {
            byte v = 0;
            for (int k = 0; k < 8; k++) v = (byte)((v << 1) | NextAlphaBit());
            magicBuf[b] = v;
        }
        var magic = Encoding.ASCII.GetString(magicBuf);
        bool compressed;
        if (magic == "stealth_pngcomp")      compressed = true;
        else if (magic == "stealth_pnginfo") compressed = false;
        else return null; // stealth_rgb* は alpha 経路では拾えないのでスコープ外

        // 32bit BE payload bit length
        uint payloadBits = 0;
        for (int k = 0; k < 32; k++) payloadBits = (payloadBits << 1) | NextAlphaBit();
        // sanity: 0 / 異常に大きい / 残り pixel に収まらない場合は弾く。8 MiB (= 67M bits) を上限とする。
        if (payloadBits == 0 || payloadBits > 8u * 1024u * 1024u * 8u) return null;
        if (bitPos + (long)payloadBits > totalPixels) return null;
        // NovelAI 仕様では bit 数を 8 で整数除算する (= 余りはペイロード末尾の無効ビット)。
        // 多くは 8 の倍数で書かれるが、念のため切り捨て採用 (= strict alignment 拒否はしない)。

        int payloadByteCount = (int)(payloadBits / 8);
        var payload = new byte[payloadByteCount];
        for (int b = 0; b < payloadByteCount; b++)
        {
            byte v = 0;
            for (int k = 0; k < 8; k++) v = (byte)((v << 1) | NextAlphaBit());
            payload[b] = v;
        }

        if (!compressed)
        {
            try { return Encoding.UTF8.GetString(payload); }
            catch { return null; }
        }

        try
        {
            using var srcStream  = new MemoryStream(payload, writable: false);
            using var gz         = new GZipStream(srcStream, CompressionMode.Decompress);
            using var sinkStream = new MemoryStream();
            gz.CopyTo(sinkStream);
            return Encoding.UTF8.GetString(sinkStream.ToArray());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AiImageMeta] stealth gzip failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>ステルスで取り出した文字列ペイロードを解釈する。
    /// 形式は実装ごとに以下のいずれか:
    /// <list type="bullet">
    /// <item>(a) NovelAI tEXt をそのまま JSON オブジェクト化したもの (Software/Description/Comment 等のキー)</item>
    /// <item>(b) NovelAI の Comment JSON 直書き (prompt/uc/steps/sampler/seed/...)</item>
    /// <item>(c) SD WebUI infotext 文字列 ("prompt\nNegative prompt: ...\nSteps: 20, ...")</item>
    /// </list>
    /// 既存の SD/NAI パーサに振り分けて <see cref="AiImageMetadata"/> を組み立てる。</summary>
    private static AiImageMetadata? TryBuildFromStealthPayload(
        string payload, string format, long fileSize, int width, int height)
    {
        var trimmed = payload.TrimStart();
        if (trimmed.StartsWith("{"))
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    // (a) tEXt bundle 形式 (Software / Description / Comment が string で並ぶ)
                    bool looksBundle = root.TryGetProperty("Software",    out _)
                                    || root.TryGetProperty("Description", out _)
                                    || root.TryGetProperty("Comment",     out _);
                    if (looksBundle)
                    {
                        var bundle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var prop in root.EnumerateObject())
                            if (prop.Value.ValueKind == JsonValueKind.String)
                                bundle[prop.Name] = prop.Value.GetString() ?? "";

                        if (IsNovelAiChunks(bundle))
                        {
                            var m = TryParseNovelAiPngTexts(bundle, format, fileSize, width, height);
                            if (m is { HasAiData: true }) return m;
                        }
                        foreach (var k in new[] { "parameters", "UserComment", "Comment" })
                        {
                            if (bundle.TryGetValue(k, out var v) && IsSDWebUIInfotext(v))
                                return BuildResult(v, format, fileSize, width, height);
                        }
                    }

                    // (b) NovelAI Comment 直書き (prompt + uc が同階層)
                    if (root.TryGetProperty("prompt", out _) && root.TryGetProperty("uc", out _))
                    {
                        string? positive = null, negative = null;
                        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
                        ParseNovelAiCommentJson(payload, ref positive, ref negative, parameters);
                        if (width > 0 && height > 0 && !parameters.ContainsKey("Size"))
                            parameters["Size"] = $"{width}x{height}";
                        parameters["Generator"] = "NovelAI";
                        return new AiImageMetadata
                        {
                            Format     = format,
                            FileSize   = fileSize,
                            Width      = width,
                            Height     = height,
                            Positive   = positive,
                            Negative   = negative,
                            Generator  = "NovelAI",
                            Parameters = parameters,
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AiImageMeta] stealth json parse failed: {ex.Message}");
            }
        }

        // (c) SD WebUI infotext として試す。BuildResult が IsSDWebUIInfotext を内部で再判定する。
        if (IsSDWebUIInfotext(payload))
            return BuildResult(payload, format, fileSize, width, height);

        return null;
    }

    private static (string Positive, string Negative, Dictionary<string, string> Parameters)
        ParseSDWebUIInfotext(string text)
    {
        var lines = text.Trim().Split('\n');
        if (lines.Length == 0) return ("", "", new());

        // 最終行が parameters 行 (key: value のペアが 3 つ以上) かを判定。
        // 微妙な infotext で「最終行が prompt の続き、その前の行が parameters」なケースも救う。
        int paramsLineIndex = lines.Length - 1;
        var lastMatches     = ParamRegex.Matches(lines[paramsLineIndex]);
        if (lastMatches.Count < 3 && lines.Length >= 2)
        {
            var prevMatches = ParamRegex.Matches(lines[lines.Length - 2]);
            if (prevMatches.Count >= 3)
            {
                paramsLineIndex = lines.Length - 2;
                lastMatches     = prevMatches;
            }
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in lastMatches)
        {
            var key   = m.Groups[1].Value.Trim();
            var value = m.Groups[2].Value.Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                // 簡易 unquote (\\\\ や \\" のエスケープは現状サポート外、SD infotext で出現頻度ほぼゼロ)
                value = value[1..^1];
            }
            parameters[key] = value;
        }

        var positive = new StringBuilder();
        var negative = new StringBuilder();
        bool inNegative = false;
        for (int i = 0; i < paramsLineIndex; i++)
        {
            var line = lines[i];
            if (line.StartsWith("Negative prompt:", StringComparison.Ordinal))
            {
                inNegative = true;
                var rest = line.Substring("Negative prompt:".Length).TrimStart();
                if (rest.Length > 0)
                {
                    if (negative.Length > 0) negative.Append('\n');
                    negative.Append(rest);
                }
            }
            else if (inNegative)
            {
                if (negative.Length > 0) negative.Append('\n');
                negative.Append(line);
            }
            else
            {
                if (positive.Length > 0) positive.Append('\n');
                positive.Append(line);
            }
        }

        return (positive.ToString().Trim(), negative.ToString().Trim(), parameters);
    }

    // -----------------------------------------------------------------
    // MP4 / MOV (ComfyUI SaveVideo 等の生成 AI 動画) — viewer からの移植
    //
    // ComfyUI は動画保存時、QuickTime メタデータ (moov/udta/meta の keys + ilst、
    // namespace "mdta") へ key="prompt" (API グラフ JSON) 等を書き込む。
    // moov ボックスだけを読み、mdat (映像本体) はシークで飛ばす = ファイル全読みしない
    // (動画は GB 級になり得るため)。
    // -----------------------------------------------------------------

    private static AiImageMetadata? ExtractFromMp4(Stream s)
    {
        long fileSize = s.Length;
        byte[]? moov = null;

        // ---- トップレベルボックス走査 (ftyp 確認・moov 取得) ----
        long pos = 0;
        var hdr = new byte[8];
        bool first = true;
        while (pos + 8 <= fileSize)
        {
            s.Position = pos;
            if (!ReadExact(s, hdr, 8)) break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(0, 4));
            string typ = Encoding.ASCII.GetString(hdr, 4, 4);
            int hdrLen = 8;
            if (size == 1)
            {
                if (!ReadExact(s, hdr, 8)) break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(0, 8));
                hdrLen = 16;
            }
            else if (size == 0) size = fileSize - pos;
            if (first && typ != "ftyp") return null; // ISO-BMFF (MP4/MOV) ではない
            first = false;
            if (typ == "moov")
            {
                long body = size - hdrLen;
                if (body <= 0 || body > 64 * 1024 * 1024) return null; // 異常サイズは対象外
                moov = new byte[body];
                if (!ReadExact(s, moov, (int)body)) return null;
                break;
            }
            if (size < hdrLen) break;
            pos += size;
        }
        if (moov == null) return null;
        return ParseMoovBody(moov, fileSize);
    }

    /// <summary>moov ボックス本体 (ヘッダ除く) から寸法とメタデータを読んで結果を組み立てる。
    /// ローカルファイル経路 (<see cref="ExtractFromMp4"/>) とネットワーク Range 取得経路の共通部。</summary>
    private static AiImageMetadata ParseMoovBody(byte[] moov, long fileSize)
    {
        // ---- moov 内: 寸法 (trak/tkhd) とメタデータ (udta/meta) ----
        int w = 0, h = 0;
        var kv = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (typ, _, start, len) in Mp4Boxes(moov, 0, moov.Length))
        {
            if (typ == "trak")
            {
                foreach (var (t2, _, s2, l2) in Mp4Boxes(moov, start, start + len))
                    if (t2 == "tkhd") ReadTkhdSize(moov, s2, l2, ref w, ref h);
            }
            else if (typ == "udta")
            {
                foreach (var (t2, _, s2, l2) in Mp4Boxes(moov, start, start + len))
                    if (t2 == "meta") ReadMetaKeysIlst(moov, s2, l2, kv);
            }
        }

        return BuildVideoResult(kv, "MP4", fileSize, w, h);
    }

    /// <summary>動画コンテナから集めたメタキー辞書を共通解釈する (MP4 / WebM 共用)。
    /// ComfyUI の key="prompt" (API グラフ JSON) を画像と同じパーサで解釈し、
    /// 解釈不能でも署名があれば部分ラベル、無ければ一般メタデータのみで返す。</summary>
    private static AiImageMetadata BuildVideoResult(
        Dictionary<string, string> kv, string format, long fileSize, int w, int h)
    {
        // ffmpeg / VideoHelperSuite 系は comment 等の汎用キー 1 個に
        // {"prompt": "<グラフJSON文字列>", "workflow": "..."} という JSON ラッパーで包んで書くことがある。
        // また生のグラフ JSON をそのままコメントに書くツールもある。どのキーに入っていても展開する。
        UnwrapNestedVideoMetadata(kv);

        // StringRecord 系ノードが実行時テキストを記録する "recorded_texts" ({"prompt": "...", ...})。
        // LLM 生成プロンプトのような「グラフを辿っても静的には得られない実行時確定値」の正本なので、
        // グラフ解析で positive/negative が取れなかったときの補完に使う。
        var (recordedPositive, recordedNegative) = ExtractRecordedTexts(kv);

        // MiniMax H3 Contex Loop (ComfyUI-MiniMaxH3-Contex-Loop) のチェーン動画:
        // 複数シーンのプロンプトが h3_plan (shots[]) / h3_manifest (segments[]) に記録される。
        // グラフの prompt 入力はループ状態ノード経由 (= シーンごとに実行時選択) で静的には辿れないため、
        // これが取れた場合はシーン別ラベル付きの複合テキストを Positive の正本として使う。
        var (h3Prompts, h3SceneCount) = ExtractH3ChainPrompts(kv);

        // ---- ComfyUI: key="prompt" (API グラフ JSON) を画像と同じパーサで解釈 ----
        if (kv.TryGetValue("prompt", out var comfyPrompt))
        {
            var meta = TryParseComfyPrompt(comfyPrompt, format, fileSize, w, h);
            if (meta is { HasAiData: true })
            {
                // Contex Loop: 全シーンのプロンプト (シーン別ラベル付き) を最優先で採用
                // (グラフから 1 シーン分だけ取れたとしても、複数シーンの正本の方が完全なため)。
                if (!string.IsNullOrEmpty(h3Prompts))
                {
                    meta = meta with { Positive = h3Prompts };
                    meta.Parameters?.TryAdd("Scenes", h3SceneCount.ToString());
                }
                // グラフから positive/negative が静的に取れないワークフロー (LLM 実行時生成等) は
                // recorded_texts の実行時記録で補完する。
                if (string.IsNullOrEmpty(meta.Positive) && !string.IsNullOrEmpty(recordedPositive))
                    meta = meta with { Positive = recordedPositive };
                if (string.IsNullOrEmpty(meta.Negative) && !string.IsNullOrEmpty(recordedNegative))
                    meta = meta with { Negative = recordedNegative };
                return meta;
            }
        }

        // グラフが無い / 解釈不能でも h3_plan / recorded_texts に実行時プロンプトが残っていれば ComfyUI として返す。
        if (!string.IsNullOrEmpty(h3Prompts) || !string.IsNullOrEmpty(recordedPositive))
        {
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["Generator"] = "ComfyUI" };
            if (w > 0 && h > 0) parameters["Size"] = $"{w}x{h}";
            if (!string.IsNullOrEmpty(h3Prompts)) parameters["Scenes"] = h3SceneCount.ToString();
            return new AiImageMetadata
            {
                Format = format, FileSize = fileSize, Width = w, Height = h,
                Positive = !string.IsNullOrEmpty(h3Prompts) ? h3Prompts : recordedPositive,
                Negative = recordedNegative,
                Generator = "ComfyUI", Parameters = parameters,
            };
        }

        // 署名 (prompt / workflow キー) はあるが JSON を解釈できなかった場合は部分結果。
        if (kv.ContainsKey("prompt") || kv.ContainsKey("workflow"))
        {
            var other = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (k, v) in kv) AddGeneralMeta(other, k, v);
            return BuildPartialAiResult("ComfyUI", other, format, fileSize, w, h);
        }

        // AI 由来でなくても、取れたキー (encoder 等) は一般メタデータとして公開。
        var otherMeta = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in kv) AddGeneralMeta(otherMeta, k, v);
        return new AiImageMetadata
        {
            Format = format, FileSize = fileSize, Width = w, Height = h,
            OtherMetadata = otherMeta,
        };
    }

    /// <summary>MiniMax H3 Contex Loop のチェーン動画メタから全シーンのプロンプトを取り出し、
    /// シーン別ラベル付きの複合テキストを組み立てる。ソースは優先順に:
    ///   1. kv["h3_plan"]     (h3_chain_plan_archive: prompt_prefix + shots[])
    ///   2. kv["h3_manifest"] (h3_chain_manifest: prompt_prefix + segments[])
    ///   3. kv["prompt"] グラフ内の MiniMaxH3ChainPlan ノードの plan_json 入力 (エディタ形式 plan)
    /// 共通プレフィックスは 1 回だけ先頭に出し、各シーンからは重複分を剥がす。
    /// 取れなければ (null, 0)。</summary>
    private static (string? Text, int SceneCount) ExtractH3ChainPrompts(Dictionary<string, string> kv)
    {
        foreach (var (json, listName) in EnumerateH3PlanCandidates(kv))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(SanitizeJsonNonStandardLiterals(json));
                var root = doc.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if (!root.TryGetProperty(listName, out var shots)
                    || shots.ValueKind != System.Text.Json.JsonValueKind.Array
                    || shots.GetArrayLength() == 0) continue;

                var prefix = root.TryGetProperty("prompt_prefix", out var pp) ? JsonTextOrJoinedLines(pp) : null;

                var sb = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(prefix))
                {
                    sb.Append("── 共通プレフィックス ──\n").Append(prefix!.Trim()).Append('\n');
                }

                int count = shots.GetArrayLength();
                int emitted = 0;

                // 各シーンが動画内の何秒〜何秒かは delivered_frames の累積 ÷ fps で機械的に決まる
                // (実測で動画実尺と一致)。generation_start_frame / audio_start_seconds は生成グリッド基準で
                // 配信タイムラインとはズレる (context 重複分) ので使わない。
                // 全シーンが delivered_frames を持つときだけ表示する (欠けると以降の累積がずれるため)。
                var fps = TryGetH3Fps(root);
                var canTime = fps > 0;
                if (canTime)
                {
                    foreach (var s in shots.EnumerateArray())
                    {
                        if (s.ValueKind == System.Text.Json.JsonValueKind.Object
                            && s.TryGetProperty("delivered_frames", out var dfp)
                            && dfp.ValueKind == System.Text.Json.JsonValueKind.Number) continue;
                        canTime = false;
                        break;
                    }
                }
                double accFrames = 0;

                foreach (var shot in shots.EnumerateArray())
                {
                    if (shot.ValueKind != System.Text.Json.JsonValueKind.Object) continue;

                    // 区間の算出は本文の有無に関わらず先に行う (= 本文が空のシーンがあっても累積をずらさない)。
                    double startSec = 0, endSec = 0;
                    var hasTime = false;
                    if (canTime
                        && shot.TryGetProperty("delivered_frames", out var dfv)
                        && dfv.TryGetDouble(out var frames))
                    {
                        startSec   = accFrames / fps;
                        accFrames += frames;
                        endSec     = accFrames / fps;
                        hasTime    = true;
                    }

                    // 本文: prompt (完全版) 優先、無ければ scene_prompt。
                    var body = shot.TryGetProperty("prompt", out var pv) ? JsonTextOrJoinedLines(pv) : null;
                    if (string.IsNullOrWhiteSpace(body)
                        && shot.TryGetProperty("scene_prompt", out var sv)) body = JsonTextOrJoinedLines(sv);
                    if (string.IsNullOrWhiteSpace(body)) continue;
                    body = body!.Trim();
                    // 各シーンの prompt には共通プレフィックスが複製されていることが多いので剥がす。
                    if (!string.IsNullOrWhiteSpace(prefix))
                    {
                        var p = prefix!.Trim();
                        if (body.StartsWith(p, StringComparison.Ordinal)) body = body[p.Length..].TrimStart();
                    }

                    var idx = shot.TryGetProperty("index", out var iv) && iv.ValueKind == System.Text.Json.JsonValueKind.Number
                        ? iv.GetInt32() : emitted + 1;
                    var id  = shot.TryGetProperty("id", out var idv) ? idv.GetString() : null;

                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append("── シーン ").Append(idx).Append('/').Append(count);
                    if (hasTime)
                        sb.Append(" [").Append(FormatSceneTime(startSec)).Append('–').Append(FormatSceneTime(endSec)).Append(']');
                    if (!string.IsNullOrEmpty(id)) sb.Append(": ").Append(id);
                    sb.Append(" ──\n").Append(body).Append('\n');
                    emitted++;
                }
                if (emitted > 0) return (sb.ToString().TrimEnd(), count);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AiVideoMeta] h3 plan parse failed: {ex.Message}");
            }
        }
        return (null, 0);
    }

    /// <summary>Contex Loop の plan / manifest から fps を取り出す (compatibility.fps → ルート直下 fps の順)。
    /// 取れない / 異常値なら 0 (= シーン区間の表示を諦める)。</summary>
    private static double TryGetH3Fps(System.Text.Json.JsonElement root)
    {
        if (root.TryGetProperty("compatibility", out var comp)
            && comp.ValueKind == System.Text.Json.JsonValueKind.Object
            && comp.TryGetProperty("fps", out var f1)
            && f1.ValueKind == System.Text.Json.JsonValueKind.Number
            && f1.TryGetDouble(out var v1) && v1 > 0) return v1;

        if (root.TryGetProperty("fps", out var f2)
            && f2.ValueKind == System.Text.Json.JsonValueKind.Number
            && f2.TryGetDouble(out var v2) && v2 > 0) return v2;

        return 0;
    }

    /// <summary>シーン区間表示用の時刻フォーマット (例: "0:00.0" / "1:00.4" / 1 時間超は "1:02:03.4")。</summary>
    private static string FormatSceneTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var total  = (int)Math.Floor(seconds);
        var tenths = (int)Math.Floor((seconds - total) * 10);
        var h = total / 3600;
        var m = (total % 3600) / 60;
        var s = total % 60;
        return h > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}.{3}", h, m, s, tenths)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}.{2}", m, s, tenths);
    }

    /// <summary>Contex Loop の plan JSON 候補を優先順に列挙する (JSON 文字列, シーン配列のプロパティ名)。</summary>
    private static IEnumerable<(string Json, string ListName)> EnumerateH3PlanCandidates(Dictionary<string, string> kv)
    {
        if (kv.TryGetValue("h3_plan", out var plan) && !string.IsNullOrEmpty(plan))
            yield return (plan, "shots");
        if (kv.TryGetValue("h3_manifest", out var mani) && !string.IsNullOrEmpty(mani))
            yield return (mani, "segments");

        // グラフ内 MiniMaxH3ChainPlan ノードの plan_json 入力 (= エディタ形式 plan: prompt_prefix + shots[])。
        // h3_plan / h3_manifest キーが剥がされた再エンコード品の救済。
        if (kv.TryGetValue("prompt", out var graphJson) && !string.IsNullOrEmpty(graphJson)
            && graphJson.Contains("plan_json", StringComparison.Ordinal))
        {
            string? planJson = null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(SanitizeJsonNonStandardLiterals(graphJson));
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Value.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                        if (!prop.Value.TryGetProperty("inputs", out var ip)) continue;
                        if (!ip.TryGetProperty("plan_json", out var pj)
                            || pj.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                        planJson = pj.GetString();
                        break;
                    }
                }
            }
            catch { /* グラフが読めなければ候補なし */ }
            if (!string.IsNullOrEmpty(planJson)) yield return (planJson!, "shots");
        }
    }

    /// <summary>JSON 値を文字列化する: 文字列はそのまま、文字列配列は改行連結 (Contex Loop の
    /// 「Prompts may be multiline strings or arrays of lines」仕様に対応)。それ以外は null。</summary>
    private static string? JsonTextOrJoinedLines(System.Text.Json.JsonElement v)
    {
        if (v.ValueKind == System.Text.Json.JsonValueKind.String) return v.GetString();
        if (v.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var lines = new List<string>();
            foreach (var item in v.EnumerateArray())
                if (item.ValueKind == System.Text.Json.JsonValueKind.String) lines.Add(item.GetString() ?? "");
            return lines.Count > 0 ? string.Join("\n", lines) : null;
        }
        return null;
    }

    /// <summary>"recorded_texts" キー ({"prompt": "...", "negative": "...", ...} 形式) から
    /// 実行時に記録されたプロンプト文字列を取り出す。キー名は "negative" を含めば負側、
    /// それ以外 ("prompt" / "positive" 等) は正側として最初の非空値を採用する。無ければ (null, null)。</summary>
    private static (string? Positive, string? Negative) ExtractRecordedTexts(Dictionary<string, string> kv)
    {
        if (!kv.TryGetValue("recorded_texts", out var json) || string.IsNullOrEmpty(json)) return (null, null);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(SanitizeJsonNonStandardLiterals(json));
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return (null, null);
            string? pos = null, neg = null;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                var v = prop.Value.GetString();
                if (string.IsNullOrEmpty(v)) continue;
                if (prop.Name.Contains("negative", StringComparison.OrdinalIgnoreCase)) neg ??= v;
                else                                                                    pos ??= v;
            }
            return (pos, neg);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AiVideoMeta] recorded_texts parse failed: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>kv のどれかの値が「JSON ラッパー ({"prompt": ..., "workflow": ...})」または
    /// 「生の ComfyUI グラフ JSON」なら、kv["prompt"] / kv["workflow"] へ昇格させる。
    /// キー名は問わない (MP4 ©cmt="cmt" / WebM "COMMENT" / "description" 等、書き手によりバラバラなため)。
    /// 既に kv["prompt"] があれば何もしない (= QuickTime keys 形式の正規経路を優先)。</summary>
    private static void UnwrapNestedVideoMetadata(Dictionary<string, string> kv)
    {
        if (kv.ContainsKey("prompt")) return;
        foreach (var (k, v) in kv.ToList())
        {
            if (string.IsNullOrEmpty(v) || !v.TrimStart().StartsWith("{", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(SanitizeJsonNonStandardLiterals(v));
                var root = doc.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object) continue;

                // (a) ラッパー形式: {"prompt": <文字列 or オブジェクト>, "workflow": ...}
                if (root.TryGetProperty("prompt", out var p))
                {
                    var ps = p.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.String => p.GetString(),
                        System.Text.Json.JsonValueKind.Object => p.GetRawText(),
                        _ => null,
                    };
                    if (!string.IsNullOrEmpty(ps))
                    {
                        kv["prompt"] = ps!;
                        if (!kv.ContainsKey("workflow") && root.TryGetProperty("workflow", out var wf))
                        {
                            var ws = wf.ValueKind switch
                            {
                                System.Text.Json.JsonValueKind.String => wf.GetString(),
                                System.Text.Json.JsonValueKind.Object => wf.GetRawText(),
                                _ => null,
                            };
                            if (!string.IsNullOrEmpty(ws)) kv["workflow"] = ws!;
                        }
                        return;
                    }
                }

                // (b) 値そのものが生のグラフ ({nodeId: {class_type, ...}, ...}) の形式
                if (LooksLikeComfyGraphJson(root))
                {
                    kv["prompt"] = v;
                    return;
                }
            }
            catch
            {
                // JSON として読めない値 (ただのコメント文字列等) は無視して次のキーへ
            }
        }
    }

    /// <summary>任意のメタデータ値から ComfyUI グラフ JSON を取り出す共通ヘルパ (画像・動画共用)。
    /// 対応形式:
    ///   - 生のグラフ JSON ({nodeId: {class_type: ...}})
    ///   - JSON ラッパー ({"prompt": "&lt;グラフ文字列&gt;"} / {"prompt": {…}})
    ///   - "Prompt: {...}" のようなプレフィックス付き (最初の '{' から読む)
    /// 該当しなければ null。</summary>
    private static string? TryExtractComfyGraphJson(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        int brace = value.IndexOf('{');
        if (brace < 0) return null;
        var json = value.Substring(brace);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(SanitizeJsonNonStandardLiterals(json));
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            if (LooksLikeComfyGraphJson(root)) return json;
            if (root.TryGetProperty("prompt", out var p))
            {
                // ラッパーの中身がグラフかどうかは呼び出し先の TryParseComfyPrompt が判定する
                // (NovelAI Comment JSON の "prompt" (= ただのテキスト) はそこで弾かれる)。
                return p.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => p.GetString(),
                    System.Text.Json.JsonValueKind.Object => p.GetRawText(),
                    _ => null,
                };
            }
            return null;
        }
        catch
        {
            return null; // JSON として読めない値 (ただのコメント等)
        }
    }

    /// <summary>JSON オブジェクトが ComfyUI API グラフ ({nodeId: {class_type: ...}}) に見えるか。
    /// 先頭から数プロパティだけ確認する軽量判定。</summary>
    private static bool LooksLikeComfyGraphJson(System.Text.Json.JsonElement root)
    {
        var inspected = 0;
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Object
                && prop.Value.TryGetProperty("class_type", out _))
                return true;
            if (++inspected >= 20) break;
        }
        return false;
    }

    /// <summary>start..end の連続ボックスを列挙。type は ASCII 4 文字、code は同 4 バイトの
    /// ビッグエンディアン値 (ilst の子はキー index が type になるため code で判定する)。</summary>
    private static IEnumerable<(string type, uint code, int start, int len)> Mp4Boxes(byte[] b, int start, int end)
    {
        int off = start;
        while (off + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(off, 4));
            uint code = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(off + 4, 4));
            string typ = Encoding.ASCII.GetString(b, off + 4, 4);
            int hdrLen = 8;
            if (size == 1 && off + 16 <= end)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(off + 8, 8));
                hdrLen = 16;
            }
            else if (size == 0) size = end - off;
            if (size < hdrLen || off + size > end) yield break;
            yield return (typ, code, off + hdrLen, (int)(size - hdrLen));
            off += (int)size;
        }
    }

    /// <summary>tkhd から幅・高さ (16.16 固定小数) を読む。音声トラックは 0×0 なので最大値を採用。</summary>
    private static void ReadTkhdSize(byte[] b, int start, int len, ref int w, ref int h)
    {
        if (len < 4) return;
        int ver = b[start];
        int wOff = start + (ver == 1 ? 88 : 76); // v1 は creation/modification/duration が 64bit
        if (wOff + 8 > start + len) return;
        int tw = (int)(BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(wOff, 4)) >> 16);
        int th = (int)(BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(wOff + 4, 4)) >> 16);
        if (tw > w) w = tw;
        if (th > h) h = th;
    }

    /// <summary>meta ボックス内の keys (キー名表) と ilst (値。type が 1 始まりのキー index) を
    /// 突き合わせ、UTF-8 テキスト値を kv へ集める。</summary>
    private static void ReadMetaKeysIlst(byte[] b, int start, int len, Dictionary<string, string> kv)
    {
        // meta は ISO-BMFF では FullBox (先頭 4 バイトが version/flags)、QuickTime 古典形式では
        // 直接子ボックスが始まる。先頭が子ボックスに見えるか (type が英数字か) で判別する。
        int body = LooksLikeMp4Box(b, start, start + len) ? start : start + 4;

        var keys = new List<string>(); // ilst の index は 1 始まり
        int ilstStart = -1, ilstLen = 0;
        foreach (var (typ, _, s2, l2) in Mp4Boxes(b, body, start + len))
        {
            if (typ == "keys" && l2 >= 8)
            {
                // ver/flags(4) + entry_count(4) + {size(4) + namespace(4)="mdta" + キー名}×n
                int count = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(s2 + 4, 4));
                int off = s2 + 8;
                for (int i = 0; i < count && off + 8 <= s2 + l2; i++)
                {
                    int ksize = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(off, 4));
                    if (ksize < 8 || off + ksize > s2 + l2) break;
                    keys.Add(Encoding.UTF8.GetString(b, off + 8, ksize - 8));
                    off += ksize;
                }
            }
            else if (typ == "ilst") { ilstStart = s2; ilstLen = l2; }
        }
        if (ilstStart < 0) return;

        // keys ボックスが無い ilst は iTunes 形式 (©cmt / ---- アトム) として読む
        // (ffmpeg の mp4 muxer 等。ComfyUI VideoHelperSuite / SaveVideo(旧) がこの形式で書く)。
        if (keys.Count == 0)
        {
            ReadItunesIlst(b, ilstStart, ilstLen, kv);
            return;
        }

        foreach (var (_, code, s2, l2) in Mp4Boxes(b, ilstStart, ilstStart + ilstLen))
        {
            int idx = (int)code; // このボックスの type がキー index
            if (idx <= 0 || idx > keys.Count) continue;
            foreach (var (dt, _, s3, l3) in Mp4Boxes(b, s2, s2 + l2))
            {
                if (dt != "data" || l3 < 8) continue;
                // data: type indicator(4) + locale(4) + 値。type=1 が UTF-8 テキスト。
                if (BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(s3, 4)) != 1) continue;
                var key = keys[idx - 1];
                if (!kv.ContainsKey(key)) kv[key] = Encoding.UTF8.GetString(b, s3 + 8, l3 - 8);
            }
        }
    }

    /// <summary>iTunes 形式の ilst を読む。子アトムは 2 種:
    ///   - ©xxx (先頭バイト 0xA9): 定型キー (©cmt=comment / ©too=encoder 等)。残り 3 文字をキー名にする。
    ///   - "----": カスタムキー。mean(名前空間)/name(キー名)/data(値) の子ボックスを持つ。
    /// 値は data ボックス (type indicator=1 = UTF-8 テキスト) から取る。</summary>
    private static void ReadItunesIlst(byte[] b, int start, int len, Dictionary<string, string> kv)
    {
        foreach (var (typ, code, s2, l2) in Mp4Boxes(b, start, start + len))
        {
            string? key = null;
            if (typ == "----")
            {
                foreach (var (t3, _, s3, l3) in Mp4Boxes(b, s2, s2 + l2))
                {
                    // name: ver/flags(4) + キー名
                    if (t3 == "name" && l3 > 4)
                        key = Encoding.UTF8.GetString(b, s3 + 4, l3 - 4);
                }
            }
            else if ((code >> 24) == 0xA9)
            {
                // ©cmt → "cmt" のように残り 3 文字をキーとして使う (かぶりは AddGeneralMeta 側で許容)
                key = new string(new[] { (char)((code >> 16) & 0xFF), (char)((code >> 8) & 0xFF), (char)(code & 0xFF) });
            }
            if (key is null) continue;

            foreach (var (dt, _, s3, l3) in Mp4Boxes(b, s2, s2 + l2))
            {
                if (dt != "data" || l3 < 8) continue;
                if (BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(s3, 4)) != 1) continue; // UTF-8 テキストのみ
                if (!kv.ContainsKey(key)) kv[key] = Encoding.UTF8.GetString(b, s3 + 8, l3 - 8);
            }
        }
    }

    private static bool LooksLikeMp4Box(byte[] b, int off, int end)
    {
        if (off + 8 > end) return false;
        for (int i = 4; i < 8; i++)
        {
            byte c = b[off + i];
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                      || c == ' ' || c == 0xA9; // 0xA9 = '©' (QuickTime の ©cmt 等)
            if (!ok) return false;
        }
        return true;
    }

    private static bool ReadExact(Stream s, byte[] buf, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = s.Read(buf, read, count - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    // -----------------------------------------------------------------
    // WebM / MKV (EBML) — ComfyUI SaveWEBM 等
    //
    // libav 系エンコーダはコンテナメタデータを Matroska の Tags 要素
    // (Segment > Tags > Tag > SimpleTag { TagName, TagString }) に書き出す。
    // ComfyUI SaveWEBM も prompt / workflow をこの形で埋める。
    // Segment 直下を走査し、必要な master 要素 (Tags / Tracks) だけ潜って
    // Cluster (映像本体) はサイズでスキップする = 全読みしない。
    // -----------------------------------------------------------------

    private const uint EbmlSegment    = 0x18538067;
    private const uint EbmlTags       = 0x1254C367;
    private const uint EbmlTag        = 0x7373;
    private const uint EbmlSimpleTag  = 0x67C8;
    private const uint EbmlTagName    = 0x45A3;
    private const uint EbmlTagString  = 0x4487;
    private const uint EbmlTracks     = 0x1654AE6B;
    private const uint EbmlTrackEntry = 0xAE;
    private const uint EbmlVideo      = 0xE0;
    private const uint EbmlPixelW     = 0xB0;
    private const uint EbmlPixelH     = 0xBA;

    private static AiImageMetadata? ExtractFromWebm(Stream s, long? totalSize = null)
    {
        // ネットワーク Range 経路では先頭バッファのみが渡るため、実ファイルサイズを引数で受ける。
        long fileSize = totalSize ?? s.Length;
        var kv = new Dictionary<string, string>(StringComparer.Ordinal);
        int w = 0, h = 0;

        // トップレベル: EBML ヘッダ → Segment。Segment の中だけ走査する。
        long pos = 0;
        while (pos < fileSize)
        {
            s.Position = pos;
            if (!TryReadEbmlElement(s, fileSize, out var id, out var size, out var bodyStart)) break;
            if (id == EbmlSegment)
            {
                var segEnd = size < 0 ? fileSize : Math.Min(fileSize, bodyStart + size);
                ScanSegmentChildren(s, bodyStart, segEnd, kv, ref w, ref h);
                break;
            }
            if (size < 0) break; // Segment 以外で不定長は想定外
            pos = bodyStart + size;
        }

        if (kv.Count == 0 && w == 0 && h == 0) return null; // Matroska として何も取れなかった
        return BuildVideoResult(kv, "WebM", fileSize, w, h);
    }

    /// <summary>Segment 直下の子要素を走査し、Tags と Tracks だけ読み込む (他はスキップ)。</summary>
    private static void ScanSegmentChildren(
        Stream s, long start, long end, Dictionary<string, string> kv, ref int w, ref int h)
    {
        long pos = start;
        while (pos < end)
        {
            s.Position = pos;
            if (!TryReadEbmlElement(s, end, out var id, out var size, out var bodyStart)) break;
            if (size < 0) break; // Segment 内の不定長 (ライブ配信形) は非対応
            if (id == EbmlTags && size <= 16 * 1024 * 1024)
            {
                var body = new byte[size];
                s.Position = bodyStart;
                if (ReadExact(s, body, (int)size)) ParseEbmlTags(body, kv);
            }
            else if (id == EbmlTracks && size <= 4 * 1024 * 1024)
            {
                var body = new byte[size];
                s.Position = bodyStart;
                if (ReadExact(s, body, (int)size)) ParseEbmlTracks(body, ref w, ref h);
            }
            pos = bodyStart + size;
        }
    }

    /// <summary>Tags バッファから SimpleTag { TagName, TagString } を kv に集める。</summary>
    private static void ParseEbmlTags(byte[] b, Dictionary<string, string> kv)
    {
        ForEachEbml(b, 0, b.Length, (id, s2, l2) =>
        {
            if (id != EbmlTag) return;
            ForEachEbml(b, s2, s2 + l2, (id2, s3, l3) =>
            {
                if (id2 != EbmlSimpleTag) return;
                string? name = null, value = null;
                ForEachEbml(b, s3, s3 + l3, (id3, s4, l4) =>
                {
                    if (id3 == EbmlTagName)   name  = Encoding.UTF8.GetString(b, s4, l4);
                    if (id3 == EbmlTagString) value = Encoding.UTF8.GetString(b, s4, l4);
                });
                if (name is not null && value is not null && !kv.ContainsKey(name)) kv[name] = value;
            });
        });
    }

    /// <summary>Tracks バッファから映像トラックの PixelWidth / PixelHeight を読む (最大値採用)。</summary>
    private static void ParseEbmlTracks(byte[] b, ref int w, ref int h)
    {
        int tw = 0, th = 0;
        ForEachEbml(b, 0, b.Length, (id, s2, l2) =>
        {
            if (id != EbmlTrackEntry) return;
            ForEachEbml(b, s2, s2 + l2, (id2, s3, l3) =>
            {
                if (id2 != EbmlVideo) return;
                ForEachEbml(b, s3, s3 + l3, (id3, s4, l4) =>
                {
                    if (id3 == EbmlPixelW) tw = Math.Max(tw, (int)ReadEbmlUInt(b, s4, l4));
                    if (id3 == EbmlPixelH) th = Math.Max(th, (int)ReadEbmlUInt(b, s4, l4));
                });
            });
        });
        if (tw > w) w = tw;
        if (th > h) h = th;
    }

    /// <summary>バッファ内の EBML 子要素を列挙してコールバックする (不正・不定長で打ち切り)。</summary>
    private static void ForEachEbml(byte[] b, int start, int end, Action<uint, int, int> visit)
    {
        int pos = start;
        while (pos < end)
        {
            if (!TryReadEbmlIdSize(b, pos, end, out var id, out var size, out var bodyStart)) return;
            if (size < 0 || bodyStart + size > end) return;
            visit(id, bodyStart, (int)size);
            pos = (int)(bodyStart + size);
        }
    }

    /// <summary>EBML 符号なし整数値 (ビッグエンディアン可変長) を読む。</summary>
    private static ulong ReadEbmlUInt(byte[] b, int start, int len)
    {
        ulong v = 0;
        for (int i = 0; i < len && i < 8; i++) v = (v << 8) | b[start + i];
        return v;
    }

    /// <summary>ストリームから EBML 要素ヘッダ (ID + サイズ) を読む。size=-1 は不定長。</summary>
    private static bool TryReadEbmlElement(Stream s, long end, out uint id, out long size, out long bodyStart)
    {
        id = 0; size = 0; bodyStart = 0;
        var buf = new byte[12];
        long pos = s.Position;
        int got = s.Read(buf, 0, (int)Math.Min(12, end - pos));
        if (got < 2) return false;
        if (!TryReadEbmlIdSize(buf, 0, got, out id, out size, out var bodyOff)) return false;
        bodyStart = pos + bodyOff;
        return true;
    }

    /// <summary>バッファから EBML の ID (1〜4 バイト) とサイズ (1〜8 バイト可変長) を読む。
    /// サイズが「全ビット 1」(未知長) のときは size=-1 を返す。</summary>
    private static bool TryReadEbmlIdSize(byte[] b, int pos, int end, out uint id, out long size, out int bodyStart)
    {
        id = 0; size = 0; bodyStart = 0;
        if (pos >= end) return false;

        // ID: 先頭バイトの最上位ビット位置で長さが決まる (VINT だがマーカービットは保持したまま使う)。
        byte first = b[pos];
        int idLen = first >= 0x80 ? 1 : first >= 0x40 ? 2 : first >= 0x20 ? 3 : first >= 0x10 ? 4 : 0;
        if (idLen == 0 || pos + idLen > end) return false;
        for (int i = 0; i < idLen; i++) id = (id << 8) | b[pos + i];

        // サイズ: VINT (マーカービットを除いた値)。
        int sp = pos + idLen;
        if (sp >= end) return false;
        byte sf = b[sp];
        int sLen = sf >= 0x80 ? 1 : sf >= 0x40 ? 2 : sf >= 0x20 ? 3 : sf >= 0x10 ? 4
                 : sf >= 0x08 ? 5 : sf >= 0x04 ? 6 : sf >= 0x02 ? 7 : sf >= 0x01 ? 8 : 0;
        if (sLen == 0 || sp + sLen > end) return false;
        long v = sf & ((1 << (8 - sLen)) - 1);
        bool allOnes = v == ((1 << (8 - sLen)) - 1);
        for (int i = 1; i < sLen; i++)
        {
            v = (v << 8) | b[sp + i];
            if (b[sp + i] != 0xFF) allOnes = false;
        }
        size = allOnes ? -1 : v; // 全ビット 1 = 未知長 (ライブ/未確定 Segment)
        bodyStart = sp + sLen;
        return true;
    }

    // -----------------------------------------------------------------
    // ネットワーク Range 取得 (サムネ表示時点のメタ表示用)
    //
    // 動画本体が未 DL でも、HTTP Range でコンテナのメタデータ部だけを取って解析する。
    //   MP4  : 先頭 256KB → box walk。moov がバッファ外なら「次 box ヘッダ 16B」を hop して
    //          moov の位置とサイズを特定し、moov 本体だけを追加 Range で取る (通常 1〜3 リクエスト)。
    //   WebM : 先頭 256KB のみで best effort (libav 系は Tags が先頭側にある)。
    // 結果 (失敗 = null も含む) はセッション内メモリキャッシュし、同一 URL の再要求はネットワークに出ない。
    // -----------------------------------------------------------------

    private const int  NetHeadFetchBytes = 256 * 1024;
    private const long NetMoovMaxBytes   = 16 * 1024 * 1024;

    private static readonly HttpClient _netHttp = CreateNetHttp();
    private static HttpClient CreateNetHttp()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression   = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            AllowAutoRedirect        = true,
            MaxAutomaticRedirections = 5,
        };
        var http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };
        // 動画 DL (VideoDownloadManager) と同じブラウザ UA で外部 CDN にアクセスする。
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36 ChBrowser");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ja,en;q=0.8");
        return http;
    }

    /// <summary>同時ネットワーク取得の上限 (= スレを開いた直後に動画スロットが大量にあっても帯域を絞る)。</summary>
    private static readonly SemaphoreSlim _netGate = new(2, 2);

    /// <summary>URL → 取得結果 (null = 取得失敗 / AI メタ無しの negative cache)。セッション内のみ。</summary>
    private static readonly Dictionary<string, AiImageMetadata?> _netCache = new(StringComparer.Ordinal);
    /// <summary>URL → in-flight タスク (= 同一 URL の並行要求を 1 つのネットワーク取得に束ねる)。</summary>
    private static readonly Dictionary<string, Task<AiImageMetadata?>> _netInFlight = new(StringComparer.Ordinal);
    private static readonly object _netLock = new();

    /// <summary>URL の拡張子が動画 (= ネットワーク Range 取得の対象) か。</summary>
    public static bool LooksLikeVideoUrl(string url)
    {
        int q = url.IndexOfAny(new[] { '?', '#' });
        var path = (q >= 0 ? url[..q] : url).ToLowerInvariant();
        return path.EndsWith(".mp4") || path.EndsWith(".m4v") || path.EndsWith(".mov")
            || path.EndsWith(".webm") || path.EndsWith(".mkv");
    }

    /// <summary>動画 URL のメタデータを HTTP Range で取得・解析する (サムネ表示時点用)。
    /// 失敗も含め結果はセッション内キャッシュされ、同一 URL は再度ネットワークに出ない。</summary>
    public Task<AiImageMetadata?> TryGetVideoMetaOverNetworkAsync(string url)
    {
        lock (_netLock)
        {
            if (_netCache.TryGetValue(url, out var hitMeta)) return Task.FromResult(hitMeta);
            if (_netInFlight.TryGetValue(url, out var inflight)) return inflight;
            var task = FetchVideoMetaAsync(url);
            _netInFlight[url] = task;
            return task;
        }
    }

    private static async Task<AiImageMetadata?> FetchVideoMetaAsync(string url)
    {
        AiImageMetadata? result = null;
        try
        {
            await _netGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var (head, totalSize) = await FetchRangeAsync(url, 0, NetHeadFetchBytes).ConfigureAwait(false);
                if (head is { Length: >= 12 })
                {
                    if (head[4] == 'f' && head[5] == 't' && head[6] == 'y' && head[7] == 'p')
                        result = await ParseMp4OverNetworkAsync(url, head, totalSize).ConfigureAwait(false);
                    else if (head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3)
                        result = ExtractFromWebm(new MemoryStream(head), totalSize > 0 ? totalSize : head.Length);
                }
            }
            finally { _netGate.Release(); }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AiVideoMeta] network fetch failed: {ex.Message}");
        }
        lock (_netLock)
        {
            _netCache[url] = result;
            _netInFlight.Remove(url);
        }
        return result;
    }

    /// <summary>先頭バッファからトップレベル box を歩き、moov を見つけて解析する。
    /// ヘッダがバッファ外に出たら 16B の Range hop (<see cref="ParseMp4HopAsync"/>) に切り替え、
    /// moov 本体だけを追加取得する。</summary>
    private static async Task<AiImageMetadata?> ParseMp4OverNetworkAsync(string url, byte[] head, long totalSize)
    {
        long fileSize = totalSize > 0 ? totalSize : head.Length;
        long pos = 0;
        for (var hop = 0; hop < 8 && pos + 8 <= fileSize; hop++)
        {
            if (pos + 16 > head.Length)
            {
                // ヘッダが先頭バッファ外 → 16B Range 取得の hop ループに切り替えて続行。
                var (next, _) = await FetchRangeAsync(url, pos, 16, requirePartial: true).ConfigureAwait(false);
                if (next is null || next.Length < 8) return null;
                return await ParseMp4HopAsync(url, pos, next, fileSize).ConfigureAwait(false);
            }

            int off = (int)pos;
            long size = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(off, 4));
            string typ = Encoding.ASCII.GetString(head, off + 4, 4);
            int hdrLen = 8;
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(off + 8, 8));
                hdrLen = 16;
            }
            else if (size == 0) size = fileSize - pos;
            if (pos == 0 && typ != "ftyp") return null;
            if (typ == "moov")
            {
                long body = size - hdrLen;
                if (body <= 0 || body > NetMoovMaxBytes) return null;
                // moov 全体が head 内にあればそのまま、無ければ本体だけ追加 Range
                if (pos + hdrLen + body <= head.Length)
                {
                    var moov = new byte[body];
                    Array.Copy(head, pos + hdrLen, moov, 0, (int)body);
                    return ParseMoovBody(moov, fileSize);
                }
                var (moovBuf, _) = await FetchRangeAsync(url, pos + hdrLen, (int)body, requirePartial: true).ConfigureAwait(false);
                if (moovBuf is null || moovBuf.Length < body) return null;
                return ParseMoovBody(moovBuf, fileSize);
            }
            if (size < hdrLen) return null;
            pos += size;
        }
        return null;
    }

    /// <summary>バッファ外 hop 用: pos 起点で取得した 16B ヘッダから box を辿り続ける。</summary>
    private static async Task<AiImageMetadata?> ParseMp4HopAsync(string url, long pos, byte[] hdr16, long fileSize)
    {
        var hdr = hdr16;
        for (var hop = 0; hop < 8 && pos + 8 <= fileSize; hop++)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(0, 4));
            string typ = Encoding.ASCII.GetString(hdr, 4, 4);
            int hdrLen = 8;
            if (size == 1 && hdr.Length >= 16)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(8, 8));
                hdrLen = 16;
            }
            else if (size == 0) size = fileSize - pos;

            if (typ == "moov")
            {
                long body = size - hdrLen;
                if (body <= 0 || body > NetMoovMaxBytes) return null;
                var (moovBuf, _) = await FetchRangeAsync(url, pos + hdrLen, (int)body, requirePartial: true).ConfigureAwait(false);
                if (moovBuf is null || moovBuf.Length < body) return null;
                return ParseMoovBody(moovBuf, fileSize);
            }
            if (size < hdrLen) return null;
            pos += size;
            if (pos + 8 > fileSize) return null;
            var (next, _) = await FetchRangeAsync(url, pos, 16, requirePartial: true).ConfigureAwait(false);
            if (next is null || next.Length < 8) return null;
            hdr = next;
        }
        return null;
    }

    /// <summary>HTTP Range GET。返り値は (取得バイト, 総ファイルサイズ)。
    /// 総サイズは Content-Range から。取れなければ 0。
    /// <paramref name="requirePartial"/>=true でサーバが Range 非対応 (200 応答) なら null を返す
    /// (= オフセット付き要求で先頭バイトを掴まされる誤読を防ぐ)。offset=0 の要求は 200 でも先頭 N バイトだけ読んで打ち切る。</summary>
    private static async Task<(byte[]? Data, long TotalSize)> FetchRangeAsync(
        string url, long offset, int count, bool requirePartial = false)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + count - 1);
        using var resp = await _netHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return (null, 0);

        long total = resp.Content.Headers.ContentRange?.Length
                  ?? (resp.StatusCode == System.Net.HttpStatusCode.OK ? (resp.Content.Headers.ContentLength ?? 0) : 0);

        if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            // 200 = Range 非対応。offset 0 なら先頭から必要分だけ読んで自主的に切る。offset>0 は誤読になるので諦める。
            if (requirePartial || offset != 0) return (null, total);
        }

        await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var buf  = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(read, count - read)).ConfigureAwait(false);
            if (n <= 0) break;
            read += n;
        }
        if (read == 0) return (null, total);
        if (read < count) Array.Resize(ref buf, read);
        return (buf, total);
    }
}

/// <summary>AI 生成画像メタの抽出結果。SD WebUI infotext を分解した形で公開する。</summary>
public sealed record AiImageMetadata
{
    /// <summary>"PNG" / "JPEG" / "WEBP"</summary>
    public string Format { get; init; } = "";

    public long FileSize { get; init; }
    public int  Width    { get; init; }
    public int  Height   { get; init; }

    /// <summary>SD infotext の "Model:" フィールド (= 例: "anything-v5", "model_name")。
    /// "Model hash:" は別キーで <see cref="Parameters"/> に入るので分離されない。</summary>
    public string? Model { get; init; }

    /// <summary>生成元アプリ判定 (= "SD WebUI Forge", "SD WebUI (A1111)", "Fooocus", "ComfyUI", "SD WebUI")。
    /// 検出ロジックは参考 viewer (file-details.js) と同一。判定不能時は null (= AI 生成画像でない)。
    /// 値はそのまま <see cref="Parameters"/>["Generator"] にも入っているので、UI 側はどちらを参照してもよい。</summary>
    public string? Generator { get; init; }

    /// <summary>ポジティブプロンプト (改行混じり、生のまま)。</summary>
    public string? Positive { get; init; }

    /// <summary>ネガティブプロンプト (改行混じり、生のまま)。</summary>
    public string? Negative { get; init; }

    /// <summary>infotext 全文 (デバッグ / 詳細ペインで「全文表示」したい場合用)。</summary>
    public string? RawInfotext { get; init; }

    /// <summary>"Steps", "Sampler", "CFG scale", "Seed", "Size", ... と "Model" 等の全パラメータ。
    /// 値はクオート除去後の生文字列 (unit 補正等はしない)。</summary>
    public Dictionary<string, string> Parameters { get; init; } = new();

    /// <summary>AI 生成として解釈できなかった場合の一般メタデータ (EXIF の Make/Model/Software、
    /// PNG text チャンク等)。値は表示用に切り詰め済み。<see cref="HasAiData"/> の判定には含めない
    /// (= カメラ写真の EXIF で AI 扱いにならないようにする)。</summary>
    public Dictionary<string, string> OtherMetadata { get; init; } = new();

    /// <summary>AI 生成画像として認識された (= SD WebUI infotext がパースできた) かどうか。
    /// false の場合は <see cref="Format"/>, <see cref="FileSize"/>, <see cref="Width"/>, <see cref="Height"/>
    /// だけが有効。スレ表示のホバーポップアップは true のときだけ出す。</summary>
    public bool HasAiData =>
        !string.IsNullOrEmpty(Positive) ||
        !string.IsNullOrEmpty(Negative) ||
        !string.IsNullOrEmpty(Model)    ||
        Parameters.Count > 0;
}
