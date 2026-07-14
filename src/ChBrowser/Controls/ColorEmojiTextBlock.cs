using System;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChBrowser.Controls;

/// <summary>テキスト + カラー絵文字を表示する軽量コントロール。WPF 標準の <see cref="System.Windows.Controls.TextBlock"/> は
/// COLR/CPAL カラーフォントを描画できないため、タブ見出しの絵文字をカラー表示する目的で使う。
///
/// <para>描画は <see cref="ColorEmojiTextRenderer"/> (DirectWrite + Direct2D) に委譲し、結果の
/// <see cref="BitmapSource"/> を <see cref="OnRender"/> で貼る。絵文字フォントは設定 (Noto/Segoe) に追従する。
/// DirectWrite 初期化等に失敗した場合は素の <see cref="FormattedText"/> (モノクロ) にフォールバックして、
/// タブが空白になることを防ぐ。</para>
///
/// <para>単一行・省略記号対応。<see cref="Text"/> / <see cref="FontSize"/> / <see cref="Foreground"/> /
/// <see cref="FontFamily"/> / <see cref="TextTrimming"/> の変更と DPI 変更・絵文字設定変更で再描画する。</para></summary>
public sealed class ColorEmojiTextBlock : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(ColorEmojiTextBlock),
        new FrameworkPropertyMetadata(string.Empty,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            OnInputChanged));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
        typeof(ColorEmojiTextBlock),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontSize,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            OnInputChanged));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(ColorEmojiTextBlock),
        new FrameworkPropertyMetadata(Brushes.Black,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender,
            OnInputChanged));

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(
        typeof(ColorEmojiTextBlock),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            OnInputChanged));

    public static readonly DependencyProperty TextTrimmingProperty = DependencyProperty.Register(
        nameof(TextTrimming), typeof(TextTrimming), typeof(ColorEmojiTextBlock),
        new FrameworkPropertyMetadata(TextTrimming.CharacterEllipsis,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            OnInputChanged));

    public string      Text         { get => (string)GetValue(TextProperty);            set => SetValue(TextProperty, value); }
    public double      FontSize     { get => (double)GetValue(FontSizeProperty);         set => SetValue(FontSizeProperty, value); }
    public Brush       Foreground   { get => (Brush)GetValue(ForegroundProperty);        set => SetValue(ForegroundProperty, value); }
    public FontFamily  FontFamily   { get => (FontFamily)GetValue(FontFamilyProperty);   set => SetValue(FontFamilyProperty, value); }
    public TextTrimming TextTrimming { get => (TextTrimming)GetValue(TextTrimmingProperty); set => SetValue(TextTrimmingProperty, value); }

    private static void OnInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ColorEmojiTextBlock)d)._cacheKey = null;

    // ---- 描画キャッシュ ----
    private object?        _cacheKey;
    private BitmapSource?  _bmp;
    private FormattedText? _fallback;
    private Size           _size;

    public ColorEmojiTextBlock()
    {
        Loaded   += (_, _) => ColorEmojiTextRenderer.SettingsChanged += OnSettingsChanged;
        Unloaded += (_, _) => ColorEmojiTextRenderer.SettingsChanged -= OnSettingsChanged;
    }

    private void OnSettingsChanged()
    {
        _cacheKey = null;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _cacheKey = null;
        InvalidateMeasure();
        base.OnDpiChanged(oldDpi, newDpi);
    }

    private double PixelsPerDip()
    {
        try { return VisualTreeHelper.GetDpi(this).PixelsPerDip; }
        catch { return 1.0; }
    }

    /// <summary>与えられた最大幅で描画結果 (bitmap or fallback) を用意する。入力が同じならキャッシュ再利用。</summary>
    private void EnsureRendered(double maxWidth)
    {
        var text = Text ?? string.Empty;
        var fg   = (Foreground as SolidColorBrush)?.Color ?? Colors.Black;
        var fam  = FontFamily?.Source ?? "Segoe UI";
        var ellipsis = TextTrimming != TextTrimming.None;
        var dpi  = PixelsPerDip();
        var widthKey = double.IsInfinity(maxWidth) ? -1.0 : Math.Round(maxWidth);
        var active   = ChBrowser.Services.Fonts.EmojiFontService.Active;
        var fontPath = ChBrowser.Services.Fonts.EmojiFontService.FilePath ?? "";

        var key = (text, FontSize, fg, fam, ellipsis, widthKey, dpi, active, fontPath);
        if (key.Equals(_cacheKey) && (_bmp is not null || _fallback is not null)) return;
        _cacheKey = key;

        if (ColorEmojiTextRenderer.TryRender(text, fam, FontSize, fg, maxWidth, ellipsis, dpi, out var bmp, out var size))
        {
            _bmp      = bmp;
            _fallback = null;
            _size     = size;
        }
        else
        {
            // DirectWrite 経路が使えないときの保険: 素の FormattedText (絵文字はモノクロ)。
            BuildFallback(text, fam, fg, maxWidth, ellipsis, dpi);
        }
    }

    private void BuildFallback(string text, string family, Color fg, double maxWidth, bool ellipsis, double dpi)
    {
        _bmp = null;
        if (string.IsNullOrEmpty(text)) { _fallback = null; _size = new Size(0, Math.Ceiling(FontSize * 1.4)); return; }
        var typeface = new Typeface(new FontFamily(family), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, FontSize, new SolidColorBrush(fg), dpi) { MaxLineCount = 1 };
        if (ellipsis && !double.IsInfinity(maxWidth) && maxWidth > 0)
        {
            ft.MaxTextWidth = maxWidth;
            ft.Trimming     = TextTrimming.CharacterEllipsis;
        }
        _fallback = ft;
        _size     = new Size(Math.Ceiling(ft.WidthIncludingTrailingWhitespace) + 1, Math.Ceiling(ft.Height));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureRendered(availableSize.Width);
        var w = double.IsInfinity(availableSize.Width) ? _size.Width : Math.Min(_size.Width, availableSize.Width);
        return new Size(w, _size.Height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Arrange 後の実幅で描画結果を確定 (trimming 幅を実際の割当幅に合わせる)。
        var maxW = ActualWidth > 0 ? ActualWidth : _size.Width;
        EnsureRendered(maxW);
        if (_bmp is not null)
            dc.DrawImage(_bmp, new Rect(0, 0, _size.Width, _size.Height));
        else if (_fallback is not null)
            dc.DrawText(_fallback, new Point(0, 0));
    }
}
