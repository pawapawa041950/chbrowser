using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ChBrowser.Services.Bbs;

/// <summary>提供者専用の通信経路 (<c>doc/reddit-design.md</c> §3 B10)。reddit では WebView2 のログインセッション内の fetch、
/// 将来は OAuth (Bearer 付き HttpClient) など。応答は通常の <see cref="HttpResponseMessage"/> に詰め直して返すので、
/// 呼び出し側 (Dat / Subject / Setting / Post の各クライアント、提供者) は経路の違いを知らなくてよい。</summary>
public interface IProviderTransport
{
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct);
}

/// <summary>ホスト → 通信経路の登録簿。<see cref="ProviderTransportHandler"/> が要求ごとに引く。
/// 経路は提供者の <see cref="IBbsProvider.RoutedHosts"/> 単位で登録する (<see cref="Register"/>)。
/// 何も登録されていなければ全要求が通常の HTTP で送られる (= 既存の掲示板は影響を受けない)。</summary>
public static class ProviderTransports
{
    private static readonly Dictionary<string, IProviderTransport> _byHost = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    /// <summary>提供者の <see cref="IBbsProvider.RoutedHosts"/> を <paramref name="transport"/> に向ける。null なら登録解除 (通常の HTTP に戻す)。</summary>
    public static void Register(IBbsProvider provider, IProviderTransport? transport)
    {
        lock (_lock)
        {
            foreach (var host in provider.RoutedHosts)
            {
                if (transport is null) _byHost.Remove(host);
                else                   _byHost[host] = transport;
            }
        }
    }

    /// <summary>ホスト一覧を直接 <paramref name="transport"/> に向ける (提供者の実装より先に経路だけ用意する場合)。null なら解除。</summary>
    public static void RegisterHosts(IEnumerable<string> hosts, IProviderTransport? transport)
    {
        lock (_lock)
        {
            foreach (var host in hosts)
            {
                if (transport is null) _byHost.Remove(host);
                else                   _byHost[host] = transport;
            }
        }
    }

    /// <summary>ホストに登録された経路。無ければ null。</summary>
    public static IProviderTransport? Find(string? host)
    {
        if (string.IsNullOrEmpty(host)) return null;
        lock (_lock) return _byHost.TryGetValue(host, out var t) ? t : null;
    }

    /// <summary>テスト用: 登録をすべて消す。</summary>
    public static void Clear()
    {
        lock (_lock) _byHost.Clear();
    }
}

/// <summary>HttpClient のパイプラインに 1 段挟むハンドラ。要求先ホストに経路が登録されていればそちらへ回し、
/// 無ければそのまま内側のハンドラ (通常の HTTP) へ渡す。</summary>
public sealed class ProviderTransportHandler : DelegatingHandler
{
    public ProviderTransportHandler(HttpMessageHandler inner) : base(inner) { }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var transport = ProviderTransports.Find(request.RequestUri?.Host);
        return transport is null ? base.SendAsync(request, ct) : transport.SendAsync(request, ct);
    }
}

/// <summary>いま走っている要求が「ユーザの操作によるもの」か「自動処理 (お気に入り一括更新・起動時のタブ復元等) によるもの」か。
/// 自動処理中はログインの失効を検知してもログイン窓を出さない (<c>doc/reddit-design.md</c> §5.4、決定 D37)。
/// <see cref="System.Threading.AsyncLocal{T}"/> なので、<see cref="Background"/> の範囲内で始めた非同期処理すべてに伝わる。</summary>
public static class ProviderRequestContext
{
    private static readonly AsyncLocal<bool> _background = new();

    public static bool IsBackground => _background.Value;

    /// <summary><c>using (ProviderRequestContext.Background()) { … }</c> の範囲を自動処理として扱う。</summary>
    public static IDisposable Background()
    {
        var previous = _background.Value;
        _background.Value = true;
        return new Scope(previous);
    }

    private sealed class Scope(bool previous) : IDisposable
    {
        public void Dispose() => _background.Value = previous;
    }
}

