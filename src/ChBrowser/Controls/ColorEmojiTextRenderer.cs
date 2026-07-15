using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Vortice.WIC;
using WpfColor = System.Windows.Media.Color;
using WpfSize = System.Windows.Size;
using D2DFactory = Vortice.Direct2D1.ID2D1Factory;

namespace ChBrowser.Controls;

/// <summary>DirectWrite + Direct2D (Vortice 経由) で「テキスト + カラー絵文字」を <see cref="BitmapSource"/> に
/// 描くレンダラ。WPF の TextBlock は COLR/CPAL カラーフォントを描画できないため、タブ見出しの絵文字を
/// カラー化する目的で <see cref="ColorEmojiTextBlock"/> から使う。
///
/// <para>絵文字フォントは設定 (<see cref="ChBrowser.Services.Fonts.EmojiFontService"/>) に追従する:
/// 設定 ON かつ DL 済みなら Noto Color Emoji (COLRv1) を絵文字レンジに適用し、そうでなければ既定の
/// システムフォントフォールバック (= Segoe UI Emoji, COLRv0) に任せる。DirectWrite は COLRv0/COLRv1 の
/// どちらも描けるので、同一経路で両対応できる。</para>
///
/// <para>全ての D2D/DWrite/WIC オブジェクトは UI スレッド専用。ファクトリは遅延生成して使い回す。
/// 例外時は <see cref="TryRender"/> が false を返し、呼び出し側 (コントロール) が
/// 素の <see cref="FormattedText"/> (モノクロ) にフォールバックする。</para></summary>
internal static class ColorEmojiTextRenderer
{
    /// <summary>絵文字設定 (ON/OFF・DL 状態) が変わったことを通知する。表示中のタブが再描画に使う。</summary>
    public static event Action? SettingsChanged;

    /// <summary>App の設定適用 (ApplyConfigImmediate) から呼ぶ。購読中のタブ見出しを再描画させる。</summary>
    public static void NotifySettingsChanged()
    {
        _notoPixelChecked = false; // 設定切替後の状態で診断ピクセル検査をやり直す
        SettingsChanged?.Invoke();
    }

    private static D2DFactory?          _d2d;
    private static IDWriteFactory?      _dw;
    private static IWICImagingFactory?  _wic;
    private static bool                 _factoryFailed;

    // Noto フォントコレクションのキャッシュ (フォントファイルパス単位で 1 回だけ構築)。
    private static IDWriteFontCollection1? _notoColl;
    private static string?                 _notoPath;
    private static string                  _notoFamily = "";

    /// <summary>診断ログ (アプリのログペイン)。同一メッセージは 1 回だけ出す (= 毎描画のスパム防止)。
    /// 別マシンで「Noto 設定時のみ白黒になる」等の環境依存問題を、リリース版でも切り分けられるようにする。</summary>
    private static readonly HashSet<string> _loggedOnce = new();
    /// <summary>Noto 適用描画のピクセル検査 (診断) を実施済みか。設定変更でリセットして再検査する。</summary>
    private static bool _notoPixelChecked;
    private static void LogOnce(string message)
    {
        if (!_loggedOnce.Add(message)) return;
        ChBrowser.Services.Logging.LogService.Instance.Write(message);
    }

