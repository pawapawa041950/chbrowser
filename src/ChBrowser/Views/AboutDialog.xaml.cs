using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ChBrowser.Views;

/// <summary>ヘルプ → バージョン情報 で開くダイアログ。
/// バージョン文字列は <see cref="AssemblyInformationalVersionAttribute"/> から読み出す
/// (= csproj の <c>&lt;InformationalVersion&gt;</c> でビルド日付を焼きこんでいる)。</summary>
public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        VersionText.Text = ReadInformationalVersion();
        IconImage.Source = LoadLargestIconFrame();
    }

    /// <summary>app.ico に含まれる複数フレーム (16/24/32/48/64/128/256 px) のうち最大解像度を返す。
    /// XAML の <c>&lt;Image Source="..."/&gt;</c> 直書きでは小さいフレームが拾われて拡大表示され
    /// ぼやけるため、code-behind で明示的に選択する。</summary>
    private static BitmapFrame LoadLargestIconFrame()
    {
        var uri = new Uri("pack://application:,,,/Resources/icon/app.ico", UriKind.Absolute);
        var decoder = BitmapDecoder.Create(uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
    }

    private static string ReadInformationalVersion()
    {
        var attr = typeof(AboutDialog).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        var v = attr?.InformationalVersion ?? "(version unknown)";
        // SDK が "+commitsha" を付けるケース (SourceLink 有効時) に備え、'+' 以降を除去。
        var plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Close();
}
