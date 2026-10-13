using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChBrowser.Models;

namespace ChBrowser.Services.Llm;

/// <summary>画像の文字のまとまり 1 つの訳。<see cref="Where"/> は画像のどこにある文字か (漫画のコマ・吹き出し、SNS の投稿の名前 / 本文等)。
/// 文字のまとまりが 1 つだけの画像では空。</summary>
public sealed record ImageTextItem(string Where, string Text);

/// <summary>画像 1 枚の文字の訳 (画像キャッシュの隣の <c>&lt;hash&gt;.tr.json</c> に保存。<see cref="ChBrowser.Services.Image.ImageCacheService.ImageTranslationPath"/>)。
/// <see cref="Items"/> が空なら「訳す文字が無かった」。</summary>
public sealed record ImageTranslation(IReadOnlyList<ImageTextItem> Items, string Model, DateTimeOffset TranslatedAt);

/// <summary>画像の中に書かれた文字を LLM で日本語に訳す (X のスクリーンショット・英語版の漫画等)。
/// 画像の説明は求めず、文字だけを訳させる。文字が画像のあちこちにある場合 (漫画) は、それぞれの場所も書かせる。
/// 画像を入力できるモデル (<see cref="LlmSettings.SupportsImages"/>) が必要。</summary>
public static class ImageTranslator
{
    private const string SystemPrompt =
        "あなたは画像の中に書かれている文字を日本語に翻訳する翻訳者です。\n" +
        "画像に書かれている文字だけを読み取り、自然な日本語に翻訳してください。画像の内容の説明・感想・要約は書かないでください。\n" +
        "出力は次の形の JSON だけにしてください (前置き・説明・コードブロックは不要):\n" +
        "{\"items\":[{\"where\":\"文字がある場所\",\"text\":\"訳文\"}]}\n" +
        "規則:\n" +
        "- 文字のまとまり (漫画のコマの吹き出し・ナレーション・効果音、SNS の投稿の名前・本文・引用、看板等) ごとに 1 項目にし、読む順に並べてください。\n" +
        "- where には、その文字が画像のどこにあるかを短く書いてください (例: 「1コマ目・右の吹き出し」「投稿の本文」「左上のユーザー名」)。" +
        "文字のまとまりが 1 つだけなら where は空文字でかまいません。\n" +
        "- すでに日本語の文字はそのまま書いてください。ユーザー名・ハッシュタグ・URL・数字はそのまま残してかまいません。\n" +
        "- 読み取れない文字は無理に推測しないでください。文字が無ければ {\"items\":[]} にしてください。";

    private const string UserPrompt = "この画像に書かれている文字を日本語に翻訳してください。";

    /// <summary>LLM に送る画像の長辺の上限 (ピクセル)。大きい画像は縮める (送る量と処理時間を抑える。文字が読める程度は残す)。</summary>
    private const int MaxSide = 2048;

    /// <summary>画像 (キャッシュのファイルの中身) の文字を訳す。失敗 (接続エラー等) は <see cref="AiTranslateException"/>。</summary>
    public static async Task<ImageTranslation> TranslateAsync(
        LlmClient llm, LlmSettings settings, byte[] imageBytes, bool disableReasoning, CancellationToken ct)
    {
        var dataUrl = await Task.Run(() => ToDataUrl(imageBytes), ct).ConfigureAwait(false);
        var messages = new[]
        {
            new LlmChatMessage("system", SystemPrompt),
            new LlmChatMessage("user", UserPrompt) { ImageDataUrls = new[] { dataUrl } },
        };
        var chat = await llm.ChatStreamAsync(settings, messages, _ => { }, null, ct,
            disableReasoning ? ChBrowser.Services.Ng.AiNgJudge.ReasoningOff : null).ConfigureAwait(false);
        if (!chat.Ok) throw new AiTranslateException(chat.Error ?? "翻訳の要求に失敗しました");

        var (text, _) = ChatArchive.SplitThink(chat.Content ?? "");
        return new ImageTranslation(ParseItems(text), settings.Model, DateTimeOffset.Now);
    }

