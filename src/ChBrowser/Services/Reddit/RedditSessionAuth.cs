using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Services.Bbs;

namespace ChBrowser.Services.Reddit;

/// <summary>reddit のログイン状態 (WebView2 セッション方式、<c>doc/reddit-design.md</c> §5)。
///
/// <list type="bullet">
/// <item><description>ログインの Cookie は WebView2 の reddit 用プロファイルがディスクに保存するので、アプリ再起動後もそのまま使える。
///   ここが持つのはユーザー名と modhash (書き込みの <c>X-Modhash</c>) だけ。</description></item>
/// <item><description>状態は <c>/api/me.json</c> で確かめる (名前が入っていればログイン中)。起動時には通信せず、
///   最初に reddit へ要求するときに確かめる。</description></item>
/// <item><description>ログイン窓は同時に 1 つだけ (<see cref="RequestLoginAsync"/> は実行中のものに相乗りする)。
///   自動処理 (<see cref="ProviderRequestContext.IsBackground"/>) からは窓を出さない (決定 D37)。</description></item>
/// <item><description><c>data/reddit.com/auth.json</c> には表示用にユーザー名と最終ログイン時刻だけを書く (トークン・パスワードは持たない)。</description></item>
/// </list></summary>
public sealed class RedditSessionAuth : IProviderAuth
{
    public const string MeUrl = "https://www.reddit.com/api/me.json?raw_json=1";
    public const string LoginUrl = "https://www.reddit.com/login/";

    /// <summary>このセッション (WebView2) 経由で送るホスト。</summary>
    public static IReadOnlyList<string> RoutedHosts { get; } = new[] { "www.reddit.com" };

    private readonly IBrowserFetcher _fetcher;
    private readonly IRedditLoginUi  _ui;
    private readonly string          _authJsonPath;
    private readonly object          _lock = new();
    private Task<bool>?              _loginTask;
    private AuthState                _state;

    public RedditSessionAuth(IBrowserFetcher fetcher, IRedditLoginUi ui, string authJsonPath)
    {
        _fetcher      = fetcher;
        _ui           = ui;
        _authJsonPath = authJsonPath;
        // 前回のユーザー名だけ表示用に復元 (状態は未確認のまま。最初の要求で me.json を見る)
        _state = new AuthState(AuthStateKind.Unknown, LoadSavedUserName());
    }

    public string    ProviderId => "reddit";
    public AuthState State      { get { lock (_lock) return _state; } }
    public string?   Modhash    { get; private set; }
    public event EventHandler? StateChanged;

    /// <summary>reddit のユーザー名は <c>u/</c> を付けて表示する。</summary>
    public string FormatUserName(string userName) => "u/" + userName;

    /// <summary>まだ一度も確かめていなければ <see cref="RefreshStateAsync"/> する。</summary>
    public async Task EnsureCheckedAsync(CancellationToken ct)
    {
        if (State.Kind == AuthStateKind.Unknown) await RefreshStateAsync(ct).ConfigureAwait(false);
    }

