using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using ChBrowser.ViewModels;

namespace ChBrowser.Converters;

/// <summary>
/// <see cref="LogMarkState"/> を Ellipse 用の <see cref="Brush"/> に変換する。
/// スレ一覧の log-mark CSS と意味・色を揃える:
///   None         … 透明 (マーク非表示扱い)
///   Cached       … 青  #1e88e5
///   Updated      … 緑  #2e7d32
///   Dropped      … 茶  #8d6e63
///   RepliedToOwn … 赤  #e53935 (自分のレスへの返信あり)
/// </summary>
public sealed class LogMarkStateToBrushConverter : IValueConverter
{
    private static readonly Brush CachedBrush       = MakeFrozen(0x1E, 0x88, 0xE5);
    private static readonly Brush UpdatedBrush      = MakeFrozen(0x2E, 0x7D, 0x32);
    private static readonly Brush DroppedBrush      = MakeFrozen(0x8D, 0x6E, 0x63);
    private static readonly Brush RepliedToOwnBrush = MakeFrozen(0xE5, 0x39, 0x35);

    private static SolidColorBrush MakeFrozen(byte r, byte g, byte b)
    {
        var b2 = new SolidColorBrush(Color.FromRgb(r, g, b));
        b2.Freeze();
        return b2;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is LogMarkState s ? s switch
        {
            LogMarkState.Cached       => CachedBrush,
            LogMarkState.Updated      => UpdatedBrush,
            LogMarkState.Dropped      => DroppedBrush,
            LogMarkState.RepliedToOwn => RepliedToOwnBrush,
            _                         => Brushes.Transparent,
        } : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