    /// <summary>画像をモデルが読める形 (PNG / JPEG の data URL) にする。動く GIF は 1 枚目。長辺が <see cref="MaxSide"/> を超えれば縮める。
    /// WebP 等もここで PNG / JPEG に直す (ローカルの LLM サーバには WebP を読めないものがあるため)。</summary>
    internal static string ToDataUrl(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        var longest = Math.Max(frame.PixelWidth, frame.PixelHeight);
        if (longest > MaxSide)
        {
            var scale = (double)MaxSide / longest;
            frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        }
        if (frame.Format != PixelFormats.Bgra32 && frame.Format != PixelFormats.Bgr32)
            frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        frame.Freeze();

        // 小さい画像は PNG (文字がくっきり)、大きい画像は JPEG (送る量を抑える)。JPEG は透明を持てないので白地に重ねる
        // (透明の背景が黒になって文字が読めなくなるのを防ぐ)
        var small = frame.PixelWidth * frame.PixelHeight <= 1024 * 1024;
        BitmapEncoder encoder = small ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(small ? frame : OnWhite(frame)));
        using var outMs = new MemoryStream();
        encoder.Save(outMs);
        return (small ? "data:image/png;base64," : "data:image/jpeg;base64,") + Convert.ToBase64String(outMs.ToArray());
    }

    /// <summary>Bgra32 / Bgr32 の画像を白地に重ねた Bgr24 にする (JPEG 用)。</summary>
    private static BitmapSource OnWhite(BitmapSource src)
    {
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        src.CopyPixels(px, stride, 0);
        var hasAlpha = src.Format == PixelFormats.Bgra32;
        var outStride = (w * 3 + 3) & ~3;
        var dst = new byte[outStride * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * stride + x * 4;
                var o = y * outStride + x * 3;
                var a = hasAlpha ? px[i + 3] : 255;
                for (var c = 0; c < 3; c++) dst[o + c] = (byte)((px[i + c] * a + 255 * (255 - a)) / 255);
            }
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, dst, outStride);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>応答 (<c>{"items":[{"where","text"}]}</c>) → 文字の訳。前後に余計な文やコードブロックがあっても最初の <c>{</c> から最後の <c>}</c> を読む。
    /// JSON として読めなければ、応答全体を場所なしの 1 項目とみなす (形を守らないモデル向け)。</summary>
    internal static List<ImageTextItem> ParseItems(string response)
    {
        var text = AiTranslator.StripWrapping(response ?? "").Trim();
        var start = text.IndexOf('{');
        var end   = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            try
            {
                using var doc = JsonDocument.Parse(text[start..(end + 1)]);
                if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<ImageTextItem>();
                    foreach (var it in items.EnumerateArray())
                    {
                        if (it.ValueKind != JsonValueKind.Object) continue;
                        var where = it.TryGetProperty("where", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString()!.Trim() : "";
                        var tr    = it.TryGetProperty("text",  out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()!.Trim() : "";
                        if (tr.Length > 0) list.Add(new ImageTextItem(where, tr));
                    }
                    return list;
                }
            }
            catch (JsonException) { /* 下の扱いへ */ }
        }
        return text.Length > 0 ? new List<ImageTextItem> { new("", text) } : new List<ImageTextItem>();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder              = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented        = true,
    };

    /// <summary>保存した訳を読む (無い / 壊れていれば null)。</summary>
    public static ImageTranslation? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<ImageTranslation>(File.ReadAllBytes(path), JsonOpts) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write($"[ImageTranslator] 訳を読めません ({path}): {ex.Message}");
            return null;
        }
    }

    public static void Save(string path, ImageTranslation translation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(translation, JsonOpts));
        File.Move(tmp, path, overwrite: true);
    }
}