    /// <summary><c>/api/me.json</c> でログイン状態を確かめる。</summary>
    public async Task RefreshStateAsync(CancellationToken ct)
    {
        BrowserFetchResult r;
        try
        {
            r = await _fetcher.FetchAsync(new BrowserFetchRequest("GET", MeUrl, JsonAccept), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            SetState(new AuthState(AuthStateKind.Error, State.UserName, $"接続失敗 ({ex.Message})"));
            return;
        }
        // 「network security」で弾かれた場合、ログイン Cookie が無ければ単に未ログイン (匿名の要求はすべて弾かれる)
        if (RedditResponse.IsNetworkSecurityBlock(r) && await _fetcher.HasLoginCookieAsync().ConfigureAwait(false) == false)
        {
            Modhash = null;
            SetState(new AuthState(AuthStateKind.LoggedOut));
            return;
        }
        ApplyMeResponse(r);
    }

    /// <summary>me.json の応答から状態を決める (ハーネスから直接検証できるよう public)。</summary>
    public void ApplyMeResponse(BrowserFetchResult r)
    {
        var (name, modhash) = ParseMe(r);
        if (name is { Length: > 0 })
        {
            Modhash = modhash;
            SetState(new AuthState(AuthStateKind.LoggedIn, name));
            SaveUserName(name);
        }
        else if (RedditResponse.IsNetworkSecurityBlock(r) && r.Status != 401)
        {
            // 確認ページ (challenge)。ログインしているかどうかは分からない
            SetState(new AuthState(AuthStateKind.NeedsAttention, State.UserName));
        }
        else
        {
            Modhash = null;
            SetState(new AuthState(AuthStateKind.LoggedOut));
        }
    }

    /// <summary>me.json の本文から (ユーザー名, modhash) を取り出す。ログインしていなければ名前は null。</summary>
    public static (string? Name, string? Modhash) ParseMe(BrowserFetchResult r)
    {
        if (r.Status != 200 || !RedditResponse.LooksLikeJson(r)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(r.Body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);
            var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : root;
            var name = data.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            var mh   = data.TryGetProperty("modhash", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            return (name, mh);
        }
        catch (JsonException) { return (null, null); }
    }

    /// <summary>転送側が失効を検知したときに呼ぶ。</summary>
    public void MarkLoggedOut()
    {
        Modhash = null;
        SetState(new AuthState(AuthStateKind.LoggedOut));
    }

    /// <summary>転送側が確認ページを検知したときに呼ぶ。</summary>
    public void MarkNeedsAttention() => SetState(new AuthState(AuthStateKind.NeedsAttention, State.UserName));

    /// <summary>設定画面 / ステータスバーからの明示ログイン。確認ページ待ち (NeedsAttention) なら確認ページの窓を出す。</summary>
    public async Task LoginAsync(CancellationToken ct)
    {
        if (State.Kind == AuthStateKind.NeedsAttention)
            await RequestLoginAsync(RedditLoginReason.Challenge, null, ct, force: true).ConfigureAwait(false);
        else
            await RequestLoginAsync(RedditLoginReason.Login, LoginUrl, ct, force: true).ConfigureAwait(false);
    }

    /// <summary>ログイン窓 (または確認ページの窓) を出して、閉じられたら状態を確かめ直す。ログイン中になれば true。
    /// 自動処理中 (<see cref="ProviderRequestContext.IsBackground"/>) は窓を出さず false (<paramref name="force"/> で上書き)。
    /// 既に窓が開いていればそれの結果を待つ (= 窓は同時に 1 つ)。</summary>
    public Task<bool> RequestLoginAsync(RedditLoginReason reason, string? url, CancellationToken ct, bool force = false)
    {
        if (!force && ProviderRequestContext.IsBackground) return Task.FromResult(false);
        lock (_lock)
        {
            if (_loginTask is { IsCompleted: false } running) return running;
            _loginTask = RunLoginAsync(reason, url, ct);
            return _loginTask;
        }
    }

    private async Task<bool> RunLoginAsync(RedditLoginReason reason, string? url, CancellationToken ct)
    {
        ChBrowser.Services.Logging.LogService.Instance.Write($"[reddit] login window: reason={reason}, url={url}");
        try
        {
            await _ui.ShowAsync(reason, url ?? LoginUrl, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            ChBrowser.Services.Logging.LogService.Instance.Write($"[reddit] login window failed: {ex.Message}");
        }
        // 窓の中での結果に関わらず、閉じた時点のセッションで確かめ直す
        await RefreshStateAsync(CancellationToken.None).ConfigureAwait(false);
        return State.IsLoggedIn;
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        await _fetcher.ClearCookiesAsync().ConfigureAwait(false);
        Modhash = null;
        try { if (File.Exists(_authJsonPath)) File.Delete(_authJsonPath); } catch (IOException) { }
        SetState(new AuthState(AuthStateKind.LoggedOut));
    }

    private void SetState(AuthState s)
    {
        bool changed;
        lock (_lock)
        {
            changed = _state != s;
            _state  = s;
        }
        if (changed) StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static readonly IReadOnlyList<KeyValuePair<string, string>> JsonAccept =
        new[] { new KeyValuePair<string, string>("Accept", "application/json") };

    private string? LoadSavedUserName()
    {
        try
        {
            if (!File.Exists(_authJsonPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(_authJsonPath));
            return doc.RootElement.TryGetProperty("userName", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    private void SaveUserName(string name)
    {
        try
        {
            var json = JsonSerializer.Serialize(new { userName = name, lastLoginAt = DateTimeOffset.Now.ToString("o") });
            File.WriteAllText(_authJsonPath, json, new UTF8Encoding(false));
        }
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[reddit] auth.json save failed: {ex.Message}");
        }
    }
}

/// <summary>reddit の応答の見分け方。</summary>
public static class RedditResponse
{
    public static bool LooksLikeJson(BrowserFetchResult r)
    {
        var ct = r.Header("content-type") ?? "";
        if (ct.Contains("json", StringComparison.OrdinalIgnoreCase)) return true;
        // content-type が無い / text の場合は先頭文字で見る
        foreach (var b in r.Body)
        {
            if (b is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t') continue;
            return b is (byte)'{' or (byte)'[';
        }
        return false;
    }

    /// <summary>「You've been blocked by network security」ページ (匿名・自動アクセスへの 403)。</summary>
    public static bool IsNetworkSecurityBlock(BrowserFetchResult r)
    {
        if (r.Status is not (403 or 401) || LooksLikeJson(r)) return false;
        var head = Encoding.UTF8.GetString(r.Body, 0, Math.Min(r.Body.Length, 262144));
        return head.Contains("blocked by network security", StringComparison.OrdinalIgnoreCase)
            || head.Contains("network security", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>ログインページへ飛ばされた (= セッションが無い)。</summary>
    public static bool IsLoginRedirect(BrowserFetchResult r)
        => r.Redirected && Uri.TryCreate(r.FinalUrl, UriKind.Absolute, out var u)
           && u.AbsolutePath.StartsWith("/login", StringComparison.OrdinalIgnoreCase);

    /// <summary>api_type=json の応答が USER_REQUIRED (= ログインが必要) を含む。</summary>
    public static bool IsUserRequired(BrowserFetchResult r)
        => LooksLikeJson(r) && Encoding.UTF8.GetString(r.Body).Contains("USER_REQUIRED", StringComparison.Ordinal);
}
