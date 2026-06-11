using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    /// 形式は認識できたが AI 生成データが無い画像については、画像基本情報 (format/size/dimensions) のみが入った
    /// インスタンスが返る (AI フィールドは null)。<see cref="AiImageMetadata.HasAiData"/> で判別可能。</summary>
    public Task<AiImageMetadata?> TryGetAsync(string url)
    {
        if (!_cache.TryGet(url, out var hit)) return Task.FromResult<AiImageMetadata?>(null);
        var path = hit.FilePath;
        // ファイル I/O + パースをスレッドプールへ。サイズの大きい PNG (数 MB) でも UI を止めない。
        return Task.Run<AiImageMetadata?>(() => TryExtractFromFile(path));
    }

    private static AiImageMetadata? TryExtractFromFile(string path)
    {
        try
        {
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

        // 何も拾えなかった場合は基本情報のみを返す (詳細ペイン用)。
        return new AiImageMetadata { Format = "PNG", FileSize = fileSize, Width = w, Height = h };
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
            var uc = FindUserCommentInBuffer(data, tiffStart, tiffEnd);
            if (!string.IsNullOrEmpty(uc))
                return BuildResult(uc, format, fileSize, width, height);
        }

        // 何も拾えなければ基本情報のみ。
        return new AiImageMetadata { Format = format, FileSize = fileSize, Width = width, Height = height };
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
            using var doc  = System.Text.Json.JsonDocument.Parse(json);
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

            foreach (var prop in root.EnumerateObject())
            {
                var node = prop.Value;
                if (node.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if (!node.TryGetProperty("class_type", out var ctElem)) continue;
                var ct = ctElem.GetString() ?? "";
                isComfyGraph = true;

                // sampler を 1 つ見つけたら hold (= 後段で positive/negative を辿る)
                if (samplerInputs is null && ct.Contains("Sampler", StringComparison.OrdinalIgnoreCase))
                {
                    if (node.TryGetProperty("inputs", out var ip)) samplerInputs = ip;
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
        // これらを終端まで辿らないと、CLIPTextEncode.text が primitive ノード参照のときにプロンプトが空になる。
        // text 系を優先し value/string は後置 (= 本物の text があればそちらを採用)。
        // 重複は除き改行で連結する。
        var texts = new List<string>();
        foreach (var fieldName in new[] { "text", "text_g", "text_l", "clip_l", "t5xxl", "prompt", "value", "string" })
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

    /// <summary>AI 生成画像として認識された (= SD WebUI infotext がパースできた) かどうか。
    /// false の場合は <see cref="Format"/>, <see cref="FileSize"/>, <see cref="Width"/>, <see cref="Height"/>
    /// だけが有効。スレ表示のホバーポップアップは true のときだけ出す。</summary>
    public bool HasAiData =>
        !string.IsNullOrEmpty(Positive) ||
        !string.IsNullOrEmpty(Negative) ||
        !string.IsNullOrEmpty(Model)    ||
        Parameters.Count > 0;
}