    private static bool EnsureFactories()
    {
        if (_factoryFailed) return false;
        if (_d2d is not null && _dw is not null && _wic is not null) return true;
        try
        {
            _d2d = Vortice.Direct2D1.D2D1.D2D1CreateFactory<D2DFactory>();
            _dw  = Vortice.DirectWrite.DWrite.DWriteCreateFactory<IDWriteFactory>();
            _wic = new IWICImagingFactory();
            return true;
        }
        catch (Exception ex)
        {
            _factoryFailed = true;
            LogOnce($"[ColorEmoji] factory init 失敗 (タブ絵文字は WPF モノクロ描画にフォールバック): {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>現在の設定で絵文字レンジに使う Noto コレクションと family 名を返す。
    /// 設定 OFF / 未 DL / 構築失敗なら null (= 既定のシステムフォールバックに任せる)。</summary>
    private static (IDWriteFontCollection1 Coll, string Family)? GetNotoCollection()
    {
        if (!ChBrowser.Services.Fonts.EmojiFontService.Active) return null;
        var path = ChBrowser.Services.Fonts.EmojiFontService.FilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        if (_notoColl is not null && _notoPath == path) return (_notoColl, _notoFamily);
        try
        {
            var f5   = _dw!.QueryInterface<IDWriteFactory5>();
            var file = f5.CreateFontFileReference(path);
            var fsb  = f5.CreateFontSetBuilder();
            fsb.AddFontFile(file);
            var set  = fsb.CreateFontSet();
            var coll = f5.CreateFontCollectionFromFontSet(set);
            _notoColl   = coll;
            _notoPath   = path;
            _notoFamily = coll.GetFontFamily(0).FamilyNames.GetString(0);
            long size = 0;
            try { size = new FileInfo(path).Length; } catch { /* 診断ログ用なので失敗は無視 */ }
            LogOnce($"[ColorEmoji] Noto コレクション構築 OK: family='{_notoFamily}', size={size} bytes, path={path}");
            return (_notoColl, _notoFamily);
        }
        catch (Exception ex)
        {
            LogOnce($"[ColorEmoji] Noto コレクション構築 失敗 (タブ絵文字は Segoe にフォールバック): {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>テキストを描画して BitmapSource と DIP サイズを返す。失敗時 false。</summary>
    /// <param name="text">描画文字列。</param>
    /// <param name="baseFamily">絵文字以外に使う基本フォント family (例: "Segoe UI")。</param>
    /// <param name="fontSizeDip">フォントサイズ (DIP = WPF px)。</param>
    /// <param name="foreground">テキスト色 (絵文字はフォント色なので無関係)。</param>
    /// <param name="maxWidthDip">最大幅 (DIP)。無限大 (=制約なし) なら trimming しない。</param>
    /// <param name="ellipsis">true かつ maxWidth 有限なら末尾を … で省略。</param>
    /// <param name="pixelsPerDip">DPI 倍率 (96dpi=1.0)。高 DPI でくっきり描くため。</param>
    public static bool TryRender(
        string text, string baseFamily, double fontSizeDip, WpfColor foreground,
        double maxWidthDip, bool ellipsis, double pixelsPerDip,
        out BitmapSource? bitmap, out WpfSize sizeDip)
    {
        bitmap  = null;
        sizeDip = default;
        if (string.IsNullOrEmpty(text)) { sizeDip = new WpfSize(0, Math.Ceiling(fontSizeDip * 1.4)); return true; }
        if (!EnsureFactories()) return false;

        try
        {
            var dw  = _dw!;
            var wic = _wic!;
            var d2d = _d2d!;

            using var fmt = dw.CreateTextFormat(
                string.IsNullOrEmpty(baseFamily) ? "Segoe UI" : baseFamily, null!,
                Vortice.DirectWrite.FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal, Vortice.DirectWrite.FontStretch.Normal, (float)fontSizeDip);
            fmt.WordWrapping = WordWrapping.NoWrap;

            bool constrained = !double.IsInfinity(maxWidthDip) && maxWidthDip > 0;
            float layoutMax  = constrained ? (float)maxWidthDip : 100000f;
            using var layout = dw.CreateTextLayout(text, fmt, layoutMax, (float)(fontSizeDip * 3));

            // 絵文字レンジだけ Noto に差し替え (設定 ON 時)。OFF 時は既定フォールバック = Segoe UI Emoji。
            var noto = GetNotoCollection();
            var hasEmoji = false;
            if (noto is { } n)
            {
                foreach (var (start, len) in EmojiRanges(text))
                {
                    hasEmoji = true;
                    var range = new TextRange((uint)start, (uint)len);
                    layout.SetFontCollection(n.Coll, range);
                    layout.SetFontFamilyName(n.Family, range);
                }
            }

            if (ellipsis && constrained)
            {
                using var sign = dw.CreateEllipsisTrimmingSign(fmt);
                layout.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, sign);
            }

            var m      = layout.Metrics;
            double dipW = Math.Ceiling(m.Width) + 1;
            if (constrained) dipW = Math.Min(dipW, Math.Ceiling(maxWidthDip));
            double dipH = Math.Ceiling(m.Height);
            if (dipW < 1) dipW = 1;
            if (dipH < 1) dipH = Math.Ceiling(fontSizeDip * 1.4);

            double scale = pixelsPerDip <= 0 ? 1.0 : pixelsPerDip;
            int pw = Math.Max(1, (int)Math.Ceiling(dipW * scale));
            int ph = Math.Max(1, (int)Math.Ceiling(dipH * scale));

            using var bmp = wic.CreateBitmap((uint)pw, (uint)ph, Vortice.WIC.PixelFormat.Format32bppPBGRA, BitmapCreateCacheOption.CacheOnLoad);
            var rtp = new RenderTargetProperties
            {
                PixelFormat = new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                DpiX = (float)(96 * scale),
                DpiY = (float)(96 * scale),
            };
            using var rt    = d2d.CreateWicBitmapRenderTarget(bmp, rtp);
            using var brush = rt.CreateSolidColorBrush(new Color4(foreground.R / 255f, foreground.G / 255f, foreground.B / 255f, foreground.A / 255f));
            rt.BeginDraw();
            rt.Clear(new Color4(0f, 0f, 0f, 0f)); // 透明 (タブ背景を透過)
            rt.DrawTextLayout(new Vector2(0f, 0f), layout, brush, DrawTextOptions.EnableColorFont);
            rt.EndDraw();

            int stride = pw * 4;
            var px     = new byte[stride * ph];
            bmp.CopyPixels((uint)stride, px);
            var src = BitmapSource.Create(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32, null, px, stride);
            src.Freeze();

            // 診断 (初回 1 回だけ): Noto を当てた絵文字入りテキストが「実際にカラー画素で描かれたか」を検査。
            // 例外なしで白黒になる環境 (= COLR 変換が静かに不発) をログで判別できるようにする。
            if (hasEmoji && !_notoPixelChecked)
            {
                _notoPixelChecked = true;
                var colored = false;
                for (int i = 0; i < px.Length; i += 4)
                {
                    // BGRA: 不透明画素で RGB が揃っていなければ「カラー」とみなす。
                    if (px[i + 3] > 0 && (px[i] != px[i + 1] || px[i + 1] != px[i + 2])) { colored = true; break; }
                }
                LogOnce($"[ColorEmoji] Noto 描画チェック: カラー画素={(colored ? "あり (正常)" : "なし (白黒描画 → COLRv1 変換不発の疑い)")} text='{text}'");
            }

            bitmap  = src;
            sizeDip = new WpfSize(dipW, dipH);
            return true;
        }
        catch (Exception ex)
        {
            // HRESULT 付きで記録 (COM 例外なら D2D/DWrite のエラーコードが原因特定の鍵になる)。
            var hr = ex is System.Runtime.InteropServices.COMException com ? $" hr=0x{com.HResult:X8}" : "";
            LogOnce($"[ColorEmoji] render 失敗 (このテキストは WPF モノクロ描画にフォールバック){hr}: {ex.GetType().Name}: {ex.Message} (noto={ChBrowser.Services.Fonts.EmojiFontService.Active})");
            return false;
        }
    }

    // ---- 絵文字レンジ検出 (Noto を当てる文字範囲を UTF-16 index/length で返す) ----

    private static bool IsEmojiBase(int cp) =>
           (cp >= 0x1F000 && cp <= 0x1FAFF) || (cp >= 0x2600 && cp <= 0x27BF)
        || (cp >= 0x2B00  && cp <= 0x2BFF)  || (cp >= 0x1F1E6 && cp <= 0x1F1FF)
        || (cp >= 0x2300  && cp <= 0x23FF)  || (cp >= 0x25A0  && cp <= 0x25FF)
        || cp == 0x203C || cp == 0x2049 || cp == 0x2122 || cp == 0x2139;

    private static bool IsEmojiCont(int cp) =>
           cp == 0xFE0F || cp == 0xFE0E || cp == 0x200D || cp == 0x20E3
        || (cp >= 0x1F3FB && cp <= 0x1F3FF) || (cp >= 0xE0020 && cp <= 0xE007F);

    /// <summary>絵文字 (基底 + 付随する VS/ZWJ/肌色修飾/地域指示子) の連続範囲を列挙する。
    /// タブ見出し用途なので厳密な grapheme 分割ではなく実用的なグルーピングに留める。</summary>
    private static List<(int Start, int Len)> EmojiRanges(string s)
    {
        var res = new List<(int, int)>();
        int i = 0;
        while (i < s.Length)
        {
            int cp = CodePointAt(s, i, out int cl);
            if (IsEmojiBase(cp))
            {
                int start = i, end = i + cl;
                i += cl;
                while (i < s.Length)
                {
                    int c2 = CodePointAt(s, i, out int l2);
                    if (IsEmojiBase(c2) || IsEmojiCont(c2)) { end = i + l2; i += l2; }
                    else break;
                }
                res.Add((start, end - start));
            }
            else i += cl;
        }
        return res;
    }

    /// <summary>char.ConvertToUtf32 の孤立サロゲート安全版。不正 UTF-16 (= 文字数切り詰め等で
    /// ペアが分断された文字列) でも例外を投げず、孤立サロゲートはそのコード単位値 (= 非絵文字) として返す。</summary>
    private static int CodePointAt(string s, int i, out int len)
    {
        char c = s[i];
        if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
        {
            len = 2;
            return char.ConvertToUtf32(c, s[i + 1]);
        }
        len = 1;
        return c; // 孤立サロゲートを含む単独 code unit
    }
}
