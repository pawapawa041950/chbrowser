using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Models;
using ChBrowser.Services.Storage;

namespace ChBrowser.Services.Donguri;

/// <summary>メール認証ログインの結果。</summary>
public enum DonguriLoginOutcome
{
    /// <summary>成功 (= ログイン Cookie を取得し、CookieJar に格納済み)。</summary>
    Success,
    /// <summary>認証情報誤り (メアド/パスワード不一致)。</summary>
    InvalidCredentials,
    /// <summary>ネットワークエラー (DNS / 接続不可 / タイムアウト等)。</summary>
    NetworkError,
    /// <summary>その他 / 解釈不能なレスポンス。</summary>
    Unknown,
}

/// <summary>ログイン結果データ。</summary>
public sealed record DonguriLoginResult(DonguriLoginOutcome Outcome, string Message);

/// <summary>どんぐり関連のメタ。state.json に永続化する。</summary>
/// <param name="AcornIssuedAtUnix">acorn を最初に Set-Cookie で受け取った時刻 (unix sec)。失効推定用。0=未発行。</param>
/// <param name="LastWriteAtUnix">最後の書き込み成功時刻 (unix sec)。Lv 上昇推定の参考用。0=未書き込み。</param>
/// <param name="LastBrokenAtUnix">直近で broken_acorn 検知した時刻 (unix sec)。0=なし。</param>
public sealed record DonguriState(
    long AcornIssuedAtUnix,
    long LastWriteAtUnix,
    long LastBrokenAtUnix);

/// <summary>
/// CookieJar の上に立ち、acorn / MonaTicket の寿命管理と broken_acorn からの復旧を担う。
///
/// - <see cref="ApplyToRequest"/> / <see cref="MergeFromResponse"/> は CookieJar に委譲しつつ、
///   acorn の発行/失効/再発行を検知して state.json に書き戻す
/// - <see cref="EstimatedAcornAgeSeconds"/> は「acorn を受け取ってから経過した秒数」。
///   設計書 §3.5 のレベル上昇/失効タイマ表示に使う
/// - <see cref="HandleBrokenAcorn"/> は acorn Cookie をストアから削除してその場で再構築できる状態に戻す
/// </summary>
public sealed class DonguriService
{
    private const string AcornName       = "acorn";
    private const string MonaTicketName  = "MonaTicket";
    private const string Default5chHost  = "5ch.io";

    /// <summary>どんぐりメール認証ログインの POST 先 URL。
    /// JDim / 公開 VBScript の参照実装と一致 (donguri.5ch.io/login)。
    /// donguri.5ch.net/login も同等動作の模様 (5ch.net→5ch.io 移行で両ドメイン受け付け)。</summary>
    private const string LoginUrl                = "https://donguri.5ch.io/login";
    private const string LoginFormFieldEmail     = "email";
    /// <summary>フォームフィールド名は "pass" (= 5ch のメール認証ログインフォームの実フィールド名)。
    /// 一般的な "password" ではないので注意。JDim 参照実装と一致。</summary>
    private const string LoginFormFieldPassword  = "pass";

    private readonly CookieJar _jar;
    private readonly string    _statePath;
    private readonly object    _lock = new();
    private DonguriState       _state;

    public DonguriService(CookieJar jar, DataPaths paths)
    {
        _jar       = jar;
        _statePath = paths.DonguriStateJsonPath;
        _state     = LoadState();
    }

    /// <summary>メール認証でログインを試みる。成功したら ログイン Cookie が <see cref="CookieJar"/> に格納される。
    /// 既存の MonazillaClient の <see cref="HttpClient"/> を経由する (= UA / Timeout は共通設定が乗る)。
    /// メアドかパスワードが空ならただちに <see cref="DonguriLoginOutcome.InvalidCredentials"/> を返す。</summary>
    public async Task<DonguriLoginResult> LoginAsync(HttpClient http, string email, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return new DonguriLoginResult(DonguriLoginOutcome.InvalidCredentials, "メアド/パスワードが空です");

        try
        {
            using var loginHttp = CreateNonRedirectingHttpClient(http);
            using var req       = BuildLoginRequest(email, password);
            using var resp      = await loginHttp.SendAsync(req, ct).ConfigureAwait(false);
            MergeFromResponse(resp);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var result = ClassifyLoginResponse(resp, body);
            if (result.Outcome == DonguriLoginOutcome.Success)
                await SaveAsync(ct).ConfigureAwait(false);
            return result;
        }
        catch (HttpRequestException ex)
        {
            Debug.WriteLine($"[DonguriService] login network error: {ex.Message}");
            return new DonguriLoginResult(DonguriLoginOutcome.NetworkError, $"通信失敗: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return new DonguriLoginResult(DonguriLoginOutcome.NetworkError, "タイムアウト");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DonguriService] login unknown error: {ex.Message}");
            return new DonguriLoginResult(DonguriLoginOutcome.Unknown, ex.Message);
        }
    }