/// <summary>掲示板のログイン状態の種類。</summary>
public enum AuthStateKind
{
    /// <summary>認証を必要としない / まだ一度も確認していない。</summary>
    Unknown,
    LoggedOut,
    LoggedIn,
    /// <summary>ブラウザでの確認 (challenge) 等、ユーザ操作が必要。</summary>
    NeedsAttention,
    Error,
}

/// <summary>掲示板のログイン状態。<see cref="UserName"/> はログイン中のみ。</summary>
public sealed record AuthState(AuthStateKind Kind, string? UserName = null, string? Message = null)
{
    public static AuthState Unknown { get; } = new(AuthStateKind.Unknown);
    public bool IsLoggedIn => Kind == AuthStateKind.LoggedIn;
}

/// <summary>掲示板ごとのログイン (<c>doc/reddit-design.md</c> §3 B4)。ステータスバー・設定「認証」・板一覧の案内はこの状態だけを見る。
/// reddit は WebView2 セッション (<c>RedditSessionAuth</c>)、将来の OAuth も同じ口に載せる。
/// 5ch のどんぐり / エッヂの Cookie は従来どおり <see cref="Api.PostClient"/> 側で扱う (ここには混ぜない)。</summary>
public interface IProviderAuth
{
    string    ProviderId { get; }
    AuthState State      { get; }
    event EventHandler? StateChanged;

    /// <summary>ログイン手順を始める (reddit: ログイン窓を出す)。完了 / キャンセルで戻る。</summary>
    Task LoginAsync(CancellationToken ct);
    Task LogoutAsync(CancellationToken ct);
    /// <summary>現在のログイン状態を掲示板に問い合わせて <see cref="State"/> を更新する。</summary>
    Task RefreshStateAsync(CancellationToken ct);

    /// <summary>表示用のユーザー名 (reddit: <c>u/name</c>)。既定はそのまま。</summary>
    string FormatUserName(string userName) => userName;
}

/// <summary><see cref="IProviderAuth"/> の状態の表示文言 (ステータスバー / 設定画面)。掲示板に依存しない。</summary>
public static class ProviderAuthDisplay
{
    /// <summary>ステータスバー用 (例: 「reddit: u/xxx」「reddit: 未ログイン」)。未確認で前回の名前も無ければ空 (= 項目を出さない)。</summary>
    public static string StatusBarText(IProviderAuth auth)
    {
        var s    = auth.State;
        var name = BbsRegistry.FindById(auth.ProviderId)?.DisplayName ?? auth.ProviderId;
        return s.Kind switch
        {
            AuthStateKind.LoggedIn       => $"{name}: {auth.FormatUserName(s.UserName ?? "")}",
            AuthStateKind.LoggedOut      => $"{name}: 未ログイン",
            AuthStateKind.NeedsAttention => $"{name}: ブラウザで確認が必要です",
            AuthStateKind.Error          => $"{name}: {s.Message}",
            _                            => s.UserName is { Length: > 0 } n ? $"{name}: {auth.FormatUserName(n)} (未確認)" : "",
        };
    }

    /// <summary>設定画面用 (例: 「ログイン中 (u/xxx)」)。</summary>
    public static string SettingsText(IProviderAuth auth)
    {
        var s = auth.State;
        return s.Kind switch
        {
            AuthStateKind.LoggedIn       => $"ログイン中 ({auth.FormatUserName(s.UserName ?? "")})",
            AuthStateKind.LoggedOut      => "未ログイン",
            AuthStateKind.NeedsAttention => "ブラウザでの確認が必要",
            AuthStateKind.Error          => s.Message ?? "エラー",
            _ => s.UserName is { Length: > 0 } n ? $"未確認 (前回 {auth.FormatUserName(n)})" : "未確認",
        };
    }

    /// <summary>ステータスバーの項目がクリックされたとき: ログイン中なら状態を確かめ直し、そうでなければログイン手順を始める。</summary>
    public static Task ActivateAsync(IProviderAuth auth)
        => auth.State.IsLoggedIn ? auth.RefreshStateAsync(CancellationToken.None) : auth.LoginAsync(CancellationToken.None);
}
