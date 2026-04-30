using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChBrowser.Services.Storage;

namespace ChBrowser.Services.Donguri;

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
