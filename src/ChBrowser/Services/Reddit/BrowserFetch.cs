using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ChBrowser.Services.Reddit;

/// <summary>ブラウザ (WebView2) の中で実行する fetch 1 件分の要求。</summary>
/// <param name="Headers">送るヘッダ。ブラウザが管理するもの (User-Agent / Cookie / Host 等) は含めない。</param>
/// <param name="Body">本文 (GET 等では null)。</param>
public sealed record BrowserFetchRequest(
    string Method,
    string Url,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    byte[]? Body = null);

/// <summary>ブラウザの fetch の結果。<see cref="FinalUrl"/> はリダイレクト追従後の URL。</summary>
public sealed record BrowserFetchResult(
    int    Status,
    string StatusText,
    string FinalUrl,
    bool   Redirected,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    byte[] Body)
{
    public string? Header(string name)
    {
        foreach (var kv in Headers)
            if (string.Equals(kv.Key, name, System.StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }
}

/// <summary>ログイン済みブラウザのセッションで HTTP 要求を行うもの (実装: <see cref="RedditWebViewHost"/>)。
/// ハーネスでは偽物に差し替えて、転送・再ログイン・後退の制御だけを検証する。</summary>
public interface IBrowserFetcher
{
    Task<BrowserFetchResult> FetchAsync(BrowserFetchRequest request, CancellationToken ct);

    /// <summary>このセッションの Cookie をすべて消す (ログアウト)。</summary>
    Task ClearCookiesAsync();

    /// <summary>ログインの Cookie (reddit: <c>reddit_session</c>) を持っているか。分からなければ null。
    /// 未ログインだと reddit は <c>me.json</c> も含めて「network security」で弾くので、「ログインが要る」のか
    /// 「ログイン中だが確認ページが出た」のかは応答からは見分けられない。Cookie の有無で見分ける。</summary>
    Task<bool?> HasLoginCookieAsync();
}

/// <summary>ログイン窓を出す理由。</summary>
public enum RedditLoginReason
{
    /// <summary>ログイン (初回 / 失効)。</summary>
    Login,
    /// <summary>「network security」等の確認ページをユーザに通してもらう。</summary>
    Challenge,
}

/// <summary>ユーザにログイン (または確認ページの通過) をしてもらう窓 (実装: <see cref="RedditLoginWindow"/>)。
/// 窓が閉じられたら完了する。戻り値は「窓の中でログインが確認できたか」。</summary>
public interface IRedditLoginUi
{
    Task<bool> ShowAsync(RedditLoginReason reason, string? url, CancellationToken ct);
}
