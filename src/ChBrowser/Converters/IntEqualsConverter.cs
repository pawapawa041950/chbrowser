using System;
using System.Globalization;
using System.Windows.Data;

namespace ChBrowser.Converters;

/// <summary>値 (int) が ConverterParameter (int を表す文字列) と等しいかを bool で返す。
/// メニューのラジオ的チェック表示 (= 現在選択中の項目に ✓ を付ける) に使う。
/// 例: <c>IsChecked="{Binding AiNgThreshold, Converter={StaticResource IntEquals}, ConverterParameter=4}"</c></summary>
public sealed class IntEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;
        return int.TryParse(value.ToString(), out var v)
            && int.TryParse(parameter.ToString(), out var p)
            && v == p;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