    /// <summary>5ch のメール認証 POST は 302 + Set-Cookie: acorn を返してくる。
    /// MonazillaClient.Http は AllowAutoRedirect=true なので 302 を自動追従してしまい中間の Set-Cookie を取りこぼす。
    /// そのため login だけ AllowAutoRedirect=false の専用 HttpClient を作る (UA は元 client から複製)。</summary>
    private static HttpClient CreateNonRedirectingHttpClient(HttpClient source)
    {
        var handler = new System.Net.Http.HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies        = false,
        };
        var http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(15) };
        foreach (var ua in source.DefaultRequestHeaders.UserAgent) http.DefaultRequestHeaders.UserAgent.Add(ua);
        return http;
    }

    /// <summary>email + pass のフォーム POST を組み立てる。既存 acorn / MonaTicket 等の Cookie も付ける
    /// (= 既ログイン状態でも改めて再ログインできるように)。</summary>
    private HttpRequestMessage BuildLoginRequest(string email, string password)
    {
        var fields = $"{Uri.EscapeDataString(LoginFormFieldEmail)}={Uri.EscapeDataString(email)}"
                   + $"&{Uri.EscapeDataString(LoginFormFieldPassword)}={Uri.EscapeDataString(password)}";
        var req = new HttpRequestMessage(HttpMethod.Post, LoginUrl)
        {
            Content = new StringContent(fields, Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        ApplyToRequest(req);
        return req;
    }

    /// <summary>レスポンスを分類して <see cref="DonguriLoginResult"/> を返す。
    /// 主シグナル: Set-Cookie に acorn が含まれていれば成功。
    /// 補助シグナル: 200 系で「メールアドレス」「パスワード」「違い」等の文言が本文にあれば認証失敗。</summary>
    private static DonguriLoginResult ClassifyLoginResponse(HttpResponseMessage resp, string body)
    {
        if (HasAcornSetCookie(resp))
            return new DonguriLoginResult(DonguriLoginOutcome.Success, "ログインしました");

        if ((int)resp.StatusCode is >= 200 and < 300)
        {
            if (BodyLooksLikeAuthFailure(body))
                return new DonguriLoginResult(DonguriLoginOutcome.InvalidCredentials, "メアド/パスワードが違うかアカウント未登録");
            return new DonguriLoginResult(DonguriLoginOutcome.Unknown, $"応答を解釈できませんでした (HTTP {(int)resp.StatusCode})");
        }
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new DonguriLoginResult(DonguriLoginOutcome.InvalidCredentials, "認証に失敗しました");
        return new DonguriLoginResult(DonguriLoginOutcome.Unknown, $"HTTP {(int)resp.StatusCode}");
    }

    private static bool HasAcornSetCookie(HttpResponseMessage resp)
    {
        if (!resp.Headers.TryGetValues("Set-Cookie", out var setCookies)) return false;
        foreach (var sc in setCookies)
        {
            if (sc.StartsWith("acorn=", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>5ch は認証失敗時もログインページを 200 で再描画してくる。
    /// 本文に出てくるエラーらしきキーワードで失敗判定する。</summary>
    private static bool BodyLooksLikeAuthFailure(string body)
        => body.Contains("メールアドレス", StringComparison.Ordinal)
        || body.Contains("パスワード",     StringComparison.Ordinal)
        || body.Contains("違い",           StringComparison.Ordinal)
        || body.Contains("不正",           StringComparison.Ordinal)
        || body.Contains("失敗",           StringComparison.Ordinal)
        || body.Contains("invalid",        StringComparison.OrdinalIgnoreCase);


    public CookieJar Cookies => _jar;

    /// <summary>5ch.io ドメインで現在保持している acorn 値 (なければ null)。</summary>
    public string? AcornValue => _jar.Find(Default5chHost, AcornName);

    /// <summary>5ch.io ドメインで現在保持している MonaTicket 値 (なければ null)。</summary>
    public string? MonaTicketValue => _jar.Find(Default5chHost, MonaTicketName);

    /// <summary>acorn を受け取ってから経過した秒数。未発行 / 失効済みなら null。
    /// 設計書 §3.5: 0→1 で約 5 分 / 約 3 時間で失効。</summary>
    public long? EstimatedAcornAgeSeconds
    {
        get
        {
            if (AcornValue is null) return null;
            var issued = _state.AcornIssuedAtUnix;
            if (issued <= 0) return null;
            var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - issued;
            return age < 0 ? 0 : age;
        }
    }

    /// <summary>HttpRequest の前に呼ぶ。CookieJar から該当 URL 向け Cookie ヘッダを組み立てて差し込む。</summary>
    public void ApplyToRequest(HttpRequestMessage req) => _jar.ApplyToRequest(req);

    /// <summary>認証モード指定版。<see cref="PostAuthMode"/> に応じて送る Cookie 集合を絞り込む。
    /// <list type="bullet">
    /// <item><description><see cref="PostAuthMode.None"/>: Cookie ヘッダ自体を空にする</description></item>
    /// <item><description><see cref="PostAuthMode.Cookie"/>: acorn と MonaTicket だけ送る (= メール認証 cookie は除外)</description></item>
    /// <item><description><see cref="PostAuthMode.MailAuth"/>: 全 Cookie を送る (= 通常パス)</description></item>
    /// </list>
    /// </summary>
    public void ApplyToRequest(HttpRequestMessage req, PostAuthMode mode)
    {
        // 既存ヘッダを必ず先に剥がす (= None / Cookie モードで残骸が混ざらないように)。
        req.Headers.Remove("Cookie");

        if (mode == PostAuthMode.None) return;
        if (req.RequestUri is null)    return;

        string header = mode == PostAuthMode.MailAuth
            ? _jar.BuildCookieHeader(req.RequestUri)
            : _jar.BuildCookieHeader(req.RequestUri,
                                     name => name == AcornName || name == MonaTicketName);
        if (!string.IsNullOrEmpty(header))
            req.Headers.TryAddWithoutValidation("Cookie", header);
    }

    /// <summary>全 Cookie 削除 + DonguriState リセット + ファイル永続化。
    /// 設定 → 通信 → 「Cookie をすべて削除」のボタンから呼ばれる。
    /// 5ch の挙動が不安定な時に「綺麗な状態」から acorn を取り直すための機能。</summary>
    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        _jar.Clear();
        UpdateState(_ => new DonguriState(0, 0, 0));
        await SaveAsync(ct).ConfigureAwait(false);
    }

    /// <summary>HTTP レスポンスを受けたら呼ぶ。Set-Cookie を取り込み、acorn が新規発行されていれば
    /// 発行時刻を記録、消えていれば 0 にリセットする。</summary>
    public void MergeFromResponse(HttpResponseMessage resp)
    {
        var hadAcornBefore = AcornValue is not null;
        _jar.MergeFromResponse(resp);
        var hasAcornNow    = AcornValue is not null;

        if (!hadAcornBefore && hasAcornNow)
        {
            UpdateState(s => s with { AcornIssuedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        }
        else if (hadAcornBefore && !hasAcornNow)
        {
            // サーバが Max-Age=0 などで失効させた場合
            UpdateState(s => s with { AcornIssuedAtUnix = 0 });
        }
    }

    /// <summary>書き込み成功時に呼ぶ。state.json の最終書き込み時刻を更新。</summary>
    public void NoteWriteSucceeded()
        => UpdateState(s => s with { LastWriteAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });

    /// <summary>broken_acorn エラーを検知したときの復旧ハンドル。
    /// 5ch.io ドメインの acorn Cookie をストアから削除し、state.json も再発行待ち状態に戻す。
    /// 呼び出し側で <see cref="SaveAsync"/> を呼んでからリトライ POST する想定。</summary>
    public void HandleBrokenAcorn()
    {
        _jar.Remove(Default5chHost, AcornName);
        UpdateState(s => s with
        {
            AcornIssuedAtUnix = 0,
            LastBrokenAtUnix  = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
    }

    /// <summary>cookies.txt と state.json を両方保存する。書き込み成功後/エラー後の終端で呼ぶ。</summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _jar.SaveAsync(ct).ConfigureAwait(false);
        DonguriState snapshot;
        lock (_lock) snapshot = _state;
        var tmp  = _statePath + ".tmp";
        var json = JsonSerializer.Serialize(snapshot, JsonOpts);
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
        File.Move(tmp, _statePath, overwrite: true);
    }

    private DonguriState LoadState()
    {
        if (!File.Exists(_statePath)) return new DonguriState(0, 0, 0);
        try
        {
            var json = File.ReadAllText(_statePath);
            return JsonSerializer.Deserialize<DonguriState>(json, JsonOpts) ?? new DonguriState(0, 0, 0);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DonguriService] state load failed: {ex.Message}");
            return new DonguriState(0, 0, 0);
        }
    }

    private void UpdateState(Func<DonguriState, DonguriState> mutate)
    {
        lock (_lock) _state = mutate(_state);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented        = true,
    };
}
