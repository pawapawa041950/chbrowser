using System.Threading.Tasks;
using WpfWebView2 = Microsoft.Web.WebView2.Wpf.WebView2;

namespace ChBrowser.Services.Browser;

/// <summary>掲示板ごとのブラウザセッション (WebView2 のプロファイル) (<c>doc/multi-bbs-design.md</c> §8.8.1)。
/// アプリ共通の WebView2 環境に掲示板ごとの <b>別プロファイル</b> で載せる。Cookie・ストレージはスレ表示用の WebView や
/// 他の掲示板と分離され、exe の隣の <c>ChBrowser.exe.WebView2\</c> 内に保存されるのでアプリ再起動後も残る
/// (reddit のログイン、4chan の Cloudflare の確認等)。</summary>
public static class BrowserProfiles
{
    /// <summary><paramref name="webView"/> を <paramref name="profileName"/> のプロファイルで初期化する。UI スレッドから呼ぶこと。</summary>
    public static async Task InitializeAsync(WpfWebView2 webView, string profileName)
    {
        var env     = await ChBrowser.Controls.WebView2Helper.GetEnvironmentAsync().ConfigureAwait(true);
        var options = env.CreateCoreWebView2ControllerOptions();
        options.ProfileName = profileName;
        await webView.EnsureCoreWebView2Async(env, options).ConfigureAwait(true);
    }
}
