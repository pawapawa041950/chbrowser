using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ChBrowser.Converters;

/// <summary>
/// LLM 接続確認ステータス文字列を表示色の <see cref="Brush"/> に変換する。
/// <see cref="ChBrowser.ViewModels.SettingsViewModel.LlmConnectionStatus"/> は
/// "OK — ..." / "NG — ..." / "確認中…" / "未確認" のいずれかで始まる規約なので、先頭で判定する:
///   "OK" 始まり … 緑  #2E7D32
///   "NG" 始まり … 赤  #C62828
///   それ以外     … 灰  #666666 (未確認 / 確認中)
/// </summary>
public sealed class LlmStatusToBrushConverter : IValueConverter
{
    private static readonly Brush OkBrush      = MakeFrozen(0x2E, 0x7D, 0x32);
    private static readonly Brush NgBrush      = MakeFrozen(0xC6, 0x28, 0x28);
    private static readonly Brush NeutralBrush = MakeFrozen(0x66, 0x66, 0x66);

    private static SolidColorBrush MakeFrozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string ?? "";
        if (s.StartsWith("OK", StringComparison.Ordinal)) return OkBrush;
        if (s.StartsWith("NG", StringComparison.Ordinal)) return NgBrush;
        return NeutralBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
