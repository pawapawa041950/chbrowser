using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

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

        // ---- 戦略 2: ComfyUI prompt JSON (workflow グラフを辿って positive/negative を取り出す) ----
        if (chunks.TryGetValue("prompt", out var comfyPrompt))
        {
            var meta = TryParseComfyPrompt(comfyPrompt, "PNG", fileSize, w, h);
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

        // APP1 セグメントから "Exif\0\0" を探し、UserComment を抽出。
        // 完全な TIFF/IFD パーサは書かず、Rust fallback と同じく "ASCII\0\0\0" / "UNICODE\0" prefix を
        // 直接 byte-scan する。EXIF UserComment 以外でこの 8 バイト並びが偶然出る可能性は低い。
        var infotext = FindEXIFUserCommentInJpeg(data);
        return BuildResult(infotext, "JPEG", fileSize, w, h);
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

    /// <summary>JPEG の APP1 (FF E1) セグメントを走査して Exif の UserComment を取り出す。</summary>
    private static string? FindEXIFUserCommentInJpeg(byte[] data)
    {
        int i = 2;
        while (i + 4 <= data.Length)
        {
            if (data[i] != 0xFF) return null;
            while (i < data.Length && data[i] == 0xFF) i++;
            if (i >= data.Length) return null;
            byte marker = data[i++];
            if (marker == 0xD9 || marker == 0xDA) return null;
            if (marker is >= 0xD0 and <= 0xD7) continue;
            if (marker == 0x01) continue;
            if (i + 2 > data.Length) return null;
            int segLen = (data[i] << 8) | data[i + 1];
            int segStart = i + 2;
            int segEnd   = Math.Min(data.Length, i + segLen);

            if (marker == 0xE1 && segStart + 6 <= segEnd
                && data[segStart] == 'E' && data[segStart + 1] == 'x'
                && data[segStart + 2] == 'i' && data[segStart + 3] == 'f'
                && data[segStart + 4] == 0   && data[segStart + 5] == 0)
            {
                var found = FindUserCommentInBuffer(data, segStart, segEnd);
                if (!string.IsNullOrEmpty(found)) return found;
            }

            i += segLen;
        }
        return null;
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
        var infotext = FindEXIFUserCommentInWebp(data);
        return BuildResult(infotext, "WEBP", fileSize, w, h);
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

    private static string? FindEXIFUserCommentInWebp(byte[] data)
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
                var found = FindUserCommentInBuffer(data, payload, payload + size);
                if (!string.IsNullOrEmpty(found)) return found;
            }
            i = payload + size + (size & 1);
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
            System.Text.Json.JsonElement? samplerInputs = null;
            string? model = null;
            int?    latW  = null;
            int?    latH  = null;

            foreach (var prop in root.EnumerateObject())
            {
                var node = prop.Value;
                if (node.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if (!node.TryGetProperty("class_type", out var ctElem)) continue;
                var ct = ctElem.GetString() ?? "";

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
            }

            if (samplerInputs is null) return null;
            var sIn = samplerInputs.Value;

            // positive / negative。SamplerCustomAdvanced は guider 経由なので fallback で辿る。
            string? positive = ResolveTextRef(sIn, "positive", root, depth: 0);
            string? negative = ResolveTextRef(sIn, "negative", root, depth: 0);

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

            // パラメータ収集 (sampler の入力 + latent サイズ + model)
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            CopyComfyParam(sIn, "steps",         parameters, "Steps");
            CopyComfyParam(sIn, "cfg",           parameters, "CFG scale");
            CopyComfyParam(sIn, "sampler_name",  parameters, "Sampler");
            CopyComfyParam(sIn, "scheduler",     parameters, "Scheduler");
            CopyComfyParam(sIn, "seed",          parameters, "Seed");
            CopyComfyParam(sIn, "noise_seed",    parameters, "Seed");          // KSamplerAdvanced 系
            CopyComfyParam(sIn, "denoise",       parameters, "Denoise");

            if (latW is int lw && latH is int lh) parameters["Size"] = $"{lw}x{lh}";
            if (!string.IsNullOrEmpty(model))     parameters["Model"] = model!;
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
        // 重複は除き改行で連結する。
        var texts = new List<string>();
        foreach (var fieldName in new[] { "text", "text_g", "text_l", "clip_l", "t5xxl", "prompt" })
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
                                       Dictionary<string, string> parameters, string outKey)
    {
        // 既に同 outKey に他の sampler 系入力で値が入っていたら上書きしない (seed と noise_seed の優先順位差を吸収)。
        if (parameters.ContainsKey(outKey)) return;
        if (!inputs.TryGetProperty(srcKey, out var v)) return;
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
